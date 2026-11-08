using System;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using AutonomyT = Sims3.Gameplay.Autonomy.Autonomy;

namespace Ts3Fixes
{
    // autt2l0 (tier-2 L0, docs/data/tier2-local-phase-handoff.md §0.2 and §7, measurement only, default off): how much time the
    // local phase (B9 ScoreInteractionsForLocalAutonomy) spends per actor class, so L2-L7 get a go/no-go.
    // Hook on B7 (ScoreInteractionsOnObjects, one frame per search; B9 itself is not hooked: ilpatch b15 owns its IL). The clock
    // runs from B7's entry to B13's entry (AutMetaHook, first line) or B7's return when the search has no meta phase: C0 + B8
    // + B9, none of which yields (docs/59 §1.3), so it is sim-thread time. Searches where B8 said no (low LOD) are timed the
    // same way and give the C0 + B8 floor to subtract. Pairs = C2 calls of that autonomy inside the window (AutScoreHook),
    // i.e. unique pairs B9 scored. Class after the clock stops: SafeUfWhy (household, seen, unseen non-household).
    // Side entries (mForceRunLocalAutonomy or an overridden lot) are counted apart, all classes together.
    // Changes no result for anyone: the game's B7 runs unchanged; the probe only reads (household Sims are counted, never
    // changed). A nested B7 (a side entry from inside another search's B9) is not timed on its own; the outer one includes it.
    // Counters (AutLocalProbeCore): [903-905] local searches by class (household, seen, unseen), [906-908] their us,
    // [909-911] searches with pairs counted, [912-914] pairs; [915] side-entry local searches, [916] us; [917] manager
    // searches without the local phase, [918] us; [919-924] unseen non-household B8 reason (active lot, same lot as the
    // active Sim, daycare, resort, household, other = on screen / hosted guest / cached flag); [925] ms between the first
    // and the latest probed search; [926] nested B7 not timed; [927] exceptions.
    public static partial class Fixes
    {
        static AutonomyT sL0Aut;   // the search inside its window (B7 entry .. B13 entry), else null
        static uint sL0T0;
        static int sL0Pairs, sL0First;
        static bool sL0Side;
        static StopWatch sL0Sw;
        static FieldInfo fL0Force, fL0OverLot;

        public class AutLocalHook
        {
            public int B7Hook(CommodityKind c, bool metaAutonomy)
            {
                if (sL0Aut != null) { if (ShortcutOn("autt2l0")) PatchCounters()[AutLocalProbeCore.kNested]++; return B7Orig(c, metaAutonomy); }
                if (!ShortcutOn("autt2l0") || OnWorker() || !L0Open((AutonomyT)(object)this)) return B7Orig(c, metaAutonomy);
                try { return B7Orig(c, metaAutonomy); }
                finally { L0Close((AutonomyT)(object)this); }  // no-op when B13's entry closed it
            }
            public int B7Orig(CommodityKind c, bool metaAutonomy) { return 0; } // receives Autonomy.ScoreInteractionsOnObjects' original header
        }

        static bool L0Open(AutonomyT a)
        {
            try
            {
                if (sL0Sw == null)
                {
                    const BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    fL0Force = typeof(AutonomyT).GetField("mForceRunLocalAutonomy", all);
                    fL0OverLot = typeof(AutonomyT).GetField("mOverriddenLocalAutonomyLot", all);
                    sL0Sw = StopWatch.Create(StopWatch.TickStyles.Microseconds);
                    sL0Sw.Restart();
                    sL0First = System.Environment.TickCount;
                }
                sL0Side = (fL0Force != null && (bool)fL0Force.GetValue(a)) || (fL0OverLot != null && fL0OverLot.GetValue(a) != null);
                sL0Pairs = 0;
                sL0Aut = a;
                sL0T0 = sL0Sw.GetElapsedTime();
                return true;
            }
            catch (Exception) { PatchCounters()[AutLocalProbeCore.kExc]++; sL0Aut = null; return false; }
        }

        // B13 entry (AutMetaHook) or B7 return: stop the clock, then classify (outside the timed part)
        static void L0Close(AutonomyT a)
        {
            if (sL0Aut == null || (object)sL0Aut != (object)a) return;
            int us = (int)unchecked(sL0Sw.GetElapsedTime() - sL0T0);
            int pairs = sL0Pairs;
            sL0Aut = null;
            int[] c = PatchCounters();
            try
            {
                c[AutLocalProbeCore.kSpanMs] = System.Environment.TickCount - sL0First;
                if (!a.IsRunningLocalAutonomy) { c[AutLocalProbeCore.kNoLocalN]++; c[AutLocalProbeCore.kNoLocalUs] += us; return; }
                if (sL0Side) { c[AutLocalProbeCore.kSideN]++; c[AutLocalProbeCore.kSideUs] += us; return; }
                Sim s = a.Actor as Sim;
                int k = AutLocalProbeCore.Class(SafeUfWhy(s));
                c[AutLocalProbeCore.kSearches + k]++;
                c[AutLocalProbeCore.kUs + k] += us;
                c[AutLocalProbeCore.kPairSearches + k]++;
                c[AutLocalProbeCore.kPairs + k] += pairs;
                if (k == AutLocalProbeCore.kUnseen && s != null)
                {
                    Sim act = Sim.ActiveActor;
                    Lot lot = s.LotCurrent;
                    c[AutLocalProbeCore.kReason + AutLocalProbeCore.Reason(act != null && s.Household == act.Household,
                        act != null && lot != null && lot.IsActive, act != null && lot != null && !lot.IsWorldLot && lot == act.LotCurrent,
                        act != null && lot != null && DaycareSituation.GetDaycareSituationForChild(s) != null,
                        act != null && lot != null && ResortManager.IsResortWorker(s))]++;
                }
            }
            catch (Exception) { c[AutLocalProbeCore.kExc]++; }
        }
    }
}
