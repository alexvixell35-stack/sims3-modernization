// docs/670 §26: host test of roshadow-core.h (shadow mode of the read-only clones) without Windows, Wine or a libc.
// Build and run on Linux (32-bit):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/rsh-host native/ts3native/roshadow-host-test.cpp && /tmp/rsh-host; echo $?
//   0 = pass; else 1 and "assert line N" on stderr.
// What it holds: with shadow on, the game's call sees the world a call with the switch off sees (no start hold-off open,
// nothing written before it), the second gather never writes the live area or its candidate storage, and a job that ran
// or runs between the two calls means no compare. The last case runs the order used until 2026-11-21 (the second gather
// on the live area, both calls inside one hold-off) through the same checks and expects them to fail: this test would
// have caught it.
// Not covered here: the hooks themselves (their counters, the dry hold's gate rule, the free of the copy's storage);
// TestLandCheckRO, TestBlocker2dRO and TestObstructAtRO run those in the MSVC selftest.
#include "roshadow-core.h"

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

typedef unsigned char u8;
static bool Same(const u8* a, const u8* b, int n) {
    for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
    return true;
}
static void Copy(u8* d, const u8* s, int n) {
    for (int i = 0; i < n; i++) d[i] = s[i];
}

// the job state as the sim thread sees it
struct FakeEnv {
    unsigned long runs;
    bool live;
    int open, holds, releases;
    unsigned long JobRuns() const { return runs; }
    bool JobLive() const { return live; }
    void HoldStarts() { open++, holds++; }
    void ReleaseStarts() { open--, releases++; }
};

// a world with one area: the fake gather does what the game's does to it (docs/670 §24.2): end = begin, one 32-byte record
// per waypoint (the vector grows into `pool` when it is full, as through the game's allocator), flag 0x400 cleared, AL 1
static u8 g_area[0x60], g_cand[RSH_CAND * 4], g_pool[RSH_CAND * 16];
static int g_poolAt, g_waypoints, g_gathers, g_liveGathers, g_skew;
static u8*& Word(u8* area, int off) { return *(u8**)(area + off); }
static unsigned long Gather(u8* area) {
    g_gathers++;
    if (area == g_area) g_liveGathers++;
    Word(area, 0x34) = Word(area, 0x30);
    for (int i = 0; i < g_waypoints; i++) {
        if (Word(area, 0x34) == Word(area, 0x38)) {  // full: twice the size (1 for an empty vector), the records moved
            const int have = (int)(Word(area, 0x34) - Word(area, 0x30)), want = have ? have * 2 : RSH_CAND;
            u8* mem = g_pool + g_poolAt;
            g_poolAt += want;
            assert(g_poolAt <= (int)sizeof(g_pool));
            Copy(mem, Word(area, 0x30), have);
            Word(area, 0x30) = mem, Word(area, 0x34) = mem + have, Word(area, 0x38) = mem + want;
        }
        u8* rec = Word(area, 0x34);
        for (int b = 0; b < RSH_CAND; b++) rec[b] = (u8)(area[0] + i * 31 + b * 7 + (g_skew && area != g_area && i == 1 && b == 5));
        Word(area, 0x34) = rec + RSH_CAND;
    }
    *(unsigned long*)(area + RSH_AREA_FLAGS) &= ~0x400ul;
    return 1;
}
static void World(int waypoints) {
    for (int i = 0; i < (int)sizeof(g_area); i++) g_area[i] = (u8)(i * 3 + 1);
    for (int i = 0; i < (int)sizeof(g_cand); i++) g_cand[i] = 0xEE;
    for (int i = 0; i < (int)sizeof(g_pool); i++) g_pool[i] = 0xDD;
    *(unsigned long*)(g_area + RSH_AREA_FLAGS) = 0x403;
    Word(g_area, 0x30) = Word(g_area, 0x34) = g_cand;
    Word(g_area, 0x38) = g_cand + sizeof(g_cand);
    g_poolAt = g_gathers = g_liveGathers = g_skew = 0;
    g_waypoints = waypoints;
}

