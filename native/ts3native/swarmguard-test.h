// docs/678: [SwarmGuard] on fakes. TestSwarmGuardCore: the decisions (swarmguard-core.h). TestSwarmGuardStub (Windows
// selftest only): the call-site stub of swarmguard.cpp against a fake update function. No timer in either.
#pragma once

static void TestSwarmGuardCore() {
    const SwgVt v = {0x1000, 0x2000, 0x3000};
    SwgStats st = SwgStats();
    unsigned long bw = 0;
    unsigned long good[8] = {0x1000, 0x2000, 0x3000, 0, 0, 2, 0, 0};
    unsigned long bad[8] = {0x1000, 0x3C55FD54, 0x3000, 0, 0, 2, 0, 0};
    unsigned long other[8] = {0x1004, 0x3C55FD54, 0x3000, 0, 0, 2, 0, 0};    // another class: never judged
    unsigned long dying[8] = {0x1000, 0x2000, 0x3004, 0, 0, 0, 0, 0};        // base vtable at +8: mid-destruction
    assert(SwgCheck(good, v) == SWG_OK && SwgCheck(bad, v) == SWG_BAD && SwgCheck(other, v) == SWG_FOREIGN && SwgCheck(dying, v) == SWG_FOREIGN);
    // skip mode: only the bad effect is skipped, nothing is written
    assert(SwgDecide(good, v, false, &st, &bw) == 0 && bw == 0);
    assert(SwgDecide(other, v, false, &st, &bw) == 0 && bw == 0 && other[1] == 0x3C55FD54);
    assert(SwgDecide(dying, v, false, &st, &bw) == 0 && bw == 0);
    assert(SwgDecide(bad, v, false, &st, &bw) == 1 && bw == 0x3C55FD54 && bad[1] == 0x3C55FD54);
    assert(SwgDecide(bad, v, false, &st, &bw) == 1);   // still bad next frame: skipped again
    assert(st.calls == 5 && st.bad == 2 && st.skipped == 2 && st.repaired == 0 && st.foreign == 2);
    // repair mode: the word is put back, the update runs, the next call is clean
    assert(SwgDecide(bad, v, true, &st, &bw) == 0 && bw == 0x3C55FD54 && bad[1] == 0x2000);
    assert(SwgDecide(bad, v, true, &st, &bw) == 0 && bw == 0);
    assert(st.calls == 7 && st.bad == 3 && st.skipped == 2 && st.repaired == 1);
    // a null word at +4 is bad too (the game would read address 0x44)
    unsigned long zero[8] = {0x1000, 0, 0x3000, 0, 0, 1, 0, 0};
    assert(SwgDecide(zero, v, false, &st, &bw) == 1 && bw == 0 && st.bad == 4);

    // scan: null entries and other classes are passed over; the first bad one is named; repair fixes all
    unsigned long bad2[8] = {0x1000, 0x68675704, 0x3000, 0, 0, 1, 0, 0};
    bad[1] = 0x3C55FD54;
    unsigned long* vec[6] = {good, nullptr, other, bad, dying, bad2};
    unsigned long* fb = nullptr;
    SwgStats s2 = SwgStats();
    assert(SwgScan(vec, vec + 6, v, false, &s2, &fb, &bw) == 2 && fb == bad && bw == 0x3C55FD54 && bad[1] == 0x3C55FD54);
    assert(SwgScan(vec, vec + 6, v, true, &s2, &fb, &bw) == 2 && fb == bad && bad[1] == 0x2000 && bad2[1] == 0x2000);
    assert(SwgScan(vec, vec + 6, v, true, &s2, &fb, &bw) == 0 && fb == nullptr && bw == 0);
    assert(SwgScan(vec, vec, v, true, &s2, &fb, &bw) == 0);   // empty vector
    assert(s2.scans == 4 && s2.scanBad == 4 && s2.repaired == 2 && other[1] == 0x3C55FD54);

    // the dump is logged once per (effect, word); the ring forgets after SWG_SEEN other pairs
    static SwgSeen ring[SWG_SEEN];
    unsigned next = 0;
    assert(SwgFirstSight(ring, &next, 0x100, 7) && !SwgFirstSight(ring, &next, 0x100, 7) && SwgFirstSight(ring, &next, 0x100, 8));
    for (unsigned long i = 0; i < SWG_SEEN; i++) assert(SwgFirstSight(ring, &next, 0x200 + i, 1));
    assert(SwgFirstSight(ring, &next, 0x100, 7));

    // the birth ring: the newest entry for an address wins; the block's words are copied; old entries fall out
    static SwgBirth births[SWG_BIRTHS];
    unsigned bn = 0;
    const unsigned long raw1[SWG_BIRTH_WORDS] = {0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18};
    const unsigned long raw2[SWG_BIRTH_WORDS] = {0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28};
    assert(SwgBirthFind(births, bn, 0x5000) == nullptr);
    SwgBirthPut(births, &bn, 0x5000, 100, 7, raw1);
    SwgBirthPut(births, &bn, 0x6000, 110, 7, raw2);
    const SwgBirth* fb1 = SwgBirthFind(births, bn, 0x5000);
    assert(fb1 && fb1->tick == 100 && fb1->tid == 7 && fb1->seq == 1 && fb1->w[0] == 0x11 && fb1->w[7] == 0x18);
    SwgBirthPut(births, &bn, 0x5000, 120, 9, raw2);   // the address is allocated again: the newer record is found
    fb1 = SwgBirthFind(births, bn, 0x5000);
    assert(fb1 && fb1->tick == 120 && fb1->tid == 9 && fb1->seq == 3 && fb1->w[1] == 0x22);
    assert(SwgBirthFind(births, bn, 0x7000) == nullptr);
    for (unsigned long i = 0; i < SWG_BIRTHS; i++) SwgBirthPut(births, &bn, 0x100000 + i * 0x280, 200, 7, nullptr);
    assert(SwgBirthFind(births, bn, 0x5000) == nullptr && SwgBirthFind(births, bn, 0x6000) == nullptr);
    fb1 = SwgBirthFind(births, bn, 0x100000);
    assert(fb1 && fb1->w[0] == 0 && fb1->seq == 4);
    // the nearest vtable word below a pointer: 4-aligned, inside [lo, hi); heap words and odd values are passed over
    const unsigned long mem[8] = {0x01040000, 0x3C000004, 0x01041001, 0x01042000, 0x3BADF7E4, 0x00FF0000, 0x77000000, 0x5};
    unsigned long vt = 0;
    assert(SwgVtBefore(mem + 5, 5, 0x01000000, 0x01100000, &vt) == 8 && vt == 0x01042000);   // the word before is heap
    assert(SwgVtBefore(mem + 3, 3, 0x01000000, 0x01100000, &vt) == 12 && vt == 0x01040000);  // 0x01041001 is not aligned
    assert(SwgVtBefore(mem + 7, 2, 0x01000000, 0x01100000, &vt) == 0 && vt == 0);            // none within two words
    assert(SwgVtBefore(mem + 7, 0, 0x01000000, 0x01100000, &vt) == 0);
    printf("ok SwarmGuard core\n");
}

