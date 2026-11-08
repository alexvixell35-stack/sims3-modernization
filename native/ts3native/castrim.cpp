// [CasTrim] (docs/191 "Who holds the part compositors" step 2; counters v912 ct*, runtime switch ctMode +0x5CF0, --ab
// castrim): OFF by default. Included by ts3native.cpp after rctrim.cpp.
//
// The leak: the CAS texture compositor service ([0x11CE6A0]) keeps every part compositor that CAS/SimBuilder makes in a
// hash map at +0x104 (insert 0x5EECD0, from the SimBuilder part paths and the download-content loader). The insert also
// registers the compositor with the resource manager (manager vtable +0x5C, flag 1), so the ResourceCache finds it by key.
// The only erase, 0x5EE640 (find the key, unregister with flag 0, free the node and its reference), runs from the
// service's clear-all 0x5EEA90, i.e. only on a service reset [verified disassembly, TS3W 1.67]. Every outfit build makes
// new compositors with random instances, so the map and the cache grow for the whole session (docs/191: ~90% of rcN).
// Contains 0x5EDAB0 (export code: ExportLot, ExportWorld, Sim outfit export, thumbnails), the SimService save 0x5EFD60 and
// the service teardown 0x5F0BA0 read the map itself [verified, docs/539 §2.3]; in play a compositor is reached through the cache.
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
// Mode 3 (save sweep) and Mode 4 (its count-only form, probe P4): see "Mode 3" below; docs/539 §6, §7.
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
static bool CtSetAdd(LONG* t, DWORD h) {  // false: full
    for (DWORD i = (h * 2654435761u) >> 13, k = 0; k < 64; i = (i + 1) & (CT_NAMED - 1), k++) {
        if (t[i] == (LONG)h) return true;
        if (!t[i]) { t[i] = (LONG)h; return true; }
    }
    return false;
}
static void CtNmAdd(const void* key) {
    g_ctKids++;
    if (!CtSetAdd(g_ctNm[0], CtKeyHash(key))) g_ctNmFull[0] = true;
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

static void CtGoneRec(DWORD o, DWORD h);  // below: the ring of our erases (docs/539 §12.5)
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
                if (g_ctErase(svc, NULL, key)) erased++, z.ctErased++, g_ctGone[h & (CT_GONE - 1)] = (LONG)h, CtGoneRec(o, h);  // (mode 1)
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
    if (z.ctSaves) Log("cas trim: saves %ld, refused %ld; live %ld, registry %ld, doomed %ld, swept %ld, kept: household %ld, Named %ld, "
        "Async %ld, young %ld; doomed then found through the async loader %ld", z.ctSaves, z.ctRefused, z.ctLive, z.ctRegistry, z.ctDoomed,
        z.ctSwept, z.ctKeptHousehold, z.ctKeptNamed, z.ctKeptAsync, z.ctKeptYoung, z.ctDoomedAsync);
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
    const DWORD ra = (DWORD)(uintptr_t)_ReturnAddress();  // docs/539 P2
    if (ra == 0x005E299E) InterlockedIncrement(&z.ctFromBuilder);
    else if (ra == 0x005E8AB1) InterlockedIncrement(&z.ctFromCtor);
    else if (ra == 0x005EA943) InterlockedIncrement(&z.ctFromParse);
    else if (ra == 0x0099C299) InterlockedIncrement(&z.ctFromDownload);
    if (!g_ctTid) {
        g_ctTid = tid, g_ctLogAt = g_ctCyAt = now;
        Log("cas trim: first insert on thread %lu (the thread that loaded us: %lu); the walk runs on that one", tid, g_mainTid);
    }
    if (tid != g_ctTid) { InterlockedIncrement(&z.ctOtherThread); return r; }
    if (comp) {
        if (CtEnt* t = CtSlot((DWORD)(uintptr_t)comp, true)) t->seen = t->used = now, t->cand = 0;
        if (g_ctNamed && z.ctMode < 3) __try {  // its names count now (modes 3 and 4 read them at the save)
            const DWORD vt = *(const DWORD*)comp;
            if (vt == g_ctVt[0] || vt == g_ctVt[1]) g_ctKidsFn((DWORD)(uintptr_t)comp);
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    if (z.ctMode < 3) CtStep(svc, now);
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
                    if (t->cand && fb == 0 && mode >= 3) InterlockedIncrement(&z.ctDoomedAsync);  // docs/539 P4: an outfit load wants it
                    t->used = now, t->cand = 0;
                    InterlockedOr(&t->from, 1 << fb);
                }
        } else if (!r && key && (mode == 1 || mode == 3) && at != CT_SCAN_RET) {
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
// Mode 3 (docs/539 §6, ledger §446): the save sweep. EA's own save 0x5EFD60 builds the set of live compositors (outfit
// inventory closed over children, cross-referenced with the registry) and hands it to its writer 0x5ED3E0 as the fourth
// argument; a registry entry outside that set is not in the save. After that writer returns, one walk of the registry
// (Mode 4 stops here: count only) lists what may go. A compositor goes only when ALL of these hold; each rule's survivors
// keep their children (a kept child can name grandchildren, so rule 4 repeats to a fixed point):
//   1 refs == 2, 2 part compositor vtable, 3 not in EA's live set, 4 not named by any survivor, 5 key not in the keep list's
//   closure, 6 last insert or find older than MinAgeSec, 7 never found by the async loader (Async, al249: outfit loads find
//   their parts by key, ~50 min later).
// The live set's nodes hold a reference, so a live entry shows 3+ refs here; rule 3 stays as a check of its own.
// Keep list: before a save ts3fixes (CasTrimKeep.cs, text channel ops 14-16) pushes the outfit keys of the active household,
// the selected Sim, Sims of unknown scope, their CurrentOutfit and maternity maps; here they are closed over 0x5E7690.
// Order, P5: ts3fixes' op 14 is a fence. Mode 3 refuses (logs, sweeps nothing) unless script's AddRef pass 0x5EE090 ran
// after the fence and before the native save, the list is complete, CAS / Edit Town are closed, and the save runs on the
// insert thread. Erases run after the save returned, from ts3fixes' pump (op 18) on that same thread, MaxErase a tick,
// each one re-checked; the pump stops in CAS, Edit Town and during a save. No save data changes: only what EA did not write goes.
static const DWORD CT_SAVE = 0x005EFD60, CT_WRITE = 0x005ED3E0, CT_MARK = 0x005EE090, CT_CACHE_IC = 0x005A5D40, CT_FB_BRANCH = 0x005EA84A;
static const u8 kCtSavePro[] = {0x81, 0xEC, 0x40, 0x01, 0x00, 0x00};              // sub esp,0x140
static const u8 kCtWritePro[] = {0x83, 0xEC, 0x18, 0x33, 0xC0};                   // sub esp,0x18; xor eax,eax
static const u8 kCtMarkPro[] = {0x8B, 0x44, 0x24, 0x04, 0x83, 0xEC, 0x08};        // mov eax,[esp+4]; sub esp,8
static const u8 kCtCachePro[] = {0x33, 0xC0, 0x83, 0xEC, 0x10};                   // xor eax,eax; sub esp,0x10
static const u8 kCtFbPro[] = {0x80, 0x7C, 0x24, 0x13, 0x00};                      // cmp byte ptr [esp+0x13],0
typedef bool(__fastcall* CtSaveFn)(u8* svc, void* edx, void* stream);
typedef bool(__fastcall* CtWriteFn)(u8* svc, void* edx, void* a, void* b, void* c, const u8* live);
typedef void(__fastcall* CtMarkFn)(u8* svc, void* edx, const void* key);
typedef void*(__cdecl* CtCacheFn)(void* out, DWORD handle, const wchar_t* name, DWORD uLo, DWORD uHi, DWORD save);
struct CtCand { u8 key[16]; DWORD o; };
static const DWORD CT_PEND = 1 << 17;  // shortcut: 2.6 MB, one save's doomed entries; more wait for the next save (ctFull)
static CtCand* g_ctPend;
static CtSaveFn g_ctSave;
static CtWriteFn g_ctWrite;
static CtMarkFn g_ctMark;
static CtCacheFn g_ctCache;
static void* g_ctFbOrig;
static DWORD g_ctMinAgeMs = 60000, g_ctSvcAt = 0x011CE6A0;  // selftest: its own
static bool g_ctProbe;
static const DWORD CT_FENCE_MS = 10 * 60000;  // a list older than this belongs to a save that never ran (a save takes minutes at most)
static const int CT_KEEP_MAX = 8192;
static u8 g_ctKeep[CT_KEEP_MAX][16];
static int g_ctKeepN, g_ctKeepW;
static DWORD g_ctKeepWord[4], g_ctFenceAt, g_ctMarkAt;
static bool g_ctKeepOk, g_ctKeepOver, g_ctStand, g_ctPendGo, g_ctAfterSave;
static volatile LONG g_ctMarksF, g_ctMarksS, g_ctMarksLate;  // after the fence; since the last save; after the native save returned
static LONG g_ctPendN, g_ctLate;
static u8* g_ctSvc;
static LONG g_ctRefLog, g_ctMissLog;

// EA's hash maps here (registry +0x104, outfit map +0xD4, the save's sets): buckets +4, count +8, bucket = (key[0] ^ key[3]) %
// count (0xAD3F40), node {key, value +0x10, next +0x18}, a chain ends at 0.
static const u8* CtMapFind(const DWORD* b, DWORD n, const void* key) {
    if (!b || !n) return NULL;
    const DWORD* k = (const DWORD*)key;
    for (const u8* e = (const u8*)(uintptr_t)b[(k[0] ^ k[3]) % n]; e; e = *(const u8* const*)(e + 0x18))
        if (!memcmp(e, k, 16)) return e;
    return NULL;
}
static bool CtLiveHas(const u8* live, const void* key) { return CtMapFind(*(const DWORD* const*)(live + 4), *(const DWORD*)(live + 8), key) != NULL; }
static void CtKeepAdd(const void* key, const u8* live) {  // a compositor of the closure; one EA's set misses is logged (docs/539 §6)
    if (!CtSetAdd(g_ctNm[1], CtKeyHash(key))) g_ctNmFull[1] = true;
    if (CtLiveHas(live, key)) return;
    ts3native_counters.ctKeepMissed++;
    if (g_ctMissLog++ < 10) Log("cas trim: keep list key %08lX %08lX %08lX %08lX is not in EA's live set", ((const DWORD*)key)[0], ((const DWORD*)key)[1],
                                ((const DWORD*)key)[2], ((const DWORD*)key)[3]);
}
// The keep list closed over EA's 0x5E7690(set, children = 1) [verified: thiscall, ret 8, the same call the save's inventory
// makes], into a set built as 0x5EFD81-0x5EFDC6 builds its own, then freed as 0x5EFFFC-0x5F0023 frees it.
struct CtEaSet { DWORD pad, b, n, count, f0, f1, next, alloc; };
typedef void(__fastcall* CtCloseFn)(const void* outfit, void* edx, void* set, DWORD children);
typedef void(__fastcall* CtSetFreeFn)(void* set, void* edx, DWORD b, DWORD n);
typedef void(__cdecl* CtFreeFn)(DWORD p);
static void CtKeepEa(u8* svc, const u8* live) {
    CtEaSet s = {0, 0x0114C900, 1, 0, *(const DWORD*)(uintptr_t)0x0107A538, *(const DWORD*)(uintptr_t)0x00F98A58, 0, 0x00FE50CC};
    for (int i = 0; i < g_ctKeepN; i++) {
        const u8* e = CtMapFind(*(const DWORD* const*)(svc + 0xD8), *(const DWORD*)(svc + 0xDC), g_ctKeep[i]);
        if (e) ((CtCloseFn)(uintptr_t)0x005E7690)(*(const void* const*)(e + 0x10), NULL, &s, 1);
    }
    for (DWORD i = 0; i < s.n; i++)
        for (const u8* e = (const u8*)(uintptr_t)((const DWORD*)(uintptr_t)s.b)[i]; e; e = *(const u8* const*)(e + 0x18)) CtKeepAdd(e, live);
    ((CtSetFreeFn)(uintptr_t)0x005ED820)(&s, NULL, s.b, s.n);
    if (s.n > 1) ((CtFreeFn)(uintptr_t)0x004E3F70)(s.b);
}
static void (*g_ctKeepFn)(u8* svc, const u8* live) = CtKeepEa;  // selftest: a fake

// docs/539 §12.5 (0.36.194, castrimdiag1108): a detector on the save's call 0x5EFE11 -> 0x5E7690, after the 11-08 travel-save
// crash at 0x5E772C ([rec+0x38] slot 1 held 0x40A00000, not a compositor). 0x5E7690 (thiscall (outfit, set, children), ret 8)
// walks the part records [outfit+0x98] to [outfit+0x9C] (0x68 bytes each); [rec+0x14] and [rec+0x38] each point at 5
// compositor pointers [verified disassembly, TS3W 1.67]. Before EA runs, every non-null slot must start with a part
// compositor vtable; one that does not is logged (first 20) with the outfit, the record, CasTrim's slot for the value and
// whether one of our erases took that memory (the ring below). Then EA runs unchanged: it logs, it never skips or fixes.
// All reads are under __try. At the call esi is EA's outfit map node {key, outfit +0x10, next +0x18} (0x5EFE0E), kept by CtInvStub.
struct CtGoneEv { DWORD o, tick, h; };
static const DWORD CT_GRING = 4096;  // the last erases (CtStep, CtSweepStep): object, tick, key hash
static CtGoneEv g_ctGRing[CT_GRING];
static volatile LONG g_ctGSeq;
static void CtGoneRec(DWORD o, DWORD h) {
    const LONG s = InterlockedIncrement(&g_ctGSeq);
    CtGoneEv& e = g_ctGRing[(DWORD)(s - 1) & (CT_GRING - 1)];
    e.o = o, e.tick = GetTickCount(), e.h = h;
}
static bool CtGoneText(DWORD v, char* out, size_t cap) {  // the newest erase whose 0x68-byte object holds v
    const LONG top = g_ctGSeq;
    for (LONG s = top; s > 0 && top - s < (LONG)CT_GRING; s--) {
        const CtGoneEv& e = g_ctGRing[(DWORD)(s - 1) & (CT_GRING - 1)];
        if (e.o && v - e.o < 0x68) {
            sprintf_s(out, cap, "ERASED by CasTrim %lu s ago (object 0x%08lX +0x%lX, key hash %08lX, erase %ld of %ld)", (GetTickCount() - e.tick) / 1000,
                      e.o, v - e.o, e.h, s, top);
            return true;
        }
    }
    sprintf_s(out, cap, "never erased by CasTrim (ring: the last %ld of %ld erases)", top < (LONG)CT_GRING ? top : (LONG)CT_GRING, top);
    return false;
}
static void CtSlotText(DWORD v, char* out, size_t cap) {
    const CtEnt* t = g_ct ? CtSlot(v, false) : NULL;
    const DWORD now = GetTickCount();
    if (!t) sprintf_s(out, cap, "no CasTrim slot");
    else sprintf_s(out, cap, "CasTrim slot: seen %ld s, used %ld s, marked %ld s ago (-1 never), found by 0x%lX", t->seen ? (LONG)(now - t->seen) / 1000 : -1L,
                   t->used ? (LONG)(now - t->used) / 1000 : -1L, t->cand ? (LONG)(now - t->cand) / 1000 : -1L, t->from);
}
typedef void(__fastcall* CtInvFn)(void* outfit, void* edx, void* set, DWORD children);
static CtInvFn g_ctInvEa = (CtInvFn)(uintptr_t)0x005E7690;  // selftest: a fake
static const u8* volatile g_ctInvNode;                       // EA's esi at the call (CtInvStub)
static LONG g_ctInvSave, g_ctInvCalls, g_ctInvRecs, g_ctInvSlots, g_ctInvBadSave, g_ctInvBad, g_ctInvLogs, g_ctInvGone;
static LONGLONG g_ctInvQpc;
static void CtInvBad(const char* what, const u8* node, const u8* outfit, LONG ri, LONG rn, const u8* rec, int arr, int slot, DWORD v) {
    g_ctInvBadSave++, g_ctInvBad++;
    char gone[160], sl[160], rb[0x68 * 3 + 4] = "unreadable";
    if (CtGoneText(v, gone, sizeof(gone))) g_ctInvGone++;
    if (g_ctInvLogs++ >= 20) return;
    DWORD key[4] = {}, w[4] = {};
    bool vOk = false;
    __try { if (node && *(const u8* const*)(node + 0x10) == outfit) memcpy(key, node, 16); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { if (v) memcpy(w, (const void*)(uintptr_t)v, 16), vOk = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { if (rec) Hex(rec, 0x68, rb, sizeof(rb)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(rb, "unreadable"); }
    CtSlotText(v, sl, sizeof(sl));
    Log("cas trim: inventory bad slot: %s; save #%ld (travel flags %d), %ld s after world load #%ld; outfit 0x%08lX key %08lX %08lX %08lX %08lX, "
        "record %ld of %ld at 0x%08lX, array +0x%X slot %d: value 0x%08lX, first dwords %s %08lX %08lX %08lX %08lX; %s; %s; record bytes: %s",
        what, g_ctInvSave, g_swTravel, g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, (DWORD)(uintptr_t)outfit,
        key[0], key[1], key[2], key[3], ri, rn, (DWORD)(uintptr_t)rec, arr < 0 ? 0 : arr ? 0x38 : 0x14, slot, v, vOk ? "" : "(unreadable)", w[0], w[1],
        w[2], w[3], sl, gone, rb);
}
static void CtInvCheck(const u8* node, const u8* outfit) {
    LONG ri = -1, rn = 0;
    const u8* rec = NULL;
    int arr = -1, slot = -1;
    __try {
        const u8 *b = *(const u8* const*)(outfit + 0x98), *e = *(const u8* const*)(outfit + 0x9C);
        if (e < b || (e - b) % 0x68 || (e - b) / 0x68 > 64) return CtInvBad("record range not 0-64 records of 0x68 (record = begin, value = end, of = bytes)", node, outfit, -1, (LONG)(e - b), b, -1, -1, (DWORD)(uintptr_t)e);
        rn = (LONG)((e - b) / 0x68), g_ctInvRecs += rn;
        for (ri = 0; ri < rn; ri++) {
            rec = b + ri * 0x68;
            for (arr = 0; arr < 2; arr++) {
                const DWORD* a = *(const DWORD* const*)(rec + (arr ? 0x38 : 0x14));
                for (slot = 0; slot < 5; slot++) {
                    const DWORD v = a[slot];
                    if (!v) continue;
                    g_ctInvSlots++;
                    DWORD vt = 0;
                    bool ok = false;
                    __try { vt = *(const DWORD*)(uintptr_t)v, ok = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
                    if (!ok || (vt != g_ctVt[0] && vt != g_ctVt[1])) CtInvBad(ok ? "not a part compositor" : "value unreadable", node, outfit, ri, rn, rec, arr, slot, v);
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        CtInvBad("fault reading the outfit, a record or an array pointer", node, outfit, ri, rn, rec, arr, slot, 0);
    }
}
static void __fastcall CtInvHook(void* outfit, void* edx, void* set, DWORD children) {
    LARGE_INTEGER q0, q1;
    QueryPerformanceCounter(&q0);
    g_ctInvCalls++;
    CtInvCheck(g_ctInvNode, (const u8*)outfit);
    QueryPerformanceCounter(&q1);
    g_ctInvQpc += q1.QuadPart - q0.QuadPart;
    g_ctInvEa(outfit, edx, set, children);
}
__declspec(naked) static void CtInvStub() {  // the call site's target: EA's esi (the outfit map node) kept, then the hook (ret 8 there)
    __asm {
        mov dword ptr [g_ctInvNode], esi
        jmp CtInvHook
    }
}
static void CtInvSaveLog() {  // after the save returned (CtSaveHook): one line per save that ran the inventory
    if (!g_ctInvCalls) return;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    Log("cas trim: inventory check save #%ld: outfits %ld, records %ld, slots %ld, bad %ld (session %ld, %ld in an erased object), %.2f ms",
        g_ctInvSave, g_ctInvCalls, g_ctInvRecs, g_ctInvSlots, g_ctInvBadSave, g_ctInvBad, g_ctInvGone, f.QuadPart ? (double)g_ctInvQpc * 1000.0 / (double)f.QuadPart : 0.0);
    g_ctInvCalls = g_ctInvRecs = g_ctInvSlots = g_ctInvBadSave = 0, g_ctInvQpc = 0;
}
// crashfx.cpp, a fault at 0x5E76DA (array [rec+0x14]) or 0x5E772C ([rec+0x38]) in 0x5E7690, any thread: EA's frame there
// (sub esp,0xC + 4 pushes) has the record at [esp+0x10], the outfit +0x14, the end +0x18, the return +0x1C, the set +0x20,
// children +0x24; esi = the slot's value, ebx = slot * 4 [verified disassembly; the 11-08 EA report's stack data].
static volatile LONG g_ctInvFaults;
static void CtInvFault(const CONTEXT* c) {
    if (InterlockedIncrement(&g_ctInvFaults) > 2) return;
    DWORD f[6] = {};
    __try { memcpy(f, (const void*)(uintptr_t)(c->Esp + 0x10), sizeof(f)); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    char gone[160], grec[160], sl[160], rb[0x68 * 3 + 4] = "unreadable";
    CtGoneText(c->Esi, gone, sizeof(gone)), CtGoneText(f[0], grec, sizeof(grec)), CtSlotText(c->Esi, sl, sizeof(sl));
    __try { if (f[0]) Hex((const u8*)(uintptr_t)f[0], 0x68, rb, sizeof(rb)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(rb, "unreadable"); }
    DWORD begin = 0;
    __try { if (f[1]) begin = *(const DWORD*)(uintptr_t)(f[1] + 0x98); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("crash forensics: cas trim inventory fault at 0x%08lX (array +0x%X slot %lu): value esi 0x%08lX (%s; %s), record 0x%08lX (index %ld, %ld left; %s), "
        "outfit 0x%08lX records 0x%08lX-0x%08lX, return 0x%08lX set 0x%08lX children %lu; outfit map node at the save's call 0x%08lX; inventory checks this "
        "save %ld, bad %ld (session %ld); save #%ld (travel flags %d), %ld s after world load #%ld; record bytes: %s",
        c->Eip, c->Eip == 0x005E772C ? 0x38 : 0x14, c->Ebx / 4, c->Esi, sl, gone, f[0], begin && f[0] >= begin ? (LONG)((f[0] - begin) / 0x68) : -1L,
        f[2] > f[0] ? (LONG)((f[2] - f[0]) / 0x68) : -1L, grec, f[1], begin, f[2], f[3], f[4], f[5], (DWORD)(uintptr_t)g_ctInvNode, g_ctInvCalls,
        g_ctInvBadSave, g_ctInvBad, g_ctInvSave, g_swTravel, g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, rb);
}

static const char* CtRefuse(DWORD now, DWORD tid, const u8* live) {
    if (!g_ctTid) return "no insert seen yet";
    if (tid != g_ctTid) return "the save is not on the insert thread";
    if (!g_ctFenceAt || (LONG)(now - g_ctFenceAt) > (LONG)CT_FENCE_MS) return "no keep list for this save";  // signed: the fence is tick | 1
    if (g_ctStand) return "CAS or Edit Town is open";
    if (!g_ctKeepOk) return "the keep list is incomplete";
    if (!g_ctMarksF) return "P5: no script mark between the keep list and the native save";
    if (!*(const DWORD*)(live + 0xC)) return "EA's live set is empty";
    return NULL;
}
struct CtScanN { LONG reg, other, refs, live, household, young, async, overflow, noSlot, named, rounds; };
// The walk of rules 1-7. The thread that runs the save; the caller's __try ends it on a fault.
static void CtScan(u8* svc, const u8* live, DWORD now, bool act, CtScanN& c) {
    memset(g_ctNm[0], 0, CT_NAMED * 4), memset(g_ctNm[1], 0, CT_NAMED * 4);
    g_ctNmFull[0] = g_ctNmFull[1] = false, g_ctCycles = 0, g_ctCyB = 0;  // Mode 1 or 2 after this starts its cycle again
    if (g_ctFenceAt) g_ctKeepFn(svc, live);
    const DWORD* b = *(const DWORD* const*)(svc + 0x108);
    const DWORD nb = *(const DWORD*)(svc + 0x10C);
    LONG n = 0;
    for (DWORD i = 0; b && i < nb; i++)
        for (const u8* e = (const u8*)(uintptr_t)b[i]; e; e = *(const u8* const*)(e + 0x18)) {
            c.reg++;
            const DWORD o = *(const DWORD*)(e + 0x10), vt = o ? *(const DWORD*)(uintptr_t)o : 0;
            if (vt != g_ctVt[0] && vt != g_ctVt[1]) { c.other++; continue; }
            CtEnt* t = CtSlot(o, true);
            if (t && !t->seen) t->seen = t->used = now;
            const DWORD a1 = t ? now - t->seen : 0, a2 = t ? now - t->used : 0, age = a1 < a2 ? a1 : a2;
            LONG* kept = !t ? &c.noSlot : *(const LONG*)(uintptr_t)(o + 4) >> 8 != 2 ? &c.refs : CtLiveHas(live, e) ? &c.live
                         : CtNmHas(g_ctNm[1], CtKeyHash(e)) ? &c.household : age < g_ctMinAgeMs ? &c.young
                         : g_ctAsync && (t->from & 1) ? &c.async : n >= (LONG)CT_PEND ? &c.overflow : NULL;
            if (t) t->cand = 0;
            if (kept) { ++*kept; if (g_ctNamed) g_ctKidsFn(o); continue; }  // a survivor names its children
            memcpy(g_ctPend[n].key, e, 16), g_ctPend[n++].o = o;
        }
    bool moved = g_ctNamed;
    while (moved && c.rounds < 64) {  // rule 4 to a fixed point
        moved = false, c.rounds++;
        for (LONG i = n - 1; i >= 0; i--) {
            const DWORD h = CtKeyHash(g_ctPend[i].key);
            if (!CtNmHas(g_ctNm[0], h)) continue;
            g_ctKidsFn(g_ctPend[i].o);
            g_ctPend[i] = g_ctPend[--n], c.named++, moved = true;
        }
    }
    if (moved || g_ctNmFull[0] || g_ctNmFull[1]) c.overflow += n, n = 0;  // no fixed point, or a full table: keep everything
    for (LONG i = 0; i < n; i++)
        if (CtEnt* t = CtSlot(g_ctPend[i].o, false)) t->cand = now;
    ts3native_counters.ctDoomed += n;
    g_ctPendN = act ? n : 0;
}
// After EA's writer 0x5ED3E0 returned: live set known, the rest of the save still to run.
static void CtSweepSave(u8* svc, const u8* live) {
    Ts3nCounters& z = ts3native_counters;
    const LONG mode = z.ctMode;
    if (mode < 3 || !live) return;
    const DWORD now = GetTickCount(), tid = GetCurrentThreadId();
    const char* no = CtRefuse(now, tid, live);
    if (no) {
        z.ctRefused++;
        if (g_ctRefLog++ < 40) Log("cas trim: save #%ld: %s %s", z.ctSaves, mode == 3 ? "no sweep:" : "would refuse:", no);
        if (mode == 3) return;
    }
    z.ctLive = *(const LONG*)(live + 0xC);
    z.ctMarksFence = g_ctMarksF;
    g_ctSvc = svc;
    CtScanN c = {};
    LARGE_INTEGER q0, q1, f;
    QueryPerformanceCounter(&q0);
    __try {
        CtScan(svc, live, now, mode == 3, c);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        z.ctMode = 0, g_ctPendN = 0;
        Log("FAIL cas trim: fault in the save sweep: off for the session");
        return;
    }
    QueryPerformanceCounter(&q1), QueryPerformanceFrequency(&f);
    z.ctRegistry = c.reg, z.ctKeptHousehold += c.household, z.ctKeptNamed += c.named, z.ctKeptAsync += c.async, z.ctKeptYoung += c.young;
    z.ctFull += c.noSlot + c.overflow;
    Log("cas trim: save #%ld mode %ld: registry %ld, live %ld, keep keys %d; kept: not a part %ld, refs %ld, live %ld, household %ld, young %ld, "
        "Async %ld, Named %ld (%ld rounds), table or fixed-point limit %ld, no slot %ld; doomed %ld (total), %lu ms", z.ctSaves, mode, c.reg, z.ctLive,
        g_ctKeepN, c.other, c.refs, c.live, c.household, c.young, c.async, c.named, c.rounds, c.overflow, c.noSlot, z.ctDoomed,
        (DWORD)((q1.QuadPart - q0.QuadPart) * 1000 / f.QuadPart));
}
// Op 18: one batch of erases, on the insert thread, after the save returned. Each entry is checked again: still in the
// registry as the same object, 2 references, not found since it was doomed.
static u8 CtSweepStep() {
    Ts3nCounters& z = ts3native_counters;
    if (z.ctMode != 3 || g_ctPendN <= 0 || !g_ctPendGo) return 0;
    if (GetCurrentThreadId() != g_ctTid) {
        g_ctPendN = 0;
        Log("cas trim: sweep dropped: the pump is not on the insert thread");
        return 0;
    }
    __try {
        for (int n = 0; g_ctPendN > 0 && n < g_ctMaxErase;) {
            const CtCand c = g_ctPend[--g_ctPendN];
            const u8* e = CtMapFind(*(const DWORD* const*)(g_ctSvc + 0x108), *(const DWORD*)(g_ctSvc + 0x10C), c.key);
            CtEnt* t = CtSlot(c.o, false);
            if (!e || *(const DWORD*)(e + 0x10) != c.o || !t || !t->cand || *(const LONG*)(uintptr_t)(c.o + 4) >> 8 != 2) { g_ctLate++; continue; }
            const DWORD h = CtKeyHash(c.key);
            t->seen = t->cand = 0;  // the address may come back as a new compositor
            n++;
            if (g_ctErase(g_ctSvc, NULL, c.key)) z.ctSwept++, g_ctGone[h & (CT_GONE - 1)] = (LONG)h, CtGoneRec(c.o, h);  // remembered after the erase: see CtStep
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        z.ctMode = 0, g_ctPendN = 0;
        Log("FAIL cas trim: fault in a sweep erase: off for the session");
        return 0;
    }
    if (g_ctPendN > 0) return 1;
    Log("cas trim: sweep done: swept %ld in total, %ld changed since the save (kept)", z.ctSwept, g_ctLate);
    return 0;
}
// Text channel ops 14-19 (userdiag.cpp; ts3fixes CasTrimKeep.cs). Sim thread. An old build answers 0: ts3fixes then sends nothing.
static u8 CtOp(int op, DWORD lo) {
    Ts3nCounters& z = ts3native_counters;
    if (op == 19) return g_ctWrite != NULL;
    if (z.ctMode < 3) return 0;
    switch (op) {
        case 14:  // a save starts: lo & 1 = CAS or Edit Town open
            g_ctKeepN = g_ctKeepW = 0, g_ctKeepOk = g_ctKeepOver = false, g_ctPendN = 0, g_ctPendGo = false, g_ctMarksF = 0;
            g_ctStand = (lo & 1) != 0, g_ctFenceAt = GetTickCount() | 1, g_ctAfterSave = false;
            return 1;
        case 15:  // one word of an outfit key
            g_ctKeepWord[g_ctKeepW++] = lo;
            if (g_ctKeepW < 4) return 1;
            g_ctKeepW = 0;
            if (g_ctKeepN < CT_KEEP_MAX) memcpy(g_ctKeep[g_ctKeepN++], g_ctKeepWord, 16);
            else g_ctKeepOver = true;
            return 1;
        case 16:  // the list ended: lo = complete (a script fault or an unknown state sends 0)
            g_ctKeepOk = lo && !g_ctKeepOver && !g_ctKeepW, z.ctKeepKeys = g_ctKeepN;
            return 1;
        case 17:  // the save returned: lo = it saved; 1 = there is something to sweep (start the pump)
            g_ctFenceAt = 0;
            if (g_ctMarksLate) Log("cas trim: P5: %ld script marks after the native save #%ld returned", g_ctMarksLate, z.ctSaves);
            if (!lo) g_ctPendN = 0;
            g_ctPendGo = g_ctPendN > 0;
            return g_ctPendGo;
        case 18: return CtSweepStep();
    }
    return 0;
}
// docs/539 P5: the save's entry, order and thread. Mode 3's "during a save" ends with this hook.
static bool __fastcall CtSaveHook(u8* svc, void* edx, void* stream) {
    Ts3nCounters& z = ts3native_counters;
    g_ctInvSave++;  // docs/539 §12.5: the inventory check's save number, every mode
    if (z.ctMode < 3) {
        const bool r0 = g_ctSave(svc, edx, stream);
        CtInvSaveLog();
        return r0;
    }
    const DWORD t0 = GetTickCount(), at = (DWORD)(uintptr_t)_ReturnAddress();
    z.ctSaves++, g_ctPendN = 0, g_ctPendGo = false, g_ctMarksLate = 0, g_ctAfterSave = false;
    int flag = -1;
    __try { flag = *(const u8*)(uintptr_t)(*(const DWORD*)(uintptr_t)0x011ECBC4 + 0x45); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const LONG marks = g_ctMarksF, sinceSave = g_ctMarksS;
    g_ctMarksS = 0;
    const bool r = g_ctSave(svc, edx, stream);
    if (!r) g_ctPendN = 0;
    g_ctAfterSave = true;
    Log("cas trim: P5 save #%ld from 0x%08lX, thread %lu (insert thread %lu, loader %lu), [0x11ECBC4]+0x45 = %d: keep list %ld ms before, marks after "
        "it %ld, since the last save %ld, last mark %ld ms before; ran %lu ms, result %d", z.ctSaves, at, GetCurrentThreadId(), g_ctTid, g_mainTid, flag,
        g_ctFenceAt ? (LONG)(t0 - g_ctFenceAt) : -1L, marks, sinceSave, g_ctMarkAt ? (LONG)(t0 - g_ctMarkAt) : -1L, GetTickCount() - t0, r);
    CtInvSaveLog();
    return r;
}
static bool __fastcall CtWriteHook(u8* svc, void* edx, void* a, void* b, void* c, const u8* live) {
    const bool r = g_ctWrite(svc, edx, a, b, c, live);
    if (r) CtSweepSave(svc, live);
    return r;
}
static void __fastcall CtMarkHook(u8* svc, void* edx, const void* key) {
    Ts3nCounters& z = ts3native_counters;
    if (z.ctMode >= 3) {
        InterlockedIncrement(&z.ctMarks), InterlockedIncrement(&g_ctMarksF), InterlockedIncrement(&g_ctMarksS);
        g_ctMarkAt = GetTickCount();
        if (g_ctAfterSave) InterlockedIncrement(&g_ctMarksLate);  // P5: the order the other way round
    }
    g_ctMark(svc, edx, key);
}
// docs/539 P1: CacheOutfit calls by name, up to the first digit (an LPWStr: the extern is a DllImport). Sim thread.
struct CtName { char s[28]; volatile LONG n; };
static CtName g_ctNames[48];
static volatile LONG g_ctNameOther;
static void CtCountName(const wchar_t* w) {
    Ts3nCounters& z = ts3native_counters;
    InterlockedIncrement(&z.ctCacheOutfit);
    char b[28] = "?";
    __try {
        for (int i = 0; w && i < 27 && w[i] && !(w[i] >= L'0' && w[i] <= L'9'); i++) b[i] = w[i] >= 0x20 && w[i] < 0x7F ? (char)w[i] : '?';
    } __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    for (CtName& e : g_ctNames) {
        if (!e.s[0]) memcpy(e.s, b, sizeof(b));
        if (!strcmp(e.s, b)) { e.n++; return; }
    }
    g_ctNameOther++;
}
static void* __cdecl CtCacheHook(void* out, DWORD h, const wchar_t* name, DWORD uLo, DWORD uHi, DWORD save) {
    CtCountName(name);
    return g_ctCache(out, h, name, uLo, uHi, save);
}
// docs/539 P6: 0x5EA84A tests the "a part's stored compositor failed to load" flag; its 5-byte cmp runs in the trampoline right
// after this, so the flags need no saving. The frame is EA's: [esp+0x13] is that flag.
__declspec(naked) static void CtFbStub() {
    __asm {
        cmp byte ptr [esp + 0x13], 0
        je go
        lock inc dword ptr [ts3native_counters.ctFbFlag]
    go:
        jmp dword ptr [g_ctFbOrig]
    }
}
typedef bool(__fastcall* CtRebuildFn)(void* preset, void* edx, void* list, DWORD a, DWORD b);
static bool __fastcall CtRebuildParse(void* preset, void* edx, void* list, DWORD a, DWORD b) {  // 0x5EA8EE: the miss rebuilds from the preset
    InterlockedIncrement(&ts3native_counters.ctFbRebuilt);
    return ((CtRebuildFn)(uintptr_t)0x005BFAB0)(preset, edx, list, a, b);
}
// docs/539 §9.4, issue #40 item 6: the two steadiest eaRes growers of al239, by owner (allocated KB, not live: RcCensus has the live side).
typedef void*(__fastcall* CtAllocFn)(void* heap, void* edx, DWORD size, DWORD flag);
static void* __fastcall CtGeoAlloc(void* heap, void* edx, DWORD size, DWORD flag) {  // 0x5C7901, CAS/GeometryResource load 0x5C75E0, buffer +0x80
    InterlockedExchangeAdd(&ts3native_counters.ctGeoKB, (LONG)(size + 512) >> 10);
    return ((CtAllocFn)(uintptr_t)0x004E69D0)(heap, edx, size, flag);
}
static void* __fastcall CtBlendAlloc(void* heap, void* edx, DWORD size, DWORD flag) {  // 0x5A2FFA, CAS/BlendGeometryResource load 0x5A2D70, buffer +0x30
    InterlockedExchangeAdd(&ts3native_counters.ctBlendKB, (LONG)(size + 512) >> 10);
    return ((CtAllocFn)(uintptr_t)0x004E69D0)(heap, edx, size, flag);
}
// docs/539 P3 + the counters of this block, one line per [MemDiag] sample (the memory worker: EA's maps are read without a lock
// as RcCensus reads them, under a guard; a rehash mid-walk costs one sample).
static void CtMemLine(LONG n, const char* tag) {
    if (!g_ctProbe) return;
    Ts3nCounters& z = ts3native_counters;
    __try {
        const u8* svc = *(const u8* const*)(uintptr_t)g_ctSvcAt;
        if (svc) {
            const DWORD* b = *(const DWORD* const*)(svc + 0xD8);
            const DWORD nb = *(const DWORD*)(svc + 0xDC);
            LONG zero = 0;
            for (DWORD i = 0, k = 0; b && i < nb && k < 4000000; i++)
                for (const u8* e = (const u8*)(uintptr_t)b[i]; e && k < 4000000; e = *(const u8* const*)(e + 0x18), k++)
                    if (!*(const DWORD*)(e + 0x14)) zero++;
            z.ctRegistry = *(const LONG*)(svc + 0x110), z.ctOutfits = *(const LONG*)(svc + 0xE0), z.ctOutfit0 = zero;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const LONG other = z.ctInserts - z.ctFromBuilder - z.ctFromCtor - z.ctFromParse - z.ctFromDownload;
    Log("diag: castrim #%ld %s reg=%ld outfits=%ld outfit0=%ld inserts=%ld builder=%ld ctor=%ld parse=%ld download=%ld other=%ld cacheOutfit=%ld "
        "fbFlag=%ld fbRebuilt=%ld geoKB=%ld blendKB=%ld saves=%ld live=%ld doomed=%ld swept=%ld wouldMiss=%ld missed=%ld doomedAsync=%ld", n, tag, z.ctRegistry,
        z.ctOutfits, z.ctOutfit0, z.ctInserts, z.ctFromBuilder, z.ctFromCtor, z.ctFromParse, z.ctFromDownload, other, z.ctCacheOutfit, z.ctFbFlag, z.ctFbRebuilt,
        z.ctGeoKB, z.ctBlendKB, z.ctSaves, z.ctLive, z.ctDoomed, z.ctSwept, z.ctWouldMiss, z.ctMissed, z.ctDoomedAsync);
    char line[400];
    int w = 0;
    for (const CtName& e : g_ctNames) {
        if (!e.s[0]) break;
        w += sprintf_s(line + w, sizeof(line) - w, " %s=%ld", e.s, e.n);
        if (w > 330) Log("diag: castrim names #%ld%s", n, line), w = 0;
    }
    if (w || g_ctNameOther) Log("diag: castrim names #%ld%s other=%ld", n, w ? line : "", g_ctNameOther);
}
static bool CtInstallSweep() {
    if (!g_ctSave) g_ctSave = (CtSaveFn)InstallDetour("SimService_Save", CT_SAVE, kCtSavePro, sizeof(kCtSavePro), (void*)CtSaveHook, &g_ctSave);
    if (!g_ctMark) g_ctMark = (CtMarkFn)InstallDetour("SimOutfit_AddRef", CT_MARK, kCtMarkPro, sizeof(kCtMarkPro), (void*)CtMarkHook, &g_ctMark);
    if (g_ctSave && g_ctMark && !g_ctWrite)
        g_ctWrite = (CtWriteFn)InstallDetour("Compositors_Write", CT_WRITE, kCtWritePro, sizeof(kCtWritePro), (void*)CtWriteHook, &g_ctWrite);
    return g_ctWrite != NULL;  // the last in: without it no sweep runs, and the other two only pass through
}
static void CtInstallProbes() {
    if (!g_ctCache) g_ctCache = (CtCacheFn)InstallDetour("IC_SimBuilderCacheOutfit", CT_CACHE_IC, kCtCachePro, sizeof(kCtCachePro), (void*)CtCacheHook, &g_ctCache);
    if (!g_ctFbOrig) g_ctFbOrig = InstallDetour("Parse_FallbackBranch", CT_FB_BRANCH, kCtFbPro, sizeof(kCtFbPro), (void*)CtFbStub, &g_ctFbOrig);
    static const CallSite sites[] = {{0x005EA8EE, 0x005BFAB0, (void*)CtRebuildParse}, {0x005C7901, 0x004E69D0, (void*)CtGeoAlloc}, {0x005A2FFA, 0x004E69D0, (void*)CtBlendAlloc}};
    PatchCalls("cas trim probe sites", sites, 3);
    // docs/539 §12.5: the inventory detector, a site of its own so the 3 above keep their install line
    static const CallSite inv[] = {{0x005EFE11, 0x005E7690, (void*)CtInvStub}};
    if (PatchCalls("cas trim inventory check site", inv, 1))
        Log("cas trim: inventory check on (docs/539 §12.5): each save's outfit inventory call 0x5EFE11 checks every part record slot first; "
            "erase ring %lu", CT_GRING);
}
static bool CtAlloc() {
    if (g_ct) return true;
    u8* m = (u8*)VirtualAlloc(NULL, CT_SLOTS * sizeof(CtEnt) + CT_GONE * 4 + 2 * CT_NAMED * 4 + CT_PEND * sizeof(CtCand), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!m) return false;
    g_ct = (CtEnt*)m, g_ctGone = (volatile LONG*)(m + CT_SLOTS * sizeof(CtEnt));
    g_ctNm[0] = (LONG*)(m + CT_SLOTS * sizeof(CtEnt) + CT_GONE * 4), g_ctNm[1] = g_ctNm[0] + CT_NAMED;
    g_ctPend = (CtCand*)(g_ctNm[1] + CT_NAMED);
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
    const int minAge = GetPrivateProfileIntW(L"CasTrim", L"MinAgeSec", 60, ini);
    g_ctMinAgeMs = (DWORD)(minAge < 0 ? 0 : minAge > 36000 ? 36000 : minAge) * 1000;
    g_ctProbe = GetPrivateProfileIntW(L"CasTrim", L"Probe", 0, ini) > 0;
    if (mode <= 0 && !g_ctProbe && GetPrivateProfileIntW(L"CasTrim", L"Install", 0, ini) <= 0) return;
    if (!CtAlloc()) return (void)Log("FAIL cas trim: no memory for the tables (14 MB): not installed");
    RhRanges();  // the exe .text range for the event chains
    if (!g_ctFind)
        g_ctFind = (CtFindFn)InstallDetour("ResourceCache_Find", CT_FIND, kCtFindPro, sizeof(kCtFindPro), (void*)CtFindHook, &g_ctFind);
    if (!g_ctFind) return (void)Log("FAIL cas trim: cache Find 0x%08lX not detoured: not installed", CT_FIND);
    if (!g_ctInsert)
        g_ctInsert = (CtInsertFn)InstallDetour("CasCompositorRegister", CT_INSERT, kCtInsertPro, sizeof(kCtInsertPro), (void*)CtInsertHook, &g_ctInsert);
    if (!g_ctInsert) return (void)Log("FAIL cas trim: compositor insert 0x%08lX not detoured: not installed (the Find detour passes through)", CT_INSERT);
    const bool sweep = CtInstallSweep();
    if (g_ctProbe) CtInstallProbes();
    Ts3nCounters& z = ts3native_counters;
    z.ctOn = 1, z.ctMode = mode < 0 ? 0 : mode > 4 ? 4 : mode;
    if (z.ctMode >= 3 && !sweep) z.ctMode = 0, Log("FAIL cas trim: the save hooks are not in: modes 3 and 4 off");
    if (z.ctMode >= 3) Log("cas trim: mode %ld (%s): at each save, part compositors outside EA's live set; MinAgeSec %lu, Probe %d", z.ctMode,
                           z.ctMode == 3 ? "sweep" : "count only", g_ctMinAgeMs / 1000, g_ctProbe);
    Log("cas trim: on (docs/191 step 2), mode %ld (%s): part compositors at 2 references, not inserted or found for %lu s; %d buckets a "
        "step, at most %d erases a step; %s; %s; %s", z.ctMode, z.ctMode == 1 ? "trim with EA's erase 0x5EE640" : z.ctMode == 2 ? "count only" : "armed, off",
        g_ctAgeMs / 1000, g_ctBuckets, g_ctMaxErase, g_ctNamed ? "only ones no other compositor names (Named 1)" : "named or not (Named 0)",
        g_ctNeedDb ? "only ones a database holds (NeedDb 1)" : "with or without a database (NeedDb 0)",
        g_ctAsync ? "never ones the async loader found (Async 1)" : "found by the async loader or not (Async 0)");
}
