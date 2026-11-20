// [LandCheckRO] (docs/670 §24): design SH for the closest-waypoint gather. Included by ts3native.cpp after slotro.cpp (uses
// its hold: SlBegin / SlEnd / SlDrop with kind SLK_LAND, and lotfd.cpp's LtFind).
//
// The wait: key 0x62FB74 with first caller 0x654BE5 (s189: 8 lines / 476 ms in 3 min). World_ArePointsConnectedByLand
// 0x787240 -> 0x65DF90 -> 0x65BB90 -> 0x6595B0 calls the gather 0x654790 (thiscall(area), no stack argument, AL) for an
// area flagged 0x400 by the area cache 0x652D80. For every lot waypoint (types 2 and 0x15) the gather asks
// LotTable_GetIfPresent 0x62FB60 = LotTable_Contains (gate 12) + GetOrInsert (gate 0, a hit after Contains), and each of
// those gate frames waits for the running job's +0x918. Everything else in the gather already runs with no lock today.
//
// The hold: on the sim thread while a job runs (and the thread holds no +0x918, serves no job callback, has no gate frame
// open), the game's own gather under QL shared, try only. Inside, RgEnter lets exactly two gate entries pass without +0x918:
// site 12 returning to 0x62FB74 and a site 0 HIT returning to 0x62FB8B (the two calls of 0x62FB60). Their read is the lot
// table (worker writer W5, QL exclusive); the entry's fields read right after (waypoint map +0x120, +0x10, +0x16C, level
// table +0x90) have W4 / W5 as their only worker writers. Anything else drops the hold (QL released first, then the game
// goes on as today and waits at its next gate): another gate site or caller, a gate-0 miss, an odd lot table, any
// Lock_Acquire, host or managed call.
//
// World_LotLookup's lock (docs/670 §23.1, settled in §24.1): 0x650030 -> 0xC68990 -> 0xC688A0 takes it only inside the
// lot index rebuild 0xC68680, and only when the dirty byte wm+0x1CC is set (0xC688A6). The lock is wm+0x228; 0x10786C8 is
// the timeout word pushed for Lock_Acquire. So: a dirty index is given back before the hold starts (LC_FB_DIRTY), and a
// rebuild that still turns up inside (the byte set by another thread meanwhile) reaches LockAcquireFull, whose first line
// QlSharedTrip(SLD_LOCK) drops the hold before the lock is asked for. The hold is never open across that lock.
//
// The area (new finding, §24.2): the area is one of the 10 blocks of the planner's FindClosest object W+0xE0, which the
// route worker's plans use too (0x65E0D0 -> 0x65BB90, under +0x918) while this IC touches it with no lock. Today a sim
// gather with lot waypoints stands at its first gate until the job ends; under the hold it would run on. So that the hold
// never puts the two threads into the same area's candidate vector at once where today's wait kept them apart, the gather's
// entry is watched on every thread: the sim publishes the area of its hold (g_lcSim), any other thread publishes the area it
// gathers (g_lcWk). The sim gives the call back when the worker is on that area (LC_FB_AREA) and drops its hold at the
// next gate when the worker arrives (LCD_WORKER: it then waits at that gate, as today); the worker waits at the gather's
// entry until the sim's hold on that area is over or dropped. The sim's hold has no wait in it (any lock drops it first),
// so the worker's wait is bounded by the sim's own work; a cap (must never be reached) turns the switch off.
// What is not covered, as today: the area cache's list itself (0x652D80's move-to-front and recycle on both threads).
//
// Shadow (no job, job starts held off): the game's gather, then the gather again under a dry hold; return value, flags,
// candidate count and a hash of the candidates compared.

