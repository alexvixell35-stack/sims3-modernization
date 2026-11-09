// docs/539 §21 (0.36.234): float-5 WRITER catch. CRASH-TRIAGE, code and release ini off. Two sections:
//
// [Float5Sticky] Enabled (+ PortalCheck, PortalGuard). float5-round4/5 all-off (0.36.233): [UafCheck] found 0x40A00000 at ONE
// fixed absolute address per session (0x3364623C three times in freed blocks of sizes 0x220 / 0x500 at world loads #3/#6/#8;
// 0x335A5FBC four times in another run), main thread, right after a save; when a live Portal's vector begin [portal+0x5C] sat
// there, Portal_Save crashed [verified log; the fixed address is the finding, the stale raw pointer behind it is inferred].
// So: the FIRST float-5 address any detector reports (UafVerify, CtF5 cells / record slots, modelbuilder cells, outfit
// strings, SlotGuard, realm, Float5Free deque, the sweep below, the portal check) gets a hardware write watch (watchpt.cpp
// WpArm, len 4, write, every thread, keep = never taken by another arm) for the rest of the session. A helper thread re-applies
// the registers to new threads every 10 s and at each world load / save start. Every write is logged with value, eip
// (module+offset, EA function guess), thread, registers, ebp chain and stack scan ("float5 sticky: write to ..."): all 0x40A00000
// writes (first 1000), other values first 200 then every 100th. At each save start and world load end the address is read
// ("float5 sticky: check at ..."). Later float-5 addresses are only logged ("float5 sticky: another float-5 address"); the
// watch never moves.
// PortalCheck=1: the PathPlanner portal save list 0x62AD40 calls Portal_Save 0x64E9A0 at 0x62AF81 (E8; thiscall(portal; stream),
// ret 4, al = 1 written; on al = 0 the caller clears its ok flag at 0x62AF8F, stops saving portals and the lot save fails the
// game's own way) [verified disassembly TS3W 1.67]. Before EA the portal's 0x38-byte element vector {+0x5C begin, +0x60 end,
// +0x64 capacity} (ctor 0x64EEF0 zeroes it; vtable 0xFED048) is checked: 0x40A00000 anywhere, misaligned / non-user-space
// pointers, end < begin, a size not a multiple of 0x38 or over 0x10000 elements, or an unreadable first / last element =
// "float5 portal: ..." (+ window line, + sticky note on &[portal+0x5C]). PortalGuard=1: Portal_Save is not called and the call
// answers al = 0 (a failed lot save instead of the AV at 0x64ED7B); 0 = log only.
//
// [Float5Watch] Enabled (+ SweepMs, BudgetUs, DrWatch, DrSlots, DrWindowSec, RotateSec): the window tools.
//   Last OK: every float-5 check that reads an object clean records it in a 16384-entry direct-mapped table (lossy): save
//   inventory and release checks (outfits, castrim.cpp), SlotComponent load / clone end (slotguard.cpp), realm add / remove
//   (nodes). One-shot teardown checks (modelbuilder, deque, EA Free) have no earlier read: their window line says "never".
//   A hit logs "float5 window: <site> obj X slot Y bad (value V); last OK N ms ago at <check> (save #S, world load #W ...)",
//   the save / world events since then, what the sim and main threads are doing now (suspended for a context + stack copy,
//   resumed before anything is formatted or logged) and our [CrashForensics] OurEvents ring entries in the window.
//   Sweep (SweepMs, default 250; BudgetUs per sweep, default 4000; worker thread): registered outfits (vtable unchanged;
//   [+0xBC] cells, part records [+0x98, +0x9C) slots +0x14 / +0x18 / +0x38 / +0x3C / +0x5C, their +0x14 / +0x38 arrays, the
//   string object's +0x2C / +0x30 / +0x34) and SlotComponents (vtable 0x1044320; pair pointers +0x0C..+0x2C) are read for
//   exactly 0x40A00000 that is not a real in-use EA block (F5fBad). Clean = last OK now ("sweep"); the first find per object is
//   a "float5 window: sweep: ..." line. Notes are not swept. Status line every 60 s: "float5 watch: sweeps ...".
//   DrWatch=1: from a travel save start, world quit or world load end, for DrWindowSec (30) every RotateSec (5) the DrSlots
//   newest candidates created up to 30 s before that event get a write watch (fn arm without keep: [UafCheck] DR arms and the
//   sticky watch can take them): SlotComponent +0x14 (registered at load / clone end = creation) and, per outfit first seen,
//   its [+0xBC] cell entry 87 / 63 / 3 (else the first) whose address is 0xC mod 16 (every bad slot so far was). Hits:
//   "float5 watch: WRITER DRk: ..." (value 0x40A00000) or "float5 watch: write DRk: ...". Watches stay until the next rotation.
// shortcut: outfits are registered at their first save inventory / release check, not at construction (no outfit ctor hook
// verified yet); a teardown check of modelbuilders, deques and frees has no earlier clean read.
static const DWORD kF5wF5 = 0x40A00000, kF5wSlotVt = 0x01044320, kF5wPortalVt = 0x00FED048;
struct F5wEnt { DWORD obj, vt, okTick, regTick; const char* okSite; LONG okSave, okWorld; DWORD badAt; u8 kind, pad[3]; };  // badAt: the slot last reported (one line per slot)
static const DWORD kF5wN = 16384;  // power of two
static F5wEnt* g_f5w;
static bool g_f5wOn;
static volatile LONG g_f5wLines, g_f5wFinds, g_f5wNew[3], g_f5wGoneN;
static DWORD g_f5wSimTid;
struct F5wEv { DWORD tick; const char* what; LONG n; };
static F5wEv g_f5wEv[64];
static volatile LONG g_f5wEvN;
static volatile DWORD g_f5wWinAt;  // tick | 1 of the last window event
static const char* volatile g_f5wWinWhat = "none";
static volatile LONG g_f5wWinN;
static HANDLE g_f5wWake;           // helper: a window event or a sticky address to arm
static volatile LONG g_f5wKick;    // save start / world load: the helper re-applies the sticky watch to every thread