// one shadowed gather as landro.cpp's LcShadow / LcCompare make it. Returns RoShadow's end; *mismatch = the compare's result
struct Seen {
    int openAtGame;        // start hold-offs open while the game's call ran
    int openAtClone;       // and while the clone ran
    bool liveSameAtClone;  // the live area and its storage, at the clone's end, as the game's call left them
    bool cloneOnLive;      // the clone gathered the live area
    bool mismatch;
    u8* storage;           // the copy's storage handed back for the free
};
static u8 g_scratch[RSH_AREA_SIZE];
static u8 g_areaAfter[sizeof(g_area)], g_candAfter[sizeof(g_cand)];
template <class Hook> static int Shadowed(FakeEnv& e, Seen& s, Hook duringGame) {
    s = Seen{-1, -1, false, false, false, 0};
    RshArea in;
    RshAreaSnap(&in, g_area);
    unsigned long r = 0;
    return RoShadow(e, [&] {
        s.openAtGame = e.open;
        r = Gather(g_area);
        Copy(g_areaAfter, g_area, sizeof(g_area)), Copy(g_candAfter, g_cand, sizeof(g_cand));
        duringGame();
    }, [&] {
        s.openAtClone = e.open;
        const int live0 = g_liveGathers;
        RshAreaLoad(g_scratch, &in);
        const unsigned long r2 = Gather(g_scratch);
        unsigned long n1 = 0, n2 = 0;
        const unsigned long h1 = RshAreaHash(g_area, &n1), h2 = RshAreaHash(g_scratch, &n2);
        s.mismatch = r != r2 || n1 != n2 || h1 != h2 ||
                     *(unsigned long*)(g_area + RSH_AREA_FLAGS) != *(unsigned long*)(g_scratch + RSH_AREA_FLAGS);
        s.cloneOnLive = g_liveGathers != live0;
        s.liveSameAtClone = Same(g_area, g_areaAfter, sizeof(g_area)) && Same(g_cand, g_candAfter, sizeof(g_cand));
        s.storage = RshAreaTakeStorage(g_scratch);
    });
}
static void Nothing() {}

// the order used until 2026-11-21: one hold-off around both calls, the second gather on the live area
static void OldShadowed(FakeEnv& e, Seen& s) {
    s = Seen{-1, -1, false, false, false, 0};
    e.HoldStarts();
    s.openAtGame = e.open;
    Gather(g_area);
    Copy(g_areaAfter, g_area, sizeof(g_area)), Copy(g_candAfter, g_cand, sizeof(g_cand));
    s.openAtClone = e.open;
    const int live0 = g_liveGathers;
    g_cand[0] ^= 0xFF;  // any reader of the live storage between the two gathers would see the rewrite; make it visible
    Gather(g_area);
    s.cloneOnLive = g_liveGathers != live0;
    s.liveSameAtClone = Same(g_area, g_areaAfter, sizeof(g_area)) && Same(g_cand, g_candAfter, sizeof(g_cand)) && !s.cloneOnLive;
    e.ReleaseStarts();
}

static void TestOrder() {
    {  // no job: the game's call with nothing open, the clone inside the hold-off, one hold and one release
        FakeEnv e = {7, false, 0, 0, 0};
        int game = 0, clone = 0, openGame = -1, openClone = -1;
        const int end = RoShadow(e, [&] { game++, openGame = e.open; }, [&] { clone++, openClone = e.open; });
        assert(end == RSH_COMPARED && game == 1 && clone == 1 && openGame == 0 && openClone == 1 && e.open == 0 && e.holds == 1 && e.releases == 1);
    }
    {  // a job already runs: the game's call only, no hold-off at all
        FakeEnv e = {7, true, 0, 0, 0};
        int game = 0, clone = 0;
        assert(RoShadow(e, [&] { game++; }, [&] { clone++; }) == RSH_JOB && game == 1 && clone == 0 && e.holds == 0 && e.open == 0);
    }
    {  // a job starts during the game's call and still runs: no clone, the hold-off is released
        FakeEnv e = {7, false, 0, 0, 0};
        int clone = 0;
        assert(RoShadow(e, [&] { e.live = true, e.runs++; }, [&] { clone++; }) == RSH_JOB && clone == 0 && e.open == 0 && e.holds == 1 && e.releases == 1);
    }
    {  // a whole job ran during the game's call (idle again): the answers are from two world states, no clone
        FakeEnv e = {7, false, 0, 0, 0};
        int clone = 0;
        assert(RoShadow(e, [&] { e.runs++; }, [&] { clone++; }) == RSH_JOB && clone == 0 && e.open == 0);
    }
    {  // the worker published A_RUNNING and is backing off for our hold-off (not yet counted as a run): no clone
        FakeEnv e = {7, false, 0, 0, 0};
        int clone = 0;
        assert(RoShadow(e, [&] { e.live = true; }, [&] { clone++; }) == RSH_JOB && clone == 0 && e.open == 0);
    }
}

