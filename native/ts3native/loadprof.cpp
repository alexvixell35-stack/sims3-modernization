// [LoadProf] (docs/39 §10, 0.32.0, counters v59), measurement only: how much of a world load is the game building routing
// data (R load and routing world setup, G / water grid, lot table and lot quadtrees, lot graphs and room graphs, footprint
// inserts and polygon caches, lot routing updates) versus everything else. Nothing is changed or skipped: every stub
// times the call and runs the game's own code with the same registers, flags and stack. Included by ts3native.cpp after
// p4clone.cpp; installed after every other section except [Verify], so a site another section already owns is skipped
// (logged) and that section keeps it.
//
// Window: a WorldManager_LoadWorld 0xC6CF80 call (from 0xAABE33, the save load, or 0xC6D9E8) opens it and zeroes the timing
// rows; world event 7 (ScriptHost_PostWorldReady 0x769950, its World_FireEvent call 0x769993) closes it and writes the
// summary to the log. Row 0 = calls that started inside the window, row 1 = calls after it (lazy builds in play).
// Routing sites nest (RoomGraph_FirstUse -> LotGraph_Build -> QuadTreeBuild -> ApplyInsert ...): per-site times are
// inclusive, and lpRtUs / lpRtThrUs add only the outermost routing frame of each thread, so they never double count.
// Phase sites (LoadWorld, World_LotStreaming on main, lot creation and loading, World_PostLoadInit on main) give the coarse
// split; they do not count as routing.
// Stubs: the values.cpp pattern (pushad/pushfd, LpEnter(id, return slot), popfd/popad, jmp on; the leave swaps the return
// address for LpLeaveThunk and keeps the real one on a per-thread stack of 32; frames left by an exception unwind are dropped
// at the thread's next entry). Prologues and call edges verified on the Steam 1.67 exe (capstone + raw branch scan,
// 2026-10-19: whole instructions, no relative operand, no branch into the stolen bytes; every call site is E8 to the
// target); steerro-test re-checks every row on the image.
// shortcut: the zeroing at a LoadWorld entry races a routing frame still open on another thread (it adds into the new rows);
// at a load start that is the menu world's tail at most. Upgrade if a window ever shows a call that started before it.
// shortcut: *Us are LONG microseconds (wrap after ~35 min of summed time); a load is minutes at most.

