using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;

namespace Ts3Agent
{
    // leak_census section "moods" (docs/191 moodlet hunt, 2026-11-11). Mood drift: MoodManager.mTargetMoodAxis (the mood
    // the Sim lerps to) is kept by AddEffect/RemoveEffect pairs; several buffs change a live instance's mEffectValue or
    // mAxisEffected without them (BuffFear.OnReAddition and others), so the target drifts off the sum of the live buffs,
    // and the difference stays after the buff ends. Counts, per Sim class, Sims whose target differs from the buff sum on
    // any axis, the total and largest difference, and live buffs whose value changed since they were added
    // (mEffectValue != mAdjustedEffectValue). Also Downtrodden / Torched / Lunacy holders (shared-field buffs). Reads only.
    // buff_drift {sims?: 3, adds?: 3}: STRESS repro on townies only (never household or selectable Sims): adds Fear `adds`
    // times, removes it, and reports each Sim's target drift before and after.
    public static partial class Handlers
    {
        static FieldInfo sMcTarget, sMcValues;

        static int[] McDrift(Sim s, out int changedBuffs)
        {
            changedBuffs = 0;
            if (sMcTarget == null) sMcTarget = typeof(MoodManager).GetField("mTargetMoodAxis", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            int[] target = sMcTarget.GetValue(s.MoodManager) as int[];
            if (target == null) return null;
            int[] sum = new int[target.Length];
            foreach (BuffInstance bi in s.BuffManager.List)
            {
                if (bi == null) continue;
                int ax = (int)bi.mAxisEffected;
                if (ax >= 0 && ax < sum.Length) sum[ax] += bi.mEffectValue;
                if (bi.mEffectValue != bi.mAdjustedEffectValue) changedBuffs++;
            }
            int[] d = new int[target.Length];
            for (int i = 0; i < d.Length; i++) d[i] = target[i] - sum[i];
            return d;
        }

        static Dictionary<string, object> LcMoods(Sim[] actors)
        {
            Dictionary<string, object> r = Util.Obj();
            Household active = Household.ActiveHousehold;
            Dictionary<string, int> drifted = new Dictionary<string, int>(), changed = new Dictionary<string, int>(), holders = new Dictionary<string, int>();
            int total = 0, max = 0, sims = 0; string maxWho = "";
            List<string> sample = new List<string>();
            foreach (Sim s in actors)
            {
                if (s == null || s.HasBeenDestroyed || s.MoodManager == null || s.BuffManager == null) continue;
                sims++;
                string cls = s.Household == active && active != null ? "household" : (s.IsSelectable ? "selectable" : "other");
                int ch;
                int[] d;
                try { d = McDrift(s, out ch); } catch (Exception) { LcBump(drifted, "read threw"); continue; }
                if (d == null) continue;
                if (ch > 0) LcBump(changed, cls, ch);
                int abs = 0;
                for (int i = 0; i < d.Length; i++) abs += Math.Abs(d[i]);
                if (abs > 0)
                {
                    LcBump(drifted, cls);
                    total += abs;
                    if (abs > max) { max = abs; maxWho = cls + " " + s.FullName; }
                    if (sample.Count < 8)
                    {
                        List<string> bl = new List<string>();
                        try
                        {
                            foreach (BuffInstance bi in s.BuffManager.List)
                                if (bi != null) bl.Add(bi.Guid + ":" + bi.mAxisEffected + ":" + bi.mEffectValue + (bi.mEffectValue != bi.mAdjustedEffectValue ? "(added " + bi.mAdjustedEffectValue + ")" : ""));
                        }
                        catch (Exception) { }
                        sample.Add(cls + " " + s.FullName + " [" + string.Join(",", Array.ConvertAll(d, x => x.ToString())) + "] buffs " + string.Join(" ", bl.ToArray()));
                    }
                }
                try
                {
                    foreach (BuffInstance bi in s.BuffManager.List)
                    {
                        if (bi == null || bi.mBuff == null) continue;
                        string n = bi.mBuff.GetType().Name;
                        if (n == "BuffDowntrodden" || n == "BuffTorched" || n == "BuffLunacy") LcBump(holders, n);
                    }
                }
                catch (Exception) { }
            }
            r["sims"] = sims;
            r["drifted_sims"] = drifted;
            r["drift_total"] = total;
            r["drift_max"] = max + " " + maxWho;
            r["drift_sample"] = sample;
            r["buffs_changed_since_add"] = changed;
            r["shared_field_buff_holders"] = holders;
            return r;
        }

        static void RegisterBuffDrift() { Dispatcher.Register("buff_drift", BuffDrift); Dispatcher.Register("buff_churn", BuffChurn); }

        // buff_churn {sims?: 10, rounds?: 20}: STRESS on townies only (never household or selectable Sims): each round, each
        // picked Sim gets a random moodlet from a list that covers listener, broadcaster, alarm and re-add buffs (docs/191
        // moodlet hunt), and loses another random one it has from the list. Reports adds, removes and errors.
        static readonly string[] sBcNames = { "Fear", "Fear", "Smelly", "GarlicBreath", "HeartBroken", "TooMuchSun", "SnuckIn", "HeatingUp",
            "Embarrassed", "Germy", "ImminentRomance", "ImminentNemesis", "InnerBeauty", "MissingAppointment", "SuccessfulPrank",
            "Hungry", "Tired", "Bored", "Uncomfortable", "Lonely", "Scared", "Singed", "Excited", "Inspired", "Fascinated", "Decorated" };

        static object BuffChurn(Args a)
        {
            RequireLive();
            int n = (int)a.Long("sims", 10), rounds = (int)a.Long("rounds", 20);
            Household active = Household.ActiveHousehold;
            List<Sim> sims = new List<Sim>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (sims.Count >= n) break;
                SimDescription d = s == null ? null : s.SimDescription;
                if (d == null || s.HasBeenDestroyed || s.IsSelectable || d.Household == active || !d.IsHuman || d.ChildOrBelow || s.BuffManager == null) continue;
                sims.Add(s);
            }
            List<BuffNames> names = new List<BuffNames>();
            foreach (string nm in sBcNames)
                try { names.Add((BuffNames)Enum.Parse(typeof(BuffNames), nm)); } catch (Exception) { }
            Random rnd = new Random();
            int adds = 0, refused = 0, removes = 0, errors = 0;
            for (int r = 0; r < rounds; r++)
                foreach (Sim s in sims)
                {
                    if (s.HasBeenDestroyed || s.BuffManager == null) continue;
                    try { if (s.BuffManager.AddElement(names[rnd.Next(names.Count)], Origin.None)) adds++; else refused++; } catch (Exception) { errors++; }
                    try
                    {
                        BuffNames rm = names[rnd.Next(names.Count)];
                        if (s.BuffManager.HasElement(rm)) { s.BuffManager.RemoveElement(rm); removes++; }
                    }
                    catch (Exception) { errors++; }
                }
            Dictionary<string, object> res = Util.Obj();
            res["sims"] = sims.Count; res["buff_types"] = names.Count; res["adds"] = adds; res["refused"] = refused; res["removes"] = removes; res["errors"] = errors;
            return res;
        }

