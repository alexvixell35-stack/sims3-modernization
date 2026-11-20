using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Objects.Misc;
using Sims3.Gameplay.Skills;

namespace Ts3Fixes
{
    // Two EA NullReferenceExceptions a stress run logged with whole stacks (contributor 2026-11-17, both ON since import 2026-11-20).
    //
    // frontstageowner. [verified, decompiled] FrontStageKit.GetOwner() is `SimOwner.CreatedSim` with no null check, so a
    // stage kit with no owner throws. Its only caller, Sim.FrontStageKitInteractionProxy.Definition.AddInteractions, runs for
    // every performer (OccupationAsPerformanceCareer) while the game builds that Sim's interactions with another Sim (social
    // menu, autonomy's social pass), over every kit in the performer's room: one unowned kit ends the build for the pair.
    // With the switch an unowned kit answers null. The caller compares the answer with the actor (null never matches) and
    // then looks for the kit on the stage's performance slots, EA's own path for a show without an owned kit. An object
    // getter: no Sim changes, nothing for the household rule to exempt.
    //
    // careerskillnull. [verified, decompiled] Career.SetLevel calls GiveCareerLevelSkills for non-selectable Sims only. It
    // adds each MetricSkillX skill of the new level with SkillManager.AddElement and levels it up, but AddElement returns
    // null when the game has no clone of that skill (GetStaticSkillClone), and the level-up call then throws. SetLevel skips
    // the rest: the carpool, the exit from a work rabbit hole the Sim may no longer work in, and the kEventCareerLevelChanged /
    // kCareerDataChanged events (wishes, opportunities, the career panel). With the switch the level's skills are given in
    // EA's order, a skill the game cannot add is skipped and counted, and SetLevel goes on as EA's does. Townies only: a
    // household or unknown-scope owner (IsPlayerDesc) runs EA's body unchanged, as does a level or skill manager EA's body
    // would also fail on (null).
    //
    // Counters: [4052] frontstageowner: unowned kits answered null; careerskillnull: [4053] skills skipped, [4054] household /
    // unknown owners left to EA. Patched in EaFixes.cs.
    // Cleanup (functions read: FrontStageKit.GetOwner, the proxy's AddInteractions, Career.SetLevel / GiveCareerLevelSkills,
    // SkillManager.AddElement): AddInteractions also reads OccupationAsPerformanceCareer.StagePerformingOn without a null
    // check while IsPerformingAShow is true; not seen throwing, left as EA's.
    public static partial class Fixes
    {
        public class FrontStageOwnerHook
        {
            public Sim GetOwnerHook()
            {
                if (((FrontStageKit)(object)this).SimOwner == null && ShortcutOn("frontstageowner")) { PatchCounters()[4052]++; return null; }
                return GetOwnerOrig();
            }
            public Sim GetOwnerOrig() { return null; } // receives FrontStageKit.GetOwner's original body
        }

        public class CareerSkillNullHook
        {
            public void GiveHook()
            {
                if (!ShortcutOn("careerskillnull")) { GiveOrig(); return; }
                Career c = (Career)(object)this;
                SimDescription d = c.OwnerDescription;
                if (IsPlayerDesc(d)) { PatchCounters()[4054]++; GiveOrig(); return; }
                CareerLevel lvl = c.CurLevel;
                SkillManager sm = d.SkillManager;
                if (lvl == null || lvl.Metrics == null || sm == null) { GiveOrig(); return; }
                foreach (PerfMetric m in lvl.Metrics)
                {
                    MetricSkillX mx = m as MetricSkillX;
                    if (mx == null) continue;
                    int level = (int)mx.Map0;
                    if (level <= 0) continue;
                    Skill s = sm.AddElement(mx.SkillGuid);
                    if (s == null) { PatchCounters()[4053]++; continue; }
                    s.ForceSkillLevelUp(level);
                }
            }
            public void GiveOrig() { } // receives Career.GiveCareerLevelSkills's original body
        }
    }
}
