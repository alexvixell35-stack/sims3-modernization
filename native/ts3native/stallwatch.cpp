// [StallWatch] (docs/67 §25 "Logging proposal" (a) + (d), docs/69 §4 idea 5; counters v202 ss*): a sim stall sampler that
// works for players. Included by ts3native.cpp after f5watch.cpp (uses crashreport.cpp CrAfterCall / CrFrame, castrim.cpp
// CtFrames, simloop.cpp SimLoopAdd, userdiag.cpp g_udPhase).
//
// A sim-loop callback (end of every Simulate iteration, sim thread) stamps the time. A watchdog thread checks once a second:
// when no iteration has finished for StallMs, it suspends the sim thread, copies its context and stack (no lock, heap or log
// while it is suspended), resumes it, and logs eip + up to 24 return addresses as module+rva (ts3native frames: rva against
// this build's .map). Only a stack that holds a return address inside MonoScriptHost_Simulate 0xD81840-0xD82110 counts as a
// stuck iteration: a world load or travel runs outside the Simulate loop (no stamp, no Simulate frame) and logs nothing.
// One line now, then every EveryS up to Lines per stall; `stall watch: ... finished after N s` when the iteration ends.
// ssOpenMs is the open iteration's age at the last check, so a reader of /native (stutter profiler, (d)) sees a stuck
// iteration as such instead of a gap. Logging only: the game's code is not changed (one rel32 of simloop.cpp's hook).
// shortcut: (b) of the proposal (route follower id from the frame when 0x8538B0 / 0x84B590 is on the stack) is not built:
// it needs those functions' frame layouts; the module+rva walk names the functions.

//
// docs/666 §5 (deep stacks; every key below defaults to the behaviour above): StackKB copies more than 8 KB of stack; KeepNoSim=1
// logs a sample with no Simulate frame instead of dropping it (tagged "no Simulate frame"); MainThread=1 adds a sample of the
// main (render) thread to every logged sim sample; Names=1 adds function names to TS3W frames (stallnames.h; the EA 1.69
// build through its address pairs, marked ~) and compiled-method names to [MonoJit] frames; Managed=N adds the script stack
// (interpreter frame records) of the sampled sim thread; Frames=N prints more return addresses. Any of them but StackKB and
// KeepNoSim switches the line to the deep format: unique return addresses in stack order with a repeat count (*n) and a
// mark (^) on those the frame-pointer chain holds, the innermost three quarters and the outermost quarter when there are
// more than Frames, further frames on "more frames" lines.
#include "stallnames.h"  // kSsSyms[] (tools/native-names.ts)

static volatile LONG g_ssTick;  // GetTickCount at the end of the last Simulate iteration, 0 = none yet
static volatile DWORD g_ssSimTid;
static DWORD g_ssStallMs = 5000, g_ssEveryMs = 10000, g_ssPollMs = 1000;  // StallMs/EveryMs (ms, optional) override StallS/EveryS for sub-second hitches
static LONG g_ssLines = 6;
static const DWORD SS_SIM_LO = TS3A(0x00D81840), SS_SIM_HI = TS3A(0x00D82110);  // MonoScriptHost_Simulate (simloop.cpp)

static void __cdecl SsIterate() {
    g_ssSimTid = GetCurrentThreadId();
    InterlockedExchange(&g_ssTick, (LONG)GetTickCount());
}

