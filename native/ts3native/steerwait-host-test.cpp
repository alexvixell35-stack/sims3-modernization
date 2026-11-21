// docs/670 §25: host test of steerwait-core.h (the [SteerRO] RetryWait loop) without Windows, Wine or a libc.
// Build and run on Linux (32-bit):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/sw-host native/ts3native/steerwait-host-test.cpp && /tmp/sw-host; echo $?
//   0 = pass; else 1 and "assert line N" on stderr.
// Not covered here: LocoHook's use of the loop (the job-start frame, the served managed call, the counters and the line);
// TestSteerWait in steerwait-test.h runs those in the MSVC selftest.
#include "steerwait-core.h"

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

enum { W_OK, W_STATE, W_TRY, W_TRIP = 8 };  // steerro.cpp's SR_OK, SR_FB_STATE, SR_FB_TRY, SR_FB_TRIP

// a scripted job: it runs for jobUs; the clone answers script[i] on its i-th rerun (the last entry repeats); a pump step is 1 ms
struct FakeEnv {
    long now, jobUs, pumps, runs;
    const int* script;
    int n;
    bool JobRuns() const { return now < jobUs; }
    long Us() const { return now; }
    void Pump() { now += 1000; pumps++; }
    int Run() { const int r = script[runs < n ? runs : n - 1]; runs++; now += 50; return r; }
    bool Curable(int why) const { return why == W_TRY || why == W_TRIP; }
};
static FakeEnv Env(long jobUs, const int* script, int n) {
    FakeEnv e = {0, jobUs, 0, 0, script, n};
    return e;
}

static void TestSteerWait() {
    {  // the writer lets go after three steps: answered long before the job's end
        static const int s[] = {W_TRY, W_TRY, W_OK};
        FakeEnv e = Env(200000, s, 3);
        const SwOutcome o = SwLoop(e, W_TRY, 50000);
        assert(o.end == SWL_ANSWERED && o.why == W_OK && o.reruns == 3 && e.pumps == 3 && o.us == 3150 && o.us < e.jobUs);
    }
    {  // a tripwire that turns into QL busy and back is still curable
        static const int s[] = {W_TRIP, W_TRY, W_TRIP, W_OK};
        FakeEnv e = Env(200000, s, 4);
        const SwOutcome o = SwLoop(e, W_TRIP, 50000);
        assert(o.end == SWL_ANSWERED && o.reruns == 4);
    }
    {  // the job ends inside a step: no clone run after it, the game's call follows
        static const int s[] = {W_TRY};
        FakeEnv e = Env(2500, s, 1);
        const SwOutcome o = SwLoop(e, W_TRY, 50000);
        assert(o.end == SWL_JOBEND && o.why == W_TRY && o.reruns == 2 && e.pumps == 3 && e.runs == 2);
    }
    {  // no job at the entry: nothing is run and nothing is waited
        static const int s[] = {W_OK};
        FakeEnv e = Env(0, s, 1);
        const SwOutcome o = SwLoop(e, W_TRY, 50000);
        assert(o.end == SWL_JOBEND && o.reruns == 0 && e.pumps == 0 && o.us == 0);
    }
    {  // a reason waiting does not cure ends the loop at once (also at the entry)
        static const int s[] = {W_TRY, W_STATE, W_OK};
        FakeEnv e = Env(200000, s, 3);
        SwOutcome o = SwLoop(e, W_TRY, 50000);
        assert(o.end == SWL_OTHER && o.why == W_STATE && o.reruns == 2);
        FakeEnv e2 = Env(200000, s, 3);
        o = SwLoop(e2, W_STATE, 50000);
        assert(o.end == SWL_OTHER && o.reruns == 0 && e2.pumps == 0);
    }
    {  // a hold longer than the budget: the loop ends within one step of it, the job still runs
        static const int s[] = {W_TRY};
        FakeEnv e = Env(200000, s, 1);
        const SwOutcome o = SwLoop(e, W_TRY, 20000);
        assert(o.end == SWL_BUDGET && o.why == W_TRY && o.us >= 20000 && o.us < 20000 + 1050 && e.JobRuns() && o.reruns == 20);
    }
    {  // budget 0 never steps; an answer passed in is an answer
        static const int s[] = {W_OK};
        FakeEnv e = Env(200000, s, 1);
        SwOutcome o = SwLoop(e, W_TRY, 0);
        assert(o.end == SWL_BUDGET && o.reruns == 0 && e.pumps == 0);
        o = SwLoop(e, W_OK, 50000);
        assert(o.end == SWL_ANSWERED && o.reruns == 0);
    }
}

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {
    TestSteerWait();
    Exit(0);
}
