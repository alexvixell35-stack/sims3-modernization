// [CasTrim] (docs/191 "Who holds the part compositors" step 2; counters v909 ct*, runtime switch ctMode +0x5C54, --ab
// castrim): OFF by default. Included by ts3native.cpp after rctrim.cpp.
//
// The leak: the CAS texture compositor service ([0x11CE6A0]) keeps every part compositor that CAS/SimBuilder makes in a
// hash map at +0x104 (insert 0x5EECD0, from the SimBuilder part paths and the download-content loader). The insert also
// registers the compositor with the resource manager (manager vtable +0x5C, flag 1), so the ResourceCache finds it by key.
// The only erase, 0x5EE640 (find the key, unregister with flag 0, free the node and its reference), runs from the
// service's clear-all 0x5EEA90, i.e. only on a service reset [verified disassembly, TS3W 1.67]. Every outfit build makes
// new compositors with random instances, so the map and the cache grow for the whole session (docs/191: ~90% of rcN).
// The only reads of the map itself are Contains 0x5EDAB0 from the export code (ExportLot, ExportWorld, Sim outfit
// export, thumbnails) [verified: its 3 callers and their strings]; in play a compositor is reached through the cache.
//
// What this does, on the thread that inserts (no lock: the map has none; insert and erase both run where EA runs them):
// after each insert it walks Buckets more buckets of the map (a cursor) and looks at each part compositor (vtables
// 0xFE68D8 / 0xFE68F4). A compositor is old when (1) its references are exactly 2, the cache's and the map's (a
// SimBuilder part, a find in progress or anything else holding it makes 3+), and (2) it has not been inserted or found by
// key for AgeSec. Mode 2 (count only): old ones are marked, not erased; a later find by key of a marked one counts as
// "would miss" (in Mode 1 that find would have failed). Mode 1 (trim; the --ab castrim on arm): old ones are erased with EA's own 0x5EE640, at
// most MaxErase per insert; their keys are remembered, and a failed find of a remembered key counts as "missed".
// The finds are seen through a detour on the cache's Find 0x7324E0 (vtable 0xFFDD94 slot 6; no direct callers).
// Safety rule: no erase of anything with a third reference; the would-miss / missed counters and their first 20 events
// (caller, age) are the evidence that nothing a live Sim renders from goes. Inserts from a second thread are counted and
// never trimmed from. A fault inside the walk turns the mode off for the session.
static const DWORD CT_INSERT = 0x005EECD0, CT_FIND = 0x007324E0, CT_ERASE = 0x005EE640;
static const u8 kCtInsertPro[] = {0x83, 0xEC, 0x24, 0x53, 0x56, 0x57, 0x8B, 0xD9};  // sub esp,0x24; push ebx/esi/edi; mov ebx,ecx
static const u8 kCtFindPro[] = {0x83, 0xEC, 0x08, 0x53, 0x56, 0x57, 0x8B, 0xF9};    // sub esp,8; push ebx/esi/edi; mov edi,ecx
typedef int(__fastcall* CtInsertFn)(u8* svc, void* edx, u8* comp);
typedef bool(__fastcall* CtFindFn)(void* cache, void* edx, const void* key, void** out);
typedef bool(__fastcall* CtEraseFn)(u8* svc, void* edx, const void* key);
static CtInsertFn g_ctInsert;
static CtFindFn g_ctFind;
static CtEraseFn g_ctErase = (CtEraseFn)(uintptr_t)CT_ERASE;  // selftest: a fake
static DWORD g_ctVt[2] = {0x00FE68D8, 0x00FE68F4};             // selftest: its own
static DWORD g_ctAgeMs = 1800 * 1000, g_ctLogMs = 5 * 60000, g_ctTid, g_ctLogAt, g_ctCursor;
static int g_ctBuckets = 16, g_ctMaxErase = 8;
struct CtEnt { volatile LONG obj; DWORD seen, used, cand; };  // ticks: inserted / first walked, last found, marked old
static const DWORD CT_SLOTS = 1 << 18, CT_GONE = 1 << 16;
static CtEnt* g_ct;            // 4 MB, allocated when installed. shortcut: never shrinks; a full probe run skips the
static volatile LONG* g_ctGone;  // compositor (never erased): fine while the live set is far below 262k
static volatile LONG g_ctAge[4], g_ctEvents;  // would-miss + missed by time since the mark: < 1 min, < 10, < 60, more

