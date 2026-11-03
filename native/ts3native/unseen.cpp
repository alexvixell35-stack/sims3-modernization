// [UnseenProbe] (docs/143, counters v79; v78 on its first branch) and [UnseenThrottle] (phase 1 v79, phase 2 v80): splits the sim thread's per-agent locomotion work,
// its +0x918 acquires and waits, footprint updates and route plans by "visible" vs "unseen" agent (the probe observes
// only), and the throttle's parts at the end of this file skip or defer that work for unseen Sims.
// Included by ts3native.cpp after hitchattr.cpp.
//
// Sites [verified disassembly, Steam 1.67]:
//   Locomotion_Update 0x83CED0 walks the agent list [sys+0x40] (node at agent+0x40) and calls Locomotion_UpdateAgent
//   0x957210 once per agent whose [agent+8+8] is set, at 0x83CF52 (`fld [ebp+8]; push ecx; mov ecx,esi; fstp [esp];
//   call 0x957210`): thiscall(agent; float dt), `ret 4` (0x957451). The rel32 is rewritten (PatchCalls), as locowin.cpp
//   does one level up. The agent is the steering component (docs/45: 0x110 bytes); [agent+4] = its owner object.
//   UpdateSimFootprint 0x833560: thiscall(world; obj, enable, force, arg), `ret 0x10`, prologue `83 EC 10 80 7C 24 1C 00`
//   (whole instructions, no relative operand; docs/89 §5.1). A detour that only times and classifies. When design U
//   ([Discourage] DeferFootprint, discourage.cpp) already owns that entry, the probe sits behind design U's stub instead:
//   g_usfOrig (design U's way to the game's body) points at UnUsfHook, so the probe counts the bodies that really run
//   (pass-throughs at call time, deferred records at their drain), not the calls design U queued.
//   Route plans: IcPlanHook (ts3native.cpp, needs [RouteAsync]) calls UnPlan around IcCollect.
// Class of an object (the ts3fixes rule, Fixes.cs FarFromViewers, minus the household part):
//   visible = on screen (the game's own frustum test 0x68D130 on the camera of 0x96B850, as IC_World_IsObjectOnScreenImpl
//   0x78AAB0, but with a fixed 1 m sphere at the object position instead of its model bounds) or within `reach` of the camera
//   target or position, reach = max(80 m, 2 * |camera y - target y|) (Camera_GetPosition 0x73D410 / GetTarget 0x73D480,
//   read once per locomotion pass); else unseen. shortcut: no active-household test (natively unknown); a household Sim
//   walking far off screen counts as unseen. ts3fixes' TownieUnseen adds it; [UnseenThrottle] would need it from ts3fixes.
// Kind: Sim = the owner has the Sim component (bit global 0x11E90FC, docs/45); other = cars, boats, other agents [inferred:
// traffic cars are "Car" steering agents with Locomotion/CarBrain, strings at 0xC44474 / 0xC44683].
typedef void(__fastcall* UnAgentFn)(void* agent, void* edx, float dt);
typedef void(__fastcall* UnUsfFn)(void* world, void* edx, void* obj, DWORD enable, DWORD force, DWORD arg);
typedef void*(__fastcall* UnCompFn)(void* obj, void* edx, DWORD bit);        // Object_GetComponent 0x935770, ret 4
typedef float*(__fastcall* UnPosFn)(void* tc, void* edx, float* out16);       // Transform position 0x959000, ret 4 (movaps)
typedef void*(__cdecl* UnFrustumFn)();                                        // camera frustum 0x96B850 (camera + 0x280)
typedef u8(__fastcall* UnInFrustumFn)(const void* fr, void* edx, const float* sphere16);  // 0x68D130, ret 4 (movaps)
typedef float*(__cdecl* UnCamFn)(float* out);                                  // Camera_GetPosition / GetTarget ICs, 12 bytes
typedef void*(__fastcall* UnFindFn)(void* mgr, void* edx, DWORD lo, DWORD hi);  // object by id 0x939560, ret 8

static const DWORD UN_AGENT_CALL = 0x0083CF52, UN_AGENT = 0x00957210, UN_USF = 0x00833560;
static const u8 kUnUsfPrologue[] = {0x83, 0xEC, 0x10, 0x80, 0x7C, 0x24, 0x1C, 0x00};
// game pointers; selftest swaps them for fakes
static UnAgentFn g_unAgentOrig = (UnAgentFn)(uintptr_t)UN_AGENT;
static UnUsfFn g_unUsfOrig;
static UnCompFn g_unComp = (UnCompFn)(uintptr_t)0x00935770;
static UnPosFn g_unPos = (UnPosFn)(uintptr_t)0x00959000;
static UnFrustumFn g_unFrustum = (UnFrustumFn)(uintptr_t)0x0096B850;
static UnInFrustumFn g_unInFrustum = (UnInFrustumFn)(uintptr_t)0x0068D130;
static UnCamFn g_unCamPos = (UnCamFn)(uintptr_t)0x0073D410, g_unCamTgt = (UnCamFn)(uintptr_t)0x0073D480;
static UnFindFn g_unFind = (UnFindFn)(uintptr_t)0x00939560;
static AsRouteFn g_unRoute = (AsRouteFn)(uintptr_t)0x0074B2C0;  // RouteHandle_Lookup, as AsyncLosRoute
static const DWORD* g_unBitTransform = (const DWORD*)(uintptr_t)0x011E9158;
static const DWORD* g_unBitSim = (const DWORD*)(uintptr_t)0x011E90FC;
static u8* const* g_unLocoSys = (u8* const*)(uintptr_t)0x011D9140;  // Locomotion_Update's ecx
static void* const* g_unObjMgr = (void* const*)(uintptr_t)0x011DC350;
static const u8* const* g_unRmSlot = (const u8* const*)(uintptr_t)0x011D40C4;

