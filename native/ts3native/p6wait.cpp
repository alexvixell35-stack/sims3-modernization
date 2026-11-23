// [P6Waits] (0.36.57, docs/189 phase 0, counters v123): the sim thread's job waits split by arm, by the outcome of the job
// they were behind (P6O_*: plain live, P6 taken, taken on a borrowed cut, stale-refused, other refusal or verify, no running
// job) and by what they waited on (P6W_*). Arm = the P6 switch (p6Plan, --ab routesnapplan) at the job's start, so an in-load
// A/B splits by job, not by the moment the switch flipped. Observe only, no hook. Call sites: LockAcquireAsync's +0x918 pump
// (plain, gate via t_p5bSite, host = the sim served the job's host calls in the wait), RtWaitJob, RunPlan's wait and the
// collect (WaitJobDone SW_COLLECT). A wait often ends before its job does (P5b / P5c lend the lock mid-job), so the outcome is
// not known yet: the wait goes to a small pending ring and is booked when the worker publishes the job's outcome and arm
// (P6wJobEnd, a 16-slot ring by seq), on the sim thread's next wait.
// Cost: a 32-slot scan and a few adds per sim job wait, two stores per job: default on.
// shortcut: waits still pending when the stats block prints are not in it yet (booked at the next wait); a job whose outcome
// slot was reused (> 16 jobs later) counts in p6wLost. Upgrade if p6wLost is ever more than a few per mille of the waits.
// Included by ts3native.cpp after movedwait.cpp.
// Arm (v131, 0.36.67): the in-load loop's arm when it sets p6warm (P6wArmNow in ts3native.cpp), else the P6 switch.

static struct { volatile LONG seq; volatile LONG out; } g_p6wDone[16];  // worker writes (out | arm << 4), sim reads
static struct { DWORD seq; LONG us; u8 cls, site, used; } g_p6wPend[32];  // sim thread only
static int g_p6wPendNext;

static void P6wBook(int arm, int out, int cls, int site, LONG us) {  // sim thread only (single writer; readers tolerate tearing)
    Ts3nCounters& k = ts3native_counters;
    const int i = arm * P6O_N * P6W_N + out * P6W_N + cls;
    k.p6wN[i]++;
    k.p6wUs[i] += us;
    if (cls == P6W_GATE && site >= 1 && site <= 16) k.p6wGateUs[arm * 16 + site - 1] += us;
}

// published (out | arm << 4) of job seq: >= 0 published, -1 still running, -2 its slot was reused (lost)
static int P6wOutOf(DWORD seq) {
    const LONG s1 = g_p6wDone[seq & 15].seq;
    const LONG o = g_p6wDone[seq & 15].out;
    const LONG s2 = g_p6wDone[seq & 15].seq;
    if (s1 == (LONG)seq && s2 == s1) return (int)o;
    return s1 && s1 == s2 && (LONG)(s1 - (LONG)seq) > 0 ? -2 : -1;
}

static void P6wFlush() {
    for (int i = 0; i < 32; i++) {
        if (!g_p6wPend[i].used) continue;
        const int o = P6wOutOf(g_p6wPend[i].seq);
        if (o == -1) continue;
        if (o >= 0) P6wBook(o >> 4, o & 15, g_p6wPend[i].cls, g_p6wPend[i].site, g_p6wPend[i].us);
        else InterlockedIncrement(&ts3native_counters.p6wLost);
        g_p6wPend[i].used = 0;
    }
}

static void P6wWait(DWORD tok, int cls, int site, LONG us) {
    if (!ts3native_counters.p6wReady || GetCurrentThreadId() != g_async.owner) return;
    P6wFlush();
    if (!tok) { P6wBook(ts3native_counters.p6Plan != 0, P6O_NONE, cls, site, us); return; }
    const DWORD seq = tok & 0x7FFFFFFF;
    const int o = P6wOutOf(seq);
    if (o >= 0) { P6wBook(o >> 4, o & 15, cls, site, us); return; }
    if (o == -2) { InterlockedIncrement(&ts3native_counters.p6wLost); return; }
    int i = 0;
    while (i < 32 && g_p6wPend[i].used) i++;
    if (i == 32) {  // full: the oldest slot in turn goes
        i = g_p6wPendNext, g_p6wPendNext = (g_p6wPendNext + 1) & 31;
        InterlockedIncrement(&ts3native_counters.p6wLost);
    }
    g_p6wPend[i].seq = seq, g_p6wPend[i].us = us, g_p6wPend[i].cls = (u8)cls, g_p6wPend[i].site = (u8)site, g_p6wPend[i].used = 1;
}

