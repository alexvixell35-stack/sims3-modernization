using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Batch 7 (docs/582 B7; REA tranche 3, docs/584 §6, plus NRaas lead #42 of docs/data/external-bug-leads-1115.md).
    // All default off. simler90 changes none of the methods below (docs/data/mod-method-diff.csv). T3 (ResortManager
    // RefreshVIPRooms) is already fixed by viproomrefresh (VipRoomRefresh.cs, docs/555 §3) and is not repeated here.
    public static partial class Fixes
    {
        // ---- B7 §1 boatstaleswimmer (tuning kBoatStaleSwimmer) -----------------------------------------------------------
        // [verified, decompiled] BoatMultipleOccupancy.OnRouteStarted walks mPassengersInTheWater.Keys (a read-only view
        // of the dictionary's key List) and removes a key whose passenger has no created Sim without leaving the loop:
        // the next MoveNext throws InvalidOperationException. The re-board wait and base.OnRouteStarted are skipped, so
        // swimmers are not waited for. Fix: just before EA's body, the stale keys (no created Sim) are removed, which is
        // what EA's loop meant to do; EA's loop then only meets live passengers. Household-visible: a household boat now
        // waits for its swimmers as EA intended.
        // Counters: [2905] stale keys removed, [2906] routes that would have thrown with the switch off, [2907] errors.
        static readonly FieldInfo sBsWater = typeof(BoatOwnable).GetField("mPassengersInTheWater", kInstAll);

        // Pure: the keys EA's loop removes (no created Sim), in key order.
        internal static List<ulong> BsStale(IList<ulong> keys, Predicate<ulong> created)
        {
            List<ulong> r = new List<ulong>();
            foreach (ulong k in keys) if (!created(k)) r.Add(k);
            return r;
        }
        // Pure: does EA's loop throw? It does when a stale key is not the last key it visits.
        internal static bool BsThrows(IList<ulong> keys, Predicate<ulong> created)
        {
            for (int i = 0; i < keys.Count - 1; i++) if (!created(keys[i])) return true;
            return false;
        }

        static void ApplyBoatStaleSwimmer()
        {
            if (sBsWater == null) { AppliedLog().Add("boatstaleswimmer: not found: BoatOwnable.mPassengersInTheWater"); return; }
            Patch(typeof(BoatMultipleOccupancy), "OnRouteStarted", Type.EmptyTypes, typeof(BBsHook), "StartHook", "StartOrig");
        }

        public class BBsHook
        {
            public void StartHook()
            {
                try
                {
                    PairedListDictionary<ulong, Slot> water = sBsWater.GetValue(this) as PairedListDictionary<ulong, Slot>;
                    if (water != null && water.Count > 0)
                    {
                        List<ulong> keys = new List<ulong>(water.Keys);
                        Predicate<ulong> created = delegate (ulong id) { return SimDescription.GetCreatedSim(id) != null; };
                        if (!ShortcutOn("boatstaleswimmer")) { if (BsThrows(keys, created)) PatchCounters()[2906]++; }
                        else foreach (ulong k in BsStale(keys, created)) { water.Remove(k); PatchCounters()[2905]++; }
                    }
                }
                catch (Exception) { PatchCounters()[2907]++; }
                StartOrig();
            }
            public void StartOrig() { } // receives BoatMultipleOccupancy.OnRouteStarted
        }

        // ---- B7 §2 oppcategoryprotect (tuning kOppCategoryProtect) -------------------------------------------------------
        // [verified, decompiled; reachability inferred] OpportunityManager.AcceptOpportunity(toAdd, prevSource, prevTarget)
        // adds MiniSim FullFromOpportunities protection to the source and target Sims first, then returns early when the
        // opportunity's shared data is missing or its category is already filled. Nothing removes that protection on the
        // early-return path (only the opportunity's own cleanup does, which never runs), and the flag list counts
        // duplicates, so the NPC is never culled. Fix at the source: on that early-return path the method is not run at
        // all (all it would do is the protection), i.e. the protection comes after EA's check. Not household-visible
        // (NPC culling only; the opportunity outcome is EA's either way).
        // Counters: [2908] early returns kept unprotected, [2909] seen with the switch off, [2910] errors.
        static readonly FieldInfo sOaShared = typeof(GenericManager<OpportunityNames, Opportunity, Opportunity>).GetField("sDictionary", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sOaValues = typeof(GenericManager<OpportunityNames, Opportunity, Opportunity>).GetField("mValues", kInstAll);

        // Pure: does EA return early (so the protection would be its only effect)?
        internal static bool OaEarly(bool sharedFound, bool categoryTaken) { return !sharedFound || categoryTaken; }

        static void ApplyOppCategoryProtect()
        {
            Type pi = typeof(OpportunityManager).GetNestedType("OpportunityPropagateInfo", BindingFlags.Public | BindingFlags.NonPublic);
            if (sOaShared == null || sOaValues == null || pi == null) { AppliedLog().Add("oppcategoryprotect: not found: GenericManager.sDictionary/mValues or OpportunityPropagateInfo"); return; }
            Patch(typeof(OpportunityManager), "AcceptOpportunity", new Type[] { typeof(Opportunity), pi, pi }, typeof(BOaHook), "AcceptHook", "AcceptOrig");
        }

        public class BOaHook
        {
            public void AcceptHook(Opportunity toAdd, object prevSource, object prevTarget)
            {
                try
                {
                    if (toAdd != null)
                    {
                        Dictionary<ulong, Opportunity> shared = sOaShared.GetValue(null) as Dictionary<ulong, Opportunity>;
                        Dictionary<ulong, Opportunity> values = sOaValues.GetValue(this) as Dictionary<ulong, Opportunity>;
                        Opportunity v = null;
                        if (shared != null) shared.TryGetValue((ulong)toAdd.Guid, out v);
                        if (shared != null && values != null && OaEarly(v != null, v != null && values.ContainsKey((ulong)v.OpportunityCategory)))
                        {
                            if (ShortcutOn("oppcategoryprotect")) { PatchCounters()[2908]++; return; }
                            PatchCounters()[2909]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2910]++; }
                AcceptOrig(toAdd, prevSource, prevTarget);
            }
            public void AcceptOrig(Opportunity toAdd, object prevSource, object prevTarget) { } // receives OpportunityManager.AcceptOpportunity(toAdd, info, info)
        }

        // ---- B7 §3 nocommercialcar (tuning kNoCommercialCar) -------------------------------------------------------------
        // [verified, decompiled] CarNpcWithNoDriver+LiteDriver.Simulate picks a random commercial lot other than the car's
        // own (RandomUtil.GetRandomObjectFromList) with no emptiness test. In a world with at most one commercial lot the
        // pick throws ArgumentException on the first pass (only ResetException is caught), a script error, and the car is
        // destroyed in finally. Fix: with no other commercial lot, our copy of EA's loop without the drive step runs: the
        // car waits EA's random interval until it should drive off, then EA's DriveOffscreenAndRelease; reset and
        // destroy as EA. With a lot to drive to, EA's code. Not household-visible (ambient traffic).
        // Counters: [2911] cars idled instead of throwing, [2912] seen with the switch off, [2913] errors.
        static Type sNcType;
        static FieldInfo sNcCar;

        // Pure: does EA's pick throw (no other commercial lot)?
        internal static bool NcNoTarget(int otherCommercialLots) { return otherCommercialLots <= 0; }

        static void ApplyNoCommercialCar()
        {
            sNcType = typeof(CarNpcWithNoDriver).GetNestedType("LiteDriver", BindingFlags.NonPublic | BindingFlags.Public);
            sNcCar = sNcType == null ? null : sNcType.GetField("mCar", kInstAll);
            if (sNcCar == null) { AppliedLog().Add("nocommercialcar: not found: CarNpcWithNoDriver+LiteDriver.mCar"); return; }
            Patch(sNcType, "Simulate", Type.EmptyTypes, typeof(BNcHook), "SimHook", "SimOrig");
        }

        static int NcTargets(CarNpcWithNoDriver car)
        {
            int n = 0;
            foreach (Lot l in LotManager.AllLots) if (car.LotCurrent != l && l.LotType == LotType.Commercial) n++;
            return n;
        }

        public class BNcHook
        {
            public void SimHook()
            {
                CarNpcWithNoDriver car = null;
                try
                {
                    car = sNcCar.GetValue(this) as CarNpcWithNoDriver;
                    if (car == null || !NcNoTarget(NcTargets(car))) car = null;
                    else if (!ShortcutOn("nocommercialcar")) { PatchCounters()[2912]++; car = null; }
                }
                catch (Exception) { PatchCounters()[2913]++; car = null; }
                // re-entry with no car (af4 2026-11-17, 6 NREs): ErrorTrap's ScriptProxy runs Simulate again after a handled script
                // error (EA's own DisplayScriptError returns false, so EA never re-runs) and EA's finally already set mCar null; EA's
                // first line then throws on every re-run. A driver with no car has nothing left to do: end the task.
                if (car == null && ShortcutOn("nocommercialcar") && sNcCar.GetValue(this) == null) return;
                if (car == null) { SimOrig(); return; }
                PatchCounters()[2911]++;
                try
                {
                    car.CarRoutingComponent.OnReset();
                    while (!car.ShouldDriveOff)
                    {
                        if (!car.InWorld) { car.Destroy(); return; }
                        uint ticks = (uint)SimClock.ConvertToTicks(RandomUtil.GetFloat(TrafficManager.kCarWaitingTimeRange[0], TrafficManager.kCarWaitingTimeRange[1]), TimeUnit.Minutes);
                        Simulator.Sleep(ticks);
                    }
                    car.DriveOffscreenAndRelease();
                    sNcCar.SetValue(this, null);
                }
                catch (ResetException) { }
                finally
                {
                    CarNpcWithNoDriver left = sNcCar.GetValue(this) as CarNpcWithNoDriver;  // EA's finally: a car still held is destroyed
                    if (left != null) { left.Destroy(); sNcCar.SetValue(this, null); }
                }
            }
            public void SimOrig() { } // receives CarNpcWithNoDriver+LiteDriver.Simulate
        }

        // ---- B7 §4 scavengernolot (tuning kScavengerNoLot) ---------------------------------------------------------------
        // [verified, decompiled] ScavengerOpportunity.OnAdded spawns each missing item on RandomUtil.GetRandomObjectFromList(
        // LotManager.GetAllCommunityLots()). In a world without community lots that pick throws ArgumentException inside
        // OnAdded, called from AcceptOpportunity after the opportunity was filed: base.OnAdded (the "accepted" event) and
        // the rest of the accept (map tag, HUD refresh) are skipped. With no community lot EA can spawn nothing anyway, so
        // the fix runs only Opportunity.OnAdded there; with community lots, EA's code. Household-visible: a scavenger
        // opportunity accepted in such a world is accepted fully (no script error); no items appear, as in EA.
        // Counters: [2914] accepts without community lots handled, [2915] seen with the switch off, [2916] errors.
        internal static bool ScNoLots(int communityLots) { return communityLots == 0; }

        static void ApplyScavengerNoLot()
        {
            Patch(typeof(ScavengerOpportunity), "OnAdded", Type.EmptyTypes, typeof(BScHook), "AddedHook", "AddedOrig");
        }

        // Derives from Opportunity so `base.OnAdded()` is a non-virtual call to Opportunity.OnAdded.
        public abstract class BScHook : Opportunity
        {
            public void AddedHook()
            {
                bool none = false;
                try
                {
                    List<Lot> lots = LotManager.GetAllCommunityLots();
                    none = ScNoLots(lots == null ? 0 : lots.Count);
                    if (none && !ShortcutOn("scavengernolot")) { PatchCounters()[2915]++; none = false; }
                }
                catch (Exception) { PatchCounters()[2916]++; none = false; }
                if (!none) { AddedOrig(); return; }
                PatchCounters()[2914]++;
                base.OnAdded();
            }
            public void AddedOrig() { } // receives ScavengerOpportunity.OnAdded
        }

        // ---- B7 §5 partylotlistener (tuning kPartyLotListener) -----------------------------------------------------------
        // [verified, decompiled; NRaas Overwatch CleanupHostlessNPCParties fixes the same state at load] NpcParty's
        // WaitForSelectableGuestToArrive and Happening subscribe OnSimLotChanged to the static Sim.OnLotChangedEvent and
        // unsubscribe in Dispose. Situation.Exit / SetState run CleanUp then Dispose; Happening.CleanUp dereferences
        // Parent.Host, so a party whose host is gone throws there and Dispose never runs: the listener stays for good (and
        // is re-added by PostLoad after a load). Each lot change of a selectable Sim then throws in it (Parent.Host.LotHome),
        // and the throw stops Sim.OnLotChangedEvent's other subscribers (jukebox, DJ booth, ...). Fix at the stale
        // listener: called for a party with no parent or no host, it unsubscribes itself and returns. Household-visible:
        // only for the selected Sim's lot changes, which reach every other subscriber again.
        // Counters: [2917] stale listeners removed, [2918] would have thrown with the switch off, [2919] errors.
        static Type sPlWait, sPlHappen;
        static MethodInfo sPlWaitM, sPlHappenM;
        static FieldInfo sPlWaitParent, sPlHappenParent;

        // Pure: is this listener stale (would EA throw for a selectable Sim)?
        internal static bool PlStale(bool hasParent, bool hasHost) { return !hasParent || !hasHost; }

        static void ApplyPartyLotListener()
        {
            sPlWait = typeof(NpcParty).GetNestedType("WaitForSelectableGuestToArrive", BindingFlags.Public | BindingFlags.NonPublic);
            sPlHappen = typeof(NpcParty).GetNestedType("Happening", BindingFlags.Public | BindingFlags.NonPublic);
            Type[] sig = { typeof(Sim), typeof(Lot), typeof(Lot) };
            sPlWaitM = sPlWait == null ? null : sPlWait.GetMethod("OnSimLotChanged", kInstAll, null, sig, null);
            sPlHappenM = sPlHappen == null ? null : sPlHappen.GetMethod("OnSimLotChanged", kInstAll, null, sig, null);
            sPlWaitParent = sPlWait == null ? null : sPlWait.GetField("Parent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            sPlHappenParent = sPlHappen == null ? null : sPlHappen.GetField("Parent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            if (sPlWaitM == null || sPlHappenM == null || sPlWaitParent == null || sPlHappenParent == null) { AppliedLog().Add("partylotlistener: not found: NpcParty child OnSimLotChanged/Parent"); return; }
            Patch(sPlWait, "OnSimLotChanged", sig, typeof(BPlWaitHook), "LotHook", "LotOrig");
            Patch(sPlHappen, "OnSimLotChanged", sig, typeof(BPlHappenHook), "LotHook", "LotOrig");
        }

        // true: handled (stale; the caller returns)
        static bool PlHandle(object self, FieldInfo parentF, MethodInfo m, Sim s)
        {
            try
            {
                if (s == null || !s.IsSelectable) return false;  // EA returns before the throw for these
                NpcParty p = parentF.GetValue(self) as NpcParty;
                if (!PlStale(p != null, p != null && p.Host != null)) return false;
                if (!ShortcutOn("partylotlistener")) { PatchCounters()[2918]++; return false; }
                Sim.OnLotChangedEvent -= (Sim.OnLotChanged)Fixes.Bind(typeof(Sim.OnLotChanged), self, m);
                PatchCounters()[2917]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2919]++; return false; }
        }

        public class BPlWaitHook
        {
            public void LotHook(Sim s, Lot oldLot, Lot newLot) { if (!PlHandle(this, sPlWaitParent, sPlWaitM, s)) LotOrig(s, oldLot, newLot); }
            public void LotOrig(Sim s, Lot oldLot, Lot newLot) { } // receives NpcParty+WaitForSelectableGuestToArrive.OnSimLotChanged
        }
        public class BPlHappenHook
        {
            public void LotHook(Sim s, Lot oldLot, Lot newLot) { if (!PlHandle(this, sPlHappenParent, sPlHappenM, s)) LotOrig(s, oldLot, newLot); }
            public void LotOrig(Sim s, Lot oldLot, Lot newLot) { } // receives NpcParty+Happening.OnSimLotChanged
        }
    }
}
