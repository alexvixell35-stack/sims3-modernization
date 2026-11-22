// docs/676 §15: host test of [WorldPrecompute] SmoothCut (smoothcut.cpp's query hook and smoothcut-core.h) without
// Windows, Wine or a libc: smoothcut-test.h is included as is. Build and run on Linux (32-bit, as the game's pointers are):
//   clang++ -m32 -msse2 -mfpmath=sse -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static \
//     -Wno-everything -o /tmp/sc-host native/ts3native/smoothcut-host-test.cpp && /tmp/sc-host; echo $?
//   0 = pass; else 1 and "assert line N" (of smoothcut-test.h) on stderr.
// Not covered here: the install checks (they need the game's code).
#define SMOOTHCUT_HOST_TEST
#define __declspec(x)
#define __fastcall __attribute__((fastcall))
#define NULL 0
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
#define assert(x) do { if (!(x)) Fail(__LINE__); } while (0)
static int printf(const char*, ...) { return 0; }
static void Log(const char*, ...) {}

static LONG InterlockedIncrement(volatile LONG* p) { return ++*p; }
static LONGLONG InterlockedExchangeAdd64(volatile LONGLONG* p, LONGLONG v) { const LONGLONG o = *p; *p += v; return o; }
static LONGLONG g_fakeQpc;
static void QueryPerformanceCounter(LARGE_INTEGER* q) { q->QuadPart = (g_fakeQpc += 1000); }
static LARGE_INTEGER g_qpf = {1000000};
static char g_fakeHeap[512 << 10] __attribute__((aligned(16)));
static int g_fakeAllocs;
static void* VirtualAlloc(void*, unsigned int size, int, int) { return !g_fakeAllocs++ && size <= sizeof(g_fakeHeap) ? g_fakeHeap : NULL; }
static DWORD GetCurrentThreadId() { return 1; }

#include "smoothcut.cpp"
#include "smoothcut-test.h"

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {
    TestSmoothCut();
    Exit(0);
}
