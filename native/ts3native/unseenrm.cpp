// [UnseenRootMotion] (docs/228, counters v151 rm*, Rodney 2026-11-02): EA's root-motion sub-steps in Animation_UpdateActors
// 0x571B90. Included by ts3native.cpp after unseenanim.cpp (needs its pass hook at 0xD81C73, classifier use, visible list,
// agent grid). Off by default; [UnseenAnim] Enabled=1 is required (its pass thunk calls RmPassBegin / RmPassClock / RmPassEnd).
//
// Loop [verified disassembly 0x571B90..0x571E24, Steam 1.67, docs/228 §1.1]: walk 1 visits every actor (entry; next link at
// +0x18, entry = link - 0x18; cursor at list +0x10; sentinel = list - 0x18): owner [entry+4] refcount +1, `call 0x942C00`
// (time step, thiscall(entry; float dt), ret 4) at 0x571BF3, root-motion test 0x93E190 (fastcall(entry) -> al), then either
// the non-root-motion pose at 0x571C3C ([UnseenAnim]'s) or, for a root-motion actor with a rig, `call 0xC0FD60` (a bare `ret`,
// ecx = entry) at 0x571C4C and the flag byte [esp+0x13] = 1. With the flag set: while dt >= 1/30 ([0x10434C4]): dt -= 1/30,
// then a full walk (refcount pair, 0x93E190, root-motion + rig: `call 0x940430(1/30)` at 0x571D20); then, if dt >= 1/60
// ([0xFC25C4]), one more full walk with `call 0x940430(dt)` at 0x571DDC. 0x571E0A pops esi/ebx and runs the drains.
//
// Mode 0 (probe, behaviour identical): rel32 rewrites at 0x571BF3, 0x571C4C, 0x571D20, 0x571DDC (PatchCalls: each must be an
// E8 to its expected target, else nothing is written). Counts every pass: the k histogram (EA's own float loop on dt), walk-1
// actors, root-motion actors (the walk-1 shortlist), sub-step walks, pose calls and calls of actors not flagged in walk 1
// (rmLateRm: what Mode 1 would not step). 1 pass in 16 is timed: walk 1 (pass start to the last time-step return), each time
// step, each root-motion pose call by class (visible Sim, unseen Sim, non-Sim) and, for unseen Sims, by first exact zone, the
// transform move across the call (rmDeltaCm), and the tail (last time step to pass end) minus the timed pose calls and the
// probe's own work = the sub-step walks + drains (passes without root motion give the drains alone). QPC floor per sample.
// Mode 1 (shortlist, rmMode 1, ini Shortlist=1 installs the jump): `jmp RmSubStub` at 0x571C83 (11 bytes byte-checked). The
// sub-steps and remainder run here: the same float loop, each walk follows EA's live cursor protocol over the real list and
// runs EA's per-actor body (refcount pair, 0x93E190, the pose) for an actor that is flagged in walk 1, a candidate, or has
// bit 15 of [entry+0x2C] (the test's forced flag, read directly); others are skipped (EA's test is false for them).
// v153 (docs/228 §10) closes the late starts the v151 smoke found (rmLateRm 1729): candidates = entries linked into the list
// during the pass (`call 0x5718C0` at 0x9402E3 hooked: activation links at the head, before the cursor, so walk 1 never saw
// them; this was the v151 count/XOR fallback "reason 2", now gone) and late root-motion finds; a root-motion clip assignment
// (the only clip write of the ClipController flag, `mov [esi+0x5FB],al` at 0x569CC9, replaced by `call RmClipStub; nop`)
// marks the pass dirty, and the next walk (and the rest of the current one) runs EA's body for every entry, adding new finds
// to the candidates. 1 Mode 1 pass in 64 is an audit: every walk runs EA's body for every entry and counts a find the rules
// above would have skipped (rmSlMiss, must stay 0; e.g. a controller with a root-motion clip linked into a tree). Only
// fallback left: shortlist full (> 512) before the first step = the whole pass to EA's code at 0x571C8E.
// Mode 2 (coarse unseen Sims, docs/228 §11, counters v154 rm2*, ini Mode=2; Rodney 2026-11-02): on EA's own loop (no shortlist),
// RmStepThunk / RmRemThunk run one pose call per MergeSteps sub-steps with the summed dt for an unseen Sim with root motion
// that no exact zone holds (Rm2WhyGame: camera jump hold, visible, almost visible = view widened by MarginPct + camera speed x
// LeadMs, parented / horse / group link, SACS actor, root motion started this or last pass, NearVisM of a visible Sim, active
// household or no table, no locomotion agent, no route or not walking, GoalM of the goal, PortalM of a portal, then
// HystPasses passes in a row before coarse). Owed steps never leave the pass: the actor's last call of the pass (its k-th
// sub-step, or the remainder walk) runs whatever it owes, so the pose published after the pass is time-exact; an actor that
// turns exact mid-pass (switch off) first runs its owed steps one 1/30 s step each (snap). Decision once per actor per pass,
// in both arms (the off arm counts would-coarse actors and their drift: symmetric overhead). Drift = transform after the last
// call vs the locomotion agent's own transform write this step (steering slot 0x10445C0 -> 0x954580 recorded per owner);
// Place=1 replays that write after each coarse call. rmMode nonzero = the ini Mode (--ab unseenrm writes 1).
// v155 (docs/228 §11.6): the camera jump hold also starts at the script camera ICs (map view, lerp/focus to a Sim or lot,
// position set, follow, tether, lock; detours at their entries, CamIc=1), before the camera moves; rmReady bit 4 = all 11.
typedef void(__fastcall* RmStepFn)(void* actor, void* edx, float dt);  // 0x942C00 / 0x940430, thiscall(entry; float), ret 4
typedef bool(__fastcall* RmTestFn)(void* actor);                       // 0x93E190
typedef void(__fastcall* RmInsFn)(void* list, void* edx, void* entry);  // 0x5718C0, thiscall(list; entry), ret 4: link at the head
typedef void(__fastcall* RmRelFn)(void* owner, void* edx, int del);   // owner vtable +0, thiscall(owner; 1)
typedef void(__fastcall* Rm2SteerFn)(void* self, void* edx, const float* q16, const float* p16);  // 0x954580, ret 8, movaps reads
static const DWORD RM_TS_CALL = 0x00571BF3, RM_TS = 0x00942C00, RM_FLAG_CALL = 0x00571C4C, RM_FLAG = 0x00C0FD60,
                   RM_STEP_CALL = 0x00571D20, RM_REM_CALL = 0x00571DDC, RM_POSE = 0x00940430, RM_TEST = 0x0093E190,
                   RM_SUB_AT = 0x00571C83, RM_SUB_EA = 0x00571C8E, RM_SUB_DONE = 0x00571E0A, RM_INS_CALL = 0x009402E3,
                   RM_INS = 0x005718C0, RM_CLIP_AT = 0x00569CC9, RM2_STEER_SLOT = 0x010445C0, RM2_STEER = 0x00954580;
