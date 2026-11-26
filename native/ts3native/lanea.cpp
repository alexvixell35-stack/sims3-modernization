// docs/321 §10, docs/332 CP-3 / CP-4 (native part): lane A consumers of the CP-1 lot hub (lotstream.cpp). Included by
// ts3native.cpp after hostcalls.cpp (it reaches autshadow.cpp, snap.cpp, replandedupe.cpp, earlyout.cpp and hostcalls.cpp
// statics). [LaneA], one key per consumer, every key default 0: off = no subscriber, every table as today.
//
// Each consumer only clears rows or drops the rows of an unloaded lot; no answer changes, so household Sims see what they
// see today (docs/332 H column). Every table here is a cache that refills on use: a dropped row costs one recompute.
//   AsSimIds  (M1)  autshadow simIds, Sim ids: cleared on world load / quit (re-added at the next AddRow; unknown = plain typed mark, exact)
//   AsTier    (M3)  autshadow g_asTierTab, Sim tiers: cleared on world change (missing = tier 1, refreshed every cut, exact)
//   AsLotTier (M4)  autshadow g_asLotTier, lot tiers: cleared on world change, the lot's row dropped on lot remove (missing = tier 1)
//   AsLite    (L1)  autshadow g_asLite, lot print baselines: cleared on world change, the lot's row dropped on lot remove (missing =
//                   no baseline: the lot is marked whenever prints run, as at an epoch)
//   SpDoor    (M9)  snap g_spDoorLots, door mirror lots: emptied on world change, the lot's slot emptied on lot remove; an emptied
//                   slot keeps its heap buffers and is reused by the next lot (SpDoorLotFor), so slots never pass the most lots one
//                   world held. An empty lot is invalid: ts3fixes re-pushes it before use (SDM_Q), as after SDM_INV_HI.
//   DoorHold  (M11) hostcalls g_dmxHold, DoorGen held lots: cleared on world quit with a generation bump (DoorGen.cs re-sends every
//                   held lot at its next arm). Not on lot remove: a hold is the script lot's handler list, which outlives streaming.
//   ReplanDedupe (M8) replandedupe g_rdTab: emptied on world change (rows are 1 s old at most; nothing of the old world is served)
//   EarlyOut  (O12) earlyout labels: rebuilt when lhLotGen moved (earlyout.cpp), freed on world quit
//   GhostSweepLot (L3, docs/321 §11.1) and ReplanDedupeLot (M8 per lot, §11.2): below LaHub's table consumers / replandedupe.cpp.
//
// Thread rules (docs/321 §8.2): the hub calls LaHub on the sim thread. Autshadow tables are changed only at the next cut
// (LaAsApply, called from the cut in autshadow.cpp, the place that already owns them); the others change in the callback under
// their own lock (g_rdLock, g_eoCs) or on the sim thread that already owns them (snap door lots, DoorGen holds).
//
// No leak (Rodney 2026-11-23): every table is fixed-size or reuses its slots; rows hold ids (lot / Sim ids), never a pointer the
// game frees. Export `ts3native_lanea` (docs/registry.md): per table rows now, high water, world clears, lot drops; a
// "lane a:" line at each world event and each autshadow apply. After a world reset every reset table reads 0 ("reset ok").

enum { LA_ASSIMIDS, LA_ASTIER, LA_ASLOTTIER, LA_ASLITE, LA_SPDOOR, LA_DOORHOLD, LA_RD, LA_EO, LA_GS, LA_RDLOT, LA_N };
static const char* const kLaName[LA_N] = {"asSimIds", "asTier", "asLotTier", "asLite", "spDoor", "doorHold", "rd", "eo", "gsLot", "rdLot"};
static const wchar_t* const kLaKey[LA_N] = {L"AsSimIds", L"AsTier", L"AsLotTier", L"AsLite", L"SpDoor", L"DoorHold", L"ReplanDedupe", L"EarlyOut",
                                            L"GhostSweepLot", L"ReplanDedupeLot"};