struct LandCheckRoCounters {
    volatile LONG lcMode;            // +0x00 runtime switch (native_set landcheckro): 1 = hold during a job
    volatile LONG lcShadow;          // +0x04 runtime switch: 1 = compare when no job runs
    volatile LONG lcReady;           // +0x08
    volatile LONG lcCalls;           // +0x0C gathers on the sim thread with a switch on (shape ok, not nested)
    volatile LONG lcInJob;           // +0x10 of them while a job ran
    volatile LONG lcTaken;           // +0x14 holds that lasted to the gather's return
    volatile LONG lcPass12;          // +0x18 gate-12 entries passed inside a hold
    volatile LONG lcPass0;           // +0x1C gate-0 hits passed inside a hold
    volatile LONG lcUs;              // +0x20 time in kept holds
    volatile LONG lcMaxUs;           // +0x24 longest kept hold
    volatile LONG lcDrop[8];         // +0x28 holds dropped: gate site, gate-0 miss, (unused), lock, managed / host call, lot
                                     //       table shape, other caller of the gate, the worker came for the area
    volatile LONG lcFb[8];           // +0x48 not held: state, nested, clone path broken, QL busy, dirty lot index, the worker is
                                     //       on the area; [6], [7] unused
    volatile LONG lcShadowRuns;      // +0x68
    volatile LONG lcShadowMismatch;  // +0x6C must stay 0
    volatile LONG lcShadowDrop;      // +0x70 dry holds that dropped (reasons in lcDrop)
    volatile LONG lcWkGathers;       // +0x74 gathers on other threads (the route worker's plans) with Mode on
    volatile LONG lcWkWaits;         // +0x78 of them that waited for the sim's hold on the same area
    volatile LONG lcWkGiveUp;        // +0x7C waits that reached the cap (must stay 0; turns Mode off)
    volatile LONG lcCands;           // +0x80 candidates gathered in kept holds
    volatile LONG lcWkWaitMaxUs;     // +0x84 longest such wait
};
extern "C" __declspec(dllexport) LandCheckRoCounters ts3native_landcheckro = {};

static const DWORD LC_GATHER = TS3A(0x00654790);
static const u8 kLcPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8};  // push ebp; mov ebp,esp; and esp,-8 (no relative operand)
static const DWORD LC_GIP = TS3A(0x0062FB60), LC_GIP_RET12 = TS3A(0x0062FB74);
enum : DWORD { LC_GIP_RET0_OFF = 0x2B };  // 0x62FB8B: the return of GetOrInsert in 0x62FB60
// 0x62FB60 without its two rel32 operands: +0x00..+0x0F (ends with the E8 of Contains), +0x14..+0x26 (test al; jne; xor;
// pop; ret 8; lea edx,[esp+8]; push; lea ecx,[esi+0x48]; the E8 of GetOrInsert), +0x2B pop esi; ret 8
static const u8 kLcGipA[] = {0x8B, 0x44, 0x24, 0x08, 0x56, 0x8B, 0xF1, 0x8B, 0x4C, 0x24, 0x08, 0x50, 0x51, 0x8B, 0xCE, 0xE8};
static const u8 kLcGipB[] = {0x84, 0xC0, 0x75, 0x06, 0x33, 0xC0, 0x5E, 0xC2, 0x08, 0x00, 0x8D, 0x54, 0x24, 0x08, 0x52, 0x8D, 0x4E, 0x48, 0xE8};
static const u8 kLcGipC[] = {0x5E, 0xC2, 0x08, 0x00};
enum { LC_FB_STATE = 1, LC_FB_NESTED, LC_FB_BROKEN, LC_FB_TRY, LC_FB_DIRTY, LC_FB_AREA };  // 2-4 = slotro.cpp's SL_FB_*
enum { LCD_CALLER = 6, LCD_WORKER = 7 };                                                    // 0-5 = SLD_*
static_assert((int)LC_FB_NESTED == (int)SL_FB_NESTED && (int)LC_FB_BROKEN == (int)SL_FB_BROKEN && (int)LC_FB_TRY == (int)SL_FB_TRY &&
                  (int)LCD_CALLER == (int)SLD_TABLE + 1,
              "the land check's reasons extend slotro.cpp's");

typedef DWORD(__fastcall* LcFn)(u8* area, void* edx);
static LcFn g_lcOrig;
static void* volatile g_lcSim;  // the area the sim thread gathers under a hold (also a dry one), else NULL
static void* volatile g_lcWk;   // the area another thread gathers, else NULL
static __declspec(thread) u8* t_lcArea;
static LONG g_lcWaitSpins = 4000, g_lcWaitSleeps = 250;  // the worker's wait: pause loops, then 1 ms sleeps, then the cap
static volatile LONG g_lcNoted, g_lcCapNoted;

// slotro.cpp SlEnd / LcOnDrop: the hold on the area is over, a waiting worker may gather it
static void LcRelease() {
    if (!t_lcArea) return;
    InterlockedCompareExchangePointer(&g_lcSim, NULL, t_lcArea);
    t_lcArea = NULL;
}
// slotro.cpp SlDrop (QL already released)
static void LcOnDrop(int why, bool dry) {
    LandCheckRoCounters& k = ts3native_landcheckro;
    if (why >= 0 && why < 8) InterlockedIncrement(&k.lcDrop[why]);
    if (dry) InterlockedIncrement(&k.lcShadowDrop);
    LcRelease();
}

