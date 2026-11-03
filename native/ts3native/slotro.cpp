// Design SH, gate-12 family F-B (docs/90). Included by ts3native.cpp after lotfd.cpp (uses QL, LtFind and the portal thunks).
//
// [SlotEnabledRO] Enabled=1 (default 0; needs [RouteGate] Enforce=1 and the async path). The P1b second frame 0x76F15D of the
// F-B waits lies in the slot position helper 0x76F0A0 (cdecl(float4* point, obj, ageGenderSpecies, a4), returns al; callers
// IC_Slots_GetPositionOfSlotImpl 0x76F2F0, 0x755AB0 (IC_RouteManager_IsSlotUnobstructed), 0x7596E0, 0x830340), not in
// IC_Slots_IsSlotEnabledImpl 0x76F000 (it calls neither 0x76F0A0 nor the lot table). With ageGenderSpecies != 0 the helper
// finds the object's portals (LotRouting_PortalsForObject 0x62F760: LotTable_Contains = gate 12, GetOrInsert = gate 0, AddRef
// per portal), picks the lane (0x82A420 -> 0x62EB40 -> GetOrInsert) and moves the point (0x62F3B0: GetOrInsert, height).
// Gate frames hold +0x918 only for the gate call itself, so today the rest already runs beside a job; the gates are the only
// serialised part. Their reads are the lot table (worker writer W5) and the lot entry's portal vector (W4), both under QL
// exclusive on the worker. So SH runs the game's own body with QL shared held (try only): inside, RgEnter passes gate 12 and
// gate-0 hits (our LtFind) without +0x918. Anything else drops the hold (QL released, then today's wait): another gate site, a
// gate-0 miss (an insert), a portal of another class or with a count below 1, any Lock_Acquire, host or managed call
// (QlSharedTrip), an odd lot table. The sim's portal AddRef/Release then race the worker's (T-L3: worker refs outside QL), so
// the portal vtable 0xFED048 gets interlocked count functions (PortalAddRefX / PortalReleaseX in lotfd.cpp) for the whole run.
// Shadow (no job, job starts held off): the game's call, then the call again under a dry hold on a copy of the point, compared.

static const DWORD SL_POS = 0x0076F0A0;
static const u8 kSlPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};  // push ebp; mov ebp,esp; and esp,-16 (no relative operand)
static const DWORD SL_PFO_RET = 0x0062F782;  // PortalsForObject's return from LotTable_Contains (P1 key of F-B)
typedef DWORD(__cdecl* SlFn)(float* point, const u8* obj, DWORD ags, DWORD a4);
static SlFn g_slOrig;
static __declspec(thread) LONG t_slDry;
enum { SL_OK, SL_FB_STATE, SL_FB_NESTED, SL_FB_BROKEN, SL_FB_TRY };

static void SlDrop(int why) {
    if (t_slHold != 1) return;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    t_slHold = 2;
    InterlockedIncrement(&ts3native_counters.slDrop[why]);
    if (t_slDry) InterlockedIncrement(&ts3native_counters.slShadowDrop);
}

// PortalsForObject(P)(lot, idLo, idHi, out) at its Contains: every portal of this object in the lot entry must be of the
// class whose count pair SH made interlocked, with a count >= 1 (its Release then never deletes)
static int SlPortals(const u8* p, const DWORD* slot) {
    const u8* e;
    if (LtFind(p, slot[1], slot[2], &e) != LT_OK) return SLD_TABLE;
    if (!e) return -1;  // Contains says no: PortalsForObject returns at once
    const DWORD idLo = slot[9], idHi = slot[10];
    for (const DWORD* it = *(const DWORD* const*)(e + 0x44); it && it != *(const DWORD* const*)(e + 0x48); it++) {
        const u8* pt = (const u8*)(uintptr_t)*it;
        if (!pt || *(const DWORD*)(pt + 8) != idLo || *(const DWORD*)(pt + 0xC) != idHi) continue;
        if (*(const DWORD*)pt != LT_PORTAL_VT || *(const LONG*)(pt + 0x70) < 1) return SLD_PORTAL;
    }
    return -1;
}