// 0x571C83: cmp byte ptr [esp+13h],0 / je 0x571E0A
static const u8 kRmSubPro[11] = {0x80, 0x7C, 0x24, 0x13, 0x00, 0x0F, 0x84, 0x7C, 0x01, 0x00, 0x00};
static RmStepFn g_rmTsOrig = (RmStepFn)(uintptr_t)RM_TS, g_rmPoseOrig = (RmStepFn)(uintptr_t)RM_POSE;
static RmTestFn g_rmTest = (RmTestFn)(uintptr_t)RM_TEST;
static RmInsFn g_rmInsOrig = (RmInsFn)(uintptr_t)RM_INS;
// 0x569CC9: mov byte ptr [esi+5FBh],al (ClipController root-motion flag from the clip, in the clip assign 0x569C50)
static const u8 kRmClipPro[6] = {0x88, 0x86, 0xFB, 0x05, 0x00, 0x00};
static const float* g_rmStepC = (const float*)(uintptr_t)0x010434C4;  // 1/30, EA's sub-step
static const float* g_rmRemC = (const float*)(uintptr_t)0x00FC25C4;   // 1/60, remainder threshold
static DWORD g_rmJmpEa = RM_SUB_EA, g_rmJmpDone = RM_SUB_DONE;
enum { RM_SET = 1024, RM_SLMAX = 512 };
enum { RMZ_PARENT, RMZ_SACS, RMZ_STARTED, RMZ_NEARVIS, RMZ_VIEWER, RMZ_NOAGENT, RMZ_NONE, RMZ_NOGRID };
static void* g_rmSet[2][RM_SET];  // walk-1 root-motion actors: [g_rmCur] this pass, other = last pass (compared, never read)
static int g_rmCur, g_rmSlN, g_rmPrevN;
static bool g_rmSlFull, g_rmIn, g_rmSample;
static void* g_rmCand[RM_SET];  // this pass: entries linked during it and late root-motion finds (EA's body in every walk)
static int g_rmCandN;
static bool g_rmAllFull, g_rmAudit;  // candidates full: every later walk runs EA's body; audit pass (Mode 1, 1 in 64)
// audit miss (rmSlMiss, 2026-11-02 smoke: 1 in 62 audits): the rest of that pass and the 63 passes up to the next audit run EA's
// full walks (rmSlMissHold counts them), so a late-start path no hook covers is posed as EA does until an audit comes back clean
static DWORD g_rmMissHold;
static int g_rmWalkN;  // walks this pass (1 = first sub-step), for the miss log
static volatile LONG g_rmMissLogs;
static volatile LONG g_rmDirty, g_rmClipN;  // RmClipStub: a root-motion clip was assigned (since pass start)
static LONG g_rmNodes;
static float g_rmDt;
static DWORD g_rmPassN;
static LARGE_INTEGER g_rmT0, g_rmTw1;
static LONGLONG g_rmPoseTk, g_rmOwnTk;  // sampled pass: ticks in root-motion pose calls, in the probe's own reads
static u64 g_rmCarry[72];               // per counter: sub-us remainder (ticks x 1e6 mod qpf), so short calls add up
static struct { LONG mergeSteps, goalM, portalM, nearVisM, marginPct, leadMs, hystPasses, camIcPasses, place; bool on; } g_rm2;  // Mode 2 (ini Mode=2)
// effective mode: rmMode 0 = probe; nonzero = the ini mode (Mode 2 configured: 2, else 1 only as before; a 2 without Mode 2 = 0)
static int RmEff() {
    const LONG m = ts3native_counters.rmMode;
    return !m ? 0 : g_rm2.on ? 2 : m == 1 ? 1 : 0;
}

static unsigned RmHash(const void* p) { return ((unsigned)((uintptr_t)p >> 4) * 2654435761u) >> 22; }  // 10 bits = RM_SET
static bool RmHas(void* const* t, const void* p) {
    for (unsigned h = RmHash(p), n = 0; n < RM_SET; h = (h + 1) & (RM_SET - 1), n++) {
        if (t[h] == p) return true;
        if (!t[h]) return false;
    }
    return false;
}
static bool RmAdd(void** t, void* p) {  // true = new; caller keeps the count <= RM_SLMAX, so a free slot exists
    for (unsigned h = RmHash(p);; h = (h + 1) & (RM_SET - 1)) {
        if (t[h] == p) return false;
        if (!t[h]) { t[h] = p; return true; }
    }
}
// counter += ticks in us (scale 1e6) or ns (1e9), remainder kept per counter
static void RmTime(LONG* ctr, LONGLONG ticks, u64 scale = 1000000) {
    if (ticks <= 0) return;
    u64& c = g_rmCarry[ctr - &ts3native_counters.rmReady];
    const u64 v = c + (u64)ticks * scale;
    *ctr += (LONG)(v / (u64)g_qpf.QuadPart), c = v % (u64)g_qpf.QuadPart;
}
static LONGLONG RmNow() { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart; }
static bool RmOnThread() { return g_rmIn && GetCurrentThreadId() == g_aaTid; }
static void Rm2PassBegin(float dt);  // Mode 2 (below)
static void Rm2PassCam(const LARGE_INTEGER& t);
static void Rm2PassEnd();

// EA's sub-step count for this dt (same float ops as 0x571C8E..0x571D5F) and whether the remainder walk runs
static int RmStepsFor(float d, bool* rem) {
    const float c = *g_rmStepC;
    int n = 0;
    while (d >= c && n < 64) d -= c, n++;
    if (rem) *rem = d >= *g_rmRemC;
    return n;
}
static int RmKBucket(int k) { return k <= 3 ? k : k <= 5 ? 4 : k <= 7 ? 5 : k <= 9 ? 6 : 7; }

// ---- pass hook (from AaPassThunk) ----
static bool RmPassBegin(float dt) {
    g_rmPassN++;
    g_rmSample = (g_rmPassN & 15) == 0;
    g_rmPrevN = g_rmSlFull ? -1 : g_rmSlN;  // -1: last pass's set is incomplete, no flips counted
    g_rmCur ^= 1;
    memset(g_rmSet[g_rmCur], 0, sizeof g_rmSet[0]);
    g_rmSlN = 0, g_rmSlFull = false, g_rmNodes = 0, g_rmDt = dt, g_rmPoseTk = g_rmOwnTk = 0;
    if (g_rmCandN) memset(g_rmCand, 0, sizeof g_rmCand), g_rmCandN = 0;
    g_rmAllFull = false, g_rmAudit = (g_rmPassN & 63) == 8, g_rmDirty = g_rmClipN = 0;  // 8: never a sampled (timed) pass
    g_rmWalkN = 0;
    if (g_rmMissHold) g_rmMissHold--, g_rmAllFull = true, ts3native_counters.rmSlMissHold++;
    if (g_rm2.on) Rm2PassBegin(dt);
    if (g_rmSample) {  // the probe's floor: 4 bare QPC pairs
        Ts3nCounters& k = ts3native_counters;
        for (int i = 0; i < 4; i++) {
            const LONGLONG a = RmNow();
            RmTime(&k.rmFloorNs, RmNow() - a, 1000000000);
        }
        k.rmFloorN += 4;
    }
    return g_rmSample || g_rm2.on;  // true = AaPassThunk builds the agent grid (zones; Mode 2 needs it every pass)
}
static void RmPassClock(const LARGE_INTEGER& t0) {
    g_rmT0 = g_rmTw1 = t0, g_rmIn = true;
    if (g_rm2.on) Rm2PassCam(t0);  // after AaPassThunk's UnRefreshCamera
}
static void RmPassEnd() {
    Ts3nCounters& k = ts3native_counters;
    const LONGLONG end = RmNow();
    g_rmIn = false;
    if (g_rm2.on) Rm2PassEnd();
    k.rmPasses++;
    k.rmSlClip += g_rmClipN;
    bool rem;
    const int steps = RmStepsFor(g_rmDt, &rem);
    k.rmK[RmKBucket(steps)]++;
    if (rem) k.rmRem++;
    k.rmActSum += g_rmNodes, k.rmRmSum += g_rmSlN;
    Max(&k.rmActMax, g_rmNodes), Max(&k.rmRmMax, g_rmSlN), Max(&k.rmSlMax, g_rmSlN);
    const int walks = g_rmSlN ? steps + (rem ? 1 : 0) : 0;
    if (g_rmSlN) k.rmPassesRm++, k.rmWalks += walks;
    if (g_rmPrevN >= 0 && !g_rmSlFull) {  // started = in this set, not the last; ended = last's size minus the kept ones
        int started = 0;
        for (int i = 0; i < RM_SET; i++)
            if (g_rmSet[g_rmCur][i] && !RmHas(g_rmSet[g_rmCur ^ 1], g_rmSet[g_rmCur][i])) started++;
        k.rmFlips += started + (g_rmPrevN - (g_rmSlN - started));
    }
    if (!g_rmSample) return;
    k.rmSampled++, k.rmSWalks += walks;
    RmTime(&k.rmW1Us, g_rmTw1.QuadPart - g_rmT0.QuadPart);
    const int had = g_rmSlN ? 1 : 0;
    k.rmTailN[had]++;
    RmTime(&k.rmTailUs[had], end - g_rmTw1.QuadPart - g_rmOwnTk);
    if (had) RmTime(&k.rmWalkUs, end - g_rmTw1.QuadPart - g_rmPoseTk - g_rmOwnTk);
}

