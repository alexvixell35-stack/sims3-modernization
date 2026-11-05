// H6 design D (docs/84 §2.4): discourage footprints deferred on the sim thread while an async route job runs.
// Included by ts3native.cpp after rtsites.cpp. Installed with [RouteGate] Enforce=1 and [Discourage] Enabled=1 (default 1);
// runtime switch discMode (counters v30): 0 = the game's calls as they are, 1 = defer.
//
// RoutingWorld_AddDiscourageFootprint 0x834370 (thiscall world: obj, points, highImpassable, allowPlacementInWorld,
// placementFootprint; ret 0x14) and RoutingWorld_RemoveDiscourageFootprint 0x834860 (thiscall world: obj; ret 4) take
// +0x918, so while a job runs the sim thread waits for it (P0: 28.8% of the sim's waits). Script ignores their result
// except two debug interactions. While a job runs on the owner thread, a call is recorded (object id, copied points,
// flags) after the original's lock-free early outs and answers 1. Records are applied in order on the owner thread,
// holding +0x918 with no job running (the original's own preconditions), at script-IC points only (Add can fire
// World_FireEvent 0x11): the next D1/D2 call with no job running, GetOptionHook (START / READY / CANCEL), save and
// travel / quit drains. A record made in another world is dropped. Records added during a drain (an event handler) go to
// the tail.
//
// Fix D-X (0.16.0, counters v34, docs/84 §7.2): destroy does not remove routing footprints, so dropping a Remove record
// whose object is gone left the footprint behind under a dead id (PetGuardSitutation: Remove, then Destroy, in one call).
//   1. Destroy gate (switch discDestroyMode, [Discourage] DestroyApply): ObjectService_DestroyById 0x939420 (rtsites.cpp
//      RT_DESTROY, after its job wait, owner thread) applies that object's queued Remove records in order under +0x918 with
//      the original 0x834860 while the object is still in the id hash, drops its Add records, keeps every other record in
//      order. Inside a job callback (no wait possible) it only counts (discDestroyInInvoke).
//   2. Dead-id sweep (switch discSweepMode, [Discourage] DeadSweep): a Remove record whose object is gone at apply calls
//      LotRouting_RemoveObjectFootprints 0x635700 for its id on all lots (lot 0), as Remove does on a lock-key-map miss,
//      then clears world+0x994. A dead id still in the lock-key map is only counted.
//   3. Pointer check: the record keeps the object pointer (no reference, never dereferenced); FindById returning another
//      object means the id was reused: dropped and counted (discIdMismatch), not swept (the sweep would strip the new
//      object's footprints).
//   Tripwire T-D5: discDropRemove = discDeadSweep while discSweepMode is on (DestroyParkDx=1: discDeadSweep = discDropRemove + usfGoneEnable0; the pf branch bumps pfSwept only, docs/88 §11).
//
// Design U (docs/89 §5.1, §10; counters v74, [Discourage] DeferFootprint=1, default 0; runtime switch usfMode, --ab
// deferfootprint): RoutingWorld_UpdateSimFootprint 0x833560 thiscall(world; obj, enable, force, arg), ret 0x10, void, is a
// remove-all plus optional re-insert of the object's routing footprints under +0x918 (key 0x833593), managed-free. On the
// sim thread while a job runs, the call's lock-free early out 0x82A720 runs now and the rest becomes a record in D's FIFO
// (kind usf: high = enable byte, allow = arg), applied by the original with force = 1 at D's drain points, D-X destroy
// included. A call for an object that already has queued records is queued too (any mode, job or not), so per-object order
// holds. Readers before the drain see the whole pre-call footprint (the body never runs part-way; docs/89 §10.2).

static const DWORD DISC_ADD = 0x00834370, DISC_REMOVE = 0x00834860;
static const u8 kDiscAddPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};  // push ebp; mov ebp,esp; and esp,-16
static const u8 kDiscRemovePro[] = {0x83, 0xEC, 0x14, 0x53, 0x55};     // sub esp,14h; push ebx; push ebp
typedef u8(__fastcall* DiscAddFn)(void* world, void* edx, void* obj, void* points, int high, int allow, int placement);
typedef u8(__fastcall* DiscRemoveFn)(void* world, void* edx, void* obj);
static DiscAddFn g_discAddOrig;
static DiscRemoveFn g_discRemoveOrig;
// the IC's own lookup and release (IC_RouteManager_RemoveDiscourageFootprint 0x754580)
typedef void*(__cdecl* SvcFn)();
// ObjectService_FindByIdAddRef 0x9395A0: thiscall(svc; idLo, idHi, Object** out), ret 0xC, true + AddRef'd *out when found
// [verified disassembly 2026-10-17; 0.12.0 declared two arguments and crashed the game: the callee popped 4 bytes too many]
typedef u8(__fastcall* FindRefFn)(void* svc, void* edx, DWORD idLo, DWORD idHi, void** out);
typedef void(__fastcall* ReleaseFn)(void** pp, void* edx);                            // releases *pp
static SvcFn g_objSvc = (SvcFn)(uintptr_t)0x00938B50;
static FindRefFn g_findRef = (FindRefFn)(uintptr_t)0x009395A0;
static ReleaseFn g_objRelease = (ReleaseFn)(uintptr_t)0x007460F0;
// D-X: LotRouting_RemoveObjectFootprints 0x635700: thiscall(P = [world+0x95C]; lotLo, lotHi, idLo, idHi), ret 0x10, al = a
// footprint was removed; lot 0 walks every lot [verified disassembly 0x635700..0x635916, Remove's call 0x834957..0x834964]
typedef u8(__fastcall* LotRemoveFn)(void* p, void* edx, DWORD lotLo, DWORD lotHi, DWORD idLo, DWORD idHi);
// lock-key map find 0x82AD60: thiscall(map = world+0x8B8; DWORD out[2], const DWORD* id), ret 8; out[0] = node, or the end
// sentinel buckets[bucketCount] (map+4, map+8) on a miss [verified disassembly 0x82AD60..0x82ADC5, Remove 0x8348F4..0x834918]
typedef void(__fastcall* KeyFindFn)(void* map, void* edx, DWORD* out, const DWORD* id);
static LotRemoveFn g_lotRemove = (LotRemoveFn)(uintptr_t)0x00635700;
static KeyFindFn g_keyFind = (KeyFindFn)(uintptr_t)0x0082AD60;

