// [RouteSnap] Count=1 (docs/94 §5 phase P1, 0.25.0, counters v46), observe only: change counters for the routing snapshot.
// Nothing is changed or skipped: every hook counts, then runs the game's own code with the same registers, flags and stack.
// Included by ts3native.cpp after values.cpp; installed after [ProbeValues] and before [ProbeSnap]/[Verify].
//
// Writer layer (docs/94 §2.1, §8.2): 19 leaf rows on 31 sites. W1-W6 (docs/83 §6), the new leaves 0x8321F0, 0x831BA0,
// 0x82A0C0, 0x635700, 0x829D50, the R blocked-bit writers 0x82B590 / 0x82A190, the footprint grid 0x636BF0 / 0x62D9F0,
// the high-cost writers 0x6733C0 / 0x670510 (docs/94 §9.2), the five G-build call sites and the blend layer 0 reload job
// 0xC62AC0 (docs/94 §9.4). Each hit is counted by thread class (sim / route worker / other); on the sim it is attributed
// to the innermost open source (below), and leaves that carry their lot in an argument add it to the dirty-lot set that
// each route START reads and clears (G1: dirty lots per START).
// Event layer (attribution): 39 IC binding-table slot swaps (the 16 portal / lock-key / ocean writers of E8-E11 and E24,
// 19 other routing writers and epochs, the four route ICs = START), an entry detour on World_FireEvent 0x78F2B0 (counts by
// event id; id 7 also registers the object-event nodes) and one on Footprint_SetEnabled 0x947590 (E4, no event of its own).
// Object events (docs/76 §3.3): a head node (priority INT_MAX) and a tail node (INT_MIN) per id 4, 6, 9, 0x0A, 0x0B, 0x0E,
// 0x18, 0x21, registered through the game's ObjectEvents_Register on the sim thread; between them a write counts as
// object-event caused. A chain stopped early by a handler that returned 0 leaves the depth up until the next IC entry
// (snObjOpenAtIc counts those).
// One owner per address (docs/68 rule 3): a site whose bytes another of our stubs owns ([SteerRO] W stubs, [RouteGate]
// sites, [ProbeValues] entries and call sites) is counted from inside that stub (g_snQl, g_snRg, g_snPv); else we own it.
// shortcut: the lot is known only for leaves that take it as an argument (snStartUnknown counts STARTs with other writes);
// resolving footprint -> lot belongs to P3, where the cut needs it anyway.
//
// P1b (0.27.0, counters v48, docs/94 §11.6-§11.7, §15), still observe only:
// - Six more source frames for the §11.6 unattributed families (rows SN_F0..): Lot PlatformManager refresh 0xB57F20,
//   UserToolWorldObject TryDrop 0xD442B0, FootprintComp_TreeInit 0x948F30 (its one call, 0x95EFF2 in TreeComponent_Create),
//   lazy terrain footprints RoutingWorld_SurfaceAtPoint 0x834B80, the off-screen road plan 0xC7BF70 and
//   LotQuadTree_FlushDeferred 0x666A40. A frame is pushed only when the stub is ours: at a site another stub owns (0x666A40
//   with [ProbeValues] on) a pushed return address would change the owner's caller keys, so there it only counts. The
//   design D drain (discourage.cpp, g_discDraining) is a source of its own and wins over any frame. W6 hits with no source
//   are "cache fills" (lazy polygon caches, not input changes) and leave the unattributed total; the rest go into a
//   64-key table keyed by leaf, return address and the next frame above it.
// - Design B records (§11.4 step 2: one per logical change) with 1-s buckets, the record bookkeeping time, and on sim W1 hits
//   a clone-equivalent copy of the footprint (0x44 bytes, level 0 polygon cache entry and its polygons) into a scratch ring,
//   timed and counted in bytes. Records inside a rebase IC frame (portal ICs, build/buy exit, world ICs) count as suppressed.
// - The lot of each sim LotGraph_Build (W4, a rebase in design B) is copied once with P3's canonical SpBuildLot at the next
//   START and timed (rebase copy cost, G1 check 4).
// shortcut: W4's own nested records are not suppressed (W4 is a [RouteGate] site, no frame of ours there), so the record
// rate is an upper bound by the leaves of 0.12-0.18 lot builds/s; frame W4 when P3 owns the recorder.

enum { SL_W1, SL_W2, SL_W3, SL_W4, SL_W5, SL_W6, SL_FPCORE, SL_PRIVATE, SL_FPREMOVE, SL_LOTREMOVEFP, SL_REMOVELOT,
       SL_APPLYLOT, SL_RESTORE, SL_GRIDADD, SL_GRIDREMOVE, SL_HIGHCOST, SL_MARKREMOVE, SL_GBUILD, SL_BLEND, SL_N, SL_SRC = 0xFF };
enum { SRC_NONE, SRC_IC, SRC_WEV, SRC_OBJ, SRC_FPEN, SRC_N,  // v46 columns (snSrc)
       SRC_PLATFORM = SRC_N, SRC_USERTOOL, SRC_TREEINIT, SRC_TERRAIN, SRC_ROADPLAN, SRC_FLUSH, SRC_DRAIN, SRC_N2 };  // v48 (snSrc2)
