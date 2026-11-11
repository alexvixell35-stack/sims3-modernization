// TestStreamBuf ([StreamBuf] streambuf.cpp, docs/547). Included by selftest.cpp. The stubs run against a fake allocator
// (a vtable of its own over malloc / free) and fake SharedPointer objects; the pool's blocks are real VirtualAlloc
// blocks. The patch test runs patched copies of the game's SharedPointer constructor and Release bytes (they call only
// through the fake vtables); MemoryStream::Realloc's copy is patched and compared, not run (it calls game code).
struct StbtAlloc { void** vt; LONG allocs, frees, live; const char* lastName; };
static void* __fastcall StbtA3(void* self, void*, DWORD size, const char* name, DWORD flags) {
    StbtAlloc* a = (StbtAlloc*)self;
    assert(flags == 0);
    InterlockedIncrement(&a->allocs), InterlockedIncrement(&a->live), a->lastName = name;
    return malloc(size);
}
static void __fastcall StbtFr(void* self, void*, void* p, DWORD size) {
    StbtAlloc* a = (StbtAlloc*)self;
    assert(size == 0);
    InterlockedIncrement(&a->frees), InterlockedDecrement(&a->live);
    free(p);
}
static void* g_stbtAVt[4] = {NULL, NULL, (void*)&StbtA3, (void*)&StbtFr};
static StbtAlloc g_stbtA = {g_stbtAVt, 0, 0, 0, NULL};
struct StbtSp { void** vt; void* alloc; u8* data; LONG ref; u8 freeData; };  // as the game's SharedPointer (0x14 bytes)
static int g_stbtSpDeletes;
static void* __fastcall StbtSpDtor(void* self, void*, DWORD flags) { assert(flags == 1); g_stbtSpDeletes++; return self; }
static void* g_stbtSpVt[1] = {(void*)&StbtSpDtor};
static u8 g_stbtStream[0x28];  // a fake MemoryStream: +4 SharedPointer*, +0x14 capacity
static DWORD g_stbtNow, g_stbtWho;
static DWORD StbtNow() { return g_stbtNow; }
static DWORD StbtWhoFn(const DWORD*, const DWORD*, DWORD* via, DWORD* how) { return *via = g_stbtWho ? g_stbtWho + 0x100 : 0, *how = g_stbtWho ? 1 : 0, g_stbtWho; }

static u8* StbtGet(DWORD n) { return (u8*)StbAlloc(n, g_stbtStream, &g_stbtA, "t"); }
static void StbtPut(u8* p) {
    StbtSp sp = {g_stbtSpVt, &g_stbtA, p, 1, 1};
    StbFree(&sp);
}
// Empty pool (every block must have been freed), counters 0, the given switches and sizes.
static void StbtReset(LONG arm, DWORD budgetMB, DWORD minKB, DWORD maxBlkMB, DWORD idleMs) {
    InterlockedExchange(&g_stbArm, 0);
    StbTick(0);
    assert(g_stb.ownedB == 0 && g_stb.idleB == 0 && g_stb.inUseB == 0 && g_stbHi == 0);
    for (LONG w : g_stbOwn) assert(w == 0);
    memset(&g_stb, 0, sizeof(g_stb)), memset(g_stbWho, 0, sizeof(g_stbWho)), memset(g_stbTid, 0, sizeof(g_stbTid)), memset(g_stbSz, 0, sizeof(g_stbSz));
    g_stbBudget = budgetMB << 20, g_stbMin = minKB << 10, g_stbMaxBlock = maxBlkMB << 20, g_stbIdleMs = idleMs;
    InterlockedExchange(&g_stbArm, arm);
}

