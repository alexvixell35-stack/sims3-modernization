// [GcGrowLog] (counters v161 gg*, docs/232 Run 8l): one "gcgrow:" line per Boehm heap expansion. Log only, default OFF.
// Included by ts3native.cpp after memexp.cpp (g_mx) and savewrite.cpp (PatchCalls).
//
// Run 8k: the GC heap grew from 352 MB to ~2.3 GB within 10 s at the main menu (EA 1.69, GC_large_free_bytes ~2.1 GB of
// it, no single allocation >= 64 MB), and the next image decode crashed on a null buffer. Every growth after GC_init goes
// through the two calls of GC_expand_hp_inner 0xE49C80 in GC_collect_or_expand 0xE4A120 [verified: the only E8 callers
// besides GC_init on both builds]: 0xE4A20C (the step, max(heap / 5 + needed + slop, GrowthStep)) and 0xE4A219 (retry
// with exactly the needed blocks after the step failed); esi holds the needed blocks at both. Both calls go to GwThunk.
// Line: site, blocks asked, needed blocks, heap before / after, GC_gc_no, bytes allocated since the last GC, large free
// bytes, thread, the return address and the first 3 TS3W code addresses above it on the stack (heuristic: stale values
// possible), and the thread's script frames (interpreter, FbStackText). The first 50 expansions, then every one asking
// >= 64 MB, 500 lines at most. Growth not through these calls (between two of ours) is counted as gwUnseenKB.
// ts3native_counters.gwMode (+0x5AE8, --ab gcgrowlog) 0 stops the lines; the counters keep counting.
// Round 2 (Run 8l launch 1: a ~3.8 MB string of Simulator_LoadXmlAsString refused with 331 MB of large blocks free):
// - "gcgrow why:" per expansion, the inputs of GC_allochblk_nth 0xE49320's refusal of a bigger free block [verified
//   disassembly, Steam]: taken only when !GC_use_entire_heap [0x11F2D98] && !GC_dont_gc && heap - large free >=
//   GC_requested_heapsize [0x12225A8] && GC_should_collect 0xE49950 (adj 0xE49900 >= min 0xE498B0 || heap >=
//   GC_collect_at_heapsize [0x1194818]); then it refuses when GC_large_allocd_bytes [0x12225B8] + free bytes of lists n..60
//   [0x11F2E98] <= GC_max_large_allocd_bytes [0x12225BC], or GC_finalizer_bytes_freed [0x12225D8] (words) * 4 > heap / 16.
//   Black-list inputs: GC_black_list_spacing [0x119482C] (BL_LIMIT: a request above it is "punted", taken despite black-listed
//   pages) and GC_large_alloc_warn_suppressed [0x11F30C4] / interval [0x11949B8]. The three functions only read globals.
// - "gcgrow script:" 16 script frames; the XML key of the thread's running Simulator_LoadXmlAsString 0x76D290 (detour,
//   "gcgrow xml:" for each result >= 1 MB, 200 lines at most).
// - the last 20 expansions stay formatted and are written when the heap first crosses 600 MB, logged before or not.
// Run 8m: EA's LoadMetaAutonomy XML string (969 blocks, ~3.8 MB) is refused on black-listed pages because it is below
// GC_black_list_spacing (heap / stack-black-listed blocks, clamped 12 KB..8 MB in GC_promote_black_lists 0xE4A7C0; ~4.8 MB with
// our 352 MB heap), and GC_collect_or_expand expands instead of collecting (nothing allocated since the last GC), so the black
// lists never refresh: 57 x 32 MB until the address space ran out. [GC] BlackListCapKB (counters v162 gc*, default 0 = off)
// lowers the 8 MB clamp (0xE4A856 / 0xE4A85D), so a request above the cap takes a free block despite black-listed pages
// (Boehm's own punt in GC_allochblk_nth at 0xE4950F, as with the stock 40 MB heap). Counters for it and for the expand streak:
// - the punt's warn call 0xE49540 (`call [GC_current_warn_proc]`) goes to GcBlackOk, which counts every punt
//   (GC_large_alloc_warn_interval set to 1) and forwards every old-interval-th call, as before;
// - gcExpandStreak: expansions with no collection in between (same GC_gc_no); the two expand calls go to GwThunk
//   whenever the cap is on, count only (gwMode 0) unless [GcGrowLog] Enabled=1.

