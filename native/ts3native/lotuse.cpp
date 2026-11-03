// [RouteSnap] LotUseReadout (0.36.73, docs/66 §7.1 A4 readout, counters v133; default off). Readout only, no per-lot clone:
// which clone lots the route jobs read and what each lot costs, to decide between "never copy untouched lots" and the full
// per-lot on-demand clone (A4). Included by ts3native.cpp after stalelot.cpp.
//   - jobs: the clone run's read set from StaleLot's LotTable_Find detour (stalelot.cpp, installed here when StaleLot is off;
//     same limits: lots reached without a key lookup are not seen, > kStlKeys lots = luOver), per lot: runs and distinct agents;
//   - bytes: a read-only walk of g_rpClone on the replay consumer thread (the clone's only writer) between its rounds, asked by
//     each [UserDiag] memory sample: per lot footprints (map nodes, FMEs, footprint copy blocks without their game-allocated
//     polygon caches), quadtree (Q objects, inner and leaf nodes, leaf lists), portals (portal pointer vectors of the lot
//     graph and its rooms), other (lot node copy, other graph vectors, room records). The world graph R is not in the clone
//     (live / stage data): its share is 0 here. Map entries with no Q go to "no lot";
//   - lines: "lot use: ..." summary with the share of lot bytes on lots no job touched, the top 20 lots by bytes; at exit the
//     summary again from the last walk's bytes and the final job counts.

struct LuLotB { u64 key; DWORD b[4]; };   // bytes: fp, tree, portals, other
struct LuTouch { u64 key; LONG runs; int nAg; DWORD ag[64]; };
static const int kLuMax = 512;
static LuLotB g_luB[kLuMax];
static int g_luNB;
static DWORD g_luNoLot, g_luWalkN;
static LuTouch g_luT[kLuMax];
static int g_luNT;
static volatile LONG g_luLock, g_luAsk;
static void LuReport(const char* when, LONG walkUs);

static void LuNoteRun(DWORD agent) {  // route worker, right after P5Plan's clone run (t_stlKeys = its read set)
    if (!g_luOn) return;
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.luRuns);
    if (t_stlOver) InterlockedIncrement(&k.luOver);
    while (InterlockedExchange(&g_luLock, 1)) YieldProcessor();
    for (int i = 0; i < t_stlN; i++) {
        int j = 0;
        while (j < g_luNT && g_luT[j].key != t_stlKeys[i]) j++;
        if (j == g_luNT) {
            if (g_luNT == kLuMax) continue;
            g_luT[g_luNT].key = t_stlKeys[i], g_luT[g_luNT].runs = 0, g_luT[g_luNT].nAg = 0, g_luNT++;
        }
        LuTouch& t = g_luT[j];
        t.runs++;
        int a = 0;
        while (a < t.nAg && t.ag[a] != agent) a++;
        if (a == t.nAg && t.nAg < 64) t.ag[t.nAg++] = agent;  // shortcut: distinct agents capped at 64 per lot ("64" = 64+)
    }
    InterlockedExchange(&g_luLock, 0);
}
static const LuTouch* LuTouchOf(u64 key) {  // g_luLock held
    for (int j = 0; j < g_luNT; j++) if (g_luT[j].key == key) return &g_luT[j];
    return NULL;
}

static DWORD LuSpan(const u8* g, DWORD off) { const u8* b = RpP(g + off); const u8* e = RpP(g + off + 4); return b && e > b ? (DWORD)(e - b) : 0; }
static DWORD LuTree(const u8* n, int depth) {
    if (!n || depth > 40) return 0;
    if (n[4] & 1) { DWORD s = 0x1C; for (int i = 0; i < 4; i++) s += LuTree(RpP(n + 0xC + 4 * i), depth + 1); return s; }
    return 0x14 + ((n[8] & 1) ? RpD(n + 0xC) * 12 : 0);
}
struct LuQ { DWORD q; int lot; };
static int __cdecl LuQCmp(const void* a, const void* b) { const DWORD x = ((const LuQ*)a)->q, y = ((const LuQ*)b)->q; return x < y ? -1 : x > y; }

