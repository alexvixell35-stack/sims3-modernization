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
// Who finds a part compositor by key, and what each does on a miss [verified disassembly, TS3W 1.67, 2026-11-05]:
// - a parent compositor's texture rebuild (0x605CE0 "handle or rebuild" from the texture code 0x6CBF20 / 0x6CCA90 /
//   0x6CCC50 -> 0x605C70 -> 0x6057F0, strings CAS/TextureCompositorResource/FabricOpKeyList, GetTextureBuilder): each op
//   of type 0x34210A5 in the parent's list (+0x5C, count byte +0x60) names a child by key (parameter 0xDCFF6D7B, key from
//   0x600850); the child comes through the manager's get 0x7366B0. A failed get makes the rebuild return handle -1: the
//   texture is not built. Parent and child are both part compositors (ctor 0x600560 sets vtable 0xFE68F4), so a child at
//   2 references can still be named by a live parent, after any time (al244: re-found after 30-60 min unused 2,591 times).
// - the get 0x7366B0 (return 0x736718), the async loader 0x729E10 (0x729E80) and the blit 0x5FA670 (0x5FA6D4, then a
//   request by key 0x5F8170): on a cache miss they load the key from a database (manager +0x44 finds which one; the get
//   then creates it through the type's factory and registers it again);
// - the parent's Load 0x607070 (0x60733E): a miss rebuilds the child from the parent's own data (new 0x68, ctor 0x600560,
//   Load again, register 0x7D9100).
// NeedDb=1: a compositor is marked old only if a database holds its key (asked once, again after AgeSec): every way back
// then exists. al246 (old save, 60 min): none of them has one, so it trims nothing; off by default.
// Named=1 (the default): a compositor is marked old only if no other part compositor in the map, itself not marked old,
// names its key as a child in its op list (read as 0x6057F0 reads it). The names are collected per full walk of the map
// (a cycle); a mark needs this cycle and the last full one, and a new compositor's names count from its insert.
// Async=1 (the default): a compositor the async loader 0x729E10 has found by key (return 0x729E80) is never marked old.
// al249 (Named, old save, 60 min): its 6 would-misses all came from there, from an outfit's load (CAS/SimOutfitResource/Load
// 0x5E9AA0 -> 0x5E55B0 -> 0x5E53E0: an async request by key, callback 0xD4B150): the outfit names its part compositors
// by key, and neither a compositor nor a database does.
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
struct CtEnt { volatile LONG obj; DWORD seen, used, cand, dbNo; volatile LONG from; };  // ticks: inserted / first walked, last
                     // found, marked old, last "no database has the key" (NeedDb); from: a bit (CtFromBit) per caller that found it
