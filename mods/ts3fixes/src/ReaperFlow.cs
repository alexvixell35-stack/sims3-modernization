using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Services;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // reaperflow (tuning kReaperFlow, off by default; docs/563 §4). EA bug fixes in the Grim Reaper situation, exact for
    // every Sim.
    //
    // GrimReaperSituation [verified, decompiled]:
    // (a) UseVendingMachine.Init forces the reaper's soda interaction (deadly vending machine) with callbackOnFailure:
    //     null, the only state that has none. When the soda fails (machine unroutable, in use, the push refused), the
    //     situation stays in UseVendingMachine for good: the reaper stands on the lot and never reaps, socializes or leaves,
    //     and the machine keeps KilledASim. Fix: the same push with EA's own completion step (PostVendingMachine: clear the
    //     machines' KilledASim, then CheckIfEveryoneIsReaped, which picks Reaping / Socializing / Leave) as the failure
    //     callback too.
    // (b) ChessChallenge ("Challenge Death to Chess") Definition.Test checks only ChessOpponent == null and a free chess
    //     table, not LastGraveCreated, the grave the match is played for. After a match ClearChessData (and after a pet
    //     save ClearHarassData) sets it to null while the reaper may stay to socialize: the pie menu's
    //     GetInteractionName then dereferences the null grave (script error), and a challenge that runs ends in
    //     PostChessMatch, which spawns that grave's ghost (null). Fix: the challenge is not offered with no grave (or a
    //     grave without a dead Sim).
    // Counters: [2513] soda pushes given EA's completion step as failure path, [2514] challenges hidden with no grave, [2515] errors.
    // Check: ReaperFlowChecks.cs.
    public static partial class Fixes
    {
        static readonly MethodInfo sRfPostVending = typeof(GrimReaperSituation.UseVendingMachine).GetMethod("PostVendingMachine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sRfChessDef = RfChessDef();

        static Type RfChessDef()
        {
            Type c = typeof(GrimReaperSituation).GetNestedType("ChessChallenge", BindingFlags.Public | BindingFlags.NonPublic);
            return c == null ? null : c.GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic);
        }

        static void ApplyReaperFlow()
        {
            if (sRfPostVending != null) Patch(typeof(GrimReaperSituation.UseVendingMachine), "Init", typeof(RfVendingHook), "InitHook", "InitOrig");
            else AppliedLog().Add("reaperflow: UseVendingMachine.PostVendingMachine not found, vending part not patched");
            if (sRfChessDef != null) Patch(sRfChessDef, "Test", typeof(RfChessHook), "TestHook", "TestOrig");
            else AppliedLog().Add("reaperflow: ChessChallenge.Definition not found, chess part not patched");
        }

        // ---- decisions (also used by the check) ----
        // (a) push the soda with EA's completion step as the failure callback (a machine to use: EA's body throws without one)
        internal static bool RfVendingFailPath(bool on, bool hasMachine)
        {
            return on && hasMachine;
        }
        // (b) hide the challenge: EA's test passed but there is no grave (or no dead Sim) to play for
        internal static bool RfChessHide(bool on, bool eaOffers, bool graveWithSim)
        {
            return on && eaOffers && !graveWithSim;
        }

        public class RfVendingHook
        {
            public void InitHook(GrimReaperSituation parent)
            {
                bool done = false;
                if (ShortcutOn("reaperflow"))
                {
                    try
                    {
                        object st = this;
                        if (parent != null && parent.Worker != null && RfVendingFailPath(true, parent.VendingMachine != null))
                        {
                            Callback post = (Callback)Fixes.Bind(typeof(Callback), st, sRfPostVending);
                            // EA's own method as both callbacks (no closure: the callbacks are saved with the interaction)
                            ((Situation)st).ForceSituationSpecificInteraction(parent.VendingMachine, parent.Worker, parent.VendingMachine.GetReaperSodaDefinition(),
                                null, post, post, new InteractionPriority(InteractionPriorityLevel.MaxDeath, 1f));
                            PatchCounters()[2513]++;
                            done = true;
                        }
                    }
                    catch (Exception) { PatchCounters()[2515]++; }
                }
                if (!done) InitOrig(parent);
            }
            public void InitOrig(GrimReaperSituation parent) { } // receives GrimReaperSituation.UseVendingMachine.Init's original body
        }

        public class RfChessHook
        {
            public bool TestHook(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                bool ok = TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                if (!ok || !ShortcutOn("reaperflow")) return ok;  // fast path: EA says no / switch off
                try
                {
                    GrimReaperSituation sit = target == null ? null : ServiceSituation.FindServiceSituationInvolving(target) as GrimReaperSituation;
                    bool grave = sit != null && sit.LastGraveCreated != null && sit.LastGraveCreated.DeadSimsDescription != null;
                    if (RfChessHide(true, true, grave)) { PatchCounters()[2514]++; return false; }
                }
                catch (Exception) { PatchCounters()[2515]++; }
                return ok;
            }
            public bool TestOrig(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // receives ChessChallenge.Definition.Test's original body
        }
    }
}