struct LaRow { volatile LONG on, cur, hw, clears, drops; };
// v2 (docs/321 §11): GhostSweepLot detail (row gsLot: cur / hw = lots queued + the running job, clears = world clears of the
// queue, drops = lot jobs done), ReplanDedupeLot refusals and the shared game-state gate (lotstream.cpp SimLiveNow)
struct Ts3nGsLot {
    volatile LONG mode, verify, nodesPerTick, queueOver, jobs, jobsDone, slices, nodes, ghosts, removed, kept3C, keptRecheck,
        lotUnknown, skipNotLive, skipBusy, restarts, gaveUp, verifyJobs, verifyLeft, verifyOther, maxSliceUs, workNotLive;
    volatile LONG rdLotDrops, rdLotNotLive, statePushes, state;  // copies, refreshed at each GsTick / world event
    // v3: M8 design A vs B data (replandedupe.cpp; rdLotDrops above = m8WouldHit)
    volatile LONG m8Hits, m8Misses, m8LotDrops, m8RowsDropped, m8WouldHit;
};
struct Ts3nLaneA { LONG version, n; LaRow r[LA_N]; Ts3nGsLot gs; };
extern "C" __declspec(dllexport) Ts3nLaneA ts3native_lanea = {3, LA_N, {}, {}};

static void LaSee(int t, LONG cur) {
    LaRow& r = ts3native_lanea.r[t];
    r.cur = cur;
    if (cur > r.hw) r.hw = cur;
}

// pending autshadow work (sim thread writes, the cut applies; both sim thread)
static volatile LONG g_laAsWorld;     // a world event since the last cut
static u64 g_laAsGone[64];            // lots removed since the last cut
static int g_laAsGoneN;
static bool g_laAsGoneOver;           // more than 64: the lot-keyed tables are cleared instead

// open-addressing table (linear probe, same hash as autshadow's slot finders) rebuilt without `lot`; n <= 1024, rare (one
// lot remove). Returns the rows left; *dropped += rows that were the lot's.
template <class T> static LONG LaRehashDrop(T* tab, int n, u64 lot, LONG* dropped) {
    static T tmp[1024];
    memcpy(tmp, tab, sizeof(T) * n);
    memset(tab, 0, sizeof(T) * n);
    LONG live = 0;
    for (int i = 0; i < n; i++) {
        const u64 k = tmp[i].lot;
        if (!k) continue;
        if (k == lot) { (*dropped)++; continue; }
        for (DWORD h = (DWORD)(k ^ k >> 29) * 0x9E3779B1u, j = 0; j < (DWORD)n; j++) {
            T& s = tab[(h + j) & (n - 1)];
            if (!s.lot) { s = tmp[i]; live++; break; }
        }
    }
    return live;
}
static LONG LaLiteCount() {
    LONG n = 0;
    for (int i = 0; i < 1024; i++) n += g_asLite[i].lot != 0;
    return n;
}

// autshadow.cpp's cut (sim thread, first thing after the learn ring): world events and lot removes since the last cut
static void LaAsApply() {
    LaRow* r = ts3native_lanea.r;
    const bool world = g_laAsWorld != 0, over = g_laAsGoneOver;
    if (!world && !g_laAsGoneN && !over) return;
    InterlockedExchange(&g_laAsWorld, 0);
    if (!r[LA_ASSIMIDS].on && !r[LA_ASTIER].on && !r[LA_ASLOTTIER].on && !r[LA_ASLITE].on) return;
    LONG before[4] = {g_as ? g_as->simIds.n : 0, g_asTierN, g_asLotTierN, LaLiteCount()};
    while (InterlockedExchange(&g_asDirtyLock, 1)) YieldProcessor();  // as the epoch path takes it for g_asLite
    if (world) {
        if (r[LA_ASSIMIDS].on && g_as) AsSetClear(g_as->simIds), r[LA_ASSIMIDS].clears++;
        if (r[LA_ASTIER].on) memset(g_asTierTab, 0, sizeof(g_asTierTab)), g_asTierN = 0, r[LA_ASTIER].clears++;
    }
    if (world || over) {
        if (r[LA_ASLOTTIER].on) memset(g_asLotTier, 0, sizeof(g_asLotTier)), g_asLotTierN = 0, r[LA_ASLOTTIER].clears++;
        if (r[LA_ASLITE].on) memset(g_asLite, 0, sizeof(g_asLite)), r[LA_ASLITE].clears++;
    } else {
        for (int i = 0; i < g_laAsGoneN; i++) {
            if (r[LA_ASLOTTIER].on) g_asLotTierN = LaRehashDrop(g_asLotTier, 512, g_laAsGone[i], (LONG*)&r[LA_ASLOTTIER].drops);
            if (r[LA_ASLITE].on) LaRehashDrop(g_asLite, 1024, g_laAsGone[i], (LONG*)&r[LA_ASLITE].drops);
        }
    }
    InterlockedExchange(&g_asDirtyLock, 0);
    g_laAsGoneN = 0, g_laAsGoneOver = false;
    const LONG after[4] = {g_as ? g_as->simIds.n : 0, g_asTierN, g_asLotTierN, LaLiteCount()};
    for (int t = 0; t < 4; t++) if (r[t].on) LaSee(t, after[t]);
    if (world)  // reset tables read 0 now: the no-leak read-back (a row of a switch that is off stays as today)
        Log("lane a: autshadow reset at cut, simIds %ld->%ld, tier %ld->%ld, lotTier %ld->%ld, lite %ld->%ld, %s", before[0], after[0],
            before[1], after[1], before[2], after[2], before[3], after[3],
            (!r[LA_ASSIMIDS].on || !after[0]) && (!r[LA_ASTIER].on || !after[1]) && (!r[LA_ASLOTTIER].on || !after[2]) &&
                    (!r[LA_ASLITE].on || !after[3]) ? "reset ok" : "FAIL rows left");
}

