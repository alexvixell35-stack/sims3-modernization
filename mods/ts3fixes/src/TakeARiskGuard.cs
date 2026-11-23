using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // takeariskguard (docs/529 §4, EA bug, default off): Political.PoliticalTakeARisk, the political career's Take a Risk tone
    // result, updates the boss's and co-workers' liking through OwnerDescription.GetRelationship(x, true).LTR with no check
    // [verified, the same in simler90's core]. GetRelationship returns null for a null boss or a description that is no
    // longer valid, so .LTR throws after the outcome's notification, and a gone co-worker stops the co-worker loop with the
    // rest unchanged. With the switch on the hook runs the same outcome (one RandomUtil.GetFloat(1, 100) draw against EA's
    // tunables, the same notification, ShouldPromote, liking deltas) and skips only an update whose relationship is null.
    // Results equal vanilla wherever vanilla does not throw; the career owner may be a household Sim (EA bug fix, crash guard
    // only, fix-ea-bugs-too). Fields missing (another game version): EA's own method runs. [997] updates skipped.
    public static partial class Fixes
    {
        static float RiskTunable(string name)
        {
            FieldInfo f = typeof(Political).GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException("Political", name);
            return (float)f.GetValue(null);
        }

        static void RiskLiking(Career c, SimDescription d, float delta)
        {
            Relationship r = d != null ? c.OwnerDescription.GetRelationship(d, true) : null;
            if (r == null || r.LTR == null) { PatchCounters()[997]++; return; }
            r.LTR.UpdateLiking(delta);
        }

        static void RiskTns(Career c, string what)
        {
            c.ShowOccupationTNS(Localization.LocalizeString(c.OwnerDescription.IsFemale, "Gameplay/Careers/Political:" + what, c.OwnerDescription));
        }

        public static class TakeARiskHook
        {
            public static void Hook(Career c)
            {
                if (c == null || c.OwnerDescription == null || !ShortcutOn("takeariskguard")) { Orig(c); return; }
                float o1, o2, o3, improve, brownBoss, brownCow, hurtBoss, hurtCow;
                try
                {
                    o1 = RiskTunable("kChanceRiskOutcome1"); o2 = RiskTunable("kChanceRiskOutcome2"); o3 = RiskTunable("kChanceRiskOutcome3");
                    improve = RiskTunable("kRiskImproveBoss"); brownBoss = RiskTunable("kRiskImproveBossByBrownnose");
                    brownCow = RiskTunable("kRiskHurtCowrkersByBrownnose"); hurtBoss = RiskTunable("kRiskHurtBoss"); hurtCow = RiskTunable("kRiskHurtCoworkers");
                }
                catch (Exception) { Orig(c); return; }
                float n = RandomUtil.GetFloat(1f, 100f);
                if (n < o1) { RiskTns(c, "Research"); c.ShouldPromote = true; return; }
                if (n < o1 + o2) { RiskTns(c, "Proposal"); RiskLiking(c, c.Boss, improve); return; }
                bool brown = n < o1 + o2 + o3;
                RiskTns(c, brown ? "BrownNose" : "ProposalFail");
                RiskLiking(c, c.Boss, brown ? brownBoss : hurtBoss);
                if (c.Coworkers == null) return; // EA's foreach would throw here; nothing left to do
                foreach (SimDescription w in new List<SimDescription>(c.Coworkers)) RiskLiking(c, w, brown ? brownCow : hurtCow);
            }
            public static void Orig(Career c) { } // receives Political.PoliticalTakeARisk's original header
        }

        // Agent check (repro takeariskguard-check): a townie's career (not of the active household, not selectable) gets no boss
        // and one never-valid co-worker for five Take a Risk results in a row, then its boss, co-workers and ShouldPromote are
        // put back. "expect throws" (switch off): at least one result threw (EA's bug). "expect guarded" (switch on): none
        // threw and [997] went up. Five draws: only outcome 1 (Research, no liking change) cannot show either side.
        public static string TakeARiskCheck(string what)
        {
            if (what != "expect throws" && what != "expect guarded") return "takearisk <expect throws|expect guarded>";
            Career c = null;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (IsPlayerSim(s) || s.SimDescription.CareerManager == null) continue;
                c = s.SimDescription.CareerManager.OccupationAsCareer;
                if (c != null) break;
            }
            if (c == null) throw new InvalidOperationException("takearisk check: no townie with a career");
            SimDescription boss = c.Boss; List<SimDescription> coworkers = c.Coworkers; bool promote = c.ShouldPromote;
            int[] n = PatchCounters(); int k0 = n[997], threw = 0;
            c.Boss = null; c.Coworkers = new List<SimDescription>(); c.Coworkers.Add(new SimDescription()); // never Fixup'd: invalid
            try { for (int i = 0; i < 5; i++) { try { Political.PoliticalTakeARisk(c); } catch (Exception) { threw++; } } }
            finally { c.Boss = boss; c.Coworkers = coworkers; c.ShouldPromote = promote; }
            bool on = ShortcutOn("takeariskguard");
            int skips = n[997] - k0;
            string line = "threw " + threw + "/5, skips " + skips + " (switch " + on + ", " + c.OwnerDescription.FullName + ")";
            bool ok = what == "expect throws" ? !on && threw > 0 : on && threw == 0 && skips > 0;
            if (!ok) throw new InvalidOperationException("takearisk " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
