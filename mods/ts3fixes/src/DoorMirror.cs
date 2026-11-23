using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;

namespace Ts3Fixes
{
    // Managed side of the P3 door mirror (docs/105 §10.5, plan in §10.4.5), switch doormirror (tuning kDoorMirror,
    // default off). The native snapshot (ts3native snap.cpp) answers door portals from a per-lot table of the lot's
    // managed doors instead of asking script. This file builds that table on the sim thread and keeps it current:
    // - DoorMirror.GetLotDoors(lotId, out epoch): the lot's door records, built at the first ask after an invalidation;
    //   null = the lot is invalid (switch off, lot gone, or a door that cannot be read): take the guard path.
    // - DoorMirror.Invalidate(lotId) / InvalidateAll(): for the native invalidation sources (docs/76 load epoch 5, 7, 9,
    //   0x0A, lot added 0x0D, lot bounds 0x0F, build/buy exit); both are also sent to ts3native (channel codes 18-20).
    // - DoorMirror.SyncRoute: the native binding (docs/94 §14): at each plan, the start and goal lots are pushed over the
    //   GetOption2 door channel when ts3native's table of that lot is not valid (DoorChannel in DoorMirrorCore.cs).
    // - A postfix on CommonDoor.ImportContent (every door subclass chains to it, incl. the closed ElectronicGate) marks
    //   the door's lot invalid after the import wrote mLockType, which raises no lock event (docs/105 §10.1).
    // A door with no bound managed target is not in the lot's managed object query, so the native side must treat a
    // portal owner id with no record as unknown (guard path), never as Anybody (§10.4.5 point 3).
    // Counters: [102] door imports seen, [103] lot invalidations, [104] lot tables built, [105] builds refused.
    public static partial class Fixes
    {
        static bool sDoorImportReady;