static LONG LaSpDoorLive() {
    LONG n = 0;
    for (int i = 0; i < g_spDoorN; i++) n += g_spDoorLots[i].key != 0;
    return n;
}
static void LaSpDoorEmpty(SpDoorLot& l) {  // buffers stay with the slot (reused by SpDoorLotFor); invalid until pushed again
    if (l.valid) ts3native_counters.spDoor[3]++;
    l.key = 0, l.valid = 0, l.n = 0, l.nIds = 0;
    if (g_spDm.open == &l) g_spDm.open = NULL, g_spDm.haveDoor = false;  // a push in flight for that lot errs (SpDoorErr), never lands elsewhere
}
static LONG LaDoorHeld() {
    LONG n = 0;
    for (int i = 0; i < 32; i++) n += g_dmxHold[i] != 0;
    return n;
}
static LONG LaRdLive() {
    LONG n = 0;
    for (int i = 0; i < RD_N; i++) n += g_rdTab[i].ms != 0;
    return n;
}

// ---- L3 GhostSweepLot (docs/321 §11.1): GhostSweep for one unloaded lot, sliced over sim ticks ----
// The load-screen pass (discourage.cpp FpgCensus) walks P's world-wide footprint map, takes the ids of FpgEntry's ghost
// signature and removes each through LotRouting_RemoveObjectFootprints 0x635700 with lot 0. With lot 0 that fn removes each
// map node of the id from the node's quadtree Q (FME +0xC), except a node whose Q is listed for the id in P's second map
// [P+0x3C] [verified disassembly 0x635700..0x635883]; with a lot it checks the lot (0x628A90), takes the lot's record
// (LotTable_GetOrInsert 0x62EAB0 on a hit: record = node + 0x10) and removes the id from each Q of the record's vector
// +0x74..+0x78 [verified 0x635885..0x6358F0, 0x628A90, 0x62EAB0]. So "the full pass, for lot L" = the ghost ids with a node
// whose Q is one of L's Qs, removed from L's Qs. This pass does exactly that: the same walk (FpgEntry), the same rechecks (no
// object, not parked), the lot form of the same game call (and of DiscPfSweep's), and an id with any [P+0x3C] row is left to
// the load-screen pass (the lot form has no such skip; counted kept3C). Q-less nodes hold no cells and no lot: left to it too.
// Slices: end of a Simulate iteration (sim thread), only in live mode (SimLiveNow), with no queued discourage record (the full
// pass drains first), no job running (g_rgOffSimOpen raised first, Dekker order) and +0x918 taken with a try (deadline 0):
// anything missing = this tick is skipped, never a wait. A slice walks whole buckets up to NodesPerTick nodes and removes what
// it found before it lets the lock go: no id is carried across ticks. A rehash between slices restarts the walk (8 restarts =
// given up, logged and counted; the load-screen pass still covers it). Queue: 64 lot ids, more = one whole-map pass (lot 0, the
// load-screen pass itself, sliced). World load / quit clears the queue (the load-screen pass runs at load).
// Verify (GhostSweepLotVerify=1): per removal, the id's nodes on L's Qs after = 0 (verifyLeft) and its other nodes unchanged
// (verifyOther); after each lot job a dry pass of the same lot must find only the kept3C ids again (verifyLeft). Gated 0.
typedef u8(__fastcall* GsLotHasFn)(void* p, void* edx, DWORD lotLo, DWORD lotHi);  // 0x628A90 thiscall(P; lot), ret 8
typedef u8*(__fastcall* GsLotRecFn)(void* table, void* edx, const DWORD* id);       // 0x62EAB0 thiscall(P+0x48; &lot), ret 4
static GsLotHasFn g_gsLotHas = (GsLotHasFn)(uintptr_t)TS3A(0x00628A90);
static GsLotRecFn g_gsLotRec = (GsLotRecFn)(uintptr_t)TS3A(0x0062EAB0);
static const int kGsQ = 64, kGsQs = 32, kGsIds = 256, kGsTakeIds = 192;
static u64 g_gsQ[kGsQ];
static int g_gsQN;
static bool g_gsTickAdded;
static LONG g_laGsLines;
struct GsJob {
    bool active, dry, all;
    u64 lot;
    DWORD pos, count, restarts;
    const void* bk;
    LONG kept3C, found;
};
static GsJob g_gsJob;

