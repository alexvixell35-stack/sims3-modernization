// H6 S2b (docs/60 §2.3): count-only stubs on the docs/60 §2.2 closure sites, so the S3b closures are sized before they
// exist. Included by ts3native.cpp after routegate.cpp (shares its frame stack, leave thunk, job and thread tests).
// Measurement only: every stub counts and then runs the game's code; nothing waits, nothing is skipped.
//
// Per site (docs/60 §2.2 row): #1 the worker's quadtree readers 0x6583E0 / 0x83B3A0 push a reader frame (worker only), so
// a worker entry into the quadtree leaves 0x681820 / 0x681E00 outside a reader is a third consumer (vfRtQtBare, must be 0);
// the mutators 0x6823E0 / 0x682080 / 0x6819B0 count while a job runs. #4 ObjectService_DestroyById 0x939420: in a job, off
// the sim thread. #5 worker lot lookups 0xC688A0, worker ApplyPendingUpdates 0x82C420 with P+0x10E set, lot index
// rebuilds 0xC68680 during a job by thread. #6 save, BuildRoutingData and lot save entries during a job. #9 the routing
// debug frame during a job. #10 world init / teardown during a job. On the sim thread the destroy and mutator entries also
// close the G11 window (routegate.cpp). Mode 1 (sync): the sim thread waits in RunPlan, so its in-job counts must be 0 and
// the worker's give per-job frequencies (divide by workerCalls).
// Prologues verified on the Steam 1.67 exe (docs/60 §2.2, capstone re-read 2026-10-16): whole instructions, no relative
// operand (0x78AE40 has an absolute one, copied as is). Each site installs on its own; a differing prologue skips that site.
//
// H6 S3b closures ([RouteGate] Enforce=1, rgEnforce; docs/60 §2.2), active only while an async job runs (the worker holds
// +0x918 for the whole job, routegate.cpp): #1 g_qtCs around the worker's quadtree readers and the other threads'
// mutators; #4 a destroy waits for the job (the owner pumps its managed calls; inside such a callback it cannot wait and
// counts vfRtDestroyInInvoke); #6 save, BuildRoutingData and lot save wait for the job and refuse START while open (rtBusy);
// #9 the routing debug frame is skipped; #10 world init and teardown clear the learned lock (START waits for a plan of the
// new world), world init clears the closing flag. §2.5: the travel / quit transition ICs 0xEC1180 / 0xEC11B0 set rtClosing
// (START refused) and drain. A thread that really owns +0x918 never waits: the worker cannot be inside its job then.

enum { RT_QREAD, RT_QWRITE, RT_QLEAF, RT_DESTROY, RT_LOTGRID, RT_PENDING, RT_IDXREBUILD, RT_SAVE, RT_BRD, RT_LOTSAVE, RT_DEBUG,
       RT_TEARDOWN, RT_CLOSING, RT_STEER };
