// [EaCrashFixes] KeepOptionsOnGpuChange (docs/37 §12, counters v219 ko*, ledger §540; default 0, opt-in until the game check passes).
// Included by ts3native.cpp after dxdiagcache.cpp (needs Log, Matches, Write5, ts3native_counters); selftest: keepopts-test.h.
//
// EA bug: when Options.ini `lastdevice` (adapter;vendor;device;n) differs from the adapter the game sees, every option goes
// back to its GraphicsRules.sgr default: ageing and season lengths, pack toggles, clock, units, audio, graphics. A changed
// device id is common: a second GPU, adding or removing a d3d9 wrapper, the RTX Remix bridge (reports a GTX 580 10de;1080)
// versus a direct TS3W.exe start (the real card).
// EA code [verified disassembly, Steam 1.67 + retail 1.67; docs/37 §12]:
//   DeviceConfig_Init 0x5928F0 loads the whole Options.ini into the ini store, then DeviceConfig_CheckLastDevice 0x590F80
//   formats the current device into cfg+0x64 (EA string, begin pointer at +0) and sets cfg+0x74 (isNewDevice) = 1 unless the
//   stored LastDevice matches. GraphicsRules.sgr then sets every option's default (hardware-dependent ones from the card),
//   and Options_LoadFromIni 0x58E470 reads each option back from the ini store, record by record (vector [cfg+0xC4] ..
//   [cfg+0xC8], stride 0x3C, key wchar_t* at +0x10). The whole reset is one test in that loop:
//     0x58E4AF  80 7F 74 00   cmp byte [edi+0x74],0     (retail 0x58E8BF, same bytes, unique in both exes)
//     0x58E4B3  75 43         jne 0x58E4F8              skip the ini read: the sgr default stays
//   edi = cfg, esi = record, ebp = records left including this one; eax / ecx / edx / flags are dead on both paths.
//   Resolution / PseudoResolution are skipped the same way in 0x58E860, which this fix leaves alone (re-detected).
// Fix: the 6 bytes jump to KoStub. Same device: EA's path. New device: only the options whose sgr default depends on the
// hardware (kKoDetected, read from GraphicsRules.sgr `if` blocks on card / cpu / memory) skip the ini read and get the
// new card's detected value; every other option keeps the player's value. One log line on the last record.
// EA 1.69 TS3.exe: no address (encrypted .text), SKIP. Household rule: not applicable (game options, no Sim decision).

static DWORD g_koCont, g_koSkip;  // EA's "read the ini" and "keep the default" targets

// the options GraphicsRules.sgr derives from the hardware [verified: their defaults sit inside card / cpu / memory tests]
static const wchar_t* const kKoDetected[] = {
    L"animationsmoothing", L"lightingquality", L"generalreflections", L"texturequality", L"visualeffects", L"terrainquality",
    L"edgesmoothing", L"objecthiding", L"simquality", L"treequality", L"drawdistance", L"maxactivelots",
    L"nummaxactivelotoptions", L"advancedrendering", L"audioquality", L"aspectratio", L"forcesquarepixels"};

static bool KoIsDetected(const wchar_t* key) {
    if (!key) return true;  // unknown record: EA's reset path for it
    for (const wchar_t* d : kKoDetected)
        if (!_wcsicmp(key, d)) return true;
    return false;
}

static void KoCopy(wchar_t* out, size_t n, const u8* strObj) {  // EA string object: +0 = wchar_t* begin, NUL-terminated
    out[0] = 0;
    __try {
        const wchar_t* s = *(const wchar_t* const*)strObj;
        size_t i = 0;
        for (; s && i + 1 < n && s[i]; i++) out[i] = s[i];
        out[i] = 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { out[0] = 0; }
}

// cdecl from KoStub, only while isNewDevice is set: 1 = read the player's value (EA's same-device path), 0 = keep the default
extern "C" int __cdecl KoKeep(const u8* cfg, const u8* rec, LONG left) {
    Ts3nCounters& k = ts3native_counters;
    const wchar_t* key = NULL;
    __try { key = *(const wchar_t* const*)(rec + 0x10); } __except (EXCEPTION_EXECUTE_HANDLER) { key = NULL; }
    const bool detected = KoIsDetected(key);
    InterlockedIncrement(detected ? &k.koRedetected : &k.koKept);
    if (left == 1) {  // last record of this load
        InterlockedIncrement(&k.koFires);
        wchar_t path[MAX_PATH], newDev[64], oldDev[64] = L"";
        KoCopy(path, MAX_PATH, cfg + 0xF4);
        KoCopy(newDev, 64, cfg + 0x64);  // empty when a command-line level forced the flag (0x592AFA)
        if (path[0]) GetPrivateProfileStringW(L"options", L"lastdevice", L"", oldDev, 64, path);
        Log("options: device changed %ls -> %ls, kept %ld gameplay keys, graphics re-detected (%ld keys + resolution; [EaCrashFixes] KeepOptionsOnGpuChange=1)",
            oldDev[0] ? oldDev : L"?", newDev[0] ? newDev : L"?", k.koKept, k.koRedetected);
    }
    return detected ? 0 : 1;
}

static __declspec(naked) void KoStub() {
    __asm {
        cmp byte ptr [edi+0x74], 0
        je keep_read           // same device: EA's ini read
        push ebp               // records left
        push esi               // record
        push edi               // cfg
        call KoKeep
        add esp, 12
        test eax, eax
        jz keep_default
    keep_read:
        jmp dword ptr [g_koCont]
    keep_default:
        jmp dword ptr [g_koSkip]
    }
}

static const u8 kKoSite[6] = {0x80, 0x7F, 0x74, 0x00, 0x75, 0x43};  // cmp byte [edi+0x74],0; jne +0x43

static bool ApplyKeepOptions(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"EaCrashFixes", L"KeepOptionsOnGpuChange", 0, ini) <= 0) {
        Log("keep options on GPU change off ([EaCrashFixes] KeepOptionsOnGpuChange=0): a new device resets every option (EA)");
        return false;
    }
    const DWORD site = TS3A_PICK(0x0058E4AF, 0, 0x0058E8BF);
    if (!site) { Log("SKIP keep options on GPU change: no EA 1.69 address (encrypted .text); a new device resets every option (EA)"); return false; }
    u8* p = (u8*)(uintptr_t)site;
    if (!Matches(p, kKoSite, sizeof(kKoSite))) { Log("SKIP keep options on GPU change: Options_LoadFromIni bytes at 0x%08lX differ, nothing written", site); return false; }
    g_koCont = site + 6;
    g_koSkip = site + 6 + 0x43;
    u8 jmp[5] = {0xE9};
    const LONG rel = (LONG)((uintptr_t)KoStub - ((uintptr_t)p + 5));
    memcpy(jmp + 1, &rel, 4);
    if (!Write5(p, jmp)) { Log("FAIL keep options on GPU change: VirtualProtect at 0x%08lX (err %lu)", site, GetLastError()); return false; }
    ts3native_counters.koMode = 1;
    Log("keep options on GPU change on (docs/37 §12): a new graphics device re-detects %u graphics options + resolution, every other option keeps its value",
        (unsigned)(sizeof(kKoDetected) / sizeof(kKoDetected[0])));
    return true;
}
