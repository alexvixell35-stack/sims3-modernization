// [ProbeP10] probe P10 (docs/87 §6), observe only: the sim thread's synchronous route plans and gate-10 waits split by the
// route IC they run in. Included by ts3native.cpp after locowin.cpp.
//
// The four ICs [verified prologues, Steam 1.67; docs/87 §2]: IC_Route_Plan 0x765160 (already our IcPlanHook when async is
// installed: it calls P10Enter itself), IC_Route_Replan 0x765490, IC_Route_PlanFromPoint 0x765240 and
// IC_Route_ReplanFromPosition 0x765550 (pass-through stubs here). Each sim-thread entry pushes a reader frame on the gate
// frame stack (routegate.cpp, not a gate frame) that sets t_p10 to the IC and puts the previous value back at the leave;
// a frame left without returning (managed exception) is dropped at the thread's next gate or IC entry (RgDrop), like
// every other frame. Readers of the tag: RgEnter at site 10 (P10_REACHED, and the P1b row 0x760BFF keyed by the IC),
// PlanLockedHook (P10_PLANNED, plan time). Leave: reached and not planned = an early return (empty accepted goal vector,
// the x/z bounds test, or the start-point builder stopping first; docs/87 §2).
// Yieldable share: CanYield() at entry (the running task's root interpreter state, ts3native.cpp). Whether the task
// belongs to a Sim (MayYield in RouteBackoff.cs) is managed state this probe does not read.
static void* g_p10Tramp[4];
static const u8 kP10Pro[4][6] = {
    {},                                    // IC_Route_Plan: IcPlanHook
    {0x8B, 0x0D, 0xC4, 0x40, 0x1D, 0x01},  // IC_Route_Replan: mov ecx,[0x11D40C4] (absolute operand, copies as is)
    {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0},  // IC_Route_PlanFromPoint: push ebp; mov ebp,esp; and esp,-16
    {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0},  // IC_Route_ReplanFromPosition: same
};
static const char* const kP10Name[4] = {"IC_Route_Plan", "IC_Route_Replan", "IC_Route_PlanFromPoint", "IC_Route_ReplanFromPosition"};

// slot = the IC's return-address slot ([esp] at its entry)
static void __cdecl P10Enter(int ic, DWORD* slot) {
    if (!g_p10On || !RgIsSim(GetCurrentThreadId())) return;
    RgThread& t = t_rg;
    RgDrop(t, slot);
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.p10Entries[ic]);
    if (ic && CanYield()) InterlockedIncrement(&k.p10CanYield[ic - 1]);
    const DWORD prev = t_p10;
    if (RgPush(t, slot, 1, RG_REL_P10, (void*)(uintptr_t)prev)) t_p10 = (DWORD)ic + 1;
}

static void P10Leave(DWORD prev) {
    const DWORD cur = t_p10;
    if ((cur & 0xFF) && (cur & P10_REACHED) && !(cur & P10_PLANNED)) InterlockedIncrement(&ts3native_counters.p10EarlyRet[(cur & 0xFF) - 1]);
    t_p10 = prev;
}

// PlanLockedHook with t_p10 set: frames below this call are dead (dropped first, so a stale tag is not charged)
static DWORD P10PlanBegin(const DWORD* slot, LARGE_INTEGER* t0) {
    RgDrop(t_rg, slot);
    const DWORD tag = t_p10 & 0xFF;
    if (tag) {
        t_p10 |= P10_PLANNED;
        QueryPerformanceCounter(t0);
    }
    return tag;
}

static void P10PlanEnd(DWORD tag, LARGE_INTEGER t0) {
    if (tag) InterlockedExchangeAdd(&ts3native_counters.p10PlanUs[tag - 1], UsSince(t0));
}

#define P10_STUB(i) static __declspec(naked) void P10Stub##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call P10Enter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_p10Tramp + i * 4] }
P10_STUB(1) P10_STUB(2) P10_STUB(3)
static void (*const kP10Stubs[4])() = {NULL, P10Stub1, P10Stub2, P10Stub3};

// [ProbeP10] Enabled=1: all three prologues are checked first (one differs: nothing installed)
static void InstallP10(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"ProbeP10", L"Enabled", 0, ini)) { Log("probe P10 off (Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady || !g_icPlanOrig) { Log("SKIP probe P10: needs [RouteGate] Enforce=1 with the async path"); return; }
    // an IC with an A10 collect detour (a10.cpp, installed first) tags itself: no stub there
    auto a10 = [&k](int i) { return (k.arReady >> (i - 1) & 1) != 0; };
    for (int i = 1; i < 4; i++)
        if (!a10(i) && !Matches((const void*)(uintptr_t)kP10Ic[i], kP10Pro[i], sizeof(kP10Pro[i]))) {
            Log("SKIP probe P10: %s 0x%08X bytes differ, nothing hooked", kP10Name[i], kP10Ic[i]);
            return;
        }
    for (int i = 1; i < 4; i++)
        if (!a10(i) && !(g_p10Tramp[i] = InstallDetour(kP10Name[i], kP10Ic[i], kP10Pro[i], sizeof(kP10Pro[i]), (void*)kP10Stubs[i], &g_p10Tramp[i]))) {
            Log("FAIL probe P10: %s detour (stubs installed so far stay pass-through)", kP10Name[i]);
            return;
        }
    g_p10On = 1;
    k.p10Ready |= 1;
    Log("OK   probe P10: route ICs 0x765160 (IcPlanHook) / 0x765490 / 0x765240 / 0x765550 tagged; gate-10 waits by IC in the "
        "P1b table (row 6)");
}
