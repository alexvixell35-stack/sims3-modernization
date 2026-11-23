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
//
// LostRemovalFix (docs/89 §16, counters v172, [Discourage] LostRemovalFix=1, runtime switch fp2lrMode, --ab lostremoval): event
// 0xA is the one place a destroyed or moved object's routing footprints leave the lot map: owner +0x3C (0xACE060 lot, 0xAA8FF0
// world, 0xB6E19C) -> 0x946F70 -> UpdateObjectLot 0x82D280 -> 0x82C120 -> 0x82B450 -> 0x635700. UpdateObjectLot is remove-only.
// So a queued kind 2 record keeps the lot of the call and goes to 0x82C120 at apply, with the object alive or gone: vanilla's
// own call, as if made then. 0x833680 (event 9, the re-insert) applies the object's records first, or the late remove would
// take the fresh insert. Off: the old rules (lot read at apply, a gone object's record dropped).
//
// GhostSweep (docs/89 §17, [Discourage] GhostSweep, 0 off, 1 count only, 2 remove): saves made with the old rules can carry those
// ghosts (LotLevel_SaveFootprints writes every map entry). One pass over P's footprint map inside the load screen, from the
// ts3fixes "world:" line (OnWorldLoadFinished, all lots loaded, docs/38), never during play. Failed read = entry kept.

static const DWORD DISC_ADD = TS3A(0x00834370), DISC_REMOVE = TS3A(0x00834860);
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
static SvcFn g_objSvc = (SvcFn)(uintptr_t)TS3A(0x00938B50);
static FindRefFn g_findRef = (FindRefFn)(uintptr_t)TS3A(0x009395A0);
static ReleaseFn g_objRelease = (ReleaseFn)(uintptr_t)TS3A(0x007460F0);
// D-X: LotRouting_RemoveObjectFootprints 0x635700: thiscall(P = [world+0x95C]; lotLo, lotHi, idLo, idHi), ret 0x10, al = a
// footprint was removed; lot 0 walks every lot [verified disassembly 0x635700..0x635916, Remove's call 0x834957..0x834964]
typedef u8(__fastcall* LotRemoveFn)(void* p, void* edx, DWORD lotLo, DWORD lotHi, DWORD idLo, DWORD idHi);
// lock-key map find 0x82AD60: thiscall(map = world+0x8B8; DWORD out[2], const DWORD* id), ret 8; out[0] = node, or the end
// sentinel buckets[bucketCount] (map+4, map+8) on a miss [verified disassembly 0x82AD60..0x82ADC5, Remove 0x8348F4..0x834918]
typedef void(__fastcall* KeyFindFn)(void* map, void* edx, DWORD* out, const DWORD* id);
static LotRemoveFn g_lotRemove = (LotRemoveFn)(uintptr_t)TS3A(0x00635700);
static KeyFindFn g_keyFind = (KeyFindFn)(uintptr_t)TS3A(0x0082AD60);
// docs/89 §16: UpdateObjectLot's body 0x82C120 thiscall(world; idLo, idHi, lotLo, lotHi), ret 0x10, al = removed; takes no
// object: the lot it is given, the lock-key map branch, world lot -> 0, portals 0x6399E0 [verified 0x82C120..0x82C1F8,
// 0x82B450..0x82B57E]. Its 0x82A0C0 call skips a NULL object [verified 0x82A0D8]. The lot: 0x8297C0 cdecl(obj), edx:eax [verified].
typedef u8(__fastcall* UolCoreFn)(void* world, void* edx, DWORD idLo, DWORD idHi, DWORD lotLo, DWORD lotHi);
typedef unsigned __int64(__cdecl* ObjLotFn)(const void* obj);
static UolCoreFn g_uolCore = (UolCoreFn)(uintptr_t)TS3A(0x0082C120);
static ObjLotFn g_objLot = (ObjLotFn)(uintptr_t)TS3A(0x008297C0);

// design U: thiscall(world; obj, enable, force, arg), ret 0x10 [verified disassembly 0x833560..0x833675]; the early out
// 0x82A720 thiscall(world; obj), ret 4, al [verified 0x82A720..0x82A778]
static const DWORD USF_FN = TS3A(0x00833560), USF_SHOULD = TS3A(0x0082A720);
static const u8 kUsfPro[] = {0x83, 0xEC, 0x10, 0x80, 0x7C, 0x24, 0x1C, 0x00};  // sub esp,10h; cmp byte [esp+1Ch],0
typedef void(__fastcall* UsfFn)(void* world, void* edx, void* obj, DWORD enable, DWORD force, DWORD arg);
typedef u8(__fastcall* UsfShouldFn)(void* world, void* edx, void* obj);
static UsfFn g_usfOrig;
static UsfShouldFn g_usfShould = (UsfShouldFn)(uintptr_t)USF_SHOULD;

// design U phase 2 (docs/89 §12): RoutingWorld_ObjectFootprintFromTool 0x8334A0 and RoutingWorld_UpdateObjectLot 0x82D280,
// both thiscall(world; obj), ret 4, void (their callers 0x94713D, 0xB585A1, 0x9462C9, 0x946F99 ignore eax) [verified
// disassembly 0x8334A0..0x833558, 0x82D280..0x82D2CE]
static const DWORD FT_FN = TS3A(0x008334A0), UOL_FN = TS3A(0x0082D280);
static const u8 kFtPro[] = {0x83, 0xEC, 0x10, 0x56, 0x57};                                 // sub esp,10h; push esi; push edi
static const u8 kUolPro[] = {0x55, 0x56, 0x8B, 0xF1, 0x8D, 0xAE, 0x18, 0x09, 0x00, 0x00};  // push ebp; push esi; mov esi,ecx; lea ebp,[esi+918h]
typedef void(__fastcall* Fp2Fn)(void* world, void* edx, void* obj);
static Fp2Fn g_ftOrig, g_uolOrig;
// the private-footprint ICs' calls, flushed first for an object with records: IC 0x754610 -> RemoveFootprintLocked 0x82A0C0
// thiscall(world; obj, flag), ret 8, al; IC 0x75DCF0 -> 0x831BA0 thiscall(world; obj, points, flag), ret 0xC, al [verified]
static const DWORD PFR_SITE = TS3A(0x0075465E), PFR_FN = TS3A(0x0082A0C0), PFA_SITE = TS3A(0x0075DE1A), PFA_FN = TS3A(0x00831BA0);
typedef u8(__fastcall* PfRemoveFn)(void* world, void* edx, void* obj, DWORD flag);
typedef u8(__fastcall* PfAddFn)(void* world, void* edx, void* obj, void* points, DWORD flag);
static PfRemoveFn g_pfRemove = (PfRemoveFn)(uintptr_t)PFR_FN;
static PfAddFn g_pfAdd = (PfAddFn)(uintptr_t)PFA_FN;
// docs/89 §14: the private footprint's lot key, 0x7CFA30 cdecl(2, 0x19, idLo, idHi) -> edx:eax, pure shifts and ors [verified
// 0x7CFA30..0x7CFA6B]; 0x82A0C0 removes it from the object's lot with 0x635700 [verified 0x82A0F6..0x82A133]
typedef unsigned __int64(__cdecl* PfKeyFn)(int kind, int type, DWORD idLo, DWORD idHi);
static PfKeyFn g_pfKey = (PfKeyFn)(uintptr_t)TS3A(0x007CFA30);

// flags kept as whole dwords; lock: the world it was made in; obj: the object at defer time (D-X pointer check, no reference)
// usf: a design U record (high = enable byte, allow = arg); fp2: a phase 2 record (1 FromTool, high = world +0x978 at the
// call; 2 UpdateObjectLot); pf: a private footprint remove (high = the IC's flag, docs/89 §14); else add says Add or Remove
// lr, lotLo, lotHi: a kind 2 record made with LostRemovalFix on and the object's lot at the call (docs/89 §16)
// fp2 3: UofDefer (docs/852): event 9's insert 0x833680, queued behind the object's records while a job runs (UofQueue)
// pfa (docs/670 §16): a private footprint ADD; pf is set too, so every "private record" rule holds for it (applied in order
// like a remove, never taken for D's own remove). pts / n = the caller's points, high = the IC's flag, lotLo / lotHi = the
// object's lot at the call (for the skew counter only)
struct DiscRec { bool add; DWORD high, allow, placement, idLo, idHi, t; int n; u8* pts; void* lock; void* obj; bool usf; u8 fp2; bool pf; bool lr; DWORD lotLo, lotHi; bool pfa; };

