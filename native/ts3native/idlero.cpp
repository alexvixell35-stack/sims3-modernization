// [IdleCheckRO] (docs/178 §8): the gate-12 GetIfPresent wait (key 0x62FB74) of IC_RouteManager_IsOkToIdleAtCurrentLocation
// 0x7556B0 answered read-only while an async route job holds +0x918. Included by ts3native.cpp after fgro.cpp (uses
// steerro.cpp's QL, writer set and SrEntry; avoidro.cpp's AvMayRun; savewrite.cpp's PatchCalls).
//
// The IC (cdecl; the script's Sim.RunIdleLogic path) finds the Sim, then for a Sim with a room index (0xB499D0 != -1) asks
// LotTable_GetIfPresent 0x62FB60 (thiscall P = the lot planner, args = the Sim's lot key +0x48 / +0x4C; Contains 0x628A90 =
// gate 12, key 0x62FB74, then GetOrInsert 0x62EAB0 = gate 0 on a hit) at 0x7558D7, and on a hit the leaf 0x625140 (thiscall
// P; entry, room, the 0x74BF10 value, &point, radius; ret 0x14; no calls [verified 0x625140..0x6253F0]: reads the entry's
// room vector +0x90/+0x94, transform +0xA0..+0xD0, each room's portal vectors and portal fields +0x14..+0x24, +0x48, +0x58)
// at 0x755916; al != 0 -> the IC returns 0x40. A miss (0) and a 0 from 0x625140 both go to 0x75592B (0x6508F0, then the
// obstacle query 0x82EDB0 at 0x75598B, which takes +0x918 itself: return key 0x755990) [verified disassembly 2026-10-24].
// P1b (clean-night-2 bp/ip-p1b): 97% of the 0x62FB74 waits have the second frame 0x7558DC = this site [measured].
//
// Two call-site retargets. A (0x7558D7): on the sim (owner) thread while a job runs, not the +0x918 owner, no job callback,
// no gate frame, ikMode: SrEntry with the Contains rules (no window-slot write) under QL shared, try only, S2's protocol
// (sequence, tripwire epoch, generation). The answer is the entry (or 0 = Contains false). B (0x755916, reached only after A's
// hit, nothing runs between): under QL shared again with A's sequence, epoch and generation unchanged, the game's own leaf
// 0x625140 on A's entry, then the same check. Any doubt in A calls the game's 0x62FB60 (today's wait); in B ("redo") the
// game's 0x62FB60 then, on a hit, the game's 0x625140 on its entry, unlocked as today (a miss returns 0: the caller's path
// for a miss and for a 0 is the same 0x75592B). Shadow (no job): the game's 0x62FB60, then SrEntry, compared.
// Counters v119 (+0x4824): ikMode (+0x4828, --ab idlecheckro), ikShadow (+0x482C, --ab idlecheckroshadow).

static const DWORD IK_SITE_GIP = 0x007558D7, IK_GIP = 0x0062FB60, IK_SITE_TEST = 0x00755916, IK_TEST = 0x00625140;
typedef u8*(__fastcall* IkGipFn)(u8* p, void* edx, DWORD lo, DWORD hi);  // ret 8
typedef DWORD(__fastcall* IkTestFn)(u8* p, void* edx, const u8* entry, DWORD room, DWORD b, const void* pt, float r);  // ret 0x14
typedef int (*IkEntryFn)(const u8* p, DWORD lo, DWORD hi, const u8** entry);
static int IkEntryGame(const u8* p, DWORD lo, DWORD hi, const u8** entry) { return SrEntry(p, lo, hi, true, entry); }
static IkGipFn g_ikGip = (IkGipFn)(uintptr_t)IK_GIP;
static IkTestFn g_ikTest = (IkTestFn)(uintptr_t)IK_TEST;
static IkEntryFn g_ikEntry = IkEntryGame;  // selftest hands in fake lot tables

enum { IK_FB_STATE, IK_FB_TRY, IK_FB_SHAPE, IK_FB_TRIP, IK_FB_JOBEND, IK_FB_REDO };
static const int IK_WHY_JOBEND = 100;  // above the SR_* codes

struct IkSnap { LONG seq, epoch, gen; };
// A's hit for B (owner thread only: both hooks check the owner first)
static struct { bool set; const u8* p; const u8* entry; DWORD lo, hi; IkSnap s; } g_ikA;

