// [UnseenAnim] (docs/144 §3.4, design U-A1, counters v147 aa*, Rodney 2026-11-02): the animation pose pass runs less often
// for Sims the player cannot see. Included by ts3native.cpp after ghost.cpp (uses unseen.cpp's classifier and viewer table).
//
// Sites [verified disassembly, Steam 1.67]:
//   0xD81C73 in MonoScriptHost_Simulate: `call 0x571B90` (Animation_UpdateActors), thiscall(actor list; float dt), `ret 4`.
//   0x571B90 walks the actor list; per actor 0x942C00(dt) (time step, never touched here), then, when the actor has a rig
//   ([actor+0x24]+0xB0 = its TrackManager) and dt > 0: 0x93E190 = flag bit 15 of [actor+0x2C] or 0x582830 (any track's
//   vtable +0x2C, the root-motion getter: ClipController 0x5681C0 = clip flag byte +0x5FB, docs/22 §5.1a). True = the
//   actor is posed later in EA's fixed 1/30 s sub-steps (0x571D20 / 0x571DDC, untouched). False = one call
//   0x571C3C `call 0x940430` (rig pose, thiscall(actor; float dt), `ret 4`) with the whole tick's dt: that call is ours.
//   So only actors whose tracks carry no root motion are ever decimated; EA itself already feeds this call any dt the tick
//   has (large at fast speeds), so a summed step stays in the range the function sees from EA [inferred: no clamp read].
// Rule (sim thread only), per call at 0x571C3C for an actor whose owner [actor+4] has the Sim component:
//   - class from unseen.cpp's UnObjClass (on screen, or within the camera reach of max(80 m, 2 x height) = visible);
//   - an unseen Sim with no hold reason runs every Every-th pass (staggered by actor address) with dt summed over the
//     skipped passes (CapPasses: the debt cap scales with the pass's dt); holds (run now with whatever it owes): parented (vehicle, horse, carried), within NearVisM of a Sim that
//     was visible last pass (social partner, crowd edge), within 80 m of an active-household Sim or no fresh household table
//     (ts3fixes UnseenViewers.cs, same rule as [UnseenThrottle]), hash slot held by another actor with debt, debt + dt over
//     MaxOwedMs;
//   - visible again (or the switch turned off): runs that same pass with the owed time (snap-back, aaSnap / aaFlushOff).
// What a skipped call delays for that Sim by at most Every-1 passes [verified as the content of 0x940430, docs/22 §2]:
// clip events of non-root-motion tracks (sound, VFX, SACS script events, AnimationDone), the routing notify / footprint
// refresh (0x947590 -> 0x833560) and the transform publish to the render side; time is summed, not dropped.
// Timing: every call at the site is timed by class (aaN / aaUs), the whole pass too (aaPassUs), in both arms.
// Non-Sim actors (Objects / ObjStats, counters v150 ao*): see the Objects block below AaHoldWhy.
typedef void(__fastcall* AaPoseFn)(void* actor, void* edx, float dt);
typedef void(__fastcall* AaPassFn)(void* list, void* edx, float dt);
static const DWORD AA_PASS_CALL = 0x00D81C73, AA_PASS = 0x00571B90, AA_POSE_CALL = 0x00571C3C, AA_POSE = 0x00940430;
static AaPoseFn g_aaPoseOrig = (AaPoseFn)(uintptr_t)AA_POSE;
static AaPassFn g_aaPassOrig = (AaPassFn)(uintptr_t)AA_PASS;
enum { AA_VMAX = 64, AA_SLOTS = 4096 };  // 4096: non-Sim actors share the table since Objects (~480 per pass on BP)
enum { AAH_PARENT, AAH_NEARVIS, AAH_VIEWER, AAH_NOVIEW, AAH_SLOT, AAH_CAP, AAH_VISFULL, AAH_NOCLASS, AAH_N };
struct AaSlot { void* actor; DWORD lo, hi; float owed; DWORD pass; };  // pass: last pass the actor was asked (stale check)
static_assert(offsetof(Ts3nCounters, aaSlotReset) == 0x5A8C && offsetof(Ts3nCounters, rmSlMissHold) == 0x5A94, "counters v156 (docs/registry.md)");
static AaSlot g_aaSlot[AA_SLOTS];
static struct { float pos[AA_VMAX][3]; int n; bool full; } g_aaVis[2];  // [g_aaCur] = this pass, other = last pass
static int g_aaCur;
static DWORD g_aaTid, g_aaPass;
static LONG g_aaEvery = 4;
static float g_aaMaxOwed = 0.25f, g_aaNearVis2 = 100.0f;  // MaxOwedMs 250, NearVisM 10 (squared)
// CapPasses (docs/144 §3.5, 2026-11-02): 0 = the debt cap is MaxOwedMs of game time (as built). bp-ua-ab / -r2: that cap held
// ~90% of unseen Sims' pose calls (aaHold[AAH_CAP] ~2,800-3,300/s of ~3,200 class-1 asks/s, aaSkipped ~50/s): at the test
// speed one pass already carries ~0.25 s of game time [inferred: CAP fires with no debt], so no skip ever fit. N = the cap is
// max(MaxOwedMs, N x this pass's dt), i.e. at most N passes' time in one step, never above HardCapMs.
static LONG g_aaCapPasses = 0;
static float g_aaHardCap = 1.5f;
// Crash triage 2026-11-02 (docs/144 §3.5): owed time is only ever paid inside EA's own call for the same actor in EA's walk
// of the live list (the walk holds an owner reference across it), so a destroyed actor is never posed by us [verified, this
// file]. Two stale cases remain and are closed here: an actor that left the list (lot / world change) and came back at the
// same address with the same owner (debt older than 2 x Every passes is dropped, aaStale) and a new actor list (table cleared,
// aaSlotReset). Pose-path render messages carry the owner handle and no payload manager (0x958430, 0x940310), see §3.5.
// Event ring for the exception handler (AaDrainFaultDump): the last 64 skip / owed-run / snap / drop decisions, sim thread.
enum { AAE_SKIP, AAE_OWEDRUN, AAE_SNAP, AAE_FLUSHOFF, AAE_STALE, AAE_RESET, AAE_N };
static const char* const kAaEv[AAE_N] = {"skip", "owed-run", "snap", "flush-off", "stale-drop", "reset"};
struct AaEv { void* actor; DWORD lo, hi, pass; LONG owedUs; u8 act, obj; };
static AaEv g_aaEv[64];
static DWORD g_aaEvN;
static void* g_aaList;
static void AaEvAdd(int act, const void* actor, DWORD lo, DWORD hi, float owed, bool obj) {
    AaEv& e = g_aaEv[g_aaEvN++ & 63];
    e.actor = (void*)actor, e.lo = lo, e.hi = hi, e.pass = g_aaPass, e.owedUs = (LONG)(owed * 1e6f), e.act = (u8)act, e.obj = obj;
}
// slot for this actor; a debt the actor has not touched for > 2 x Every passes is dropped (it left the actor list)
static AaSlot* AaSlotOf(void* actor, DWORD every) {
    AaSlot* s = &g_aaSlot[((uintptr_t)actor >> 4) & (AA_SLOTS - 1)];
    if (s->actor && s->owed > 0 && g_aaPass - s->pass > 2 * every) {  // any holder: a stale debt also blocks the slot (AAH_SLOT)
        InterlockedIncrement(&ts3native_counters.aaStale);
        AaEvAdd(AAE_STALE, s->actor, s->lo, s->hi, s->owed, false);
        s->actor = NULL, s->owed = 0;
    }
    if (s->actor == actor) s->pass = g_aaPass;
    return s;
}
static float AaCap(float dt) {
    if (g_aaCapPasses <= 0) return g_aaMaxOwed;
    const float c = (float)g_aaCapPasses * dt;
    return c < g_aaMaxOwed ? g_aaMaxOwed : c > g_aaHardCap ? g_aaHardCap : c;
}