static void StbtPolicy() {
    assert(StbCap(1) == 0x10000 && StbCap(0x10000) == 0x10000 && StbCap(0x10001) == 0x20000 && StbCap(1398384) == 0x160000 && StbCap(699320) == 0xB0000);
    assert(StbFits(0x160000, 1398384) && StbFits(0xB0000, 699320));        // the block the request itself would make
    assert(!StbFits(0x160000, 699320) && !StbFits(0xB0000, 1398384));      // a 1408 KB block never serves 704 KB, nor the reverse
    assert(StbFits(0x160000, 0x130000) && !StbFits(0x160000, 0x110000));   // at most a quarter of the request to spare
    assert(StbFits(0x20000, 0x10001) && !StbFits(0x30000, 0x10001) && !StbFits(0x10000, 0x10001));
    g_stbMin = 128u << 10, g_stbMaxBlock = 8u << 20, g_stbBudget = 48u << 20;
    assert(!StbPoolable((128u << 10) - 1) && StbPoolable(128u << 10) && StbPoolable(8u << 20) && !StbPoolable((8u << 20) + 1));
    g_stbBudget = 1u << 20;
    assert(StbPoolable(1u << 20) && !StbPoolable((1u << 20) + 1));  // never more than the whole pool
    g_stbBudget = 48u << 20;
    assert(StbSzIx(0) == 0 && StbSzIx(4095) == 0 && StbSzIx(4096) == 1 && StbSzIx(1398384) == 9 && StbSzIx((16u << 20) - 1) == 12 && StbSzIx(16u << 20) == 13 && StbSzIx(0xFFFFFFFF) == 13);
    // owner bitmap: one bit per 64 KB, only for an aligned pointer
    assert(!StbOwned((void*)(uintptr_t)0x12340000));
    StbOwn((void*)(uintptr_t)0x12340000, true);
    assert(StbOwned((void*)(uintptr_t)0x12340000) && !StbOwned((void*)(uintptr_t)0x12340010) && !StbOwned((void*)(uintptr_t)0x12350000) && !StbOwned((void*)(uintptr_t)0x12330000));
    StbOwn((void*)(uintptr_t)0x12340000, false);
    assert(!StbOwned((void*)(uintptr_t)0x12340000));
    // verify helpers
    u8 b[64];
    memset(b, 0xB5, sizeof(b));
    assert(StbAllIs(b, 64, 0xB5) && StbAllIs(b, 61, 0xB5) && !StbAllIs(b, 64, 0));
    b[60] = 0;
    assert(!StbAllIs(b, 64, 0xB5) && StbAllIs(b, 60, 0xB5));
    memset(b, STB_PAT, sizeof(b));
    DWORD first = 99;
    assert(StbUnwritten(b, 64, &first) == 64 && first == 0);
    memset(b, 1, 24), b[40] = 2;
    assert(StbUnwritten(b, 64, &first) == 32 && first == 24);  // groups at 24, 32 (not 40: one byte written), 48, 56
    memset(b, 1, sizeof(b));
    assert(StbUnwritten(b, 64, &first) == 0);
}

// StbCreator on a stack laid out as the byte-checked code leaves it.
static void StbtCreator() {
    static u8 text[64];
    memset(text, 0x90, sizeof(text));
    text[27] = 0xE8;  // a call before text+32
    const DWORD lo = g_asTextLo, hi = g_asTextHi, ret = (DWORD)(uintptr_t)text + 32;
    g_asTextLo = (DWORD)(uintptr_t)text, g_asTextHi = (DWORD)(uintptr_t)text + sizeof(text);
    DWORD a[100] = {}, via, how;
    const bool frames = g_stbFrames;
    g_stbFrames = true;
    a[5] = STB_RET_CTOR, a[13] = STB_RET_SETSIZE, a[17] = 0x0072A2B4, a[19] = (DWORD)(uintptr_t)text + 20 /* no call before */, a[22] = ret;
    assert(StbCreator(a, a + 100, &via, &how) == 0x0072A2B4 && how == 1 && via == ret);
    a[13] = STB_RET_SETPOS;
    assert(StbCreator(a, a + 100, &via, &how) == 0x0072A2B4 && how == 3);
    a[13] = STB_RET_WRITE, a[21] = 0x007C9725;
    assert(StbCreator(a, a + 100, &via, &how) == 0x007C9725 && how == 2 && via == ret);
    a[13] = 0x00401234;  // Realloc called from somewhere else
    assert(StbCreator(a, a + 100, &via, &how) == 0 && how == 0 && via == 0);
    a[13] = STB_RET_SETSIZE, a[5] = 0x00401234;  // not the constructor's only caller
    assert(StbCreator(a, a + 100, &via, &how) == 0);
    a[5] = STB_RET_CTOR;
    assert(StbCreator(a, a + 24, &via, &how) == 0);  // the stack ends first
    g_stbFrames = false;
    assert(StbCreator(a, a + 100, &via, &how) == 0);
    g_stbFrames = frames, g_asTextLo = lo, g_asTextHi = hi;
}