static void LaGsSync() {  // copies for the export (replandedupe.cpp and lotstream.cpp are included before this file)
    Ts3nGsLot& g = ts3native_lanea.gs;
    g.rdLotDrops = g_laRdLotDrops, g.rdLotNotLive = g_laRdLotNotLive, g.statePushes = g_simStatePushes, g.state = g_simState;
    g.m8Hits = g_m8Hits, g.m8Misses = g_m8Misses, g.m8WouldHit = g_laRdLotDrops;
}

// M8 design A at a lot add / remove (sim thread): every failed row still inside its 500 ms serve window is refused from now on.
// Counted for the A vs B decision: m8LotDrops = events that refused >= 1 such row, m8RowsDropped = those rows.
static void M8LotEvent(LONG gen) {  // gen = lhLotGen after this event; rows from before an earlier event were counted then
    const ULONGLONG now = GetTickCount64();
    LONG rows = 0;
    AcquireSRWLockShared(&g_rdLock);
    for (int i = 0; i < RD_N; i++) rows += g_rdTab[i].ms && g_rdTab[i].fail && now - g_rdTab[i].ms <= kRdServeMs && g_rdTab[i].lotGen == gen - 1;
    ReleaseSRWLockShared(&g_rdLock);
    if (rows) ts3native_lanea.gs.m8LotDrops++, ts3native_lanea.gs.m8RowsDropped += rows;
}
// the decision lines (world events and LaneAGate "m8"): B-gain = m8WouldHit / (m8Hits + m8Misses)
static void M8Log(const char* why) {
    Ts3nGsLot& g = ts3native_lanea.gs;
    LaGsSync();
    const LONG look = g.m8Hits + g.m8Misses;
    Log("m8: %s, m8Hits %ld, m8Misses %ld, m8LotDrops %ld, m8RowsDropped %ld, m8WouldHit %ld", why, g.m8Hits, g.m8Misses, g.m8LotDrops,
        g.m8RowsDropped, g.m8WouldHit);
    Log("m8 B-gain: %.3f%% of lookups (%ld of %ld)", look ? 100.0 * g.m8WouldHit / look : 0.0, g.m8WouldHit, look);
}
static void GsSee() { LaSee(LA_GS, g_gsQN + (g_gsJob.active ? 1 : 0)); }
static void GsQueue(u64 id) {  // sim thread (hub callback): bookkeeping only, in any game state
    for (int i = 0; i < g_gsQN; i++) if (g_gsQ[i] == id) return;
    if (g_gsJob.active && !g_gsJob.all && !g_gsJob.dry && g_gsJob.lot == id && !g_gsJob.pos) return;
    if (g_gsQN < kGsQ) g_gsQ[g_gsQN++] = id; else ts3native_lanea.gs.queueOver = 1;
    GsSee();
}
static void GsWorld() {  // world load / quit: the load-screen pass covers the next world
    g_gsQN = 0, ts3native_lanea.gs.queueOver = 0, g_gsJob = GsJob();
    ts3native_lanea.r[LA_GS].clears++;
    GsSee();
}

// nodes of the id in map m (the [P+0x24] / [P+0x3C] layout: buckets +4, count +8, key +0, FME +8, next +0x10), split by
// "the node's Q (FME +0xC; the +8 value itself for [P+0x3C], as 0x635794 compares it) is one of qs"
static void GsIdNodes(const u8* m, bool fmeQ, DWORD lo, DWORD hi, const u8* const* qs, int nq, LONG* in, LONG* out) {
    *in = *out = 0;
    const u8* const* bk = *(const u8* const* const*)(m + 4);
    const DWORD count = *(const DWORD*)(m + 8);
    if (!bk || !count) return;
    for (const u8* nd = bk[lo % count]; nd; nd = *(const u8* const*)(nd + 0x10)) {
        if (*(const DWORD*)nd != lo || *(const DWORD*)(nd + 4) != hi) continue;
        const u8* v = *(const u8* const*)(nd + 8);
        const u8* q = fmeQ ? (v ? *(const u8* const*)(v + 0xC) : NULL) : v;
        bool mine = false;
        for (int i = 0; i < nq && !mine; i++) mine = q && q == qs[i];
        (mine ? *in : *out)++;
    }
}
static bool GsHas3C(const u8* p, DWORD lo, DWORD hi) {
    const u8* m3 = *(const u8* const*)(p + 0x3C);
    LONG in = 0, out = 0;
    if (m3) GsIdNodes(m3, false, lo, hi, NULL, 0, &in, &out);
    return out > 0;
}