static const int SN_N = 38, SN_G0 = 24, SN_WEV = 30, SN_FPEN = 31, SN_F0 = 32, SN_W6B = 5;
static const int SN_IC_N = 39, SN_IC_START = 35, SN_OBJ_N = 8;
// lot: 1 = lot pointer in argument `arg` (key at +0x48/+0x4C, NULL = lot 0), 2 = lot id in arguments arg, arg + 1
struct SnSite { DWORD at; u8 len; u8 pro[10]; DWORD target; u8 leaf; u8 lot; u8 arg; const char* name; };
// Prologues: whole instructions, no relative operand, no branch into the stolen bytes (capstone + raw branch scan on the
// Steam 1.67 exe, 2026-10-18; rows shared with kQlSites / kPv / kRgSites carry the same bytes). steerro-test re-checks
// every row on the image.
static const SnSite kSn[SN_N] = {
    {0x00665C50, 6, {0x81, 0xEC, 0x80, 0x00, 0x00, 0x00}, 0, SL_W1, 0, 0, "LotQuadTree_ApplyInsert (W1)"},
    {0x00666750, 5, {0x83, 0xEC, 0x60, 0x53, 0x55}, 0, SL_W2, 0, 0, "LotQuadTree_ApplyRemove (W2)"},
    {0x0062F8A0, 7, {0x8B, 0x44, 0x24, 0x08, 0x83, 0xEC, 0x14}, 0, SL_W3, 0, 0, "LotRouting_QuadTreeBuild (W3)"},
    {0x0062E680, 8, {0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x14}, 0, SL_W5, 0, 0, "LotTable_Insert (W5)"},
    {0x0080E5B0, 5, {0x53, 0x8B, 0x5C, 0x24, 0x08}, 0, SL_W6, 0, 0, "Footprint_EnsureLevelPolygons (W6a)"},
    {0x0080E610, 8, {0x51, 0x57, 0x8B, 0xF9, 0x83, 0x7F, 0x38, 0x00}, 0, SL_W6, 0, 0, "Footprint_EnsureAllPolygons (W6b)"},
    {0x0080E980, 8, {0x83, 0xEC, 0x2C, 0x53, 0x8B, 0x5C, 0x24, 0x34}, 0, SL_W6, 0, 0, "footprint +0x78 rebuild (W6c)"},
    {0x0080EC30, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_W6, 0, 0, "footprint +0xF0 transform (W6d)"},
    {0x0080E0B0, 7, {0x56, 0x8B, 0xF1, 0x83, 0x7E, 0x38, 0x00}, 0, SL_W6, 0, 0, "Footprint_InvalidatePolygons (W6e)"},
    {0x0080F6A0, 5, {0x8B, 0x54, 0x24, 0x08, 0x53}, 0, SL_W6, 0, 0, "Footprint_SetLevelPolygons (W6f)"},
    {0x0080CB40, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_W6, 0, 0, "footprint +0x100 class 0xFEB848 (W6g)"},
    {0x00806770, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_W6, 0, 0, "footprint +0x100 class 0xFEB970 (W6g)"},
    {0x0063CB90, 8, {0x83, 0xEC, 0x5C, 0x53, 0x8B, 0x5C, 0x24, 0x64}, 0, SL_W4, 0, 0, "LotGraph_Build (W4)"},
    {0x008321F0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_FPCORE, 0, 0, "RoutingWorld_UpdateObjectFootprintCore"},
    {0x00831BA0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_PRIVATE, 0, 0, "RoutingWorld_AddPrivateFootprint"},
    {0x0082A0C0, 5, {0x83, 0xEC, 0x08, 0x56, 0x57}, 0, SL_FPREMOVE, 0, 0, "RoutingWorld footprint remove 0x82A0C0"},
    {0x00635700, 7, {0x83, 0xEC, 0x34, 0x8B, 0x44, 0x24, 0x38}, 0, SL_LOTREMOVEFP, 2, 1, "LotRouting_RemoveObjectFootprints"},
    {0x00829D50, 10, {0x55, 0x56, 0x8B, 0xF1, 0x8D, 0xAE, 0x18, 0x09, 0x00, 0x00}, 0, SL_REMOVELOT, 2, 1, "RoutingWorld_RemoveLot"},
    {0x0082B590, 5, {0x83, 0xEC, 0x2C, 0x56, 0x57}, 0, SL_APPLYLOT, 1, 1, "RoutingWorld_ApplyLotUpdate"},
    {0x0082A190, 7, {0x8B, 0x44, 0x24, 0x04, 0x85, 0xC0, 0x56}, 0, SL_RESTORE, 1, 1, "RoutingWorld_RestoreLotBlockedBits"},
    {0x00636BF0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_GRIDADD, 1, 2, "PathPlanner_AddHangingFootprint"},
    {0x0062D9F0, 5, {0x83, 0xEC, 0x4C, 0x53, 0x55}, 0, SL_GRIDREMOVE, 2, 1, "footprint grid remove 0x62D9F0"},
    {0x006733C0, 7, {0x83, 0xEC, 0x14, 0x8B, 0x44, 0x24, 0x30}, 0, SL_HIGHCOST, 0, 0, "WaterGrid_MarkFootprintPolygon"},
    {0x00670510, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_MARKREMOVE, 0, 0, "water grid mark 0x670510"},
    {0x0082A098, 5, {}, 0x006734A0, SL_GBUILD, 0, 0, "G build (message path)"},
    {0x00834A82, 5, {}, 0x006734A0, SL_GBUILD, 0, 0, "G build (BuildWorldRoutingData)"},
    {0x008352F1, 5, {}, 0x0082A030, SL_GBUILD, 0, 0, "WaterGrid_BuildFromWorld from 0x8352F1"},
    {0x00835390, 5, {}, 0x0082A030, SL_GBUILD, 0, 0, "WaterGrid_BuildFromWorld from 0x835390"},
    {0x008358B5, 5, {}, 0x0082A030, SL_GBUILD, 0, 0, "WaterGrid_BuildFromWorld from 0x8358B5"},
    {0x00C62AC0, 8, {0x83, 0xEC, 0x18, 0x83, 0x7C, 0x24, 0x24, 0x04}, 0, SL_BLEND, 0, 0, "TerrainData_ReloadJob"},
    {0x0078F2B0, 7, {0x83, 0xEC, 0x44, 0x8B, 0x44, 0x24, 0x4C}, 0, SL_SRC, 0, 0, "World_FireEvent"},
    {0x00947590, 5, {0x53, 0x8A, 0x5C, 0x24, 0x08}, 0, SL_SRC, 0, 0, "Footprint_SetEnabled"},
    // P1b frames (arg = source); prologues and the call edge checked on the Steam 1.67 exe, no branch into the stolen bytes
    {0x00B57F20, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_SRC, 0, SRC_PLATFORM, "Lot PlatformManager footprint refresh"},
    {0x00D442B0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_SRC, 0, SRC_USERTOOL, "UserToolWorldObject TryDrop"},
    {0x0095EFF2, 5, {}, 0x00948F30, SL_SRC, 0, SRC_TREEINIT, "FootprintComp_TreeInit from TreeComponent_Create"},
    {0x00834B80, 6, {0x81, 0xEC, 0x80, 0x00, 0x00, 0x00}, 0, SL_SRC, 0, SRC_TERRAIN, "RoutingWorld_SurfaceAtPoint (lazy terrain)"},
    {0x00C7BF70, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, SL_SRC, 0, SRC_ROADPLAN, "World_FindPlaceOnRoadOffScreen"},
    {0x00666A40, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, 0, SL_SRC, 0, SRC_FLUSH, "LotQuadTree_FlushDeferred"},
};