// design U: thiscall(world; obj, enable, force, arg), ret 0x10 [verified disassembly 0x833560..0x833675]; the early out
// 0x82A720 thiscall(world; obj), ret 4, al [verified 0x82A720..0x82A778]
static const DWORD USF_FN = 0x00833560, USF_SHOULD = 0x0082A720;
static const u8 kUsfPro[] = {0x83, 0xEC, 0x10, 0x80, 0x7C, 0x24, 0x1C, 0x00};  // sub esp,10h; cmp byte [esp+1Ch],0
typedef void(__fastcall* UsfFn)(void* world, void* edx, void* obj, DWORD enable, DWORD force, DWORD arg);
typedef u8(__fastcall* UsfShouldFn)(void* world, void* edx, void* obj);
static UsfFn g_usfOrig;
static UsfShouldFn g_usfShould = (UsfShouldFn)(uintptr_t)USF_SHOULD;

// design U phase 2 (docs/89 §12): RoutingWorld_ObjectFootprintFromTool 0x8334A0 and RoutingWorld_UpdateObjectLot 0x82D280,
// both thiscall(world; obj), ret 4, void (their callers 0x94713D, 0xB585A1, 0x9462C9, 0x946F99 ignore eax) [verified
// disassembly 0x8334A0..0x833558, 0x82D280..0x82D2CE]
static const DWORD FT_FN = 0x008334A0, UOL_FN = 0x0082D280;
static const u8 kFtPro[] = {0x83, 0xEC, 0x10, 0x56, 0x57};                                 // sub esp,10h; push esi; push edi
static const u8 kUolPro[] = {0x55, 0x56, 0x8B, 0xF1, 0x8D, 0xAE, 0x18, 0x09, 0x00, 0x00};  // push ebp; push esi; mov esi,ecx; lea ebp,[esi+918h]
typedef void(__fastcall* Fp2Fn)(void* world, void* edx, void* obj);
static Fp2Fn g_ftOrig, g_uolOrig;
// the private-footprint ICs' calls, flushed first for an object with records: IC 0x754610 -> RemoveFootprintLocked 0x82A0C0
// thiscall(world; obj, flag), ret 8, al; IC 0x75DCF0 -> 0x831BA0 thiscall(world; obj, points, flag), ret 0xC, al [verified]
static const DWORD PFR_SITE = 0x0075465E, PFR_FN = 0x0082A0C0, PFA_SITE = 0x0075DE1A, PFA_FN = 0x00831BA0;
typedef u8(__fastcall* PfRemoveFn)(void* world, void* edx, void* obj, DWORD flag);
typedef u8(__fastcall* PfAddFn)(void* world, void* edx, void* obj, void* points, DWORD flag);
static PfRemoveFn g_pfRemove = (PfRemoveFn)(uintptr_t)PFR_FN;
static PfAddFn g_pfAdd = (PfAddFn)(uintptr_t)PFA_FN;
// docs/89 §14: the private footprint's lot key, 0x7CFA30 cdecl(2, 0x19, idLo, idHi) -> edx:eax, pure shifts and ors [verified
// 0x7CFA30..0x7CFA6B]; 0x82A0C0 removes it from the object's lot with 0x635700 [verified 0x82A0F6..0x82A133]
typedef unsigned __int64(__cdecl* PfKeyFn)(int kind, int type, DWORD idLo, DWORD idHi);
static PfKeyFn g_pfKey = (PfKeyFn)(uintptr_t)0x007CFA30;

// flags kept as whole dwords; lock: the world it was made in; obj: the object at defer time (D-X pointer check, no reference)
// usf: a design U record (high = enable byte, allow = arg); fp2: a phase 2 record (1 FromTool, high = world +0x978 at the
// call; 2 UpdateObjectLot); pf: a private footprint remove (high = the IC's flag, docs/89 §14); else add says Add or Remove
struct DiscRec { bool add; DWORD high, allow, placement, idLo, idHi, t; int n; u8* pts; void* lock; void* obj; bool usf; u8 fp2; bool pf; };
static const int kDiscCap = 256;
static const DWORD kDiscMaxAgeMs = 250;  // older records are applied even if that means waiting for the running job
static DiscRec g_disc[kDiscCap];
static int g_discHead, g_discCount;  // owner (sim) thread only
static bool g_discDraining;

// docs/186 §8: the object DestroyById(idLo, idHi) is about to destroy, for the our-events ring (sim thread; the lookup and
// reference P5cPark takes, given back at once); 0 = not found
static DWORD CeDestroyObj(DWORD idLo, DWORD idHi) {
    void* svc = g_objSvc();
    void* obj = NULL;
    if (!svc || !g_findRef(svc, NULL, idLo, idHi, &obj) || !obj) return 0;
    const DWORD p = (DWORD)(uintptr_t)obj;
    g_objRelease(&obj, NULL);
    return p;
}
// docs/186 §8: our-events ring entry for a queued record
static void CeDiscQ(const DiscRec& r) {
    CeRec(CE_FP_QUEUE, (DWORD)(uintptr_t)r.obj, r.idLo, r.usf ? 1 : r.fp2 ? 2 + r.fp2 : r.pf ? 5 : r.add ? 0x10 : 0);
}
static bool DiscWorldOk(void* world) { return world && *((const u8*)world + 0x974) != 0; }

// the id is in the lock-key map (a parked object). The map is written on the sim thread only and never by a job (docs/89
// §3.2), so the sim reads it without +0x918.
static bool DiscKeyMapHas(u8* world, DWORD idLo, DWORD idHi) {
    if (!*(const DWORD*)(world + 0x8C4)) return false;  // Remove / 0x833560 look the id up only when the map holds entries
    const DWORD id[2] = {idLo, idHi};
    DWORD it[2] = {};
    g_keyFind(world + 0x8B8, NULL, it, id);
    const DWORD* buckets = *(const DWORD* const*)(world + 0x8BC);
    return it[0] != buckets[*(const DWORD*)(world + 0x8C0)];
}