// FpgRemove's rechecks, then the lot form of the same game calls; caller holds +0x918 with no job running
static bool GsRemoveLot(u8* world, u64 lot, DWORD lo, DWORD hi, const u8* const* qs, int nq) {
    Ts3nGsLot& g = ts3native_lanea.gs;
    u8* p = *(u8**)(world + 0x95C);
    if (!p || !FpgNoObject(lo, hi) || DiscKeyMapHas(world, lo, hi)) { g.keptRecheck++; return false; }
    if (GsHas3C(p, lo, hi)) { g.kept3C++, g_gsJob.kept3C++; return false; }
    const u8* m = *(const u8* const*)(p + 0x24);
    LONG in0 = 0, out0 = 0;
    if (g.verify) GsIdNodes(m, true, lo, hi, qs, nq, &in0, &out0);
    {
        UafTagScope uafTag(UT_GHOST_SWEEP, _ReturnAddress());  // [UafCheck]
        g_lotRemove(p, NULL, (DWORD)lot, (DWORD)(lot >> 32), lo, hi);
        world[0x994] = 0;  // as Remove does after 0x635700
        const unsigned __int64 key = g_pfKey(2, 0x19, lo, hi);  // DiscPfSweep, this lot only
        g_lotRemove(p, NULL, (DWORD)lot, (DWORD)(lot >> 32), (DWORD)key, (DWORD)(key >> 32));
    }
    if (g.verify) {
        LONG in1 = 0, out1 = 0;
        GsIdNodes(*(const u8* const*)(p + 0x24), true, lo, hi, qs, nq, &in1, &out1);
        if (in1) g.verifyLeft++;
        if (out1 != out0) g.verifyOther++;
    }
    g.removed++;
    return true;
}

static void GsEnd(const char* why) {
    Ts3nGsLot& g = ts3native_lanea.gs;
    GsJob& j = g_gsJob;
    const bool done = !strcmp(why, "done");
    if (j.dry) {
        if (!done || j.found != j.kept3C) g.verifyLeft++;
        Log("ghost sweep lot: verify %08lX%08lX %s, dry pass found %ld ghost ids on the lot, %ld kept for [P+0x3C], %s", (DWORD)(j.lot >> 32),
            (DWORD)j.lot, why, j.found, j.kept3C, done && j.found == j.kept3C ? "same" : "FAIL left");
        j = GsJob();
        return;
    }
    if (done) g.jobsDone++, ts3native_lanea.r[LA_GS].drops++;
    const LONG n = ++g_laGsLines;
    if (n <= 32 || !(n & (n - 1)) || !done)
        Log("ghost sweep lot: %s %08lX%08lX %s, mode %ld, ghost ids %ld, kept [P+0x3C] %ld; totals: jobs %ld done %ld, slices %ld, removed %ld, "
            "skipped not live %ld, busy %ld, max slice %ld us", j.all ? "all lots" : "lot", (DWORD)(j.lot >> 32), (DWORD)j.lot, why, g.mode,
            j.found, j.kept3C, g.jobs, g.jobsDone, g.slices, g.removed, g.skipNotLive, g.skipBusy, g.maxSliceUs);
    const bool verifyNext = g.verify && g.mode == 2 && !j.all && done;
    const u64 lot = j.lot;
    j = GsJob();
    if (verifyNext) j.active = j.dry = true, j.lot = lot, g.verifyJobs++;
}