static DWORD CtKeyHash(const void* k) {
    DWORD h = 2166136261u;
    for (int i = 0; i < 16; i++) h = (h ^ ((const u8*)k)[i]) * 16777619u;
    return h | 1;
}
static CtEnt* CtSlot(DWORD o, bool claim) {
    for (DWORD i = (o * 2654435761u) >> 14, k = 0; k < 32; i = (i + 1) & (CT_SLOTS - 1), k++) {
        const LONG a = g_ct[i].obj;
        if (a == (LONG)o) return &g_ct[i];
        if (a) continue;
        if (!claim) return NULL;
        if (InterlockedCompareExchange(&g_ct[i].obj, (LONG)o, 0) == 0) {
            g_ct[i].seen = g_ct[i].used = g_ct[i].cand = 0;
            return &g_ct[i];
        }
        if (g_ct[i].obj == (LONG)o) return &g_ct[i];
    }
    return NULL;
}
static void CtEvent(const char* what, DWORD o, DWORD at, DWORD sinceMark, DWORD age) {
    const DWORD m = sinceMark / 60000;
    InterlockedIncrement(&g_ctAge[m < 1 ? 0 : m < 10 ? 1 : m < 60 ? 2 : 3]);
    if (InterlockedIncrement(&g_ctEvents) <= 20)
        Log("cas trim: %s: compositor 0x%08lX found by key from 0x%08lX, %lu s after it was marked old (unused %lu s then)", what, o, at,
            sinceMark / 1000, age / 1000);
}