// D-X 2: the Remove record's object is gone; remove whatever footprints its id left in the lot quadtrees. Caller holds +0x918.
static void DiscDeadSweep(const DiscRec& r, u8* world) {
    Ts3nCounters& k = ts3native_counters;
    void* p = *(void**)(world + 0x95C);
    if (!p) return;
    if (DiscKeyMapHas(world, r.idLo, r.idHi)) InterlockedIncrement(&k.discDeadKeyMap);  // shortcut: not erased
    InterlockedIncrement(&k.discDeadSweep);
    if (g_lotRemove(p, NULL, 0, 0, r.idLo, r.idHi)) InterlockedIncrement(&k.discDeadHit);
    world[0x994] = 0;  // as Remove does after 0x635700
}

// true = applied with the original
static bool DiscApplyOne(DiscRec& r, void* world) {
    Ts3nCounters& k = ts3native_counters;
    void* svc = g_objSvc();
    void* obj = NULL;
    if (!svc || !g_findRef(svc, NULL, r.idLo, r.idHi, &obj)) obj = NULL;
    const bool applied = obj && obj == r.obj;
    if (r.usf) {  // design U: same lookup and pointer check; gone: a disable sweeps like a Remove, an enable is dropped
        if (applied) {
            if (g_async.state == A_RUNNING) InterlockedIncrement(&k.usfApplyInJob);  // T-U1
            g_usfOrig(world, NULL, obj, r.high, 1, r.allow);  // force 1: the early out already passed at call time
            InterlockedIncrement(&k.usfApplied);
        } else if (obj) {
            InterlockedIncrement(&k.usfIdMismatch);
        } else if (r.high) {
            InterlockedIncrement(&k.usfGoneEnable1);
        } else {
            InterlockedIncrement(&k.usfGoneEnable0);
            if (k.discSweepMode) { DiscDeadSweep(r, (u8*)world); InterlockedIncrement(&k.usfSwept); }
        }
        if (obj) g_objRelease(&obj, NULL);
    } else if (r.fp2) {  // phase 2: the game's call as if made now; gone or reused id: dropped (a later call re-does it)
        if (applied) {
            if (g_async.state == A_RUNNING) InterlockedIncrement(&k.fp2ApplyInJob);
            if (r.fp2 == 1) {  // the caller's +0x978 bracket, replayed (its early outs read it in the world's batch mode)
                u8* const w = (u8*)world;
                const u8 cur = w[0x978];
                w[0x978] = (u8)r.high;
                g_ftOrig(world, NULL, obj);
                w[0x978] = cur;
            } else {
                g_uolOrig(world, NULL, obj);
            }
            InterlockedIncrement(&k.fp2Applied[r.fp2 - 1]);
        } else {
            InterlockedIncrement(&k.fp2Dropped[r.fp2 - 1]);
        }
        if (obj) g_objRelease(&obj, NULL);
    } else if (r.pf) {  // private footprint remove: the original; gone: its lot key swept from every lot (the component went
                        // with the object); reused id: dropped
        if (applied) {
            if (g_async.state == A_RUNNING) InterlockedIncrement(&k.pfApplyInJob);
            g_pfRemove(world, NULL, obj, r.high);
            InterlockedIncrement(&k.pfApplied);
        } else if (!obj && k.discSweepMode && *(void**)((u8*)world + 0x95C)) {
            const unsigned __int64 key = g_pfKey(2, 0x19, r.idLo, r.idHi);
            ((u8*)world)[0x994] = 0;  // as 0x82A0C0 does before 0x635700
            g_lotRemove(*(void**)((u8*)world + 0x95C), NULL, 0, 0, (DWORD)key, (DWORD)(key >> 32));
            InterlockedIncrement(&k.pfSwept);
        } else {
            InterlockedIncrement(&k.pfDropped);
        }
        if (obj) g_objRelease(&obj, NULL);
    } else if (obj && obj != r.obj) {  // the id names another object now (reused id): not the one the call was made for
        InterlockedIncrement(&k.discIdMismatch);
        g_objRelease(&obj, NULL);
        InterlockedIncrement(&k.discDropped);
    } else if (!obj) {
        InterlockedIncrement(&k.discDropped);
        InterlockedIncrement(r.add ? &k.discDropAdd : &k.discDropRemove);
        if (!r.add && k.discSweepMode) DiscDeadSweep(r, (u8*)world);
    } else {
        if (r.add) {
            u8* vec[3] = {r.pts, r.pts + r.n * 16, r.pts + r.n * 16};  // begin, end, capacity (the original reads begin and end)
            g_discAddOrig(world, NULL, obj, vec, (int)r.high, (int)r.allow, (int)r.placement);
        } else {
            g_discRemoveOrig(world, NULL, obj);
        }
        g_objRelease(&obj, NULL);
        InterlockedIncrement(&k.discApplied);
    }
    _aligned_free(r.pts);
    r.pts = NULL;
    CeRec(CE_FP_APPLY, (DWORD)(uintptr_t)r.obj, r.idLo, (applied ? 1 : 0) | (g_async.state == A_RUNNING ? 2 : 0));  // docs/186 §8
    return applied;
}

// Design U collapse at the drain (r already popped): the object's next queued record is also a U record, so it redoes the
// whole remove plus re-insert from the then-current component state and r's apply changes nothing it leaves. Not for a parked
// object: the parked branch only stores the enable byte and inserts into an empty node, so there a skipped enable could lose
// an insert [verified 0x833639..0x833663]. Caller holds +0x918 with no job running.
static bool UsfSuperseded(const DiscRec& r, u8* world) {
    for (int i = 0; i < g_discCount; i++) {
        const DiscRec& n = g_disc[(g_discHead + i) % kDiscCap];
        if (n.idLo == r.idLo && n.idHi == r.idHi && n.lock == r.lock)
            return n.usf && n.obj == r.obj && !DiscKeyMapHas(world, r.idLo, r.idHi);
    }
    return false;
}