// ---- walk 1 ----
static void __fastcall RmTsThunk(void* actor, void* edx, float dt) {
    if (!RmOnThread()) { g_rmTsOrig(actor, edx, dt); return; }
    g_rmNodes++;
    if (!g_rmSample) { g_rmTsOrig(actor, edx, dt); return; }
    Ts3nCounters& k = ts3native_counters;
    const LONGLONG a = RmNow();
    g_rmTsOrig(actor, edx, dt);
    g_rmTw1.QuadPart = RmNow();
    k.rmTsN++;
    RmTime(&k.rmTsUs, g_rmTw1.QuadPart - a);
}
// replaces `call 0xC0FD60` (a bare ret, ecx = the root-motion actor): nothing to call
static void __fastcall RmFlagThunk(void* actor) {
    if (!RmOnThread() || g_rmSlFull) return;
    if (g_rmSlN >= RM_SLMAX) { g_rmSlFull = true; return; }
    if (RmAdd(g_rmSet[g_rmCur], actor)) g_rmSlN++;
}

// ---- root-motion pose calls ----
// class 0 visible Sim (or unclassified), 1 unseen Sim, 2 non-Sim; zone for class 1; pos = the Sim's position (posOk)
static int RmClassOf(void* actor, int* zone, float* pos, bool* posOk) {
    Ts3nCounters& k = ts3native_counters;
    *zone = -1, *posOk = false;
    void* owner = NULL;
    __try {
        owner = *(void* const*)((const u8*)actor + 4);
        if (!owner || !g_unComp(owner, NULL, *g_unBitSim)) return 2;
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.rmFaults++; return 2; }
    int kind = 0;
    bool par = false;
    const int c = UnObjClass(owner, &kind, pos, &par);
    if (c < 0) return 0;
    *posOk = true;
    if (c != 1) return 0;
    bool sacs = true;
    __try {
        const u8* sc = (const u8*)g_unComp(owner, NULL, *g_aaBitSacs);
        sacs = sc && *(void* const*)(sc + 8) != *(void* const*)(sc + 0xC);
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.rmFaults++; }
    *zone = par ? RMZ_PARENT : sacs ? RMZ_SACS : !RmHas(g_rmSet[g_rmCur ^ 1], actor) ? RMZ_STARTED
            : AaNearVisible(pos) ? RMZ_NEARVIS : UtViewerWhy(pos) >= 0 ? RMZ_VIEWER : !g_aoAg.ok ? RMZ_NOGRID
            : AoNear(owner, pos) != 3 ? RMZ_NOAGENT : RMZ_NONE;
    return 1;
}
static bool RmPosOf(void* actor, float* out) {
    __try {
        void* owner = *(void* const*)((const u8*)actor + 4);
        void* tc = owner ? g_unComp(owner, NULL, *g_unBitTransform) : NULL;
        if (!tc) return false;
        __declspec(align(16)) float p[4];
        memcpy(out, g_unPos(tc, NULL, p), 12);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.rmFaults++; return false; }
}
static void RmPose(void* actor, void* edx, float dt) {
    Ts3nCounters& k = ts3native_counters;
    if (!RmOnThread()) { g_rmPoseOrig(actor, edx, dt); return; }
    k.rmCallsAll++;
    if (!g_rmSlFull && !RmHas(g_rmSet[g_rmCur], actor)) k.rmLateRm++;
    if (!g_rmSample) { g_rmPoseOrig(actor, edx, dt); return; }
    const LONGLONG a = RmNow();
    int zone;
    float p0[3], p1[3];
    bool posOk;
    const int c = RmClassOf(actor, &zone, p0, &posOk);
    const LONGLONG t0 = RmNow();
    g_rmPoseOrig(actor, edx, dt);
    const LONGLONG t1 = RmNow(), us = t1 - t0;
    g_rmPoseTk += us;
    k.rmN[c]++;
    RmTime(&k.rmUs[c], us);
    if (zone >= 0) k.rmZoneN[zone]++, RmTime(&k.rmZoneUs[zone], us);
    if (c != 2 && posOk && RmPosOf(actor, p1)) {
        const float dx = p1[0] - p0[0], dy = p1[1] - p0[1], dz = p1[2] - p0[2];
        const LONG cm = (LONG)(sqrtf(dx * dx + dy * dy + dz * dz) * 100.0f + 0.5f);
        k.rmDeltaN[c]++, k.rmDeltaCm[c] += cm;
        Max(&k.rmDeltaMaxCm, cm);
    }
    g_rmOwnTk += (t0 - a) + (RmNow() - t1);
}
// ---- Mode 2: coarse root-motion steps for unseen Sims (docs/228 §11, counters v154 rm2*) ----
// Exact reasons, first that holds (rm2Why_*): not a Sim, camera jump hold, unclassified, visible, almost visible (margin),
// parented / horse / group link, SACS actor, root motion started this or last pass, near a visible Sim, household / no
// table, no agent, no route / not walking, goal band, portal band, slot taken (another actor owes in this hash slot),
// hysteresis (fewer than HystPasses eligible passes in a row).
enum { RM2_NOTSIM, RM2_CAM, RM2_NOCLASS, RM2_VISIBLE, RM2_MARGIN, RM2_PARENT, RM2_SACS, RM2_STARTED, RM2_NEARVIS, RM2_VIEWER,
       RM2_NOAGENT, RM2_NOROUTE, RM2_GOAL, RM2_PORTAL, RM2_SLOT, RM2_HYST, RM2_N };
