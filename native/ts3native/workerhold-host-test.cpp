// docs/673: host test of workerhold.cpp ([RouteJobPhases] HoldProbe: the job / plan / boundary hooks on its counters) and
// workerhold-core.h without Windows, Wine or a libc (workerhold-test.h is included as is). Build and run on Linux (32-bit):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/wh-host native/ts3native/workerhold-host-test.cpp && /tmp/wh-host; echo $?
//   0 = pass; else 1 and "assert line N" (of workerhold-test.h) on stderr.
// Not covered here: the install checks and the log lines (they need the ini and the game's process).
#define WORKERHOLD_HOST_TEST
#define __declspec(x)
#define NULL 0
typedef unsigned long DWORD;
typedef long LONG;
typedef long long LONGLONG;
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

static LONG InterlockedIncrement(volatile LONG* p) { return ++*p; }
static LONG InterlockedExchangeAdd(volatile LONG* p, LONG v) { const LONG o = *p; *p += v; return o; }
static LONGLONG InterlockedExchangeAdd64(volatile LONGLONG* p, LONGLONG v) { const LONGLONG o = *p; *p += v; return o; }
static LONGLONG g_fakeQpc;
static void QueryPerformanceCounter(LARGE_INTEGER* q) { q->QuadPart = (g_fakeQpc += 1000); }
static void QueryPerformanceFrequency(LARGE_INTEGER* q) { q->QuadPart = 1000000; }
static LARGE_INTEGER g_qpf;
static DWORD g_fakeTick = 5000;
static DWORD GetTickCount() { return g_fakeTick += 10; }
static void Sleep(DWORD) {}  // workerhold-test.h gives timed parts a real length on Windows; the fake clock steps on every read

// what workerhold.cpp takes from the rest of ts3native.cpp (plancand.cpp's site ids, the sim's +0x918 wait count)
enum { PCC_PH, PCC_CLOSEST, PCC_SAMELAND, PCC_SAMEWATER, PCC_CHOOSE, PCC_GATHER, PCC_SHORE, PCC_LOTSEARCH, PCC_CELL, PCC_LOTPLAN,
       PCC_WORLD, PCC_WATER, PCC_POST, PCC_INSERT, PCC_HOST, PCC_N };
static volatile LONG g_whSimWait;

#include "workerhold.cpp"
#include "workerhold-test.h"

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {  // the kernel enters with esp 16-aligned, not esp + 4
    TestWorkerHold();
    Exit(0);
}
