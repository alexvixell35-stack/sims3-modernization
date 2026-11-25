// Terrain height cache (docs/20 §4b, #7). Included by ts3native.cpp (one translation unit).
//
// 0xC2A570 (thiscall, ecx = world cell grid, float x, float z, ret 8, height in ST0) casts a ray down through the
// cell's terrain kd-tree (0x67EDE0 -> 0x67E2B0) for the terrain-only height. Its inputs are (x, z), the grid and the
// ray start [grid+0x34]; route search (A* edge test 0x627780, path-node pass 0x64C7C0) asks the same points again and
// again within one search. The only code that installs a new cell shape is 0xC2A840 (cell rebuild, from terrain update
// and world setup) [verified, H5].
//
// Per-cell invalidation [verified asm]: 0xC2A570 reads only one cell, (floor(x) >> 8, floor(z) >> 8) checked against
// [grid+0xCC] x [grid+0xD0] (cells at [grid+0xBC]); floor is FIST + compare of x - n with 0x80000001 + ADC -1. The
// rebuild 0xC2A840 (thiscall, int x, int z, shape, shape2, float* bounds; ret 0x14) touches only cell (x >> 8, z >> 8)
// of the same grid. So a rebuild bumps the generation of that cell's slot in g_hcCellGen (cells hashed on (cx & 63,
// cz & 63), so slot sharing only drops extra entries). A rebuild whose cell is out of range bumps the global g_hcGen.
// Points outside the grid are not cached (the game returns a constant there without a raycast), so a grid resize at
// world load cannot leave a stale entry.
//
// 2-way set-associative table keyed on (grid, bits(x), bits(z), [grid+0x34], global and cell generation), LRU by a
// per-set MRU word. Entries carry a check word written last, so a reader on another thread (route worker) that sees a
// half-written entry counts a miss, never a wrong height. Verify=1: every hit also runs the original and compares the
// bits (first 10 mismatches logged; a mismatch returns the original's answer). Runtime switch:
// ts3native_counters.heightMode (+0x1B4; 0 = the game's raycast, 1 = cache).

static const DWORD HC_FN = TS3A(0x00C2A570), HC_REBUILD = TS3A(0x00C2A840);
static const u8 kHcPrologue[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};  // push ebp; mov ebp,esp; and esp,-16 (both functions)

typedef double(__fastcall* HcFn)(void* grid, void* edx, float x, float z);  // thiscall via fastcall + dummy edx
typedef DWORD(__fastcall* HcRebuildFn)(void* grid, void* edx, int x, int z, void* shape, void* shape2, void* bounds);
static HcFn g_hcOrig;
static HcRebuildFn g_hcRebuildOrig;
static volatile LONG g_hcGen = 1;
static volatile LONG g_hcCellGen[4096];
static bool g_hcVerify;

// mru: only meaningful in way 0 of a set (the way hit or written last)
struct HcEntry { volatile DWORD grid, xb, zb, top, gen, cgen; volatile double h; volatile DWORD chk, mru; };
static HcEntry* g_hc;      // sets of 2 ways
static DWORD g_hcMask;     // set count - 1

static DWORD HcMix(DWORD grid, DWORD xb, DWORD zb, DWORD top, DWORD gen, DWORD cgen, const double& h) {
    const DWORD* hw = (const DWORD*)&h;
    return ((grid * 0x9E3779B1u) ^ (xb * 0x85EBCA6Bu) ^ (zb * 0xC2B2AE35u) ^ top ^ (gen * 0x27D4EB2Fu) ^ (cgen * 0x165667B1u) ^
            hw[0] ^ (hw[1] * 31)) | 1;
}

// Round floats have zero low mantissa bits, so x * K keeps only its top bits; the old index (bits 7+ of the plain
// product) put e.g. every whole-number point on one set. Fold the high bits down first.
static inline HcEntry* HcSet(DWORD grid, DWORD xb, DWORD zb) {
    DWORD h = (xb * 0x9E3779B1u) ^ (zb * 0x85EBCA6Bu) ^ (grid >> 4);
    h ^= h >> 16;
    h *= 0x7FEB352Du;
    h ^= h >> 15;
    return &g_hc[(h & g_hcMask) * 2];
}

static inline DWORD HcSlot(int cx, int cz) { return (DWORD)(cx & 63) | (DWORD)(cz & 63) << 6; }