// the walk (replay consumer, outside its locks; the consumer is the clone's only writer)
static void LuWalk() {
    Ts3nCounters& k = ts3native_counters;
    const RpClone* c = g_rpClone;
    if (!c || c->nLots <= 0) return;
    const int nl = min(c->nLots, kLuMax);
    LuQ* qs = (LuQ*)VirtualAlloc(NULL, nl * 16 * sizeof(LuQ), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    const DWORD cap = 1u << 18;  // footprint block set (k.rpFps ~ tens of thousands)
    DWORD* seen = (DWORD*)VirtualAlloc(NULL, cap * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!qs || !seen) { if (qs) VirtualFree(qs, 0, MEM_RELEASE); if (seen) VirtualFree(seen, 0, MEM_RELEASE); return; }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    int nq = 0;
    DWORD noLot = 0;
    __try {
        for (int i = 0; i < nl; i++) {
            const RpLot& l = c->lots[i];
            LuLotB& b = g_luB[i];
            b.key = l.key, b.b[0] = b.b[1] = b.b[2] = b.b[3] = 0;
            for (int j = 0; j < l.nQ; j++)
                if (l.q[j]) { qs[nq].q = (DWORD)(uintptr_t)l.q[j], qs[nq].lot = i, nq++; b.b[1] += kRpQSz + LuTree(RpP(l.q[j] + 0x24), 0); }
            if (const u8* node = l.node) {
                const u8* g = RpGraph((u8*)node);
                b.b[2] += LuSpan(g, 0x44) + LuSpan(g, 0x54);
                b.b[3] += kRpNodeSz + LuSpan(g, 0x64) + LuSpan(g, 0x74) + LuSpan(g, 0x90);
                if (l.rooms) for (const u8* r = RpP(g + 0x90); r && r + 0x44 <= RpP(g + 0x94); r += 0x44) b.b[2] += LuSpan(r, 4) + LuSpan(r, 0x14);
            }
        }
        qsort(qs, nq, sizeof(LuQ), LuQCmp);
        if (const u8* m = c->m) {
            const DWORD* bk = (const DWORD*)RpP(m + 4);
            const DWORD nb = RpD(m + 8);
            for (DWORD i = 0; nb > 1 && i < nb; i++)
                for (const u8* nd = (const u8*)(uintptr_t)bk[i]; nd; nd = RpP(nd + 0x10)) {
                    const u8* f = RpP(nd + 8);
                    const DWORD q = f ? RpD(f + 0xC) : 0;
                    int lo = 0, hi = nq - 1, lot = -1;
                    while (q && lo <= hi) { const int mid = (lo + hi) / 2; if (qs[mid].q == q) { lot = qs[mid].lot; break; } if (qs[mid].q < q) lo = mid + 1; else hi = mid - 1; }
                    DWORD s = 0x18 + (f ? 0x14 : 0);
                    if (const u8* fp = f ? RpP(f + 8) : NULL) {
                        const u8* top = RpIsChild(fp) && RpP(fp + 0xC) ? RpP(fp + 0xC) : fp;
                        DWORD h = ((DWORD)(uintptr_t)top >> 4) * 2654435761u & (cap - 1);
                        while (seen[h] && seen[h] != (DWORD)(uintptr_t)top) h = (h + 1) & (cap - 1);
                        if (!seen[h]) { seen[h] = (DWORD)(uintptr_t)top; s += 16 + ((const RpFpHdr*)(top - 16))->bytes; }
                    }
                    if (lot >= 0) g_luB[lot].b[0] += s; else noLot += s;
                }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { InterlockedIncrement(&k.rpFaults); nq = -1; }
    VirtualFree(qs, 0, MEM_RELEASE), VirtualFree(seen, 0, MEM_RELEASE);
    if (nq < 0) { g_luNB = 0; return; }
    g_luNB = nl, g_luNoLot = noLot, g_luWalkN++;
    InterlockedIncrement(&k.luWalks);
    LuReport("sample", UsSince(t0));
}

static void LuReport(const char* when, LONG walkUs) {
    Ts3nCounters& k = ts3native_counters;
    const int n = g_luNB;
    if (!n) { Log("lot use (%s): no clone walk yet; %ld runs noted", when, k.luRuns); return; }
    u64 all = 0, un = 0, part[4] = {};
    int touched = 0;
    static DWORD sz[kLuMax];
    static bool used[kLuMax];
    int top[20], nTop = 0;
    // at exit a killed worker may hold the lock: bounded spin, then read without it (counts only)
    bool locked = false;
    for (int s = 0; s < 1000000 && !(locked = !InterlockedExchange(&g_luLock, 1)); s++) YieldProcessor();
    for (int i = 0; i < n; i++) {
        sz[i] = g_luB[i].b[0] + g_luB[i].b[1] + g_luB[i].b[2] + g_luB[i].b[3], used[i] = false;
        for (int p = 0; p < 4; p++) part[p] += g_luB[i].b[p];
        all += sz[i];
        if (LuTouchOf(g_luB[i].key)) touched++; else un += sz[i];
    }
    for (; nTop < 20 && nTop < n; nTop++) {  // top 20 by bytes
        int best = -1;
        for (int i = 0; i < n; i++) if (!used[i] && (best < 0 || sz[i] > sz[best])) best = i;
        used[best] = true, top[nTop] = best;
    }
    const LONG pm = all ? (LONG)(un * 1000 / all) : 0;
    k.luLots = n, k.luTouched = touched, k.luUntouchedPermille = pm, k.luUntouchedKB = (LONG)(un >> 10);
    Log("lot use (%s #%lu): %d clone lots, %llu KB in lots (footprints %llu, quadtree %llu, portals %llu, other %llu; world graph "
        "not in the clone), no-lot map entries %lu KB; route jobs %ld (read set over %d lots: %ld); touched %d lots, untouched %d "
        "holding %llu KB = %ld.%ld%% of lot bytes; walk %ld us",
        when, g_luWalkN, n, all >> 10, part[0] >> 10, part[1] >> 10, part[2] >> 10, part[3] >> 10, g_luNoLot >> 10, k.luRuns,
        kStlKeys, k.luOver, touched, n - touched, un >> 10, pm / 10, pm % 10, walkUs);
    for (int t = 0; t < nTop; t++) {
        const LuLotB& b = g_luB[top[t]];
        const LuTouch* u = LuTouchOf(b.key);
        Log("lot use: #%-2d lot 0x%08lX%08lX %lu KB (footprints %lu, quadtree %lu, portals %lu, other %lu) jobs %ld agents %d%s", t + 1,
            (DWORD)(b.key >> 32), (DWORD)b.key, (b.b[0] + b.b[1] + b.b[2] + b.b[3]) >> 10, b.b[0] >> 10, b.b[1] >> 10, b.b[2] >> 10,
            b.b[3] >> 10, u ? u->runs : 0, u ? u->nAg : 0, u && u->nAg == 64 ? "+" : "");
    }
    if (locked) InterlockedExchange(&g_luLock, 0);
}
static void LuPoll() {  // replay consumer, between rounds
    if (g_luOn && g_luAsk && InterlockedExchange(&g_luAsk, 0)) LuWalk();
}
static void LuAsk() {  // [UserDiag] memory sample (its worker thread)
    if (!g_luOn) return;
    InterlockedExchange(&g_luAsk, 1);
    if (g_rpEvent) SetEvent(g_rpEvent);
}
static void LuExit() { if (g_luOn) LuReport("exit", 0); }

static void ApplyLu(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"RouteSnap", L"LotUseReadout", 0, ini) != 1) return;
    if (!g_stlFindTramp)
        g_stlFindTramp = InstallDetour("LotTable_Find (lot use read set)", STL_FIND, kStlFindPro, sizeof(kStlFindPro), (void*)StlFindStub, &g_stlFindTramp);
    if (!g_stlFindTramp) { Log("SKIP route snapshot LotUseReadout: LotTable_Find detour not installed"); return; }
    g_luOn = true;
    ts3native_counters.luOn = 1;
    Log("OK   route snapshot LotUseReadout=1 (docs/66 §7.1 A4 readout): clone lots read per route job, bytes per lot at each memory sample and at exit");
}