// eip + return-address scan of thread `tid`; *inSim = a return address inside Simulate. false = no context.
static DWORD g_ssStkDef[2048];
static DWORD* g_ssStk = g_ssStkDef;  // the watchdog thread only; [StallWatch] StackKB sizes it
static DWORD g_ssStkWords = 2048;
static bool SsCapture(DWORD tid, char* out, size_t cap, bool* inSim) {
    *inSim = false;
    HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT, FALSE, tid);
    if (!h) { sprintf_s(out, cap, "not opened (err %lu)", GetLastError()); return false; }
    DWORD* const stk = g_ssStk;
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
                if (ns > g_ssStkWords) ns = g_ssStkWords;
                memcpy(stk, (const void*)(uintptr_t)c.Esp, ns * 4);
            }
        }
        ResumeThread(h);
    }
    CloseHandle(h);
    if (!got) { strcpy_s(out, cap, "no context"); return false; }
    DWORD f[24];
    int nf = 0;
    for (DWORD i = 0; i < ns; i++) {
        if (!CrAfterCall(stk[i])) continue;
        if (stk[i] > SS_SIM_LO && stk[i] < SS_SIM_HI) *inSim = true;
        if (nf < 24) f[nf++] = stk[i];
    }
    char at[48], fr[24 * 44 + 8];
    CrFrame(c.Eip, at, sizeof(at)), CtFrames(f, nf, fr, sizeof(fr));
    sprintf_s(out, cap, "eip %s, stack scan (%d):%s", at, nf, fr);
    return true;
}

// ---- docs/666 §5: deep capture ----
static volatile LONG g_ssNoSimLines, g_ssMainLines;  // lines kept without a Simulate frame / main-thread samples
static bool (*g_ssCapture)(DWORD, char*, size_t, bool*) = SsCapture;  // SsCaptureDeep with a deep key
static LONG g_ssDeep, g_ssFrames = 24, g_ssKeepNoSim, g_ssMain, g_ssNames, g_ssManaged;
static DWORD g_ssExe, g_ssExeSize;  // the game image (Names)
enum { SS_HITS = 256, SS_MORE = 8 };
struct SsHit { DWORD a; int n, fp; };  // n = times on the stack, fp = the frame-pointer chain holds one of them
struct SsCap {
    DWORD eip, esp, ebp, teb, words;
    int hits, total, simAt, nMeth;  // unique / all return addresses, index of the first Simulate one among all (-1 none)
    DWORD meth[16];
    SsHit h[SS_HITS];
};
static char g_ssMore[SS_MORE][1400];  // lines after the sample's own (the watchdog thread only)
static int g_ssMoreN;

// the name row of an image offset (Steam 1.67 layout); NULL = no named function there
static const SsSym* SsSymOf(DWORD rva) {
    int lo = 0, hi = (int)(sizeof(kSsSyms) / sizeof(kSsSyms[0])) - 1;
    while (lo <= hi) {
        const int mid = (lo + hi) / 2;
        const SsSym& y = kSsSyms[mid];
        if (rva < y.lo) hi = mid - 1;
        else if (rva - y.lo >= y.len) lo = mid + 1;
        else return &y;
    }
    return NULL;
}
// name + offset of a game address; *approx = found through a neighbouring address pair (EA 1.69 build)
static const char* SsNameOf(DWORD a, DWORD* off, bool* approx) {
    *approx = false;
    if (a > SS_SIM_LO && a < SS_SIM_HI) { *off = a - SS_SIM_LO; return "MonoScriptHost_Simulate"; }
    if (!g_ssExe || a - g_ssExe >= g_ssExeSize) return NULL;
#ifdef TS3A_TABLE
    // this build's address -> the Steam one through the nearest pair at or below it (same function only, checked below)
    DWORD best = 0, bestSteam = 0;
    for (int i = 0; i < ts3addr_count; i++) {
        const DWORD ea = (DWORD)ts3addr_pairs[i][1] - 0x400000 + g_ssExe;
        if (ts3addr_pairs[i][1] && ea <= a && ea > best) best = ea, bestSteam = (DWORD)ts3addr_pairs[i][0];
    }
    if (!best || a - best >= 0x1000 || bestSteam < 0x400000) return NULL;
    const DWORD rva = bestSteam - 0x400000 + (a - best);
    const SsSym* const y = SsSymOf(rva);
    if (!y || bestSteam - 0x400000 < y->lo) return NULL;  // the pair sits in another function
    *approx = a != best;
#else
    const DWORD rva = a - g_ssExe;
    const SsSym* const y = SsSymOf(rva);
    if (!y) return NULL;
#endif
    *off = rva - y->lo;
    return y->name;
}
static bool SsJitAt(DWORD a) { return g_jitCode && a >= (DWORD)(uintptr_t)g_jitCode && a < (DWORD)(uintptr_t)g_jitCodeEnd; }
// "TS3W+0x25E0F0[PathPlanner_PlanHierarchical+0x20]", "jit+0x1234[Ns.Cls.Meth+0x1A]"; module+rva alone when there is no name
static void SsFrameText(DWORD a, char* out, size_t cap) {
    if (SsJitAt(a)) {
        const int w = sprintf_s(out, cap, "jit+0x%lX", a - (DWORD)(uintptr_t)g_jitCode);
        const int nMeth = g_jitMethN;
        for (int i = 0; g_ssNames && i < nMeth; i++) {
            const JitMeth& m = g_jitMeth[i];
            const DWORD m0 = (DWORD)(uintptr_t)m.native0;
            if (a < m0 || a - m0 >= (DWORD)m.bytes) continue;
            _snprintf_s(out + w, cap - w, _TRUNCATE, "[%.80s+0x%lX]", m.name, a - m0);
            break;
        }
        for (char* p = out; *p; p++) if (*p == ' ') *p = '_';  // frames are split at spaces
        return;
    }
    CrFrame(a, out, cap);
    if (!g_ssNames) return;
    DWORD off = 0;
    bool approx = false;
    const char* const nm = SsNameOf(a, &off, &approx);
    const size_t w = strlen(out);
    if (nm) _snprintf_s(out + w, cap - w, _TRUNCATE, "[%s%s+0x%lX]", approx ? "~" : "", nm, off);
}

