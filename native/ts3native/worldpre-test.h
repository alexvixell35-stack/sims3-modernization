// docs/676 worldpre.cpp / waterjps-core.h / worldpre-core.h: [WorldPrecompute]. Included by selftest.cpp and
// worldpre-host-test.cpp (which runs the long version on Linux).
// - the scan table against the walk (WaterJump without a table, itself checked against the game's code by waterjps-test.cpp)
//   on random grids: every direction, with and without the start / goal boxes, goals on and off the ray, rows that run on
//   into the next row, tiny grids, a sea wider than an entry's longest run;
// - a table updated cell by cell after byte changes == a table built from scratch;
// - the slot manager: refresh classes (unchanged / cells / built), two slots and the least recently used one, a search
//   that cannot take the lock, a search left by an unwind, the scope gate, a grid too large, no memory;
// - the jump's gate (same thread, same grids, the game's callback only), shadow counting;
// - the search hook and the probe's repeat counts; the world probe's outermost frame; the post pass bookkeeping.

enum { WXT_MAXW = 320, WXT_MAXCELLS = WXT_MAXW * WXT_MAXW };
static u8 g_wxtG1[WXT_MAXCELLS], g_wxtG2[WXT_MAXCELLS], g_wxtH1[4096], g_wxtH2[4096];
static unsigned short g_wxtTab[4 * WXT_MAXCELLS], g_wxtFresh[4 * WXT_MAXCELLS];
static int g_wxtDummyCb;
static DWORD g_wxtSeed = 2468;
static DWORD WxtRnd(DWORD n) { g_wxtSeed = g_wxtSeed * 1103515245 + 12345; return ((g_wxtSeed >> 8) & 0xFFFFFF) % n; }
static long g_wxtJumps, g_wxtScans, g_wxtSlow, g_wxtUpdates;

// kind 0: open sea with a land last column (the game's grid shape); 1: random land and marks; 2: no land at all (rows run on)
static void WxtFill(u8* g1, u8* g2, DWORD w, DWORD h, int kind) {
    const DWORD land = kind == 1 ? 5 + WxtRnd(40) : 0, marks = kind == 1 ? WxtRnd(8) : kind == 0 ? WxtRnd(2) : 0;
    for (DWORD i = 0; i < w * h; i++) g1[i] = WxtRnd(100) >= land, g2[i] = WxtRnd(100) < marks;
    if (kind == 0)
        for (DWORD y = 0; y < h; y++) g1[y * w + w - 1] = 0;
    if (kind == 2) return;
    for (int b = (int)WxtRnd(10); b > 0; b--) {  // islands and marked patches
        const DWORD cx = WxtRnd(w), cy = WxtRnd(h), r = 1 + WxtRnd(9);
        const bool mark = WxtRnd(3) == 0;
        for (DWORD y = cy > r ? cy - r : 0; y < h && y <= cy + r; y++)
            for (DWORD x = cx > r ? cx - r : 0; x < w && x <= cx + r; x++)
                if (mark) g2[y * w + x] = 1; else g1[y * w + x] = 0;
    }
}

// every direction and both flag values from `cell`: the table (tab over g1 / g2) against the walk
static void WxtCheckCell(const u8* g1, const u8* g2, const unsigned short* tab, DWORD w, DWORD h, DWORD cell, DWORD goal, bool boxes, const int* box) {
    for (int d = 0; d < 8; d++)
        for (int flag = 0; flag < 2; flag++) {
            WjCtx a = WsCtx0(g1, g2, w, h), b = WsCtx0(g1, g2, w, h);
            if (boxes) {
                a.cb = b.cb = &g_wxtDummyCb, a.nearCb = b.nearCb = true;
                for (int i = 0; i < 4; i++) a.o[4 + i] = b.o[4 + i] = box[i];
            }
            assert(WjTabOk(b));
            b.tab = tab;
            if (goal < w * h) b.goalX = goal % w, b.goalY = goal / w;
            const DWORD ra = WaterJump(a, goal, cell, d, flag != 0), rb = WaterJump(b, goal, cell, d, flag != 0);
            assert(ra == rb);
            assert(a.cells == b.cells);  // the table path counts the cells it skipped
            g_wxtJumps++, g_wxtScans += (long)b.tabScans, g_wxtSlow += (long)b.tabSlow;
        }
}