static void StbtBasic() {
    const DWORD T5 = 1398384, T1 = 699320;  // 1024x1024 DXT5 / DXT1 with mips + DDS header
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
    g_stbtNow = 1000, g_stbtWho = 0x0072A2B4;
    memset(g_stbtStream, 0, sizeof(g_stbtStream));
    StbtReset(STB_POOL | STB_ZERO | STB_STATS, 4, 128, 2, 5000);
    u8* p1 = StbtGet(T5);
    assert(p1 && StbOwned(p1) && StbAllIs(p1, T5, 0) && g_stb.fresh == 1 && g_stb.served == 0 && g_stb.ownedB == 0x160000 && g_stb.inUseB == 0x160000 && g_stbtA.allocs == 0);
    memset(p1, 0xEE, T5);
    g_stbtNow += 18;
    StbtPut(p1);
    assert(g_stb.idleB == 0x160000 && g_stb.inUseB == 0 && g_stb.frees == 1 && g_stbtA.frees == 0 && StbOwned(p1) && p1[0] == 0xEE);
    u8* p2 = StbtGet(T5);  // the same block again, zero-filled over the request
    assert(p2 == p1 && StbAllIs(p2, T5, 0) && g_stb.served == 1 && g_stb.servedB == T5 && g_stb.zeroB == T5 && g_stb.idleB == 0);
    u8* p3 = StbtGet(T1);
    StbtPut(p2);
    u8* p4 = StbtGet(T1);  // the idle 1408 KB block does not serve a 704 KB request
    assert(p3 && p4 && p4 != p2 && p3 != p4 && g_stb.fresh == 3 && g_stb.ownedB == 0x160000 + 2 * 0xB0000 && g_stb.idleB == 0x160000);
    u8* s = StbtGet(1000);  // small: the game's allocator, the game's free
    assert(s && !StbOwned(s) && g_stbtA.allocs == 1 && !strcmp(g_stbtA.lastName, "t"));
    StbtPut(s);
    assert(g_stbtA.frees == 1 && g_stb.frees == 3 - 1);  // small blocks are not tracked: frees counts 128 KB+ only
    u8* big = StbtGet(3u << 20);  // over MaxBlockMB: the game's path, tracked for Stats
    assert(big && !StbOwned(big) && g_stb.tooBig == 1 && g_stbtA.allocs == 2 && g_stb.liveN == 3);
    StbtPut(big);
    assert(g_stbtA.frees == 2 && g_stb.liveN == 2);
    u8* p5 = StbtGet(T5);
    assert(p5 == p2 && g_stb.served == 2);
    u8* f = StbtGet(T5);  // 2.75 MB owned + 1.375 MB > 4 MB and nothing idle: the game's path
    assert(f && !StbOwned(f) && g_stb.fallback == 1 && g_stbtA.allocs == 3);
    StbtPut(f);
    StbtPut(p3);
    u8* p6 = StbtGet(T5);  // the idle 704 KB block is released to make room
    assert(p6 && StbOwned(p6) && !StbOwned(p3) && g_stb.evicts == 1 && g_stb.fresh == 4 && g_stb.ownedB == 2 * 0x160000 + 0xB0000);
    StbtPut(p6);
    StbtPut(p6);  // freed twice: counted, nothing else happens
    assert(g_stb.dfree == 1 && g_stb.idleB == 0x160000 && g_stbtA.frees == 3);
    // idle release: a block goes IdleSec after its free, a block in use never
    const DWORD freedAt = g_stbtNow;
    g_stbtNow = freedAt + 4999, StbTick(g_stbtNow);
    assert(StbOwned(p6) && g_stb.trims == 0);
    g_stbtNow = freedAt + 5000, StbTick(g_stbtNow);
    assert(!StbOwned(p6) && g_stb.trims == 1 && g_stb.idleB == 0 && g_stb.ownedB == 0x160000 + 0xB0000 && StbOwned(p5) && StbOwned(p4));
    // who row and the grow count (a stream that already has a buffer)
    *(DWORD*)(g_stbtStream + 4) = 1, *(DWORD*)(g_stbtStream + 0x14) = 5000;
    u8* g = StbtGet(9000);
    *(DWORD*)(g_stbtStream + 4) = 0;
    StbtPut(g);
    assert(g_stb.grows == 1 && g_stb.growB == 5000 && g_stbWho[1].at == 0x0072A2B4 && g_stbWho[1].via == 0x0072A3B4 && g_stbWho[1].how == 1 && g_stbWho[1].grows == 1);
    assert(g_stbWho[1].n == g_stb.reqs && g_stbWho[1].maxReq == 3u << 20 && g_stbWho[1].liveN == 2 && g_stbWho[1].frees == g_stb.frees && g_stbWho[1].peakB >= 2 * T5);
    assert(g_stbSz[StbSzIx(T5)] == 5 && g_stbSz[0] == 1 && g_stbTid[1].tid == GetCurrentThreadId() && g_stbTid[1].allocs == g_stb.big && g_stb.xfree == 0);
    assert(g_stbWho[1].lifeMs >= 18 && g_stbWho[1].quick == g_stbWho[1].frees);
    // run-time kill switch: requests go to the game, a pool block still comes home and is released by the tick
    InterlockedExchange(&g_stbArm, 0);
    u8* k = StbtGet(T5);
    assert(k && !StbOwned(k) && g_stbtA.live == 1);
    StbtPut(k);
    StbtPut(p5);
    assert(g_stbtA.live == 0 && g_stb.idleB == 0x160000);
    StbTick(g_stbtNow);
    assert(!StbOwned(p5) && g_stb.ownedB == 0xB0000);
    // process exit: a free of a pool block takes no lock and does nothing
    InterlockedExchange(&g_stbDetached, 1);
    StbtPut(p4);
    assert(g_stb.inUseB == 0xB0000 && StbOwned(p4));
    InterlockedExchange(&g_stbDetached, 0);
    StbtPut(p4);

    // Verify 1: a freed block is poisoned; a write after the free is seen at the reuse; the block is handed out zeroed
    StbtReset(STB_POOL | STB_ZERO | STB_VERIFY, 4, 128, 2, 5000);
    u8* v = StbtGet(T5);
    memset(v, 0x11, T5);
    StbtPut(v);
    assert(StbAllIs(v, 0x160000, STB_POISON));
    assert(StbtGet(T5) == v && StbAllIs(v, T5, 0) && g_stb.uafw == 0);
    StbtPut(v);
    v[0x1234] = 7;  // use after free
    assert(StbtGet(T5) == v && g_stb.uafw == 1 && StbAllIs(v, T5, 0));
    StbtPut(v);
    // Verify 2: handed out as the pattern; what was never written is counted at the free
    StbtReset(STB_POOL | STB_ZERO | STB_VERIFY | STB_VERIFY2, 4, 128, 2, 5000);
    v = StbtGet(T5);
    assert(StbAllIs(v, T5, STB_PAT));
    memset(v, 0x22, T5 / 2);  // a consumer that fills only half
    StbtPut(v);
    assert(g_stb.unwrittenN == 1 && g_stb.unwrittenB >= T5 / 2 - 16 && g_stb.unwrittenB <= T5 / 2 + 8);
    assert(StbtGet(T5) == v && StbAllIs(v, T5, STB_PAT) && g_stb.uafw == 0);
    memset(v, 0x22, T5);
    StbtPut(v);
    assert(g_stb.unwrittenN == 1);
    // Stats alone: nothing pooled, lifetimes of the game's own 128 KB+ blocks
    StbtReset(STB_STATS, 4, 128, 2, 5000);
    u8* e = StbtGet(T5);
    assert(e && !StbOwned(e) && g_stb.liveN == 1 && g_stb.ownedB == 0 && g_stbWho[1].liveB == T5);
    g_stbtNow += 1500;
    StbtPut(e);
    assert(g_stb.liveN == 0 && g_stb.frees == 1 && g_stbWho[1].lifeMs == 1500 && g_stbWho[1].quick == 0 && g_stbHi == 0);
    g_stbInstalled = 1, StbMemLine(1, "selftest"), g_stbInstalled = 0;  // formats every line (no log file here)
    StbtReset(0, 48, 128, 8, 5000);
    assert(g_stbtA.live == 0);
}