// a return address? As CrAfterCall, with the last regions kept (a deep copy asks tens of thousands of times); a value in the
// [MonoJit] code region counts too (compiled code calls with E8 / FF /2 like the game's)
struct SsReg { DWORD lo, hi; bool code; };
static SsReg g_ssReg[8];
static unsigned g_ssRegI;
static bool SsIsRet(DWORD v) {
    const SsReg* r = NULL;
    for (const SsReg& e : g_ssReg)
        if (v >= e.lo && v < e.hi) { r = &e; break; }
    if (!r) {
        MEMORY_BASIC_INFORMATION mi;
        if (!VirtualQuery((const void*)(uintptr_t)v, &mi, sizeof(mi))) return false;
        SsReg& e = g_ssReg[g_ssRegI++ & 7];
        e.lo = (DWORD)(uintptr_t)mi.BaseAddress, e.hi = e.lo + (DWORD)mi.RegionSize;
        e.code = mi.State == MEM_COMMIT && (mi.Type == MEM_IMAGE || SsJitAt(v)) &&
                 (mi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
        r = &e;
    }
    if (!r->code || v - r->lo < 8) return false;
    const u8* const p = (const u8*)(uintptr_t)v;
    if (p[-5] == 0xE8) return true;
    for (int k : {2, 3, 4, 6, 7})
        if (p[-k] == 0xFF && ((p[-k + 1] >> 3) & 7) == 2) return true;
    return false;
}

// unique return addresses of a stack copy (stk[0] = [esp]) in stack order, innermost first. A value that points into the
// copied stack itself is skipped before isRet. The frame-pointer chain from ebp marks the slots it names (fp).
static void SsScan(const DWORD* stk, DWORD ns, DWORD esp, DWORD ebp, bool (*isRet)(DWORD), SsCap* c) {
    c->hits = c->total = 0, c->simAt = -1;
    DWORD fpIx[64];
    int nFp = 0;
    for (DWORD b = ebp; nFp < 64 && b >= esp && !(b & 3) && (b - esp) / 4 + 1 < ns;) {
        const DWORD i = (b - esp) / 4;
        fpIx[nFp++] = i + 1;
        if (stk[i] <= b) break;  // the chain must climb
        b = stk[i];
    }
    for (DWORD i = 0; i < ns; i++) {
        const DWORD v = stk[i];
        if (v < 0x10000 || v - esp < ns * 4 || !isRet(v)) continue;
        if (c->simAt < 0 && v > SS_SIM_LO && v < SS_SIM_HI) c->simAt = c->total;
        c->total++;
        int fp = 0;
        for (int f = 0; f < nFp; f++) if (fpIx[f] == i) fp = 1;
        int j = 0;
        while (j < c->hits && c->h[j].a != v) j++;
        if (j == c->hits) {
            if (c->hits == SS_HITS) continue;  // shortcut: past 256 unique addresses the outermost ones are only counted
            c->h[j].a = v, c->h[j].n = 0, c->h[j].fp = 0;
            c->hits++;
        }
        c->h[j].n++;
        c->h[j].fp |= fp;
    }
}

// a read of another thread's memory while it is suspended: no fault handler runs (it could need a lock that thread holds)
static bool SsRd(DWORD a, DWORD* v) {
    SIZE_T n = 0;
    return a >= 0x10000 && ReadProcessMemory(GetCurrentProcess(), (const void*)(uintptr_t)a, v, 4, &n) && n == 4;
}
// script frames of the thread whose TEB is `teb`, innermost first (MonoMethod pointers, 0 = a frame without one): the walk
// of autledger.cpp AsLgFrames (TLS slot [0x11F4160] -> context, +8 frame, frame[0] parent, frame[1] info, info +8 method)
static int SsManaged(DWORD teb, DWORD* m, int max) {
    DWORD idx = 0, ctx = 0, f = 0;
    if (!teb || max <= 0 || !SsRd((DWORD)(uintptr_t)g_lgTlsIdx, &idx)) return 0;
    if (idx < 64) {
        if (!SsRd(teb + 0xE10 + idx * 4, &ctx)) return 0;
    } else {
        DWORD ext = 0;
        if (idx >= 1088 || !SsRd(teb + 0xF94, &ext) || !SsRd(ext + (idx - 64) * 4, &ctx)) return 0;
    }
    if (!SsRd(ctx + 8, &f)) return 0;
    int got = 0;
    while (f && got < max) {
        DWORD next = 0, info = 0, meth = 0;
        if (!SsRd(f, &next) || !SsRd(f + 4, &info)) break;
        if (info) SsRd(info + 8, &meth);
        m[got++] = meth;
        f = next;
    }
    return got;
}

// suspends `tid`, copies its context, stack and (managed > 0) script frames into c and g_ssStk, resumes it
static bool SsGrab(DWORD tid, SsCap* c, int managed, char* err, size_t cap) {
    HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, tid);
    if (!h) { sprintf_s(err, cap, "not opened (err %lu)", GetLastError()); return false; }
    CONTEXT x = {};
    x.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_SEGMENTS;
    bool got = false;
    c->words = 0, c->nMeth = 0, c->teb = 0;
    if (SuspendThread(h) != (DWORD)-1) {
        if (GetThreadContext(h, &x)) {
            got = true;
            MEMORY_BASIC_INFORMATION mi;
            if (VirtualQuery((const void*)(uintptr_t)x.Esp, &mi, sizeof(mi)) && mi.State == MEM_COMMIT && !(mi.Protect & (PAGE_GUARD | PAGE_NOACCESS))) {
                const DWORD end = (DWORD)(uintptr_t)mi.BaseAddress + (DWORD)mi.RegionSize;
                DWORD ns = (end - x.Esp) / 4;
                if (ns > g_ssStkWords) ns = g_ssStkWords;
                memcpy(g_ssStk, (const void*)(uintptr_t)x.Esp, ns * 4);
                c->words = ns;
            }
            LDT_ENTRY ld = {};
            if (managed > 0 && GetThreadSelectorEntry(h, x.SegFs, &ld)) {
                c->teb = (DWORD)ld.BaseLow | (DWORD)ld.HighWord.Bytes.BaseMid << 16 | (DWORD)ld.HighWord.Bytes.BaseHi << 24;
                c->nMeth = SsManaged(c->teb, c->meth, managed < 16 ? managed : 16);
            }
        }
        ResumeThread(h);
    }
    CloseHandle(h);
    if (!got) { strcpy_s(err, cap, "no context"); return false; }
    c->eip = x.Eip, c->esp = x.Esp, c->ebp = x.Ebp;
    return true;
}