// RgEnter (routegate.cpp), outermost non-worker entry with the hold live. slot = the gate's return slot, slot[-2] = ecx
static bool SlGate(int site, DWORD* slot) {
    Ts3nCounters& k = ts3native_counters;
    int why = SLD_GATE;
    if (site == 12) {  // LotTable_Contains(P)(lo, hi): reads only the table
        why = VfRealRet(slot) == SL_PFO_RET ? SlPortals((const u8*)(uintptr_t)slot[-2], slot) : -1;
        if (why < 0) { InterlockedIncrement(&k.slPass12); return true; }
    } else if (site == 0) {  // GetOrInsert(P+0x48)(key*): a hit writes nothing, a miss inserts
        const DWORD* key = (const DWORD*)(uintptr_t)slot[1];
        const u8* e;
        const int r = LtFind((const u8*)(uintptr_t)slot[-2] - 0x48, key[0], key[1], &e);
        if (r == LT_OK && e) { InterlockedIncrement(&k.slPass0); return true; }
        why = r == LT_OK ? SLD_MISS : SLD_TABLE;
    }
    SlDrop(why);
    return false;
}

static int SlBegin(bool dry) {
    if (t_qlDepth || t_qlShared || t_slHold) return SL_FB_NESTED;
    if (g_steerBroken) return SL_FB_BROKEN;
    if (!TryAcquireSRWLockShared(&g_ql)) return SL_FB_TRY;
    t_qlShared = 1;
    t_slHold = 1;
    t_slDry = dry;
    return SL_OK;
}
// true: the hold lasted to the return
static bool SlEnd() {
    const bool kept = t_slHold == 1;
    if (kept) {
        t_qlShared = 0;
        ReleaseSRWLockShared(&g_ql);
    }
    t_slHold = 0;
    t_slDry = 0;
    return kept;
}
// the game's body inside the hold; an unwind through it still releases QL
static DWORD SlCall(float* point, const u8* obj, DWORD ags, DWORD a4, bool* kept) {
    DWORD r = 0;
    __try {
        r = g_slOrig(point, obj, ags, a4);
    } __finally {
        *kept = SlEnd();
    }
    return r;
}

static void SlFallback(int why) {
    if (why > 0 && why <= 4) InterlockedIncrement(&ts3native_counters.slFb[why - 1]);
}

static volatile LONG g_slNoted;
static DWORD SlShadow(float* point, const u8* obj, DWORD ags, DWORD a4) {
    Ts3nCounters& k = ts3native_counters;
    __declspec(align(16)) float in[4];
    memcpy(in, point, sizeof(in));
    const DWORD r = g_slOrig(point, obj, ags, a4);
    const int why = SlBegin(true);
    if (why) { SlFallback(why); return r; }
    bool kept;
    const DWORD r2 = SlCall(in, obj, ags, a4, &kept);
    InterlockedIncrement(&k.slShadowRuns);
    if ((u8)r != (u8)r2 || memcmp(in, point, sizeof(in))) {
        InterlockedIncrement(&k.slShadowMismatch);
        if (InterlockedIncrement(&g_slNoted) <= 16)
            Log("slot position RO: shadow mismatch obj %08lX: game %u (%.3f %.3f %.3f), hold %u (%.3f %.3f %.3f)",
                (DWORD)(uintptr_t)obj, (u8)r, point[0], point[1], point[2], (u8)r2, in[0], in[1], in[2]);
    }
    return r;
}

