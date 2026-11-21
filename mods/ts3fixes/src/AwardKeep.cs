using System;
using System.Reflection;
using Sims3.Gameplay.Objects.RabbitHoles;

namespace Ts3Fixes
{
    // awardkeep (tuning kAwardKeep, off by default; docs/546 §11). EA bug fix, every Sim.
    //
    // A career "great success" (e.g. the firefighter's hero award) books a ceremony at City Hall: CityHall.CreateCelebration
    // makes the award jig and an NPC mayor, and the honoree's AttendAwardCeremony pushes a GiveAward pair (mayor = master,
    // honoree = receiver). GiveAward.Cleanup on the master calls GreatSuccessCelebrationCleanup whether or not the award
    // was given, which destroys the mayor, the jig and the crowd broadcasters and drops CeremonyAwardInfo. So when the
    // honoree's route to the jig fails (route tantrum), the mayor's route fails, or the two never sync, the ceremony
    // vanishes and the award (kSimAcceptedReward, the HeroOfTheCity buff) is lost for good [verified in the decompiled game
    // code; reported in play 2026-11-14]. EA's only fallback is MissedCelebrationCallback, the timer for a ceremony nobody
    // attended: it tells the player and turns the award into a "Collect Service Award" pickup at City Hall, then cleans up.
    //
    // The hook runs that same fallback when the master cleans up a ceremony that is still open: the success path calls
    // ClearCanAttendAwardCeremony before Cleanup, so CanAttendAwardCeremony still true with CeremonyAwardInfo set means the
    // award was not given. A second GreatSuccessCelebrationCleanup (from EA's Cleanup body) finds everything already
    // cleared and only re-clears the flag and the protester list, both empty. The successful ceremony is unchanged.
    // Counter [2322] awards kept as a pickup, [2323] errors (registry fixes-2322). Harness check: AwardKeepCheck (EventChecks.cs).
    public static partial class Fixes
    {
        static readonly MethodInfo sAkMissed = typeof(CityHall).GetMethod("MissedCelebrationCallback", kInstAll);

        static void ApplyAwardKeep()
        {
            if (sAkMissed == null) { AppliedLog().Add("CityHall.MissedCelebrationCallback: not found, awardkeep skipped"); return; }
            Patch(typeof(CityHall.GiveAward), "Cleanup", typeof(AwardKeepHook), "CleanupHook", "CleanupOrig");
        }

        public class AwardKeepHook
        {
            public void CleanupHook()
            {
                AwardKeepFallback((CityHall.GiveAward)(object)this);
                CleanupOrig();
            }
            public void CleanupOrig() { } // receives CityHall.GiveAward.Cleanup's original body
        }

        static void AwardKeepFallback(CityHall.GiveAward ga)
        {
            if (!ShortcutOn("awardkeep")) return;
            try
            {
                CityHall hall = ga.RabbitHoleToRouteTo;
                if (!ga.IsMaster || hall == null || hall.CeremonyAwardInfo == null || !hall.CanAttendAwardCeremony) return;
                sAkMissed.Invoke(hall, null);  // EA's missed-ceremony path: notice, pickup, cleanup
                PatchCounters()[2322]++;
            }
            catch (Exception) { PatchCounters()[2323]++; }
        }
    }
}