// the frames of c as text: the first part into out (after "eip X, stack scan (n):"), the rest as "more frames" lines in
// g_ssMore. With more unique addresses than Frames: the innermost 3/4 and the outermost 1/4, "...(+skipped)" between.
static void SsText(const char* who, DWORD tid, const SsCap& c, char* out, size_t cap) {
    const int want = c.hits < (int)g_ssFrames ? c.hits : (int)g_ssFrames, tail = c.hits > want ? want / 4 : 0, head = want - tail;
    char at[160], sim[32] = "no Simulate hit";
    SsFrameText(c.eip, at, sizeof(at));
    if (c.simAt >= 0) sprintf_s(sim, "Simulate hit #%d", c.simAt + 1);
    _snprintf_s(out, cap, _TRUNCATE, "deep %lu KB, %d hits, %d unique, %s: eip %s, stack scan (%d):", (c.words + 255) / 256, c.total, c.hits, sim, at, want);
    char* line = out;
    size_t room = cap, w = strlen(out);
    for (int i = 0; i < want; i++) {
        const SsHit& hit = c.h[i < head ? i : c.hits - want + i];
        char fr[200];
        SsFrameText(hit.a, fr, sizeof(fr) - 16);
        size_t n = strlen(fr);
        if (hit.fp) fr[n++] = '^', fr[n] = 0;
        if (hit.n > 1) n += (size_t)sprintf_s(fr + n, sizeof(fr) - n, "*%d", hit.n);
        if (w + n + 32 >= room) {  // the line is full: go on in a "more frames" line
            if (g_ssMoreN >= SS_MORE) return;
            line = g_ssMore[g_ssMoreN++], room = sizeof(g_ssMore[0]);
            w = (size_t)sprintf_s(line, room, "stall watch: %s thread %lu more frames (from #%d):", who, tid, i + 1);
        }
        if (i == head && tail) w += (size_t)sprintf_s(line + w, room - w, " ...(+%d)", c.hits - want);  // the gap before the outermost quarter
        w += (size_t)sprintf_s(line + w, room - w, " %s", fr);
    }
}
static void SsManagedLine(const char* who, DWORD tid, const SsCap& c) {
    if (c.nMeth <= 0 || g_ssMoreN >= SS_MORE) return;
    char* const d = g_ssMore[g_ssMoreN++];
    const int room = (int)sizeof(g_ssMore[0]);
    int n = sprintf_s(d, sizeof(g_ssMore[0]), "stall watch: %s thread %lu script stack (innermost first): ", who, tid);
    for (int i = 0; i < c.nMeth && n < room - 260; i++) {
        if (i) { memcpy(d + n, " < ", 4); n += 3; }
        n += FbName(d + n, room - n, c.meth[i]);
    }
}
static SsCap g_ssCap;
// the deep sample of `tid` as SsCapture's text; its further lines wait in g_ssMore
static bool SsCaptureDeep(DWORD tid, char* out, size_t cap, bool* inSim) {
    *inSim = false;
    g_ssMoreN = 0;
    memset(g_ssReg, 0, sizeof(g_ssReg));
    SsCap& c = g_ssCap;
    if (!SsGrab(tid, &c, (int)g_ssManaged, out, cap)) return false;
    SsScan(g_ssStk, c.words, c.esp, c.ebp, SsIsRet, &c);
    *inSim = c.simAt >= 0;
    SsText("sim", tid, c, out, cap > 1240 ? 1240 : cap);  // the caller's prefix shares Log's 1536
    SsManagedLine("sim", tid, c);
    return true;
}
// [StallWatch] MainThread=1: the main thread right after a logged sim sample (not the same instant: two suspensions)
static void SsMainSample(DWORD age) {
    const int more0 = g_ssMoreN;
    SsCap& c = g_ssCap;
    static char body[1240];
    if (!SsGrab(g_mainTid, &c, 0, body, sizeof(body))) { Log("stall watch: main thread %lu: %s", g_mainTid, body); return; }
    SsScan(g_ssStk, c.words, c.esp, c.ebp, SsIsRet, &c);
    SsText("main", g_mainTid, c, body, sizeof(body));
    Log("stall watch: main thread %lu while the sim thread stalls %lu ms: %s", g_mainTid, age, body);
    for (int i = more0; i < g_ssMoreN; i++) Log("%s", g_ssMore[i]);
    g_ssMoreN = more0;
    InterlockedIncrement(&g_ssMainLines);
}