typedef int(__cdecl* GwExpandFn)(DWORD blocks);
typedef DWORD(__cdecl* GwFn0)();
typedef DWORD(__cdecl* GwXmlFn)(DWORD lo, DWORD hi, DWORD group, DWORD type);
static const DWORD GW_EXPAND = 0x00E49C80;
static const DWORD GW_SITE[2] = {0x00E4A20C, 0x00E4A219};
static const DWORD GW_TEXT_LO = 0x00401000, GW_TEXT_HI = 0x00F95000;
static const DWORD GW_XML = 0x0076D290;  // IC_Simulator_LoadXmlAsString(ulong instance, uint group, uint type) -> string
static const DWORD* const GW_LARGE_FREE = (const DWORD*)(uintptr_t)0x012225B4;  // GC_large_free_bytes
struct GwBoehm {  // GC_allochblk_nth inputs; the selftest points them at fakes
    const DWORD *useEntire, *dontGc, *requested, *collectAt, *largeAllocd, *maxLargeAllocd, *freeBytes, *finFreedW;
    DWORD* blSpacing;  // written by the streak guard (GwExpand)
    const DWORD *warnSupp, *warnIval;
    GwFn0 shouldCollect, adj, min;
};
static GwBoehm g_gwb = {(const DWORD*)(uintptr_t)0x011F2D98, (const DWORD*)(uintptr_t)0x011F30A8,
                        (const DWORD*)(uintptr_t)0x012225A8, (const DWORD*)(uintptr_t)0x01194818,
                        (const DWORD*)(uintptr_t)0x012225B8, (const DWORD*)(uintptr_t)0x012225BC,
                        (const DWORD*)(uintptr_t)0x011F2E98, (const DWORD*)(uintptr_t)0x012225D8,
                        (DWORD*)(uintptr_t)0x0119482C, (const DWORD*)(uintptr_t)0x011F30C4,
                        (const DWORD*)(uintptr_t)0x011949B8, (GwFn0)(uintptr_t)0x00E49950,
                        (GwFn0)(uintptr_t)0x00E49900, (GwFn0)(uintptr_t)0x00E498B0};
// push ecx; mov ecx,[esp+14h]; mov edx,[esp+10h]
static const u8 kGwXmlPro[9] = {0x51, 0x8B, 0x4C, 0x24, 0x14, 0x8B, 0x54, 0x24, 0x10};
static GwExpandFn g_gwExpand = (GwExpandFn)(uintptr_t)GW_EXPAND;
static GwXmlFn g_gwXmlOrig;
static const DWORD* g_gwLargeFree = GW_LARGE_FREE;
static DWORD g_gwLastHeap;  // heap bytes after our previous call, 0 before the first
static volatile DWORD g_gwXmlTid, g_gwXmlKey[2], g_gwXmlGT[2];  // thread inside LoadXmlAsString, its key (instance hi, lo; group, type)
static volatile LONG g_gwXmlLines;
struct GwLines { char head[512], why[768], script[1300]; };
static GwLines g_gwRing[20];  // the last 20 expansions, formatted
static int g_gwRingN;
static bool g_gwCrossed;
static DWORD g_gwStreakGc = 0xFFFFFFFF;  // GC_gc_no of the current expand streak
static DWORD g_gcCapBytes;               // [GC] BlackListCapKB in bytes (0 = off)
static const LONG GW_STREAK_PUNT = 3;    // 232 of 233 runs never reach streak 2 (2026-11-03..07 logs)
// Runtime switch (native_set gcstreakat, repro gc-streak-guard): the streak at which the spacing drops. Another value = guard off
// with a stop at that streak ("gc expand streak test stop:"); 0 = no guard and no stop, the address space runs out (docs/67 §35).
extern "C" __declspec(dllexport) volatile LONG ts3native_gc_streak_at = GW_STREAK_PUNT;

