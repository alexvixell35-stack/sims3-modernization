// --- docs/177 v102: [GoalGen] SameRing D1 (goalgen.cpp) and probe P-G ([RouteJobPhases] GoalSplit, routephases.cpp).
// Included by selftest.cpp after routephases-test.h.

// D1: a fake radial generator loop that calls the ring builder through GgThunk as 0x75C246 does: loop locals at the return
// slot + 0x44 (count +0x20, i +0x28, step +0x3C), 16 pushed arguments, ecx = generator; the fake builder pops them (ret 0x40)
// and answers its "validations" through GgVal at the real ring call's return address.
static volatile DWORD g_fkCount, g_fkI, g_fkStep, g_fkArg, g_fkEcx;
static int g_fkCalls, g_fkAnsN;
static DWORD g_fkRet = 0x00ABC123;  // the return address the fake validations report
static u8 g_fkAns[8];
static volatile DWORD g_fkEsp0, g_fkEsp1;
static void __cdecl FakeRingBody() {
    g_fkCalls++;
    for (int j = 0; j < g_fkAnsN; j++) GgVal(g_fkRet, g_fkAns[j]);
}
static __declspec(naked) void FakeRing() {
    __asm {
        pushad
        call FakeRingBody
        popad
        ret 0x40
    }
}
static __declspec(naked) void FakeRingLoop() {
    __asm {
        sub esp, 0x50
        mov eax, g_fkCount
        mov [esp + 0x20], eax
        mov eax, g_fkI
        mov [esp + 0x28], eax
        mov eax, g_fkStep
        mov [esp + 0x3C], eax
        push 0x16  // the 16th argument (first pushed): the first/last ring flag, may differ
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        push g_fkArg
        mov ecx, g_fkEcx
        call GgThunk
        add esp, 0x50
        ret
    }
}
static void Ring(DWORD count, DWORD i, DWORD stepBits) {
    g_fkCount = count;
    g_fkI = i;
    g_fkStep = stepBits;
    __asm mov g_fkEsp0, esp
    FakeRingLoop();
    __asm mov g_fkEsp1, esp
    assert(g_fkEsp0 == g_fkEsp1);  // skip and call paths both pop the 16 arguments
}

static void TestGoalGen() {
    static_assert(offsetof(Ts3nCounters, pgReady) == 0x4360 && offsetof(Ts3nCounters, pgValHist) == 0x4394 &&
                  offsetof(Ts3nCounters, pgLongRest) == 0x43B0 && offsetof(Ts3nCounters, ggMode) == 0x43B4 &&
                  offsetof(Ts3nCounters, ggValDup) == 0x43D4 && offsetof(Ts3nCounters, hxMode) == 0x43D8 &&
                  offsetof(Ts3nCounters, hxCalls) == 0x43E0 && offsetof(Ts3nCounters, hxStale) == 0x4408 &&
                  sizeof(Ts3nCounters) == 5933 * 4, "counters v102 layout after v101 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.pgReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pgReady));
    g_ggOrig = (void*)FakeRing;
    g_ggValRet = 0x00ABC123;
    g_fkArg = 0x1234;
    g_fkEcx = 0x77;
    static const u8 kYes[3] = {1, 0, 1};
    memcpy(g_fkAns, kYes, 3);
    g_fkAnsN = 3;
    // mode 0: both rings run, the duplicate counted
    k.ggMode = 0;
    Ring(1, 0, 0);
    Ring(1, 1, 0);
    assert(g_fkCalls == 2 && k.ggRings == 2 && k.ggDup == 1 && k.ggSkipped == 0 && t_gg.state == 0);
    // mode 1: the duplicate skipped; a ranged generator (count >= 2, step != 0) and a non-zero step untouched
    k.ggMode = 1;
    Ring(1, 0, 0);
    Ring(1, 1, 0);
    assert(g_fkCalls == 3 && k.ggDup == 2 && k.ggSkipped == 1);
    Ring(3, 1, 0x3F000000);
    Ring(1, 1, 0x3F000000);
    Ring(2, 1, 0);
    assert(g_fkCalls == 6 && k.ggDup == 2 && k.ggSkipped == 1);
    // mode 2 (shadow): both run; equal arguments and answers compare equal
    k.ggMode = 2;
    Ring(1, 0, 0);
    assert(t_gg.state == 2 && t_gg.n0 == 3);
    Ring(1, 1, 0);
    assert(g_fkCalls == 8 && t_gg.state == 0 && k.ggArgMismatch == 0 && k.ggValCmp == 1 && k.ggValMismatch == 0 && k.ggValDup == 3);
    // an argument that differs, and an answer that differs
    Ring(1, 0, 0);
    g_fkArg = 0x1235;
    g_fkAns[1] = 1;
    Ring(1, 1, 0);
    assert(k.ggArgMismatch == 1 && k.ggValCmp == 2 && k.ggValMismatch == 1 && k.ggValDup == 6);
    // answers from another call site (the facing point) are not ring answers; no answers at all = nothing compared
    g_fkRet = 0x00ABC124;
    g_fkArg = 0x1234;
    Ring(1, 0, 0);
    g_fkRet = 0x00ABC123;
    g_fkAnsN = 0;
    Ring(1, 1, 0);
    assert(k.ggValCmp == 2 && k.ggValMismatch == 1);
    // a duplicate without its ring 0 (state lost): passes through, no compare
    Ring(1, 1, 0);
    assert(k.ggArgMismatch == 1 && t_gg.state == 0);
    g_ggOrig = (void*)(uintptr_t)GG_RING;
    g_ggValRet = GG_VAL_RET;
    memset(&k.pgReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pgReady));
    printf("goal gen D1: duplicate ring detected from the loop locals, skipped with ret 0x40, shadow argument and answer compare ok\n");
}