// one check (watchdog thread, also the selftest): now = GetTickCount
struct SsState { bool in; LONG lines; DWORD next, logged; };
static SsState g_ssSt;
static void SsCheck(DWORD now, bool (*capture)(DWORD, char*, size_t, bool*)) {
    Ts3nCounters& k = ts3native_counters;
    SsState& s = g_ssSt;
    const DWORD last = (DWORD)g_ssTick;
    if (!last) return;
    const DWORD age = now - last;
    k.ssOpenMs = (LONG)age;
    if (age < g_ssStallMs) {
        if (s.in) Log("stall watch: sim iteration finished after a stall of at least %lu s (%ld lines)", s.logged / 1000, s.lines);
        s.in = false;
        return;
    }
    if (!s.in) s.lines = 0, s.next = now;
    if (s.lines >= g_ssLines || (LONG)(now - s.next) < 0) return;
    static char line[1400];
    bool inSim = false;
    const bool got = capture(g_ssSimTid, line, sizeof(line), &inSim);
    s.next = now + g_ssEveryMs;
    // not inside a Simulate iteration (load, travel, quit): no stall. KeepNoSim=1 (docs/666): logged with a tag, since an
    // 8 KB copy of a deep script or planner stack never reaches the Simulate frame
    if (got && !inSim && !g_ssKeepNoSim) return;
    if (got && !inSim) InterlockedIncrement(&g_ssNoSimLines);
    if (!s.in) s.in = true, k.ssStalls++;
    s.lines++, s.logged = age;
    k.ssLines++;
    Max(&k.ssMaxMs, (LONG)age);
    const char* ph = g_udPhase;
    Log("stall watch: sim thread %lu: no Simulate iteration finished for %lu ms (%ld/%ld)%s%s%s: %s", g_ssSimTid, age, s.lines, g_ssLines,
        got && !inSim ? ", no Simulate frame" : "", ph ? ", save phase " : "", ph ? ph : "", line);
    for (int i = 0; i < g_ssMoreN; i++) Log("%s", g_ssMore[i]);
    g_ssMoreN = 0;
    if (g_ssMain && got && g_mainTid && g_mainTid != g_ssSimTid && capture == SsCaptureDeep) SsMainSample(age);
}

