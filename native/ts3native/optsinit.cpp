// [OptsInit] (0.34.1, docs/94 §21.16, I-94-322): the planner's search options (a stack struct at 0x65E828, ctor 0x652BF0)
// leave the start and goal lot hub references +4 / +0x34 unset for a start or goal on open ground, and the world A* costs
// compare a node's hub with +0x34 before testing flag 0x20 (RouteCost_Mode1 0x674F7B; modes 3 and 4 0x67438C, 0x6745A6,
// 0x675989, 0x6759F0) [verified disassembly, steerro-test K8]. So an answer can depend on what an earlier call left on the
// stack, and a P4 shadow compares two stacks (the job's and the shadow's). Enabled=1 sets both references to 0 before the
// ctor runs, i.e. what K8's cleared stack gives (K8: all plans equal live vs copy); the ctor and the planner still set them
// for a start or goal on a lot. Default off: it changes the game's own answers where the stack held a hub (decision open).
// Enabled=2 (0.35.2, docs/94 §21.17): only inside a clone run (t_snapRun: P4 shadows and P5 runs), so the game's own plans
// keep their behaviour. The BP goal (1261.20, 68.38, 1758.27) gave the same game answer in all 8 runs on 0.31.2-0.34.3,
// with and without Enabled=1, while the copy took another door in 4 of 4 compares on 0.33.1+ without the patch and matched
// with it [measured]: the copy side's stack was the one holding a hub.
// Included by ts3native.cpp. One detour at the ctor's entry (5 bytes: xorps xmm0,xmm0; mov eax,ecx), thiscall, ecx = this.
static void* g_oiJmp;
static LONG g_oiMode = 1;  // 1 every call, 2 clone runs only (ApplyOptsInit; steerro-test K8 sets both)
static int __cdecl OiWant() {
    if (g_oiMode == 1) return 1;
    if (!t_snapRun) return 0;
    InterlockedIncrement(&ts3native_counters.oiClone);
    return 1;
}
static __declspec(naked) void OiStub() {
    __asm {
        push ecx
        call OiWant  // eax / edx: the ctor's own first instruction overwrites eax, thiscall passes nothing in edx
        pop ecx
        test eax, eax
        jz oi_skip
        mov dword ptr [ecx + 4], 0
        mov dword ptr [ecx + 34h], 0
    oi_skip:
        jmp dword ptr [g_oiJmp]
    }
}
static bool OiInstall() {
    if (g_oiJmp) return true;
    static const u8 kPro[] = {0x0F, 0x57, 0xC0, 0x8B, 0xC1};
    g_oiJmp = InstallDetour("RoutePlanOptions_ctor (I-94-322 hub refs)", TS3A(0x00652BF0), kPro, sizeof(kPro), (void*)OiStub, &g_oiJmp);
    return g_oiJmp != NULL;
}
static bool ApplyOptsInit(const wchar_t* ini) {
    const int mode = GetPrivateProfileIntW(L"OptsInit", L"Enabled", 0, ini);
    if (mode != 1 && mode != 2) return false;
    g_oiMode = mode;
    if (!OiInstall()) { Log("FAIL OptsInit: ctor 0x00652BF0 bytes differ, nothing patched"); return false; }
    Log("OK   OptsInit: search options hub references +4 / +0x34 start at 0 (I-94-322)%s",
        mode == 2 ? " in clone runs only (Enabled=2)" : "");
    return true;
}