static DWORD GwRead(const DWORD* p) {
    __try { return *p; } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
static DWORD GwCall(GwFn0 f) {
    __try { return f(); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0xFFFFFFFF; }
}
// GC_allochblk's first free-list index for n blocks (0xE49857..0xE4987C)
static int GwListIdx(DWORD n) { return n <= 32 ? (int)n : n >= 256 ? 60 : (int)((n - 32) >> 3) + 32; }

// The refusal inputs right before the expansion, as text
static void GwWhy(DWORD needed, DWORD heap, DWORD largeFree, char* d, int room) {
    const GwBoehm& b = g_gwb;
    const DWORD used = heap - largeFree, req = GwRead(b.requested), at = GwRead(b.collectAt);
    const DWORD should = GwCall(b.shouldCollect), adj = GwCall(b.adj), mn = GwCall(b.min);
    const DWORD la = GwRead(b.largeAllocd), maxLa = GwRead(b.maxLargeAllocd), fin = GwRead(b.finFreedW), bl = GwRead(b.blSpacing);
    unsigned __int64 left = la;
    for (int i = 60; i >= GwListIdx(needed); i--) left += GwRead(b.freeBytes + i);
    const bool gate = !GwRead(b.useEntire) && !GwRead(b.dontGc) && used >= req && should == 1;
    const bool notEnough = left <= maxLa, finRefuse = (unsigned __int64)fin * 4 > (heap >> 4);
    _snprintf_s(d, room, _TRUNCATE,
              "gate %s (useEntire %lu dontGc %lu, used %lu KB vs requested %lu KB, should_collect %lu: adj %lu min %lu, collect_at %lu KB); "
              "enough large left %s (large_allocd %lu KB + lists %d..60 %I64u KB vs max_large_allocd %lu KB); finalizer freed %lu KB vs "
              "heap/16 %lu KB%s; bl spacing %lu KB (needed %lu KB %s it); large-alloc warn %lu / %lu",
              gate ? "ON" : "off", GwRead(b.useEntire), GwRead(b.dontGc), used >> 10, req >> 10, should, adj, mn, at >> 10,
              notEnough ? "NO" : "yes", la >> 10, GwListIdx(needed), (left - la) >> 10, maxLa >> 10, fin >> 8, heap >> 14,
              finRefuse ? " REFUSE" : "", bl >> 10, needed << 2, (needed << 12) > bl ? "above" : "within", GwRead(b.warnSupp),
              GwRead(b.warnIval));
}

static void GwLog(const GwLines& L) { Log("%s", L.head); Log("%s", L.why); Log("%s", L.script); }

// sp = esp at the thunk's entry: sp[0] = return address into GC_collect_or_expand, sp[1] = blocks
static int __cdecl GwExpand(DWORD blocks, DWORD needed, const DWORD* sp) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD before = GwRead(g_mx.heapSize), gcNo = GwRead(g_mx.gcNo), words = GwRead(g_mx.wordsAllocd), lf = GwRead(g_gwLargeFree);
    char why[700];
    if (k.gwMode) GwWhy(needed, before, lf, why, sizeof(why));
    const int r = g_gwExpand(blocks);
    const DWORD after = GwRead(g_mx.heapSize);
    if (g_gwLastHeap && before > g_gwLastHeap) k.gwUnseenKB += (LONG)((before - g_gwLastHeap) >> 10);
    g_gwLastHeap = after;
    const LONG n = ++k.gwExpands;  // under the GC allocation lock
    if (gcNo != g_gwStreakGc) k.gcExpandStreak = 0, g_gwStreakGc = gcNo;
    if (++k.gcExpandStreak > k.gcExpandStreakMax && (k.gcExpandStreakMax = k.gcExpandStreak) >= 2)
        Log("gc expand streak %ld: %lu blocks needed, no collection since gc_no %lu", k.gcExpandStreak, needed, gcNo);
    // docs/67 §35: a request at or below the spacing is never punted, new sections hold black-listed pages too and only a
    // collection refreshes the lists, so the heap took the whole address space (824 KB string, cap 1024 KB). The next GC_promote_black_lists rewrites this.
    const LONG streakAt = ts3native_gc_streak_at;
    if (g_gcCapBytes && streakAt && k.gcExpandStreak == streakAt) {
        char w[700];
        GwWhy(needed, after, GwRead(g_gwLargeFree), w, sizeof(w));
        Log("gc expand streak %s: %lu blocks needed, spacing %lu KB -> 12 KB (stack black-listed blocks %lu, gc_no %lu); %s",
            streakAt == GW_STREAK_PUNT ? "guard" : "test stop", needed, GwRead(g_gwb.blSpacing) >> 10,
            GwRead((const DWORD*)(uintptr_t)0x0123435C), gcNo, w);
        *g_gwb.blSpacing = 0x3000;
    }
    if (!r) k.gwFails++;
    k.gwKB += (LONG)((after - before) >> 10);
    const LONG askKB = (LONG)(blocks << 2);
    if (askKB > k.gwMaxAskKB) k.gwMaxAskKB = askKB;
    if (!k.gwMode) return r;
    const DWORD ret = sp[0], tid = GetCurrentThreadId();
    const int site = ret - 5 == GW_SITE[0] ? 1 : ret - 5 == GW_SITE[1] ? 2 : 0;
    DWORD chain[3] = {0, 0, 0};
    __try {
        for (int i = 2, m = 0; i < 2 + 96 && m < 3; i++)
            if (sp[i] >= GW_TEXT_LO && sp[i] < GW_TEXT_HI) chain[m++] = sp[i];
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    char xml[96] = "none", st[1200];
    if (g_gwXmlTid == tid)
        sprintf_s(xml, "type 0x%08lX group 0x%08lX instance 0x%08lX%08lX", g_gwXmlGT[1], g_gwXmlGT[0], g_gwXmlKey[0], g_gwXmlKey[1]);
    if (FbStackText(st, sizeof(st), 16) <= 0) strcpy_s(st, "none");
    GwLines& L = g_gwRing[g_gwRingN++ % 20];
    _snprintf_s(L.head, _TRUNCATE,
                "gcgrow: #%ld site %d (%s) ask %lu blocks (%lu KB) needed %lu blocks -> %s; heap %lu -> %lu KB; gc_no %lu, allocated since "
                "the last GC %lu KB, large free %lu KB; thread %lu%s; ret 0x%08lX, stack code refs 0x%08lX 0x%08lX 0x%08lX; xml %s",
                n, site, site == 1 ? "step" : site == 2 ? "retry needed" : "?", blocks, blocks << 2, needed, r ? "ok" : "FAILED",
                before >> 10, after >> 10, gcNo, (words << 2) >> 10, lf >> 10, tid, tid == g_mainTid ? " (main)" : "", ret, chain[0],
                chain[1], chain[2], xml);
    _snprintf_s(L.why, _TRUNCATE, "gcgrow why: #%ld %s", n, why);
    _snprintf_s(L.script, _TRUNCATE, "gcgrow script: #%ld %s", n, st);
    if (!g_gwCrossed && after > (600u << 20)) {  // the expansions up to the crossing, logged before or not, oldest first
        g_gwCrossed = true;
        const int have = g_gwRingN < 20 ? g_gwRingN : 20;
        Log("gcgrow: heap crossed 600 MB at expansion #%ld; the last %d expansions follow (oldest first)", n, have);
        for (int i = g_gwRingN - have; i < g_gwRingN; i++) GwLog(g_gwRing[i % 20]);
        k.gwLines += have;
        return r;
    }
    if ((n > 50 && blocks < 0x4000) || k.gwLines >= 500) return r;
    k.gwLines++;
    GwLog(L);
    return r;
}

// Replaces GC_expand_hp_inner at both calls (cdecl, 1 argument): GwExpand(blocks, esi = needed, entry esp)
static __declspec(naked) void GwThunk() {
    __asm {
        mov eax, esp
        push eax
        push esi
        push dword ptr [eax + 4]
        call GwExpand
        add esp, 12
        ret
    }
}

// Simulator_LoadXmlAsString (cdecl): the key for this thread while the game reads the resource and builds the string
static DWORD __cdecl GwXml(DWORD lo, DWORD hi, DWORD group, DWORD type) {
    const DWORD tid = GetCurrentThreadId();
    g_gwXmlKey[0] = hi, g_gwXmlKey[1] = lo, g_gwXmlGT[0] = group, g_gwXmlGT[1] = type;
    g_gwXmlTid = tid;
    const DWORD r = g_gwXmlOrig(lo, hi, group, type);
    if (g_gwXmlTid == tid) g_gwXmlTid = 0;
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.gwXmlCalls);
    const DWORD len = r ? GwRead((const DWORD*)(uintptr_t)(r + 4)) : 0;  // EA's MonoString {vtable, length +4, chars +8} (autshadow.cpp)
    const LONG kb = (LONG)(len >> 9);
    if (kb > k.gwXmlMaxKB) k.gwXmlMaxKB = kb;
    if (k.gwMode && kb >= 1024 && InterlockedIncrement(&g_gwXmlLines) <= 200)
        Log("gcgrow xml: type 0x%08lX group 0x%08lX instance 0x%08lX%08lX -> %lu chars (%ld KB) string 0x%08lX; thread %lu; bl spacing %lu KB, heap %lu KB, "
            "gc_no %lu, expand streak %ld", type, group, hi, lo, len, kb, r, tid, GwRead(g_gwb.blSpacing) >> 10, GwRead(g_mx.heapSize) >> 10,
            GwRead(g_mx.gcNo), k.gcExpandStreak);
    return r;
}