// floor(v) as 0xC2A570 computes it, for |v| < 2^31; outside that (and NaN) the cell is out of range either way
static inline int HcFloor(float v) { const int n = (int)v; return n - ((float)n > v ? 1 : 0); }

// --- [TerrainGrave] (docs/60 §2.2 #2, docs/38 C1 graveyard variant; H6 S3a) ---
// The cell rebuild 0xC2A840 releases the cell's old collision shape (cell+0x38) with PhysShape_Release 0x682760 (return
// address 0xC2A896) and then stores the new one, with no lock, while the sim thread and the route worker ray-cast through
// 0xC2A570, which reads cell+0x38 without taking a reference [verified listing; "no reference" inferred, docs/38]. A release
// that drops the count to 0 frees the shape under a running ray. PhysShape_Release [verified disassembly]: add [ecx+0x4C],-1;
// at 0 call the deleting destructor (vtable slot 0, arg 1); return the new count, which the swap ignores (eax is rewritten
// before any use after 0xC2A896) [verified]. With the switch on, that one release is parked in a 64-entry ring instead
// (count untouched, shape alive) and run later by the next rebuild's entry, once no ray is in flight. g_tgInFlight counts
// threads inside the game's 0xC2A570 (every call our HcHook passes on); the interlocked add orders it before the ray's load
// of cell+0x38, and the drain reads it with an interlocked op after the swap's store, so 0 there means every ray that could
// have read a parked pointer has ended. Ring full: wait (spin) for 0, then drain.
// shortcut: parked shapes (at most 64) stay until the next rebuild, also across a world unload; that release then runs the
// destructor later than the game would (its realm links are inferred to be gone by then). Upgrade: drain at world teardown.
static const DWORD TG_RELEASE = TS3A(0x00682760);
static DWORD g_tgSwapRet = TS3A(0x00C2A896);  // return address of the swap's release call (the selftest points it at its own)
static const u8 kTgPrologue[5] = {0x83, 0x41, 0x4C, 0xFF, 0x56};  // add dword [ecx+0x4C],-1; push esi
typedef LONG(__fastcall* TgReleaseFn)(void* shape, void* edx);
static void* g_tgTramp;           // the original PhysShape_Release (trampoline)
static volatile LONG g_tgVf;      // Verify probe index + 1 counted from inside the stub (rtShapeRelease)
static bool g_tgOn;               // installed: HcHook keeps g_tgInFlight
static volatile LONG g_tgInFlight;
static void* g_tgRing[64];
static int g_tgN;
static SRWLOCK g_tgLock = SRWLOCK_INIT;

static void __cdecl VfEnter(int id, DWORD* slot);  // verify.cpp

static void TgDrainLocked() {  // caller holds g_tgLock and saw g_tgInFlight == 0
    Ts3nCounters& k = ts3native_counters;
    for (int i = 0; i < g_tgN; i++) {
        ((TgReleaseFn)g_tgTramp)(g_tgRing[i], NULL);
        InterlockedIncrement(&k.vfRtGraveFreed);
    }
    g_tgN = 0;
    k.vfRtGraveHeld = 0;
}

static inline bool TgNoRays() { return InterlockedCompareExchange(&g_tgInFlight, 0, 0) == 0; }

// Stub side: slot = the caller's return-address slot; ecx (the shape) at slot[-2]. Returns 1 = parked (skip the original).
static LONG __cdecl TgRelease(DWORD* slot) {
    if (g_tgVf) VfEnter(g_tgVf - 1, slot);  // count-only probe: no frame, *slot unchanged
    if (*slot != g_tgSwapRet) return 0;
    Ts3nCounters& k = ts3native_counters;
    void* shape = (void*)(uintptr_t)slot[-2];
    if (k.tgMode != 1) {
        if (*(const LONG*)((const u8*)shape + 0x4C) == 1 && !TgNoRays()) InterlockedIncrement(&k.vfRtGraveRaces);
        return 0;
    }
    AcquireSRWLockExclusive(&g_tgLock);
    if (g_tgN == 64) {
        InterlockedIncrement(&k.vfRtGraveSpins);
        while (!TgNoRays()) YieldProcessor();  // rays take microseconds; the swap's thread holds nothing a ray waits on
        TgDrainLocked();
    }
    g_tgRing[g_tgN++] = shape;
    k.vfRtGraveHeld = g_tgN;
    InterlockedIncrement(&k.vfRtGraveParked);
    ReleaseSRWLockExclusive(&g_tgLock);
    return 1;
}

