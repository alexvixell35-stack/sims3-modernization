using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/646 §2 trashoutsidefallback (tuning kTrashOutsideFallback, default off; kTrashOutsideFallbackHours 4).
    // [verified, decompiled TrashcanIndoor / TrashPile] An NPC's EmptyTrash on an indoor can makes a trash bag and pushes
    // TrashPile.DisposeOutside as a must-run continuation (its only caller); DisposeOutside.Run calls
    // PutHeldTrashInCan(forceOutdoor: true), which only looks for TrashcanOutside / RecycleBinOutdoor. When the route to that
    // can fails, the forced branch puts the bag down on the floor (PutTrashDown, not destroyed) and returns false. DisposeTrash
    // (autonomy, non-forced) later carries the pile to the closest indoor can, the can fills, EmptyTrash runs again: a loop.
    // [measured, routecause lines, phase-20261115..19] Bridgeport towers: the Sim holds the bag on floor 1 (y 64.8), the
    // lot's only outdoor can is 30 m below (y 34.8) and the plan ends NoPath at once (0 nodes); 1375 fails in 109 runs.
    // This fix, for non-exempt NPCs only:
    //   - the first failure is EA's own (the bag is put down); a no-path plan seen during that forced call remembers the lot
    //     for kTrashOutsideFallbackHours sim-hours (TrashOutsideFallbackCore);
    //   - DisposeOutside on a remembered lot runs EA's non-forced disposal (PutHeldTrashInCan with forceOutdoor false: chute,
    //     best indoor or outdoor can, recycle bin), or, when that search has nothing to offer (EA would then destroy the bag),
    //     puts the bag down as EA's forced branch does on failure;
    //   - autonomy does not pick EmptyTrash on an indoor can of a remembered lot (refused in EmptyTrash.Definition.Test).
    //   After every call we made, a bag still held by the Sim is put down (TrashPile.PutTrashDown, not destroyed): the held
    //   object is never lost and never left parented to the Sim.
    // Household rule: Fixes.HouseholdExempt and IsPlayerSim (unknown scope = household; the selected Sim is never touched).
    // Counters [4610] non-forced fallbacks, [4611] bags put down (EA's failure remembered, or no receptacle), [4612] EmptyTrash
    // picks refused, [4613] exempt (household, selected, unknown scope), [4614] errors (and bags we had to put down after a call).
    public static partial class Fixes
    {
        static readonly TrashOutsideFallbackCore sTof = new TrashOutsideFallbackCore();
        static readonly Dictionary<ulong, bool> sTofWatch = new Dictionary<ulong, bool>(), sTofSaw = new Dictionary<ulong, bool>();  // no HashSet in the game's mscorlib
        static int sTofMinute = -1, sTofLogged, sTofLines;

        static void TofLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sTofMinute) { sTofMinute = minute; sTofLogged = 0; }
            if (sTofLogged++ < 10 && sTofLines++ < 300) DiagLine("trashoutsidefallback: " + s);
        }

        static bool TofExempt(Sim s) { return HouseholdExempt(s) || IsPlayerSim(s); }

        static long TofSpan()
        {
            float h = Instantiator.kTrashOutsideFallbackHours;
            return SimClock.ConvertToTicks(h > 0f ? h : 4f, TimeUnit.Hours);
        }

        static bool TofKnown(Lot lot)
        {
            if (lot == null) return false;
            lock (sTof) return sTof.Known(lot.LotId, SimClock.CurrentTicks, TofSpan());
        }

        static void ApplyTrashOutsideFallback()
        {
            Patch(typeof(TrashPile), "PutHeldTrashInCan", new Type[] { typeof(InteractionInstance), typeof(bool), typeof(int), typeof(SacsEventHandler) }, typeof(TofPutHook), "PutHook", "PutOrig");
            Type def = typeof(TrashcanIndoor.EmptyTrash).GetNestedType("Definition", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (def == null) { AppliedLog().Add("trashoutsidefallback: not found: TrashcanIndoor+EmptyTrash+Definition (EmptyTrash picks not refused)"); return; }
            Patch(def, "Test", new Type[] { typeof(Sim), typeof(IAcceptsGarbage), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() }, typeof(TofTestHook), "TestHook", "TestOrig");
        }

        // RouteFailLog.RflPlan, every failed plan: a no-path plan of a Sim inside a forced call we watch
        static void TofPlan(Sim sim, RoutePlanResult res)
        {
            if (sTofWatch.Count == 0) return;
            if (res.mType != RoutePlanResultType.FailedBecauseNoPathToDestination && res.mType != RoutePlanResultType.FailedBecauseDestinationPortalLocked) return;
            try
            {
                ulong id = sim.SimDescription.SimDescriptionId;
                if (sTofWatch.ContainsKey(id)) sTofSaw[id] = true;
            }
            catch (Exception) { }
        }

        // what EA's non-forced search of PutHeldTrashInCan would find first (none = EA destroys the bag)
        static bool TofReceptacle(Sim a, GameObject held)
        {
            if (GlobalFunctions.GetClosestObject<WasteChute>(a, false, true, null, null) != null) return true;
            if (Trashcan.FindBestTrashCan(a, new List<Trashcan>()) != null) return true;
            IThrowAwayable t = held as IThrowAwayable;
            return t != null && t.IsRecycleable && RecycleBin.FindBestRecycleBin(a, new List<RecycleBin>()) != null;
        }

        // after a call we made: the bag must be in a can, on the floor, or destroyed, never still in the Sim's hand
        static void TofEnsureDown(Sim a, GameObject held, string where)
        {
            try
            {
                if (held == null || held.HasBeenDestroyed) return;
                if (held.Parent != a && a.GetObjectInRightHand() != held) return;
                PatchCounters()[4614]++;
                TrashPile.PutTrashDown(a, held, false);
                TofLog(a.FullName + ": bag still held after " + where + ", put down");
            }
            catch (Exception ex) { PatchCounters()[4614]++; TofLog("error putting the bag down " + ex.GetType().Name); }
        }

        static string TofWhere(Sim a)
        {
            Lot l = a.LotCurrent;
            return " at lot 0x" + (l != null ? l.LotId.ToString("X") + " (" + l.Name + ")" : "?") + ", level " + a.Level;
        }

        public class TofPutHook
        {
            public static bool PutHook(InteractionInstance interaction, bool forceOutdoor, int numTrashUnits, SacsEventHandler nukeBagHandler)
            {
                if (!forceOutdoor || !(interaction is TrashPile.DisposeOutside) || !ShortcutOn("trashoutsidefallback"))
                    return PutOrig(interaction, forceOutdoor, numTrashUnits, nukeBagHandler);
                int[] c = PatchCounters();
                Sim a = null;
                GameObject held = null;
                int what = TrashOutsideFallbackCore.kEa;
                try
                {
                    a = interaction.InstanceActor;
                    held = a != null ? a.GetObjectInRightHand() : null;
                    bool exempt = a == null || TofExempt(a);
                    bool known = !exempt && held != null && TofKnown(a.LotCurrent);
                    what = TrashOutsideFallbackCore.Decide(true, true, exempt, known, known && TofReceptacle(a, held));
                }
                catch (Exception ex) { c[4614]++; TofLog("error at decide " + ex.GetType().Name); what = TrashOutsideFallbackCore.kExempt; }

                if (what == TrashOutsideFallbackCore.kExempt) { c[4613]++; return PutOrig(interaction, true, numTrashUnits, nukeBagHandler); }
                if (what == TrashOutsideFallbackCore.kFallback)
                {
                    c[4610]++;
                    TofLog(a.FullName + ": outdoor can unreachable, EA's non-forced disposal" + TofWhere(a));
                    bool r = PutOrig(interaction, false, numTrashUnits, nukeBagHandler);
                    TofEnsureDown(a, held, "the non-forced disposal");
                    return r;
                }
                if (what == TrashOutsideFallbackCore.kPutDown)
                {
                    c[4611]++;
                    TofLog(a.FullName + ": outdoor can unreachable, nothing else on the lot, bag put down" + TofWhere(a));
                    try { TrashPile.PutTrashDown(a, held, false); } catch (Exception ex) { c[4614]++; TofLog("error at put down " + ex.GetType().Name); }
                    TofEnsureDown(a, held, "the put down");
                    return false;
                }

                // EA's forced disposal, watched for a no-path plan of this Sim
                ulong id = 0;
                try { id = a.SimDescription.SimDescriptionId; sTofWatch[id] = true; sTofSaw.Remove(id); } catch (Exception) { }
                bool ok = false;
                try { ok = PutOrig(interaction, true, numTrashUnits, nukeBagHandler); }
                finally
                {
                    bool saw = sTofSaw.Remove(id);
                    sTofWatch.Remove(id);
                    try
                    {
                        if (TrashOutsideFallbackCore.Record(true, true, false, ok, saw))
                        {
                            Lot lot = a.LotCurrent;
                            bool fresh;
                            lock (sTof) fresh = lot != null && sTof.Note(lot.LotId, SimClock.CurrentTicks, TofSpan());
                            c[4611]++;
                            if (fresh) TofLog(a.FullName + ": no path to the outdoor can, bag put down by EA, lot remembered for "
                                + Instantiator.kTrashOutsideFallbackHours + " h" + TofWhere(a));
                        }
                    }
                    catch (Exception ex) { c[4614]++; TofLog("error at record " + ex.GetType().Name); }
                }
                return ok;
            }
            public static bool PutOrig(InteractionInstance interaction, bool forceOutdoor, int numTrashUnits, SacsEventHandler nukeBagHandler) { return false; } // TrashPile.PutHeldTrashInCan
        }

        public class TofTestHook
        {
            public bool TestHook(Sim a, IAcceptsGarbage target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                bool r = TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                if (!r || !isAutonomous || !(target is TrashcanIndoor) || !ShortcutOn("trashoutsidefallback")) return r;
                try
                {
                    if (sTof.Count == 0) return r;
                    bool exempt = TofExempt(a);
                    if (TrashOutsideFallbackCore.Refuse(true, true, true, exempt, true, !exempt && TofKnown(target.LotCurrent)))
                    {
                        PatchCounters()[4612]++;
                        return false;
                    }
                }
                catch (Exception) { PatchCounters()[4614]++; }
                return r;
            }
            public bool TestOrig(Sim a, IAcceptsGarbage target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // TrashcanIndoor.EmptyTrash.Definition.Test
        }
    }
}