// [GC] BlackListCapKB: GC_promote_black_lists clamps the spacing to 8 MB (cmp eax,0x800000 ; jbe ; mov [spacing],0x800000)
static const Site kBlCap[] = {
    {0x00E4A856, {0x3D, 0x00, 0x00, 0x80, 0x00, 0x76, 0x0A}, 7, 1},
    {0x00E4A85D, {0xC7, 0x05, 0x2C, 0x48, 0x19, 0x01, 0x00, 0x00, 0x80, 0x00}, 10, 6},
};
// GC_allochblk_nth punt: push eax (bytes) ; push fmt ; call [GC_current_warn_proc] ; add esp,8 (eax/ecx/edx dead after)
static const Site kBlPunt[] = {{0x00E49540, {0xFF, 0x15, 0xC0, 0x49, 0x19, 0x01}, 6, 0}};
typedef void(__cdecl* GwWarnFn)(const char* fmt, DWORD arg);
static void* const* g_gwWarnProc = (void* const*)(uintptr_t)0x011949C0;  // GC_current_warn_proc (Mono sets it after GC_init)
static LONG g_gwWarnEvery = 5;  // GC_large_alloc_warn_interval before we set it to 1
static DWORD g_gcT0;                // GetTickCount at init
static bool g_gcStrict;             // the 8 MB clamp is back (a world is loaded and playing)

