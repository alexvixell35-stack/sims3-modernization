// TestStreamBuf ([StreamBuf] streambuf.cpp, docs/547). Included by selftest.cpp. The stubs run against a fake allocator
// (a vtable of its own over malloc / free) and fake SharedPointer objects; the arena is a real reservation in this
// process. The patch tests run patched copies of the game's SharedPointer constructor and Release bytes (they call only
// through the fake vtables) and each patched window of the compressor and the package writer on its own;
// MemoryStream::Realloc's copy and the rest of those functions are patched and compared, not run (they call game code).
// The test's arena goes (in the game the arena lives until the process ends).
static void StaRelease() {
    if (g_sta.base && g_sta.sec) UnmapViewOfFile(g_sta.base), CloseHandle(g_sta.sec);
    else if (g_sta.base) VirtualFree(g_sta.base, 0, MEM_RELEASE);
    InterlockedExchange((volatile LONG*)&g_sta.n, 0);
    g_sta = Sta{};
}
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
static const DWORD* g_stbtFrame;  // what the stub handed the caller walk
static DWORD StbtFrameFn(const DWORD* a, const DWORD*, DWORD* via, DWORD* how) { return g_stbtFrame = a, *via = 0, *how = 0, 0; }

static u8* StbtGet(DWORD n) { return (u8*)StbAlloc(n, g_stbtStream, &g_stbtA, "t"); }
static void StbtPut(u8* p) {
    StbtSp sp = {g_stbtSpVt, &g_stbtA, p, 1, 1};
    StbFree(&sp);
}
static DWORD StbtOsCom() {
    DWORD com = 0;
    assert(StaOsView(&com));  // one reservation from end to end
    return com;
}
// Empty arena of arenaMB (every block must have been freed; a reservation of another size is replaced), counters 0, the
// given switches and sizes.
static void StbtReset(LONG arm, DWORD arenaMB, DWORD minKB, DWORD maxBlkMB, DWORD idleMs) {
    InterlockedExchange(&g_stbArm, 0);
    StbTick(0);
    assert(g_sta.useN == 0 && (g_sta.sec || g_sta.comN == 0) && g_sta.freeN == g_sta.n && g_stbHi == 0);  // a shared arena cannot decommit
    for (DWORD i = 0; i < g_sta.n; i++) assert(g_sta.sec ? !(g_staSt[i] & (STA_USED | STA_PURGE)) : g_staSt[i] == 0);
    if (g_sta.base && (g_sta.n != arenaMB << 4 || (g_sta.sec != NULL) != g_stbShared)) {
        assert(g_sta.sec || StbtOsCom() == 0);
        StaRelease();
    }
    memset(&g_stb, 0, sizeof(g_stb)), memset(g_stbWho, 0, sizeof(g_stbWho)), memset(g_stbTid, 0, sizeof(g_stbTid)), memset(g_stbSz, 0, sizeof(g_stbSz));
    assert(g_stbHeadN == 0);
    memset(&g_stbTime, 0, sizeof(g_stbTime));
    if (g_stbTls != TLS_OUT_OF_INDEXES) TlsSetValue(g_stbTls, NULL);
    g_stbBudget = arenaMB << 20, g_stbMin = minKB << 10, g_stbMaxBlock = maxBlkMB << 20, g_stbIdleMs = idleMs;
    assert(StaReserve(g_stbBudget, 0) && g_sta.n == arenaMB << 4 && g_sta.freeN == g_sta.n);
    g_sta.peakUseN = g_sta.peakComN = 0;
    InterlockedExchange(&g_stbArm, arm);
}

