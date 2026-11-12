// TestMemExp (memexp.cpp, docs/199): [MemDiag] TypeGrowth and [GC] SqrtHeapLimit on a fake Boehm heap. Included by
// selftest.cpp. Checks the block walk (bottom index list, free and continuation entries), marked bytes, the class census
// (vtable -> klass accepted only through the klass's runtime info), Cork ratio ranking, the points-from walk, the
// square-root limit and the collect_or_expand decision.
static int g_mxtExpands, g_mxtOrigCalls;
static DWORD g_mxtHeap;
static int __cdecl MxtExpand(DWORD blocks) { g_mxtExpands++, g_mxtHeap += blocks * 4096; return 1; }
static int __cdecl MxtCoeOrig(DWORD, int) { g_mxtOrigCalls++; return 1; }

// cost of one pass on a game-sized fake heap: 6,000 blocks of 32-byte objects (768k live), 4,000 classes, 30% of the
// objects with a non-vtable first word spread over the whole 4 GB (the in-game census paid one VirtualQuery per such
// page, 2-8 s per pass). Target <= ~50 ms per pass without the graph (docs/199 §7.1).
static void TestMemExpBench() {
    const DWORD NB = 6000, NC = 4000, CH = 4u << 20;
    u8* raw = (u8*)VirtualAlloc(NULL, NB * 4096 + CH * 2, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    assert(raw);
    const DWORD base = ((DWORD)(uintptr_t)raw + CH - 1) & ~(CH - 1);
    const DWORD nbi = (NB * 4096 + CH - 1) / CH;
    static DWORD top[1024], invalid = 0xDEAD0000, gcNo = 1, hs, w, bf, incr = 1, dg;
    static u8 map8[4096];
    for (int i = 0; i < 4096; i++) map8[i] = (u8)((i >> 2) % 8);
    DWORD* bis = (DWORD*)VirtualAlloc(NULL, nbi * 0x2000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    DWORD* hdr = (DWORD*)VirtualAlloc(NULL, NB * 0xA0, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    DWORD* cl = (DWORD*)VirtualAlloc(NULL, NC * 0x100, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    assert(bis && hdr && cl);
    memset(top, 0, sizeof(top));
    static DWORD head;
    head = 0;
    for (DWORD b = nbi; b-- > 0;) {
        DWORD* bi = bis + b * 0x800;
        bi[0x1000 / 4] = head, bi[0x1008 / 4] = (base >> 22) + b, head = (DWORD)(uintptr_t)bi;
        top[(base >> 22) + b] = (DWORD)(uintptr_t)bi;
    }
    for (DWORD c = 0; c < NC; c++) {  // klass +0xB8 -> runtime info (+0 max domain 0, +4 vtable), vtable +0 -> klass
        DWORD* k = cl + c * 0x40;
        k[0xB8 / 4] = (DWORD)(uintptr_t)(k + 0x30), k[0x30] = 0, k[0x31] = (DWORD)(uintptr_t)(k + 0x38), k[0x38] = (DWORD)(uintptr_t)k;
    }
    DWORD rng = 12345;
    auto rnd = [&]() { rng = rng * 1664525u + 1013904223u; return rng; };
    for (DWORD b = 0; b < NB; b++) {
        DWORD* h = hdr + b * 0x28;
        const DWORD blk = base + b * 4096;
        h[0] = 8, h[3] = 0x10, h[4] = (DWORD)(uintptr_t)map8, ((u8*)h)[0x14] = 1;
        for (int m = 0; m < 32; m++) h[7 + m] = 0x01010101u;  // every 8-word object start marked
        ((DWORD*)(uintptr_t)top[blk >> 22])[(blk >> 12) & 0x3FF] = (DWORD)(uintptr_t)h;
        DWORD* o = (DWORD*)(uintptr_t)blk;
        for (int i = 0; i < 1024; i += 8) {
            const DWORD r = rnd();
            o[i] = r % 10 < 3 ? (rnd() & ~3u) : (DWORD)(uintptr_t)(cl + (r >> 8) % NC * 0x40 + 0x38);
            for (int j = 1; j < 8; j++) o[i + j] = j & 1 ? base + (rnd() % (NB * 128)) * 32 : rnd();
        }
    }
    MxState* st = (MxState*)VirtualAlloc(NULL, sizeof(MxState), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    u8* pg = (u8*)VirtualAlloc(NULL, 1 << 18, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    const MxAddrs saved = g_mx;
    MxState* savedS = g_mxs;
    u8* savedP = g_mxPage;
    g_mx = {top, &head, &invalid, &gcNo, &hs, &w, &bf, &incr, &dg};
    g_mxs = st, g_mxPage = pg;
    double cen[3], gr = 0;
    LONG q = 0;
    for (int pass = 0; pass < 3; pass++) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        const LONG q0 = g_mxQueries;
        MxCensus();
        cen[pass] = UsSince(t0) / 1000.0, q = g_mxQueries - q0;
        for (int i = 0; i < st->nCls; i++) MxRank(st->cls[i], 0.15);
        if (pass == 2) {
            QueryPerformanceCounter(&t0);
            for (int i = 0; i < 8; i++) st->cls[i * 97].last = 0;  // 8 growing classes: the graph has targets
            MxPickTargets(8);
            MxGraph();
            gr = UsSince(t0) / 1000.0;
        }
    }
    printf("mem experiments bench: %llu objects, %d classes, %llu rejects, census %.1f / %.1f / %.1f ms (%ld VirtualQuery), graph %.1f ms\n",
           st->objs, st->nCls, st->rejects, cen[0], cen[1], cen[2], q, gr);
    assert(st->nCls == (int)NC && st->objs > NB * 128 * 6 / 10 && st->nTg == 8);
    assert(q < 5000);         // one query per region, not per page (was ~one per garbage page)
    TIMING_ASSERT("MemExp census < 250 ms", cen[2] < 250.0);  // loose: host noise; the printed figure is the measurement
    g_mx = saved, g_mxs = savedS, g_mxPage = savedP;
    VirtualFree(st, 0, MEM_RELEASE), VirtualFree(pg, 0, MEM_RELEASE), VirtualFree(raw, 0, MEM_RELEASE);
    VirtualFree(bis, 0, MEM_RELEASE), VirtualFree(hdr, 0, MEM_RELEASE), VirtualFree(cl, 0, MEM_RELEASE);
}

static void TestMemExp() {
    static_assert(offsetof(Ts3nCounters, tyMode) == 0x52F0 && offsetof(Ts3nCounters, sqMode) == 0x52F4 &&
                      offsetof(Ts3nCounters, sqForced) == 0x5350 && offsetof(Ts3nCounters, mxSpare) == 0x5354 && sizeof(Ts3nCounters) == 6294 * 4,
                  "counters v137 layout after v136 (docs/registry.md)");
    // fake heap: 16 blocks of 4 KB in one 4 MB bottom index
    u8* heap = (u8*)VirtualAlloc(NULL, 0x10000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    static DWORD top[1024], bottom[0x1010 / 4], hdrs[6][0x48], invalid = 0xDEAD0000, gcNo = 7, heapSize, words, before, incr = 1, dontGc = 0;
    static u8 map4[4096], mapLarge[4096], ns[] = "Sims3.Gameplay", nmA[] = "Foo", nmB[] = "Holder", nmArr[] = "Foo[]";
    static DWORD klassA[0x40], klassB[0x40], klassArr[0x40], riA[2], riB[2], riArr[2], vtA[4], vtB[4], vtArr[4];
    const DWORD base = (DWORD)(uintptr_t)heap;
    memset(top, 0, sizeof(top)), memset(bottom, 0, sizeof(bottom)), memset(hdrs, 0, sizeof(hdrs));
    top[base >> 22] = (DWORD)(uintptr_t)bottom;
    bottom[0x1008 / 4] = base >> 22;  // key
    static DWORD head;
    head = (DWORD)(uintptr_t)bottom;
    const MxAddrs saved = g_mx;
    g_mx = {top, &head, &invalid, &gcNo, &heapSize, &words, &before, &incr, &dontGc};
    auto setHdr = [&](int i, DWORD blkIdx, DWORD szWords, u8 kind, DWORD descr, const u8* map) {
        hdrs[i][0] = szWords, hdrs[i][3] = descr, hdrs[i][4] = (DWORD)(uintptr_t)map;
        ((u8*)hdrs[i])[0x14] = kind;
        bottom[((base >> 12) + blkIdx) & 0x3FF] = (DWORD)(uintptr_t)hdrs[i];
    };
    auto mark = [&](int i, DWORD byteOff) { hdrs[i][7 + (byteOff >> 7)] |= 1u << ((byteOff >> 2) & 31); };
    for (int i = 0; i < 4096; i++) map4[i] = (u8)((i >> 2) % 4), mapLarge[i] = 0xFE;
    // classes: runtime info holds the vtable for domain 0
    auto cls = [&](DWORD* k, DWORD* ri, DWORD* vt, u8* name) {
        k[0x34 / 4] = (DWORD)(uintptr_t)name, k[0x38 / 4] = (DWORD)(uintptr_t)ns, k[0xB8 / 4] = (DWORD)(uintptr_t)ri;
        ri[0] = 0, ri[1] = (DWORD)(uintptr_t)vt, vt[0] = (DWORD)(uintptr_t)k;
    };
    cls(klassA, riA, vtA, nmA), cls(klassB, riB, vtB, nmB), cls(klassArr, riArr, vtArr, nmArr);
    static DWORD fakeVt[4];  // looks like a vtable (klass with runtime info) but the info does not list it
    fakeVt[0] = (DWORD)(uintptr_t)klassA;
    DWORD* blk0 = (DWORD*)heap;              // 16-byte objects, kind 1, descr != 0
    DWORD* blk1 = (DWORD*)(heap + 0x1000);   // free block
    DWORD* blk2 = (DWORD*)(heap + 0x2000);   // large object 0x500 words (2 blocks), kind 1
    DWORD* blk4 = (DWORD*)(heap + 0x4000);   // 16-byte objects, kind 2 (uncollectable), marked
    DWORD* blk5 = (DWORD*)(heap + 0x5000);   // 16-byte objects, pointer-free, kind 0
    setHdr(0, 0, 4, 1, 0x10, map4);
    setHdr(1, 1, 4, 1, 0x10, map4), hdrs[1][4] = invalid;
    setHdr(2, 2, 0x500, 1, 0x10, mapLarge);
    bottom[((base >> 12) + 3) & 0x3FF] = 1;  // continuation of the large object
    setHdr(3, 4, 4, 2, 0x10, map4);
    setHdr(4, 5, 4, 0, 0, map4);
    // objects: A at 0x00, 0x10; B at 0x20 (unmarked), B at 0x30 holding A@0x00 and A@0x10 + 4 (interior: not counted);
    // garbage word0 at 0x40; fake vtable at 0x50
    blk0[0] = (DWORD)(uintptr_t)vtA, blk0[4] = (DWORD)(uintptr_t)vtA, blk0[8] = (DWORD)(uintptr_t)vtB;
    blk0[12] = (DWORD)(uintptr_t)vtB, blk0[13] = base, blk0[14] = base + 0x14, blk0[15] = 0x12345678;
    blk0[16] = 0x00000F00, blk0[20] = (DWORD)(uintptr_t)fakeVt;
    for (DWORD o : {0x00u, 0x10u, 0x30u, 0x40u, 0x50u}) mark(0, o);
    blk2[0] = (DWORD)(uintptr_t)vtArr, blk2[1] = base + 0x10, mark(2, 0);  // array of 0x1400 bytes holding A@0x10
    blk4[0] = (DWORD)(uintptr_t)vtA, mark(3, 0);
    blk5[0] = (DWORD)(uintptr_t)vtA, blk5[1] = base, mark(4, 0);  // pointer-free: counted, never scanned
    (void)blk1;
    int nb = 0;
    MxBlocks([&](DWORD, const DWORD*) { nb++; });
    assert(nb == 4);  // free block and continuation entry skipped
    assert(MxMarkedBytes() == 5 * 16 + 0x1400 + 16 + 16);

    MxState* st = (MxState*)VirtualAlloc(NULL, sizeof(MxState), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    u8* pg = (u8*)VirtualAlloc(NULL, 1 << 18, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    MxState* savedS = g_mxs;
    u8* savedP = g_mxPage;
    g_mxs = st, g_mxPage = pg;
    MxCensus();
    const int ia = MxClassIdx((DWORD)(uintptr_t)klassA), ib = MxClassIdx((DWORD)(uintptr_t)klassB), iarr = MxClassIdx((DWORD)(uintptr_t)klassArr);
    assert(st->nCls == 3);
    assert(st->cls[ia].n == 3 && st->cls[ia].bytes == 48);  // two in blk0, one pointer-free; the uncollectable one not
    assert(st->cls[ib].n == 1 && st->cls[ib].bytes == 16);
    assert(st->cls[iarr].n == 1 && st->cls[iarr].bytes == 0x1400);
    assert(st->rejects == 2 && st->markedBytes == MxMarkedBytes());  // garbage word0 and the unlisted vtable
    assert(st->heapLo == base && st->heapHi == base + 0x6000);
    char nm[96];
    MxName((DWORD)(uintptr_t)klassA, nm, sizeof(nm));
    assert(!strcmp(nm, "Sims3.Gameplay.Foo"));

    // growth of A between passes -> A becomes the graph target; holders: B (16 bytes for A@0x00) and the array (A@0x10)
    blk0[24] = (DWORD)(uintptr_t)vtA, mark(0, 0x60);
    MxCensus();
    for (int i = 0; i < st->nCls; i++) MxRank(st->cls[i], 0.15);
    assert(st->cls[ia].bytes == 64 && st->cls[ia].last == 48 && st->cls[ia].phases == 1);
    MxPickTargets(1);
    assert(st->nTg == 1 && st->tg[0].cls == ia);
    MxGraph();
    const MxTarget& t = st->tg[0];
    u64 fromB = 0, fromArr = 0;
    for (DWORD i = 0; i < t.nh; i++) {
        if (t.h[i].cls == ib) fromB = t.h[i].bytes;
        if (t.h[i].cls == iarr) fromArr = t.h[i].bytes;
    }
    assert(fromB == 16 && fromArr == 16 && t.nh == 2 && t.other == 0);  // interior pointer and pointer-free blk5 not counted

    // Cork ranking: steady growth ranks up, a drop below (1 - f) of the max resets
    {
        MxCls c = {};
        for (u64 v : {100ull, 120ull, 150ull, 200ull}) c.last = c.bytes, c.bytes = v, MxRank(c, 0.15);
        assert(c.phases == 4 && c.rank > 1.0 && c.maxVol == 200);
        c.last = c.bytes, c.bytes = 190, MxRank(c, 0.15);  // small shrink: still a phase, rank falls
        assert(c.phases == 5 && c.rank > 2.0 && c.rank < 2.4);  // 2.48 - 5 * (200 / 190 - 1)
        c.last = c.bytes, c.bytes = 100, MxRank(c, 0.15);
        assert(c.phases == 0 && c.rank == 0 && c.maxVol == 100);
    }

    // square-root rule: L 200 MB, g 10 MB/s, s 1000 MB/s, c 0.001/MB -> E = sqrt(200 * 0.01 / 0.001) = 44.7 MB
    {
        const double MB = 1048576.0;
        const DWORD m = MxSqrtLimit(200 * MB, 10 * MB, 1000 * MB, 0.001, 100u << 20, 1024u << 20, 2u << 20);
        assert(fabs(m / MB - 244.72) < 0.1);
        assert(MxSqrtLimit(200 * MB, 10 * MB, 1000 * MB, 0.001, 400u << 20, 1024u << 20, 2u << 20) == 400u << 20);   // floor
        assert(MxSqrtLimit(900 * MB, 100 * MB, 10 * MB, 0.001, 100u << 20, 1024u << 20, 2u << 20) == 1024u << 20);  // ceiling
        assert(MxSqrtLimit(200 * MB, 0, 1000 * MB, 0.001, 0, 1024u << 20, 16u << 20) == 216u << 20);                // min extra
        assert(MxGrowBlocks(100u << 20, 0, 10, 8192) == 0);
        assert(MxGrowBlocks(100u << 20, 100u << 20, 10, 8192) == 0);
        assert(MxGrowBlocks(100u << 20, (100u << 20) + 4096 * 5, 10, 8192) == 0);  // less room than the allocation needs
        assert(MxGrowBlocks(100u << 20, 200u << 20, 10, 8192) == 8192);            // capped at one step
        assert(MxGrowBlocks(100u << 20, (100u << 20) + 4096 * 100, 10, 8192) == 100);
    }
    // collect_or_expand: under the limit with allocation since the last GC -> grow, else the game's collection
    {
        Ts3nCounters& k = ts3native_counters;
        const MxExpandFn se = g_mxExpand;
        const MxCoeFn so = g_mxCoeOrig;
        const DWORD sl = g_shLimit, ss = g_shStepBlocks;
        const LONG sm = k.sqMode;
        g_mxExpand = MxtExpand, g_mxCoeOrig = MxtCoeOrig, g_shStepBlocks = 8192;
        g_mxtHeap = 300u << 20;
        g_mx.heapSize = &g_mxtHeap, words = 100, k.sqMode = 1, g_shLimit = 400u << 20;
        assert(MxCoe(10, 0) == 1 && g_mxtExpands == 1 && g_mxtOrigCalls == 0 && g_mxtHeap == (332u << 20));
        g_shLimit = 300u << 20;
        assert(MxCoe(10, 0) == 1 && g_mxtExpands == 1 && g_mxtOrigCalls == 1);  // at the limit: forced collection
        g_shLimit = 900u << 20, k.sqMode = 2;
        assert(MxCoe(10, 0) == 1 && g_mxtExpands == 1 && g_mxtOrigCalls == 2);  // observe mode never grows
        k.sqMode = 1, words = 0;
        assert(MxCoe(10, 0) == 1 && g_mxtOrigCalls == 3);  // nothing allocated since the GC: the game's own expand path
        g_mxExpand = se, g_mxCoeOrig = so, g_shLimit = sl, g_shStepBlocks = ss, k.sqMode = sm;
    }
    // one whole pass through the event handler (logs typegrowth / gcsize lines into the selftest log)
    {
        Ts3nCounters& k = ts3native_counters;
        const LONG tm = k.tyMode, sm = k.sqMode;
        k.tyMode = 1, k.sqMode = 2, g_mx.heapSize = &heapSize, heapSize = 0x6000, words = 5000, before = 100000;
        MxOnGc(), MxOnGc();
        assert(k.tyPasses >= 2 && k.sqGcs >= 2 && k.sqLiveKB == (LONG)(MxMarkedBytes() >> 10) && k.tyFaults == 0);
        k.tyMode = tm, k.sqMode = sm;
    }
    // runtime info 0xFFFFFFFF (the in-game fault after load, docs/199 §7.1): rejected, no wrap past 4 GB
    {
        assert(!MxReadable(0xFFFFFFFFu, 4) && !MxReadable(0xFFFFF000u, 0x2000) && !MxReadable(base, 0));
        static DWORD klassBad[0x40], vtBad[4];
        klassBad[0xB8 / 4] = 0xFFFFFFFFu, vtBad[0] = (DWORD)(uintptr_t)klassBad;
        memset(pg, 0, 1 << 18);
        assert(MxVt((DWORD)(uintptr_t)vtBad) < 0);
    }
    g_mxs = savedS, g_mxPage = savedP, g_mx = saved;
    VirtualFree(st, 0, MEM_RELEASE), VirtualFree(pg, 0, MEM_RELEASE), VirtualFree(heap, 0, MEM_RELEASE);
    TestMemExpBench();
    printf("mem experiments: fake Boehm heap block walk, marked bytes, class census (runtime-info check), Cork ratio rank, points-from holders, square-root limit, collect_or_expand grow / forced / observe ok\n");
}
