// docs/631 §5: host test of playerfirst-core.h (the route worker's pick with [RouteAsync] PlayerFirst) without Windows,
// Wine or a libc. Build and run on Linux (32-bit):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/pf-host native/ts3native/playerfirst-host-test.cpp && /tmp/pf-host; echo $?
//   0 = pass; else 1 and "assert line N" on stderr.
// The worker is modelled as ServeAsync uses the core: one PfPick per job, the picked job leaves the queue, a job's end is
// noted, a START asks PfRepeat. Not covered here: the slots' states and the claim (TestAsync in selftest.cpp, MSVC).
#include "playerfirst-core.h"

static long Sys(long nr, long a, long b, long c) {
    long r;
    __asm__ volatile("int $0x80" : "=a"(r) : "a"(nr), "b"(a), "c"(b), "d"(c) : "memory");
    return r;
}
[[noreturn]] static void Exit(long code) {
    Sys(1, code, 0, 0);
    __builtin_unreachable();
}
[[noreturn]] static void Fail(int line) {
    char b[32] = "assert line ";
    int at = 12;
    char d[12];
    int n = 0;
    do { d[n++] = (char)('0' + line % 10); line /= 10; } while (line);
    while (n) b[at++] = d[--n];
    b[at++] = '\n';
    Sys(4, 2, (long)b, at);
    Exit(1);
}
#define assert(x) do { if (!(x)) Fail(__LINE__); } while (0)

struct Atomic {
    static long Cas(volatile long* p, long expect, long set) {
        const long old = *p;
        if (old == expect) *p = set;
        return old;
    }
};

struct Job { bool queued; long pf, passed; unsigned long seq, handle; int kind; };
struct Queue {
    Job j[32];
    unsigned long seq;
    PfRetryTab retry;
    long mode;
    int Count() { return 32; }
    bool Queued(int i) { return j[i].queued; }
    long Pf(int i) { return j[i].pf; }
    long Passed(int i) { return j[i].passed; }
    unsigned long Seq(int i) { return j[i].seq; }
    void Pass(int i) { j[i].passed++; }
    void Reset(long m, unsigned long firstSeq) {
        for (int i = 0; i < 32; i++) j[i] = Job{};
        for (int i = 0; i < kPfRetrySlots; i++) retry.h[i] = 0;
        seq = firstSeq, mode = m;
    }
    // AsyncStart + JobQueue: the mark of a repeat replan is not honoured
    int Start(unsigned long handle, int kind, bool marked) {
        for (int i = 0; i < 32; i++) {
            if (j[i].queued) continue;
            j[i] = Job{true, marked && !PfRepeat<Atomic>(retry, mode, kind, handle) ? 1 : 0, 0, ++seq, handle, kind};
            return i;
        }
        assert(false);
    }
    // ServeAsync: pick, run, note the end of a marked route's job; returns the handle run (0 = queue empty)
    unsigned long Run(bool failed, bool marked, PfPicked* out = 0) {
        const PfPicked p = PfPick(*this, mode);
        if (out) *out = p;
        if (p.at < 0) return 0;
        j[p.at].queued = false;
        if (marked) PfRetryNote<Atomic>(retry, j[p.at].handle, failed);
        return j[p.at].handle;
    }
};
static Queue q;

