using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.ChildAndTeenUpdates;
using Sims3.Gameplay.Objects.Seating;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // joyridecostume (tuning kJoyrideCostume, off by default until its off/on run; docs/546 §4).
    //
    // Hitch [measured 2026-11-13, ts3agent aut_census longest call]: the first time autonomy tests "Go for Joyride" on a
    // sofa for a child, the test takes ~0.6 s (618 ms for a household child, phase-20261113-2212), once per child per
    // save. EA code [verified, decompiled]: GoForJoyride.Definition.Test and RouteAndGetToCostumeForJoyride.Definition.Test
    // call ImaginationHelper.GetSimOutfitForActor, which, when the child has no ChildImagination outfits yet, builds both
    // imagination costumes (two OutfitUtils.CreateOutfitForSim) right there, inside the autonomy test.
    //
    // Both tests use the costume only to ask "is the child wearing it now", which cannot be true while the child has no
    // ChildImagination outfit. So:
    //   GoForJoyride test, no costume yet: return false. Vanilla returns false on every path then (not a child, no seat,
    //     or build + "not wearing it"); it would differ only if the build failed (EP4 data missing) [inferred: not seen].
    //   RouteAndGetToCostumeForJoyride test: runs as is, but GetSimOutfitForActor returns null instead of building while
    //     the test runs and no costume exists; the "already wearing it" branch is then false, as it is in vanilla after a
    //     successful build. Exact.
    // The costume is still built where it is used: RouteAndGetToCostumeForJoyride.Run calls GetSimOutfitForActor before
    // the outfit spin (and GetInteractionForJoin is untouched). Same results for every Sim, household included.
    // Counters: [2304] Joyride tests answered without a build, [2305] costume-route tests run without a build,
    // [2306] costume builds outside the tests (the game's own, counted only), [2307] errors.
    public static partial class Fixes
    {
        static void ApplyJoyrideCostume()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type cb = typeof(GreyedOutTooltipCallback).MakeByRefType();
            Type[] testArgs = { typeof(Sim), typeof(MultiSeatObject), typeof(bool), cb };
            Type jd = typeof(GoForJoyride).GetNestedType("Definition", all), rd = typeof(RouteAndGetToCostumeForJoyride).GetNestedType("Definition", all);
            if (jd == null || rd == null) { AppliedLog().Add("joyridecostume: Definition types not found"); return; }
            Patch(typeof(ImaginationHelper), "GetSimOutfitForActor", new Type[] { typeof(SimDescription), typeof(ImaginationHelper.ImaginationUniformType) },
                typeof(JcHelperHook), "GetHook", "GetOrig");
            Patch(jd, "Test", testArgs, typeof(JcJoyrideHook), "TestHook", "TestOrig");
            Patch(rd, "Test", testArgs, typeof(JcRouteHook), "TestHook", "TestOrig");
        }

        static bool sJcNoBuild;  // sim thread only: set while RouteAndGetToCostumeForJoyride's Test runs

        static bool JcNoCostume(SimDescription sd)
        {
            return sd != null && sd.GetOutfitCount(OutfitCategories.ChildImagination) == 0;
        }

        public class JcHelperHook
        {
            public static SimOutfit GetHook(SimDescription simDescription, ImaginationHelper.ImaginationUniformType imaginationType)
            {
                try
                {
                    if (JcNoCostume(simDescription))
                    {
                        if (sJcNoBuild) { PatchCounters()[2305]++; return null; }
                        int n = ++PatchCounters()[2306];
                        if (n <= 12)  // which interaction the child was in when the game built the costume (fix_applied log)
                        {
                            Sim s = simDescription.CreatedSim;
                            string cur = s != null && s.CurrentInteraction != null ? s.CurrentInteraction.GetType().FullName : "none";
                            string q = s != null && s.InteractionQueue != null && s.InteractionQueue.TransitionInteraction != null ? s.InteractionQueue.TransitionInteraction.GetType().Name : "none";
                            AppliedLog().Add("joyridecostume build #" + n + ": " + (s != null && s.IsSelectable ? "household" : "townie") + " child, current " + cur + ", transition " + q + ", sJcNoBuild " + sJcNoBuild);
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2307]++; }
                return GetOrig(simDescription, imaginationType);
            }
            public static SimOutfit GetOrig(SimDescription simDescription, ImaginationHelper.ImaginationUniformType imaginationType) { return null; } // receives the original
        }

        public class JcJoyrideHook
        {
            public bool TestHook(Sim a, MultiSeatObject target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                try
                {
                    if (ShortcutOn("joyridecostume") && a != null && JcNoCostume(a.SimDescription)) { PatchCounters()[2304]++; return false; }
                }
                catch (Exception) { PatchCounters()[2307]++; }
                return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
            }
            public bool TestOrig(Sim a, MultiSeatObject target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // receives the original
        }

        public class JcRouteHook
        {
            public bool TestHook(Sim a, MultiSeatObject target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                if (!ShortcutOn("joyridecostume") || sJcNoBuild) return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                sJcNoBuild = true;
                try { return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback); }
                finally { sJcNoBuild = false; }
            }
            public bool TestOrig(Sim a, MultiSeatObject target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // receives the original
        }
    }
}