struct RtSite { DWORD addr; u8 len; u8 pro[10]; u8 kind; const char* name; };
static const int RT_N = 25;
static const RtSite kRtSites[RT_N] = {
    {0x006583E0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RT_QREAD, "Routing_PhysicsRayVertical"},
    {0x0083B3A0, 5, {0x83, 0xEC, 0x10, 0x53, 0x55}, RT_QREAD, "Routing_PhysicsRangeQuery"},
    {0x006823E0, 7, {0x53, 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x14}, RT_QWRITE, "Realm_AddShape"},
    {0x00682080, 6, {0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x0C}, RT_QWRITE, "Realm_RemoveShape"},
    {0x006819B0, 6, {0x56, 0x57, 0x8B, 0x7C, 0x24, 0x0C}, RT_QWRITE, "LooseQuadTree_Reinsert"},
    {0x00681820, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RT_QLEAF, "quadtree ray leaf"},
    {0x00681E00, 7, {0x8B, 0x41, 0x08, 0xD9, 0x44, 0x24, 0x08}, RT_QLEAF, "quadtree range leaf"},
    {0x00939420, 5, {0x83, 0xEC, 0x08, 0x53, 0x56}, RT_DESTROY, "ObjectService_DestroyById"},
    {0x00C688A0, 6, {0x83, 0xEC, 0x08, 0x56, 0x8B, 0xF1}, RT_LOTGRID, "WorldManager_LotGridLookup"},
    {0x0082C420, 9, {0x57, 0x8B, 0xF9, 0x8B, 0x87, 0x5C, 0x09, 0x00, 0x00}, RT_PENDING, "RoutingWorld_ApplyPendingUpdates"},
    {0x00C68680, 6, {0x83, 0xEC, 0x1C, 0x53, 0x55, 0x56}, RT_IDXREBUILD, "WorldManager_RebuildLotIndex"},
    {0x00AAC110, 6, {0x81, 0xEC, 0x30, 0x04, 0x00, 0x00}, RT_SAVE, "Save_WritePackage"},
    {0x0078AE40, 7, {0x56, 0x8B, 0x35, 0xC4, 0xCB, 0x1E, 0x01}, RT_BRD, "IC_World_BuildRoutingDataImpl"},  // absolute operand
    {0x008361B0, 6, {0x56, 0x57, 0x8B, 0x7C, 0x24, 0x0C}, RT_LOTSAVE, "RoutingSystem_SaveLot"},
    {0x00818EA0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RT_DEBUG, "RoutingDebug_FrameUpdate"},
    {0x00829410, 5, {0x53, 0x56, 0x8B, 0xF1, 0x57}, RT_TEARDOWN, "RoutingWorld_LockedOp_WorldInit"},
    {0x0082D140, 5, {0x83, 0xEC, 0x0C, 0x53, 0x55}, RT_TEARDOWN, "RoutingWorld_Clear"},
    {0x008299E0, 10, {0x53, 0x56, 0x8B, 0xF1, 0x8D, 0x9E, 0x18, 0x09, 0x00, 0x00}, RT_TEARDOWN, "RoutingWorld_DestroyPlanners"},
    // docs/60 §2.5 (0.11.0): call 0x7692B0 is re-targeted in the trampoline; mov eax,[0x11FF7AC] is absolute
    {0x00EC1180, 5, {0xE8, 0x2B, 0x81, 0x8A, 0xFF}, RT_CLOSING, "IC_GameUtils_TransitionToLoadWorldImpl"},
    {0x00EC11B0, 5, {0xA1, 0xAC, 0xF7, 0x1F, 0x01}, RT_CLOSING, "IC_GameUtils_TransitionToNotInWorldImpl"},
    // 0.11.1: worker quadtree consumers outside the two readers above (BP M1 run 2026-10-17: vfRtQtBare 2067, ray leaf
    // from 0x681E83). Realm ray wrapper 0x681E50 (from 0x789DD0 <- route code 0x759560, and from 0x686CC0 / 0x686FD0),
    // segment ray 0x686FD0 (from routing 0x82AEC0), range wrapper 0x682590 (from 0x825015, 0x82A96C, 0x82EC6D). Each reaches
    // no managed, remote, event or Lock_Acquire call on direct calls (depth 6). 0x789DD0 itself does (World_FireEvent via
    // 0xAC0570), so it holds no lock; 0x682610 (filtered range query) takes callbacks and stays on the leaf fallback.
    {0x00681E50, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RT_QREAD, "Realm_RayCast"},
    {0x00686FD0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RT_QREAD, "PhysicsWorld_RayCastSegment"},
    {0x00682590, 7, {0x83, 0xEC, 0x10, 0x8B, 0x54, 0x24, 0x1C}, RT_QREAD, "Realm_RangeQuery"},
    // 0.11.2 (vfG11CloseBy, docs/81/82): the two per-tick steering +0x918 takers; their caller keys the G11 close
    {0x0082EDB0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RT_STEER, "RoutingWorld_ObstacleQuery"},
    {0x0082F0B0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RT_STEER, "GoalFootprintGather"},
    // 0.11.4 probe P0 also framed RouteFollow_EditPath 0x85F5A0 (caller key: locomotion 0x85FEDC vs the plan's own tail
    // 0x85FFC7). Removed in 0.11.5: with that frame game time froze after a save in 2 of 2 runs, and not without it (bisect
    // 2026-10-17). P0 had answered: the tail, not locomotion. Do not frame 0x85F5A0 again before that freeze is understood.
};
static const LONG RT_ALL = (1 << RT_N) - 1;
static void* g_rtTramp[RT_N];
static volatile LONG g_rtVf[RT_N];  // Verify probe index + 1 counted from inside the stub, 0 = none

