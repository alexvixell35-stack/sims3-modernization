// [WorldImage] M3.1 capture trigger (docs/123 §2.5, §8). Included by ts3native.cpp (one translation unit). Off by default.
//
// Cut = end of a MonoScriptHost_Simulate 0xD81840 loop iteration (simloop.cpp; docs/51 §188: Simulate is entered once and
// loops, so the earlier hook on its return never ran during play), on the sim thread. Every 30 iterations it looks for a
// file named "request" in OutDir (the harness or a probe writes it); on one, it deletes the file and asks the worldimage helper (native/worldimage) for a capture: the helper suspends every game
// thread, writes OutDir\wi-<UTC time>-<n>\ (header.txt, mem.bin), and resumes. No reference driver yet (M3.3 / docs/122
// pilot): image.ref says so. user.txt also gets the G3-1 managed statics (kWiStatics in wimage.h: SimClock ticks, lot count,
// meta object count, autonomy level), read here before the capture. Each capture stalls the game a few seconds: capture jobs only, never timing runs.
// Images hold the whole private memory of the game, personal data included: OutDir belongs under game-copy/.
#include "../worldimage/wimage.h"

static DWORD g_wiTicks;
static int g_wiDone, g_wiMax;
static char g_wiDir[MAX_PATH], g_wiHelper[MAX_PATH], g_wiReq[MAX_PATH + 16];

static void WiRefNone(void*, const char* dir) {
    char path[MAX_PATH + 32];
    sprintf_s(path, "%s\\image.ref", dir);
    FILE* f = NULL;
    if (!fopen_s(&f, path, "w") && f) { fputs("ref none (M3.1: no reference driver yet, docs/123 §8)\n", f); fclose(f); }
}

static void __cdecl WiCut() {
    if (++g_wiTicks % 30 || g_wiDone >= g_wiMax || GetFileAttributesA(g_wiReq) == INVALID_FILE_ATTRIBUTES) return;
    DeleteFileA(g_wiReq);
    g_wiDone++;
    SYSTEMTIME st;
    GetSystemTime(&st);
    char dir[MAX_PATH + 64], user[2048], err[256];
    sprintf_s(dir, "%s\\wi-%04d%02d%02d-%02d%02d%02d-%d", g_wiDir, st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, g_wiDone);
    const DWORD host = Readable((void*)(uintptr_t)TS3A(0x011EE514), 4) ? *(DWORD*)(uintptr_t)TS3A(0x011EE514) : 0;
    // MT index after the 624 state words at host+0xC30 (docs/27 §1.1) [inferred offset, I-123-23]
    const DWORD mt = host && Readable((void*)(uintptr_t)(host + 0x15F0), 4) ? *(DWORD*)(uintptr_t)(host + 0x15F0) : 0xFFFFFFFF;
    int n = sprintf_s(user, "build ts3native-" TS3NATIVE_VERSION "\ncut simulate-iteration\ntick %lu\nhost %08lX\nmtindex %lu\n", g_wiTicks, host, mt);
    // G3-1 check values (docs/123 §8.6): managed statics read here, on the sim thread, before the helper suspends anything
    unsigned long long sum = 0;
    int got = 0;
    for (int i = 0; i < kWiNStatics && n > 0; i++) {
        const WiStatic& ws = kWiStatics[i];
        void* k = WiFindClass(ws.ns, ws.cls);
        unsigned long long v = 0;
        const bool ok = WiReadStatic(k, ws, &v);
        got += ok;
        if (ok && i < 3) sum += v;  // SimClock.CurrentTicks = sSimTickCnt + sStartTime.Ticks + sTicksAdvanced (the first 3 entries)
        n += sprintf_s(user + n, sizeof(user) - n, ok ? "g31.%s %llX\ng31k.%s %08lX\n" : "g31.%s none\ng31k.%s %08lX\n", ws.key, v, ws.key,
                       (DWORD)(uintptr_t)k);
    }
    if (n > 0 && got == kWiNStatics) sprintf_s(user + n, sizeof(user) - n, "g31.currentticks %llX\n", sum);
    const DWORD t0 = GetTickCount();
    const int rc = WiRequestCapture(g_wiHelper, dir, 0x400000, kWiTs3wTls, 7, kWiTs3wLocks, 16, user, WiRefNone, NULL, err, sizeof(err));
    Log("world image %d: %s -> %s%s (%lu ms stall on the sim thread, tick %lu; G3-1 statics %d of %d read, CurrentTicks %llu)", g_wiDone, dir,
        rc ? "FAILED " : "ok", err, GetTickCount() - t0, g_wiTicks, got, kWiNStatics, sum);
}

// [WorldImage] Enabled=1, OutDir=<folder> (required), Helper=<worldimage.exe> (default: next to ts3native.asi), MaxCaptures=5
static bool ApplyWorldImage(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"WorldImage", L"Enabled", 0, ini)) { Log("world image disabled (Enabled=0)"); return false; }
    wchar_t w[MAX_PATH];
    GetPrivateProfileStringW(L"WorldImage", L"OutDir", L"", w, MAX_PATH, ini);
    WideCharToMultiByte(CP_ACP, 0, w, -1, g_wiDir, MAX_PATH, NULL, NULL);
    GetPrivateProfileStringW(L"WorldImage", L"Helper", L"", w, MAX_PATH, ini);
    WideCharToMultiByte(CP_ACP, 0, w, -1, g_wiHelper, MAX_PATH, NULL, NULL);
    if (!g_wiHelper[0]) {
        HMODULE self = NULL;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)&ApplyWorldImage, &self);
        GetModuleFileNameA(self, g_wiHelper, MAX_PATH);
        char* slash = strrchr(g_wiHelper, '\\');
        if (slash) strcpy_s(slash + 1, MAX_PATH - (slash + 1 - g_wiHelper), "worldimage.exe");
    }
    g_wiMax = GetPrivateProfileIntW(L"WorldImage", L"MaxCaptures", 5, ini);
    if (!g_wiDir[0] || GetFileAttributesA(g_wiHelper) == INVALID_FILE_ATTRIBUTES) {
        Log("SKIP world image: OutDir empty or helper %s missing", g_wiHelper);
        return false;
    }
    CreateDirectoryA(g_wiDir, NULL);
    sprintf_s(g_wiReq, "%s\\request", g_wiDir);
    if (!SimLoopAdd(WiCut, "world image")) { Log("SKIP world image: no Simulate iteration hook"); return false; }
    Log("OK   world image: cut at the end of every 30th Simulate iteration; request file %s, helper %s, max %d captures", g_wiReq,
        g_wiHelper, g_wiMax);
    return true;
}