static DWORD WINAPI SsMain(LPVOID) {
    for (;;) {
        Sleep(g_ssPollMs);
        SsCheck(GetTickCount(), g_ssCapture);
    }
}

// [StallWatch] Enabled=1 (default 0 until a correctness run; the design wants it in every run)
static bool ApplyStallWatch(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"StallWatch", L"Enabled", 0, ini)) return false;
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP stall watch ([StallWatch]): no EA 1.69 address, docs/232 §8"); return false; }
#endif
    const int stallS = (int)GetPrivateProfileIntW(L"StallWatch", L"StallS", 5, ini);
    const int everyS = (int)GetPrivateProfileIntW(L"StallWatch", L"EveryS", 10, ini);
    const int lines = (int)GetPrivateProfileIntW(L"StallWatch", L"Lines", 6, ini);
    g_ssStallMs = (DWORD)(stallS < 2 ? 2 : stallS > 120 ? 120 : stallS) * 1000;
    g_ssEveryMs = (DWORD)(everyS < 2 ? 2 : everyS > 300 ? 300 : everyS) * 1000;
    g_ssLines = lines < 1 ? 1 : lines > 30 ? 30 : lines;
    const int stallMs = (int)GetPrivateProfileIntW(L"StallWatch", L"StallMs", 0, ini);  // sub-second sampling (0 = use StallS)
    const int everyMs = (int)GetPrivateProfileIntW(L"StallWatch", L"EveryMs", 0, ini);
    if (stallMs > 0) {
        g_ssStallMs = (DWORD)(stallMs < 150 ? 150 : stallMs > 120000 ? 120000 : stallMs);
        g_ssPollMs = g_ssStallMs / 3 < 1000 ? (g_ssStallMs / 3 < 25 ? 25 : g_ssStallMs / 3) : 1000;
    }
    if (everyMs > 0) g_ssEveryMs = (DWORD)(everyMs < 50 ? 50 : everyMs > 300000 ? 300000 : everyMs);
    // docs/666 §5: deep stacks (every default gives the old line)
    const int stackKB = Clamp(GetPrivateProfileIntW(L"StallWatch", L"StackKB", 8, ini), 8, 1024);
    g_ssFrames = Clamp(GetPrivateProfileIntW(L"StallWatch", L"Frames", 24, ini), 8, 96);
    g_ssKeepNoSim = GetPrivateProfileIntW(L"StallWatch", L"KeepNoSim", 0, ini) ? 1 : 0;
    g_ssMain = GetPrivateProfileIntW(L"StallWatch", L"MainThread", 0, ini) ? 1 : 0;
    g_ssNames = GetPrivateProfileIntW(L"StallWatch", L"Names", 0, ini) ? 1 : 0;
    g_ssManaged = Clamp(GetPrivateProfileIntW(L"StallWatch", L"Managed", 0, ini), 0, 16);
    if (g_ssManaged && !Readable(g_lgTlsIdx, 4)) g_ssManaged = 0;
    g_ssDeep = g_ssFrames != 24 || g_ssMain || g_ssNames || g_ssManaged;
    if (stackKB > 8) {
        DWORD* const big = (DWORD*)VirtualAlloc(NULL, (SIZE_T)stackKB * 1024, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (big) g_ssStk = big, g_ssStkWords = (DWORD)stackKB * 256;
        else Log("note: stall watch StackKB=%d: no memory, the copy stays 8 KB", stackKB);
    }
    if (g_ssDeep) {
        g_ssCapture = SsCaptureDeep;
        g_ssExe = (DWORD)(uintptr_t)GetModuleHandleW(NULL), g_ssExeSize = (DWORD)TS3A_HOST_SIZE;
    }
    if (!SimLoopAdd(SsIterate, "stall watch: iteration stamp")) { Log("SKIP stall watch: no Simulate iteration hook"); return false; }
    HANDLE t = CreateThread(NULL, 64 * 1024, SsMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!t) { Log("FAIL stall watch: thread (err %lu)", GetLastError()); return false; }
    CloseHandle(t);
    ts3native_counters.ssReady = 1;
    Log("OK   stall watch (docs/67 §25): sim thread stack logged after %lu ms without a finished Simulate iteration, every %lu ms, poll %lu ms, up to %ld lines",
        g_ssStallMs, g_ssEveryMs, g_ssPollMs, g_ssLines);
    if (g_ssDeep || g_ssKeepNoSim || g_ssStkWords != 2048)
        Log("OK   stall watch deep stacks (docs/666): StackKB=%lu Frames=%ld KeepNoSim=%ld MainThread=%ld Names=%ld (%d names) Managed=%ld, %s line",
            g_ssStkWords / 256, g_ssFrames, g_ssKeepNoSim, g_ssMain, g_ssNames, (int)(sizeof(kSsSyms) / sizeof(kSsSyms[0])), g_ssManaged,
            g_ssDeep ? "deep" : "old");
    return true;
}