static inline int RtRole(DWORD tid) {  // 0 main, 1 sim, 2 worker, 3 other
    return tid == g_mainTid ? 0 : RgIsSim(tid) ? 1 : RgIsWorker(tid) ? 2 : 3;
}

// Waits until the async job is done unless this thread may not or need not: the worker, a real +0x918 owner (the worker is
// outside its job body), the owner inside a callback for the job (returns false: waiting would deadlock). The owner pumps.
// P5c (p5c.cpp): an owner of the lock the worker lent out is inside the job's window, so it waits (WaitAsyncDone hands it back)
static bool RtWaitJob(volatile LONG* us, DWORD key) {
    const DWORD tid = GetCurrentThreadId();
    if (g_async.state != A_RUNNING || !g_async.lock || RgIsWorker(tid) || (ts3native_counters.rgOwnerTid == (LONG)tid && !P5cLent(tid)))
        return true;
    if (tid == g_async.owner && g_inInvoke) return false;
    RcDrop();  // docs/177 §7: a lent lock kept by the refcount guard goes back before the wait
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const bool fb = g_jobFb != 0;  // docs/134 §11: the job waited for is a foreign one (A_RUNNING checked above)
    const bool own = g_jobOwn != 0;  // docs/178 T4: an own one
    const DWORD handle = g_async.handle;
    const DWORD mwTok = MwSeqNow();  // [MovedWaits]
    UdWait("the running route job");  // [UserDiag]: no-op unless this thread is saving
    if (tid == g_async.owner) WaitAsyncDone(SW_RTWAIT);
    else while (g_async.state == A_RUNNING) Sleep(1);
    UdWait(NULL);
    const LONG waited = UsSince(t0);
    InterlockedExchangeAdd(us, waited);
    if (tid == g_async.owner) WaitNote(key, waited, mwTok), HaNote(key, waited, HA_JOB), FbjcSimWait(waited, fb), OwSimWait(waited, own, handle), P6wWait(mwTok, P6W_RTWAIT, 0, waited);
    return true;
}

// #1: g_qtCs, timing only the waits that block
static void RtQtEnter() {
    if (TryEnterCriticalSection(&g_qtCs)) return;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    EnterCriticalSection(&g_qtCs);
    const LONG us = UsSince(t0);
    InterlockedExchangeAdd(&ts3native_counters.vfRtQtWaitUs, us);
    Max(&ts3native_counters.vfRtQtWaitMaxUs, us);
}

// M1 world state for a job on the lock's world: 0 = quiet, 1 = unreadable, 3 = pending lot updates (the worker would apply
// them), 4 = dirty lot index (the worker would rebuild it; shortcut: refused, not cleaned first)
static int RtWorldState(const void* lock) {
    const u8* world = lock ? (const u8*)lock - 0x918 : NULL;
    const u8* p = world && Readable(world + 0x95C, 12) ? *(const u8* const*)(world + 0x95C) : NULL;
    const u8* wm = p ? *(const u8* const*)(world + 0x964) : NULL;
    if (!wm || !Readable(p + 0x10E, 1) || !Readable(wm + 0x1CC, 1)) return 1;
    return p[0x10E] ? 3 : wm[0x1CC] ? 4 : 0;
}
static bool RtQuietWorld(const void* lock) { return RtWorldState(lock) == 0; }  // worker, before each queued job