static DWORD F5wIdx(DWORD obj) { return ((obj >> 4) ^ (obj >> 18)) & (kF5wN - 1); }
static DWORD F5wSimTid() { return g_mtSimTid ? g_mtSimTid : g_f5wSimTid; }
static const char* F5wWho(DWORD tid) { return tid == F5wSimTid() ? "sim" : tid == g_mainTid ? "main" : "other"; }

// ---- [Float5Sticky] ----
static bool g_f5sOn, g_f5sPortal, g_f5sPortalGuard;
static volatile LONG g_f5sAddr;          // the watched address, 0 = none yet
static const char* volatile g_f5sSite;   // the first detector that reported it
static volatile LONG g_f5sSlot = -1;     // DR slot, -1 = not armed
static volatile LONG g_f5sWrites, g_f5sF5, g_f5sLogs, g_f5sOther, g_f5sChecks, g_f5sOtherAddr;
static DWORD g_f5sAt;                    // tick of the first note

static void F5sNote(DWORD addr, const char* site) {  // any thread, under locks too (UafVerify): no waits here
    if (!g_f5sOn || !addr || (addr & 3)) return;
    if (InterlockedCompareExchange(&g_f5sAddr, (LONG)addr, 0) == 0) {
        g_f5sSite = site, g_f5sAt = GetTickCount();
        if (g_f5wWake) SetEvent(g_f5wWake);
        return;
    }
    if ((DWORD)g_f5sAddr != addr && InterlockedIncrement(&g_f5sOtherAddr) <= 20)
        Log("float5 sticky: another float-5 address 0x%08lX (%s), watch stays on 0x%08lX; save #%ld, world load #%ld", addr, site, (DWORD)g_f5sAddr,
            g_ctInvSave, g_udWorlds);
}
static void F5sHit(int s, const CONTEXT* c) {  // watchpt.cpp WpVeh, on the writing thread after the write (eip = next instruction)
    const DWORD a = g_wp[s].addr;
    DWORD v = 0;
    const bool ok = Readable((const void*)(uintptr_t)a, 4);
    if (ok) v = *(const DWORD*)(uintptr_t)a;
    const bool f5 = v == kF5wF5;
    const LONG n = InterlockedIncrement(&g_f5sWrites);
    if (f5) {
        if (InterlockedIncrement(&g_f5sF5) > 1000) return;
    } else {
        const LONG k = InterlockedIncrement(&g_f5sOther);
        if (k > 200 && k % 100) return;
    }
    DWORD eb[8] = {}, rs[10] = {};
    const int ne = CtEbpChain(c->Ebp, c->Esp, eb, 8), nr = CrStack(c->Esp, rs, 10);
    char at[48], fn[48] = "", chain[8 * 44 + 8], scan[10 * 44 + 8];
    CrFrame(c->Eip, at, sizeof(at)), CtFrames(eb, ne, chain, sizeof(chain)), CtFrames(rs, nr, scan, sizeof(scan));
    if (const DWORD f = CtFnStart(c->Eip)) sprintf_s(fn, ", EA function ~0x%08lX +0x%lX", f, c->Eip - f);
    const DWORD tid = GetCurrentThreadId();
    Log("float5 sticky: write to 0x%08lX value %s0x%08lX%s eip 0x%08lX (%s%s) thread %lu (%s); eax %08lX ecx %08lX edx %08lX ebx %08lX esi %08lX edi %08lX "
        "ebp %08lX esp %08lX; stack scan (%d):%s; ebp chain (%d):%s; save #%ld, world load #%ld; DR%d, writes %ld (float 5.0 %ld)", a, ok ? "" : "(unreadable) ", v,
        f5 ? " (float 5.0)" : "", c->Eip, at, fn, tid, F5wWho(tid), c->Eax, c->Ecx, c->Edx, c->Ebx, c->Esi, c->Edi, c->Ebp, c->Esp, nr, scan, ne, chain,
        g_ctInvSave, g_udWorlds, s, n, g_f5sF5);
}
static void F5sCheck(const char* what, LONG n) {  // F5wEvent: save start, world load end
    const DWORD a = (DWORD)g_f5sAddr;
    if (!g_f5sOn || !a) return;
    DWORD v = 0;
    bool ok = false;
    __try { v = *(const volatile DWORD*)(uintptr_t)a, ok = true; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    InterlockedIncrement(&g_f5sChecks);
    Log("float5 sticky: check at %s #%ld: 0x%08lX holds %s0x%08lX%s; DR%ld, writes %ld (float 5.0 %ld)", what, n, a, ok ? "" : "(unreadable) ", v,
        v == kF5wF5 ? " (float 5.0)" : "", (LONG)g_f5sSlot, g_f5sWrites, g_f5sF5);
}
static void F5sStep() {  // helper thread: arm once an address is known, keep the registers on every thread
    const DWORD a = (DWORD)g_f5sAddr;
    if (!a) return;
    const LONG s = g_f5sSlot;
    if (s >= 0 && g_wp[s].addr == a && g_wp[s].fn == F5sHit) { WpRefresh(); return; }
    const int k = WpArm((const void*)(uintptr_t)a, 4, true, F5sHit, true);
    InterlockedExchange(&g_f5sSlot, k);
    DWORD v = 0;
    __try { v = *(const volatile DWORD*)(uintptr_t)a; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    static LONG s_notSet;
    if (k >= 0 || s_notSet++ < 5)
        Log("float5 sticky: %s DR%d on 0x%08lX (first float-5 at %s %lu ms ago; holds 0x%08lX now), %ld thread(s) set; kept for the session; save #%ld, "
            "world load #%ld", k >= 0 ? (s >= 0 ? "re-armed" : "armed") : "NOT ARMED (no debug register free, retry in 10 s)", k, a, g_f5sSite,
            GetTickCount() - g_f5sAt, v, g_wpThreads, g_ctInvSave, g_udWorlds);
}

// Portal_Save call site 0x62AF81 (PortalCheck).
typedef DWORD(__fastcall* F5pSaveFn)(u8* portal, void* edx, void* stream);
static F5pSaveFn g_f5pSave = (F5pSaveFn)(uintptr_t)0x0064E9A0;  // selftest: a fake
static volatile LONG g_f5pChecks, g_f5pBad, g_f5pSkip, g_f5pLogs;
static const char* F5pWhat(const DWORD* v) {  // v = [portal+0x5C], +0x60, +0x64; NULL = fine
    const DWORD b = v[0], e = v[1], c = v[2];
    if (b == kF5wF5 || e == kF5wF5 || c == kF5wF5) return "0x40A00000 (float 5.0) in the vector";
    if (b == e) return NULL;  // empty: EA writes nothing
    if (((b | e | c) & 3) || b < 0x10000 || e >= 0xFFFE0000 || c >= 0xFFFE0000) return "not a heap pointer";
    if (e < b || (e - b) % 0x38 || (e - b) / 0x38 > 0x10000 || (c && c < e)) return "vector range not 0-65536 elements of 0x38";
    __try { (void)*(const volatile DWORD*)(uintptr_t)b, (void)*(const volatile DWORD*)(uintptr_t)(e - 4); } __except (EXCEPTION_EXECUTE_HANDLER) { return "vector memory unreadable"; }
    return NULL;
}
static DWORD __fastcall F5pSaveHook(u8* portal, void* edx, void* stream) {
    InterlockedIncrement(&g_f5pChecks);
    DWORD v[3] = {}, vt = 0;
    const char* what = NULL;
    __try { vt = *(const DWORD*)portal, memcpy(v, portal + 0x5C, 12); } __except (EXCEPTION_EXECUTE_HANDLER) { what = "portal unreadable"; }
    if (!what) what = F5pWhat(v);
    if (!what) return g_f5pSave(portal, edx, stream);
    InterlockedIncrement(&g_f5pBad);
    const DWORD p = (DWORD)(uintptr_t)portal;
    for (int k = 0; k < 3; k++) if (v[k] == kF5wF5) { F5wBad("portal save list: Portal_Save vector", p, p + 0x5C + 4 * k, kF5wF5); break; }
    if (g_f5sPortalGuard) InterlockedIncrement(&g_f5pSkip);
    if (InterlockedIncrement(&g_f5pLogs) <= 40) {
        DWORD f[10] = {};
        const int nf = CrStack((DWORD)(uintptr_t)_AddressOfReturnAddress(), f, 10);
        char fr[10 * 44 + 8], hb[0x70 * 3 + 4] = "unreadable";
        CtFrames(f, nf, fr, sizeof(fr));
        __try { Hex(portal, 0x70, hb, sizeof(hb)); } __except (EXCEPTION_EXECUTE_HANDLER) {}
        Log("float5 portal: portal 0x%08lX (vtable %08lX%s): %s; vector begin 0x%08lX end 0x%08lX capacity 0x%08lX at 0x%08lX; %s; thread %lu (%s); save #%ld, "
            "world load #%ld; bad %ld, skipped %ld, checks %ld; stack scan (%d):%s; portal +0..+0x70: %s", p, vt, vt == kF5wPortalVt ? ", Portal" : ", NOT the Portal vtable",
            what, v[0], v[1], v[2], p + 0x5C, g_f5sPortalGuard ? "Portal_Save not called, al = 0: this lot save fails (PortalGuard=1)" : "passed to EA (PortalGuard 0)",
            GetCurrentThreadId(), F5wWho(GetCurrentThreadId()), g_ctInvSave, g_udWorlds, g_f5pBad, g_f5pSkip, g_f5pChecks, nf, fr, hb);
    }
    return g_f5sPortalGuard ? 0 : g_f5pSave(portal, edx, stream);
}

// ---- [Float5Watch] ----
static LONG g_f5wSweepMs, g_f5wBudgetUs = 4000, g_f5wDrMax = 4;
static bool g_f5wDr;
static DWORD g_f5wDrWinMs = 30000, g_f5wRotMs = 5000;

static void F5wEvent(const char* what, LONG n) {  // userdiag.cpp (sim thread): always recorded, cheap
    const DWORD now = GetTickCount();
    const LONG k = InterlockedIncrement(&g_f5wEvN);
    F5wEv& e = g_f5wEv[(k - 1) & 63];
    e.tick = now, e.what = what, e.n = n;
    const bool travel = !strcmp(what, "travel save start"), load = !strcmp(what, "world load");
    if (travel || load || !strcmp(what, "world quit")) g_f5wWinWhat = what, g_f5wWinN = n, g_f5wWinAt = now | 1;
    if (travel || load || !strcmp(what, "save start")) g_f5wSimTid = GetCurrentThreadId();  // the save and the "world:" line run on the sim thread
    if (travel) UafTravelHold(true, what, n);
    else if (load) UafTravelHold(false, what, n);
    if (travel || load || !strcmp(what, "save start")) F5sCheck(what, n);
    if (load || !strcmp(what, "save start")) g_f5wKick = 1;
    if (g_f5wWake && (travel || load || !strcmp(what, "world quit") || !strcmp(what, "save start"))) SetEvent(g_f5wWake);
}

// candidates for the DR rotation: the newest registered records
struct F5wCand { DWORD slot, obj, vt, tick; u8 kind; LONG e, n; };
static F5wCand g_f5wCand[256];
static volatile LONG g_f5wCandN;
static void F5wCandAdd(DWORD slot, DWORD obj, DWORD vt, u8 kind, LONG e, LONG n) {
    if (!g_f5wDr || !slot) return;
    const LONG k = InterlockedIncrement(&g_f5wCandN);
    F5wCand& c = g_f5wCand[(k - 1) & 255];
    c.slot = slot, c.obj = obj, c.vt = vt, c.kind = kind, c.e = e, c.n = n, c.tick = GetTickCount();
}
// the outfit cell the rotation watches: entry 87 / 63 / 3 (the hit entries) else the first, whose address is 0xC mod 16; -1 none
static LONG F5wOutfitCell(DWORD b, LONG n) {
    for (const LONG i : {87L, 63L, 3L}) if (i < n && ((b + 4 * (DWORD)i) & 15) == 0xC) return i;
    for (LONG i = 0; i < n && i < 16; i++) if (((b + 4 * (DWORD)i) & 15) == 0xC) return i;
    return -1;
}

static bool F5wOk(DWORD obj, u8 kind, const char* site, bool cand) {
    if (!g_f5wOn || !obj || !g_f5w) return false;
    F5wEnt& e = g_f5w[F5wIdx(obj)];
    const DWORD now = GetTickCount() | 1;
    const bool fresh = e.obj != obj;
    if (fresh) {
        if (kind == F5K_NOTE && e.obj && e.kind != F5K_NOTE) return false;  // a note never evicts a swept entry
        DWORD vt = 0;
        if (kind != F5K_NOTE) __try { vt = *(const DWORD*)(uintptr_t)obj; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        e.obj = 0;
        _WriteBarrier();
        e.vt = vt, e.kind = kind, e.badAt = 0, e.regTick = now;
        InterlockedIncrement(&g_f5wNew[kind < 3 ? kind : 0]);
    }
    e.okTick = now, e.okSite = site, e.okSave = g_ctInvSave, e.okWorld = g_udWorlds, e.badAt = 0;
    _WriteBarrier();
    e.obj = obj;
    if (fresh && cand && kind == F5K_SLOT) F5wCandAdd(obj + 0x14, obj, e.vt, kind, -1, 0);
    return fresh;
}
static void F5wGone(DWORD obj) {
    if (!g_f5w || !obj) return;
    F5wEnt& e = g_f5w[F5wIdx(obj)];
    if (e.obj == obj) e.obj = 0, InterlockedIncrement(&g_f5wGoneN);
}
static void F5wOutfit(const u8* outfit, bool clean, const char* site, bool cand) {
    if (!g_f5wOn || !clean) return;
    const DWORD o = (DWORD)(uintptr_t)outfit;
    if (!F5wOk(o, F5K_OUTFIT, site) || !cand) return;
    DWORD b = 0, e = 0, vt = 0;
    __try { vt = *(const DWORD*)outfit, b = *(const DWORD*)(outfit + 0xBC), e = *(const DWORD*)(outfit + 0xC0); } __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    const LONG n = b && e > b && e - b <= 4096 * 4 ? (LONG)((e - b) / 4) : 0, i = F5wOutfitCell(b, n);
    if (i >= 0) F5wCandAdd(b + 4 * (DWORD)i, o, vt, F5K_OUTFIT, i, n);
}

// What another thread runs now: suspend, context + a copy of its stack (no lock, no heap, no log while suspended), resume.
static void F5wThreadNow(DWORD tid, char* out, size_t cap) {
    if (!tid) { strcpy_s(out, cap, "unknown (no sim thread id yet)"); return; }
    if (tid == GetCurrentThreadId()) { strcpy_s(out, cap, "this thread (the detector's line is its activity)"); return; }
    HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT, FALSE, tid);
    if (!h) { sprintf_s(out, cap, "not opened (err %lu)", GetLastError()); return; }
    static DWORD stk[512];  // shortcut: one capture at a time (s_busy); a second caller gets "busy"
    static volatile LONG s_busy;
    if (InterlockedExchange(&s_busy, 1)) { CloseHandle(h); strcpy_s(out, cap, "busy (another capture running)"); return; }
    CONTEXT c = {};
    c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
    DWORD ns = 0;
    bool got = false;
    if (SuspendThread(h) != (DWORD)-1) {
        if (GetThreadContext(h, &c)) {
            got = true;
            MEMORY_BASIC_INFORMATION mi;
            if (VirtualQuery((const void*)(uintptr_t)c.Esp, &mi, sizeof(mi)) && mi.State == MEM_COMMIT && !(mi.Protect & (PAGE_GUARD | PAGE_NOACCESS))) {
                const DWORD end = (DWORD)(uintptr_t)mi.BaseAddress + (DWORD)mi.RegionSize;
                ns = (end - c.Esp) / 4;
                if (ns > 512) ns = 512;
                memcpy(stk, (const void*)(uintptr_t)c.Esp, ns * 4);
            }
        }
        ResumeThread(h);
    }
    CloseHandle(h);
    DWORD f[8];
    int nf = 0;
    for (DWORD i = 0; i < ns && nf < 8; i++) if (CrAfterCall(stk[i])) f[nf++] = stk[i];
    InterlockedExchange(&s_busy, 0);
    if (!got) { strcpy_s(out, cap, "no context"); return; }
    char at[48], fr[8 * 44 + 8];
    CrFrame(c.Eip, at, sizeof(at)), CtFrames(f, nf, fr, sizeof(fr));
    sprintf_s(out, cap, "eip %s, stack scan (%d):%s", at, nf, fr);
}
static void F5wEvText(DWORD from, bool never, DWORD now, char* out, size_t cap) {  // save / world events since `from` (never: the last 6)
    int w = 0, k = 0;
    out[0] = 0;
    const LONG top = g_f5wEvN;
    for (LONG s = top; s > 0 && top - s < 64 && w < (int)cap - 64 && (!never || k < 6); s--) {
        const F5wEv& e = g_f5wEv[(s - 1) & 63];
        if (!e.what || (!never && now - e.tick > now - from)) break;
        w += sprintf_s(out + w, cap - w, "%s %s #%ld %lu ms ago", k++ ? "," : "", e.what, e.n, now - e.tick);
    }
    if (!k) strcpy_s(out, cap, " none");
}
static void F5wCeText(DWORD ms, char* out, size_t cap) {  // our-events ring entries in the last `ms`
    if (!g_ceOn) { strcpy_s(out, cap, "ring off ([CrashForensics] OurEvents=0)"); return; }
    LARGE_INTEGER q, f;
    QueryPerformanceCounter(&q), QueryPerformanceFrequency(&f);
    const LONGLONG lo = q.QuadPart - (LONGLONG)ms * f.QuadPart / 1000;
    LONG n[CE_NTAGS] = {}, th[3] = {}, tot = 0;
    const LONG top = g_ceSeq;
    LONG s = top;
    for (; s > 0 && top - s < (LONG)kCeRing; s--) {
        const CeEnt& e = g_ceRing[(DWORD)(s - 1) & (kCeRing - 1)];
        if (e.seq != s) continue;
        if (e.qpc < lo) break;
        if (e.tag < CE_NTAGS) n[e.tag]++;
        th[e.tid == F5wSimTid() ? 0 : e.tid == g_mainTid ? 1 : 2]++, tot++;
    }
    int w = sprintf_s(out, cap, "%ld (sim %ld, main %ld, other %ld)%s", tot, th[0], th[1], th[2], s > 0 && top - s >= (LONG)kCeRing ? " ring wrapped, more" : "");
    for (int t = 1; t < CE_NTAGS && w < (int)cap - 32; t++) if (n[t]) w += sprintf_s(out + w, cap - w, " %s=%ld", kCeTag[t], n[t]);
}
static void F5wOkText(const F5wEnt& e, DWORD obj, DWORD now, char* out, size_t cap) {
    if (e.obj != obj || !e.okTick) sprintf_s(out, cap, "last OK never (no clean check of this object recorded)");
    else sprintf_s(out, cap, "last OK %lu ms ago at %s (save #%ld, world load #%ld; registered %lu ms ago as %s)", now - e.okTick, e.okSite ? e.okSite : "?",
                   e.okSave, e.okWorld, now - e.regTick, e.kind == F5K_OUTFIT ? "outfit" : e.kind == F5K_SLOT ? "slot component" : "note");
}
static void F5wBad(const char* site, DWORD obj, DWORD slot, DWORD val) {
    if (val == kF5wF5 && slot) F5sNote(slot, site);  // [Float5Sticky]
    if (!g_f5wOn || !g_f5w) return;
    const LONG n = InterlockedIncrement(&g_f5wLines);
    F5wEnt e = {};
    if (obj) {
        F5wEnt& r = g_f5w[F5wIdx(obj)];
        e = r;
        if (r.obj == obj) r.badAt = slot;
    }
    if (n > 60 && n % 100) return;
    const DWORD now = GetTickCount(), tid = GetCurrentThreadId();
    const bool known = e.obj == obj && e.okTick;
    static char ok[200], ev[480], sim[560], mn[560], ce[480];  // shortcut: lines from two threads at once can mix these texts (log only)
    F5wOkText(e, obj, now, ok, sizeof(ok)), F5wEvText(known ? e.okTick : 0, !known, now, ev, sizeof(ev));
    F5wThreadNow(F5wSimTid(), sim, sizeof(sim)), F5wThreadNow(g_mainTid, mn, sizeof(mn));
    F5wCeText(known ? now - e.okTick : 2000, ce, sizeof(ce));
    Log("float5 window: %s obj 0x%08lX slot 0x%08lX bad (value 0x%08lX%s); %s; events in window:%s; thread %lu (%s); sim thread %lu now: %s; main thread %lu now: %s; "
        "our events in window%s: %s; window lines %ld", site, obj, slot, val, val == kF5wF5 ? ", float 5.0" : "", ok, ev, tid, F5wWho(tid), F5wSimTid(), sim,
        g_mainTid, mn, known ? "" : " (last 2 s)", ce, n);
}

// The sweep. Returns 1 = a float-5 slot (at, site), 0 = clean, -1 = gone (vtable changed or unreadable).
static bool F5wStray(DWORD v) { return v == kF5wF5 && F5fBad(v) != NULL; }  // a real in-use EA block at 0x40A00000 is no stray (seen 11-09)
static int F5wSweepOne(const F5wEnt& e, DWORD obj, DWORD* at, char* site, size_t cap, LONG* reads) {
    __try {
        const DWORD* o = (const DWORD*)(uintptr_t)obj;
        if (o[0] != e.vt) return -1;
        if (e.kind == F5K_SLOT) {
            for (int i = 0; i < 5; i++) {
                (*reads)++;
                if (F5wStray(o[(0xC + 8 * i) / 4])) return *at = obj + 0xC + 8 * i, sprintf_s(site, cap, "sweep: slot component +0x%X (%s)", 0xC + 8 * i, kSgPair[i]), 1;
            }
            return 0;
        }
        for (const DWORD off : {0xBCu, 0xC0u, 0x98u, 0x9Cu})
            if (F5wStray(o[off / 4])) return *at = obj + off, sprintf_s(site, cap, "sweep: outfit +0x%lX", off), 1;
        const DWORD* cb = (const DWORD*)(uintptr_t)o[0xBC / 4];
        const DWORD* ce = (const DWORD*)(uintptr_t)o[0xC0 / 4];
        if (cb && ce > cb && ce - cb <= 4096)
            for (LONG i = 0, n = (LONG)(ce - cb); i < n; i++) {
                (*reads)++;
                if (F5wStray(cb[i])) return *at = (DWORD)(uintptr_t)(cb + i), sprintf_s(site, cap, "sweep: outfit cell [+0xBC] entry %ld of %ld", i, n), 1;
            }
        const u8 *rb = (const u8*)(uintptr_t)o[0x98 / 4], *re = (const u8*)(uintptr_t)o[0x9C / 4];
        if (rb && re > rb && (re - rb) % 0x68 == 0 && (re - rb) / 0x68 <= 64)
            for (LONG ri = 0, rn = (LONG)((re - rb) / 0x68); ri < rn; ri++) {
                const DWORD* r = (const DWORD*)(rb + ri * 0x68);
                for (const DWORD off : {0x14u, 0x18u, 0x38u, 0x3Cu, 0x5Cu}) {
                    (*reads)++;
                    if (F5wStray(r[off / 4])) return *at = (DWORD)(uintptr_t)r + off, sprintf_s(site, cap, "sweep: outfit record %ld of %ld +0x%lX", ri, rn, off), 1;
                }
                for (const DWORD arr : {0x14u, 0x38u}) {
                    const DWORD *a = (const DWORD*)(uintptr_t)r[arr / 4], *ae = (const DWORD*)(uintptr_t)r[arr / 4 + 1];
                    if (!a || ae <= a || ae - a > 64) continue;
                    for (LONG k = 0; k < (LONG)(ae - a); k++) {
                        (*reads)++;
                        if (F5wStray(a[k])) return *at = (DWORD)(uintptr_t)(a + k), sprintf_s(site, cap, "sweep: outfit record %ld array +0x%lX slot %ld", ri, arr, k), 1;
                    }
                }
                if (const DWORD so = r[0x5C / 4])
                    for (const DWORD off : {0x2Cu, 0x30u, 0x34u}) {
                        (*reads)++;
                        if (F5wStray(*(const DWORD*)(uintptr_t)(so + off)))
                            return *at = so + off, sprintf_s(site, cap, "sweep: outfit record %ld string object [rec+0x5C] +0x%lX", ri, off), 1;
                    }
            }
        if (o[0] != e.vt) return -1;  // torn down while read
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
    return 0;
}
static DWORD g_f5wCur, g_f5wPassAt, g_f5wPassMs, g_f5wPassSweeps, g_f5wSweepsInPass;
static volatile LONG g_f5wSweeps, g_f5wPasses;
static LONGLONG g_f5wReads, g_f5wUsSum, g_f5wUsMax;
static void F5wSweep(DWORD now) {
    LARGE_INTEGER q0, q, f;
    QueryPerformanceCounter(&q0), QueryPerformanceFrequency(&f);
    const LONGLONG budget = (LONGLONG)g_f5wBudgetUs * f.QuadPart / 1000000;
    LONG reads = 0;
    DWORD i = g_f5wCur;
    if (!g_f5wPassAt) g_f5wPassAt = now | 1;
    for (DWORD v = 0; v < kF5wN; v++) {
        if ((v & 63) == 63) {
            QueryPerformanceCounter(&q);
            if (q.QuadPart - q0.QuadPart > budget) break;
        }
        F5wEnt& e = g_f5w[i];
        const DWORD obj = e.obj;
        if (obj && e.kind != F5K_NOTE) {
            DWORD at = 0;
            char site[112];
            const int r = F5wSweepOne(e, obj, &at, site, sizeof(site), &reads);
            if (e.obj == obj) {
                if (r < 0) e.obj = 0, InterlockedIncrement(&g_f5wGoneN);
                else if (!r) e.okTick = now | 1, e.okSite = "sweep", e.okSave = g_ctInvSave, e.okWorld = g_udWorlds, e.badAt = 0;
                else if (e.badAt != at) InterlockedIncrement(&g_f5wFinds), F5wBad(site, obj, at, kF5wF5), e.badAt = at;
            }
        }
        i = (i + 1) & (kF5wN - 1);
        if (!i) {  // a full pass
            g_f5wPassMs = now - (g_f5wPassAt & ~1u), g_f5wPassSweeps = g_f5wSweepsInPass + 1, g_f5wPassAt = now | 1, g_f5wSweepsInPass = 0;
            InterlockedIncrement(&g_f5wPasses);
        }
    }
    g_f5wCur = i, g_f5wSweepsInPass++;
    QueryPerformanceCounter(&q);
    const LONGLONG us = (q.QuadPart - q0.QuadPart) * 1000000 / f.QuadPart;
    g_f5wReads += reads, g_f5wUsSum += us;
    if (us > g_f5wUsMax) g_f5wUsMax = us;
    InterlockedIncrement(&g_f5wSweeps);
}

// DR rotation
struct F5wArm { DWORD slot, obj, vt, tick, created; u8 kind; LONG e, n; const char* ev; LONG evN; LONG writes; };
static F5wArm g_f5wArm[4];  // by DR slot
static volatile LONG g_f5wDrArms, g_f5wDrHits, g_f5wDrF5, g_f5wDrLogs, g_f5wDrF5Logs;
static DWORD g_f5wRotAt;
static void F5wArmDesc(const F5wArm& a, char* out, size_t cap) {
    if (a.kind == F5K_SLOT) sprintf_s(out, cap, "slot component obj 0x%08lX +0x14 (ContainmentSlots)", a.obj);
    else sprintf_s(out, cap, "outfit 0x%08lX cell [+0xBC] entry %ld of %ld", a.obj, a.e, a.n);
}
static void F5wDrHit(int s, const CONTEXT* c) {  // WpVeh, writing thread
    const F5wArm& a = g_f5wArm[s];
    const DWORD slot = g_wp[s].addr, now = GetTickCount();
    DWORD v = 0, vt = 0;
    const bool ok = Readable((const void*)(uintptr_t)slot, 4);
    if (ok) v = *(const DWORD*)(uintptr_t)slot;
    if (a.slot == slot && Readable((const void*)(uintptr_t)a.obj, 4)) vt = *(const DWORD*)(uintptr_t)a.obj;
    const bool f5 = v == kF5wF5, live = a.slot == slot && vt == a.vt;
    InterlockedIncrement(&g_f5wDrHits);
    const LONG wn = a.slot == slot ? InterlockedIncrement(&g_f5wArm[s].writes) : 0;
    if (f5) InterlockedIncrement(&g_f5wDrF5);
    if (f5 ? InterlockedIncrement(&g_f5wDrF5Logs) > 100 : InterlockedIncrement(&g_f5wDrLogs) > 200) return;
    DWORD eb[8] = {}, rs[10] = {};
    const int ne = CtEbpChain(c->Ebp, c->Esp, eb, 8), nr = CrStack(c->Esp, rs, 10);
    char at[48], fn[48] = "", chain[8 * 44 + 8], scan[10 * 44 + 8], d[96] = "record pending";
    CrFrame(c->Eip, at, sizeof(at)), CtFrames(eb, ne, chain, sizeof(chain)), CtFrames(rs, nr, scan, sizeof(scan));
    if (const DWORD f = CtFnStart(c->Eip)) sprintf_s(fn, ", EA function ~0x%08lX +0x%lX", f, c->Eip - f);
    if (a.slot == slot) F5wArmDesc(a, d, sizeof(d));
    const DWORD tid = GetCurrentThreadId();
    Log("float5 watch: %s DR%d: slot 0x%08lX (%s, %s) value now %s0x%08lX%s by eip 0x%08lX (%s%s), thread %lu (%s), write %ld of this watch, armed %lu ms ago "
        "(window %s #%ld), created %lu ms ago; save #%ld, world load #%ld; eax %08lX ecx %08lX edx %08lX ebx %08lX esi %08lX edi %08lX ebp %08lX esp %08lX; "
        "ebp chain (%d):%s; stack scan (%d):%s", f5 ? "WRITER" : "write", s, slot, d, live ? "object live" : "object gone or moved (a write after its teardown?)",
        ok ? "" : "(unreadable) ", v, f5 ? " (float 5.0)" : "", c->Eip, at, fn, tid, F5wWho(tid), wn, now - a.tick, a.ev ? a.ev : "?", a.evN, now - a.created,
        g_ctInvSave, g_udWorlds, c->Eax, c->Ecx, c->Edx, c->Ebx, c->Esi, c->Edi, c->Ebp, c->Esp, ne, chain, nr, scan);
}
static int F5wPick(DWORD win, DWORD now, F5wCand* out) {  // newest candidates from 30 s before the window event, half of each kind first
    int np = 0, per[3] = {};
    const int max = (int)g_f5wDrMax, half = (max + 1) / 2;
    for (int pass = 0; pass < 2 && np < max; pass++) {
        const LONG top = g_f5wCandN;
        for (LONG k = top; k > 0 && top - k < 256 && np < max; k--) {
            const F5wCand c = g_f5wCand[(k - 1) & 255];
            if (now - c.tick > now - win + 30000) break;
            if (!pass && per[c.kind] >= half) continue;
            bool dup = false;
            for (int j = 0; j < np; j++) dup |= out[j].slot == c.slot;
            DWORD vt = 0;
            __try { vt = *(const DWORD*)(uintptr_t)c.obj; } __except (EXCEPTION_EXECUTE_HANDLER) {}
            if (dup || vt != c.vt) continue;
            out[np++] = c, per[c.kind]++;
        }
    }
    return np;
}
static void F5wRotate(DWORD now) {
    const DWORD win = g_f5wWinAt;
    if (!g_f5wDr || !win || now - win > g_f5wDrWinMs || now - g_f5wRotAt < g_f5wRotMs) return;
    g_f5wRotAt = now;
    F5wCand p[4];
    const int np = F5wPick(win, now, p);
    bool same = np > 0;
    for (int j = 0; j < np && same; j++) {
        bool in = false;
        for (int s = 0; s < 4; s++) in |= g_wp[s].fn == F5wDrHit && g_wp[s].addr == p[j].slot;
        same = in;
    }
    if (same || !np) return;
    for (int s = 0; s < 4; s++) if (g_wp[s].fn == F5wDrHit) WpDisarm(s);
    char txt[4 * 120] = "";
    int w = 0;
    for (int j = 0; j < np; j++) {
        F5wArm a = {p[j].slot, p[j].obj, p[j].vt, now, p[j].tick, p[j].kind, p[j].e, p[j].n, g_f5wWinWhat, g_f5wWinN, 0};
        const int s = WpArm((const void*)(uintptr_t)p[j].slot, 4, true, F5wDrHit, false);  // shortcut: a write before the record below logs "record pending"
        char d[96];
        F5wArmDesc(a, d, sizeof(d));
        if (s >= 0) g_f5wArm[s] = a, InterlockedIncrement(&g_f5wDrArms);
        w += sprintf_s(txt + w, sizeof(txt) - w, "%s DR%d %s slot 0x%08lX (created %lu ms ago)", j ? ";" : "", s, d, p[j].slot, now - p[j].tick);
    }
    Log("float5 watch: DR rotation at %s #%ld +%lu ms: %d watch(es):%s", g_f5wWinWhat, g_f5wWinN, now - (win & ~1u), np, txt);
}

static void F5wStatus() {
    const LONG sw = g_f5wSweeps;
    LONG ent[3] = {};
    if (g_f5w) for (DWORD i = 0; i < kF5wN; i++) if (g_f5w[i].obj) ent[g_f5w[i].kind < 3 ? g_f5w[i].kind : 0]++;
    Log("float5 watch: sweeps %ld (every %ld ms, budget %ld us), entries %ld (outfits %ld, slot comps %ld, notes %ld; registered %ld / %ld / %ld, gone %ld), "
        "full passes %ld (last %lu ms over %lu sweeps), per sweep mean %ld us max %ld us, reads %I64d, bad finds %ld, window lines %ld; DR arms %ld, writes %ld "
        "(WRITER %ld); sticky 0x%08lX DR%ld writes %ld (float 5.0 %ld), checks %ld; portal checks %ld bad %ld; DR hits by ts3native %ld, on a dropped address %ld, nested %ld", sw, g_f5wSweepMs, g_f5wBudgetUs,
        ent[0] + ent[1] + ent[2], ent[F5K_OUTFIT], ent[F5K_SLOT], ent[F5K_NOTE], g_f5wNew[F5K_OUTFIT], g_f5wNew[F5K_SLOT], g_f5wNew[F5K_NOTE], g_f5wGoneN,
        g_f5wPasses, g_f5wPassMs, g_f5wPassSweeps, sw ? (LONG)(g_f5wUsSum / sw) : 0L, (LONG)g_f5wUsMax, g_f5wReads, g_f5wFinds, g_f5wLines, g_f5wDrArms,
        g_f5wDrHits, g_f5wDrF5, (DWORD)g_f5sAddr, (LONG)g_f5sSlot, g_f5sWrites, g_f5sF5, g_f5sChecks, g_f5pChecks, g_f5pBad, g_wpOwn, g_wpStale, g_wpNest);
}
static DWORD WINAPI F5wMain(LPVOID) {
    for (DWORD status = GetTickCount(), refresh = status, tried = 0;;) {
        WaitForSingleObject(g_f5wWake, g_f5wSweepMs > 0 ? (DWORD)g_f5wSweepMs : 1000);
        const DWORD now = GetTickCount();
        if (g_f5wOn && g_f5wSweepMs > 0) F5wSweep(now);
        if (g_f5wOn) F5wRotate(now);
        if (g_f5sOn && g_f5sAddr) {  // arm at once, retry an unarmed watch every 10 s, refresh threads every 10 s and at save start / world load
            const LONG k = g_f5sSlot;
            const bool live = k >= 0 && g_wp[k].fn == F5sHit && g_wp[k].addr == (DWORD)g_f5sAddr;
            if ((!live && (!tried || now - tried >= 10000)) || InterlockedExchange(&g_f5wKick, 0) || now - refresh >= 10000)
                F5sStep(), refresh = now, tried = now | 1;
        } else if (g_f5wDr && now - refresh >= 10000) {
            bool any = false;
            for (int s = 0; s < 4; s++) any |= g_wp[s].fn == F5wDrHit;
            if (any) WpRefresh();
            refresh = now;
        }
        if (now - status >= 60000) F5wStatus(), status = now;
    }
}

static bool ApplyFloat5Watch(const wchar_t* ini) {
    g_f5sOn = GetPrivateProfileIntW(L"Float5Sticky", L"Enabled", 0, ini) > 0;
    g_f5wOn = GetPrivateProfileIntW(L"Float5Watch", L"Enabled", 0, ini) > 0;
    if (!g_f5sOn && !g_f5wOn) return false;
    g_f5sPortal = g_f5sOn && GetPrivateProfileIntW(L"Float5Sticky", L"PortalCheck", 0, ini) > 0;
    g_f5sPortalGuard = g_f5sPortal && GetPrivateProfileIntW(L"Float5Sticky", L"PortalGuard", 0, ini) > 0;
    g_f5wSweepMs = Clamp(GetPrivateProfileIntW(L"Float5Watch", L"SweepMs", 250, ini), 0, 10000);
    g_f5wBudgetUs = Clamp(GetPrivateProfileIntW(L"Float5Watch", L"BudgetUs", 4000, ini), 100, 100000);
    g_f5wDr = g_f5wOn && GetPrivateProfileIntW(L"Float5Watch", L"DrWatch", 0, ini) > 0;
    g_f5wDrMax = Clamp(GetPrivateProfileIntW(L"Float5Watch", L"DrSlots", 4, ini), 1, 4);
    g_f5wDrWinMs = (DWORD)Clamp(GetPrivateProfileIntW(L"Float5Watch", L"DrWindowSec", 30, ini), 5, 600) * 1000;
    g_f5wRotMs = (DWORD)Clamp(GetPrivateProfileIntW(L"Float5Watch", L"RotateSec", 5, ini), 1, 60) * 1000;
    if (g_f5wOn && !g_f5w) g_f5w = (F5wEnt*)VirtualAlloc(NULL, kF5wN * sizeof(F5wEnt), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (g_f5wOn && !g_f5w) { Log("FAIL float5 watch: table (err %lu)", GetLastError()), g_f5wOn = false; }
    if (g_f5sOn || g_f5wDr) WpInstall();
    if (g_f5sPortal) {
        static const CallSite kPortal[] = {{0x0062AF81, 0x0064E9A0, (void*)F5pSaveHook}};
        if (!PatchCalls("float5 portal check (Portal_Save call in the portal save list)", kPortal, 1)) g_f5sPortal = g_f5sPortalGuard = false;
    }
    g_f5wWake = CreateEventW(NULL, FALSE, FALSE, NULL);
    HANDLE h = g_f5wWake ? CreateThread(NULL, 64 << 10, F5wMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL) : NULL;
    if (h) CloseHandle(h);
    Log("float5 sticky %s (CRASH-TRIAGE, docs/539 §21): the first 0x40A00000 address any detector reports gets a DR write watch on every thread for the session "
        "(\"float5 sticky: write to\"), checked at each save start and world load end; portal check %s%s",
        g_f5sOn ? (g_wpVeh ? "on" : "on, NO handler") : "off", g_f5sPortal ? "on (Portal_Save call 0x62AF81)" : "off",
        g_f5sPortal ? (g_f5sPortalGuard ? ", PortalGuard=1: a bad portal is not saved (al = 0, the lot save fails)" : ", log only") : "");
    Log("float5 watch %s (CRASH-TRIAGE, docs/539 §21): last-OK table %lu entries; sweep every %ld ms, budget %ld us (0 = no sweep); DR watch %s (%ld slot(s), "
        "window %lu s, rotate %lu s); helper thread %s", g_f5wOn ? "on" : "off", g_f5wOn ? kF5wN : 0, g_f5wSweepMs, g_f5wBudgetUs, g_f5wDr ? "on" : "off",
        g_f5wDrMax, g_f5wDrWinMs / 1000, g_f5wRotMs / 1000, h ? "started" : "NOT started");
    return true;
}