static void StbtPolicy() {
    assert(StbCap(1) == 0x10000 && StbCap(0x10000) == 0x10000 && StbCap(0x10001) == 0x20000 && StbCap(1398384) == 0x160000 && StbCap(699320) == 0xB0000);
    StbtReset(0, 4, 128, 8, 5000);
    assert(!StbPoolable((128u << 10) - 1) && StbPoolable(128u << 10) && StbPoolable(4u << 20) && !StbPoolable((4u << 20) + 1));  // never more than the whole arena
    g_stbMaxBlock = 1u << 20;
    assert(StbPoolable(1u << 20) && !StbPoolable((1u << 20) + 1));
    assert(StbSzIx(0) == 0 && StbSzIx(4095) == 0 && StbSzIx(4096) == 1 && StbSzIx(1398384) == 9 && StbSzIx((16u << 20) - 1) == 12 && StbSzIx(16u << 20) == 13 && StbSzIx(0xFFFFFFFF) == 13);
    // ours = inside the arena, by address only
    assert(StaOwned(g_sta.base) && StaOwned(g_sta.base + (4u << 20) - 1) && !StaOwned(g_sta.base + (4u << 20)) && !StaOwned(g_sta.base - 1) && !StaOwned(NULL));
    // the headroom table: more entries than it follows, then every one removed in two passes
    for (DWORD i = 1; i <= 49200; i++) StbHeadPut((const u8*)(uintptr_t)(i * 8), i);
    assert(g_stbHeadN == 49152 && g_stb.headUntracked == 48 && g_stb.headB == 49152u / 2 * 49153u && g_stb.peakHeadB == g_stb.headB);
    StbHeadGone((const u8*)(uintptr_t)0x7FFF0000);  // not in the table: nothing happens
    for (DWORD i = 1; i <= 49200; i += 2) StbHeadGone((const u8*)(uintptr_t)(i * 8));
    assert(g_stbHeadN == 24576 && g_stb.headB == 24576u * 24577u);
    for (DWORD i = 2; i <= 49200; i += 2) StbHeadGone((const u8*)(uintptr_t)(i * 8));
    assert(g_stbHeadN == 0 && g_stb.headB == 0);
    for (const StbHead& h : g_stbHead) assert(!h.p);
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

// StaPlace against free-range lists: made-up ones and the two measured in the game (in-process renderer, 45 s after the
// start and at the end of a 12 minute travel run).
static void StbtPlace() {
    const DWORD M = 1u << 20, sz = 48 * M;
    StaHole in, big;
    DWORD fits;
    assert(StaPlace(NULL, 0, sz, &in, &big, &fits) == 0 && fits == 0);
    // start of a run: two middle ranges and the large one up to the top of the address space
    const StaHole early[3] = {{0x6D473000, 96 * M}, {0x772D9000, 141 * M}, {0x95FF0000, 1692 * M}};
    assert(StaPlace(early, 3, sz, &in, &big, &fits) == 0x95FF0000 + 1692 * M - sz && fits == 3 && in.base == 0x95FF0000 && big.base == 0x95FF0000);
    // end of the run: only the large range is left; the arena sits at its top, so the range loses exactly 48 MB
    const StaHole late[1] = {{0xB5AC0000, 1185 * M}};
    DWORD at = StaPlace(late, 1, sz, &in, &big, &fits);
    assert(at == 0xB5AC0000 + 1185 * M - sz && fits == 1 && !(at & 0xFFFF));
    // a smaller range above the large one that fits: taken, the large one is untouched
    const StaHole two[3] = {{0xF4000000, 64 * M}, {0xAC740000, 1035 * M}, {0xEE506000, 34 * M}};
    at = StaPlace(two, 3, sz, &in, &big, &fits);
    assert(at == 0xF4000000 + 16 * M && in.base == 0xF4000000 && big.base == 0xAC740000 && fits == 2);
    // ends off the 64 KB grid: the block is placed down to a boundary and must still start inside the range
    const StaHole odd[1] = {{0x10003000, sz + 0x1F000}};
    assert(StaPlace(odd, 1, sz, &in, &big, &fits) == 0x10020000 && fits == 1);
    const StaHole tight[1] = {{0x10003000, sz + 0xC000}};  // 48 MB long and more, but not from a boundary
    assert(StaPlace(tight, 1, sz, &in, &big, &fits) == 0 && fits == 0 && big.base == 0x10003000);
    const StaHole exact[2] = {{0x20000000, sz}, {0x10000000, sz - 0x10000}};
    assert(StaPlace(exact, 2, sz, &in, &big, &fits) == 0x20000000 && fits == 1);
    const StaHole top[1] = {{0xFD000000, 0x02FF0000}};  // up to the last user address: no overflow
    assert(StaPlace(top, 1, 32 * M, &in, &big, &fits) == 0xFDFF0000 && fits == 1);
    // the live map of this process: the scan finds free ranges, the placement is the top of one of them, and the
    // reservation made from it is one region with nothing committed
    // MinFreeMB: only a reservation inside the largest range is held against the floor
    assert(StaFloorOk(late[0], late[0], sz, 1137) && !StaFloorOk(late[0], late[0], sz, 1138) && StaFloorOk(two[0], two[1], sz, 4000));
    StbtReset(0, 4, 128, 8, 5000);
    StaRelease();
    static StaHole h[256];
    const DWORD n = StaScan(h, 256, 4 * M);
    assert(n > 0);
    for (DWORD i = 0; i < n; i++) {
        assert(h[i].size >= 4 * M && (i == 0 || h[i].base >= h[i - 1].base + h[i - 1].size));
        MEMORY_BASIC_INFORMATION mbi;
        assert(VirtualQuery((void*)(uintptr_t)h[i].base, &mbi, sizeof(mbi)) && mbi.State == MEM_FREE);
    }
    at = StaPlace(h, n, 4 * M, &in, &big, &fits);
    assert(at && at >= in.base && (u64)at + 4 * M <= (u64)in.base + in.size && (u64)at + 4 * M + 0x10000 > (u64)in.base + in.size && fits && fits <= n);
    StbtReset(0, 4, 128, 8, 5000);  // StaReserve
    assert(g_sta.how == 1 && (DWORD)(uintptr_t)g_sta.base == at && g_sta.hole.base == in.base && StbtOsCom() == 0);
    MEMORY_BASIC_INFORMATION mbi;
    assert(VirtualQuery(g_sta.base, &mbi, sizeof(mbi)) && mbi.AllocationBase == g_sta.base && mbi.RegionSize == 4 * M && mbi.State == MEM_RESERVE);
    // no arena (the reservation failed or was refused): every request is the game's
    StaRelease();
    InterlockedExchange(&g_stbArm, STB_POOL | STB_ZERO);
    g_stbtA.allocs = g_stbtA.frees = 0;
    u8* p = StbtGet(1398384);
    assert(p && !StaOwned(p) && g_stbtA.allocs == 1 && g_stb.noArena == 1 && g_stb.fallback == 1);
    StbtPut(p);
    assert(g_stbtA.frees == 1);
    InterlockedExchange(&g_stbArm, 0);
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
    const DWORD T5 = 1398384, T1 = 699320, S5 = 22, S1 = 11;  // 1024x1024 DXT5 / DXT1 with mips + DDS header; their slices
    const DWORD K = 0x10000;
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
    g_stbtNow = 1000, g_stbtWho = 0x0072A2B4;
    memset(g_stbtStream, 0, sizeof(g_stbtStream));
    StbtReset(STB_POOL | STB_ZERO | STB_STATS, 4, 128, 2, 5000);
    u8* const base = g_sta.base;
    u8* p1 = StbtGet(T5);  // the lowest slices, committed now: zero pages
    assert(p1 == base && StbAllIs(p1, T5, 0) && g_stb.fresh == 1 && g_stb.served == 0 && g_sta.comN == S5 && g_sta.useN == S5 && g_stbtA.allocs == 0);
    assert(StbtOsCom() == S5 * K && g_stb.commitB == S5 * K);
    memset(p1, 0xEE, T5);
    g_stbtNow += 18;
    StbtPut(p1);
    assert(g_sta.useN == 0 && g_sta.comN == S5 && g_sta.freeN == 64 && g_stb.frees == 1 && g_stbtA.frees == 0 && p1[0] == 0xEE);  // free at once, still committed
    u8* p2 = StbtGet(T5);  // the same slices again, zero-filled over the request, no system call
    assert(p2 == p1 && StbAllIs(p2, T5, 0) && g_stb.served == 1 && g_stb.servedB == T5 && g_stb.zeroB == T5 && g_stb.commitB == S5 * K);
    u8* p3 = StbtGet(T1);
    assert(p3 == base + S5 * K && g_stb.fresh == 2);
    StbtPut(p2);
    u8* p4 = StbtGet(T1);  // best fit: the 22 free slices at the bottom, not the 31 above p3; no commit needed
    assert(p4 == base && g_stb.served == 2 && g_sta.comN == S5 + S1);
    u8* p4b = StbtGet(T1);  // the rest of the old block: exactly 11 slices, the smallest run that fits
    assert(p4b == base + S1 * K && g_stb.served == 3 && g_sta.freeN == 64 - 3 * S1);
    u8* s = StbtGet(1000);  // small: the game's allocator, the game's free
    assert(s && !StaOwned(s) && g_stbtA.allocs == 1 && !strcmp(g_stbtA.lastName, "t"));
    StbtPut(s);
    assert(g_stbtA.frees == 1 && g_stb.frees == 2);  // small blocks are not tracked: frees counts 128 KB+ only
    u8* big = StbtGet(3u << 20);  // over MaxBlockMB: the game's path, tracked for Stats
    assert(big && !StaOwned(big) && g_stb.tooBig == 1 && g_stbtA.allocs == 2 && g_stb.liveN == 4);
    StbtPut(big);
    assert(g_stbtA.frees == 2 && g_stb.liveN == 3);
    // full arena: 31 slices are free, a 22-slice request fits once, the next goes to the game (fallback, not stranded)
    u8* p5 = StbtGet(T5);
    assert(p5 == base + 3 * S1 * K && g_sta.freeN == 9);
    u8* f = StbtGet(T5);
    assert(f && !StaOwned(f) && g_stb.fallback == 1 && g_stb.stranded == 0 && g_stbtA.allocs == 3);
    StbtPut(f);
    // stranded: 11 + 9 free slices in two runs do not serve 15 slices; they serve 9 (the smaller run first)
    StbtPut(p4b);
    f = StbtGet(15 * K);
    assert(f && !StaOwned(f) && g_stb.fallback == 2 && g_stb.stranded == 1);
    StbtPut(f);
    u8* p6 = StbtGet(9 * K);
    assert(p6 == base + (3 * S1 + S5) * K && g_sta.freeN == S1);
    StbtPut(p6);
    StbtPut(p6);  // freed twice: counted, nothing else happens
    assert(g_stb.dfree == 1 && g_sta.freeN == S1 + 9 && g_stbtA.frees == 4);
    StbtPut(p5 + K);  // a pointer into a block: never the game's free
    assert(g_stb.dfree == 2 && g_stbtA.frees == 4 && g_sta.useN == 2 * S1 + S5);
    // idle decommit: free slices go IdleSec after their free, slices in use never, and a neighbour freed later keeps its own clock
    const DWORD t0 = g_stbtNow;  // p4b's and p6's slices were freed at t0
    g_stbtNow = t0 + 3000;
    StbtPut(p3);
    g_stbtNow = t0 + 4999, StbTick(g_stbtNow);
    assert(g_stb.decommits == 0 && g_sta.comN == 64);
    g_stbtNow = t0 + 5000, StbTick(g_stbtNow);  // two runs: p4b's 11 slices and p6's 9
    assert(g_stb.decommits == 2 && g_sta.comN == 64 - S1 - 9 && StbtOsCom() == (64 - S1 - 9) * K && g_staSt[S1] == 0 && g_staSt[2 * S1] == STA_COM);
    g_stbtNow = t0 + 8000, StbTick(g_stbtNow);  // p3's slices
    assert(g_stb.decommits == 3 && g_sta.comN == S1 + S5 && StbtOsCom() == (S1 + S5) * K);
    u8* p7 = StbtGet(T1);  // decommitted slices are committed again: zeros without a fill (the 9-slice run is too small: the 22 free slices below p5)
    const u64 zb = g_stb.zeroB;
    assert(p7 == base + S1 * K && StbAllIs(p7, T1, 0) && g_stb.zeroB == zb && g_sta.comN == 2 * S1 + S5);
    // who row and the grow count (a stream that already has a buffer)
    *(DWORD*)(g_stbtStream + 4) = 1, *(DWORD*)(g_stbtStream + 0x14) = 5000;
    u8* g = StbtGet(9000);
    *(DWORD*)(g_stbtStream + 4) = 0;
    StbtPut(g);
    assert(g_stb.grows == 1 && g_stb.growB == 5000 && g_stbWho[1].at == 0x0072A2B4 && g_stbWho[1].via == 0x0072A3B4 && g_stbWho[1].how == 1 && g_stbWho[1].grows == 1);
    assert(g_stbWho[1].n == g_stb.reqs && g_stbWho[1].maxReq == 3u << 20 && g_stbWho[1].liveN == 3 && g_stbWho[1].frees == g_stb.frees && g_stbWho[1].peakB >= 2 * T5);
    assert(g_stbSz[StbSzIx(T5)] == 4 && g_stbSz[0] == 1 && g_stbTid[1].tid == GetCurrentThreadId() && g_stbTid[1].allocs == g_stb.big && g_stb.xfree == 0);
    assert(g_stbWho[1].lifeMs >= 18 && g_stbWho[1].quick == g_stbWho[1].frees - 1);  // all but p3 lived under a second
    assert(g_sta.peakUseN == 3 * S1 + S5 && g_sta.peakComN == 64);
    // run-time kill switch: requests go to the game, an arena block still comes home and the tick decommits everything free
    InterlockedExchange(&g_stbArm, 0);
    u8* k = StbtGet(T5);
    assert(k && !StaOwned(k) && g_stbtA.live == 1);
    StbtPut(k);
    StbtPut(p5);
    assert(g_stbtA.live == 0 && g_sta.useN == 2 * S1);
    StbTick(g_stbtNow);
    assert(g_sta.comN == 2 * S1 && StbtOsCom() == 2 * S1 * K);
    // process exit: a free of an arena block takes no lock and does nothing
    InterlockedExchange(&g_stbDetached, 1);
    StbtPut(p4);
    assert(g_sta.useN == 2 * S1);
    InterlockedExchange(&g_stbDetached, 0);
    StbtPut(p4), StbtPut(p7);

    // Verify 1: a freed block is poisoned; a write after the free is seen at the reuse; the block is handed out zeroed
    StbtReset(STB_POOL | STB_ZERO | STB_VERIFY, 4, 128, 2, 5000);
    u8* v = StbtGet(T5);
    memset(v, 0x11, T5);
    StbtPut(v);
    assert(StbAllIs(v, 0x160000, STB_POISON));
    assert(StbtGet(T5) == v && StbAllIs(v, T5, 0) && g_stb.uafw == 0);
    StbtPut(v);
    v[0x1234] = 7;  // use after free
    assert(StbtGet(T1) == v && g_stb.uafw == 1 && StbAllIs(v, T1, 0));  // seen by the smaller block that takes the slice
    v[S1 * K + 5] = 7;  // and in a slice the smaller block did not take: seen when that one is reused
    u8* v2 = StbtGet(T1);
    assert(v2 == v + S1 * K && g_stb.uafw == 2);
    StbtPut(v), StbtPut(v2);
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
    // Zero=0: a reused slice keeps the old bytes, a newly committed one is zeros
    StbtReset(STB_POOL, 4, 128, 2, 5000);
    v = StbtGet(T1);
    memset(v, 0x33, T1);
    StbtPut(v);
    v = StbtGet(T5);
    assert(StbAllIs(v, T1, 0x33) && StbAllIs(v + S1 * K, T5 - S1 * K, 0) && g_stb.zeroB == 0);
    StbtPut(v);
    // PreZero: the upkeep thread's fill makes freed slices "known zero"; the next request fills nothing
    StbtReset(STB_POOL | STB_ZERO | STB_PREZERO, 4, 128, 2, 5000);
    u8* z1 = StbtGet(T1);
    u8* z2 = StbtGet(T1);
    u8* z3 = StbtGet(T1);
    u8* z4 = StbtGet(31 * K);  // the rest of the arena
    assert(z1 == base && z3 == base + 2 * S1 * K && z4 && g_sta.freeN == 0 && StbZeroSome(g_stbArm) == 0);  // nothing free: nothing to do
    memset(z1, 0x77, T1), memset(z3, 0x77, T1);
    StbtPut(z3);
    assert(g_staSt[2 * S1] == STA_COM && StbZeroSome(g_stbArm) == S1 && StbZeroSome(g_stbArm) == 0);
    assert(g_staSt[2 * S1] == (STA_COM | STA_ZERO) && StbAllIs(z3, S1 * K, 0) && g_stb.bgZeroB == S1 * K);
    StbtPut(z1);  // dirty, lower, the same length: the clean run is preferred
    u64 zb0 = g_stb.zeroB;
    u8* z5 = StbtGet(T1);
    assert(z5 == z3 && StbAllIs(z5, T1, 0) && g_stb.zeroB == zb0 && g_stb.cleanN == 1 && g_staSt[2 * S1] == (STA_USED | STA_COM | STA_HEAD));
    u8* z6 = StbtGet(T1);  // the dirty one: filled on the caller's thread as before
    assert(z6 == z1 && StbAllIs(z6, T1, 0) && g_stb.zeroB == zb0 + T1 && g_stb.cleanN == 1);
    // a run that is being filled is nobody's: marked PURGE, a request does not take it
    memset(z6, 0x77, T1);
    StbtPut(z6);
    for (DWORD i = 0; i < S1; i++) g_staSt[i] |= STA_PURGE;
    u8* z7 = StbtGet(T1);
    assert(z7 && !StaOwned(z7) && g_stb.fallback == 1);
    StbtPut(z7);
    for (DWORD i = 0; i < S1; i++) g_staSt[i] = STA_COM;
    // the whole arena free and dirty: filled in runs of at most 16 slices; zeroed slices are decommitted when idle like any other
    StbtPut(z5), StbtPut(z2), StbtPut(z4);
    assert(StbZeroSome(g_stbArm) == 16 && StbZeroSome(g_stbArm) == 16 && StbZeroSome(g_stbArm) == 16 && StbZeroSome(g_stbArm) == 16 && StbZeroSome(g_stbArm) == 0);
    for (DWORD i = 0; i < 64; i++) assert(g_staSt[i] == (STA_COM | STA_ZERO));
    g_stbtNow += 6000, StbTick(g_stbtNow);
    assert(g_sta.comN == 0 && StbtOsCom() == 0);
    // Verify 1 with PreZero: the poison is checked before the fill, and a write after the fill is found when the slice is handed out
    StbtReset(STB_POOL | STB_ZERO | STB_PREZERO | STB_VERIFY, 4, 128, 2, 5000);
    z1 = StbtGet(T1);
    memset(z1, 0x11, T1);
    StbtPut(z1);
    assert(StbAllIs(z1, S1 * K, STB_POISON) && StbZeroSome(g_stbArm) == S1 && g_stb.uafw == 0 && StbAllIs(z1, S1 * K, 0));
    z1[0x4321] = 9;  // written after the free and after the fill
    assert(StbtGet(T1) == z1 && g_stb.uafw == 1 && StbAllIs(z1, T1, 0));
    StbtPut(z1);
    z1[5] = 9;  // written after the free, before the fill
    assert(StbZeroSome(g_stbArm) == S1 && g_stb.uafw == 2 && StbAllIs(z1, S1 * K, 0));
    // SkipClear: a site whose consumer initialises its buffer gets no fill; every other request still does
    StbtReset(STB_POOL | STB_ZERO | STB_SCRATCH | STB_SKIPCLEAR, 4, 128, 2, 5000);
    g_stbScratchName = "scratch";
    z1 = (u8*)StbScratchAlloc(0x80000, &g_stbtA);
    assert(z1 == g_sta.base && StbAllIs(z1, 0x80000, 0) && g_stb.skipClearN == 1 && g_stb.skipClearB == 0);  // new pages: zero anyway
    memset(z1, 0x66, 0x80000);
    StbSiteFree(z1, &g_stbtA);
    z1 = (u8*)StbScratchAlloc(0x80000, &g_stbtA);
    assert(z1 == g_sta.base && StbAllIs(z1, 0x80000, 0x66) && g_stb.skipClearN == 2 && g_stb.skipClearB == 0x80000 && g_stb.zeroB == 0);
    StbSiteFree(z1, &g_stbtA);
    z1 = StbtGet(0x80000);  // a stream buffer in the same slices: zero-filled
    assert(z1 == g_sta.base && StbAllIs(z1, 0x80000, 0) && g_stb.zeroB == 0x80000);
    StbtPut(z1);
    InterlockedExchange(&g_stbArm, STB_POOL | STB_ZERO | STB_SCRATCH);  // SkipClear off: the table is filled like everything else
    z1 = (u8*)StbScratchAlloc(0x80000, &g_stbtA);
    memset(z1, 0x66, 0x80000);
    StbSiteFree(z1, &g_stbtA);
    z1 = (u8*)StbScratchAlloc(0x80000, &g_stbtA);
    assert(StbAllIs(z1, 0x80000, 0) && g_stb.skipClearN == 2);
    StbSiteFree(z1, &g_stbtA);
    g_stbScratchName = (const char*)(uintptr_t)0x00FB9030;
    // a request under MinKB with Stats and Timing off: the game's call, nothing counted; with Stats every 64th is followed to its free
    StbtReset(STB_POOL | STB_ZERO, 4, 128, 2, 5000);
    StbtPut(StbtGet(1000));
    assert(g_stb.reqs == 0);
    StbtReset(STB_POOL | STB_ZERO | STB_STATS, 4, 128, 2, 5000);
    for (int i = 0; i < 130; i++) g_stbtNow += 3, StbtPut(StbtGet(1000));
    assert(g_stb.reqs == 130 && g_stb.smallN == 3 && g_stb.smallX == 0 && g_stbHi == 0);
    // Stats alone: nothing from the arena, lifetimes of the game's own 128 KB+ blocks
    StbtReset(STB_STATS, 4, 128, 2, 5000);
    u8* e = StbtGet(T5);
    assert(e && !StaOwned(e) && g_stb.liveN == 1 && g_sta.useN == 0 && g_stbWho[1].liveB == T5);
    g_stbtNow += 1500;
    StbtPut(e);
    assert(g_stb.liveN == 0 && g_stb.frees == 1 && g_stbWho[1].lifeMs == 1500 && g_stbWho[1].quick == 0 && g_stbHi == 0);
    g_stbInstalled = 1, StbMemLine(1, "selftest"), g_stbInstalled = 0;  // formats every line (no log file here)
    StbtReset(0, 48, 128, 8, 5000);
    assert(g_stbtA.live == 0);
}

// Randomized run against the arena written the plain way (one state per slice). Fixed seed.
struct StbtLive { u8* p; DWORD n; int at; u8 sig; };  // at = first slice, -1 = the game's block
static void StbtModel() {
    enum { N = 12 * 16 };  // 12 MB
    static StbtLive live[600];
    static u8 mSt[N];      // 0 free and decommitted, 1 free and committed, 2 in a block
    static DWORD mT[N];
    memset(mSt, 0, sizeof(mSt));
    DWORD liveN = 0, seed = 0x5B0001;
    auto rnd = [&seed]() { seed = seed * 1664525u + 1013904223u; return seed >> 8; };
    static const DWORD kSizes[8] = {1398384, 699320, 589824, 2818048, 3538944, 4194304, 5636096, 524288};
    StbtReset(STB_POOL | STB_ZERO, 12, 128, 6, 2000);
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
    DWORD served = 0, fresh = 0, fallback = 0, stranded = 0, decommits = 0, gameLive = 0, tooBig = 0;
    for (int step = 0; step < 8000; step++) {
        g_stbtNow += 1 + rnd() % 300;
        const DWORD op = step % 1000 < 40 ? 0 : rnd() % 100;  // a burst of requests every 1000 steps fills the arena
        if (op < (liveN < 8 ? 60u : 35u) && liveN < 600) {
            const DWORD r = rnd() % 10, n = r < 7 ? kSizes[rnd() % 8] : r < 9 ? 65536 + rnd() % (3u << 20) : 1 + rnd() % 100000;
            const DWORD k = StbCap(n) >> 16;
            // the model's answer: the smallest free run of at least k slices, the lowest on a tie
            int pick = -1;
            DWORD pickLen = ~0u, freeN = 0;
            bool expFresh = false;
            const bool poolable = n >= 128u << 10 && n <= 6u << 20;
            if (poolable) {
                for (DWORD i = 0; i < N;) {
                    if (mSt[i] == 2) { i++; continue; }
                    DWORD j = i;
                    while (j < N && mSt[j] != 2) j++;
                    freeN += j - i;
                    if (j - i >= k && j - i < pickLen) pick = (int)i, pickLen = j - i;
                    i = j;
                }
                if (pick < 0) fallback++, stranded += freeN >= k;
                else {
                    for (DWORD i = 0; i < k; i++) expFresh |= mSt[pick + i] == 0, mSt[pick + i] = 2;
                    if (expFresh) fresh++; else served++;
                }
            } else tooBig += n > 6u << 20;
            u8* const p = StbtGet(n);
            assert(p);
            if (pick >= 0) assert(p == g_sta.base + ((DWORD)pick << 16) && StbAllIs(p, n, 0));  // the model's slices, and always zeros (Zero=1)
            else assert(!StaOwned(p)), gameLive++;
            const u8 sig = (u8)(1 + rnd() % 250);
            memset(p, sig, n);
            live[liveN++] = StbtLive{p, n, pick, sig};
        } else if (op < 94 && liveN) {
            const DWORD i = rnd() % liveN;
            const StbtLive l = live[i];
            assert(StbAllIs(l.p, l.n, l.sig));  // nobody else was given these bytes
            live[i] = live[--liveN];
            StbtPut(l.p);
            if (l.at < 0) gameLive--;
            else
                for (DWORD s = 0; s < StbCap(l.n) >> 16; s++) mSt[l.at + s] = 1, mT[l.at + s] = g_stbtNow;
        } else {
            StbTick(g_stbtNow);
            for (DWORD i = 0; i < N; i++) {
                const bool idle = mSt[i] == 1 && g_stbtNow - mT[i] >= 2000;
                if (idle && (i == 0 || mSt[i - 1] != 0xFF)) decommits++;  // one system call per run of idle slices
                if (idle) mSt[i] = 0xFF;
            }
            for (DWORD i = 0; i < N; i++)
                if (mSt[i] == 0xFF) mSt[i] = 0;
        }
        DWORD use = 0, com = 0;
        for (DWORD i = 0; i < N; i++) {
            use += mSt[i] == 2, com += mSt[i] != 0;
            assert(g_staSt[i] == (mSt[i] == 2 ? (g_staSt[i] & STA_HEAD) | STA_USED | STA_COM : mSt[i] == 1 ? STA_COM : 0));
        }
        assert(g_sta.useN == use && g_sta.comN == com && g_sta.freeN == N - use);
        assert(g_stb.served == served && g_stb.fresh == fresh && g_stb.fallback == fallback && g_stb.stranded == stranded && g_stb.decommits == decommits && g_stb.tooBig == tooBig);
        assert((DWORD)g_stbtA.live == gameLive && !g_stb.dfree && !g_stb.lost && !g_stb.vaFail && !g_stb.uafw);
        if (step % 250 == 0) assert(StbtOsCom() == com << 16);  // the OS agrees: one reservation, these slices committed
    }
    printf("streambuf model: 8000 steps in a 12 MB arena: %lu served without a system call, %lu with a commit, %lu to the game (%lu of them with enough free "
           "slices but no run), %lu decommits, peak in use %.1f MB\n", served, fresh, fallback, stranded, decommits, g_sta.peakUseN / 16.0);
    assert(served > 500 && fresh > 100 && fallback > 10 && stranded > 0 && decommits > 10);  // every path was taken
    while (liveN) liveN--, assert(StbAllIs(live[liveN].p, live[liveN].n, live[liveN].sig)), StbtPut(live[liveN].p);
    StbtReset(0, 48, 128, 8, 5000);  // asserts: nothing in use, nothing committed
    assert(g_stbtA.live == 0 && g_stbtA.allocs == g_stbtA.frees);
}

// Four threads allocate, fill, check and free; blocks are handed to the next thread's free through a shared slot, and
// the upkeep tick decommits beside them.
static u8* volatile g_stbtHand[4];
static volatile LONG g_stbtStop;
static DWORD WINAPI StbtWorker(void* arg) {
    const DWORD me = (DWORD)(uintptr_t)arg;
    DWORD seed = 77 + me;
    static const DWORD kN[4] = {699320, 1398384, 699320, 3538944};
    for (int i = 0; i < 3000; i++) {
        seed = seed * 1664525u + 1013904223u;
        const DWORD n = kN[(seed >> 9) & 3];
        u8* p = StbtGet(n);
        assert(p && (!StaOwned(p) || (StbAllIs(p, 64, 0) && StbAllIs(p + n - 64, 64, 0))));
        memset(p, (int)(me + 1), 4096), memset(p + n - 64, (int)(me + 1), 64);
        p = (u8*)InterlockedExchangePointer((void* volatile*)&g_stbtHand[(me + 1) & 3], p);  // the neighbour frees ours
        if (p) assert(p[0] == p[4095] && p[0] >= 1 && p[0] <= 4), StbtPut(p);
    }
    return 0;
}
static DWORD WINAPI StbtTicker(void*) {
    while (!g_stbtStop) {
        StbTick(StbNowMs());
        for (int i = 0; i < 64 && StbZeroSome(g_stbArm); i++) {}
        Sleep(0);
    }
    return 0;
}
static void StbtThreads() {
    StbtReset(STB_POOL | STB_ZERO | STB_STATS | STB_PREZERO, 16, 128, 4, 3);  // idle 3 ms: decommits and the background fill race the requests
    g_stbNow = StbNowMs;
    g_stbtStop = 0;
    HANDLE h[4], tk = CreateThread(NULL, 0, StbtTicker, NULL, 0, NULL);
    for (DWORD i = 0; i < 4; i++) h[i] = CreateThread(NULL, 0, StbtWorker, (void*)(uintptr_t)i, 0, NULL);
    WaitForMultipleObjects(4, h, TRUE, INFINITE);
    g_stbtStop = 1;
    WaitForSingleObject(tk, INFINITE), CloseHandle(tk);
    for (DWORD i = 0; i < 4; i++) {
        CloseHandle(h[i]);
        if (g_stbtHand[i]) StbtPut(g_stbtHand[i]), g_stbtHand[i] = NULL;
    }
    g_stbNow = StbtNow;
    assert(g_sta.useN == 0 && g_stb.liveN == 0 && !g_stb.dfree && !g_stb.lost && !g_stb.vaFail && g_stb.reqs == 12000 && g_stb.frees == 12000 && g_stb.xfree > 0);
    assert(g_stb.served + g_stb.fresh + g_stb.fallback == 12000 && g_stb.served + g_stb.fresh > 10000 && g_sta.peakUseN <= 16 * 16);
    assert(StbtOsCom() == g_sta.comN << 16);
    printf("streambuf threads: 12000 requests on 4 threads, %llu without a system call, %llu with a commit, %llu to the game, %lu decommits beside them, "
           "%.0f MB zeroed in the background, %.0f MB by the callers, %llu requests found everything clean\n",
           g_stb.served, g_stb.fresh, g_stb.fallback, g_stb.decommits, g_stb.bgZeroB / 1048576.0, g_stb.zeroB / 1048576.0, g_stb.cleanN);
    assert(g_stb.bgZeroB > 0 && g_stb.cleanN > 0);
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
// Runs one patched window (a `ret` was written behind it) with the registers and the two frame slots the game's code
// has there: [esp+18h] and [esp+1Ch] as the window sees them. The window must leave the stack and ebx / esi / edi as it
// found them.
struct StbtRegs { DWORD eax, ecx, ebx, esi, edi, s18, s1c; };
static void* StbtRun(void* a_at, const StbtRegs& a_r) {
    void* r;
    DWORD e0, e1, si1, bx1, di1;
    const DWORD vA = a_r.eax, vC = a_r.ecx, vB = a_r.ebx, vS = a_r.esi, vD = a_r.edi, s18 = a_r.s18, s1c = a_r.s1c;
    __asm {
        push edi
        push esi
        push ebx
        mov e0, esp
        push s1c
        push s18
        sub esp, 0x14
        mov eax, vA
        mov ecx, vC
        mov ebx, vB
        mov esi, vS
        mov edi, vD
        call a_at
        mov r, eax
        add esp, 0x1C
        mov e1, esp
        mov si1, esi
        mov bx1, ebx
        mov di1, edi
        pop ebx
        pop esi
        pop edi
    }
    assert(e0 == e1 && si1 == vS && bx1 == vB && di1 == vD);
    return r;
}
static void* StbtRunC(void* at, void* alloc, DWORD cx) { return StbtRun(at, StbtRegs{0, cx, 0x0B0B0B0B, 0x51515151, (DWORD)(uintptr_t)alloc, 0, 0}); }  // a compressor window: edi = the allocator
static LONG g_stbtHeapAllocs, g_stbtHeapFrees;
static void* g_stbtHeapLast;
static void* __fastcall StbtMemalign(void* heap, void*, DWORD size, DWORD, DWORD, DWORD) { return g_stbtHeapAllocs++, g_stbtHeapLast = heap, malloc(size); }
static void __fastcall StbtHeapFree(void* heap, void*, void* p) { g_stbtHeapFrees++, g_stbtHeapLast = heap, free(p); }
// Calls a copy of game code: thiscall with two stack arguments (MemoryStream::Write).
__declspec(noinline) static DWORD StbtCall2(void* a_fn, void* a_this, const void* a1, DWORD a2) {
    DWORD r, e0, e1;
    __asm {
        mov e0, esp
        push a2
        push a1
        mov ecx, a_this
        call a_fn
        mov r, eax
        mov e1, esp
    }
    assert(e0 == e1);
    return r & 0xFF;
}
static DWORD g_stbtReCap;
static const void* g_stbtReRet;
static const void* g_stbtRet2;  // Write's own return address, as its frame holds it above Realloc's call: the writer
static bool __fastcall StbtRealloc(void*, void*, DWORD cap) { return g_stbtReCap = cap, g_stbtReRet = _ReturnAddress(), g_stbtRet2 = (const void*)(uintptr_t)((const DWORD*)_AddressOfReturnAddress())[8], false; }  // as Realloc: thiscall, one argument; "failed"
static void StbtPatch() {
    u8* x = (u8*)VirtualAlloc(NULL, 0x2000, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
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
    // the patched constructor: a buffer from the arena, the fields as the game's code leaves them
    StbtReset(STB_POOL | STB_ZERO, 48, 128, 8, 5000);
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0, g_stbtSpDeletes = 0;
    StbtSp sp = {};
    assert(StbtCall(ctor, &sp, 1398384, &g_stbtA, "name", g_stbtStream, 3) == &sp);
    assert((DWORD)(uintptr_t)sp.vt == 0x00FB84D0 && sp.alloc == &g_stbtA && sp.data && StaOwned(sp.data) && sp.ref == 0 && sp.freeData == 1 && g_stb.fresh == 1);
    u8* const d = sp.data;
    // the patched Release: a reference left = only the count drops; the last one = the buffer to the arena, then the object's destructor
    sp.vt = g_stbtSpVt, sp.ref = 2;
    StbtCall(rel, &sp, 0, NULL, NULL, NULL, 0);
    assert(sp.ref == 1 && g_stb.frees == 0 && g_stbtSpDeletes == 0);
    assert(StbtCall(rel, &sp, 0, NULL, NULL, NULL, 0) == NULL && g_stb.frees == 1 && g_sta.useN == 0 && g_sta.comN == 22 && g_stbtSpDeletes == 1 && g_stbtA.frees == 0);
    // the caller walk is handed the stack as the patched constructor left it (the "who" rows were empty in the game: the
    // walk had been given the address of a copy of the first argument): the stub's four arguments, the constructor's
    // saved esi and return, then the constructor's own three arguments
    g_stbWhoFn = StbtFrameFn, g_stbtFrame = NULL;
    static const char kName[] = "frame";
    StbtSp spf = {};
    StbtCall(ctor, &spf, 1398384, &g_stbtA, kName, g_stbtStream, 3);
    const DWORD* a = g_stbtFrame;
    assert(a && a[0] == 1398384 && a[1] == (DWORD)(uintptr_t)g_stbtStream && a[2] == (DWORD)(uintptr_t)&g_stbtA && a[3] == (DWORD)(uintptr_t)kName);
    assert(a[6] == 1398384 && a[7] == (DWORD)(uintptr_t)&g_stbtA && a[8] == (DWORD)(uintptr_t)kName);
    g_stbWhoFn = StbtWhoFn;
    spf.vt = g_stbtSpVt, spf.ref = 1;
    StbtCall(rel, &spf, 0, NULL, NULL, NULL, 0);
    // a small buffer through the same code: the game's allocator with the stream's name, the game's free
    StbtSp sp2 = {};
    StbtCall(ctor, &sp2, 4000, &g_stbtA, "small", g_stbtStream, 3);
    assert(sp2.data && !StaOwned(sp2.data) && g_stbtA.allocs == 1 && !strcmp(g_stbtA.lastName, "small") && sp2.freeData == 1);
    sp2.vt = g_stbtSpVt, sp2.ref = 1;
    StbtCall(rel, &sp2, 0, NULL, NULL, NULL, 0);
    assert(g_stbtA.frees == 1 && g_stbtSpDeletes == 3);
    // "free the data" byte clear (a buffer the stream does not own): no free at all
    StbtSp sp3 = {g_stbtSpVt, &g_stbtA, d, 1, 0};
    StbtCall(rel, &sp3, 0, NULL, NULL, NULL, 0);
    assert(g_stbtA.frees == 1 && g_stb.dfree == 0 && g_stbtSpDeletes == 4);

    // Grow. The two call sites: Write's `call Realloc` and Realloc's `call memcpy`
    u8* wr = x + 0xE00;
    static const u8 kWriteTail[14] = {0x84, 0xC0, 0x75, 0x16, 0x5F, 0x5B, 0x5E, 0x5D, 0x83, 0xC4, 0x08, 0xC2, 0x08, 0x00};  // 0x4DF061: Realloc failed -> return
    memcpy(wr, kStbWrite, sizeof(kStbWrite)), memcpy(wr + sizeof(kStbWrite), kWriteTail, sizeof(kWriteTail));
    wr[STB_WRITE_CALL + 1] ^= 1;
    assert(!StbInstallGrow(wr, re) && !g_stbPatch[13].at);
    wr[STB_WRITE_CALL + 1] ^= 1;
    assert(StbInstallGrow(wr, re) && g_stbTls != TLS_OUT_OF_INDEXES);
    g_stbGrowIn = true;  // as ApplyStreamBuf records it
    assert(STB_WRITE + STB_WRITE_CALL == 0x004DF05C && STB_REALLOC + STB_REALLOC_COPY == 0x004DEECB && kStbRealloc[STB_REALLOC_COPY] == 0xE8 && kStbWrite[STB_WRITE_CALL] == 0xE8);
    assert(wr[STB_WRITE_CALL] == 0xE8 && (uintptr_t)wr + STB_WRITE_CALL + 5 + *(LONG*)(wr + STB_WRITE_CALL + 1) == (uintptr_t)&StbWriteGrow && !memcmp(wr, kStbWrite, STB_WRITE_CALL));
    assert(re[STB_REALLOC_COPY] == 0xE8 && (uintptr_t)re + STB_REALLOC_COPY + 5 + *(LONG*)(re + STB_REALLOC_COPY + 1) == (uintptr_t)&StbGrowCopy);
    assert(!memcmp(re, kStbRealloc, STB_REALLOC_COPY) && !memcmp(re + STB_REALLOC_COPY + 5, kStbRealloc + STB_REALLOC_COPY + 5, STB_REALLOC_AT - STB_REALLOC_COPY - 5));
    {
        // the policy: four times the old capacity under 4 KB, twice from there, at least the writer's floor
        StbtReset(STB_POOL | STB_ZERO | STB_GROW, 48, 128, 8, 5000);
        g_stbGrowFloor = 64, g_stbGrowText = 4096, g_stbGrowFirst = 0;
        u8 st[0x28] = {};
        assert(StbGrowCap(st, 4, 0x401000) == 4 && g_stb.headN == 0);  // no buffer yet: the game's value (GrowFirst 0)
        g_stbGrowFirst = 64;
        assert(StbGrowCap(st, 4, 0x401000) == 64 && StbGrowCap(st, 100, 0x401000) == 100 && g_stb.headN == 1);
        g_stbGrowFirst = 0;
        *(DWORD*)(st + 4) = 1, *(DWORD*)(st + 0x14) = 4;
        assert(StbGrowCap(st, 8, 0x401000) == 64 && g_stb.headN == 2 && g_stb.headSteps == 8 + 6 && g_stb.headTotB == 60 + 56);  // the game: 8, 12, 18, 27, 40, 60, 90
        assert(StbGrowCap(st, 8, STB_TEXT_WRITE_RET) == 4096);  // the text encoder's streams start their growth at 4 KB
        *(DWORD*)(st + 0x14) = 64;
        assert(StbGrowCap(st, 96, 0x401000) == 256 && StbGrowCap(st, 1000, 0x401000) == 1000);  // a write that needs more: as asked
        *(DWORD*)(st + 0x14) = 4096;
        assert(StbGrowCap(st, 6144, 0x401000) == 8192 && StbGrowCap(st, 6144, STB_TEXT_WRITE_RET) == 8192);
        *(DWORD*)(st + 0x14) = 70000;
        assert(StbGrowCap(st, 105000, 0x401000) == 105000);  // twice would reach MinKB: the game's 1.5x
        *(DWORD*)(st + 0x14) = 200000;
        const u64 grants = g_stb.headN;
        assert(StbGrowCap(st, 300000, 0x401000) == 300000 && g_stb.headN == grants && grants == 6);
        // the patched Write, run up to its Realloc call: capacity 64, position 64, four more bytes. The game asks for
        // 96; Realloc is handed 128 and sees Write's own return address
        void* const realFn = g_stbReallocFn;
        g_stbReallocFn = (void*)&StbtRealloc;
        const float f15 = 1.5f;
        memset(st, 0, sizeof(st));
        *(DWORD*)(st + 4) = 1, *(DWORD*)(st + 0x10) = 64, *(DWORD*)(st + 0x14) = 64, *(DWORD*)(st + 0x18) = 64, st[0x1C] = 1, memcpy(st + 0x20, &f15, 4);
        static const u8 four[4] = {1, 2, 3, 4};
        InterlockedExchange(&g_stbArm, STB_POOL | STB_ZERO | STB_GROW | STB_STATS);
        assert(StbtCall2(wr, st, four, 4) == 0 && g_stbtReCap == 256 && g_stbtReRet == wr + STB_WRITE_CALL + 5 && g_stb.headN == grants + 1);
        // the writer is read from Write's frame: with this test's call as the text encoder's, the floor is 4 KB
        g_stbTextAt = (DWORD)(uintptr_t)g_stbtRet2;
        const DWORD r2 = StbtCall2(wr, st, four, 4);
        assert(r2 == 0 && g_stbtReCap == 4096);
        g_stbTextAt = STB_TEXT_WRITE_RET;
        if (g_stbTls != TLS_OUT_OF_INDEXES) TlsSetValue(g_stbTls, NULL);
        assert(StbtCall2(wr, st, four, 4) == 0 && g_stbtReCap == 256);
        // Stats: the 160 bytes of headroom follow the buffer the Realloc then asks for, until that buffer is freed
        u8* hb = StbtGet(256);
        assert(hb && !StaOwned(hb) && g_stb.headB == 160 && g_stb.peakHeadB == 160 && g_stbHeadN == 1);
        StbtPut(hb);
        assert(g_stb.headB == 0 && g_stbHeadN == 0 && g_stb.peakHeadB == 160);
        InterlockedExchange(&g_stbArm, STB_POOL | STB_ZERO | STB_STATS);  // Grow off: the game's 96
        assert(StbtCall2(wr, st, four, 4) == 0 && g_stbtReCap == 96);
        g_stbReallocFn = realFn;
        // the copy stub: nothing to do for source = destination, else a copy
        u8 c1[64] = {}, c2[64];
        memset(c2, 7, sizeof(c2));
        assert(StbGrowCopy(c1, c1, 64) == c1 && c1[0] == 0 && StbGrowCopy(c1, c2, 64) == c1 && StbAllIs(c1, 64, 7));

        // grow in place: a stream whose 704 KB block has free slices behind it asks for 1408 KB
        for (int round = 0; round < 2; round++) {
            StbtReset(STB_POOL | STB_ZERO | STB_GROW | STB_STATS | (round ? STB_VERIFY | STB_VERIFY2 : 0), 48, 128, 8, 5000);
            g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
            g_stbtWho = 0x0072A2B4;
            memset(g_stbtStream, 0, sizeof(g_stbtStream));
            const DWORD T5 = 1398384, T1 = 699320;
            u8* b1 = StbtGet(T1);
            assert(b1 == g_sta.base);
            memset(b1, 0x5A, T1);
            StbtSp osp = {g_stbtSpVt, &g_stbtA, b1, 1, 1};
            *(void**)(g_stbtStream + 4) = &osp, *(DWORD*)(g_stbtStream + 0x14) = T1;
            u8* b2 = StbtGet(T5);
            assert(b2 == b1 && g_stb.inPlace == 1 && g_stb.inPlaceB == T1 && g_sta.useN == 22 && g_staBlk[0].keep == 1 && g_staBlk[0].req == T5 && g_stb.liveN == 1);
            assert(StbAllIs(b2, T1, 0x5A) && StbAllIs(b2 + T1, T5 - T1, round ? STB_PAT : 0) && g_stb.reqs == 2 && g_stb.grows == 1 && g_stbWho[1].liveB == T5);
            // Realloc's release of the old SharedPointer through its patched free site: the block stays, the mark goes
            const u8 old = re[STB_REALLOC_AT + 16];
            re[STB_REALLOC_AT + 16] = 0xC3;
            StbtRun(re + STB_REALLOC_AT, StbtRegs{0, 0, 7, (DWORD)(uintptr_t)&osp, 7, 0, 0});
            assert(g_sta.useN == 22 && g_staBlk[0].keep == 0 && g_stb.frees == 0 && g_stbtA.frees == 0 && g_stb.dfree == 0);
            // an old SharedPointer that someone else holds too: Realloc will not free it, so a new block
            StbtSp nsp = {g_stbtSpVt, &g_stbtA, b2, 2, 1};
            *(void**)(g_stbtStream + 4) = &nsp, *(DWORD*)(g_stbtStream + 0x14) = T5;
            u8* b3 = StbtGet(2000000);
            assert(b3 == g_sta.base + 22 * 0x10000 && g_stb.inPlace == 1 && g_stb.growBlocked == 0);
            // the slices behind the block are taken: the ordinary path
            nsp.ref = 1;
            u8* b4 = StbtGet(1500000);
            assert(b4 && b4 != b2 && StaOwned(b4) && g_stb.growBlocked == 1 && g_stb.inPlace == 1 && g_staBlk[0].keep == 0);
            // the free of a block that was not grown in place, through the same site: freed
            memset(b4, 0x5A, 1500000);
            StbtSp fsp = {g_stbtSpVt, &g_stbtA, b4, 1, 1};
            StbtRun(re + STB_REALLOC_AT, StbtRegs{0, 0, 7, (DWORD)(uintptr_t)&fsp, 7, 0, 0});
            re[STB_REALLOC_AT + 16] = old;
            assert(g_stb.frees == 1 && g_sta.useN == 22 + 31);
            *(void**)(g_stbtStream + 4) = NULL, *(DWORD*)(g_stbtStream + 0x14) = 0;
            memset(b2 + T1, 0x5A, T5 - T1), memset(b3, 0x5A, 2000000);
            StbtPut(b3), StbtPut(b2);
            assert(g_sta.useN == 0 && g_stb.frees == 3 && g_stb.unwrittenN == 0 && g_stb.uafw == 0 && g_stbtA.allocs == 0);
        }
        // Timing: the stubs' own time by family, the pass-through overhead, and every 64th served request sampled on the game's path
        StbtReset(STB_POOL | STB_ZERO | STB_STATS | STB_TIME, 48, 128, 8, 5000);
        g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
        for (int i = 0; i < 130; i++) StbtPut(StbtGet(699320));
        StbtPut(StbtGet(1000));
        assert(g_stbTime.arenaN == 130 && g_stbTime.sampN == 2 && g_stbTime.sampSum > 0 && g_stbtA.allocs == 3 && g_stbtA.frees == 3 && g_stbTime.passN == 1);
        assert(g_stbTime.arena[1] > 0 && g_stbTime.arenaFree > 0 && g_stb.served + g_stb.fresh == 130);
        StbGrowCopy(c1, c2, 64);
        assert(g_stbTime.copyN == 1 && g_stbTime.copyB == 64);
        g_stbTime.since = StbQpc() - g_stbQpf;
        g_stbInstalled = 1, StbMemLine(2, "selftest"), g_stbInstalled = 0;
    }

    // Assemble: the block assembler's two calls change their target; its stubs against a fake heap
    {
        u8* as = (u8*)VirtualAlloc(NULL, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
        memcpy(as, kStbAsm, sizeof(kStbAsm));
        as[0x200] ^= 1;
        assert(!StbInstallAssemble(as) && !g_stbPatch[15].at);
        as[0x200] ^= 1;
        assert(StbInstallAssemble(as));
        g_stbAsmIn = true;
        assert(STB_ASM + STB_ASM_ALLOC + 5 == 0x006169AD && STB_ASM + STB_ASM_FREE == 0x00616A93 && kStbAsm[STB_ASM_ALLOC] == 0xE8 && kStbAsm[STB_ASM_FREE] == 0xE8);
        assert(STB_ASM + STB_ASM_ALLOC + 5 + *(const LONG*)(kStbAsm + STB_ASM_ALLOC + 1) == 0x004E6A20 && STB_ASM + STB_ASM_FREE + 5 + *(const LONG*)(kStbAsm + STB_ASM_FREE + 1) == 0x004E5920);
        assert(as[STB_ASM_ALLOC] == 0xE8 && (uintptr_t)as + STB_ASM_ALLOC + 5 + *(LONG*)(as + STB_ASM_ALLOC + 1) == (uintptr_t)&StbAsmAlloc);
        assert(as[STB_ASM_FREE] == 0xE8 && (uintptr_t)as + STB_ASM_FREE + 5 + *(LONG*)(as + STB_ASM_FREE + 1) == (uintptr_t)&StbAsmFree);
        for (DWORD i = 0; i < sizeof(kStbAsm); i++) assert(as[i] == kStbAsm[i] || (i > STB_ASM_ALLOC && i < STB_ASM_ALLOC + 5) || (i > STB_ASM_FREE && i < STB_ASM_FREE + 5));
        const StbMemalignFn realA = g_stbMemalign;
        const StbHeapFreeFn realF = g_stbHeapFree;
        g_stbMemalign = StbtMemalign, g_stbHeapFree = StbtHeapFree;
        StbtReset(STB_POOL | STB_ZERO | STB_ASSEMBLE | STB_STATS, 48, 128, 8, 5000);
        g_stbtHeapAllocs = g_stbtHeapFrees = 0;
        int heap = 0;
        u8* a1 = (u8*)StbAsmAlloc(&heap, NULL, 2500000, 16, 0, 0);
        assert(a1 && StaOwned(a1) && g_stbtHeapAllocs == 0 && StbAllIs(a1, 2500000, 0) && g_stbWho[1].how == 6 && g_stb.liveN == 1);
        StbAsmFree(&heap, NULL, a1);
        assert(g_sta.useN == 0 && g_stb.frees == 1 && g_stbtHeapFrees == 0);
        u8* a2 = (u8*)StbAsmAlloc(&heap, NULL, 3000, 16, 0, 0);  // small: the heap's Memalign with the site's arguments, the heap's Free
        assert(a2 && !StaOwned(a2) && g_stbtHeapAllocs == 1 && g_stbtHeapLast == &heap);
        StbAsmFree(&heap, NULL, a2);
        assert(g_stbtHeapFrees == 1);
        u8* a3 = (u8*)StbAsmAlloc(&heap, NULL, 2500000, 4096, 0, 0);  // not the call this site makes: passed through
        assert(a3 && !StaOwned(a3) && g_stbtHeapAllocs == 2);
        StbAsmFree(&heap, NULL, a3);
        InterlockedExchange(&g_stbArm, STB_POOL | STB_ZERO);  // Assemble off: the heap's
        a1 = (u8*)StbAsmAlloc(&heap, NULL, 2500000, 16, 0, 0);
        assert(a1 && !StaOwned(a1) && g_stbtHeapAllocs == 3);
        StbAsmFree(&heap, NULL, a1);
        assert(g_stbtHeapFrees == 3);
        g_stbMemalign = realA, g_stbHeapFree = realF;
        memset(g_stbPatch + 15, 0, 2 * sizeof(StbPatch));
        VirtualFree(as, 0, MEM_RELEASE);
        g_stbAsmIn = false;
    }

    // Scratch: the compressor. A foreign byte: nothing written. Then the four windows and nothing else.
    u8* comp = x + 0x400;
    memcpy(comp, kStbComp, sizeof(kStbComp));
    comp[0x100] ^= 1;
    assert(!StbInstallScratch(comp) && !g_stbPatch[3].at);
    comp[0x100] ^= 1;
    assert(!memcmp(comp, kStbComp, sizeof(kStbComp)) && StbInstallScratch(comp));
    g_stbScratchIn = true;  // as ApplyStreamBuf records it
    static const struct { DWORD at, n; } kWin[4] = {{STB_COMP_A1, 16}, {STB_COMP_A2, 19}, {STB_COMP_F1, 12}, {STB_COMP_F2, 12}};
    DWORD prev = 0;
    for (const auto& w : kWin) {
        assert(!memcmp(comp + prev, kStbComp + prev, w.at - prev) && memcmp(comp + w.at, kStbComp + w.at, w.n));
        prev = w.at + w.n;
    }
    assert(!memcmp(comp + prev, kStbComp + prev, sizeof(kStbComp) - prev));
    assert(STB_COMP + STB_COMP_A1 + 16 == 0x004EC122 && STB_COMP + STB_COMP_A2 + 19 == 0x004EC140 && STB_COMP + STB_COMP_F1 + 12 == 0x004EC1C5 && STB_COMP + STB_COMP_F2 + 12 == 0x004EC1DA);
    assert(comp[STB_COMP_A1 + 11] == 0xE8 && (uintptr_t)comp + STB_COMP_A1 + 16 + *(LONG*)(comp + STB_COMP_A1 + 12) == (uintptr_t)&StbScratchAlloc);  // the calls end where the game's ended
    assert(comp[STB_COMP_A2 + 14] == 0xE8 && (uintptr_t)comp + STB_COMP_A2 + 19 + *(LONG*)(comp + STB_COMP_A2 + 15) == (uintptr_t)&StbScratchAlloc);
    assert(!memcmp(comp + STB_COMP_A2, kStbComp + STB_COMP_A2 + 2, 7));  // lea edx,[ecx*4+4] kept, moved to the front
    for (DWORD f : {STB_COMP_F1, STB_COMP_F2}) assert(comp[f] == 0x57 && comp[f + 1] == 0x51 && comp[f + 2] == 0xE8 && (uintptr_t)comp + f + 7 + *(LONG*)(comp + f + 3) == (uintptr_t)&StbSiteFree);
    // each window run on its own (a ret behind it; the copy is not used as a whole afterwards)
    for (const auto& w : kWin) comp[w.at + w.n] = 0xC3;
    g_stbScratchName = "scratch";
    StbtReset(STB_POOL | STB_ZERO | STB_SCRATCH | STB_STATS, 48, 128, 8, 5000);
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
    u8* heads = (u8*)StbtRunC(comp + STB_COMP_A1, &g_stbtA, 0);
    u8* links = (u8*)StbtRunC(comp + STB_COMP_A2, &g_stbtA, 0x1FFFF);  // (mask + 1) * 4 = 0x80000 bytes
    assert(heads && links && StaOwned(heads) && StaOwned(links) && g_sta.useN == 4 + 8 && g_stbtA.allocs == 0 && StbAllIs(links, 0x80000, 0));
    assert(g_stbWho[1].at == (DWORD)(uintptr_t)comp + STB_COMP_A1 + 16 && g_stbWho[1].how == 4 && g_stbWho[1].maxReq == 0x40000);
    assert(g_stbWho[2].at == (DWORD)(uintptr_t)comp + STB_COMP_A2 + 19 && g_stbWho[2].maxReq == 0x80000);
    memset(links, 0x44, 0x80000);
    StbtRunC(comp + STB_COMP_F1, &g_stbtA, (DWORD)(uintptr_t)links);
    StbtRunC(comp + STB_COMP_F2, &g_stbtA, (DWORD)(uintptr_t)heads);
    assert(g_sta.useN == 0 && g_stb.frees == 2 && g_stbtA.frees == 0 && g_stbWho[2].frees == 1);
    u8* small = (u8*)StbtRunC(comp + STB_COMP_A2, &g_stbtA, 0x3FFF);  // the short window's 0x10000 bytes: under MinKB, the game's allocator with the game's name
    assert(small && !StaOwned(small) && g_stbtA.allocs == 1 && !strcmp(g_stbtA.lastName, "scratch"));
    StbtRunC(comp + STB_COMP_F1, &g_stbtA, (DWORD)(uintptr_t)small);
    assert(g_stbtA.frees == 1);
    // Scratch off (Pool on): the compressor's requests are the game's, uncounted; a table handed out before still comes home
    links = (u8*)StbtRunC(comp + STB_COMP_A2, &g_stbtA, 0x1FFFF);
    const u64 reqs = g_stb.reqs;
    StbWatch(STB_POOL | STB_ZERO | STB_SCRATCH | STB_STATS, true);  // Apex Radiance turned up
    assert(g_stbArm == (STB_POOL | STB_ZERO | STB_STATS));
    u8* g2 = (u8*)StbtRunC(comp + STB_COMP_A2, &g_stbtA, 0x1FFFF);
    assert(g2 && !StaOwned(g2) && g_stb.reqs == reqs && g_stbtA.allocs == 2);
    StbtRunC(comp + STB_COMP_F1, &g_stbtA, (DWORD)(uintptr_t)g2);
    StbtRunC(comp + STB_COMP_F1, &g_stbtA, (DWORD)(uintptr_t)links);
    assert(g_stbtA.frees == 2 && g_sta.useN == 0);
    StbtRunC(comp + STB_COMP_F2, &g_stbtA, 0);  // a NULL table (the game's allocation failed): the game's free, as before
    assert(g_stbtA.frees == 3);
    g_stbtA.live = 0;

    // Package: the package writer's three functions. A foreign byte in any: nothing written. Then the six windows.
    u8* const fn[3] = {x + 0x600, x + 0xA00, x + 0xB00};
    for (int i = 0; i < 3; i++) memcpy(fn[i], kStbPkgFn[i], kStbPkgLen[i]);
    fn[1][0x40] ^= 1;
    assert(!StbInstallPackage(fn) && !g_stbPatch[7].at && !memcmp(fn[0], kStbPkg0, sizeof(kStbPkg0)) && !memcmp(fn[2], kStbPkg2, sizeof(kStbPkg2)));
    fn[1][0x40] ^= 1;
    assert(StbInstallPackage(fn));
    g_stbPkgIn = true;  // as ApplyStreamBuf records it
    for (int f = 0; f < 3; f++)  // only the windows changed
        for (DWORD i = 0; i < kStbPkgLen[f]; i++) {
            bool in = false;
            for (const StbSite& k : kStbPkgSite) in |= k.fn == f && i >= k.at && i < (DWORD)k.at + k.n;
            assert(in || fn[f][i] == kStbPkgFn[f][i]);
        }
    for (int i = 0; i < 6; i++) {
        const StbSite& k = kStbPkgSite[i];
        u8* const at = fn[k.fn] + k.at;
        assert(at[k.call] == 0xE8 && (uintptr_t)at + k.call + 5 + *(LONG*)(at + k.call + 1) == (i < 4 ? (uintptr_t)&StbSiteFree : (uintptr_t)&StbPkgAlloc));
        assert(!memcmp(at, kStbPkgFn[k.fn] + k.at, 3));  // each window still starts with the game's `mov ecx,[obj+10h]`
        if (i >= 4) assert(k.call + 5 == k.n);            // an allocation's call ends where the game's ended
    }
    assert(STB_PKG_FN[0] + 0x1D4 + 18 == 0x004A7216 && STB_PKG_FN[2] + 0x0D3 + 20 == 0x004A89F7 && STB_PKG_FN[0] + 0x275 == 0x004A72A5 && STB_PKG_FN[1] + 0x08E == 0x004A834E &&
           STB_PKG_FN[2] + 0x1D1 == 0x004A8AE1 && STB_PKG_FN[2] + 0x1E1 == 0x004A8AF1);
    StbtReset(STB_POOL | STB_ZERO | STB_PACKAGE | STB_STATS, 48, 128, 8, 5000);
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
    DWORD obj[5] = {0, 0, 0, 0, (DWORD)(uintptr_t)&g_stbtA};  // the package: +0x10 its allocator
    auto run = [&](int i, const StbtRegs& r) {  // window i on its own: a ret behind it for the call (two windows touch)
        u8* const at = fn[kStbPkgSite[i].fn] + kStbPkgSite[i].at;
        const u8 old = at[kStbPkgSite[i].n];
        at[kStbPkgSite[i].n] = 0xC3;
        void* const v = StbtRun(at, r);
        at[kStbPkgSite[i].n] = old;
        return v;
    };
    const DWORD O = (DWORD)(uintptr_t)obj, RS = 1000000;
    // 0x4A89E3: esi = the package, edi = the record's size; the buffer is size + 0x10 bytes
    u8* rd = (u8*)run(5, StbtRegs{0, 0, 0, O, RS, 0, 0});
    assert(rd && StaOwned(rd) && g_staBlk[(rd - g_sta.base) >> 16].req == RS + 0x10 && g_stbWho[1].at == (DWORD)(uintptr_t)fn[2] + 0x0D3 + 20 && g_stbWho[1].how == 5);
    // 0x4A7204: ebx = the package, eax = the codec's bound
    u8* pr = (u8*)run(4, StbtRegs{1200000, 0, O, 0x51515151, 7, 0, 0});
    assert(pr && StaOwned(pr) && pr != rd && g_staBlk[(pr - g_sta.base) >> 16].req == 1200000 && g_stbWho[2].at == (DWORD)(uintptr_t)fn[0] + 0x1D4 + 18 && g_stbtA.allocs == 0);
    // the commit's two frees take the pointers from its frame: [esp+18h] the packed buffer, [esp+1Ch] the record's
    run(2, StbtRegs{0, 0, 0, O, 7, (DWORD)(uintptr_t)pr, (DWORD)(uintptr_t)rd});
    assert(g_stb.frees == 1 && g_stbWho[2].frees == 1 && g_sta.useN == 16);
    run(3, StbtRegs{0, 0, 0, O, 7, (DWORD)(uintptr_t)pr, (DWORD)(uintptr_t)rd});
    assert(g_stb.frees == 2 && g_stbWho[1].frees == 1 && g_sta.useN == 0 && g_stbtA.frees == 0);
    // the two early frees of the packed buffer: eax = the pointer, ebx / esi = the package
    pr = (u8*)run(4, StbtRegs{1200000, 0, O, 0x51515151, 7, 0, 0});
    run(0, StbtRegs{(DWORD)(uintptr_t)pr, 0, O, 0x51515151, 7, 0, 0});
    pr = (u8*)run(4, StbtRegs{1200000, 0, O, 0x51515151, 7, 0, 0});
    run(1, StbtRegs{(DWORD)(uintptr_t)pr, 0, 7, O, 7, 0, 0});
    assert(g_stb.frees == 4 && g_sta.useN == 0 && g_stb.dfree == 0 && g_stbtA.frees == 0);
    // a small record: the game's allocator with the game's name, the game's free; Package off: the game's, uncounted
    u8* sm = (u8*)run(5, StbtRegs{0, 0, 0, O, 5000, 0, 0});
    assert(sm && !StaOwned(sm) && g_stbtA.allocs == 1 && (DWORD)(uintptr_t)g_stbtA.lastName == 0x00FB26A4);
    run(3, StbtRegs{0, 0, 0, O, 7, 0, (DWORD)(uintptr_t)sm});
    assert(g_stbtA.frees == 1);
    rd = (u8*)run(5, StbtRegs{0, 0, 0, O, RS, 0, 0});
    InterlockedExchange(&g_stbArm, STB_POOL | STB_ZERO | STB_STATS);
    const u64 reqs2 = g_stb.reqs;
    pr = (u8*)run(4, StbtRegs{1200000, 0, O, 0x51515151, 7, 0, 0});
    assert(pr && !StaOwned(pr) && g_stb.reqs == reqs2 && g_stbtA.allocs == 2 && (DWORD)(uintptr_t)g_stbtA.lastName == 0x00FB257C);
    run(2, StbtRegs{0, 0, 0, O, 7, (DWORD)(uintptr_t)pr, (DWORD)(uintptr_t)rd});
    run(3, StbtRegs{0, 0, 0, O, 7, (DWORD)(uintptr_t)pr, (DWORD)(uintptr_t)rd});  // handed out while Package was on: still comes home
    assert(g_stbtA.frees == 2 && g_sta.useN == 0 && g_stbtA.live == 0);
    // the watch: the ini's switches apply (Scratch only where its sites are in); a site another module rewrote stops the module
    const int mask = STB_POOL | STB_ZERO;
    StbWatch(mask | STB_STATS | STB_SCRATCH, false);
    assert(g_stbArm == (mask | STB_STATS | STB_SCRATCH) && !g_stbDead);
    g_stbScratchIn = false;
    StbWatch(STB_SCRATCH | STB_ZERO | STB_STATS, false);  // Scratch asked for, not installed, Pool off: Stats only
    assert(g_stbArm == STB_STATS);
    g_stbScratchIn = true;
    StbWatch(STB_PACKAGE | STB_ZERO, false);
    assert(g_stbArm == (STB_PACKAGE | STB_ZERO));
    g_stbPkgIn = false;
    StbWatch(STB_PACKAGE | STB_ZERO | STB_STATS, false);
    assert(g_stbArm == STB_STATS);
    g_stbPkgIn = true;
    StbWatch(STB_GROW | STB_TIME, false);  // Grow and Timing need no arena part
    assert(g_stbArm == (STB_GROW | STB_TIME) && g_stbTime.since);
    g_stbGrowIn = false;
    StbWatch(STB_GROW | STB_STATS, false);
    assert(g_stbArm == STB_STATS);
    g_stbGrowIn = true;
    comp[STB_COMP_F2 + 1] ^= 0x40;
    StbWatch(mask, false);
    assert(g_stbDead && g_stbArm == 0);
    StbWatch(mask, false);
    assert(g_stbArm == 0);
    g_stbDead = false, g_stbFrames = false, g_stbScratchIn = false, g_stbPkgIn = false, g_stbGrowIn = false, g_stbReallocFreeRet = NULL;
    g_stbScratchName = (const char*)(uintptr_t)0x00FB9030;
    memset(g_stbPatch, 0, sizeof(g_stbPatch));
    StbtReset(0, 48, 128, 8, 5000);
    VirtualFree(x, 0, MEM_RELEASE);
}

// Per request, with the block written once from end to end as a decoder or a read fills it: the game's path today (one
// VirtualAlloc + release per buffer; EA's direct block asks for the request + 0x27 rounded to pages) against the arena:
// mode 1 warm with Zero=1, 2 warm with Zero=0, 3 cold (every block committed and decommitted: the arena's worst case).
static double StbtBenchOne(DWORD n, int mode, const u8* src, int iters) {
    StbtReset(mode == 1 || mode == 3 ? STB_POOL | STB_ZERO : mode == 2 ? STB_POOL : 0, 48, 128, 8, mode == 3 ? 1 : 5000);
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
            if (mode == 3) g_stbtNow += 10, StbTick(g_stbtNow);
        }
    }
    const double us = (double)(StbQpc() - t0) * 1e6 / (double)g_stbQpf / iters;
    if (mode == 1 || mode == 2) assert(g_stb.served == (u64)iters - 1 && g_stb.fresh == 1);
    if (mode == 3) assert(g_stb.fresh == (u64)iters && g_stb.decommits == (DWORD)iters);
    return us;
}
static void StbtBench() {
    static const DWORD kN[3] = {1398384, 699320, 0x80000};
    for (DWORD n : kN) {
        u8* src = (u8*)malloc(n);
        for (DWORD i = 0; i < n; i++) src[i] = (u8)(i * 31 + (i >> 8));
        double us[4][2];
        for (int mode = 0; mode < 4; mode++) us[mode][0] = StbtBenchOne(n, mode, src, 300), us[mode][1] = StbtBenchOne(n, mode, NULL, 300);
        printf("bench streambuf %lu KB x300, us per buffer (filled once / not touched): game path %.1f / %.1f; arena Zero=1 %.1f / %.1f; arena Zero=0 %.1f / %.1f; "
               "arena cold (commit + decommit each time) %.1f / %.1f\n",
               n >> 10, us[0][0], us[0][1], us[1][0], us[1][1], us[2][0], us[2][1], us[3][0], us[3][1]);
        free(src);
    }
    // the carve itself: a best-fit search in a 48 MB arena with every other 1408 KB place taken
    StbtReset(STB_POOL, 48, 128, 8, 5000);
    static u8* keep[40];
    DWORD k = 0;
    for (; k < 34; k++) keep[k] = StbtGet(1398384);
    for (DWORD i = 0; i < k; i += 2) StbtPut(keep[i]), keep[i] = NULL;
    const LONGLONG t0 = StbQpc();
    for (int i = 0; i < 20000; i++) StbtPut(StbtGet(1398384));
    printf("bench streambuf carve: %.2f us per request + free in a 48 MB arena with 17 blocks live and 17 holes (Zero=0, nothing touched)\n",
           (double)(StbQpc() - t0) * 1e6 / (double)g_stbQpf / 20000);
    for (DWORD i = 0; i < k; i++)
        if (keep[i]) StbtPut(keep[i]);
    StbtReset(0, 48, 128, 8, 5000);
}

// Shared: the arena as a view of a named section; a second view stands in for the 64-bit host.
static void StbtShared() {
    const DWORD T1 = 699320, K = 0x10000, S1 = 11;
    wchar_t name[64];
    StbSharedName(name, 64, GetCurrentProcessId());
    g_stbShared = true;
    StbtReset(STB_POOL | STB_ZERO | STB_PREZERO, 4, 128, 2, 5000);
    MEMORY_BASIC_INFORMATION mbi;
    assert(g_sta.sec && g_sta.how == 1 && VirtualQuery(g_sta.base, &mbi, sizeof(mbi)) && mbi.Type == MEM_MAPPED && mbi.AllocationBase == g_sta.base && StbtOsCom() == 0);
    // the other side: opened by name, mapped anywhere; an offset in the arena is the same byte in both views
    HANDLE h2 = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    assert(h2);
    u8* v2 = (u8*)MapViewOfFile(h2, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0);
    assert(v2 && v2 != g_sta.base);
    u8* b = StbtGet(T1);
    assert(b == g_sta.base && StbtOsCom() == S1 * K && StbAllIs(b, T1, 0));
    memset(b, 0x3C, T1);
    const DWORD off = (DWORD)(b - g_sta.base);
    assert(StbAllIs(v2 + off, T1, 0x3C));  // read in place
    memset(v2 + off + 100, 0xC3, 1000);     // filled in place
    assert(StbAllIs(b + 100, 1000, 0xC3) && b[99] == 0x3C && b[1100] == 0x3C);
    HANDLE hx = OpenFileMappingW(FILE_MAP_EXECUTE | FILE_MAP_READ, FALSE, name);  // not executable
    assert(!hx || !MapViewOfFile(hx, FILE_MAP_EXECUTE | FILE_MAP_READ, 0, 0, 0));
    if (hx) CloseHandle(hx);
    // the block comes back; PreZero fills it; idle: reset instead of decommit, still committed, dirty again, never filled twice
    StbtPut(b);
    assert(StbZeroSome(g_stbArm) == S1 && g_staSt[0] == (STA_COM | STA_ZERO) && StbAllIs(v2 + off, S1 * K, 0));
    g_stbtNow += 6000, StbTick(g_stbtNow);
    assert(g_staSt[0] == (STA_COM | STA_RESET) && g_sta.comN == S1 && g_stb.decommits == 0 && g_stb.resets + g_stb.resetFail == 1 && StbtOsCom() == S1 * K);
    printf("streambuf shared: MEM_RESET on the view %s\n", g_stb.resets ? "works" : "FAILS (idle slices stay committed and resident)");
    g_stbtNow += 6000, StbTick(g_stbtNow);
    assert(g_stb.resets + g_stb.resetFail == 1 && StbZeroSome(g_stbArm) == 0);
    b[5] = 0x7E;  // whatever the pages hold after a reset: the next request is filled on the caller's thread
    u8* c = StbtGet(T1);
    assert(c == b && StbAllIs(c, T1, 0) && g_stb.zeroB == T1 && g_staSt[0] == (STA_USED | STA_COM | STA_HEAD));
    StbtPut(c);
    // the host is gone (its view and handle closed): the arena works on
    UnmapViewOfFile(v2), CloseHandle(h2);
    c = StbtGet(T1);
    memset(c, 1, T1);
    StbtPut(c);
    // the name is taken by someone else: refused, the arena is private memory with the same behaviour
    StbtReset(0, 4, 128, 2, 5000);
    StaRelease();
    HANDLE squat = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, 4096, name);
    assert(squat);
    StbtReset(STB_POOL | STB_ZERO, 4, 128, 2, 5000);
    assert(g_sta.base && !g_sta.sec && VirtualQuery(g_sta.base, &mbi, sizeof(mbi)) && mbi.Type == MEM_PRIVATE);
    c = StbtGet(T1);
    assert(c == g_sta.base);
    StbtPut(c);
    g_stbtNow += 6000, StbTick(g_stbtNow);
    assert(g_stb.decommits == 1 && g_sta.comN == 0);
    CloseHandle(squat);
    g_stbShared = false;
    StbtReset(0, 4, 128, 2, 5000);  // back to a private arena for the tests that follow
    assert(!g_sta.sec);
}

static void TestStreamBuf() {
    g_stbNow = StbtNow, g_stbWhoFn = StbtWhoFn;
    // the game's bytes are not in this process: nothing matches, nothing is written
    assert(!StbInstallSites((u8*)(uintptr_t)STB_CTOR, (u8*)(uintptr_t)STB_REL, (u8*)(uintptr_t)STB_REALLOC, (const u8*)(uintptr_t)STB_SETSIZE, (const u8*)(uintptr_t)STB_WRITE,
                           (const u8*)(uintptr_t)STB_SETPOS));
    assert(!StbInstallScratch((u8*)(uintptr_t)STB_COMP));
    // not armed: every stub is the game's call
    g_stbtA.allocs = g_stbtA.frees = g_stbtA.live = 0;
    u8* p = StbtGet(1398384);
    assert(p && !StaOwned(p) && g_stbtA.allocs == 1 && g_stb.reqs == 0);
    StbtPut(p);
    void* q = StbScratchAlloc(0x80000, &g_stbtA);
    StbSiteFree(q, &g_stbtA);
    q = StbPkgAlloc(1200000, &g_stbtA, "p");
    StbSiteFree(q, &g_stbtA);
    assert(q && g_stbtA.frees == 3 && g_stbtA.allocs == 3 && g_stb.reqs == 0);
    u8* const none[3] = {(u8*)(uintptr_t)STB_PKG_FN[0], (u8*)(uintptr_t)STB_PKG_FN[1], (u8*)(uintptr_t)STB_PKG_FN[2]};
    assert(!StbInstallPackage(none) && !StbInstallGrow((u8*)(uintptr_t)STB_WRITE, (u8*)(uintptr_t)STB_REALLOC) && !StbInstallAssemble((u8*)(uintptr_t)STB_ASM));
    StbtPolicy();
    StbtPlace();
    StbtCreator();
    StbtBasic();
    StbtShared();
    StbtModel();
    StbtThreads();
    StbtPatch();
    StbtBench();
    StbtReset(0, 48, 128, 8, 5000);
    StaRelease();  // the test's arena goes; the module starts as in a new process
    g_stbNow = StbNowMs, g_stbWhoFn = StbCreator;
    printf("streambuf ok\n");
}