enum { RM2_VMAX = 64, RM2_ROUTES = 256 };
static const float RM2_JUMP_M = 20.0f;   // shortcut: camera move per pass that counts as a jump (map view, portrait snap); tune from rm2CamSpeedMax
static const float RM2_HALF_FOV = 0.6f;  // shortcut: tan(half view angle) [inferred], turns MarginPct into a sphere radius per metre
struct Rm2Slot { void* actor; DWORD pass; LONG streak; int why, n, owedN; float owed; bool arm; };  // arm: decided in the coarse arm
static Rm2Slot g_rm2Slot[RM_SET];  // keyed by actor address (RmHash); the actor is never dereferenced through it
static int g_rm2Open;              // slots holding owed steps this pass
static int g_rm2Steps;             // EA's sub-step count this pass
static bool g_rm2Rem;              // and whether the remainder walk runs
static float g_rm2DtCarry, g_rm2LostCarry;  // sub-ms remainders of rm2DtMs / rm2LostMs
static struct { float pos[RM2_VMAX][3]; int n; bool full; } g_rm2Vis[2];  // visible Sims with root motion: [g_rm2VisCur] this pass
static int g_rm2VisCur;
static struct { float pos[3], tgt[3], reach, speed; bool have; int hold; LONGLONG at; } g_rm2Cam;
struct Rm2Route { __declspec(align(16)) float q[4]; __declspec(align(16)) float p[4]; void* owner; void* self; DWORD pass; };
static Rm2Route g_rm2Route[RM2_ROUTES];  // the locomotion agent's last transform write per owner (steering slot 0x10445C0)
static Rm2SteerFn g_rm2SteerOrig = (Rm2SteerFn)(uintptr_t)RM2_STEER;
static int Rm2WhyGame(void* actor);
static int (*g_rm2WhyFn)(void* actor) = Rm2WhyGame;  // selftest swaps in a fake

static float Rm2Dist(const float* a, const float* b) {
    const float x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
    return sqrtf(x * x + y * y + z * z);
}
static void Rm2AddMs(LONG* ctr, float* carry, float sec) {
    *carry += sec * 1000.0f;
    const LONG ms = (LONG)*carry;
    *ctr += ms, *carry -= (float)ms;
}

// steering slot 0x10445C0 (0x954580, thiscall(self; q*, p*), ret 8; owner = [self-4]): record, then EA's write
static void __fastcall Rm2SteerThunk(void* self, void* edx, const float* q, const float* p) {
    if (g_rm2.on && GetCurrentThreadId() == g_aaTid) {
        Ts3nCounters& k = ts3native_counters;
        __try {
            void* owner = *(void* const*)((const u8*)self - 4);
            Rm2Route& r = g_rm2Route[RmHash(owner) & (RM2_ROUTES - 1)];
            memcpy(r.q, q, 16), memcpy(r.p, p, 16), r.owner = owner, r.self = self, r.pass = g_rmPassN;
            k.rm2RouteRecs++;
            const float n2 = q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3];
            if (!(n2 > 0.81f && n2 < 1.21f)) k.rm2QuatBad++;  // arg order check: arg 1 must be a unit quaternion (I-420-17)
        } __except (EXCEPTION_EXECUTE_HANDLER) { k.rm2Faults++; }
    }
    g_rm2SteerOrig(self, edx, q, p);
}
// the write the agent made in the locomotion pass just before this anim pass (stamped with the previous pass number)
static Rm2Route* Rm2RouteOf(void* owner) {
    Rm2Route* r = &g_rm2Route[RmHash(owner) & (RM2_ROUTES - 1)];
    return owner && r->owner == owner && r->pass + 1 == g_rmPassN ? r : NULL;
}
static void* Rm2Owner(void* actor) {
    __try { return *(void* const*)((const u8*)actor + 4); } __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.rm2Faults++; return NULL; }
}

// Script camera ICs (docs/228 §11.6, counters v155 rm2CamIc*): ScriptCore.CameraController internal calls, cdecl, all
// arguments on the stack (Vector3 = 3 floats by value) [verified: script binding table names + Steam 1.67 disassembly of each
// body; arg index of the lerp time from its fld]. Hooked only with Mode 2 (ini CamIc=1, default). Each hit asks "all exact"
// for CamIcPasses + the call's lerp time x 30 passes (lerp time capped at 10 s), taken by Rm2CamStep at the next pass start,
// so the hold starts before the camera can move (the IC runs in the script phase; the camera moves on the main thread
// later). The target position is not needed: the hold is all-exact, a superset of "near the target". The 20 m/pass
// heuristic stays as the fallback (main-thread moves, ICs not hooked). Thread (I-420-6): the hold is an atomic max, so a
// caller off the sim thread is safe; rm2CamIcOff counts such calls.
struct Rm2CamIcSite { DWORD addr; u8 len; u8 pro[6]; signed char durArg; const char* name; };
enum { RM2_IC_N = 11 };
static const Rm2CamIcSite kRm2CamIc[RM2_IC_N] = {
    {0x0073D550, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, -1, "IC_Camera_SetPositionAndTarget"},           // (pos, target)
    {0x0073D840, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 3, "IC_Camera_RequestLerpToTarget"},             // (target, time, swing)
    {0x0073D950, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 6, "IC_Camera_RequestLerpToPositionAndTarget"},  // (pos, target, time, swing)
    {0x0073DB00, 5, {0xE8, 0x8B, 0xD8, 0x22, 0x00}, 4, "IC_Camera_FocusOnLot"},                            // (lot u64, zoom, pitch, time)
    {0x0073DB70, 5, {0xE8, 0x1B, 0xD8, 0x22, 0x00}, 4, "IC_Camera_FocusOnLotForBuildBuy"},                 // (lot u64, zoom, pitch, time, tol)
    {0x0073DC20, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, -1, "IC_Camera_EnableObjectFollow"},             // (obj u64, offset)
    {0x0073DDC0, 5, {0xE8, 0xCB, 0xD5, 0x22, 0x00}, -1, "IC_Camera_TetherToLot"},                          // (lot u64, immediate)
    {0x0073DE60, 5, {0xE8, 0x2B, 0xD5, 0x22, 0x00}, -1, "IC_Camera_TetherToObject"},                       // (obj u64)
    {0x0073DEF0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 8, "IC_Camera_Lock"},                            // (obj u64, offset, zoom, pitch, yaw, time)
    {0x0073DFB0, 5, {0xE8, 0xDB, 0xD3, 0x22, 0x00}, 0, "IC_Camera_EnableMapViewMode"},                     // (time)
    {0x0073E000, 5, {0xE8, 0x8B, 0xD3, 0x22, 0x00}, 2, "IC_Camera_DisableMapViewMode"},                    // (zoom, pitch, time)
};
static void* g_rm2IcTramp[RM2_IC_N];
static volatile LONG g_rm2IcHold;  // passes asked by the camera ICs since the last pass start (atomic max; any thread)
// from the IC stubs: i = site, args = the IC's stack arguments
static void __cdecl Rm2CamIcHit(int i, const DWORD* args) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.rm2CamIc[i]);
    if (g_aaTid && GetCurrentThreadId() != g_aaTid) InterlockedIncrement(&k.rm2CamIcOff);
    LONG n = g_rm2.camIcPasses > 0 ? g_rm2.camIcPasses : 1;
    if (kRm2CamIc[i].durArg >= 0) {
        const float s = *(const float*)(args + kRm2CamIc[i].durArg);
        if (s > 0 && s <= 10.0f) n += (LONG)(s * 30.0f + 0.5f);  // NaN fails both tests
    }
    for (LONG cur = g_rm2IcHold; cur < n; cur = g_rm2IcHold)
        if (InterlockedCompareExchange(&g_rm2IcHold, n, cur) == cur) break;
}
// eax/ecx/edx are free at these cdecl entries (each body writes them before any read [verified disassembly])
#define RM2_IC_STUB(i) static __declspec(naked) void Rm2IcStub##i() { __asm lea eax, [esp + 4] __asm push eax __asm push i \
    __asm call Rm2CamIcHit __asm add esp, 8 __asm jmp dword ptr [g_rm2IcTramp + i * 4] }
