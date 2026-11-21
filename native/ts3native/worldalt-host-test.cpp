// docs/676 §14: host test of [WorldPrecompute] WorldAlt (worldalt.cpp, worldalt-core.h) without Windows, Wine or a libc.
// worldalt-test.h is included as is, with the long run switched on: the landmark bound of every node against a brute-force
// shortest distance on random routing worlds with random blocking, and the path cost of a stand-in for the world A* with
// and without the bound against the same brute force.
// Build and run on Linux (32-bit, so the game's pointer layouts hold), about 15 s:
//   clang++ -m32 -O2 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -fuse-ld=lld \
//     -Wno-everything -o /tmp/wa-host native/ts3native/worldalt-host-test.cpp && /tmp/wa-host; echo $?
//   0 = pass (one summary line on stdout); else 1 and "assert line N" (of worldalt-test.h) on stderr.
// Not covered here: the two thunks' assembly and the install checks (the Windows selftest runs them), the log lines.
#define WORLDALT_HOST_TEST
#define NULL 0
#define __fastcall __attribute__((fastcall))
#define __cdecl __attribute__((cdecl))
#define __declspec(x)
typedef unsigned long DWORD;
typedef long LONG;
typedef long long LONGLONG;
typedef unsigned short WORD;
typedef unsigned char u8;
typedef unsigned int size_t;
typedef unsigned int uintptr_t;
union LARGE_INTEGER { LONGLONG QuadPart; };

extern "C" void* memset(void* d, int c, size_t n) {
    volatile unsigned char* p = (unsigned char*)d;
    while (n--) *p++ = (unsigned char)c;
    return d;
}
extern "C" void* memcpy(void* d, const void* s, size_t n) {
    volatile unsigned char* p = (unsigned char*)d;
    const unsigned char* q = (const unsigned char*)s;
    while (n--) *p++ = *q++;
    return d;
}
extern "C" int memcmp(const void* a, const void* b, size_t n) {
    const volatile unsigned char* p = (const unsigned char*)a;
    const volatile unsigned char* q = (const unsigned char*)b;
    for (; n--; p++, q++)
        if (*p != *q) return *p < *q ? -1 : 1;
    return 0;
}
// 64-bit division helpers the compiler calls on 32-bit x86 (no libgcc here)
extern "C" unsigned long long __udivmoddi4(unsigned long long n, unsigned long long d, unsigned long long* rem) {
    unsigned long long q = 0, r = 0;
    for (int i = 63; i >= 0; i--) {
        r = (r << 1) | ((n >> i) & 1);
        if (r >= d) r -= d, q |= 1ULL << i;
    }
    if (rem) *rem = r;
    return q;
}
extern "C" unsigned long long __udivdi3(unsigned long long n, unsigned long long d) { return __udivmoddi4(n, d, 0); }
extern "C" unsigned long long __umoddi3(unsigned long long n, unsigned long long d) {
    unsigned long long r = 0;
    __udivmoddi4(n, d, &r);
    return r;
}
extern "C" long long __divdi3(long long n, long long d) {
    const bool neg = (n < 0) != (d < 0);
    const unsigned long long q = __udivmoddi4(n < 0 ? 0ULL - (unsigned long long)n : (unsigned long long)n, d < 0 ? 0ULL - (unsigned long long)d : (unsigned long long)d, 0);
    return neg ? -(long long)q : (long long)q;
}
extern "C" long long __moddi3(long long n, long long d) { return n - __divdi3(n, d) * d; }
static long Sys(long nr, long a, long b, long c) {
    long r;
    __asm__ volatile("int $0x80" : "=a"(r) : "a"(nr), "b"(a), "c"(b), "d"(c) : "memory");
    return r;
}
[[noreturn]] static void Exit(long code) {
    Sys(1, code, 0, 0);
    __builtin_unreachable();
}
static int PutNum(char* b, long long v) {
    char d[24];
    int n = 0, at = 0;
    if (v < 0) b[at++] = '-', v = -v;
    do { d[n++] = (char)('0' + v % 10); v /= 10; } while (v);
    while (n) b[at++] = d[--n];
    return at;
}
[[noreturn]] static void Fail(int line) {
    char b[40] = "assert line ";
    int at = 12 + PutNum(b + 12, line);
    b[at++] = '\n';
    Sys(4, 2, (long)b, at);
    Exit(1);
}
#define assert(x) do { if (!(x)) Fail(__LINE__); } while (0)
static void Log(const char*, ...) {}
static double HostSqrt(double x) {
    double r;
    __asm__("fsqrt" : "=t"(r) : "0"(x));
    return r;
}
#define WA_SQRT(x) HostSqrt(x)

static LONG InterlockedIncrement(volatile LONG* p) { return ++*p; }
static LONG InterlockedExchangeAdd(volatile LONG* p, LONG v) { const LONG o = *p; *p += v; return o; }
static LONGLONG InterlockedExchangeAdd64(volatile LONGLONG* p, LONGLONG v) { const LONGLONG o = *p; *p += v; return o; }
static LONGLONG g_fakeQpc;
static void QueryPerformanceCounter(LARGE_INTEGER* q) { q->QuadPart = (g_fakeQpc += 1000); }
static LARGE_INTEGER g_qpf = {1000000};
struct SRWLOCK { int held; };
#define SRWLOCK_INIT {0}
static bool TryAcquireSRWLockExclusive(SRWLOCK* l) { return l->held ? false : (l->held = 1, true); }
static void AcquireSRWLockExclusive(SRWLOCK* l) { assert(!l->held); l->held = 1; }
static void ReleaseSRWLockExclusive(SRWLOCK* l) { assert(l->held); l->held = 0; }

// what worldalt.cpp takes from the rest of ts3native.cpp
struct Budget { LONG capped; };
static Budget t_budget;
static LONG PccScopeNow(bool* worker) { return *worker = false, 0; }  // plancand.cpp: no plan session here

#include "worldalt.cpp"

// worldalt.cpp's allocator hook: a bump arena that starts over whenever nothing is held (the tests free everything
// between worlds; a graph block freed while tables live is simply not reused)
static u8 g_arena[96 << 20];
static size_t g_arenaAt;
static void* HostAlloc(size_t bytes) {
    if (!g_waWords) g_arenaAt = 0;
    bytes = (bytes + 15) & ~(size_t)15;
    if (g_arenaAt + bytes > sizeof(g_arena)) return NULL;
    void* p = g_arena + g_arenaAt;
    g_arenaAt += bytes;
    return p;
}

#define WAT_WORLDS 3000
#define WAT_QUERIES 24
#include "worldalt-test.h"

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {
    g_waAlloc = HostAlloc;
    TestWorldAlt();
    char b[200];
    int at = 0;
    const char* s1 = "world alt host test ok: bound checks ";
    while (*s1) b[at++] = *s1++;
    at += PutNum(b + at, g_watBoundChecks);
    const char* s2 = " (above half the true distance ";
    while (*s2) b[at++] = *s2++;
    at += PutNum(b + at, g_watTight);
    const char* s3 = "), searches with equal cost ";
    while (*s3) b[at++] = *s3++;
    at += PutNum(b + at, g_watEqual);
    const char* s4 = ", waypoints expanded plain ";
    while (*s4) b[at++] = *s4++;
    at += PutNum(b + at, g_watPopsPlain);
    const char* s5 = " / bound ";
    while (*s5) b[at++] = *s5++;
    at += PutNum(b + at, g_watPopsBound);
    b[at++] = '\n';
    Sys(4, 1, (long)b, at);
    Exit(0);
}
