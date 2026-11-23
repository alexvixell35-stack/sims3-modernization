// [Startup] DxDiagCache (docs/284; default 0). Included by ts3native.cpp (needs Log, Matches, Write5, Readable, Clamp,
// ts3native_counters).
//
// TS3W's start-up VRAM query VRAM_DxDiag 0x612640 (one caller, DeviceCaps_Probe 0x613EA6, which stores the cdecl return
// in DeviceConfigManager +0xC4; docs/37 §6) runs IDxDiagProvider::Initialize on every launch: ~60 s on the test laptop,
// and its WMI calls leave a combase ping-loop worker busy ~5.3 min after (docs/270 §39). The hook replaces the function:
//   1 = cache: same answer from <Bin>\ts3native-vram.ini (dxdiagvram.h replays the game's device loop on the stored
//       DxDiag strings); a missing or stale sidecar (machine key changed) runs one real query, same arguments, and writes it
//   2 = zero: return 0 without DxDiag (the game's "no adapter matched" answer, what it reads under the RTX Remix bridge).
//   3 = refresh: as 1 with the sidecar ignored (always probes and rewrites it; the correctness queue's miss arm)
//       2 is forced while [Rewind] Park=1 (test-only, any DxDiagCache value), so a rewind snapshot never meets the ping loop.
// Both keep the game's CoInitialize(NULL) ... CoUninitialize() pair on the main thread.
#include "dxdiagvram.h"

static int g_ddcMode;
static wchar_t g_ddcPath[MAX_PATH];
static const u8 kDdcPrologue[] = {0x81, 0xEC, 0xA8, 0x02, 0x00, 0x00, 0x55, 0x57, 0x33, 0xFF, 0x33, 0xED};  // sub esp,2A8h; push ebp; push edi; xor edi,edi; xor ebp,ebp

static DWORD __cdecl DdcHook(const u8* ident) {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER f, t0, t1;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t0);
    CoInitialize(NULL);
    DWORD mb = 0;
    DdcDev d[DDC_MAX];
    int n = 0;
    DWORD probeMs = 0;
    wchar_t sig[24], by[16] = L"";
    DdcSig(sig, 24);
    const bool have = g_ddcMode != 3 && DdcRead(g_ddcPath, sig, d, &n, &probeMs, by, 16);
    const GUID* id = (const GUID*)(ident + 0x438);  // D3DADAPTER_IDENTIFIER9.DeviceIdentifier
    if (g_ddcMode == 2) {
        k.ddcPath = 4;
        k.ddcSavedMs = have ? (LONG)probeMs : 0;
    } else if (have) {
        mb = DdcResolve(d, n, id);
        k.ddcPath = 1;
        k.ddcSavedMs = (LONG)probeMs;
    } else {
        const bool ok = DdcEnum(d, &n);
        QueryPerformanceCounter(&t1);
        probeMs = (DWORD)((t1.QuadPart - t0.QuadPart) * 1000 / f.QuadPart);
        mb = ok ? DdcResolve(d, n, id) : 0;
        k.ddcPath = ok && DdcWrite(g_ddcPath, sig, d, n, probeMs, L"game") ? 2 : 3;
        k.ddcSavedMs = 0;
    }
    CoUninitialize();
    QueryPerformanceCounter(&t1);
    k.ddcMB = (LONG)mb;
    static const char* const kPath[] = {"", "hit", "miss, probed and written", "miss, probe or write failed (nothing cached)", "zero"};
    Log("DxDiagCache: %s -> %lu MB (mode %d, %d device(s), sig %ls%s%ls) in %lld ms, ~%ld ms saved", kPath[k.ddcPath], mb, g_ddcMode, n, sig,
        have ? ", sidecar by " : "", have ? by : L"", (t1.QuadPart - t0.QuadPart) * 1000 / f.QuadPart, k.ddcSavedMs);
    return mb;
}

static bool ApplyDxDiagCache(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    int mode = Clamp(GetPrivateProfileIntW(L"Startup", L"DxDiagCache", 0, ini), 0, 3);
    if (GetPrivateProfileIntW(L"Rewind", L"Park", 0, ini)) mode = 2;  // test-only rewind runs: never the ping loop
    if (!mode) { Log("DxDiag cache off (DxDiagCache=0)"); return false; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP DxDiag cache ([Startup] DxDiagCache): no EA 1.69 address, docs/232 §8; the game runs its own DxDiag query"); return false; }
#endif
    u8* fn = (u8*)(uintptr_t)TS3A(0x00612640);
    if (!Matches(fn, kDdcPrologue, sizeof(kDdcPrologue))) { Log("SKIP DxDiag cache: VRAM_DxDiag 0x612640 bytes differ, nothing written"); return false; }
    WCpy(g_ddcPath, ini);
    wchar_t* slash = wcsrchr(g_ddcPath, L'\\');
    if (WCpy(slash ? slash + 1 : g_ddcPath, MAX_PATH - (slash ? slash + 1 - g_ddcPath : 0), L"ts3native-vram.ini")) g_ddcPath[0] = 0;  // docs/323: no cut name
    g_ddcMode = mode;
    u8 jmp[5] = {0xE9};
    const LONG rel = (LONG)((uintptr_t)DdcHook - ((uintptr_t)fn + 5));
    memcpy(jmp + 1, &rel, 4);
    if (!Write5(fn, jmp)) { Log("FAIL DxDiag cache: VirtualProtect at 0x612640 (err %lu)", GetLastError()); return false; }
    k.ddcMode = mode;
    Log("DxDiag cache: DxDiagCache=%d (%s), sidecar ts3native-vram.ini", mode, mode == 1 ? "cache" : mode == 3 ? "refresh: probe and rewrite" : "zero, no DxDiag");
    return true;
}