static void WxtCheckGrid(const u8* g1, const u8* g2, const unsigned short* tab, DWORD w, DWORD h, int samples) {
    const DWORD cells = w * h;
    for (int s = 0; s < samples; s++) {
        const DWORD cell = WxtRnd(cells);
        // goal: none, anywhere, on the cell's row, on its column, the cell itself
        const DWORD pick = WxtRnd(5);
        const DWORD goal = pick == 0 ? 0xFFFFFFFF : pick == 1 ? WxtRnd(cells) : pick == 2 ? (cell / w) * w + WxtRnd(w) : pick == 3 ? WxtRnd(h) * w + cell % w : cell;
        // boxes: around random cells, around the ray's row or column, at the grid's corners, partly outside the grid
        int box[4] = {0, 0, 0, 0};
        const DWORD bp = WxtRnd(4);
        box[0] = bp == 0 ? (int)WxtRnd(w) : bp == 1 ? (int)(cell % w) + (int)WxtRnd(9) - 4 : bp == 2 ? 0 : (int)w + (int)WxtRnd(6);
        box[1] = bp == 0 ? (int)WxtRnd(h) : bp == 1 ? (int)WxtRnd(h) : bp == 2 ? (int)h - 1 : (int)(cell / w) + (int)WxtRnd(9) - 4;
        box[2] = (int)WxtRnd(w), box[3] = (int)(cell / w) + (int)WxtRnd(7) - 3;
        for (int i = 0; i < 4; i++)
            if (box[i] < 0) box[i] = 0;
        WxtCheckCell(g1, g2, tab, w, h, cell, goal, false, box);
        WxtCheckCell(g1, g2, tab, w, h, cell, goal, true, box);
    }
}

static bool WxtSameTab(DWORD cells) {
    for (DWORD i = 0; i < 4 * cells; i++)
        if (g_wxtTab[i] != g_wxtFresh[i]) return false;
    return true;
}

