// Design L1 and probe P1b (docs/84 §4.4, as built §4.6). Included by ts3native.cpp after steerro.cpp (uses its QL).
//
// L1, [LotFrontDoorRO] Enabled=1 (default 0; needs [RouteGate] Enforce=1 and the async path): LotRouting_FrontDoorPortal
// 0x631800 (thiscall(P)(lotLo, lotHi, type, Portal** out), ret 0x10, result EDX:EAX = the door portal's id; only caller
// IC_World_GetLotFrontDoorImpl 0x788340, which passes out = NULL) runs LotTable_Contains (gate 12) and GetOrInsert (gate 0),
// so while a job runs the sim thread waits for it there. The clone answers from the same data without +0x918 and without
// the portal's AddRef/Release (the original's net change of the count is 0): the Contains rules, our own copy of the
// LotTable_Find 0x627E00 walk, the door index by type, the portal's id. It calls no game function and writes nothing but its
// own stack, under QL shared (try only), so the sim never blocks on QL and never holds it across a lock, host or managed call
// (T-L2). The worker writers of the read set hold QL exclusive: W5 LotTable_Insert (table), W4 the LotGraph_Build gate stub
// (ValidateLotData 0x636330: portal vector), W6g 0x80CB40 / 0x806770 (front-door reset 0x636130); every other writer runs on
// the sim thread. Any doubt calls the original (today's wait). Runtime switches (counters v32): ltMode (+0x1134) and ltShadow
// (+0x1138): with no job running, the game's call, then the clone, compared (T-L4).
//
// P1b, [ProbeP1b] Enabled=1 (observe only): each gate wait keyed by its leaf's caller (P1) is keyed by the caller's caller as
// well, read at the fixed stack offset of the five known keys (prologues read 2026-10-17, docs/84 §4.6), and portal AddRef /
// Release through vtable 0xFED048 are counted by thread during jobs (T-L3, .rdata slot swap). It also installs the QL writer
// stubs W4, W5, W6g (uncontended without a reader) so T-L3 can tell worker calls inside them from the others.

static const DWORD LT_FD = 0x00631800;
static const u8 kFdPro[] = {0x8B, 0x44, 0x24, 0x08, 0x56, 0x8B, 0xF1};  // mov eax,[esp+8]; push esi; mov esi,ecx (no relative operand)
typedef u64(__fastcall* FdFn)(const u8* p, void* edx, DWORD lo, DWORD hi, DWORD type, const u8** out);
static FdFn g_fdOrig;
static DWORD g_ltAddRef = 0x0064E8E0, g_ltRelease = 0x0064E8F0;  // portal vtable 0xFED048 slots 0 / +4 (tests point them elsewhere)
static const DWORD LT_PORTAL_VT = 0x00FED048;
static bool g_ltCheckVt = true;  // selftest fakes turn the portal vtable check off

// --- T-L3: portal AddRef / Release by thread during jobs (counting thunks in the .rdata slots of vtable 0xFED048) ---
static void __cdecl PortalRefNote(int rel) {
    if (g_async.state != A_RUNNING) return;
    const DWORD tid = GetCurrentThreadId();
    const int cls = RgIsSim(tid) ? 0 : RgIsWorker(tid) ? (t_qlDepth ? 1 : 2) : 3;
    InterlockedIncrement(&ts3native_counters.portalRef[rel * 4 + cls]);
}
// thiscall, no stack arguments: ecx kept for the original; eax/edx are scratch for the caller as for any call
static __declspec(naked) void PortalAddRefThunk() {
    __asm {
        push ecx
        push 0
        call PortalRefNote
        add esp, 4
        pop ecx
        jmp dword ptr [g_ltAddRef]
    }
}
static __declspec(naked) void PortalReleaseThunk() {
    __asm {
        push ecx
        push 1
        call PortalRefNote
        add esp, 4
        pop ecx
        jmp dword ptr [g_ltRelease]
    }
}