// IC binding table {fn, name} slots (tools/internal-calls-map.ts layout; slot = address of fn), expected fn checked first
struct SnIc { DWORD slot; DWORD fn; const char* name; };
static const SnIc kSnIcDef[SN_IC_N] = {
    {0x01160C98, 0x0074B540, "RouteManager_FootprintSetLockKeys"},        // E8
    {0x01160CA0, 0x0074B5C0, "RouteManager_FootprintAddLockKey"},
    {0x01160CA8, 0x0074B600, "RouteManager_FootprintRemoveLockKey"},
    {0x01160CB8, 0x0074B680, "RouteManager_FootprintClearLockKeys"},
    {0x01160CC8, 0x00757E10, "RouteManager_AddPortal"},                   // E9
    {0x01160CD8, 0x0074D3F0, "RouteManager_AddPortalLocationVariation"},
    {0x01160CE0, 0x0074B3B0, "RouteManager_AddDivingBoardPortals"},
    {0x01160CE8, 0x0074B3E0, "RouteManager_AddWaterSlidePortals"},
    {0x01160CF0, 0x0074B410, "RouteManager_AddPoolPortals"},
    {0x01160CF8, 0x0074B410, "RouteManager_AddPlatformPortals"},
    {0x01160D00, 0x0074B440, "RouteManager_AddStairPortals"},
    {0x01160D10, 0x0074D6B0, "RouteManager_RemovePortals"},
    {0x01160D18, 0x00758110, "RouteManager_SetPortalCost"},               // E10
    {0x01160D20, 0x00758220, "RouteManager_SetPortalAgeGenderFlags"},
    {0x01160D28, 0x0074B490, "RouteManager_SetPortalLockStatus"},         // E11
    {0x01160D08, 0x0074B470, "RouteManager_SetOceanPortalOwner"},         // E24
    {0x01160CD0, 0x0074B380, "RouteManager_DestroyPortalHandle"},         // other routing writers and epochs
    {0x01160D30, 0x0074B4D0, "RouteManager_MarkRoomForUpdate"},
    {0x01160C78, 0x0075DB50, "RouteManager_AddDiscourageFootprint"},      // E6
    {0x01160C80, 0x00754580, "RouteManager_RemoveDiscourageFootprint"},
    {0x01160C88, 0x0075DCF0, "RouteManager_AddPrivateFootprint"},         // E7
    {0x01160C90, 0x00754610, "RouteManager_RemovePrivateFootprint"},
    {0x01160DA8, 0x0074B6B0, "RouteManager_OnExitBuildBuy"},              // E13
    {0x011623B0, 0x007868A0, "World_LotClusterTreesImpl"},
    {0x01162BF0, 0x00788310, "World_DoorLockStateChangedImpl"},           // docs/105 door mirror refresh
    {0x01162670, 0x0078D360, "World_ObjectSetHiddenFlags"},               // E5
    {0x01162678, 0x0078D3E0, "World_ObjectSetHiddenFlagsWithWorldFootprint"},
    {0x011626A0, 0x0078D520, "World_ObjectEnableFootprint"},
    {0x011626B0, 0x0078D5B0, "World_ObjectResetFootprintState"},
    {0x01162C28, 0x0078AE40, "World_BuildRoutingDataImpl"},               // E19
    {0x011623B8, 0x007868E0, "World_BulldozeLotImpl"},                    // E15
    {0x01162D60, 0x00792CA0, "World_CreateHouseboatLotImpl"},
    {0x01162D68, 0x00788800, "World_DeleteHouseboatLotImpl"},
    {0x01162258, 0x007897D0, "World_LotSetTerrainHeightImpl"},            // E21
    {0x01162C88, 0x007883C0, "World_LotSetEntranceByTargetPointImpl"},    // docs/94 §8.4 (2)
    {0x011608F0, 0x00765160, "Route_Plan"},                               // START (SN_IC_START..)
    {0x011608E0, 0x00765490, "Route_Replan"},
    {0x011608F8, 0x00765240, "Route_PlanFromPoint"},
    {0x011608E8, 0x00765550, "Route_ReplanFromPosition"},
};
static SnIc g_snIc[SN_IC_N];  // kSnIcDef; selftest points slots at its own table

static const DWORD kSnObjIds[SN_OBJ_N] = {4, 6, 9, 0x0A, 0x0B, 0x0E, 0x18, 0x21};
typedef char(__cdecl* SnObjRegFn)(DWORD id, void* fn, LONG priority);   // ObjectEvents_Register 0x936BC0 (al = registered)
static SnObjRegFn g_snObjReg = (SnObjRegFn)(uintptr_t)0x00936BC0;
static DWORD* g_snObjHeads = (DWORD*)(uintptr_t)0x011DC2A8;              // 42 list heads, node {priority, fn, next}

static void* g_snJmp[SN_N];                 // trampoline (entry) or original callee (call site); NULL = shared or skipped
static void* volatile g_snIcOrig[SN_IC_N];  // the IC's own fn, our shims jump there
static volatile LONG g_snOn;

// --- per-thread source frames (sim thread) ---
struct SnFrame { DWORD* slot; DWORD ret; u8 src; u8 idx; };
struct SnThread { int n; SnFrame f[16]; LONG objDepth; };
static __declspec(thread) SnThread t_sn;
static void __cdecl SnLeaveThunk();

static void SnDrop(SnThread& t, DWORD* slot) { while (t.n && t.f[t.n - 1].slot <= slot) t.n--; }  // unwound frames

static void SnPush(SnThread& t, DWORD* slot, int src, int idx) {
    if (t.n == 16) { InterlockedIncrement(&ts3native_counters.snFrameFull); return; }
    SnFrame& f = t.f[t.n++];
    f.slot = slot;
    f.ret = *slot;
    f.src = (u8)src;
    f.idx = (u8)idx;
    *slot = (DWORD)(uintptr_t)SnLeaveThunk;
}

// same rule as PvLeave: the leaving frame is the one with the highest slot at or below `at`
static DWORD __cdecl SnLeave(DWORD* at) {
    SnThread& t = t_sn;
    int i = 0;
    while (i < t.n - 1 && t.f[i].slot > at) i++;
    const DWORD ret = t.f[i].ret;
    t.n = i;
    return ret;
}

