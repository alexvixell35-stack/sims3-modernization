// [RouteAsync] ReplanDedupe (docs/150 §13, counters v90): repeated replans of one follower. Included by ts3native.cpp after
// planrepeat.cpp (PrVersions, PrVec) and obstructro.cpp (g_orRoute, g_orRmSlot). Default 0: nothing installed.
//
// Where: IC_Route_Replan 0x765490 and IC_Route_ReplanFromPosition 0x765550, around the whole IC. With [RouteAsync] Kinds
// hooking a kind, A10's CallIc runs RdRun on whichever thread runs the IC (the worker inside a job, the sim on a sync
// fallback); else this file's own prologue detour does (sim thread; [UnseenThrottle]'s v83 replan split chains behind it since the 2026-10-22 merge, unseen.cpp, and counts planned calls only).
// Observe (every call, both modes): a repeat = same follower (route+0x40/+0x44), same IC, same goal (the destination set's
// destinations, first 0x40 bytes each, and its words +0x20..+0x40) and a start within 10 cm (ReplanFromPosition: its point;
// Replan: the follower position as Route_StartPointsFromPosition reads it, 0x75D920 -> 0x752480), counted by the time since
// the previous plan's end (<= 250 / 500 / 1000 ms, cumulative) and by result (failure with the same answer, success with the
// same answer, another answer), with the repeat's own us.
// Cut (rdMode 1, --ab replandedupe): a repeat of a FAILED plan within 500 ms is answered with that plan's result bytes and
// the IC's own `add [route+0x1F4],1`, without planning, when all hold:
//   - the route is in the state that plan left (hash of the options O and every route field Route_PlanCommon resets or
//     fills, docs/93 §4.1, plus each path's status, nodes and blocker records), so no setter, follow or other plan ran;
//   - the world did not change since that plan's end: [RouteSnap] Count's change counters (PrVersions; this section turns
//     them on) unchanged, except the per-object footprint family when the route has neither BlockedByPeople (4) nor
//     BlockedByDynamicFootprints (0x1000) (standing Sims' footprints are read through those filters, docs/92 §7);
//   - same routing world, no pending lot updates or dirty lot index (RtQuietWorld: the plan would apply them first, E1);
//   - the result is not 18 FailedBecauseDestinationPortalLocked (the C# AboutToPlanCallback clears
//     SimRoutingComponent.LockedDoorsDuringPlan before the IC; the plan's door answers refill it; only the type-18 branch of
//     DoRouteFailureBehavior reads it [verified decompile]); no P4/P5 clone run (g_p4On).
// Why that is the plan's own answer: docs/150 §13.2 (verified / inferred per step). In observe mode (rdMode 0) the same test
// runs and the plan runs anyway: rdWould / rdWouldSame / rdWouldDiff are the live check of that argument.
// shortcut: 256 followers, 4 probes, the oldest evicted; a busy town with more replanning followers inside 1 s loses repeats.

static const int RD_N = 256;  // power of two
struct RdEntry {
    DWORD lo, hi;
    int kind;
    u64 goal, state;
    float pt[3];
    ULONGLONG ms;  // the plan's end
    DWORD ver, sver;
    const void* lock;
    LONG us;
    bool fail;
    u8 res[0x48];
};
struct RdKey {
    DWORD lo, hi;
    u64 goal, state;
    float pt[3];
    bool dyn;  // BlockedByPeople | BlockedByDynamicFootprints
    u8* route;
};
static RdEntry g_rdTab[RD_N];
static SRWLOCK g_rdLock = SRWLOCK_INIT;
static void* g_rdOrig[4];  // own detours' trampolines ([1] Replan, [3] ReplanFromPosition) when A10 does not hook the kind
// Route_GetFollowerPosition 0x752480: thiscall(route; pos16*, dir16*, ids*), ret 0xC; 1 = ok, 5 = no follower [verified]
typedef DWORD(__fastcall* RdFollowerPosFn)(void* route, void* edx, float* pos16, float* dir16, DWORD* ids);
static RdFollowerPosFn g_rdFollowerPos = (RdFollowerPosFn)(uintptr_t)0x00752480;
static const ULONGLONG kRdWinMs[3] = {250, 500, 1000};
static const ULONGLONG kRdServeMs = 500;  // docs/151 D7
static const float kRdStartM = 0.1f;