// one slice; caller holds +0x918, no job running, live mode
static void GsSlice(u8* world) {
    Ts3nGsLot& g = ts3native_lanea.gs;
    GsJob& j = g_gsJob;
    u8* p = *(u8**)(world + 0x95C);
    const u8* m = p ? *(const u8* const*)(p + 0x24) : NULL;
    const u8* const* bk = m ? *(const u8* const* const*)(m + 4) : NULL;
    const DWORD count = m ? *(const DWORD*)(m + 8) : 0;
    if (!bk || !count) { g.gaveUp++; GsEnd("given up (no footprint map)"); return; }
    if (j.bk != bk || j.count != count) {
        if (j.pos && ++j.restarts > 8) { g.gaveUp++; GsEnd("given up (map rehashed 8 times)"); return; }
        if (j.pos) g.restarts++;
        j.pos = 0, j.bk = bk, j.count = count, j.found = 0, j.kept3C = 0;
    }
    const u8* qs[kGsQs];
    int nq = 0;
    if (!j.all) {
        const DWORD id[2] = {(DWORD)j.lot, (DWORD)(j.lot >> 32)};
        if (!g_gsLotHas(p, NULL, id[0], id[1])) { g.lotUnknown++; GsEnd("not in the routing lot table"); return; }
        const u8* rec = g_gsLotRec(p + 0x48, NULL, id);
        const u8* const* q = rec ? *(const u8* const* const*)(rec + 0x74) : NULL;
        const u8* const* qe = rec ? *(const u8* const* const*)(rec + 0x78) : NULL;
        for (; q && q < qe; q++) {
            if (nq == kGsQs) { g.gaveUp++; GsEnd("given up (more than 32 quadtrees)"); return; }
            if (*q) qs[nq++] = *q;
        }
    }
    struct { DWORD lo, hi; } ids[kGsIds];
    int n = 0;
    LONG nodes = 0;
    while (j.pos < count && nodes < g.nodesPerTick && n < kGsTakeIds) {
        for (const u8* nd = bk[j.pos]; nd && n < kGsIds; nd = *(const u8* const*)(nd + 0x10)) {
            nodes++;
            FpgId f = {};
            int v;
            __try { v = FpgEntry(nd, world, &f); } __except (EXCEPTION_EXECUTE_HANDLER) { v = -1; }
            if (v <= 0) continue;
            bool mine = j.all;
            for (int i = 0; i < nq && !mine; i++) mine = f.q == qs[i];
            bool dup = !mine;
            for (int i = 0; i < n && !dup; i++) dup = ids[i].lo == f.lo && ids[i].hi == f.hi;  // one bucket holds every node of an id
            if (!dup) ids[n].lo = f.lo, ids[n].hi = f.hi, n++;
        }
        j.pos++;
    }
    g.nodes += nodes;
    j.found += n;
    if (j.dry) {
        for (int i = 0; i < n; i++) if (GsHas3C(p, ids[i].lo, ids[i].hi)) j.kept3C++;  // what a full pass would still find here
    } else {
        g.ghosts += n;
        for (int i = 0; i < n && g.mode == 2; i++) {
            if (!j.all) GsRemoveLot(world, j.lot, ids[i].lo, ids[i].hi, qs, nq);
            else if (FpgRemove(world, ids[i].lo, ids[i].hi)) g.removed++;
            else g.keptRecheck++;
        }
    }
    if (j.pos >= count) GsEnd("done");
}

// end of every Simulate iteration (sim thread)
static void __cdecl GsTick() {
    Ts3nGsLot& g = ts3native_lanea.gs;
    Ts3nCounters& k = ts3native_counters;
    if (!g_gsJob.active && !g_gsQN && !g.queueOver) return;  // idle: three compares
    LaGsSync();
    if (!SimLiveNow()) { g.skipNotLive++; return; }  // paused, CAS, Build/Buy, Edit Town, travel, load: the queue waits
    u8* world = *g_fpgWorldSlot;
    const DWORD me = GetCurrentThreadId();
    if (!DiscWorldOk(world) || !g_rgLock || (void*)(world + 0x918) != g_rgLock || k.rtClosing || g_discCount ||
        g_async.state == A_RUNNING || (g_async.owner && me != g_async.owner) || k.rgOwnerTid == (LONG)me) {
        g.skipBusy++;
        return;
    }
    InterlockedIncrement(&g_rgOffSimOpen);  // no job starts during the slice (worker Dekker order)
    static const DWORD kTry = 0;
    if (g_async.state == A_RUNNING || g_fbAcq(g_rgLock, NULL, &kTry) <= 0) { RgOffSimDrop(); g.skipBusy++; return; }
    if (!g_gsJob.active) {
        g_gsJob = GsJob();
        g_gsJob.active = true;
        if (g.queueOver) g_gsJob.all = true, g_gsQN = 0, g.queueOver = 0;  // more lots than the queue holds: one whole-map pass
        else g_gsJob.lot = g_gsQ[0], memmove(g_gsQ, g_gsQ + 1, sizeof(u64) * --g_gsQN);
        g.jobs++;
    }
    if (!SimLiveNow()) g.workNotLive++;  // the gate's read-back: work started outside live mode (expected 0)
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    bool faulted = false;
    __try { GsSlice(world); } __except (EXCEPTION_EXECUTE_HANDLER) { faulted = true; }
    const LONG us = UsSince(t0);
    g_fbRel(g_rgLock, NULL);
    RgOffSimDrop();
    if (faulted) g.gaveUp++, g_gsJob = GsJob(), Log("ghost sweep lot: slice faulted, job dropped (the load-screen pass still covers it)");
    g.slices++;
    if (us > g.maxSliceUs) g.maxSliceUs = us;
    GsSee();
}

