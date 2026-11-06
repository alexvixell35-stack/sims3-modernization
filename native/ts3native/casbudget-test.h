// --- [CasFrameBudget] (casbudget.cpp, docs/534): layout, the prologue bytes, the hook on a fake SimService with a fake
// trampoline and a fake clock: budget set during the call only in live play, both fields put back, foreign writes kept.
// Included by selftest.cpp.
namespace cfbt {
static u8 ss[0x140];
static LONG calls;
static DWORD seenSlice, seenBudget, argA, argB;
static int writeDuring;  // 1: the call writes +0x139 = 0 (script turned slicing off), 2: the call writes +0x2C
static void __fastcall Orig(void* self, void*, DWORD a, DWORD b) {
    u8* p = (u8*)self;
    calls++, seenSlice = p[0x139], seenBudget = *(DWORD*)(p + 0x2C), argA = a, argB = b;
    if (writeDuring == 1) p[0x139] = 0;
    if (writeDuring == 2) *(DWORD*)(p + 0x2C) = 77;
}
}  // namespace cfbt

static void TestCasFrameBudget() {
    using namespace cfbt;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, cfbMs) == 0x5CC4 && offsetof(Ts3nCounters, cfbCalls) == 0x5CC8 &&
                      offsetof(Ts3nCounters, cfbHits) == 0x5CCC && offsetof(Ts3nCounters, cfbForeign) == 0x5CD0 &&
                      sizeof(Ts3nCounters) == 5941 * 4,
                  "counters v912 layout after the v908-v911 gap (docs/registry.md)");
    assert(k.version == 912);
    // the live prologue (capstone on the Steam 1.67 TS3W.exe): push ebp; mov ebp,esp; and esp,-16; sub esp,0x94
    static const u8 live[12] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0x81, 0xEC, 0x94, 0x00, 0x00, 0x00};
    assert(kCfbAt == 0x005F0E50 && Matches(live, kCfbPro, sizeof(kCfbPro)));

    const CfbUpdateFn savedTramp = g_cfbTramp;
    const volatile DWORD* const savedPause = g_cfbPause;
    const LONG savedMs = k.cfbMs, savedLogged = g_cfbScaleLogged;
    static volatile DWORD clockPause;
    g_cfbTramp = Orig, g_cfbPause = &clockPause, g_cfbScaleLogged = 1;
    k.cfbCalls = k.cfbHits = k.cfbForeign = 0;
    memset(ss, 0, sizeof(ss));
    *(DWORD*)(ss + 0x2C) = 0x1234;

    // off (cfbMs 0): EA's unlimited step, nothing written, nothing counted
    k.cfbMs = 0;
    CfbHook(ss, NULL, 5, 6);
    assert(calls == 1 && seenSlice == 0 && seenBudget == 0x1234 && argA == 5 && argB == 6 && k.cfbCalls == 0);

    // live play: budget during the call, both fields back after
    k.cfbMs = 8, clockPause = 4;  // bit 4 (device lost / unfocused) does not matter
    CfbHook(ss, NULL, 7, 8);
    assert(calls == 2 && seenSlice == 1 && seenBudget == 8 && argA == 7 && argB == 8);
    assert(ss[0x139] == 0 && *(DWORD*)(ss + 0x2C) == 0x1234 && k.cfbCalls == 1 && k.cfbForeign == 0);

    // CAS / Build/Buy / load (script pause bit 1 set): EA's step unchanged
    clockPause = 1 | 4;
    CfbHook(ss, NULL, 0, 0);
    assert(calls == 3 && seenSlice == 0 && seenBudget == 0x1234 && k.cfbCalls == 1);

    // script slicing already on: its own budget stays
    clockPause = 0, ss[0x139] = 1;
    CfbHook(ss, NULL, 0, 0);
    assert(calls == 4 && seenSlice == 1 && seenBudget == 0x1234 && k.cfbCalls == 1 && ss[0x139] == 1);
    ss[0x139] = 0;

    // a write during the call is kept and counted; the other field still goes back
    writeDuring = 1;
    CfbHook(ss, NULL, 0, 0);
    assert(ss[0x139] == 0 && *(DWORD*)(ss + 0x2C) == 0x1234 && k.cfbForeign == 1 && k.cfbCalls == 2);
    writeDuring = 2;
    CfbHook(ss, NULL, 0, 0);
    assert(ss[0x139] == 0 && *(DWORD*)(ss + 0x2C) == 77 && k.cfbForeign == 2 && k.cfbCalls == 3);
    writeDuring = 0;

    // a call slower than the budget counts as a budget-hit frame (1 ms budget, the fake sleeps 20 ms)
    struct Slow { static void __fastcall F(void* self, void* edx, DWORD a, DWORD b) { Orig(self, edx, a, b); Sleep(20); } };
    g_cfbTramp = Slow::F, k.cfbMs = 1, k.cfbHits = 0;
    CfbHook(ss, NULL, 0, 0);
    assert(k.cfbHits == 1 && seenBudget == 1 && *(DWORD*)(ss + 0x2C) == 77);

    g_cfbTramp = savedTramp, g_cfbPause = savedPause, k.cfbMs = savedMs, g_cfbScaleLogged = savedLogged;
    k.cfbCalls = k.cfbHits = k.cfbForeign = 0;
    printf("CasFrameBudget: budget set in live play only, fields restored, foreign writes kept, slow call counted: ok\n");
}