// options O = route+0x158 and the route fields Route_PlanCommon resets or fills, then the paths it leaves (docs/93 §4)
static u64 RdState(const u8* route) {
    const u8* o = route + 0x158;
    u64 s = Fnv(0xCBF29CE484222325ull, *(const DWORD*)(o + 0x08));
    for (DWORD off = 0x28; off < 0x38; off += 4) s = Fnv(s, *(const DWORD*)(o + off));
    s = PrVec(PrVec(s, o, 0x38, 8), o, 0x68, 8);
    s = Fnv(Fnv(s, *(const DWORD*)(o + 0x78)), *(const DWORD*)(o + 0x7C));
    for (DWORD off = 0x3C; off < 0x50; off += 4) s = Fnv(s, *(const DWORD*)(route + off));  // AGS, follower, +0x48/+0x4C
    for (DWORD off = 0xA4; off < 0xAC; off += 4) s = Fnv(s, *(const DWORD*)(route + off));  // lengths
    for (DWORD off = 0xE0; off < 0xF4; off += 4) s = Fnv(s, *(const DWORD*)(route + off));  // path list, current path, index
    s = Fnv(Fnv(s, *(const DWORD*)(route + 0x140)), *(const DWORD*)(route + 0x144));      // facing flags, destination set
    const u8* head = route + 0xE0;
    const u8* n = *(const u8* const*)head;
    for (int i = 0; n && n != head && i < 8; i++, n = *(const u8* const*)n) {
        const u8* path = *(const u8* const*)(n + 8);
        if (!path) { s = Fnv(s, 0); continue; }
        s = Fnv(s, *(const DWORD*)(path + 0x94));          // status
        s = PrVec(s, path, 0x98, 48);                        // nodes
        s = PrVec(s, path, 0xD0, 40);                        // blocker records
    }
    return s;
}