// QL shared (try only) with S2's protocol; SR_OK = held
static int IkEnter(IkSnap* s) {
    if (t_qlDepth || t_qlShared) return SR_FB_STATE;  // SRW locks are not recursive
    if (!TryAcquireSRWLockShared(&g_ql)) return SR_FB_TRY;
    if (g_steerBroken) { ReleaseSRWLockShared(&g_ql); return SR_FB_STATE; }
    s->seq = g_qlSeq;
    InterlockedExchange(&g_srInClone, 1);
    s->epoch = g_twEpoch, s->gen = g_twGen;
    t_inRo = true;
    t_qlShared = 1;
    t_srFail = SR_OK;
    return SR_OK;
}
static void IkLeave() {
    t_inRo = false;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    InterlockedExchange(&g_srInClone, 0);
}
// what moved since s: a writer (sequence, tripwire) or the job's end (generation, armed)
static int IkMoved(const IkSnap& s, bool job) {
    if (t_srFail) return t_srFail;
    if (g_qlSeq != s.seq || g_twActive || g_twEpoch != s.epoch) return SR_FB_TRIP;
    if (job && (g_twGen != s.gen || !g_twArmed)) return IK_WHY_JOBEND;
    return SR_OK;
}

// A's read: SrEntry under QL shared; *snap = the state it was read in
static int IkRunA(const u8* p, DWORD lo, DWORD hi, bool job, const u8** entry, IkSnap* snap) {
    *entry = NULL;
    int why = IkEnter(snap);
    if (why) return why;
    why = job && !g_twArmed ? IK_WHY_JOBEND : g_twActive ? SR_FB_TRIP : SR_OK;
    if (!why) why = g_ikEntry(p, lo, hi, entry);
    if (!why) why = IkMoved(*snap, job);
    IkLeave();
    return why;
}

static void IkFallback(int why) {
    int i;
    switch (why) {
    case SR_FB_TRY: i = IK_FB_TRY; break;
    case SR_FB_SHAPE: case SR_FB_LOTMISS: case SR_FB_DIRTY: case SR_FB_DEFERRED: case SR_FB_CACHE: i = IK_FB_SHAPE; break;
    case SR_FB_TRIP: i = IK_FB_TRIP; break;
    case IK_WHY_JOBEND: i = IK_FB_JOBEND; break;
    default: i = IK_FB_STATE; break;
    }
    InterlockedIncrement(&ts3native_counters.ikFb[i]);
}

static volatile LONG g_ikNoted;
static u8* __fastcall IkGipStub(u8* p, void* edx, DWORD lo, DWORD hi) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if ((!k.ikMode && !k.ikShadow) || tid != g_async.owner) return g_ikGip(p, edx, lo, hi);
    g_ikA.set = false;
    InterlockedIncrement(&k.ikCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.ikShadow) return g_ikGip(p, edx, lo, hi);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        u8* a = g_ikGip(p, edx, lo, hi);
        if (g_async.state != A_RUNNING) {
            const u8* e;
            IkSnap s;
            const int why = IkRunA(p, lo, hi, false, &e, &s);
            InterlockedIncrement(&k.ikShadowRuns);
            if (why) IkFallback(why);
            else if (e != a) {
                InterlockedIncrement(&k.ikShadowMismatch);
                if (InterlockedIncrement(&g_ikNoted) <= 16)
                    Log("idle check RO: shadow mismatch for lot %08lX:%08lX: game %p, clone %p", hi, lo, a, e);
            }
        }
        RgOffSimDrop();
        return a;
    }
    if (!k.ikMode) return g_ikGip(p, edx, lo, hi);
    int why = SR_FB_STATE;
    if (AvMayRun(tid)) {  // not the +0x918 owner (lent or recursive hold), no job callback, no gate frame open
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        const u8* e;
        IkSnap s;
        why = IkRunA(p, lo, hi, true, &e, &s);
        if (!why) {
            const LONG us = UsSince(t0);
            InterlockedIncrement(&k.ikTaken);
            MwSkip(MW_IK);
            InterlockedExchangeAdd(&k.ikUs, us);
            Max(&k.ikMaxUs, us);
            if (!e) { InterlockedIncrement(&k.ikOnward); return NULL; }  // Contains false: the IC goes on to 0x82EDB0
            InterlockedIncrement(&k.ikHits);
            g_ikA.set = true, g_ikA.p = p, g_ikA.entry = e, g_ikA.lo = lo, g_ikA.hi = hi, g_ikA.s = s;
            return (u8*)e;
        }
    }
    IkFallback(why);
    return g_ikGip(p, edx, lo, hi);
}