// op 49 sub 7 (ts3fixes Fixes.LaneAGate, reflect_call from the queue's repro only): window checks of the game-state gate.
// 0 mark (snapshot), 1 still = no sweep / invalidation work since the mark, 2 skipped = the gate turned work away since the mark,
// 3 drained = nothing queued or running, 4 ok = every verify counter 0, 5 requeue = one whole-map pass queued (the load-screen
// pass, sliced; needs GhostSweepLot on) so the window has work waiting.
static LONG g_laGateMark[8];
static u8 LaGateOp(DWORD what) {
    Ts3nGsLot& g = ts3native_lanea.gs;
    LaGsSync();
    const LONG now[8] = {g.slices, g.jobs, g.removed, g.ghosts, g.nodes, g_laRdLotDrops, g.skipNotLive, 0};
    switch (what) {
        case 0: memcpy(g_laGateMark, now, sizeof(now)); return 1;
        case 1: return !memcmp(g_laGateMark, now, sizeof(LONG) * 6) ? 1 : 0;
        case 2: return now[6] > g_laGateMark[6] ? 1 : 0;
        case 3: return !g_gsQN && !g_gsJob.active && !g.queueOver ? 1 : 0;
        case 4: return !g.verifyLeft && !g.verifyOther && !g.workNotLive && !g.gaveUp && !g_laRdLotNotLive ? 1 : 0;
        case 5: if (!g.mode) return 0; g.queueOver = 1; GsSee(); return 1;
        case 6: M8Log("report"); return 1;  // the M8 A vs B lines into ts3native.log
    }
    return 0;
}

static void LaHub(int kind, const u8* lot, LONG gen, void*) {
    LaRow* r = ts3native_lanea.r;
    if (g_laRdLot && (kind == LH_ADD || kind == LH_REMOVE)) M8LotEvent(gen);
    LaGsSync();
    if (kind == LH_REMOVE) {
        u64 id = 0;
        __try { id = *(const u64*)(lot + 0x48); } __except (EXCEPTION_EXECUTE_HANDLER) { id = 0; }  // the id [LotStream] logs (docs/51 I-593-1)
        if (!id) return;
        if (r[LA_GS].on) GsQueue(id);  // bookkeeping only: the sweep runs in live mode (GsTick)
        if (r[LA_ASLOTTIER].on || r[LA_ASLITE].on) {
            if (g_laAsGoneN < 64) g_laAsGone[g_laAsGoneN++] = id; else g_laAsGoneOver = true;
        }
        if (r[LA_SPDOOR].on) {
            for (int i = 0; i < g_spDoorN; i++)
                if (g_spDoorLots[i].key == id) { LaSpDoorEmpty(g_spDoorLots[i]); r[LA_SPDOOR].drops++; }
            LaSee(LA_SPDOOR, LaSpDoorLive());
        }
        return;
    }
    if (kind != LH_WORLD_LOAD && kind != LH_WORLD_QUIT) return;  // adds: nothing to drop (ts3fixes DoorGen re-sends per lot)
    InterlockedExchange(&g_laAsWorld, 1);
    if (g_laRdLot) M8Log(kind == LH_WORLD_LOAD ? "world load" : "world quit");
    if (r[LA_GS].on) GsWorld();
    const LONG doorBefore = LaSpDoorLive(), holdBefore = LaDoorHeld();
    LONG rdBefore = 0, eoBefore = 0;
    if (r[LA_SPDOOR].on) {
        for (int i = 0; i < g_spDoorN; i++) if (g_spDoorLots[i].key) LaSpDoorEmpty(g_spDoorLots[i]);
        r[LA_SPDOOR].clears++, LaSee(LA_SPDOOR, LaSpDoorLive());
    }
    if (r[LA_DOORHOLD].on && kind == LH_WORLD_QUIT) {
        for (int i = 0; i < 32; i++) g_dmxHold[i] = 0;
        InterlockedExchange(&g_dmxHoldAll, 0);
        InterlockedIncrement(&ts3native_doorgen.gen);  // memo rows kept before are another generation (dead)
        r[LA_DOORHOLD].clears++, LaSee(LA_DOORHOLD, LaDoorHeld());
    }
    if (r[LA_RD].on) {
        AcquireSRWLockExclusive(&g_rdLock);
        rdBefore = LaRdLive();
        memset(g_rdTab, 0, sizeof(g_rdTab));
        ReleaseSRWLockExclusive(&g_rdLock);
        r[LA_RD].clears++, LaSee(LA_RD, 0);
    }
    if (r[LA_EO].on && kind == LH_WORLD_QUIT) {  // g_eoCs exists: [LaneA] EarlyOut subscribes only with [RouteEarlyOut] on
        EnterCriticalSection(&g_eoCs);
        eoBefore = (LONG)g_eoCar.n;
        HeapFree(GetProcessHeap(), 0, g_eoCar.base);
        HeapFree(GetProcessHeap(), 0, g_eoCar.lab);
        g_eoCar = EoCar();  // rec NULL: the next ask rebuilds
        LeaveCriticalSection(&g_eoCs);
        r[LA_EO].clears++, LaSee(LA_EO, 0);
    }
    const bool ok = (!r[LA_SPDOOR].on || !LaSpDoorLive()) && (!r[LA_DOORHOLD].on || kind != LH_WORLD_QUIT || !LaDoorHeld());
    Log("lane a: world %s, spDoor %ld->%ld (slots %d), doorHold %ld->%ld, rd %ld->0, eo %ld->0, %s; autshadow at the next cut",
        kind == LH_WORLD_LOAD ? "load" : "quit", doorBefore, LaSpDoorLive(), g_spDoorN, holdBefore, LaDoorHeld(), rdBefore, eoBefore,
        ok ? "reset ok" : "FAIL rows left");
}