static LONG AaRun(void* actor, void* edx, float dt, int cls) {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    g_aaPoseOrig(actor, edx, dt);
    const LONG us = UsSince(t0);
    InterlockedIncrement(&k.aaN[cls]);
    InterlockedExchangeAdd(&k.aaUs[cls], us);
    return us;
}

static bool AaNearVisibleBody(const float* p) {
    const auto& v = g_aaVis[g_aaCur ^ 1];
    for (int i = 0; i < v.n; i++) {
        float d2 = 0;
        for (int j = 0; j < 3; j++) d2 += (p[j] - v.pos[i][j]) * (p[j] - v.pos[i][j]);
        if (d2 < g_aaNearVis2) return true;
    }
    return false;
}
static bool AaNearVisible(const float* p) { return g_ucOn ? UcTime(UC_NEARVIS, [&] { return AaNearVisibleBody(p); }) : AaNearVisibleBody(p); }

// -1 = may skip; else the hold reason
static int AaHoldWhy(bool parented, const float* pos, const AaSlot* s, bool mine, float owed, float dt) {
    if (parented) return AAH_PARENT;
    if (g_aaVis[g_aaCur ^ 1].full) return AAH_VISFULL;
    if (AaNearVisible(pos)) return AAH_NEARVIS;
    const int vw = UtViewerWhy(pos);
    if (vw >= 0) return vw == UTW_VIEWER ? AAH_VIEWER : AAH_NOVIEW;
    if (!mine && s->actor && s->owed > 0) return AAH_SLOT;
    if (owed + dt > AaCap(dt)) return AAH_CAP;
    return -1;
}

