using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // strayleave (tuning kStrayLeave, default off; docs/656 §4.2). EA sends a stray (pet pool StrayCat / StrayDog) to a lot
    // with StrayGoToLot; on arrival it gets the StrayPet buff for 30-90 sim-minutes (kVisitLengthMin/Max), and only the buff's
    // OnTimeout sends it home (Sim.MakeSimGoHome -> GoToVirtualHome) [verified, decompiled BuffStrayPet, StrayGoToLot,
    // StrayPets]. Meta-autonomy's go-home runs only when autonomy picked nothing, and a pet always has Sit On Ground. A stray
    // that lost the buff (a reset clears buffs, a failed walk home) or never got it (GoToLot failed after the arrival callback)
    // stays on the lot for days, sitting and standing in place (health.jsonl: CC Cat, Dink Cat, Zoe Cat 70-83 sim-hours
    // still; pets still > 12 h with no interaction: 71 % of NPC pet samples vs 23.5 % of humans). EA also counts every
    // instantiated stray against kMaxStraysInWorld (6), so stuck strays stop new stray visits.
    // Fix: EA's own hourly StrayPets.StrayPetHelper alarm first looks at each instantiated stray: a non-exempt stray without
    // the StrayPet buff, not leaving or on a trip (GoHome, GoToVirtualHome, GoToLot, StrayGoToLot, ReactToStrayGnome in its
    // queue), not in a social and not held, for kStrayLeaveHours sim-hours gets EA's Sim.MakeSimGoHome(sim, false), exactly
    // what the buff's timeout does. A walk, no teleport. Household, selected and unknown-scope strays (HouseholdExempt or
    // IsPlayerSim) are never touched. Rule: StrayLeaveCore. Counters [4174] strays sent home, [4175] strays seen without the
    // buff (clock started), [4176] exempt, [4177] errors. Log "strayleave: ..." capped at 10 a minute.
    public static partial class Fixes
    {
        static readonly StrayLeaveCore sSl = new StrayLeaveCore();
        static int sSlMinute = -1, sSlLogged;

        static void ApplyStrayLeave()
        {
            Patch(typeof(StrayPets), "StrayPetHelper", Type.EmptyTypes, typeof(StrayLeaveHook), "HelperHook", "HelperOrig");
        }

        static void SlLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sSlMinute) { sSlMinute = minute; sSlLogged = 0; }
            if (sSlLogged++ < 10) DiagLine("strayleave: " + s);
        }

        static bool SlTrip(InteractionInstance ia)
        {
            if (ia == null) return false;
            if (ia is GoHome || ia is Sim.GoToVirtualHome || ia is Sim.GoToVirtualHome.GoToVirtualHomeInternal || ia is GoToLot) return true;
            string n = ia.GetType().Name;
            return n == "StrayGoToLot" || n == "ReactToStrayGnome";
        }

        static bool SlBusy(Sim s)
        {
            if (s.Conversation != null || s.CurrentInteraction is Sims3.Gameplay.Socializing.SocialInteraction) return true;
            if (UfHeld(s) || s.Parent != null) return true;
            if (SlTrip(s.CurrentInteraction)) return true;
            if (s.InteractionQueue != null)
                foreach (InteractionInstance ia in s.InteractionQueue.InteractionList)
                    if (SlTrip(ia)) return true;
            return false;
        }

        // from StrayPetHelper (EA's hourly alarm), before EA's own body
        static void SlScan()
        {
            int[] c = PatchCounters();
            try
            {
                List<SimDescription> strays = StrayPets.GetStrayPets(false);
                if (strays == null) return;
                long now = SimClock.CurrentTicks, limit = SimClock.ConvertToTicks(Instantiator.kStrayLeaveHours, TimeUnit.Hours);
                foreach (SimDescription d in strays)
                {
                    Sim s = d != null ? d.CreatedSim : null;
                    if (s == null) continue;
                    if (s.HasBeenDestroyed || !s.InWorld) { sSl.Forget(s.ObjectId.Value); continue; }
                    bool buff = s.BuffManager != null && s.BuffManager.HasElement(BuffNames.StrayPet);
                    int v = sSl.Step(s.ObjectId.Value, HouseholdExempt(s) || IsPlayerSim(s), buff, !buff && SlBusy(s), now, limit);
                    if (v == StrayLeaveCore.kExempt) { if (!buff) c[4176]++; continue; }
                    if (v == StrayLeaveCore.kNew) { c[4175]++; continue; }
                    if (v != StrayLeaveCore.kSend) continue;
                    string where = s.LotCurrent != null ? s.LotCurrent.Name : "street";
                    bool pushed = Sim.MakeSimGoHome(s, false);
                    if (pushed) c[4174]++;
                    SlLog(s.FullName + " (" + d.Species + ") on " + where + ": no StrayPet buff for " + Instantiator.kStrayLeaveHours
                        + " sim-h, " + (pushed ? "EA's MakeSimGoHome pushed" : "MakeSimGoHome refused"));
                }
            }
            catch (Exception ex) { c[4177]++; SlLog("error " + ex.GetType().Name); }
        }

        public class StrayLeaveHook
        {
            public static void HelperHook()
            {
                if (ShortcutOn("strayleave")) SlScan();
                HelperOrig();
            }
            public static void HelperOrig() { } // receives StrayPets.StrayPetHelper's original body
        }
    }
}