// H6 M1 (docs/60 §2.4), on the sim thread from AsyncStart: true = this START may go async, *lock = the job's lock.
static bool RtM1Allows(void** lock, int* whyOut) {
    Ts3nCounters& k = ts3native_counters;
    int why = -1;
    const RgThread& t = t_rg;
    const int ws = RtWorldState(g_rgLock);
    if (!k.rgEnforce || ws == 1) why = 0;
    else if (k.rtBusy > 0) why = 1;
    else if (k.rtClosing) why = 2;
    else if (ws) why = ws;
    else if (t.n > t.readers || k.rgOwnerTid == (LONG)GetCurrentThreadId()) why = 5;
    if (why >= 0) {
        InterlockedIncrement(&k.asyncRefusedM1[why]);
        if (whyOut) *whyOut = why;
        return false;
    }
    *lock = g_rgLock;
    return true;
}

static void DiscDrain(bool force);  // discourage.cpp
static void DiscOnDestroy(DWORD idLo, DWORD idHi, bool canWait);
static DWORD CeDestroyObj(DWORD idLo, DWORD idHi);  // discourage.cpp: docs/186 §8 our-events ring
static bool P5cPark(DWORD idLo, DWORD idHi);  // p5c.cpp: a destroy in a FreeLock window parks one reference and runs
static void P5cFlush();                       // p5c.cpp: parked references released (no job running)
static bool g_p5cParkDx;                      // p5c.cpp P5cPark: the last park had queued discourage records (DestroyParkDx)