// ---- Objects (docs/144 §3.6, counters v150 ao*, Rodney 2026-11-02 "also handle non sim objects") ----
// bp-ua-ab: ~14,400 of ~17,500 pose asks/s at 0x571C3C are non-Sim actors (aaN_2, ~7 ms/s). ObjStats=1: per owner kind and
// per owner-vtable bucket (top 8 after the first g_aoFreezeAt asks, then fixed for the run; others = bucket 8) the asks, and
// on 1 ask in 16 the class (seen / unseen), the pose us and whether the owner sits in a SACS state machine. Objects=1: an
// unseen non-Sim actor's call is summed over ObjEvery passes like an unseen Sim's (same slot table, owed time, CapPasses cap,
// NearVisM / household holds), except when it may be animating with a Sim or be route-relevant (holds below).
// SACS link [verified disassembly 0x571AD0 / 0x94E9C0, meaning inferred I-419-1]: the SACS pass looks up component
// [0x11E90F0] on the actor's owner and runs 0x94E9C0 on it, which walks a vector (+8 begin, +0xC end) of state machines and
// updates each (0x804580). A non-empty vector = the object is an actor in a running state machine (an interaction with a
// Sim, a door or elevator cycle, a SACS-driven ambient object): held; a fault reading it also holds.
// Locomotion agents [inferred I-419-2]: the agent list (sentinel [0x11D9140]+0x40, as unseentable.cpp) is walked once per
// pass into a 2D grid; an owner that is itself an agent (car, boat, bicycle) or has any agent (any Sim, seen or not) within
// UseM is held. That is the route-relevance rule: doors, gates and elevators have no native type test we know, but animate
// only with a Sim at them (I-419-3). A failed or capped walk holds every object.
// Objects are classed by UnObjClass (table row or the 1 m rule), then once more with an ObjRadiusM sphere on screen (big
// models whose origin is off screen stay exact).
static const DWORD* g_aaBitSacs = (const DWORD*)(uintptr_t)0x011E90F0;
enum { AO_KINDS = 5, AO_BKT = 8, AO_VT = 64, AO_AMAX = 1024, AO_CELLS = 256, AO_WALK = 4096 };
enum { AOK_NOCLASS, AOK_AGENT, AOK_PARENT, AOK_SACS, AOK_PLAIN };
enum { AOH_PARENT, AOH_SACS, AOH_AGENT, AOH_NEARVIS, AOH_VIEWER, AOH_SLOT, AOH_CAP, AOH_NOCLASS, AOH_N, AOH_SEEN = AOH_N };
static bool g_aoOn, g_aoStats;
static LONG g_aoEvery = 4;
static float g_aoUseM = 8.0f, g_aoRadius = 5.0f;
static DWORD g_aoAsks, g_aoPre, g_aoFreezeAt = 65536;
static struct { void* vt; LONG n; } g_aoVt[AO_VT];
static struct { void* owner[AO_AMAX]; void* agent[AO_AMAX]; float x[AO_AMAX], z[AO_AMAX]; short next[AO_AMAX]; short head[AO_CELLS]; int n; bool ok; } g_aoAg;
struct AoInfo { int cls, kind, nearK; float pos[3]; bool par, sacs, idOk; DWORD lo, hi; };  // nearK, not near: windows.h macro