RM2_IC_STUB(0) RM2_IC_STUB(1) RM2_IC_STUB(2) RM2_IC_STUB(3) RM2_IC_STUB(4) RM2_IC_STUB(5) RM2_IC_STUB(6) RM2_IC_STUB(7)
RM2_IC_STUB(8) RM2_IC_STUB(9) RM2_IC_STUB(10)
static void (*const kRm2IcStubs[RM2_IC_N])() = {Rm2IcStub0, Rm2IcStub1, Rm2IcStub2, Rm2IcStub3, Rm2IcStub4, Rm2IcStub5,
                                                Rm2IcStub6, Rm2IcStub7, Rm2IcStub8, Rm2IcStub9, Rm2IcStub10};
// detours at the eleven IC entries; a leading `call rel32` is re-targeted in the trampoline (as rtsites.cpp); returns the count
static int Rm2InstallCamIc() {
    int n = 0;
    for (int i = 0; i < RM2_IC_N; i++) {
        char name[80];
        sprintf_s(name, "%s (unseen root motion camera IC)", kRm2CamIc[i].name);
        u8* tr = (u8*)InstallDetour(name, kRm2CamIc[i].addr, kRm2CamIc[i].pro, kRm2CamIc[i].len, (void*)kRm2IcStubs[i]);
        if (!tr) continue;
        if (tr[0] == 0xE8) {
            const u8* callee = (const u8*)(uintptr_t)kRm2CamIc[i].addr + 5 + *(const LONG*)(kRm2CamIc[i].pro + 1);
            *(LONG*)(tr + 1) = (LONG)(callee - (tr + 5));
        }
        g_rm2IcTramp[i] = tr;
        n++;
    }
    return n;
}

// camera per pass (pos, tgt, reach as unseen.cpp g_unCam, sec since the last pass): speed for the lead, jump = hold all exact;
// a script camera IC since the last pass (g_rm2IcHold) holds too
static void Rm2CamStep(const float* pos, const float* tgt, float reach, bool ok, float sec) {
    Ts3nCounters& k = ts3native_counters;
    auto& c = g_rm2Cam;
    if (c.hold > 0) c.hold--;
    float d = 0;
    bool jump = !ok;
    if (ok && c.have) {
        const float dp = Rm2Dist(pos, c.pos), dt = Rm2Dist(tgt, c.tgt), dr = fabsf(reach - c.reach);
        d = dp > dt ? dp : dt;
        jump = d > RM2_JUMP_M || dr > RM2_JUMP_M;
    } else if (ok) jump = true;  // camera found again (load, after a lost camera)
    if (jump) {
        if (ok) k.rm2CamJump++;
        const int h = g_rm2.camIcPasses > 0 ? g_rm2.camIcPasses : 1;
        if (c.hold < h) c.hold = h;  // max: never shortens a longer script camera IC hold
    }
    const LONG ic = InterlockedExchange(&g_rm2IcHold, 0);  // a script camera IC since the last pass (Rm2CamIcHit)
    if (ic > c.hold) c.hold = ic;
    const float sp = !jump && sec > 0.001f ? d / sec : 0;
    c.speed = sp > c.speed * 0.8f ? sp : c.speed * 0.8f;  // decay: a pan that pauses one pass keeps its lead
    if (c.speed > 100.0f) c.speed = 100.0f;
    Max(&k.rm2CamSpeedMax, (LONG)(c.speed * 100.0f));
    if (ok) memcpy(c.pos, pos, 12), memcpy(c.tgt, tgt, 12), c.reach = reach;
    c.have = ok;
    if (c.hold > 0) k.rm2CamHold++;
}
static void Rm2PassCam(const LARGE_INTEGER& t) {
    const float sec = g_rm2Cam.at && g_qpf.QuadPart ? (float)(t.QuadPart - g_rm2Cam.at) / (float)g_qpf.QuadPart : 0;
    g_rm2Cam.at = t.QuadPart;
    Rm2CamStep(g_unCam.pos, g_unCam.tgt, g_unCam.reach, g_unCam.ok, sec);
}
// sphere radius of the widened view at distance d from the camera: 1 m (the classifier's) + MarginPct of the half width + lead
static float Rm2MarginR(float d, float lead) { return 1.0f + d * RM2_HALF_FOV * (float)g_rm2.marginPct / 100.0f + lead; }
// inside the view widened by MarginPct and camera speed x LeadMs, or the camera reach widened the same way; no camera = true
static bool Rm2InMargin(const float* p) {
    if (!g_unCam.ok) return true;
    const float lead = g_rm2Cam.speed * (float)g_rm2.leadMs / 1000.0f;
    const float rw = g_unCam.reach * (1.0f + (float)g_rm2.marginPct / 100.0f) + lead;
    if (Rm2Dist(p, g_unCam.tgt) < rw || Rm2Dist(p, g_unCam.pos) < rw) return true;
    __try {
        __declspec(align(16)) float sph[4] = {p[0], p[1] + 1.0f, p[2], Rm2MarginR(Rm2Dist(p, g_unCam.pos), lead)};
        const void* fr = g_unFrustum();
        return !fr || g_unInFrustum(fr, NULL, sph) != 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.rm2Faults++; return true; }
}
// within NearVisM of a Sim visible last pass ([UnseenAnim]'s list: non-root-motion poses; ours: root-motion ones); full = true
static bool Rm2NearVis(const float* p) {
    const float r2 = (float)(g_rm2.nearVisM * g_rm2.nearVisM);
    const auto& a = g_aaVis[g_aaCur ^ 1];
    const auto& b = g_rm2Vis[g_rm2VisCur ^ 1];
    if (a.full || b.full) return true;
    for (int i = 0; i < a.n; i++) {
        const float x = p[0] - a.pos[i][0], y = p[1] - a.pos[i][1], z = p[2] - a.pos[i][2];
        if (x * x + y * y + z * z < r2) return true;
    }
    for (int i = 0; i < b.n; i++) {
        const float x = p[0] - b.pos[i][0], y = p[1] - b.pos[i][1], z = p[2] - b.pos[i][2];
        if (x * x + y * y + z * z < r2) return true;
    }
    return false;
}
// first exact reason for a root-motion actor this pass, or -1 (coarse-eligible); cheapest tests first
static int Rm2WhyGame(void* actor) {
    Ts3nCounters& k = ts3native_counters;
    void* owner = Rm2Owner(actor);
    __try {
        if (!owner || !g_unComp(owner, NULL, *g_unBitSim)) return RM2_NOTSIM;
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.rm2Faults++; return RM2_NOTSIM; }
    if (g_rm2Cam.hold > 0) return RM2_CAM;
    int kind = 0;
    bool par = false;
    float pos[3];
    const int c = UnObjClass(owner, &kind, pos, &par);
    if (c < 0) return RM2_NOCLASS;
    if (c == 0) {
        auto& v = g_rm2Vis[g_rm2VisCur];
        if (v.n < RM2_VMAX) memcpy(v.pos[v.n++], pos, 12); else v.full = true;
        return RM2_VISIBLE;
    }
    if (Rm2InMargin(pos)) return RM2_MARGIN;
    if (par || UtHeldObj(owner)) return RM2_PARENT;
    __try {
        const u8* sc = (const u8*)g_unComp(owner, NULL, *g_aaBitSacs);
        if (sc && *(void* const*)(sc + 8) != *(void* const*)(sc + 0xC)) return RM2_SACS;
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.rm2Faults++; return RM2_SACS; }
    if (g_rmSlFull || !RmHas(g_rmSet[g_rmCur], actor) || !RmHas(g_rmSet[g_rmCur ^ 1], actor)) return RM2_STARTED;
    if (Rm2NearVis(pos)) return RM2_NEARVIS;
    if (UtViewerWhy(pos) >= 0) return RM2_VIEWER;
    u8* agent = (u8*)AoAgentOf(owner, pos);
    void* path = NULL;
    if (!agent) return RM2_NOAGENT;
    __try {
        if (*(void* const*)(agent + 4) != owner) return RM2_NOAGENT;  // the grid is from the pass start: still this owner's agent
        if (UtGroupLinked(agent)) return RM2_PARENT;
        if (*(const DWORD*)(agent + 0x94) != 1) return RM2_NOROUTE;  // 1 = a walking Sim (unseen.cpp UTW_STATE)
        path = *(void* const*)(agent + 0x10);
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.rm2Faults++; return RM2_NOAGENT; }
    if (!path) return RM2_NOROUTE;
    const int w = UtPathWhy(agent, path, (float)g_rm2.goalM, (float)g_rm2.portalM);
    return w == UTW_NEAR ? RM2_GOAL : w == UTW_PORTAL ? RM2_PORTAL : w >= 0 ? RM2_NOROUTE : -1;
}