// Design SH (slotro.cpp, docs/90 §4): interlocked forms of the portal count pair, put in vtable 0xFED048 (or behind P1b's
// counting thunks) because SH lets the sim AddRef/Release portals while the worker does the same outside QL (T-L3). Same
// rules as 0x64E8E0 / 0x64E8F0: AddRef returns the new count; Release at a count <= 1 calls the deleting slot +8 with 1 and
// returns 0 (no decrement first), else stores count - 1 and returns it.
static LONG __fastcall PortalAddRefX(u8* p, void*) { return InterlockedIncrement((volatile LONG*)(p + 0x70)); }
static LONG __fastcall PortalReleaseX(u8* p, void*) {
    volatile LONG* c = (volatile LONG*)(p + 0x70);
    for (;;) {
        const LONG v = *c;
        if (v <= 1) {
            typedef void(__fastcall * DelFn)(u8*, void*, DWORD);
            ((DelFn)(uintptr_t)(*(const DWORD* const*)p)[2])(p, NULL, 1);
            return 0;
        }
        if (InterlockedCompareExchange(c, v - 1, v) == v) return v - 1;
        InterlockedIncrement(&ts3native_counters.slRelRetry);
    }
}

enum { LT_OK, LT_FB_STATE, LT_FB_NESTED, LT_FB_NOWM, LT_FB_TRY, LT_FB_SHAPE, LT_FB_BROKEN };

// LotTable_Contains 0x628A90 (key 0, all-ones key, the active lot [[0x11ECBC4]+0xB0]: no), LotTable_Find 0x627E00 on P+0x48
// (bucket keyLo % [P+0x50] of [P+0x4C], chain +0x190, key at +0; no match = the end slot's value), the key again at
// node+0x10, then GetOrInsert 0x62EAB0's hit (entry = node+0x10). entry NULL = not in the table.
static int LtFind(const u8* p, DWORD lo, DWORD hi, const u8** entry) {
    *entry = NULL;
    if (!(lo | hi) || (lo & hi) == 0xFFFFFFFF) return LT_OK;
    const u8* wm = *g_srWmSlot;
    const u8* act = wm ? *(const u8* const*)(wm + 0xB0) : NULL;
    if (!act) return LT_FB_NOWM;  // the original reads [0 + 0x48]
    if (lo == *(const DWORD*)(act + 0x48) && hi == *(const DWORD*)(act + 0x4C)) return LT_OK;
    const DWORD cnt = *(const DWORD*)(p + 0x50);
    const DWORD* bk = *(const DWORD* const*)(p + 0x4C);
    if (!cnt || !bk) return LT_FB_SHAPE;  // the original divides by 0
    const u8* n = (const u8*)(uintptr_t)bk[lo % cnt];
    while (n && !(*(const DWORD*)n == lo && *(const DWORD*)(n + 4) == hi)) n = *(const u8* const*)(n + 0x190);
    if (!n || (DWORD)(uintptr_t)n == bk[cnt]) return LT_OK;
    if (*(const DWORD*)(n + 0x10) != lo || *(const DWORD*)(n + 0x14) != hi) return LT_OK;
    *entry = n + 0x10;
    return LT_OK;
}