// One step of the walk (the inserting thread, after EA's insert returned).
static void CtStep(u8* svc, DWORD now) {
    Ts3nCounters& z = ts3native_counters;
    const LONG mode = z.ctMode;
    int erased = 0;
    __try {
        const DWORD* b = *(const DWORD* const*)(svc + 0x108);
        const DWORD nb = *(const DWORD*)(svc + 0x10C);
        for (int k = 0; b && nb && k < g_ctBuckets; k++) {
            for (u8 *e = (u8*)(uintptr_t)b[g_ctCursor++ % nb], *next; e; e = next) {
                next = *(u8* const*)(e + 0x18);  // node {ResourceKey, compositor +0x10, next +0x18} (docs/191)
                z.ctScanned++;
                const DWORD o = *(const DWORD*)(e + 0x10), vt = o ? *(const DWORD*)(uintptr_t)o : 0;
                if (vt != g_ctVt[0] && vt != g_ctVt[1]) continue;
                CtEnt* t = CtSlot(o, true);
                if (!t) { z.ctFull++; continue; }
                if (!t->seen) t->seen = t->used = now;
                const DWORD a1 = now - t->seen, a2 = now - t->used, age = a1 < a2 ? a1 : a2;
                if (*(const LONG*)(uintptr_t)(o + 4) >> 8 != 2 || age < g_ctAgeMs) { t->cand = 0; continue; }
                if (!t->cand) t->cand = now, z.ctCand++;
                if (mode != 1 || erased >= g_ctMaxErase) continue;
                u8 key[16];
                memcpy(key, e, 16);
                const DWORD h = CtKeyHash(key);
                t->seen = t->cand = 0;  // the address may come back as a new compositor (its insert sets it again)
                // remembered only after the erase: EA's erase unregisters through 0x4B00C0, whose cache scan 0x4B0070 calls
                // each cache's Find(key, NULL), and those failed finds are not misses (al237, 2026-11-05: 20 false MISSED)
                if (g_ctErase(svc, NULL, key)) erased++, z.ctErased++, g_ctGone[h & (CT_GONE - 1)] = (LONG)h;  // (mode 1)
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        z.ctMode = 0;
        Log("FAIL cas trim: fault in the map walk (cursor %lu): off for the session", g_ctCursor);
    }
}
static void CtLog(const u8* svc) {
    const Ts3nCounters& z = ts3native_counters;
    DWORD n = 0;
    __try { n = *(const DWORD*)(svc + 0x110); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("cas trim: mode %ld: inserts %ld, walked %ld, marked old %ld, erased %ld, would miss %ld, missed after a trim %ld (by time since "
        "the mark: < 1 min %ld, < 10 %ld, < 60 %ld, more %ld); registry %lu entries; table full %ld, other-thread inserts %ld",
        z.ctMode, z.ctInserts, z.ctScanned, z.ctCand, z.ctErased, z.ctWouldMiss, z.ctMissed, g_ctAge[0], g_ctAge[1], g_ctAge[2], g_ctAge[3], n,
        z.ctFull, z.ctOtherThread);
}
static int __fastcall CtInsertHook(u8* svc, void* edx, u8* comp) {
    const int r = g_ctInsert(svc, edx, comp);
    Ts3nCounters& z = ts3native_counters;
    if (!z.ctMode || !svc) return r;
    const DWORD now = GetTickCount(), tid = GetCurrentThreadId();
    InterlockedIncrement(&z.ctInserts);
    if (!g_ctTid) {
        g_ctTid = tid, g_ctLogAt = now;
        Log("cas trim: first insert on thread %lu (the thread that loaded us: %lu); the walk runs on that one", tid, g_mainTid);
    }
    if (tid != g_ctTid) { InterlockedIncrement(&z.ctOtherThread); return r; }
    if (comp)
        if (CtEnt* t = CtSlot((DWORD)(uintptr_t)comp, true)) t->seen = t->used = now, t->cand = 0;
    CtStep(svc, now);
    if (now - g_ctLogAt >= g_ctLogMs) g_ctLogAt = now, CtLog(svc);
    return r;
}
static bool __fastcall CtFindHook(void* cache, void* edx, const void* key, void** out) {
    const bool r = g_ctFind(cache, edx, key, out);
    Ts3nCounters& z = ts3native_counters;
    const LONG mode = z.ctMode;
    if (!mode) return r;
    const DWORD at = (DWORD)(uintptr_t)_ReturnAddress();
    __try {
        if (r && out && *out) {
            const DWORD o = (DWORD)(uintptr_t)*out, vt = *(const DWORD*)(uintptr_t)o;
            if (vt == g_ctVt[0] || vt == g_ctVt[1])
                if (CtEnt* t = CtSlot(o, false)) {
                    const DWORD now = GetTickCount();
                    if (t->cand) {
                        InterlockedIncrement(&z.ctWouldMiss);
                        const DWORD a1 = t->cand - t->seen, a2 = t->cand - t->used;
                        CtEvent("would miss", o, at, now - t->cand, a1 < a2 ? a1 : a2);
                    }
                    t->used = now, t->cand = 0;
                }
        } else if (!r && key && mode == 1) {
            const DWORD h = CtKeyHash(key);
            if (g_ctGone[h & (CT_GONE - 1)] == (LONG)h) {
                InterlockedIncrement(&z.ctMissed);
                CtEvent("MISSED after a trim", 0, at, 0, 0);
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return r;
}
static bool CtAlloc() {
    if (g_ct) return true;
    u8* m = (u8*)VirtualAlloc(NULL, CT_SLOTS * sizeof(CtEnt) + CT_GONE * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!m) return false;
    g_ct = (CtEnt*)m, g_ctGone = (volatile LONG*)(m + CT_SLOTS * sizeof(CtEnt));
    return true;
}
static void ApplyCasTrim(const wchar_t* ini) {
    const int mode = GetPrivateProfileIntW(L"CasTrim", L"Mode", 0, ini);
    const int age = GetPrivateProfileIntW(L"CasTrim", L"AgeSec", 1800, ini);
    const int buckets = GetPrivateProfileIntW(L"CasTrim", L"Buckets", 16, ini);
    const int maxErase = GetPrivateProfileIntW(L"CasTrim", L"MaxErase", 8, ini);
    g_ctAgeMs = (DWORD)(age < 10 ? 10 : age > 36000 ? 36000 : age) * 1000;
    g_ctBuckets = buckets < 1 ? 1 : buckets > 256 ? 256 : buckets;
    g_ctMaxErase = maxErase < 1 ? 1 : maxErase > 64 ? 64 : maxErase;
    if (mode <= 0 && GetPrivateProfileIntW(L"CasTrim", L"Install", 0, ini) <= 0) return;
    if (!CtAlloc()) return (void)Log("FAIL cas trim: no memory for the tables (4.3 MB): not installed");
    if (!g_ctFind)
        g_ctFind = (CtFindFn)InstallDetour("ResourceCache_Find", CT_FIND, kCtFindPro, sizeof(kCtFindPro), (void*)CtFindHook, &g_ctFind);
    if (!g_ctFind) return (void)Log("FAIL cas trim: cache Find 0x%08lX not detoured: not installed", CT_FIND);
    if (!g_ctInsert)
        g_ctInsert = (CtInsertFn)InstallDetour("CasCompositorRegister", CT_INSERT, kCtInsertPro, sizeof(kCtInsertPro), (void*)CtInsertHook, &g_ctInsert);
    if (!g_ctInsert) return (void)Log("FAIL cas trim: compositor insert 0x%08lX not detoured: not installed (the Find detour passes through)", CT_INSERT);
    Ts3nCounters& z = ts3native_counters;
    z.ctOn = 1, z.ctMode = mode < 0 ? 0 : mode > 2 ? 2 : mode;
    Log("cas trim: on (docs/191 step 2), mode %ld (%s): part compositors at 2 references, not inserted or found for %lu s; %d buckets a "
        "step, at most %d erases a step", z.ctMode, z.ctMode == 1 ? "trim with EA's erase 0x5EE640" : z.ctMode == 2 ? "count only" : "armed, off",
        g_ctAgeMs / 1000, g_ctBuckets, g_ctMaxErase);
}
