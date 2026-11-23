// docs/663: host test of routespeed-core.h (the NeighbourMemo table) without Windows, Wine or a libc. Build and run on Linux:
//   clang++ -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -static -o /tmp/rs-host native/ts3native/routespeed-host-test.cpp
//   /tmp/rs-host; echo $?   (0 = pass, else the number of the failed check)
// The hook itself (references, release, scope, shadow) is covered by routespeed-test.h in the Windows selftest.
#include "routespeed-core.h"

extern "C" void* memset(void* d, int c, unsigned long n) {
    unsigned char* p = (unsigned char*)d;
    while (n--) *p++ = (unsigned char)c;
    return d;
}
[[noreturn]] static void Exit(long code) {
    __asm__ volatile("syscall" ::"a"(60L), "D"(code) : "rcx", "r11", "memory");
    __builtin_unreachable();
}
static int g_check;
#define CHECK(x) do { ++g_check; if (!(x)) Exit(g_check); } while (0)

static NmMemo m;
static uintptr_t big[NM_MAXN + 1];

extern "C" [[noreturn]] void _start() {
    CHECK(NmSync(&m, 1, 1) && !NmSync(&m, 1, 1) && NmSync(&m, 2, 1) && NmSync(&m, 2, 2));
    const uintptr_t l[3] = {0x100, 0x200, 0x300}, l2[3] = {0x100, 0x300, 0x200}, withNull[2] = {0x100, 0};
    CHECK(NmStore(&m, 0x10, 0x1000, 7, 8, l, 3) == NM_STORED && m.entries == 1 && m.used == 3);
    const NmSlot* s = NmFind(&m, 0x10, 0x1000, 7, 8);
    CHECK(s && s->n == 3 && NmSame(&m, s, l, 3) && !NmSame(&m, s, l2, 3) && !NmSame(&m, s, l, 2));
    CHECK(!NmFind(&m, 0x11, 0x1000, 7, 8) && !NmFind(&m, 0x10, 0x1000, 7, 9) && !NmFind(&m, 0x10, 0x1004, 7, 8));
    CHECK(NmStore(&m, 0x10, 0x1000, 7, 8, l, 3) == NM_STORED && m.entries == 1);
    CHECK(NmStore(&m, 0x10, 0x2000, 1, 1, withNull, 2) == NM_NULLCELL);
    for (int i = 0; i <= NM_MAXN; i++) big[i] = 0x10 + i;
    CHECK(NmStore(&m, 0x10, 0x3000, 1, 1, big, NM_MAXN + 1) == NM_LONG);
    CHECK(NmSync(&m, 3, 2) && !NmFind(&m, 0x10, 0x1000, 7, 8) && m.used == 0);
    m.gen = 0xFFFFFFFF, m.slot[5].gen = 1;
    NmReset(&m);
    CHECK(m.gen == 1 && m.slot[5].gen == 0);
    // pool cap
    memset(&m, 0, sizeof(m));
    NmSync(&m, 1, 1);
    int stored = 0, full = 0;
    for (uint32_t c = 1; c <= NM_POOL / NM_MAXN + 4; c++) {
        const int r = NmStore(&m, 0x10, c * 0x40, c, c, big, NM_MAXN);
        if (r == NM_STORED) stored++; else if (r == NM_POOLFULL) full++;
    }
    CHECK(stored == NM_POOL / NM_MAXN && full == 4 && m.used == NM_POOL);
    // every stored key still found with its own list (no probe-run loss)
    for (uint32_t c = 1; c <= (uint32_t)stored; c++) CHECK(NmFind(&m, 0x10, c * 0x40, c, c));
    // probe cap: 9 keys on one home slot -> the 9th is refused
    memset(&m, 0, sizeof(m));
    NmSync(&m, 1, 1);
    const uint32_t home = NmHash(0x10, 0x40);
    int same = 0, tableFull = 0;
    for (uintptr_t cell = 0x40; same < NM_PROBE + 1; cell += 4)
        if (NmHash(0x10, cell) == home) {
            same++;
            const int r = NmStore(&m, 0x10, cell, 0, 0, l, 1);
            if (same > NM_PROBE) tableFull = r == NM_TABLEFULL;
            else if (r != NM_STORED) Exit(100);
        }
    CHECK(tableFull);
    CHECK(NmServeRule(1, true, true, 4, 3) == 1 && NmServeRule(2, true, true, 4, 3) == 0 && NmServeRule(1, false, true, 4, 3) == 0);
    CHECK(NmServeRule(1, true, false, 4, 3) == 0 && NmServeRule(1, true, true, 2, 3) == 0 && NmServeRule(0, true, true, 4, 3) == 0);
    Exit(0);
}