// 0x631800 after the table hit (disassembly 0x63182D..0x6318A3): n = (end - begin) >> 2 (sar), indices compared unsigned;
// type 0: +0x1C when it is not -1 and < n, else +0x18; 1: +0x1C; 2: +0x18; other: 0. Result: the portal's id +8/+0xC.
static int LtFrontDoor(const u8* p, DWORD lo, DWORD hi, DWORD type, u64* res, const u8** portal) {
    *res = 0;
    *portal = NULL;
    const u8* e;
    const int why = LtFind(p, lo, hi, &e);
    if (why || !e) return why;
    const DWORD n = (DWORD)((LONG)(*(const DWORD*)(e + 0x48) - *(const DWORD*)(e + 0x44)) >> 2);
    DWORD i;
    if (type == 0) {
        i = *(const DWORD*)(e + 0x1C);
        if (i == 0xFFFFFFFF || i >= n) i = *(const DWORD*)(e + 0x18);
    } else if (type == 1) {
        i = *(const DWORD*)(e + 0x1C);
    } else if (type == 2) {
        i = *(const DWORD*)(e + 0x18);
    } else {
        return LT_OK;
    }
    if (i == 0xFFFFFFFF || i >= n) return LT_OK;
    const u8* pt = (const u8*)(uintptr_t)(*(const DWORD* const*)(e + 0x44))[i];
    if (!pt) return LT_OK;
    const DWORD* vt = *(const DWORD* const*)pt;
    // the skipped AddRef/Release must be the plain count pair (0x64E8E0 / 0x64E8F0), and Release must not delete (count >= 1)
    // P1b's T-L3 thunks or SH's interlocked pair may sit there
    if (g_ltCheckVt && !(vt && (vt[0] == g_ltAddRef || vt[0] == (DWORD)(uintptr_t)PortalAddRefThunk || vt[0] == (DWORD)(uintptr_t)PortalAddRefX) &&
                         (vt[1] == g_ltRelease || vt[1] == (DWORD)(uintptr_t)PortalReleaseThunk || vt[1] == (DWORD)(uintptr_t)PortalReleaseX)))
        return LT_FB_SHAPE;
    if (*(const LONG*)(pt + 0x70) < 1) return LT_FB_SHAPE;
    *res = (u64)*(const DWORD*)(pt + 0xC) << 32 | *(const DWORD*)(pt + 8);
    *portal = pt;
    return LT_OK;
}

// QL shared, try only, for the read; never nested (SRW locks are not recursive), never with a writer frame lost
static int LtClone(const u8* p, DWORD lo, DWORD hi, DWORD type, u64* res, const u8** portal) {
    *res = 0;
    *portal = NULL;
    if (t_qlDepth || t_qlShared) return LT_FB_NESTED;
    if (!TryAcquireSRWLockShared(&g_ql)) return LT_FB_TRY;
    t_qlShared = 1;
    const int why = g_steerBroken ? LT_FB_BROKEN : LtFrontDoor(p, lo, hi, type, res, portal);
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    return why;
}

static void LtFallback(int why) {
    if (why > 0 && why <= 6) InterlockedIncrement(&ts3native_counters.ltFb[why - 1]);
}

static volatile LONG g_ltNoted;
static u64 LtShadow(const u8* p, void* edx, DWORD lo, DWORD hi, DWORD type, const u8** out) {
    Ts3nCounters& k = ts3native_counters;
    const u8* before = out ? *out : NULL;
    const u64 r = g_fdOrig(p, edx, lo, hi, type, out);
    u64 r2;
    const u8* pt;
    const int why = LtClone(p, lo, hi, type, &r2, &pt);
    InterlockedIncrement(&k.ltShadowRuns);
    if (why) { LtFallback(why); return r; }
    if (r != r2 || (out && *out != (pt ? pt : before))) {
        InterlockedIncrement(&k.ltShadowMismatch);
        if (InterlockedIncrement(&g_ltNoted) <= 16)
            Log("lot front door RO: shadow mismatch lot %08lX:%08lX type %lu: game %08lX:%08lX, clone %08lX:%08lX", hi, lo, type,
                (DWORD)(r >> 32), (DWORD)r, (DWORD)(r2 >> 32), (DWORD)r2);
    }
    return r;
}

static u64 __fastcall FdHook(const u8* p, void* edx, DWORD lo, DWORD hi, DWORD type, const u8** out) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if ((!k.ltMode && !k.ltShadow) || tid != g_async.owner) return g_fdOrig(p, edx, lo, hi, type, out);
    InterlockedIncrement(&k.ltCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.ltShadow) return g_fdOrig(p, edx, lo, hi, type, out);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        const u64 r = g_async.state == A_RUNNING ? g_fdOrig(p, edx, lo, hi, type, out) : LtShadow(p, edx, lo, hi, type, out);
        RgOffSimDrop();
        return r;
    }
    if (!k.ltMode) return g_fdOrig(p, edx, lo, hi, type, out);
    // a job runs: read-only answer, unless this thread holds +0x918, serves the job's callback or has a gate frame open
    const RgThread& t = t_rg;
    int why = LT_FB_STATE;
    if (!g_inInvoke && k.rgOwnerTid != (LONG)tid && t.n == t.readers) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        u64 r;
        const u8* pt;
        why = LtClone(p, lo, hi, type, &r, &pt);
        if (!why) {
            if (out && pt) *out = pt;
            const LONG us = UsSince(t0);
            InterlockedIncrement(&k.ltTaken);
            InterlockedExchangeAdd(&k.ltUs, us);
            Max(&k.ltMaxUs, us);
            return r;
        }
    }
    LtFallback(why);
    return g_fdOrig(p, edx, lo, hi, type, out);
}