        static void ApplyDoorMirror()
        {
            sDoorImportReady = Patch(typeof(CommonDoor), "ImportContent", new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamReader) },
                                     typeof(DoorImportHook), "ImportHook", "ImportOrig");
        }

        // Interface for the native binding (docs/105 §10.5). Sim thread only, like every caller of managed game code.
        public static class DoorMirror
        {
            static readonly DoorMirrorTable sTable = new DoorMirrorTable();
            static readonly DoorMirrorTable.LotReader sRead = ReadLotDoors;

            public static DoorRecord[] GetLotDoors(ulong lotId, out uint epoch)
            {
                if (!ShortcutOn("doormirror")) { epoch = 0; return null; }
                int built = sTable.Builds, refused = sTable.Refused;
                DoorRecord[] r = sTable.Get(lotId, sRead, out epoch);
                int[] c = PatchCounters();
                c[104] += sTable.Builds - built;
                c[105] += sTable.Refused - refused;
                return r;
            }

            // Also sent to ts3native (channel codes 18-20), whatever the switch says: cheap, rare, and a native table filled
            // while the switch was on never outlives a managed invalidation. Without ts3native the game answers false.
            public static void Invalidate(ulong lotId) { sTable.Invalidate(lotId); PatchCounters()[103]++; DoorChannel.Invalidate(lotId, sSend); }

            public static void InvalidateAll() { sTable.InvalidateAll(); PatchCounters()[103]++; DoorChannel.InvalidateAll(sSend); }

            // Native binding (docs/94 §14 adapter): at a plan's start (Route.Plan and the Replan family), for the start
            // lot and the goal lot, ask ts3native whether its table of that lot is valid; when not, push a fresh read
            // (DoorChannel.Sync). start Invalid = the follower's position. Lot 0 (no lot) and a missing goal are skipped.
            // shortcut: start and primary goal lot only (docs/94 §11.2: transit lots are known only after the world search);
            // add P4's request lot set when the native START handshake carries it.
            internal static void SyncRoute(ScriptCore.Route r, Vector3 start)
            {
                if (!ShortcutOn("doormirror")) return;
                try
                {
                    if (start == Vector3.Invalid)
                    {
                        IScriptProxy proxy = r.Follower;
                        GameObject g = proxy != null ? proxy.Target as GameObject : null;
                        if (g == null) return;
                        start = g.Position;
                    }
                    LotLocation loc = default(LotLocation);
                    ulong a = World.GetLotLocation(start, ref loc);
                    Vector3 dest = r.GetApproximateOriginalDestination();
                    ulong b = dest == Vector3.Invalid || dest == Vector3.Origin ? 0 : World.GetLotLocation(dest, ref loc);
                    Sync(a);
                    if (b != a) Sync(b);
                }
                catch (Exception) { } // a failed sync pushes nothing: the lot stays invalid natively (guard path)
            }

            static void Sync(ulong lotId)
            {
                if (lotId == 0) return;
                int built = sTable.Builds, refused = sTable.Refused;
                DoorChannel.Sync(lotId, sTable, sRead, sSend);
                int[] c = PatchCounters();
                c[104] += sTable.Builds - built;
                c[105] += sTable.Refused - refused;
            }

            // one channel message: GetOption2 on a handle-0 route (RouteBackoff.cs MayYield's probe; the handle is ignored)
            static readonly DoorChannel.Sender sSend = delegate(ulong o) { return sYieldProbe.GetOption2((Route.RouteOption2)o); };

            // Every CommonDoor on the lot (Lot.GetObjects, the native lot object query); false on the first door that is
            // gone or whose lock state cannot be mirrored (DoorRecord.Make).
            // docs/328 §7.5 (Private Room Door 566837: LockableDoor.IsAllowedThrough lets only key holders through, its LockType stays
            // the default): the record would say open. A door type whose IsAllowedThrough is declared outside the game's assemblies
            // makes the whole lot fall back to the game (same as a refused read).
            static readonly Dictionary<Type, bool> sModDoor = new Dictionary<Type, bool>();
            static bool ModDoorRule(Type t)
            {
                bool r;
                if (sModDoor.TryGetValue(t, out r)) return r;
                System.Reflection.MethodInfo m = t.GetMethod("IsAllowedThrough", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, new Type[] { typeof(int), typeof(int), typeof(Sims3.Gameplay.Actors.Sim) }, null);
                r = m != null && !UfPreCore.GameAssembly(m.DeclaringType.Assembly.GetName().Name);
                sModDoor[t] = r;
                return r;
            }

            static bool ReadLotDoors(ulong lotId, List<DoorRecord> into)
            {
                Lot lot = LotManager.GetLot(lotId);
                if (lot == null || lot.HasBeenDestroyed) return false;
                foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
                {
                    if (d == null || d.HasBeenDestroyed) return false;
                    if (ModDoorRule(d.GetType())) { PatchCounters()[105]++; return false; }
                    SimDescription owner = d.LockOwner;
                    Household h = owner != null ? owner.Household : null;
                    int kind = (d is Door ? DoorRecord.kIsDoor : 0) | (d is IBarnDoor ? DoorRecord.kBarnDoor : 0)
                             | (d is GateDouble ? DoorRecord.kGateDouble : 0) | (d is IElectronicGate ? DoorRecord.kElectronicGate : 0)
                             | (d.CanLargeAnimalUse ? DoorRecord.kLargeAnimalUse : 0) | (d is DoorSingleGender ? DoorRecord.kSingleGender : 0);
                    DoorRecord r = DoorRecord.Make(d.ObjectId.Value, (uint)d.LockType, owner != null ? owner.SimDescriptionId : 0,
                                                   h != null ? h.HouseholdId : 0, d.OwnerList, d.OneWayRoomDestinationId, d.GetRoomConnections(), kind);
                    if (r == null) return false;
                    into.Add(r);
                }
                return true;
            }
        }

        // Postfix on CommonDoor.ImportContent (docs/105 §10.3 point 2): after the game's body, mark the door's lot invalid
        // and log the imported lock type. Runs whatever the switch says, so a table built while the switch was on is
        // never kept stale; it only touches our own table.
        public class DoorImportHook
        {
            public bool ImportHook(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader)
            {
                bool result = ImportOrig(resKeyTable, objIdTable, reader);
                if (!sDoorImportReady) return result;
                try
                {
                    CommonDoor door = (CommonDoor)(object)this;
                    Lot lot = door.LotCurrent;
                    if (lot != null) DoorMirror.Invalidate(lot.LotId); else DoorMirror.InvalidateAll();
                    PatchCounters()[102]++;
                    if (ShortcutOn("doormirror"))
                        ShortcutLog("door import " + door.ObjectId.Value.ToString("X") + " lot " + (lot != null ? lot.LotId.ToString("X") : "none") + " lock " + door.LockType);
                }
                catch (Exception) { DoorMirror.InvalidateAll(); }
                return result;
            }
            public bool ImportOrig(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader) { return false; } // receives CommonDoor.ImportContent's original header
        }
    }
}
