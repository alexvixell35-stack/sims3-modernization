// docs/674: host test of doorshare.cpp ([RouteSnap] DoorShare) and doorshare-core.h without Windows, Wine or a libc
// (doorshare-test.h is included as is). Build and run on Linux (32-bit):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/ds-host native/ts3native/doorshare-host-test.cpp && /tmp/ds-host; echo $?
//   0 = pass; else 1 and "assert line N" (of doorshare-test.h) on stderr.
// Not covered here: the door stub around DsBefore / DsAfter, the ini read and the log lines.
#define DOORSHARE_HOST_TEST
#define __declspec(x)
#define NULL 0
typedef unsigned long DWORD;
typedef long LONG;
typedef long long LONGLONG;
typedef unsigned char u8;
union LARGE_INTEGER { LONGLONG QuadPart; };

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
// 64-bit signed division for the 32-bit build (no libgcc): only the tick -> us conversions use it
extern "C" long long __divdi3(long long a, long long b) {
    const bool neg = (a < 0) != (b < 0);
    unsigned long long n = a < 0 ? 0ull - (unsigned long long)a : (unsigned long long)a, d = b < 0 ? 0ull - (unsigned long long)b : (unsigned long long)b, q = 0, r = 0;
    for (int i = 63; i >= 0; i--) {
        r = r << 1 | (n >> i & 1);
        if (r >= d) r -= d, q |= 1ull << i;
    }
    return neg ? -(long long)q : (long long)q;
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
static int printf(const char*, ...) { return 0; }
static void Log(const char*, ...) {}

static LONG InterlockedIncrement(volatile LONG* p) { return ++*p; }
static LONG InterlockedExchange(volatile LONG* p, LONG v) { const LONG o = *p; *p = v; return o; }
static LONGLONG InterlockedExchangeAdd64(volatile LONGLONG* p, LONGLONG v) { const LONGLONG o = *p; *p += v; return o; }
static LARGE_INTEGER g_qpf = {1000000};
static LONG Clamp(LONG v, LONG lo, LONG hi) { return v < lo ? lo : v > hi ? hi : v; }
static unsigned GetPrivateProfileIntW(const wchar_t*, const wchar_t*, int def, const wchar_t*) { return (unsigned)def; }

// what doorshare.cpp takes from the rest of ts3native.cpp
struct DmxScriptGen { volatile LONG gen, armed, bumps, unarmedAsks, missCold, missGen, missAge, notUnlocked, exempt; };
static DmxScriptGen ts3native_doorgen;
static LONG t_hcScope, t_dsBits, t_dsPlDepth, t_dsPlans;
static DWORD g_dsH;
static LONG g_dsBits;
static void DsClassSet(DWORD handle, LONG bits) { g_dsH = handle, g_dsBits = bits; }
static LONG DsClassTake(DWORD handle) {
    const LONG r = handle && g_dsH == handle ? g_dsBits : 0;
    g_dsH = 0;
    return r;
}
static bool HhExempt(bool hh) { return hh; }  // [Experimental] HouseholdToo off
static void DsIcBump(int ic, const DWORD* slot);
static void DsWevBump(DWORD id);

#include "doorshare.cpp"
#include "doorshare-test.h"

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {  // the kernel enters with esp 16-aligned, not esp + 4
    TestDoorShare();
    Exit(0);
}