// Owner thread: applies every record in order. force = wait for a running job; else only when no job runs.
static void DiscDrain(bool force) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_discCount || g_discDraining || GetCurrentThreadId() != g_async.owner) return;
    if (!force && g_async.state == A_RUNNING) return;
    const bool jobRan = g_async.state == A_RUNNING;
    void* lock = g_rgLock;
    void* world = lock ? (u8*)lock - 0x918 : NULL;
    g_discDraining = true;
    InterlockedIncrement(&g_rgOffSimOpen);  // the worker starts no job until the drain is done
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    UdWait("the running route job (deferred footprints)");  // [UserDiag]: no-op unless this thread is saving
    WaitAsyncDone(SW_DISC);
    static const DWORD kInfinite = 0xFFFFFFFF;
    UdWait("+0x918 (deferred footprints)");
    if (lock) LockAcquireHook(lock, NULL, &kInfinite);  // uncontended: no job runs and none can start
    UdWait(NULL);
    const LONG waited = UsSince(t0);
    InterlockedExchangeAdd(&k.discDrainWaitUs, waited);
    HaNote(0x06000000, waited, HA_JOB);
    if (jobRan) InterlockedIncrement(&k.discForced);
    const DWORD now = GetTickCount();
    while (g_discCount) {  // records added by an event handler meanwhile go to the tail and are applied too
        DiscRec r = g_disc[g_discHead];
        g_disc[g_discHead].pts = NULL;
        g_discHead = (g_discHead + 1) % kDiscCap;
        g_discCount--;
        Max(&k.discAgeMaxMs, (LONG)(now - r.t));
        if (r.usf) Max(&k.usfAgeMaxMs, (LONG)(now - r.t));
        if (r.fp2) Max(&k.fp2AgeMaxMs, (LONG)(now - r.t));
        if (r.usf && r.lock == lock && DiscWorldOk(world) && UsfSuperseded(r, (u8*)world)) InterlockedIncrement(&k.usfCollapsed);
        else if (r.lock == lock && DiscWorldOk(world)) DiscApplyOne(r, world);  // a record from another world is dropped
        else {
            InterlockedIncrement(&k.discDropped);
            if (r.fp2) InterlockedIncrement(&k.fp2Dropped[r.fp2 - 1]);
            if (r.pf) InterlockedIncrement(&k.pfDropped);
            _aligned_free(r.pts);
        }
    }
    if (lock) LockReleaseHook(lock, NULL);
    RgOffSimDrop();
    g_discDraining = false;
}

static bool DiscPending() { return g_discCount != 0; }  // records still deferred (docs/134 §2.6 order check)

// A drain point that is not a D1/D2 call: apply when no job runs, or when the oldest record is too old.
static void DiscPoll() {
    if (!g_discCount) return;
    DiscDrain(GetTickCount() - g_disc[g_discHead].t >= kDiscMaxAgeMs);
}

// PlanDrain (docs/89 §13; counters v107, [Discourage] PlanDrain=1, runtime switch sdMode, --ab plandrain): before a sim-thread
// sync plan (PlanLockedBody, after the original's early outs) every deferred record is applied, so the plan sees the world it
// would see with DeferFootprint=0. Skipped (today's defer-on behaviour) inside a job callback (WaitAsyncDone would wait for the
// job that waits on us), with +0x918 already owned here (no nested drain), with the P5c lent lock (the drain's job wait would
// hand it back and switch P5c off), while draining, and with an Add queued (may fire World_FireEvent 0x11: IC points only).
// Returns 1 = drained, -1 = skipped, 0 = not asked (off, nothing queued, another thread, P4 clone run).
static int DiscDrainForPlan() {
    Ts3nCounters& k = ts3native_counters;
    if (k.sdMode != 1 || !g_discCount) return 0;
    const DWORD me = GetCurrentThreadId();
    if (me != g_async.owner || t_snapRun) return 0;
    InterlockedIncrement(&k.sdChecks);
    int why = g_inInvoke ? 1 : k.rgOwnerTid == (LONG)me ? 2 : P5cLent(me) ? 3 : g_discDraining ? 4 : -1;
    for (int i = 0; why < 0 && i < g_discCount; i++)
        if (g_disc[(g_discHead + i) % kDiscCap].add) why = 0;
    if (why >= 0) { InterlockedIncrement(&k.sdSkip[why]); return -1; }
    const int n = g_discCount;
    const bool ran = g_async.state == A_RUNNING;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    DiscDrain(true);
    const LONG us = UsSince(t0);
    InterlockedIncrement(&k.sdDrains);
    InterlockedExchangeAdd(&k.sdRecs, n);
    if (ran) InterlockedIncrement(&k.sdJobRan);
    InterlockedExchangeAdd(&k.sdUs, us);
    Max(&k.sdMaxUs, us);
    return 1;
}

