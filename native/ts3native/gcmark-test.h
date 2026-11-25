// TestGcMarkHelpers (gcmark.cpp, docs/287 §6-§7): the [GC] MarkHelpers pool on a toy heap through the live entry points
// (G5Little, G5ParMarkFrom, G5Clear with fake originals), plus the emulated patch sites. Included by selftest.cpp.
// Toy heap: node i has out-edges g_tEdge[g_tOff[i] .. g_tOff[i + 1]). Toy marker (TMarkFrom, the stand-in for
// GC_mark_from): an entry is (node, first edge); it scans at most 16 edges per entry (pushing the rest back, like a
// partial scan of a large object), spends one credit per edge, sets mark bits with _interlockedbittestandset (the P3
// lock bts) and pushes newly marked children; on overflow it drops the 512 newest entries and runs the overflow rule of the
// 0xE4C390 detour: a helper only raises its flag (G5HelperOverflow), the coordinator writes state 5 + too_small as EA does.
// The fake GC_collect_a_little_inner (TLittle) emulates GC_mark_some_inner: roots in batches, up to `win` marker calls per
// window, a rescan of marked nodes in state 5 (overflow or helper fault), state 0 when the mark ends.

static DWORD *g_tOff, *g_tEdge;
static LONG g_tN, g_tNRoots, g_tCredit = 128;
static volatile LONG *g_tMark, *g_tScan;
static volatile LONG g_tPush, g_tPop, g_tDrop, g_tPoison, g_tViol, g_tAvArm;
static volatile LONG g_tState, g_tTooSmall;  // stand-ins for [0x11F3078] (1 = marking, 5 = rescan, 0 = done) / [0x11F307C]
static DWORD g_tCoord;

static G5E* __cdecl TMarkFrom(G5E* top, G5E* base, G5E* limit) {
    const int helper = GetCurrentThreadId() != g_tCoord;
    if (helper && g_tPoison) InterlockedIncrement(&g_tViol);  // started after G5WindowLeave
    LONG credit = g_tCredit, push = 0, pop = 0, drop = 0;
    auto put = [&](DWORD s, DWORD d) {
        if (top + 1 >= limit) {
            if (G5HelperOverflow()) assert(helper);  // helper: flag only, never the state
            else assert(!helper), InterlockedExchange(&g_tState, 5), InterlockedExchange(&g_tTooSmall, 1);
            const LONG n = G5Depth(top, base) < 512 ? G5Depth(top, base) : 512;
            top -= n, drop += n;
        }
        ++top, top->start = s, top->descr = d, push++;
    };
    while (top >= base && credit > 0) {
        const G5E e = *top--;
        pop++;
        if (helper && g_tAvArm && InterlockedExchange(&g_tAvArm, 0)) *(volatile int*)(uintptr_t)8 = 1;  // toy fault
        if (!e.descr) InterlockedIncrement(&g_tScan[e.start]);
        const DWORD beg = g_tOff[e.start] + e.descr, end = g_tOff[e.start + 1];
        DWORD stop = end;
        if (end - beg > 16) stop = beg + 16, put(e.start, e.descr + 16);
        for (DWORD j = beg; j < stop; j++, credit--) {
            const DWORD c = g_tEdge[j];
            if (!_interlockedbittestandset(&g_tMark[c >> 5], c & 31)) put(c, 0);
        }
    }
    InterlockedExchangeAdd(&g_tPush, push), InterlockedExchangeAdd(&g_tPop, pop), InterlockedExchangeAdd(&g_tDrop, drop);
    if (helper && g_tPoison) InterlockedIncrement(&g_tViol);  // still inside fn after G5WindowLeave returned
    return top;
}