static int AoCellOf(float v) { return (int)floorf(v / g_aoUseM); }
static int AoHash(int ix, int iz) { return (int)(((unsigned)ix * 73856093u) ^ ((unsigned)iz * 19349663u)) & (AO_CELLS - 1); }
static void AoWalkBody() {
    const u8* sys = *g_unLocoSys;
    if (!sys) { g_aoAg.ok = false; return; }
    u8* const sent = (u8*)sys + 0x40;
    int walked = 0;
    for (u8* node = *(u8* const*)sent; node && node != sent; node = *(u8* const*)node) {
        if (++walked > AO_WALK || g_aoAg.n == AO_AMAX) { g_aoAg.ok = false; return; }
        void* owner = *(void* const*)(node - 0x40 + 4);
        void* tc = owner ? g_unComp(owner, NULL, *g_unBitTransform) : NULL;
        if (!tc) continue;
        __declspec(align(16)) float p[4];
        const float* q = g_unPos(tc, NULL, p);
        const int i = g_aoAg.n++, h = AoHash(AoCellOf(q[0]), AoCellOf(q[2]));
        g_aoAg.owner[i] = owner, g_aoAg.agent[i] = node - 0x40, g_aoAg.x[i] = q[0], g_aoAg.z[i] = q[2];
        g_aoAg.next[i] = g_aoAg.head[h], g_aoAg.head[h] = (short)i;
    }
}
static void AoWalk() {
    Ts3nCounters& k = ts3native_counters;
    memset(g_aoAg.head, 0xFF, sizeof g_aoAg.head);  // -1 = empty cell
    g_aoAg.n = 0, g_aoAg.ok = true;
    __try { AoWalkBody(); } __except (EXCEPTION_EXECUTE_HANDLER) { g_aoAg.ok = false; }
    if (!g_aoAg.ok) k.aoWalkFail++;
    k.aoAgents = g_aoAg.n;
}
// 3 = the owner is a locomotion agent itself, 2 = an agent within UseM (2D), 1 = one in the 3x3 cell window only, 0 = none,
// -1 = no usable agent list
static int AoNear(const void* owner, const float* p) {
    if (!g_aoAg.ok) return -1;
    const int cx = AoCellOf(p[0]), cz = AoCellOf(p[2]);
    const float r2 = g_aoUseM * g_aoUseM;
    int best = 0;
    for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
            for (int i = g_aoAg.head[AoHash(cx + dx, cz + dz)]; i >= 0; i = g_aoAg.next[i]) {
                if (g_aoAg.owner[i] == owner) return 3;
                const float ex = g_aoAg.x[i] - p[0], ez = g_aoAg.z[i] - p[2];
                const int n = ex * ex + ez * ez < r2 ? 2 : 1;
                if (n > best) best = n;
            }
    return best;
}

// the owner's locomotion agent from this pass's grid (positions at the pass start, 3x3 cells around p), NULL = none or no grid
// ([UnseenRootMotion] Mode 2: route and goal tests)
static void* AoAgentOf(const void* owner, const float* p) {
    if (!g_aoAg.ok) return NULL;
    const int cx = AoCellOf(p[0]), cz = AoCellOf(p[2]);
    for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
            for (int i = g_aoAg.head[AoHash(cx + dx, cz + dz)]; i >= 0; i = g_aoAg.next[i])
                if (g_aoAg.owner[i] == owner) return g_aoAg.agent[i];
    return NULL;
}

// owner-vtable bucket 0-7, 8 = other, -1 = not fixed yet (counted in g_aoVt) or unreadable
static int AoBucket(void* owner) {
    Ts3nCounters& k = ts3native_counters;
    void* vt;
    __try { vt = *(void* const*)owner; } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
    if (k.aoFrozen) {
        for (int i = 0; i < AO_BKT; i++)
            if (k.aoBktVt[i] == (LONG)(uintptr_t)vt) return i;
        return AO_BKT;
    }
    for (DWORD h = ((uintptr_t)vt >> 3) & (AO_VT - 1), n = 0; n < AO_VT; h = (h + 1) & (AO_VT - 1), n++) {
        if (g_aoVt[h].vt == vt) { g_aoVt[h].n++; break; }
        if (!g_aoVt[h].vt) { g_aoVt[h].vt = vt, g_aoVt[h].n = 1; break; }
    }  // shortcut: a 65th vtable before the freeze is not counted; fine for a top-8 pick
    if (++g_aoPre >= g_aoFreezeAt) {
        for (int b = 0; b < AO_BKT; b++) {
            int best = -1;
            for (int i = 0; i < AO_VT; i++)
                if (g_aoVt[i].vt && g_aoVt[i].n > 0 && (best < 0 || g_aoVt[i].n > g_aoVt[best].n)) best = i;
            if (best < 0) break;
            k.aoBktVt[b] = (LONG)(uintptr_t)g_aoVt[best].vt, g_aoVt[best].n = -1;
        }
        k.aoFrozen = (LONG)g_aoPre;
    }
    return -1;
}