// D-X 1, from rtsites.cpp RT_DESTROY before ObjectService_DestroyById(idLo, idHi) runs; canWait = its job wait returned (no
// job runs now), false inside a job callback. Applies the object's Remove records in order under +0x918 (managed-free,
// docs/84 §7.1, so safe at every native destroy caller), drops its Add records, keeps the other records in their order.
static void DiscOnDestroy(DWORD idLo, DWORD idHi, bool canWait) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_discCount || g_discDraining || GetCurrentThreadId() != g_async.owner) return;
    void* lock = g_rgLock;
    const int n0 = g_discCount;
    int mine = 0;
    for (int i = 0; i < n0; i++) {
        const DiscRec& r = g_disc[(g_discHead + i) % kDiscCap];
        if (r.idLo == idLo && r.idHi == idHi && r.lock == lock) mine++;
    }
    if (!mine) return;
    if (!canWait) { InterlockedIncrement(&k.discDestroyInInvoke); return; }  // left queued: the dead-id sweep handles it
    void* world = lock ? (u8*)lock - 0x918 : NULL;
    if (!k.discDestroyMode || !DiscWorldOk(world)) return;
    static const DWORD kInfinite = 0xFFFFFFFF;
    g_discDraining = true;  // no drain inside; a record made meanwhile (none expected: Remove has no managed reach) goes last
    LockAcquireHook(lock, NULL, &kInfinite);  // uncontended: no job runs and none starts while the destroy is open
    int w = 0;
    for (int i = 0; i < n0; i++) {
        DiscRec& r = g_disc[(g_discHead + i) % kDiscCap];
        if (r.idLo != idLo || r.idHi != idHi || r.lock != lock) {
            if (w != i) { g_disc[(g_discHead + w) % kDiscCap] = r; r.pts = NULL; }
            w++;
        } else if (r.usf || r.fp2 || r.pf) {  // design U, phase 2, private remove: applied in order like a Remove (destroy does
                                              // not remove routing footprints)
            if (DiscApplyOne(r, world) && r.usf) InterlockedIncrement(&k.usfDestroyApplied);
        } else if (r.add) {
            InterlockedIncrement(&k.discDropAdd);
            InterlockedIncrement(&k.discDropped);
            _aligned_free(r.pts);
            r.pts = NULL;
        } else if (DiscApplyOne(r, world)) {
            InterlockedIncrement(&k.discDestroyApplied);
        }
    }
    for (int i = n0; i < g_discCount; i++, w++) {  // records made during the loop move down behind the kept ones
        DiscRec& r = g_disc[(g_discHead + i) % kDiscCap];
        g_disc[(g_discHead + w) % kDiscCap] = r;
        r.pts = NULL;
    }
    g_discCount = w;
    LockReleaseHook(lock, NULL);
    g_discDraining = false;
    CeRec(CE_DISC_DESTROY, (DWORD)(uintptr_t)world, idLo, (DWORD)(n0 - w));  // docs/186 §8
}

// true = recorded (the caller answers 1)
static bool DiscDefer(bool add, void* obj, const u8* begin, int n, int high, int allow, int placement) {
    Ts3nCounters& k = ts3native_counters;
    if (k.discMode != 1 || GetCurrentThreadId() != g_async.owner) return false;
    if (!g_discDraining && g_async.state != A_RUNNING) return false;  // no job: apply now (after older records)
    if (g_discCount == kDiscCap) { InterlockedIncrement(&k.discOverflow); return false; }
    DiscRec& r = g_disc[(g_discHead + g_discCount) % kDiscCap];
    r.add = add;
    r.high = (DWORD)high;
    r.allow = (DWORD)allow;
    r.placement = (DWORD)placement;
    r.idLo = *(const DWORD*)((const u8*)obj + 8);
    r.idHi = *(const DWORD*)((const u8*)obj + 0xC);
    r.t = GetTickCount();
    r.n = n;
    r.pts = NULL;
    r.lock = g_rgLock;
    r.obj = obj;
    r.usf = false;
    r.fp2 = 0;
    r.pf = false;
    if (add) {
        // 16-byte aligned like the game's own vector: the original reads the points with movaps (0xAB88BD); a malloc'd
        // copy (8-byte aligned) crashed it in 0.12.2 (#GP reported as an access violation at 0xFFFFFFFF, 2026-10-17)
        r.pts = (u8*)_aligned_malloc((size_t)n * 16, 16);
        if (!r.pts) { InterlockedIncrement(&k.discOverflow); return false; }
        memcpy(r.pts, begin, (size_t)n * 16);
    }
    g_discCount++, CeDiscQ(r);
    if (g_discDraining) InterlockedIncrement(&k.discNested);
    InterlockedIncrement(add ? &k.discDeferredAdd : &k.discDeferredRemove);
    Max(&k.discPendingMax, g_discCount);
    return true;
}

static u8 __fastcall DiscAddHook(void* world, void* edx, void* obj, void* points, int high, int allow, int placement) {
    const u8* const* v = (const u8* const*)points;
    const int n = v && v[0] ? (int)((v[1] - v[0]) / 16) : 0;
    if (t_snapRun || !DiscWorldOk(world) || !obj || n < 3) return g_discAddOrig(world, edx, obj, points, high, allow, placement);  // early outs
    if (DiscDefer(true, obj, v[0], n, high, allow, placement)) return 1;
    DiscDrain(true);  // older records first (no job runs, or the queue is full)
    return g_discAddOrig(world, edx, obj, points, high, allow, placement);
}

static u8 __fastcall DiscRemoveHook(void* world, void* edx, void* obj) {
    if (t_snapRun || !DiscWorldOk(world) || !obj) return g_discRemoveOrig(world, edx, obj);  // early outs, P4 clone run
    if (DiscDefer(false, obj, NULL, 0, 0, 0, 0)) return 1;
    DiscDrain(true);
    return g_discRemoveOrig(world, edx, obj);
}