// Randomized run against the policy written the plain way. Fixed seed.
struct StbtLive { u8* p; DWORD n; bool pooled; u8 sig; };
struct StbtIdle { u8* p; DWORD cap, t; };
static void StbtModel() {
    static StbtLive live[600];
    static StbtIdle idle[600];
    DWORD liveN = 0, idleN = 0, owned = 0, seed = 0x5B0001;
    auto rnd = [&seed]() { seed = seed * 1664525u + 1013904223u; return seed >> 8; };
    static const DWORD kSizes[6] = {1398384, 699320, 131072, 300000, 349680, 2796344};
    StbtReset(STB_POOL | STB_ZERO, 12, 128, 2, 2000);
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
    DWORD served = 0, fresh = 0, fallback = 0, trims = 0, evicts = 0, gameLive = 0;
    for (int step = 0; step < 6000; step++) {
        g_stbtNow += 1 + rnd() % 300;
        const DWORD op = step % 1000 < 60 ? 0 : rnd() % 100;  // a burst of requests every 1000 steps fills the pool
        if (op < (liveN < 10 ? 60u : 35u) && liveN < 600) {  // about ten buffers live between the bursts
            const DWORD r = rnd() % 10, n = r < 6 ? kSizes[rnd() % 6] : r < 8 ? 65536 + rnd() % (3u << 20) : 1 + rnd() % 100000;
            // the model's answer
            int pick = -1;
            bool expFresh = false;
            if (StbPoolable(n)) {
                for (DWORD i = 0; i < idleN; i++)
                    if (StbFits(idle[i].cap, n) && (pick < 0 || idle[i].cap < idle[pick].cap || (idle[i].cap == idle[pick].cap && idle[i].t > idle[pick].t))) pick = (int)i;
                if (pick < 0) {
                    const DWORD cap = StbCap(n);
                    while (owned + cap > g_stbBudget && idleN) {
                        DWORD old = 0;
                        for (DWORD i = 1; i < idleN; i++)
                            if (idle[i].t < idle[old].t) old = i;
                        assert(StbOwned(idle[old].p));
                        owned -= idle[old].cap, idle[old] = idle[--idleN], evicts++;
                    }
                    if (owned + cap <= g_stbBudget) expFresh = true, owned += cap, fresh++; else fallback++;
                }
            }
            u8* const p = StbtGet(n);
            assert(p);
            if (pick >= 0) assert(p == idle[pick].p), idle[pick] = idle[--idleN], served++;
            else if (expFresh) {
                assert(StbOwned(p));
                for (DWORD i = 0; i < liveN; i++) assert(live[i].p != p);
                for (DWORD i = 0; i < idleN; i++) assert(idle[i].p != p);
            } else assert(!StbOwned(p)), gameLive++;
            const bool pooled = pick >= 0 || expFresh;
            if (pooled) assert(StbAllIs(p, n, 0));  // a pooled block always starts as zeros (Zero=1)
            const u8 sig = (u8)(1 + rnd() % 250);
            memset(p, sig, n);
            live[liveN++] = StbtLive{p, n, pooled, sig};
        } else if (op < 94 && liveN) {
            const DWORD i = rnd() % liveN;
            const StbtLive l = live[i];
            assert(StbAllIs(l.p, l.n, l.sig));  // nobody else was given these bytes
            live[i] = live[--liveN];
            StbtPut(l.p);
            if (l.pooled) idle[idleN++] = StbtIdle{l.p, StbCap(l.n) /* replaced below */, g_stbtNow};
            else gameLive--;
            if (l.pooled) {  // the block keeps the size it was made with, which may be larger than this request's own
                DWORD cap = 0;
                for (DWORD k = 0; k < g_stbHi; k++)
                    if (g_stbBlk[k].p == l.p) cap = g_stbBlk[k].cap;
                assert(cap && StbFits(cap, l.n));
                idle[idleN - 1].cap = cap;
            }
        } else {
            StbTick(g_stbtNow);
            for (DWORD i = 0; i < idleN;)
                if (g_stbtNow - idle[i].t >= 2000) { assert(!StbOwned(idle[i].p)); owned -= idle[i].cap, idle[i] = idle[--idleN], trims++; }
                else assert(StbOwned(idle[i].p)), i++;
        }
        DWORD idleB = 0;
        for (DWORD i = 0; i < idleN; i++) idleB += idle[i].cap;
        assert(g_stb.ownedB == owned && g_stb.idleB == idleB && g_stb.inUseB == owned - idleB && owned <= g_stbBudget);
        assert(g_stb.served == served && g_stb.fresh == fresh && g_stb.fallback == fallback && g_stb.trims == trims && g_stb.evicts == evicts);
        assert((DWORD)g_stbtA.live == gameLive && !g_stb.dfree && !g_stb.lost && !g_stb.vaFail && !g_stb.uafw);
    }
    printf("streambuf model: 6000 steps, %lu reused, %lu new blocks, %lu to the game (pool full), %lu idle releases, %lu evictions\n", served, fresh, fallback, trims, evicts);
    assert(served > 500 && fresh > 100 && fallback > 10 && trims > 10 && evicts > 10);  // every path was taken
    while (liveN) liveN--, assert(StbAllIs(live[liveN].p, live[liveN].n, live[liveN].sig)), StbtPut(live[liveN].p);
    StbtReset(0, 48, 128, 8, 5000);  // asserts: nothing owned, every owner bit clear
    assert(g_stbtA.live == 0 && g_stbtA.allocs == g_stbtA.frees);
}