static __declspec(naked) void __cdecl SnLeaveThunk() {
    __asm {
        push eax  // becomes the return address
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        call SnLeave
        add esp, 4
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}

// --- dirty-lot set between route STARTs (any thread; tiny spin lock) ---
static volatile LONG g_snLotLock;
static unsigned __int64 g_snLots[16];
static LONG g_snLotN, g_snWrites;
static bool g_snLotFull, g_snUnknown;

static void SnLotLock() { while (InterlockedExchange(&g_snLotLock, 1)) YieldProcessor(); }
static void SnLotUnlock() { InterlockedExchange(&g_snLotLock, 0); }

static void SnMark(bool known, unsigned __int64 key) {
    SnLotLock();
    g_snWrites++;
    if (!known) g_snUnknown = true;
    else {
        int i = 0;
        while (i < g_snLotN && g_snLots[i] != key) i++;
        if (i == g_snLotN) { if (g_snLotN < 16) g_snLots[g_snLotN++] = key; else g_snLotFull = true; }
    }
    SnLotUnlock();
}

static int SnBin(LONG v, const LONG* top, int n) { int b = 0; while (b < n - 1 && v > top[b]) b++; return b; }

static void SnStart() {  // sim, at a route IC entry: close the window since the previous START
    Ts3nCounters& k = ts3native_counters;
    SnLotLock();
    const LONG n = g_snLotN, w = g_snWrites;
    const bool full = g_snLotFull, unk = g_snUnknown;
    g_snLotN = g_snWrites = 0;
    g_snLotFull = g_snUnknown = false;
    SnLotUnlock();
    static const LONG kLots[8] = {0, 1, 2, 3, 4, 5, 10, 0x7FFFFFFF}, kWrites[8] = {0, 2, 5, 10, 20, 50, 100, 0x7FFFFFFF};
    k.snStarts++;
    k.snStartLots[full ? 7 : SnBin(n, kLots, 8)]++;
    k.snStartWrites[SnBin(w, kWrites, 8)]++;
    if (unk) k.snStartUnknown++;
    if (full) k.snSetFull++;
}

// sim thread only: the drain runs on the owner (sim) thread and replays the game's own Add/Remove outside any IC frame
static int SnSource(const SnThread& t, int* idx) {
    *idx = 0;
    if (g_discDraining) return SRC_DRAIN;
    if (t.n) { *idx = t.f[t.n - 1].idx; return t.f[t.n - 1].src; }
    return t.objDepth > 0 ? SRC_OBJ : SRC_NONE;
}

// --- P1b: design B records (docs/94 §11.4 step 2), per-second buckets, recorder-equivalent cost ---
enum { RK_W1, RK_W2, RK_W3, RK_W5, RK_GRIDADD, RK_GRIDREMOVE, RK_HCADD, RK_HCREMOVE, RK_RBIT, RK_LOCKKEY, RK_PORTAL, RK_SUPPRESSED, RK_N };
static int SnRecKind(int leaf) {
    switch (leaf) {
        case SL_W1: return RK_W1;
        case SL_W2: return RK_W2;
        case SL_W3: return RK_W3;
        case SL_W5: return RK_W5;
        case SL_GRIDADD: return RK_GRIDADD;
        case SL_GRIDREMOVE: return RK_GRIDREMOVE;
        case SL_HIGHCOST: return RK_HCADD;
        case SL_MARKREMOVE: return RK_HCREMOVE;
        case SL_APPLYLOT: case SL_RESTORE: return RK_RBIT;
        default: return -1;  // W4 and W6 and the footprint/lot leaves above W1/W2 are not records
    }
}
static bool SnRebaseIc(int ic) { return (ic >= 4 && ic <= 11) || ic == 22 || (ic >= 29 && ic <= 33); }
static bool SnInRebase(const SnThread& t) {
    for (int i = 0; i < t.n; i++) if (t.f[i].src == SRC_IC && SnRebaseIc(t.f[i].idx)) return true;
    return false;
}

static LARGE_INTEGER g_snQpf;
static LONG SnUs(unsigned __int64 ticks) {
    if (!g_snQpf.QuadPart) QueryPerformanceFrequency(&g_snQpf);
    return (LONG)(ticks * 1000000 / (unsigned __int64)g_snQpf.QuadPart);
}

static unsigned __int64 g_snRecSec, g_snRecTicks;  // under g_snLotLock
static LONG g_snRecCur;
static ULONGLONG(WINAPI* g_snNowMs)() = GetTickCount64;  // selftest steps the clock
static void SnRecClose(unsigned __int64 sec) {
    Ts3nCounters& k = ts3native_counters;
    static const LONG kTop[8] = {0, 250, 500, 1000, 2000, 5000, 10000, 0x7FFFFFFF};
    if (g_snRecSec) {
        k.snRecSec[SnBin(g_snRecCur, kTop, 8)]++;
        if (g_snRecCur > k.snRecMaxSec) k.snRecMaxSec = g_snRecCur;
        if (sec > g_snRecSec + 1) k.snRecSec[0] += (LONG)min(sec - g_snRecSec - 1, (unsigned __int64)86400);  // seconds with none
    }
    g_snRecSec = sec;
    g_snRecCur = 0;
}
static void SnRecord(int kind, bool suppressed) {
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    Ts3nCounters& k = ts3native_counters;
    SnLotLock();
    const unsigned __int64 sec = g_snNowMs() / 1000;
    if (sec != g_snRecSec) SnRecClose(sec);
    if (suppressed) k.snRec[RK_SUPPRESSED]++;
    else { k.snRec[kind]++; g_snRecCur++; }
    QueryPerformanceCounter(&t1);
    g_snRecTicks += (unsigned __int64)(t1.QuadPart - t0.QuadPart);
    k.snRecUs = SnUs(g_snRecTicks);
    SnLotUnlock();
}

// sim W1 (LotQuadTree_ApplyInsert, thiscall Q, arg 1 = footprint [verified 0x665C58]): what an insert record would copy.
// Footprint 0x44 bytes, the level 0 cache entry [fp+0x38] (0x24 bytes, verified 0x80E5B9), and its polygon vector
// (+0/+4, 16-byte elements whose +0 is a heap block, verified 0x61EE20; element +4 = block end [inferred]).
// shortcut: level 0 only and no forced polygon build (§11.4 would run EnsureAllPolygons first); upgrade if G1 check 3 is close.
static u8 g_snScratch[1 << 16];
static DWORD g_snScrAt;
static unsigned __int64 g_snCloneTicks;  // sim thread only
static DWORD SnScratch(const void* p, DWORD n) {
    if (n > sizeof(g_snScratch)) n = sizeof(g_snScratch);
    if (g_snScrAt + n > sizeof(g_snScratch)) g_snScrAt = 0;
    memcpy(g_snScratch + g_snScrAt, p, n);
    g_snScrAt += n;
    return n;
}
static bool RpFpHasCache(DWORD vt);  // replay.cpp: a footprint class with a polygon cache at +0x38
static void SnCloneProbe(const u8* fp) {
    Ts3nCounters& k = ts3native_counters;
    if (!fp) return;
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    DWORD bytes = 0;
    __try {
        bytes += SnScratch(fp, 0x44);
        // only classes with a cache have one at +0x38: the box class 0xFEAE08 keeps floats there (-0.5f = 0xBF000000 read as a
        // pointer: 11 first-chance AVs per load in the snap-p4 runs 0.29.0-0.31.2) and the 0x28-byte child class ends before it
        const u8* lv = RpFpHasCache(*(const DWORD*)fp) ? *(const u8* const*)(fp + 0x38) : NULL;
        if (lv && lv[0x20]) {  // +0x20: the level is valid (an invalid one may hold freed arrays)
            bytes += SnScratch(lv, 0x24);
            const u8* b = *(const u8* const*)lv;
            const u8* e = *(const u8* const*)(lv + 4);
            for (int i = 0; b && e > b && b + 16 <= e && i < 256; b += 16, i++) {
                bytes += SnScratch(b, 16);
                const u8* vb = *(const u8* const*)b;
                const u8* ve = *(const u8* const*)(b + 4);
                if (vb && ve > vb) bytes += SnScratch(vb, (DWORD)min(ve - vb, 4096));
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.snCloneFault++; return; }
    QueryPerformanceCounter(&t1);
    g_snCloneTicks += (unsigned __int64)(t1.QuadPart - t0.QuadPart);
    k.snCloneUs = SnUs(g_snCloneTicks);
    k.snCloneN++;
    static DWORD bytesLow;  // bytes below 1 KB carried into the next probe
    bytesLow += bytes;
    k.snCloneBytesK += (LONG)(bytesLow >> 10);
    bytesLow &= 1023;
    if ((LONG)bytes > k.snCloneMaxBytes) k.snCloneMaxBytes = (LONG)bytes;
}

// rebase copy cost (G1 check 4): the lot of a sim LotGraph_Build, copied once at the next START (sim thread only)
static LONG SpProbeLotCopy(unsigned __int64 key, LONG* us);  // snap.cpp
static unsigned __int64 g_snCopyKey;
static bool g_snCopyPending;
static void SnLotCopyProbe() {
    Ts3nCounters& k = ts3native_counters;
    g_snCopyPending = false;
    LONG us = 0;
    const LONG bytes = SpProbeLotCopy(g_snCopyKey, &us);
    if (bytes <= 0) { k.snLotCopyFault++; return; }
    static const LONG kTop[8] = {100, 250, 500, 1000, 2000, 5000, 10000, 0x7FFFFFFF};
    k.snLotCopyN++;
    k.snLotCopyUs[SnBin(us, kTop, 8)]++;
    Max(&k.snLotCopyMaxUs, us);
    Max(&k.snLotCopyMaxKB, bytes >> 10);
}

// --- P1b: unattributed caller table (64 keys: leaf, return address, next frame) ---
static DWORD g_snTextLo, g_snTextHi;  // the exe's first section (code); the selftest points it at its own
static void SnTextInit() {
    const u8* b = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(b + ((const IMAGE_DOS_HEADER*)b)->e_lfanew);
    const IMAGE_SECTION_HEADER* s = IMAGE_FIRST_SECTION(nt);
    g_snTextLo = (DWORD)(uintptr_t)(b + s->VirtualAddress);
    g_snTextHi = g_snTextLo + s->Misc.VirtualSize;
}
// a return address: inside the code section and right after a call (E8 rel32, FF /2 reg, [reg+disp8], [disp32]/[reg+disp32])
static bool SnIsRet(DWORD v) {
    if (v < g_snTextLo + 8 || v >= g_snTextHi) return false;
    const u8* p = (const u8*)(uintptr_t)v;
    return p[-5] == 0xE8 || (p[-2] == 0xFF && (p[-1] & 0xF8) == 0xD0) || (p[-3] == 0xFF && (p[-2] & 0xF8) == 0x50) ||
           (p[-6] == 0xFF && ((p[-5] & 0xF8) == 0x90 || p[-5] == 0x15));
}
// heuristic: the first return address above the leaf's own (frame sizes are not known per caller)
static DWORD SnNextRet(const DWORD* slot) {
    for (int j = 1; j <= 32; j++) if (SnIsRet(slot[j])) return slot[j];
    return 0;
}
static void SnUaNote(int leaf, const DWORD* slot) {  // sim thread only: no interlocked insert
    Ts3nCounters& k = ts3native_counters;
    const DWORD ret = slot[0];
    const LONG key = (LONG)((DWORD)leaf << 24 | (ret < 0x01000000 ? ret : 0x00FFFFFF));
    const LONG next = (LONG)SnNextRet(slot);
    int j = 0;
    while (j < 64 && k.snUa2Key[j] && !(k.snUa2Key[j] == key && k.snUa2Next[j] == next)) j++;
    if (j == 64) { k.snUa2Other++; return; }
    k.snUa2Key[j] = key;
    k.snUa2Next[j] = next;
    k.snUa2N[j]++;
}

static bool SnLot(const SnSite& s, const DWORD* slot, unsigned __int64* key) {
    DWORD lo = 0, hi = 0;
    if (s.lot == 2) { lo = slot[s.arg]; hi = slot[s.arg + 1]; }
    else if (s.lot == 1) {
        const u8* lot = (const u8*)(uintptr_t)slot[s.arg];
        if (lot && (!PvRead(lot + 0x48, &lo) || !PvRead(lot + 0x4C, &hi))) return false;
    } else return false;
    *key = ((unsigned __int64)hi << 32) | lo;
    return true;
}

static void SnSeqNote(LONG* last, const char* what, DWORD at, DWORD tid) {
    const LONG seq = InterlockedIncrement(&ts3native_counters.snSeq);
    *last = seq;
    Log("routesnap %s #%ld at 0x%08lX (thread %lu, sim/worker/other %d)", what, seq, at, tid, PvSwClass(tid));
}

// object events: head nodes per id, one tail node (docs/76 §3.3)
static void SnObjHit(int i) {
    const DWORD tid = GetCurrentThreadId();
    const int cls = PvSwClass(tid);
    InterlockedIncrement(&ts3native_counters.snObj[i][cls]);
    if (cls == 0) t_sn.objDepth++;
}
template <int I> static char __cdecl SnObjHead(void*, void*) { SnObjHit(I); return 1; }
static char __cdecl SnObjTail(void*, void*) {
    if (t_sn.objDepth > 0) t_sn.objDepth--;
    return 1;
}
static void* const kSnObjHeads[SN_OBJ_N] = {(void*)SnObjHead<0>, (void*)SnObjHead<1>, (void*)SnObjHead<2>, (void*)SnObjHead<3>,
                                             (void*)SnObjHead<4>, (void*)SnObjHead<5>, (void*)SnObjHead<6>, (void*)SnObjHead<7>};

static bool SnObjListed(DWORD id, void* fn) {
    DWORD node = 0, f = 0;
    if (!PvRead(&g_snObjHeads[id], &node)) return false;
    for (int n = 0; node && n < 4096; n++) {
        if (!PvRead((const u8*)(uintptr_t)node + 4, &f)) return false;
        if (f == (DWORD)(uintptr_t)fn) return true;
        if (!PvRead((const u8*)(uintptr_t)node + 8, &node)) return false;
    }
    return false;
}

// sim, inside World_FireEvent id 7 (no object chain open there): register what is missing (the lists are freed with the
// object service, so a later world may need them again)
static void SnObjRegister() {
    Ts3nCounters& k = ts3native_counters;
    for (int i = 0; i < SN_OBJ_N; i++) {
        const DWORD id = kSnObjIds[i];
        if (!SnObjListed(id, kSnObjHeads[i])) {
            if (g_snObjReg(id, kSnObjHeads[i], 0x7FFFFFFF)) k.snObjNodes++; else k.snObjRegFails++;
        }
        if (!SnObjListed(id, (void*)SnObjTail)) {
            if (g_snObjReg(id, (void*)SnObjTail, (LONG)0x80000000)) k.snObjNodes++; else k.snObjRegFails++;
        }
    }
    if (k.snObjNodes) k.snReady |= 8;
}

static void AsWorldEvent(DWORD id);  // autshadow.cpp ([AutShadow] change tracking, a consumer only)
static void C2WorldEvent(DWORD id);  // c2diag.cpp ([Crash2Diag] world phase, docs/259 §9)
static void AsEpochBump();
static void SpNote(int i, const DWORD* slot);  // snap.cpp (P3 dirty marks)
static void SpIcNote(int i);
static void SpStart();
static const u8* SpWorld();  // snap.cpp: the routing world (RoutingWorld_Get slot)

// 0.31.4 (docs/94 §21.11): LotRouting_RemoveObjectFootprints 0x635700 with lot 0 walks map M's range for the id. An FME whose
// removal is pending in P+0x3C (deferred while its Q was iterated, 0x666E30) is stepped over twice: once by the pending check
// (0x635794..0x6357A8) and once more after Q_FindFootprint 0x6649C0 answers 0 for a pending id (0x63580B). When that FME ends
// the range, the walk passes the range end, runs to the bucket sentinel and reads [0xFFFFFFFF + 8] at 0x63576A (the bp-snap-p4
// crash on 0.31.2) [verified disassembly; reproduced on the game's code in steerro-test G2]. Before the original runs, the Qs
// with a pending remove of this id are flushed with the game's own LotQuadTree_FlushDeferred 0x666A40, as the game's remove
// 0x666E30 does whenever the Q is not being iterated; an iterated Q (+0x48 > 0) cannot be flushed and is counted.
typedef void(__fastcall* SnQFn)(void* q, void* edx);
static SnQFn g_snFlushDeferred = (SnQFn)(uintptr_t)0x00666A40;
static void SnLotRemovePending(DWORD lo, DWORD hi) {
    Ts3nCounters& k = ts3native_counters;
    u8* qs[8];
    int n = 0;
    __try {
        const u8* world = SpWorld();
        const u8* p = world ? *(const u8* const*)(world + 0x95C) : NULL;
        const u8* m = p ? *(const u8* const*)(p + 0x3C) : NULL;  // pending removes: EASTL map, node key +0/+4, Q +8, next +0x10
        if (!m || !*(const DWORD*)(m + 0xC)) return;
        const DWORD nb = *(const DWORD*)(m + 8);
        const DWORD* bk = *(const DWORD* const*)(m + 4);
        if (!nb || !bk) return;
        for (const u8* nd = (const u8*)(uintptr_t)bk[lo % nb]; nd && n < 8; nd = *(const u8* const*)(nd + 0x10)) {
            u8* q = *(u8* const*)(nd + 8);
            if (*(const DWORD*)nd != lo || *(const DWORD*)(nd + 4) != hi || !q) continue;
            bool dup = false;
            for (int i = 0; i < n; i++) dup = dup || qs[i] == q;
            if (!dup) qs[n++] = q;
        }
        for (int i = 0; i < n; i++) {
            if (*(const LONG*)(qs[i] + 0x48) <= 0) continue;
            if (InterlockedIncrement(&k.lrPendBusy) <= 8)
                Log("routesnap: pending remove of id 0x%08lX%08lX on Q %p while it is iterated (+0x48 > 0): lot-0 remove 0x635700 "
                    "may walk past its range (0x63576A)", hi, lo, qs[i]);
            qs[i--] = qs[--n];
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    const UafTag uafPrev = UafTagSet(UT_LOTRM_FLUSH, _ReturnAddress());  // [UafCheck] (manual pair: __try above)
    for (int i = 0; i < n; i++) { g_snFlushDeferred(qs[i], NULL); InterlockedIncrement(&k.lrPendFlush); CeRec(CE_LOTRM_FLUSH, (DWORD)(uintptr_t)qs[i], lo, hi); }  // outside __try: the game's code
    UafTagPut(uafPrev);
}

// 0.31.5 [LotRemoveGuard] (docs/94 §21.12): the same guard without [RouteSnap]. The precondition is the game's own (a remove
// deferred while its Q is iterated, still pending at a later lot-0 call for that id), not one only our reordering makes, so
// it gets an entry detour of its own on every build; with [RouteSnap] the stub above already runs it.
typedef u8(__fastcall* LrRemoveFn)(void* p, void* edx, DWORD lotLo, DWORD lotHi, DWORD idLo, DWORD idHi);
static LrRemoveFn g_lrOrig = (LrRemoveFn)(uintptr_t)0x00635700;
static u8 __fastcall LrGuardHook(void* p, void* edx, DWORD lotLo, DWORD lotHi, DWORD idLo, DWORD idHi) {
    if (!(lotLo | lotHi)) SnLotRemovePending(idLo, idHi);
    return g_lrOrig(p, edx, lotLo, lotHi, idLo, idHi);
}
static void InstallLotRemoveGuard(const wchar_t* ini) {
    for (int i = 0; i < SN_N; i++)
        if (kSn[i].leaf == SL_LOTREMOVEFP && g_snJmp[i]) { Log("lot-0 remove guard: inside the [RouteSnap] stub"); return; }
    if (!GetPrivateProfileIntW(L"LotRemoveGuard", L"Enabled", 1, ini)) { Log("lot-0 remove guard off (Enabled=0)"); return; }
    static const u8 kPro[] = {0x83, 0xEC, 0x34, 0x8B, 0x44, 0x24, 0x38};  // sub esp,34h; mov eax,[esp+38h]
    InstallDetour("LotRouting_RemoveObjectFootprints (lot-0 guard)", 0x00635700, kPro, sizeof(kPro), (void*)LrGuardHook, &g_lrOrig);
}
static volatile LONG g_snWorldStopLog;
// slot = the return-address slot at the hooked instruction (a function entry or a call)
static void __cdecl SnEnter(int i, DWORD* slot) {
    Ts3nCounters& k = ts3native_counters;
    const SnSite& s = kSn[i];
    if (t_rpReplay) return;  // the replay consumer replaying on its clone (replay.cpp RpDrain)
    if (t_snapRun) {  // P4 clone run (docs/94 §12.4.3): no counting; a writer leaf makes its lot worker-dirty, an event trips
        if (i == SN_WEV) P4Abort(P4_AB_EVENT);
        // I-94-231 (docs/94 §21.13.1): the world-level writers edit containers the working copy holds as byte copies: P's
        // hanging-footprint index P+0xEC (live nodes), G's highCostFootprints tree G+0x7D0 (live nodes, EA-freed by 0x670510),
        // and our RpAlloc'd grid, blockedNodeMap and group hash (EA-freed on erase or rehash). None is in the clone run's
        // closure except the unpin of pending portal removes, which the acquire refuses (H20c): stop the run (D4) if one comes
        else if (s.leaf == SL_REMOVELOT || s.leaf == SL_APPLYLOT || s.leaf == SL_GRIDADD || s.leaf == SL_GRIDREMOVE ||
                 s.leaf == SL_HIGHCOST || s.leaf == SL_MARKREMOVE || s.leaf == SL_GBUILD) {
            if (InterlockedIncrement(&g_snWorldStopLog) <= 8) Log("P4: %s in a clone run (byte-copied world containers): run stopped", s.name);
            // 0.35.21 (docs/94 §22.24.1, I-94-484): ApplyLotUpdate's callers write only its own lot before the call (0x82B810:
            // 0x631AA0 / 0x630F10 / 0x631AF0 room and built bytes, 0x63D7B0 -> W4; 0x82C420: reads) [verified disassembly]:
            // that lot goes into the dirty list and the release rebases it instead of rebuilding the clone
            unsigned __int64 key = 0;
            if (s.leaf == SL_APPLYLOT && SnLot(s, slot, &key)) { P4WorkerDirty(true, key); P4LazyKeep(); }
            P4Lazy();
        } else if (s.leaf != SL_SRC && i != SN_FPEN) {
            unsigned __int64 key = 0;
            bool known = s.leaf != SL_GBUILD && s.leaf != SL_BLEND && SnLot(s, slot, &key);
            // 0.35.14 (docs/94 §22.17, I-94-471): W1 / W2 (ecx = Q, lot id at Q+0x18/+0x1C) and W4 (arg 1 = the lot graph,
            // lot id at +0/+4, replay.cpp RpPreBody) name their lot; before, every one made the release rebase all lots
            DWORD lo = 0, hi = 0;
            if (!known && (s.leaf == SL_W1 || s.leaf == SL_W2))
                known = PvRead((const u8*)(uintptr_t)slot[-2] + 0x18, &lo) && PvRead((const u8*)(uintptr_t)slot[-2] + 0x1C, &hi);
            else if (!known && s.leaf == SL_W4)
                known = PvRead((const u8*)(uintptr_t)slot[1], &lo) && PvRead((const u8*)(uintptr_t)slot[1] + 4, &hi);
            else if (!known && s.leaf == SL_W3) lo = slot[1], hi = slot[2], known = (lo | hi) != 0;  // lot id, args 1-2 (0.35.21)
            else if (!known && i == SN_W6B) {  // 0.35.21 (docs/94 §22.24.2): ecx = a clone footprint, its lots from the clone map
                unsigned __int64 lots[8];
                const u8* fp = (const u8*)(uintptr_t)slot[-2];
                const int nl = RpCloneFpLots(fp, lots, 8);
                if (nl < 0) {  // 0.35.23: a live footprint's polygon cache, no clone memory: nothing to rebase
                    static volatile LONG s_liveW6b;
                    const LONG n = InterlockedIncrement(&s_liveW6b);
                    if (n <= 8 || (n & (n - 1)) == 0) {
                        DWORD vt = 0;
                        PvRead(fp, &vt);
                        Log("P4: W6b on a live footprint in a clone run (footprint 0x%08lX class 0x%08lX): no clone lot written, %ld so far",
                            (DWORD)(uintptr_t)fp, vt, n);
                    }
                    return;
                }
                for (int j = 0; j + 1 < nl; j++) P4WorkerDirty(true, lots[j]);
                if (nl > 0) known = true, key = lots[nl - 1];
            }
            if (lo | hi) key = ((unsigned __int64)hi << 32) | lo;
            if (!known) {  // the write kinds that still force an all-lot rebase: logged at 1, 10, 100, ... per site
                static volatile LONG s_unk[SN_N];
                const LONG n = InterlockedIncrement(&s_unk[i]);
                LONG p = 1;
                while (p < n && p < 100000000) p *= 10;
                if (p == n) Log("P4: clone run write with no lot (all lots rebased at release): %s, %ld so far", s.name, n);
            }
            P4WorkerDirty(known, key);
        }
        return;
    }
    if (s.leaf == SL_LOTREMOVEFP && !(slot[1] | slot[2])) SnLotRemovePending(slot[3], slot[4]);  // before the flush's own leaves
    const DWORD tid = GetCurrentThreadId();
    const int cls = PvSwClass(tid);
    SnThread& t = t_sn;
    SnDrop(t, slot);
    SpNote(i, slot);
    if (i == SN_WEV) {  // World_FireEvent(arg 1, id): id read at 0x78F2B3 from [esp+0x4C] after sub esp,0x44
        const DWORD id = slot[2];
        InterlockedIncrement(&k.snWev[id < 95 ? id : 95]);
        if (cls != 0) { InterlockedIncrement(&k.snWevOffSim); return; }
        AsWorldEvent(id);
        C2WorldEvent(id);
        if (id == 7) { SnSeqNote(&k.snSeqEv7, "world event 7", s.at, tid); SnObjRegister(); }
        SnPush(t, slot, SRC_WEV, id < 255 ? id : 255);
        return;
    }
    if (i == SN_FPEN) {
        InterlockedIncrement(&k.snFpEn[cls]);
        if (cls == 0) SnPush(t, slot, SRC_FPEN, 0);
        return;
    }
    if (s.leaf == SL_SRC) {  // P1b frames: a frame only at our own stub (see the header)
        if (cls != 0) return;
        k.snFrameCalls[i - SN_F0]++;
        if (g_snJmp[i]) SnPush(t, slot, s.arg, 0);
        return;
    }
    const int leaf = s.leaf;
    if (leaf == SL_BLEND && slot[3] != 4) return;  // the job's other phases (cmp [esp+0x24],4 at 0xC62AC3)
    InterlockedIncrement(&k.snLeaf[leaf][cls]);
    if (leaf == SL_GBUILD) {
        InterlockedIncrement(&k.snGSite[i - SN_G0]);
        SnSeqNote(&k.snSeqGBuild, "G build", s.at, tid);
        return;
    }
    if (leaf == SL_BLEND) return;  // main-thread render job (docs/94 §9.5 exception 1): counted, not a writer-layer mark
    unsigned __int64 key = 0;
    const bool known = SnLot(s, slot, &key);
    SnMark(known, key);
    const int rk = SnRecKind(leaf);
    if (rk >= 0 && cls != 1) SnRecord(rk, cls == 0 && SnInRebase(t));  // the route worker's writes go to its clone in design B
    if (cls != 0) return;
    if (leaf == SL_W1) SnCloneProbe((const u8*)(uintptr_t)slot[1]);
    if (leaf == SL_W4 && !g_snCopyPending) {  // W4 arg 1 = the lot graph = lot table node + 0x10 (snap.cpp SpRefNote)
        DWORD lo, hi;
        const u8* node = (const u8*)(uintptr_t)slot[1] - 0x10;
        if (slot[1] && PvRead(node, &lo) && PvRead(node + 4, &hi)) { g_snCopyKey = (unsigned __int64)hi << 32 | lo; g_snCopyPending = true; }
    }
    int idx;
    const int src = SnSource(t, &idx);
    InterlockedIncrement(src < SRC_N ? &k.snSrc[leaf][src] : &k.snSrc2[leaf][src - SRC_N]);
    if (src == SRC_IC) InterlockedIncrement(&k.snIcWrites[idx]);
    if (src != SRC_NONE) return;
    if (leaf == SL_W6) { k.snCacheFill++; return; }  // a reader filling a lazy polygon cache, not an input change
    InterlockedIncrement(&k.snUnattributed);
    SnUaNote(leaf, slot);
}

static void P4VarTrip(DWORD handle);  // p4.cpp
static void __cdecl SnIcEnter(int i, DWORD* slot) {
    if (t_snapRun) return;  // P4 clone run: pass through
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.snIc[i]);
    if (i == 5 && SnLive()) P4VarTrip(slot[3]);  // AddPortalLocationVariation, arg 3 = portal handle: H20 tripwire, count only
    const DWORD tid = GetCurrentThreadId();
    const bool sim = RgIsSim(tid);
    SnThread& t = t_sn;
    if (sim) SnDrop(t, slot);
    // inline writers record the new value from the slot shim (design B): lock keys 0-3, portal cost/flags/lock/ocean 12-15
    if (i <= 3 || (i >= 12 && i <= 15)) SnRecord(i <= 3 ? RK_LOCKKEY : RK_PORTAL, sim && SnInRebase(t));
    if (!sim) { InterlockedIncrement(&k.snIcOffSim); return; }
    if (i == 22 || i == 23) AsEpochBump();  // [AutShadow]: build/buy boundary (OnExitBuildBuy, LotClusterTrees), docs/122 §2.1
    if (t.objDepth) { k.snObjOpenAtIc++; t.objDepth = 0; }  // a chain that stopped before our tail node
    SpIcNote(i);
    if (i >= SN_IC_START) {
        SnStart();
        SpStart();
        if (g_snCopyPending) SnLotCopyProbe();
    }
    SnPush(t, slot, SRC_IC, i);
}

#define SN_STUB(i) static __declspec(naked) void SnStub##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call SnEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_snJmp + i * 4] }
SN_STUB(0) SN_STUB(1) SN_STUB(2) SN_STUB(3) SN_STUB(4) SN_STUB(5) SN_STUB(6) SN_STUB(7) SN_STUB(8) SN_STUB(9) SN_STUB(10)
SN_STUB(11) SN_STUB(12) SN_STUB(13) SN_STUB(14) SN_STUB(15) SN_STUB(16) SN_STUB(17) SN_STUB(18) SN_STUB(19) SN_STUB(20)
SN_STUB(21) SN_STUB(22) SN_STUB(23) SN_STUB(24) SN_STUB(25) SN_STUB(26) SN_STUB(27) SN_STUB(28) SN_STUB(29) SN_STUB(30)
SN_STUB(31) SN_STUB(32) SN_STUB(33) SN_STUB(34) SN_STUB(35) SN_STUB(36) SN_STUB(37)
static void (*const kSnStubs[SN_N])() = {
    SnStub0, SnStub1, SnStub2, SnStub3, SnStub4, SnStub5, SnStub6, SnStub7, SnStub8, SnStub9, SnStub10, SnStub11, SnStub12,
    SnStub13, SnStub14, SnStub15, SnStub16, SnStub17, SnStub18, SnStub19, SnStub20, SnStub21, SnStub22, SnStub23, SnStub24,
    SnStub25, SnStub26, SnStub27, SnStub28, SnStub29, SnStub30, SnStub31, SnStub32, SnStub33, SnStub34, SnStub35, SnStub36,
    SnStub37};

#define SN_IC(i) static __declspec(naked) void SnIc##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call SnIcEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_snIcOrig + i * 4] }
SN_IC(0) SN_IC(1) SN_IC(2) SN_IC(3) SN_IC(4) SN_IC(5) SN_IC(6) SN_IC(7) SN_IC(8) SN_IC(9) SN_IC(10) SN_IC(11) SN_IC(12)
SN_IC(13) SN_IC(14) SN_IC(15) SN_IC(16) SN_IC(17) SN_IC(18) SN_IC(19) SN_IC(20) SN_IC(21) SN_IC(22) SN_IC(23) SN_IC(24)
SN_IC(25) SN_IC(26) SN_IC(27) SN_IC(28) SN_IC(29) SN_IC(30) SN_IC(31) SN_IC(32) SN_IC(33) SN_IC(34) SN_IC(35) SN_IC(36)
SN_IC(37) SN_IC(38)
static void (*const kSnIcShims[SN_IC_N])() = {
    SnIc0, SnIc1, SnIc2, SnIc3, SnIc4, SnIc5, SnIc6, SnIc7, SnIc8, SnIc9, SnIc10, SnIc11, SnIc12, SnIc13, SnIc14, SnIc15,
    SnIc16, SnIc17, SnIc18, SnIc19, SnIc20, SnIc21, SnIc22, SnIc23, SnIc24, SnIc25, SnIc26, SnIc27, SnIc28, SnIc29, SnIc30,
    SnIc31, SnIc32, SnIc33, SnIc34, SnIc35, SnIc36, SnIc37, SnIc38};

// one site at `at` (calling `target`, 0 = an entry): 1 hooked, 2 counted inside another stub of ours, 0 skipped
static int SnInstallAt(int i, DWORD at, DWORD target) {
    const SnSite& s = kSn[i];
    for (int q = 0; q < QL_N; q++)
        if (!target && g_qlTramp[q] && kQlSites[q].addr == at) { g_snQl[q] = i + 1; return 2; }
    for (int r = 0; r < RG_SITES; r++)
        if (!target && g_rgTramp[r] && kRgSites[r].addr == at) { g_snRg[r] = i + 1; return 2; }
    for (int p = 0; p < PV_N; p++)
        if (g_pvJmp[p] && kPv[p].at == at && kPv[p].target == target) { g_snPv[p] = i + 1; return 2; }
    u8 want[10];
    if (target) {
        want[0] = 0xE8;
        const LONG rel = (LONG)(target - (at + 5));
        memcpy(want + 1, &rel, 4);
    } else memcpy(want, s.pro, s.len);
    const u8* p = (const u8*)(uintptr_t)at;
    if (!Matches(p, want, s.len)) {
        char hex[64];
        if (Readable(p, s.len)) Hex(p, s.len, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
        Log("SKIP routesnap %s: bytes at 0x%08lX differ (found %s)", s.name, at, hex);
        return 0;
    }
    if (!target) {
        char name[64];
        sprintf_s(name, "%s (routesnap)", s.name);
        return (g_snJmp[i] = InstallDetour(name, at, s.pro, s.len, (void*)kSnStubs[i], &g_snJmp[i])) != NULL;
    }
    g_snJmp[i] = (void*)(uintptr_t)target;
    const LONG rel = (LONG)((DWORD)(uintptr_t)kSnStubs[i] - (at + 5));
    return Write((void*)(p + 1), &rel, 4) ? 1 : 0;
}

// one IC slot: the expected fn, else another owner (or a different exe) and the slot stays
static bool SnSwapIc(int i) {
    DWORD* slot = (DWORD*)(uintptr_t)g_snIc[i].slot;
    DWORD cur = 0;
    if (!PvRead(slot, &cur) || cur != g_snIc[i].fn) {
        Log("SKIP routesnap IC slot %s at 0x%08lX: holds 0x%08lX, expected 0x%08lX", g_snIc[i].name, g_snIc[i].slot, cur, g_snIc[i].fn);
        return false;
    }
    g_snIcOrig[i] = (void*)(uintptr_t)cur;
    const DWORD shim = (DWORD)(uintptr_t)kSnIcShims[i];
    return Write(slot, &shim, 4);
}

// [RouteSnap] Count=1: every site checked and hooked or shared now (DllMain, after [ProbeValues]); nodes at world event 7
static bool ApplyRouteSnap(const wchar_t* ini) {
    // [PlanRepeat] (planrepeat.cpp) and [RouteAsync] ReplanDedupe (replandedupe.cpp) read these counters as their world version
    if (!GetPrivateProfileIntW(L"RouteSnap", L"Count", 0, ini) && !GetPrivateProfileIntW(L"RouteSnap", L"Enabled", 0, ini) &&
        !GetPrivateProfileIntW(L"PlanRepeat", L"Enabled", 0, ini) && !GetPrivateProfileIntW(L"RouteAsync", L"ReplanDedupe", 0, ini) &&
        !GetPrivateProfileIntW(L"RouteAsync", L"FailMemo", 0, ini)) {  // docs/164 §7 D5 reads them too
        Log("route snapshot counters off (Count=0)");
        return false;
    }
    Ts3nCounters& k = ts3native_counters;
    memcpy(g_snIc, kSnIcDef, sizeof(g_snIc));
    SnTextInit();
    for (int i = 0; i < SN_N; i++) {
        const int r = SnInstallAt(i, kSn[i].at, kSn[i].target);
        if (r == 1) k.snHooks++; else if (r == 2) k.snShared++; else k.snSkipped++;
    }
    for (int i = 0; i < SN_IC_N; i++) if (SnSwapIc(i)) k.snIcSlots++;
    g_snOn = 1;
    k.snReady = (k.snHooks + k.snShared ? 1 : 0) | (g_snJmp[SN_WEV] ? 2 : 0) | (k.snIcSlots ? 4 : 0);
    Log("%s route snapshot counters: %ld sites hooked, %ld counted inside another stub, %ld skipped; %ld of %d IC slots",
        k.snSkipped || k.snIcSlots != SN_IC_N ? "PART" : "OK  ", k.snHooks, k.snShared, k.snSkipped, k.snIcSlots, SN_IC_N);
    return true;
}