// Design U hook. Pass-through rules (docs/89 §5.1 rule 1), then the early out, then a record.
static void __fastcall UsfHook(u8* world, void* edx, u8* obj, DWORD enable, DWORD force, DWORD arg) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD me = GetCurrentThreadId();
    if (me != g_async.owner || t_snapRun || !obj) return g_usfOrig(world, edx, obj, enable, force, arg);
    InterlockedIncrement(&k.usfCalls);
    const bool running = g_async.state == A_RUNNING;
    if (running) InterlockedIncrement(&k.usfInJob);
    // nested under +0x918 (discourage Add/Remove, D's drains and the applies themselves), a job callback, another world, closing
    if (k.rgOwnerTid == (LONG)me || g_inInvoke || g_discDraining || !DiscWorldOk(world) || (void*)(world + 0x918) != g_rgLock ||
        k.rtClosing) {
        InterlockedIncrement(&k.usfPass);
        return g_usfOrig(world, edx, obj, enable, force, arg);
    }
    const DWORD idLo = *(const DWORD*)(obj + 8), idHi = *(const DWORD*)(obj + 0xC);
    int last = -1;  // the object's newest queued record: later calls queue behind it in any mode
    for (int i = g_discCount - 1; i >= 0 && last < 0; i--) {
        const DiscRec& r = g_disc[(g_discHead + i) % kDiscCap];
        if (r.idLo == idLo && r.idHi == idHi && r.lock == g_rgLock) last = i;
    }
    if (last < 0 && !(k.usfMode == 1 && running)) return g_usfOrig(world, edx, obj, enable, force, arg);  // today's call
    if (!(u8)force && !g_usfShould(world, NULL, obj)) { InterlockedIncrement(&k.usfEarlyOut); return; }  // as the original
    if (last < 0 && DiscKeyMapHas(world, idLo, idHi)) {  // parked: no collapse rule for it, so today's call (it waits)
        InterlockedIncrement(&k.usfParkedPass);
        return g_usfOrig(world, edx, obj, enable, 1, arg);
    }
    const DWORD en = enable & 0xFF;  // the original reads the byte
    if (last >= 0) {  // the same call again: applying both equals applying one (both branches of the body)
        const DiscRec& p = g_disc[(g_discHead + last) % kDiscCap];
        if (p.usf && p.obj == obj && p.high == en && p.allow == arg) { InterlockedIncrement(&k.usfDup); return; }
    }
    if (g_discCount == kDiscCap) {
        bool add = false;
        for (int i = 0; i < g_discCount && !add; i++) add = g_disc[(g_discHead + i) % kDiscCap].add;
        // Remove and U records are managed-free (docs/84 §7.1, docs/89 §1), so they may be applied here; an Add may fire
        // World_FireEvent 0x11 and waits for an IC point: then this call runs out of order (counted)
        if (!add) DiscDrain(true);
        InterlockedIncrement(add ? &k.usfFullPass : &k.usfFullDrain);
        return g_usfOrig(world, edx, obj, enable, 1, arg);
    }
    DiscRec& r = g_disc[(g_discHead + g_discCount) % kDiscCap];
    r = DiscRec{};
    r.usf = true;
    r.high = en;
    r.allow = arg;
    r.idLo = idLo;
    r.idHi = idHi;
    r.t = GetTickCount();
    r.lock = g_rgLock;
    r.obj = obj;
    g_discCount++, CeDiscQ(r);
    InterlockedIncrement(&k.usfDeferred);
    if (!(k.usfMode == 1 && running)) InterlockedIncrement(&k.usfOrderDefer);
    Max(&k.discPendingMax, g_discCount);
}

// Design U phase 2 (docs/89 §12; counters v94, [Discourage] DeferFootprint2=1, default 0; runtime switch fp2Mode, --ab
// deferfootprint2; needs design U installed, whose order rule keeps a U call behind a queued phase 2 record):
//   kind 1, ObjectFootprintFromTool 0x8334A0 (key 0x8334CC; event 0x0E transform published -> 0x9470B0, build tool 0xB585A1):
//     the original's lock-free early out 0x82A720 runs at call time; the record keeps world +0x978 (the callers' bracket), the
//     apply runs the original with that byte set and restores it. While the world's batch mode is on (+0x97E and +0x976) the
//     byte decides the early outs 0x82A720 / 0x829A60 inside (and the first clears +0x97F), so the call runs now.
//   kind 2, UpdateObjectLot 0x82D280 (key 0x82D296; events 9 / 0x0A): no early out, no +0x978 reader or writer within 8
//     direct hops; the apply re-homes the footprints to the object's lot at apply time.
// Pass-through, order rule, duplicate drop, queue full, destroy and gone-at-apply as design U. Readers that need the fresh
// footprints of the object get a flush first (fp2Forced): the private-footprint ICs (call sites 0x75465E, 0x75DE1A), D's
// non-deferred Add/Remove (DiscDrain first), destroy (D-X), save / travel / quit and the START / READY / CANCEL polls.
static int DiscLastOf(DWORD idLo, DWORD idHi) {  // the object's newest queued record in this world, else -1
    for (int i = g_discCount - 1; i >= 0; i--) {
        const DiscRec& r = g_disc[(g_discHead + i) % kDiscCap];
        if (r.idLo == idLo && r.idHi == idHi && r.lock == g_rgLock) return i;
    }
    return -1;
}

static void Fp2Call(int kind, u8* world, u8* obj) { (kind == 1 ? g_ftOrig : g_uolOrig)(world, NULL, obj); }

static void Fp2Hook(int kind, u8* world, u8* obj) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD me = GetCurrentThreadId();
    if (me != g_async.owner || t_snapRun || !obj) return Fp2Call(kind, world, obj);
    if (k.rgOwnerTid == (LONG)me || g_inInvoke || g_discDraining || !DiscWorldOk(world) || (void*)(world + 0x918) != g_rgLock ||
        k.rtClosing || (kind == 1 && world[0x97E] && world[0x976])) {
        InterlockedIncrement(&k.fp2Pass);
        return Fp2Call(kind, world, obj);
    }
    const DWORD idLo = *(const DWORD*)(obj + 8), idHi = *(const DWORD*)(obj + 0xC);
    const int last = DiscLastOf(idLo, idHi);
    if (last < 0 && !(k.fp2Mode == 1 && g_async.state == A_RUNNING)) return Fp2Call(kind, world, obj);  // today's call
    if (kind == 1 && !g_usfShould(world, NULL, obj)) return;  // the original's own early out says no: it does nothing
    const DWORD br = kind == 1 ? world[0x978] : 0;
    if (last >= 0) {  // the same call again right behind: remove plus re-insert (re-home) from the same state twice = once
        const DiscRec& p = g_disc[(g_discHead + last) % kDiscCap];
        if (p.fp2 == kind && p.obj == obj && p.high == br) { InterlockedIncrement(&k.fp2Dup); return; }
    }
    if (g_discCount == kDiscCap) {  // as design U: drain unless an Add (World_FireEvent 0x11) is queued
        bool add = false;
        for (int i = 0; i < g_discCount && !add; i++) add = g_disc[(g_discHead + i) % kDiscCap].add;
        if (!add) DiscDrain(true);
        InterlockedIncrement(add ? &k.fp2FullPass : &k.fp2Forced);
        return Fp2Call(kind, world, obj);
    }
    DiscRec& r = g_disc[(g_discHead + g_discCount) % kDiscCap];
    r = DiscRec{};
    r.fp2 = (u8)kind;
    r.high = br;
    r.idLo = idLo;
    r.idHi = idHi;
    r.t = GetTickCount();
    r.lock = g_rgLock;
    r.obj = obj;
    g_discCount++, CeDiscQ(r);
    InterlockedIncrement(&k.fp2Deferred[kind - 1]);
    Max(&k.discPendingMax, g_discCount);
}
static void __fastcall FtHook(u8* world, void*, u8* obj) { Fp2Hook(1, world, obj); }
static void __fastcall UolHook(u8* world, void*, u8* obj) { Fp2Hook(2, world, obj); }

