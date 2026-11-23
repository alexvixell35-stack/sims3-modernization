// docs/183 §7: host test of jobyield-core.h (the records of a route whose task steps aside) without Windows, Wine or a libc.
// Build and run on Linux (32-bit):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/jy-host native/ts3native/jobyield-host-test.cpp && /tmp/jy-host; echo $?
//   0 = pass; else 1 and "assert line N" on stderr.
// The slots are a model of ts3native's (g_rfbHandle / g_rfbWhy, g_scSyncH, g_kdScopeH / g_kdScope / g_kdScopeIa, g_ufH) with
// the readers written as the code reads them: IcCollect's reason, PdScopeAt's scope, SyncIc's two marks. Part 1 is the
// state leak of a sleep with the records left in the slots (JobYield before this file); part 2 the same steps with the
// records parked. Not covered here: JyBusy's use of it (the busy rule, a followed route, the three ops), which
// TestJobYield in jobyield-test.h runs in the MSVC selftest.
#include "jobyield-core.h"

extern "C" void* memset(void* d, int c, unsigned n) {
    volatile char* p = (volatile char*)d;
    while (n--) *p++ = (char)c;
    return d;
}

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

enum { NOSTART = 0, QFULL = 9, RUNNING = 8 };  // RFB_* of ts3native.cpp
enum { HOUSEHOLD = 1, UNSEEN = 2, SEEN_NPC = 3 };  // ts3fixes' scope answers

struct Slots {
    unsigned rfbH, scH, kdH, ufH;
    int rfbWhy;
    long kdScope, kdIa;
    // jobyield-core.h's Env
    bool TakeWhy(unsigned h, int* why) { if (rfbH != h) return false; *why = rfbWhy; rfbH = 0; return true; }
    bool TakeSc(unsigned h) { if (scH != h) return false; scH = 0; return true; }
    bool TakeScope(unsigned h, long* s, long* ia) { if (kdH != h) return false; *s = kdScope, *ia = kdIa; kdH = 0; return true; }
    bool TakeUf(unsigned h) { if (ufH != h) return false; ufH = 0; return true; }
    void PutWhy(unsigned h, int why) { rfbH = h, rfbWhy = why; }
    void PutSc(unsigned h) { scH = h; }
    void PutScope(unsigned h, long s, long ia) { kdH = h, kdScope = s, kdIa = ia; }
    void PutUf(unsigned h) { ufH = h; }
    // ts3fixes' messages for a route, then its START (refused: the reason is noted; started: the job takes the records)
    void Marks(unsigned h, long scope, long ia, bool uf, bool sc) { kdH = h, kdScope = scope, kdIa = ia; if (uf) ufH = h; pendSc = sc ? h : 0; }
    void StartRefused(unsigned h, int why) { rfbH = h, rfbWhy = why; scH = pendSc == h ? h : 0; pendSc = 0; }
    void StartTaken(unsigned h) { rfbH = 0, scH = 0, pendSc = 0; if (kdH == h) kdH = 0; if (ufH == h) ufH = 0; }
    unsigned pendSc;
    // the readers
    int IcWhy(unsigned h) { const int w = rfbH == h ? rfbWhy : NOSTART; if (rfbH == h) rfbH = 0; return w; }  // IcCollect
    bool IcSc(unsigned h) { const bool m = scH == h; scH = 0; return m; }                                    // IcCollect, kind 0
    bool IcUf(unsigned h) { if (!h || ufH != h) return false; ufH = 0; return true; }                        // UfTake
    long Scope(unsigned h) const { return kdH && (!h || kdH == h) ? kdScope : 0; }                           // PdScopeAt
    void PlanEnd(unsigned h) { if (kdH == h) kdH = 0; }                                                      // KdScopeClear
};

static const unsigned kKeep = 20000;
static const unsigned hNpc = 0x25000011, hOther = 0x25000022, hHouse = 0x25000033;