// this pass's slot for the actor, decided once per pass; NULL = the hash slot holds another actor's owed steps (exact)
static void Rm2EndDrift(void* actor, int arm);
static Rm2Slot* Rm2Decide(void* actor, bool coarseArm) {
    Ts3nCounters& k = ts3native_counters;
    Rm2Slot* s = &g_rm2Slot[RmHash(actor)];
    if (s->actor == actor && s->pass == g_rmPassN) return s;
    if (s->actor != actor && s->pass == g_rmPassN && s->owedN > 0) { k.rm2Why[RM2_SLOT]++; return NULL; }
    const bool cont = s->actor == actor && s->pass + 1 == g_rmPassN;  // root motion on the last pass too
    const bool wasElig = cont && s->why < 0, wasArm = s->arm;
    if (!cont) s->actor = actor, s->streak = 0;
    s->pass = g_rmPassN, s->n = s->owedN = 0, s->owed = 0, s->arm = coarseArm;
    int why = g_rm2WhyFn(actor);
    if (why == RM2_GOAL && wasElig) Rm2EndDrift(actor, wasArm ? 1 : 0);  // route end after eligible travel
    if (why >= 0) s->streak = 0;
    else if (s->streak < 0x7FFF) s->streak++;
    if (why < 0 && s->streak <= g_rm2.hystPasses) why = RM2_HYST;
    s->why = why;
    if (why >= 0) k.rm2Why[why]++;
    else k.rm2Elig++, k.rm2CoarsePasses += coarseArm ? 1 : 0;
    return s;
}
// drift at the actor's last call of the pass: transform vs this step's agent write; arm 0 exact, 1 coarse; buckets <5, <10,
// <20, <30, <50, >=50 cm
static void Rm2Drift(void* actor, int arm) {
    Ts3nCounters& k = ts3native_counters;
    void* owner = Rm2Owner(actor);
    if (!owner) return;
    const Rm2Route* r = Rm2RouteOf(owner);
    float p[3];
    if (!r) { k.rm2RouteStale++; return; }
    if (!RmPosOf(actor, p)) return;
    const LONG cm = (LONG)(Rm2Dist(p, r->p) * 100.0f + 0.5f);
    k.rm2DriftN[arm * 6 + (cm < 5 ? 0 : cm < 10 ? 1 : cm < 20 ? 2 : cm < 30 ? 3 : cm < 50 ? 4 : 5)]++;
    Max(&k.rm2DriftMaxCm[arm], cm);
}
// route end (goal band after an eligible pass): transform before this pass's first step vs this step's agent write
static void Rm2EndDrift(void* actor, int arm) {
    Ts3nCounters& k = ts3native_counters;
    void* owner = Rm2Owner(actor);
    const Rm2Route* r = owner ? Rm2RouteOf(owner) : NULL;
    float p[3];
    if (!r || !RmPosOf(actor, p)) return;
    k.rm2EndN[arm]++;
    if (Rm2Dist(p, r->p) >= 0.3f) k.rm2End30[arm]++;
}
// Place=1: replay the agent's write of this step (0x954580 -> 0x959720 on the owner's transform); never parented (an exact zone)
static void Rm2Place(void* actor) {
    Ts3nCounters& k = ts3native_counters;
    void* owner = Rm2Owner(actor);
    const Rm2Route* r = owner ? Rm2RouteOf(owner) : NULL;
    if (!r) return;
    __declspec(align(16)) float q[4], p[4];
    memcpy(q, r->q, 16), memcpy(p, r->p, 16);
    __try { g_rm2SteerOrig(r->self, NULL, q, p); k.rm2Placed++; } __except (EXCEPTION_EXECUTE_HANDLER) { k.rm2Faults++; }
}
// the actor turned exact with owed steps: run them one EA step each before its normal step
static void Rm2Snap(void* actor, Rm2Slot* s) {
    Ts3nCounters& k = ts3native_counters;
    float p0[3], p1[3];
    const bool ok = RmPosOf(actor, p0);
    const int n = s->owedN;
    s->owedN = 0, s->owed = 0, g_rm2Open--;
    for (int i = 0; i < n; i++) RmPose(actor, NULL, *g_rmStepC);
    k.rm2Snap++, k.rm2SnapSteps += n;
    if (ok && RmPosOf(actor, p1)) {
        const LONG cm = (LONG)(Rm2Dist(p0, p1) * 100.0f + 0.5f);
        k.rm2SnapCm += cm;
        Max(&k.rm2SnapMaxCm, cm);
    }
}
// one root-motion pose call of EA's sub-step (isRem = the remainder walk) under Mode 2
static void Rm2Step(void* actor, void* edx, float dt, bool isRem) {
    Ts3nCounters& k = ts3native_counters;
    if (!RmOnThread()) { RmPose(actor, edx, dt); return; }
    const bool arm = RmEff() == 2;
    Rm2Slot* s = Rm2Decide(actor, arm);
    if (!s) { RmPose(actor, edx, dt); return; }
    s->n++;
    const bool last = isRem || (!g_rm2Rem && s->n >= g_rm2Steps);
    if (s->why >= 0 || !arm) {
        if (s->owedN) Rm2Snap(actor, s);
        RmPose(actor, edx, dt);
        if (last && s->why < 0) Rm2Drift(actor, 0);
        return;
    }
    if (!last && s->owedN + 1 < g_rm2.mergeSteps) {  // owe this step (time kept, run with a later call this pass)
        if (!s->owedN++) g_rm2Open++;
        s->owed += dt, k.rm2Owed++;
        return;
    }
    const float owed = s->owed;
    if (s->owedN) g_rm2Open--;
    s->owedN = 0, s->owed = 0;
    RmPose(actor, edx, dt + owed);
    k.rm2Coarse++;
    Rm2AddMs(&k.rm2DtMs, &g_rm2DtCarry, owed);
    __try {
        if (!g_rmTest(actor)) k.rm2EndInMerge++;  // root motion ended inside the merged step (EA still holds the owner ref)
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.rm2Faults++; }
    if (last) Rm2Drift(actor, 1);
    if (g_rm2.place) Rm2Place(actor);
}
static void Rm2PassBegin(float dt) {
    g_rm2Steps = RmStepsFor(dt, &g_rm2Rem);
    g_rm2VisCur ^= 1;
    g_rm2Vis[g_rm2VisCur].n = 0, g_rm2Vis[g_rm2VisCur].full = false;
}
// owed steps not run by the pass end (the actor got no last call: unlinked, or its root motion ended from outside): dropped
static void Rm2PassEnd() {
    if (!g_rm2Open) return;
    Ts3nCounters& k = ts3native_counters;
    for (int i = 0; i < RM_SET; i++) {
        Rm2Slot& s = g_rm2Slot[i];
        if (s.pass != g_rmPassN || !s.owedN) continue;
        k.rm2Lost++;
        Rm2AddMs(&k.rm2LostMs, &g_rm2LostCarry, s.owed);
        s.owedN = 0, s.owed = 0;
    }
    g_rm2Open = 0;
}
static void __fastcall RmStepThunk(void* actor, void* edx, float dt) {  // 0x571D20
    if (g_rm2.on) Rm2Step(actor, edx, dt, false); else RmPose(actor, edx, dt);
}
static void __fastcall RmRemThunk(void* actor, void* edx, float dt) {  // 0x571DDC
    if (g_rm2.on) Rm2Step(actor, edx, dt, true); else RmPose(actor, edx, dt);
}