// flush before a reader that needs the object's fresh footprints (an IC point: managed code is on the stack); true = flushed
static bool Fp2FlushFor(const u8* obj) {
    const DWORD me = GetCurrentThreadId();
    if (!obj || !g_discCount || g_discDraining || me != g_async.owner || ts3native_counters.rgOwnerTid == (LONG)me) return false;
    if (DiscLastOf(*(const DWORD*)(obj + 8), *(const DWORD*)(obj + 0xC)) < 0) return false;
    DiscDrain(true);
    InterlockedIncrement(&ts3native_counters.fp2Forced);
    return true;
}

// docs/89 §14 ([Discourage] DeferPrivateFp=1, counters v113, switch pfMode, --ab deferprivatefp): the IC's RemoveFootprintLocked
// call (0x75465E) on the sim thread while a job runs becomes a record in D's FIFO, answered 1 (both script callers, EA's and
// simler90's DynamicSimFootprint.set_Enabled, pop the result [verified IL]). Same skeleton as Fp2Hook: pass-through rules, the
// order rule (an object with any queued record queues in any mode), duplicate drop, queue full, apply at D's drain points and
// D-X destroy. Returns -1 = not deferred (the caller runs the original), else the IC result.
static int PfRemoveDefer(u8* world, u8* obj, DWORD flag) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD me = GetCurrentThreadId();
    if (me != g_async.owner || t_snapRun || !obj) return -1;
    InterlockedIncrement(&k.pfCalls);
    const bool running = g_async.state == A_RUNNING;
    if (running) InterlockedIncrement(&k.pfInJob);
    if (k.rgOwnerTid == (LONG)me || g_inInvoke || g_discDraining || !DiscWorldOk(world) || (void*)(world + 0x918) != g_rgLock ||
        k.rtClosing) {
        InterlockedIncrement(&k.pfPass);
        return -1;
    }
    const DWORD idLo = *(const DWORD*)(obj + 8), idHi = *(const DWORD*)(obj + 0xC);
    const int last = DiscLastOf(idLo, idHi);
    if (last < 0 && !(k.pfMode == 1 && running)) return -1;  // today's call
    if (last >= 0) {  // the same remove right behind: the second finds nothing (0x635700 says no, the flag branch is skipped)
        const DiscRec& p = g_disc[(g_discHead + last) % kDiscCap];
        if (p.pf && p.obj == obj && p.high == flag) { InterlockedIncrement(&k.pfDup); return 1; }
    }
    if (g_discCount == kDiscCap) {  // as design U: drain unless an Add (World_FireEvent 0x11) is queued, then the call now
        bool add = false;
        for (int i = 0; i < g_discCount && !add; i++) add = g_disc[(g_discHead + i) % kDiscCap].add;
        if (!add) DiscDrain(true);
        else InterlockedIncrement(&k.pfFullPass);
        return -1;
    }
    DiscRec& r = g_disc[(g_discHead + g_discCount) % kDiscCap];
    r = DiscRec{};
    r.pf = true;
    r.high = flag;
    r.idLo = idLo;
    r.idHi = idHi;
    r.t = GetTickCount();
    r.lock = g_rgLock;
    r.obj = obj;
    g_discCount++, CeDiscQ(r);
    InterlockedIncrement(&k.pfDeferred);
    Max(&k.discPendingMax, g_discCount);
    return 1;
}

static u8 __fastcall PfRemoveSite(void* world, void*, u8* obj, DWORD flag) {
    if (ts3native_counters.pfReady) {
        const int r = PfRemoveDefer((u8*)world, obj, flag);
        if (r >= 0) return (u8)r;
    }
    Fp2FlushFor(obj);  // not deferred: the object's older records first (a deferred FromTool could re-insert after this remove)
    return g_pfRemove(world, NULL, obj, flag);
}
static u8 __fastcall PfAddSite(void* world, void*, u8* obj, void* points, DWORD flag) {
    // shortcut: Add is not deferred (0x831BA0 is ~0x640 bytes with string and lot lookups, not audited); a queued remove of the
    // object is flushed first, so the add waits as today. Defer it too if pfAddFlush is large in the verify.
    if (Fp2FlushFor(obj) && ts3native_counters.pfReady) InterlockedIncrement(&ts3native_counters.pfAddFlush);
    return g_pfAdd(world, NULL, obj, points, flag);
}

// the two private-footprint IC call sites, shared by phase 2 (flush) and DeferPrivateFp (defer); patched once
static bool PfSitesInstall() {
    static int done = 0;  // 1 ok, -1 failed
    if (!done) {
        static const CallSite kPf[] = {{PFR_SITE, PFR_FN, (void*)PfRemoveSite}, {PFA_SITE, PFA_FN, (void*)PfAddSite}};
        done = PatchCalls("private footprint sites (docs/89 §12, §14)", kPf, 2) ? 1 : -1;
    }
    return done > 0;
}

