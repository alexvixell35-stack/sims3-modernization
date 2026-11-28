// docs/631 [RouteAsync] PlayerFirst: which queued route job the worker runs next. No Windows or game type here, so
// playerfirst-host-test.cpp runs it as it is.
//
// Mode 0: oldest START first (EA's order). Mode 2: a marked job (a route the player ordered, ts3fixes playerfirst) before
// every older unmarked one, with no bound; the order shipped before docs/631 §5, kept for comparison runs. Mode 1 adds
// two rules (docs/631 §5):
//   - bound: an unmarked job that marked jobs have passed kPfMaxPassed times runs before the next marked one. Mode 2 has
//     no bound: a route queues a new job as soon as its last one is collected, so a marked route that replans every tick
//     (EA's stand-and-wait cycle) keeps an unmarked job waiting for as long as it does;
//   - repeats: a Replan / ReplanFromPosition of a route whose last job failed is not served first (PfRetry*): it is the
//     wait cycle of a blocked Sim, not the plan of a click. It keeps its place in START order, as with the switch off.
// Every job is the same call in every mode; only the order differs.
#pragma once

static const long kPfMaxPassed = 8;  // docs/631 §2 promised "at most 8 more jobs"; mode 1 holds it

// does queued job a run before queued job b? first = the job is served first (PfFirst), seq = its START number (wraps)
static bool PfBefore(long mode, long aFirst, unsigned long aSeq, long bFirst, unsigned long bSeq) {
    if (mode != 0 && (aFirst != 0) != (bFirst != 0)) return aFirst != 0;
    return (long)(aSeq - bSeq) < 0;
}
// is a queued job served first? pf = marked and not a repeat, passed = marked jobs that ran ahead of it so far
static long PfFirst(long mode, long pf, long passed) {
    if (mode == 1) return pf || passed >= kPfMaxPassed ? 1 : 0;
    return mode == 2 && pf ? 1 : 0;
}

struct PfPicked {
    int at;        // index of the job to run, -1 = none queued
    int passed;    // older unmarked queued jobs it ran ahead of (0 = START order kept)
    int aged;      // 1 = an unmarked job picked by the bound, ahead of a marked one
    unsigned long oldest;  // START number of the oldest job passed (passed > 0)
};

// Q: int Count(); bool Queued(int i); long Pf(int i); long Passed(int i); unsigned long Seq(int i); void Pass(int i)
// (one more marked job ran ahead of job i). The worker calls it once per job it is about to claim.
template <class Q> static PfPicked PfPick(Q& q, long mode) {
    PfPicked r = {-1, 0, 0, 0};
    const int n = q.Count();
    bool markedWaits = false;
    for (int i = 0; i < n; i++) {
        if (!q.Queued(i)) continue;
        if (q.Pf(i)) markedWaits = true;
        if (r.at < 0 || PfBefore(mode, PfFirst(mode, q.Pf(i), q.Passed(i)), q.Seq(i), PfFirst(mode, q.Pf(r.at), q.Passed(r.at)), q.Seq(r.at))) r.at = i;
    }
    if (r.at < 0 || mode == 0) return r;
    if (!q.Pf(r.at)) {  // unmarked: START order, or the bound put it ahead of a waiting marked job
        r.aged = mode == 1 && markedWaits && q.Passed(r.at) >= kPfMaxPassed ? 1 : 0;
        return r;
    }
    const unsigned long seq = q.Seq(r.at);
    for (int i = 0; i < n; i++) {
        if (i == r.at || !q.Queued(i) || q.Pf(i) || (long)(q.Seq(i) - seq) >= 0) continue;
        if (!r.passed || (long)(q.Seq(i) - r.oldest) < 0) r.oldest = q.Seq(i);
        r.passed++;
        q.Pass(i);
    }
    return r;
}

// Routes whose last marked job failed, by route handle. The worker notes a job's end, the sim thread asks at the next
// START of that handle; A::Cas(volatile long* p, long expect, long set) returns the value found (interlocked in the dll).
// A handle that hashes onto a full row replaces the row's first slot: the dropped route's next replan is served first
// once more, which is mode 2's order for that one job.
static const int kPfRetrySlots = 32, kPfRetryRow = 4;
struct PfRetryTab { volatile long h[kPfRetrySlots]; };
static int PfRetryRowOf(unsigned long handle) { return (int)((handle * 2654435761ul >> 16) % (kPfRetrySlots / kPfRetryRow)) * kPfRetryRow; }
template <class A> static bool PfRetryHas(PfRetryTab& t, unsigned long handle) {
    (void)sizeof(A);
    if (!handle) return false;
    const int row = PfRetryRowOf(handle);
    for (int i = 0; i < kPfRetryRow; i++)
        if (t.h[row + i] == (long)handle) return true;
    return false;
}
// a marked job of this route ended: failed = remembered, succeeded = forgotten
template <class A> static void PfRetryNote(PfRetryTab& t, unsigned long handle, bool failed) {
    if (!handle) return;
    const int row = PfRetryRowOf(handle);
    for (int i = 0; i < kPfRetryRow; i++) {
        if (t.h[row + i] != (long)handle) continue;
        if (!failed) A::Cas(&t.h[row + i], (long)handle, 0);
        return;
    }
    if (!failed) return;
    for (int i = 0; i < kPfRetryRow; i++)
        if (A::Cas(&t.h[row + i], 0, (long)handle) == 0) return;
    t.h[row] = (long)handle;
}
// is this START's mark honoured? kind 1 = Replan, 3 = ReplanFromPosition (a10.cpp); a Plan / PlanFromPoint always is
template <class A> static bool PfRepeat(PfRetryTab& t, long mode, int kind, unsigned long handle) {
    return mode == 1 && (kind == 1 || kind == 3) && PfRetryHas<A>(t, handle);
}