static void __cdecl GcBlackOk(const char* fmt, DWORD bytes) {
    Ts3nCounters& k = ts3native_counters;
    const LONG n = ++k.gcBlackOk, kb = (LONG)(bytes >> 10);  // under the GC allocation lock
    if (kb > k.gcBlackOkMaxKB) k.gcBlackOkMaxKB = kb;
    if (n <= 20) Log("gc black-list ok #%ld: %ld KB taken despite black-listed pages (spacing %lu KB); thread %lu", n, kb,
                     GwRead(g_gwb.blSpacing) >> 10, GetCurrentThreadId());
    if (n % g_gwWarnEvery == 0)
        if (GwWarnFn f = (GwWarnFn)*g_gwWarnProc) f(fmt, bytes);
}

static bool ApplyGcBlackCap(const wchar_t* ini) {
    const int kb = GetPrivateProfileIntW(L"GC", L"BlackListCapKB", 0, ini);
    if (!kb || !GetPrivateProfileIntW(L"GC", L"Enabled", 1, ini)) { Log("gc black-list cap off (BlackListCapKB=0)"); return false; }
    const DWORD cap = (DWORD)Clamp(kb, 12, 8192);
    if (!PatchImm32("GC black-list spacing cap", kBlCap, 2, cap << 10)) return false;
    ts3native_counters.gcCapKB = (LONG)cap;
    g_gcCapBytes = cap << 10, g_gcT0 = GetTickCount();  // lenient until the first world has loaded (GcCapWindow)
    u8 call[6] = {0xE8, 0, 0, 0, 0, 0x90};
    *(LONG*)(call + 1) = (LONG)((uintptr_t)GcBlackOk - (kBlPunt[0].addr + 5));
    DWORD* ival = (DWORD*)(uintptr_t)g_gwb.warnIval;
    if (!Matches((const void*)(uintptr_t)kBlPunt[0].addr, kBlPunt[0].expect, 6) || !Readable(ival, 4)) {
        Log("SKIP gc black-list ok count: bytes at 0x%08X differ; the cap stays", kBlPunt[0].addr);
        return true;
    }
    g_gwWarnEvery = *ival >= 1 && *ival < 0x10000 ? (LONG)*ival : 5;
    *ival = 1;  // every punt reaches GcBlackOk (.data, writable)
    if (!Write((void*)(uintptr_t)kBlPunt[0].addr, call, 6)) Log("FAIL gc black-list ok count: VirtualProtect at 0x%08X", kBlPunt[0].addr);
    else Log("OK   gc black-list ok count: 0x%08X call [warn proc] -> GcBlackOk (warning forwarded every %ld)", kBlPunt[0].addr, g_gwWarnEvery);
    return true;
}