// slotro.cpp SlGate with the land check's hold live: the two gate entries of LotTable_GetIfPresent pass, nothing else.
// slot = the gate's return slot, slot[-2] = ecx (routegate.cpp)
static int LcGateWhy(int site, DWORD* slot) {
    LandCheckRoCounters& k = ts3native_landcheckro;
    if (g_lcWk == (void*)t_lcArea) return LCD_WORKER;  // hand the area over as today: this gate then waits for the job
    const DWORD ret = VfRealRet(slot);
    if (site == 12) {  // LotTable_Contains(P)(lo, hi): reads only the table
        if (ret != LC_GIP_RET12) return LCD_CALLER;
        InterlockedIncrement(&k.lcPass12);
        return -1;
    }
    if (site == 0) {  // GetOrInsert(P+0x48)(key*): a hit writes nothing, a miss inserts
        if (ret != LC_GIP + LC_GIP_RET0_OFF) return LCD_CALLER;
        const DWORD* key = (const DWORD*)(uintptr_t)slot[1];
        const u8* e = NULL;
        const int r = LtFind((const u8*)(uintptr_t)slot[-2] - 0x48, key[0], key[1], &e);
        if (r != LT_OK) return SLD_TABLE;
        if (!e) return SLD_MISS;
        InterlockedIncrement(&k.lcPass0);
        return -1;
    }
    return SLD_GATE;
}

// The gather's own early returns (0x65479F..0x6547D8: no FindClosest object, a NULL in its +0xC / +0x14 / +0x18, try >= 3):
// the game answers 0 before any routing read, nothing to hold. Also no planner at +0x10 (the lot waypoint path needs it).
static bool LcShapeOk(const u8* area) {
    const u8* ctx = *(const u8* const*)(area + 0x40);
    if (!ctx) return false;
    return *(const DWORD*)(ctx + 0xC) && *(const DWORD*)(ctx + 0x10) && *(const DWORD*)(ctx + 0x14) && *(const DWORD*)(ctx + 0x18) &&
           *(const DWORD*)(area + 0x24) < 3;
}
// the two ways the gather names the world manager: FindClosest +0x14 (bounds, terrain) and planner +0xC (0x650030's lookup)
static bool LcIndexDirty(const u8* area) {
    const u8* ctx = *(const u8* const*)(area + 0x40);
    const u8* wm = *(const u8* const*)(ctx + 0x14);
    const u8* wmw = *(const u8* const*)(*(const u8* const*)(ctx + 0x10) + 0xC);
    return wm[0x1CC] || (wmw && wmw[0x1CC]);  // World_LotLookup would rebuild the lot index under its lock (wm+0x228)
}

// 0 = the hold is on; else the LC_FB_* reason (nothing is held or published)
static int LcBegin(u8* area, bool dry) {
    if (LcIndexDirty(area)) return LC_FB_DIRTY;
    // Dekker with LcOther: each side publishes its area, then reads the other's
    InterlockedExchangePointer(&g_lcSim, area);
    int why = g_lcWk == (void*)area ? LC_FB_AREA : SlBegin(dry, SLK_LAND);
    if (why) {
        InterlockedCompareExchangePointer(&g_lcSim, NULL, area);
        return why;
    }
    t_lcArea = area;
    return 0;
}
// the game's gather inside the hold; an unwind through it still releases QL and the area
static DWORD LcCall(u8* area, void* edx, bool* kept) {
    DWORD r = 0;
    __try {
        r = g_lcOrig(area, edx);
    } __finally {
        *kept = SlEnd();
    }
    return r;
}
static void LcFallback(int why) {
    if (why > 0 && why <= 8) InterlockedIncrement(&ts3native_landcheckro.lcFb[why - 1]);
}

// candidates of the area: 32 bytes each in [+0x30, +0x34); FNV-1a over them
static DWORD LcHash(const u8* area, DWORD* count) {
    const u8* b = *(const u8* const*)(area + 0x30);
    const u8* e = *(const u8* const*)(area + 0x34);
    *count = 0;
    if (!b || e < b || (size_t)(e - b) > 0x400000) return 0;
    *count = (DWORD)(e - b) / 32;
    DWORD h = 2166136261u;
    for (const u8* p = b; p < e; p++) h = (h ^ *p) * 16777619u;
    return h;
}