// --- P1b: second frames of the known gate-wait keys (docs/84 §4.4 "P1b") ---
// slot = the gate's return-address slot ([esp] at the leaf's entry, under the stub's pushad frame). For a gate-12 key the
// caller's own return slot sits at a fixed offset (its pushes, locals and the two Contains arguments, read in the
// prologues); 0x62EF30 aligns its stack (and esp, -16), so for the gate-0 key its ebp frame is used: ebp = slot[-6] (pushad),
// return slot [ebp+4]. Gate 10 (docs/87 §6): Route_PlanCommon keeps ebp, so for key 0x760BFF [ebp+4] is 0x76429D
// (Plan/Replan) or 0x764400 (PlanFromPoint/ReplanFromPosition); with P10 on, the IC's own address replaces it (4-way split,
// and also for G10-C, whose site-10 key is Route_PlanCommon's caller).
struct P1bSite { DWORD key; int site; int off; };
static const int kP1bRows = 6;
static const DWORD kP10Ic[4] = {0x00765160, 0x00765490, 0x00765240, 0x00765550};  // IC_Route_Plan, Replan, PlanFromPoint, ReplanFromPosition
static const P1bSite kP1b[kP1bRows] = {
    {0x0062F782, 12, 7},  // LotRouting_PortalsForObject 0x62F760: sub esp,8; push esi; push edi; 2 args
    {0x0062FB74, 12, 4},  // LotTable_GetIfPresent 0x62FB60: push esi; 2 args
    {0x00631814, 12, 4},  // LotRouting_FrontDoorPortal 0x631800: push esi; 2 args (with L1 on: our hook, 0xFFFFFF)
    {0x00631F40, 12, 9},  // LotRouting_FootprintsNearPoint 0x631F10: sub esp,0xC; push ebx/esi/edi; 2 args
    {0x0062EF7A, 0, 0},   // lot transform wrapper 0x62EF30 -> GetOrInsert (gate 0): push ebp; mov ebp,esp; and esp,-16
    {0x00760BFF, 10, 0},  // Route_PlanCommon 0x7606E0 -> goal step 0x75C2D0 (gate 10): push ebp; mov ebp,esp; and esp,-16
};

static void P1bNote(int site, DWORD key, const DWORD* slot, LONG us) {
    Ts3nCounters& k = ts3native_counters;
    int i = 0;
    while (i < kP1bRows && !(kP1b[i].key == key && kP1b[i].site == site)) i++;
    const DWORD ic = site == 10 && g_p10On ? t_p10 & 0xFF : 0;
    if (ic) i = kP1bRows - 1;  // P10: every site-10 wait by its IC (G10-C keys included)
    if (i == kP1bRows) return;
    DWORD ret = 0;
    if (ic) {
        ret = kP10Ic[ic - 1];
    } else if (kP1b[i].off) {
        ret = VfRealRet(slot + kP1b[i].off);
    } else {
        const DWORD s = (DWORD)(uintptr_t)slot, ebp = slot[-6];
        if (ebp > s && ebp - s < 0x10000 && !(ebp & 3)) ret = VfRealRet((const DWORD*)(uintptr_t)ebp + 1);
    }
    if (!ret) { InterlockedIncrement(&k.p1bNoFrame); return; }
    const LONG v = (LONG)((DWORD)(i + 1) << 24 | (ret < 0x01000000 ? ret : 0x00FFFFFF));
    // any thread may wait at a gate: claim an empty slot with a compare-exchange
    int j = 0;
    for (; j < 32; j++) {
        const LONG cur = k.p1bKey[j];
        if (cur == v) break;
        if (!cur && (InterlockedCompareExchange(&k.p1bKey[j], v, 0) == 0 || k.p1bKey[j] == v)) break;
    }
    if (j == 32) { InterlockedIncrement(&k.p1bOther); return; }
    InterlockedIncrement(&k.p1bN[j]);
    InterlockedExchangeAdd(&k.p1bUs[j], us);
}