static void InstallPrivateFpDefer(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"Discourage", L"DeferPrivateFp", 0, ini)) { Log("private footprint deferral off (DeferPrivateFp=0)"); return; }
    // phase 2's detours keep FromTool / UpdateObjectLot calls of the object behind its queued remove (order rule, also with
    // fp2Mode 0: DeferFootprint2=2)
    if (!k.fp2Ready) { Log("FAIL private footprint deferral: needs design U phase 2 ([Discourage] DeferFootprint2=1 or 2)"); return; }
    if (!PfSitesInstall()) { Log("FAIL private footprint deferral: call sites"); return; }
    k.pfReady = 1;
    k.pfMode = 1;
    Log("OK   private footprint deferral (IC RemovePrivateFootprint 0x75465E -> 0x82A0C0; docs/89 §14)");
}

static void InstallFootprintDefer2(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const int v = GetPrivateProfileIntW(L"Discourage", L"DeferFootprint2", 0, ini);  // 2 = installed, switch off (order rule only)
    if (!v) { Log("footprint deferral phase 2 off (DeferFootprint2=0)"); return; }
    if (!k.usfReady) { Log("FAIL footprint deferral phase 2: needs design U ([Discourage] DeferFootprint=1)"); return; }
    // the flush sites first: without them a deferred record could land after a private-footprint change of its object
    if (!PfSitesInstall()) { Log("FAIL footprint deferral phase 2: flush sites"); return; }
    g_ftOrig = (Fp2Fn)InstallDetour("RoutingWorld_ObjectFootprintFromTool", FT_FN, kFtPro, sizeof(kFtPro), (void*)FtHook, &g_ftOrig);
    g_uolOrig = g_ftOrig ? (Fp2Fn)InstallDetour("RoutingWorld_UpdateObjectLot", UOL_FN, kUolPro, sizeof(kUolPro), (void*)UolHook, &g_uolOrig) : NULL;
    if (!g_uolOrig) { Log("FAIL footprint deferral phase 2: detours incomplete"); return; }  // shortcut: a lone FromTool detour stays, mode 0
    k.fp2Ready = 1;
    k.fp2Mode = v == 2 ? 0 : 1;
    Log("OK   footprint deferral phase 2 (0x8334A0 FromTool, 0x82D280 UpdateObjectLot; docs/89 §12; mode %ld)", k.fp2Mode);
}

static void InstallFootprintDefer(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"Discourage", L"DeferFootprint", 0, ini)) { Log("footprint deferral off (DeferFootprint=0)"); return; }
    if (!ts3native_counters.discReady) { Log("FAIL footprint deferral: needs the discourage deferral ([Discourage] Enabled=1)"); return; }
    g_usfOrig = (UsfFn)InstallDetour("RoutingWorld_UpdateSimFootprint", USF_FN, kUsfPro, sizeof(kUsfPro), (void*)UsfHook, &g_usfOrig);
    if (!g_usfOrig) { Log("FAIL footprint deferral: detour"); return; }
    ts3native_counters.usfReady = 1;
    ts3native_counters.usfMode = 1;
    Log("OK   footprint deferral (design U, docs/89)");
    if (GetPrivateProfileIntW(L"Discourage", L"PlanDrain", 0, ini)) {
        ts3native_counters.sdReady = 1;
        ts3native_counters.sdMode = 1;
        Log("OK   plan drain: deferred footprints applied before every sim-thread sync plan (docs/89 §13)");
    } else {
        Log("plan drain off (PlanDrain=0)");
    }
}

static void InstallDiscourage(const wchar_t* ini) {
    if (!ts3native_counters.rgEnforce) return;  // the deferral is only needed (and only safe to reason about) with the gate on
    // design G standalone (docs/88 §10, p5c.cpp P5cPark): independent of D's detours and of P5c / RouteSnap
    if (GetPrivateProfileIntW(L"Discourage", L"DestroyPark", 0, ini)) {
        ts3native_counters.dpMode = 1;
        ts3native_counters.dpReady = 1;
        Log("OK   destroy park (design G): a destroy during an async route job parks one reference and runs without the wait");
        ts3native_counters.dxMode = Clamp(GetPrivateProfileIntW(L"Discourage", L"DestroyParkDx", 0, ini), 0, 1);
        if (ts3native_counters.dxMode) Log("OK   destroy park DestroyParkDx=1: queued discourage records of the id no longer keep the wait");
    }
    // default off until the 0.12.4 alignment fix has run in game (0.12.2 crashed on an 8-byte aligned points copy)
    if (!GetPrivateProfileIntW(L"Discourage", L"Enabled", 0, ini)) { Log("discourage deferral off (Enabled=0)"); return; }
    g_discAddOrig = (DiscAddFn)InstallDetour("RoutingWorld_AddDiscourageFootprint", DISC_ADD, kDiscAddPro, sizeof(kDiscAddPro), (void*)DiscAddHook, &g_discAddOrig);
    g_discRemoveOrig = g_discAddOrig ? (DiscRemoveFn)InstallDetour("RoutingWorld_RemoveDiscourageFootprint", DISC_REMOVE, kDiscRemovePro,
                                                                 sizeof(kDiscRemovePro), (void*)DiscRemoveHook, &g_discRemoveOrig) : NULL;
    if (!g_discRemoveOrig) { Log("FAIL discourage deferral: detours incomplete"); return; }  // shortcut: a lone Add detour stays, mode 0
    ts3native_counters.discReady = 1;
    ts3native_counters.discMode = Clamp(GetPrivateProfileIntW(L"Discourage", L"Mode", 1, ini), 0, 1);
    // D-X: correctness parts of D, on with it unless switched off for an A/B
    ts3native_counters.discDestroyMode = Clamp(GetPrivateProfileIntW(L"Discourage", L"DestroyApply", 1, ini), 0, 1);
    ts3native_counters.discSweepMode = Clamp(GetPrivateProfileIntW(L"Discourage", L"DeadSweep", 1, ini), 0, 1);
    Log("OK   discourage deferral (Mode=%ld DestroyApply=%ld DeadSweep=%ld)", ts3native_counters.discMode,
        ts3native_counters.discDestroyMode, ts3native_counters.discSweepMode);
    InstallFootprintDefer(ini);
    InstallFootprintDefer2(ini);
    InstallPrivateFpDefer(ini);
}