static void TestJyPark() {
    // 1. the leak: an unseen NPC's plan (scope, UnseenFallback mark, stuck-check mark) is refused its START and sleeps with
    // its records left in the slots
    {
        Slots s = {};
        s.Marks(hNpc, UNSEEN, 77, true, true);
        s.StartRefused(hNpc, QFULL);
        // while it sleeps: a household Sim's plan with no route handle reads the NPC's scope as its own
        assert(s.Scope(0) == UNSEEN);
        // and another route's marks and START take the slots
        s.Marks(hOther, SEEN_NPC, 5, false, false);
        s.StartTaken(hOther);
        // the NPC's plan after the sleep: a plan nobody asked a START for, no scope, no stuck-check mark
        assert(s.IcWhy(hNpc) == NOSTART && s.Scope(hNpc) == 0 && !s.IcSc(hNpc));
    }
    // 2. the same steps with the records parked at the first busy answer and put back at the last
    {
        Slots s = {};
        JyPark p = {};
        s.Marks(hNpc, UNSEEN, 77, true, true);
        s.StartRefused(hNpc, QFULL);
        assert(JypPark(p, s, hNpc, 1000, kKeep) == (JYP_WHY | JYP_SC | JYP_SCOPE | JYP_UF) && p.parked == 1 && JypLive(p) == 1);
        assert(!s.rfbH && !s.scH && !s.kdH && !s.ufH);  // nothing of the sleeping route is left to read
        assert(s.Scope(0) == 0);                          // the household plan in between: its own (unknown = household) scope
        s.Marks(hOther, SEEN_NPC, 5, false, false);
        s.StartTaken(hOther);
        assert(JypPark(p, s, hNpc, 1016, kKeep) == (JYP_WHY | JYP_SC | JYP_SCOPE | JYP_UF) && p.parked == 1);  // next tick, still busy
        assert(JypBack(p, s, hNpc, 1032, kKeep) == (JYP_WHY | JYP_SC | JYP_SCOPE | JYP_UF) && p.back == 1 && JypLive(p) == 0);
        assert(s.Scope(hNpc) == UNSEEN && s.kdIa == 77);
        assert(s.IcWhy(hNpc) == QFULL && s.IcSc(hNpc) && s.IcUf(hNpc));  // the plan it would have been without the sleep
        s.PlanEnd(hNpc);
        // exactly once: nothing comes back a second time
        assert(JypBack(p, s, hNpc, 1040, kKeep) == 0 && p.back == 1 && !s.rfbH && !s.kdH && !s.scH && !s.ufH);
    }
    // 3. two routes asleep at once keep their own records; another route's record in a slot is not touched by a park
    {
        Slots s = {};
        JyPark p = {};
        s.Marks(hNpc, UNSEEN, 1, false, false);
        s.StartRefused(hNpc, QFULL);
        assert(JypPark(p, s, hNpc, 0, kKeep) == (JYP_WHY | JYP_SCOPE));
        s.Marks(hHouse, HOUSEHOLD, 2, false, false);
        s.StartRefused(hHouse, RUNNING);
        assert(JypPark(p, s, hNpc, 16, kKeep) == (JYP_WHY | JYP_SCOPE) && s.kdH == hHouse && s.rfbH == hHouse);
        assert(JypPark(p, s, hHouse, 16, kKeep) == (JYP_WHY | JYP_SCOPE) && JypLive(p) == 2 && !s.kdH && !s.rfbH);
        assert(JypBack(p, s, hHouse, 32, kKeep) && s.Scope(hHouse) == HOUSEHOLD && s.IcWhy(hHouse) == RUNNING);
        s.PlanEnd(hHouse);
        assert(JypBack(p, s, hNpc, 48, kKeep) && s.Scope(hNpc) == UNSEEN && s.IcWhy(hNpc) == QFULL && JypLive(p) == 0);
    }
    // 4. the sleep ends by an exception (drop), the route sends a new START (drop), or the task never comes back (expiry)
    {
        Slots s = {};
        JyPark p = {};
        s.Marks(hNpc, UNSEEN, 1, true, false);
        s.StartRefused(hNpc, QFULL);
        assert(JypPark(p, s, hNpc, 0, kKeep));
        assert(JypDrop(p, hNpc) && p.dropped == 1 && JypLive(p) == 0 && !JypDrop(p, hNpc));
        assert(JypBack(p, s, hNpc, 16, kKeep) == 0 && !s.kdH && !s.ufH && !s.rfbH);
        s.Marks(hNpc, UNSEEN, 1, false, false);
        s.StartRefused(hNpc, QFULL);
        assert(JypPark(p, s, hNpc, 100, kKeep));
        assert(JypBack(p, s, hNpc, 100 + kKeep + 1, kKeep) == 0 && p.expired == 1 && JypLive(p) == 0 && !s.kdH && !s.rfbH);
        // the clock wraps: a young record stays young
        s.Marks(hNpc, UNSEEN, 1, false, false);
        s.StartRefused(hNpc, QFULL);
        assert(JypPark(p, s, hNpc, 0xFFFFFFF0u, kKeep));
        assert(JypBack(p, s, hNpc, 0x00000010u, kKeep) == (JYP_WHY | JYP_SCOPE));
    }
    // 5. limits: no handle (an older ts3fixes asks on a route with handle 0) and nothing armed park nothing; a full table
    // leaves the slots as they are, and a lost record makes room
    {
        Slots s = {};
        JyPark p = {};
        s.Marks(hNpc, UNSEEN, 1, false, false);
        s.StartRefused(hNpc, QFULL);
        assert(JypPark(p, s, 0, 0, kKeep) == 0 && s.kdH == hNpc && s.rfbH == hNpc && p.parked == 0);
        assert(JypPark(p, s, hOther, 0, kKeep) == 0 && JypLive(p) == 0 && p.parked == 0);
        for (unsigned i = 0; i < JYP_SLOTS; i++) {
            s.Marks(0x26000000 + i, SEEN_NPC, 0, false, false);
            s.StartRefused(0x26000000 + i, QFULL);
            assert(JypPark(p, s, 0x26000000 + i, 10, kKeep));
        }
        s.Marks(hNpc, UNSEEN, 1, false, false);
        s.StartRefused(hNpc, QFULL);
        assert(JypLive(p) == JYP_SLOTS && JypPark(p, s, hNpc, 20, kKeep) == JYP_FULL && p.full == 1 && s.kdH == hNpc && s.rfbH == hNpc);
        assert(JypPark(p, s, hNpc, 10 + kKeep + 1, kKeep) == (JYP_WHY | JYP_SCOPE) && p.expired >= 1 && JypLive(p) == 1);
    }
}

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {
    TestJyPark();
    Exit(0);
}