static DWORD __cdecl SlHook(float* point, const u8* obj, DWORD ags, DWORD a4) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    // no flags or no object: the original returns before any routing read (0x76F0AC, 0x76F0BC)
    if ((!k.slMode && !k.slShadow) || !ags || !obj || tid != g_async.owner || t_slHold) return g_slOrig(point, obj, ags, a4);
    InterlockedIncrement(&k.slCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.slShadow) return g_slOrig(point, obj, ags, a4);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        const DWORD r = g_async.state == A_RUNNING ? g_slOrig(point, obj, ags, a4) : SlShadow(point, obj, ags, a4);
        RgOffSimDrop();
        return r;
    }
    if (!k.slMode) return g_slOrig(point, obj, ags, a4);
    // a job runs: hold, unless this thread holds +0x918, serves the job's callback or has a gate frame open
    const RgThread& t = t_rg;
    int why = SL_FB_STATE;
    if (!g_inInvoke && k.rgOwnerTid != (LONG)tid && t.n == t.readers) why = SlBegin(false);
    if (why) { SlFallback(why); return g_slOrig(point, obj, ags, a4); }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    bool kept;
    const DWORD r = SlCall(point, obj, ags, a4, &kept);
    if (kept) {
        const LONG us = UsSince(t0);
        InterlockedIncrement(&k.slTaken);
        MwSkip(MW_SL);
        InterlockedExchangeAdd(&k.slUs, us);
        Max(&k.slMaxUs, us);
    }
    return r;
}

// interlocked count pair in vtable 0xFED048: in the slots, or behind P1b's counting thunks (they jump through g_ltAddRef /
// g_ltRelease); refused when the slots hold anything else
static bool InstallPortalRefX() {
    DWORD* vt = (DWORD*)(uintptr_t)LT_PORTAL_VT;
    const DWORD x[2] = {(DWORD)(uintptr_t)PortalAddRefX, (DWORD)(uintptr_t)PortalReleaseX};
    if (!Readable(vt, 8)) return false;
    if (vt[0] == x[0] && vt[1] == x[1]) return true;
    if (vt[0] == (DWORD)(uintptr_t)PortalAddRefThunk && vt[1] == (DWORD)(uintptr_t)PortalReleaseThunk) {
        InterlockedExchange((volatile LONG*)&g_ltAddRef, (LONG)x[0]);
        InterlockedExchange((volatile LONG*)&g_ltRelease, (LONG)x[1]);
        return true;
    }
    if (vt[0] != 0x0064E8E0 || vt[1] != 0x0064E8F0) return false;
    return Write(vt, x, sizeof(x));
}

static void InstallSlotRo(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"SlotEnabledRO", L"Enabled", 0, ini)) { Log("slot position RO off (Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady || !g_rgTramp[0] || !g_rgTramp[12]) {
        Log("SKIP slot position RO: need [RouteGate] Enforce=1 (gate stubs 0 and 12) with the async path");
        return;
    }
    if (!Matches((const void*)(uintptr_t)SL_POS, kSlPro, sizeof(kSlPro))) { Log("SKIP slot position RO: 0x76F0A0 bytes differ"); return; }
    if (!InstallQlSubset()) { Log("SKIP slot position RO: the QL writer stubs W4/W5/W6g are not all in place"); return; }
    if (!InstallPortalRefX()) { Log("SKIP slot position RO: portal vtable 0xFED048 holds other count slots"); return; }
    k.slRefSwap = 1;
    g_slOrig = (SlFn)InstallDetour("Slots_PortalLanePosition", SL_POS, kSlPro, sizeof(kSlPro), (void*)SlHook, &g_slOrig);
    if (!g_slOrig) return;
    k.slMode = Clamp(GetPrivateProfileIntW(L"SlotEnabledRO", L"Mode", 1, ini), 0, 1);
    k.slShadow = Clamp(GetPrivateProfileIntW(L"SlotEnabledRO", L"Shadow", 0, ini), 0, 1);
    k.slReady = 1;
    Log("OK   slot position RO: 0x76F0A0 under QL shared, interlocked portal refs, QL writers W4/W5/W6g (Mode=%ld Shadow=%ld)",
        k.slMode, k.slShadow);
}
