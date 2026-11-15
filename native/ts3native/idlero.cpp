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

static const DWORD IK_SITE_GIP = TS3A(0x007558D7), IK_GIP = TS3A(0x0062FB60), IK_SITE_TEST = TS3A(0x00755916), IK_TEST = TS3A(0x00625140);
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
static const DWORD IC_SITE_OQ = TS3A(0x0075598B);
static GvFn g_icOrig = (GvFn)(uintptr_t)GV_TARGET;
static volatile LONG g_icNoted;

// [IdleCheckRO] SiteCLoop (C2, docs/195 §8, counters v905): with footprints near the point, the IC's own loop
// (0x7559A0..0x755A4C) runs on the clone's footprints inside GvRun's QL hold. Answer 0 (no footprint decides): the vector
// stays empty and the IC returns 0 at 0x755A5B, as after the game's call. Any other answer (4, 8, 0x10, 0x20) or a doubt
// calls the game. Every call of the loop is a read [verified disassembly 2026-11-04]: interface slot +0xC is a constant kind
// getter, +4 (0x620E60 -> 0x806FE0) the id at +0x14, +0x1C (0x620D20) the primary class slot +8 with (0, 0) (one of the k8
// functions the S2 clone calls), +8 the type byte +0x35 (0x624ED0) or 10 (0x620E50); kind 5 looks the id up in the frame's
// id list (0x788EA0 is a find) and ObjectService_FindById 0x939560 (no lock, no AddRef), then flag word +0x2C & [0x11E90FC].
// The frame is the IC's esp at 0x755990 (the call returns with ret 0x14): +0x18 ids, +0x1C count, +0x34 ids end, +0x38 the
// vector, +0x4C the object service [verified disassembly: 0x75576C, 0x75592F-0x755949, 0x7556CF].
typedef const u8*(__fastcall* IcFindFn)(const void* svc, void* edx, DWORD lo, DWORD hi);
typedef bool (*IcClassFn)(const u8* obj, int* cls);
static bool IcClassGame(const u8* obj, int* cls) {  // 0x620D20's call; false = not a verified class function
    static const DWORD k8[] = {TS3A(0x0080F840), TS3A(0x0080C300), TS3A(0x00807090), TS3A(0x00807B70), TS3A(0x008066C0), TS3A(0x006119F0)};
    const void* const* vt = *(const void* const* const*)obj;
    if (!vt || !SrIn((DWORD)(uintptr_t)vt[2], k8, 6)) return false;
    *cls = ((SlotClassFn)vt[2])(obj, NULL, NULL, 0);
    return true;
}
static IcFindFn g_icFind = (IcFindFn)(uintptr_t)TS3A(0x00939560);
static IcClassFn g_icClass = IcClassGame;                    // selftest hands in fakes
static const DWORD* g_icMask = (const DWORD*)(uintptr_t)TS3A(0x011E90FC);
static const u8* g_icTestFrame;                               // selftest: the frame (its own return address is not 0x755990)
static const int IC_LOOP_DOUBT = -1;
// SiteCLoopJump (C2b, counters v906): the IC's exit for each answer. Each one is lea ecx, [esp+0x38]; call 0x652470 (the
// vector's release: ReleaseRange, then free if allocated; empty here), mov eax, answer, the pops and ret; none reads a
// register the loop set [verified disassembly 2026-11-04]. Answer 0 already ends at 0x755A5B the same way.
static const DWORD kIcExit[4][2] = {{4, TS3A(0x00755A12)}, {8, TS3A(0x00755A82)}, {0x10, TS3A(0x00755A97)}, {0x20, TS3A(0x00755A6D)}};
static bool g_icJumpOk;   // the four exits' bytes as verified at install
static DWORD g_icJumpTo;  // selftest (g_icTestFrame): the exit IcStub would have returned to
static DWORD IcExit(int a) {
    for (const auto& e : kIcExit)
        if ((int)e[0] == a) return e[1];
    return 0;
}
static bool IcExitsOk() {  // lea ecx, [esp+0x38]; call 0x652470; then mov eax, answer within the next 12 bytes
    for (const auto& e : kIcExit) {
        const u8* p = (const u8*)(uintptr_t)e[1];
        if (!Readable(p, 21) || memcmp(p, "\x8D\x4C\x24\x38\xE8", 5) || p + 9 + *(const LONG*)(p + 5) != (const u8*)TS3A(0x00652470))
            return false;
        bool mov = false;
        for (int i = 9; i < 17 && !mov; i++) mov = p[i] == 0xB8 && *(const DWORD*)(p + i + 1) == e[0];
        if (!mov) return false;
    }
    return true;
}
// a doubt is -why: 1 no interface table, 2 kind slot target, 3 kind-5 id slot, 4 slot +0x1C, 5 class function, 6 slot +8,
// 7 clone list truncated; the last one's slot targets for the log (owner thread only)
static struct { int why; DWORD sv1, sv2, sv3, sv7, vt2; } g_icDoubt;
static int IcDoubt(int why, const u8* obj) {
    const DWORD* sv = obj ? *(const DWORD* const*)(obj + 4) : NULL;
    const DWORD* vt = obj ? *(const DWORD* const*)obj : NULL;
    g_icDoubt.why = why, g_icDoubt.vt2 = vt ? vt[2] : 0;
    g_icDoubt.sv1 = sv ? sv[1] : 0, g_icDoubt.sv2 = sv ? sv[2] : 0, g_icDoubt.sv3 = sv ? sv[3] : 0, g_icDoubt.sv7 = sv ? sv[7] : 0;
    return -why;
}
// one element of the IC's loop (obj = the primary object; the vector holds obj + 4): 0 = next, -1 = doubt, else the answer
static int IcLoopOne(const u8* obj, const u8* f) {
    const DWORD* sv = *(const DWORD* const*)(obj + 4);
    if (!sv) return IcDoubt(1, obj);
    int kind;
    switch (sv[3]) {  // interface slot +0xC: sub ecx, 4, then a constant getter
    case TS3A(0x006262F0): kind = 1; break;
    case TS3A(0x0074BB10): kind = 2; break;
    case TS3A(0x00624EC0): kind = 3; break;
    case TS3A(0x00624E10): kind = 4; break;
    case TS3A(0x00624E60): kind = 5; break;
    case TS3A(0x006250A0): kind = 6; break;
    case TS3A(0x00620E00): kind = 7; break;
    case TS3A(0x00624E50): kind = 8; break;
    case TS3A(0x00624E00): kind = 9; break;  // 0xFEBA74 (2026-11-04: all 24 logged doubts of the first shadow)
    case TS3A(0x00620E50): kind = 0xA; break;
    case TS3A(0x00624EB0): kind = 0xB; break;
    default: return IcDoubt(2, obj);
    }
    if (kind == 5) {  // 0x7559B8
        if (!*(const DWORD*)(f + 0x1C)) return 0x20;
        if (sv[1] != TS3A(0x00620E60)) return IcDoubt(3, obj);
        const DWORD lo = *(const DWORD*)(obj + 0x14), hi = *(const DWORD*)(obj + 0x18);
        for (const DWORD *p = *(const DWORD* const*)(f + 0x18), *e = *(const DWORD* const*)(f + 0x34); p != e; p += 2)
            if (p[0] == lo && p[1] == hi) return 0;
        const u8* o = g_icFind(*(const void* const*)(f + 0x4C), NULL, lo, hi);
        return o && (*(const DWORD*)(o + 0x2C) & *g_icMask) ? 4 : 0x20;
    }
    if (sv[7] == TS3A(0x00624F70)) return 0;  // the member footprint's +0x1C: false
    if (sv[7] != TS3A(0x00620D20)) return IcDoubt(4, obj);
    int cls;
    if (!g_icClass(obj, &cls)) return IcDoubt(5, obj);
    if ((DWORD)cls < 2 || cls == 0xD) return 0;
    int t;
    if (sv[2] == TS3A(0x00624ED0)) t = *(const signed char*)(obj + 0x35);
    else if (sv[2] == TS3A(0x00620E50)) t = 0xA;
    else return IcDoubt(6, obj);
    return t == 1 ? 0x10 : t == 12 || t == 23 ? 8 : 0;
}
// the loop over a list (sub 0: the clone's primary objects, 4: the game's vector of interface pointers); NULL entries skipped
static int IcLoopRun(const u8* const* list, int n, int sub, const u8* f) {
    for (int i = 0; i < n; i++) {
        if (!list[i]) continue;
        if (const int a = IcLoopOne(list[i] - sub, f)) return a;
    }
    return 0;
}
static int IcVisit(const SrCtx& c, const void* f) { return c.over ? IcDoubt(7, NULL) : IcLoopRun(c.objs, c.n, 0, (const u8*)f); }
// the IC's frame from IcStub's return slot, or NULL when the caller is not the IC as verified
static const u8* IcFrameOf(const void* retSlot, const DWORD* vec) {
    const u8* f = g_icTestFrame ? g_icTestFrame : (const u8*)retSlot + 4 + 0x14;
    if (!g_icTestFrame && *(const DWORD*)retSlot != IC_SITE_OQ + 5) return NULL;
    if ((const u8*)vec != f + 0x38) return NULL;
    const DWORD b = *(const DWORD*)(f + 0x18), cnt = *(const DWORD*)(f + 0x1C), e = *(const DWORD*)(f + 0x34);
    return cnt < 0x10000 && e == b + cnt * 8 ? f : NULL;
}
static void IcLoopCode(int a) {
    Ts3nCounters& k = ts3native_counters;
    if (a < 0) {
        static volatile LONG noted;
        InterlockedIncrement(&k.icLoopDoubt);
        if (InterlockedIncrement(&noted) <= 24)
            Log("idle check RO site C loop: doubt %d (1 table, 2 kind, 3 id slot, 4 slot +0x1C, 5 class, 6 slot +8, 7 truncated): "
                "interface +4 %08lX +8 %08lX +0xC %08lX +0x1C %08lX, primary +8 %08lX", g_icDoubt.why, g_icDoubt.sv1, g_icDoubt.sv2,
                g_icDoubt.sv3, g_icDoubt.sv7, g_icDoubt.vt2);
    }
    else InterlockedIncrement(&k.icLoopCode[a == 4 ? 0 : a == 8 ? 1 : a == 0x10 ? 2 : 3]);
}
static DWORD IcShadow(u8* world, void* edx, const float* pt, float r, DWORD* vec, DWORD mode, DWORD opt, const u8* f) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD a = g_icOrig(world, edx, pt, r, vec, mode, opt);
    const int size = (int)((vec[1] - vec[0]) / 4);  // the game's copy: sorted, unique footprint refs
    bool lot = false;
    int n = 0, mine = 0;
    const int why = mode ? SR_FB_STATE : GvRun(world, pt, r, opt, false, &lot, &n, f ? IcVisit : NULL, f, &mine);
    if (!why && f && size && n == size) {  // SiteCLoop: the loop on the game's refs vs on the clone
        const int game = IcLoopRun((const u8* const*)(uintptr_t)vec[0], size, 4, f);
        if (game < 0 || mine < 0) IcLoopCode(game < 0 ? game : mine);
        else {
            InterlockedIncrement(&k.icLoopShadowRuns);
            if (!game) InterlockedIncrement(&k.icLoopShadowZero);
            if (!game != !mine) {
                InterlockedIncrement(&k.icLoopShadowMismatch);
                if (InterlockedIncrement(&g_icNoted) <= 16)
                    Log("idle check RO site C loop: shadow mismatch at (%.2f, %.2f): game list 0x%X, clone 0x%X, %d footprints",
                        pt[0], pt[2], game, mine, size);
            } else if (game != mine) InterlockedIncrement(&k.icLoopShadowCodeDiff);
        }
    }
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
    const u8* const f = k.icLoopMode || k.icShadow ? IcFrameOf(_AddressOfReturnAddress(), vec) : NULL;
    if ((!k.icMode && !k.icShadow) || tid != g_async.owner) return g_icOrig(world, edx, pt, r, vec, mode, opt);
    InterlockedIncrement(&k.icCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.icShadow) return g_icOrig(world, edx, pt, r, vec, mode, opt);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        const DWORD a = g_async.state == A_RUNNING ? g_icOrig(world, edx, pt, r, vec, mode, opt) : IcShadow(world, edx, pt, r, vec, mode, opt, f);
        RgOffSimDrop();
        return a;
    }
    if (!k.icMode) return g_icOrig(world, edx, pt, r, vec, mode, opt);
    int why = SR_FB_STATE;
    if (!mode && vec[0] == vec[1] && AvMayRun(tid)) {  // as the call site passes them; not the +0x918 owner, no callback
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        bool lot;
        int n, ans = IC_LOOP_DOUBT;
        const u8* const lf = k.icLoopMode ? f : NULL;
        why = GvRun(world, pt, r, opt, true, &lot, &n, lf ? IcVisit : NULL, lf, &ans);
        if (!why && !n) {
            const LONG us = UsSince(t0);
            InterlockedIncrement(&k.icTaken);
            MwSkip(MW_IK);
            if (!lot) InterlockedIncrement(&k.icNoLot);
            InterlockedExchangeAdd(&k.icUs, us);
            Max(&k.icMaxUs, us);
            return lot ? 1 : 0;
        }
        if (!why && lf) {  // SiteCLoop: the IC's loop already ran on the clone
            InterlockedIncrement(&k.icLoopRuns);
            if (!ans) {
                const LONG us = UsSince(t0);
                InterlockedIncrement(&k.icLoopTaken);
                MwSkip(MW_IK);
                InterlockedExchangeAdd(&k.icLoopUs, us);
                return 1;  // a lot was found (al 1); the IC reads only the vector, which stays empty: it returns 0
            }
            if (const DWORD to = ans > 0 && k.icJumpMode && g_icJumpOk ? IcExit(ans) : 0) {  // SiteCLoopJump (C2b)
                const LONG us = UsSince(t0);
                InterlockedIncrement(&k.icJumped);
                IcLoopCode(ans);
                MwSkip(MW_IK);
                InterlockedExchangeAdd(&k.icLoopUs, us);
                // the IC's exit for this answer instead of 0x755990 (IcFrameOf checked that slot); the vector stays empty
                if (g_icTestFrame) g_icJumpTo = to;
                else *(DWORD*)_AddressOfReturnAddress() = to;
                return 1;
            }
            IcLoopCode(ans);
            return g_icOrig(world, edx, pt, r, vec, mode, opt);
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
    k.icLoopMode = Clamp(GetPrivateProfileIntW(L"IdleCheckRO", L"SiteCLoop", 0, ini), 0, 1);
    g_icJumpOk = IcExitsOk();
    k.icJumpMode = g_icJumpOk ? Clamp(GetPrivateProfileIntW(L"IdleCheckRO", L"SiteCLoopJump", 0, ini), 0, 1) : 0;
    if (!g_icJumpOk) Log("SKIP idle check RO site C loop jump: the IC's exits (0x755A12 / 0x755A6D / 0x755A82 / 0x755A97) differ");
    k.icReady = 1;
    Log("OK   idle check RO site C: 0x75598B (SiteCMode=%ld SiteCShadow=%ld SiteCLoop=%ld SiteCLoopJump=%ld)", k.icMode, k.icShadow,
        k.icLoopMode, k.icJumpMode);
}