static bool InstallPortalRefThunks(DWORD* vt) {
    if (!Readable(vt, 8) || vt[0] != g_ltAddRef || vt[1] != g_ltRelease) {
        Log("SKIP portal refcount probe (T-L3): vtable 0x%08X holds other slots", (DWORD)(uintptr_t)vt);
        return false;
    }
    const DWORD th[2] = {(DWORD)(uintptr_t)PortalAddRefThunk, (DWORD)(uintptr_t)PortalReleaseThunk};
    return Write(vt, th, sizeof(th));
}

// W4 (the LotGraph_Build gate stub), W5 and W6g of docs/83: the worker writers of L1's read set (steerro.cpp's stubs; with
// [SteerRO] on they are in place already)
static bool InstallQlSubset() {
    static const int kIdx[] = {3, 10, 11};
    if (!g_rgTramp[3]) return false;
    for (int i : kIdx)
        if (!g_qlTramp[i] && !Matches((const void*)(uintptr_t)kQlSites[i].addr, kQlSites[i].pro, kQlSites[i].len)) return false;
    for (int i : kIdx)
        if (!g_qlTramp[i] &&
            !(g_qlTramp[i] = InstallDetour(kQlSites[i].name, kQlSites[i].addr, kQlSites[i].pro, kQlSites[i].len, (void*)kQlStubs[i], &g_qlTramp[i])))
            return false;
    g_qlOn = 1;
    return true;
}

static void InstallLotFd(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const bool p1b = GetPrivateProfileIntW(L"ProbeP1b", L"Enabled", 0, ini) != 0;
    const bool l1 = GetPrivateProfileIntW(L"LotFrontDoorRO", L"Enabled", 0, ini) != 0;
    if (!p1b) Log("probe P1b off (Enabled=0)");
    if (!l1) Log("lot front door RO off (Enabled=0)");
    if (!p1b && !l1) return;
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP probe P1b / lot front door RO: need [RouteGate] Enforce=1 with the async path"); return; }
    const bool ql = InstallQlSubset();
    if (!ql) Log("FAIL QL writer stubs W4/W5/W6g (a prologue differs or the LotGraph_Build gate stub is missing)");
    if (p1b) {
        const bool refs = InstallPortalRefThunks((DWORD*)(uintptr_t)LT_PORTAL_VT);
        g_p1bOn = 1;
        k.p1bReady = 1;
        Log("OK   probe P1b: second-frame keys for gate waits at 0x62F782/0x62FB74/0x631814/0x631F40/0x62EF7A; portal refcount "
            "probe %s; worker split by QL stubs %s", refs ? "on" : "off", ql ? "on" : "off");
    }
    if (!l1) return;
    if (!ql) { Log("SKIP lot front door RO: the QL writer stubs are not all in place"); return; }
    g_fdOrig = (FdFn)InstallDetour("LotRouting_FrontDoorPortal", LT_FD, kFdPro, sizeof(kFdPro), (void*)FdHook, &g_fdOrig);
    if (!g_fdOrig) return;
    k.ltMode = Clamp(GetPrivateProfileIntW(L"LotFrontDoorRO", L"Mode", 1, ini), 0, 1);
    k.ltShadow = Clamp(GetPrivateProfileIntW(L"LotFrontDoorRO", L"Shadow", 0, ini), 0, 1);
    k.ltReady = 1;
    Log("OK   lot front door RO: 0x631800 + QL writers W4/W5/W6g (Mode=%ld Shadow=%ld)", k.ltMode, k.ltShadow);
}
