using System;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.Enums;

namespace Ts3Fixes
{
    // petstartleskip (tuning kPetStartleSkip, default off; docs/656 §8.1). A startled pet runs Sim.PetStartle, whose flee route
    // (run away after the reaction) can head through a door locked for pets; the plan fails with
    // FailedBecauseDestinationPortalLocked and the next noise (TV, stereo, shower, dishwasher: PetStartleBehavior.CheckForStartle)
    // pushes the same startle again once EA's per-pet cooldown ends (OFF runs: Bridgeport 321 in 40 runs, Isla Paradiso 251,
    // Twinbrook 174 such fails).
    // Fix: when a non-exempt NPC pet's plan inside PetStartle (its current interaction) fails with PortalLocked
    // (RouteFailLog.RflPlan calls PssPlan), the pet is remembered for kPetStartleSkipMinutes sim-minutes, and
    // PetStartleBehavior.StartlePet (the funnel every startle goes through) returns at once for it, as EA itself does inside
    // the cooldown. Forced startles still run: ignoreCooldown, electrocution, Grim Reaper, fire and meteor. Household,
    // selected and unknown-scope pets (HouseholdExempt or IsPlayerSim) are never recorded and never skipped. Rule:
    // PetStartleSkipCore. Counters [4620] recorded, [4621] startles skipped, [4624] exempt, [4625] errors (shared with petlonghome).
    // Log "petstartleskip: recorded|skipped <pet> ...", capped at 10 a minute.
    public static partial class Fixes
    {
        static readonly PetStartleSkipCore sPss = new PetStartleSkipCore();
        static int sPssMinute = -1, sPssLogged;

        static void ApplyPetStartleSkip()
        {
            Patch(typeof(PetStartleBehavior), "StartlePet", new Type[] { typeof(Sim), typeof(StartleType), typeof(Origin), typeof(GameObject), typeof(bool), typeof(PetStartleReactionType), typeof(InteractionPriority) }, typeof(PetStartleSkipHook), "StartleHook", "StartleOrig");
        }

        static void PssLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sPssMinute) { sPssMinute = minute; sPssLogged = 0; }
            if (sPssLogged++ < 10) DiagLine("petstartleskip: " + s);
        }

        static bool PssExempt(Sim s) { return HouseholdExempt(s) || IsPlayerSim(s); }

        // from RflPlan (RouteFailLog.cs), failed plans of a Sim follower only
        static void PssPlan(Sim sim, RoutePlanResult res)
        {
            if (res.mType != RoutePlanResultType.FailedBecauseDestinationPortalLocked || !ShortcutOn("petstartleskip")) return;
            int[] c = PatchCounters();
            try
            {
                if (!(sim.CurrentInteraction is Sim.PetStartle)) return;
                bool pet = sim.SimDescription != null && sim.SimDescription.IsPet;
                long life = SimClock.ConvertToTicks(Instantiator.kPetStartleSkipMinutes, TimeUnit.Minutes);
                int r = sPss.Record(sim.ObjectId.Value, pet, PssExempt(sim), (int)res.mType, SimClock.CurrentTicks, life);
                if (r == PetStartleSkipCore.kExempt) { c[4624]++; return; }
                if (r != PetStartleSkipCore.kRecorded) return;
                c[4620]++;
                PssLog("recorded " + sim.FullName + " (" + sim.SimDescription.Species + "), flee route PortalLocked, startles skipped "
                    + Instantiator.kPetStartleSkipMinutes + " sim-min");
            }
            catch (Exception ex) { c[4625]++; PssLog("error " + ex.GetType().Name); }
        }

        static bool PssForced(StartleType t, bool ignoreCooldown)
        {
            return ignoreCooldown || t == StartleType.Electrocution || t == StartleType.GrimReaperAppear || t == StartleType.Fire || t == StartleType.Meteor;
        }

        public class PetStartleSkipHook
        {
            public static void StartleHook(Sim s, StartleType startleType, Origin origin, GameObject objThatIsStartling, bool ignoreCooldown,
                PetStartleReactionType forcedSpecificReactionType, InteractionPriority interactionPriority)
            {
                if (s != null && sPss.Count > 0 && ShortcutOn("petstartleskip") && PssSkip(s, startleType, ignoreCooldown)) return;
                StartleOrig(s, startleType, origin, objThatIsStartling, ignoreCooldown, forcedSpecificReactionType, interactionPriority);
            }
            public static void StartleOrig(Sim s, StartleType startleType, Origin origin, GameObject objThatIsStartling, bool ignoreCooldown,
                PetStartleReactionType forcedSpecificReactionType, InteractionPriority interactionPriority) { } // receives PetStartleBehavior.StartlePet's original body
        }

        static bool PssSkip(Sim s, StartleType t, bool ignoreCooldown)
        {
            int[] c = PatchCounters();
            try
            {
                long now = SimClock.CurrentTicks;
                ulong id = s.ObjectId.Value;
                if (!sPss.Live(id, now)) return false;
                int d = sPss.Startle(id, PssForced(t, ignoreCooldown), PssExempt(s), now);
                if (d == PetStartleSkipCore.kPassExempt) { c[4624]++; return false; }
                if (d != PetStartleSkipCore.kSkip) return false;
                c[4621]++;
                PssLog("skipped " + s.FullName + ", " + t);
                return true;
            }
            catch (Exception ex) { c[4625]++; PssLog("error " + ex.GetType().Name); return false; }
        }
    }
}