static void TBuild(LONG n, unsigned seed) {
    static DWORD s;
    s = seed;
    auto rnd = [] { return s = s * 1664525u + 1013904223u, s >> 8; };
    free(g_tOff), free(g_tEdge), free((void*)g_tMark), free((void*)g_tScan);
    g_tN = n, g_tNRoots = 16 + (LONG)(rnd() % 64);
    g_tOff = (DWORD*)malloc((n + 1) * sizeof(DWORD));
    LONG m = 0;
    for (LONG i = 0; i < n; i++) g_tOff[i] = m, m += (rnd() % 64 == 0) ? 40 + rnd() % 60 : rnd() % 6;  // a few large objects
    g_tOff[n] = m;
    g_tEdge = (DWORD*)malloc((m ? m : 1) * sizeof(DWORD));
    for (LONG j = 0; j < m; j++) g_tEdge[j] = rnd() % (DWORD)n;
    g_tMark = (volatile LONG*)calloc(n / 32 + 1, 4), g_tScan = (volatile LONG*)calloc(n, 4);
}
static void TClear() {
    memset((void*)g_tMark, 0, (g_tN / 32 + 1) * 4), memset((void*)g_tScan, 0, g_tN * 4);
    g_tPush = g_tPop = g_tDrop = 0;
    g_tState = 1, g_tTooSmall = 0;
}
static LONG* TSerial() {  // the reference closure, plain single-thread DFS
    LONG* mk = (LONG*)calloc(g_tN / 32 + 1, 4);
    DWORD* st = (DWORD*)malloc(g_tN * sizeof(DWORD));
    LONG sp = 0;
    for (LONG r = 0; r < g_tNRoots; r++)
        if (!(mk[r >> 5] & (1 << (r & 31)))) mk[r >> 5] |= 1 << (r & 31), st[sp++] = r;
    while (sp) {
        const DWORD v = st[--sp];
        for (DWORD j = g_tOff[v]; j < g_tOff[v + 1]; j++) {
            const DWORD c = g_tEdge[j];
            if (!(mk[c >> 5] & (1 << (c & 31)))) mk[c >> 5] |= 1 << (c & 31), st[sp++] = c;
        }
    }
    free(st);
    return mk;
}

static G5E g_tStack[4096];
struct TOpt {
    int win;      // calls per window (1..win)
    int resetAt;  // cycle restart (G5Clear) + full re-mark after this many calls (0 = never)
    int poisonSleepMs;
};
static struct {
    G5E* top;
    LONG phase, pos, calls, inWin, rescans, win, resetAt, done, reset, resets;
} g_td;

// fake GC_collect_a_little_inner 0xE49DA0 behind G5Little: up to win marker calls, then back (the window closes)
static int __cdecl TLittle(int, void*) {
    G5E *const base = g_tStack, *const limit = g_tStack + _countof(g_tStack);
    for (;;) {
        if (g_td.top < base) {
            if (g_td.phase == 0) {  // roots, 1024 per batch
                for (LONG i = 0; g_td.pos < g_tNRoots && i < 1024; g_td.pos++, i++)
                    if (!_interlockedbittestandset(&g_tMark[g_td.pos >> 5], g_td.pos & 31))
                        ++g_td.top, g_td.top->start = g_td.pos, g_td.top->descr = 0, InterlockedIncrement(&g_tPush);
                if (g_td.pos >= g_tNRoots) g_td.phase = 1;
            } else if (g_td.phase == 2) {  // state 5: rescan from marked nodes
                for (LONG i = 0; g_td.pos < g_tN && i < 1024; g_td.pos++)
                    if (g_tMark[g_td.pos >> 5] & (1 << (g_td.pos & 31)))
                        ++g_td.top, g_td.top->start = g_td.pos, g_td.top->descr = 0, i++, InterlockedIncrement(&g_tPush);
                if (g_td.pos >= g_tN) g_td.phase = 1;
            }
            if (g_td.top < base && g_td.phase == 1) {
                if (InterlockedCompareExchange(&g_tState, 1, 5) != 5) {  // done: EA's state 0 (0xE4D38F)
                    InterlockedExchange(&g_tState, 0), g_td.done = 1;
                    return 0;
                }
                g_tTooSmall = 0, g_td.phase = 2, g_td.pos = 0, g_td.rescans++;
            }
            continue;
        }
        g_td.top = G5ParMarkFrom(g_td.top, base, limit), g_td.calls++;
        if (g_td.top < base) assert(G5OutsideN() == 0);  // I1
        if (g_td.resetAt && g_td.calls == g_td.resetAt) return g_td.reset = 1, 0;
        if (++g_td.inWin >= g_td.win) return g_td.inWin = 0, 0;
    }
}
static int __cdecl TClearNext() { return 0; }

// returns the number of rescans (overflow or helper fault); checks I1 at every empty return
static int TDrive(const TOpt& o, unsigned seed) {
    G5E *const base = g_tStack, *const limit = g_tStack + _countof(g_tStack);
    memset(&g_td, 0, sizeof g_td);
    g_td.top = base - 1, g_td.win = 1 + (LONG)(seed % (unsigned)o.win), g_td.resetAt = o.resetAt;
    g_g5LittleOrig = TLittle, g_g5ClearNext = TClearNext;
    TClear();
    while (!g_td.done) {
        G5Little(1, NULL);
        if (g_td.done) break;
        if (g_td.reset) {  // cycle restart mid-mark (0xE49FDF, outside the window), then a full re-mark
            G5Clear(), g_td.resets++;
            assert(G5OutsideN() == 0);
            TClear();
            g_td.reset = 0, g_td.top = base - 1, g_td.phase = 0, g_td.pos = 0;
            continue;
        }
        InterlockedExchange(&g_tPoison, 1);
        if (o.poisonSleepMs && g_td.calls % 400 == 0) Sleep(o.poisonSleepMs);
        if (g_td.calls % 3 == 0 && g_td.top >= base) {  // a call outside the window: hand-back path
            g_td.top = G5ParMarkFrom(g_td.top, base, limit), g_td.calls++;
            if (g_td.top < base) assert(G5OutsideN() == 0);
        }
        InterlockedExchange(&g_tPoison, 0);
    }
    assert(G5OutsideN() == 0 && g_tState == 0);
    return g_td.rescans;
}