static __declspec(thread) int t_unCls;  // 1 + class while this thread runs an agent update or a footprint update, else 0
static __declspec(thread) u8* t_unAgent;    // the agent whose update this thread runs ([UnseenThrottle]), else NULL
static __declspec(thread) float t_unPos[3];  // its owner's position (valid while t_unAgent is set)
// [UnseenThrottle] vehicle / rider holds (docs/143 §8.10): the owner is not a Sim (a vehicle agent: Car, Bicycle,
// Paddleboat, Rail, or an AnimatedBoat, which has state 1 like a walking Sim), or the owner is parented (a Sim slotted
// into a vehicle or onto a horse, or carried): such updates are never throttled. Valid while t_unAgent is set.
// A group link on the agent part (count +0xC > 0 and byte +0x20): the test 0x83A280, ObstacleAvoidance (0x84744E) and
// 0x85D980 (0x85F183) make before calling into the first linked agent (0x839CA0(part, 0)); the rate part held it from the
// start, avoidance and the obstacle list hold it since 0.36.15's crash fix (docs/143 §8.11). Caller has a __try.
static bool UtGroupLinked(const u8* agent) { return *(const LONG*)(agent + 8 + 0xC) > 0 && agent[8 + 0x20] != 0; }
static __declspec(thread) int t_unKind;
static __declspec(thread) bool t_unRider;
// Ridden horses, their riders and horses being mounted / dismounted, pushed by ts3fixes (UnseenViewers.cs) on the viewer
// channel (codes 12 BEGIN, 13 id low, 14 id high = append, 15 END with the count); held like a parented rider in every
// part. Older than 2 s = empty. Sim thread only.
enum { UT_HMAX = 32 };
static struct { DWORD lo[UT_HMAX], hi[UT_HMAX]; int n; DWORD at; } g_utHeld, g_utHeldIn;
static bool g_utHeldOpen;
static DWORD g_utHeldLo;
static bool UtHeldObj(const void* obj) {
    if (!g_utHeld.n || GetTickCount() - g_utHeld.at > 2000) return false;
    __try {
        const DWORD lo = *(const DWORD*)((const u8*)obj + 8), hi = *(const DWORD*)((const u8*)obj + 0xC);
        for (int i = 0; i < g_utHeld.n; i++)
            if (g_utHeld.lo[i] == lo && g_utHeld.hi[i] == hi) return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return false;
}
static DWORD g_unSimTid;                // the thread that runs Locomotion_Update (learned at the first agent)
static struct { float pos[3], tgt[3], reach; bool ok; LARGE_INTEGER at; } g_unCam;  // sim thread only
static void* g_unLast[512];  // last class per agent (hash slot): agent pointer, class in bit 0
enum { UN_STEER, UN_AVOID, UN_FOOT, UN_OTHER };

// visible (0) or unseen (1) for a world position, given the on-screen answer; no camera = visible (never "unseen" by error)
static int UnClassify(const float* p, bool onScreen) {
    if (onScreen || !g_unCam.ok) return 0;
    float d2t = 0, d2p = 0;
    for (int i = 0; i < 3; i++) {
        d2t += (p[i] - g_unCam.tgt[i]) * (p[i] - g_unCam.tgt[i]);
        d2p += (p[i] - g_unCam.pos[i]) * (p[i] - g_unCam.pos[i]);
    }
    const float r2 = g_unCam.reach * g_unCam.reach;
    return d2t < r2 || d2p < r2 ? 0 : 1;
}

static void UnRefreshCameraBody() {
    __declspec(align(16)) float a[4] = {}, b[4] = {};
    g_unCam.ok = false;
    __try {
        if (g_unFrustum() && g_unCamPos(a) && g_unCamTgt(b)) {
            memcpy(g_unCam.pos, a, 12), memcpy(g_unCam.tgt, b, 12);
            const float dy = a[1] > b[1] ? a[1] - b[1] : b[1] - a[1];
            g_unCam.reach = 2 * dy > 80.0f ? 2 * dy : 80.0f;
            g_unCam.ok = true;
            ts3native_counters.unReachX10 = (LONG)(g_unCam.reach * 10);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (!g_unCam.ok) InterlockedIncrement(&ts3native_counters.unCamFail);
    QueryPerformanceCounter(&g_unCam.at);
}
static void UnRefreshCamera() {
    if (!g_ucOn) { UnRefreshCameraBody(); return; }
    UcTime(UC_CAMERA, [] { UnRefreshCameraBody(); return 0; });  // docs/227 probe
}

// class of an object (0 visible, 1 unseen) and its kind (0 Sim, 1 other); -1 = no transform or a fault
static int UnObjClassBody(void* obj, int* kind, float* posOut, bool* parented) {
    __declspec(align(16)) float pos[4], sphere[4];
    __try {
        void* tc = obj ? g_unComp(obj, NULL, *g_unBitTransform) : NULL;
        if (!tc) return -1;
        // transform +0x30 = its parent: 0x958EE0 composes the world transform through it when set [verified], so the
        // position below is the world one also for a rider; a set parent = slotted into a vehicle / onto a horse / carried
        if (parented) *parented = *(void* const*)((const u8*)tc + 0x30) != NULL;
        const float* p = g_unPos(tc, NULL, pos);
        if (posOut) memcpy(posOut, p, 12);
        sphere[0] = p[0], sphere[1] = p[1] + 1.0f, sphere[2] = p[2], sphere[3] = 1.0f;  // shortcut: fixed 1 m sphere, not model bounds
        const void* fr = g_unFrustum();
        const bool on = fr && g_unInFrustum(fr, NULL, sphere) != 0;
        if (kind) *kind = g_unComp(obj, NULL, *g_unBitSim) ? 0 : 1;
        return UnClassify(sphere, on);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}
// docs/227 §6 [UnseenTable] (unseentable.cpp UtbClass): the per-tick table's answer; -2 = no answer (off, other thread, not
// listed), the old rule runs. NULL unless installed.
static int (*g_utbClass)(void* obj, int* kind, float* posOut, bool* parented);
static int UnObjClassOld(void* obj, int* kind, float* posOut, bool* parented) {
    if (!g_ucOn) return UnObjClassBody(obj, kind, posOut, parented);
    if (GetCurrentThreadId() == g_unSimTid) UcNoteMain(obj, ts3native_counters.unPasses);  // docs/227 repeat ratio
    return UcTime(UC_CLASS, [&] { return UnObjClassBody(obj, kind, posOut, parented); });
}
static int UnObjClass(void* obj, int* kind, float* posOut = NULL, bool* parented = NULL) {
    if (g_utbClass) {
        const int c = g_utbClass(obj, kind, posOut, parented);
        if (c != -2) return c;
    }
    return UnObjClassOld(obj, kind, posOut, parented);
}

static bool g_utRateOn, g_utFpOn;  // [UnseenThrottle] phase 2 parts installed (Rate=1, Footprint=1)
// Snap-back watch (docs/143 §8.9, agent command unseen_snapback): one object id armed over the viewer channel (codes 6/7);
// the first agent update after its class flips unseen -> visible is recorded: same pass ran, owed rate time, and whether
// the avoidance / footprint calls after the flip ran (any skip after the flip counts). Sim thread only.
// skipsVis = a part skipped while the Sim was classed visible (impossible by the rules: the bug the check looks for);
// skipsUnseen = skips after the Sim was classed unseen again (the camera no longer shows it); unseenAfter = its updates
// classed unseen after the flip (2026-10-21 bp-ut2-snapback: 3 of 6 Sims had skips after the flip, all while unseen again)
static struct { DWORD lo, hi; bool armed, flipped, ran, avCalled, avRan, fpCalled, fpRan, animFlipped; LONG pass, skipsVis, skipsUnseen, unseenAfter; float owed, curDebt, animOwed; } g_utWatch;  // curDebt: Rate debt at its last update (code 18); animOwed: [UnseenAnim] owed pose time run at the Sim's first visible pose call (code 24, unseenanim.cpp)
static void UtWatchSkip(bool visible) { if (visible) g_utWatch.skipsVis++; else g_utWatch.skipsUnseen++; }
static __declspec(thread) bool t_utWatchRun;  // the watched agent's update after its flip is running
static bool UtWatched(const void* obj) {
    __try { return obj && *(const DWORD*)((const u8*)obj + 8) == g_utWatch.lo && *(const DWORD*)((const u8*)obj + 0xC) == g_utWatch.hi; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static float UtRateOwed(const u8* agent);
static bool UtRateStep(u8* agent, int c, int kind, const float* pos, float dt, float* step, float* pre);
static void UtFpFlush(bool pass);
// docs/202 §3.4.2 [UnseenFallback] FinishWalk (unseenfbc.cpp UfwHoldSkip): true = this agent is held, skip its update this pass
// (c = its class here: 0 visible, 1 unseen; a visible class ends the hold); NULL unless FinishWalk is installed
static bool (*g_unHoldSkip)(u8* agent, int c);
// docs/202 §3.9.5 [UnseenGhost] (ghost.cpp GhAgentSkip): true = this agent's owner is a ghost, skip its update; NULL unless installed
static bool (*g_unGhostSkip)(u8* agent);

static void __fastcall UnAgentThunk(void* agent, void* edx, float dt) {
    Ts3nCounters& k = ts3native_counters;
    if (t_unCls) { g_unAgentOrig(agent, edx, dt); return; }  // nested (never seen): the outer update owns the attribution
    g_unSimTid = GetCurrentThreadId();
    // a new pass = the list head's agent, or 250 ms without a refresh (the head was disabled)
    const u8* sys = *g_unLocoSys;
    const u8* head = NULL;
    __try { head = sys ? *(u8* const*)(sys + 0x40) : NULL; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if ((head && agent == head - 0x40) || !g_unCam.at.QuadPart || (now.QuadPart - g_unCam.at.QuadPart) * 4 > g_qpf.QuadPart) {
        InterlockedIncrement(&k.unPasses);
        UnRefreshCamera();
        if (g_utFpOn) UtFpFlush(true);  // footprint updates held back for unseen Sims that got no later call (phase 2)
    }
    if (g_unGhostSkip && g_unGhostSkip((u8*)agent)) return;  // a ghost: no step, no steering / avoidance, no +0x918
    int kind = 1, c = -1;
    float pos[3] = {};
    bool rider = false;
    __try { c = UnObjClass(*(void* const*)((const u8*)agent + 4), &kind, pos, &rider); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (c < 0) { InterlockedIncrement(&k.unSkipped); g_unAgentOrig(agent, edx, dt); return; }
    void** slot = &g_unLast[((uintptr_t)agent >> 4) & 511];
    const int prevCls = ((uintptr_t)*slot & ~(uintptr_t)1) == (uintptr_t)agent ? (int)((uintptr_t)*slot & 1) : -1;
    if (prevCls >= 0 && prevCls != c) InterlockedIncrement(&k.unFlips);
    *slot = (void*)((uintptr_t)agent | (uintptr_t)c);  // agents are at least 4-byte aligned
    bool watchFlip = false;  // this call is the watched agent's first update after it turned visible
    if (g_utWatch.armed && UtWatched(*(void* const*)((const u8*)agent + 4))) {
        g_utWatch.curDebt = UtRateOwed((const u8*)agent);  // lets the check prefer a Sim the Rate part is holding
        if (!g_utWatch.flipped && c == 0 && prevCls == 1) {
            g_utWatch.flipped = watchFlip = true, g_utWatch.pass = k.unPasses, g_utWatch.owed = UtRateOwed((const u8*)agent);
        } else if (g_utWatch.flipped && c == 1) {
            g_utWatch.unseenAfter++;
        }
    }
    // [UnseenThrottle] Rate=1 (phase 2): an unseen walking Sim's whole update every Every-th pass with the summed step;
    // a visible one (or any snap-back reason) runs this pass with whatever it still owes
    float pre = 0;  // owed time run first, as its own step, when dt + debt would pass behaviour B's 0.5 s clamp
    if (!rider && kind == 0) rider = UtHeldObj(*(void* const*)((const u8*)agent + 4));  // ridden / (dis)mounting horse or rider
    t_unKind = kind, t_unRider = rider;
    if (g_unHoldSkip && g_unHoldSkip((u8*)agent, c)) { t_unKind = 0, t_unRider = false; return; }  // held: no step, no event, no lock
    if (g_utRateOn &&!UtRateStep((u8*)agent, c, kind, pos, dt, &dt, &pre)) {
        if (g_utWatch.flipped && g_utWatch.armed && UtWatched(*(void* const*)((const u8*)agent + 4))) UtWatchSkip(c == 0);
        return;
    }
    if (watchFlip) g_utWatch.ran = true;
    t_utWatchRun = g_utWatch.flipped && g_utWatch.armed && UtWatched(*(void* const*)((const u8*)agent + 4));
    t_unCls = 1 + c;
    t_unAgent = (u8*)agent;
    memcpy(t_unPos, pos, 12);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    __try {
        if (pre > 0) g_unAgentOrig(agent, edx, pre);
        g_unAgentOrig(agent, edx, dt);
    } __finally {
        t_unCls = 0;
        t_unAgent = NULL;
        t_utWatchRun = false;
        t_unKind = 0, t_unRider = false;
        const LONG us = UsSince(t0);
        InterlockedIncrement(&k.unAgents[c][kind]);
        InterlockedExchangeAdd(&k.unAgentUs[c][kind], us);
        Max(&k.unAgentMaxUs[c], us);
    }
    // EA's own visibility byte, recomputed at the update's entry (0x954240 -> 0x980E00: frustum test of 3x the model bounds,
    // off beyond 100x that radius); 0 = off screen, which picks OffScreenSimBrain + SimpleBody for a walking Sim
    __try { if (!((const u8*)agent)[0xC8]) InterlockedIncrement(&k.unEaOff[c]); } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static int UnKeyGroup(DWORD key, bool waits) {
    return key == 0x0085FE10 ? UN_STEER : key == 0x00846D21 ? UN_AVOID : waits && key == 0x00833593 ? UN_FOOT : waits ? UN_OTHER : 2;
}

// LockAcquireHook: a first (non-recursive) acquire of +0x918 on the sim thread, keyed as WaitNote
static void UnAcq(DWORD key) {
    if (GetCurrentThreadId() != g_unSimTid) return;
    InterlockedIncrement(&ts3native_counters.unAcq[t_unCls ? t_unCls - 1 : 2][UnKeyGroup(key, false)]);
}

// WaitNote (owner thread): one keyed wait for the route job
static void UnWait(DWORD key, LONG us) {
    const int c = t_unCls ? t_unCls - 1 : 2, g = UnKeyGroup(key, true);
    InterlockedIncrement(&ts3native_counters.unWaitN[c][g]);
    InterlockedExchangeAdd(&ts3native_counters.unWaitUs[c][g], us);
}

static void __fastcall UnUsfHook(void* world, void* edx, void* obj, DWORD enable, DWORD force, DWORD arg) {
    if (GetCurrentThreadId() != g_unSimTid) { g_unUsfOrig(world, edx, obj, enable, force, arg); return; }
    int c = t_unCls ? t_unCls - 1 : UnObjClass(obj, NULL);
    if (c < 0) c = 2;
    const int prev = t_unCls;
    if (c < 2) t_unCls = 1 + c;  // its own +0x918 wait (key 0x833593) lands in the same class
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    __try {
        g_unUsfOrig(world, edx, obj, enable, force, arg);
    } __finally {
        t_unCls = prev;
        InterlockedIncrement(&ts3native_counters.unFp[c]);
        InterlockedExchangeAdd(&ts3native_counters.unFpUs[c], UsSince(t0));
    }
}

// IC_Route_Plan (IcPlanHook): kind 1 = a car-only route (RouteOption DisablePlanningAsPedestrian 0x20 in the option word
// route+0x158+0x28, docs/15; CarRoutingComponent.CreateRoute sets it with BeginAsCar / EndAsCar), class of the follower
// object (route+0x40/+0x44, written by Route_SetFollower 0x74B1B0 [verified]); 2 = no follower or not classified
// objOut: the follower object found by id (NULL = none); [UnseenThrottle]'s replan split keys it like an agent's owner
static void UnPlanClass(DWORD handle, int* kind, int* cls, void** objOut = NULL) {
    *kind = 0, *cls = 2;
    if (objOut) *objOut = NULL;
    __try {
        const u8* rm = *g_unRmSlot;
        const u8* route = rm ? g_unRoute(rm, NULL, handle) : NULL;
        if (!route) return;
        *kind = (*(const DWORD*)(route + 0x158 + 0x28) & 0x20) ? 1 : 0;
        const DWORD lo = *(const DWORD*)(route + 0x40), hi = *(const DWORD*)(route + 0x44);
        void* mgr = *g_unObjMgr;
        void* obj = mgr && (lo | hi) ? g_unFind(mgr, NULL, lo, hi) : NULL;
        if (objOut) *objOut = obj;
        const int c = obj ? UnObjClass(obj, NULL) : -1;
        if (c >= 0) *cls = c;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static u8* UnPlan(u8* out, DWORD handle, const void* ret) {
    int kind, c;
    if (!g_unCam.ok) UnRefreshCamera();  // plans before the first locomotion pass
    UnPlanClass(handle, &kind, &c);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    u8* r = IcCollect(0, out, handle, NULL, ret);
    InterlockedIncrement(&ts3native_counters.unPlan[kind][c]);
    InterlockedExchangeAdd(&ts3native_counters.unPlanUs[kind][c], UsSince(t0));
    return r;
}

// ---- [UnseenThrottle] phase 1 (docs/143 §6): the steering obstacle list every Nth tick for unseen walking Sims ----
// Site [verified disassembly, Steam 1.67]: Locomotion_UpdateAgent 0x957210 zeroes a local id vector (0x957388..0x957396),
// then at 0x9573B7 `call Locomotion_ObstacleList 0x85FFE0`, cdecl(part, path, &vec, 0, 0), the caller pops 0x14 and
// ignores eax. part = ebx = the agent's own +8 part, or a linked agent's (+8) when the 0x83A280 lookup at 0x957333 found
// one; path = 0xC93970(agent+8), the route the agent follows. After the call a non-empty vector goes to the agent's
// vtable +0x60 (obstacle notice); an empty one is the game's "no obstacles" path. A skipped call returns at once, so the
// vector stays empty and nothing is written. The call only happens for state agent+0x94 1 (walking Sim) or 4 (paddleboat).
// Path tests [inferred from ObstacleAvoidance 0x847390, which runs them every tick for the same agent]: remaining
// distance 0x8469F0 cdecl(path ref by value: the callee releases it, cur.lo, cur.hi) with cur = agent part vtable +0x10 (edx:eax); its first sub-path term is
// path vtable +0x12C (length, st0) minus +0xA4(cur.lo, cur.hi) (progress, st0). Sub-paths end at portals (doors, stairs)
// [inferred], so a short rest or a short progress in the current sub-path = a portal ahead or just crossed.
typedef void(__cdecl* UtListFn)(void* part, void* path, void* vec, DWORD a3, DWORD a4);
typedef float(__cdecl* UtRemainFn)(void* path, DWORD lo, DWORD hi);
typedef unsigned __int64(__fastcall* UtCurFn)(void* part, void* edx);
typedef float(__fastcall* UtLenFn)(void* path, void* edx);
typedef float(__fastcall* UtProgFn)(void* path, void* edx, DWORD lo, DWORD hi);
static const DWORD UT_LIST_CALL = 0x009573B7, UT_LIST = 0x0085FFE0;
static UtListFn g_utOrig = (UtListFn)(uintptr_t)UT_LIST;
static UtRemainFn g_utRemain = (UtRemainFn)(uintptr_t)0x008469F0;
enum { UTW_OFF, UTW_NOAGENT, UTW_VISIBLE, UTW_EAON, UTW_STATE, UTW_LINKED, UTW_VIEWER, UTW_NOVIEW, UTW_AFTER, UTW_TURN,
       UTW_NEAR, UTW_PORTAL, UTW_PATH, UTW_N };
// phase 2 only (utAvWhy / utRateWhy have UTW_N2 rows): not a Sim, short agent timer, new path, step cap, hash slot taken
enum { UTW_KIND = UTW_N, UTW_TIMER, UTW_NEWPATH, UTW_CAP, UTW_SLOT, UTW_N2 };
static bool g_utOn;
static bool g_utNeedViewers = true;
static bool g_aaOn;  // [UnseenAnim] installed (unseenanim.cpp): it reads the household table below too
static bool g_utbOn;  // [UnseenTable] installed (unseentable.cpp): same
static u8 UtbReadyMsg(DWORD code);  // unseentable.cpp: viewer-channel codes 25 (ICs ready) / 26 (Check on)
static float g_utNearM = 15.0f, g_utPortalM = 3.0f;  // 15 m = [0xF9A62C], the avoidance near-goal default
static const float UT_VIEWER_M = 80.0f;                // ts3fixes kShortcutMinDistance
static const DWORD UT_VIEWER_AGE_MS = 2000;
static void* g_utPrev[512];  // per agent (hash slot): agent pointer, bit 0 = a run reason held at its last call

// Active-household positions from ts3fixes (UnseenViewers.cs) over GetOption2, RouteOption2 bits 63 + 57 (optHi top byte
// 0x82, code in bits 16-23, value in optLo): 1 BEGIN, 2/3/4 x/y/z float bits (z appends one viewer), 5 END (value = count).
// Sim thread only (GetOptionHook). 0 = refused: the throttle is not installed (ts3fixes stops for this load) or bad order.
enum { UT_VMAX = 32 };
static struct { float pos[UT_VMAX][3]; int n; DWORD at; } g_utView, g_utIn;
static u8 UtSkipQuery(DWORD code, DWORD lo);
static bool g_utInOpen;
static DWORD g_utInHave;
static float g_utInCur[3];
static u8 UtViewerMsg(DWORD lo, DWORD hi) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD code = (hi >> 16) & 0xFF;
    if (code == 25 || code == 26) return UtbReadyMsg(code);  // docs/227 §6 [UnseenTable]: ts3fixes may call UnseenTableNative (unseentable.cpp)
    if (!g_utOn && !((g_asTierMs > 0 || g_aaOn || g_utbOn) && code <= 5)) return 0;  // the viewer table also feeds [AutShadow] TierMs, [UnseenAnim], [UnseenTable]
    if (code == 1) { g_utInOpen = true, g_utIn.n = 0, g_utInHave = 0; return 1; }
    // snap-back watch (unseen_snapback): 6 arm with the object id's low half (record cleared), 7 its high half (armed);
    // 8 bit `lo` of the record (0 armed, 1 flipped, 2 ran that same pass, 3 avoidance called, 4 avoidance ran,
    // 5 footprint called, 6 footprint ran, 7 owed time > 0); 9 owed ms >= lo; 10 skips after the flip >= lo; 11 disarm;
    // 24 [UnseenAnim] owed pose ms at its first visible pose call >= lo (0 until that call)
    if (code >= 12 && code <= 15) {  // held objects (horses): see g_utHeld
        if (code == 12) { g_utHeldOpen = true, g_utHeldIn.n = 0; return 1; }
        if (!g_utHeldOpen) { k.utViewerBad++; return 0; }
        if (code == 13) { g_utHeldLo = lo; return 1; }
        if (code == 14) {
            if (g_utHeldIn.n == UT_HMAX) { g_utHeldOpen = false, k.utViewerBad++; return 0; }
            g_utHeldIn.lo[g_utHeldIn.n] = g_utHeldLo, g_utHeldIn.hi[g_utHeldIn.n++] = lo;
            return 1;
        }
        g_utHeldOpen = false;
        if ((DWORD)g_utHeldIn.n != lo) { k.utViewerBad++; return 0; }
        g_utHeld = g_utHeldIn;
        g_utHeld.at = GetTickCount() ? GetTickCount() : 1;
        return 1;
    }
    if ((code >= 6 && code <= 11) || (code >= 16 && code <= 18) || code == 24) {
        if (code == 6) { memset(&g_utWatch, 0, sizeof(g_utWatch)); g_utWatch.lo = lo; return 1; }
        if (code == 7) { g_utWatch.hi = lo, g_utWatch.armed = true; return 1; }
        if (code == 11) { g_utWatch.armed = false; return 1; }
        if (code == 9) return g_utWatch.owed * 1000.0f >= (float)lo ? 1 : 0;
        if (code == 10) return g_utWatch.skipsVis >= (LONG)lo ? 1 : 0;
        if (code == 16) return g_utWatch.skipsUnseen >= (LONG)lo ? 1 : 0;
        if (code == 17) return g_utWatch.unseenAfter >= (LONG)lo ? 1 : 0;
        if (code == 18) return g_utWatch.curDebt * 1000.0f >= (float)lo ? 1 : 0;
        if (code == 24) return g_utWatch.animOwed * 1000.0f >= (float)lo ? 1 : 0;
        const bool b[8] = {g_utWatch.armed, g_utWatch.flipped, g_utWatch.ran, g_utWatch.avCalled, g_utWatch.avRan,
                           g_utWatch.fpCalled, g_utWatch.fpRan, g_utWatch.owed > 0};
        return lo < 8 && b[lo] ? 1 : 0;
    }
    if (code >= 19 && code <= 23) return UtSkipQuery(code, lo);
    if (!g_utInOpen || code < 2 || code > 5) { k.utViewerBad++; return 0; }
    if (code <= 4) {
        memcpy(&g_utInCur[code - 2], &lo, 4);
        g_utInHave |= 1u << (code - 2);
        if (code < 4) return 1;
        if (g_utInHave != 7 || g_utIn.n == UT_VMAX) { g_utInOpen = false, k.utViewerBad++; return 0; }
        memcpy(g_utIn.pos[g_utIn.n++], g_utInCur, 12);
        g_utInHave = 0;
        return 1;
    }
    g_utInOpen = false;
    if ((DWORD)g_utIn.n != lo) { k.utViewerBad++; return 0; }
    g_utView = g_utIn;
    g_utView.at = GetTickCount();
    if (!g_utView.at) g_utView.at = 1;
    k.utViewers = g_utView.n;
    k.utViewerPushes++;
    k.utReady |= 2;
    return 1;
}

// near a viewer (ts3fixes FarFromViewers' household part), or no fresh table while NeedViewers=1; -1 = neither
static int UtViewerWhyBody(const float* p) {
    if (!g_utView.at || GetTickCount() - g_utView.at > UT_VIEWER_AGE_MS) return g_utNeedViewers ? UTW_NOVIEW : -1;
    for (int i = 0; i < g_utView.n; i++) {
        float d2 = 0;
        for (int j = 0; j < 3; j++) d2 += (p[j] - g_utView.pos[i][j]) * (p[j] - g_utView.pos[i][j]);
        if (d2 < UT_VIEWER_M * UT_VIEWER_M) return UTW_VIEWER;
    }
    return -1;
}
static int UtViewerWhy(const float* p) { return g_ucOn ? UcTime(UC_VIEWER, [&] { return UtViewerWhyBody(p); }) : UtViewerWhyBody(p); }

// [AutShadow] TierMs (autshadow.cpp, docs/122 §14.16): 1 = tier 2 (a far unseen Sim: off screen, beyond the camera reach, no
// active-household viewer within 80 m in a fresh table), 0 = tier 1 (also when anything is unknown). id 0: start of a cut.
// id 0 returns 1 when the camera moved more than 8 m (position or target) or its reach changed since the last 1: the
// cached tiers are classified again (TierClassMs cache, docs/122 §14.17)
static int AsTierOf(AsU64 id) {
    if (!id) {
        static float s_last[7];
        static bool s_have;
        UnRefreshCamera();
        if (!g_unCam.ok) return 0;
        float d2p = 0, d2t = 0;
        for (int i = 0; i < 3; i++) {
            d2p += (g_unCam.pos[i] - s_last[i]) * (g_unCam.pos[i] - s_last[i]);
            d2t += (g_unCam.tgt[i] - s_last[3 + i]) * (g_unCam.tgt[i] - s_last[3 + i]);
        }
        const float dr = g_unCam.reach - s_last[6];
        if (s_have && d2p < 64.0f && d2t < 64.0f && dr < 8.0f && dr > -8.0f) return 0;
        memcpy(s_last, g_unCam.pos, 12), memcpy(s_last + 3, g_unCam.tgt, 12), s_last[6] = g_unCam.reach, s_have = true;
        return 1;
    }
    if (!g_unCam.ok) return 0;
    float p[3];
    void* obj = NULL;
    __try {
        void* mgr = *g_unObjMgr;
        obj = mgr ? g_unFind(mgr, NULL, (DWORD)id, (DWORD)(id >> 32)) : NULL;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
    AsObjSeen(obj);  // CutLean=2: the Sim's lot is placed at the next cut (tier-1 lots)
    if (!obj || UnObjClass(obj, NULL, p) != 1) return 0;
    const bool need = g_utNeedViewers;
    g_utNeedViewers = true;  // no fresh viewer table: tier 1 (never far by error)
    const int why = UtViewerWhy(p);
    g_utNeedViewers = need;
    return why < 0 ? 1 : 0;
}

// Snap-backs to full rate caused by becoming visible (v80 utSnap / utSnapMaxMs, parts 0 list, 1 avoidance, 2 footprint,
// 3 rate): a call that runs because the agent is visible after its previous call of that part was skipped; owed = the
// time since that part last ran for it (rate: the owed step).
static void UtSnapNote(int part, DWORD owedMs) {
    Ts3nCounters& k = ts3native_counters;
    k.utSnap[part]++;
    Max(&k.utSnapMaxMs[part], (LONG)owedMs);
}
static struct { u8* agent; DWORD lastRun; bool skipped; } g_utSnapT[2][512];  // list, avoidance (hash slot by agent)
static void UtSnapTrack(int part, u8* agent, int why) {
    if (!agent) return;
    auto& e = g_utSnapT[part][((uintptr_t)agent >> 4) & 511];
    const DWORD now = GetTickCount();
    if (e.agent == agent && e.skipped && why == UTW_VISIBLE) UtSnapNote(part, now - e.lastRun);
    if (e.agent != agent) e.agent = agent, e.lastRun = now, e.skipped = false;
    if (why < 0) e.skipped = true;
    else e.skipped = false, e.lastRun = now;
}

// ---- [UnseenThrottle] diagnostics (docs/143 §8.14, counters v83, observe only) ----
// Per object (the agent's owner +4, or the footprint object): when each part last skipped it and whether its last obstacle
// list was non-empty (the list's run posts the agent's vtable +0x60 notice then: `cmp eax,edx` at 0x9573C7 on the vector's
// begin/end [verified disassembly]). The replan detours read it for the route's follower, found by id (UnPlanClass): the
// follower object and the steering agent's owner are the same object [inferred], so class 0 staying 0 while the parts skip
// would refute that, not the hypothesis.
enum { UT_DG_LIST, UT_DG_AVOID, UT_DG_RATE, UT_DG_FOOT, UT_DG_N };
static const LONG UT_DG_RECENT = 8;  // passes: "throttled lately" (2 x Every at the default Every=4)
struct UtDgSlot { const void* owner; LONG pass; DWORD at[UT_DG_N]; bool obst; };
static UtDgSlot g_utDg[512];  // sim thread only
static UtDgSlot* UtDgFor(const void* owner, bool claim) {
    if (!owner) return NULL;
    UtDgSlot& s = g_utDg[((uintptr_t)owner >> 4) & 511];
    if (s.owner != owner) {
        if (!claim) return NULL;
        memset(&s, 0, sizeof(s));
        s.owner = owner, s.pass = ts3native_counters.unPasses - 1000;
    }
    return &s;
}
static const void* UtOwner(const u8* agent) {
    __try { return agent ? *(void* const*)(agent + 4) : NULL; } __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
}
// a skipped call of `part` for this object; a list / avoidance / rate skip while its last list was non-empty = a deferred notice
static void UtDgSkip(int part, const void* owner) {
    UtDgSlot* s = UtDgFor(owner, true);
    if (!s) return;
    s->pass = ts3native_counters.unPasses;
    s->at[part] = GetTickCount() | 1;
    if (part != UT_DG_FOOT && s->obst) ts3native_counters.utDefer[part]++;
}

// Per-Sim skip read for the agent's stranding check (unseen_water_cause, docs/143 §8.16), viewer channel, sim thread, read
// only: 19 object id low half, 20 its high half (finds that object's g_utDg slot by the id at owner +8/+0xC, as UtWatched;
// 1 = found), 21 newest skip of any part <= lo ms ago, 22 the newest skip's part == lo (UT_DG_*), 23 any part switched on
// (utMode / utAvMode / utRateMode / utFpMode: the --ab side). The agent reads the age by bisecting code 21.
static DWORD g_utQLo;
static const UtDgSlot* g_utQ;
static u8 UtSkipQuery(DWORD code, DWORD lo) {
    const Ts3nCounters& k = ts3native_counters;
    if (code == 23) return k.utMode == 1 || k.utAvMode == 1 || k.utRateMode == 1 || k.utFpMode == 1 ? 1 : 0;
    if (code == 19) { g_utQLo = lo, g_utQ = NULL; return 1; }
    if (code == 20) {
        g_utQ = NULL;
        for (int i = 0; i < 512 && !g_utQ; i++) __try {
                const u8* o = (const u8*)g_utDg[i].owner;
                if (o && *(const DWORD*)(o + 8) == g_utQLo && *(const DWORD*)(o + 0xC) == lo) g_utQ = &g_utDg[i];
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
        return g_utQ ? 1 : 0;
    }
    if (!g_utQ) return 0;
    const DWORD now = GetTickCount();
    int part = -1;
    DWORD age = 0;
    for (int p = 0; p < UT_DG_N; p++)
        if (g_utQ->at[p] && (part < 0 || now - g_utQ->at[p] < age)) part = p, age = now - g_utQ->at[p];
    if (part < 0) return 0;
    return (code == 21 ? age <= lo : (DWORD)part == lo) ? 1 : 0;
}

// IC_Route_Replan 0x765490 cdecl(result*, handle) and IC_Route_ReplanFromPosition 0x765550 cdecl(result*, handle, x, y, z)
// [verified, docs/87 §2; a10.cpp]: timed whole, as stutter-profile.js times its byEntry names, split by the follower
typedef u8*(__cdecl* UtRpFn)(u8* out, DWORD handle);
typedef u8*(__cdecl* UtRpPosFn)(u8* out, DWORD handle, float x, float y, float z);
static UtRpFn g_utRpOrig;
static UtRpPosFn g_utRpPosOrig;
struct UtRpCls { int c, part; DWORD ms; };
static UtRpCls UtRpClass(DWORD handle) {
    UtRpCls r = {2, -1, 0};
    int kind, cls;
    void* obj;
    UnPlanClass(handle, &kind, &cls, &obj);
    if (!obj) return r;
    r.c = 1;
    const UtDgSlot* s = UtDgFor(obj, false);
    if (!s || ts3native_counters.unPasses - s->pass > UT_DG_RECENT) return r;
    r.c = 0;
    const DWORD now = GetTickCount();
    for (int p = 0; p < UT_DG_N; p++)  // the part that skipped it last
        if (s->at[p] && (r.part < 0 || now - s->at[p] < r.ms)) r.part = p, r.ms = now - s->at[p];
    return r;
}
static void UtRpNote(int ic, const UtRpCls& r, LARGE_INTEGER t0) {
    Ts3nCounters& k = ts3native_counters;
    const LONG us = UsSince(t0);
    k.utRpN[ic][r.c]++;
    k.utRpUs[ic][r.c] += us;
    if (us >= 100000) k.utRpLong[ic][r.c]++;
    if (r.part >= 0) k.utRpPart[ic][r.part]++, k.utRpPartMs[ic][r.part] += (LONG)r.ms;
}
static u8* __cdecl UtRpHook(u8* out, DWORD handle) {
    if (GetCurrentThreadId() != g_unSimTid) return g_utRpOrig(out, handle);
    const UtRpCls r = UtRpClass(handle);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    u8* res = g_utRpOrig(out, handle);
    UtRpNote(0, r, t0);
    return res;
}
static u8* __cdecl UtRpPosHook(u8* out, DWORD handle, float x, float y, float z) {
    if (GetCurrentThreadId() != g_unSimTid) return g_utRpPosOrig(out, handle, x, y, z);
    const UtRpCls r = UtRpClass(handle);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    u8* res = g_utRpPosOrig(out, handle, x, y, z);
    UtRpNote(1, r, t0);
    return res;
}

// within nearM of the goal, or a portal ahead / just crossed within portalM (portalM < 0: no portal test); a fault or no
// path = run (UTW_PATH); -1 = may skip
static int UtPathWhy(u8* agent, void* path, float nearM, float portalM) {
    if (!path) return UTW_PATH;
    __try {
        u8* part = agent + 8;
        const unsigned __int64 cur = ((UtCurFn)(*(void***)part)[0x10 / 4])(part, NULL);
        const DWORD lo = (DWORD)cur, hi = (DWORD)(cur >> 32);
        // 0x8469F0 takes the path as a by-value intrusive ref (count at +4) and releases it (0x846A7E / 0x846AB2), so add
        // one first as ObstacleAvoidance does at 0x847532 [verified]; without it every call freed a ref and the path went
        // away under the agent (0.36.13 bp-ut-play hang, docs/143 §6.6). A fault inside leaks the ref (safe side).
        InterlockedIncrement((volatile LONG*)((u8*)path + 4));
        const float rest = g_utRemain(path, lo, hi);
        if (!(rest > nearM)) return UTW_NEAR;  // NaN runs too
        if (portalM < 0) return -1;
        void** vt = *(void***)path;
        const float len = ((UtLenFn)vt[0x12C / 4])(path, NULL), prog = ((UtProgFn)vt[0xA4 / 4])(path, NULL, lo, hi);
        if (!(len - prog > portalM) || !(prog > portalM)) return UTW_PORTAL;
        return -1;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return UTW_PATH; }
}

// why this call runs (UTW_*), or -1 to skip it. Rules (docs/143 §4): visible by the probe's rule or EA's byte, not a
// walking Sim, linked agent, near a viewer; the first call after any of those (or a path reason) held, or of an agent not
// seen before; the agent's stagger turn ((pass + owner id) % N == 0); near goal or portal.
static int UtWhyBody(u8* part, void* path) {
    Ts3nCounters& k = ts3native_counters;
    if (k.utMode != 1) return UTW_OFF;
    u8* agent = t_unAgent;
    if (!agent || !t_unCls) return UTW_NOAGENT;
    int held = -1;
    DWORD id = 0;
    __try {
        if (t_unCls != 2) held = UTW_VISIBLE;
        else if (agent[0xC8]) held = UTW_EAON;
        else if (*(const DWORD*)(agent + 0x94) != 1 || t_unKind != 0) held = UTW_STATE;  // vehicles incl. AnimatedBoat (state 1)
        else if (part != agent + 8 || t_unRider || UtGroupLinked(agent)) held = UTW_LINKED;  // linked agent, parented rider, group link
        const u8* owner = *(u8* const*)(agent + 4);
        if (owner) id = *(const DWORD*)(owner + 8);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return UTW_NOAGENT; }
    if (held < 0) held = UtViewerWhy(t_unPos);
    void** slot = &g_utPrev[((uintptr_t)agent >> 4) & 511];
    const bool known = ((uintptr_t)*slot & ~(uintptr_t)1) == (uintptr_t)agent;
    int why = held;
    if (why < 0 && (!known || ((uintptr_t)*slot & 1))) why = UTW_AFTER;
    if (why < 0 && ((DWORD)k.unPasses + id) % (DWORD)k.utEvery == 0) why = UTW_TURN;
    if (why < 0) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        why = held = UtPathWhy(agent, path, g_utNearM, g_utPortalM);
        k.utPathUs += UsSince(t0);
    }
    *slot = (void*)((uintptr_t)agent | (held >= 0 ? 1 : 0));
    return why;
}

static int UtWhy(u8* part, void* path) { return g_ucOn ? UcTime(UC_LIST, [&] { return UtWhyBody(part, path); }) : UtWhyBody(part, path); }

static void __cdecl UtListThunk(void* part, void* path, void* vec, DWORD a3, DWORD a4) {
    Ts3nCounters& k = ts3native_counters;
    k.utCalls++;  // sim thread only
    const int why = UtWhy((u8*)part, path);
    if (k.utMode == 1) UtSnapTrack(0, t_unAgent, why);
    if (why < 0) {
        k.utSkipped++;
        if (t_utWatchRun) UtWatchSkip(t_unCls == 1);
        UtDgSkip(UT_DG_LIST, UtOwner(t_unAgent));
        return;  // the caller's vector stays empty: "no obstacles"
    }
    k.utWhy[why]++;
    k.utRun++;
    g_utOrig(part, path, vec, a3, a4);
    if (t_unAgent) {  // v83: the notice this run posts (non-empty vector: begin [vec], end [vec+4]) for the deferral count
        const bool obst = ((const DWORD*)vec)[0] != ((const DWORD*)vec)[1];
        if (obst && t_unCls == 2) k.utListNotices++;
        if (UtDgSlot* s = UtDgFor(UtOwner(t_unAgent), obst)) s->obst = obst;
    }
}

// ---- [UnseenThrottle] phase 2 (docs/143 §8, counters v80): avoidance, footprint updates, whole-update rate ----
// The tail every phase-2 rule shares after its hold tests: the first call after a held reason (*heldBit, true for an
// agent not seen before), the stagger turn (the phase-1 turn, so every part of one agent runs on the same pass and a
// removed +0x918 wait is not just handed from one taker to the next), then the path tests. -1 = may skip.
static int UtTail(int held, bool* heldBit, DWORD id, u8* agent, void* path, float nearM, float portalM) {
    Ts3nCounters& k = ts3native_counters;
    int why = held;
    if (why < 0 && *heldBit) why = UTW_AFTER;
    if (why < 0 && ((DWORD)k.unPasses + id) % (DWORD)k.utEvery == 0) why = UTW_TURN;
    if (why < 0) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        why = held = UtPathWhy(agent, path, nearM, portalM);
        k.utPathUs += UsSince(t0);
    }
    *heldBit = held >= 0;
    return why;
}

// (1) Avoidance. The behaviour slot +0xC of vtable 0x1026E04 (`.rdata` 0x1026E10) holds ObstacleAvoidance 0x847390,
// thiscall(behaviour; float dt), `ret 4` [verified: vtable words and the epilogue at 0x847708]; it is the behaviour update
// both Sim brains (SimBrain, OffScreenSimBrain) and the paddleboat brain run every tick (docs/143 §2.3). Its effects are
// writes only: the agent part's avoidance record (0x85D760 x2, 0x85D980 with the agent radius), then, when the route rest
// 0x8469F0 is under max(15 m [0xF9A62C], 2 x radius [0xF98A58]), the goal footprint gather and RouteFollow_A 0x846B70
// (the +0x918 taker with key 0x846D21) and on a hit the agent's vtable +0x64 "goal obstructed" notice [verified
// disassembly 0x847390..0x847708]. Nothing reads a result: a skipped tick leaves the last run's record and posts no notice,
// so a goal obstruction is noticed up to Every-1 ticks later (docs/143 §4 (c)) [inferred: no reader needs a per-tick
// refresh of the record]. The behaviour's agent part is [behaviour+0xC] (the same `mov ecx,[esi+0xC]` feeds 0xC93970).
typedef void(__fastcall* UtAvFn)(void* beh, void* edx, float dt);
static const DWORD UT_AV_SLOT = 0x01026E10, UT_AV = 0x00847390;
static UtAvFn g_utAvOrig = (UtAvFn)(uintptr_t)UT_AV;
static float g_utAvNearM = 3.0f;
static void* g_utAvPrev[512];  // as g_utPrev: agent pointer, bit 0 = a run reason held at its last call

static int UtAvWhy(u8* beh) {
    Ts3nCounters& k = ts3native_counters;
    if (k.utAvMode != 1) return UTW_OFF;
    u8* agent = t_unAgent;
    if (!agent || !t_unCls) return UTW_NOAGENT;  // outside a classified agent update
    int held = -1;
    DWORD id = 0;
    void* path = NULL;
    __try {
        if (*(u8* const*)(beh + 0xC) != agent + 8) return UTW_LINKED;  // not the updating agent's own behaviour
        if (t_unCls != 2) held = UTW_VISIBLE;
        else if (agent[0xC8]) held = UTW_EAON;
        else if (*(const DWORD*)(agent + 0x94) != 1) held = UTW_STATE;  // paddleboats run every tick
        else if (t_unKind != 0) held = UTW_KIND;    // an AnimatedBoat (state 1, not a Sim)
        else if (t_unRider || UtGroupLinked(agent)) held = UTW_LINKED;  // a parented rider or a group link (docs/143 §8.11)
        const u8* owner = *(u8* const*)(agent + 4);
        if (owner) id = *(const DWORD*)(owner + 8);
        path = *(void* const*)(agent + 0x10);  // part+8 = the route the agent follows (0xC93970 returns [ecx+8])
    } __except (EXCEPTION_EXECUTE_HANDLER) { return UTW_NOAGENT; }
    if (held < 0) held = UtViewerWhy(t_unPos);
    void** slot = &g_utAvPrev[((uintptr_t)agent >> 4) & 511];
    bool hb = ((uintptr_t)*slot & ~(uintptr_t)1) != (uintptr_t)agent || ((uintptr_t)*slot & 1);
    const int why = UtTail(held, &hb, id, agent, path, g_utAvNearM, -1);
    *slot = (void*)((uintptr_t)agent | (hb ? 1 : 0));
    return why;
}

static void __fastcall UtAvHook(u8* beh, void* edx, float dt) {
    Ts3nCounters& k = ts3native_counters;
    k.utAvCalls++;  // sim thread (the locomotion pass); a call elsewhere has no agent context and runs
    const int why = UtAvWhy(beh);
    if (k.utAvMode == 1) UtSnapTrack(1, t_unAgent, why);
    if (t_utWatchRun) {
        g_utWatch.avCalled = true;
        if (why < 0) UtWatchSkip(t_unCls == 1);
        else g_utWatch.avRan = true;
    }
    if (why < 0) { k.utAvSkipped++; UtDgSkip(UT_DG_AVOID, UtOwner(t_unAgent)); return; }
    k.utAvWhy[why]++;
    g_utAvOrig(beh, edx, dt);
}

// (4) Whole agent update every Every-th pass for an unseen walking Sim, with the summed step. Behaviour B (OffScreenSimBrain's
// path follower 0x847830, the brain every unseen Sim has: EA's byte is 0 for 100% of unseen updates, docs/143 §5) clamps
// its dt to [1/30 s [0x10434C4], 0.5 s [0xF9A5AC]] at entry [verified 0x847852..0x847896], so a step up to 0.5 s is one
// EA already takes after a hitch. Each skipped pass adds max(dt, 1/30) to the agent's debt (what behaviour B would have
// used that pass), the run passes dt' = max(dt, 1/30) + debt (exact when no pass was skipped: then dt itself), a pass
// whose next skip could pass 0.5 s runs (UTW_CAP), and when a dt spike would still take dt' over 0.5 s the debt runs as
// its own update first (two updates that pass, each within the clamp). The agent timer at +0xE0/+0xE4 (vtable +0x68 once per period,
// 0x9572A5..0x9572F3) sums the same dt; an agent with a period under 1 s runs every pass (UTW_TIMER). Snap-back as
// phase 1 plus: not a Sim (cars, boats), a group link (part +0xC > 0 and byte part +0x20, the test 0x83A280 and
// ObstacleAvoidance make before looking for a linked agent [inferred meaning]), a new path (debt carried into it), and
// RateNearM / PortalM on the path. A skipped pass does not call the game at all: no on-screen byte refresh, no behaviours,
// no timer; the visible test runs every pass (ours), so a Sim that comes into view runs that same pass with its debt.
static const float UT_MIN_STEP = 1.0f / 30.0f, UT_MAX_STEP = 0.5f;
static float g_utRateNearM = 3.0f;
struct UtRateSlot { u8* agent; void* path; float debt; LONG pass; bool held; };
static UtRateSlot g_utRate[512];

static int UtRateWhy(u8* agent, int c, int kind, const float* pos, UtRateSlot& s, float dt) {
    Ts3nCounters& k = ts3native_counters;
    if (k.utRateMode != 1) return UTW_OFF;
    int held = c != 1 ? UTW_VISIBLE : kind != 0 ? UTW_KIND : -1;
    DWORD id = 0;
    void* path = NULL;
    __try {
        if (held < 0) {
            const u8* part = agent + 8;
            const float period = *(const float*)(agent + 0xE0);
            if (agent[0xC8]) held = UTW_EAON;
            else if (*(const DWORD*)(agent + 0x94) != 1) held = UTW_STATE;
            else if ((*(const LONG*)(part + 0xC) > 0 && part[0x20]) || t_unRider) held = UTW_LINKED;  // group link or a parented rider
            else if (period > 0 && *(const float*)(agent + 0xC0) == 0 && period < 2 * UT_MAX_STEP) held = UTW_TIMER;
        }
        const u8* owner = *(u8* const*)(agent + 4);
        if (owner) id = *(const DWORD*)(owner + 8);
        path = *(void* const*)(agent + 0x10);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return UTW_NOAGENT; }
    if (held < 0) held = UtViewerWhy(pos);
    if (held < 0 && !path) held = UTW_PATH;  // the game returns at once without a path; nothing to save
    if (path != s.path) {  // a new route: start it now (the debt goes into its first step)
        s.path = path;
        if (held < 0) return UTW_NEWPATH;
    }
    // one more skipped pass could take the run's step over behaviour B's 0.5 s clamp: run now
    if (held < 0 && !s.held && s.debt + 2 * (dt > UT_MIN_STEP ? dt : UT_MIN_STEP) > UT_MAX_STEP) return UTW_CAP;
    return UtTail(held, &s.held, id, agent, path, g_utRateNearM, g_utPortalM);
}

// true = run now with *step; false = skipped (the pass's step joins the agent's debt)
static bool UtRateStep(u8* agent, int c, int kind, const float* pos, float dt, float* step, float* pre) {
    Ts3nCounters& k = ts3native_counters;
    k.utRateCalls++;  // sim thread only
    *step = dt;
    const LONG pass = k.unPasses;
    UtRateSlot& s = g_utRate[((uintptr_t)agent >> 4) & 511];
    if (s.agent != agent) {
        // another live agent owes time in this slot: this one is not throttled (the owner keeps its debt)
        if (s.agent && s.debt > 0 && pass - s.pass <= 1) { k.utRateWhy[UTW_SLOT]++; return true; }
        if (s.agent && s.debt > 0) k.utRateDropped++;
        s.agent = agent, s.path = NULL, s.debt = 0, s.held = true;
    } else if (s.debt > 0 && pass - s.pass > 1) {  // not called last pass (disabled or out of the list meanwhile)
        k.utRateDropped++;
        s.debt = 0;
    }
    s.pass = pass;
    const float d = dt > UT_MIN_STEP ? dt : UT_MIN_STEP;
    const int why = UtRateWhy(agent, c, kind, pos, s, dt);
    if (why < 0) {
        s.debt += d;
        k.utRateSkipped++;
        UtDgSkip(UT_DG_RATE, UtOwner(agent));
        return false;
    }
    k.utRateWhy[why]++;
    if (why == UTW_VISIBLE && s.debt > 0) UtSnapNote(3, (DWORD)(s.debt * 1000.0f + 0.5f));
    if (s.debt > 0) {
        if (d + s.debt <= UT_MAX_STEP) *step = d + s.debt;
        else *pre = s.debt;  // a dt spike after skips: the owed time as its own update first, then this pass's dt
        Max(&k.utRateDebtMaxUs, (LONG)((*pre > 0 ? *pre : *step) * 1e6f));
        s.debt = 0;
        k.utRateCatchUp++;
    }
    return true;
}

static float UtRateOwed(const u8* agent) {
    const UtRateSlot& s = g_utRate[((uintptr_t)agent >> 4) & 511];
    return s.agent == agent ? s.debt : 0;
}

// (2) Footprint updates. UpdateSimFootprint 0x833560 is remove-all plus an optional re-insert of the object's routing
// footprints from its current component state (docs/89 §1, §10.2; design U's collapse rule relies on the same [verified
// 0x833560..0x833675]), so of several calls with the same (enable, arg) only the last one's effect is left. For an
// unseen Sim (probe class, not near a viewer) an enable call within FpMs of the object's last applied update is held
// back as one pending update per object (later equal calls fold into it); it is applied when its FpMs are up, at the
// next footprint call or pass start with no route job running (or 2 x FpMs late regardless), directly into the layer
// below (the game's body, or the probe behind us) with the object looked up again by id (gone or reused = dropped).
// Any other call for that object (visible, disable, other arguments, parked in the lock-key map, nested under +0x918 as
// design U's drains are, other thread) drops the pending one first and runs at once: the later call decides the state.
// Readers in between see the object's footprint at its last applied position (at most FpMs old, unseen Sims only).
enum { UTF_OFF, UTF_PASS, UTF_NOCLASS, UTF_VISIBLE, UTF_KIND, UTF_VIEWER, UTF_NOVIEW, UTF_PARKED, UTF_CHANGE, UTF_DUE, UTF_FULL,
       UTF_RIDER, UTF_N };  // UTF_RIDER: the object is parented (in a vehicle, on a horse, carried); the array's spare row 11
static UnUsfFn g_utFpNext;  // the layer below this hook (game body / probe); never NULL while g_utFpOn
static DWORD g_utFpMs = 500;
static __declspec(thread) bool t_utFpIn;  // our own flush call is running
struct UtFpPend { DWORD idLo, idHi, due, en, arg, force; void* obj; void* world; };
enum { UT_FP_MAX = 64 };
static UtFpPend g_utFpPend[UT_FP_MAX];
static int g_utFpN;
static struct { DWORD idLo, idHi, t; } g_utFpLast[256];  // last applied update per object (hash slot)

static DWORD* UtFpLastSlot(DWORD idLo, DWORD idHi, bool claim) {
    auto& e = g_utFpLast[(idLo ^ (idLo >> 8) ^ idHi) & 255];
    if (e.idLo != idLo || e.idHi != idHi) {
        if (!claim) return NULL;
        e.idLo = idLo, e.idHi = idHi, e.t = 0;
    }
    return &e.t;
}
static int UtFpFind(DWORD idLo, DWORD idHi) {
    for (int i = 0; i < g_utFpN; i++)
        if (g_utFpPend[i].idLo == idLo && g_utFpPend[i].idHi == idHi) return i;
    return -1;
}
static void UtFpDrop(int i) { g_utFpPend[i] = g_utFpPend[--g_utFpN]; }

// apply pending updates whose time is up; pass = called at a locomotion pass start
static void UtFpFlush(bool pass) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_utFpN || t_utFpIn || GetCurrentThreadId() != g_unSimTid) return;
    const DWORD now = GetTickCount();
    const bool job = g_async.state == A_RUNNING;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < g_utFpN;) {
        const int late = (int)(now - g_utFpPend[i].due);
        if (late < 0 || (job && late < 2 * (int)g_utFpMs)) { i++; continue; }
        const UtFpPend p = g_utFpPend[i];
        UtFpDrop(i);  // before the call: a nested footprint call sees no pending entry for it
        void* svc = g_rgLock == (u8*)p.world + 0x918 ? g_objSvc() : NULL;  // the same world (travel drops the rest)
        void* obj = NULL;
        if (!svc || !g_findRef(svc, NULL, p.idLo, p.idHi, &obj)) obj = NULL;
        if (obj && obj == p.obj) {
            t_utFpIn = true;
            g_utFpNext(p.world, NULL, obj, p.en, p.force, p.arg);
            t_utFpIn = false;
            k.utFpFlushed++;
            if (DWORD* t = UtFpLastSlot(p.idLo, p.idHi, true)) *t = now ? now : 1;
        } else {
            k.utFpGone++;
        }
        if (obj) g_objRelease(&obj, NULL);
    }
    (void)pass;
    k.utFpFlushUs += UsSince(t0);
}

static void __fastcall UtFpHook(void* world, void* edx, void* obj, DWORD enable, DWORD force, DWORD arg) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD me = GetCurrentThreadId();
    int why = -1;
    if (k.utFpMode != 1) why = UTF_OFF;
    else if (!obj || me != g_unSimTid || t_utFpIn || t_snapRun || g_inInvoke || g_discDraining || k.rgOwnerTid == (LONG)me ||
             !g_rgLock || (u8*)world + 0x918 != g_rgLock)
        why = UTF_PASS;
    if (why >= 0) {
        if (obj && me == g_unSimTid && g_utFpN) {  // this later call decides the object's state: forget a held-back one
            const int i = UtFpFind(*(const DWORD*)((u8*)obj + 8), *(const DWORD*)((u8*)obj + 0xC));
            if (i >= 0) { UtFpDrop(i); k.utFpCoalesced++; }
        }
        k.utFpWhy[why]++;
        g_utFpNext(world, edx, obj, enable, force, arg);
        return;
    }
    k.utFpCalls++;
    UtFpFlush(false);
    const DWORD idLo = *(const DWORD*)((u8*)obj + 8), idHi = *(const DWORD*)((u8*)obj + 0xC), en = enable & 0xFF;
    const DWORD now = GetTickCount();
    int kind = 1;
    float pos[3];
    bool rider = false;
    const int c = UnObjClass(obj, &kind, pos, &rider);
    if (!rider && kind == 0) rider = UtHeldObj(obj);
    why = c < 0 ? UTF_NOCLASS : c != 1 ? UTF_VISIBLE : kind != 0 ? UTF_KIND : rider ? UTF_RIDER : -1;
    if (why < 0) {
        const int v = UtViewerWhy(pos);
        why = v == UTW_VIEWER ? UTF_VIEWER : v == UTW_NOVIEW ? UTF_NOVIEW : -1;
    }
    if (why < 0 && DiscKeyMapHas((u8*)world, idLo, idHi)) why = UTF_PARKED;  // its branch stores the enable byte (design U)
    if (why < 0 && !en) why = UTF_CHANGE;  // disables always run (destroy does not remove routing footprints)
    const int i = UtFpFind(idLo, idHi);
    if (why < 0 && i >= 0) {
        UtFpPend& p = g_utFpPend[i];
        if (p.en == en && p.arg == arg && p.obj == obj) {  // the same update again: fold into the pending one
            if (g_utWatch.flipped && g_utWatch.armed && UtWatched(obj)) g_utWatch.fpCalled = true, UtWatchSkip(c == 0);
            p.force |= force & 0xFF;
            k.utFpSkipped++;
            k.utFpCoalesced++;
            UtDgSkip(UT_DG_FOOT, obj);
            return;
        }
        why = UTF_CHANGE;
    }
    if (why < 0) {
        const DWORD* t = UtFpLastSlot(idLo, idHi, false);
        if (!t || !*t || now - *t >= g_utFpMs) why = UTF_DUE;
        else if (g_utFpN == UT_FP_MAX) why = UTF_FULL;
        else {
            UtFpPend& p = g_utFpPend[g_utFpN++];
            p.idLo = idLo, p.idHi = idHi, p.due = *t + g_utFpMs, p.en = en, p.arg = arg, p.force = force & 0xFF;
            p.obj = obj, p.world = world;
            Max(&k.utFpPendMax, g_utFpN);
            k.utFpSkipped++;
            UtDgSkip(UT_DG_FOOT, obj);
            if (g_utWatch.flipped && g_utWatch.armed && UtWatched(obj)) g_utWatch.fpCalled = true, UtWatchSkip(c == 0);
            return;
        }
    }
    if (why == UTF_VISIBLE && i >= 0) UtSnapNote(2, now - (g_utFpPend[i].due - g_utFpMs));
    if (i >= 0) { UtFpDrop(i); k.utFpCoalesced++; }  // superseded by this call
    if (g_utWatch.flipped && g_utWatch.armed && UtWatched(obj)) g_utWatch.fpCalled = g_utWatch.fpRan = true;
    k.utFpWhy[why]++;
    g_utFpNext(world, edx, obj, enable, force, arg);
    if (DWORD* t = UtFpLastSlot(idLo, idHi, true)) *t = now ? now : 1;
}

// [UnseenThrottle] Enabled=1 (needs the probe's agent call site for the class and the agent): Every, NearM, PortalM,
// NeedViewers; List=1 phase 1 (utMode, --ab unseenthrottle); phase 2: Avoid=1 (utAvMode, --ab unseenavoid, AvoidNearM),
// Footprint=1 (utFpMode, --ab unseenfootprint, FpMs), Rate=1 (utRateMode, --ab unseenrate, RateNearM). Each mode starts
// at 1 when its part is installed. Footprint goes in before the probe's footprint split (ApplyUnseenProbe), so the probe
// sits below it and counts the bodies that run.
static void ApplyUnseenThrottle(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const int every = GetPrivateProfileIntW(L"UnseenThrottle", L"Every", 4, ini);
    k.utEvery = every < 2 ? 2 : every > 16 ? 16 : every;
    g_utNearM = (float)GetPrivateProfileIntW(L"UnseenThrottle", L"NearM", 15, ini);
    g_utPortalM = (float)GetPrivateProfileIntW(L"UnseenThrottle", L"PortalM", 3, ini);
    g_utNeedViewers = GetPrivateProfileIntW(L"UnseenThrottle", L"NeedViewers", 1, ini) != 0;
    if (GetPrivateProfileIntW(L"UnseenThrottle", L"List", 1, ini)) {
        const CallSite site = {UT_LIST_CALL, UT_LIST, (void*)UtListThunk};
        if (PatchCalls("unseen throttle obstacle list", &site, 1)) {
            k.utReady |= 1;
            k.utMode = 1;
            g_utOn = true;
            Log("OK   unseen throttle: obstacle list every %ld ticks for unseen walking Sims (near goal %.0f m, portal %.0f m, viewers %s%s)",
                k.utEvery, g_utNearM, g_utPortalM, g_utNeedViewers ? "required" : "optional",
                g_getOptOrig ? "" : "; no viewer channel without [RouteAsync]");
        }
    }
    if (GetPrivateProfileIntW(L"UnseenThrottle", L"Avoid", 0, ini)) {
        g_utAvNearM = (float)GetPrivateProfileIntW(L"UnseenThrottle", L"AvoidNearM", 3, ini);
        DWORD* slot = (DWORD*)(uintptr_t)UT_AV_SLOT;
        const DWORD hook = (DWORD)(uintptr_t)UtAvHook;
        if (!Readable(slot, 4) || *slot != UT_AV)
            Log("SKIP unseen throttle avoidance: vtable slot 0x%08X holds 0x%08X, not ObstacleAvoidance 0x%08X", UT_AV_SLOT,
                Readable(slot, 4) ? *slot : 0, UT_AV);
        else if (Write(slot, &hook, 4)) {
            k.ut2Ready |= 1, k.utAvMode = 1, g_utOn = true;
            Log("OK   unseen throttle avoidance: behaviour slot 0x%08X every %ld ticks for unseen walking Sims (goal %.0f m)",
                UT_AV_SLOT, k.utEvery, g_utAvNearM);
        }
    }
    if (GetPrivateProfileIntW(L"UnseenThrottle", L"Footprint", 0, ini)) {
        const int ms = GetPrivateProfileIntW(L"UnseenThrottle", L"FpMs", 500, ini);
        g_utFpMs = ms < 50 ? 50 : ms > 2000 ? 2000 : (DWORD)ms;
        if (k.usfReady && g_usfOrig) {  // design U owns 0x833560: sit below its stub (its drains reach us under +0x918: pass)
            g_utFpNext = g_usfOrig;
            g_usfOrig = UtFpHook;
            k.ut2Ready |= 2 | 4;
        } else if ((g_utFpNext = (UnUsfFn)InstallDetour("UpdateSimFootprint (unseen throttle)", UN_USF, kUnUsfPrologue,
                                                        sizeof(kUnUsfPrologue), (void*)UtFpHook, &g_utFpNext)) != NULL) {
            k.ut2Ready |= 2;
        }
        if (g_utFpNext) {
            k.utFpMode = 1, g_utFpOn = g_utOn = true;
            Log("OK   unseen throttle footprint: unseen Sims' footprint updates at most every %lu ms%s", g_utFpMs,
                k.usfReady ? ", behind design U" : "");
        }
    }
    if (GetPrivateProfileIntW(L"UnseenThrottle", L"Rate", 0, ini)) {
        g_utRateNearM = (float)GetPrivateProfileIntW(L"UnseenThrottle", L"RateNearM", 3, ini);
        // behaviour B's dt clamp, which the summed step relies on
        const float* lo = (const float*)(uintptr_t)0x010434C4;
        const float* hi = (const float*)(uintptr_t)0x00F9A5AC;
        if (!Readable(lo, 4) || !Readable(hi, 4) || *lo != UT_MIN_STEP || *hi != UT_MAX_STEP) {
            Log("SKIP unseen throttle rate: behaviour B's step clamp is not [1/30, 0.5] at 0x010434C4 / 0x00F9A5AC");
        } else {
            k.ut2Ready |= 8, k.utRateMode = 1, g_utRateOn = g_utOn = true;
            Log("OK   unseen throttle rate: unseen walking Sims' agent update every %ld ticks, summed step <= %.1f s (goal %.0f m, portal %.0f m)",
                k.utEvery, UT_MAX_STEP, g_utRateNearM, g_utPortalM);
        }
    }
    // v83 diagnostics (§8.14): Replan / ReplanFromPosition IC entries; an IC whose prologue another section already owns
    // ([RouteAsync] Kinds, [ProbeP10], both installed earlier) is left to it and its split stays 0
    for (int i = 0; i < 2; i++) {
        const int ic = i ? 3 : 1;
        void* const hook = i ? (void*)UtRpPosHook : (void*)UtRpHook;
        void* t = i ? InstallDetour(kP10Name[ic], kP10Ic[ic], kP10Pro[ic], sizeof(kP10Pro[ic]), hook, &g_utRpPosOrig)
                    : InstallDetour(kP10Name[ic], kP10Ic[ic], kP10Pro[ic], sizeof(kP10Pro[ic]), hook, &g_utRpOrig);
        // [RouteAsync] ReplanDedupe's own detour (A10 off) owns the prologue: chain behind it, so the split counts the calls
        // that planned (served repeats never reach it); the trampoline is published before the swap
        const bool chained = !t && g_rdOrig[ic];
        if (chained) t = g_rdOrig[ic];
        if (!t) continue;
        if (i) g_utRpPosOrig = (UtRpPosFn)t;
        else g_utRpOrig = (UtRpFn)t;
        if (chained) InterlockedExchangePointer(&g_rdOrig[ic], hook), Log("OK   unseen throttle diagnostics: IC %d chained behind replan dedupe", ic);
        k.utDgReady |= 1 << i;
    }
    Log("unseen throttle diagnostics: replan split ready 0x%lX (bit 0 Replan, 1 ReplanFromPosition), deferred notices, skip ages",
        k.utDgReady);
}

// [UnseenProbe] Enabled=1 or [UnseenThrottle] Enabled=1: the agent call site is checked first (refused and logged when it
// differs); the probe's footprint / plan / lock splits only with [UnseenProbe]
static bool ApplyUnseenProbe(const wchar_t* ini) {
    const bool probe = GetPrivateProfileIntW(L"UnseenProbe", L"Enabled", 0, ini) != 0;
    const bool thr = GetPrivateProfileIntW(L"UnseenThrottle", L"Enabled", 0, ini) != 0;
    if (!probe && !thr) return false;
    Ts3nCounters& k = ts3native_counters;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const CallSite site = {UN_AGENT_CALL, UN_AGENT, (void*)UnAgentThunk};
    if (!PatchCalls("unseen probe agent update", &site, 1)) return true;  // logged; the throttle needs this site
    LONG ready = 1;
    if (thr) ApplyUnseenThrottle(ini);  // first: its footprint layer goes above the probe's
    if (probe) {
        if (GetPrivateProfileIntW(L"UnseenProbe", L"Footprint", 1, ini)) {
            // one stub per address: below the throttle's footprint layer, else below design U's stub, else our own detour
            UnUsfFn* bottom = g_utFpOn ? &g_utFpNext : k.usfReady && g_usfOrig ? &g_usfOrig : NULL;
            if (bottom) {
                g_unUsfOrig = *bottom;
                *bottom = UnUsfHook;
                ready |= 2;
            } else if ((g_unUsfOrig = (UnUsfFn)InstallDetour("UpdateSimFootprint (unseen probe)", UN_USF, kUnUsfPrologue,
                                                             sizeof(kUnUsfPrologue), (void*)UnUsfHook, &g_unUsfOrig)) != NULL)
                ready |= 2;
        }
        if (g_icPlanOrig) ready |= 4;  // plans are split only through the async plan detour
        if (k.rgEnforce) ready |= 8;   // +0x918 acquires and waits keyed through [RouteGate] Sites / Enforce
        g_unOn = true;
    }
    k.unReady = ready;
    if (probe)
        Log("OK   unseen probe: agent updates split visible / unseen, ready 0x%lX (footprint %s, plans %s, keyed lock %s)", ready,
            ready & 2 ? (k.usfReady ? "on, behind design U" : "on") : "off", ready & 4 ? "on" : "off ([RouteAsync] not installed)",
            ready & 8 ? "on" : "off");
    return true;
}
