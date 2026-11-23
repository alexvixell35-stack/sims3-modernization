// docs/671: host test of [FglProf]'s tallies (fglprof-core.h) without Windows, Wine or a libc: the Windows selftest's
// TestFglProfCore as it is (fglprof-test.h).
// Build and run on Linux:
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/fp-host native/ts3native/fglprof-host-test.cpp && /tmp/fp-host; echo $?
//   0 = pass; 1 and "assert line N" (of fglprof-test.h) on stderr.
// Not covered here: the stubs and the install (they need the game's code); the in-game check is the "OK   fgl prof" line
// and the per-search lines of [FglProf] Enabled=1.
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

#include "fglprof-core.h"
#include "fglprof-test.h"

// the kernel enters with esp 16-byte aligned, not offset by a return address as the compiler assumes for a function
extern "C" __attribute__((force_align_arg_pointer)) void _start() {
    TestFglProfCore();
    Exit(0);
}