static void TestArea() {
    FakeEnv e = {0, false, 0, 0, 0};
    Seen s;
    for (int wp = 0; wp <= 4; wp++) {  // 0 = an empty gather; up to the live vector's capacity
        World(wp);
        assert(Shadowed(e, s, Nothing) == RSH_COMPARED);
        assert(s.openAtGame == 0 && s.openAtClone == 1 && e.open == 0);
        assert(!s.cloneOnLive && s.liveSameAtClone && !s.mismatch && g_gathers == 2 && g_liveGathers == 1);
        assert(Same(g_area, g_areaAfter, sizeof(g_area)) && Same(g_cand, g_candAfter, sizeof(g_cand)));
        assert(Word(g_area, 0x30) == g_cand && Word(g_area, 0x34) == g_cand + wp * RSH_CAND && Word(g_area, 0x38) == g_cand + sizeof(g_cand));
        assert(*(unsigned long*)(g_area + RSH_AREA_FLAGS) == 3);
        // the copy's storage came from the allocator (never the live vector's) and is handed back; the copy keeps none
        assert(wp ? s.storage >= g_pool && s.storage < g_pool + sizeof(g_pool) : s.storage == 0);
        assert(!Word(g_scratch, 0x30) && !Word(g_scratch, 0x34) && !Word(g_scratch, 0x38));
    }
    // the copy starts from the inputs before the game's call (flag 0x400 still set there), not from what the call left
    World(2);
    RshArea in;
    RshAreaSnap(&in, g_area);
    Gather(g_area);
    RshAreaLoad(g_scratch, &in);
    assert(*(unsigned long*)(g_scratch + RSH_AREA_FLAGS) == 0x403 && *(unsigned long*)(g_area + RSH_AREA_FLAGS) == 3);
    assert(Same(g_scratch, in.b, RSH_AREA_VEC) && Same(g_scratch + RSH_AREA_TAG, in.b + RSH_AREA_TAG, RSH_AREA_SIZE - RSH_AREA_TAG));
    assert(!Word(g_scratch, 0x30) && !Word(g_scratch, 0x38));  // its own (empty) vector, not the live one's words
    // a second gather that differs is a mismatch, and still leaves the live area alone
    World(3);
    g_skew = 1;
    assert(Shadowed(e, s, Nothing) == RSH_COMPARED && s.mismatch && s.liveSameAtClone && !s.cloneOnLive);
    // a job in between: one gather only
    World(3);
    assert(Shadowed(e, s, [&] { e.runs++; }) == RSH_JOB && g_gathers == 1 && s.openAtClone == -1 && e.open == 0);
    // an empty vector hashes the same with and without storage (the copy of an empty gather has none)
    World(0);
    unsigned long n0 = 9, n1 = 9;
    u8 none[RSH_AREA_SIZE] = {};
    assert(RshAreaHash(g_area, &n0) == RshAreaHash(none, &n1) && n0 == 0 && n1 == 0);
    // a bad vector hashes to nothing instead of reading it
    World(1);
    unsigned long n = 9;
    Word(g_area, 0x34) = Word(g_area, 0x30) - 1;
    assert(RshAreaHash(g_area, &n) == 0 && n == 0);
}

// the order before the fix fails the same checks
static void TestOldOrderIsCaught() {
    FakeEnv e = {0, false, 0, 0, 0};
    Seen s;
    World(3);
    OldShadowed(e, s);
    assert(s.openAtGame == 1);  // the game's call ran inside our hold-off: not the call a switch-off run makes
    assert(s.cloneOnLive && !s.liveSameAtClone && g_liveGathers == 2);  // the second gather rewrote the live storage
}

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {
    TestOrder();
    TestArea();
    TestOldOrderIsCaught();
    Exit(0);
}
