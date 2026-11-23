using System;

namespace Ts3Fixes
{
    // docs/641 spotwaitattr (default off, log and counters only, no behaviour change). A lot-edge routability check
    // (PlanFromPoint inside EA's Sim.IsPointInLotSafelyRoutable: SafeSpot's "spot", the watchdog, EA's own callers) sleeps
    // one frame per READY poll. Its wall time is the frames it slept through, not its job: when another part of the frame
    // stalls the sim thread, a one-poll check reads the whole stall ("ufplansplit: kind 2 683 ms, wait 683 (1 polls)") although
    // its job ended within that frame. Each such wait of >= kSpotWaitMs is split by its longest single poll: frame-bound
    // = one frame holds >= 80 % of the wait (the stall is elsewhere in that frame; a short job with 1-2 polls), job-bound =
    // the wait is spread over polls (the job itself ran that long). Counters [4662] waits >= kSpotWaitMs, [4663] frame-bound,
    // [4664] job-bound, [4665] frame-bound ms. Line "spotwait:" (60 a session).
    public static partial class Fixes
    {
        const int kSpotWaitMs = 250, kSpotWaitLines = 60;
        static int sSpotWaitLogged;

        // Pure: 1 = frame-bound, 2 = job-bound, 0 = not a slow async wait
        internal static int SpotWaitClass(int waitMs, int polls, int maxPollMs)
        {
            if (waitMs < kSpotWaitMs || polls <= 0) return 0;
            return maxPollMs * 5 >= waitMs * 4 ? 1 : 2;
        }

        static void SpotWaitNote(int planMs, UfSplit u, string src)
        {
            if (!u.Seen || u.SyncMs >= 0) return;  // no async wait (sync plan, START refused, or answered without a job)
            int k = SpotWaitClass(u.WaitMs, u.Polls, u.MaxPollMs);
            if (k == 0) return;
            int[] c = PatchCounters();
            c[4662]++;
            if (k == 1) { c[4663]++; c[4665] += u.WaitMs; } else c[4664]++;
            if (sSpotWaitLogged >= kSpotWaitLines) return;
            sSpotWaitLogged++;
            DiagLine("spotwait: " + (k == 1 ? "frame-bound" : "job-bound") + ", plan " + planMs + " ms, wait " + u.WaitMs + " ms in " + u.Polls
                + " polls, longest poll " + u.MaxPollMs + " ms" + (k == 1 ? " (one game frame: the stall is elsewhere in it, the job ended inside it)" : "")
                + ", src " + (src ?? "?") + ", tick " + Sims3.Gameplay.Utilities.SimClock.CurrentTicks + ", at " + System.Environment.TickCount + " ms");
        }
    }
}
