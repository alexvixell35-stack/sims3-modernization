using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // Batch 10b (docs/588 B10 §16-19): the deferred story-progression callee leads that can be built. All default off.
    // simler90 UPDATE242 changes none of the hooked methods except PetPool.RandomRemovePet, which petpoolghost only
    // prefixes (its body, simler90's or EA's, runs unchanged).
    public static partial class Fixes
    {
        // ---- B10 §16 disposevirtualslot (tuning kDisposeVirtualSlot) -----------------------------------------------------
        // [verified, decompiled; saving inferred] Household.Dispose moves a household out only when it lives on a real home
        // lot. A townie household with only a virtual home (disposed by Edit Town's DestroyHouseholdsWithoutGuardians, EA
        // CheckForAbandonedChildren or Household.Cleanup's last loop) stays in Lot.mVirtualSlotHouseholds, the NPC door map
        // and the mailbox: slot counts stay high (FindSuitableVirtualHome weighs by them) and the dead household graph is
        // kept. Fix: Dispose of a household with no real lot and a virtual one first runs EA's own Lot.VirtualMoveOut(this)
        // (what Household.MoveOut does for such a household); the world-load sweep moves out households already gone from
        // the household lists. Only disposed households are touched. Counters: [2974] virtual move-outs at Dispose,
        // [2975] left in their slot (switch off), [2976] stale slot entries swept at load.
        static readonly FieldInfo sDvToDelete = typeof(Household).GetField("sHouseholdToDeleteList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static string sDvMissing = "";

        static void ApplyDisposeVirtualSlot()
        {
            sDvMissing = SpNames("Household.sHouseholdToDeleteList", sDvToDelete);
            if (sDvMissing.Length > 0) AppliedLog().Add("disposevirtualslot: not found: " + sDvMissing);
            Patch(typeof(Household), "Dispose", Type.EmptyTypes, typeof(BDvHook), "DisposeHook", "DisposeOrig");
        }

        // Pure: does Dispose leave a virtual slot behind? (EA moves out only from a real home lot)
        internal static bool DvLeaves(bool realLot, bool virtualLot) { return !realLot && virtualLot; }

        public class BDvHook
        {
            public void DisposeHook()
            {
                try
                {
                    Household h = (Household)(object)this;
                    if (DvLeaves(h.LotHome != null, h.VirtualLotHome != null))
                    {
                        if (ShortcutOn("disposevirtualslot")) { h.VirtualLotHome.VirtualMoveOut(h); PatchCounters()[2974]++; }
                        else PatchCounters()[2975]++;
                    }
                }
                catch (Exception) { }
                DisposeOrig();
            }

            public void DisposeOrig() { }
        }

        internal static void DisposeVirtualSlotWorldLoad()
        {
            if (!ShortcutOn("disposevirtualslot") || sDvMissing.Length > 0) return;
            Dictionary<Household, bool> live = new Dictionary<Household, bool>();
            foreach (Household h in Household.sHouseholdList) if (h != null) live[h] = true;
            List<Household> del = sDvToDelete.GetValue(null) as List<Household>;
            if (del != null) foreach (Household h in del) if (h != null) live[h] = true;
            foreach (Lot lot in LotManager.AllLots)
            {
                List<Household> slots = lot.VirtualSlotHouseholds;
                if (slots == null || slots.Count == 0) continue;
                List<Household> stale = null;
                foreach (Household h in slots)
                    if (h != null && !live.ContainsKey(h)) { if (stale == null) stale = new List<Household>(); stale.Add(h); }
                if (stale == null) continue;
                lot.VirtualMoveOut(stale);
                PatchCounters()[2976] += stale.Count;
            }
        }

        // ---- B10 §17 agingalarmcancel (tuning kAgingAlarmCancel) ---------------------------------------------------------
        // [verified, decompiled; cost only] AgingManager's day pass arms AgingState.GraduationEarlyMessageAlarm for a teen
        // close to aging, overwriting the handle, and CancelAgingAlarmsForSim, which removes every other aging alarm, never
        // removes it, so a cancel-and-rearm pass leaves the older alarm armed beside the new one. Fix: CancelAgingAlarmsForSim
        // also removes that alarm (EA's own remove-and-reset, as ShowGraduationEarlyMessageCallback does). The day pass
        // re-arms it as before. Household-visible: no change to what is shown (the message is still given once, guarded by
        // GraduationEarlyMessageGiven). Counters: [2977] graduation alarms cancelled, [2978] left armed (switch off), [2979]
        // errors.
        static readonly FieldInfo sAcGrad = typeof(AgingState).GetField("GraduationEarlyMessageAlarm", kInstAll);
        static string sAcMissing = "";

        static void ApplyAgingAlarmCancel()
        {
            sAcMissing = SpNames("AgingState.GraduationEarlyMessageAlarm", sAcGrad);
            if (sAcMissing.Length > 0) { AppliedLog().Add("agingalarmcancel: not found: " + sAcMissing); return; }
            Patch(typeof(AgingManager), "CancelAgingAlarmsForSim", new Type[] { typeof(AgingState) }, typeof(BAcHook), "CancelHook", "CancelOrig");
        }

        // Pure: does the cancel leave an armed graduation alarm?
        internal static bool AcLeaves(bool on, bool armed) { return armed && !on; }

        public class BAcHook
        {
            public void CancelHook(AgingState agingState)
            {
                if (agingState != null && sAcMissing.Length == 0)
                {
                    try
                    {
                        AlarmHandle h = (AlarmHandle)sAcGrad.GetValue(agingState);
                        bool armed = h != AlarmHandle.kInvalidHandle;
                        if (AcLeaves(ShortcutOn("agingalarmcancel"), armed)) PatchCounters()[2978]++;
                        else if (armed)
                        {
                            AlarmManager.Global.RemoveAlarm(h);
                            sAcGrad.SetValue(agingState, AlarmHandle.kInvalidHandle);
                            PatchCounters()[2977]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[2979]++; }
                }
                CancelOrig(agingState);
            }

            public void CancelOrig(AgingState agingState) { }
        }

        // ---- B10 §18 virtualmoveindup (tuning kVirtualMoveInDup) ---------------------------------------------------------
        // [inferred] Lot.VirtualMoveIn(SimDescription) adds the Sim to mVirtualSlotSims with no Contains test (its Household
        // overload has the same list kind and EA's VirtualMoveOut removes one entry): a second virtual move-in of the same
        // service Sim counts it twice in VirtualSlotsFilled, and one move-out leaves an entry behind. Fix: an already
        // listed Sim only gets EA's SetVirtualLot(this). Counters: [2980] repeat move-ins not added twice, [2981] added
        // twice (switch off), [2982] errors.
        static void ApplyVirtualMoveInDup()
        {
            Patch(typeof(Lot), "VirtualMoveIn", new Type[] { typeof(SimDescription) }, typeof(BVmHook), "MoveInHook", "MoveInOrig");
        }

        // Pure: does EA add a duplicate?
        internal static bool VmDup(bool worldLot, bool listed) { return !worldLot && listed; }

        public class BVmHook
        {
            public void MoveInHook(SimDescription simDesc)
            {
                try
                {
                    Lot lot = (Lot)(object)this;
                    List<SimDescription> sims = lot.VirtualSlotSims;
                    if (simDesc != null && VmDup(lot.IsWorldLot, sims != null && sims.Contains(simDesc)))
                    {
                        if (ShortcutOn("virtualmoveindup")) { simDesc.SetVirtualLot(lot); PatchCounters()[2980]++; return; }
                        PatchCounters()[2981]++;
                    }
                }
                catch (Exception) { PatchCounters()[2982]++; }
                MoveInOrig(simDesc);
            }

            public void MoveInOrig(SimDescription simDesc) { }
        }

        // ---- B10 §19 petpoolghost (tuning kPetPoolGhost) -----------------------------------------------------------------
        // [latent] PetPool.RandomRemovePet picks a random id from mSimDescriptionIds and removes the pet only when the pet
        // household still has a member with that id; an id whose pet is gone is never removed, yet still counts in
        // GetPoolSize/IsFull, so a pool of such ids stays "full" and can draw nothing. Fix: before the pick (EA's or
        // simler90's body, unchanged), ids with no pet-household member are dropped from the pool list. Counters: [2983]
        // ghost ids dropped, [2984] ghost ids seen (switch off), [2985] errors.
        static readonly FieldInfo sPpgIds = typeof(PetPool).GetField("mSimDescriptionIds", kInstAll);
        static string sPpgMissing = "";

        static void ApplyPetPoolGhost()
        {
            sPpgMissing = SpNames("PetPool.mSimDescriptionIds", sPpgIds);
            if (sPpgMissing.Length > 0) { AppliedLog().Add("petpoolghost: not found: " + sPpgMissing); return; }
            Patch(typeof(PetPool), "RandomRemovePet", null, typeof(BPgHook), "RemoveHook", "RemoveOrig");
        }

        // Pure: the ids kept. member(id) says whether the pet household has it.
        internal static List<ulong> PgKeep(List<ulong> ids, Predicate<ulong> member)
        {
            List<ulong> keep = new List<ulong>();
            foreach (ulong id in ids) if (member(id)) keep.Add(id);
            return keep;
        }

        public class BPgHook
        {
            public bool RemoveHook()
            {
                if (sPpgMissing.Length == 0)
                {
                    try
                    {
                        List<ulong> ids = sPpgIds.GetValue(this) as List<ulong>;
                        Household pets = Household.PetHousehold;
                        if (ids != null && pets != null && pets.CurrentMembers != null)
                        {
                            List<ulong> keep = PgKeep(ids, delegate (ulong id) { return pets.CurrentMembers.GetSimDescriptionFromId(id) != null; });
                            int ghosts = ids.Count - keep.Count;
                            if (ghosts > 0)
                            {
                                if (ShortcutOn("petpoolghost"))
                                {
                                    ids.Clear();
                                    ids.AddRange(keep);
                                    PatchCounters()[2983] += ghosts;
                                }
                                else PatchCounters()[2984] += ghosts;
                            }
                        }
                    }
                    catch (Exception) { PatchCounters()[2985]++; }
                }
                return RemoveOrig();
            }

            public bool RemoveOrig() { return false; }
        }
    }
}
