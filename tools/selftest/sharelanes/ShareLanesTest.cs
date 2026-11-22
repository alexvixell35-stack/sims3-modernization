// Usage: node tools/selftest/sharelanes/run.ts  (compiles this with mods/ts3fixes/src/ShareLanesCore.cs)
// Offline self-check of ShareLanesCore.cs (docs/686 §8): the lane classes against the game's table answer, the decision by
// switch and scope, the game's lane choice with its fallback, a model of Sims crossing a one-lane door and two-lane stairs
// (the game's table against the shared one), the portal pass schedule, the walk-through rule, the ladder rules and the
// wait counter. Exit code 1 on failure.
using System;
using Ts3Fixes;

static class ShareLanesTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    const int N = ShareLanesCore.kNone, W = ShareLanesCore.kWould, X = ShareLanesCore.kExempt, G = ShareLanesCore.kGo;
    const int kFree = ShareLanesCore.kLaneFree, kOpp = ShareLanesCore.kLaneOpposite, kClosed = ShareLanesCore.kLaneClosed;

    static ShareLanesCore.Holder H(uint lane, uint slot, bool inScope, bool elevator)
    {
        ShareLanesCore.Holder h = new ShareLanesCore.Holder();
        h.Lane = lane; h.StartSlot = slot; h.InScope = inScope; h.Elevator = elevator;
        return h;
    }

    static void Lanes()
    {
        ShareLanesCore.Holder[] none = new ShareLanesCore.Holder[0];
        Check(ShareLanesCore.EaFree(none, 0, 0, 10) && ShareLanesCore.Lane(none, 0, 0, 10) == kFree, "empty table: free");
        ShareLanesCore.Holder[] h = { H(0, 20, true, false) };
        Check(!ShareLanesCore.EaFree(h, 1, 0, 10) && ShareLanesCore.Lane(h, 1, 0, 10) == kOpp, "held from the other end: the game refuses, ours opposite");
        Check(ShareLanesCore.EaFree(h, 1, 0, 20) && ShareLanesCore.Lane(h, 1, 0, 20) == kFree, "held from the same end: both free");
        Check(ShareLanesCore.EaFree(h, 1, 1, 11) && ShareLanesCore.Lane(h, 1, 1, 11) == kFree, "another lane: both free");
        h[0] = H(0, 20, false, false);
        Check(ShareLanesCore.Lane(h, 1, 0, 10) == kClosed && ShareLanesCore.Lane(h, 1, 0, 20) == kFree, "a holder out of scope closes only the other direction");
        h[0] = H(0, 10, true, true);
        Check(!ShareLanesCore.EaFree(h, 1, 0, 10) && ShareLanesCore.Lane(h, 1, 0, 10) == kClosed, "an elevator lane is closed in both");
        // the game looks at the first holder only; ours at all of them
        ShareLanesCore.Holder[] two = { H(0, 10, true, false), H(0, 20, true, false) };
        Check(ShareLanesCore.EaFree(two, 2, 0, 10) && ShareLanesCore.Lane(two, 2, 0, 10) == kOpp, "mixed lane: the game's first-holder test says free, ours opposite (still sharable)");
        two[1] = H(0, 20, false, false);
        Check(ShareLanesCore.Lane(two, 2, 0, 10) == kClosed, "mixed lane with an out-of-scope opposite holder: closed");
        // whatever the table: ours never calls a lane closed that the game's test gives, unless a holder is out of scope or an elevator
        Random r = new Random(686);
        for (int t = 0; t < 4000; t++)
        {
            int n = r.Next(0, 5);
            ShareLanesCore.Holder[] a = new ShareLanesCore.Holder[n];
            bool special = false;
            for (int i = 0; i < n; i++) { a[i] = H((uint)r.Next(0, 2), (uint)(10 + 10 * r.Next(0, 2)), r.Next(0, 4) != 0, r.Next(0, 8) == 0); special |= !a[i].InScope || a[i].Elevator; }
            uint lane = (uint)r.Next(0, 2), slot = (uint)(10 + 10 * r.Next(0, 2));
            int cls = ShareLanesCore.Lane(a, n, lane, slot);
            if (!special) Check(cls != kClosed, "all in scope, no elevator: never closed");
            if (cls == kFree) { bool opp = false; for (int i = 0; i < n; i++) if (a[i].Lane == lane && a[i].StartSlot != slot) opp = true; Check(!opp, "free means no holder from the other end"); }
        }
    }

    static void Decisions()
    {
        Check(ShareLanesCore.Decide(false, true, true, true) == W && ShareLanesCore.Decide(true, true, true, true) == G, "off counts, on shares");
        Check(ShareLanesCore.Decide(true, false, true, true) == X, "household Sim without its switch: exempt");
        Check(ShareLanesCore.Decide(true, true, false, true) == N && ShareLanesCore.Decide(true, true, true, false) == N, "another portal kind or no sharable lane: the game's refusal stays");
        bool fb = false, isFb;
        Check(ShareLanesCore.Take(true, true, false, ref fb, out isFb) && !fb && !isFb, "checks pass: taken");
        Check(!ShareLanesCore.Take(true, false, false, ref fb, out isFb) && fb && isFb, "obstructed: the fallback");
        Check(!ShareLanesCore.Take(true, false, false, ref fb, out isFb) && fb && !isFb, "a second obstructed lane does not replace the fallback");
        fb = false;
        Check(!ShareLanesCore.Take(false, false, false, ref fb, out isFb) && !fb, "extra checks failed: no fallback");
        Check(!ShareLanesCore.Take(false, false, true, ref fb, out isFb) && fb && isFb, "extra checks failed on a lane that may skip them: the fallback");
    }

    // Sims arrive at a portal with `lanes` lanes from end 0 or 1, hold a lane for `hold` steps. Returns the steps Sims
    // spent refused. shared = our rule; else the game's.
    static int Model(int lanes, int sims, int hold, bool shared, Random r, out int maxHolders)
    {
        ShareLanesCore.Holder[] h = new ShareLanesCore.Holder[sims];
        int[] left = new int[sims], arrive = new int[sims], end = new int[sims];
        bool[] holds = new bool[sims], done = new bool[sims];
        int n = 0, waited = 0, finished = 0;
        maxHolders = 0;
        for (int i = 0; i < sims; i++) { arrive[i] = r.Next(0, hold * 2); end[i] = r.Next(0, 2); }
        for (int step = 0; step < 100000 && finished < sims; step++)
        {
            for (int i = 0; i < sims; i++)
            {
                if (done[i] || step < arrive[i]) continue;
                if (holds[i])
                {
                    if (--left[i] > 0) continue;
                    // leave: drop this Sim's row
                    for (int k = 0; k < n; k++) if (h[k].StartSlot / 100 == (uint)i) { h[k] = h[n - 1]; n--; break; }
                    holds[i] = false; done[i] = true; finished++;
                    continue;
                }
                bool got = false;
                for (int lane = 0; lane < lanes && !got; lane++)
                {
                    uint slot = (uint)end[i];
                    // rows carry the Sim in the hundreds of StartSlot; strip it for the test
                    ShareLanesCore.Holder[] plain = new ShareLanesCore.Holder[n];
                    for (int k = 0; k < n; k++) { plain[k] = h[k]; plain[k].StartSlot = h[k].StartSlot % 100; }
                    bool free = shared ? ShareLanesCore.Lane(plain, n, (uint)lane, slot) != kClosed : ShareLanesCore.EaFree(plain, n, (uint)lane, slot);
                    if (!free) continue;
                    h[n++] = H((uint)lane, (uint)(i * 100 + end[i]), true, false);
                    holds[i] = true; left[i] = hold; got = true;
                }
                if (!got) waited++;
            }
            if (n > maxHolders) maxHolders = n;
        }
        Check(finished == sims, "every Sim crosses (" + (shared ? "shared" : "game") + ", lanes " + lanes + ")");
        Check(n == 0, "the table is empty at the end");
        return waited;
    }

    static void Crossings()
    {
        Random r = new Random(2026);
        int eaDoor = 0, ourDoor = 0, eaStairs = 0, ourStairs = 0, m;
        for (int t = 0; t < 300; t++)
        {
            int seed = r.Next();
            eaDoor += Model(1, 6, 8, false, new Random(seed), out m);
            ourDoor += Model(1, 6, 8, true, new Random(seed), out m);
            Check(m <= 6, "never more rows than Sims");
            eaStairs += Model(2, 6, 8, false, new Random(seed), out m);
            ourStairs += Model(2, 6, 8, true, new Random(seed), out m);
        }
        Check(ourDoor == 0 && ourStairs == 0, "shared: nobody is refused");
        Check(eaDoor > 0, "the game's table makes Sims wait at a one-lane door (" + eaDoor + " steps)");
        Check(eaStairs < eaDoor, "two lanes wait less than one under the game's table (" + eaStairs + " < " + eaDoor + ")");
        Console.WriteLine("crossings: refused steps, game door " + eaDoor + ", game stairs " + eaStairs + ", shared " + ourDoor + " / " + ourStairs);
    }

    static void Rest()
    {
        Check(ShareLanesCore.PortalPass(true, true, true, 1, 3) == G && ShareLanesCore.PortalPass(true, true, true, 2, 3) == N
            && ShareLanesCore.PortalPass(true, true, true, 4, 3) == G, "portal pass: first blocked cycle, then every third");
        Check(ShareLanesCore.PortalPass(true, true, false, 1, 3) == N && ShareLanesCore.PortalPass(true, true, true, 0, 3) == N, "another plan answer or no blocked cycle: nothing");
        Check(ShareLanesCore.PortalPass(false, true, true, 1, 3) == W && ShareLanesCore.PortalPass(true, false, true, 1, 3) == X, "portal pass: off counts, household exempt");
        Check(ShareLanesCore.PortalPass(true, true, true, 3, 0) == G, "a spacing below 1 is every cycle");
        Check(ShareLanesCore.LadderLine(true, true, true, true) == G && ShareLanesCore.LadderLine(false, true, true, true) == W, "ladder line: on skips, off counts");
        Check(ShareLanesCore.LadderLine(true, false, true, true) == N, "another object's line is never skipped");
        Check(ShareLanesCore.LadderLine(true, true, false, true) == X && ShareLanesCore.LadderLine(true, true, true, false) == X, "a household Sim climbing or in the line without its switch: exempt");
        bool kept;
        Check(ShareLanesCore.Pass(true, true, 2, 0, true, false, out kept) == G && !kept, "only Sims in the way: walk through");
        Check(ShareLanesCore.Pass(true, true, 0, 0, true, false, out kept) == N && !kept, "nothing in the way: nothing");
        Check(ShareLanesCore.Pass(true, true, 1, 1, true, false, out kept) == N && kept, "an object in the way: the game's wait");
        Check(ShareLanesCore.Pass(true, true, 1, 0, true, true, out kept) == N && kept, "a Sim at the route's end: the game's wait");
        Check(ShareLanesCore.Pass(false, true, 1, 0, true, false, out kept) == W, "off counts");
        Check(ShareLanesCore.Pass(true, false, 1, 0, true, false, out kept) == X && ShareLanesCore.Pass(true, true, 1, 0, false, false, out kept) == X, "walker or a Sim in the way out of scope: exempt");
        Timing();
        Check(ShareLanesCore.AngleStairs(true, true, true, true, false, 3) == G && ShareLanesCore.AngleStairs(false, true, true, true, false, 3) == W
            && ShareLanesCore.AngleStairs(true, false, true, true, false, 3) == X, "stairs approach: on, off, exempt");
        Check(ShareLanesCore.AngleStairs(true, true, true, false, false, 3) == N && ShareLanesCore.AngleStairs(true, true, true, true, true, 3) == N
            && ShareLanesCore.AngleStairs(true, true, false, true, false, 3) == N && ShareLanesCore.AngleStairs(true, true, true, true, false, 0) == N,
            "stairs approach: the game plans it itself, not stairs, or no path: nothing");
        Check(ShareLanesCore.AngleLadder(true, true, true, true, true) == G && ShareLanesCore.AngleLadder(true, true, true, false, true) == N
            && ShareLanesCore.AngleLadder(true, true, true, true, false) == N && ShareLanesCore.AngleLadder(true, true, false, true, true) == N, "ladder approach: only the climb up, only a slot plan");
        Check(ShareLanesCore.AngleLadder(false, true, true, true, true) == W && ShareLanesCore.AngleLadder(true, false, true, true, true) == X, "ladder approach: off, exempt");
        Check(ShareLanesCore.Tenths(0f) == 0 && ShareLanesCore.Tenths(-3f) == 0 && ShareLanesCore.Tenths(float.NaN) == 0 && ShareLanesCore.Tenths(1.24f) == 12
            && ShareLanesCore.Tenths(20f) == 200 && ShareLanesCore.Tenths(1e9f) == 6000, "wait counter: tenths, never negative, capped");
        Check(ShareLanesCore.kKindNames.Length == 6 && ShareLanesCore.kKindNames[ShareLanesCore.kSpiral] == "spiral", "a name per portal kind (spiral stairs: switch spiralshare)");
    }

    // docs/686 §8.8: at once only near a portal, elsewhere after the shared grace; the near-portal test
    static void Timing()
    {
        const int D = ShareLanesCore.kDefer;
        Check(ShareLanesCore.Timed(G, true, 0f, 2.5f) == G, "near a portal: at once");
        Check(ShareLanesCore.Timed(G, false, 0f, 2.5f) == D && ShareLanesCore.Timed(G, false, 2.4f, 2.5f) == D, "open space before the grace: left to the wait");
        Check(ShareLanesCore.Timed(G, false, 2.5f, 2.5f) == G && ShareLanesCore.Timed(G, false, 30f, 2.5f) == G, "open space from the grace on: walk through");
        Check(ShareLanesCore.Timed(W, false, 0f, 2.5f) == D && ShareLanesCore.Timed(X, false, 0f, 2.5f) == D, "would and exempt are not counted before the grace either");
        Check(ShareLanesCore.Timed(W, true, 0f, 2.5f) == W && ShareLanesCore.Timed(X, false, 3f, 2.5f) == X, "would and exempt keep their answer when due");
        Check(ShareLanesCore.Timed(N, false, 0f, 2.5f) == N && ShareLanesCore.Timed(N, true, 9f, 2.5f) == N, "nothing to do stays nothing");
        Check(ShareLanesCore.Timed(G, false, 0f, 0f) == G && ShareLanesCore.Timed(G, false, 0f, -1f) == G && ShareLanesCore.Timed(G, false, 0f, float.NaN) == G, "grace 0, negative or NaN: at once everywhere");
        Check(ShareLanesCore.Timed(G, false, float.NaN, 2.5f) == D, "a broken clock never shortens the grace");
        Check(ShareLanesCore.GraceDue(2.5f, 2.5f) && !ShareLanesCore.GraceDue(2.49f, 2.5f), "grace: due from the tunable on");
        // a door lane: front (0,0,0) to rear (0,0,2)
        Check(ShareLanesCore.NearLane(0f, 0f, -1.4f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f), "1.4 m in front of a door's lane end: near");
        Check(!ShareLanesCore.NearLane(0f, 0f, -1.6f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f), "1.6 m in front: not near");
        Check(ShareLanesCore.NearLane(1.4f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f) && !ShareLanesCore.NearLane(1.6f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f), "beside the doorway: by the distance to the lane");
        Check(ShareLanesCore.NearLane(0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f), "in the doorway: near");
        Check(!ShareLanesCore.NearLane(0f, 3f, 1f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f), "the floor above the door: not near");
        // a stair lane: foot (0,0,0) to top (0,3,4)
        Check(ShareLanesCore.NearLane(0.3f, 1.5f, 2f, 0f, 0f, 0f, 0f, 3f, 4f, 1.5f), "on the stairs, halfway up: near");
        Check(ShareLanesCore.NearLane(0f, 3f, 5f, 0f, 0f, 0f, 0f, 3f, 4f, 1.5f) && !ShareLanesCore.NearLane(0f, 3f, 6f, 0f, 0f, 0f, 0f, 3f, 4f, 1.5f), "at the stair top: 1 m near, 2 m not");
        Check(!ShareLanesCore.NearLane(3f, 0f, 2f, 0f, 0f, 0f, 0f, 3f, 4f, 1.5f), "3 m beside the stairs: not near");
        Check(ShareLanesCore.NearLane(1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1.5f) && !ShareLanesCore.NearLane(2f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1.5f), "a lane whose ends are one point: the distance to it");
        Check(!ShareLanesCore.NearLane(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 2f, 0f) && !ShareLanesCore.NearLane(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 2f, float.NaN), "near 0 or NaN: never (the grace everywhere)");
        Check(!ShareLanesCore.NearLane(float.NaN, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f), "a broken position is not near");
        // simpass with the timing: two Sims meet in a hallway and at a door
        bool kept;
        int pass = ShareLanesCore.Pass(true, true, 1, 0, true, false, out kept);
        Check(ShareLanesCore.Timed(pass, false, 0f, 2.5f) == D && ShareLanesCore.Timed(pass, true, 0f, 2.5f) == G && ShareLanesCore.Timed(pass, false, 2.5f, 2.5f) == G, "simpass: hallway waits the grace, door at once");
    }

    static int Main()
    {
        Lanes();
        Decisions();
        Crossings();
        Rest();
        Console.WriteLine(sFails == 0 ? "sharelanes selftest: ok" : "sharelanes selftest: " + sFails + " failed");
        return sFails == 0 ? 0 : 1;
    }
}
