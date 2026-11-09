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
// docs/539 §13.5 (0.36.198, castrimdiag1108b), after the 11-08 release-smoke AV at 0xD4B156: [outfit+0xBC] entry 63 held
// 0x40A00000 (float 5.0) when the SimOutfit destructor released it. [outfit+0xBC, +0xC0) are the outfit Load's async-request
// result cells (0x5E55B0 -> 0x5E53E0): bit 0 or 1 set = a pending request, else a finished result that 0x5AF640 releases through
// vtable slot 1 [verified disassembly, TS3W 1.67]. The result type is not proven, so an untagged cell only has to start with a
// TS3W .rdata vtable, and the vtables seen are counted. All log-only, all reads under __try; EA always runs unchanged.
// Paths: 0 the save inventory (CtInvHook), 1 / 2 the release call sites (CtRelHook).
static LONG g_ctInvSave, g_ctInvCalls, g_ctInvRecs, g_ctInvSlots, g_ctInvBadSave, g_ctInvBad, g_ctInvLogs, g_ctInvGone;
static const DWORD CT_F5 = 0x40A00000;  // float 5.0: the value in all three crashes (docs/539 §13.3)
static bool g_rlGuard;  // [CasTrim] RealmGuard (docs/539 §16.6)
static const char* const kCtPath[3] = {"save inventory 0x5EFE11", "outfit clear 0x5E6E60", "outfit destructor 0x5E7633"};
struct CtVtN { volatile LONG vt, n; };
static const int CT_VTN = 24;
static CtVtN g_ctCellVt[2][CT_VTN];  // [0] the save's cells (reset per save), [1] the release sites' (session)
static LONG g_ctInvCells, g_ctInvCellBad, g_ctF5Save;  // this save (the save's thread)
static volatile LONG g_ctF5, g_ctF5Logs, g_ctCellBad, g_ctCellLogs, g_ctCellGone, g_ctRelCalls, g_ctRelCells, g_ctRelBad, g_ctRelOther;
static LONGLONG g_ctRelQpc, g_ctRelMaxQpc;  // shortcut: plain adds, a second thread at the release sites can lose a sample (log only)
static void CtVtCount(CtVtN* t, DWORD vt) {
    for (int i = 0; i < CT_VTN; i++) {
        const LONG a = t[i].vt;
        if (a == (LONG)vt || (!a && (InterlockedCompareExchange(&t[i].vt, (LONG)vt, 0) == 0 || t[i].vt == (LONG)vt))) {
            InterlockedIncrement(&t[i].n);
            return;
        }
    }
}
static void CtVtText(const CtVtN* t, char* out, size_t cap) {
    int w = 0;
    out[0] = 0;
    for (int i = 0; i < CT_VTN && t[i].vt && w < (int)cap - 24; i++) w += sprintf_s(out + w, cap - w, " %08lX=%ld", t[i].vt, t[i].n);
    if (!w) strcpy_s(out, cap, " none");
}
static void CtCtx(int path, const u8* node, const u8* outfit, char* out, size_t cap) {  // where: path, save, world, outfit, key
    DWORD key[4] = {}, ovt = 0;
    __try { if (node && *(const u8* const*)(node + 0x10) == outfit) memcpy(key, node, 16); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { if (outfit) ovt = *(const DWORD*)outfit; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    sprintf_s(out, cap, "%s, thread %lu; save #%ld (travel flags %d), %ld s after world load #%ld; outfit 0x%08lX (vtable %08lX) key %08lX %08lX %08lX %08lX",
              kCtPath[path], GetCurrentThreadId(), g_ctInvSave, g_swTravel, g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds,
              (DWORD)(uintptr_t)outfit, ovt, key[0], key[1], key[2], key[3]);
}
static void CtNear(const DWORD* c, int n, int i, char* out, size_t cap) {  // cells i-8 .. i+8 of [0, n)
    int w = 0;
    out[0] = 0;
    __try {
        for (int k = i < 8 ? 0 : i - 8; k < n && k <= i + 8 && w < (int)cap - 12; k++) w += sprintf_s(out + w, cap - w, k == i ? " [%08lX]" : " %08lX", c[k]);
    } __except (EXCEPTION_EXECUTE_HANDLER) { strcat_s(out, cap, " (unreadable)"); }
}
// Item 2, the float-5 watch: a record slot or cell equal to 0x40A00000, or one whose first dword is below 0x10000, in every mode.
// arr: 0x14 / 0x38 a record array (rec), 0xBC a cell (cells, n).
static void CtF5(int path, const u8* node, const u8* outfit, int arr, LONG ri, int i, DWORD v, DWORD first, bool ok, const u8* rec, const DWORD* cells, int n) {
    InterlockedIncrement(&g_ctF5);
    if (!path) g_ctF5Save++;
    if (v == CT_F5) {  // [UafCheck] (docs/539 §17): was the slot, its record or its outfit freed by one of our paths?
        UafNearLog(cells ? (DWORD)(uintptr_t)(cells + i) : (DWORD)(uintptr_t)rec, cells ? "outfit cell" : "outfit record");
        if (cells && rec) UafNearLog((DWORD)(uintptr_t)rec, "outfit record");
        UafNearLog((DWORD)(uintptr_t)outfit, "outfit");
        char site[112];  // docs/539 §21 [Float5Watch] window line, [Float5Sticky] address
        DWORD at = cells ? (DWORD)(uintptr_t)(cells + i) : 0;
        if (cells) sprintf_s(site, "%s: outfit cell [+0xBC] entry %d of %d", kCtPath[path], i, n);
        else {
            __try { at = *(const DWORD*)(rec + arr) + 4 * (DWORD)i; } __except (EXCEPTION_EXECUTE_HANDLER) {}
            sprintf_s(site, "%s: outfit record %ld array +0x%X slot %d", kCtPath[path], ri, arr, i);
        }
        F5wBad(site, (DWORD)(uintptr_t)outfit, at, v);
    }
    if (InterlockedIncrement(&g_ctF5Logs) > 20) return;
    char ctx[300], gone[160], sl[160], b[0x68 * 3 + 4] = "unreadable";
    CtCtx(path, node, outfit, ctx, sizeof(ctx)), CtGoneText(v, gone, sizeof(gone)), CtSlotText(v, sl, sizeof(sl));
    if (cells) CtNear(cells, n, i, b, sizeof(b));
    else __try { if (rec) Hex(rec, 0x68, b, sizeof(b)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(b, "unreadable"); }
    Log("cas trim: float-5 cell: %s; %s +0x%X record %ld index %d: value 0x%08lX%s, first dword %s%08lX; %s; %s; %s: %s", ctx, cells ? "cells" : "record array", arr, ri, i,
        v, v == CT_F5 ? " (float 5.0)" : "", ok ? "" : "(unreadable) ", first, sl, gone, cells ? "cells -8..+8" : "record bytes", b);
}
static void CtCellBad(int path, const u8* node, const u8* outfit, const DWORD* cells, int n, int i, DWORD v, const char* what) {
    InterlockedIncrement(&g_ctCellBad);
    if (path) InterlockedIncrement(&g_ctRelBad);
    else g_ctInvCellBad++;
    char gone[160];
    if (CtGoneText(v, gone, sizeof(gone))) InterlockedIncrement(&g_ctCellGone);
    if (InterlockedIncrement(&g_ctCellLogs) > 20) return;
    char ctx[300], sl[160], nb[17 * 11 + 24] = "";
    DWORD w[4] = {};
    bool vOk = false;
    __try { if (v) memcpy(w, (const void*)(uintptr_t)v, 16), vOk = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    CtCtx(path, node, outfit, ctx, sizeof(ctx)), CtSlotText(v, sl, sizeof(sl));
    if (i >= 0) CtNear(cells, n, i, nb, sizeof(nb));
    Log("cas trim: outfit cell bad: %s; %s; [outfit+0xBC] entry %d of %d at 0x%08lX: value 0x%08lX, first dwords %s%08lX %08lX %08lX %08lX; %s; %s; cells -8..+8:%s", what,
        ctx, i, n, (DWORD)(uintptr_t)(cells + (i < 0 ? 0 : i)), v, vOk ? "" : "(unreadable) ", w[0], w[1], w[2], w[3], sl, gone, nb);
}
// Which save's inventory check last saw an outfit (the skip line's "last written by"): direct-mapped, the save's thread writes.
struct CtSeenOut { DWORD outfit, save; };
static CtSeenOut g_ctInvSeen[4096];
static void CtSeenSet(const u8* o) { CtSeenOut& s = g_ctInvSeen[((DWORD)(uintptr_t)o >> 5) & 4095]; s.outfit = (DWORD)(uintptr_t)o, s.save = (DWORD)g_ctInvSave; }
static LONG CtSeenSave(const u8* o) { const CtSeenOut& s = g_ctInvSeen[((DWORD)(uintptr_t)o >> 5) & 4095]; return s.outfit == (DWORD)(uintptr_t)o ? (LONG)s.save : -1; }
// [CasTrim] CellGuard (Rodney 2026-11-08, default 1, every mode): at the release sites a cell that is not a valid object is
// set to 0 before EA's 0x5AF640 runs, so EA neither asks the loader (0x728760 -> 0xD4B150 Release) nor releases it inline
// (0x5AF67A): that one entry leaks, every other one is torn down by EA as before.
static bool g_ctCellGuard;
static volatile LONG g_ctInSave, g_ctGuardLogs;
static void CtGuardSkip(int path, const u8* outfit, DWORD* b, int n, int i, DWORD v, DWORD vt, bool ok, const DWORD* sp) {
    char nb[17 * 11 + 24];
    CtNear(b, n, i, nb, sizeof(nb));  // before the write: the bad value shows in the brackets
    b[i] = 0;
    InterlockedIncrement(&ts3native_counters.ctCellSkip);
    if (InterlockedIncrement(&g_ctGuardLogs) > 40) return;
    DWORD c[6] = {};  // exe .text values up the stack (a scan, as CtEvent: stale values can show)
    if (sp && g_rhTxHi) __try {
            for (int k = 0, j = 0; k < 96 && j < 6; k++)
                if (sp[k] >= g_rhTxLo && sp[k] < g_rhTxHi) c[j++] = sp[k];
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    char ctx[300], gone[160], gout[160], sl[160];
    CtCtx(path, NULL, outfit, ctx, sizeof(ctx)), CtGoneText(v, gone, sizeof(gone)), CtGoneText((DWORD)(uintptr_t)outfit, gout, sizeof(gout)), CtSlotText(v, sl, sizeof(sl));
    Log("cas trim: cell guard skipped: %s; CasTrim mode %ld, in a save %ld; [outfit+0xBC] entry %d of %d: value 0x%08lX, first dword %s%08lX: not released (leaked); "
        "outfit last seen by the inventory check at save #%ld (-1 never); value: %s; %s; outfit: %s; callers %08lX < %08lX < %08lX < %08lX < %08lX < %08lX; cells -8..+8:%s",
        ctx, ts3native_counters.ctMode, g_ctInSave, i, n, v, ok ? "" : "(unreadable) ", vt, CtSeenSave(outfit), gone, sl, gout, c[0], c[1], c[2], c[3], c[4], c[5], nb);
}
// Item 1: the result cells. A tagged cell is a pending request (0x728760 recycles it; 0x5AF640 skips its release).
static void CtCellCheck(int path, const u8* node, const u8* outfit, DWORD* b, DWORD* e, const DWORD* sp = NULL) {
    if (!b && !e) return;
    if (!b || e < b || e - b > 4096) return CtCellBad(path, node, outfit, b, 0, -1, (DWORD)(uintptr_t)e, "cell range not 0-4096 entries (at = begin, value = end)");
    const int n = (int)(e - b);
    int i = -1;
    __try {
        for (i = 0; i < n; i++) {
            const DWORD v = b[i];
            if (!v || (v & 3)) continue;
            if (path) InterlockedIncrement(&g_ctRelCells), InterlockedIncrement(&ts3native_counters.ctCellSeen);
            else g_ctInvCells++;
            DWORD vt = 0;
            bool ok = false;
            __try { vt = *(const DWORD*)(uintptr_t)v, ok = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
            const bool f5 = v == CT_F5 || (ok && vt < 0x10000), bad = !ok || vt < g_rhRdLo || vt >= g_rhRdHi || (vt & 3);
            if (f5) CtF5(path, node, outfit, 0xBC, -1, i, v, vt, ok, NULL, b, n);
            if (bad) CtCellBad(path, node, outfit, b, n, i, v, ok ? "first dword is not a TS3W .rdata vtable" : "value unreadable");
            else CtVtCount(g_ctCellVt[path ? 1 : 0], vt);
            if (path && g_ctCellGuard && (f5 || bad)) CtGuardSkip(path, outfit, b, n, i, v, vt, ok, sp);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        CtCellBad(path, node, outfit, b, n, i, 0, "fault reading the cells");
    }
}
// docs/539 §16 [CasTrim] WriteTrap=1 (test only, default 0, needs Detect): name the code that stores 0x40A00000 into an
// [outfit+0xBC] cell. Each save's inventory pass notes cells of outfits with vtable 0xFE4A10 (the class of every float-5 cell
// hit): entry 87 of 121 / 91 and entry 3 of 81 first, then entry 87 / 3 of other sizes. After the save, up to 4 of them get a
// hardware write watch (watchpt.cpp WpTrapSet: DR0-DR3, len 4, write only, every thread; rotated by save number so coverage
// grows). Each write is logged from the vectored handler (eip, value, EA function, thread, ebp chain, stack scan): the first
// 200 a session, "float5 WRITER" when the value is 0x40A00000 (first 100). An outfit clear / destroy (CtRelHook) disarms its
// cell first; a cell whose outfit or [outfit+0xBC] moved counts as stale. Threads started later get the registers at the next
// save or within 30 s (CtTrapRefresh). Our own detectors only read the cells.
static const DWORD CT_TRAP_VT = 0x00FE4A10;
static const int CT_TRAP_CAND = 256;
struct CtTrapC { DWORD outfit, cell; LONG e, n; };
struct CtTrap { DWORD outfit, cell; LONG e, n, save; volatile LONG writes, gone; };
static CtTrapC g_ctTrapC[2][CT_TRAP_CAND];  // [0] the hit shapes, [1] other entries 87 / 3 of the class (the save's thread)
static LONG g_ctTrapNC[2];
static CtTrap g_ctTrap[4];  // by DR slot
static bool g_ctTrapOn;
static DWORD g_ctTrapSimTid;  // the save's thread (SimService_Save runs on the sim thread)
static volatile LONG g_ctTrapLogs, g_ctTrapF5Logs;
static CtVtN g_ctTrapEip[CT_VTN];  // writer eips and counts (session)
static void CtTrapNote(const u8* outfit, const DWORD* b, const DWORD* e) {  // CtOutfitCells, the save path
    const LONG n = (LONG)(e - b);
    if (!b || e < b || n <= 3 || n > 4096) return;
    DWORD vt = 0;
    __try { vt = *(const DWORD*)outfit; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (vt != CT_TRAP_VT) return;
    for (const LONG i : {87L, 3L}) {
        if (i >= n) continue;
        const int p = (i == 87 ? n == 121 || n == 91 : n == 81) ? 0 : 1;
        if (g_ctTrapNC[p] < CT_TRAP_CAND) g_ctTrapC[p][g_ctTrapNC[p]++] = {(DWORD)(uintptr_t)outfit, (DWORD)(uintptr_t)(b + i), i, n};
    }
}
// shortcut: the EA function is guessed as the first 16-aligned address after MSVC's 0xCC padding (no symbols); check in Ghidra
static DWORD CtFnStart(DWORD eip) {
    if (!g_rhTxHi || eip < g_rhTxLo || eip >= g_rhTxHi) return 0;
    for (DWORD p = eip & ~15u; p > g_rhTxLo && eip - p < 0x10000; p -= 16)
        if (*(const u8*)(uintptr_t)(p - 1) == 0xCC && *(const u8*)(uintptr_t)p != 0xCC) return p;
    return 0;
}
static int CtEbpChain(DWORD ebp, DWORD esp, DWORD* out, int max) {  // [ebp+4] up the frame-pointer chain while it climbs the stack
    int k = 0;
    while (k < max && ebp >= esp && ebp - esp < 0x100000 && !(ebp & 3) && Readable((const void*)(uintptr_t)ebp, 8)) {
        const DWORD* f = (const DWORD*)(uintptr_t)ebp;
        out[k++] = f[1];
        if (f[0] <= ebp) break;
        ebp = f[0];
    }
    return k;
}
static void CtFrames(const DWORD* f, int n, char* out, size_t cap) {
    out[0] = 0;
    for (int i = 0, w = 0; i < n && w < (int)cap - 40; i++) {
        char a[48];
        CrFrame(f[i], a, sizeof(a));
        w += sprintf_s(out + w, cap - w, " %s", a);
    }
}
static void CtTrapHit(int s, const CONTEXT* c) {  // watchpt.cpp WpVeh on the writing thread, after the write (eip = the next instruction)
    Ts3nCounters& z = ts3native_counters;
    CtTrap& t = g_ctTrap[s];
    DWORD v = 0, ovt = 0, ob = 0;
    const bool vOk = Readable((const void*)(uintptr_t)t.cell, 4);
    if (vOk) v = *(const DWORD*)(uintptr_t)t.cell;
    if (Readable((const void*)(uintptr_t)t.outfit, 0xC0)) ovt = *(const DWORD*)(uintptr_t)t.outfit, ob = *(const DWORD*)(uintptr_t)(t.outfit + 0xBC);
    const bool live = !t.gone && ovt == CT_TRAP_VT && ob + 4 * (DWORD)t.e == t.cell, f5 = v == CT_F5;
    InterlockedIncrement(&z.trHits);
    const LONG wn = InterlockedIncrement(&t.writes);
    CtVtCount(g_ctTrapEip, c->Eip);
    if (f5 && live) InterlockedIncrement(&z.trF5);
    if (!live && InterlockedExchange(&t.gone, 1) == 0) InterlockedIncrement(&z.trGone);
    if (f5) UafNearLog(t.cell, "trapped outfit cell"), UafNearLog(t.outfit, "trapped outfit");  // [UafCheck] (docs/539 §17)
    if (f5 ? InterlockedIncrement(&g_ctTrapF5Logs) > 100 : InterlockedIncrement(&g_ctTrapLogs) > 200) return;
    DWORD eb[8] = {}, rs[10] = {};
    const int ne = CtEbpChain(c->Ebp, c->Esp, eb, 8), nr = CrStack(c->Esp, rs, 10);
    char at[48], fn[48] = "", chain[8 * 44 + 8], scan[10 * 44 + 8];
    CrFrame(c->Eip, at, sizeof(at)), CtFrames(eb, ne, chain, sizeof(chain)), CtFrames(rs, nr, scan, sizeof(scan));
    if (const DWORD f = CtFnStart(c->Eip)) sprintf_s(fn, ", EA function ~0x%08lX +0x%lX", f, c->Eip - f);
    const DWORD tid = GetCurrentThreadId();
    Log("cas trim: %s DR%d: cell 0x%08lX ([outfit+0xBC] entry %ld of %ld, outfit 0x%08lX, armed at save #%ld%s) value now %s0x%08lX%s by eip 0x%08lX (%s%s), "
        "thread %lu (%s), write %ld of this watch, save #%ld; eax %08lX ecx %08lX edx %08lX ebx %08lX esi %08lX edi %08lX ebp %08lX esp %08lX; ebp chain (%d):%s; "
        "stack scan (%d):%s",
        f5 ? (live ? "float5 WRITER" : "float5 write on a stale watch") : "write trap hit", s, t.cell, t.e, t.n, t.outfit, t.save,
        live ? "" : ", stale: outfit cleared or its cells moved", vOk ? "" : "(unreadable) ", v, f5 ? " (float 5.0)" : "", c->Eip, at, fn, tid,
        tid == g_ctTrapSimTid ? "sim, the save's thread" : tid == g_mainTid ? "main" : "other", wn, g_ctInvSave, c->Eax, c->Ecx, c->Edx, c->Ebx, c->Esi,
        c->Edi, c->Ebp, c->Esp, ne, chain, nr, scan);
}
static void CtTrapDrop(const u8* outfit, int path) {  // CtRelHook, before EA's release and our CellGuard write
    for (int s = 0; s < 4; s++) {
        CtTrap& t = g_ctTrap[s];
        if (t.outfit != (DWORD)(uintptr_t)outfit || !g_wp[s].trap || g_wp[s].addr != t.cell) continue;
        WpDisarm(s);
        if (InterlockedExchange(&t.gone, 1) == 0) InterlockedIncrement(&ts3native_counters.trGone);
        Log("cas trim: write trap DR%d disarmed at %s: outfit 0x%08lX entry %ld of %ld (cell 0x%08lX, armed at save #%ld), %ld write(s) seen", s, kCtPath[path],
            t.outfit, t.e, t.n, t.cell, t.save, t.writes);
    }
}
static void CtTrapArm() {  // after each save (CtInvSaveLog), the save's thread: the next 4 cells, in place of the last
    Ts3nCounters& z = ts3native_counters;
    g_ctTrapSimTid = GetCurrentThreadId();
    char vt[CT_VTN * 18 + 8], cells[4 * 96] = "";
    CtVtText(g_ctTrapEip, vt, sizeof(vt));
    Log("cas trim: write trap session at save #%ld: writes %ld (float5 WRITER %ld), watches dropped %ld; writer eips:%s", g_ctInvSave, z.trHits, z.trF5, z.trGone, vt);
    DWORD a[4] = {};
    CtTrap nt[4] = {};
    int n = 0;
    for (int p = 0; p < 2 && n < 4; p++) {
        const LONG m = g_ctTrapNC[p];
        for (LONG i = 0; i < m && n < 4; i++) {
            const CtTrapC& k = g_ctTrapC[p][(g_ctInvSave * 4 + i) % m];
            nt[n].outfit = k.outfit, nt[n].cell = k.cell, nt[n].e = k.e, nt[n].n = k.n, nt[n].save = g_ctInvSave;
            a[n++] = k.cell;
        }
    }
    const int mask = WpTrapSet(a, n);  // shortcut: a hit between the set and the copy below logs the last save's outfit (log only)
    for (int s = 0; s < 4; s++) g_ctTrap[s].outfit = nt[s].outfit, g_ctTrap[s].cell = nt[s].cell, g_ctTrap[s].e = nt[s].e, g_ctTrap[s].n = nt[s].n,
                                g_ctTrap[s].save = nt[s].save, g_ctTrap[s].writes = 0, g_ctTrap[s].gone = s < n ? 0 : 1;
    int w = 0;
    for (int s = 0; s < n; s++) {
        DWORD v = 0;
        __try { v = *(const DWORD*)(uintptr_t)a[s]; } __except (EXCEPTION_EXECUTE_HANDLER) {}
        w += sprintf_s(cells + w, sizeof(cells) - w, "%s DR%d%s outfit 0x%08lX entry %ld of %ld cell 0x%08lX (now 0x%08lX)", s ? ";" : "", s,
                       mask & (1 << s) ? "" : " NOT SET (slot in use)", g_ctTrap[s].outfit, g_ctTrap[s].e, g_ctTrap[s].n, a[s], v);
        if (mask & (1 << s)) InterlockedIncrement(&z.trArms);
    }
    Log("cas trim: write trap save #%ld: %d cell(s) watched, %ld thread(s) set; candidates: %ld of the hit shapes (87 of 121 / 91, 3 of 81), %ld other "
        "(entry 87 / 3, vtable %08lX):%s", g_ctInvSave, n, g_wpThreads, g_ctTrapNC[0], g_ctTrapNC[1], CT_TRAP_VT, n ? cells : " none");
    g_ctTrapNC[0] = g_ctTrapNC[1] = 0;
}
static DWORD WINAPI CtTrapRefresh(LPVOID) {  // threads started between saves: every 30 s
    for (LONG last = 0;;) {
        Sleep(30000);
        const LONG n = WpRefresh();
        if (n != last) Log("cas trim: write trap refresh: %ld thread(s) set (was %ld)", n, last), last = n;
    }
}
static void CtOutfitCells(int path, const u8* node, const u8* outfit) {  // the save path: the outfit's own begin / end
    DWORD *b = NULL, *e = NULL;
    __try { b = *(DWORD* const*)(outfit + 0xBC), e = *(DWORD* const*)(outfit + 0xC0); } __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    CtSeenSet(outfit);
    CtCellCheck(path, node, outfit, b, e);
    if (g_ctTrapOn && !path) CtTrapNote(outfit, b, e);
}
typedef void(__fastcall* CtInvFn)(void* outfit, void* edx, void* set, DWORD children);
static CtInvFn g_ctInvEa = (CtInvFn)(uintptr_t)0x005E7690;  // selftest: a fake
static const u8* volatile g_ctInvNode;                       // EA's esi at the call (CtInvStub)
static LONGLONG g_ctInvQpc;
static void CtInvBad(const char* what, const u8* node, const u8* outfit, LONG ri, LONG rn, const u8* rec, int arr, int slot, DWORD v, int path = 0) {
    if (!path) g_ctInvBadSave++;
    InterlockedIncrement(&g_ctInvBad);
    char gone[160], sl[160], rb[0x68 * 3 + 4] = "unreadable";
    if (CtGoneText(v, gone, sizeof(gone))) InterlockedIncrement(&g_ctInvGone);
    if (InterlockedIncrement(&g_ctInvLogs) > 20) return;
    DWORD key[4] = {}, w[4] = {};
    bool vOk = false;
    __try { if (node && *(const u8* const*)(node + 0x10) == outfit) memcpy(key, node, 16); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { if (v) memcpy(w, (const void*)(uintptr_t)v, 16), vOk = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { if (rec) Hex(rec, 0x68, rb, sizeof(rb)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(rb, "unreadable"); }
    CtSlotText(v, sl, sizeof(sl));
    Log("cas trim: inventory bad slot (%s, thread %lu): %s; save #%ld (travel flags %d), %ld s after world load #%ld; outfit 0x%08lX key %08lX %08lX %08lX %08lX, "
        "record %ld of %ld at 0x%08lX, array +0x%X slot %d: value 0x%08lX, first dwords %s %08lX %08lX %08lX %08lX; %s; %s; record bytes: %s",
        kCtPath[path], GetCurrentThreadId(), what, g_ctInvSave, g_swTravel, g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, (DWORD)(uintptr_t)outfit,
        key[0], key[1], key[2], key[3], ri, rn, (DWORD)(uintptr_t)rec, arr < 0 ? 0 : arr ? 0x38 : 0x14, slot, v, vOk ? "" : "(unreadable)", w[0], w[1],
        w[2], w[3], sl, gone, rb);
}
static void CtInvCheck(const u8* node, const u8* outfit, int path = 0) {  // path 0: the save's numbers; 1 / 2: the release sites'
    LONG ri = -1, rn = 0;
    const u8* rec = NULL;
    int arr = -1, slot = -1;
    __try {
        const u8 *b = *(const u8* const*)(outfit + 0x98), *e = *(const u8* const*)(outfit + 0x9C);
        if (e < b || (e - b) % 0x68 || (e - b) / 0x68 > 64) return CtInvBad("record range not 0-64 records of 0x68 (record = begin, value = end, of = bytes)", node, outfit, -1, (LONG)(e - b), b, -1, -1, (DWORD)(uintptr_t)e, path);
        rn = (LONG)((e - b) / 0x68);
        if (!path) g_ctInvRecs += rn;
        for (ri = 0; ri < rn; ri++) {
            rec = b + ri * 0x68;
            for (arr = 0; arr < 2; arr++) {
                const DWORD* a = *(const DWORD* const*)(rec + (arr ? 0x38 : 0x14));
                int ns = 5;  // the save's 0x5E7690 reads 5 slots; the clear's 0x5E5AF0 releases [begin, end) (+0x18 / +0x3C)
                if (path) {
                    const DWORD* ae = *(const DWORD* const*)(rec + (arr ? 0x3C : 0x18));
                    ns = ae < a || ae - a > 64 ? -1 : (int)(ae - a);
                    if (ns < 0) { CtInvBad("array range not 0-64 entries (value = end)", node, outfit, ri, rn, rec, arr, -1, (DWORD)(uintptr_t)ae, path); continue; }
                }
                for (slot = 0; slot < ns; slot++) {
                    const DWORD v = a[slot];
                    if (!v) continue;
                    if (!path) g_ctInvSlots++;
                    DWORD vt = 0;
                    bool ok = false;
                    __try { vt = *(const DWORD*)(uintptr_t)v, ok = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
                    if (v == CT_F5 || (ok && vt < 0x10000)) CtF5(path, node, outfit, arr ? 0x38 : 0x14, ri, slot, v, vt, ok, rec, NULL, 0);
                    if (ok && vt != g_ctVt[0] && vt != g_ctVt[1] && path && vt >= g_rhRdLo && vt < g_rhRdHi && !(vt & 3)) {
                        InterlockedIncrement(&g_ctRelOther);  // the clear releases any object here: another type with a TS3W vtable is no fault
                        continue;
                    }
                    if (!ok || (vt != g_ctVt[0] && vt != g_ctVt[1])) CtInvBad(ok ? "not a part compositor" : "value unreadable", node, outfit, ri, rn, rec, arr, slot, v, path);
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        CtInvBad("fault reading the outfit, a record or an array pointer", node, outfit, ri, rn, rec, arr, slot, 0, path);
    }
}
static void __fastcall CtInvHook(void* outfit, void* edx, void* set, DWORD children) {
    LARGE_INTEGER q0, q1;
    QueryPerformanceCounter(&q0);
    g_ctInvCalls++;
    const LONG f50 = g_ctF5;
    CtInvCheck(g_ctInvNode, (const u8*)outfit);
    CtOutfitCells(0, g_ctInvNode, (const u8*)outfit);  // docs/539 §13.5 item 1
    F5wOutfit((const u8*)outfit, g_ctF5 == f50, kCtPath[0], true);  // docs/539 §21: last OK, sweep and DR candidates
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
// docs/539 §14 (0.36.218, ledger §468), after the 11-09 bp-leak40-memtravel-x3 AV at 0xD4B154 (the 4th float-5 hit): EA's
// 0x5D82A0 (thiscall (ModelBuilder), plain ret) is called at 0x5D83B4 (in 0x5D83B0 <- ModelBuilder_Dtor's 0x5DC539) and at
// 0x5D96AD (ecx = [holder+0xC]). It runs three release loops through the loader's +0x3C with the callback 0xD4B150: list
// nodes at +0x1C (cell node+0x1C), list nodes at +0x28 (cell node+8), then the 0x74-byte part records [builder+0xC, +0x10): a
// record with a non-zero first dword gets its 4 cells rec+4..+0x10 released (bit 0/1 = pending; else a finished result, vtable
// slot 1) [verified disassembly, TS3W 1.67]. The 11-09 crash: cell rec+4 held 0x40A00000. Checked before EA at both call
// sites; log only unless [CasTrim] MbGuard=1 (default 0): a bad cell is then set to 0 and EA skips it (that result leaks).
static const DWORD CT_MB_RET_DTOR = 0x005D83B9, CT_MB_RET_HOLD = 0x005D96B2;
typedef void(__fastcall* CtMbFn)(u8* mb, void* edx);
static CtMbFn g_ctMbEa = (CtMbFn)(uintptr_t)0x005D82A0;  // selftest: a fake
static bool g_ctMbGuard;
static volatile LONG g_ctMbLogs, g_ctMbRecs, g_ctMbGone;
static LONGLONG g_ctMbQpc, g_ctMbMaxQpc;  // shortcut: plain adds as g_ctRelQpc (log only)
static CtVtN g_ctMbVt[CT_VTN];
static DWORD g_ctSweepAt;        // tick | 1 at the last "sweep done" (CtSweepStep)
static LONG g_ctSweepSave = -1;  // the save # it followed
static bool CtFloaty(DWORD v) { const DWORD x = (v >> 23) & 0xFF; return !(v & 0xFFFF) && x >= 0x70 && x <= 0x90; }  // a round float, |x| 2^-15..2^17
static void CtSweepText(char* out, size_t cap) {
    if (!g_ctSweepAt) sprintf_s(out, cap, "no CasTrim sweep this session");
    else sprintf_s(out, cap, "last CasTrim sweep done %ld s ago, after save #%ld (swept %ld in total)", (LONG)(GetTickCount() - g_ctSweepAt) / 1000, g_ctSweepSave,
                   ts3native_counters.ctSwept);
}
static const char* CtMbFrom(DWORD ret) { return ret == CT_MB_RET_DTOR ? "ModelBuilder dtor 0x5D83B4" : ret == CT_MB_RET_HOLD ? "holder release 0x5D96AD" : "?"; }
static void CtMbBad(const u8* mb, DWORD ret, const u8* rec, LONG ri, LONG rn, int k, DWORD v, const char* what) {
    char gv[160], gr[160], gb[160];
    if (CtGoneText(v, gv, sizeof(gv)) | CtGoneText((DWORD)(uintptr_t)rec, gr, sizeof(gr)) | CtGoneText((DWORD)(uintptr_t)mb, gb, sizeof(gb))) InterlockedIncrement(&g_ctMbGone);
    if (v == CT_F5) {  // [UafCheck] (docs/539 §17)
        if (rec && k >= 0) UafNearLog((DWORD)(uintptr_t)(rec + 4 + 4 * k), "modelbuilder cell");
        UafNearLog((DWORD)(uintptr_t)rec, "modelbuilder record"), UafNearLog((DWORD)(uintptr_t)mb, "modelbuilder");
        char site[96];  // docs/539 §21
        sprintf_s(site, "modelbuilder release (%s): record %ld of %ld cell %d", CtMbFrom(ret), ri, rn, k);
        F5wBad(site, (DWORD)(uintptr_t)mb, rec && k >= 0 ? (DWORD)(uintptr_t)(rec + 4 + 4 * k) : 0, v);
    }
    if (InterlockedIncrement(&g_ctMbLogs) > 40) return;
    char sl[160], sw[120], rb[0x74 * 3 + 4] = "unreadable", hb[0x40 * 3 + 4] = "unreadable";
    DWORD w[4] = {};
    bool vOk = false;
    __try { if (v) memcpy(w, (const void*)(uintptr_t)v, 16), vOk = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { if (rec) Hex(rec, 0x74, rb, sizeof(rb)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(rb, "unreadable"); }
    __try { Hex(mb, 0x40, hb, sizeof(hb)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(hb, "unreadable"); }
    CtSlotText(v, sl, sizeof(sl)), CtSweepText(sw, sizeof(sw));
    Log("cas trim: modelbuilder cell bad: %s; thread %lu, from 0x%08lX (%s); builder 0x%08lX, record %ld of %ld at 0x%08lX, cell %d (rec+0x%X): value 0x%08lX%s%s, "
        "first dwords %s%08lX %08lX %08lX %08lX; MbGuard %d (%s); value: %s; %s; record: %s; builder: %s; %s; CasTrim mode %ld, in a save %ld, save #%ld (travel flags %d), "
        "%ld s after world load #%ld; record bytes: %s; builder +0..+0x40: %s", what, GetCurrentThreadId(), ret, CtMbFrom(ret), (DWORD)(uintptr_t)mb, ri, rn,
        (DWORD)(uintptr_t)rec, k, 4 + 4 * k, v, v == CT_F5 ? " (float 5.0)" : "", v != CT_F5 && CtFloaty(v) ? " (float-like)" : "", vOk ? "" : "(unreadable) ", w[0], w[1],
        w[2], w[3], g_ctMbGuard, g_ctMbGuard ? "set to 0, not released" : "log only, EA releases it", gv, sl, gr, gb, sw, ts3native_counters.ctMode, g_ctInSave,
        g_ctInvSave, g_swTravel, g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, rb, hb);
}
static void CtMbCheck(u8* mb, DWORD ret) {  // the part records as 0x5D82A0's third loop reads them
    LONG ri = -1, rn = 0;
    u8* rec = NULL;
    int k = -1;
    __try {
        u8 *b = *(u8* const*)(mb + 0xC), *e = *(u8* const*)(mb + 0x10);
        if (b == e) return;
        if (!b || e < b || (e - b) % 0x74 || (e - b) / 0x74 > 1024)
            return CtMbBad(mb, ret, b, -1, (LONG)(e - b), -1, (DWORD)(uintptr_t)e, "record range not 0-1024 records of 0x74 (record = begin, value = end)");
        rn = (LONG)((e - b) / 0x74);
        InterlockedExchangeAdd(&g_ctMbRecs, rn);
        for (ri = 0; ri < rn; ri++) {
            rec = b + ri * 0x74;
            if (!*(const DWORD*)rec) continue;  // EA releases nothing of this record
            DWORD* c = (DWORD*)(rec + 4);
            for (k = 0; k < 4; k++) {
                const DWORD v = c[k];
                if (!v || (v & 3)) continue;  // empty or a pending request (the loader recycles it)
                InterlockedIncrement(&ts3native_counters.mbCells);
                DWORD vt = 0;
                bool ok = false;
                __try { vt = *(const DWORD*)(uintptr_t)v, ok = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
                const bool f5 = v == CT_F5;
                if (!f5 && ok && vt >= g_rhRdLo && vt < g_rhRdHi && !(vt & 3)) { CtVtCount(g_ctMbVt, vt); continue; }
                InterlockedIncrement(&ts3native_counters.mbBad);
                CtMbBad(mb, ret, rec, ri, rn, k, v, f5 ? "float 5.0" : !ok ? "value unreadable" : "first dword is not a TS3W .rdata vtable");
                if (g_ctMbGuard) c[k] = 0, InterlockedIncrement(&ts3native_counters.mbSkip);
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        CtMbBad(mb, ret, rec, ri, rn, k, 0, "fault reading the builder or a record");
    }
}
static void __fastcall CtMbHook(u8* mb, void* edx) {
    LARGE_INTEGER q0, q1;
    QueryPerformanceCounter(&q0);
    InterlockedIncrement(&ts3native_counters.mbCalls);
    if (mb) CtMbCheck(mb, (DWORD)(uintptr_t)_ReturnAddress());
    QueryPerformanceCounter(&q1);
    const LONGLONG d = q1.QuadPart - q0.QuadPart;
    g_ctMbQpc += d;
    if (d > g_ctMbMaxQpc) g_ctMbMaxQpc = d;
    g_ctMbEa(mb, edx);
}
// crashfx.cpp: a fault in the callback 0xD4B150 (0xD4B154 reads the value's vtable, 0xD4B156 its slot 1) called from 0x5D82A0.
// Frame at the callback [verified disassembly; the 11-09 EA report]: [esp] 0x7287EC, +4 the value, +8 / +0xC / +0x10 0x728760's
// saved edi / ebp / ebx (ebp = the record end, ebx = the record in the third loop), +0x14 0x728DD4, +0x18 the cell, +0x20 /
// +0x24 0x728DB0's saved edi / esi, +0x28 the loop's return (0x5D82CF list +0x1C, 0x5D831A list +0x28, 0x5D8377 part
// records), +0x2C the cell, +0x34..+0x40 0x5D82A0's saved edi, esi, ebp, ebx, +0x44 its return; saved esi = the builder from
// 0x5D83B4 (return 0x5D83B9), the holder from 0x5D96AD (return 0x5D96B2; builder = [holder+0xC]).
static bool CtMbFaultText(const CONTEXT* c, char* out, size_t cap) {
    if (c->Eip < 0x00D4B150 || c->Eip > 0x00D4B159) return false;
    DWORD f[18] = {};
    __try { memcpy(f, (const void*)(uintptr_t)c->Esp, sizeof(f)); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    const int loop = f[10] == 0x005D82CF ? 1 : f[10] == 0x005D831A ? 2 : f[10] == 0x005D8377 ? 3 : 0;
    if (f[0] != 0x007287EC || f[5] != 0x00728DD4 || !loop) return false;
    const DWORD v = f[1], cell = f[6], ret = f[17], rec = loop == 3 ? f[4] : 0, end = loop == 3 ? f[3] : 0;
    DWORD mb = ret == CT_MB_RET_DTOR ? f[14] : 0, mvt = 0, b = 0, w[4] = {};
    bool vOk = false;
    __try { if (ret == CT_MB_RET_HOLD) mb = *(const DWORD*)(uintptr_t)(f[14] + 0xC); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { if (mb) mvt = *(const DWORD*)(uintptr_t)mb, b = *(const DWORD*)(uintptr_t)(mb + 0xC); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { if (v) memcpy(w, (const void*)(uintptr_t)v, 16), vOk = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const LONG ri = rec && b && rec >= b ? (LONG)((rec - b) / 0x74) : -1L, rn = end && b && end >= b ? (LONG)((end - b) / 0x74) : -1L;
    const int k = rec && cell >= rec + 4 ? (int)((cell - rec - 4) / 4) : -1;
    char gv[160], gr[160], gb[160], sl[160], sw[120], rb[0x74 * 3 + 4] = "none", hb[0x40 * 3 + 4] = "unreadable";
    CtGoneText(v, gv, sizeof(gv)), CtGoneText(rec, gr, sizeof(gr)), CtGoneText(mb, gb, sizeof(gb)), CtSlotText(v, sl, sizeof(sl)), CtSweepText(sw, sizeof(sw));
    __try { if (rec) Hex((const u8*)(uintptr_t)rec, 0x74, rb, sizeof(rb)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(rb, "unreadable"); }
    __try { if (mb) Hex((const u8*)(uintptr_t)mb, 0x40, hb, sizeof(hb)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(hb, "unreadable"); }
    sprintf_s(out, cap, "crash forensics: cas trim modelbuilder release fault at 0x%08lX (0x5D82A0 %s, loop return 0x%08lX): value 0x%08lX%s%s, first dwords %s%08lX %08lX %08lX %08lX; "
              "value: %s; %s; cell 0x%08lX (rec+0x%X, cell %d); record 0x%08lX (%ld of %ld, end 0x%08lX; %s); builder 0x%08lX (vtable %08lX, from 0x%08lX %s; %s); "
              "modelbuilder checks: calls %ld, cells %ld, bad %ld, skips %ld (MbGuard %d); %s; CasTrim mode %ld, in a save %ld, save #%ld (travel flags %d), %ld s after world "
              "load #%ld; record bytes: %s; builder +0..+0x40: %s", c->Eip, loop == 3 ? "part records" : loop == 1 ? "list +0x1C" : "list +0x28", f[10], v,
              v == CT_F5 ? " (float 5.0)" : "", v != CT_F5 && CtFloaty(v) ? " (float-like)" : "", vOk ? "" : "(unreadable) ", w[0], w[1], w[2], w[3], gv, sl, cell,
              rec ? cell - rec : 0, k, rec, ri, rn, end, gr, mb, mvt, ret, CtMbFrom(ret), gb, ts3native_counters.mbCalls, ts3native_counters.mbCells,
              ts3native_counters.mbBad, ts3native_counters.mbSkip, g_ctMbGuard, sw, ts3native_counters.ctMode, g_ctInSave, g_ctInvSave, g_swTravel,
              g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, rb, hb);
    return true;
}

// docs/539 §15 (0.36.225, ledger §469), after the 11-09 bp-float5-memtravel-x8 AV writing NULL in MSVCR80's memcpy (the 5th
// float-5 hit): the outfit save writer 0x5E7A20 calls 0x5E61A0 at 0x5E7BC2 (thiscall (outfit; vector of part-record pointers,
// 3 more args), ret 0x10; return 0x5E7BC7). Per record it takes obj = [rec+0x5C] (0: skipped), calls 0x5BE810(obj) (reads a
// non-empty string at obj+0x2C as a stream) and 0x5BF4C0(obj, &str), which with [obj+0x28] == 0 assigns the UTF-16 string
// {begin +0x2C, end +0x30, capacity end +0x34} through 0x408E20 -> 0x408B90 [verified disassembly, TS3W 1.67]. The crash:
// begin 0x40A00000 (float 5.0), end 0x011C7E54 (EA's empty string), so end - begin < 0: a ~3 GB allocation fails and memcpy
// writes to NULL. obj is the 0x60-byte class built by 0x5BF260 (vtable 0xFE2A80), which starts the string as EA's empty
// triple 0x11C7E54 / 0x11C7E54 / 0x11C7E56; its destructor 0x5BF710 frees the buffer only when (cap - begin) & ~1 > 2, so
// that triple is never freed. Checked before EA (any CasTrim mode) with Detect=1 or OutfitStrGuard=1. OutfitStrGuard=1 resets
// the string to that triple only where EA's copy would certainly fault: vtable 0xFE2A80, [obj+0x28] == 0, begin != end, and
// begin > end by under 2 GB or [begin, end) unreadable. Everything else is log only.
static const DWORD CT_OS_VT = 0x00FE2A80, CT_STR_EMPTY = 0x011C7E54, CT_OS_RET = 0x005E7BC7;
typedef void(__fastcall* CtOsFn)(void* outfit, void* edx, DWORD* vec, DWORD a2, DWORD a3, DWORD a4);
static CtOsFn g_ctOsEa = (CtOsFn)(uintptr_t)0x005E61A0;  // selftest: a fake
static DWORD g_ctOsVtWant = CT_OS_VT;  // selftest: a vtable in its own .rdata
static bool g_ctOsGuard;
static volatile LONG g_ctOsLogs, g_ctOsGone, g_ctOsRecs;
static LONGLONG g_ctOsQpc, g_ctOsMaxQpc;  // shortcut: plain adds as g_ctRelQpc (log only)
static CtVtN g_ctOsVt[CT_VTN];
static bool CtRangeReadable(DWORD b, DWORD e) {  // [b, e): one byte a page and the last one, under __try
    __try {
        for (DWORD p = b; p < e; p = (p | 0xFFF) + 1) (void)*(const volatile u8*)(uintptr_t)p;
        if (e > b) (void)*(const volatile u8*)(uintptr_t)(e - 1);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return true;
}
// what is wrong with obj's string (NULL = fine); *fix: EA's copy in 0x5BF4C0 would certainly fault on it (the guard's condition)
static const char* CtOsWhat(DWORD vt, DWORD flag, DWORD b, DWORD e, DWORD c, bool* fix) {
    *fix = false;
    if (vt < g_rhRdLo || vt >= g_rhRdHi || (vt & 3)) return "object's first dword is not a TS3W .rdata vtable";
    if (b == e) return b == CT_F5 ? "string begin = end = 0x40A00000" : NULL;  // empty: EA copies nothing
    const bool small = b < e && e - b < 0x10000, rd = small && CtRangeReadable(b, e);
    *fix = vt == g_ctOsVtWant && !flag && ((b > e && b - e < 0x80000000) || (small && !rd));  // docs/539 §15.5: > 2 GB to allocate and copy
    if (b > e) return "string begin after end (EA's copy length is negative: a ~GB allocation fails, then memcpy to NULL)";
    if (!small) return "string 64 KB or longer";
    if (!rd) return "string memory unreadable";
    if (b == CT_F5 || e == CT_F5 || c == CT_F5) return "string pointer 0x40A00000";
    if (c < e) return "string end after its capacity end";
    return NULL;
}
static void CtOsBad(const u8* outfit, DWORD ret, const u8* rec, LONG ri, LONG rn, const u8* obj, const char* what, bool fixed) {
    char go[160], gr[160];
    if (CtGoneText((DWORD)(uintptr_t)obj, go, sizeof(go)) | CtGoneText((DWORD)(uintptr_t)rec, gr, sizeof(gr))) InterlockedIncrement(&g_ctOsGone);
    UafNearLog((DWORD)(uintptr_t)obj, "outfit string object"), UafNearLog((DWORD)(uintptr_t)rec, "outfit string record");  // [UafCheck] (docs/539 §17)
    UafNearLog((DWORD)(uintptr_t)outfit, "outfit");
    if (InterlockedIncrement(&g_ctOsLogs) > 40) return;
    char sw[120], rb[0x68 * 3 + 4] = "unreadable", ob[0x60 * 3 + 4] = "unreadable", ub[0x40 * 3 + 4] = "unreadable";
    __try { if (rec) Hex(rec, 0x68, rb, sizeof(rb)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(rb, "unreadable"); }
    __try { if (obj) Hex(obj, 0x60, ob, sizeof(ob)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(ob, "unreadable"); }
    __try { if (outfit) Hex(outfit, 0x40, ub, sizeof(ub)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(ub, "unreadable"); }
    CtSweepText(sw, sizeof(sw));
    Log("cas trim: outfit string bad: %s; thread %lu, from 0x%08lX (%s); outfit 0x%08lX (last seen by the inventory check at save #%ld), record %ld of %ld at 0x%08lX, "
        "object [rec+0x5C] 0x%08lX; OutfitStrGuard %d (%s); object: %s; record: %s; %s; CasTrim mode %ld, in a save %ld, save #%ld (travel flags %d), %ld s after world "
        "load #%ld; object bytes (+0x28 flag, +0x2C begin, +0x30 end, +0x34 capacity end): %s; record bytes: %s; outfit +0..+0x40: %s", what, GetCurrentThreadId(), ret,
        ret == CT_OS_RET ? "outfit save writer 0x5E7BC2" : "?", (DWORD)(uintptr_t)outfit, outfit ? CtSeenSave(outfit) : -1L, ri, rn, (DWORD)(uintptr_t)rec,
        (DWORD)(uintptr_t)obj, g_ctOsGuard, fixed ? "reset to EA's empty string" : g_ctOsGuard ? "not a certain fault: log only" : "log only, EA copies it", go, gr, sw,
        ts3native_counters.ctMode, g_ctInSave, g_ctInvSave, g_swTravel, g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, ob, rb, ub);
}
static void CtOsCheck(const u8* outfit, const DWORD* vec, DWORD ret) {  // the part records as 0x5E61A0 reads them
    LONG ri = -1, rn = 0;
    const u8 *rec = NULL, *obj = NULL;
    __try {
        const u8* const* b = *(const u8* const* const*)vec;
        const u8* const* e = *(const u8* const* const*)(vec + 1);
        if (b == e) return;
        if (!b || e < b || e - b > 1024)
            return CtOsBad(outfit, ret, (const u8*)b, -1, (LONG)((const u8*)e - (const u8*)b), NULL, "record pointer range not 0-1024 pointers (record = begin, of = bytes)", false);
        rn = (LONG)(e - b);
        InterlockedExchangeAdd(&g_ctOsRecs, rn);
        for (ri = 0; ri < rn; ri++) {
            rec = b[ri], obj = NULL;
            obj = *(const u8* const*)(rec + 0x5C);
            if (!obj) continue;  // EA skips it (0x5E61FF)
            if ((DWORD)(uintptr_t)obj == CT_F5) F5wBad("outfit save writer: record [rec+0x5C]", (DWORD)(uintptr_t)outfit, (DWORD)(uintptr_t)(rec + 0x5C), CT_F5);  // docs/539 §21
            InterlockedIncrement(&ts3native_counters.osObjs);
            DWORD o[4] = {}, vt = 0;  // +0x28 flag, +0x2C begin, +0x30 end, +0x34 capacity end
            bool ok = false;
            __try { vt = *(const DWORD*)obj, memcpy(o, obj + 0x28, 16), ok = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
            bool fix = false;
            const char* what = ok ? CtOsWhat(vt, o[0], o[1], o[2], o[3], &fix) : "object unreadable";
            if (!what) { CtVtCount(g_ctOsVt, vt); continue; }
            InterlockedIncrement(&ts3native_counters.osBad);
            for (int k = 1; k < 4; k++)  // docs/539 §21: +0x2C begin, +0x30 end, +0x34 capacity end
                if (o[k] == CT_F5) { F5wBad("outfit save writer: string object [rec+0x5C] +0x2C..+0x34", (DWORD)(uintptr_t)outfit, (DWORD)(uintptr_t)(obj + 0x28 + 4 * k), CT_F5); break; }
            const bool fixed = fix && g_ctOsGuard;
            if (fixed) {  // 0x5BF260's own empty string: a legal value everywhere, never freed (0x5BF710)
                DWORD* s = (DWORD*)(obj + 0x2C);
                s[0] = s[1] = CT_STR_EMPTY, s[2] = CT_STR_EMPTY + 2;
                InterlockedIncrement(&ts3native_counters.osFix);
            }
            CtOsBad(outfit, ret, rec, ri, rn, obj, what, fixed);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        CtOsBad(outfit, ret, rec, ri, rn, obj, "fault reading the record vector, a record or an object", false);
    }
}
static void __fastcall CtOsHook(void* outfit, void* edx, DWORD* vec, DWORD a2, DWORD a3, DWORD a4) {
    LARGE_INTEGER q0, q1;
    QueryPerformanceCounter(&q0);
    InterlockedIncrement(&ts3native_counters.osCalls);
    if (vec) CtOsCheck((const u8*)outfit, vec, (DWORD)(uintptr_t)_ReturnAddress());
    QueryPerformanceCounter(&q1);
    const LONGLONG d = q1.QuadPart - q0.QuadPart;
    g_ctOsQpc += d;
    if (d > g_ctOsMaxQpc) g_ctOsMaxQpc = d;
    g_ctOsEa(outfit, edx, vec, a2, a3, a4);
}
// crashfx.cpp, any access violation: the 11-09 fault is in MSVCR80's memcpy (no TS3W eip), so esp..esp+0x400 is searched for
// 0x5BF4C0's return 0x5E621C at A with 0x408E20's return 0x5BF5E0 at A-0x64 and 0x5E61A0's return 0x5E7BC7 at A+0x54
// [verified: the 11-09 EA report's stack data against the disassembly]. Then A-0x58 = 0x5BF4C0's saved esi (the record),
// A-0x60 / A-0x5C = the begin / end it passed, A-0x6C = 0x408E20's saved esi (the object), A+0xC = 0x5E61A0's saved esi (the
// outfit). 0x408C3A (0x408B90's return from memcpy) below A is reported, not required.
static bool CtOsFaultText(const CONTEXT* c, char* out, size_t cap) {
    DWORD s[0x100] = {};
    int n = 0;
    __try { for (; n < 0x100; n++) s[n] = ((const DWORD*)(uintptr_t)c->Esp)[n]; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    int a = -1;
    for (int i = 0x1B; i + 0x15 < n && a < 0; i++)
        if (s[i] == 0x005E621C && s[i - 0x19] == 0x005BF5E0 && s[i + 0x15] == CT_OS_RET) a = i;
    if (a < 0) return false;
    bool mc = false;
    for (int i = 0; i < a && !mc; i++) mc = s[i] == 0x00408C3A;
    const DWORD rec = s[a - 0x16], b = s[a - 0x18], e = s[a - 0x17], obj = s[a - 0x1B], outfit = s[a + 3];
    DWORD ro = 0, o[5] = {};
    bool rok = false, ook = false;
    __try { ro = *(const DWORD*)(uintptr_t)(rec + 0x5C), rok = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { o[0] = *(const DWORD*)(uintptr_t)obj, memcpy(o + 1, (const void*)(uintptr_t)(obj + 0x28), 16), ook = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    char go[160], gr[160], sw[120], ob[0x60 * 3 + 4] = "unreadable";
    CtGoneText(obj, go, sizeof(go)), CtGoneText(rec, gr, sizeof(gr)), CtSweepText(sw, sizeof(sw));
    __try { Hex((const u8*)(uintptr_t)obj, 0x60, ob, sizeof(ob)); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(ob, "unreadable"); }
    sprintf_s(out, cap, "crash forensics: cas trim outfit string copy fault at 0x%08lX (%s; 0x5BF4C0 frame at 0x%08lX, %s): string begin 0x%08lX%s, end 0x%08lX%s; "
              "object 0x%08lX (%s, vtable %08lX%s, +0x28 %08lX, now begin %08lX end %08lX capacity end %08lX; %s); record 0x%08lX ([rec+0x5C] %s0x%08lX%s; %s); "
              "outfit 0x%08lX (last seen by the inventory check at save #%ld); outfit string checks: calls %ld, objects %ld, bad %ld, resets %ld (OutfitStrGuard %d); %s; "
              "CasTrim mode %ld, in a save %ld, save #%ld (travel flags %d), %ld s after world load #%ld; object bytes: %s", c->Eip,
              c->Eip >= 0x00401000 && c->Eip < 0x00F95000 ? "in TS3W" : "outside TS3W, the CRT memcpy", (DWORD)(c->Esp + a * 4),
              mc ? "0x408C3A memcpy return below it" : "no 0x408C3A below it", b, b == CT_F5 ? " (float 5.0)" : "", e, e == CT_STR_EMPTY ? " (EA's empty string)" : "",
              obj, ook ? "readable" : "unreadable", o[0], o[0] == g_ctOsVtWant ? " = 0xFE2A80" : "", o[1], o[2], o[3], o[4], go, rec, rok ? "" : "unreadable ", ro,
              rok && ro != obj ? " (not the object)" : "", gr, outfit, outfit ? CtSeenSave((const u8*)(uintptr_t)outfit) : -1L, ts3native_counters.osCalls,
              ts3native_counters.osObjs, ts3native_counters.osBad, ts3native_counters.osFix, g_ctOsGuard, sw, ts3native_counters.ctMode, g_ctInSave, g_ctInvSave,
              g_swTravel, g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, ob);
    return true;
}
static volatile LONG g_ctOsFaults;
static void CtOsFault(const CONTEXT* c) {  // crashfx.cpp, every access violation: logs only when the frame is there (first 2)
    char t[3072];
    if (!CtOsFaultText(c, t, sizeof(t)) || InterlockedIncrement(&g_ctOsFaults) > 2) return;
    Log("%s", t);
}
// docs/539 §15.7, [CasTrim] Quarantine=N (test only): before each sweep erase, one extra reference through the compositor's
// vtable slot 0 (0x723130 -> 0x731D00, thiscall, no args: the AddRef EA's keep-set insert 0x5E76D8 uses) [verified disassembly],
// never released, so nothing the sweep erases returns to the heap. At N held the sweep stops for the session. Each save scans
// the held 0x68-byte blocks (the +0x28 child vector header included) for 0x40A00000: a hit = a stale writer into an erased compositor.
static DWORD* g_ctQuar;
static LONG g_ctQuarCap, g_ctQuarN, g_ctQuarHitLog;
static bool g_ctQuarFull;
static void CtAddRefEa(DWORD o) { ((void(__fastcall*)(DWORD, void*))(uintptr_t)(*(const DWORD* const*)(uintptr_t)o)[0])(o, NULL); }
static void (*g_ctAddRefFn)(DWORD o) = CtAddRefEa;  // selftest: a fake
static bool CtQuarHold(DWORD o) {  // false: full, no more erases this session
    if (g_ctQuarN >= g_ctQuarCap) {
        if (!g_ctQuarFull) g_ctQuarFull = true, Log("cas trim: quarantine full (%ld held): no more erases this session", g_ctQuarN);
        return false;
    }
    g_ctAddRefFn(o), g_ctQuar[g_ctQuarN++] = o;
    return true;
}
static void CtQuarScan() {  // per save, with the summary lines
    if (!g_ctQuarCap) return;
    LONG hits = 0;
    for (LONG i = 0; i < g_ctQuarN; i++) {
        __try {
            const DWORD* p = (const DWORD*)(uintptr_t)g_ctQuar[i];
            for (int k = 0; k < 0x68 / 4; k++)
                if (p[k] == CT_F5 && (hits++, g_ctQuarHitLog++ < 40))
                    Log("cas trim: quarantine hit: offset +0x%02X, compositor 0x%08lX (%ld of %ld held), save #%ld; refs %ld", k * 4, g_ctQuar[i], i, g_ctQuarN,
                        g_ctInvSave, (LONG)p[1] >> 8);
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    Log("cas trim: quarantine save #%ld: held %ld of %ld%s, dwords 0x40A00000 in held blocks now %ld", g_ctInvSave, g_ctQuarN, g_ctQuarCap,
        g_ctQuarFull ? " (full, erases stopped)" : "", hits);
}
static void CtInvSaveLog() {  // after the save returned (CtSaveHook): one line per save that ran the inventory
    if (g_ctTrapOn) CtTrapArm();  // docs/539 §16
    CtQuarScan();
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    const double ms = f.QuadPart ? 1000.0 / (double)f.QuadPart : 0.0;
    char vt[CT_VTN * 18 + 8];
    const Ts3nCounters& z = ts3native_counters;
    if (z.mbCalls) {  // docs/539 §14, session totals
        CtVtText(g_ctMbVt, vt, sizeof(vt));
        Log("cas trim: modelbuilder check (0x5D83B4 / 0x5D96AD, session) at save #%ld: calls %ld, records %ld, cells %ld, bad %ld (%ld touching an erased object), "
            "skips %ld (MbGuard %d); %.2f ms in all, max %.1f us a call; result vtables:%s", g_ctInvSave, z.mbCalls, g_ctMbRecs, z.mbCells, z.mbBad, g_ctMbGone, z.mbSkip,
            g_ctMbGuard, (double)g_ctMbQpc * ms, (double)g_ctMbMaxQpc * ms * 1000.0, vt);
    }
    if (z.osCalls) {  // docs/539 §15, session totals
        CtVtText(g_ctOsVt, vt, sizeof(vt));
        Log("cas trim: outfit string check (0x5E7BC2, session) at save #%ld: calls %ld, records %ld, objects %ld, bad %ld (%ld touching an erased object), "
            "resets %ld (OutfitStrGuard %d); %.2f ms in all, max %.1f us a call; object vtables:%s", g_ctInvSave, z.osCalls, g_ctOsRecs, z.osObjs, z.osBad, g_ctOsGone,
            z.osFix, g_ctOsGuard, (double)g_ctOsQpc * ms, (double)g_ctOsMaxQpc * ms * 1000.0, vt);
    }
    if (z.rlCalls)  // docs/539 §16.6, session totals
        Log("cas trim: realm shape check (0x682080 / 0x6823E0, session) at save #%ld: calls %ld, bad %ld, repaired %ld, skipped %ld (RealmGuard %d)", g_ctInvSave,
            z.rlCalls, z.rlBad, z.rlFix, z.rlSkip, g_rlGuard);
    if (g_ctRelCalls) {  // the release sites (docs/539 §13.5 item 3), session totals
        CtVtText(g_ctCellVt[1], vt, sizeof(vt));
        Log("cas trim: outfit release check (0x5E6E60 / 0x5E7633, session) at save #%ld: calls %ld, cells %ld, bad %ld, record slots of another TS3W type %ld; float-5 %ld (session, all paths); "
            "%.2f ms in all, max %.1f us a call; result vtables:%s", g_ctInvSave, g_ctRelCalls, g_ctRelCells, g_ctRelBad, g_ctRelOther, g_ctF5, (double)g_ctRelQpc * ms,
            (double)g_ctRelMaxQpc * ms * 1000.0, vt);
    }
    if (!g_ctInvCalls) return;
    CtVtText(g_ctCellVt[0], vt, sizeof(vt));
    Log("cas trim: inventory check save #%ld: outfits %ld, records %ld, slots %ld, bad %ld (session %ld, %ld in an erased object), %.2f ms",
        g_ctInvSave, g_ctInvCalls, g_ctInvRecs, g_ctInvSlots, g_ctInvBadSave, g_ctInvBad, g_ctInvGone, (double)g_ctInvQpc * ms);
    Log("cas trim: inventory cells save #%ld: cells %ld, bad %ld, float-5 %ld (session: cells bad %ld, %ld in an erased object, float-5 %ld); result vtables:%s",
        g_ctInvSave, g_ctInvCells, g_ctInvCellBad, g_ctF5Save, g_ctCellBad, g_ctCellGone, g_ctF5, vt);
    g_ctInvCalls = g_ctInvRecs = g_ctInvSlots = g_ctInvBadSave = 0, g_ctInvQpc = 0, g_ctInvCells = g_ctInvCellBad = g_ctF5Save = 0;
    memset(g_ctCellVt[0], 0, sizeof(g_ctCellVt[0]));
}
// docs/539 §13.5 item 3: the call sites 0x5E6E60 (the clear 0x5E6E20; return 0x5E6E65) and 0x5E7633 (the destructor 0x5E75A0;
// 0x5E7638) -> 0x5AF640, thiscall (&outfit[+0xBC], begin, end), ret 8 [verified disassembly]. The clear's part records are still
// there (it frees them after this call and empties [+0x98, +0x9C)), so both get checked; the destructor's call comes after the
// clear, usually on an empty range. Main thread in the 11-08 crash. EA runs unchanged after the check.
typedef void(__fastcall* CtRelFn)(DWORD* vec, void* edx, DWORD* b, DWORD* e);
static CtRelFn g_ctRelEa = (CtRelFn)(uintptr_t)0x005AF640;  // selftest: a fake
static bool g_ctRelDetect = true;  // [CasTrim] Detect: the record check at these sites too
static int CtRelPath(DWORD ret) { return ret == 0x005E7638 ? 2 : 1; }
static void __fastcall CtRelHook(DWORD* vec, void* edx, DWORD* b, DWORD* e) {
    LARGE_INTEGER q0, q1;
    QueryPerformanceCounter(&q0);
    const int path = CtRelPath((DWORD)(uintptr_t)_ReturnAddress());
    const u8* outfit = (const u8*)vec - 0xBC;
    if (g_ctTrapOn) CtTrapDrop(outfit, path);  // docs/539 §16: before EA's release writes the cells
    InterlockedIncrement(&g_ctRelCalls);
    const LONG f50 = g_ctF5;
    if (g_ctRelDetect) CtInvCheck(NULL, outfit, path);  // the part records: a detector only (Detect)
    CtCellCheck(path, NULL, outfit, b, e, (const DWORD*)_AddressOfReturnAddress());
    if (path == 2) F5wGone((DWORD)(uintptr_t)outfit);  // docs/539 §21: the destructor's release
    else F5wOutfit(outfit, g_ctF5 == f50, kCtPath[path], false);
    QueryPerformanceCounter(&q1);
    const LONGLONG d = q1.QuadPart - q0.QuadPart;
    g_ctRelQpc += d;
    if (d > g_ctRelMaxQpc) g_ctRelMaxQpc = d;
    g_ctRelEa(vec, edx, b, e);
}
// docs/539 §16.6 (11-09 bp-float5-memtravel-x8-guard crash at 0x6820F4: a node's shape vector held 0x40A00000): the realm
// shape check, with [CasTrim] Detect=1 or RealmGuard=1, any Mode. Five call sites: Realm_RemoveShape 0x682080 (thiscall
// (realm, shape), ret 4) from 0x6821FA (node-empty loop 0x6821E0, esi = node), 0x686BE1, 0x686FBE; Realm_AddShape 0x6823E0
// ((realm, shape, node; 0 = root [realm]), ret 8) from 0x686BFA, 0x686F46 [verified disassembly TS3W 1.67]. EA's invariants
// (0x6823E0 / 0x682080): realm vector [realm+8, +0xC) holds every shape, [s+0x38] = its index there; node vector
// [node+0x1C, +0x20) holds the node's shapes, [s+0x3C] = its index there, [s+0x50] = node. The remove swaps the node
// vector's last entry into the removed slot and writes [last+0x3C] without a check (the crash); the realm vector part is
// bounds- and identity-checked (0x682094 / 0x68209B). Before EA: every node vector entry and the realm vector's last entry
// are checked for 0x40A00000 that is not a real shape at that index ("cas trim: realm shape bad", first 40, with node /
// index / caller / stack scan). RealmGuard=1 (default 0): a bad node entry gets back the one realm-vector shape whose
// [s+0x50] / [s+0x3C] name that node and index (restored), else it is swap-removed as EA's remove does; a bad realm last
// entry is dropped (EA's own checks skip a shape no longer listed); a 0x40A00000 shape argument is not passed to EA.
typedef void(__fastcall* RlRemFn)(u8* realm, void* edx, u8* shape);
typedef void(__fastcall* RlAddFn)(u8* realm, void* edx, u8* shape, u8* node);
static RlRemFn g_rlRemEa = (RlRemFn)(uintptr_t)0x00682080;  // selftest: fakes
static RlAddFn g_rlAddEa = (RlAddFn)(uintptr_t)0x006823E0;
static const DWORD RL_LOOP_RET = 0x006821FF;  // 0x6821FA's return: the shape is [node+0x20]-4 of esi
static volatile LONG g_rlLogs;
static DWORD volatile g_rlLoopNode;  // esi at 0x6821FA (RlLoopStub)
static bool RlIsShape(DWORD v, DWORD node, DWORD at, DWORD i) {  // [v+at] == i (and [v+0x50] == node when node)
    __try {
        const DWORD* p = (const DWORD*)(uintptr_t)v;
        return p[at / 4] == i && (!node || p[0x50 / 4] == node);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static DWORD RlFind(const u8* realm, DWORD node, DWORD i) {  // the one realm-vector shape listed at node index i, 0 none or two
    DWORD hit = 0;
    __try {
        const DWORD *b = *(const DWORD* const*)(realm + 8), *e = *(const DWORD* const*)(realm + 0xC);
        if (!b || e < b || e - b > (1 << 20)) return 0;
        for (const DWORD* p = b; p < e; p++)
            if (*p != CT_F5 && *p && RlIsShape(*p, node, 0x3C, i)) {
                if (hit) return 0;
                hit = *p;
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
    return hit;
}
static void RlLog(const char* what, const u8* realm, DWORD node, LONG i, LONG n, DWORD shape, bool add, const char* fix, DWORD ret, const DWORD* sp) {
    UafNearLog(node, "realm shape node"), UafNearLog((DWORD)(uintptr_t)realm, "realm"), UafNearLog(shape, "realm shape");  // [UafCheck] (docs/539 §17)
    if (InterlockedIncrement(&g_rlLogs) > 40) return;
    DWORD f[10] = {};
    const int nf = CrStack((DWORD)(uintptr_t)sp, f, 10);
    char fr[10 * 44 + 8];
    CtFrames(f, nf, fr, sizeof(fr));
    Log("cas trim: realm shape bad: %s; %s from 0x%08lX, thread %lu; realm 0x%08lX node 0x%08lX entry %ld of %ld = 0x40A00000; shape 0x%08lX; %s; save #%ld, "
        "%ld s after world load #%ld; stack scan (%d):%s", what, add ? "Realm_AddShape 0x6823E0" : "Realm_RemoveShape 0x682080", ret, GetCurrentThreadId(),
        (DWORD)(uintptr_t)realm, node, i, n, shape, fix, g_ctInvSave, g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, nf, fr);
}
static void RlCheck(u8* realm, DWORD node, DWORD shape, bool add, DWORD ret, const DWORD* sp) {
    Ts3nCounters& z = ts3native_counters;
    const LONG bad0 = z.rlBad;
    if (node) __try {
            for (LONG i = 0;; i++) {
                DWORD *b = *(DWORD**)(uintptr_t)(node + 0x1C), *e = *(DWORD**)(uintptr_t)(node + 0x20);
                const LONG n = (LONG)(e - b);
                if (!b || e < b || n > 65536 || i >= n) break;
                if (b[i] != CT_F5 || RlIsShape(CT_F5, node, 0x3C, (DWORD)i)) continue;
                InterlockedIncrement(&z.rlBad);
                F5wBad("realm node shape vector entry", node, (DWORD)(uintptr_t)(b + i), CT_F5);  // docs/539 §21
                const char* fix = "log only (RealmGuard 0)";
                char fx[96];
                if (g_rlGuard) {
                    if (const DWORD got = RlFind(realm, node, (DWORD)i)) {
                        b[i] = got;
                        sprintf_s(fx, "restored shape 0x%08lX (its node and node index)", got), fix = fx;
                    } else {  // EA's swap-remove of slot i
                        const DWORD last = e[-1];
                        b[i] = last;
                        if (last != CT_F5 && RlIsShape(last, node, 0x3C, (DWORD)(n - 1))) *(DWORD*)(uintptr_t)(last + 0x3C) = (DWORD)i;
                        *(DWORD**)(uintptr_t)(node + 0x20) = e - 1;
                        fix = "dropped (no realm shape names it; swapped with the last entry as EA's remove does)";
                        i--;  // slot i holds the old last now
                    }
                    InterlockedIncrement(&z.rlFix);
                }
                RlLog("node shape vector entry", realm, node, fix[0] == 'd' ? i + 1 : i, n, shape, add, fix, ret, sp);
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (node && z.rlBad == bad0) F5wOk(node, F5K_NOTE, add ? "realm add shape check" : "realm remove shape check");  // docs/539 §21
    __try {  // the realm vector: only the last entry is used blind (0x6820A3 / 0x6820AF)
        DWORD *b = *(DWORD**)(realm + 8), *e = *(DWORD**)(realm + 0xC);
        const LONG n = (LONG)(e - b);
        if (b && e > b && n <= (1 << 20) && e[-1] == CT_F5 && !RlIsShape(CT_F5, 0, 0x38, (DWORD)(n - 1))) {
            InterlockedIncrement(&z.rlBad);
            F5wBad("realm vector last entry", (DWORD)(uintptr_t)realm, (DWORD)(uintptr_t)(e - 1), CT_F5);  // docs/539 §21
            if (g_rlGuard) *(DWORD**)(realm + 0xC) = e - 1, InterlockedIncrement(&z.rlFix);
            RlLog("realm vector last entry", realm, 0, n - 1, n, shape, add, g_rlGuard ? "dropped (EA's remove checks index and identity)" : "log only (RealmGuard 0)", ret, sp);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
static void __fastcall RlRemHook(u8* realm, void* edx, u8* shape) {
    Ts3nCounters& z = ts3native_counters;
    InterlockedIncrement(&z.rlCalls);
    const DWORD ret = (DWORD)(uintptr_t)_ReturnAddress(), s = (DWORD)(uintptr_t)shape;
    const DWORD* sp = (const DWORD*)_AddressOfReturnAddress();
    DWORD node = 0;
    if (s == CT_F5) node = ret == RL_LOOP_RET ? g_rlLoopNode : 0;
    else __try { node = *(const DWORD*)(shape + 0x50); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    RlCheck(realm, node, s, false, ret, sp);
    if (s == CT_F5) {  // EA reads [shape+0x38] first: a certain fault
        InterlockedIncrement(&z.rlBad);
        if (g_rlGuard) InterlockedIncrement(&z.rlSkip);
        RlLog("shape argument", realm, node, -1, 0, s, false, g_rlGuard ? "not passed to EA (its node entry was checked above)" : "log only (RealmGuard 0)", ret, sp);
        if (g_rlGuard) return;
    }
    g_rlRemEa(realm, edx, shape);
}
static void __fastcall RlAddHook(u8* realm, void* edx, u8* shape, u8* node) {
    InterlockedIncrement(&ts3native_counters.rlCalls);
    DWORD nd = (DWORD)(uintptr_t)node;
    if (!nd) __try { nd = *(const DWORD*)realm; } __except (EXCEPTION_EXECUTE_HANDLER) {}  // EA's default: the root
    RlCheck(realm, nd, (DWORD)(uintptr_t)shape, true, (DWORD)(uintptr_t)_ReturnAddress(), (const DWORD*)_AddressOfReturnAddress());
    g_rlAddEa(realm, edx, shape, node);
}
__declspec(naked) static void RlLoopStub() {  // 0x6821FA: keep esi (the node being emptied), then the hook
    __asm {
        mov dword ptr [g_rlLoopNode], esi
        jmp RlRemHook
    }
}
static bool g_rlIn;
static void CtInstallRl() {
    if (g_rlIn) return;
    static const CallSite rl[] = {{0x006821FA, 0x00682080, (void*)RlLoopStub}, {0x00686BE1, 0x00682080, (void*)RlRemHook}, {0x00686FBE, 0x00682080, (void*)RlRemHook},
                                  {0x00686BFA, 0x006823E0, (void*)RlAddHook}, {0x00686F46, 0x006823E0, (void*)RlAddHook}};
    if (!PatchCalls("cas trim realm shape sites", rl, 5)) return;
    g_rlIn = true;
    Log("cas trim: realm shape check on (docs/539 §16.6): the 3 calls to Realm_RemoveShape 0x682080 and 2 to Realm_AddShape 0x6823E0 check the node's "
        "shape vector and the realm vector's last entry for 0x40A00000 first; RealmGuard %d (%s)", g_rlGuard,
        g_rlGuard ? "a bad node entry is restored from the realm vector or swap-removed, a bad realm last entry dropped, a 0x40A00000 shape not passed on" : "log only");
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

// docs/539 §13.5 item 4, crashfx.cpp: a fault at 0xD4B156 (EA's result callback 0xD4B150: the result's vtable is read, then
// slot 1) or 0x5AF67E (the same Release inlined in 0x5AF640). Frames [verified disassembly; the 11-08 EA report's stack data]:
// - 0xD4B156 from 0x5AF640: [esp] 0x7287EC, [esp+4] the value, +0x14 0x728DD4, +0x18 and +0x2C the cell, +0x28 0x5AF66D,
//   +0x34 / +0x38 0x5AF640's saved edi (= outfit + 0xBC) / esi (= the outfit), +0x3C its return (0x5E6E65 clear, 0x5E7638
//   destructor), +0x40 begin, +0x44 end. Another caller of the callback: the value only.
// - 0x5AF67E: esi the cell, ecx the value, eax its first dword; [esp] saved edi, +4 saved esi (the outfit), +8 return, +0xC begin, +0x10 end.
static volatile LONG g_ctRelFaults;
static bool CtRelFaultText(const CONTEXT* c, char* out, size_t cap) {  // true: the 0x5AF640 frame was found
    DWORD f[18] = {};
    bool fr = false;
    __try { memcpy(f, (const void*)(uintptr_t)c->Esp, sizeof(f)), fr = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    DWORD cell = 0, v = 0, outfit = 0, ret = 0, b = 0, e = 0;
    bool frame = false;
    if (c->Eip == 0x005AF67E) cell = c->Esi, v = c->Ecx, outfit = f[1], ret = f[2], b = f[3], e = f[4], frame = fr;
    else {
        v = f[1];
        if (fr && f[0] == 0x007287EC && f[5] == 0x00728DD4 && f[10] == 0x005AF66D) cell = f[11], outfit = f[14], ret = f[15], b = f[16], e = f[17], frame = true;
    }
    DWORD w[4] = {}, ovt = 0;
    bool vOk = false;
    __try { if (v) memcpy(w, (const void*)(uintptr_t)v, 16), vOk = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { if (outfit) ovt = *(const DWORD*)(uintptr_t)outfit; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    char gone[160], sl[160], nb[17 * 11 + 24] = " (no frame)";
    CtGoneText(v, gone, sizeof(gone)), CtSlotText(v, sl, sizeof(sl));
    const int n = frame && e >= b && e - b < 4096 * 4 ? (int)((e - b) / 4) : 0, idx = frame && cell >= b && cell < e ? (int)((cell - b) / 4) : -1;
    if (idx >= 0) CtNear((const DWORD*)(uintptr_t)b, n, idx, nb, sizeof(nb));
    sprintf_s(out, cap, "crash forensics: cas trim outfit release fault at 0x%08lX (%s): value 0x%08lX%s, first dwords %s%08lX %08lX %08lX %08lX; %s; %s; cell 0x%08lX "
              "(entry %d of %d, 0x%08lX-0x%08lX), outfit 0x%08lX (vtable %08lX), return 0x%08lX (%s); outfit last seen by the inventory check at save #%ld; "
              "release checks: calls %ld, cells %ld, bad %ld, guard skips %ld (CellGuard %d); float-5 %ld; CasTrim mode %ld, in a save %ld, save #%ld (travel flags %d), "
              "%ld s after world load #%ld; cells -8..+8:%s", c->Eip, frame ? "0x5AF640 frame" : "value only", v, v == CT_F5 ? " (float 5.0)" : "", vOk ? "" : "(unreadable) ",
              w[0], w[1], w[2], w[3], gone, sl, cell, idx, n, b, e, outfit, ovt, ret, ret == 0x005E6E65 ? "clear 0x5E6E20" : ret == 0x005E7638 ? "destructor 0x5E75A0" : "?",
              outfit ? CtSeenSave((const u8*)(uintptr_t)outfit) : -1L, g_ctRelCalls, g_ctRelCells, g_ctRelBad, ts3native_counters.ctCellSkip, g_ctCellGuard, g_ctF5,
              ts3native_counters.ctMode, g_ctInSave, g_ctInvSave, g_swTravel, g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, nb);
    return frame;
}
static void CtRelFault(const CONTEXT* c) {
    if (InterlockedIncrement(&g_ctRelFaults) > 2) return;
    char t[3072];
    if (!CtMbFaultText(c, t, sizeof(t))) CtRelFaultText(c, t, sizeof(t));  // docs/539 §14: the 0x5D82A0 frame first
    Log("%s", t);
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
            if (g_ctQuarCap && !CtQuarHold(c.o)) { g_ctPendN = 0; break; }  // §15.7: held for good, or full = stop
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
    g_ctSweepAt = GetTickCount() | 1, g_ctSweepSave = g_ctInvSave;  // docs/539 §14: the modelbuilder lines name it
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
        InterlockedIncrement(&g_ctInSave);
        const bool r0 = g_ctSave(svc, edx, stream);
        InterlockedDecrement(&g_ctInSave);
        CtInvSaveLog();
        return r0;
    }
    const DWORD t0 = GetTickCount(), at = (DWORD)(uintptr_t)_ReturnAddress();
    z.ctSaves++, g_ctPendN = 0, g_ctPendGo = false, g_ctMarksLate = 0, g_ctAfterSave = false;
    int flag = -1;
    __try { flag = *(const u8*)(uintptr_t)(*(const DWORD*)(uintptr_t)0x011ECBC4 + 0x45); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const LONG marks = g_ctMarksF, sinceSave = g_ctMarksS;
    g_ctMarksS = 0;
    InterlockedIncrement(&g_ctInSave);
    const bool r = g_ctSave(svc, edx, stream);
    InterlockedDecrement(&g_ctInSave);
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
}
// docs/539 §12.5: the inventory detector ([CasTrim] Detect, on with Mode >= 1 or Probe): one call site, run once per outfit
// in a save (0.7-3.9 ms a save, castrim-live 11-08). The erase ring (CtGoneRec) and the 0x5E772C fault decoder (crashfx.cpp)
// need no install. Probe's sites stay out of it: they are diagnostics (CacheOutfit names, load counts, the outfit map walk).
static bool CtDetectOn(const wchar_t* ini, int mode, bool probe) { return GetPrivateProfileIntW(L"CasTrim", L"Detect", mode >= 1 || probe ? 1 : 0, ini) > 0; }
static void CtInstallDetect() {
    static const CallSite inv[] = {{0x005EFE11, 0x005E7690, (void*)CtInvStub}};
    if (PatchCalls("cas trim inventory check site", inv, 1))
        Log("cas trim: inventory check on (docs/539 §12.5, §13.5): each save's outfit inventory call 0x5EFE11 checks every part record slot and "
            "[outfit+0xBC] result cell first; float-5 watch; erase ring %lu", CT_GRING);
}
// docs/539 §13.5 item 3 + CellGuard: the two release call sites, with Detect or CellGuard (any mode, no tables needed).
static bool g_ctRelIn;
static void CtInstallRel(bool detect) {
    g_ctRelDetect = detect;
    if (g_ctRelIn) return;
    static const CallSite rel[] = {{0x005E6E60, 0x005AF640, (void*)CtRelHook}, {0x005E7633, 0x005AF640, (void*)CtRelHook}};
    if (!PatchCalls("cas trim outfit release sites", rel, 2)) return;
    g_ctRelIn = true;
    Log("cas trim: outfit release check on (docs/539 §13.5): the clear 0x5E6E60 and destructor 0x5E7633 calls to 0x5AF640 check the "
        "[outfit+0xBC] cells%s first; CellGuard %d (%s)", detect ? " and part records" : "", g_ctCellGuard,
        g_ctCellGuard ? "a cell that is not a valid object is set to 0 and not released" : "log only");
}
// docs/539 §14: the two calls to 0x5D82A0, with Detect or MbGuard (any mode, no tables needed).
static bool g_ctMbIn;
static void CtInstallMb() {
    if (g_ctMbIn) return;
    static const CallSite mbs[] = {{0x005D83B4, 0x005D82A0, (void*)CtMbHook}, {0x005D96AD, 0x005D82A0, (void*)CtMbHook}};
    if (!PatchCalls("cas trim modelbuilder release sites", mbs, 2)) return;
    g_ctMbIn = true;
    Log("cas trim: modelbuilder release check on (docs/539 §14): the calls 0x5D83B4 and 0x5D96AD to 0x5D82A0 check the part records' cells "
        "rec+4..+0x10 first; MbGuard %d (%s)", g_ctMbGuard, g_ctMbGuard ? "a cell that is not a valid object is set to 0 and not released" : "log only");
}
// docs/539 §15: the outfit save writer's call 0x5E7BC2 to 0x5E61A0, with Detect or OutfitStrGuard (any mode, no tables needed).
static bool g_ctOsIn;
static void CtInstallOs() {
    if (g_ctOsIn) return;
    static const CallSite os[] = {{0x005E7BC2, 0x005E61A0, (void*)CtOsHook}};
    if (!PatchCalls("cas trim outfit string site", os, 1)) return;
    g_ctOsIn = true;
    Log("cas trim: outfit string check on (docs/539 §15): the save writer's call 0x5E7BC2 to 0x5E61A0 checks each part object's string [rec+0x5C]+0x2C first; "
        "OutfitStrGuard %d (%s)", g_ctOsGuard, g_ctOsGuard ? "a string EA's copy would certainly fault on is reset to EA's empty string" : "log only");
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
    const bool detect = CtDetectOn(ini, mode, g_ctProbe);
    g_ctCellGuard = GetPrivateProfileIntW(L"CasTrim", L"CellGuard", 1, ini) > 0;  // Rodney 2026-11-08: on in every mode
    if (g_ctCellGuard || detect) RhRanges(), CtInstallRel(detect);
    g_ctMbGuard = GetPrivateProfileIntW(L"CasTrim", L"MbGuard", 0, ini) > 0;  // docs/539 §14: default 0, log only
    if (detect || g_ctMbGuard) RhRanges(), CtInstallMb();
    g_ctOsGuard = GetPrivateProfileIntW(L"CasTrim", L"OutfitStrGuard", 0, ini) > 0;  // docs/539 §15
    if (detect || g_ctOsGuard) RhRanges(), CtInstallOs();
    g_rlGuard = GetPrivateProfileIntW(L"CasTrim", L"RealmGuard", 0, ini) > 0;  // docs/539 §16.6: default 0, log only
    if (detect || g_rlGuard) RhRanges(), CtInstallRl();
    const int quar = GetPrivateProfileIntW(L"CasTrim", L"Quarantine", 0, ini);  // docs/539 §15.7, test only
    if (quar > 0 && !g_ctQuar) {
        g_ctQuarCap = quar > 65536 ? 65536 : quar;
        g_ctQuar = (DWORD*)VirtualAlloc(NULL, (SIZE_T)g_ctQuarCap * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!g_ctQuar) g_ctQuarCap = 0, Log("FAIL cas trim: no memory for the quarantine list: off");
        else Log("cas trim: quarantine on (docs/539 §15.7, test only): each sweep erase keeps 1 reference for the session, at most %ld; then no more erases", g_ctQuarCap);
    }
    if (mode <= 0 && !g_ctProbe && !detect && GetPrivateProfileIntW(L"CasTrim", L"Install", 0, ini) <= 0) return;
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
    if (detect) CtInstallDetect();
    if (GetPrivateProfileIntW(L"CasTrim", L"WriteTrap", 0, ini) > 0 && !g_ctTrapOn) {  // docs/539 §16, test only
        if (!detect) Log("FAIL cas trim: WriteTrap=1 needs Detect=1 (the save's inventory pass picks the cells): off");
        else {
            WpInstall();
            g_wpTrapFn = CtTrapHit, g_ctTrapOn = g_wpVeh != NULL;
            if (HANDLE h = g_ctTrapOn ? CreateThread(NULL, 64 << 10, CtTrapRefresh, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL) : NULL) CloseHandle(h);
            Log("cas trim: write trap %s (docs/539 §16, test only): after each save up to 4 [outfit+0xBC] cells of vtable %08lX outfits (entry 87 of 121 / 91, "
                "3 of 81 first) get a DR write watch on every thread (again every 30 s for new threads); each write logged (first 200, float5 WRITER first 100)",
                g_ctTrapOn ? "on" : "NOT on (no vectored handler)", CT_TRAP_VT);
        }
    }
    Ts3nCounters& z = ts3native_counters;
    z.ctOn = 1, z.ctMode = mode < 0 ? 0 : mode > 4 ? 4 : mode;
    if (z.ctMode >= 3 && !sweep) z.ctMode = 0, Log("FAIL cas trim: the save hooks are not in: modes 3 and 4 off");
    if (z.ctMode >= 3) Log("cas trim: mode %ld (%s): at each save, part compositors outside EA's live set; MinAgeSec %lu, Probe %d, Detect %d", z.ctMode,
                           z.ctMode == 3 ? "sweep" : "count only", g_ctMinAgeMs / 1000, g_ctProbe, detect);
    Log("cas trim: on (docs/191 step 2), mode %ld (%s): part compositors at 2 references, not inserted or found for %lu s; %d buckets a "
        "step, at most %d erases a step; %s; %s; %s", z.ctMode, z.ctMode == 1 ? "trim with EA's erase 0x5EE640" : z.ctMode == 2 ? "count only" : "armed, off",
        g_ctAgeMs / 1000, g_ctBuckets, g_ctMaxErase, g_ctNamed ? "only ones no other compositor names (Named 1)" : "named or not (Named 0)",
        g_ctNeedDb ? "only ones a database holds (NeedDb 1)" : "with or without a database (NeedDb 0)",
        g_ctAsync ? "never ones the async loader found (Async 1)" : "found by the async loader or not (Async 0)");
}
