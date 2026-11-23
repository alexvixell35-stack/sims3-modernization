// docs/669: host test of [RouteSpeed] CellIndex without Windows, Wine or a libc. Three parts:
//   - cellindex-core.h (the index sets);
//   - the Windows selftest's cases as they are (cellindex-test.h on cellindex.cpp's CiFindHook: fake 32-bit tables and cells);
//   - a model check: a table built as the game's (10007 chained buckets, bucket = (k0 xor k1) mod 10007) and the index in
//     front of it must give the same answer for every lookup of a random insert / find / clear run.
// Build and run on Linux (32-bit, as the game's pointers are):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/ci-host native/ts3native/cellindex-host-test.cpp && /tmp/ci-host; echo $?
//   0 = pass; 1 and "assert line N" (of cellindex-test.h) on stderr; 200+ = a wrong served answer in the model check;
//   else the number of the failed CHECK of this file.
// Not covered here: the install (prologue bytes, the clear stub), which needs the game's code.
#define CELLINDEX_HOST_TEST
#define __declspec(x)
#define __fastcall __attribute__((fastcall))
#define NULL 0
#define TS3A(x) (x)
typedef unsigned char u8;
typedef unsigned long DWORD;
typedef long LONG;
typedef long long LONGLONG;
union LARGE_INTEGER { LONGLONG QuadPart; };
enum { MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_READWRITE = 4 };

