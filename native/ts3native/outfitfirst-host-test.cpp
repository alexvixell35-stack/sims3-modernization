// docs/684 §9: host test of [OutfitFirst]'s table and choice (outfitfirst-core.h) without Windows, Wine or a libc: the
// Windows selftest's TestOutfitFirstCore as it is (outfitfirst-test.h).
// Build and run on Linux:
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/of-host native/ts3native/outfitfirst-host-test.cpp && /tmp/of-host; echo $?
//   0 = pass; 1 and "assert line N" (of outfitfirst-test.h) on stderr.
// Not covered here: the two call-site hooks and the install (they need the game's code); the Windows selftest runs the
// pick on a fake SimService (thumbstall-test.h), the in-game check is the "OK   outfit first" line and the "outfitfirst:" lines.
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

#include "outfitfirst-core.h"
#include "outfitfirst-test.h"

// the kernel enters with esp 16-byte aligned, not offset by a return address as the compiler assumes for a function
extern "C" __attribute__((force_align_arg_pointer)) void _start() {
    TestOutfitFirstCore();
    Exit(0);
}