enum {
    // E: entries (routing)
    LP_RTDATA, LP_LOTINS, LP_QTBUILD, LP_LOTGRAPH, LP_ROOMFIRST, LP_APPLYLOT, LP_RESTORE, LP_FPLEVEL, LP_FPALL, LP_FLUSH,
    LP_FPCORE, LP_QTINS,
    // E: entries (phases)
    LP_STREAMING, LP_POSTLOAD,
    // C: call sites (routing)
    LP_WORLDINIT, LP_WATER0, LP_WATER1, LP_WATER2, LP_BWRD0, LP_BWRD1, LP_BWRD2, LP_FLOOR,
    // C: call sites (phases, window, event 7)
    LP_LOADWORLD0, LP_LOADWORLD1, LP_CREATELOTS, LP_LOADLOTS, LP_LOADLOTS2, LP_EV7,
    LP_N
};
enum { LPK_ROUTING, LPK_PHASE, LPK_EVENT };
struct LpSite { DWORD at; u8 len; u8 pro[8]; DWORD target; u8 kind; const char* name; };
static const LpSite kLpSites[LP_N] = {
    {0x0066D110, 6, {0x83, 0xEC, 0x58, 0x53, 0x55, 0x56}, 0, LPK_ROUTING, "RTData_Load (R)"},
    {0x0062E680, 8, {0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x14}, 0, LPK_ROUTING, "LotTable_Insert"},
    {0x0062F8A0, 7, {0x8B, 0x44, 0x24, 0x08, 0x83, 0xEC, 0x14}, 0, LPK_ROUTING, "LotRouting_QuadTreeBuild"},
    {0x0063CB90, 8, {0x83, 0xEC, 0x5C, 0x53, 0x8B, 0x5C, 0x24, 0x64}, 0, LPK_ROUTING, "LotGraph_Build"},
    {0x0063D2F0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, 0, LPK_ROUTING, "RoomGraph_FirstUse"},
    {0x0082B590, 5, {0x83, 0xEC, 0x2C, 0x56, 0x57}, 0, LPK_ROUTING, "RoutingWorld_ApplyLotUpdate"},
    {0x0082A190, 7, {0x8B, 0x44, 0x24, 0x04, 0x85, 0xC0, 0x56}, 0, LPK_ROUTING, "RoutingWorld_RestoreLotBlockedBits"},
    {0x0080E5B0, 5, {0x53, 0x8B, 0x5C, 0x24, 0x08}, 0, LPK_ROUTING, "Footprint_EnsureLevelPolygons"},
    {0x0080E610, 8, {0x51, 0x57, 0x8B, 0xF9, 0x83, 0x7F, 0x38, 0x00}, 0, LPK_ROUTING, "Footprint_EnsureAllPolygons"},
    {0x00666A40, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, 0, LPK_ROUTING, "LotQuadTree_FlushDeferred"},
    {0x008321F0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, LPK_ROUTING, "RoutingWorld_UpdateObjectFootprintCore"},
    {0x00665C50, 6, {0x81, 0xEC, 0x80, 0x00, 0x00, 0x00}, 0, LPK_ROUTING, "LotQuadTree_ApplyInsert"},
    {0x00C622D0, 7, {0x83, 0xEC, 0x1C, 0x53, 0x55, 0x56, 0x57}, 0, LPK_PHASE, "World_LotStreaming (world init, main)"},
    {0x00C6AEB0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, LPK_PHASE, "World_PostLoadInit (main)"},
    {0x00C626F8, 5, {}, 0x00829410, LPK_ROUTING, "RoutingWorld_LockedOp_WorldInit (routing world setup)"},
    {0x008352F1, 5, {}, 0x0082A030, LPK_ROUTING, "WaterGrid_BuildFromWorld from 0x8352F1"},
    {0x00835390, 5, {}, 0x0082A030, LPK_ROUTING, "WaterGrid_BuildFromWorld from 0x835390 (load)"},
    {0x008358B5, 5, {}, 0x0082A030, LPK_ROUTING, "WaterGrid_BuildFromWorld from 0x8358B5"},
    {0x00C6BE53, 5, {}, 0x008349C0, LPK_ROUTING, "BuildWorldRoutingData from 0xC6BE53"},
    {0x00C6CD02, 5, {}, 0x008349C0, LPK_ROUTING, "BuildWorldRoutingData from 0xC6CD02"},
    {0x00835A27, 5, {}, 0x008349C0, LPK_ROUTING, "BuildWorldRoutingData from 0x835A27"},
    {0x00ACF3CF, 5, {}, 0x00834DC0, LPK_ROUTING, "RoutingSystem_LotLoadFloorUpdate (Lot_FinishLoad)"},
    {0x00AABE33, 5, {}, 0x00C6CF80, LPK_PHASE, "WorldManager_LoadWorld from 0xAABE33"},
    {0x00C6D9E8, 5, {}, 0x00C6CF80, LPK_PHASE, "WorldManager_LoadWorld from 0xC6D9E8"},
    {0x00C6D105, 5, {}, 0x00C65E00, LPK_PHASE, "WorldManager_CreateLotsFromLayer"},
    {0x00C6D264, 5, {}, 0x00C678D0, LPK_PHASE, "WorldManager_LoadLots"},
    {0x00C6D24E, 5, {}, 0x00C64630, LPK_PHASE, "WorldManager_LoadLots 0xC64630"},
    {0x00769993, 5, {}, 0x0078F2B0, LPK_EVENT, "World_FireEvent 7 (load finished)"},
};

static void* g_lpJmp[LP_N];        // trampoline (E) or original callee (C)
static volatile LONG g_lpOpen;     // 1 between a LoadWorld entry and world event 7
static LONGLONG g_lpT0;            // QPC at the LoadWorld entry that opened the window