// 1 = skip the site's body and return to its caller (only the routing debug frame, a plain `ret` function)
static int __cdecl RtEnter(int i, DWORD* slot) {
    // TailUnlock: passes 16-19 reach the lot index (World_GetHeightAt -> 0xC688A0 -> 0xC68680, WM+0x228); nothing else here
    if (t_tuOpen && kRtSites[i].kind != RT_LOTGRID && kRtSites[i].kind != RT_IDXREBUILD && kRtSites[i].kind != RT_QREAD &&
        kRtSites[i].kind != RT_QLEAF)
        TuTrip(2);
    if (g_rtVf[i]) VfEnter(g_rtVf[i] - 1, slot);
    Ts3nCounters& k = ts3native_counters;
    if (!k.rtReady) return 0;
    const bool enforce = k.rgEnforce != 0, running = g_async.state == A_RUNNING;
    const RtSite& s = kRtSites[i];
    // P4 clone run (docs/94 §12.4.3 with §17.4 D1): pass through, except the #1 quadtree readers (g_qtCs) and the #5 lot
    // lookups / pending updates, which keep acting; they also note the plan's reader bits (D5)
    if (t_snapRun && s.kind != RT_QREAD && s.kind != RT_QLEAF && s.kind != RT_LOTGRID && s.kind != RT_PENDING) return 0;
    if (t_p4Read) t_p4Read |= s.kind == RT_QREAD || s.kind == RT_QLEAF ? P4R_PHYS : s.kind == RT_LOTGRID ? P4R_LOTIDX : 0;
    if (t_p4Read && s.addr == 0x006583E0) t_p4Read |= P4R_VRAY;  // D5 display level family (docs/94 §17.9 N15)
    const DWORD tid = GetCurrentThreadId();
    const bool worker = RgIsWorker(tid), job = RgJobLive();
    RgThread& t = t_rg;
    RgDrop(t, slot);
    const char* note = NULL;
    // D-X 0x939420 thiscall(svc; idLo, idHi): slot[1], slot[2]
    const bool parked = s.kind == RT_DESTROY && enforce && !worker && P5cPark(slot[1], slot[2]);
    if (g_p4On && !worker) {
        // D5: writer families counted for the shadow's window (physics, destroys, lot index rebuilds)
        if (s.kind == RT_QWRITE) InterlockedIncrement(&g_nrN[0]);
        else if (s.kind == RT_DESTROY) InterlockedIncrement(&g_nrN[2]);
        else if (s.kind == RT_IDXREBUILD) InterlockedIncrement(&g_nrN[3]);
        // D2: destroys, saves, BuildRoutingData, lot saves and the travel / quit drain wait for a running clone run
        // (a parked destroy runs beside the clone run: its object stays allocated, the nr count above refuses the answer)
        if ((s.kind == RT_DESTROY && !parked) || s.kind == RT_SAVE || s.kind == RT_BRD || s.kind == RT_LOTSAVE || s.kind == RT_CLOSING) {
            P5cNrEnter();
            if (!RgPush(t, slot, 1, RG_REL_NR)) InterlockedDecrement(&g_snNrIn);
        }
    }
    switch (s.kind) {
        case RT_QREAD:
            if (!worker) break;
            if (!t.readers) InterlockedIncrement(&k.vfRtQtReads);
            if (!enforce) { RgPush(t, slot, 1); break; }
            RtQtEnter();  // recursive: nested readers just count up
            if (!RgPush(t, slot, 1, RG_REL_QT)) LeaveCriticalSection(&g_qtCs);
            break;
        case RT_QLEAF:
            if (worker && !t.readers) {
                InterlockedIncrement(&k.vfRtQtBare);
                note = "quadtree read outside a reader";
                if (enforce) {  // fallback: the traversal at least runs under the lock (its consumer's later reads do not)
                    RtQtEnter();
                    if (!RgPush(t, slot, 1, RG_REL_QT)) LeaveCriticalSection(&g_qtCs);
                }
            }
            break;
        case RT_QWRITE:
            if (job) { InterlockedIncrement(&k.vfRtQtWriteInJob); if (worker) note = "quadtree write on the worker"; }
            G11Touch(G11_QTWRITE, 0x03000000 | i);
            // published first (no job starts while it is open), then the quadtree lock if a job already runs
            if (enforce && !worker) {
                InterlockedIncrement(&g_rgOffSimOpen);
                LONG rel = RG_REL_OFFSIM;
                // D3 (docs/94 §17.4): with P4 installed the mutators lock always (a clone run reads the tree after J_DONE)
                if (g_async.state == A_RUNNING || g_p4On) { RtQtEnter(); rel |= RG_REL_QT; }
                if (!RgPush(t, slot, 1, rel)) RgUndo(RgFrame{NULL, 0, 0, rel, NULL});
            }
            break;
        case RT_DESTROY: {
            if (job) InterlockedIncrement(&k.vfRtDestroyInJob);
            if (!RgIsSim(tid)) { InterlockedIncrement(&k.vfRtDestroyOffSim); note = "destroy off the sim thread"; }
            G11Touch(G11_DESTROY, 0x02000000);
            // docs/186 §8: our-events ring (the object looked up on the sim thread only, as P5cPark does)
            const DWORD ceObj = g_ceOn && RgIsSim(tid) ? CeDestroyObj(slot[1], slot[2]) : 0;
            CeRec(CE_DESTROY_IN, ceObj, slot[1], slot[2]);
            if (enforce && !worker) {  // published first (no job starts during the destroy), then wait for a running one
                InterlockedIncrement(&g_rgOffSimOpen);
                if (!RgPush(t, slot, 1, RG_REL_OFFSIM | (g_ceOn ? RG_REL_CE : 0), (void*)(uintptr_t)ceObj)) RgOffSimDrop();
                const LONG w0 = k.vfRtDestroyWaitUs;
                const bool waited = parked || RtWaitJob(&k.vfRtDestroyWaitUs, 0x02000000 | RT_DESTROY);
                CeRec(CE_DESTROY_RUN, ceObj, slot[1], (DWORD)(k.vfRtDestroyWaitUs - w0));
                if (!waited) { InterlockedIncrement(&k.vfRtDestroyInInvoke); note = "destroy inside a job callback"; }
                // D-X (docs/84 §7.2): thiscall(svc; idLo, idHi), ret 8 [verified 0x939420..0x939506]: slot[1], slot[2]
                if (!(parked && g_p5cParkDx)) DiscOnDestroy(slot[1], slot[2], waited);  // DestroyParkDx: records stay queued
            }
            break;
        }
        case RT_LOTGRID:
            if (worker) InterlockedIncrement(&k.vfRtLotGridWorker);
            break;
        case RT_PENDING: {  // thiscall, ecx = routing world (pushad slot -2); P = [world+0x95C], pending flag P+0x10E
            const u8* world = (const u8*)(uintptr_t)slot[-2];
            const u8* p = worker && world ? *(const u8* const*)(world + 0x95C) : NULL;
            if (p && p[0x10E]) { InterlockedIncrement(&k.vfRtPendingInJob); note = "pending lot updates applied by the worker"; }
            break;
        }
        case RT_IDXREBUILD:
            if (job) { InterlockedIncrement(&k.vfRtLotIdxRebuildInJob[RtRole(tid)]); note = "lot index rebuild during a job"; }
            break;
        case RT_SAVE:
            SaveGateEnter(tid);  // savewrite.cpp: write-phase start when Apex owns the call 0xAB12AF (docs/148 §7a)
            if (job) { InterlockedIncrement(&k.vfRtDrainSave); note = "save during a job"; }
            break;
        case RT_BRD: if (job) { InterlockedIncrement(&k.vfRtDrainBrd); note = "BuildRoutingData during a job"; } break;
        case RT_LOTSAVE: if (job) { InterlockedIncrement(&k.vfRtDrainLotSave); note = "lot save during a job"; } break;
        case RT_DEBUG:
            if (job) InterlockedIncrement(&k.vfRtDebugSkip);
            if (enforce && running) return 1;
            break;
        case RT_TEARDOWN:
            if (job) { InterlockedIncrement(&k.vfRtTeardownInJob); note = "world init/teardown during a job"; }
            if (enforce && !job) {  // a new or vanishing world: no async START before a plan has learned its lock
                g_rgLock = NULL;
                if (s.addr == 0x00829410) InterlockedExchange(&k.rtClosing, 0);
            }
            break;
        case RT_STEER:  // outermost only: a nested call keeps the outer caller as the key
            if (!t_steerRet) {
                const DWORD prev = t_steerRet;
                t_steerRet = t_avSite ? 0x00846D21 : t_stubKey ? t_stubKey : VfRealRet(slot);  // a stub's own return would key it (0x6F28F468)
                if (!RgPush(t, slot, 1, RG_REL_STEER, (void*)(uintptr_t)prev)) t_steerRet = prev;
            }
            break;
        case RT_CLOSING:
            if (!enforce) break;
            DiscDrain(true);  // deferred discourage footprints belong to this world: apply them before it goes
            InterlockedExchange(&k.rtClosing, 1);
            InterlockedIncrement(&k.vfRtClosingDrains);
            RtWaitJob(&k.vfRtDrainWaitUs, 0x02000000 | RT_CLOSING);
            break;
    }
    // #6: refuse START and hold queued jobs until the call returns, then wait for a running job
    if (enforce && !worker && (s.kind == RT_SAVE || s.kind == RT_BRD || s.kind == RT_LOTSAVE)) {
        if (s.kind == RT_SAVE) DiscDrain(true);  // the save must see every deferred discourage footprint
        InterlockedIncrement(&k.rtBusy);
        InterlockedIncrement(&g_rgOffSimOpen);
        if (!RgPush(t, slot, 1, RG_REL_BUSY | RG_REL_OFFSIM)) { InterlockedDecrement(&k.rtBusy); RgOffSimDrop(); }
        RtWaitJob(&k.vfRtDrainWaitUs, 0x02000000 | s.kind);
    }
    // P5c: parked references go at these entries when no job runs (a save, travel / quit, world init / teardown). A destroy
    // entry releases them only while the world closes (DestroyAll, shutdown): DestroyById has native callers that sit
    // mid-update (locomotion 0x84CE30 and its follow family); a parked object's destructor run there can free what that
    // caller still holds (crash I, docs/67 §11, 0.36.19: inferred cause; p5c.cpp's released ring tells it apart)
    if (!worker && ((s.kind == RT_DESTROY && k.rtClosing) || s.kind == RT_SAVE || s.kind == RT_CLOSING || s.kind == RT_TEARDOWN))
        P5cFlush();
    if (note) RgNoteAs(32 + i, s.name, s.addr, note, VfRealRet(slot), (LONG)tid);
    return 0;
}

