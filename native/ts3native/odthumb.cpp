// [ThumbDrainCap] (docs/533, counters v913 td*), off by default. Included by ts3native.cpp (one translation unit).
// ObjectDesignerService_Update 0xB3A960 (main thread, under lock 0x106B874) calls ProcessThumbnailRequests 0xB373A0,
// which empties the whole thumbnail request map (+0x214, head +0x218, count +0x220) in one frame: no count cap, no
// deadline. Each pass erases the map's head first (0xB33670 at 0xB3744E), then builds that request's TextureBuilder.
// Opening a Buy/Build catalog page queues one request per swatch, so that frame pays for the whole page.
// The cap stops the loop after MaxPerFrame requests or BudgetUs microseconds; the rest stay in EA's map, in order, for
// the next frame's Update. At least one request runs per frame, so the queue always drains.
//
// Sites [verified, disassembly of the Steam TS3W.exe 1.67]:
//  1. 0xB3A9BF `call 0xB373A0` in Update (the only caller; no vtable or other reference): pointed at TdBeginStub,
//     which counts the drain and starts its budget, then jumps to 0xB373A0 (an entry detour there still runs).
//  2. 0xB37730 back-edge `cmp dword [edi+220h],0 / jne 0xB373D2` (13 bytes; also the target of the two release paths
//     at 0xB374A8 / 0xB374D1): TdBackStub. Empty map: EA's exit 0xB3773D. Otherwise TdMore decides: next request
//     (0xB373D2) or the same exit. esp and edi are the loop's own at both targets.
//  3. 0xB3ABC8 end of Update `xor ecx,ecx / lea edx,[esi+5Ch] / xchg [edx],ecx` clears the "work pending" flag +0x5C
//     (set by every request insert, read by 0xB32950: the script icall 0x787740 and the BlueprintHelper wait loop
//     0xC9FB50, which yields until the flag clears). TdClearStub keeps it 1 while requests are left, so those waiters
//     still wait for every request; without the cap the map is always empty here (lock held) and the flag clears as before.
// Not capped: presets 0xB34A50 (BlueprintHelper reads preset pattern data after that same wait; count first), hair
// swatches 0xB34750, large patterns 0xB37DA0 (one request per frame already).
static const DWORD TD_CALL = 0x00B3A9BF, TD_FN = 0x00B373A0, TD_BACK = 0x00B37730, TD_LOOP = 0x00B373D2,
                   TD_EXIT = 0x00B3773D, TD_CLEAR = 0x00B3ABC8, TD_CLEAR_BACK = 0x00B3ABCF;
static const u8 kTdCall[5] = {0xE8, 0xDC, 0xC9, 0xFF, 0xFF};
static const u8 kTdBack[13] = {0x83, 0xBF, 0x20, 0x02, 0x00, 0x00, 0x00, 0x0F, 0x85, 0x95, 0xFC, 0xFF, 0xFF};
static const u8 kTdClear[7] = {0x33, 0xC9, 0x8D, 0x56, 0x5C, 0x87, 0x0A};
static DWORD g_tdFn = TD_FN, g_tdLoop = TD_LOOP, g_tdExit = TD_EXIT, g_tdClearBack = TD_CLEAR_BACK;  // the stubs' jump targets
static LONG g_tdCap = 8, g_tdN;  // cap per drain (0 = no cap), requests done in this drain (main thread only)
static LONGLONG g_tdBudget, g_tdStart;  // QPC ticks (0 = no budget), drain start

// a drain starts: queue length at entry (EA's count +0x220)
static void __stdcall TdBegin(DWORD queued) {
    Ts3nCounters& k = ts3native_counters;
    g_tdN = 0;
    if (!queued) return;
    InterlockedIncrement(&k.tdDrains);
    Max(&k.tdMaxQueue, (LONG)queued);
    if (g_tdBudget) {
        LARGE_INTEGER t;
        QueryPerformanceCounter(&t);
        g_tdStart = t.QuadPart;
    }
}

