using System;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // metricskillmissing (docs/532): EA's MetricSkillX (a career level's "skill X" performance metric) takes its skill
    // from the career XML with SuperSetEnum.ParseEnumValue, which looks names up only, so a hex id or a skill that is not
    // registered in this game leaves SkillGuid at a value with no static skill. MetricTitle / MetricDescription then call
    // SkillManager.GetStaticSkill(SkillGuid).Name on null and throw NullReferenceException each time the career panel
    // fills, and FindMetricValue answers 0 (no such skill on the Sim), below Map0, so the job loses performance to a skill
    // nobody can learn [verified in the decompiled game code; seen live with SimsMx Career Metrics, issue #166 build 94].
    // Fix: with no static skill, the title and description name an empty skill, and the value is Map0, which
    // PerfMetric.CalcMetric scores as neutral (0). Counters [2059] titles / descriptions, [2060] values (registry
    // fixes-2059). Default off.
    public static partial class Fixes
    {
        public class MetricSkillMissingHook : MetricSkillX
        {
            public MetricSkillMissingHook(XmlDbRow row, int metricNumber) : base(row, metricNumber) { } // never called: the methods are swapped
            bool Missing() { return ShortcutOn("metricskillmissing") && SkillManager.GetStaticSkill(SkillGuid) == null; }

            public float ValueHook(Career career)
            {
                if (!Missing()) return ValueOrig(career);
                PatchCounters()[2060]++;
                return mMap0;
            }
            public float ValueOrig(Career career) { return 0f; } // receives MetricSkillX.FindMetricValue's original body

            public string TitleHook(Career career)
            {
                if (!Missing()) return TitleOrig(career);
                PatchCounters()[2059]++;
                return Localization.LocalizeString("Gameplay/Careers/Metrics:TitleSkillX", "");
            }
            public string TitleOrig(Career career) { return null; } // receives MetricSkillX.MetricTitle's original body

            public string DescHook(Career career)
            {
                if (!Missing()) return DescOrig(career);
                PatchCounters()[2059]++;
                return Localization.LocalizeString("Gameplay/Careers/Metrics:DescriptionSkillX", "");
            }
            public string DescOrig(Career career) { return null; } // receives MetricSkillX.MetricDescription's original body
        }

        // Harness check (repro metric-skill-missing-check): the first MetricSkillX of any career's levels gets, for one
        // synchronous call each, the skill id the player's career XML gave (0xC7DB6873, no static skill), restored in a
        // finally. "expect broken" (switch off) wants EA's NullReferenceException from MetricTitle and MetricDescription;
        // "expect fixed" (switch on) wants both to return text and GetCurrentCalcMetric (public, it calls the protected
        // FindMetricValue; reflection Invoke on the swapped body threw TargetException in the first run) to score 0 with the
        // value hook run once. The prototype career has no owner, so EA's FindMetricValue is not called in the off arm (it
        // would throw on the missing owner, not the bug).
        public static object MetricSkillMissingCheck(string what)
        {
            if (what != "expect broken" && what != "expect fixed") return "metricskillmissing <expect broken|expect fixed>";
            MetricSkillX m = null;
            Career career = null;
            foreach (Career c in CareerManager.CareerList)
            {
                if (c == null) continue;
                System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<int, CareerLevel>> levels = null;
                try { levels = c.CareerLevels; } catch (NullReferenceException) { } // a career with no shared data
                if (levels == null) continue;
                foreach (System.Collections.Generic.Dictionary<int, CareerLevel> branch in levels.Values)
                    foreach (CareerLevel l in branch.Values)
                        if (l != null && l.Metrics != null)
                            foreach (PerfMetric p in l.Metrics)
                                if (m == null && p is MetricSkillX) { m = (MetricSkillX)p; career = c; }
                if (m != null) break;
            }
            if (m == null) throw new InvalidOperationException("metricskillmissing check: no career level has a skill metric");
            const SkillNames kMissing = (SkillNames)0xC7DB6873;
            if (SkillManager.GetStaticSkill(kMissing) != null) throw new InvalidOperationException("metricskillmissing check: 0xC7DB6873 is a registered skill here");
            bool on = ShortcutOn("metricskillmissing");
            SkillNames saved = m.SkillGuid;
            string title = "threw nothing", desc = "threw nothing", value = "not called";
            bool titleOk, descOk, valueOk = true;
            m.SkillGuid = kMissing;
            try
            {
                try { string t = m.MetricTitle(career); titleOk = on && t != null; } catch (Exception e) { title = "threw " + e.GetType().Name; titleOk = !on && e is NullReferenceException; }
                try { string d = m.MetricDescription(career); descOk = on && d != null; } catch (Exception e) { desc = "threw " + e.GetType().Name; descOk = !on && e is NullReferenceException; }
                if (on)
                {
                    try { int before = PatchCounters()[2060]; int score = m.GetCurrentCalcMetric(career); bool ran = PatchCounters()[2060] == before + 1; value = "score " + score + ", value hook " + (ran ? "ran" : "not run"); valueOk = ran && score == 0; }
                    catch (Exception e) { value = "threw " + (e.InnerException ?? e).GetType().Name; valueOk = false; }
                }
            }
            finally { m.SkillGuid = saved; }
            string line = "title " + title + ", description " + desc + ", value " + value + " (" + career.Name + ", switch " + on + ")";
            bool ok = (what == "expect broken") == !on && titleOk && descOk && valueOk;
            if (!ok) throw new InvalidOperationException("metricskillmissing " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
