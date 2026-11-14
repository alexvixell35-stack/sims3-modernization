using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness checks for the event fixes (test only; called through the agent's reflect_call, never by the game):
    // AwardKeepCheck (awardkeep), EventCeremonyCheck (eventcrowdring / eventrouteretry / awardkeep), PetFireCheck
    // (petfireplan). Each acts on the active Sim's world and cleans up what it can after itself.
    public static partial class Fixes
    {
        // ---- shared ----

        static CityHall FirstCityHall()
        {
            foreach (CityHall h in Sims3.Gameplay.Queries.GetObjects<CityHall>()) return h;
            return null;
        }

        // Books a ceremony for sim at hall and opens it at once (EA's GreatSuccessCelebrationStart, what the pre-ceremony
        // alarm runs). Null when it worked, else why not.
        static string CheckOpenCeremony(CityHall hall, Sim sim)
        {
            if (hall.IsGatheringActive) return "a gathering is already active at City Hall";
            if (!hall.CreateCelebration(sim, 0x7E57A11Du)) return "CreateCelebration refused";
            MethodInfo start = typeof(CityHall).GetMethod("GreatSuccessCelebrationStart", kInstAll);
            if (start == null) return "GreatSuccessCelebrationStart not found";
            start.Invoke(hall, null);
            return null;
        }

        static InteractionDefinition CityHallDefinition(string nested)
        {
            Type t = typeof(CityHall).GetNestedType(nested, BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo f = t == null ? null : t.GetField("Singleton", BindingFlags.Public | BindingFlags.Static);
            return f == null ? null : (InteractionDefinition)f.GetValue(null);
        }

        // ---- awardkeep ----

        // Two calls a few seconds apart (the mayor is made by a one-shot task): "setup" opens a ceremony for the active
        // Sim; "expect lost" / "expect kept" then cleans up a master GiveAward that never gave the award, which is what
        // every failure path ends in, and reads the result: lost = no pickup (EA, switch off), kept = one pickup at City
        // Hall (switch on); both want the ceremony gone. The pickup the check caused is removed again.
        public static object AwardKeepCheck(string what)
        {
            Sim sim = Sim.ActiveActor;
            CityHall hall = FirstCityHall();
            if (sim == null || hall == null) return "awardkeep check: no active Sim or no City Hall";
            if (what == "setup")
            {
                string why = CheckOpenCeremony(hall, sim);
                return why != null ? "awardkeep check: " + why : "setup ok: ceremony open " + hall.CanAttendAwardCeremony + ", mayor made later by a one-shot task";
            }
            if (what != "expect lost" && what != "expect kept") return "awardkeep <setup|expect lost|expect kept>";
            if (hall.CeremonyAwardInfo == null || !hall.CanAttendAwardCeremony) return "FAIL: no open ceremony (run setup first)";
            Sim mayor = hall.NPCMayor;
            if (mayor == null) return "FAIL: the mayor is not made yet, retry in a few seconds";
            CityHall.GiveAward ga = CityHall.GiveAward.Singleton.CreateInstance(sim, mayor, new InteractionPriority(InteractionPriorityLevel.CriticalNPCBehavior), false, false) as CityHall.GiveAward;
            ga.IsMaster = true;
            ga.RabbitHoleToRouteTo = hall;
            int before = PatchCounters()[2322];
            ga.Cleanup();
            ulong id = sim.SimDescription.SimDescriptionId;
            int pickups = hall.InRabbitHoleAwardInfo.RemoveAll(i => i.SimDescID == id);
            if (pickups > 0 && sim.MapTagManager != null) sim.MapTagManager.RemoveOccupationRewardMaptag();
            bool gone = hall.CeremonyAwardInfo == null && hall.NPCMayor == null && !hall.CanAttendAwardCeremony;
            bool ok = gone && (what == "expect kept" ? pickups == 1 : pickups == 0);
            return (ok ? "PASS " : "FAIL ") + what + ": pickups " + pickups + ", ceremony gone " + gone + ", counter 2322 +" + (PatchCounters()[2322] - before);
        }

        // ---- eventcrowdring / eventrouteretry ----

        // A real ceremony with a real crowd (phasesoak4 phase "ceremony"):
        //   "start <n> [block]"  opens a ceremony for the active Sim (no award buff or pickup left from an earlier run),
        //                        pushes EA's CityHall.Celebrate on the n NPC teens or older nearest to City Hall, and
        //                        pushes AttendAwardCeremony on the active Sim. "block" first places those NPCs on the
        //                        jig's slot B, the honoree's goal (the worst case).
        //   "result"             GIVEN (HeroOfTheCity on the Sim), PICKUP, OPEN or LOST, plus the fixes' counters.
        public static object EventCeremonyCheck(string what)
        {
            Sim sim = Sim.ActiveActor;
            CityHall hall = FirstCityHall();
            if (sim == null || hall == null) return "ceremony check: no active Sim or no City Hall";
            int[] c = PatchCounters();
            ulong me = sim.SimDescription.SimDescriptionId;
            if (what == "result")
            {
                string state = sim.BuffManager != null && sim.BuffManager.HasElement(BuffNames.HeroOfTheCity) ? "GIVEN"
                    : hall.InRabbitHoleAwardInfo.Exists(i => i.SimDescID == me) ? "PICKUP"
                    : hall.CeremonyAwardInfo != null ? "OPEN" : "LOST";
                return "ceremony " + state + ", awardkeep 2322=" + c[2322] + ", ladders 2326=" + c[2326] + " ring 2327=" + c[2327]
                    + " pushed 2332=" + c[2332] + " jig 2328=" + c[2328] + " moved 2329=" + c[2329] + " retry-ok 2330=" + c[2330]
                    + " errors 2331=" + c[2331] + " spot 2333=" + c[2333] + " anchored 2334=" + c[2334] + " 2323=" + c[2323];
            }
            string[] w = what.Split(' ');
            int n;
            if (w.Length < 2 || w[0] != "start" || !int.TryParse(w[1], out n)) return "ceremony <start n [block]|result>";
            bool block = w.Length > 2 && w[2] == "block";
            if (sim.BuffManager != null) sim.BuffManager.RemoveElement(BuffNames.HeroOfTheCity);
            hall.InRabbitHoleAwardInfo.RemoveAll(i => i.SimDescID == me);
            InteractionDefinition celebrate = CityHallDefinition("Celebrate"), attend = CityHallDefinition("AttendAwardCeremony");
            if (celebrate == null || attend == null) return "ceremony check: EA interactions not found";
            string why = CheckOpenCeremony(hall, sim);
            if (why != null) return "ceremony check: " + why;

            Vector3 at = hall.RabbitHoleProxy.Position;
            List<Sim> crowd = new List<Sim>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                if (IsNpcSim(s) && s.InWorld && s.LotCurrent != null && s.SimDescription.IsHuman && s.SimDescription.TeenOrAbove
                    && s.Service == null && !(s.Posture is InRabbitHolePosture))
                    crowd.Add(s);
            crowd.Sort((a, b) => (a.Position - at).LengthSqr().CompareTo((b.Position - at).LengthSqr()));
            if (crowd.Count > n) crowd.RemoveRange(n, crowd.Count - n);

            SocialJigTwoPerson jig = hall.AwardJig;
            for (int k = 0; k < crowd.Count; k++)
            {
                Sim s = crowd[k];
                if (block && jig != null)
                {
                    // the first on slot B itself, the rest in a 0.4 m ring around it
                    float a = k * 2.4f, r = k > 0 ? 0.4f : 0f;
                    Vector3 goal = jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimB);
                    PlaceSim(s, goal + new Vector3((float)Math.Cos(a) * r, 0f, (float)Math.Sin(a) * r), jig.ForwardVector, "ceremony check: crowd on slot B");
                }
                s.InteractionQueue.CancelAllInteractions();
                s.InteractionQueue.Add(celebrate.CreateInstance(hall, s, new InteractionPriority(InteractionPriorityLevel.CriticalNPCBehavior), true, true));
            }
            sim.InteractionQueue.Add(attend.CreateInstance(hall, sim, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true));
            return "ceremony started: crowd " + crowd.Count + (block ? " placed on slot B" : "") + ", honoree " + sim.FullName + " sent";
        }

        // ---- petfireplan ----

        // "start" lights one fire on the active household's home lot, 3 m from a household pet (or the active Sim when no
        // pet is home), so the pets' ReactToFire runs; "stop" puts out every fire on that lot at once; "result" gives
        // counters 2320/2321 and the fires burning. Route failures per arm come from ts3native.log's routefail lines.
        public static object PetFireCheck(string what)
        {
            Sim sim = Sim.ActiveActor;
            if (sim == null || sim.LotHome == null) return "petfire check: no active Sim with a home lot";
            Lot home = sim.LotHome;
            if (what == "result")
                return "petfire: plans skipped 2320=" + PatchCounters()[2320] + ", errors 2321=" + PatchCounters()[2321] + ", fires " + home.GetObjects<Fire>().Length;
            if (what == "stop")
            {
                int n = 0;
                foreach (Fire f in home.GetObjects<Fire>()) { f.ExtinguishFireNow(true); n++; }
                return "petfire: put out " + n;
            }
            if (what != "start") return "petfire <start|stop|result>";
            Sim near = sim;
            foreach (Sim s in sim.Household.Sims)
                if (s.SimDescription.IsPet && s.LotCurrent == home) { near = s; break; }
            if (near.LotCurrent != home) return "petfire check: nobody of the household is on the home lot";
            Fire fire = FireManager.AddFire(near.Position + near.ForwardVector * 3f) ?? FireManager.AddFire(near.Position - near.ForwardVector * 3f);
            return fire == null ? "petfire check: AddFire refused" : "petfire: fire lit near " + near.FullName + (near.SimDescription.IsPet ? " (pet)" : "");
        }
    }
}