static DWORD __fastcall IkTestStub(u8* p, void* edx, const u8* entry, DWORD room, DWORD b, const void* pt, float r) {
    if (!g_ikA.set || GetCurrentThreadId() != g_async.owner) return g_ikTest(p, edx, entry, room, b, pt, r);
    g_ikA.set = false;
    Ts3nCounters& k = ts3native_counters;
    if (g_ikA.p != p || g_ikA.entry != entry) return g_ikTest(p, edx, entry, room, b, pt, r);  // not A's answer
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    IkSnap s;
    int why = IkEnter(&s);
    DWORD res = 0;
    if (!why) {
        why = IkMoved(g_ikA.s, true);  // between A and B (worker writers, the job's end)
        if (!why) res = g_ikTest(p, edx, entry, room, b, pt, r);  // a leaf: reads only, no calls
        if (!why) why = IkMoved(g_ikA.s, true);
        IkLeave();
    }
    if (!why) {
        const LONG us = UsSince(t0);
        InterlockedExchangeAdd(&k.ikUs, us);
        Max(&k.ikMaxUs, us);
        InterlockedIncrement((u8)res ? &k.ikPass : &k.ikOnward);
        return res;
    }
    IkFallback(why);
    InterlockedIncrement(&k.ikFb[IK_FB_REDO]);
    // the game's order from the lookup on: 0x62FB60 (today's wait), then 0x625140 on its entry
    u8* e = g_ikGip(p, NULL, g_ikA.lo, g_ikA.hi);
    if (!e) return 0;  // 0x7558DE and 0x75591D both jump to 0x75592B
    return g_ikTest(p, edx, e, room, b, pt, r);
}

// Site C (docs/195 §4 C1, 2026-11-04): the IC's obstacle query 0x82EDB0 at 0x75598B (return key 0x755990), reached when A
// misses or 0x625140 answers 0. The call matches [GoalValidRO]'s (gvro.cpp) [verified disassembly 2026-11-04]: thiscall world
// = [esp+0x34]; pt = the 0x6508F0 point (xyz +0, level +0x24); r = [ebp+0x14]; vec = a fresh empty FootprintRefVec (begin =
// end = cap = 0, tag "Routing/Host"); mode 0 (0x755960); opt = [ebp+0x18]. After it the IC reads only the vector: begin ==
// end -> 0x755A5B (frees the empty range, returns 0); al is not read. So an empty answer is exact without the game's call:
// GvRun under QL shared as GvStub does; footprints found or any doubt call the game's 0x82EDB0 (today's wait). Shadow (no
// job): the game's call, then GvRun, compared (al, footprint count). Counters v903 (+0x5B84): icMode (--ab idlecheckc),
// icShadow (--ab idlecheckcshadow).
static const DWORD IC_SITE_OQ = 0x0075598B;
static GvFn g_icOrig = (GvFn)(uintptr_t)GV_TARGET;
static volatile LONG g_icNoted;
static DWORD IcShadow(u8* world, void* edx, const float* pt, float r, DWORD* vec, DWORD mode, DWORD opt) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD a = g_icOrig(world, edx, pt, r, vec, mode, opt);
    const int size = (int)((vec[1] - vec[0]) / 4);  // the game's copy: sorted, unique footprint refs
    bool lot = false;
    int n = 0;
    const int why = mode ? SR_FB_STATE : GvRun(world, pt, r, opt, false, &lot, &n);
    InterlockedIncrement(&k.icShadowRuns);
    if (why) { InterlockedIncrement(&k.icFb[GvFbIdx(why)]); return a; }
    if (!(u8)a || !size) InterlockedIncrement(&k.icShadowEmpty);
    if (((u8)a != 0) != lot || (lot && size != n)) {
        InterlockedIncrement(&k.icShadowMismatch);
        if (InterlockedIncrement(&g_icNoted) <= 16)
            Log("idle check RO site C: shadow mismatch at (%.2f, %.2f) level %ld opt 0x%lX: game al %u / %d footprints, clone lot %u / %d",
                pt[0], pt[2], *(const LONG*)(pt + 9), opt, (unsigned)(u8)a, size, (unsigned)lot, n);
    }
    return a;
}
static DWORD __fastcall IcStub(u8* world, void* edx, const float* pt, float r, DWORD* vec, DWORD mode, DWORD opt) {
    Ts3nCounters& k = ts3native_counters;
    const StubKey sk(IC_SITE_OQ + 5);  // probe P0: the game's calls below keep the site's key
    const DWORD tid = GetCurrentThreadId();
    if ((!k.icMode && !k.icShadow) || tid != g_async.owner) return g_icOrig(world, edx, pt, r, vec, mode, opt);
    InterlockedIncrement(&k.icCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.icShadow) return g_icOrig(world, edx, pt, r, vec, mode, opt);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        const DWORD a = g_async.state == A_RUNNING ? g_icOrig(world, edx, pt, r, vec, mode, opt) : IcShadow(world, edx, pt, r, vec, mode, opt);
        RgOffSimDrop();
        return a;
    }
    if (!k.icMode) return g_icOrig(world, edx, pt, r, vec, mode, opt);
    int why = SR_FB_STATE;
    if (!mode && vec[0] == vec[1] && AvMayRun(tid)) {  // as the call site passes them; not the +0x918 owner, no callback
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        bool lot;
        int n;
        why = GvRun(world, pt, r, opt, true, &lot, &n);
        if (!why && !n) {
            const LONG us = UsSince(t0);
            InterlockedIncrement(&k.icTaken);
            MwSkip(MW_IK);
            if (!lot) InterlockedIncrement(&k.icNoLot);
            InterlockedExchangeAdd(&k.icUs, us);
            Max(&k.icMaxUs, us);
            return lot ? 1 : 0;
        }
        if (!why) {  // footprints near the point: the IC's loop needs the game's refs
            InterlockedIncrement(&k.icFb[GV_FB_HITS]);
            return g_icOrig(world, edx, pt, r, vec, mode, opt);
        }
    }
    InterlockedIncrement(&k.icFb[GvFbIdx(why)]);
    return g_icOrig(world, edx, pt, r, vec, mode, opt);
}
static const CallSite kIcSite = {IC_SITE_OQ, GV_TARGET, (void*)IcStub};