struct LpFrame { DWORD* slot; DWORD ret; u8 id; u8 row; u8 outer; LONGLONG t0; };
struct LpThread { int n; LpFrame f[32]; };
static __declspec(thread) LpThread t_lp;

static void __cdecl LpLeaveThunk();
static LONGLONG LpNow() { LARGE_INTEGER q; QueryPerformanceCounter(&q); return q.QuadPart; }
static LONG LpUs(LONGLONG t0) { return (LONG)((LpNow() - t0) * 1000000 / g_qpf.QuadPart); }
static int LpThreadClass(DWORD tid) { return tid == g_mainTid ? 0 : RgIsSim(tid) ? 1 : 2; }  // main, sim, other

static void LpSummary(LONG windowMs) {
    Ts3nCounters& k = ts3native_counters;
    const LONG rt = k.lpRtUs[0] / 1000;
    Log("loadprof window %ld ms (LoadWorld entry -> world event 7): routing %ld ms = %ld.%ld%% (main %ld, sim %ld, other %ld ms)",
        windowMs, rt, windowMs ? rt * 100 / windowMs : 0, windowMs ? rt * 1000 / windowMs % 10 : 0, k.lpRtThrUs[0] / 1000,
        k.lpRtThrUs[1] / 1000, k.lpRtThrUs[2] / 1000);
    for (int i = 0; i < LP_N; i++)
        if (k.lpSite[i][0][0])
            Log("loadprof   %-8s %s: %ld calls, %ld.%03ld ms, max %ld.%03ld ms", kLpSites[i].kind == LPK_ROUTING ? "routing" : "phase",
                kLpSites[i].name, k.lpSite[i][0][0], k.lpSite[i][0][1] / 1000, k.lpSite[i][0][1] % 1000, k.lpSite[i][0][2] / 1000,
                k.lpSite[i][0][2] % 1000);
}

// slot = the return-address slot at the hooked instruction ([esp] at a function entry or at the call)
static void __cdecl LpEnter(int id, DWORD* slot) {
    Ts3nCounters& k = ts3native_counters;
    LpThread& t = t_lp;
    while (t.n && t.f[t.n - 1].slot <= slot) t.n--;  // frames whose return slot is gone (unwound)
    if (id == LP_EV7) {
        if (InterlockedExchange(&g_lpOpen, 0)) {
            k.lpWindowMs = LpUs(g_lpT0) / 1000;
            k.lpEv7++;
            LpSummary(k.lpWindowMs);
        }
        return;
    }
    if (id == LP_LOADWORLD0 || id == LP_LOADWORLD1) {
        memset(&k.lpWindowMs, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, lpWindowMs));
        g_lpT0 = LpNow();
        InterlockedExchange(&g_lpOpen, 1);
        k.lpLoads++;
    }
    if (t.n == 32) { InterlockedIncrement(&k.lpFrameFull); return; }
    LpFrame& f = t.f[t.n++];
    f.slot = slot;
    f.ret = *slot;
    f.id = (u8)id;
    f.row = g_lpOpen ? 0 : 1;
    f.outer = kLpSites[id].kind == LPK_ROUTING;
    for (int i = 0; f.outer && i < t.n - 1; i++) f.outer = kLpSites[t.f[i].id].kind != LPK_ROUTING;  // no routing frame below
    f.t0 = LpNow();
    *slot = (DWORD)(uintptr_t)LpLeaveThunk;
}

// at = the slot the swapped return address was popped from (+4 = the caller's esp). Returns the real return address.
static DWORD __cdecl LpLeave(DWORD* at) {
    Ts3nCounters& k = ts3native_counters;
    LpThread& t = t_lp;
    int i = 0;
    while (i < t.n - 1 && t.f[i].slot > at) i++;  // only frames we swapped return here, so one is at or below `at`
    const LpFrame f = t.f[i];
    t.n = i;
    const LONG us = LpUs(f.t0);
    LONG* row = k.lpSite[f.id][f.row];
    InterlockedIncrement(&row[0]);
    InterlockedExchangeAdd(&row[1], us);
    Max(&row[2], us);
    if (f.outer) {
        InterlockedExchangeAdd(&k.lpRtUs[f.row], us);
        if (f.row == 0) InterlockedExchangeAdd(&k.lpRtThrUs[LpThreadClass(GetCurrentThreadId())], us);
    }
    return f.ret;
}

