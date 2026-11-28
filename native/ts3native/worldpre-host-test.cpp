// docs/676: host test of [WorldPrecompute] (worldpre.cpp, worldpre-core.h) and the water jump's straight-scan table
// (waterjps-core.h) without Windows, Wine or a libc. worldpre-test.h is included as is, with the long run switched on.
// Build and run on Linux (32-bit), about 40 s:
//   clang++ -m32 -O2 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -fuse-ld=lld \
//     -Wno-everything -o /tmp/wx-host native/ts3native/worldpre-host-test.cpp && /tmp/wx-host; echo $?
//   0 = pass; else 1 and "assert line N" (of worldpre-test.h) on stderr.
// Not covered here: the install checks, the post pass stubs' assembly and the log lines (they need the game's process),
// and WaterJumpHook's own lines around the gate (waterjps.cpp; the Windows selftest and waterjps-test.cpp build them).
#define WORLDPRE_HOST_TEST
#define NULL 0
#define __fastcall __attribute__((fastcall))
#define __cdecl
#define __declspec(x)
typedef unsigned long DWORD;
typedef long LONG;
typedef long long LONGLONG;
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
static void Log(const char*, ...) {}

static LONG InterlockedIncrement(volatile LONG* p) { return ++*p; }
static LONG InterlockedExchangeAdd(volatile LONG* p, LONG v) { const LONG o = *p; *p += v; return o; }
static LONGLONG InterlockedIncrement64(volatile LONGLONG* p) { return ++*p; }
static LONGLONG InterlockedExchangeAdd64(volatile LONGLONG* p, LONGLONG v) { const LONGLONG o = *p; *p += v; return o; }
static LONGLONG g_fakeQpc;
static void QueryPerformanceCounter(LARGE_INTEGER* q) { q->QuadPart = (g_fakeQpc += 1000); }
static LARGE_INTEGER g_qpf = {1000000};
// an SRW lock as one flag (one thread here): a second exclusive try fails, as on Windows
struct SRWLOCK { int held; };
#define SRWLOCK_INIT {0}
static bool TryAcquireSRWLockExclusive(SRWLOCK* l) { return l->held ? false : (l->held = 1, true); }
static void AcquireSRWLockExclusive(SRWLOCK* l) { assert(!l->held); l->held = 1; }
static void ReleaseSRWLockExclusive(SRWLOCK* l) { assert(l->held); l->held = 0; }

// what worldpre.cpp takes from the rest of ts3native.cpp
static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
static LONG PccScopeNow(bool* worker) { return *worker = false, 0; }  // plancand.cpp
static volatile LONG g_snWriteSeq, g_snOn;                            // routesnap.cpp

#include "waterjps-core.h"
#include "worldpre.cpp"
#define WXT_GRIDS 260
#define WXT_SAMPLES 250
#define WXT_BIGSEA true
#include "worldpre-test.h"

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {
    TestWorldPre();
    Exit(0);
}