// A blocked household Sim replans its route every tick (EA's stand-and-wait cycle: each failed Replan is followed by the
// next one), with one townie job queued before the first of them. Returns the number of household jobs the worker ran
// before the townie job, at most `ticks`.
static int WaitCycle(long mode, int ticks) {
    q.Reset(mode, 0);
    const unsigned long kTownie = 0x25000001, kSim = 0x25000002;
    q.Start(kTownie, 0, false);
    q.Start(kSim, 0, true);  // the Sim's plan: fails (the goal is taken)
    for (int t = 0; t < ticks; t++) {
        const unsigned long ran = q.Run(true, true);
        if (ran == kTownie) return t;
        assert(ran == kSim);
        q.Start(kSim, 1, true);  // the next Replan is queued before the worker picks again
    }
    return ticks;
}

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {
    PfPicked p;
    // the order of two jobs: off = oldest first; on = a job served first before an older one that is not; seq wraps
    assert(PfBefore(0, 0, 1, 1, 2) && !PfBefore(0, 1, 2, 0, 1) && PfBefore(1, 1, 2, 0, 1) && !PfBefore(1, 0, 1, 1, 2));
    assert(PfBefore(2, 1, 2, 0, 1) && PfBefore(1, 1, 1, 1, 2) && PfBefore(1, 0, 1, 0, 2) && PfBefore(1, 1, 0xFFFFFFFFul, 1, 0));
    assert(PfFirst(0, 1, 99) == 0 && PfFirst(2, 1, 0) == 1 && PfFirst(2, 0, 99) == 0 && PfFirst(1, 1, 0) == 1);
    assert(PfFirst(1, 0, kPfMaxPassed - 1) == 0 && PfFirst(1, 0, kPfMaxPassed) == 1);

    // 1. the benefit, modes 1 and 2 alike: a click's plan runs before the townie jobs queued ahead of it, the household's
    //    own jobs keep their order, the townie jobs keep theirs; mode 0 is START order
    for (long mode = 0; mode <= 2; mode++) {
        q.Reset(mode, 0xFFFFFFFDul);  // the START number wraps inside the case
        q.Start(0xA1, 0, false), q.Start(0xA2, 0, false), q.Start(0xB1, 0, true), q.Start(0xA3, 2, false), q.Start(0xB2, 1, true);
        unsigned long got[5];
        for (int i = 0; i < 5; i++) {
            got[i] = q.Run(false, false, &p);
            if (i == 0) assert(mode ? p.passed == 2 && p.oldest == 0xFFFFFFFEul && !p.aged : p.passed == 0);
            if (i == 1) assert(mode ? p.passed == 3 : p.passed == 0);
        }
        const unsigned long on[5] = {0xB1, 0xB2, 0xA1, 0xA2, 0xA3}, off[5] = {0xA1, 0xA2, 0xB1, 0xA3, 0xB2};
        for (int i = 0; i < 5; i++) assert(got[i] == (mode ? on[i] : off[i]));
        assert(q.Run(false, false, &p) == 0 && p.at == -1);
    }

    // 2. the fault of the order without limits (mode 2 = the order before docs/631 §5): the townie job queued before a
    //    blocked Sim's wait cycle never runs while the cycle lasts (EA's wait is up to 50 sim-minutes of replans)
    assert(WaitCycle(2, 1000) == 1000);
    //    mode 1: the failed plan's replans are repeats (START order), so the townie job is next after that plan
    assert(WaitCycle(1, 1000) == 1);
    //    mode 0: the townie job is the oldest
    assert(WaitCycle(0, 1000) == 0);

    // 3. the bound alone (no failures: eight household Sims whose plans succeed and are followed by new ones): mode 2
    //    starves the townie job, mode 1 runs it after kPfMaxPassed marked jobs, ahead of the marked job waiting then
    for (long mode = 1; mode <= 2; mode++) {
        q.Reset(mode, 0);
        q.Start(0xA1, 0, false);
        int ranAt = -1;
        for (int t = 0; t < 100 && ranAt < 0; t++) {
            q.Start(0xB0 + (t & 7), t & 1, true);
            if (q.Run(false, true, &p) == 0xA1) { ranAt = t; assert(p.aged == 1 && p.passed == 0); }
            else assert(p.passed == 1 && p.oldest == 1);
        }
        assert(mode == 1 ? ranAt == kPfMaxPassed : ranAt == -1);
    }
    //    two townie jobs: both age together and leave in their START order, then the marked jobs go on
    q.Reset(1, 0);
    q.Start(0xA1, 0, false), q.Start(0xA2, 0, false);
    for (int t = 0; t < kPfMaxPassed; t++) { q.Start(0xB1, 0, true); assert(q.Run(false, true) == 0xB1); }
    q.Start(0xB1, 0, true), q.Start(0xA3, 0, false);
    assert(q.Run(false, true) == 0xA1 && q.Run(false, true) == 0xA2 && q.Run(false, true) == 0xB1 && q.Run(false, true) == 0xA3);

    // 4. the repeat rule: only replans (kinds 1, 3), only after a failed job of that route, only in mode 1; a success
    //    forgets the route; a new plan of the route (kind 0 / 2) is served first whatever came before
    PfRetryTab t = {};
    assert(!PfRepeat<Atomic>(t, 1, 1, 0x77));
    PfRetryNote<Atomic>(t, 0x77, true);
    PfRetryNote<Atomic>(t, 0x77, true);  // noted once
    assert(PfRepeat<Atomic>(t, 1, 1, 0x77) && PfRepeat<Atomic>(t, 1, 3, 0x77) && !PfRepeat<Atomic>(t, 1, 0, 0x77) && !PfRepeat<Atomic>(t, 1, 2, 0x77));
    assert(!PfRepeat<Atomic>(t, 2, 1, 0x77) && !PfRepeat<Atomic>(t, 0, 1, 0x77) && !PfRepeat<Atomic>(t, 1, 1, 0x78) && !PfRepeat<Atomic>(t, 1, 1, 0));
    PfRetryNote<Atomic>(t, 0x78, false);  // a success of a route not in the table adds nothing
    PfRetryNote<Atomic>(t, 0x77, false);
    assert(!PfRepeat<Atomic>(t, 1, 1, 0x77));
    int used = 0;
    for (int i = 0; i < kPfRetrySlots; i++) used += t.h[i] != 0;
    assert(used == 0);
    //    a full table forgets, it never refuses: 1000 failed routes, the latest one is always known
    for (unsigned long h = 0x25000000; h < 0x25000000 + 1000; h++) {
        PfRetryNote<Atomic>(t, h, true);
        assert(PfRepeat<Atomic>(t, 1, 1, h));
    }
    //    in the queue: a failed plan, then its replan is in START order behind the townie job; after the replan succeeds
    //    the route's next replan is served first again
    q.Reset(1, 0);
    q.Start(0xB1, 0, true);
    assert(q.Run(true, true) == 0xB1);
    q.Start(0xA1, 0, false), q.Start(0xB1, 1, true);
    assert(q.Run(false, false) == 0xA1 && q.Run(false, true) == 0xB1);
    q.Start(0xA1, 0, false), q.Start(0xB1, 1, true);
    assert(q.Run(false, true, &p) == 0xB1 && p.passed == 1);
    Exit(0);
}