// P-G: fake generator (push ebp; mov ebp,esp; and esp,-16) calls a fake validation (same prologue, al = 1, ret 0x20) 3 times
static void TestGoalSplit() {
    Ts3nCounters& k = ts3native_counters;
    memset(&k.pgReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pgReady));
    memset(&k.rjpReady, 0, offsetof(Ts3nCounters, rjpMaxUs) + sizeof(k.rjpMaxUs) - offsetof(Ts3nCounters, rjpReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    static const u8 kVal[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0x8B, 0xE5, 0x5D, 0xB0, 0x01, 0xC2, 0x20, 0x00};
    memcpy(code, kVal, sizeof(kVal));
    u8* g = code + 64;
    static const u8 kPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};
    memcpy(g, kPro, 6);
    u8* p = g + 6;
    DWORD firstRet = 0;
    for (int c = 0; c < 3; c++) {
        for (int a = 0; a < 8; a++) *p++ = 0x6A, *p++ = 0x00;  // push 0 (8 arguments)
        *p++ = 0xE8;
        *(LONG*)p = (LONG)(code - (p + 4));
        p += 4;
        if (!c) firstRet = (DWORD)(uintptr_t)p;
    }
    *p++ = 0x8B, *p++ = 0xE5, *p++ = 0x5D, *p++ = 0xC3;  // mov esp,ebp; pop ebp; ret
    assert(RjpInstallAt(RJS_GVAL, (DWORD)(uintptr_t)code, NULL) && RjpInstallAt(RJS_GGEN, (DWORD)(uintptr_t)g, NULL));
    typedef void(__cdecl * GenFn)();
    const GenFn gen = (GenFn)g;
    k.rjpReady = 1;
    k.pgReady = 9;
    gen();  // no job: straight through
    assert(k.pgN[0] == 0 && k.pgN[3] == 0);
    // a job: one generation frame, three validations (self time, nested), D1's answer feed at the first call's return address
    g_ggValRet = firstRet;
    t_gg.state = 1;
    t_gg.n0 = 0;
    const LONGLONG t0 = RjpNow();
    RjpJobBegin(0);
    gen();
    const LONG us = RjpUs(RjpNow() - t0);
    RjpJobEnd(us);
    assert(k.pgN[0] == 1 && k.pgN[3] == 3 && k.pgN[1] == 0 && k.pgValHist[1] == 1 && k.pgValMax == 3 && k.pgLongRest == 0);
    assert(t_gg.n0 == 1 && t_gg.ans[0] == 1 && t_rjp.n == 0 && k.rjpN[0][RJP_OTHER] == 1);
    assert(k.rjpUs[0][RJP_OTHER] + k.pgUs[0] + k.pgUs[3] <= us);  // OTHER no longer holds the P-G phases' self time
    // a job with no validation and a long rest: histogram bucket 0, counted as a long rest (logged)
    RjpJobBegin(1);
    RjpJobEnd(60000);
    assert(k.pgValHist[0] == 1 && k.pgLongRest == 1);
    t_gg.state = 0;
    g_ggValRet = GG_VAL_RET;
    VirtualFree(code, 0, MEM_RELEASE);
    memset(&k.pgReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pgReady));
    memset(&k.rjpReady, 0, offsetof(Ts3nCounters, rjpMaxUs) + sizeof(k.rjpMaxUs) - offsetof(Ts3nCounters, rjpReady));
    printf("route phases goal split (P-G): worker-only frames, validations per job, rest-of-job split and long-rest count ok\n");
}

static void __cdecl GsCallRet20(const void* fn) {  // 8 zero arguments, callee cleanup (ret 0x20)
    __asm {
        push 0
        push 0
        push 0
        push 0
        push 0
        push 0
        push 0
        push 0
        call fn
    }
}