// Four threads allocate, fill, check and free; blocks are handed to the next thread's free through a shared slot.
static u8* volatile g_stbtHand[4];
static DWORD WINAPI StbtWorker(void* arg) {
    const DWORD me = (DWORD)(uintptr_t)arg;
    DWORD seed = 77 + me;
    for (int i = 0; i < 3000; i++) {
        seed = seed * 1664525u + 1013904223u;
        const DWORD n = (seed >> 9) % 3 ? 699320 : 1398384;
        u8* p = StbtGet(n);
        assert(p && (!StbOwned(p) || StbAllIs(p, 64, 0)));
        memset(p, (int)(me + 1), 4096);
        p = (u8*)InterlockedExchangePointer((void* volatile*)&g_stbtHand[(me + 1) & 3], p);  // the neighbour frees ours
        if (p) StbtPut(p);
    }
    return 0;
}
static void StbtThreads() {
    StbtReset(STB_POOL | STB_ZERO | STB_STATS, 16, 128, 2, 5000);
    g_stbNow = StbNowMs;
    HANDLE h[4];
    for (DWORD i = 0; i < 4; i++) h[i] = CreateThread(NULL, 0, StbtWorker, (void*)(uintptr_t)i, 0, NULL);
    WaitForMultipleObjects(4, h, TRUE, INFINITE);
    for (DWORD i = 0; i < 4; i++) {
        CloseHandle(h[i]);
        if (g_stbtHand[i]) StbtPut(g_stbtHand[i]), g_stbtHand[i] = NULL;
    }
    g_stbNow = StbtNow;
    assert(g_stb.inUseB == 0 && g_stb.liveN == 0 && !g_stb.dfree && !g_stb.lost && g_stb.reqs == 12000 && g_stb.frees == 12000 && g_stb.xfree > 0);
    assert(g_stb.served + g_stb.fresh + g_stb.fallback == 12000 && g_stb.served > 10000 && g_stb.peakInUseB <= 16u << 20);
    StbtReset(0, 48, 128, 8, 5000);
    assert(g_stbtA.live == 0);
}