static void TestWsCore(int grids, int samples, bool bigSea) {
    u8* const g1 = g_wxtG1;
    u8* const g2 = g_wxtG2;
    // entry arithmetic on a one-row sea: 0 1 2 3 4, all water, no marks
    {
        const DWORD w = 5, h = 1;
        for (DWORD i = 0; i < w; i++) g1[i] = 1, g2[i] = 0;
        const WjCtx c0 = WsCtx0(g1, g2, w, h);
        WsBuild(c0, g_wxtTab);
        assert(g_wxtTab[1 * w + 0] == 5 && g_wxtTab[1 * w + 4] == 1);  // east from 0: four water cells, the fifth step leaves the grid
        assert(g_wxtTab[3 * w + 0] == 1 && g_wxtTab[3 * w + 4] == 5);  // west
        assert(g_wxtTab[0 * w + 2] == 1 && g_wxtTab[2 * w + 2] == 1);  // north, south: outside at once
        g2[3] = 1;                                                     // a mark: a jump point for the cells west of it ...
        WsUpdateCell(c0, g_wxtTab, 3);
        WsBuild(c0, g_wxtFresh);
        assert(WxtSameTab(w * h));
        assert(g_wxtTab[1 * w + 2] == (WS_HIT | 1));                   // ... and the cell before it stops there
        WjCtx c = WsCtx0(g1, g2, w, h);
        c.tab = g_wxtTab;
        assert(WjScanTab(c, 0xFFFFFFFF, 2, 2, 0, 2) == 3 && c.tabScans == 1);
        g1[1] = 0;                                                     // land: the cell east of it sees "not water" at once going west
        WsUpdateCell(c0, g_wxtTab, 1);
        WsBuild(c0, g_wxtFresh);
        assert(WxtSameTab(w * h) && g_wxtTab[3 * w + 2] == 1);
    }
    // boxes the table path does not take
    {
        WjCtx c = WsCtx0(g1, g2, 8, 8);
        assert(WjTabOk(c));                                            // no callback
        c.cb = &g_wxtDummyCb;
        assert(!WjTabOk(c));                                           // a callback that is not the game's
        c.nearCb = true;
        assert(WjTabOk(c));
        c.o[5] = -1;
        assert(!WjTabOk(c));                                           // a centre the 32-bit compare could wrap on
        c.o[5] = 0x40000000;
        assert(!WjTabOk(c));
        const WjCtx one = WsCtx0(g1, g2, 1, 8);
        assert(!WjTabOk(one));
    }
    // random grids
    for (int gi = 0; gi < grids; gi++) {
        const bool tiny = gi < grids / 4;
        const DWORD w = 2 + WxtRnd(tiny ? 7 : 150), h = 1 + WxtRnd(tiny ? 7 : 150);
        WxtFill(g1, g2, w, h, gi % 3);
        const WjCtx c0 = WsCtx0(g1, g2, w, h);
        WsBuild(c0, g_wxtTab);
        WxtCheckGrid(g1, g2, g_wxtTab, w, h, tiny ? samples / 4 : samples);
        // change bytes, update cell by cell after all bytes are in place, compare with a fresh build, check jumps again
        for (int round = 0; round < 3; round++) {
            DWORD changed[WS_INCMAX] = {};
            const DWORD n = 1 + WxtRnd(round == 1 && (gi & 1) ? WS_INCMAX : 24);
            for (DWORD i = 0; i < n; i++) {
                const DWORD at = round == 2 && i ? (changed[0] + WxtRnd(3) * w + WxtRnd(3)) % (w * h) : WxtRnd(w * h);  // round 2: a cluster
                changed[i] = at;
                if (WxtRnd(2)) g1[at] = !g1[at]; else g2[at] = !g2[at];
            }
            if (gi & 1) {  // odd grids: the batch (every touched column once), as WsRefresh does it
                DWORD cols[WS_COLWORDS] = {};
                WsUpdateCells(c0, g_wxtTab, changed, n, cols), g_wxtUpdates += (long)n;
            } else
                for (DWORD i = 0; i < n; i++) WsUpdateCell(c0, g_wxtTab, changed[i]), g_wxtUpdates++;
            WsBuild(c0, g_wxtFresh);
            assert(WxtSameTab(w * h));
        }
        WxtCheckGrid(g1, g2, g_wxtTab, w, h, tiny ? samples / 8 : samples / 3);
    }
    assert(g_wxtJumps > 0 && g_wxtScans > 0 && g_wxtUpdates > 0);
    if (!bigSea) return;
    // a sea with no land: a row ray runs on through every row, longer than an entry can hold -> "unknown", walked
    const DWORD w = 300, h = 300;
    for (DWORD i = 0; i < w * h; i++) g1[i] = 1, g2[i] = 0;
    const WjCtx c0 = WsCtx0(g1, g2, w, h);
    WsBuild(c0, g_wxtTab);
    assert(g_wxtTab[1 * w * h + 0] == 0);                              // east from cell 0: 89,999 cells
    assert(g_wxtTab[1 * w * h + w * h - 100] == 100);                  // near the end the run fits
    const long slow0 = g_wxtSlow;
    WxtCheckGrid(g1, g2, g_wxtTab, w, h, 40);
    assert(g_wxtSlow > slow0);
    g2[w * h - 50] = 1;                                                // a change at the far end reaches back along the whole ray
    WsUpdateCell(c0, g_wxtTab, w * h - 50);
    WsBuild(c0, g_wxtFresh);
    assert(WxtSameTab(w * h));
    WxtCheckGrid(g1, g2, g_wxtTab, w, h, 20);
}

static void TestWxRep() {
    static WxRep t;
    memset(&t, 0, sizeof(t));
    assert(WxRepNote(&t, 77, 5) == 0 && t.used == 1);
    assert(WxRepNote(&t, 77, 5) == 2);                 // seen, nothing moved
    assert(WxRepNote(&t, 77, 6) == 1);                 // seen, the counter moved since
    assert(WxRepNote(&t, 77, 6) == 2 && t.e[(DWORD)(77ull >> 20) & (WX_REPN - 1)].n == 4);
    assert(WxRepNote(&t, 0, 1) == 0 && WxRepNote(&t, 0, 1) == 2 && WxRepNote(&t, 1, 1) == 2);  // key 0 is kept as key 1
    // keys that share a home slot: WX_REPPROBE are kept, the next is counted as not kept
    for (DWORD i = 0; i < (DWORD)WX_REPPROBE + 3; i++) {
        const unsigned long long key = ((unsigned long long)(i + 1) << 40) | (500ull << 20);
        assert(WxRepNote(&t, key, 0) == (i < (DWORD)WX_REPPROBE ? 0 : -1));
    }
    assert(t.full == 3 && t.used == 2 + (DWORD)WX_REPPROBE);
    assert(WxMix(1, 2) != WxMix(2, 1) && WxMix(0, 0) == 0);
}