extern "C" void* memset(void* d, int c, unsigned int n) {
    volatile unsigned char* p = (unsigned char*)d;
    while (n--) *p++ = (unsigned char)c;
    return d;
}
extern "C" void* memcpy(void* d, const void* s, unsigned int n) {
    volatile unsigned char* p = (unsigned char*)d;
    const unsigned char* q = (const unsigned char*)s;
    while (n--) *p++ = *q++;
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
#define assert(x) ((x) ? (void)0 : Fail(__LINE__))
static int printf(const char*, ...) { return 0; }
static void Log(const char*, ...) {}
static int g_check;
#define CHECK(x) do { ++g_check; if (!(x)) Exit(g_check + 1); } while (0)

static LONG InterlockedIncrement(volatile LONG* p) { return ++*p; }
static LONGLONG g_fakeQpc;
static void QueryPerformanceCounter(LARGE_INTEGER* q) { q->QuadPart = (g_fakeQpc += 1000); }
static void QueryPerformanceFrequency(LARGE_INTEGER* q) { q->QuadPart = 1000000; }
static LARGE_INTEGER g_qpf;
static char g_fakeHeap[1 << 20] __attribute__((aligned(16)));
static int g_fakeAllocs;
static void* VirtualAlloc(void*, unsigned int size, int, int) { return !g_fakeAllocs++ && size <= sizeof(g_fakeHeap) ? g_fakeHeap : NULL; }

// what cellindex.cpp takes from the rest of ts3native.cpp
static LONG g_fakeScope;
static LONG PccScopeNow(bool* worker) { return *worker = false, g_fakeScope; }
static volatile LONG g_nmClearSeq;

#include "cellindex.cpp"
#include "cellindex-test.h"

// the model: the game's table. Cells are numbered from 1; next = the chain link
enum { M_CELLS = 6000 };
struct MCell { uint32_t k0, k1, next; };
static MCell g_cell[M_CELLS + 1];
static uint32_t g_head[2][CI_BUCKETS], g_cells;
static uint64_t g_steps, g_walks;
static uint32_t MFind(int t, uint32_t k0, uint32_t k1, uint32_t* bucket) {
    *bucket = CiBucket(k0, k1);
    g_walks++;
    for (uint32_t p = g_head[t][*bucket]; p; p = g_cell[p].next) {
        g_steps++;
        if (g_cell[p].k0 == k0 && g_cell[p].k1 == k1) return p;
    }
    return 0;
}
static uint32_t g_rng = 12345;
static uint32_t Rnd() { g_rng ^= g_rng << 13, g_rng ^= g_rng >> 17, g_rng ^= g_rng << 5; return g_rng; }

static CiIndex x;  // the model check's own index (the hook tests use the thread's)

extern "C" [[noreturn]] void _start() {
    TestCellIndex();
    // an NPC router (scope 2 / 3) is served with CellIndexAll=0; household (1) and unknown (0) are not
    {
        CellIndexCounters& c = ts3native_cellindex;
        g_ciFindNext = CiFakeFind, g_ciAll = false, c.ciMode = 1;
        CiFakeInit(g_ciTblA);
        CiFakeInsert(g_ciTblA, &g_ciCells[0], 9, 0);
        InterlockedIncrement(&g_nmClearSeq);
        const DWORD key[2] = {9, 0};
        DWORD out[2];
        const LONG scopes[4] = {2, 3, 1, 0};
        CiFindHook(g_ciTblA, NULL, out, key);  // fills
        for (int i = 0; i < 4; i++) {
            const LONG h0 = c.ciHits, m0 = c.ciScopeMiss;
            g_fakeScope = scopes[i];
            CHECK(CiFindHook(g_ciTblA, NULL, out, key) == out && out[0] == (DWORD)(uintptr_t)&g_ciCells[0]);
            CHECK(c.ciHits - h0 == (i < 2) && c.ciScopeMiss - m0 == (i >= 2));
        }
        c.ciMode = 0;
    }
    CHECK(CiBucket(0x12345678, 0) == 0x12345678u % 10007 && CiBucket(5, 5) == 0 && CiBucket(5 ^ 3, 0) == CiBucket(5, 3));
    CHECK(CiSync(&x, 1) && !CiSync(&x, 1) && CiSync(&x, 2));
    CHECK(!CiFind(&x, 0x1000, 7, 8) && CiStore(&x, 0x1000, 7, 8, 0xC0, 0xB0) == CI_NEW);
    CiSlot* s = CiFind(&x, 0x1000, 7, 8);
    CHECK(s && s->cell == 0xC0 && s->bucket == 0xB0);
    CHECK(!CiFind(&x, 0x1004, 7, 8) && !CiFind(&x, 0x1000, 7, 9) && !CiFind(&x, 0x1000, 6, 8));
    CHECK(CiStore(&x, 0x1000, 7, 8, 0xC4, 0xB4) == CI_SAME && CiFind(&x, 0x1000, 7, 8)->cell == 0xC4);
    CiDrop(CiFind(&x, 0x1000, 7, 8));
    CHECK(!CiFind(&x, 0x1000, 7, 8));
    // one set: CI_WAYS keys fit, one more evicts exactly one
    memset(&x, 0, sizeof(x));
    CiSync(&x, 1);
    const uint32_t home = CiHash(0x1000, 1, 0);
    uint32_t same[CI_WAYS + 1] = {0}, n = 0;
    for (uint32_t k = 1; n < CI_WAYS + 1; k++)
        if (CiHash(0x1000, k, 0) == home) same[n++] = k;
    for (uint32_t i = 0; i < CI_WAYS; i++) CHECK(CiStore(&x, 0x1000, same[i], 0, 0x100 + i, 0x200 + i) == CI_NEW);
    CHECK(CiStore(&x, 0x1000, same[CI_WAYS], 0, 0x100 + CI_WAYS, 0x200 + CI_WAYS) == CI_EVICT);
    uint32_t kept = 0;
    for (uint32_t i = 0; i <= CI_WAYS; i++) {
        const CiSlot* f = CiFind(&x, 0x1000, same[i], 0);
        if (f) { kept++; CHECK(f->cell == 0x100 + i && f->bucket == 0x200 + i); }
    }
    CHECK(kept == CI_WAYS && CiFind(&x, 0x1000, same[CI_WAYS], 0));
    x.gen = 0xFFFFFFFF, x.slot[5].gen = 1;
    CiReset(&x);
    CHECK(x.gen == 1 && x.slot[5].gen == 0);
    CHECK(CiServeRule(1, true, true, true) == 1 && CiServeRule(2, true, true, true) == 0 && CiServeRule(0, true, true, true) == 0);
    CHECK(CiServeRule(1, false, true, true) == 0 && CiServeRule(1, true, false, true) == 0 && CiServeRule(1, true, true, false) == 0);

    // model check, keys shaped as the game's (QuadCell_Key 0x669FF0): k0 = ix | depth << 12 | iz << 16, k1 = shape | level << 8
    // | tree << 12, two tables. Every lookup: the index answer (when it has one) must be the model's; the model's answer is
    // then kept, as the hook does. A "not found" is followed by the insert, as the game's find-or-create.
    memset(&x, 0, sizeof(x));
    uint32_t seq = 1, served = 0, lookups = 0;
    for (int round = 0; round < 4; round++) {
        memset(g_head, 0, sizeof(g_head));
        g_cells = 0, seq++;  // the table clear
        for (int i = 0; i < 60000; i++) {
            const int t = Rnd() & 1;
            const uint32_t ix = Rnd() % 12, iz = Rnd() % 12, shape = Rnd() % 20;  // 2880 keys a table
            const uint32_t k0 = ix | 6u << 12 | iz << 16, k1 = shape | 4u << 8 | 3u << 12;
            const uintptr_t table = 0x10000 + (uintptr_t)t * 0x10000;
            CiSync(&x, seq);
            uint32_t bucket;
            const uint32_t want = MFind(t, k0, k1, &bucket);
            const CiSlot* f = CiFind(&x, table, k0, k1);
            lookups++;
            if (f) {
                served++;
                if (f->cell != want || f->bucket != bucket) Exit(200 + round);  // a wrong served answer
            }
            if (want) { CiStore(&x, table, k0, k1, want, bucket); continue; }
            if (g_cells == M_CELLS) continue;
            MCell& c = g_cell[++g_cells];
            c.k0 = k0, c.k1 = k1, c.next = g_head[t][bucket];
            g_head[t][bucket] = g_cells;
        }
    }
    CHECK(served > lookups / 2);                // repeats are the common case and the index answers them
    CHECK(g_steps > g_walks * 3);               // the game's chains for these keys: several cells a walk (ix xor shape collides)
    Exit(0);
}
