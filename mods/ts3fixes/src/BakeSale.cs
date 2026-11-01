using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // bakesaleseller (issue #102, docs/225): BakeSaleTable.mSeller is set by TendTable and cleared only by StopTending and
    // the table's own reset. A seller who is reset or deleted while tending stays the seller, and HasSeller is only
    // mSeller != null. With a deleted seller (HasBeenDestroyed, InteractionQueue null) every PurchaseAutonomously passes
    // its test and throws in Run, and an NPC table's clean-up handler throws after it has removed its own alarm, so the
    // table is never cleaned up and every later buyer throws too. The save keeps the reference.
    // Fix, switch on: GetSeller reads a destroyed seller, or one with no interaction queue, as no seller; HasSeller is
    // GetSeller != null, so the buyers' test and the clean-up handler agree; OnStartup gives an NPC table with no clean-up
    // alarm a new one a sim-minute out (only a table orphaned this way has none: SetUpForNPC sets it, and the handler that
    // clears it destroys the table), and EA's handler then removes the table. A reset seller who is still alive stays the
    // seller, as in EA's game. The OnStartup part runs at load only. [683] dead-seller reads, [684] orphaned tables re-armed.
    public static partial class Fixes
    {
        static readonly FieldInfo sBstSeller = typeof(BakeSaleTable).GetField("mSeller", kInstAll);
        static readonly FieldInfo sBstNpc = typeof(BakeSaleTable).GetField("mIsNPCTable", kInstAll);
        static readonly FieldInfo sBstAlarm = typeof(BakeSaleTable).GetField("mNPCCleanUpAlarm", kInstAll);
        static readonly MethodInfo sBstHandler = typeof(BakeSaleTable).GetMethod("NPCCleanUpAlarmHandler", kInstAll, null, Type.EmptyTypes, null);
        static bool sBstLogged;

        public class BakeSaleHook
        {
            public Sim GetSellerHook()
            {
                Sim s = GetSellerOrig();
                if (s == null || !BakeSaleCore.SellerGone(true, s.HasBeenDestroyed, s.InteractionQueue != null) || !ShortcutOn("bakesaleseller")) return s;
                PatchCounters()[683]++;
                if (!sBstLogged)
                {
                    sBstLogged = true;
                    ShortcutLog("bakesaleseller: a bake sale table's seller " + s.FullName + " is gone (destroyed " + s.HasBeenDestroyed
                        + "), read as no seller; NPC table " + (sBstNpc != null ? sBstNpc.GetValue(this) : "?"));
                }
                return null;
            }
            public bool HasSellerHook()
            {
                if (!ShortcutOn("bakesaleseller")) return HasSellerOrig();
                return ((BakeSaleTable)(object)this).GetSeller != null;
            }
            public void StartupHook()
            {
                StartupOrig();
                if (ShortcutOn("bakesaleseller")) BakeSaleRearm((BakeSaleTable)(object)this);
            }
            public Sim GetSellerOrig() { return null; } // receives BakeSaleTable.get_GetSeller's original header
            public bool HasSellerOrig() { return false; } // receives BakeSaleTable.get_HasSeller's original header
            public void StartupOrig() { } // receives BakeSaleTable.OnStartup's original header
        }

        // an NPC table without its clean-up alarm: give it EA's alarm (same handler, type and delay as SetUpForNPC's re-arm)
        static bool BakeSaleRearm(BakeSaleTable t)
        {
            if (sBstNpc == null || sBstAlarm == null || sBstHandler == null) return false;
            if (!BakeSaleCore.NeedsRearm((bool)sBstNpc.GetValue(t), (AlarmHandle)sBstAlarm.GetValue(t) != AlarmHandle.kInvalidHandle, t.HasBeenDestroyed)) return false;
            AlarmTimerCallback cb = (AlarmTimerCallback)Delegate.CreateDelegate(typeof(AlarmTimerCallback), t, sBstHandler);
            sBstAlarm.SetValue(t, t.AddAlarm(1f, TimeUnit.Minutes, cb, "BakeSaleTable - NPC Clean up alarm (ts3fixes: orphaned table)", AlarmType.AlwaysPersisted));
            PatchCounters()[684]++;
            ShortcutLog("bakesaleseller: NPC bake sale table at " + t.Position + " had no clean-up alarm; new alarm in 1 sim-minute");
            return true;
        }

        static BakeSaleTable sBstTable;
        static bool sBstCleaned; // the cleanup handler returned, so it called Destroy()
        static object sBstKeepEA; // a patched Objects build's own switch (BakeSaleTable.kKeepEADeadSeller), set to EA for the check
        // Agent checks for #102 (repro bake-sale-dead-seller-check). They build the bug's state on a test table out of the world:
        //   "start":   a new NPC table (tableBakeSale, EP2), a new Sim made its seller, then the Sim deleted as remove_sims does.
        //   "state":   the seller as stored, HasSeller and GetSeller (through the switch), the clean-up alarm, counters [683-684].
        //   "cleanup": runs the table's NPCCleanUpAlarmHandler, as its alarm would: EA throws on the deleted seller and keeps the
        //              table; with the switch on the handler sees no seller and destroys the table.
        //   "rearm":   clears the alarm and runs the load-time part (BakeSaleRearm), as OnStartup does for an orphaned table.
        //   "stop":    destroys the test table if it still exists.
        public static string BakeSaleCheck(string what)
        {
            switch (what)
            {
                case "start":
                {
                    if (sBstTable != null) return "a test table exists; stop first";
                    if (sBstSeller == null || sBstNpc == null || sBstAlarm == null || sBstHandler == null) return "BakeSaleTable members not found";
                    Household home = null;
                    foreach (Household h in Household.sHouseholdList)
                        if (!h.IsActive && !h.IsSpecialHousehold && h.LotHome != null) { home = h; break; }
                    if (home == null) return "no non-active household with a home lot";
                    BakeSaleTable t = GlobalFunctions.CreateObjectOutOfWorld("tableBakeSale", ProductVersion.EP2, null, null) as BakeSaleTable;
                    if (t == null) return "tableBakeSale (EP2) could not be created";
                    SimUtils.SimCreationSpec spec = new SimUtils.SimCreationSpec();
                    spec.Age = CASAgeGenderFlags.YoungAdult | CASAgeGenderFlags.Adult;
                    spec.Normalize();
                    SimDescription sd = spec.Instantiate();
                    home.Add(sd);
                    Sim seller = sd.Instantiate(home.LotHome.Position);
                    if (seller == null) { home.Remove(sd); sd.Dispose(true, false, true); t.Destroy(); return "test Sim not instantiated"; }
                    sBstTable = t;
                    sBstNpc.SetValue(t, true);
                    sBstSeller.SetValue(t, seller);
                    home.Remove(sd);
                    sd.Dispose(true, false, true);
                    FieldInfo keep = typeof(BakeSaleTable).GetField("kKeepEADeadSeller", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (keep != null) { sBstKeepEA = keep.GetValue(null); keep.SetValue(null, true); }
                    return "table " + t.ObjectId + ", seller " + seller.FullName + " deleted; switch " + ShortcutOn("bakesaleseller") + (keep != null ? "; patched Objects build set to EA" : "");
                }
                case "state":
                {
                    if (sBstTable == null) return "start first";
                    Sim s = sBstSeller.GetValue(sBstTable) as Sim;
                    int[] c = PatchCounters();
                    // Destroy() finishes after the calling task yields: a "state" read in the handler's own call still sees the table
                    return "table destroyed " + sBstTable.HasBeenDestroyed + (Simulator.GetProxy(sBstTable.ObjectId) == null ? " (proxy gone)" : "") + "; stored seller " + (s == null ? "none" : s.FullName + " (destroyed " + s.HasBeenDestroyed + ", queue " + (s.InteractionQueue == null ? "null" : "set") + ")")
                        + "; HasSeller " + sBstTable.HasSeller + ", GetSeller " + (sBstTable.GetSeller == null ? "null" : "set")
                        + "; alarm " + ((AlarmHandle)sBstAlarm.GetValue(sBstTable) != AlarmHandle.kInvalidHandle ? "set" : "none") + "; dead-seller reads " + c[683] + ", re-armed " + c[684];
                }
                case "cleanup":
                {
                    if (sBstTable == null) return "start first";
                    string r;
                    try { sBstHandler.Invoke(sBstTable, null); r = "handler returned"; sBstCleaned = true; }
                    catch (TargetInvocationException e) { r = "handler threw " + (e.InnerException != null ? e.InnerException.GetType().Name : "?"); }
                    return r + "; " + BakeSaleCheck("state");
                }
                case "rearm":
                {
                    if (sBstTable == null) return "start first";
                    if (sBstTable.HasBeenDestroyed) return "the table is destroyed (start again)";
                    AlarmHandle old = (AlarmHandle)sBstAlarm.GetValue(sBstTable);
                    if (old != AlarmHandle.kInvalidHandle) sBstTable.RemoveAlarm(old);
                    sBstAlarm.SetValue(sBstTable, AlarmHandle.kInvalidHandle);
                    bool on = ShortcutOn("bakesaleseller");
                    return (on ? "re-armed " + BakeSaleRearm(sBstTable) : "switch off: EA has no re-arm") + "; " + BakeSaleCheck("state");
                }
                case "stop":
                {
                    if (sBstTable != null && !sBstCleaned && !sBstTable.HasBeenDestroyed) sBstTable.Destroy(); // EA's handler already destroyed it
                    sBstTable = null; sBstCleaned = false;
                    FieldInfo keep = typeof(BakeSaleTable).GetField("kKeepEADeadSeller", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (keep != null && sBstKeepEA != null) keep.SetValue(null, sBstKeepEA);
                    sBstKeepEA = null;
                    return "stopped";
                }
                default: return "unknown check " + what + " (start, state, cleanup, rearm, stop)";
            }
        }
    }
}