static __declspec(naked) void __cdecl LpLeaveThunk() {
    __asm {
        push eax  // becomes the return address
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        call LpLeave
        add esp, 4
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}

#define LP_STUB(i) static __declspec(naked) void LpStub##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call LpEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_lpJmp + i * 4] }
LP_STUB(0) LP_STUB(1) LP_STUB(2) LP_STUB(3) LP_STUB(4) LP_STUB(5) LP_STUB(6) LP_STUB(7) LP_STUB(8) LP_STUB(9) LP_STUB(10)
LP_STUB(11) LP_STUB(12) LP_STUB(13) LP_STUB(14) LP_STUB(15) LP_STUB(16) LP_STUB(17) LP_STUB(18) LP_STUB(19) LP_STUB(20)
LP_STUB(21) LP_STUB(22) LP_STUB(23) LP_STUB(24) LP_STUB(25) LP_STUB(26) LP_STUB(27)
static void (*const kLpStubs[LP_N])() = {
    LpStub0, LpStub1, LpStub2, LpStub3, LpStub4, LpStub5, LpStub6, LpStub7, LpStub8, LpStub9, LpStub10, LpStub11, LpStub12,
    LpStub13, LpStub14, LpStub15, LpStub16, LpStub17, LpStub18, LpStub19, LpStub20, LpStub21, LpStub22, LpStub23, LpStub24,
    LpStub25, LpStub26, LpStub27};

// the expected bytes of a site: the prologue (E, target 0) or E8 rel32 to the target (C)
static void LpExpect(const LpSite& s, DWORD at, DWORD target, u8* out) {
    if (!target) { memcpy(out, s.pro, s.len); return; }
    out[0] = 0xE8;
    const LONG rel = (LONG)(target - (at + 5));
    memcpy(out + 1, &rel, 4);
}

// one site at `at` calling `target` (0 = an entry): the game's, or a selftest dummy of the same shape. True = hooked
static bool LpInstallAt(int id, DWORD at, DWORD target) {
    const LpSite& s = kLpSites[id];
    u8 want[8];
    LpExpect(s, at, target, want);
    const u8* p = (const u8*)(uintptr_t)at;
    if (!Matches(p, want, s.len)) {
        char hex[64];
        if (Readable(p, s.len)) Hex(p, s.len, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
        Log("SKIP loadprof %s: bytes at 0x%08lX differ (found %s; another section owns it?)", s.name, at, hex);
        return false;
    }
    if (!target) {
        char name[80];
        sprintf_s(name, "%s (loadprof)", s.name);
        return (g_lpJmp[id] = InstallDetour(name, at, s.pro, s.len, (void*)kLpStubs[id], &g_lpJmp[id])) != NULL;
    }
    g_lpJmp[id] = (void*)(uintptr_t)target;
    const LONG rel = (LONG)((DWORD)(uintptr_t)kLpStubs[id] - (at + 5));
    return Write((void*)(p + 1), &rel, 4);
}

static bool ApplyLoadProf(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"LoadProf", L"Enabled", 0, ini)) { Log("load profile off (Enabled=0)"); return false; }
    Ts3nCounters& k = ts3native_counters;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    for (int i = 0; i < LP_N; i++) {
        if (LpInstallAt(i, kLpSites[i].at, kLpSites[i].target)) k.lpHooks++;
        else k.lpSkipped++;
    }
    // the window needs both ends: no LoadWorld or no event-7 stub = no window, only totals
    k.lpReady = (g_lpJmp[LP_LOADWORLD0] || g_lpJmp[LP_LOADWORLD1]) && g_lpJmp[LP_EV7] ? 1 : 0;
    Log("%s load profile: %ld of %d sites hooked (%ld skipped), window %s", k.lpSkipped ? "PART" : "OK  ", k.lpHooks, LP_N,
        k.lpSkipped, k.lpReady ? "on" : "OFF (LoadWorld or event-7 site skipped)");
    return true;
}