// PhysShape_Release entry: parked -> return to the caller (its eax is not used, flags rewritten), else the original.
static __declspec(naked) void TgStub() {
    __asm {
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        call TgRelease
        add esp, 4
        test eax, eax
        jnz parked
        popfd
        popad
        jmp dword ptr [g_tgTramp]
    parked:
        popfd
        popad
        ret
    }
}

// cell rebuild entry: release what an earlier swap parked once no ray is in flight; count the swap and an overlap with a ray
static void TgOnSwap() {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.vfSwaps);
    if (!TgNoRays()) InterlockedIncrement(&k.vfSwapsInRay);
    if (!g_tgN) return;
    AcquireSRWLockExclusive(&g_tgLock);
    if (g_tgN && TgNoRays()) TgDrainLocked();
    ReleaseSRWLockExclusive(&g_tgLock);
}

// the game's ray, counted in flight while the graveyard is installed (cache misses, Verify and mode 0 all come here)
static inline double HcOrig(void* gridp, void* edx, float x, float z) {
    if (!g_tgOn) return g_hcOrig(gridp, edx, x, z);
    InterlockedIncrement(&g_tgInFlight);
    const double h = g_hcOrig(gridp, edx, x, z);
    InterlockedDecrement(&g_tgInFlight);
    return h;
}

static double __fastcall HcHook(void* gridp, void* edx, float x, float z) {
    Ts3nCounters& s = ts3native_counters;
    if (t_p4Read) t_p4Read |= P4R_TERRAIN;  // P4 D5: the plan read terrain heights (this stub stays on in a clone run, D1)
    if (s.heightMode != 1 || !g_hc) return HcOrig(gridp, edx, x, z);
    s.heightCalls++;  // shortcut: plain ++, a lost count from the route worker only skews the stats
    const u8* g = (const u8*)gridp;
    const int cx = HcFloor(x) >> 8, cz = HcFloor(z) >> 8;
    if ((DWORD)cx >= *(const DWORD*)(g + 0xCC) || (DWORD)cz >= *(const DWORD*)(g + 0xD0)) {
        s.heightMisses++;
        return HcOrig(gridp, edx, x, z);  // no cell: the game returns its constant without a raycast
    }
    const DWORD grid = (DWORD)(uintptr_t)gridp, xb = *(const DWORD*)&x, zb = *(const DWORD*)&z;
    const DWORD top = *(const DWORD*)(g + 0x34), gen = (DWORD)g_hcGen, cgen = (DWORD)g_hcCellGen[HcSlot(cx, cz)];
    HcEntry* set = HcSet(grid, xb, zb);
    for (DWORD w = 0; w < 2; w++) {
        HcEntry& e = set[w];
        const DWORD chk = e.chk;
        if (chk && e.grid == grid && e.xb == xb && e.zb == zb && e.top == top && e.gen == gen && e.cgen == cgen) {
            const double h = e.h;
            if (chk == HcMix(grid, xb, zb, top, gen, cgen, h) && e.chk == chk) {
                s.heightHits++;
                if (set[0].mru != w) set[0].mru = w;
                if (!g_hcVerify) return h;
                const double real = HcOrig(gridp, edx, x, z);
                if (memcmp(&real, &h, sizeof(h)) == 0) return h;
                if (InterlockedIncrement(&s.heightMismatches) <= 10)
                    Log("heightcache MISMATCH grid %08X x %.3f z %.3f cached %.6f real %.6f", grid, x, z, h, real);
                return real;
            }
        }
    }
    s.heightMisses++;
    const double h = HcOrig(gridp, edx, x, z);
    const DWORD w = set[0].mru ^ 1;  // replace the way not used last
    HcEntry& e = set[w];
    e.chk = 0;
    e.grid = grid; e.xb = xb; e.zb = zb; e.top = top; e.gen = gen; e.cgen = cgen; e.h = h;
    e.chk = HcMix(grid, xb, zb, top, gen, cgen, h);
    set[0].mru = w;
    return h;
}

