using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // birthroute harness check (docs/558 §1). Production code: BirthRoute.cs.
    public static partial class Fixes
    {
        // ---------- harness check (pure decisions + a read-only list; nothing in the world changes) ----------
        // BirthRouteCheck
        //   run     F6 the cut decision on a throwaway HaveBabyHospital at Pregnancy priority (autonomous: never kept); F1 the
        //           retry wait of an NPC's 3rd attempt (EA 1 min) and the kind after 3 failed hospital trips (EA: the same
        //           hospital again); F2/F3 a stale call and a duplicate call. off: all EA = BROKEN; on: FIXED. A household mom
        //           keeps EA's wait and kind (UNEXPECTED otherwise).
        //   who     pregnant Sims: hour, household/NPC, home lot, queued birth interaction, attempts, failures counted
        //   result  counters 2438-2442, 2469-2476
        public static object BirthRouteCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "birthroute: kept 2438=" + c[2438] + ", failed hospital births 2439=" + c[2439] + ", other kind 2440=" + c[2440]
                + ", homeless to hospital 2441=" + c[2441] + ", errors/guards 2442=" + c[2442] + ", deferred 2469=" + c[2469] + ", lost re-armed 2470=" + c[2470]
                + ", stale/duplicate 2471=" + c[2471] + ", buff restored 2472=" + c[2472] + ", placed 2473=" + c[2473] + ", watchdogs 2474=" + c[2474]
                + ", homeless after-birth 2475=" + c[2475] + ", born after a failure 2476=" + c[2476];
            if (what == "result") return counters;
            if (what == "who") return BrWho() + counters;
            if (what != "run") return "birthroute <run|who|result>";
            bool on = ShortcutOn("birthroute");
            InteractionInstance birth = BrThrowaway(InteractionPriorityLevel.Pregnancy), autonomous = BrThrowaway(InteractionPriorityLevel.Autonomous);
            bool kept = on && BirthIa(birth), autoKept = on && BirthIa(autonomous);
            float wait3 = BirthRetryWaitMin(on, true, 2), waitHh = BirthRetryWaitMin(on, false, 2);
            int kind = BirthKind(on, true, 3, 1, true, true), kindHh = BirthKind(on, false, 3, 1, true, true), kind2 = BirthKind(on, true, 2, 1, true, true);
            int stale = BirthCallAction(on, false, false), dup = BirthCallAction(on, true, true), normal = BirthCallAction(on, true, false);
            bool fixedAll = kept && wait3 > 1f && kind == 2 && stale == 1 && dup == 1 && normal == 2;
            bool brokenAll = !kept && wait3 == 1f && kind == 0 && stale == 0 && dup == 0 && normal == 0;
            bool bad = autoKept || waitHh != 1f || kindHh != 0 || kind2 != 0;
            string verdict = bad ? "UNEXPECTED" : fixedAll ? "FIXED" : brokenAll ? "BROKEN" : "UNEXPECTED";
            return "birthroute check (switch " + (on ? "on" : "off") + "): birth route kept " + kept + " (autonomous " + autoKept + "); NPC 3rd re-have waits "
                + wait3 + " min (household " + waitHh + "); after 3 failed hospital trips kind " + kind + " (0 EA, 2 home; household " + kindHh + ", 2 fails " + kind2
                + "); stale call " + stale + ", duplicate " + dup + ", normal " + normal + " (0 EA, 1 dropped, 2 EA + re-arm); verdict: " + verdict + "; " + counters;
        }

        // an uninitialized HaveBabyHospital (no actor, never queued) carrying only a priority; SetPriority would call
        // OnPriorityChanged on the missing actor, so the protected field is written directly
        static InteractionInstance BrThrowaway(InteractionPriorityLevel level)
        {
            InteractionInstance ia = (InteractionInstance)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Pregnancy.HaveBabyHospital));
            typeof(InteractionInstance).GetField("mPriority", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ia, new InteractionPriority(level));
            return ia;
        }

        static string BrWho()
        {
            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null || s.SimDescription.Pregnancy == null) continue;
                Pregnancy p = s.SimDescription.Pregnancy;
                string q = "none";
                foreach (InteractionInstance ia in s.InteractionQueue.InteractionList)
                {
                    if (!BirthIa(ia)) continue;
                    Pregnancy.HaveBabyHospital h = ia as Pregnancy.HaveBabyHospital;
                    q = ia.GetType().Name + (h != null ? (h.BabyShouldBeBorn ? " (birth due)" : " (waiting)") : "");
                    break;
                }
                int fails;
                BrState st;
                sBirthFails.TryGetValue(s.SimDescription.SimDescriptionId, out fails);
                sBr.TryGetValue(s.SimDescription.SimDescriptionId, out st);
                sb.Append(s.FullName).Append(IsPlayerSim(s) ? " household" : " npc").Append(p is PetPregnancy ? " pet" : "")
                  .Append(", hour ").Append(Pregnancy.kHoursOfPregnancy - p.HoursUntilBirth).Append("/").Append(Pregnancy.kHoursOfPregnancy)
                  .Append(", labour buff ").Append(s.BuffManager.HasElement(BuffNames.BabyIsComing))
                  .Append(", home ").Append(s.LotHome != null ? s.LotHome.Name : "none")
                  .Append(", birth interaction ").Append(q).Append(", attempts ").Append(st != null ? st.Attempts : 0)
                  .Append(", failed hospital trips ").Append(fails).Append("\n");
                n++;
            }
            return "pregnant Sims: " + n + "\n" + sb.ToString();
        }
    }
}
