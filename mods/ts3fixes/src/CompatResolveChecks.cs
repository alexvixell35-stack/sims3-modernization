using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness repro for the Enhanced Hospitals birthroute precedence (docs/328 §7, repros eh-donor-birth-loop*). Production
    // code: CompatResolve.cs (CrBh / CrBc). Changes the world (one test pregnancy); the test save is never saved.
    // BirthModRepro
    //   start [household]  an instantiated adult / young-adult human woman without a pregnancy (NPC by default; "household":
    //                      a player-scope one) gets the mod's donor pregnancy (its 4-argument CreateDonorBaby constructor, the
    //                      donors two other instantiated adults; no hourly alarm) and one HaveBabyHospitalDonor at the closest
    //                      hospital, cancelled at once. From then on every birth interaction the mod queues for her is cancelled
    //                      in the same call (InteractionQueue.CancelInteraction on a queued entry removes it and runs its Cleanup
    //                      [verified, decompiled InteractionQueue.cs]: the mod's Cleanup sees no newborns and re-haves in 1 min).
    //   state              mom, household, switch_on, cancels (= birth attempts), sim_minutes since start, deferred (2469 delta),
    //                      verdict: FIXED (on, NPC: <= 8 attempts in >= 180 sim-min), VANILLA (one per minute: >= 30), else OPEN.
    //   stop               no more cancels (the pregnancy stays; the save is not saved).
    public static partial class Fixes
    {
        static Sim sBmrMom;
        static int sBmrCancels, sBmr2469;
        static long sBmrStart;

        public static object BirthModRepro(string what)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            if (what == "stop") { sBmrMom = null; r["stopped"] = true; return r; }
            if (what == "start" || what == "start household")
            {
                if (sBmrMom != null) { r["error"] = "a repro runs; stop first"; return r; }
                Type pt = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "PuddingFace_EnhancedHospital") pt = a.GetType("PuddingFace_EnhancedHospital.DemoClass+CreateDonorBaby", false);
                if (pt == null) { r["error"] = "Enhanced Hospitals not loaded"; return r; }
                bool hh = what == "start household";
                Sim mom = null;
                List<Sim> donors = new List<Sim>();
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s == null || s.HasBeenDestroyed || !s.InWorld || s.SimDescription == null || s.IsPet || !(s.SimDescription.YoungAdult || s.SimDescription.Adult)) continue;
                    if (mom == null && s.IsFemale && s.SimDescription.Pregnancy == null && IsPlayerSim(s) == hh && s.InteractionQueue != null) { mom = s; continue; }
                    if (donors.Count < 2) donors.Add(s);
                }
                if (mom == null || donors.Count < 2) { r["error"] = "no " + (hh ? "household" : "NPC") + " mom or donors (" + donors.Count + ")"; return r; }
                ConstructorInfo ctor = pt.GetConstructor(new Type[] { typeof(Sim), typeof(SimDescription), typeof(SimDescription), typeof(SimDescription) });
                Type it = pt.Assembly.GetType("PuddingFace_EnhancedHospital.DemoClass+HaveBabyHospitalDonor", false);
                FieldInfo single = it != null ? it.GetField("Singleton", BindingFlags.Static | BindingFlags.Public) : null;
                FieldInfo[] bf = it != null ? CrBirthFields(it) : null;
                RabbitHole hospital = RabbitHole.GetClosestRabbitHoleOfType(RabbitHoleType.Hospital, mom.Position);
                if (ctor == null || single == null || bf == null || hospital == null) { r["error"] = "CreateDonorBaby constructor / HaveBabyHospitalDonor / a hospital not found"; return r; }
                Pregnancy p = (Pregnancy)ctor.Invoke(new object[] { mom, donors[0].SimDescription, donors[0].SimDescription, donors[1].SimDescription });
                mom.SimDescription.Pregnancy = p;
                mom.InteractionQueue.CancelAllInteractions();
                sBmrMom = mom; sBmrCancels = 0; sBmr2469 = PatchCounters()[2469]; sBmrStart = SimClock.CurrentTicks;
                // the first birth as the mod queues it (its HaveTheBaby is protected), ended at once: the mod's own Cleanup arms
                // its 1-minute re-have, and every later HaveTheBaby call comes from the mod's alarms
                InteractionInstance ia = ((InteractionDefinition)single.GetValue(null)).CreateInstance(hospital, mom, new InteractionPriority(InteractionPriorityLevel.Pregnancy), false, false);
                bf[0].SetValue(ia, true);
                if (!mom.InteractionQueue.Add(ia)) { sBmrMom = null; r["error"] = "the queue refused the birth interaction"; return r; }
                mom.InteractionQueue.CancelInteraction(ia, false);
                sBmrCancels++;
            }
            else if (what != "state") { r["error"] = "birthmod <start [household]|state|stop>"; return r; }
            Sim m = sBmrMom;
            bool on = ShortcutOn("birthroute"), npc = m != null && IsNpcSim(m);
            double min = SimClock.ConvertFromTicks(SimClock.CurrentTicks - sBmrStart, TimeUnit.Minutes);
            r["mom"] = m != null ? m.FullName : null;
            r["household"] = m != null && !npc;
            r["switch_on"] = on;
            r["cancels"] = sBmrCancels;
            r["sim_minutes"] = (int)min;
            r["deferred"] = PatchCounters()[2469] - sBmr2469;
            r["looping"] = sBmrCancels >= 30;  // one attempt per sim-minute: the uncapped re-have
            r["verdict"] = sBmrCancels >= 30 ? "VANILLA" : on && npc && min >= 180 && sBmrCancels >= 3 && sBmrCancels <= 8 ? "FIXED" : "OPEN";
            return r;
        }

        // after the mod's HaveTheBaby body (CrBh hooks, only while a repro runs): the birth it queued for the repro mom ends at once
        static void BmrFailQueued(Pregnancy p, Sim mMom)
        {
            try
            {
                Sim mom = CrBirthMom(p, mMom);
                if (mom == null || mom != sBmrMom) return;
                InteractionInstance ia = CrBirthQueued(mom, p.GetType().Assembly);
                if (ia == null) return;
                mom.InteractionQueue.CancelInteraction(ia, false);
                sBmrCancels++;
            }
            catch (Exception) { PatchCounters()[2442]++; }
        }
    }
}
