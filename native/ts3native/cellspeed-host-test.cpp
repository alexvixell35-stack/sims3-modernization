// docs/669 §8-§10: host test of [RouteSpeed] ClassMemo and OpenHint without Windows, Wine or a libc: the Windows
// selftest's cases as they are (cellspeed-test.h on cellspeed.cpp's two hooks: fake 32-bit cells, contexts, agents, A*
// nodes and buckets, a fake recompute and a reference sorted insert), then the NPC / household scope rule of both and
// longer model runs of the open list (more searches, other spreads and seeds).
// Build and run on Linux (32-bit, as the game's pointers are):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -fno-strict-aliasing -mstackrealign -static -Wno-everything \
//     -o /tmp/cs-host native/ts3native/cellspeed-host-test.cpp && /tmp/cs-host; echo $?
//   0 = pass; 1 and "assert line N" (of cellspeed-test.h) on stderr; else the number of the failed CHECK of this file + 1.
// Twelve broken copies (proof without the tie / state / link test, a link step short, a key or sequence short, a serve
// without the key clear or X+0x4C, no scope rule) each fail it (docs/669 §11).
// Not covered here: the installs (prologue bytes, the clear stub) and the game's real functions.
#define CELLSPEED_HOST_TEST
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
static unsigned int g_fakeUsed;
static void* VirtualAlloc(void*, unsigned int size, int, int) {  // zeroed memory, never freed
    size = (size + 4095) & ~4095u;
    if (g_fakeUsed + size > sizeof(g_fakeHeap)) return NULL;
    void* const p = g_fakeHeap + g_fakeUsed;
    g_fakeUsed += size;
    return p;
}

// what cellspeed.cpp takes from the rest of ts3native.cpp
static LONG g_fakeScope;
static LONG PccScopeNow(bool* worker) { return *worker = false, g_fakeScope; }
static volatile LONG g_nmClearSeq, g_snWriteSeq;
static LONG t_fmHostSeq;

#include "cellspeed.cpp"
#include "cellspeed-test.h"

extern "C" [[noreturn]] void _start() {
    TestCellSpeed();
    // an NPC router (scope 2 / 3) is served with ClassMemoAll=0 / OpenHintAll=0; household (1) and unknown (0) are not
    {
        ClassMemoCounters& c = ts3native_classmemo;
        static KmFakeLeaf leaf;
        static u8 x[0x60], cell[0x40];
        leaf.cls[1] = 1, leaf.mult[1] = 0x47C35000;
        *(DWORD*)(x + 0x50) = 0x10000;
        g_kmNext = KmFakeRecalc, g_kmAll = false, c.kmMode = 1, g_csOptGlobal = &g_kmFakeGlobal;
        InterlockedIncrement(&g_snWriteSeq);
        KmFakeCell(cell, &leaf, 0x7000, 1, x);
        KmRecalcHook(cell, NULL, x);  // fills
        const LONG scopes[4] = {2, 3, 1, 0};
        for (int i = 0; i < 4; i++) {
            const LONG h0 = c.kmHits, m0 = c.kmScopeMiss;
            const int r0 = g_kmFakeRuns;
            g_fakeScope = scopes[i];
            KmFakeCell(cell, &leaf, 0x7000, 1, x);
            KmRecalcHook(cell, NULL, x);
            CHECK(cell[0x20] == 1 && *(DWORD*)(cell + 0x10) == 0x47C35000 && *(DWORD*)(cell + 0x1C) == 1);
            CHECK(c.kmHits - h0 == (i < 2) && c.kmScopeMiss - m0 == (i >= 2) && g_kmFakeRuns - r0 == (i >= 2));
        }
        c.kmMode = 0;
    }
    {
        OpenHintCounters& c = ts3native_openhint;
        g_ohNext = OhFakeInsert, g_csGen = &g_ohGenWord, g_csVtGraph = 0x00FEA080, g_ohAll = false;
        const LONG scopes[4] = {2, 3, 1, 0};
        for (int i = 0; i < 4; i++) {
            const LONG h0 = c.ohHits, m0 = c.ohScopeMiss;
            g_fakeScope = scopes[i];
            OhModel(1, 2, 1, 2.0f);
            CHECK((c.ohHits - h0 > 500) == (i < 2) && (c.ohScopeMiss - m0 > 500) == (i >= 2));
            CHECK(c.ohHits - h0 == 0 || c.ohScopeMiss - m0 == 0);
        }
        // longer model runs: other seeds and spreads, serve and shadow; OhModel asserts every insert's place and the lists
        g_ohAll = true;
        const float spreads[5] = {1.0f, 2.0f, 3.5f, 6.0f, 12.0f};
        for (int i = 0; i < 40; i++) {
            g_ohRng = 0x9E3779B9u * (DWORD)(i + 1) | 1;
            OhModel(1 + (i & 1), 6, i % 3 == 2 ? 3 : 1, spreads[i % 5]);
        }
        CHECK(c.ohMismatch == 0 && c.ohChecks > 10000 && c.ohHits > 10000);
        c.ohMode = 0;
    }
    Exit(0);
}