// 0xC2A840 (cell rebuild): the game's function first, then the new generation, so an entry another thread computed
// from the old shape during the rebuild carries the old generation.
static DWORD __fastcall HcRebuildHook(void* gridp, void* edx, int x, int z, void* shape, void* shape2, void* bounds) {
    if (g_tgOn) TgOnSwap();
    const DWORD r = g_hcRebuildOrig(gridp, edx, x, z, shape, shape2, bounds);
    const u8* g = (const u8*)gridp;
    if ((DWORD)(x >> 8) < *(const DWORD*)(g + 0xCC) && (DWORD)(z >> 8) < *(const DWORD*)(g + 0xD0))
        InterlockedIncrement(&g_hcCellGen[HcSlot(x >> 8, z >> 8)]);
    else
        InterlockedIncrement(&g_hcGen);  // cell not identified: drop everything
    InterlockedIncrement(&ts3native_counters.heightRebuilds);
    return r;
}

// [HeightCache] Enabled=1 hooks; Mode = start value of ts3native_counters.heightMode; Verify=1 checks every hit;
// Entries = table size (power of two, 1024..1M, 40 B each, 2 ways per set).
// [TerrainGrave] Enabled=1 (H6 S3a) needs the same two detours: alone, they are installed with no table (heightMode stays
// 0, and a probe write of 1 still runs the game's ray); Mode = start value of tgMode (+0xD00, --ab terraingrave).
static bool ApplyHeightCache(const wchar_t* ini) {
    const bool hc = GetPrivateProfileIntW(L"HeightCache", L"Enabled", 0, ini) != 0;
    const bool grave = GetPrivateProfileIntW(L"TerrainGrave", L"Enabled", 0, ini) != 0;
    if (!hc) Log("height cache disabled (Enabled=0)");
    if (!grave) Log("terrain graveyard disabled (Enabled=0)");
    if (!hc && !grave) return false;
    DWORD n = 0;
    if (hc) {
        n = GetPrivateProfileIntW(L"HeightCache", L"Entries", 65536, ini);
        if (n < 1024) n = 1024;
        if (n > (1u << 20)) n = 1u << 20;
        while (n & (n - 1)) n &= n - 1;
        g_hc = (HcEntry*)calloc(n, sizeof(HcEntry));
        if (!g_hc) Log("FAIL height cache: no memory for %lu entries", n);
        if (!g_hc && !grave) return false;
        g_hcMask = n / 2 - 1;
        g_hcVerify = GetPrivateProfileIntW(L"HeightCache", L"Verify", 0, ini) != 0;
    }
    ts3native_counters.heightMode = g_hc && GetPrivateProfileIntW(L"HeightCache", L"Mode", 1, ini) != 0 ? 1 : 0;
    // the invalidation first: the cache never runs without it
    g_hcRebuildOrig = (HcRebuildFn)InstallDetour("cell rebuild (height cache)", HC_REBUILD, kHcPrologue, sizeof(kHcPrologue), (void*)HcRebuildHook, &g_hcRebuildOrig);
    if (!g_hcRebuildOrig) { free(g_hc); g_hc = NULL; ts3native_counters.heightMode = 0; return false; }
    g_hcOrig = (HcFn)InstallDetour("terrain height (height cache)", HC_FN, kHcPrologue, sizeof(kHcPrologue), (void*)HcHook, &g_hcOrig);
    if (!g_hcOrig) return false;  // the rebuild detour stays: it only bumps counters after the game's code
    if (g_hc) {
        ts3native_counters.heightReady = 1;
        Log("OK   height cache: 0x%08X, 0x%08X (Mode=%ld Verify=%d Entries=%lu, 2-way, per-cell invalidation)", HC_FN, HC_REBUILD,
            ts3native_counters.heightMode, g_hcVerify ? 1 : 0, n);
    }
    if (grave) {
        g_tgOn = true;  // rays are counted from here on, before any release can park
        ts3native_counters.tgMode = GetPrivateProfileIntW(L"TerrainGrave", L"Mode", 1, ini) == 0 ? 0 : 1;
        g_tgTramp = InstallDetour("PhysShape_Release (terrain graveyard)", TG_RELEASE, kTgPrologue, sizeof(kTgPrologue), (void*)TgStub, &g_tgTramp);
        ts3native_counters.tgReady = g_tgTramp != NULL;
        Log("%s terrain graveyard: 0x%08X (the release from 0x%08X is parked until no ray runs), Mode=%ld", g_tgTramp ? "OK  " : "FAIL",
            TG_RELEASE, g_tgSwapRet, ts3native_counters.tgMode);
    }
    return true;
}