        static object BuffDrift(Args a)
        {
            RequireLive();
            int n = (int)a.Long("sims", 3), adds = (int)a.Long("adds", 3);
            Household active = Household.ActiveHousehold;
            List<object> rows = new List<object>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (rows.Count >= n) break;
                SimDescription d = s == null ? null : s.SimDescription;
                if (d == null || s.HasBeenDestroyed || s.IsSelectable || d.Household == active || !d.IsHuman || d.ChildOrBelow
                    || s.BuffManager == null || s.MoodManager == null) continue;
                if (s.BuffManager.HasElement(BuffNames.Fear)) continue;
                int ch;
                int[] before = McDrift(s, out ch);
                int added = 0;
                for (int i = 0; i < adds; i++) if (s.BuffManager.AddElement(BuffNames.Fear, Origin.None)) added++;
                int[] during = McDrift(s, out ch);
                s.BuffManager.RemoveElement(BuffNames.Fear);
                int[] after = McDrift(s, out ch);
                Dictionary<string, object> row = Util.Obj();
                row["sim"] = s.FullName; row["added"] = added; row["has_fear"] = s.BuffManager.HasElement(BuffNames.Fear);
                row["before"] = before; row["during"] = during; row["after"] = after;
                rows.Add(row);
            }
            Dictionary<string, object> r = Util.Obj();
            r["rows"] = rows;
            return r;
        }
    }
}