// [Discourage] DeferPrivateFpAdd (docs/670 §16). Exported block ts3native_pfadd: +0x00 mode is the runtime switch (native_set
// pfadd): 1 = the IC's add becomes a record while a job runs; 2 = probe (today's flush and call, then the object's lot is read
// again at the next drain point: how often a deferred add would have met a moved object)
struct PfAddCounters {
    volatile LONG paMode;        // +0x00
    volatile LONG paReady;       // +0x04
    volatile LONG paCalls;       // +0x08 sim-thread IC adds seen
    volatile LONG paInJob;       // +0x0C of those, while a job ran
    volatile LONG paDeferred;    // +0x10 records made (answered 1)
    volatile LONG paOrderDefer;  // +0x14 of those, with no job running (the object had a queued record)
    volatile LONG paApplied;     // +0x18 records applied with the game's 0x831BA0
    volatile LONG paDropped;     // +0x1C the object was gone or its id reused at the apply: no footprint
    volatile LONG paSkew;        // +0x20 applied, but the object's lot was another one than at the call
    volatile LONG paPass;        // +0x24 +0x918 held, a job callback, a drain, another world, closing: today's call
    volatile LONG paFlag0;       // +0x28 not deferred: flag 0 (the result depends on the object's lot), today's flush and call
    volatile LONG paShape;       // +0x2C not deferred: fewer than 3 points, more than kPfaMaxPts, no world manager
    volatile LONG paFull;        // +0x30 queue full
    LONG paAgeMaxMs;             // +0x34 oldest record at its apply
    volatile LONG paProbe;       // +0x38 mode 2: adds checked again at the next drain point
    volatile LONG paProbeMoved;  // +0x3C of those, the lot differed (or the object was gone)
    LONG paProbeMaxMs;           // +0x40 longest call-to-check time
    volatile LONG paWaitUs;      // +0x44 mode 0 / 2 and not-deferred calls: time in the flush before the add (the wait a deferral saves)
};
extern "C" __declspec(dllexport) PfAddCounters ts3native_pfadd = {};
enum { kPfaMaxPts = 64, kPfaProbes = 16 };
struct PfaProbe { DWORD idLo, idHi, lotLo, lotHi, t; void* obj; };
static PfaProbe g_pfaProbe[kPfaProbes];
static int g_pfaProbeN;  // owner thread only
static const DWORD* g_pfaWm = (const DWORD*)(uintptr_t)TS3A(0x011ECBC4);  // the world manager slot 0x831BA0 tests under its lock
static const int kDiscCap = 256;
static const DWORD kDiscMaxAgeMs = 250;  // older records are applied even if that means waiting for the running job
static DiscRec g_disc[kDiscCap];
static int g_discHead, g_discCount;  // owner (sim) thread only
static bool g_discDraining;
// UofDefer (below UofSite). Owner-thread statics logged by DiscRfLog, no counter fields (the ts3native_counters layout is unchanged).
static int g_udMode;
static LONG g_udQueued, g_udApplied, g_udDropped, g_udFull;
static void UofApply(void* world, void* obj);  // event 9's insert, the original 0x833680

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
    CeRec(CE_FP_QUEUE, (DWORD)(uintptr_t)r.obj, r.idLo, r.usf ? 1 : r.fp2 == 3 ? 6 : r.fp2 ? 2 + r.fp2 : r.pfa ? 7 : r.pf ? 5 : r.add ? 0x10 : 0);
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