// worker, at the job's +0x918 release (before A_IDLE): publish the outcome P5Plan left in t_p6wOut and the arm taken at the
// job's start (t_p6wArm, ServeAsync), count the span
static void P6wJobEnd(DWORD seq, LONG spanUs) {
    Ts3nCounters& k = ts3native_counters;
    if (!k.p6wReady) return;
    const int o = t_p6wOut >= 0 && t_p6wOut < P6O_NONE ? t_p6wOut : P6O_PLAIN, arm = t_p6wArm ? 1 : 0;
    InterlockedIncrement(&k.p6wJobN[arm * P6O_NONE + o]);
    InterlockedExchangeAdd(&k.p6wSpanUs[arm * P6O_NONE + o], spanUs);
    InterlockedExchange(&g_p6wDone[seq & 15].seq, 0);  // readers see a torn slot as "running", never a wrong outcome
    InterlockedExchange(&g_p6wDone[seq & 15].out, o | arm << 4);
    InterlockedExchange(&g_p6wDone[seq & 15].seq, (LONG)seq);
}

static const char* const kP6wOut[P6O_N] = {"plain", "taken", "borrowed", "stale", "refused", "nojob"};

// stats block: one line per (arm, outcome) with jobs or waits, then the gate sites per arm
static void P6wLogCounters() {
    const Ts3nCounters& c = ts3native_counters;
    for (int a = 0; a < 2; a++) {
        for (int o = 0; o < P6O_N; o++) {
            const LONG* n = &c.p6wN[(a * P6O_N + o) * P6W_N];
            const LONG* u = &c.p6wUs[(a * P6O_N + o) * P6W_N];
            const LONG jobs = o < P6O_NONE ? c.p6wJobN[a * P6O_NONE + o] : 0;
            const LONG span = o < P6O_NONE ? c.p6wSpanUs[a * P6O_NONE + o] / 1000 : 0;
            if (!jobs && !(n[0] | n[1] | n[2] | n[3] | n[4])) continue;
            Log("p6 waits arm %d %s: jobs %ld span %ld ms; lock %ld/%ld gate %ld/%ld rtwait %ld/%ld result %ld/%ld host %ld/%ld (n/ms)",
                a, kP6wOut[o], jobs, span, n[0], u[0] / 1000, n[1], u[1] / 1000, n[2], u[2] / 1000, n[3], u[3] / 1000, n[4], u[4] / 1000);
        }
        char b[400];
        int len = 0;
        for (int s = 0; s < 16 && len < 360; s++)
            if (c.p6wGateUs[a * 16 + s]) len += SFmt(b + len, sizeof(b) - len, " %d %ld", s, c.p6wGateUs[a * 16 + s] / 1000);
        b[len] = 0;
        if (len) Log("p6 waits arm %d gates (site ms):%s", a, b);
    }
    Log("p6 waits: host calls served in +0x918 waits %ld (%ld ms); lost %ld", c.p6wServeN, c.p6wServeUs / 1000, c.p6wLost);
}

static void InstallP6Waits(const wchar_t* ini) {
    // 0.36.63: default 0 (every switch off by default; the P6 queues set Enabled=1)
    if (!GetPrivateProfileIntW(L"P6Waits", L"Enabled", 0, ini)) { Log("p6 waits off (Enabled=0)"); return; }
    ts3native_counters.p6wReady = 1;
    Log("OK   p6 waits: sim job waits by arm, job outcome and waiter class (docs/189 phase 0)");
}