static void AoInfoOf(void* owner, AoInfo* o) {
    int kd = 1;
    o->par = false, o->sacs = true, o->idOk = false, o->lo = o->hi = 0;
    o->cls = UnObjClass(owner, &kd, o->pos, &o->par);
    __try {
        o->lo = *(const DWORD*)((const u8*)owner + 8), o->hi = *(const DWORD*)((const u8*)owner + 0xC), o->idOk = true;
        const u8* sc = (const u8*)g_unComp(owner, NULL, *g_aaBitSacs);
        o->sacs = sc && *(void* const*)(sc + 8) != *(void* const*)(sc + 0xC);
        if (o->cls == 1) {  // a big model: ObjRadiusM sphere 1 m up, on screen = seen
            __declspec(align(16)) float sph[4] = {o->pos[0], o->pos[1] + 1.0f, o->pos[2], g_aoRadius};
            const void* fr = g_unFrustum();
            if (!fr || g_unInFrustum(fr, NULL, sph)) o->cls = 0;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        o->sacs = true;
        if (o->cls == 1) o->cls = 0;
    }
    o->nearK = o->cls >= 0 ? AoNear(owner, o->pos) : -1;
    o->kind = o->cls < 0 ? AOK_NOCLASS : o->nearK == 3 ? AOK_AGENT : o->par ? AOK_PARENT : o->sacs ? AOK_SACS : AOK_PLAIN;
}

// -1 = may skip; AOH_SEEN = visible (snap-back); else the hold reason
static int AoHoldWhy(const AoInfo* o, const AaSlot* s, bool mine, float owed, float dt) {
    if (o->cls < 0) return AOH_NOCLASS;
    if (o->cls == 0) return AOH_SEEN;
    if (o->par) return AOH_PARENT;
    if (o->nearK < 0 || o->nearK >= 2) return AOH_AGENT;
    if (o->sacs) return AOH_SACS;
    const int w = AaHoldWhy(false, o->pos, s, mine, owed, dt);
    if (w < 0) { InterlockedIncrement(&ts3native_counters.aoNear[o->nearK == 1 ? 0 : 1]); return -1; }
    if (w == AAH_NEARVIS || w == AAH_VISFULL) return AOH_NEARVIS;
    if (w == AAH_VIEWER || w == AAH_NOVIEW) return AOH_VIEWER;
    return w == AAH_SLOT ? AOH_SLOT : AOH_CAP;
}

static void AoSample(int b, const AoInfo* o, LONG us) {
    Ts3nCounters& k = ts3native_counters;
    const int c = o->cls == 1 ? 1 : 0;
    k.aoKindN[o->kind][c]++, k.aoKindUs[o->kind] += us;
    if (b < 0) return;
    k.aoBktS[b][c]++, k.aoBktUs[b] += us;
    if (o->sacs) k.aoBktSacs[b]++;
}

// one non-Sim pose call at 0x571C3C (sim thread)
static void AaObj(void* actor, void* edx, float dt, void* owner) {
    Ts3nCounters& k = ts3native_counters;
    const bool gate = g_aoOn && k.aoMode == 1, sample = g_aoStats && (++g_aoAsks & 15) == 0;
    const int b = g_aoStats ? AoBucket(owner) : -1;
    if (b >= 0) k.aoBktN[b]++;
    AaSlot* s = AaSlotOf(actor, (DWORD)g_aoEvery);
    if (!gate && !sample && !(s->actor == actor && s->owed > 0)) { AaRun(actor, edx, dt, 2); return; }
    AoInfo o;
    AoInfoOf(owner, &o);
    if (s->actor == actor && (!o.idOk || s->lo != o.lo || s->hi != o.hi)) s->actor = NULL, s->owed = 0;  // address reused
    const bool mine = s->actor == actor;
    const float owed = mine ? s->owed : 0;
    const int why = gate ? AoHoldWhy(&o, s, mine, owed, dt) : -2;
    if (why == -1 && (g_aaPass + (DWORD)((uintptr_t)actor >> 4)) % (DWORD)g_aoEvery != 0) {
        if (!mine) s->actor = actor, s->lo = o.lo, s->hi = o.hi, s->pass = g_aaPass;
        s->owed = owed + dt;
        k.aoSkipped++;
        AaEvAdd(AAE_SKIP, actor, o.lo, o.hi, s->owed, true);
        Max(&k.aoDebtMaxUs, (LONG)(s->owed * 1e6f));
        if (sample) AoSample(b, &o, 0);
        return;
    }
    if (why >= 0 && why < AOH_N) k.aoHold[why]++;
    if (owed > 0) {
        if (!gate) k.aoFlushOff++;
        else if (why == AOH_SEEN) k.aoSnap++;
        else k.aoOwedRuns++;
        AaEvAdd(!gate ? AAE_FLUSHOFF : why == AOH_SEEN ? AAE_SNAP : AAE_OWEDRUN, actor, o.lo, o.hi, owed, true);
    }
    if (mine) s->owed = 0;
    const LONG us = AaRun(actor, edx, dt + owed, 2);
    if (sample) AoSample(b, &o, us);
}

static void __fastcall AaPoseThunk(void* actor, void* edx, float dt) {
    Ts3nCounters& k = ts3native_counters;
    if (GetCurrentThreadId() != g_aaTid) { g_aaPoseOrig(actor, edx, dt); return; }
    void* owner = NULL;
    DWORD lo = 0, hi = 0;
    bool sim = false;
    __try {
        owner = *(void* const*)((const u8*)actor + 4);
        sim = owner && g_unComp(owner, NULL, *g_unBitSim) != NULL;
        if (sim) lo = *(const DWORD*)((const u8*)owner + 8), hi = *(const DWORD*)((const u8*)owner + 0xC);
    } __except (EXCEPTION_EXECUTE_HANDLER) { sim = false; }
    if (!sim) { if (owner) AaObj(actor, edx, dt, owner); else AaRun(actor, edx, dt, 2); return; }
    int kind = 1;
    float pos[3] = {};
    bool parented = false;
    const int c = UnObjClass(owner, &kind, pos, &parented);
    if (UtHeldObj(owner)) {  // docs/249: a household Sim always animates (PushHeld); kdAaHeldIn = the four class counters' sum
        InterlockedIncrement(&k.kdAaHeldIn);
        InterlockedIncrement(c == 0 ? &k.kdAaHeldSeen : c < 0 ? &k.kdAaHeldNoClass : parented ? &k.kdAaHeldPar : &k.kdAaHeld);
        parented = true;
    }
    AaSlot* s = AaSlotOf(actor, (DWORD)g_aaEvery);
    if (s->actor == actor && (s->lo != lo || s->hi != hi)) s->actor = NULL, s->owed = 0;  // address reused by another Sim
    const bool mine = s->actor == actor;
    const float owed = mine ? s->owed : 0;
    if (c == 0) {
        auto& v = g_aaVis[g_aaCur];
        if (v.n < AA_VMAX) memcpy(v.pos[v.n++], pos, 12); else v.full = true;
    }
    if (c == 0 && g_utWatch.armed && !g_utWatch.animFlipped && UtWatched(owner))  // unseen_snapback (code 24): the owed pose time at the flip
        g_utWatch.animFlipped = true, g_utWatch.animOwed = owed;
    if (c != 1 || k.aaMode != 1) {
        if (owed > 0) {
            InterlockedIncrement(c == 0 ? &k.aaSnap : k.aaMode != 1 ? &k.aaFlushOff : &k.aaHold[AAH_NOCLASS]);
            AaEvAdd(c == 0 ? AAE_SNAP : k.aaMode != 1 ? AAE_FLUSHOFF : AAE_OWEDRUN, actor, lo, hi, owed, false);
        }
        if (mine) s->owed = 0;
        AaRun(actor, edx, dt + owed, c == 1 ? 1 : 0);
        return;
    }
    const int why = AaHoldWhy(parented, pos, s, mine, owed, dt);
    if (why < 0 && (g_aaPass + (DWORD)((uintptr_t)actor >> 4)) % (DWORD)g_aaEvery != 0) {
        if (!mine) s->actor = actor, s->lo = lo, s->hi = hi, s->pass = g_aaPass;
        s->owed = owed + dt;
        InterlockedIncrement(&k.aaSkipped);
        AaEvAdd(AAE_SKIP, actor, lo, hi, s->owed, false);
        Max(&k.aaDebtMaxUs, (LONG)(s->owed * 1e6f));
        return;
    }
    if (why >= 0) InterlockedIncrement(&k.aaHold[why]);
    if (owed > 0) InterlockedIncrement(&k.aaOwedRuns), AaEvAdd(AAE_OWEDRUN, actor, lo, hi, owed, false);
    if (mine) s->owed = 0;
    AaRun(actor, edx, dt + owed, 1);
}

// [UnseenRootMotion] (unseenrm.cpp, docs/228) shares this pass hook: g_rmOn is set by its Apply; RmPassBegin returns true for a
// sampled pass (which needs the agent grid below for its zone split) and on every pass with Mode 2 configured (route tests)
static bool g_rmOn;
static bool RmPassBegin(float dt);
static void RmPassClock(const LARGE_INTEGER& t0);
static void RmPassEnd();

static void __fastcall AaPassThunk(void* list, void* edx, float dt) {
    Ts3nCounters& k = ts3native_counters;
    g_aaTid = GetCurrentThreadId();
    if (list != g_aaList) {  // a new actor list (world load / unload): no debt carries over
        if (g_aaList) InterlockedIncrement(&k.aaSlotReset), AaEvAdd(AAE_RESET, list, 0, 0, 0, false);
        memset(g_aaSlot, 0, sizeof g_aaSlot), g_aaList = list;
    }
    g_aaPass++;
    InterlockedIncrement(&k.aaPasses);
    k.aaVisSims = g_aaVis[g_aaCur].n;
    g_aaCur ^= 1;
    g_aaVis[g_aaCur].n = 0, g_aaVis[g_aaCur].full = false;
    const bool rmSample = g_rmOn && RmPassBegin(dt);
    if (g_aoOn || g_aoStats || rmSample) AoWalk();  // Objects: this pass's agent grid (positions before the pass)
    UnRefreshCamera();  // the locomotion pass refreshes it too; the anim pass must not depend on [UnseenProbe] being on
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    if (g_rmOn) RmPassClock(t0);
    g_aaPassOrig(list, edx, dt);
    InterlockedExchangeAdd(&k.aaPassUs, UsSince(t0));
    if (g_rmOn) RmPassEnd();
}

// [UnseenAnim] Enabled=1: both call sites, or neither
static bool ApplyUnseenAnim(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"UnseenAnim", L"Enabled", 0, ini)) return false;
    Ts3nCounters& k = ts3native_counters;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const int every = (int)GetPrivateProfileIntW(L"UnseenAnim", L"Every", 4, ini);
    const int maxMs = (int)GetPrivateProfileIntW(L"UnseenAnim", L"MaxOwedMs", 250, ini);
    const int nearM = (int)GetPrivateProfileIntW(L"UnseenAnim", L"NearVisM", 10, ini);
    const int capPasses = (int)GetPrivateProfileIntW(L"UnseenAnim", L"CapPasses", 0, ini);
    const int hardMs = (int)GetPrivateProfileIntW(L"UnseenAnim", L"HardCapMs", 1500, ini);
    g_aaCapPasses = capPasses <= 0 ? 0 : capPasses < 2 ? 2 : capPasses > 8 ? 8 : capPasses;
    g_aaHardCap = (hardMs < 250 ? 250 : hardMs > 3000 ? 3000 : hardMs) / 1000.0f;
    g_aaEvery = every < 2 ? 2 : every > 8 ? 8 : every;
    g_aaMaxOwed = (maxMs < 50 ? 50 : maxMs > 500 ? 500 : maxMs) / 1000.0f;
    g_aaNearVis2 = (float)(nearM < 0 ? 0 : nearM) * (float)(nearM < 0 ? 0 : nearM);
    const CallSite sites[] = {{AA_PASS_CALL, AA_PASS, (void*)AaPassThunk}, {AA_POSE_CALL, AA_POSE, (void*)AaPoseThunk}};
    if (PatchCalls("unseen anim (pass 0xD81C73, pose 0x571C3C)", sites, 2)) {
        k.aaReady = 3;
        g_aaOn = true;  // unseen.cpp UtViewerMsg: accept the household table without [UnseenThrottle]
        k.aaMode = GetPrivateProfileIntW(L"UnseenAnim", L"Mode", 1, ini) == 1 ? 1 : 0;
        // Objects (docs/144 §3.6): non-Sim actors, own switch aoMode (--ab unseenobj); ObjStats = the owner histogram only
        const int objEvery = (int)GetPrivateProfileIntW(L"UnseenAnim", L"ObjEvery", 4, ini);
        const int useM = (int)GetPrivateProfileIntW(L"UnseenAnim", L"UseM", 8, ini);
        const int radM = (int)GetPrivateProfileIntW(L"UnseenAnim", L"ObjRadiusM", 5, ini);
        g_aoEvery = objEvery < 2 ? 2 : objEvery > 8 ? 8 : objEvery;
        g_aoUseM = (float)(useM < 2 ? 2 : useM > 30 ? 30 : useM);
        g_aoRadius = (float)(radM < 1 ? 1 : radM > 20 ? 20 : radM);
        g_aoOn = GetPrivateProfileIntW(L"UnseenAnim", L"Objects", 0, ini) != 0;
        g_aoStats = GetPrivateProfileIntW(L"UnseenAnim", L"ObjStats", 0, ini) != 0;
        k.aoReady = (g_aoOn ? 1 : 0) | (g_aoStats ? 2 : 0);
        k.aoMode = g_aoOn && GetPrivateProfileIntW(L"UnseenAnim", L"ObjMode", 1, ini) == 1 ? 1 : 0;
    }
    Log("%s unseen anim: ready 0x%lX, mode %ld, Every=%ld MaxOwedMs=%d NearVisM=%d CapPasses=%ld HardCapMs=%d; unseen Sims' "
        "non-root-motion pose calls summed over Every passes (--ab unseenanim)", g_aaOn ? "OK  " : "FAIL", k.aaReady, k.aaMode,
        g_aaEvery, maxMs, nearM, g_aaCapPasses, (int)(g_aaHardCap * 1000));
    Log("%s unseen anim objects: ready 0x%lX, mode %ld, ObjEvery=%ld UseM=%d ObjRadiusM=%d; unseen non-Sim actors outside a SACS "
        "state machine, not agents, no agent within UseM: summed over ObjEvery passes (--ab unseenobj)", g_aaOn ? "OK  " : "FAIL",
        k.aoReady, k.aoMode, g_aoEvery, (int)g_aoUseM, (int)g_aoRadius);
    return true;
}