// Window (docs/232 Run 8q): lenient (the cap) from init, at the menu and on every load screen; strict (EA's 8 MB clamp)
// while a world is loaded and playing. Sim thread, from the ts3fixes "world:" (load finished) and "world quit" lines
// (userdiag.cpp). Both immediates are byte-checked against the value we expect there first (PatchImm32: both or none).
// Without ts3fixes no line arrives and the cap stays lenient for the whole session (the PR #238 behaviour).
static void GcCapWindow(bool strict, const char* ev) {
    if (!g_gcCapBytes || strict == g_gcStrict) return;
    const DWORD from = strict ? g_gcCapBytes : 0x800000u, to = strict ? 0x800000u : g_gcCapBytes;
    Site s[2] = {kBlCap[0], kBlCap[1]};
    memcpy(s[0].expect + 1, &from, 4), memcpy(s[1].expect + 6, &from, 4);
    if (!PatchImm32(strict ? "GC black-list clamp strict" : "GC black-list clamp lenient", s, 2, to)) return;
    g_gcStrict = strict;
    Ts3nCounters& k = ts3native_counters;
    (strict ? k.gcStrictN : k.gcLenientN)++;
    k.gcSwitchSec = (LONG)((GetTickCount() - g_gcT0) / 1000);
    Log("gc blcap: %s at %s (switch %ld, %ld s after init; spacing now %lu KB, follows at the next promote)",
        strict ? "restored 8 MB clamp" : "cap lenient again", ev, k.gcStrictN + k.gcLenientN, k.gcSwitchSec, GwRead(g_gwb.blSpacing) >> 10);
}

static bool ApplyGcGrowLog(const wchar_t* ini) {
    const bool cap = ApplyGcBlackCap(ini), log = GetPrivateProfileIntW(L"GcGrowLog", L"Enabled", 0, ini) != 0;
    if (!log && !cap) { Log("gc grow log off (Enabled=0)"); return false; }
    const CallSite s[2] = {{GW_SITE[0], GW_EXPAND, (void*)GwThunk}, {GW_SITE[1], GW_EXPAND, (void*)GwThunk}};
    if (!PatchCalls("gc grow log", s, 2)) return cap;
    ts3native_counters.gwReady = 1;
    ts3native_counters.gwMode = log;
    if (log && InstallDetour("Simulator_LoadXmlAsString (gc grow log)", GW_XML, kGwXmlPro, sizeof(kGwXmlPro), (void*)GwXml, &g_gwXmlOrig))
        ts3native_counters.gwReady |= 2;
    Log("OK   gc grow log: GC_expand_hp_inner 0x%08X calls 0x%08X / 0x%08X -> GwThunk (%s); xml key %s", GW_EXPAND, GW_SITE[0], GW_SITE[1],
        log ? "first 50 expansions, then >= 64 MB, the last 20 at 600 MB" : "count only, for the [GC] BlackListCapKB streak counter",
        g_gwXmlOrig ? "on" : "SKIP");
    return true;  // keep the log open for the gcgrow / black-list lines
}