static DWORD WINAPI TModeFlipper(void* p) {  // flips g5Mode every ~0.5 ms until *p is cleared
    for (int i = 0; *(volatile LONG*)p; i++) {
        InterlockedExchange(&ts3native_counters.g5Mode, i & 1);
        for (int j = 0; j < 2000; j++) YieldProcessor();
    }
    InterlockedExchange(&ts3native_counters.g5Mode, 1);
    return 0;
}

static void TCheck(const LONG* ref, int clean, const char* what) {
    const LONG words = g_tN / 32 + 1;
    if (memcmp(ref, (const void*)g_tMark, words * 4)) {
        printf("gc mark helpers: %s: marked set differs from the serial closure\n", what);
        assert(!"gcmark: marked set");
    }
    assert(g_tViol == 0);                          // no helper touched the heap after G5WindowLeave
    assert(ts3native_counters.g5Anomaly == 0);     // no helper flag after the mark ended
    if (!clean) return;
    assert(g_tPush == g_tPop && g_tDrop == 0);  // no entry lost or duplicated across donate / take / hand-back
    for (LONG i = 0; i < g_tN; i++) assert(g_tScan[i] == ((ref[i >> 5] >> (i & 31)) & 1));
}

// ---- emulated patch sites: EA's bytes and ours run on fake headers (docs/287 §7) ----
typedef int(__cdecl* TBitFn)(void* hdr, int bit);
typedef int(__cdecl* TPushFn)(void* hdr, int word, int bit, DWORD* flags);
typedef DWORD(__cdecl* TSetFn)(DWORD* word, DWORD mask);
// mark-bit site at +0x80 with the game's pre-code (ecx = bit & 31, eax = 1 << ecx, edx = bit >> 5, edi = hdr, esi = bit);
// returns 1 when the jne / jc to the skip target was taken
static void TBitBuild(u8* b, const u8* site, u8 rel) {
    static const u8 pro[] = {0x53, 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x10, 0x8B, 0x74, 0x24, 0x14, 0x8B, 0xCE, 0x83, 0xE1,
                             0x1F, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xD3, 0xE0, 0x8B, 0xD6, 0xC1, 0xFA, 0x05, 0xE9};
    memset(b, 0xCC, 0x110);
    memcpy(b, pro, sizeof pro);
    LONG r = 0x80 - (LONG)(sizeof pro + 4);
    memcpy(b + sizeof pro, &r, 4);
    memcpy(b + 0x80, site, 14);
    b[0x8E] = 0x33, b[0x8F] = 0xC0, b[0x90] = 0xE9, r = 0x100 - 0x95, memcpy(b + 0x91, &r, 4);  // fall-through: eax = 0
    const int t = 0x88 + (signed char)rel;                                                     // skip target: eax = 1
    b[t] = 0xB8, b[t + 1] = 1, b[t + 2] = b[t + 3] = b[t + 4] = 0, b[t + 5] = 0xE9, r = 0x100 - (t + 10), memcpy(b + t + 6, &r, 4);
    b[0x100] = 0x5F, b[0x101] = 0x5E, b[0x102] = 0x5B, b[0x103] = 0xC3;
}
static void TPushBuild(u8* b, const u8* site) {  // esi = hdr, edx = word, ecx = bit; flags out
    static const u8 pro[] = {0x56, 0x8B, 0x74, 0x24, 0x08, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x10};
    static const u8 epi[] = {0x9C, 0x5A, 0x8B, 0x4C, 0x24, 0x14, 0x89, 0x11, 0x5E, 0xC3};
    memset(b, 0xCC, 0x40);
    memcpy(b, pro, sizeof pro), memcpy(b + sizeof pro, site, 11), memcpy(b + sizeof pro + 11, epi, sizeof epi);
}
static void TSetBuild(u8* b, const u8* site) {  // eax = word, edx = mask; the site pops esi and returns; we return that esi
    memset(b, 0xCC, 0x50);
    const u8 pro[] = {0x56, 0x8B, 0x44, 0x24, 0x08, 0x8B, 0x54, 0x24, 0x0C, 0x68, 0, 0, 0, 0, 0x68, 0x5A, 0x5A, 0x5A, 0x5A, 0xE9};
    memcpy(b, pro, sizeof pro);
    const DWORD after = (DWORD)(uintptr_t)(b + 0x30);
    memcpy(b + 10, &after, 4);
    const LONG r = 0x40 - (LONG)(sizeof pro + 4);
    memcpy(b + sizeof pro, &r, 4);
    b[0x30] = 0x8B, b[0x31] = 0xC6, b[0x32] = 0x5E, b[0x33] = 0xC3;
    memcpy(b + 0x40, site, 5);
}
static u8* g_tCode;
static volatile LONG g_tRaceGo;
static u8 g_tRaceHdr[64][0x100];
static DWORD WINAPI TBitRacer(void* p) {  // patched site from 4 threads, interleaved bits of the same words
    const int t = (int)(uintptr_t)p;
    const TBitFn fn = (TBitFn)(void*)(g_tCode + 0x200);
    while (!g_tRaceGo) YieldProcessor();
    LONG taken = 0;
    for (int h = 0; h < 64; h++)
        for (int bit = t; bit < 1024; bit += 4) taken += fn(g_tRaceHdr[h], bit);
    return (DWORD)taken;
}
static void TestG5Sites() {
    u8* const code = (u8*)VirtualAlloc(NULL, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    assert(code);
    g_tCode = code;
    DWORD s = 12345;
    auto rnd = [&s] { return s = s * 1664525u + 1013904223u, (u8)(s >> 16); };
    for (const G5BitSite& site : kG5BitSites) {
        u8 old[14], nw[14];
        G5SiteOld(site.rel, old), G5SiteNew(site.rel, nw);
        TBitBuild(code, old, site.rel), TBitBuild(code + 0x200, nw, site.rel);
        FlushInstructionCache(GetCurrentProcess(), code, 0x1000);
        for (int bit = 0; bit < 1024; bit++)
            for (int v = 0; v < 3; v++) {  // random header, the bit already set, the bit clear
                u8 a[0x100], b[0x100];
                for (u8& x : a) x = rnd();
                if (v == 1) a[0x1C + bit / 8] |= (u8)(1 << (bit & 7));
                if (v == 2) a[0x1C + bit / 8] &= (u8)~(1 << (bit & 7));
                memcpy(b, a, sizeof a);
                const int was = (a[0x1C + bit / 8] >> (bit & 7)) & 1;
                const int ra = ((TBitFn)(void*)code)(a, bit), rb = ((TBitFn)(void*)(code + 0x200))(b, bit);
                assert(ra == was && rb == was && !memcmp(a, b, sizeof a) && ((a[0x1C + bit / 8] >> (bit & 7)) & 1));
            }
    }
    {  // the patched site from 4 threads: no bit lost, each set exactly once
        memset(g_tRaceHdr, 0, sizeof g_tRaceHdr);
        g_tRaceGo = 0;
        HANDLE th[4];
        for (int t = 0; t < 4; t++) th[t] = CreateThread(NULL, 0, TBitRacer, (void*)(uintptr_t)t, 0, NULL);
        InterlockedExchange(&g_tRaceGo, 1);
        for (HANDLE h : th) {
            DWORD taken = 1;
            WaitForSingleObject(h, INFINITE), GetExitCodeThread(h, &taken), CloseHandle(h);
            assert(taken == 0);
        }
        for (auto& h : g_tRaceHdr)
            for (int i = 0; i < 0x80; i++) assert(h[0x1C + i] == 0xFF);
    }
    TPushBuild(code, kG5PushOld), TPushBuild(code + 0x200, kG5PushNew);
    TSetBuild(code + 0x400, kG5SetOld), TSetBuild(code + 0x600, kG5SetNew);
    FlushInstructionCache(GetCurrentProcess(), code, 0x1000);
    for (int bit = 0; bit < 1024; bit++)
        for (int v = 0; v < 3; v++) {
            u8 a[0x100], b[0x100];
            for (u8& x : a) x = rnd();
            if (v == 1) a[0x1C + bit / 8] |= (u8)(1 << (bit & 7));
            if (v == 2) a[0x1C + bit / 8] &= (u8)~(1 << (bit & 7));
            memcpy(b, a, sizeof a);
            DWORD fa = 0, fb = 0;
            const int ra = ((TPushFn)(void*)code)(a, bit >> 5, bit & 31, &fa), rb = ((TPushFn)(void*)(code + 0x200))(b, bit >> 5, bit & 31, &fb);
            assert(ra == rb && (fa & 0x8C5) == (fb & 0x8C5) && !memcmp(a, b, sizeof a));  // CF PF ZF SF OF (AF undefined after or)
            DWORD wa = 0, wb = 0;
            memcpy(&wa, a + 4 * (bit & 31), 4), wb = wa;
            const DWORD mask = (DWORD)rnd() << 24 | (DWORD)rnd() << 8 | (1u << (bit & 31));
            assert(((TSetFn)(void*)(code + 0x400))(&wa, mask) == 0x5A5A5A5A && ((TSetFn)(void*)(code + 0x600))(&wb, mask) == 0x5A5A5A5A);
            assert(wa == wb);
        }
    VirtualFree(code, 0, MEM_RELEASE);
}

// the two naked stubs with fake targets: registers kept, overflow on a helper only flags it
static __declspec(naked) void TGrowOrig() {
    __asm {
        mov eax, edi
        add eax, esi
        ret
    }
}
static __declspec(naked) void TOvfResume() {  // what EA's code at 0xE4C397 would see: the flags of cmp [print_stats],0
    __asm {
        mov eax, dword ptr [esp + 4]
        jz quiet
        inc eax
    quiet:
        ret
    }
}
static int TCallGrow(void* stub) {
    int r;
    __asm {
        push esi
        push edi
        mov edi, 0x1000
        mov esi, 4
        mov eax, stub
        call eax
        pop edi
        pop esi
        mov r, eax
    }
    return r;
}
static void TestG5Stubs() {
    Ts3nCounters& k = ts3native_counters;
    assert(G5Init(2, TMarkFrom, &g_tState, &g_tTooSmall) == 2);
    g_g5GrowOrig = (DWORD)(uintptr_t)TGrowOrig;
    G5E fake[200] = {};
    auto fill = [&] {
        AcquireSRWLockExclusive(&g_g5.lock);
        G5DonateLk(fake + 199, fake, 100);
        ReleaseSRWLockExclusive(&g_g5.lock);
    };
    const LONG gd = k.g5GrowDrop, an = k.g5Anomaly;
    assert(TCallGrow((void*)G5GrowInvalid) == 0x1004 && k.g5GrowDrop == gd);  // nothing outside: nothing counted
    fill();
    assert(G5OutsideN() == 100 && TCallGrow((void*)G5GrowInvalid) == 0x1004 && G5OutsideN() == 0 && k.g5GrowDrop == gd + 1 && k.g5Anomaly == an);
    fill();
    assert(TCallGrow((void*)G5GrowDone) == 0x1004 && G5OutsideN() == 0 && k.g5Anomaly == an + 1);
    k.g5Anomaly = an;
    static LONG printStats;
    g_g5OvfPrintStats = (DWORD)(uintptr_t)&printStats, g_g5OvfResume = (DWORD)(uintptr_t)TOvfResume;
    typedef DWORD(__cdecl * OvfFn)(DWORD);
    const OvfFn ovf = (OvfFn)(void*)G5OvfStub;
    printStats = 0;
    assert(ovf(0x50000) == 0x50000);  // coordinator: EA's path, je taken
    printStats = 1;
    assert(ovf(0x50000) == 0x50001);  // coordinator: EA's path, logging branch
    const DWORD tid = g_g5.h[0].tid;
    const LONG po = k.g5PrivOverflow;
    g_g5.h[0].tid = GetCurrentThreadId();  // pose as helper 0 (parked: no window open)
    assert(ovf(0x50000) == 0x50000 - 0x1000 && g_g5.h[0].ovf == 1 && k.g5PrivOverflow == po + 1);
    g_g5.h[0].tid = tid, g_g5.h[0].ovf = 0;
    G5Shutdown();

    assert(g_g5.fn == TMarkFrom);  // fn survives (a retargeted site never calls NULL)
}

// P4 [GC] MarkHelpersVerify: G5Verify on a toy block index (2 bottom indices, 5 used blocks + 1 free + a forwarding entry)
// with a toy GC_mark_some that marks a fixed pattern and dirties everything the check must restore
static u8 g_tvHdr[6][0x11C];
static DWORD g_tvBi[2][0x403], g_tvBiHead, g_tvInvalid = 0x1234;
static const DWORD kTvPat[6] = {0x00000101, 0x80000000, 0x0000F0F0, 0, 0x00000001, 0x00010000};  // live marks, word 5 of each block
static volatile LONG g_tvScan, g_tvObj, g_tvResc, g_tvWorld, g_tvPend, g_tvStops, g_tvLose, g_tvHang, g_tvRuns;
static G5E g_tvStk[8], *g_tvBase = g_tvStk, *g_tvTop = g_tvStk - 1;
static u8 g_tvBl[2][0x2000], *g_tvBlP[2] = {g_tvBl[0], g_tvBl[1]};
static DWORD* TvMarks(int b) { return (DWORD*)(g_tvHdr[b] + 0x1C); }
static void __cdecl TvStop() {
    assert(!g_tvStops && !g_tvWorld);
    g_tvStops++;
}
static void __cdecl TvStart() {
    assert(g_tvStops == 1 && !g_tvWorld);
    g_tvStops--;
}
static int __cdecl TvMarkSome(void* cold) {
    assert(cold && g_tvWorld == 1 && g_tvStops == 1);
    if (g_tvHang) return g_tvTop = g_tvStk + 2, 0;  // never finishes, entries left on the stack
    if (g_tState == 5) {  // first step of a full mark: collectable blocks arrive cleared, uncollectable ones keep their marks
        assert(!g_tvObj && !g_tvScan);
        for (int b = 0; b < 6; b++)
            assert(TvMarks(b)[5] == (b == 2 || b == 5 ? kTvPat[b] : b == 3 ? 0xDEADu : 0u) && TvMarks(b)[0] == (b == 3 ? 0xDEADu : 0u));
        g_tState = 3, g_tvObj = 1, g_tvScan = 0x77, g_tTooSmall = 1, g_tvResc += 7, g_tvBl[0][3] |= 0x10, g_tvBl[1][9] ^= 0xFF, g_tvRuns++;
        return 0;
    }
    assert(g_tState == 3);
    for (int b = 0; b < 6; b++)
        if (b != 3) TvMarks(b)[5] |= kTvPat[b];
    if (g_tvLose && g_g5.active) TvMarks(1)[5] = 0;  // the helper mark loses an object
    g_tState = 0, g_tvScan = 0;
    return 1;
}
static int __cdecl TvLittle(int, void*) { return g_tvPend = 1; }
static void TestG5Verify() {
    Ts3nCounters& k = ts3native_counters;
    assert(G5Init(2, TMarkFrom, &g_tState, &g_tTooSmall) == 2 && k.g5Mode == 1);
    const int kind[6] = {0, 1, 2, 0, 0, 3};
    for (int b = 0; b < 6; b++) {
        memset(g_tvHdr[b], 0, sizeof g_tvHdr[b]);
        ((DWORD*)g_tvHdr[b])[0] = 4 + b, ((DWORD*)g_tvHdr[b])[4] = b == 3 ? g_tvInvalid : 0x5000 + b, g_tvHdr[b][0x14] = (u8)kind[b];
    }
    memset(g_tvBi, 0, sizeof g_tvBi);
    g_tvBi[0][3] = (DWORD)(uintptr_t)g_tvHdr[0], g_tvBi[0][4] = 1 /* forwarding */, g_tvBi[0][5] = (DWORD)(uintptr_t)g_tvHdr[1];
    g_tvBi[0][9] = (DWORD)(uintptr_t)g_tvHdr[2], g_tvBi[0][10] = (DWORD)(uintptr_t)g_tvHdr[3] /* free */;
    g_tvBi[0][0x3FF] = (DWORD)(uintptr_t)g_tvHdr[4], g_tvBi[0][0x400] = (DWORD)(uintptr_t)g_tvBi[1], g_tvBi[0][0x402] = 5;
    g_tvBi[1][0] = (DWORD)(uintptr_t)g_tvHdr[5], g_tvBi[1][0x402] = 6;
    g_tvBiHead = (DWORD)(uintptr_t)g_tvBi[0];
    g_g5v = {&g_tvBiHead, &g_tvInvalid, &g_tvScan, &g_tvObj,   &g_tvResc, &g_tvWorld, &g_tvPend,
             &g_tvBase,   &g_tvTop,     {&g_tvBlP[0], &g_tvBlP[1]}, TvMarkSome, TvStop,    TvStart};
    G5VRec rec[8];
    assert(G5VBlocks(NULL, 0) == 5 && G5VBlocks(rec, 4) == -1 && G5VBlocks(rec, 8) == 5);
    assert(rec[0].hdr == (DWORD*)g_tvHdr[0] && rec[0].blk == ((5u << 10 | 3) << 12) && rec[3].blk == ((5u << 10 | 0x3FF) << 12) &&
           rec[4].hdr == (DWORD*)g_tvHdr[5] && rec[4].blk == (6u << 10) << 12);
    static u8 hdr0[6][0x11C], bl0[2][0x2000];
    auto arm = [&](DWORD miss) {  // the cycle's marks: the live pattern plus floating garbage (word 0), minus `miss` in block 0
        for (int b = 0; b < 6; b++) TvMarks(b)[5] = b == 3 ? 0xDEAD : kTvPat[b], TvMarks(b)[0] = b == 3 ? 0xDEAD : b == 2 || b == 5 ? 0 : 0x40 + b;
        TvMarks(0)[5] &= ~miss;
        memset(g_tvBl, 0x5A, sizeof g_tvBl);
        g_tState = 0, g_tTooSmall = 0, g_tvScan = 0x11, g_tvObj = 1, g_tvResc = 3, g_tvTop = g_tvBase - 1, g_tvLose = g_tvHang = g_tvRuns = 0;
        memcpy(hdr0, g_tvHdr, sizeof hdr0), memcpy(bl0, g_tvBl, sizeof bl0);
        InterlockedExchange(&k.g5Mode, 1);
    };
    auto same = [&] {  // everything back as the cycle left it, the world started
        return !memcmp(hdr0, g_tvHdr, sizeof hdr0) && !memcmp(bl0, g_tvBl, sizeof bl0) && g_tState == 0 && g_tTooSmall == 0 && g_tvScan == 0x11 &&
               g_tvObj == 1 && g_tvResc == 3 && g_tvTop == g_tvBase - 1 && !g_tvWorld && !g_tvStops && G5OutsideN() == 0 && !g_g5.active;
    };
    const LONG n0 = k.g5VerifyN, bad0 = k.g5VerifyBad, sub0 = k.g5VerifySub, skip0 = k.g5VerifySkip;
    arm(0);
    G5Verify();  // clean: helper mark == serial mark, both inside the cycle's marks
    assert(same() && g_tvRuns == 2 && k.g5VerifyN == n0 + 1 && k.g5VerifyBad == bad0 && k.g5VerifySub == sub0 && k.g5Mode == 1);
    arm(0), g_tvLose = 1;
    G5Verify();  // the helper mark lost a bit: bad, latched
    assert(same() && k.g5VerifyN == n0 + 2 && k.g5VerifyBad == bad0 + 1 && k.g5VerifySub == sub0 && k.g5Mode == 0);
    arm(0), g_tvLose = 1, InterlockedExchange(&k.g5Mode, 0);
    G5Verify();  // g5Mode 0: no window, both marks serial, nothing to find
    assert(same() && k.g5VerifyN == n0 + 3 && k.g5VerifyBad == bad0 + 1 && k.g5Mode == 0);
    arm(0x100);
    G5Verify();  // the cycle's marks lack a live object: counted, no latch
    assert(same() && k.g5VerifyN == n0 + 4 && k.g5VerifyBad == bad0 + 1 && k.g5VerifySub == sub0 + 1 && k.g5Mode == 1);
    arm(0), g_tvHang = 1, g_g5vMaxMs = 20;
    G5Verify();  // time limit inside the helper mark: skipped, stack emptied, marks back
    g_tvHang = 0, g_g5vMaxMs = 60000;
    assert(same() && k.g5VerifyN == n0 + 4 && k.g5VerifySkip == skip0 + 1 && k.g5Mode == 1);
    arm(0), g_tState = 1;
    G5Verify();  // a mark is running: skipped before the world stop
    assert(g_tState == 1 && g_tvRuns == 0 && k.g5VerifySkip == skip0 + 2 && !memcmp(hdr0, g_tvHdr, sizeof hdr0));
    // through G5Little: only a call that turns finish pending 0 -> 1 counts as a cycle; every 2nd is checked
    arm(0), g_g5LittleOrig = TvLittle, g_g5vEvery = 2, g_g5vCycle = 0, g_tvPend = 0;
    G5Little(1, NULL);
    assert(g_g5vCycle == 1 && k.g5VerifyN == n0 + 4);
    G5Little(1, NULL);  // pending already set: not a new cycle
    assert(g_g5vCycle == 1 && k.g5VerifyN == n0 + 4);
    g_tvPend = 0;
    G5Little(1, NULL);
    assert(g_g5vCycle == 2 && k.g5VerifyN == n0 + 5 && same() && k.g5VerifyBad == bad0 + 1);
    g_g5vEvery = 0, g_g5vCycle = 0, g_g5LittleOrig = NULL, memset(&g_g5v, 0, sizeof g_g5v);
    G5Shutdown();
}

static void TestGcMarkHelpers() {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    g_tCoord = GetCurrentThreadId();
    TestG5Sites();
    TestG5Stubs();
    const LONG sizes[] = {100000, 200000, 1000000};
    for (int n = 1; n <= 4; n++) {
        assert(G5Init(n, TMarkFrom, &g_tState, &g_tTooSmall) == n && k.g5Ready == 1 && k.g5Mode == 1);
        for (unsigned seed = 1; seed <= 3; seed++) {
            TBuild(sizes[(seed + n) % (n == 4 ? 3 : 2)], seed * 7919u + n);
            LONG* ref = TSerial();
            const LONG w0 = k.g5Windows, d0 = k.g5Donated, t0n = k.g5Taken;
            const TOpt plain = {20, 0, 0};
            const int rescans = TDrive(plain, seed);
            TCheck(ref, rescans == 0, "plain");
            assert(k.g5Windows > w0 && k.g5Donated > d0 && k.g5Taken > t0n);  // helpers really shared the work
            const TOpt reset = {8, 150 + (int)seed * 40, 0};  // G5Clear mid-mark, then a full re-mark
            const LONG r0 = k.g5Resets;
            TCheck(ref, TDrive(reset, seed + 5) == 0, "reset");
            assert(k.g5Resets == r0 + g_td.resets);  // the mark may end before resetAt calls when helpers take most of it

            if (seed == 1) {  // mode flips mid-mark from another thread
                volatile LONG run = 1;
                HANDLE th = CreateThread(NULL, 0, TModeFlipper, (void*)&run, 0, NULL);
                const TOpt flip = {20, 0, 0};
                TCheck(ref, TDrive(flip, seed) == 0, "mode flip");
                InterlockedExchange(&run, 0);
                WaitForSingleObject(th, INFINITE), CloseHandle(th);
            }
            if (seed == 2) {  // a helper faults: contained, counted, state 5 written by the coordinator, the rescan makes it exact
                const LONG av0 = k.g5HelperAv, sa0 = k.g5StateApply;
                k.g5HelperAv = 0;  // latch after 3 per session: tested below
                g_tAvArm = 1;
                const TOpt plainAv = {20, 0, 0};
                assert(TDrive(plainAv, seed) > 0);
                TCheck(ref, 0, "helper fault");
                assert(g_tAvArm == 0 && k.g5HelperAv == 1 && k.g5StateApply > sa0 && k.g5Mode == 1);
                k.g5HelperAv = av0 + 1;
                const TOpt poison = {4, 0, 1};  // window Leave joins: helpers stay off the heap while parked
                TCheck(ref, TDrive(poison, seed) == 0, "poison");
            }
            free(ref);
        }
        G5Shutdown();
        assert(k.g5Ready == 0 && k.g5Mode == 0);
    }
    // forced tiny private stacks: overflow flagged by the helper, state 5 written by the coordinator, the rescan is exact
    {
        const LONG ov0 = k.g5PrivOverflow, sa0 = k.g5StateApply;
        assert(G5Init(3, TMarkFrom, &g_tState, &g_tTooSmall, 16, 4096, 8) == 3);
        TBuild(100000, 4242);
        LONG* ref = TSerial();
        const TOpt plain = {20, 0, 0};
        const int rescans = TDrive(plain, 9);
        TCheck(ref, 0, "tiny private stacks");
        assert(rescans > 0 && k.g5PrivOverflow > ov0 && k.g5StateApply > sa0);
        // three faults latch g5Mode 0; the marker stays exact
        k.g5HelperAv = 2;
        g_tAvArm = 1;
        TDrive(plain, 9);
        TCheck(ref, 0, "fault latch");
        assert(k.g5HelperAv == 3 && k.g5Mode == 0);
        g_tAvArm = 0;
        TDrive(plain, 9);
        TCheck(ref, 0, "latched");
        free(ref);
        G5Shutdown();
    }
    // G5Init(0): serial pass-through, nothing created; a later G5Init starts helpers
    {
        G5Init(0, TMarkFrom, &g_tState, &g_tTooSmall);
        assert(!g_g5.n && !g_g5.pool && !g_g5.wake && k.g5Ready == 0);
        TBuild(50000, 77);
        LONG* ref = TSerial();
        const TOpt plain = {20, 0, 0};
        TCheck(ref, TDrive(plain, 1) == 0, "no helpers");
        assert(G5Init(2, TMarkFrom, &g_tState, &g_tTooSmall) == 2);
        TDrive(plain, 1);
        TCheck(ref, 0, "after a second G5Init");
        free(ref);
        G5Shutdown();
    }
    free(g_tOff), free(g_tEdge), free((void*)g_tMark), free((void*)g_tScan);
    g_tOff = g_tEdge = NULL, g_tMark = g_tScan = NULL;
    TestG5Verify();
    g_g5LittleOrig = NULL, g_g5ClearNext = NULL, g_g5GrowOrig = g_g5OvfResume = g_g5OvfPrintStats = 0;
    const LONG ms = UsSince(t0) / 1000;
    memset(&k.g5Mode, 0, 24 * sizeof(LONG));
    printf("gc mark helpers: emulated sites (8 lock bts, 2 lock or, 4-thread race), stubs, N=1..4 toy heaps exact through "
           "G5Little (I1, reset, mode flip, fault, latch, overflow deferral, join), verify (equal, lost bit, missed object, time limit) in %ld ms\n", ms);
}