static bool RdMakeKey(int kind, DWORD handle, const float* pt, RdKey* key) {
    __try {
        const u8* rm = *g_orRmSlot;
        u8* route = rm ? g_orRoute(rm, NULL, handle) : NULL;
        if (!route) return false;
        key->route = route;
        key->lo = *(const DWORD*)(route + 0x40), key->hi = *(const DWORD*)(route + 0x44);
        if (!(key->lo | key->hi)) return false;
        key->dyn = (*(const DWORD*)(route + 0x158 + 0x28) & (4 | 0x1000)) != 0;
        u64 g = Fnv(0xCBF29CE484222325ull, (DWORD)kind);
        const u8* ds = *(const u8* const*)(route + 0x144);
        if (ds) {  // destination list +0x18: node = destination + 4, next at the node (0x751FF0) [verified]
            const u8* head = ds + 0x18;
            const u8* n = *(const u8* const*)head;
            for (int i = 0; n && n != head && i < 16; i++, n = *(const u8* const*)n)
                for (int w = 0; w < 16; w++) g = Fnv(g, ((const DWORD*)(n - 4))[w]);
            for (DWORD off = 0x20; off < 0x40; off += 4) g = Fnv(g, *(const DWORD*)(ds + off));
        }
        key->goal = g;
        key->state = RdState(route);
        if (kind == 3) {
            memcpy(key->pt, pt, sizeof(key->pt));
        } else {
            __declspec(align(16)) float p[4], d[4];
            DWORD ids[2];
            if (g_rdFollowerPos(route, NULL, p, d, ids) != 1) return false;
            memcpy(key->pt, p, sizeof(key->pt));
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return true;
}

static RdEntry* RdSlot(DWORD lo, DWORD hi, int kind, bool add) {  // g_rdLock held
    const DWORD h = (lo * 2654435761u ^ hi * 40503u ^ (DWORD)kind) & (RD_N - 1);
    RdEntry* old = NULL;
    for (int i = 0; i < 4; i++) {
        RdEntry& e = g_rdTab[(h + i) & (RD_N - 1)];
        if (e.ms && e.lo == lo && e.hi == hi && e.kind == kind) return &e;
        if (!old || e.ms < old->ms) old = &e;
    }
    return add ? old : NULL;
}

typedef u8* (*RdInner)(int kind, u8* out, DWORD handle, const float* pt);
// kind 0 = the route IC Plan on the sim's sync path ([RouteAsync] FailMemo, docs/164 §7 D5, plandeadline.cpp): the same check
// and entry table, its own switch fmMode and counters fm* (v99); the start is the follower position as for Replan [inferred:
// Route_PlanCommon plans Plan from the follower like Replan, docs/93 §4; fmWouldSame / fmWouldDiff in observe mode check it],
// and no +0x1F4 bump (only the two replan ICs write it, docs/101 §5)
static bool KdKnownUnseen(DWORD handle);  // plandeadline.cpp
static u8* RdRun(int kind, u8* out, DWORD handle, const float* pt, RdInner inner) {
    Ts3nCounters& k = ts3native_counters;
    const bool fm = kind == 0;
    const int ic = kind == 1 ? 0 : 1;
    LONG* const calls = fm ? &k.fmCalls : &k.rdCalls[ic];
    LONG* const inval = fm ? k.fmInval : k.rdInval;
    RdKey key;
    const bool keyed = RdMakeKey(kind, handle, pt, &key);
    if (!keyed) InterlockedIncrement(fm ? &k.fmNoKey : &k.rdNoKey);
    DWORD ver0, sver0;
    PrVersions(&ver0, &sver0);
    const bool verOk = (k.snReady & 5) == 5;
    if (verOk && !(k.rdReady & 4)) InterlockedOr(&k.rdReady, 4);
    const ULONGLONG now = GetTickCount64();
    RdEntry prev = {};
    bool rep = false;
    if (keyed) {
        AcquireSRWLockShared(&g_rdLock);
        const RdEntry* e = RdSlot(key.lo, key.hi, kind, false);
        if (e && e->goal == key.goal && now - e->ms <= kRdWinMs[2]) {
            const float dx = e->pt[0] - key.pt[0], dy = e->pt[1] - key.pt[1], dz = e->pt[2] - key.pt[2];
            if (dx * dx + dy * dy + dz * dz <= kRdStartM * kRdStartM) prev = *e, rep = true;
        }
        ReleaseSRWLockShared(&g_rdLock);
    }
    int elig = -1;  // 0 = nothing changed, 1 = per-object footprint writes only (route not blocked by them)
    if (rep && prev.fail && now - prev.ms <= kRdServeMs) {
        const DWORD type = *(const DWORD*)prev.res;
        const int why = prev.state != key.state                                                            ? 0
                        : !verOk || !g_rgLock || prev.lock != g_rgLock || type == 18 || g_p4On ||
                                  !RtQuietWorld(g_rgLock)                                                  ? 3
                        : sver0 != prev.sver                                                               ? 2
                        : ver0 != prev.ver && key.dyn                                                      ? 1
                                                                                                           : -1;
        if (why >= 0) InterlockedIncrement(&inval[why]);
        else elig = ver0 == prev.ver ? 0 : 1;
        if (fm) InterlockedIncrement(&k.fmRep);
    }
    // docs/249: elig 0 = nothing changed; elig 1 = object footprints moved, the old failure may not be the plan's answer: known unseen only
    if (elig == 1 && !KdKnownUnseen(handle)) elig = -1, InterlockedIncrement(&ts3native_counters.kdRdHeld);
    if (elig >= 0 && (fm ? k.fmMode : k.rdMode) == 1) {
        if (!fm) __try { *(DWORD*)(key.route + 0x1F4) += 1; } __except (EXCEPTION_EXECUTE_HANDLER) {}  // the IC's own replan count
        memcpy(out, prev.res, sizeof(prev.res));
        InterlockedIncrement(calls);
        InterlockedIncrement(fm ? &k.fmServed : &k.rdServed);
        InterlockedExchangeAdd(fm ? &k.fmServedUs : &k.rdServedUs, prev.us);
        return out;  // the entry keeps the real plan's end: the window never extends past 500 ms from a plan
    }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    u8* r = inner(kind, out, handle, pt);
    const LONG us = UsSince(t0);
    InterlockedIncrement(calls);
    if (!fm) InterlockedExchangeAdd(&k.rdUs[ic], us);
    const bool fail = *(const DWORD*)out != 1;
    if (fail) InterlockedIncrement(fm ? &k.fmFails : &k.rdFails[ic]);
    if (!keyed) return r;
    if (rep) {
        const bool same = memcmp(out, prev.res, 0x44) == 0;  // +0x44 is stale stack (docs/93 §2)
        const int cls = same ? (fail ? 0 : 1) : 2;
        if (!fm)
            for (int w = 0; w < 3; w++)
                if (now - prev.ms <= kRdWinMs[w]) InterlockedIncrement(&k.rdRepN[w][cls]), InterlockedExchangeAdd(&k.rdRepUs[w][cls], us);
        if (elig >= 0) {
            InterlockedIncrement(fm ? &k.fmWould[elig] : &k.rdWould[elig]);
            InterlockedIncrement(same ? (fm ? &k.fmWouldSame[elig] : &k.rdWouldSame[elig]) : (fm ? &k.fmWouldDiff[elig] : &k.rdWouldDiff[elig]));
        }
    }
    u64 state = 0;
    __try { state = RdState(key.route); } __except (EXCEPTION_EXECUTE_HANDLER) { return r; }
    DWORD ver1, sver1;
    PrVersions(&ver1, &sver1);
    AcquireSRWLockExclusive(&g_rdLock);
    RdEntry& e = *RdSlot(key.lo, key.hi, kind, true);
    e.lo = key.lo, e.hi = key.hi, e.kind = kind, e.goal = key.goal, e.state = state;
    memcpy(e.pt, key.pt, sizeof(e.pt));
    e.ms = GetTickCount64();
    e.ver = ver1, e.sver = sver1, e.lock = g_rgLock, e.us = us, e.fail = fail;
    memcpy(e.res, out, sizeof(e.res));
    ReleaseSRWLockExclusive(&g_rdLock);
    return r;
}

static u8* RdCallOrig(int kind, u8* out, DWORD handle, const float* pt) {
    if (kind == 1) return ((IcPlanFn)g_rdOrig[1])(out, handle);
    return ((IcPointFn)g_rdOrig[3])(out, handle, pt[0], pt[1], pt[2]);
}
static u8* __cdecl RdReplanHook(u8* out, DWORD handle) { return RdRun(1, out, handle, NULL, RdCallOrig); }
static u8* __cdecl RdReplanPosHook(u8* out, DWORD handle, float x, float y, float z) {
    const float pt[3] = {x, y, z};
    return RdRun(3, out, handle, pt, RdCallOrig);
}

// [RouteAsync] ReplanDedupe = 0 off (nothing installed), 1 = installed, rdMode starts 1 (serve), 2 = installed, rdMode
// starts 0 (observe only). After InstallA10 (its collect detours own a hooked kind) and InstallP10.
static void InstallReplanDedupe(const wchar_t* ini) {
    const int v = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"ReplanDedupe", 0, ini), 0, 2);
    if (!v) { Log("replan dedupe off (ReplanDedupe=0)"); return; }
    Ts3nCounters& k = ts3native_counters;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.rdMode = v == 1 ? 1 : 0;
    for (int kind = 1; kind <= 3; kind += 2) {
        const LONG bit = kind == 1 ? 1 : 2;
        if (g_arOrig[kind]) { k.rdReady |= bit; continue; }  // A10's collect detour: CallIc runs RdRun
        g_rdOrig[kind] = InstallDetour(kP10Name[kind], kP10Ic[kind], kP10Pro[kind], sizeof(kP10Pro[kind]),
                                       kind == 1 ? (void*)RdReplanHook : (void*)RdReplanPosHook, &g_rdOrig[kind]);
        if (g_rdOrig[kind]) k.rdReady |= bit | 8;
    }
    g_rdOn = (k.rdReady & 3) ? 1 : 0;
    Log("%s replan dedupe: ready 0x%lX (bit 0 Replan, 1 ReplanFromPosition, 3 own detour), rdMode %ld; world version from "
        "[RouteSnap] counters (turned on for this section)",
        (k.rdReady & 3) == 3 ? "OK  " : g_rdOn ? "PART" : "SKIP", k.rdReady, k.rdMode);
}