static DWORD LcShadow(u8* area, void* edx) {
    LandCheckRoCounters& k = ts3native_landcheckro;
    const DWORD r = g_lcOrig(area, edx);
    DWORD n1 = 0, n2 = 0;
    const DWORD h1 = LcHash(area, &n1), f1 = *(const DWORD*)(area + 0x2C);
    const int why = LcBegin(area, true);
    if (why) { LcFallback(why); return r; }
    bool kept = false;
    const DWORD r2 = LcCall(area, edx, &kept);
    InterlockedIncrement(&k.lcShadowRuns);
    const DWORD h2 = LcHash(area, &n2), f2 = *(const DWORD*)(area + 0x2C);
    if ((u8)r != (u8)r2 || n1 != n2 || h1 != h2 || f1 != f2) {
        InterlockedIncrement(&k.lcShadowMismatch);
        if (InterlockedIncrement(&g_lcNoted) <= 16)
            Log("land check RO: SHADOW MISMATCH area %08lX: game %u, %lu candidates, hash %08lX, flags %08lX; hold %u, %lu, %08lX, %08lX (kept %d)",
                (DWORD)(uintptr_t)area, (u8)r, n1, h1, f1, (u8)r2, n2, h2, f2, kept ? 1 : 0);
    }
    return r;
}

// a gather on a thread that is not the job owner (the route worker inside a plan): it waits while the sim's hold is on this
// very area, then gathers with the area published so the sim neither starts nor keeps a hold on it
static void LcWkWait(const u8* area) {
    LandCheckRoCounters& k = ts3native_landcheckro;
    InterlockedIncrement(&k.lcWkWaits);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    for (LONG i = 0; g_lcSim == (const void*)area; i++) {
        if (i < g_lcWaitSpins) { YieldProcessor(); continue; }
        if (i - g_lcWaitSpins >= g_lcWaitSleeps) {  // never expected: the sim's hold has no wait in it
            InterlockedIncrement(&k.lcWkGiveUp);
            InterlockedExchange(&k.lcMode, 0);
            if (InterlockedIncrement(&g_lcCapNoted) == 1)
                Log("land check RO: the worker's wait for area %08lX reached its cap: Mode set to 0", (DWORD)(uintptr_t)area);
            break;
        }
        Sleep(1);
    }
    Max((LONG*)&k.lcWkWaitMaxUs, UsSince(t0));
}
static DWORD LcOther(u8* area, void* edx) {
    InterlockedIncrement(&ts3native_landcheckro.lcWkGathers);
    void* const prev = InterlockedExchangePointer(&g_lcWk, area);
    if (g_lcSim == (void*)area) LcWkWait(area);
    DWORD r = 0;
    __try {
        r = g_lcOrig(area, edx);
    } __finally {
        InterlockedExchangePointer(&g_lcWk, prev);
    }
    return r;
}

static DWORD __fastcall LcHook(u8* area, void* edx) {
    LandCheckRoCounters& k = ts3native_landcheckro;
    if (!(k.lcMode | k.lcShadow)) return g_lcOrig(area, edx);
    const DWORD tid = GetCurrentThreadId();
    if (tid != g_async.owner) return k.lcMode ? LcOther(area, edx) : g_lcOrig(area, edx);
    if (t_slHold || !LcShapeOk(area)) return g_lcOrig(area, edx);
    InterlockedIncrement(&k.lcCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.lcShadow) return g_lcOrig(area, edx);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two gathers are compared (worker Dekker order)
        const DWORD r = g_async.state == A_RUNNING ? g_lcOrig(area, edx) : LcShadow(area, edx);
        RgOffSimDrop();
        return r;
    }
    if (!k.lcMode) return g_lcOrig(area, edx);
    InterlockedIncrement(&k.lcInJob);
    // a job runs: hold, unless this thread holds +0x918, serves the job's callback or has a gate frame open
    const RgThread& t = t_rg;
    int why = LC_FB_STATE;
    if (!g_inInvoke && ts3native_counters.rgOwnerTid != (LONG)tid && t.n == t.readers) why = LcBegin(area, false);
    if (why) { LcFallback(why); return g_lcOrig(area, edx); }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    bool kept = false;
    const DWORD r = LcCall(area, edx, &kept);
    if (kept) {
        const LONG us = UsSince(t0);
        DWORD n = 0;
        LcHash(area, &n);
        InterlockedIncrement(&k.lcTaken);
        InterlockedExchangeAdd(&k.lcUs, us);
        InterlockedExchangeAdd(&k.lcCands, (LONG)n);
        Max((LONG*)&k.lcMaxUs, us);
    }
    return r;
}