// RtEnter's eax: 0 = the site's body (trampoline), 1 = straight back to the caller (the site takes no stack arguments)
#define RT_STUB(i) static __declspec(naked) void RtStub##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call RtEnter __asm add esp, 8 __asm test eax, eax __asm jnz skip##i \
    __asm popfd __asm popad __asm jmp dword ptr [g_rtTramp + i * 4] __asm skip##i: __asm popfd __asm popad __asm ret }
RT_STUB(0) RT_STUB(1) RT_STUB(2) RT_STUB(3) RT_STUB(4) RT_STUB(5) RT_STUB(6) RT_STUB(7) RT_STUB(8) RT_STUB(9) RT_STUB(10)
RT_STUB(11) RT_STUB(12) RT_STUB(13) RT_STUB(14) RT_STUB(15) RT_STUB(16) RT_STUB(17) RT_STUB(18) RT_STUB(19) RT_STUB(20)
RT_STUB(21) RT_STUB(22) RT_STUB(23) RT_STUB(24)
static void (*const kRtStubs[RT_N])() = {RtStub0, RtStub1, RtStub2, RtStub3, RtStub4, RtStub5, RtStub6, RtStub7, RtStub8, RtStub9,
                                         RtStub10, RtStub11, RtStub12, RtStub13, RtStub14, RtStub15, RtStub16, RtStub17,
                                         RtStub18, RtStub19, RtStub20, RtStub21, RtStub22, RtStub23, RtStub24};