static u8 g_wxtHeap[4 * 10 * 4096];
static size_t g_wxtHeapAt;
static bool g_wxtAllocFail;
static void* WxtAlloc(size_t bytes) {
    if (g_wxtAllocFail || g_wxtHeapAt + bytes > sizeof(g_wxtHeap)) return NULL;
    void* const p = g_wxtHeap + g_wxtHeapAt;
    g_wxtHeapAt += bytes;
    return p;
}
static LONG g_wxtScopeVal;
static LONG WxtScope(bool* worker) { return *worker = true, g_wxtScopeVal; }
static int g_wxtOrigCalls;
static bool g_wxtOrigSawTab;
static DWORD __cdecl WxtSearchOrig(const u8* const*, const u8* const*, DWORD, DWORD, DWORD, const DWORD*, const DWORD*, void*, void*) {
    g_wxtOrigCalls++;
    g_wxtOrigSawTab = t_wx.tab != NULL;
    return 1;
}
// jumps from sample cells through the gate, as WaterJumpHook does: the table's answers equal the walk's
static void WxtJumpsThroughGate(const u8* g1, const u8* g2, DWORD w, DWORD h, int want) {
    for (int s = 0; s < 40; s++) {
        const DWORD cell = WxtRnd(w * h), goal = WxtRnd(w * h);
        for (int d = 0; d < 8; d++) {
            WjCtx c = WsCtx0(g1, g2, w, h), walk = WsCtx0(g1, g2, w, h);
            c.cb = walk.cb = &g_wxtDummyCb, c.nearCb = walk.nearCb = true;
            c.o[4] = walk.o[4] = (int)(cell % w), c.o[5] = walk.o[5] = (int)(cell / w);
            c.o[6] = walk.o[6] = (int)(goal % w), c.o[7] = walk.o[7] = (int)(goal / w);
            assert(WxJumpTab(c, goal) == want && (c.tab != NULL) == (want != 0));
            assert(WaterJump(c, goal, cell, d, false) == WaterJump(walk, goal, cell, d, false));
            if (want) WxJumpDone(c);
        }
    }
}