// ---- Mode 1: the sub-step walks over the walk-1 shortlist ----
static void RmCandAdd(void* e) {
    if (g_rmAllFull || RmHas(g_rmCand, e)) return;
    if (g_rmCandN >= RM_SLMAX) { g_rmAllFull = true, ts3native_counters.rmSlCandFull++; return; }
    RmAdd(g_rmCand, e), g_rmCandN++;
}
// replaces `call 0x5718C0` at 0x9402E3 (anim component activation 0x9402C0 links the entry at the list head)
static void __fastcall RmInsThunk(void* list, void* edx, void* entry) {
    g_rmInsOrig(list, edx, entry);
    if (RmOnThread()) ts3native_counters.rmSlIns++, RmCandAdd(entry);
}
// replaces `mov byte ptr [esi+5FBh],al` at 0x569CC9 (call + nop; flags are dead there: next come mov, mov, test)
static __declspec(naked) void RmClipStub() {
    __asm {
        mov byte ptr [esi + 0x5FB], al
        test al, al
        jz rm_clip_done
        mov dword ptr [g_rmDirty], 1
        inc dword ptr [g_rmClipN]
    rm_clip_done:
        ret
    }
}
// audit miss: log the path (first 16) and run EA's full walks for the rest of this pass and up to the next audit
static void RmMiss(const u8* e, const u8* owner) {
    g_rmAllFull = true, g_rmMissHold = 63;
    if (InterlockedIncrement(&g_rmMissLogs) > 16) return;
    DWORD lo = 0, hi = 0, vt = 0, fl = 0, rig = 0, tm = 0;
    int sim = -1;
    __try {
        fl = *(const DWORD*)(e + 0x2C), rig = *(const DWORD*)(e + 0x24);
        if (rig) tm = *(const DWORD*)(uintptr_t)(rig + 0xB0);
        if (owner) vt = *(const DWORD*)owner, lo = *(const DWORD*)(owner + 8), hi = *(const DWORD*)(owner + 0xC);
        if (owner) sim = g_unComp((void*)owner, NULL, *g_unBitSim) ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("unseen root motion: audit miss: actor %08lX owner %08lX handle %08lX%08lX vtable %08lX sim %d, flags [+0x2C] %08lX (bit 15 %d, "
        "bit 31 %d), rig %08lX tracks %08lX; root-motion clip assigns this pass %ld (dirty %ld), in last pass's set %d, pass %lu walk %d "
        "(audit %d); EA's full walks until the next audit", (DWORD)(uintptr_t)e, (DWORD)(uintptr_t)owner, hi, lo, vt,
        sim, fl, (int)(fl >> 15 & 1), (int)(fl >> 31), rig, tm, g_rmClipN, g_rmDirty,
        RmHas(g_rmSet[g_rmCur ^ 1], (void*)e) ? 1 : 0, g_rmPassN, g_rmWalkN, g_rmAudit ? 1 : 0);
}
// One walk with EA's cursor protocol (cursor at list +0x10 always points at the next entry, so an entry unlinked by a pose
// call is skipped as in EA). EA's per-actor body runs for flagged actors, candidates and bit-15 actors; for every entry when
// the walk is full (audit, candidates full) or dirty (a root-motion clip was assigned since the last walk start, or by a
// pose call earlier in this walk: then the flag stays set, so the next walk covers the entries before that point).
static void RmSlWalk(u8* list, float step) {
    Ts3nCounters& k = ts3native_counters;
    const bool full = g_rmAllFull || g_rmAudit;
    bool dirty = g_rmDirty != 0;
    g_rmDirty = 0;
    if (full || dirty) k.rmSlFullWalks++;
    g_rmWalkN++;
    u8* const sent = list - 0x18;
    u8** const cur = (u8**)(list + 0x10);
    u8* const head = *(u8**)list;
    *cur = head ? head - 0x18 : NULL;
    while (*cur != sent) {
        u8* const e = *cur;
        u8* const nx = *(u8**)(e + 0x18);
        *cur = nx ? nx - 0x18 : NULL;
        if (!dirty && g_rmDirty) dirty = true, k.rmSlFullWalks += full ? 0 : 1;
        const bool known = RmHas(g_rmSet[g_rmCur], e) || (g_rmCandN && RmHas(g_rmCand, e));  // flagged in walk 1 or a candidate
        const bool b15 = (*(const DWORD*)(e + 0x2C) & 0x8000) != 0;
        if (!known && !b15 && !full && !dirty) continue;
        u8* const owner = *(u8**)(e + 4);
        if (owner) InterlockedIncrement((LONG*)(owner + 4));
        if (g_rmTest(e)) {
            const u8* rig = *(const u8* const*)(e + 0x24);
            if (rig && *(void* const*)(rig + 0xB0)) {
                if (!known) {  // a late find (rmLateRm counts the calls)
                    k.rmSlLate++;
                    if (b15 && !full && !dirty) k.rmSlBit15++;
                    if (!b15 && !dirty && !g_rmAllFull) k.rmSlMiss++, RmMiss(e, owner);  // only an audit walk gets here
                    RmCandAdd(e);
                }
                RmPose(e, NULL, step);
            }
        }
        if (owner && InterlockedDecrement((LONG*)(owner + 4)) == 0) {  // EA: xchg [owner+4],1 then vtable +0 (owner, 1)
            InterlockedExchange((LONG*)(owner + 4), 1);
            ((RmRelFn)(**(void***)owner))(owner, NULL, 1);
        }
    }
    k.rmSlSteps++;
}
// from RmSubStub (flag set): 1 = sub-steps and remainder ran here, caller goes to 0x571E0A; 0 = EA's code at 0x571C8E runs
// them (Mode 1 off, not the sim thread's pass, or the walk-1 shortlist overflowed: rmSlFallback_1)
static int __cdecl RmSubSteps(u8* list, float* dt) {
    Ts3nCounters& k = ts3native_counters;
    if (RmEff() != 1 || !RmOnThread()) return 0;
    if (g_rmSlFull) { k.rmSlFallback[1]++; return 0; }
    k.rmSlPasses++;
    if (g_rmAudit) k.rmSlAudit++;
    const float c = *g_rmStepC;
    float d = *dt;
    while (d >= c) d -= c, RmSlWalk(list, c);
    if (d >= *g_rmRemC) RmSlWalk(list, d);
    return 1;
}
// replaces `cmp byte ptr [esp+13h],0 / je 0x571E0A` at 0x571C83 (reached by jmp: EA's frame, edi = list, [esp+0x1C] = dt)
static __declspec(naked) void RmSubStub() {
    __asm {
        cmp byte ptr [esp + 0x13], 0
        je rm_done
        lea eax, [esp + 0x1C]
        push eax
        push edi
        call RmSubSteps
        add esp, 8
        test eax, eax
        jnz rm_done
        jmp dword ptr [g_rmJmpEa]
    rm_done:
        jmp dword ptr [g_rmJmpDone]
    }
}

// [UnseenRootMotion] Enabled=1 (needs [UnseenAnim] Enabled=1 installed first): the four probe call sites, or none; Shortlist=1
// also the jump at 0x571C83 (Mode 1 runs only with it); Mode = start value of rmMode (0, 1, 2); Mode=2 also hooks the
// steering slot 0x10445C0 (route record for drift / Place; checked to hold 0x954580, else skipped and Mode 2 runs without it)
// and, with CamIc=1 (default), the eleven script camera IC entries (kRm2CamIc)
static bool ApplyUnseenRootMotion(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"UnseenRootMotion", L"Enabled", 0, ini)) return false;
    Ts3nCounters& k = ts3native_counters;
    if (k.aaReady != 3) {
        Log("FAIL unseen root motion: needs [UnseenAnim] Enabled=1 with both its sites patched (ready 0x%lX); nothing written", k.aaReady);
        return true;
    }
    const auto in = [&](const wchar_t* key, int def, int lo, int hi) { return Clamp((int)GetPrivateProfileIntW(L"UnseenRootMotion", key, def, ini), lo, hi); };
    g_rm2.mergeSteps = in(L"MergeSteps", 4, 2, 8), g_rm2.goalM = in(L"GoalM", 3, 0, 30), g_rm2.portalM = in(L"PortalM", 3, 0, 30);
    g_rm2.nearVisM = in(L"NearVisM", 10, 0, 50), g_rm2.marginPct = in(L"MarginPct", 25, 0, 100), g_rm2.leadMs = in(L"LeadMs", 500, 0, 3000);
    g_rm2.hystPasses = in(L"HystPasses", 8, 0, 64), g_rm2.camIcPasses = in(L"CamIcPasses", 30, 0, 300), g_rm2.place = in(L"Place", 0, 0, 1);
    const CallSite sites[] = {{RM_TS_CALL, RM_TS, (void*)RmTsThunk}, {RM_FLAG_CALL, RM_FLAG, (void*)RmFlagThunk},
                              {RM_STEP_CALL, RM_POSE, (void*)RmStepThunk}, {RM_REM_CALL, RM_POSE, (void*)RmRemThunk}};
    if (!PatchCalls("unseen root motion (0x571BF3, 0x571C4C, 0x571D20, 0x571DDC)", sites, 4)) return true;
    k.rmReady = 1;
    g_rmOn = true;
    if (in(L"Shortlist", 0, 0, 1)) {  // late-start hooks first (insert call, clip flag write), the jump only after both
        const CallSite ins[] = {{RM_INS_CALL, RM_INS, (void*)RmInsThunk}};
        u8 cl[sizeof kRmClipPro] = {0xE8, 0, 0, 0, 0, 0x90};
        *(LONG*)(cl + 1) = (LONG)((DWORD)(uintptr_t)RmClipStub - (RM_CLIP_AT + 5));
        if (!Matches((const void*)(uintptr_t)RM_SUB_AT, kRmSubPro, sizeof kRmSubPro) ||
            !Matches((const void*)(uintptr_t)RM_CLIP_AT, kRmClipPro, sizeof kRmClipPro)) {
            Log("SKIP unseen root motion shortlist: bytes at 0x%08lX or 0x%08lX differ, nothing written (Mode 1 unavailable)",
                RM_SUB_AT, RM_CLIP_AT);
        } else if (!PatchCalls("unseen root motion shortlist insert (0x9402E3)", ins, 1)) {
            // logged by PatchCalls; no jump, Mode 1 unavailable
        } else if (!Write((void*)(uintptr_t)RM_CLIP_AT, cl, sizeof cl)) {
            Log("FAIL unseen root motion shortlist: write at 0x%08lX (err %lu)", RM_CLIP_AT, GetLastError());
        } else {
            u8 j[sizeof kRmSubPro];
            memset(j, 0x90, sizeof j);
            j[0] = 0xE9, *(LONG*)(j + 1) = (LONG)((DWORD)(uintptr_t)RmSubStub - (RM_SUB_AT + 5));
            if (Write((void*)(uintptr_t)RM_SUB_AT, j, sizeof j)) k.rmReady |= 2;
            else Log("FAIL unseen root motion shortlist: write at 0x%08lX (err %lu)", RM_SUB_AT, GetLastError());
        }
    }
    const int mode = in(L"Mode", 0, 0, 2);
    if (mode == 2) {  // Mode 2 runs on EA's loop (the four probe sites); the steering slot only feeds drift and Place
        g_rm2.on = true, k.rmReady |= 4;
        DWORD* slot = (DWORD*)(uintptr_t)RM2_STEER_SLOT;
        const DWORD hook = (DWORD)(uintptr_t)Rm2SteerThunk;
        if (!Readable(slot, 4) || *slot != RM2_STEER)
            Log("SKIP unseen root motion Mode 2 route record: vtable slot 0x%08lX holds 0x%08lX, not 0x%08lX (no drift, no Place)",
                RM2_STEER_SLOT, Readable(slot, 4) ? *slot : 0, RM2_STEER);
        else if (Write(slot, &hook, 4)) k.rmReady |= 8;
        else Log("FAIL unseen root motion Mode 2 route record: write at 0x%08lX (err %lu)", RM2_STEER_SLOT, GetLastError());
        if (in(L"CamIc", 1, 0, 1)) {  // script camera ICs (docs/228 §11.6); each one alone, the 20 m heuristic covers the rest
            const int n = Rm2InstallCamIc();
            if (n == RM2_IC_N) k.rmReady |= 16;
            Log("%s unseen root motion Mode 2 camera ICs: %d of %d hooked", n == RM2_IC_N ? "OK  " : "SKIP", n, (int)RM2_IC_N);
        }
    }
    k.rmMode = mode == 2 ? 2 : mode == 1 && (k.rmReady & 2) ? 1 : 0;
    Log("OK   unseen root motion: ready 0x%lX, mode %ld (ini %d); probe 1 pass in 16 timed; Mode 1 = sub-steps over the walk-1 "
        "shortlist + late starts (insert 0x9402E3, clip 0x569CC9, bit 15; audit 1 pass in 64) (--ab unseenrm, needs Shortlist=1); "
        "Mode 2 = coarse root-motion steps for unseen Sims (--ab unseenrm, on = 2)", k.rmReady, k.rmMode, mode);
    Log("     unseen root motion Mode 2%s: MergeSteps=%ld GoalM=%ld PortalM=%ld NearVisM=%ld MarginPct=%ld LeadMs=%ld "
        "HystPasses=%ld CamIcPasses=%ld Place=%ld", g_rm2.on ? "" : " keys (Mode != 2: not used)", g_rm2.mergeSteps, g_rm2.goalM,
        g_rm2.portalM, g_rm2.nearVisM, g_rm2.marginPct, g_rm2.leadMs, g_rm2.hystPasses, g_rm2.camIcPasses, g_rm2.place);
    return true;
}