// docs/178 T3 P-GS: a fake goal step (the game's prologue) calls a fake obstacle query (same prologue, ret 0x20) twice; the
// query is timed only inside the goal step of a worker job, its time leaves the goal step's self time; a goal step >= 50 ms is
// counted (logged). docs/178 T1: the per-phase line of a foreign job >= 300 ms
static void TestGoalStepSplit() {
    Ts3nCounters& k = ts3native_counters;
    memset(&k.pgReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pgReady));
    memset(&k.rjpReady, 0, offsetof(Ts3nCounters, rjpMaxUs) + sizeof(k.rjpMaxUs) - offsetof(Ts3nCounters, rjpReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    // obstacle query dummy: push ebp; mov ebp,esp; and esp,-16; mov esp,ebp; pop ebp; mov al,1; ret 20h
    static const u8 kQ[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0x8B, 0xE5, 0x5D, 0xB0, 0x01, 0xC2, 0x20, 0x00};
    memcpy(code, kQ, sizeof(kQ));
    u8* g = code + 64;  // goal step dummy: prologue, two query calls with 8 pushed zeros each, epilogue
    static const u8 kPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};
    memcpy(g, kPro, 6);
    u8* p = g + 6;
    for (int c = 0; c < 2; c++) {
        for (int a = 0; a < 8; a++) *p++ = 0x6A, *p++ = 0x00;
        *p++ = 0xE8;
        *(LONG*)p = (LONG)(code - (p + 4));
        p += 4;
    }
    *p++ = 0x8B, *p++ = 0xE5, *p++ = 0x5D, *p++ = 0xC3;
    const int qs = RJS_GS0 + 4;
    assert(RjpInstallAt(qs, (DWORD)(uintptr_t)code, NULL) && RjpInstallAt(RJS_GSTEP, (DWORD)(uintptr_t)g, NULL));
    typedef void(__cdecl * StepFn)();
    k.rjpReady = 1, k.pgReady = 2, k.gsReady = 0x10;
    ((StepFn)g)();  // no job: straight through
    assert(k.gsN[4] == 0 && k.gsSteps == 0);
    RjpJobBegin(0);
    GsCallRet20(code);  // in a job, outside a goal step: not a P-GS frame
    assert(t_rjp.n == 0 && k.gsN[4] == 0);
    ((StepFn)g)();
    RjpJobEnd(1000);
    assert(k.gsN[4] == 2 && k.gsSteps == 1 && k.gsLong == 0 && k.pgN[1] == 1 && t_rjp.n == 0);
    for (int i = 0; i < RJP_NALL; i++) assert(t_rjp.gsPh[i] == 0 && t_rjp.gsPhN[i] == 0 && (i >= 8 || i == 4 || k.gsN[i] == 0));
    // docs/178 §6.5: known nested timings in a 100 ms goal step: portal search (P-GS) 80 ms inclusive with a 70 ms cell A*
    // inside. Per step: cell 70 (a v87 phase, its own row), portal search self 10, step's own code 20 = unattributed; the
    // split adds up to the step (0.36.31 showed only the sites' self time and divided by sites + step self)
    RjpJobBegin(0);
    const LONGLONG ms = g_qpf.QuadPart / 1000, now0 = RjpNow();
    DWORD stk[16];
    const LONG pgStep0 = k.pgUs[1], gsLong0 = k.gsLong;
    assert(RjpPush(RJP_GSTEP, &stk[12], false));
    t_rjp.f[t_rjp.n - 1].t0 = now0 - 100 * ms;
    assert(RjpPush(RJP_GS0 + 2, &stk[8], false));
    t_rjp.f[t_rjp.n - 1].t0 = now0 - 80 * ms;
    assert(RjpPush(RJP_CELL, &stk[4], false));
    t_rjp.f[t_rjp.n - 1].t0 = now0 - 70 * ms;
    RjpPop(&stk[4]);
    RjpPop(&stk[8]);
    const LONG cell = t_rjp.gsPh[RJP_CELL], site = t_rjp.gsPh[RJP_GS0 + 2];
    LONG in = 0;
    for (int i = 0; i < RJP_NALL; i++) in += t_rjp.gsPh[i];
    assert(cell >= 70000 && cell < 72000 && site >= 9000 && site < 11000 && in == cell + site);
    assert(t_rjp.gsPhN[RJP_CELL] == 1 && t_rjp.gsPhN[RJP_GS0 + 2] == 1);
    RjpPop(&stk[12]);
    const LONG own = k.pgUs[1] - pgStep0;  // the step's self = unattributed
    assert(own >= 19000 && own < 21000 && in + own >= 100000 && in + own < 102000 && k.gsLong == gsLong0 + 1);
    for (int i = 0; i < RJP_NALL; i++) assert(t_rjp.gsPh[i] == 0 && t_rjp.gsPhN[i] == 0);  // reset per step
    RjpJobEnd(1000);
    // T1 line: a foreign job (kind 2) >= 300 ms with the line on; an own job or a shorter one: none
    k.fbjdReady = 4;
    RjpJobBegin(2);
    RjpJobEnd(400000);
    RjpJobBegin(0);
    RjpJobEnd(400000);
    RjpJobBegin(2);
    RjpJobEnd(299000);
    assert(k.fbjdLines == 1 && t_rjp.jobUs[RJP_OTHER] > 0);
    VirtualFree(code, 0, MEM_RELEASE);
    memset(&k.pgReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pgReady));
    memset(&k.rjpReady, 0, offsetof(Ts3nCounters, rjpMaxUs) + sizeof(k.rjpMaxUs) - offsetof(Ts3nCounters, rjpReady));
    printf("route phases goal step split (P-GS, docs/178 T3): frames only inside a worker job's goal step, per-step split and "
           "long-step count; T1 foreign long-job phase line ok\n");
}