#ifndef SWG_HOST_TEST
static DWORD g_swgtEcx, g_swgtRan, g_swgtOut[3], g_swgtEsp0, g_swgtEsp1, g_swgtArg;
// the fake update: thiscall, no stack arguments, plain ret (as 0x92E0C0)
static __declspec(naked) void SwgtFakeUpdate() {
    __asm mov dword ptr [g_swgtEcx], ecx __asm inc dword ptr [g_swgtRan] __asm mov eax, 0x77777777 __asm ret
}
// a call site like 0x91BC08: ebx = group, edi = effect, `mov ecx, edi` / `call`
static void SwgtCall() {
    __asm {
        pushad
        mov g_swgtEsp0, esp
        mov ebx, 0
        mov esi, 0x51515151
        mov edi, g_swgtArg
        mov ecx, edi
        call SwgStub
        mov dword ptr [g_swgtOut], ebx
        mov dword ptr [g_swgtOut + 4], esi
        mov dword ptr [g_swgtOut + 8], edi
        mov g_swgtEsp1, esp
        mov esp, g_swgtEsp0
        popad
    }
}
// the fake allocator: cdecl, six stack arguments (as 0x4E3F90)
static unsigned long g_swgtBlock[SWG_BIRTH_WORDS];
static DWORD g_swgtAllocSum, g_swgtAllocNull;
static void* __cdecl SwgtFakeAlloc(DWORD a, DWORD b, DWORD c, DWORD d, DWORD e, DWORD f) {
    g_swgtAllocSum = a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f;
    return g_swgtAllocNull ? nullptr : (void*)g_swgtBlock;
}
static void TestSwarmGuardStub() {
    const SwgVt keepVt = g_swgVt;
    const DWORD keepTarget = g_swgTarget;
    const LONG keepRepair = g_swgk.repair, keepScan = g_swgk.scan;
    const SwgStats keepSt = g_swgSt;
    g_swgVt.vt0 = 0x1000; g_swgVt.vt4 = 0x2000; g_swgVt.vt8 = 0x3000;
    g_swgTarget = (DWORD)(uintptr_t)SwgtFakeUpdate;
    g_swgk.repair = 0; g_swgk.scan = 0;
    g_swgSt = SwgStats();
    static unsigned long good[0x48 / 4], bad[0x48 / 4];
    good[0] = 0x1000; good[1] = 0x2000; good[2] = 0x3000;
    bad[0] = 0x1000; bad[1] = 0; bad[2] = 0x3000;   // word 0: the dump reads nothing through it
    // a good effect: the game's update runs with ecx = the effect; the caller's ebx / esi / edi / esp are its own
    g_swgtRan = 0; g_swgtEcx = 0; g_swgtArg = (DWORD)(uintptr_t)good;
    SwgtCall();
    assert(g_swgtRan == 1 && g_swgtEcx == (DWORD)(uintptr_t)good && g_swgtEsp1 == g_swgtEsp0);
    assert(g_swgtOut[0] == 0 && g_swgtOut[1] == 0x51515151 && g_swgtOut[2] == (DWORD)(uintptr_t)good);
    // a bad effect, skip mode: the update does not run, the stack and registers are the same, nothing is written
    g_swgtArg = (DWORD)(uintptr_t)bad;
    SwgtCall();
    assert(g_swgtRan == 1 && g_swgtEsp1 == g_swgtEsp0 && bad[1] == 0 && g_swgSt.skipped == 1);
    assert(g_swgtOut[0] == 0 && g_swgtOut[1] == 0x51515151 && g_swgtOut[2] == (DWORD)(uintptr_t)bad);
    // repair mode: the word is put back and the update runs
    g_swgk.repair = 1;
    SwgtCall();
    assert(g_swgtRan == 2 && g_swgtEcx == (DWORD)(uintptr_t)bad && bad[1] == 0x2000 && g_swgSt.repaired == 1 && g_swgtEsp1 == g_swgtEsp0);
    // Scan: every group is scanned when its turn comes, not only the first group of the tick (UpdateFrame walks the groups
    // in turn within one tick); a group's own effects in a row share one scan per tick
    {
        g_swgk.scan = 1;
        static unsigned long* gv[2] = {good, good};
        static unsigned long grpA[0x38 / 4], grpB[0x38 / 4];
        grpA[0x30 / 4] = grpB[0x30 / 4] = (unsigned long)(uintptr_t)gv;
        grpA[0x34 / 4] = grpB[0x34 / 4] = (unsigned long)(uintptr_t)(gv + 2);
        const long s0 = g_swgSt.scans;
        assert(SwgEnter(good, grpA) == 0 && g_swgSt.scans >= s0 + 1);
        const long s1 = g_swgSt.scans;
        assert(SwgEnter(good, grpB) == 0 && g_swgSt.scans == s1 + 1);  // another group: its own scan whatever the tick
        const long s2 = g_swgSt.scans;
        assert(SwgEnter(good, grpA) == 0 && g_swgSt.scans == s2 + 1);
        assert(SwgEnter(good, nullptr) == 0 && g_swgSt.scans == s2 + 1);  // no group: nothing to scan
        g_swgk.scan = 0;
        g_swgScanGroup = nullptr;
    }
    // the allocation stub: the six arguments reach the allocator in order, its block comes back, the block is recorded
    // with the words it held; a failed allocation (null) is passed on and not recorded
    const DWORD keepAlloc = g_swgAllocTarget;
    const unsigned keepNext = (unsigned)g_swgBirthNext;
    typedef void* (__cdecl * SwgtAllocFn)(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
    g_swgAllocTarget = (DWORD)(uintptr_t)SwgtFakeAlloc;
    g_swgtBlock[0] = 0xAAAA0001; g_swgtBlock[1] = 0xAAAA0002; g_swgtBlock[7] = 0xAAAA0008;
    g_swgtAllocSum = 0; g_swgtAllocNull = 0;
    void* blk = ((SwgtAllocFn)(void*)SwgAllocStub)(0x280, 0x10, 0x200, 0x3000, 0x40000, 0x500000);
    assert(blk == g_swgtBlock && g_swgtAllocSum == 0x280 + 2 * 0x10 + 3 * 0x200 + 4 * 0x3000 + 5 * 0x40000 + 6 * 0x500000);
    const SwgBirth* nb = SwgBirthFind(g_swgBirth, (unsigned)g_swgBirthNext, (unsigned long)(uintptr_t)g_swgtBlock);
    assert(nb && nb->w[0] == 0xAAAA0001 && nb->w[1] == 0xAAAA0002 && nb->w[7] == 0xAAAA0008 && nb->tid == GetCurrentThreadId());
    assert(g_swgSt.births == 1 && (unsigned)g_swgBirthNext == keepNext + 1);
    g_swgtAllocNull = 1;
    blk = ((SwgtAllocFn)(void*)SwgAllocStub)(0x280, 0, 0, 0, 0, 0);
    assert(blk == nullptr && g_swgSt.births == 1 && (unsigned)g_swgBirthNext == keepNext + 1);
    g_swgBirth[keepNext % SWG_BIRTHS] = SwgBirth();
    g_swgBirthNext = (LONG)keepNext;
    g_swgAllocTarget = keepAlloc;
    g_swgVt = keepVt; g_swgTarget = keepTarget; g_swgk.repair = keepRepair; g_swgk.scan = keepScan; g_swgSt = keepSt;
    g_swgSeenNext = 0;
    for (int i = 0; i < SWG_SEEN; i++) g_swgSeen[i] = SwgSeen();
    printf("ok SwarmGuard stub\n");
}
#endif