// Exception handler hook (ts3native.cpp XcptLog, crash triage 2026-11-02): a wild call (eip in no module) whose return address
// is the render-message destroy in FrameDb_DrainEvents (0x59386B: `ecx=[msg+4]; call [[ecx]](msg+0x10)`, docs/144 §3.5): the
// message (pushed arg = msg+0x10), 32 bytes at its payload manager and our last 64 [UnseenAnim] decisions. Reads guarded.
static void AaHex(const char* what, DWORD at, int n) {
    char d[3 * 0x50 + 1] = "";
    int len = 0;
    for (int i = 0; i < n; i++) {
        u8 v;
        __try { v = *(const u8*)(uintptr_t)(at + i); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(d + len, sizeof d - len, " unreadable"); break; }
        len += sprintf_s(d + len, sizeof d - len, i % 4 ? "%02X" : " %02X", v);
    }
    Log("  drain fault: %s 0x%08lX:%s", what, at, d);
}
static void AaDrainFaultDump(const CONTEXT* c) {
    static volatile LONG once;
    DWORD ret = 0, arg = 0;
    __try { ret = *(const DWORD*)(uintptr_t)c->Esp, arg = *(const DWORD*)(uintptr_t)(c->Esp + 4); } __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    if (ret != 0x0059386B && ret != 0x00593880) return;  // 0x593880: the same destroy for a chunk's last message
    if (InterlockedIncrement(&once) > 2) return;
    const DWORD msg = arg - 0x10;
    Log("  drain fault: render message destroy in FrameDb_DrainEvents (ret 0x%08lX), message 0x%08lX, manager (ecx) 0x%08lX, "
        "[UnseenAnim] %s pass %lu events %lu", ret, msg, c->Ecx, g_aaOn ? "on" : "off", g_aaPass, g_aaEvN);
    AaHex("message", msg, 0x50);
    AaHex("manager", c->Ecx, 32);
    for (DWORD i = 0; i < 64 && i < g_aaEvN; i++) {
        const AaEv& e = g_aaEv[(g_aaEvN - 1 - i) & 63];
        Log("  aa event -%lu: %s %s actor %08lX owner %08lX%08lX owed %ld us pass %lu", i, e.act < AAE_N ? kAaEv[e.act] : "?",
            e.obj ? "obj" : "sim", (DWORD)(uintptr_t)e.actor, e.hi, e.lo, e.owedUs, e.pass);
    }
}
