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
    g_swgVt = keepVt; g_swgTarget = keepTarget; g_swgk.repair = keepRepair; g_swgk.scan = keepScan; g_swgSt = keepSt;
    g_swgSeenNext = 0;
    for (int i = 0; i < SWG_SEEN; i++) g_swgSeen[i] = SwgSeen();
    printf("ok SwarmGuard stub\n");
}
#endif