static void TestWxManager() {
    WorldPreCounters& k = ts3native_worldpre;
    const bool savedAll = g_wxAll;
    LONG (*const savedScope)(bool*) = g_wxScope;
    const WxSearchFn savedOrig = g_wxSearchOrig;
    const LONG savedSnOn = g_snOn, savedSeq = g_snWriteSeq;
    memset((void*)&k, 0, sizeof(k));
    memset(g_wxSlot, 0, sizeof(g_wxSlot));
    memset(g_wxRep, 0, sizeof(g_wxRep));
    memset(&t_wx, 0, sizeof(t_wx));
    g_wxAlloc = WxtAlloc, g_wxtHeapAt = 0, g_wxtAllocFail = false;
    g_wxAll = true, k.wxSlots = 2;
    const DWORD w = 40, h = 30, cells = w * h;
    u8* const a1 = g_wxtG1;
    u8* const a2 = g_wxtG2;
    WxtFill(a1, a2, w, h, 0);
    WxtFill(g_wxtH1, g_wxtH2, w, h, 1);

    // outside a search no jump takes a table
    WxtJumpsThroughGate(a1, a2, w, h, 0);
    // first search over a pair: built
    assert(WxTableBegin(a1, a2, w, h, 1) && t_wx.tab && t_wx.held && k.wxSearches == 1 && k.wxTabSearches == 1 && k.wxFull == 1);
    assert(k.wxMemKb == (LONG)(cells / 1024 * 10));
    WxtJumpsThroughGate(a1, a2, w, h, 1);
    assert(k.wxTabJumps == 320 && k.wxTabScans > 0);
    // a jump over other grids, or with a callback that is not the game's, walks
    {
        WjCtx c = WsCtx0(g_wxtH1, a2, w, h);
        assert(WxJumpTab(c, 0) == 0 && !c.tab && k.wxJumpNoTab == 1);
        WjCtx c2 = WsCtx0(a1, a2, w, h);
        c2.cb = &g_wxtDummyCb;
        assert(WxJumpTab(c2, 0) == 0 && k.wxJumpNoTab == 2);
        WjCtx c3 = WsCtx0(a1, a2, w, h);
        assert(WxJumpTab(c3, 5 * w + 7) == 1 && c3.goalX == 7 && c3.goalY == 5);
    }
    WxTableEnd();
    assert(!t_wx.tab && !t_wx.held);
    // the same grids again: nothing to do
    assert(WxTableBegin(a1, a2, w, h, 1) && k.wxClean == 1 && k.wxFull == 1);
    WxTableEnd();
    // three bytes changed: updated in place, and right
    a1[5 * w + 5] = !a1[5 * w + 5], a2[9 * w + 20] = !a2[9 * w + 20], a2[9 * w + 21] = !a2[9 * w + 21];
    assert(WxTableBegin(a1, a2, w, h, 1) && k.wxInc == 1 && k.wxIncCells == 3 && k.wxFull == 1);
    WxtJumpsThroughGate(a1, a2, w, h, 1);
    WxTableEnd();
    // many bytes changed: built again
    for (DWORD i = 0; i < 200; i++) a2[(i * 7) % cells] = !a2[(i * 7) % cells];
    assert(WxTableBegin(a1, a2, w, h, 1) && k.wxFull == 2 && k.wxInc == 1 && k.wxFullOver == 1 && k.wxFullEvict == 0 && k.wxIncMax == 3);
    WxtJumpsThroughGate(a1, a2, w, h, 1);
    WxTableEnd();
    // a second pair takes the second slot; a third replaces the least recently used (the first pair)
    assert(WxTableBegin(a1, g_wxtH2, w, h, 1) && k.wxFull == 3);
    WxtJumpsThroughGate(a1, g_wxtH2, w, h, 1);
    WxTableEnd();
    assert(WxTableBegin(a1, g_wxtH2, w, h, 1) && k.wxClean == 2);
    WxTableEnd();
    assert(WxTableBegin(g_wxtH1, g_wxtH2, w, h, 1) && k.wxFull == 4);
    WxtJumpsThroughGate(g_wxtH1, g_wxtH2, w, h, 1);
    WxTableEnd();
    assert(WxTableBegin(a1, g_wxtH2, w, h, 1) && k.wxClean == 3);   // still there
    WxTableEnd();
    assert(WxTableBegin(a1, a2, w, h, 1) && k.wxFull == 5 && k.wxFullEvict == 2 && k.wxFullOver == 1);  // was replaced: built again, and right
    WxtJumpsThroughGate(a1, a2, w, h, 1);
    WxTableEnd();
    assert(k.wxMemKb == 2 * (LONG)(cells / 1024 * 10));
    // another thread's search holds the tables: this one walks
    AcquireSRWLockExclusive(&g_wxLock);
    assert(!WxTableBegin(a1, a2, w, h, 1) && k.wxBusy == 1 && !t_wx.tab && !t_wx.held);
    WxtJumpsThroughGate(a1, a2, w, h, 0);
    ReleaseSRWLockExclusive(&g_wxLock);
    // a search left by an unwind (no end): the next one on this thread keeps the lock it still holds, then frees it
    assert(WxTableBegin(a1, a2, w, h, 1) && t_wx.held);
    assert(WxTableBegin(a1, a2, w, h, 1) && t_wx.held && k.wxBusy == 1);
    WxTableEnd();
    assert(TryAcquireSRWLockExclusive(&g_wxLock));
    ReleaseSRWLockExclusive(&g_wxLock);
    // the scope gate: household, selected or unknown scope walks unless WaterScanAll / HouseholdToo
    g_wxAll = false, g_wxScope = WxtScope, g_wxtScopeVal = 0;
    assert(!WxTableBegin(a1, a2, w, h, 1) && k.wxScope == 1 && !t_wx.held);
    g_wxtScopeVal = 1;
    assert(!WxTableBegin(a1, a2, w, h, 1) && k.wxScope == 2);
    g_wxtScopeVal = 2;
    assert(WxTableBegin(a1, a2, w, h, 1));
    WxTableEnd();
    g_wxtScopeVal = 3;
    assert(WxTableBegin(a1, a2, w, h, 1));
    WxTableEnd();
    g_wxAll = true;
    // a grid too large, a grid of one column, no memory: walked, the lock is free again
    assert(!WxTableBegin(a1, a2, 0x8001, 2, 1) && !WxTableBegin(a1, a2, 2048, 2048, 1) && !WxTableBegin(a1, a2, 1, 30, 1) && k.wxNoSlot == 3);
    g_wxtAllocFail = true;
    assert(!WxTableBegin(a1, a2, w - 1, h, 1) && k.wxNoSlot == 4 && !t_wx.held);
    g_wxtAllocFail = false;
    assert(TryAcquireSRWLockExclusive(&g_wxLock));
    ReleaseSRWLockExclusive(&g_wxLock);
    // shadow mode: the gate says 2; equal results count a check, unequal ones a mismatch
    assert(WxTableBegin(a1, a2, w, h, 2));
    WxtJumpsThroughGate(a1, a2, w, h, 2);
    {
        const WjCtx c = WsCtx0(a1, a2, w, h);
        WxShadowNote(7, 7, c, 0, 0, 0, 0);
        assert(k.wxShadow == 1 && k.wxMismatch == 0);
        WxShadowNote(7, 8, c, 0, 0, 0, 0);
        assert(k.wxShadow == 2 && k.wxMismatch == 1 && k.wxLines == 1);
    }
    WxTableEnd();

    // the search hook: mode off and probe off = straight through; mode on = the search runs with a table and leaves none
    const u8* p1 = a1;
    const u8* p2 = a2;
    const DWORD start[2] = {3, 4}, goal[2] = {30, 20}, goal2[2] = {31, 20};
    g_wxSearchOrig = WxtSearchOrig, g_wxtOrigCalls = 0;
    k.wxScanMode = 0, k.wxProbe = 0;
    assert(WxSearchHook(&p1, &p2, w, h, 0, start, goal, NULL, NULL) == 1 && g_wxtOrigCalls == 1 && !g_wxtOrigSawTab && k.wxWaterN == 0);
    k.wxScanMode = 1, k.wxProbe = 1;
    const LONG searches0 = k.wxSearches;
    assert(WxSearchHook(&p1, &p2, w, h, 0, start, goal, NULL, NULL) == 1 && g_wxtOrigSawTab && !t_wx.tab && !t_wx.held && k.wxSearches == searches0 + 1);
    assert(k.wxWaterN == 1 && k.wxWaterFound == 1 && k.wxWaterRep == 0 && k.wxDistinct[0] == 1);
    // the same query twice more with unchanged grids, then after a change, then another goal
    WxSearchHook(&p1, &p2, w, h, 0, start, goal, NULL, NULL);
    WxSearchHook(&p1, &p2, w, h, 0, start, goal, NULL, NULL);
    assert(k.wxWaterN == 3 && k.wxWaterRep == 2 && k.wxWaterSame == 2);
    a2[2 * w + 2] = !a2[2 * w + 2];
    WxSearchHook(&p1, &p2, w, h, 0, start, goal, NULL, NULL);
    assert(k.wxWaterRep == 3 && k.wxWaterSame == 2);
    WxSearchHook(&p1, &p2, w, h, 0, start, goal2, NULL, NULL);
    assert(k.wxWaterN == 5 && k.wxWaterRep == 3 && k.wxDistinct[0] == 2);
    // probe without the table: "unchanged" comes from the routing write sequence, and only while it is counted
    k.wxScanMode = 0;
    g_snOn = 0;
    WxSearchHook(&p1, &p2, w, h, 0, start, goal2, NULL, NULL);
    WxSearchHook(&p1, &p2, w, h, 0, start, goal2, NULL, NULL);
    assert(!g_wxtOrigSawTab && k.wxWaterRep == 5 && k.wxWaterSame == 2);
    g_snOn = 1;
    WxSearchHook(&p1, &p2, w, h, 0, start, goal2, NULL, NULL);
    assert(k.wxWaterRep == 6 && k.wxWaterSame == 3);
    InterlockedIncrement(&g_snWriteSeq);
    WxSearchHook(&p1, &p2, w, h, 0, start, goal2, NULL, NULL);
    assert(k.wxWaterRep == 7 && k.wxWaterSame == 3);

    // the world probe: the outermost frame is one search; the circle pass (a frame below it) is part of it
    DWORD opts[0x30] = {};
    DWORD jobWord = 0x4100, startRef = 0x00030005, goalRef = 0x00090007;
    opts[0] = 0x30, opts[0x23] = (DWORD)(uintptr_t)&jobWord;
    int marks[4] = {};
    // a waypoint graph of 8 chunks: chunk 5 starts at colour index 10, chunk 7 at 20; waypoint 3 of chunk 5 is colour 4,
    // waypoint 9 of chunk 7 colour 6
    DWORD firsts[8] = {0, 0, 0, 0, 0, 10, 0, 20};
    signed char colours[40] = {};
    colours[13] = 4, colours[29] = 6;
    u8 graph[0x30] = {};
    const DWORD* const firstsP = firsts;
    const signed char* const coloursP = colours;
    const DWORD* const firstsEnd = firsts + 8;
    const signed char* const coloursEnd = colours + 40;
    memcpy(graph + 0x18, &firstsP, 4), memcpy(graph + 0x1C, &firstsEnd, 4), memcpy(graph + 0x28, &coloursP, 4), memcpy(graph + 0x2C, &coloursEnd, 4);
    assert(WxLandColour(graph, 0x00030005) == -3);                       // components not built
    graph[0x14] = 1;
    assert(WxLandColour(graph, 0x00030005) == 4 && WxLandColour(graph, 0x00090007) == 6 && WxLandColour(graph, 0x00000000) == 0);
    assert(WxLandColour(graph, 0x0003FFFF) == -3 && WxLandColour(graph, 0x00030008) == -3 && WxLandColour(graph, 0x00150007) == -3);
    assert(WxLandColour(NULL, 0x00030005) == -3);
    WxWorldEnter(&marks[2], graph, &startRef, &goalRef, 3, opts);
    WxWorldEnter(&marks[1], graph, &startRef, &goalRef, 3, opts);
    WxWorldLeave(&marks[1], 0);
    assert(k.wxWorldN == 0);
    WxWorldLeave(&marks[2], 1);
    assert(k.wxWorldN == 1 && k.wxWorldFail == 0 && k.wxWorldRep[0] == 0 && k.wxDistinct[1] == 1 && k.wxDistinct[2] == 1);
    assert(k.wxWorldCrossN == 1 && k.wxWorldWaterN == 0);  // colours 4 and 6; flags 0x30: no water links
    WxWorldEnter(&marks[2], graph, &startRef, &goalRef, 3, opts);
    WxWorldLeave(&marks[2], 1);
    assert(k.wxWorldN == 2 && k.wxWorldRep[0] == 1 && k.wxWorldRep[1] == 1 && k.wxWorldSame[0] == 1 && k.wxWorldSame[1] == 1);
    InterlockedIncrement(&g_snWriteSeq);  // a routing write in between
    opts[2] = 0x41200000;                 // and another start point: the same key A, a new key B
    WxWorldEnter(&marks[2], graph, &startRef, &goalRef, 3, opts);
    WxWorldLeave(&marks[2], 0);
    assert(k.wxWorldN == 3 && k.wxWorldFail == 1 && k.wxWorldRep[0] == 2 && k.wxWorldRep[1] == 1 && k.wxWorldSame[0] == 1 && k.wxDistinct[2] == 2);
    opts[0] = 0xB0;                                                 // water links allowed: new keys, counted as such
    goalRef = 0x00030005;                                           // and the goal is the start's waypoint: one component
    WxWorldEnter(&marks[2], graph, &startRef, &goalRef, 1, opts);  // (another cost mode as well)
    WxWorldLeave(&marks[2], 1);
    assert(k.wxWorldWaterN == 1 && k.wxWorldCrossN == 3);
    assert(k.wxWorldRep[0] == 2 && k.wxDistinct[1] == 2);
    WxWorldEnter(&marks[1], graph, &startRef, &goalRef, 1, opts);  // a frame left by an unwind (no leave) ...
    WxWorldEnter(&marks[3], graph, &startRef, &goalRef, 1, opts);  // ... is replaced by a search that starts above it
    WxWorldLeave(&marks[3], 1);
    assert(k.wxWorldN == 5);
    k.wxProbe = 0;
    WxWorldEnter(&marks[2], graph, &startRef, &goalRef, 1, opts);
    WxWorldLeave(&marks[2], 1);
    assert(k.wxWorldN == 5);

    // the post pass stubs' bookkeeping: entries are a stack of return slots; an exit gives its entry's return address
    DWORD stack[8] = {0, 0x1001, 0x1002, 0x1003, 0x1004, 0x1005, 0x1006, 0x1007};
    assert(WxPostEnter(2, &stack[5]) == 0);                 // probe off: the call is not timed, the stub leaves the stack alone
    k.wxProbe = 1;
    assert(WxPostEnter((DWORD)WX_POSTN, &stack[5]) == 0);
    assert(WxPostEnter(2, &stack[5]) == 1 && WxPostEnter(15, &stack[3]) == 1);   // a pass inside a pass lies below it
    assert(WxPostLeave(&stack[4]) == 0x1003 && WxPostLeave(&stack[6]) == 0x1005 && k.wxPostN[2] == 1 && k.wxPostN[15] == 1);
    assert(WxPostEnter(1, &stack[4]) == 1);                 // left by an unwind ...
    assert(WxPostEnter(4, &stack[6]) == 1 && t_wxPost.n == 1);  // ... dropped by the next entry at or above it
    assert(WxPostLeave(&stack[7]) == 0x1006 && t_wxPost.n == 0 && k.wxPostN[1] == 0 && k.wxPostN[4] == 1);
    assert(WxPostEnter(0, &stack[7]) == 1 && WxPostEnter(1, &stack[6]) == 1 && WxPostEnter(2, &stack[5]) == 1 && WxPostEnter(3, &stack[4]) == 1);
    assert(WxPostEnter(4, &stack[3]) == 0);                 // a fifth level: untimed
    assert(WxPostLeave(&stack[5]) == 0x1004 && WxPostLeave(&stack[6]) == 0x1005 && WxPostLeave(&stack[7]) == 0x1006 && WxPostLeave(&stack[8]) == 0x1007 &&
           WxPostLeave(&stack[8]) == 0);
    // an entry inside a pass that an unwind left (an exception caught within the pass) and no later entry dropped: the
    // pass's own exit returns to the pass's caller, not to the dead entry's address
    {
        const LONG n15 = k.wxPostN[15], n2 = k.wxPostN[2];
        assert(WxPostEnter(2, &stack[5]) == 1 && WxPostEnter(15, &stack[3]) == 1);
        assert(WxPostLeave(&stack[6]) == 0x1005 && t_wxPost.n == 0 && k.wxPostN[15] == n15 && k.wxPostN[2] == n2 + 1);
        // a callee-cleans pass (ret 8): its exit is entered three words above the slot
        assert(WxPostEnter(2, &stack[3]) == 1 && WxPostLeave(&stack[6]) == 0x1003 && t_wxPost.n == 0);
    }
    // pass 3's callees: timed only under an open pass 3; the lot walk reads the smoother's +0x72 from the saved ECX
    {
        static u8 smoother[0x80];
        DWORD fr[16] = {};
        for (DWORD i = 0; i < 16; i++) fr[i] = 0x3000 + i;
        smoother[0x72] = 1;
        fr[7] = (DWORD)(uintptr_t)smoother;                      // ECX: three slots below the return address at fr[10]
        assert(WxPostEnter((DWORD)WX_POSTN + 2, &fr[3]) == 0);  // no pass 3 open
        assert(WxPostEnter(5, &fr[14]) == 1 && WxPostEnter((DWORD)WX_POSTN + 2, &fr[3]) == 0);  // another pass: not counted
        assert(WxPostLeave(&fr[15]) == 0x300E);
        const LONG n2 = k.wxPostN[2];
        assert(WxPostEnter(2, &fr[14]) == 1 && WxPostEnter((DWORD)WX_POSTN + 3, &fr[13]) == 1 && WxPostEnter((DWORD)WX_POSTN + 4, &fr[10]) == 1);
        assert(k.wxP3Flag72 == 1);
        assert(WxPostEnter((DWORD)WX_POSTN + 9, &fr[5]) == 1 && WxPostEnter((DWORD)WX_POSTN + 5, &fr[4]) == 0);  // four deep at most
        assert(WxPostLeave(&fr[6]) == 0x3005 && WxPostLeave(&fr[11]) == 0x300A && WxPostLeave(&fr[14]) == 0x300D && WxPostLeave(&fr[15]) == 0x300E &&
               WxPostLeave(&fr[15]) == 0);
        assert(k.wxP3N[2] == 1 && k.wxP3N[3] == 1 && k.wxP3N[6] == 1 && k.wxP3N[4] == 0 && k.wxPostN[2] == n2 + 1);
        assert(WxPostEnter((DWORD)(WX_POSTN + WX_P3SITES), &fr[5]) == 0);
        smoother[0x72] = 0;
    }

    memset((void*)&k, 0, sizeof(k));
    memset(g_wxSlot, 0, sizeof(g_wxSlot));
    memset(g_wxRep, 0, sizeof(g_wxRep));
    memset(&t_wx, 0, sizeof(t_wx));
    memset(&t_wxPost, 0, sizeof(t_wxPost));
    memset(&t_wxWorld, 0, sizeof(t_wxWorld));
    g_wxAlloc = NULL, g_wxAll = savedAll, g_wxScope = savedScope, g_wxSearchOrig = savedOrig, g_wxUse = 0, g_wxGridGen = 0;
    g_snOn = savedSnOn, g_snWriteSeq = savedSeq;
}

#ifndef WXT_GRIDS
#define WXT_GRIDS 24   // selftest: a short run; worldpre-host-test.cpp runs 260 grids and the wide sea
#define WXT_SAMPLES 60
#define WXT_BIGSEA false
#endif
static void TestWorldPre() {
    TestWsCore(WXT_GRIDS, WXT_SAMPLES, WXT_BIGSEA);
    TestWxRep();
    TestWxManager();
}
