using System;
using System.Collections.Generic;
using Ts3Fixes;
using FM = Ts3Fixes.FarSimModel;

// docs/345 §13.2 selftests of the far-Sim machine core (FarSimModel.cs): D1 PRNG vector, D2 same seed twice, D3 shuffled
// order and DueBudget 1 / 8 / unlimited, D4 lazy against stepped, D5 catch-up, D6 sidecar line format, D8 version bump,
// leak churn, household rows, hysteresis, kill list, drift bounds, shadow table line formats.
static class FarSimTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    // Recorded-style inputs: every answer is a pure function of (id, day), like a replay file.
    sealed class In : FM.IInputs
    {
        public ulong HouseholdId, LateExclId; public int LateExcl;
        public int Excl(ulong id) { return id == HouseholdId ? FM.XHousehold : id == LateExclId ? LateExcl : 0; }
        public int WakeMin(ulong id) { return id % 7 == 0 ? 840 : 300 + (int)(id % 4) * 30; }
        public bool Shift(ulong id, int day, out int s, out int e, out ulong place, out bool school)
        {
            school = id % 5 == 1; place = 9000 + id % 3; s = school ? 540 : 480 + (int)(id % 3) * 60; e = school ? 900 : s + 480;
            return id % 5 <= 2 && day % 7 < 5;
        }
        public int VenueCount(int day) { return 4; }
        public ulong Venue(int day, int i) { return 7000 + (ulong)i; }
        public int TravelMin(ulong a, ulong b) { return 20 + (int)((a ^ b) % 25); }
        public int VisitPermille(ulong id, int day) { return id % 3 == 0 ? 300 : 0; }
        public bool VisitOk(ulong id, int day, int minute) { return (id + (ulong)day) % 4 != 0; }
    }

    static FM.Cfg Cfg(bool all)
    {
        FM.Cfg c = new FM.Cfg();
        c.WorldSeed = 0x9c41d2e07b53a611UL;
        c.Work = c.Venue = c.Visit = all;
        return c;
    }

    const int kTpd = 54000;

    // Distance script: far all the time, except that a Sim is near for one hour on day 3 + id % 20 (a camera visit).
    static int Dist(ulong id, long t) { long day = t / kTpd, m = t % kTpd; return day == 3 + (long)(id % 20) && m >= 27000 && m < 29250 ? 5000 : 60000; }

    // Three passes that take one Sim from nothing to a far state when the dwell is 1 minute (or already served).
    static void Far(FM.Machine m, ulong id, long t)
    {
        for (int i = 0; i < 3; i++) m.Boundary(id, t + m.C.Ticks(i), 0, 90000, false, false, true, 50);
    }

    // Runs `sims` Sims for `days` days: a boundary pass every 5 sim minutes in the given order, a drain with `budget` after
    // each. Go-lives finish at once (shadow). Returns the trace hash.
    static ulong Run(int sims, int days, int budget, bool shuffle, out FM.Machine mOut)
    {
        In inp = new In();
        FM.Machine m = new FM.Machine(Cfg(true), inp);
        m.Trace = new List<FM.TraceLine>();
        ulong[] ids = new ulong[sims];
        for (int i = 0; i < sims; i++) ids[i] = 7215113988226514UL + (ulong)i * 977;
        if (shuffle) for (int i = sims - 1; i > 0; i--) { int k = (int)FM.Range(FM.M((ulong)i), (uint)(i + 1)); ulong t = ids[i]; ids[i] = ids[k]; ids[k] = t; }
        long step = m.C.Ticks(5), end = (long)days * kTpd;
        for (long now = 0; now <= end; now += step)
        {
            for (int i = 0; i < sims; i++)
            {
                ulong id = ids[i];
                m.Boundary(id, now, inp.Excl(id), Dist(id, now), false, false, true, 5000 + id % 50);
                FM.Rec r = m.Find(id);
                if (r != null && r.State == FM.PendingLive) m.GoLiveDone(id, now, true);
            }
            m.Drain(now, budget);
        }
        m.Drain(end, 0);  // a limited budget may leave edges: settle them so the runs end at the same point
        for (int i = 0; i < sims; i++) { FM.Rec r = m.Find(ids[i]); if (r != null && r.State == FM.PendingLive) m.GoLiveDone(ids[i], end, true); }
        mOut = m;
        return FM.TraceHash(m.Trace);
    }

    static int Main()
    {
        // D1: docs/345 §8.2 test vector and the §13.1 trace hash example
        Check(FM.M(0) == 0xe220a8397b1dcdafUL, "M(0)");
        ulong seed = FM.Seed(0x9c41d2e07b53a611UL, 7215113988226514UL, 1);
        Check(seed == 0x2aebc8589dd9f080UL, "seed vector: " + seed.ToString("x16"));
        ulong draw = FM.Draw(seed, 0, 12, 0, 3);
        Check(draw == 0xa0fea6b83f171b02UL, "draw vector: " + draw.ToString("x16"));
        Check(FM.Range(draw, 90) == 56, "range(90) = 56");
        FM.TraceLine one = new FM.TraceLine(); one.Tick = 1834100; one.Id = 7215113988226514UL; one.Ti = 40; one.From = 5; one.To = 2;
        Check(FM.HashLine(0, one) == 0x5b19bf2ce8828ce4UL, "trace hash example: " + FM.HashLine(0, one).ToString("x16"));

        // D8: model version bump changes the seed; old S / D / E lines are discarded, W and K stay
        Check(FM.Seed(0x9c41d2e07b53a611UL, 7215113988226514UL, 2) != seed, "version bump: seed differs");
        Check(!FM.KeepLine("{\"t\":\"S\",\"id\":\"1\"}", 2) && !FM.KeepLine("{\"t\":\"D\",\"id\":\"1\"}", 2) && !FM.KeepLine("{\"t\":\"E\",\"ticks\":\"1\"}", 2), "other fs: S D E dropped");
        Check(FM.KeepLine("{\"t\":\"K\",\"k\":\"t3EdgeM\",\"v\":300,\"src\":\"ME-4\"}", 2) && FM.KeepLine("{\"t\":\"W\",\"w\":\"x\"}", 2) && FM.KeepLine("{\"t\":\"S\",\"id\":\"1\"}", FM.kModelVersion), "K and W kept; same fs keeps S");

        // table: 24 rows, numbered in order
        Check(FM.kTable.Length == 24, "24 transitions");
        for (int i = 0; i < FM.kTable.Length; i++) Check(FM.kTable[i].T == i + 1, "row " + (i + 1) + " in place");
        Check(FM.Allowed(4, FM.GoFarPending, FM.FarSleep) && !FM.Allowed(4, FM.GoFarPending, FM.VisitProposed) && !FM.Allowed(4, FM.Live, FM.FarHome), "T4 only from GoFarPending to 2-7");
        Check(FM.Allowed(15, FM.VisitProposed, FM.PendingLive) && !FM.Allowed(15, FM.Excluded, FM.PendingLive) && !FM.Allowed(15, FM.Live, FM.PendingLive), "T15 only from 2-8");
        Check(FM.DueRow(FM.FarHome, FM.FarSleep) == 5 && FM.DueRow(FM.FarSleep, FM.FarHome) == 6 && FM.DueRow(FM.FarTransit, FM.FarVenue) == 11 && FM.DueRow(FM.VisitProposed, FM.PendingLive) == 16
            && FM.DueRow(FM.VisitProposed, FM.FarHome) == 17 && FM.DueRow(FM.FarSleep, FM.FarTransit) == 22, "due rows");
        Check(FM.kStateName.Length == FM.kStates && FM.kExclName.Length == FM.kExcl && FM.kWhyName.Length == 20 && FM.kEaOfState.Length == FM.kStates, "name tables line up");

        // plan: at most 12 blocks, sorted, first block at midnight, minutes add up, both switch sets
        In pin = new In();
        int maxBlocks = 0;
        bool planOk = true;
        for (int v = 0; v < 2 && planOk; v++)
        {
            FM.Cfg c = Cfg(v == 1);
            for (ulong id = 1; id <= 400 && planOk; id++)
                for (int day = 0; day < 10 && planOk; day++)
                {
                    List<FM.Block> p = FM.BuildPlan(c, pin, FM.Seed(c.WorldSeed, id, 1), id, 5000, 1, 0, day);
                    if (p.Count > maxBlocks) maxBlocks = p.Count;
                    bool ok = p.Count >= 1 && p.Count <= FM.kMaxBlocks && p[0].Start == (long)day * kTpd;
                    for (int i = 1; i < p.Count; i++) ok &= p[i].Start > p[i - 1].Start && p[i].Start < (long)(day + 1) * kTpd;
                    long sum = 0;
                    for (int s = FM.FarHome; s <= FM.PendingLive; s++) sum += FM.TicksIn(c, p, day, s);
                    ok &= sum == kTpd;
                    if (v == 0) for (int i = 0; i < p.Count; i++) ok &= p[i].State == FM.FarHome || p[i].State == FM.FarSleep;
                    if (v == 0) ok &= FM.MinutesIn(c, p, day, FM.FarSleep) >= 480 - 90 && FM.MinutesIn(c, p, day, FM.FarSleep) <= 480 + 180;
                    if (!ok) { planOk = false; Check(false, "plan shape id " + id + " day " + day + " switches " + v + " blocks " + p.Count + " ticks " + sum); }
                }
        }
        Check(maxBlocks > 6 && maxBlocks <= FM.kMaxBlocks, "plans use up to " + maxBlocks + " blocks (<= 12)");
        Check(FM.TravelMin(0, 160) == 20 && FM.TravelMin(100000, 1500) == 87 && FM.TravelMin(100000, 160) == 720, "travel time: min 20, x 1.3 rounded up, capped at 12 h");

        // D2 / D3: same seed twice, shuffled order, DueBudget 1 / 8 / unlimited: one hash
        FM.Machine m1, m2, m3, m4;
        ulong h1 = Run(120, 12, 8, false, out m1), h2 = Run(120, 12, 8, false, out m2), h3 = Run(120, 12, 1, true, out m3), h4 = Run(120, 12, 0, true, out m4);
        Check(h1 == h2, "D2 same seed twice: " + h1.ToString("x16") + " " + h2.ToString("x16"));
        Check(h1 == h3 && h1 == h4, "D3 shuffled order and budget 1 / unlimited: " + h1.ToString("x16") + " " + h3.ToString("x16") + " " + h4.ToString("x16"));
        Check(m1.Trace.Count > 2000 && m1.N[FM.CGoFar] >= 120 && m1.N[FM.CGoLive] >= 100, "the run has transitions: " + m1.Trace.Count + " goFar " + m1.N[FM.CGoFar] + " goLive " + m1.N[FM.CGoLive]);
        Check(m1.N[FM.CIllegal] == 0 && m3.N[FM.CIllegal] == 0, "every transition is a table row");
        Check(m1.N[FM.CFlap] == 0 && m1.N[FM.CHouseholdInFar] == 0 && m1.N[FM.CHhCaughtInFar] == 0, "no flap, no household row");
        m1.Audit(12L * kTpd);
        Check(m1.N[FM.CFViolations] == 0 && !m1.StoodDown, "V4: stored state = F(t) for every record");
        bool visits = false, work = false, venue = false;
        foreach (FM.TraceLine l in m1.Trace) { visits |= l.To == FM.VisitProposed; work |= l.To == FM.FarWork || l.To == FM.FarSchool; venue |= l.To == FM.FarVenue; }
        Check(visits && work && venue && m1.N[FM.CVisitsProposed] > 0 && m1.N[FM.CVisitHandover] > 0, "work, venue and visit states are reached");

        // D4: lazy against stepped, 1000 seeded Sims over 30 sim days
        {
            In inp = new In();
            FM.Machine st = new FM.Machine(Cfg(true), inp), lz = new FM.Machine(Cfg(true), inp);
            st.C.Visit = lz.C.Visit = false;  // a visit hands the Sim to EA: the stepped Sim would leave the far states
            st.C.FarDwellMin = lz.C.FarDwellMin = 1;
            long t0 = kTpd / 2;
            int bad = 0;
            for (int i = 0; i < 1000; i++) { Far(st, 100 + (ulong)i * 31, t0); Far(lz, 100 + (ulong)i * 31, t0); }
            for (long now = t0; now <= 30L * kTpd; now += 750) st.Drain(now, 0);
            long tEnd = 30L * kTpd;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                ulong id = 100 + (ulong)i * 31;
                FM.Rec a = st.Find(id), b = lz.Find(id);
                int s; ulong place; long next;
                lz.F(b, tEnd, out s, out place, out next);
                if (a.State != s || a.Place != place) bad++;
            }
            sw.Stop();
            Check(bad == 0, "D4 lazy F(t) equals the stepped state for 1000 Sims over 30 days (" + bad + " differ)");
            Check(st.N[FM.CCatchUp] == 0 && st.N[FM.CIllegal] == 0 && st.N[FM.CTransitions] > 100000, "stepped run: no catch-up, all rows legal, " + st.N[FM.CTransitions] + " transitions");
            Console.WriteLine("farsim selftest: F(t) with a cold plan " + (sw.Elapsed.TotalMilliseconds * 1000 / 1000).ToString("0.0") + " us per Sim (D4, 1000 Sims)");

            // D5: drain blocked for 1, 3 and 30 sim days, then one drain: state equals the stepped state at t
            foreach (int blocked in new int[] { 1, 3, 30 })
            {
                FM.Machine cu = new FM.Machine(Cfg(true), inp), ref2 = new FM.Machine(Cfg(true), inp);
                cu.C.Visit = ref2.C.Visit = false;
                cu.C.FarDwellMin = ref2.C.FarDwellMin = 1;
                int diff = 0;
                for (int i = 0; i < 200; i++) { Far(cu, 100 + (ulong)i * 31, t0); Far(ref2, 100 + (ulong)i * 31, t0); }
                long t = t0 + (long)blocked * kTpd + 1234;
                for (long now = t0; now <= t; now += 750) ref2.Drain(now, 0);
                ref2.Drain(t, 0);
                cu.Drain(t, 0);
                for (int i = 0; i < 200; i++)
                {
                    FM.Rec a = cu.Find(100 + (ulong)i * 31), b = ref2.Find(100 + (ulong)i * 31);
                    if (a.State != b.State || a.Place != b.Place) diff++;
                }
                Check(diff == 0, "D5 catch-up after " + blocked + " blocked days equals the stepped state (" + diff + " differ)");
                Check(blocked == 1 ? cu.N[FM.CCatchUp] < 200 : cu.N[FM.CCatchUp] == 200, "D5 " + blocked + " days: catch-up count " + cu.N[FM.CCatchUp]);
            }
        }

        // D6 (format part): S line round trip, E and D lines
        {
            FM.Rec r = new FM.Rec(); r.Id = 7215113988226514UL; r.State = FM.FarHome; r.Ti = 41; r.Ep = 3; r.Rev = 2;
            string s = FM.SLine(r);
            Check(s == "{\"t\":\"S\",\"id\":\"7215113988226514\",\"st\":\"FarHome\",\"ti\":41,\"c\":{\"ep\":3,\"rev\":2}}", "S line: " + s);
            ulong id; uint ti, ep, rev;
            Check(FM.ParseS(s, out id, out ti, out ep, out rev) && id == r.Id && ti == 41 && ep == 3 && rev == 2, "S line reads back id, ti, ep, rev");
            Check(!FM.ParseS("{\"t\":\"D\",\"id\":\"1\",\"k\":\"relDrift\",\"v\":3}", out id, out ti, out ep, out rev) && !FM.ParseS(null, out id, out ti, out ep, out rev), "other lines are not S lines");
            one.Why = FM.WDue;
            Check(FM.ELine(one) == "{\"t\":\"E\",\"ticks\":\"1834100\",\"id\":\"7215113988226514\",\"ti\":40,\"from\":\"FarWork\",\"to\":\"FarHome\",\"why\":\"due\"}", "E line: " + FM.ELine(one));
            Check(FM.DLine(5, 9, "relDrift", -3) == "{\"t\":\"D\",\"id\":\"5\",\"to\":\"9\",\"k\":\"relDrift\",\"v\":-3}" && FM.DLine(5, 0, "skillDrift:Logic", 2) == "{\"t\":\"D\",\"id\":\"5\",\"k\":\"skillDrift:Logic\",\"v\":2}", "D lines");
        }

        // hysteresis, timeout, retry, flap, dwell entries (times from 10:00 so the Sim is awake)
        {
            In inp = new In();
            FM.Cfg c = Cfg(false);
            FM.Machine m = new FM.Machine(c, inp);
            Func<int, long> T = delegate(int minutes) { return c.Ticks(600 + minutes); };
            m.Boundary(1, T(0), 0, 30000, false, false, true, 50);   // 300 m: between the radii, not far
            Check(m.Find(1) == null, "between the radii: no record");
            m.Boundary(1, T(5), 0, 46000, false, false, true, 50);
            m.Boundary(1, T(30), 0, 46000, false, false, true, 50);
            Check(m.Find(1).State == FM.Live, "far for 25 min: still a dwell entry");
            m.Boundary(1, T(32), 0, 30000, false, false, true, 50);
            Check(m.Find(1) == null, "back inside R_far: dwell entry dropped");
            m.Boundary(1, T(40), 0, 46000, false, false, false, 50);
            m.Boundary(1, T(70), 0, 46000, false, false, false, 50);
            Check(m.Find(1).State == FM.GoFarPending, "T1 after FarDwellMin");
            m.Boundary(1, T(100), 0, 46000, false, false, false, 50);
            Check(m.Find(1).State == FM.GoFarPending, "no safe point: stays pending");
            m.Drain(T(70 + 120), 8);
            Check(m.Find(1).State == FM.Live && m.N[FM.CNoSafePoint] == 1, "T3 after GoFarTimeoutMin");
            m.Boundary(1, T(200), 0, 46000, false, false, true, 50);
            Check(m.Find(1).State == FM.Live, "inside GoFarRetryMin: no T1");
            m.Boundary(1, T(251), 0, 46000, false, false, true, 50);
            Check(m.Find(1).State == FM.GoFarPending, "T1 again after the retry time");
            m.Boundary(1, T(255), 0, 46000, false, false, true, 50);
            Check(m.Find(1).State == FM.FarHome && m.N[FM.CGoFar] == 1 && m.Find(1).Ep == 1, "T4 at a safe point: FarHome at 14:15, episode 1");
            m.Boundary(1, T(260), 0, 16000, false, false, true, 50);
            Check(m.Find(1).State == FM.FarHome, "160 m: outside R_live, stays far (gap between the radii)");
            m.Boundary(1, T(265), 0, 14000, false, false, true, 50);
            Check(m.Find(1).State == FM.PendingLive, "T15 inside R_live");
            m.GoLiveDone(1, T(265), true);
            Check(m.Find(1).State == FM.Live && m.N[FM.CGoLive] == 1, "T18");
            m.Boundary(1, T(270), 0, 14000, false, false, true, 50);
            Check(m.Find(1) != null, "near, inside the flap window: entry kept");
            m.Boundary(1, T(280), 0, 14000, false, false, true, 50);
            Check(m.Find(1) == null, "near, after the flap window: entry dropped");
            // parked rule: 250 m and in a rabbit hole for ParkedDwellMin
            m.Boundary(2, T(0), 0, 25000, true, false, true, 50);
            m.Boundary(2, T(59), 0, 25000, true, false, true, 50);
            Check(m.Find(2).State == FM.Live, "parked 59 min");
            m.Boundary(2, T(60), 0, 25000, true, false, true, 50);
            Check(m.Find(2).State == FM.GoFarPending, "parked rule: T1 after ParkedDwellMin beyond HandoffRadiusM");
            m.Boundary(2, T(61), 0, 25000, true, true, true, 50);
            Check(m.Find(2).State == FM.Live, "near trigger at any distance: T2");
            // flap is counted when a go-far lands inside the window (dwell shortened to make it possible)
            c.FarDwellMin = 1;
            Far(m, 3, T(0));
            m.Boundary(3, T(3), 0, 1000, false, false, true, 50); m.GoLiveDone(3, T(3), true);
            Far(m, 3, T(4));
            Check(m.N[FM.CFlap] == 1 && m.Find(3).Ep == 2, "flap counted: a go-far inside FlapWindowMin of the last go-live (" + m.N[FM.CFlap] + ")");
            Check(c.NearCm == 15000, "ring 0: near radius = LiveRadiusM");
            c.RingCm = 99999;
            Check(c.NearCm == 40000, "ring clamped: R_live + ring <= R_far - 50 m");
            Check(m.N[FM.CIllegal] == 0, "hysteresis run: all rows legal");
        }

        // household, selected and unknown scope: never far; a scope change on a far Sim; stand-down
        {
            In inp = new In();
            inp.HouseholdId = 11;
            FM.Cfg c = Cfg(true);
            c.Visit = false;
            c.FarDwellMin = 1;
            FM.Machine m = new FM.Machine(c, inp);
            int drops = 0;
            m.OnDrop = delegate(ulong id) { drops++; };
            long noon = kTpd / 2;
            for (int i = 0; i < 400; i++) m.Boundary(11, c.Ticks(i * 5), inp.Excl(11), 90000, true, false, true, 50);
            Check(m.Find(11).State == FM.Excluded && m.ExclN[FM.XHousehold] == 1 && m.N[FM.CGoFar] == 0, "EP1: a household Sim 900 m away stays Excluded");
            foreach (int x in new int[] { FM.XSelected, FM.XUnknownScope, FM.XTie, FM.XPregnant })
            {
                ulong id = 20 + (ulong)x;
                for (int i = 0; i < 20; i++) m.Boundary(id, c.Ticks(i * 5), x, 90000, false, false, true, 50);
                Check(m.Find(id).State == FM.Excluded && m.ExclN[x] == 1, "excluded: " + FM.kExclName[x]);
            }
            // EP2: scope turns household while pending
            m.Boundary(40, noon, 0, 90000, false, false, false, 50); m.Boundary(40, noon + c.Ticks(2), 0, 90000, false, false, false, 50);
            Check(m.Find(40).State == FM.GoFarPending, "pending");
            m.Boundary(40, noon + c.Ticks(3), FM.XHousehold, 90000, false, false, true, 50);
            Check(m.Find(40).State == FM.Excluded && m.N[FM.CHhBlockedAtGoFar] == 1 && m.N[FM.CGoFar] == 0, "EP2: refused at go-far, then Excluded");
            // EP1 on a far Sim (the pass sees the scope change): T15 scope, T18, T20 on one tick, not a violation
            Far(m, 41, noon);
            Check(m.Find(41).State >= FM.FarHome && m.Find(41).State <= FM.FarVenue, "far");
            m.Boundary(41, noon + c.Ticks(4), FM.XSelected, 90000, false, false, true, 50);
            Check(m.Find(41).State == FM.PendingLive, "scope change: T15");
            m.GoLiveDone(41, noon + c.Ticks(4), true);
            Check(m.Find(41).State == FM.Excluded && m.N[FM.CScopePromoted] == 1 && m.N[FM.CHhCaughtInFar] == 0, "then T18 and T20");
            m.Boundary(41, noon + c.Ticks(9), 0, 90000, false, false, true, 50);
            Check(m.Find(41).State == FM.Live, "T21 when the reason is gone");
            m.Audit(noon + c.Ticks(9));
            Check(!m.StoodDown && m.N[FM.CHouseholdInFar] == 0, "audit clean");
            // EP3: the drain finds a household Sim in a far state (an event was missed): counted, promoted, stand-down
            for (ulong id = 50; id < 53; id++) Far(m, id, noon);
            inp.LateExclId = 50; inp.LateExcl = FM.XHousehold;
            m.Drain(3L * kTpd / 2, 0);
            Check(m.Find(50).State == FM.PendingLive && m.N[FM.CHhCaughtInFar] == 1, "EP3: caught in the drain, T15 scope");
            m.GoLiveDone(50, 3L * kTpd / 2, true);
            Check(m.Find(50).State == FM.Excluded, "caught Sim ends Excluded");
            m.Audit(3L * kTpd / 2);
            Check(m.StoodDown && m.N[FM.CStoodDown] == 1 && m.Find(51).State == FM.PendingLive && m.Find(52).State == FM.PendingLive, "stand-down: everything promoted");
            m.GoLiveDone(51, 3L * kTpd / 2, true); m.GoLiveDone(52, 3L * kTpd / 2, true);
            for (int i = 0; i < 30; i++) m.Boundary(51, 2L * kTpd + c.Ticks(i * 5), 0, 90000, false, false, true, 50);
            Check(m.Find(51).State == FM.Live, "stood down: no new go-far");
            // V2: the audit itself counts a scope Sim that sits in a far state
            FM.Machine v2 = new FM.Machine(c, inp);
            inp.LateExclId = 0;
            Far(v2, 60, noon);
            inp.LateExclId = 60; inp.LateExcl = FM.XUnknownScope;
            v2.Audit(noon + c.Ticks(4));
            Check(v2.N[FM.CHouseholdInFar] == 1 && v2.StoodDown && v2.Find(60).State == FM.PendingLive, "V2: unknown scope in a far state is counted and stands the machine down");
            // reset clears the stand-down and every table; drops reach the sink
            m.Reset();
            Check(m.Count == 0 && m.N[FM.CRecords] == 0 && m.N[FM.CDueSize] == 0 && m.N[FM.CPlanCache] == 0 && !m.StoodDown && drops > 0, "reset: tables empty");
            Check(m.N[FM.CIllegal] == 0 && v2.N[FM.CIllegal] == 0, "scope run: all rows legal");
        }

        // kill list, plan change, forced events, gone, end of pass, switch off
        {
            In inp = new In();
            FM.Cfg c = Cfg(false);
            c.FarDwellMin = 1;
            FM.Machine m = new FM.Machine(c, inp);
            c.StatesOff = 1 << FM.FarSleep;
            Far(m, 1, 0);
            Check(m.Find(1).State == FM.Live && m.N[FM.CStateOff] == 1 && m.N[FM.CGoFar] == 0, "StatesOff: no go-far into a killed state (00:02 is FarSleep)");
            long noon = kTpd / 2;
            m.Boundary(1, noon, 0, 90000, false, false, true, 50); m.Boundary(1, noon + c.Ticks(1), 0, 90000, false, false, true, 50);
            Check(m.Find(1).State == FM.FarHome, "go-far at noon");
            m.Drain(kTpd, 0);
            Check(m.Find(1).State == FM.PendingLive && m.Find(1).Why == FM.WStandDown, "StatesOff: promoted at the bed-time edge");
            m.GoLiveDone(1, kTpd, true);
            c.StatesOff = 0;
            Far(m, 2, noon);
            uint ti = m.Find(2).Ti;
            m.PlanChanged(2, noon + c.Ticks(5));
            Check(m.Find(2).Rev == 1 && m.Find(2).State == FM.FarHome && m.Find(2).Ti == ti, "T23: revision bumped, same state, nothing logged");
            m.Audit(noon + c.Ticks(6));
            Check(m.N[FM.CFViolations] == 0, "V4 holds after a plan change");
            m.Forced(2, noon + c.Ticks(7), FM.WEaPush);
            Check(m.Find(2).State == FM.PendingLive && m.Find(2).Why == FM.WEaPush, "forced event: T15 eaPush");
            m.GoLiveDone(2, noon + c.Ticks(7), false);
            Check(m.N[FM.CGoLiveFallback] == 1 && m.Find(2).Why == FM.WFallback, "T19 fallback counted");
            m.GoLiveDone(2, noon + c.Ticks(8), true);
            Check(m.N[FM.CIllegal] == 1, "a second go-live of a live Sim is refused and counted");
            Far(m, 3, noon);
            List<ulong> seen = new List<ulong>(); seen.Add(1); seen.Add(2);
            m.EndPass(seen);
            m.Sizes();
            Check(m.Find(3) == null && m.N[FM.CGone] == 1 && m.N[FM.CDueSize] == 0, "T24: a record the pass did not see is dropped with its due entry");
            for (ulong id = 10; id < 210; id++) Far(m, id, noon);
            m.Sizes();
            Check(m.N[FM.CRecords] == 200 && m.ByState[FM.FarHome] + m.ByState[FM.FarSleep] == 200 && m.N[FM.CPlanCache] >= 200, "200 far Sims");
            // a switch change builds the plans again: with farsimvisit on then off a proposed visit goes back to FarHome (T17)
            c.Visit = true;
            m.Reconfigure(noon + c.Ticks(3));
            m.Sizes();
            long proposed = m.ByState[FM.VisitProposed];
            c.Visit = false;
            m.Reconfigure(noon + c.Ticks(4));
            m.Sizes();
            Check(proposed > 0 && m.ByState[FM.VisitProposed] == 0 && m.ByState[FM.FarHome] + m.ByState[FM.FarSleep] + m.ByState[FM.PendingLive] == 200, "T14 with farsimvisit on (" + proposed + "), T17 when it goes off");
            m.PromoteAll(noon + c.Ticks(5), FM.WSwitchOff);
            int live = 0;
            for (ulong id = 10; id < 210; id++) { m.GoLiveDone(id, noon + c.Ticks(5), true); if (m.Find(id).State == FM.Live) live++; }
            m.Reset();
            Check(live == 200 && m.N[FM.CIllegal] == 1 && m.Count == 0, "switch off: all live in one tick, then reset");
        }

        // leak churn: 10,000 records created and destroyed, 100 load / quit cycles
        {
            In inp = new In();
            FM.Cfg c = Cfg(true);
            c.Visit = false;
            c.FarDwellMin = 1;
            FM.Machine m = new FM.Machine(c, inp);
            long snaps = 0;
            m.OnDrop = delegate(ulong id) { snaps--; };
            long noon = kTpd / 2;
            for (int round = 0; round < 10; round++)
            {
                for (ulong id = 1; id <= 1000; id++) { Far(m, id, noon); snaps++; }
                m.Sizes();
                Check(m.N[FM.CRecords] == 1000 && m.N[FM.CDueSize] == 1000, "churn round " + round + ": 1000 records, 1000 due entries");
                m.EndPass(new List<ulong>());
                m.Sizes();
                Check(m.Count == 0 && m.N[FM.CDueSize] == 0 && m.N[FM.CPlanCache] == 0 && snaps == 0, "churn round " + round + ": all sizes back to 0 (snapshots " + snaps + ")");
            }
            Check(m.N[FM.CGone] == 10000 && m.N[FM.CRecordsHigh] == 1000, "10,000 records destroyed, high-water 1000");
            for (int cycle = 0; cycle < 100; cycle++)
            {
                for (ulong id = 1; id <= 50; id++) { Far(m, id, noon); snaps++; }
                m.Reset();
                if (m.Count != 0 || m.N[FM.CRecords] != 0 || m.N[FM.CDueSize] != 0 || snaps != 0) { Check(false, "load / quit cycle " + cycle + " leaves " + m.Count + " records, " + snaps + " snapshots"); break; }
            }
        }

        // FS-3: drift bounds and determinism
        {
            int lo = 9, hi = -9;
            bool sym = true;
            for (ulong a = 1; a < 60; a++)
                for (uint day = 0; day < 20; day++)
                {
                    int d = FM.RelDriftDay(77, a, a + 13, 1, day, 3, 40), e = FM.RelDriftDay(77, a + 13, a, 1, day, 3, 40);
                    if (d != e) sym = false;
                    if (d < lo) lo = d;
                    if (d > hi) hi = d;
                }
            Check(sym && lo == -3 && hi == 3, "relationship drift: same for both sides, inside +-3 per day (" + lo + ".." + hi + ")");
            Check(FM.RelDriftDay(77, 1, 2, 1, 5, 0, 10) == 0, "no shared block: no drift");
            int one1 = FM.RelDriftDay(77, 1, 2, 1, 5, 1, 10);
            Check(one1 >= -1 && one1 <= 2 && FM.RelDriftDay(77, 1, 2, 1, 5, 1, -10) == -one1, "one block: -1..+2, mirrored for a pair that dislikes each other");
            Check(FM.CapStretch(14, 3, 15) == 1 && FM.CapStretch(-15, -2, 15) == 0 && FM.CapStretch(0, -3, 15) == -3 && FM.CapStretch(15, -3, 15) == -3, "per-stretch cap +-15");
            int smin = 999, smax = 0;
            for (ulong k = 0; k < 500; k++) { int s = FM.SkillSharePermille(seed, 1, (uint)k, 12); if (s < smin) smin = s; if (s > smax) smax = s; }
            Check(smin >= 250 && smax <= 500 && smax - smin > 200, "skill share 250..500 per mille (" + smin + ".." + smax + ")");
            int pmin = 9999, pmax = -9999;
            for (ulong k = 0; k < 500; k++) { int p = FM.PhaseMilliHours(seed + k, 1, 0x100); if (p < pmin) pmin = p; if (p > pmax) pmax = p; }
            Check(pmin >= -1000 && pmax <= 1000 && pmax - pmin > 1800 && FM.PhaseMilliHours(seed, 1, 7) != FM.PhaseMilliHours(seed, 2, 7), "phase draw: +-1 h, per episode (" + pmin + ".." + pmax + ")");
            // shared blocks: two coworkers on a work day, none on the weekend
            In inp = new In();
            FM.Cfg c = Cfg(true);
            c.Venue = c.Visit = false;
            List<FM.Block> pa = FM.BuildPlan(c, inp, FM.Seed(c.WorldSeed, 5, 1), 5, 50, 1, 0, 1), pb = FM.BuildPlan(c, inp, FM.Seed(c.WorldSeed, 20, 1), 20, 51, 1, 0, 1),
                wa = FM.BuildPlan(c, inp, FM.Seed(c.WorldSeed, 5, 1), 5, 50, 1, 0, 5), wb = FM.BuildPlan(c, inp, FM.Seed(c.WorldSeed, 20, 1), 20, 51, 1, 0, 5);
            Check(FM.SharedBlocks(c, pa, pb, 1) == 1 && FM.SharedBlocks(c, wa, wb, 5) == 0, "shared work block on a work day only");
        }

        // shadow tables: the exact lines the queue regexes read
        {
            FM.ErrTable t = new FM.ErrTable("need:Hunger", 5, 10, 20, 40);
            t.Add(10, 12); t.Add(-30, 20); t.Add(0, 0); t.Add(50, 39);
            Check(t.Line("end") == "farsim: shadow err end need:Hunger n 4 meanabs 15 bias -10 max 50 buckets le5:2 le10:0 le20:1 le40:0 over:1", "err line: " + t.Line("end"));
            t.Clear();
            Check(t.Line("x") == "farsim: shadow err x need:Hunger n 0 meanabs 0 bias 0 max 0 buckets le5:0 le10:0 le20:0 le40:0 over:0", "empty err line");
            FM.Confusion cf = new FM.Confusion();
            cf.Add(FM.FarHome, FM.EaHome); cf.Add(FM.FarHome, FM.EaVenue); cf.Add(FM.FarSleep, FM.EaSleep); cf.Add(FM.VisitProposed, FM.EaHome); cf.Add(FM.Live, FM.EaHome); cf.Add(FM.FarHome, 99);
            List<string> ls = cf.Lines("end");
            Check(ls.Count == 8, "7 state rows and the total");
            Check(ls[0] == "farsim: shadow state end FarHome ea home:1 sleep:0 transit:0 work:0 school:0 venue:1 visit:0 other:0", "state row: " + ls[0]);
            Check(ls[7] == "farsim: shadow state end total n 4 agree 3 pct 75", "state total: " + ls[7]);
        }

        Console.WriteLine(sFail == 0 ? "farsim selftest: PASS" : "farsim selftest: " + sFail + " FAIL");
        return sFail == 0 ? 0 : 1;
    }
}