// one line a minute once installed (MaybeLogCounters)
static void LcLogCounters() {
    const LandCheckRoCounters& k = ts3native_landcheckro;
    if (!k.lcReady) return;
    Log("land check RO: mode %ld shadow %ld gathers %ld, in a job %ld: held to the end %ld (gate 12 passed %ld, gate 0 hits %ld, "
        "candidates %ld; %ld ms, max %ld us); dropped: gate %ld, miss %ld, lock %ld, managed %ld, table %ld, caller %ld, worker %ld; "
        "not held: state %ld, nested %ld, broken %ld, QL busy %ld, dirty lot index %ld, worker on the area %ld; worker gathers %ld "
        "waited %ld (max %ld us) gave up %ld; shadow runs %ld mismatch %ld dropped %ld",
        k.lcMode, k.lcShadow, k.lcCalls, k.lcInJob, k.lcTaken, k.lcPass12, k.lcPass0, k.lcCands, k.lcUs / 1000, k.lcMaxUs, k.lcDrop[SLD_GATE],
        k.lcDrop[SLD_MISS], k.lcDrop[SLD_LOCK], k.lcDrop[SLD_MANAGED], k.lcDrop[SLD_TABLE], k.lcDrop[LCD_CALLER], k.lcDrop[LCD_WORKER],
        k.lcFb[LC_FB_STATE - 1], k.lcFb[LC_FB_NESTED - 1], k.lcFb[LC_FB_BROKEN - 1], k.lcFb[LC_FB_TRY - 1], k.lcFb[LC_FB_DIRTY - 1],
        k.lcFb[LC_FB_AREA - 1], k.lcWkGathers, k.lcWkWaits, k.lcWkWaitMaxUs, k.lcWkGiveUp, k.lcShadowRuns, k.lcShadowMismatch, k.lcShadowDrop);
}

// [LandCheckRO] Enabled=1 (default 0): needs [RouteGate] Enforce=1 (gate stubs 0 and 12) with the async path and the QL
// writer stubs W4 / W5 / W6g. Entry detour: [WaypointSnap] Probe's stub on the call site 0x659787 calls through it.
static void InstallLandCheckRO(const wchar_t* ini) {
    LandCheckRoCounters& k = ts3native_landcheckro;
    if (!GetPrivateProfileIntW(L"LandCheckRO", L"Enabled", 0, ini)) { Log("land check RO off (Enabled=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP land check RO: no EA 1.69 address, docs/670 §24"); return; }
#endif
    if (!ts3native_counters.rgEnforce || !ts3native_counters.asyncReady || !g_rgTramp[0] || !g_rgTramp[12]) {
        Log("SKIP land check RO: need [RouteGate] Enforce=1 (gate stubs 0 and 12) with the async path");
        return;
    }
    if (!Matches((const void*)(uintptr_t)LC_GATHER, kLcPro, sizeof(kLcPro))) { Log("SKIP land check RO: 0x654790 prologue differs"); return; }
    if (!Matches((const void*)(uintptr_t)LC_GIP, kLcGipA, sizeof(kLcGipA)) || !Matches((const void*)(uintptr_t)(LC_GIP + 0x14), kLcGipB, sizeof(kLcGipB)) ||
        !Matches((const void*)(uintptr_t)(LC_GIP + LC_GIP_RET0_OFF), kLcGipC, sizeof(kLcGipC))) {
        Log("SKIP land check RO: 0x62FB60 bytes differ (the two gate returns are read from its layout)");
        return;
    }
    if (!InstallQlSubset()) { Log("SKIP land check RO: the QL writer stubs W4/W5/W6g are not all in place"); return; }
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_lcOrig = (LcFn)InstallDetour("ClosestWaypoints_GatherChunk", LC_GATHER, kLcPro, sizeof(kLcPro), (void*)LcHook, &g_lcOrig);
    if (!g_lcOrig) return;
    k.lcMode = Clamp(GetPrivateProfileIntW(L"LandCheckRO", L"Mode", 1, ini), 0, 1);
    k.lcShadow = Clamp(GetPrivateProfileIntW(L"LandCheckRO", L"Shadow", 0, ini), 0, 1);
    k.lcReady = 1;
    Log("OK   land check RO: 0x654790 under QL shared, QL writers W4/W5/W6g (Mode=%ld Shadow=%ld)", k.lcMode, k.lcShadow);
}