// one request done and more are queued: 1 = take the next one, 0 = leave the rest for the next frame
static int __stdcall TdMore() {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.tdReqs);
    bool cut = g_tdCap && ++g_tdN >= g_tdCap;
    if (!cut && g_tdBudget) {
        LARGE_INTEGER t;
        QueryPerformanceCounter(&t);
        cut = t.QuadPart - g_tdStart >= g_tdBudget;
    }
    if (!cut) return 1;
    InterlockedIncrement(&k.tdCut);
    return 0;
}

static __declspec(naked) void TdBeginStub() {
    __asm {
        pushad
        push dword ptr [ecx + 0x220]
        call TdBegin
        popad
        jmp dword ptr [g_tdFn]
    }
}

// the last request of a drain is counted here too (map empty: straight to EA's exit)
static __declspec(naked) void TdBackStub() {
    __asm {
        cmp dword ptr [edi + 0x220], 0
        je last
        pushad
        call TdMore
        test eax, eax
        popad
        jz done
        jmp dword ptr [g_tdLoop]
    last:
        lock inc dword ptr [ts3native_counters.tdReqs]
    done:
        jmp dword ptr [g_tdExit]
    }
}

// EA's flag clear, with flag = (requests left) instead of 0
static __declspec(naked) void TdClearStub() {
    __asm {
        xor ecx, ecx
        cmp dword ptr [esi + 0x220], ecx
        setne cl
        lea edx, [esi + 0x5C]
        xchg dword ptr [edx], ecx
        jmp dword ptr [g_tdClearBack]
    }
}

static void TdRel(u8* b, u8 op, const u8* at, const void* to) {
    b[0] = op;
    *(LONG*)(b + 1) = (LONG)((uintptr_t)to - ((uintptr_t)at + 5));
}

// at[] = where the 3 sites live (the game, or dummies in the selftest). All three or none: a cap without the flag
// fix would let the BlueprintHelper wait end with requests left.
static bool TdPatch(u8* const at[3]) {
    if (!Matches(at[0], kTdCall, 5) || !Matches(at[1], kTdBack, 13) || !Matches(at[2], kTdClear, 7)) return false;
    u8 back[13], clear[7], call[5];
    memset(back, 0xCC, sizeof(back));
    TdRel(back, 0xE9, at[1], &TdBackStub);
    TdRel(clear, 0xE9, at[2], &TdClearStub);
    clear[5] = 0x66, clear[6] = 0x90;
    TdRel(call, 0xE8, at[0], &TdBeginStub);
    // a failed write puts back the sites already written, so a partial patch never stays live
    if (!Write(at[2], clear, 7)) return false;
    if (!Write(at[1], back, 13)) { Write(at[2], kTdClear, 7); return false; }
    if (!Write(at[0], call, 5)) { Write(at[1], kTdBack, 13); Write(at[2], kTdClear, 7); return false; }
    return true;
}

static bool ApplyThumbDrainCap(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"ThumbDrainCap", L"Enabled", 0, ini)) { Log("thumb drain cap off (Enabled=0)"); return false; }
    g_tdCap = (LONG)GetPrivateProfileIntW(L"ThumbDrainCap", L"MaxPerFrame", 8, ini);
    const LONG us = (LONG)GetPrivateProfileIntW(L"ThumbDrainCap", L"BudgetUs", 2000, ini);
    if (g_tdCap < 0) g_tdCap = 0;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_tdBudget = us > 0 ? f.QuadPart * us / 1000000 : 0;
    u8* const at[3] = {(u8*)(uintptr_t)TD_CALL, (u8*)(uintptr_t)TD_BACK, (u8*)(uintptr_t)TD_CLEAR};
    if (!TdPatch(at)) {
        k.tdOn = -1;
        Log("SKIP thumb drain cap: bytes at 0x%08X / 0x%08X / 0x%08X differ: not installed", TD_CALL, TD_BACK, TD_CLEAR);
        return false;
    }
    k.tdOn = 1;
    Log("OK   thumb drain cap (docs/533): ObjectDesigner thumbnail requests %ld per frame (0 = no cap), budget %ld us", g_tdCap, us);
    return false;
}
