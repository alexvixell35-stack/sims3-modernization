using System;
using System.Collections.Generic;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace.CAS;

namespace Ts3Agent
{
    // Every townie up its career ladder, one by one, for the leak stress (issue #40; user 11-09: "make all sim go up in
    // their careers one by one like with story progression and see what leaks"). EA's story progression promotes with
    // StoryProgression.PromoteDemote: any townie SimDescription, in the world or not, whose Occupation is below its
    // HighestLevel, then Occupation.PromoteSim() [verified: decompiled 1.67]. The career command does the same call, but
    // the stress picks its Sims with pick_sims: only the first 40 instantiated townies in world order. career_ladder walks
    // ALL townies of the world in a fixed rotation instead. TOWNIES ONLY: never the active household or a special household.
    public static partial class Handlers
    {
        static void RegisterCareerLadder()
        {
            Dispatcher.Register("career_ladder", CareerLadder);
        }

        static ulong sCareerCursor;  // the last promoted SimDescriptionId: the next call goes on after it

        // career_ladder {count?: 5 (1..50)}: the next `count` townies (in SimDescriptionId order after the last call's,
        // wrapping) whose job is below its top level get one promotion each (Occupation.PromoteSim, as PromoteDemote.Execute).
        // Returns promoted [{sim, career, from, to, top}], eligible (below top), at_top, jobless (YA/adult/elder without a job;
        // joining is the career command's join, it needs a Sim in the world), and wrapped (the rotation started over).
        static object CareerLadder(Args a)
        {
            RequireLive();
            int n = (int)a.Long("count", 5);
            if (n < 1 || n > 50) throw new McpException("count must be 1..50");
            Household active = Household.ActiveHousehold;
            List<SimDescription> below = new List<SimDescription>();
            int top = 0, jobless = 0;
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null || h == active || h.IsActive || h.IsSpecialHousehold) continue;
                foreach (SimDescription sd in h.AllSimDescriptions)
                {
                    if (sd == null || sd.IsPet || sd.IsDead || sd.IsGhost || sd.CareerManager == null) continue;
                    Occupation job = sd.CareerManager.Occupation;
                    if (job == null)
                    {
                        if ((sd.Age & (CASAgeGenderFlags.YoungAdult | CASAgeGenderFlags.Adult | CASAgeGenderFlags.Elder)) != 0) jobless++;
                        continue;
                    }
                    if (job.Level >= job.HighestLevel) { top++; continue; }
                    below.Add(sd);
                }
            }
            below.Sort(delegate(SimDescription x, SimDescription y) { return x.SimDescriptionId.CompareTo(y.SimDescriptionId); });
            int start = below.FindIndex(delegate(SimDescription sd) { return sd.SimDescriptionId > sCareerCursor; });
            bool wrapped = start < 0;
            if (wrapped) start = 0;
            List<object> promoted = new List<object>();
            for (int i = 0; i < n && i < below.Count; i++)
            {
                SimDescription sd = below[(start + i) % below.Count];
                Occupation job = sd.CareerManager.Occupation;
                Dictionary<string, object> d = Util.Obj();
                d["sim"] = sd.FullName;
                d["career"] = job.CareerName;
                d["from"] = job.Level;
                try { job.PromoteSim(); }
                catch (Exception e) { d["error"] = e.GetType().Name + ": " + e.Message; }
                Occupation now = sd.CareerManager != null ? sd.CareerManager.Occupation : null;  // a promotion can change the job object
                d["to"] = now != null ? now.Level : -1;
                d["top"] = now != null ? now.HighestLevel : -1;
                promoted.Add(d);
                sCareerCursor = sd.SimDescriptionId;
                if ((start + i + 1) % below.Count == 0 && i + 1 < n) wrapped = true;
            }
            Dictionary<string, object> r = Util.Obj();
            r["promoted"] = promoted;
            r["eligible"] = below.Count;
            r["at_top"] = top;
            r["jobless"] = jobless;
            r["wrapped"] = wrapped;
            return r;
        }
    }
}