// Calls a copy of game code: thiscall with up to three stack arguments and ebx set, as Realloc calls the constructor.
static void* StbtCall(void* a_fn, void* a_this, DWORD a1, void* a2, const void* a3, void* a_bx, int a_n) {
    void* r;
    DWORD e0, e1;
    __asm {
        push ebx
        mov e0, esp
        mov ebx, a_bx
        cmp a_n, 0
        je go
        push a3
        push a2
        push a1
    go:
        mov ecx, a_this
        call a_fn
        mov r, eax
        mov e1, esp
        pop ebx
    }
    assert(e0 == e1);  // the patched code left the stack as the original does
    return r;
}
static void StbtPatch() {
    u8* x = (u8*)VirtualAlloc(NULL, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    assert(x);
    u8 *ctor = x, *rel = x + 0x100, *re = x + 0x200;
    memcpy(ctor, kStbCtor, sizeof(kStbCtor)), memcpy(rel, kStbRel, sizeof(kStbRel)), memcpy(re, kStbRealloc, sizeof(kStbRealloc));
    // a foreign byte anywhere in a window: nothing is written
    re[0x10] ^= 1;
    assert(!StbInstallSites(ctor, rel, re, kStbSetSize, kStbWrite, kStbSetPos) && !memcmp(ctor, kStbCtor, sizeof(kStbCtor)) && !memcmp(rel, kStbRel, sizeof(kStbRel)));
    re[0x10] ^= 1, ctor[0x30] ^= 1;
    assert(!StbInstallSites(ctor, rel, re, kStbSetSize, kStbWrite, kStbSetPos) && !memcmp(rel, kStbRel, sizeof(kStbRel)) && !memcmp(re, kStbRealloc, sizeof(kStbRealloc)));
    ctor[0x30] ^= 1;
    assert(!g_stbPatch[0].at && !g_stbPatch[2].at);
    assert(StbInstallSites(ctor, rel, re, kStbSetSize, kStbWrite, kStbSetPos) && g_stbFrames);
    // only the three patch ranges changed, and they hold what the header says
    static const u8 kCtorNew[8] = {0x51, 0x50, 0x53, 0xFF, 0x74, 0x24, 0x14, 0xE8};
    assert(!memcmp(ctor, kStbCtor, STB_CTOR_AT) && !memcmp(ctor + STB_CTOR_AT, kCtorNew, 8) && !memcmp(ctor + STB_CTOR_AT + 12, kStbCtor + STB_CTOR_AT + 12, sizeof(kStbCtor) - STB_CTOR_AT - 12));
    assert((uintptr_t)ctor + STB_CTOR_AT + 12 + *(LONG*)(ctor + STB_CTOR_AT + 8) == (uintptr_t)&StbAlloc);
    for (int i = 0; i < 2; i++) {
        u8* w = i ? re : rel;
        const u8* orig = i ? kStbRealloc : kStbRel;
        const DWORD at = i ? STB_REALLOC_AT : STB_REL_AT, n = i ? sizeof(kStbRealloc) : sizeof(kStbRel);
        assert(!memcmp(w, orig, at) && w[at] == 0x56 && w[at + 1] == 0xE8 && w[at + 6] == 0xEB && w[at + 7] == 8 && !memcmp(w + at + 16, orig + at + 16, n - at - 16));
        assert((uintptr_t)w + at + 6 + *(LONG*)(w + at + 2) == (uintptr_t)&StbFree);
    }
    assert(STB_CTOR + STB_CTOR_AT + 12 == 0x004DEC17 && STB_REL + STB_REL_AT == 0x004DEA99 && STB_REALLOC + STB_REALLOC_AT == 0x004DEEF9);
    // the patched constructor: a pooled buffer, the fields as the game's code leaves them
    StbtReset(STB_POOL | STB_ZERO, 48, 128, 8, 5000);
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0, g_stbtSpDeletes = 0;
    StbtSp sp = {};
    assert(StbtCall(ctor, &sp, 1398384, &g_stbtA, "name", g_stbtStream, 3) == &sp);
    assert((DWORD)(uintptr_t)sp.vt == 0x00FB84D0 && sp.alloc == &g_stbtA && sp.data && StbOwned(sp.data) && sp.ref == 0 && sp.freeData == 1 && g_stb.fresh == 1);
    u8* const d = sp.data;
    // the patched Release: a reference left = only the count drops; the last one = the buffer to the pool, then the object's destructor
    sp.vt = g_stbtSpVt, sp.ref = 2;
    StbtCall(rel, &sp, 0, NULL, NULL, NULL, 0);
    assert(sp.ref == 1 && g_stb.frees == 0 && g_stbtSpDeletes == 0);
    assert(StbtCall(rel, &sp, 0, NULL, NULL, NULL, 0) == NULL && g_stb.frees == 1 && g_stb.idleB == 0x160000 && g_stbtSpDeletes == 1 && g_stbtA.frees == 0);
    // a small buffer through the same code: the game's allocator with the stream's name, the game's free
    StbtSp sp2 = {};
    StbtCall(ctor, &sp2, 4000, &g_stbtA, "small", g_stbtStream, 3);
    assert(sp2.data && !StbOwned(sp2.data) && g_stbtA.allocs == 1 && !strcmp(g_stbtA.lastName, "small") && sp2.freeData == 1);
    sp2.vt = g_stbtSpVt, sp2.ref = 1;
    StbtCall(rel, &sp2, 0, NULL, NULL, NULL, 0);
    assert(g_stbtA.frees == 1 && g_stbtSpDeletes == 2);
    // "free the data" byte clear (a buffer the stream does not own): no free at all
    StbtSp sp3 = {g_stbtSpVt, &g_stbtA, d, 1, 0};
    StbtCall(rel, &sp3, 0, NULL, NULL, NULL, 0);
    assert(g_stbtA.frees == 1 && g_stb.dfree == 0 && g_stbtSpDeletes == 3);
    // the watch: a site another module rewrote stops the module
    const int mask = STB_POOL | STB_ZERO;
    StbWatch(mask | STB_STATS);
    assert(g_stbArm == (mask | STB_STATS) && !g_stbDead);
    rel[STB_REL_AT + 3] ^= 0x40;
    StbWatch(mask);
    assert(g_stbDead && g_stbArm == 0);
    StbWatch(mask);
    assert(g_stbArm == 0);
    g_stbDead = false, g_stbFrames = false;
    memset(g_stbPatch, 0, sizeof(g_stbPatch));
    StbtReset(0, 48, 128, 8, 5000);
    VirtualFree(x, 0, MEM_RELEASE);
}

// Per request, with the block written once from end to end as a decoder or a read fills it: the game's path today (one
// VirtualAlloc + release per buffer; EA's direct block asks for the request + 0x27 rounded to pages) against the pool.
static double StbtBenchOne(DWORD n, int mode, const u8* src, int iters) {
    StbtReset(mode == 1 ? STB_POOL | STB_ZERO : mode == 2 ? STB_POOL : 0, 48, 128, 8, 5000);
    const LONGLONG t0 = StbQpc();
    for (int i = 0; i < iters; i++) {
        if (!mode) {
            u8* p = (u8*)VirtualAlloc(NULL, (n + 0x27 + 0xFFF) & ~0xFFFu, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
            if (src) memcpy(p + 0x10, src, n);
            VirtualFree(p, 0, MEM_RELEASE);
        } else {
            u8* p = StbtGet(n);
            if (src) memcpy(p, src, n);
            StbtPut(p);
        }
    }
    const double us = (double)(StbQpc() - t0) * 1e6 / (double)g_stbQpf / iters;
    if (mode) assert(g_stb.served == (u64)iters - 1 && g_stb.fresh == 1);
    return us;
}
static void StbtBench() {
    g_stbNow = StbNowMs;
    static const DWORD kN[2] = {1398384, 699320};
    for (DWORD n : kN) {
        u8* src = (u8*)malloc(n);
        for (DWORD i = 0; i < n; i++) src[i] = (u8)(i * 31 + (i >> 8));
        double us[3][2];
        for (int mode = 0; mode < 3; mode++) us[mode][0] = StbtBenchOne(n, mode, src, 300), us[mode][1] = StbtBenchOne(n, mode, NULL, 300);
        printf("bench streambuf %lu KB x300, us per buffer (filled once / not touched): game path %.1f / %.1f; pool Zero=1 %.1f / %.1f; pool Zero=0 %.1f / %.1f\n",
               n >> 10, us[0][0], us[0][1], us[1][0], us[1][1], us[2][0], us[2][1]);
        free(src);
    }
    StbtReset(0, 48, 128, 8, 5000);
    g_stbNow = StbtNow;
}

static void TestStreamBuf() {
    g_stbNow = StbtNow, g_stbWhoFn = StbtWhoFn;
    // the game's bytes are not in this process: nothing matches, nothing is written
    assert(!StbInstallSites((u8*)(uintptr_t)STB_CTOR, (u8*)(uintptr_t)STB_REL, (u8*)(uintptr_t)STB_REALLOC, (const u8*)(uintptr_t)STB_SETSIZE, (const u8*)(uintptr_t)STB_WRITE,
                           (const u8*)(uintptr_t)STB_SETPOS));
    // not armed: both stubs are the game's calls
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
    u8* p = StbtGet(1398384);
    assert(p && !StbOwned(p) && g_stbtA.allocs == 1 && g_stb.reqs == 0);
    StbtPut(p);
    assert(g_stbtA.frees == 1);
    StbtPolicy();
    StbtCreator();
    StbtBasic();
    StbtModel();
    StbtThreads();
    StbtPatch();
    StbtBench();
    g_stbNow = StbNowMs, g_stbWhoFn = StbCreator;
    printf("streambuf ok\n");
}
