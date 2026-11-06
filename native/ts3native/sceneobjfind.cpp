// [SceneObjFind] (docs/532), counters v911 (contributor block), observe only: how long are the chains that the scene-object
// hash find SceneObjMgr_FindObject 0x6FDFB0 walks? It runs per object per frame (FrameDb delta records inside the FrameDb
// +0x48 lock, render sets, rig pose) and is the top non-idle self function in the docs/71 sampler (2.1%).
// Find: thiscall, ecx = table, args out {node, bucket*} and key {lo, hi}, ret 8, eax = out. Fixed 0x409 buckets inline at
// table+0 (the end bucket at +0x1024), singly linked: next = node[0], key = node[2..3]. The detour walks the chain once more
// (read only, main thread) and then runs EA's find through the trampoline, so the answer is EA's own.
// Included by ts3native.cpp.

static const DWORD SF_FIND = 0x006FDFB0;
static const u8 kSfPro[8] = {0x83, 0xEC, 0x08, 0x56, 0x8B, 0x74, 0x24, 0x14};  // sub esp,8; push esi; mov esi,[esp+0x14]
static const DWORD SF_BUCKETS = 0x409;
typedef DWORD* (__thiscall* SfFindFn)(const DWORD* table, DWORD* out, const DWORD* key);
static SfFindFn g_sfOrig;
static unsigned long long g_sfCalls, g_sfNodes;  // main thread only; published in units of 1024 (32-bit fields would wrap)

// nodes compared before EA's loop stops: the hit's depth + 1, or the whole chain on a miss. Capped at SF_WALK_CAP: a
// corrupt or cyclic chain must not spin the main thread (EA's own loop would, but that is EA's bug, not this counter's).
static const LONG SF_WALK_CAP = 4096;
static LONG SfWalk(const DWORD* table, const DWORD* key) {
    LONG n = 0;
    for (const DWORD* node = (const DWORD*)(uintptr_t)table[key[0] % SF_BUCKETS]; node; node = (const DWORD*)(uintptr_t)node[0]) {
        n++;
        if ((node[2] == key[0] && node[3] == key[1]) || n >= SF_WALK_CAP) break;
    }
    return n;
}

static void SfCount(const DWORD* table, const DWORD* key) {
    Ts3nCounters& k = ts3native_counters;
    if (GetCurrentThreadId() != g_mainTid) { InterlockedIncrement(&k.sfOffMain); return; }  // expected 0 (docs/532 §2)
    const LONG n = SfWalk(table, key);
    g_sfCalls++;
    g_sfNodes += n;
    k.sfKCalls = (LONG)(g_sfCalls >> 10);
    k.sfKNodes = (LONG)(g_sfNodes >> 10);
    if (n > k.sfMaxNodes) k.sfMaxNodes = n;
}

// fastcall with an unused edx is thiscall for the caller: ecx = table, the two stack args popped here, eax = out
static DWORD* __fastcall SfFind(const DWORD* table, void*, DWORD* out, const DWORD* key) {
    SfCount(table, key);
    return g_sfOrig(table, out, key);
}

static bool ApplySceneObjFind(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"SceneObjFind", L"Enabled", 0, ini)) { Log("scene object find counters off (Enabled=0)"); return false; }
    // Apex ObjectLookupIndex owns this entry (RenMapFind, with its erase 0x6FDF40 / insert 0x6FE1F0) and installs after us
    if (ApexStandsDown(AXF_OBJECT_INDEX, "[SceneObjFind]", "entry 0x6FDFB0")) return true;
    // DetourCore logs OK, or SKIP when the bytes differ (another build or another mod owns the entry)
    InstallDetour("SceneObjMgr_FindObject (chain counters)", SF_FIND, kSfPro, sizeof(kSfPro), (void*)SfFind, &g_sfOrig);
    return true;
}