static const DWORD CT_SLOTS = 1 << 18, CT_GONE = 1 << 18;  // gone: al251 erased 120k keys an hour; 2^16 forgot most after ~30 min
static CtEnt* g_ct;            // 6 MB, allocated when installed. shortcut: never shrinks; a full probe run skips the
static volatile LONG* g_ctGone;  // compositor (never erased): fine while the live set is far below 262k
static volatile LONG g_ctAge[4], g_ctEvents;  // would-miss + missed by time since the mark: < 1 min, < 10, < 60, more
// finds by key of a known part compositor by how long it had been unused (no insert, no find): < 1 min, 1-5, 5-10, 10-30,
// 30-60, 60+ (al243: the old save finds some again after 10+ min, so this picks a safe AgeSec, or shows there is none)
static volatile LONG g_ctGap[6];

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
            g_ct[i].seen = g_ct[i].used = g_ct[i].cand = g_ct[i].dbNo = 0, g_ct[i].from = 0;
            return &g_ct[i];
        }
        if (g_ct[i].obj == (LONG)o) return &g_ct[i];
    }
    return NULL;
}
// A would-miss asks the resource manager ([0x11C78D8], vtable 0xFFE250) its own "database for this key" (+0x44 = 0x4AFDA0),
// the lookup the get 0x7366B0 makes right after a cache miss: a database = the game would load it again, none = a trim
// would lose it (the get returns false). Read only. EA calls it on every get (0x7D80E0, under the get's lock); it takes
// the manager's own list lock (+0x48) and drops it while it asks each database (0x4AFFC0), so the walk's thread may ask too.
static bool g_ctNeedDb = false;
static volatile LONG g_ctMarkDb[2], g_ctReload;  // marks (NeedDb): no database (not marked) / a database; trimmed, then loaded again
static LONGLONG g_ctDbQpc;                       // time in those mark checks (the walk's thread only)
typedef void*(__fastcall* CtDbFn)(void* rm, void* edx, const void* key);
static DWORD g_ctRmAt = 0x011C78D8, g_ctRmVt = 0x00FFE250;  // selftest: its own
static volatile LONG g_ctDbYes, g_ctDbNo;  // would-miss keys
static int CtInDb(const void* key) {  // 1 a database has it, 0 none, -1 not asked
    void* rm = *(void* const*)(uintptr_t)g_ctRmAt;
    if (!rm || *(const DWORD*)rm != g_ctRmVt) return -1;
    return (*(CtDbFn const*)(uintptr_t)(g_ctRmVt + 0x44))(rm, NULL, key) ? 1 : 0;
}
// Named: the keys part compositors name as children, per walk cycle (every bucket once): [0] this cycle, [1] the last full
// one. FNV hashes, open addressing, the walk's thread only; a hash collision keeps a compositor (the safe side).
// shortcut: 512k slots each (2 MB); a full table keeps everything until it has cycled out (logged): grow it if that shows.
static bool g_ctNamed = true;
static bool g_ctAsync = true;
static volatile LONG g_ctFromN[5], g_ctKeptAs;  // finds of known compositors per CtFromBit; old ones Async kept (walk visits)
static const DWORD CT_NAMED = 1 << 19;
static LONG* g_ctNm[2];
static bool g_ctNmFull[2];
static DWORD g_ctCyB, g_ctCyNb, g_ctCyAt, g_ctCyMs;  // buckets walked this cycle, the bucket count it counts to, start, last length
static LONG g_ctCycles, g_ctKids, g_ctKept;  // full cycles; child keys read; walk visits of an old one kept because one names it
static void CtNmAdd(const void* key) {
    const DWORD h = CtKeyHash(key);
    g_ctKids++;
    for (DWORD i = (h * 2654435761u) >> 13, k = 0; k < 64; i = (i + 1) & (CT_NAMED - 1), k++) {
        if (g_ctNm[0][i] == (LONG)h) return;
        if (!g_ctNm[0][i]) { g_ctNm[0][i] = (LONG)h; return; }
    }
    g_ctNmFull[0] = true;
}
static bool CtNmHas(const LONG* t, DWORD h) {
    for (DWORD i = (h * 2654435761u) >> 13, k = 0; k < 64; i = (i + 1) & (CT_NAMED - 1), k++) {
        if (t[i] == (LONG)h) return true;
        if (!t[i]) return false;
    }
    return true;  // a probe this long: keep
}
static bool CtNamedKey(const void* key) {  // before the first full cycle, or with a full table, everything is named
    if (!g_ctCycles || g_ctNmFull[0] || g_ctNmFull[1]) return true;
    const DWORD h = CtKeyHash(key);
    return CtNmHas(g_ctNm[0], h) || CtNmHas(g_ctNm[1], h);
}
static void CtCycle(DWORD now) {  // a full walk: this cycle's names become the last full one's
    LONG* t = g_ctNm[1];
    g_ctNm[1] = g_ctNm[0], g_ctNmFull[1] = g_ctNmFull[0];
    memset(t, 0, CT_NAMED * 4), g_ctNm[0] = t, g_ctNmFull[0] = false;
    g_ctCyB = 0, g_ctCycles++, g_ctCyMs = now - g_ctCyAt, g_ctCyAt = now;
}
// A compositor's children, read as the texture rebuild 0x6057F0 reads them (0x605910-0x605A1A) [verified disassembly]:
// each op of type 0x34210A5 (op & 0xFF indexes the type table 0x1157E04) has the parameter block 0x602380(op >> 8);
// parameter 0xDCFF6D7B (name table [0x1157EB8] / size [0x1157EBC], node {name, type byte +4, next +8}) is found in it with
// the lower bound 0x600280 (order table [0x1157ED4]); its value >> 8 is a key id, and 0x600850 copies the key. 0x602380
// and 0x600850 take EA's critical section 0x11CEB38 (recursive) and take no other lock inside it.
typedef const DWORD*(__cdecl* CtParamsFn)(DWORD id);
typedef const DWORD*(__cdecl* CtLowerFn)(DWORD b, DWORD e, const DWORD* want, DWORD n);
typedef bool(__cdecl* CtKeyFn)(DWORD id, void* key);
static void CtKidsEa(DWORD o) {
    const DWORD n = *(const u8*)(uintptr_t)(o + 0x60), *ops = *(const DWORD* const*)(uintptr_t)(o + 0x5C);
    for (DWORD i = 0; ops && i < n; i++) {
        const DWORD op = ops[i];
        if (*(const DWORD*)(uintptr_t)(0x01157E04 + (op & 0xFF) * 4) != 0x034210A5) continue;
        const DWORD* pb = ((CtParamsFn)(uintptr_t)0x00602380)(op >> 8);
        DWORD v = *(const DWORD*)(uintptr_t)0x01157EB0;  // EA's "no such parameter"
        const DWORD sz = *(const DWORD*)(uintptr_t)0x01157EBC;
        const DWORD* const* tbl = *(const DWORD* const* const*)(uintptr_t)0x01157EB8;
        const DWORD* nd = sz && tbl ? tbl[0xDCFF6D7Bu % sz] : NULL;
        while (nd && nd[0] != 0xDCFF6D7Bu) nd = (const DWORD*)(uintptr_t)nd[2];
        if (nd) {
            const DWORD want = *(const u8*)(nd + 1) | 0xFFFFFF00, *ord = *(const DWORD* const*)(uintptr_t)0x01157ED4;
            const DWORD* p = ((CtLowerFn)(uintptr_t)0x00600280)(pb[0], pb[1], &want, *(const u8*)(pb + 4));
            if (p != (const DWORD*)(uintptr_t)pb[1] && !(ord[(want & 0xFF) * 2] < ord[(*p & 0xFF) * 2])) v = *p;
        }
        u8 key[16];
        if ((v & 0xFF) != 0xFF && ((CtKeyFn)(uintptr_t)0x00600850)(v >> 8, key)) CtNmAdd(key);
    }
}
static void (*g_ctKidsFn)(DWORD o) = CtKidsEa;  // selftest: a fake
static void CtEvent(const char* what, DWORD o, DWORD at, DWORD sinceMark, DWORD age, const DWORD* sp, int db = -1, LONG from = 0) {
    const DWORD m = sinceMark / 60000;
    InterlockedIncrement(&g_ctAge[m < 1 ? 0 : m < 10 ? 1 : m < 60 ? 2 : 3]);
    if (InterlockedIncrement(&g_ctEvents) > 20) return;
    DWORD c[3] = {};  // the next exe .text values up the stack (as RsFindChain: a scan, stale values can show)
    if (sp && g_rhTxHi) __try {
            for (int i = 1, k = 0; i < 80 && k < 3; i++)
                if (sp[i] >= g_rhTxLo && sp[i] < g_rhTxHi) c[k++] = sp[i];
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("cas trim: %s: compositor 0x%08lX found by key from 0x%08lX < 0x%08lX < 0x%08lX < 0x%08lX, %lu s after it was marked old "
        "(unused %lu s then); a database has the key: %s; found before by 0x%lX (1 async loader, 2 get, 4 blit, 8 parent Load, "
        "16 other)", what, o, at, c[0], c[1], c[2], sinceMark / 1000, age / 1000,
        db > 0 ? "yes (would load again)" : db == 0 ? "NO (a trim would lose it)" : "not asked", from);
}

// One step of the walk (the inserting thread, after EA's insert returned).
static void CtStep(u8* svc, DWORD now) {
    Ts3nCounters& z = ts3native_counters;
    const LONG mode = z.ctMode;
    int erased = 0;
    __try {
        const DWORD* b = *(const DWORD* const*)(svc + 0x108);
        const DWORD nb = *(const DWORD*)(svc + 0x10C);
        if (nb != g_ctCyNb) g_ctCyNb = nb, g_ctCyB = 0;  // a new bucket array (the map grew): the cycle starts again
        for (int k = 0; b && nb && k < g_ctBuckets; k++) {
            for (u8 *e = (u8*)(uintptr_t)b[g_ctCursor++ % nb], *next; e; e = next) {
                next = *(u8* const*)(e + 0x18);  // node {ResourceKey, compositor +0x10, next +0x18} (docs/191)
                z.ctScanned++;
                const DWORD o = *(const DWORD*)(e + 0x10), vt = o ? *(const DWORD*)(uintptr_t)o : 0;
                if (vt != g_ctVt[0] && vt != g_ctVt[1]) continue;
                CtEnt* t = CtSlot(o, true);
                if (g_ctNamed && (!t || !t->cand)) g_ctKidsFn(o);  // it names its children unless it is marked old itself
                if (!t) { z.ctFull++; continue; }
                if (!t->seen) t->seen = t->used = now;
                const DWORD a1 = now - t->seen, a2 = now - t->used, age = a1 < a2 ? a1 : a2;
                if (*(const LONG*)(uintptr_t)(o + 4) >> 8 != 2 || age < g_ctAgeMs) { t->cand = 0; continue; }
                if (g_ctNamed && CtNamedKey(e)) { g_ctKept++, t->cand = 0; continue; }  // the node starts with the key
                if (g_ctAsync && (t->from & 1)) { g_ctKeptAs++, t->cand = 0; continue; }  // an outfit names it (Async)
                if (!t->cand) {
                    if (g_ctNeedDb) {
                        if (t->dbNo && now - t->dbNo < g_ctAgeMs) continue;
                        LARGE_INTEGER q0, q1;
                        QueryPerformanceCounter(&q0);
                        const int db = CtInDb(e);  // the node starts with the key
                        QueryPerformanceCounter(&q1);
                        g_ctDbQpc += q1.QuadPart - q0.QuadPart;
                        InterlockedIncrement(&g_ctMarkDb[db > 0]);
                        if (db <= 0) { t->dbNo = now | 1; continue; }
                    }
                    t->cand = now, z.ctCand++;
                }
                if (mode != 1 || erased >= g_ctMaxErase) continue;
                u8 key[16];
                memcpy(key, e, 16);
                const DWORD h = CtKeyHash(key);
                t->seen = t->cand = 0;  // the address may come back as a new compositor (its insert sets it again)
                // remembered only after the erase: EA's erase unregisters through 0x4B00C0, whose cache scan 0x4B0070 calls
                // each cache's Find(key, NULL), and those failed finds are not misses (al237, 2026-11-05: 20 false MISSED)
                if (g_ctErase(svc, NULL, key)) erased++, z.ctErased++, g_ctGone[h & (CT_GONE - 1)] = (LONG)h;  // (mode 1)
            }
            if (++g_ctCyB >= nb) CtCycle(now);
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
    Log("cas trim: re-found after unused for: < 1 min %ld, 1-5 min %ld, 5-10 %ld, 10-30 %ld, 30-60 %ld, 60+ %ld; would-miss keys in a "
        "database %ld, in none %ld", g_ctGap[0], g_ctGap[1], g_ctGap[2], g_ctGap[3], g_ctGap[4], g_ctGap[5], g_ctDbYes, g_ctDbNo);
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    Log("cas trim: NeedDb %d: old ones with a database %ld (marked), without %ld (kept), %lu ms asking; trimmed then loaded again %ld",
        g_ctNeedDb, g_ctMarkDb[1], g_ctMarkDb[0], f.QuadPart ? (DWORD)(g_ctDbQpc * 1000 / f.QuadPart) : 0, g_ctReload);
    Log("cas trim: Named %d: walk cycles %ld (the last %lu s), child keys read %ld, old ones kept because a compositor names "
        "them %ld (walk visits); name table full: this cycle %d, the last %d", g_ctNamed, g_ctCycles, g_ctCyMs / 1000, g_ctKids,
        g_ctKept, g_ctNmFull[0], g_ctNmFull[1]);
    Log("cas trim: Async %d: old ones kept because the async loader found them %ld (walk visits); finds of known compositors "
        "by caller: async loader %ld, get %ld, blit %ld, parent Load %ld, other %ld", g_ctAsync, g_ctKeptAs, g_ctFromN[0],
        g_ctFromN[1], g_ctFromN[2], g_ctFromN[3], g_ctFromN[4]);
}
static int __fastcall CtInsertHook(u8* svc, void* edx, u8* comp) {
    const int r = g_ctInsert(svc, edx, comp);
    Ts3nCounters& z = ts3native_counters;
    if (!z.ctMode || !svc) return r;
    const DWORD now = GetTickCount(), tid = GetCurrentThreadId();
    InterlockedIncrement(&z.ctInserts);
    if (!g_ctTid) {
        g_ctTid = tid, g_ctLogAt = g_ctCyAt = now;
        Log("cas trim: first insert on thread %lu (the thread that loaded us: %lu); the walk runs on that one", tid, g_mainTid);
    }
    if (tid != g_ctTid) { InterlockedIncrement(&z.ctOtherThread); return r; }
    if (comp) {
        if (CtEnt* t = CtSlot((DWORD)(uintptr_t)comp, true)) t->seen = t->used = now, t->cand = 0;
        if (g_ctNamed) __try {  // its names count now
            const DWORD vt = *(const DWORD*)comp;
            if (vt == g_ctVt[0] || vt == g_ctVt[1]) g_ctKidsFn((DWORD)(uintptr_t)comp);
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    CtStep(svc, now);
    if (now - g_ctLogAt >= g_ctLogMs) g_ctLogAt = now, CtLog(svc);
    return r;
}
// The resource manager's "which cache holds this key" scan 0x4B0070 (only caller: the register / unregister slot 0x4B00C0,
// manager vtable +0x5C) asks each cache with Find(key, NULL) and returns here. Its failed finds are not a Sim wanting a
// texture: EA's erase unregisters through it, and so does the compositor's own release later (al237 / al240: all the
// MISSED events came from here).
static const DWORD CT_LOAD_RET = 0x0060733E;  // the parent's Load: a miss rebuilds the child from the parent's data
static const DWORD CT_ASYNC_RET = 0x00729E80, CT_GET_RET = 0x00736718, CT_BLIT_RET = 0x005FA6D4;  // (see the header)
static int CtFromBit(DWORD at) { return at == CT_ASYNC_RET ? 0 : at == CT_GET_RET ? 1 : at == CT_BLIT_RET ? 2 : at == CT_LOAD_RET ? 3 : 4; }
static const DWORD CT_SCAN_RET = 0x004B009E;  // shortcut: with [MemDiag] RcRefCallers on, its slot hook is the caller and this test misses: run them apart
static void CtFound(bool r, const void* key, void** out, DWORD at, const DWORD* sp) {
    Ts3nCounters& z = ts3native_counters;
    const LONG mode = z.ctMode;
    __try {
        if (r && out && *out) {
            const DWORD o = (DWORD)(uintptr_t)*out, vt = *(const DWORD*)(uintptr_t)o;
            if (vt == g_ctVt[0] || vt == g_ctVt[1])
                if (CtEnt* t = CtSlot(o, false)) {
                    const DWORD now = GetTickCount(), last = now - t->seen < now - t->used ? t->seen : t->used, g = (now - last) / 60000;
                    if (t->seen) InterlockedIncrement(&g_ctGap[g < 1 ? 0 : g < 5 ? 1 : g < 10 ? 2 : g < 30 ? 3 : g < 60 ? 4 : 5]);
                    const int fb = CtFromBit(at);
                    InterlockedIncrement(&g_ctFromN[fb]);
                    if (t->cand) {
                        InterlockedIncrement(&z.ctWouldMiss);
                        const DWORD a1 = t->cand - t->seen, a2 = t->cand - t->used;
                        const int db = key ? CtInDb(key) : -1;
                        if (db >= 0) InterlockedIncrement(db ? &g_ctDbYes : &g_ctDbNo);
                        CtEvent("would miss", o, at, now - t->cand, a1 < a2 ? a1 : a2, sp, db, t->from);
                    }
                    t->used = now, t->cand = 0;
                    InterlockedOr(&t->from, 1 << fb);
                }
        } else if (!r && key && mode == 1 && at != CT_SCAN_RET) {
            const DWORD h = CtKeyHash(key);
            if (g_ctGone[h & (CT_GONE - 1)] == (LONG)h) {
                const int db = at == CT_LOAD_RET ? 1 : CtInDb(key);
                if (db > 0) InterlockedIncrement(&g_ctReload);  // the caller builds it again (a database, or the parent's data)
                else InterlockedIncrement(&z.ctMissed), CtEvent("MISSED after a trim", 0, at, 0, 0, sp, db);  // lost
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
static bool __fastcall CtFindHook(void* cache, void* edx, const void* key, void** out) {
    const bool r = g_ctFind(cache, edx, key, out);
    if (ts3native_counters.ctMode) CtFound(r, key, out, (DWORD)(uintptr_t)_ReturnAddress(), (const DWORD*)_AddressOfReturnAddress());
    return r;
}
static bool CtAlloc() {
    if (g_ct) return true;
    u8* m = (u8*)VirtualAlloc(NULL, CT_SLOTS * sizeof(CtEnt) + CT_GONE * 4 + 2 * CT_NAMED * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!m) return false;
    g_ct = (CtEnt*)m, g_ctGone = (volatile LONG*)(m + CT_SLOTS * sizeof(CtEnt));
    g_ctNm[0] = (LONG*)(m + CT_SLOTS * sizeof(CtEnt) + CT_GONE * 4), g_ctNm[1] = g_ctNm[0] + CT_NAMED;
    return true;
}
static void ApplyCasTrim(const wchar_t* ini) {
    const int mode = GetPrivateProfileIntW(L"CasTrim", L"Mode", 0, ini);
    const int age = GetPrivateProfileIntW(L"CasTrim", L"AgeSec", 1800, ini);
    const int buckets = GetPrivateProfileIntW(L"CasTrim", L"Buckets", 16, ini);
    const int maxErase = GetPrivateProfileIntW(L"CasTrim", L"MaxErase", 8, ini);
    g_ctNeedDb = GetPrivateProfileIntW(L"CasTrim", L"NeedDb", 0, ini) != 0;
    g_ctNamed = GetPrivateProfileIntW(L"CasTrim", L"Named", 1, ini) != 0;
    g_ctAsync = GetPrivateProfileIntW(L"CasTrim", L"Async", 1, ini) != 0;
    g_ctAgeMs = (DWORD)(age < 10 ? 10 : age > 36000 ? 36000 : age) * 1000;
    g_ctBuckets = buckets < 1 ? 1 : buckets > 256 ? 256 : buckets;
    g_ctMaxErase = maxErase < 1 ? 1 : maxErase > 64 ? 64 : maxErase;
    if (mode <= 0 && GetPrivateProfileIntW(L"CasTrim", L"Install", 0, ini) <= 0) return;
    if (!CtAlloc()) return (void)Log("FAIL cas trim: no memory for the tables (11 MB): not installed");
    RhRanges();  // the exe .text range for the event chains
    if (!g_ctFind)
        g_ctFind = (CtFindFn)InstallDetour("ResourceCache_Find", CT_FIND, kCtFindPro, sizeof(kCtFindPro), (void*)CtFindHook, &g_ctFind);
    if (!g_ctFind) return (void)Log("FAIL cas trim: cache Find 0x%08lX not detoured: not installed", CT_FIND);
    if (!g_ctInsert)
        g_ctInsert = (CtInsertFn)InstallDetour("CasCompositorRegister", CT_INSERT, kCtInsertPro, sizeof(kCtInsertPro), (void*)CtInsertHook, &g_ctInsert);
    if (!g_ctInsert) return (void)Log("FAIL cas trim: compositor insert 0x%08lX not detoured: not installed (the Find detour passes through)", CT_INSERT);
    Ts3nCounters& z = ts3native_counters;
    z.ctOn = 1, z.ctMode = mode < 0 ? 0 : mode > 2 ? 2 : mode;
    Log("cas trim: on (docs/191 step 2), mode %ld (%s): part compositors at 2 references, not inserted or found for %lu s; %d buckets a "
        "step, at most %d erases a step; %s; %s; %s", z.ctMode, z.ctMode == 1 ? "trim with EA's erase 0x5EE640" : z.ctMode == 2 ? "count only" : "armed, off",
        g_ctAgeMs / 1000, g_ctBuckets, g_ctMaxErase, g_ctNamed ? "only ones no other compositor names (Named 1)" : "named or not (Named 0)",
        g_ctNeedDb ? "only ones a database holds (NeedDb 1)" : "with or without a database (NeedDb 0)",
        g_ctAsync ? "never ones the async loader found (Async 1)" : "found by the async loader or not (Async 0)");
}
