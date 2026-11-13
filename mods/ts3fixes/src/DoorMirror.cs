using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
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
    // - [DoorFast] extras (ts3native doorfast.cpp, docs/548): with each lot push, the lot's rule-b portal owner ids (objects of a
    //   portal type with no rule of its own in Sim.OnPortalPathPlan) and two lot flags (only EA's Sim handler listens on the
    //   lot's PortalPathPlan event; the lot has hidden rooms); at each route START the actor's flags (human, not a tomb mummy,
    //   no Proprietor role, household scope). Prefixes on Lot.add_PortalPathPlan / remove_PortalPathPlan invalidate the lot
    //   before a handler (a PrivacySituation or a mod) comes or goes, so no prediction outlives its handler set.
    //   Coverage (v919, docs/548 "Coverage"; only when ts3native answers DoorChannel.Coverage): typed records for the other portal
    //   owners (ElevatorDoors .. HouseboatJig), the lot's hidden room ids instead of a lot bit, a record for a router that is no
    //   Sim, and at each START up to 4 lots ts3native's door asks found no table for (transit lots). No new answer rule.
    // Counters: [102] door imports seen, [103] lot invalidations, [104] lot tables built, [105] builds refused.
    public static partial class Fixes
    {
        static bool sDoorImportReady, sPortalPlanReady;

        static void ApplyDoorMirror()
        {
            sDoorImportReady = Patch(typeof(CommonDoor), "ImportContent", new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamReader) },
                                     typeof(DoorImportHook), "ImportHook", "ImportOrig");
            Type[] h = new Type[] { typeof(PortalPathPlanHandler) };
            sPortalPlanReady = Patch(typeof(Lot), "add_PortalPathPlan", h, typeof(PortalPlanHook), "AddHook", "AddOrig")
                             & Patch(typeof(Lot), "remove_PortalPathPlan", h, typeof(PortalPlanHook), "RemoveHook", "RemoveOrig");
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
                    // [DoorFast]: the asks' actor is the router object (the follower). Coverage (v919): a follower that is no Sim gets
                    // a record too (kActNotSim: ts3native rule g, v920), and the lots ts3native's door asks found no table for
                    // (transit lots: only the start and goal lots are pushed above) are pushed, up to kMissedPerStart a START
                    // (ts3native hands each lot out at most every 5 s)
                    if (DoorChannel.NoExtras) return;
                    IScriptProxy fp = r.Follower;
                    object t = fp != null ? fp.Target : null;
                    Sim s = t as Sim;
                    GameObject o = t as GameObject;
                    if (s != null) DoorChannel.Actor(s.ObjectId.Value, ActorFlags(s), sSend);
                    else if (o != null && DoorChannel.Coverage(sSend)) DoorChannel.Actor(o.ObjectId.Value, NotSimFlags(), sSend);
                    for (int i = 0; i < kMissedPerStart; i++)
                    {
                        ulong m = DoorChannel.NextMissed(sSend);
                        if (m == 0) break;
                        if (m != a && m != b) Sync(m);
                    }
                }
                catch (Exception) { } // a failed sync pushes nothing: the lot stays invalid natively (guard path)
            }
            const int kMissedPerStart = 4;

            // [DoorFast] actor flags; household scope when the Sim cannot be classified (CLAUDE.md household rule)
            static uint ActorFlags(Sim s)
            {
                uint f = 0;
                try
                {
                    SimDescription d = s.SimDescription;
                    if (d == null) return DoorChannel.kActHousehold;
                    if (s.IsHuman && !d.IsPet && !d.IsHorse && !s.IsStray) f |= DoorChannel.kActHuman;
                    if (!d.IsTombMummy) f |= DoorChannel.kActNotMummy;
                    if (!Proprietor(d)) f |= DoorChannel.kActNoProprietor;
                    // v920 rules c-f: EA's own tests (ElevatorDoors / DivingBoard steps, Ladder.DiscourageSimFromUsing, DoorSingleGender)
                    if (d.ToddlerOrBelow) f |= DoorChannel.kActToddler;
                    if (d.IsVisuallyPregnant) f |= DoorChannel.kActPregnant;
                    if (sLadderTraits == null) sLadderTraits = typeof(Sims3.Gameplay.Objects.Ladder).GetField("kTraitsThatAvoidLadders",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    Sims3.Gameplay.ActorSystems.TraitNames[] avoid = sLadderTraits != null ? sLadderTraits.GetValue(null) as Sims3.Gameplay.ActorSystems.TraitNames[] : null;
                    if (avoid == null) return DoorChannel.kActHousehold;  // cannot mirror DiscourageSimFromUsing: unclassified, no rule applies
                    if (d.Elder || (s.TraitManager != null && s.TraitManager.HasAnyElement(avoid))) f |= DoorChannel.kActAvoidLadder;  // a null TraitManager throws in EA's step: not discouraged
                    if (d.Gender == CASAgeGenderFlags.Male) f |= DoorChannel.kActMale;
                    else if (d.Gender == CASAgeGenderFlags.Female) f |= DoorChannel.kActFemale;
                }
                catch (Exception) { return DoorChannel.kActHousehold; }
                return HhScope(s) ? f | DoorChannel.kActHousehold : f;
            }
            static System.Reflection.FieldInfo sLadderTraits;

            // [DoorFast] a router that is no Sim (rule g): household scope unless the running script task is a Sim outside it (a
            // vehicle's route plans inside its driver's interaction); no task Sim or a failure: household (CLAUDE.md household rule)
            static uint NotSimFlags()
            {
                try
                {
                    IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                    Sim s = p != null ? p.Target as Sim : null;
                    return DoorChannel.kActNotSim | (s == null || HhScope(s) ? DoorChannel.kActHousehold : 0);
                }
                catch (Exception) { return DoorChannel.kActNotSim | DoorChannel.kActHousehold; }
            }

            // the Sim's assigned role is a Proprietor (any lot: a superset of "on its role lot", Sim.OnPortalPathPlan step 2)
            static readonly Dictionary<Type, bool> sProprietor = new Dictionary<Type, bool>();
            static bool Proprietor(SimDescription d)
            {
                object role = d.AssignedRole;
                if (role == null) return false;
                Type t = role.GetType();
                bool r;
                if (sProprietor.TryGetValue(t, out r)) return r;
                for (Type b = t; b != null && !r; b = b.BaseType) r = b.Name == "Proprietor";
                sProprietor[t] = r;
                return r;
            }

            static void Sync(ulong lotId)
            {
                if (lotId == 0) return;
                int built = sTable.Builds, refused = sTable.Refused;
                DoorChannel.Sync(lotId, sTable, sRead, sReadInfo, sSend);
                int[] c = PatchCounters();
                c[104] += sTable.Builds - built;
                c[105] += sTable.Refused - refused;
            }

            // one channel message: GetOption2 on a handle-0 route (RouteBackoff.cs MayYield's probe; the handle is ignored)
            static readonly DoorChannel.Sender sSend = delegate(ulong o) { return sYieldProbe.GetOption2((Route.RouteOption2)o); };

            // Every CommonDoor on the lot (Lot.GetObjects, the native lot object query); false on the first door that is
            // gone or whose lock state cannot be mirrored (DoorRecord.Make).
            static bool ReadLotDoors(ulong lotId, List<DoorRecord> into)
            {
                Lot lot = LotManager.GetLot(lotId);
                if (lot == null || lot.HasBeenDestroyed) return false;
                foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
                {
                    if (d == null || d.HasBeenDestroyed) return false;
                    SimDescription owner = d.LockOwner;
                    Household h = owner != null ? owner.Household : null;
                    int kind = (d is Door ? DoorRecord.kIsDoor : 0) | (d is IBarnDoor ? DoorRecord.kBarnDoor : 0)
                             | (d is GateDouble ? DoorRecord.kGateDouble : 0) | (d is IElectronicGate ? DoorRecord.kElectronicGate : 0)
                             | (d.CanLargeAnimalUse ? DoorRecord.kLargeAnimalUse : 0) | (d is DoorSingleGender ? DoorRecord.kSingleGender : 0)
                             | (Overridden(d.GetType()) ? DoorRecord.kOverridden : 0) | GenderBits(d);
                    DoorRecord r = DoorRecord.Make(d.ObjectId.Value, (uint)d.LockType, owner != null ? owner.SimDescriptionId : 0,
                                                   h != null ? h.HouseholdId : 0, d.OwnerList, d.OneWayRoomDestinationId, d.GetRoomConnections(), kind);
                    if (r == null) return false;
                    into.Add(r);
                }
                return true;
            }

            // the type declaring the door type's IsAllowedThrough(int, int, Sim) (null: none found). Overridden = not CommonDoor's
            // own (unknown counts as overridden).
            static readonly Dictionary<Type, Type> sAllowedOwner = new Dictionary<Type, Type>();
            static Type AllowedOwner(Type t)
            {
                Type o;
                if (sAllowedOwner.TryGetValue(t, out o)) return o;
                System.Reflection.MethodInfo m = t.GetMethod("IsAllowedThrough", new Type[] { typeof(int), typeof(int), typeof(Sim) });
                o = m != null ? m.DeclaringType : null;
                sAllowedOwner[t] = o;
                return o;
            }
            static bool Overridden(Type t) { return AllowedOwner(t) != typeof(CommonDoor); }

            // [DoorFast] rule d (v920): kGenderOwn when IsAllowedThrough is DoorSingleGender's own, with the door's mGender as
            // kDoorMale / kDoorFemale (an unreadable or other value: no gender bit, no prediction)
            static System.Reflection.FieldInfo sGenderField;
            static int GenderBits(CommonDoor d)
            {
                if (!(d is DoorSingleGender) || AllowedOwner(d.GetType()) != typeof(DoorSingleGender)) return 0;
                if (sGenderField == null) sGenderField = typeof(DoorSingleGender).GetField("mGender", kAnyInstance);
                object g = sGenderField != null ? sGenderField.GetValue(d) : null;
                if (!(g is CASAgeGenderFlags)) return DoorRecord.kGenderOwn;
                CASAgeGenderFlags v = (CASAgeGenderFlags)g;
                return DoorRecord.kGenderOwn | (v == CASAgeGenderFlags.Male ? DoorRecord.kDoorMale : v == CASAgeGenderFlags.Female ? DoorRecord.kDoorFemale : 0);
            }

            // [DoorFast] rule b: portal types with no rule of their own in Sim.OnPortalPathPlan (docs/105 §2 steps 3-6), the ones
            // seen in the managed shadow (doorplain, 0 mismatches); subclasses come with them, minus any CommonDoor or rule type
            static readonly DoorChannel.LotInfoReader sReadInfo = ReadLotInfo;
            static readonly Dictionary<Type, bool> sRuleType = new Dictionary<Type, bool>();
            static bool RuleType(Type t)  // a CommonDoor, or a type (or base) with its own rule in Sim.OnPortalPathPlan
            {
                bool r;
                if (sRuleType.TryGetValue(t, out r)) return r;
                for (Type b = t; b != null && !r; b = b.BaseType)
                {
                    switch (b.FullName)
                    {
                        case "Sims3.Gameplay.Abstracts.CommonDoor":
                        case "Sims3.Gameplay.Objects.FirefighterPole":
                        case "Sims3.Gameplay.Objects.Pets.BoxStall":
                        case "Sims3.Gameplay.Objects.HobbiesSkills.HorseJump":
                        case "Sims3.Gameplay.Objects.Elevator.ElevatorDoors":
                        case "Sims3.Gameplay.Pools.DivingBoard":
                        case "Sims3.Gameplay.Objects.Ladder":
                            r = true;
                            break;
                    }
                }
                sRuleType[t] = r;
                return r;
            }
            static void AddRuleB<T>(Lot lot, List<ulong> into) where T : GameObject
            {
                foreach (T o in lot.GetObjects<T>()) if (o != null && !o.HasBeenDestroyed && !RuleType(o.GetType())) into.Add(o.ObjectId.Value);
            }

            static System.Reflection.FieldInfo sHandlerField, sSavedDataField;
            const System.Reflection.BindingFlags kAnyInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

            // At push time only (sim thread, lot table invalid natively). Null = the lot cannot be read: no extras, no prediction.
            static LotInfo ReadLotInfo(ulong lotId)
            {
                Lot lot = LotManager.GetLot(lotId);
                if (lot == null || lot.HasBeenDestroyed) return null;
                LotInfo info = new LotInfo();
                List<ulong> b = new List<ulong>();
                AddRuleB<Sims3.Gameplay.Core.Stairs>(lot, b);
                AddRuleB<Sims3.Gameplay.Abstracts.StaircaseSpiral>(lot, b);
                AddRuleB<Sims3.Gameplay.Pools.Pool>(lot, b);
                AddRuleB<Sims3.Gameplay.Pools.PoolLadder>(lot, b);
                AddRuleB<Sims3.Gameplay.Objects.Environment.BuildableFountain>(lot, b);
                AddRuleB<Sims3.Gameplay.Objects.Elevator.ElevatorInterior>(lot, b);
                AddRuleB<Sims3.Gameplay.Core.Platform>(lot, b);
                AddRuleB<Sims3.Gameplay.Objects.Miscellaneous.VelvetRopes>(lot, b);
                AddRuleB<Sims3.Gameplay.Objects.RabbitHoles.Subway>(lot, b);
                info.RuleB = b.ToArray();
                // coverage (v919): typed records for the other portal owners, kind = ts3native's dfType index; kinds 0-5 have a rule of
                // their own in Sim.OnPortalPathPlan (steps 4-7), 6-9 none (LotInfo.kTypePlain); first kind wins, rule-b ids excluded.
                // v920: kinds 0, 4, 5 carry ts3native rules c, f, e, so only the exact EA type gets them (a subclass may override what
                // the rule mirrors; it is left out, and a later kind it also is may take it)
                Dictionary<ulong, bool> seen = new Dictionary<ulong, bool>();
                foreach (ulong id in b) seen[id] = true;
                List<ulong> tIds = new List<ulong>();
                List<uint> tKinds = new List<uint>();
                AddTyped<Sims3.Gameplay.Objects.Elevator.ElevatorDoors>(lot, 0, true, seen, tIds, tKinds);
                AddTyped<Sims3.Gameplay.Objects.FirefighterPole>(lot, 1, false, seen, tIds, tKinds);
                AddTyped<IBoxStall>(lot, 2, false, seen, tIds, tKinds);
                AddTyped<IHorseJump>(lot, 3, false, seen, tIds, tKinds);
                AddTyped<Sims3.Gameplay.Pools.DivingBoard>(lot, 4, true, seen, tIds, tKinds);
                AddTyped<Sims3.Gameplay.Objects.Ladder>(lot, 5, true, seen, tIds, tKinds);
                AddTyped<Sims3.Gameplay.Objects.TubeElevator>(lot, 6, false, seen, tIds, tKinds);
                AddTyped<Sims3.Gameplay.Pools.WaterSlide>(lot, 7, false, seen, tIds, tKinds);
                AddTyped<Sims3.Gameplay.Objects.Island.Houseboat>(lot, 8, false, seen, tIds, tKinds);
                AddTyped<Sims3.Gameplay.Objects.Island.HouseboatJig>(lot, 9, false, seen, tIds, tKinds);
                info.Typed = tIds.ToArray();
                info.TypedKind = tKinds.ToArray();
                // one handler: only Sim.OnPortalPathPlan (Lot.PostWorldLoad's); a PrivacySituation or a mod handler can lock any portal.
                // Trusted only while the accessor prefixes are in: they invalidate the lot before the handler set changes.
                if (sHandlerField == null) sHandlerField = typeof(Lot).GetField("mOnPortalPathPlanHandler", kAnyInstance);
                Delegate h = sHandlerField != null ? sHandlerField.GetValue(lot) as Delegate : null;
                Delegate[] list = h != null ? h.GetInvocationList() : null;
                if (sPortalPlanReady && list != null && list.Length == 1 && list[0].Method != null && list[0].Method.DeclaringType == typeof(Sim)
                    && list[0].Method.Name == "OnPortalPathPlan") info.Flags |= LotInfo.kOneHandler;
                // v920: rule c (ElevatorDoors.IsAllowedThrough reads its lot's HasVirtualResidentialSlots), rule d (DoorSingleGender's
                // resort branch). shortcut: a lot type change is not known to invalidate the table; a stale kApartment only stops rule c,
                // a stale missing one needs a lot with saved virtual slots made residential (the Mode 2 self-check then sets dfFail)
                if (lot.HasVirtualResidentialSlots) info.Flags |= LotInfo.kApartment;
                if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort) info.Flags |= LotInfo.kResort;
                // hidden rooms (Lot.IsRoomHidden reads mSavedData.mHiddenRooms; set only from world-builder markers at load and
                // rebuild); the ids go to ts3native, which tests the asked room pair (coverage); unreadable = null (no door predicted)
                if (sSavedDataField == null) sSavedDataField = typeof(Lot).GetField("mSavedData", kAnyInstance);
                object sd = sSavedDataField != null ? sSavedDataField.GetValue(lot) : null;
                System.Reflection.FieldInfo hf = sd != null ? sd.GetType().GetField("mHiddenRooms", kAnyInstance) : null;
                List<int> hr = hf != null ? hf.GetValue(sd) as List<int> : null;
                info.Hidden = hr != null ? hr.ToArray() : null;
                return info;
            }
            static void AddTyped<T>(Lot lot, uint kind, bool exact, Dictionary<ulong, bool> seen, List<ulong> ids, List<uint> kinds) where T : class
            {
                foreach (T x in lot.GetObjects<T>())
                {
                    GameObject o = x as GameObject;
                    if (o == null || o.HasBeenDestroyed || o is CommonDoor || seen.ContainsKey(o.ObjectId.Value) || (exact && o.GetType() != typeof(T))) continue;
                    seen[o.ObjectId.Value] = true;
                    ids.Add(o.ObjectId.Value);
                    kinds.Add(kind);
                }
            }
        }

        // Prefixes on Lot.add_PortalPathPlan / remove_PortalPathPlan ([DoorFast], docs/548): the lot's door table is invalidated
        // (here and in ts3native) before the handler set changes, whatever the switch says, so ts3native never predicts from a
        // lot flag "only the Sim handler" that no longer holds. The game's accessor body runs unchanged after.
        public class PortalPlanHook
        {
            public void AddHook(PortalPathPlanHandler value) { PortalPlanChanging((Lot)(object)this); AddOrig(value); }
            public void AddOrig(PortalPathPlanHandler value) { } // receives Lot.add_PortalPathPlan's original header
            public void RemoveHook(PortalPathPlanHandler value) { PortalPlanChanging((Lot)(object)this); RemoveOrig(value); }
            public void RemoveOrig(PortalPathPlanHandler value) { } // receives Lot.remove_PortalPathPlan's original header
        }
        static void PortalPlanChanging(Lot lot)
        {
            try { if (lot != null) DoorMirror.Invalidate(lot.LotId); else DoorMirror.InvalidateAll(); }
            catch (Exception) { try { DoorMirror.InvalidateAll(); } catch (Exception) { } }
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
