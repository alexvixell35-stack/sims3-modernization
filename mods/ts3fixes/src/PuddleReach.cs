using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // puddlereach (tuning kPuddleReach, off by default; docs/578 §3). EA bug fix: autonomy keeps picking "splash in a
    // rain puddle" for a Sim whose Run cannot reach any puddle.
    //
    // [verified, decompiled] Sim+AutonomousSplashInPuddle.Definition.Test passes for any autonomous Sim on a lot where
    // World.HasAnyValidRainPuddlesOnLot. Run plans to the 3 nearest outdoor-valid puddles and returns false when none
    // plans (or when splashroof drops every roof puddle for an NPC). The Sim is the target, so no exit reason and no
    // UpdateBlockTime: the next autonomy pass offers the same splash again while it rains. Measured: Matty (household)
    // aims at puddles 3 m up (roof), FailedBecauseNoDestinationSpecified, 3 plans per try, re-picked x9 in one run.
    //
    // Fix: when Run returns false with rain puddles still on the lot, the Sim gets a memo (lot, puddle count, sim-minute).
    // While the switch is on, Test answers false for that Sim on that lot as long as the lot's puddle count is unchanged
    // and less than kPrExpiryMin sim-minutes have passed. A new or dried puddle, another lot or the expiry drops the
    // memo, and Run decides as in vanilla. Every Sim, household included: only an option whose Run has just failed on the
    // same puddle set is withheld (vanilla intent: the interaction is offered when a puddle can be splashed in), so the
    // Sim's other choices and every outcome are vanilla's minus the failed tries.
    // Counters: [3979] tests answered false from the memo (on), [3980] the same case seen with the switch off, [3981]
    // errors. Run is shared with splashroof (SplashRoof.cs SplashRoofRunHook calls PrRunDone). Check: PuddleReachChecks.cs.
    public static partial class Fixes
    {
        const float kPrExpiryMin = 30f;
        const int kPrMax = 64;

        class PrMemo { internal ulong Lot; internal int Puddles; internal float At; internal bool Counted; }
        static readonly Dictionary<ulong, PrMemo> sPrMemo = new Dictionary<ulong, PrMemo>();  // sim thread only, by SimDescriptionId

        static Type sPrDef;

        static void ApplyPuddleReach()
        {
            Type ia = typeof(Sim).GetNestedType("AutonomousSplashInPuddle", BindingFlags.Public | BindingFlags.NonPublic);
            sPrDef = ia == null ? null : ia.GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic);
            if (sPrDef == null) { AppliedLog().Add("puddlereach: Sim+AutonomousSplashInPuddle+Definition not found"); return; }
            Patch(sPrDef, "Test", new Type[] { typeof(Sim), typeof(Sim), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() },
                typeof(PrTestHook), "TestHook", "TestOrig");
            // Run: SplashRoofRunHook (ApplySplashRoof) is the one hook on it and calls PrRunDone.
        }

        // Pure: withhold the splash for this Sim?
        internal static bool PrSkip(bool on, bool autonomous, bool hasMemo, bool sameLot, int puddlesNow, int puddlesAtMemo, float ageMin)
        {
            return on && autonomous && hasMemo && sameLot && puddlesNow > 0 && puddlesNow == puddlesAtMemo && ageMin >= 0f && ageMin < kPrExpiryMin;
        }

        static float PrNow() { return SimClock.ElapsedTime(TimeUnit.Minutes); }

        static int PrPuddles(ulong lot)
        {
            List<Vector3> p = World.GetRainPuddles(lot);
            return p == null ? 0 : p.Count;
        }

        // From SplashRoofRunHook after the original Run: remember a failed run while puddles remain, forget a good one.
        internal static void PrRunDone(Sim actor, bool ok)
        {
            try
            {
                if (actor == null || actor.SimDescription == null || actor.LotCurrent == null) return;
                ulong id = actor.SimDescription.SimDescriptionId;
                if (ok) { sPrMemo.Remove(id); return; }
                ulong lot = actor.LotCurrent.LotId;
                int n = PrPuddles(lot);
                if (n == 0) { sPrMemo.Remove(id); return; }
                if (!sPrMemo.ContainsKey(id) && sPrMemo.Count >= kPrMax) PrPrune();
                sPrMemo[id] = new PrMemo { Lot = lot, Puddles = n, At = PrNow() };
            }
            catch (Exception) { PatchCounters()[3981]++; }
        }

        static void PrPrune()
        {
            float now = PrNow();
            List<ulong> drop = new List<ulong>();
            foreach (KeyValuePair<ulong, PrMemo> kv in sPrMemo) if (now - kv.Value.At >= kPrExpiryMin) drop.Add(kv.Key);
            if (drop.Count == 0) sPrMemo.Clear();  // all fresh: start over rather than grow (a memo only saves tries)
            foreach (ulong k in drop) sPrMemo.Remove(k);
        }

        public class PrTestHook
        {
            public bool TestHook(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                try
                {
                    PrMemo m;
                    if (isAutonomous && sPrMemo.Count > 0 && a != null && a.SimDescription != null && a.LotCurrent != null
                        && sPrMemo.TryGetValue(a.SimDescription.SimDescriptionId, out m))
                    {
                        ulong lot = a.LotCurrent.LotId;
                        int n = m.Lot == lot ? PrPuddles(lot) : -1;
                        float age = PrNow() - m.At;
                        if (!PrSkip(true, true, true, m.Lot == lot, n, m.Puddles, age))
                            sPrMemo.Remove(a.SimDescription.SimDescriptionId);  // stale: Run decides again
                        else if (ShortcutOn("puddlereach")) { PatchCounters()[3979]++; return false; }
                        else if (!m.Counted) { m.Counted = true; PatchCounters()[3980]++; }
                    }
                }
                catch (Exception) { PatchCounters()[3981]++; }
                return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
            }
            public bool TestOrig(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // receives Sim+AutonomousSplashInPuddle+Definition.Test's original body
        }
    }
}
