// docs/679: host test of [WaypointSnap]'s memo and burst bookkeeping (wpsnap-core.h) against a brute-force nearest-
// waypoint reference on random point sets, long run, without Windows, Wine or a libc. Build and run on Linux (32-bit):
//   clang++ -m32 -O2 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -fuse-ld=lld \
//     -Wno-everything -o /tmp/wps-host native/ts3native/wpsnap-host-test.cpp && /tmp/wps-host; echo $?
//   0 = pass; else 1 and "assert line N" (of wpsnap-test.h) on stderr.
// Not covered here: the two call-site stubs, the install checks and the log lines (they need the game's process).
#define NULL 0
typedef unsigned int size_t;

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

#include "wpsnap-core.h"
#define WPST_ROUNDS 3000000
#include "wpsnap-test.h"

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {
    TestWpSnap();
    Exit(0);
}