static const CallSite kIkSites[] = {{IK_SITE_GIP, IK_GIP, (void*)IkGipStub}, {IK_SITE_TEST, IK_TEST, (void*)IkTestStub}};

// [IdleCheckRO] Enabled=1: needs [RouteGate] Enforce=1 with the async path and the S2 writer set (QL, W1-W6, tripwires)
static void InstallIdleCheckRO(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"IdleCheckRO", L"Enabled", 0, ini)) { Log("idle check RO off (Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP idle check RO: needs [RouteGate] Enforce=1 with the async path"); return; }
    if (!InstallSrWriters()) { Log("SKIP idle check RO: the S2 writer set (W1-W6, W4, tripwires) is not all in place"); return; }
    if (!PatchCalls("idle check RO (IsOkToIdleAtCurrentLocation -> GetIfPresent, 0x625140)", kIkSites, 2)) return;
    k.ikMode = Clamp(GetPrivateProfileIntW(L"IdleCheckRO", L"Mode", 1, ini), 0, 1);
    k.ikShadow = Clamp(GetPrivateProfileIntW(L"IdleCheckRO", L"Shadow", 0, ini), 0, 1);
    k.ikReady = 1;
    Log("OK   idle check RO: 0x7558D7 + 0x755916 + the S2 writer set (Mode=%ld Shadow=%ld)", k.ikMode, k.ikShadow);
    if (!GetPrivateProfileIntW(L"IdleCheckRO", L"SiteC", 0, ini)) return;
    if (!PatchCalls("idle check RO site C (0x7556B0 -> ObstacleQuery)", &kIcSite, 1)) return;
    k.icMode = Clamp(GetPrivateProfileIntW(L"IdleCheckRO", L"SiteCMode", 1, ini), 0, 1);
    k.icShadow = Clamp(GetPrivateProfileIntW(L"IdleCheckRO", L"SiteCShadow", 0, ini), 0, 1);
    k.icReady = 1;
    Log("OK   idle check RO site C: 0x75598B (SiteCMode=%ld SiteCShadow=%ld)", k.icMode, k.icShadow);
}