// P's footprint map holds the id: one bucket, hash = key lo % bucket count [verified 0x6643E0]. Caller holds +0x918.
static bool FpgMapHas(u8* world, DWORD lo, DWORD hi) {
    __try {
        const u8* m = *(const u8* const*)(*(const u8* const*)(world + 0x95C) + 0x24);
        const u8* const* bk = *(const u8* const* const*)(m + 4);
        for (const u8* nd = bk[lo % *(const DWORD*)(m + 8)]; nd; nd = *(const u8* const*)(nd + 0x10))
            if (*(const DWORD*)nd == lo && *(const DWORD*)(nd + 4) == hi) return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
    return false;
}

// a gone id's private footprint: its lot key swept from every lot (the component went with the object)
static void DiscPfSweep(u8* world, DWORD idLo, DWORD idHi) {
    void* p = *(void**)(world + 0x95C);
    if (!p) return;
    const unsigned __int64 key = g_pfKey(2, 0x19, idLo, idHi);
    world[0x994] = 0;  // as 0x82A0C0 does before 0x635700
    g_lotRemove(p, NULL, 0, 0, (DWORD)key, (DWORD)(key >> 32));
}

// true = applied with the original
static bool DiscApplyOne(DiscRec& r, void* world) {
    UafTagScope uafTag(r.lr ? UT_LOST_REMOVAL : UT_DISC_REPLAY, _ReturnAddress());  // [UafCheck]: site = the drain / replay caller
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
    } else if (r.fp2 == 3) {  // UofDefer: vanilla's insert as if made now (0x833680 reads the object's lot itself); gone or reused id: dropped
        if (applied) UofApply(world, obj), g_udApplied++;
        else g_udDropped++;
        if (obj) g_objRelease(&obj, NULL);
    } else if (r.lr && (applied || !obj)) {  // LostRemovalFix: vanilla's call with the call's lot; a reused id goes to the branch below
        if (g_async.state == A_RUNNING) InterlockedIncrement(&k.fp2ApplyInJob);
        const u8 hit = g_uolCore(world, NULL, r.idLo, r.idHi, r.lotLo, r.lotHi);
        InterlockedIncrement(&k.fp2Applied[1]);
        if (applied) {
            InterlockedIncrement(&k.fp2lrApplied);
            const unsigned __int64 now = g_objLot(obj);
            if ((DWORD)now != r.lotLo || (DWORD)(now >> 32) != r.lotHi) InterlockedIncrement(&k.fp2lrMoved);
            g_objRelease(&obj, NULL);
        } else {
            InterlockedIncrement(&k.fp2lrGone);
            if (hit) {
                InterlockedIncrement(&k.fp2lrGoneHit);
                DiscPfSweep((u8*)world, r.idLo, r.idHi);  // the private key 0x82B551 removes with the object, which is gone here
            }
        }
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
                const unsigned __int64 lot = k.fp2lrReady ? g_objLot(obj) : 0;
                g_uolOrig(world, NULL, obj);
                // fix off: this remove went to the new lot, so the lot of the call keeps its copy
                if (k.fp2lrReady && ((DWORD)lot != r.lotLo || (DWORD)(lot >> 32) != r.lotHi)) InterlockedIncrement(&k.fp2lrLost);
            }
            InterlockedIncrement(&k.fp2Applied[r.fp2 - 1]);
        } else {
            InterlockedIncrement(&k.fp2Dropped[r.fp2 - 1]);
            if (r.fp2 == 2 && k.fp2lrReady && !obj && FpgMapHas((u8*)world, r.idLo, r.idHi)) InterlockedIncrement(&k.fp2lrLost);  // fix off: a ghost
        }
        if (obj) g_objRelease(&obj, NULL);
    } else if (r.pfa) {  // private footprint add (docs/670 §16): the game's call on the points of the call. Gone or a reused
                         // id: nothing (vanilla's footprint would have left with the object, 0x82B551)
        PfAddCounters& a = ts3native_pfadd;
        Max(&a.paAgeMaxMs, (LONG)(GetTickCount() - r.t));
        if (applied) {
            if (g_async.state == A_RUNNING) InterlockedIncrement(&k.pfApplyInJob);
            const unsigned __int64 lot = g_objLot(obj);
            if ((DWORD)lot != r.lotLo || (DWORD)(lot >> 32) != r.lotHi) InterlockedIncrement(&a.paSkew);
            u8* vec[3] = {r.pts, r.pts + r.n * 16, r.pts + r.n * 16};  // begin, end, capacity (0x831BA0 reads begin and end)
            g_pfAdd(world, NULL, obj, vec, r.high);
            InterlockedIncrement(&a.paApplied);
        } else {
            InterlockedIncrement(&a.paDropped);
        }
        if (obj) g_objRelease(&obj, NULL);
    } else if (r.pf) {  // private footprint remove: the original; gone: its lot key swept from every lot (the component went
                        // with the object); reused id: dropped
        if (applied) {
            if (g_async.state == A_RUNNING) InterlockedIncrement(&k.pfApplyInJob);
            g_pfRemove(world, NULL, obj, r.high);
            InterlockedIncrement(&k.pfApplied);
        } else if (!obj && k.discSweepMode && *(void**)((u8*)world + 0x95C)) {
            DiscPfSweep((u8*)world, r.idLo, r.idHi);
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
// site (DS_*, docs/247 §13.4 C): the drain point, for the per-site wait counters v192 (dsN / dsUs / dsYield)
static void PfaProbeCheck();
static void DiscDrain(bool force, int site) {
    Ts3nCounters& k = ts3native_counters;
    if (g_pfaProbeN && !g_discDraining && GetCurrentThreadId() == g_async.owner && (force || g_async.state != A_RUNNING)) PfaProbeCheck();
    if (!g_discCount || g_discDraining || GetCurrentThreadId() != g_async.owner) return;
    if (!force && g_async.state == A_RUNNING) return;
    const bool jobRan = g_async.state == A_RUNNING;
    const DWORD jobH = jobRan ? g_async.handle : 0;  // [HitchAttr] JobNameMs line (docs/247 §12.4)
    void* lock = g_rgLock;
    void* world = lock ? (u8*)lock - 0x918 : NULL;
    g_discDraining = true;
    InterlockedIncrement(&g_rgOffSimOpen);  // the worker starts no job until the drain is done
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    UdWait("the running route job (deferred footprints)");  // [UserDiag]: no-op unless this thread is saving
    g_swDs = site;  // docs/640: SwNote's >= 300 ms line names the drain point
    WaitAsyncDone(SW_DISC);
    g_swDs = -1;
    static const DWORD kInfinite = 0xFFFFFFFF;
    UdWait("+0x918 (deferred footprints)");
    if (lock) LockAcquireHook(lock, NULL, &kInfinite);  // uncontended: no job runs and none can start
    UdWait(NULL);
    const LONG waited = UsSince(t0);
    InterlockedExchangeAdd(&k.discDrainWaitUs, waited);
    HaNote(0x06000000, waited, HA_JOB);
    if (jobRan) RjnWait(0x06000000, waited, jobH);
    if (jobRan) {
        InterlockedIncrement(&k.discForced);
        const int ds = site >= 0 && site < DS_N ? site : DS_OTHER;
        InterlockedIncrement(&k.dsN[ds]);
        InterlockedExchangeAdd(&k.dsUs[ds], waited);
        if (CanYield()) InterlockedIncrement(&k.dsYield[ds]);  // the task sat at its root: a managed caller could have slept
    }
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
            if (r.fp2 == 1 || r.fp2 == 2) InterlockedIncrement(&k.fp2Dropped[r.fp2 - 1]);
            if (r.fp2 == 3) g_udDropped++;
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
static void DiscPoll(int site) {
    if (g_pfaProbeN && !g_discDraining && GetCurrentThreadId() == g_async.owner && g_async.state != A_RUNNING) PfaProbeCheck();
    if (!g_discCount) return;
    DiscDrain(GetTickCount() - g_disc[g_discHead].t >= kDiscMaxAgeMs, site);
}

// docs/640 [RouteAsync] DiscYieldForeign (default 0; no counter fields, the layout stays; log lines "disc yield"). A forced
// DiscDrain at our script-IC points (DiscPoll: the oldest record is >= kDiscMaxAgeMs old) blocks the sim thread until the
// running job ends (WaitAsyncDone, 300+ ms for an IP boat ReplanFromPosition foreign job). With the switch on, when that job is a
// foreign one (fb / af), the caller is known unseen (scope 2 or an UnseenFallback mark; unknown = household keeps the wait) and its
// task sits at its root interpreter state (CanYield), the drain is skipped instead: START parks (J_PEND, dy; the job is queued by
// READY once the records are applied, so it plans on the same footprints as after the wait), READY answers "not yet" or hands over a
// result the drain could not change (DyReadyMay). The records are applied by the first DiscPoll with no job running. Ceilings: an
// oldest record >= kDyMaxAgeMs old is drained with the wait as before, whoever asks; a parked START waits at most kDyTicks READY ticks.
//
// docs/670 §27 [RouteAsync] DiscYieldOwn (default 0; no counter fields; the same lines): the same skip when the running job is an
// own one (a Sim's Route.Plan job, most of a storm's jobs: s294-s309 have 67 of 81 READY disc waits behind one). Every rule above
// holds unchanged (known unseen caller at its task root, the 1 s ceiling, DyReadyMay at READY, the park at START); only the kind of
// the job that is waited out differs, and nothing in the park or in READY reads it. The two switches are independent.
static int g_dyMode, g_dyOwn;
static const DWORD kDyMaxAgeMs = 1000;
static LONG g_dyYield[DS_N], g_dyKept[3], g_dyTicksMax, g_dyCapped, g_dyAgeMaxMs;  // kept: 0 not unseen, 1 cannot yield, 2 age ceiling
static LONG g_dyOwnYield, g_dyReadyQueued;  // yields behind an own job; READY polls whose own job was queued (DyReadyMay: the wait stays)
static DWORD g_dyLogMin, g_dyLogLines;
static DWORD DiscOldestAge() { return g_discCount ? GetTickCount() - g_disc[g_discHead].t : 0; }
// owner thread, at a START / A10 START / READY point right before its DiscPoll: true = skip the forced drain (the caller yields).
// unseen: the caller's route is known unseen (scope 2 or an UnseenFallback mark)
static bool DiscYieldOk(bool unseen, int site) {
    if ((g_dyMode != 1 && g_dyOwn != 1) || !g_discCount || g_discDraining || GetCurrentThreadId() != g_async.owner) return false;
    if (g_async.state != A_RUNNING) return false;  // no wait
    const bool own = g_jobOwn != 0;  // read once: the worker clears it at the job's end
    if (own ? g_dyOwn != 1 : g_dyMode != 1) return false;  // DiscYieldForeign: a foreign job; DiscYieldOwn (docs/670 §27): an own one
    const DWORD age = DiscOldestAge();
    if (age < kDiscMaxAgeMs) return false;  // DiscPoll would not wait either
    // household, selected and unknown scope keep the wait (HhExempt; [Experimental] HouseholdToo=1 lifts it: the same plan, later)
    const int kept = HhExempt(!unseen) ? 0 : !CanYield() ? 1 : age >= kDyMaxAgeMs ? 2 : -1;
    if (kept >= 0) { g_dyKept[kept]++; return false; }
    g_dyYield[site >= 0 && site < DS_N ? site : DS_OTHER]++;
    if (own) g_dyOwnYield++;
    if ((LONG)age > g_dyAgeMaxMs) g_dyAgeMaxMs = (LONG)age;
    const DWORD now = GetTickCount();
    if (now - g_dyLogMin >= 60000) g_dyLogMin = now, g_dyLogLines = 0;
    if (g_dyLogLines++ < 10)
        Log("disc yield: %s skipped a forced drain (oldest record %lu ms, %d queued) while %s job %08lX runs", site == DS_READY ? "READY" :
            site == DS_A10START ? "A10 START" : "START", age, g_discCount, own ? "own" : "foreign", g_async.handle);
    return true;
}
// READY of a caller whose job is still queued, right before its DiscPoll: counted when that poll will wait out a job a yield
// switch covers (the drain is what keeps the worker from claiming the queued job past the old records, docs/640 §2)
static void DiscYieldQueuedNote() {
    if (!g_discCount || g_discDraining || g_async.state != A_RUNNING || DiscOldestAge() < kDiscMaxAgeMs) return;
    if (g_jobOwn ? g_dyOwn == 1 : g_dyMode == 1) g_dyReadyQueued++;
}
// one READY tick of a START parked by it (DiscPoll ran just before): true = keep waiting (a forced drain is still due)
static bool DiscYieldWait(LONG ticks) {
    const bool due = g_discCount && DiscOldestAge() >= kDiscMaxAgeMs;
    if (due && ticks < kDyTicks) return true;
    if (due) g_dyCapped++;
    if (ticks > g_dyTicksMax) g_dyTicksMax = ticks;
    return false;
}
static void DiscYieldLog() {
    if (g_dyMode || g_dyOwn || g_dyYield[DS_START] + g_dyYield[DS_A10START] + g_dyYield[DS_READY])
        Log("disc yield: mode %d yields START %ld A10 START %ld READY %ld, kept not unseen %ld no yield %ld age cap %ld, parked capped %ld "
            "max ticks %ld, max age %ld ms; own mode %d: %ld of the yields behind an own job, READY kept for a queued job %ld",
            g_dyMode, g_dyYield[DS_START], g_dyYield[DS_A10START], g_dyYield[DS_READY], g_dyKept[0], g_dyKept[1], g_dyKept[2], g_dyCapped,
            g_dyTicksMax, g_dyAgeMaxMs, g_dyOwn, g_dyOwnYield, g_dyReadyQueued);
}

// RemovalFirst (docs/89 §18, [Discourage] RemovalFirst=1, 0 = off; no counter fields, the hotfix keeps the layout; log line
// "footprint removal first"): a START that finds a removal queued (a job runs, so DiscPoll left it) would plan past it: the worker
// takes the next job at once and nothing drains between two jobs. A removal a Sim's own script made just before (a region's
// Cleanup, then the next route) then blocks that plan: result 16, a walk-around, the household Sim leaves the stove. AsyncStart
// parks such a START (J_PEND, READY ticks; the sim thread does not wait) until DiscPoll has applied it: at the first poll with
// no job, or at kDiscMaxAgeMs by the wait that poll does anyway. Sims known unseen keep today's behaviour. usf and kind 1 records
// do not count: they refill every tick in a crowd and only move a footprint. shortcut: a refused START's sync plan is not
// covered ([Discourage] PlanDrain=1 would, not verified live); use it if household sync plans show this shape.
static int g_rfMode;
static LONG g_rfParked, g_rfCapped, g_rfTicksMax;
static bool DiscRemovalQueued() {
    for (int i = 0; i < g_discCount; i++) {
        const DiscRec& r = g_disc[(g_discHead + i) % kDiscCap];
        if (r.fp2 == 2 || (r.pf ? !r.pfa : (!r.usf && !r.fp2 && !r.add))) return true;  // a private add (docs/670 §16) is no removal
    }
    return false;
}
static bool DiscRfStart() {
    if (g_rfMode != 1 || !DiscRemovalQueued()) return false;
    g_rfParked++;
    return true;
}
// one READY tick of such a START (DiscPoll ran just before): true = wait; false = go on, at the tick cap with the removal queued
static bool DiscRfWait(LONG ticks, LONG cap) {
    const bool queued = DiscRemovalQueued();
    if (queued && ticks < cap) return true;
    if (queued) g_rfCapped++;
    if (ticks > g_rfTicksMax) g_rfTicksMax = ticks;
    return false;
}
static void DiscRfLog() {
    if (g_udMode || g_udQueued) Log("uof defer: mode %d queued %ld applied %ld dropped %ld full %ld", g_udMode, g_udQueued, g_udApplied, g_udDropped, g_udFull);
    if (g_rfMode || g_rfParked) Log("footprint removal first: mode %d parked %ld capped %ld max ticks %ld", g_rfMode, g_rfParked, g_rfCapped, g_rfTicksMax);
    DiscYieldLog();
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
    DiscDrain(true, DS_PLAN);
    const LONG us = UsSince(t0);
    InterlockedIncrement(&k.sdDrains);
    InterlockedExchangeAdd(&k.sdRecs, n);
    if (ran) InterlockedIncrement(&k.sdJobRan);
    InterlockedExchangeAdd(&k.sdUs, us);
    Max(&k.sdMaxUs, us);
    return 1;
}

// The object's records in order under +0x918, the others kept in their order. Caller: no job runs and none can start. destroy:
// its Add records go (D-X). Else they stay queued: an Add may fire World_FireEvent 0x11, IC points only.
static void DiscApplyFor(DWORD idLo, DWORD idHi, int n0, bool destroy) {
    Ts3nCounters& k = ts3native_counters;
    void* lock = g_rgLock;
    void* world = (u8*)lock - 0x918;
    static const DWORD kInfinite = 0xFFFFFFFF;
    g_discDraining = true;  // no drain inside; a record made meanwhile (none expected: Remove has no managed reach) goes last
    LockAcquireHook(lock, NULL, &kInfinite);  // uncontended: no job runs and none starts while the destroy is open
    int w = 0;
    for (int i = 0; i < n0; i++) {
        DiscRec& r = g_disc[(g_discHead + i) % kDiscCap];
        if (r.idLo != idLo || r.idHi != idHi || r.lock != lock || (r.add && !destroy)) {
            if (w != i) { g_disc[(g_discHead + w) % kDiscCap] = r; r.pts = NULL; }
            w++;
        } else if (r.usf || r.fp2 || r.pf) {  // design U, phase 2, private remove: applied in order like a Remove
            if (DiscApplyOne(r, world) && r.usf && destroy) InterlockedIncrement(&k.usfDestroyApplied);
        } else if (r.add) {
            InterlockedIncrement(&k.discDropAdd);
            InterlockedIncrement(&k.discDropped);
            _aligned_free(r.pts);
            r.pts = NULL;
        } else if (DiscApplyOne(r, world) && destroy) {
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
    DiscApplyFor(idLo, idHi, n0, true);
}

// true = recorded (the caller answers 1)
static bool DiscDefer(bool add, void* obj, const u8* begin, int n, int high, int allow, int placement) {
    Ts3nCounters& k = ts3native_counters;
    if (k.discMode != 1 || GetCurrentThreadId() != g_async.owner) return false;
    if (!g_discDraining && g_async.state != A_RUNNING) return false;  // no job: apply now (after older records)
    if (g_discCount == kDiscCap) { InterlockedIncrement(&k.discOverflow); return false; }
    DiscRec& r = g_disc[(g_discHead + g_discCount) % kDiscCap];
    r = DiscRec{};  // #408: as the other writers; a slot last used by a kind 2 record kept its lr and lot
    r.add = add;
    r.high = (DWORD)high;
    r.allow = (DWORD)allow;
    r.placement = (DWORD)placement;
    r.idLo = *(const DWORD*)((const u8*)obj + 8);
    r.idHi = *(const DWORD*)((const u8*)obj + 0xC);
    r.t = GetTickCount();
    r.n = n;
    r.lock = g_rgLock;
    r.obj = obj;
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
    DiscDrain(true, DS_ADD);  // older records first (no job runs, or the queue is full)
    return g_discAddOrig(world, edx, obj, points, high, allow, placement);
}

static u8 __fastcall DiscRemoveHook(void* world, void* edx, void* obj) {
    if (t_snapRun || !DiscWorldOk(world) || !obj) return g_discRemoveOrig(world, edx, obj);  // early outs, P4 clone run
    if (DiscDefer(false, obj, NULL, 0, 0, 0, 0)) return 1;
    DiscDrain(true, DS_REMOVE);
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
        if (!add) DiscDrain(true, DS_USF);
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
//     direct hops; the body only removes (docs/89 §16 corrects the old "re-home"). Event 9's insert is 0x833680, see UofSite.
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
    const bool lr = kind == 2 && k.fp2lrMode == 1;
    const unsigned __int64 lot = kind == 2 && k.fp2lrReady ? g_objLot(obj) : 0;  // the lot 0x82D280 would read now; with the fix off only for fp2lrLost
    if (last >= 0) {  // the same call again right behind, on the same lot: twice = once
        const DiscRec& p = g_disc[(g_discHead + last) % kDiscCap];
        if (p.fp2 == kind && p.obj == obj && p.high == br && p.lr == lr && p.lotLo == (DWORD)lot && p.lotHi == (DWORD)(lot >> 32)) {
            InterlockedIncrement(&k.fp2Dup);
            return;
        }
    }
    if (g_discCount == kDiscCap) {  // as design U: drain unless an Add (World_FireEvent 0x11) is queued
        bool add = false;
        for (int i = 0; i < g_discCount && !add; i++) add = g_disc[(g_discHead + i) % kDiscCap].add;
        if (!add) DiscDrain(true, DS_FP2);
        InterlockedIncrement(add ? &k.fp2FullPass : &k.fp2Forced);
        return Fp2Call(kind, world, obj);
    }
    DiscRec& r = g_disc[(g_discHead + g_discCount) % kDiscCap];
    r = DiscRec{};
    r.fp2 = (u8)kind;
    r.high = br;
    r.lr = lr;
    r.lotLo = (DWORD)lot;
    r.lotHi = (DWORD)(lot >> 32);
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

// docs/89 §16: 0x833680 thiscall(world; obj), ret 4, void [verified 0x833680..0x8336E9], called only from event 9's 0x946160 (two
// sites). It inserts the object's footprints, so a queued remove of the object goes first, as vanilla's order has it; the call
// takes +0x918 next anyway, so the job wait is not new. shortcut: with +0x918 held here or inside a job callback there is no
// flush (fp2lrOrderMiss, expected 0, and for a queued Add of the object, which stays behind), and the late remove takes this
// insert; make the record redo the insert if it ever counts.
static const DWORD UOF_FN = TS3A(0x00833680), UOF_SITE1 = TS3A(0x009462D4), UOF_SITE2 = TS3A(0x0094630A);
typedef void(__fastcall* UofFn)(void* world, void* edx, void* obj);
static UofFn g_uof = (UofFn)(uintptr_t)UOF_FN;
// site (docs/247 §13.4 C, counters v192 lrs*): 0 = the call at UOF_SITE1, 1 = UOF_SITE2 (event 9's 0x946160)
static void UolFlushFor(u8* world, const u8* obj, int site) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD me = GetCurrentThreadId();
    if (!obj || !g_discCount || g_discDraining || me != g_async.owner || t_snapRun || (void*)(world + 0x918) != g_rgLock || !DiscWorldOk(world)) return;
    const DWORD idLo = *(const DWORD*)(obj + 8), idHi = *(const DWORD*)(obj + 0xC);
    const int n0 = g_discCount;
    bool mine = false, add = false;
    for (int i = 0; i < n0; i++) {
        const DiscRec& r = g_disc[(g_discHead + i) % kDiscCap];
        if (r.idLo != idLo || r.idHi != idHi || r.lock != g_rgLock) continue;
        mine |= r.lr || r.fp2 == 3;  // UofDefer: a queued insert of it goes first too (no job: no wait)
        add |= r.add;
    }
    if (!mine) return;
    if (g_inInvoke || k.rgOwnerTid == (LONG)me) { InterlockedIncrement(&k.fp2lrOrderMiss); return; }
    if (add) InterlockedIncrement(&k.fp2lrOrderMiss);
    InterlockedIncrement(&g_rgOffSimOpen);  // no job starts until the flush is done
    const LONG us0 = k.fp2lrFlushUs;  // sim thread only adds to it
    RtWaitJob(&k.fp2lrFlushUs, 0x02000000 | 0x40);
    const LONG waited = k.fp2lrFlushUs - us0;
    InterlockedIncrement(&k.lrsN[site]);
    InterlockedExchangeAdd(&k.lrsUs[site], waited);
    if (CanYield()) InterlockedIncrement(&k.lrsYieldN), InterlockedExchangeAdd(&k.lrsYieldUs, waited);
    DiscApplyFor(idLo, idHi, n0, false);
    RgOffSimDrop();
    InterlockedIncrement(&k.fp2lrFlush);
}
static void UofApply(void* world, void* obj) { g_uof(world, NULL, obj); }

// UofDefer (docs/852, [Discourage] UofDefer=1, default 1 since 2026-11-19; needs LostRemovalFix). Bridgeport: the sim thread waited 10.2 s a game at
// "rtwait", most of it here: event 9 queues the object's remove (kind 2) while a job runs, then this insert flushes it, waiting for
// the job (UolFlushFor), or 0x833680 waits for +0x918 itself. With the switch on, the insert becomes a kind 3 record behind the
// object's records instead, as Fp2Hook's records: same pass-through rules, FIFO per object (the drain runs remove then insert,
// vanilla's order), applied at D's drain points and flushes (destroy, private-footprint and Fp2FlushFor readers, save / travel /
// quit). The new footprint shows up at the next drain, the staleness phase 2 already accepts. Two inserts of one object are not
// merged: vanilla inserts twice too. true = queued.
static bool UofQueue(u8* world, u8* obj) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD me = GetCurrentThreadId();
    if (me != g_async.owner || t_snapRun || !obj || g_async.state != A_RUNNING) return false;  // no job: today's path, no wait
    if (k.rgOwnerTid == (LONG)me || g_inInvoke || g_discDraining || !DiscWorldOk(world) || (void*)(world + 0x918) != g_rgLock || k.rtClosing)
        return false;
    if (g_discCount == kDiscCap) { g_udFull++; return false; }  // today's path: the flush waits as before
    DiscRec& r = g_disc[(g_discHead + g_discCount) % kDiscCap];
    r = DiscRec{};
    r.fp2 = 3;
    r.idLo = *(const DWORD*)(obj + 8);
    r.idHi = *(const DWORD*)(obj + 0xC);
    r.t = GetTickCount();
    r.lock = g_rgLock;
    r.obj = obj;
    g_discCount++, CeDiscQ(r);
    g_udQueued++;
    Max(&k.discPendingMax, g_discCount);
    return true;
}

static void __fastcall UofSite(u8* world, void*, u8* obj) {
    // our call replaced EA's `call 0x833680` at UOF_SITE1 / UOF_SITE2, so the return address names the site
    if (ts3native_counters.fp2lrMode == 1 && g_udMode == 1 && UofQueue(world, obj)) return;
    if (ts3native_counters.fp2lrMode == 1) UolFlushFor(world, obj, (DWORD)(uintptr_t)_ReturnAddress() == UOF_SITE2 + 5 ? 1 : 0);
    g_uof(world, NULL, obj);
}

// flush before a reader that needs the object's fresh footprints (an IC point: managed code is on the stack); true = flushed
static bool Fp2FlushFor(const u8* obj) {
    const DWORD me = GetCurrentThreadId();
    if (!obj || !g_discCount || g_discDraining || me != g_async.owner || ts3native_counters.rgOwnerTid == (LONG)me) return false;
    if (DiscLastOf(*(const DWORD*)(obj + 8), *(const DWORD*)(obj + 0xC)) < 0) return false;
    DiscDrain(true, DS_FP2READ);
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
        if (p.pf && !p.pfa && p.obj == obj && p.high == flag) { InterlockedIncrement(&k.pfDup); return 1; }
    }
    if (g_discCount == kDiscCap) {  // as design U: drain unless an Add (World_FireEvent 0x11) is queued, then the call now
        bool add = false;
        for (int i = 0; i < g_discCount && !add; i++) add = g_disc[(g_discHead + i) % kDiscCap].add;
        if (!add) DiscDrain(true, DS_PF);
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
// docs/670 §16 ([Discourage] DeferPrivateFpAdd=1, switch pfadd): the IC's 0x831BA0 call (0x75DE1A) on the sim thread while a job
// runs becomes a record in D's FIFO with a copy of the caller's points, answered 1. 0x831BA0 [audit: decompile + disassembly,
// docs/670 §16.1]: returns 0 before its lock for a closed world, no object or fewer than 3 points; under +0x918 it returns 0
// with no world manager, and for an object on no lot / the active lot / an unknown lot ONLY when the flag byte is 0; every
// other path ends in `return 1`. So with a non-zero flag (the script's boat case) the answer is 1 whatever the lot says, and
// the record is exact in its result; with flag 0 the call is not deferred. Same skeleton as PfRemoveDefer: pass-through
// rules, order rule, queue full, applied at D's drain points in FIFO order and at D-X destroy. Returns -1 = not deferred.
static int PfAddDefer(u8* world, u8* obj, const DWORD* points, DWORD flag) {
    Ts3nCounters& k = ts3native_counters;
    PfAddCounters& a = ts3native_pfadd;
    const DWORD me = GetCurrentThreadId();
    if (me != g_async.owner || t_snapRun || !obj || !points) return -1;
    InterlockedIncrement(&a.paCalls);
    const bool running = g_async.state == A_RUNNING;
    if (running) InterlockedIncrement(&a.paInJob);
    if (a.paMode != 1) return -1;
    if (k.rgOwnerTid == (LONG)me || g_inInvoke || g_discDraining || !DiscWorldOk(world) || (void*)(world + 0x918) != g_rgLock ||
        k.rtClosing) {
        InterlockedIncrement(&a.paPass);
        return -1;
    }
    const DWORD idLo = *(const DWORD*)(obj + 8), idHi = *(const DWORD*)(obj + 0xC);
    if (DiscLastOf(idLo, idHi) < 0 && !running) return -1;  // today's call: nothing queued for the object, no job to wait for
    const DWORD n = (points[1] - points[0]) >> 4;  // unsigned, as 0x831BD0
    if (n < 3 || n > (DWORD)kPfaMaxPts || !points[0] || !*g_pfaWm) { InterlockedIncrement(&a.paShape); return -1; }
    if (!(flag & 0xFF)) { InterlockedIncrement(&a.paFlag0); return -1; }
    if (g_discCount == kDiscCap) { InterlockedIncrement(&a.paFull); return -1; }  // PfAddSite's flush drains (or the add waits as today)
    u8* pts = (u8*)_aligned_malloc((size_t)n * 16, 16);
    if (!pts) { InterlockedIncrement(&a.paFull); return -1; }
    memcpy(pts, (const void*)(uintptr_t)points[0], (size_t)n * 16);
    const unsigned __int64 lot = g_objLot(obj);
    DiscRec& r = g_disc[(g_discHead + g_discCount) % kDiscCap];
    r = DiscRec{};
    r.pf = r.pfa = true;
    r.high = flag;
    r.n = (int)n;
    r.pts = pts;
    r.lotLo = (DWORD)lot;
    r.lotHi = (DWORD)(lot >> 32);
    r.idLo = idLo;
    r.idHi = idHi;
    r.t = GetTickCount();
    r.lock = g_rgLock;
    r.obj = obj;
    g_discCount++, CeDiscQ(r);
    InterlockedIncrement(&a.paDeferred);
    if (!running) InterlockedIncrement(&a.paOrderDefer);
    Max(&k.discPendingMax, g_discCount);
    return 1;
}

// mode 2: the add ran as today; remember the object's lot for the next drain point
static void PfaProbeNote(u8* obj) {
    if (g_pfaProbeN == kPfaProbes || GetCurrentThreadId() != g_async.owner) return;
    const unsigned __int64 lot = g_objLot(obj);
    g_pfaProbe[g_pfaProbeN++] = PfaProbe{*(const DWORD*)(obj + 8), *(const DWORD*)(obj + 0xC), (DWORD)lot, (DWORD)(lot >> 32), GetTickCount(), obj};
}
// owner thread, a drain point with no job running or about to be waited for: where a record of the add would be applied
static void PfaProbeCheck() {
    PfAddCounters& a = ts3native_pfadd;
    void* svc = g_objSvc();
    const DWORD now = GetTickCount();
    for (int i = 0; i < g_pfaProbeN; i++) {
        const PfaProbe& p = g_pfaProbe[i];
        void* obj = NULL;
        if (!svc || !g_findRef(svc, NULL, p.idLo, p.idHi, &obj)) obj = NULL;
        bool moved = !obj || obj != p.obj;
        if (!moved) {
            const unsigned __int64 lot = g_objLot(obj);
            moved = (DWORD)lot != p.lotLo || (DWORD)(lot >> 32) != p.lotHi;
        }
        if (obj) g_objRelease(&obj, NULL);
        InterlockedIncrement(&a.paProbe);
        if (moved) InterlockedIncrement(&a.paProbeMoved);
        Max(&a.paProbeMaxMs, (LONG)(now - p.t));
    }
    g_pfaProbeN = 0;
}

static u8 __fastcall PfAddSite(void* world, void*, u8* obj, void* points, DWORD flag) {
    PfAddCounters& a = ts3native_pfadd;
    if (a.paReady) {
        const int r = PfAddDefer((u8*)world, obj, (const DWORD*)points, flag);
        if (r >= 0) return (u8)r;
    }
    // not deferred: a queued record of the object is flushed first, so the add waits as today
    LARGE_INTEGER t0 = {};
    QueryPerformanceCounter(&t0);
    const bool flushed = Fp2FlushFor(obj);
    if (flushed && ts3native_counters.pfReady) InterlockedIncrement(&ts3native_counters.pfAddFlush);
    if (flushed && a.paReady) InterlockedExchangeAdd(&a.paWaitUs, UsSince(t0));
    const u8 res = g_pfAdd(world, NULL, obj, points, flag);
    if (a.paMode == 2 && res && obj && GetCurrentThreadId() == g_async.owner) PfaProbeNote(obj);
    return res;
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
    if (!GetPrivateProfileIntW(L"Discourage", L"DeferPrivateFp", 0, ini)) {
        Log("private footprint deferral off (DeferPrivateFp=0)");
        if (GetPrivateProfileIntW(L"Discourage", L"DeferPrivateFpAdd", 0, ini)) Log("SKIP private footprint add deferral: needs DeferPrivateFp=1");
        return;
    }
    // phase 2's detours keep FromTool / UpdateObjectLot calls of the object behind its queued remove (order rule, also with
    // fp2Mode 0: DeferFootprint2=2)
    if (!k.fp2Ready) { Log("FAIL private footprint deferral: needs design U phase 2 ([Discourage] DeferFootprint2=1 or 2)"); return; }
    if (!PfSitesInstall()) { Log("FAIL private footprint deferral: call sites"); return; }
    k.pfReady = 1;
    k.pfMode = 1;
    Log("OK   private footprint deferral (IC RemovePrivateFootprint 0x75465E -> 0x82A0C0; docs/89 §14)");
    // docs/670 §16: the add, on the same call-site pair. Needs the remove's deferral: the script's remove of the object must be
    // able to queue behind a queued add, or it would flush (wait) instead
    PfAddCounters& a = ts3native_pfadd;
    const LONG mode = Clamp(GetPrivateProfileIntW(L"Discourage", L"DeferPrivateFpAdd", 0, ini), 0, 2);
    if (!mode) { Log("private footprint add deferral off (DeferPrivateFpAdd=0)"); return; }
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    a.paMode = mode;
    a.paReady = 1;
    Log("OK   private footprint add deferral (IC AddPrivateFootprint 0x75DE1A -> 0x831BA0; docs/670 §16; mode %ld: %s)", mode,
        mode == 1 ? "defer while a job runs" : "probe only");
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void PfaLogCounters() {
    const PfAddCounters& a = ts3native_pfadd;
    if (!a.paReady) return;
    Log("private footprint add: mode %ld calls %ld (in a job %ld): deferred %ld (no job, order rule %ld), applied %ld, object gone %ld, lot "
        "moved %ld, oldest %ld ms; not deferred: pass %ld, flag 0 %ld, shape %ld, queue full %ld; flush wait before an add %ld ms; probe "
        "%ld moved %ld (longest %ld ms)",
        a.paMode, a.paCalls, a.paInJob, a.paDeferred, a.paOrderDefer, a.paApplied, a.paDropped, a.paSkew, a.paAgeMaxMs, a.paPass, a.paFlag0,
        a.paShape, a.paFull, a.paWaitUs / 1000, a.paProbe, a.paProbeMoved, a.paProbeMaxMs);
}

#if defined(TS3N_EA_PORT) || defined(TS3A_RETAIL)
// past the detour bytes, inside UpdateObjectLot: a direct call to the target (v0.2.10 EA, 55a098af; EA and retail builds + selftest only)
static bool UolCalls(DWORD fn, DWORD target) {
    const DWORD from = fn + 0x10, to = fn + 0x50;
    if (!Readable((const void*)(uintptr_t)from, to - from + 5)) return false;
    for (DWORD a = from; a < to; a++) {
        const u8* p = (const u8*)(uintptr_t)a;
        if (p[0] == 0xE8 && (DWORD)(a + 5 + *(const LONG*)(p + 1)) == target) return true;
    }
    return false;
}
#endif

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
    if (!GetPrivateProfileIntW(L"Discourage", L"LostRemovalFix", 1, ini)) { Log("lost removal fix off (LostRemovalFix=0)"); return; }
#ifdef TS3A_TABLE
    // no byte check covers g_uolCore and g_objLot, so a wrong table row on another build must stop here: UpdateObjectLot calls both
    // [verified Steam 0x82D2B0, 0x82D2BE]; the Steam asi has no such check (the fold keeps it identical)
    if (!UolCalls(UOL_FN, (DWORD)(uintptr_t)g_objLot) || !UolCalls(UOL_FN, (DWORD)(uintptr_t)g_uolCore)) {
        Log("FAIL lost removal fix: UpdateObjectLot at 0x%08X does not call 0x%08X and 0x%08X, fix off", UOL_FN, (DWORD)(uintptr_t)g_objLot, (DWORD)(uintptr_t)g_uolCore);
        return;
    }
#endif
    static const CallSite kUof[] = {{UOF_SITE1, UOF_FN, (void*)UofSite}, {UOF_SITE2, UOF_FN, (void*)UofSite}};
    if (!PatchCalls("0x833680 order sites (docs/89 §16)", kUof, 2)) { Log("FAIL lost removal fix: order sites"); return; }
    k.fp2lrReady = 1;
    k.fp2lrMode = 1;
    Log("OK   lost removal fix: queued UpdateObjectLot records keep the lot of the call and always reach the lot map (docs/89 §16)");
    g_udMode = GetPrivateProfileIntW(L"Discourage", L"UofDefer", 0, ini) == 1 ? 1 : 0;
    if (g_udMode) Log("OK   uof defer: event 9's insert 0x833680 is queued behind the object's records while a job runs (UofDefer=1)");
}

// --- GhostSweep (docs/89 §17): dead object footprints in P's footprint map ---
// Layout [verified 0x6643E0, 0x635700..0x635850]: map M = [P+0x24], buckets +4, bucket count +8, hash = key lo % count; node key +0,
// FME +8, next +0x10; FME Q +0xC (0 = on its way out), footprint +8 [docs/94 §11.4]. "No object" alone says nothing: a clean Baseline
// has 86954 of 126787 entries without one (member class 0xFEBF90, 0xFEBA98, ids like 0000000002E31605, 00001179038B0C05). The
// signature, all of it: object-shaped key (low nibble 0 [observed on every object id so far]; 0x7CFA30 keys have bit 0 set
// [verified]), class 0xFEB848 (the object footprint, id +0x14 / +0x18 = the key, type byte +0x35 = 1), Q set, not parked, no object
// under the id. It decides nothing in count-only mode; whether it is tight is a measurement: count ~0 on a clean save, the made
// ghost's id on a made ghost. Keys of object shape outside the signature only land in the per-class line. Any failed read keeps
// the entry.
static const DWORD kFpgFpClass = TS3A(0x00FEB848);
static u8* const* g_fpgWorldSlot = (u8* const*)(uintptr_t)TS3A(0x011D9128);  // RoutingWorld_Get 0x836200: at load g_rgLock is not known yet
struct FpgId { DWORD lo, hi, flags; const void* q; };
struct FpgClass { DWORD cls; LONG n; };
static const int kFpgMax = 1024;  // shortcut: more ghosts than this wait for the next load
static const int kFpgLogMax = 200;
static FpgClass g_fpgClass[12];    // object-shaped keys without an object outside the signature, by class; last slot = the rest
static bool g_fpgTest;

static bool FpgNoObject(DWORD lo, DWORD hi) {  // false also when the lookup itself cannot answer
    void* svc = g_objSvc();
    void* obj = NULL;
    if (!svc) return false;
    if (!g_findRef(svc, NULL, lo, hi, &obj)) return true;
    if (obj) g_objRelease(&obj, NULL);
    return !obj;
}

static void FpgClassNote(DWORD cls) {
    InterlockedIncrement(&ts3native_counters.fpgOther);
    const int last = (int)(sizeof(g_fpgClass) / sizeof(g_fpgClass[0])) - 1;
    int i = 0;
    while (i < last && g_fpgClass[i].n && g_fpgClass[i].cls != cls) i++;
    g_fpgClass[i].cls = i < last ? cls : 0;
    g_fpgClass[i].n++;
}

// one entry's verdict: 1 ghost, 0 not, -1 kept on a bad shape
static int FpgEntry(const u8* nd, u8* world, FpgId* g) {
    Ts3nCounters& k = ts3native_counters;
    const u8* fme = *(const u8* const*)(nd + 8);
    const u8* fp = fme ? *(const u8* const*)(fme + 8) : NULL;
    if (!fp) return -1;
    const DWORD lo = *(const DWORD*)nd, hi = *(const DWORD*)(nd + 4);
    if ((lo & 0xF) || !(lo | hi) || !hi) return 0;  // not object-shaped
    InterlockedIncrement(&k.fpgObjFp);
    if (!FpgNoObject(lo, hi)) return 0;
    const DWORD cls = *(const DWORD*)fp;
    const void* q = *(const void* const*)(fme + 0xC);
    if (cls != kFpgFpClass || *(const DWORD*)(fp + 0x14) != lo || *(const DWORD*)(fp + 0x18) != hi || *(const signed char*)(fp + 0x35) != 1 || !q ||
        DiscKeyMapHas(world, lo, hi)) {
        FpgClassNote(cls);
        return 0;
    }
    g->lo = lo, g->hi = hi, g->flags = *(const DWORD*)(fme + 0x10), g->q = q;
    return 1;
}

// the whole map, one bucket chain at a time; -1 = the walk itself failed, nothing it found is trusted
static int FpgWalk(u8* world, FpgId* out, int cap) {
    Ts3nCounters& k = ts3native_counters;
    int n = 0;
    __try {
        const u8* p = *(const u8* const*)(world + 0x95C);
        const u8* m = p ? *(const u8* const*)(p + 0x24) : NULL;
        const u8* const* bk = m ? *(const u8* const* const*)(m + 4) : NULL;
        const DWORD count = m ? *(const DWORD*)(m + 8) : 0;
        if (!bk || !count) return -1;
        for (DWORD i = 0; i < count; i++) {
            for (const u8* nd = bk[i]; nd; nd = *(const u8* const*)(nd + 0x10)) {
                k.fpgEntries++;
                FpgId g = {};
                int v;
                __try { v = FpgEntry(nd, world, &g); } __except (EXCEPTION_EXECUTE_HANDLER) { v = -1; }
                if (v < 0) InterlockedIncrement(&k.fpgKeptRead);
                if (v <= 0) continue;
                bool dup = false;  // one node per level
                for (int j = 0; j < n && !dup; j++) dup = out[j].lo == g.lo && out[j].hi == g.hi;
                if (!dup && n < cap) out[n++] = g;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
    return n;
}

// Rechecks at the removal. Caller holds +0x918 with no job running.
static bool FpgRemove(u8* world, DWORD lo, DWORD hi) {
    Ts3nCounters& k = ts3native_counters;
    void* p = *(void**)(world + 0x95C);
    if (!p || !FpgNoObject(lo, hi) || DiscKeyMapHas(world, lo, hi)) return false;
    InterlockedIncrement(&k.fpgRemoved);
    UafTagScope uafTag(UT_GHOST_SWEEP, _ReturnAddress());  // [UafCheck]
    g_lotRemove(p, NULL, 0, 0, lo, hi);
    world[0x994] = 0;  // as Remove does after 0x635700
    DiscPfSweep(world, lo, hi);
    return true;
}

// One pass inside the load screen (userdiag.cpp UdWorldLoad, the ts3fixes "world:" line, sim thread, OnWorldLoadFinished). Never
// during play: a live census would need a per-tick node budget, a walk by bucket index and the recheck FpgRemove already has.
static void FpgCensus(int loadNo) {
    Ts3nCounters& k = ts3native_counters;
    const LONG mode = k.fpgMode;
    if (!mode) return;
    u8* world = *g_fpgWorldSlot;
    if (!DiscWorldOk(world)) { Log("ghost census: world load #%d skipped, no routing world", loadNo); return; }
    void* lock = world + 0x918;
    DiscDrain(true, DS_FPGCENSUS);
    InterlockedIncrement(&g_rgOffSimOpen);  // no job starts during the pass
    if (g_async.state == A_RUNNING && GetCurrentThreadId() != g_async.owner) {
        RgOffSimDrop();
        Log("ghost census: world load #%d skipped, a job runs and this is not its owner thread", loadNo);
        return;
    }
    WaitAsyncDone(SW_DISC);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    static const DWORD kInfinite = 0xFFFFFFFF;
    LockAcquireHook(lock, NULL, &kInfinite);
    k.fpgEntries = 0, k.fpgObjFp = 0, k.fpgOther = 0, k.fpgKeptRead = 0;
    memset(g_fpgClass, 0, sizeof(g_fpgClass));
    static FpgId s_gh[kFpgMax];  // sim thread, one pass at a time
    const int n = FpgWalk(world, s_gh, kFpgMax);
    int removed = 0;
    for (int i = 0; i < n && mode == 2; i++) removed += FpgRemove(world, s_gh[i].lo, s_gh[i].hi) ? 1 : 0;
    LockReleaseHook(lock, NULL);
    RgOffSimDrop();
    const LONG us = UsSince(t0);
    k.fpgUs = us;
    k.fpgGhosts = n < 0 ? 0 : n;
    InterlockedIncrement(&k.fpgCensus);
    for (int i = 0; i < n && i < kFpgLogMax; i++)
        Log("ghost census: id %08lX%08lX no object, class 0x%08lX type 1, Q %p flags %08lX, %s", s_gh[i].hi, s_gh[i].lo, kFpgFpClass, s_gh[i].q,
            s_gh[i].flags, mode == 2 ? "removed" : "kept (count only)");
    for (int i = 0; i < 12 && g_fpgClass[i].n; i++)
        Log("ghost census: outside the signature, class 0x%08lX: %ld object-shaped keys without an object, kept", g_fpgClass[i].cls, g_fpgClass[i].n);
    Log("ghost census: world load #%d, mode %ld, %ld entries, %ld object-shaped keys, %d ghost ids%s, %d removed, %ld kept on a failed read, "
        "%ld outside the signature, %.1f ms on the load screen", loadNo, mode, k.fpgEntries, k.fpgObjFp, n < 0 ? 0 : n,
        n < 0 ? " (walk failed: nothing removed)" : n == kFpgMax ? " (list full)" : "", removed, k.fpgKeptRead, k.fpgOther, us / 1000.0);
}

// [Discourage] GhostTest=1 only (repro footprint-ghost, never in the shipped ini; ops on the 0x87 channel, no counter offsets, so
// the same harness drives a master and a v0.2.9 pair). 20 lo = hold the next job's start up to lo ms, 21 a job is held, 22 release,
// 23 / 24 id lo / hi, 25 after a drain: the map has an entry for the id, 26 remove that id's ghost (FpgRemove's rechecks), 27 lo =
// LostRemovalFix switch, 28 a census pass now (load screen rule waived: test only), 29 lo = GhostSweep mode for the next load.
static volatile LONG g_fpgHoldMs, g_fpgHolding;
static DWORD g_fpgId[2];
#ifdef TS3N_RFTEST
// docs/89 §18, test builds only (/DTS3N_RFTEST, build v0210d): 30 lo = RemovalFirst switch, 31 the held job ends once a later START
// is queued or parked (the plan after a Cleanup cannot release the hold itself: it blocks), 32 any START parked, 33 any tick cap hit,
// 34 the "footprint removal first" log line now.
static volatile LONG g_fpgUntil;
static DWORD g_fpgAfterSeq;
static bool FpgNextJobWaits() {
    if (!g_fpgUntil) return false;
    for (int i = 0; i < kJobs; i++) {
        const LONG s = g_jobs[i].state;
        if (s == J_PEND || (s == J_QUEUED && (LONG)(g_jobs[i].seq - g_fpgAfterSeq) > 0)) return true;
    }
    return false;
}
#else
static bool FpgNextJobWaits() { return false; }
#endif
static void FpgHoldPoint() {
    const LONG ms = g_fpgTest ? InterlockedExchange(&g_fpgHoldMs, 0) : 0;
    if (!ms) return;
    InterlockedIncrement(&ts3native_counters.fpgHeld);
    const DWORD t0 = GetTickCount();
    InterlockedExchange(&g_fpgHolding, 1);
    while (g_fpgHolding && GetTickCount() - t0 < (DWORD)ms && !FpgNextJobWaits()) Sleep(1);  // bounded: a sim wait inside the hold cannot hang
    InterlockedExchange(&g_fpgHolding, 0);
}
static u8 FpgOp(int op, DWORD v) {
    if (!g_fpgTest) return 0;
    u8* world = *g_fpgWorldSlot;
    switch (op) {
        case 20: InterlockedExchange(&g_fpgHoldMs, (LONG)(v < 3000 ? v : 3000)); return 1;
        case 21: return g_fpgHolding != 0;
        case 22:
#ifdef TS3N_RFTEST
            InterlockedExchange(&g_fpgUntil, 0);
#endif
            InterlockedExchange(&g_fpgHoldMs, 0);
            InterlockedExchange(&g_fpgHolding, 0);
            return 1;
        case 23: g_fpgId[0] = v; return 1;
        case 24: g_fpgId[1] = v; return 1;
        case 25:
            if (!DiscWorldOk(world)) return 0;
            DiscDrain(true, DS_FPGOP);
            return FpgMapHas(world, g_fpgId[0], g_fpgId[1]);
        case 26: {
            if (!DiscWorldOk(world)) return 0;
            DiscDrain(true, DS_FPGOP);
            InterlockedIncrement(&g_rgOffSimOpen);
            WaitAsyncDone(SW_DISC);
            static const DWORD kInfinite = 0xFFFFFFFF;
            LockAcquireHook(world + 0x918, NULL, &kInfinite);
            const bool ok = FpgRemove(world, g_fpgId[0], g_fpgId[1]);
            LockReleaseHook(world + 0x918, NULL);
            RgOffSimDrop();
            return ok;
        }
#ifdef TS3N_RFTEST
        case 30: g_rfMode = v ? 1 : 0; return 1;
        case 31: g_fpgAfterSeq = g_jobSeq; InterlockedExchange(&g_fpgUntil, 1); return 1;
        case 32: return g_rfParked > 0;
        case 33: return g_rfCapped > 0;
        case 34: DiscRfLog(); return 1;
#endif
        case 27: if (ts3native_counters.fp2lrReady) ts3native_counters.fp2lrMode = v ? 1 : 0; return 1;
        case 28: FpgCensus(-1); return 1;
        case 29: ts3native_counters.fpgMode = v < 2 ? (LONG)v : 2; return 1;
    }
    return 0;
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
    g_rfMode = Clamp(GetPrivateProfileIntW(L"Discourage", L"RemovalFirst", 1, ini), 0, 1);
    Log("footprint removal first: RemovalFirst=%d (docs/89 §18)", g_rfMode);
    g_dyMode = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"DiscYieldForeign", 0, ini), 0, 1);
    g_dyOwn = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"DiscYieldOwn", 0, ini), 0, 1);
    Log("disc yield: DiscYieldForeign=%d (docs/640; 1 = an unseen caller parks instead of waiting out a foreign job at a forced drain), "
        "DiscYieldOwn=%d (docs/670 §27; the same behind an own job)", g_dyMode, g_dyOwn);
    ts3native_counters.fpgMode = Clamp(GetPrivateProfileIntW(L"Discourage", L"GhostSweep", 1, ini), 0, 2);
    g_fpgTest = GetPrivateProfileIntW(L"Discourage", L"GhostTest", 0, ini) != 0;
    Log("ghost census at world load: GhostSweep=%ld (0 off, 1 count only, 2 remove)%s", ts3native_counters.fpgMode, g_fpgTest ? ", GhostTest ops on" : "");
}