// [RouteGate] Sites=1 (needs the gate installed: job and thread tests, G11): one detour per docs/60 §2.2 site.
static void InstallRtSites(const wchar_t* ini, bool gate) {
    if (!GetPrivateProfileIntW(L"RouteGate", L"Sites", 0, ini)) { Log("route gate sites off (Sites=0)"); return; }
    if (!gate) { Log("SKIP route gate sites: the gate itself is not installed"); return; }
    LONG ready = 0;
    int n = 0;
    for (int i = 0; i < RT_N; i++) {
        char name[64];
        sprintf_s(name, "%s (route gate site)", kRtSites[i].name);
        // InstallDetour re-targets a copied leading call rel32 before the jmp goes live
        if ((g_rtTramp[i] = InstallDetour(name, kRtSites[i].addr, kRtSites[i].pro, kRtSites[i].len, (void*)kRtStubs[i], &g_rtTramp[i])) != NULL) {
            ready |= 1 << i;
            n++;
        }
    }
    ts3native_counters.rtReady = ready;
    Log("%s route gate sites: %d of %d docs/60 2.2/2.5 sites, ready 0x%lX", n == RT_N ? "OK  " : "PART", n, RT_N, ready);
    // S3b: closures on only with every site and the async path in place; otherwise count only and async START stays refused
    if (!GetPrivateProfileIntW(L"RouteGate", L"Enforce", 0, ini)) { Log("route gate closures off (Enforce=0)"); return; }
    if (ready != RT_ALL || !ts3native_counters.asyncReady) {
        Log("FAIL route gate closures: sites 0x%lX of 0x%lX, async ready %ld; async START stays refused", ready, RT_ALL,
            ts3native_counters.asyncReady);
        return;
    }
    InitializeCriticalSection(&g_qtCs);
    ts3native_counters.rgEnforce = 1;
    Log("OK   route gate closures on (Enforce=1): async jobs hold +0x918, gate entries wait for them, M1 START limits");
}