// [LaneA] (docs/321 §10): one key per consumer, all default 0. Loader thread, before the game runs.
static void ApplyLaneA(const wchar_t* ini) {
    const bool earlyOutOn = GetPrivateProfileIntW(L"RouteEarlyOut", L"Enabled", 0, ini) != 0;  // ReadEarlyOut made g_eoCs
    int any = 0;
    char on[160];
    int n = 0;
    on[0] = 0;
    for (int t = 0; t < LA_N; t++) {
        LONG v = GetPrivateProfileIntW(L"LaneA", kLaKey[t], 0, ini) != 0;
        if (t == LA_EO && v && !earlyOutOn) v = 0;  // no labels without [RouteEarlyOut] (and no g_eoCs)
        ts3native_lanea.r[t].on = v;
        any += v;
        if (v) n += _snprintf_s(on + n, sizeof(on) - n, _TRUNCATE, "%s%s", n ? " " : "", kLaName[t]);
    }
    g_laEo = ts3native_lanea.r[LA_EO].on;
    Ts3nGsLot& g = ts3native_lanea.gs;
    g.mode = ts3native_lanea.r[LA_GS].on ? Clamp(GetPrivateProfileIntW(L"LaneA", L"GhostSweepLot", 0, ini), 0, 2) : 0;  // 1 count only, 2 remove
    g.verify = g.mode && GetPrivateProfileIntW(L"LaneA", L"GhostSweepLotVerify", 0, ini) != 0;
    g.nodesPerTick = Clamp(GetPrivateProfileIntW(L"LaneA", L"GhostSweepLotNodes", 2048, ini), 64, 65536);
    if (!any) { Log("lane a consumers: off ([LaneA] all 0)"); return; }
    if (!ts3native_counters.lhReady) { Log("SKIP lane a consumers: lot hub not installed (lhReady 0), tables as today"); return; }
    if (!LotHubSubscribe(LaHub, NULL)) { Log("FAIL lane a consumers: lot hub subscriber table full"); return; }
    g_laRdLot = ts3native_lanea.r[LA_RDLOT].on;  // only with the hub: without lot events the rows keep today's rule
    if (g.mode) {
        if (g_gsTickAdded || SimLoopAdd(GsTick, "lane a: ghost sweep per lot")) {
            if (!g_gsTickAdded) InterlockedIncrement(&g_simStateWant);
            g_gsTickAdded = true;
            Log("OK   lane a ghost sweep per lot: mode %ld (1 count only, 2 remove), verify %ld, %ld nodes per tick; live mode only", g.mode,
                g.verify, g.nodesPerTick);
        } else {
            ts3native_lanea.r[LA_GS].on = 0, g.mode = 0;
            Log("SKIP lane a ghost sweep per lot: no Simulate iteration hook");
        }
    }
    Log("OK   lane a consumers: %s (docs/321 §10; rows cleared on world change, dropped on lot remove)", on);
}
