// [ProbeValues] (docs/108, 0.24.0, counters v44), observe only: the VALUES the static routing studies could not settle
// (docs/91 §5-§6, docs/98 U6, docs/99 F-1/F-2/F-7/F-11/F-12, docs/100 L1-L4/L6/L8/L10, docs/101 U2/U4-U8, docs/105
// I-105-1/3/4/5). Nothing is changed or skipped: every hook counts, then runs the game's own code with the same registers,
// flags and stack. Included by ts3native.cpp after vcall.cpp.
//
// Two hook kinds, both a pass-through stub (pushad/pushfd, PvEnter(id, return slot), popfd/popad, jmp on):
//   E  entry detour: the verified prologue moves to a trampoline (InstallDetour), as in verify.cpp
//   C  call-site redirect: the rel32 of one `call` (E8) points at the stub, which jumps to the original callee; the caller is
//      the site itself, so no return-address table is needed and an entry detour on the callee (ours or another module's)
//      still runs
// Ids that need the leave (time, return value, "inside X" scopes) swap the return address for PvLeaveThunk and keep the
// real one on a per-thread stack of 16 frames; a frame left without returning (exception unwind) is dropped at the
// thread's next entry, because its return slot is at or below the new entry's (rule of verify.cpp). Handlers do integer
// work only, so x87 returns in ST0 pass through. An entry whose prologue another of our stubs owns (routegate.cpp sites 2
// and 3 with [RouteGate] on) is counted from inside that stub (docs/68 §6 rule 3); any other difference skips the site.
// One poll thread (PollSec, default 2 s) reads the load-time values (world planner tuning, world origin, sea height, G
// header, R checksum, static guards) once per world and the drifting ones on every poll; R is re-checksummed every 60 s.
// Not measured (need a debugger watchpoint, Stalker or script-side code): docs/101 U1, U3, U9, U10, docs/98 U7, docs/105
// I-105-2/6/7/8, docs/91 §6.1 and §6.5, docs/99 F-3/F-4/F-10/F-13, docs/100 L5/L7/L9; docs/108 §4 lists why.
// 0.24.1 (counters v45, docs/112, docs/108 §8): Route_PlanCommon status from argument 1 (was 2: U5 read a caller-local
// vector), display-level raises counted inside the CanSee frame, RoomGraph/EnsureLevelPolygons builds (not entries), both
// R blocked-bit writers logged with the lot, R re-baselined after the first G build, a hash over the blocked bits.
// shortcut: pvGBuildUs is LONG microseconds (wraps after ~35 min of summed build time; builds take ms).

enum {
    // E: entries
    PV_ASTAR, PV_LOTPLAN, PV_IPNUM, PV_PART_A, PV_PART_B, PV_DIRTY, PV_BLK_A, PV_BLK_B, PV_HIGHCOST,
    PV_LAZY0, PV_LAZY1, PV_LAZY2, PV_LAZY3, PV_LAZY4, PV_LAZY5, PV_LAZY6, PV_LAZY7, PV_RESTORE,
    // C: call sites
    PV_GB_MSG, PV_GB_REB, PV_GMSG0, PV_GMSG1, PV_GMSG2, PV_REB0, PV_REB1, PV_REB2, PV_JOB, PV_TIE, PV_IPALLOC, PV_H9REM,
    PV_H9INS, PV_PC_PLAN, PV_PC_POINT, PV_PATH_GOAL, PV_PATH_LOT, PV_PATH_ASTAR, PV_LS_LOCKED, PV_LS_DISC, PV_QUERY,
    PV_CANSEE, PV_RAISE0, PV_RAISE1, PV_EV68,
    PV_N
};
static const int PV_FIRST_C = PV_GB_MSG;
// E: at, len, prologue; C: at, target (the site holds E8 rel32 to it). Prologues: whole instructions, no relative operand,
// no branch into the stolen bytes (capstone on the Steam 1.67 exe, 2026-10-18; steerro-test re-checks every row on the image).
struct PvSite { DWORD at; u8 len; u8 pro[8]; DWORD target; const char* name; };
static const PvSite kPv[PV_N] = {
    {0x00676260, 8, {0x83, 0xEC, 0x3C, 0x53, 0x8B, 0x5C, 0x24, 0x58}, 0, "WorldAStar_Search"},
    {0x0063F6E0, 6, {0x81, 0xEC, 0xC0, 0x01, 0x00, 0x00}, 0, "LotPlan_Inner"},
    {0x00755390, 5, {0x51, 0x8B, 0x44, 0x24, 0x08}, 0, "IC_Route_GetNumInterestPoints"},
    {0x00C08C30, 6, {0x81, 0xEC, 0xD8, 0x00, 0x00, 0x00}, 0, "partition grid 0xC08C30"},
    {0x00C094E0, 5, {0x83, 0xEC, 0x20, 0x53, 0x55}, 0, "partition grid 0xC094E0"},
    {0x00C61FE0, 7, {0x83, 0xB9, 0xB4, 0x01, 0x00, 0x00, 0x00}, 0, "WorldManager_PostWaterGridDirty"},
    {0x0066FB40, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, 0, "blockedNodeMap 0x66FB40"},
    {0x0066F6D0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, 0, "blockedNodeMap 0x66F6D0"},
    {0x006733C0, 7, {0x83, 0xEC, 0x14, 0x8B, 0x44, 0x24, 0x30}, 0, "high-cost polygons 0x6733C0"},
    {0x0062E680, 8, {0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x14}, 0, "LotTable_Insert"},
    {0x0063CB90, 8, {0x83, 0xEC, 0x5C, 0x53, 0x8B, 0x5C, 0x24, 0x64}, 0, "LotGraph_Build"},
    {0x0063D2F0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, 0, "RoomGraph_FirstUse"},
    {0x0082B590, 5, {0x83, 0xEC, 0x2C, 0x56, 0x57}, 0, "RoutingWorld_ApplyLotUpdate"},
    {0x00666A40, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, 0, "LotQuadTree_FlushDeferred"},
    {0x0080E5B0, 5, {0x53, 0x8B, 0x5C, 0x24, 0x08}, 0, "Footprint_EnsureLevelPolygons"},
    {0x00670510, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, "water grid mark 0x670510"},
    {0x00670750, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, "WaterGrid_MarkB"},
    {0x0082A190, 7, {0x8B, 0x44, 0x24, 0x04, 0x85, 0xC0, 0x56}, 0, "RoutingWorld_RestoreLotBlockedBits"},
    {0x0082A098, 5, {}, 0x006734A0, "G build (message path)"},
    {0x00834A82, 5, {}, 0x006734A0, "G build (BuildWorldRoutingData)"},
    {0x008352F1, 5, {}, 0x0082A030, "WaterGrid_BuildFromWorld from 0x8352F1"},
    {0x00835390, 5, {}, 0x0082A030, "WaterGrid_BuildFromWorld from 0x835390"},
    {0x008358B5, 5, {}, 0x0082A030, "WaterGrid_BuildFromWorld from 0x8358B5"},
    {0x00C6BE53, 5, {}, 0x008349C0, "BuildWorldRoutingData from 0xC6BE53"},
    {0x00C6CD02, 5, {}, 0x008349C0, "BuildWorldRoutingData from 0xC6CD02"},
    {0x00835A27, 5, {}, 0x008349C0, "BuildWorldRoutingData from 0x835A27"},
    {0x0082C7AD, 5, {}, 0x0081B2A0, "RoutingJob_Execute"},
    {0x0081BC65, 5, {}, 0x004FA840, "JobRng_Next (goal tie)"},
    {0x00821B3D, 5, {}, 0x004E3F90, "interest point alloc"},
    {0x0063257E, 5, {}, 0x006675B0, "H9 quadtree remove"},
    {0x00632595, 5, {}, 0x00666C50, "H9 quadtree insert"},
    {0x00764298, 5, {}, 0x007606E0, "Route_PlanCommon (Plan/Replan)"},
    {0x007643FB, 5, {}, 0x007606E0, "Route_PlanCommon (point ICs)"},
    {0x0063F65E, 5, {}, 0x0063EC40, "LotPortalSearch (goal step)"},
    {0x00641026, 5, {}, 0x0063EC40, "LotPortalSearch (LotSearch_Entry)"},
    {0x0063E708, 5, {}, 0x00634500, "PortalEdgeCost (lot A*)"},
    {0x0064F55A, 5, {}, 0x0064F2C0, "Portal_LockState (IsLocked)"},
    {0x0064F57A, 5, {}, 0x0064F2C0, "Portal_LockState (IsDiscouraged)"},
    {0x0064F4D3, 5, {}, 0x00791C00, "World_FirePortalLockedQuery"},
    {0x0075AC22, 5, {}, 0x00789DD0, "World_CanSeeObject (radial goal)"},
    {0x00789EB5, 5, {}, 0x00AC0570, "Lot_SetDisplayLevel (CanSee 1)"},
    {0x0078A218, 5, {}, 0x00AC0570, "Lot_SetDisplayLevel (CanSee 2)"},
    {0x00AC0763, 5, {}, 0x007920A0, "World_FireLotLevelInfoChange (event 68)"},
};
static const DWORD PV_INROOM_RET = 0x00632559;  // return of the in-room Portal_IsLocked call 0x632554 (0x632460, H9 path)

static void* g_pvJmp[PV_N];         // trampoline (E) or original callee (C)
static volatile LONG g_pvOn;        // 1 once ApplyProbeValues installed the hooks
static volatile LONG g_pvGDone;     // G builds finished (PV_GB_* leaves): the poll thread re-baselines R after the first
static DWORD g_pvPollMs = 2000;
static volatile LONG g_snPv[PV_N];  // [RouteSnap] site index + 1 counted from inside this stub, 0 = none (routesnap.cpp)

// the expected bytes of a site: the prologue (E, target 0) or E8 rel32 to the target (C)
static void PvExpect(const PvSite& s, DWORD at, DWORD target, u8* out) {
    if (!target) { memcpy(out, s.pro, s.len); return; }
    out[0] = 0xE8;
    const LONG rel = (LONG)(target - (at + 5));
    memcpy(out + 1, &rel, 4);
}

// --- per-thread frames (ids with a leave) ---
struct PvFrame { DWORD* slot; DWORD ret; u8 id; u8 tag; LONG arg; LONGLONG t0; };
struct PvThread { int n; PvFrame f[16]; };
static __declspec(thread) PvThread t_pv;

static void __cdecl PvLeaveThunk();

static bool PvPush(PvThread& t, int id, DWORD* slot, u8 tag, LONG arg, bool timed) {
    if (t.n == 16) { InterlockedIncrement(&ts3native_counters.pvFrameFull); return false; }
    PvFrame& f = t.f[t.n++];
    f.slot = slot;
    f.ret = *slot;
    f.id = (u8)id;
    f.tag = tag;
    f.arg = arg;
    f.t0 = 0;
    if (timed) { LARGE_INTEGER q; QueryPerformanceCounter(&q); f.t0 = q.QuadPart; }
    *slot = (DWORD)(uintptr_t)PvLeaveThunk;
    return true;
}

// innermost open frame of one of the ids [lo, hi], or NULL
static PvFrame* PvOpen(PvThread& t, int lo, int hi) {
    for (int i = t.n - 1; i >= 0; i--) if (t.f[i].id >= lo && t.f[i].id <= hi) return &t.f[i];
    return NULL;
}
static int PvFam(PvThread& t) {  // 0 no Route_PlanCommon frame, 1 Plan/Replan family, 2 point family
    const PvFrame* f = PvOpen(t, PV_PC_PLAN, PV_PC_POINT);
    return f ? f->id - PV_PC_PLAN + 1 : 0;
}

static bool PvRead(const void* p, DWORD* v) {
    __try { *v = *(volatile const DWORD*)p; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return true;
}

// one row: calls / inside a plan / off the sim thread
static void PvLazy(volatile LONG* row, DWORD tid, PvThread& t) {
    InterlockedIncrement(&row[0]);
    if (PvOpen(t, PV_JOB, PV_JOB)) InterlockedIncrement(&row[1]);
    if (!RgIsSim(tid)) InterlockedIncrement(&row[2]);
}

static int PvThreadClass(DWORD tid) { return tid == g_mainTid ? 0 : RgIsSim(tid) ? 1 : 2; }
static int PvSwClass(DWORD tid) { return RgIsSim(tid) ? 0 : RgIsWorker(tid) ? 1 : 2; }  // v45 fields: sim, route worker, other

// R blocked-bit writers (docs/112 §3.2): the lot (argument 1) and its id (lot +0x48/+0x4C, as both writers pass it on)
static void PvLogWriter(const char* name, const DWORD* slot, DWORD tid) {
    static volatile LONG logged;
    if (InterlockedIncrement(&logged) > 1024) return;  // ~256 per 10 min in the BP run; the cap only bounds the log
    const u8* lot = (const u8*)(uintptr_t)slot[1];
    DWORD lo = 0, hi = 0;
    if (lot) { PvRead(lot + 0x48, &lo); PvRead(lot + 0x4C, &hi); }
    Log("values R writer %s: lot %p id 0x%08lX%08lX (thread %lu, sim/worker/other %d)", name, lot, hi, lo, tid, PvSwClass(tid));
}

static const DWORD kPvAStarBits[10] = {0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x40, 0x80, 0x100, 0x200};
static const DWORD kPvLotBits[11] = {0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x100, 0x80000, 0x100000, 0x200000, 0x2000000};

// slot = the return-address slot at the hooked instruction ([esp] at a function entry or at the call); pushad below it:
// slot[-1] eax, slot[-2] ecx, slot[-3] edx, slot[-4] ebx
static void __cdecl PvEnter(int id, DWORD* slot) {
    if (g_snPv[id]) SnEnter(g_snPv[id] - 1, slot);  // [RouteSnap] shares this stub (one owner, docs/68 rule 3); first, before a frame swaps *slot
    if (t_snapRun) {  // P4 clone run (docs/94 §12.4.3): pass through; 0x64F4D3 replays behind this stub (p4.cpp P4DoorStub)
        if (id == PV_LAZY0) P4Lazy();  // D4: LotTable_Insert in a clone run: P4Lazy stops the run before it (0.30.0, H20c)
        return;
    }
    Ts3nCounters& k = ts3native_counters;
    PvThread& t = t_pv;
    while (t.n && t.f[t.n - 1].slot <= slot) t.n--;  // frames whose return slot is gone (unwound)
    const DWORD tid = GetCurrentThreadId();
    switch (id) {
        case PV_ASTAR: {  // opts = arg 6 ([esp+0x58] after sub esp,0x3C; push ebx at 0x676264)
            const int fam = PvFam(t);
            InterlockedIncrement(&k.pvAStar[fam]);
            const u8* o = (const u8*)(uintptr_t)slot[6];
            DWORD fl = 0, r14 = 0, r44 = 0;
            if (!PvRead(o, &fl) || !PvRead(o + 0x14, &r14) || !PvRead(o + 0x44, &r44)) break;
            for (int b = 0; b < 10; b++) if (fl & kPvAStarBits[b]) InterlockedIncrement(&k.pvAStarBit[fam][b]);
            if (r14) InterlockedIncrement(&k.pvAStarRef14[fam]);
            if (r44) InterlockedIncrement(&k.pvAStarRef44[fam]);
            static volatile LONG logged;  // F-11: the first few contexts with both refs and hubs
            if (InterlockedIncrement(&logged) <= 8) {
                DWORD sHub = 0, gHub = 0;
                PvRead(o + 0x04, &sHub);
                PvRead(o + 0x34, &gHub);
                Log("values A* opts: flags 0x%lX start hub 0x%lX +0x14 0x%lX goal hub 0x%lX +0x44 0x%lX (family %d, thread %lu)", fl, sHub, r14,
                    gHub, r44, fam, tid);
            }
            break;
        }
        case PV_LOTPLAN: {  // caller flags = arg 9 ([esp+0x1F4] at 0x63F7E0), obstacle mode = arg 12 ([esp+0x200])
            const int fam = PvFam(t);
            InterlockedIncrement(&k.pvLotPlan[fam]);
            const DWORD fl = slot[9];
            for (int b = 0; b < 11; b++) if (fl & kPvLotBits[b]) InterlockedIncrement(&k.pvLotPlanBit[fam][b]);
            if (slot[12]) InterlockedIncrement(&k.pvLotPlanObst[fam]);
            break;
        }
        case PV_IPNUM: InterlockedIncrement(&k.pvIpCalls); PvPush(t, id, slot, 0, 0, false); break;
        case PV_PART_A: case PV_PART_B:
            InterlockedIncrement(&k.pvPart[id - PV_PART_A]);
            if (!RgIsSim(tid)) InterlockedIncrement(&k.pvPartOffSim[id - PV_PART_A]);
            break;
        case PV_DIRTY: InterlockedIncrement(&k.pvDirtyPosts); break;
        case PV_BLK_A: case PV_BLK_B: InterlockedIncrement(&k.pvBlk[id - PV_BLK_A]); break;
        case PV_HIGHCOST: InterlockedIncrement(&k.pvHighCost); break;
        case PV_LAZY4: {  // thiscall(Q): only a flush with queued work (byte Q+0x4C, 0x666A4D)
            DWORD w = 0;
            if (PvRead((const u8*)(uintptr_t)slot[-2] + 0x4C, &w) && (w & 0xFF)) PvLazy(k.pvLazy[4], tid, t);
            break;
        }
        case PV_LAZY2: {  // thiscall, args lot graph G, room (u16): builds when G+0x8C is 0 (0x63D300) or the room record
            PvLazy(k.pvLazy[2], tid, t);  // (0x44 bytes at G+0x90) has byte 0 (0x63D359); else it returns the built byte
            const u8* g = (const u8*)(uintptr_t)slot[1];
            DWORD built = 0, b = 0, e = 0, rec = 1;
            if (!PvRead(g + 0x8C, &built)) break;
            bool build = !(built & 0xFF);
            const DWORD room = slot[2] & 0xFFFF;
            if (!build && PvRead(g + 0x90, &b) && PvRead(g + 0x94, &e) && e > b && room < (e - b) / 0x44 &&
                PvRead((const u8*)(uintptr_t)(b + room * 0x44), &rec))
                build = !(rec & 0xFF);
            if (build) InterlockedIncrement(&k.pvRoomBuild[PvSwClass(tid)]);
            break;
        }
        case PV_LAZY5: {  // thiscall(fp), arg level: rebuilds when byte [fp+0x38] + level*0x24 + 0x20 is 0 (0x80E5BF)
            PvLazy(k.pvLazy[5], tid, t);
            DWORD lv = 0, valid = 1;
            if (PvRead((const u8*)(uintptr_t)slot[-2] + 0x38, &lv) && PvRead((const u8*)(uintptr_t)(lv + slot[1] * 0x24 + 0x20), &valid) &&
                !(valid & 0xFF))
                InterlockedIncrement(&k.pvLevelBuild[PvSwClass(tid)]);
            break;
        }
        case PV_LAZY3: PvLazy(k.pvLazy[3], tid, t); PvLogWriter("ApplyLotUpdate 0x82B590", slot, tid); break;
        case PV_RESTORE: PvLazy(k.pvRestore, tid, t); PvLogWriter("RestoreLotBlockedBits 0x82A190", slot, tid); break;
        case PV_LAZY0: case PV_LAZY1: case PV_LAZY6: case PV_LAZY7:
            PvLazy(k.pvLazy[id - PV_LAZY0], tid, t);
            break;
        case PV_GB_MSG: case PV_GB_REB:  // thiscall(G); timed, logged at the leave
            InterlockedIncrement(&k.pvGBuild[id - PV_GB_MSG]);
            InterlockedIncrement(&k.pvGBuildThr[PvThreadClass(tid)]);
            PvPush(t, id, slot, 0, (LONG)slot[-2], true);
            break;
        case PV_GMSG0: case PV_GMSG1: case PV_GMSG2:
            InterlockedIncrement(&k.pvGMsg[id - PV_GMSG0]);
            Log("values G message path 0x%08lX (thread %lu, class %d)", kPv[id].at, tid, PvThreadClass(tid));
            break;
        case PV_REB0: case PV_REB1: case PV_REB2:
            InterlockedIncrement(&k.pvRebuild[id - PV_REB0]);
            Log("values world routing rebuild from 0x%08lX (thread %lu, class %d)", kPv[id].at, tid, PvThreadClass(tid));
            break;
        case PV_JOB: InterlockedIncrement(&k.pvJobs); PvPush(t, id, slot, 0, 0, false); break;  // arg: H9 rewrites inside
        case PV_TIE: InterlockedIncrement(&k.pvTies); break;
        case PV_IPALLOC: InterlockedIncrement(&k.pvIpAlloc); break;
        case PV_H9REM: case PV_H9INS: {
            InterlockedIncrement(&k.pvH9[id - PV_H9REM]);
            PvFrame* j = PvOpen(t, PV_JOB, PV_JOB);
            if (j) { InterlockedIncrement(&k.pvH9InPlan); j->arg++; }
            break;
        }
        // arg 1 = the 18-dword result (mType at +0), copied there at every exit; arg 2 is a caller-local vector (docs/112 §2)
        case PV_PC_PLAN: case PV_PC_POINT: PvPush(t, id, slot, 0, (LONG)slot[1], false); break;
        case PV_PATH_GOAL: case PV_PATH_LOT: case PV_PATH_ASTAR: PvPush(t, id, slot, (u8)(id - PV_PATH_GOAL + 1), 0, false); break;
        case PV_LS_LOCKED: case PV_LS_DISC: {  // inside Portal_IsLocked / IsDiscouraged: slot[3] = the wrapper's return address
            const PvFrame* p = PvOpen(t, PV_PATH_GOAL, PV_PATH_ASTAR);
            const u8 path = slot[3] == PV_INROOM_RET ? 4 : p ? p->tag : 0;
            PvPush(t, id, slot, path, 0, false);
            break;
        }
        case PV_QUERY:  // arg 9 = initialLocked (sete at 0x64F4B7: the native state was 1)
            if (PvOpen(t, PV_JOB, PV_JOB)) InterlockedIncrement(&k.pvQueryInPlan);
            PvPush(t, id, slot, (u8)(slot[9] & 1), 0, false);
            break;
        case PV_CANSEE: InterlockedIncrement(&k.pvCanSee); PvPush(t, id, slot, 0, 0, false); break;
        case PV_RAISE0: case PV_RAISE1:  // pvCanSeeRaise: every caller of World_CanSeeObject (script IC too, docs/112 §5.1)
            InterlockedIncrement(&k.pvCanSeeRaise);
            if (PvOpen(t, PV_CANSEE, PV_CANSEE)) InterlockedIncrement(&k.pvRaiseIn[PvSwClass(tid)]);
            break;
        case PV_EV68:
            InterlockedIncrement(&k.pvEv68);
            if (PvOpen(t, PV_CANSEE, PV_CANSEE)) InterlockedIncrement(&k.pvEv68InCanSee);
            break;
        default: break;
    }
}

static inline int PvBucket(DWORD v, int n) { return v < (DWORD)(n - 1) ? (int)v : n - 1; }

// at = the thunk's placeholder, just below esp after the function's `ret N` (so frame slot + N when it popped N bytes of
// arguments); at[-1] = eax as the function left it. The leaving frame is the one with the highest slot at or below `at`
// (frames are stored outermost first, slots descending); frames after it never returned (unwound past) and are dropped.
static DWORD __cdecl PvLeave(DWORD* at) {
    Ts3nCounters& k = ts3native_counters;
    PvThread& t = t_pv;
    int i = 0;
    while (i < t.n - 1 && t.f[i].slot > at) i++;  // only frames we swapped return here, so one is at or below `at`
    const PvFrame f = t.f[i];
    t.n = i;
    const DWORD eax = at[-1];
    switch (f.id) {
        case PV_IPNUM: if (eax) InterlockedIncrement(&k.pvIpNonZero); break;
        case PV_GB_MSG: case PV_GB_REB: {
            LARGE_INTEGER q;
            QueryPerformanceCounter(&q);
            const LONG us = (LONG)((q.QuadPart - f.t0) * 1000000 / g_qpf.QuadPart);
            InterlockedExchangeAdd(&k.pvGBuildUs, us);
            Max(&k.pvGBuildMaxUs, us);
            InterlockedIncrement(&g_pvGDone);
            const u8* g = (const u8*)(uintptr_t)f.arg;
            DWORD built = 0, cells = 0, water = 0, bodies = 0;
            PvRead(g, &built);
            PvRead(g + 0x0C, &cells);
            PvRead(g + 0x10, &water);
            PvRead(g + 0x14, &bodies);
            Log("values G build via 0x%08lX: %ld us, thread %lu (class %d), G %p built %lu cells %lu water %lu bodies %lu", kPv[f.id].at, us,
                GetCurrentThreadId(), PvThreadClass(GetCurrentThreadId()), g, built & 0xFF, cells, water, bodies);
            break;
        }
        case PV_JOB:
            if (f.arg) { InterlockedIncrement(&k.pvH9Plans); Max(&k.pvH9Max, f.arg); }
            break;
        case PV_PC_PLAN: case PV_PC_POINT: {
            DWORD type = 0;
            if (PvRead((const void*)(uintptr_t)f.arg, &type)) InterlockedIncrement(&k.pvStatus[f.id - PV_PC_PLAN][PvBucket(type, 20)]);
            break;
        }
        case PV_LS_LOCKED: case PV_LS_DISC: InterlockedIncrement(&k.pvPortal[f.tag][PvBucket(eax, 5)]); break;
        case PV_QUERY: InterlockedIncrement(&k.pvQuery[f.tag][PvBucket(eax, 5)]); break;
        default: break;
    }
    return f.ret;
}

static __declspec(naked) void __cdecl PvLeaveThunk() {
    __asm {
        push eax  // becomes the return address
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        call PvLeave
        add esp, 4
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}

#define PV_STUB(i) static __declspec(naked) void PvStub##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call PvEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_pvJmp + i * 4] }
PV_STUB(0) PV_STUB(1) PV_STUB(2) PV_STUB(3) PV_STUB(4) PV_STUB(5) PV_STUB(6) PV_STUB(7) PV_STUB(8) PV_STUB(9) PV_STUB(10)
PV_STUB(11) PV_STUB(12) PV_STUB(13) PV_STUB(14) PV_STUB(15) PV_STUB(16) PV_STUB(17) PV_STUB(18) PV_STUB(19) PV_STUB(20)
PV_STUB(21) PV_STUB(22) PV_STUB(23) PV_STUB(24) PV_STUB(25) PV_STUB(26) PV_STUB(27) PV_STUB(28) PV_STUB(29) PV_STUB(30)
PV_STUB(31) PV_STUB(32) PV_STUB(33) PV_STUB(34) PV_STUB(35) PV_STUB(36) PV_STUB(37) PV_STUB(38) PV_STUB(39) PV_STUB(40)
PV_STUB(41) PV_STUB(42)
static void (*const kPvStubs[PV_N])() = {
    PvStub0, PvStub1, PvStub2, PvStub3, PvStub4, PvStub5, PvStub6, PvStub7, PvStub8, PvStub9, PvStub10, PvStub11, PvStub12,
    PvStub13, PvStub14, PvStub15, PvStub16, PvStub17, PvStub18, PvStub19, PvStub20, PvStub21, PvStub22, PvStub23, PvStub24,
    PvStub25, PvStub26, PvStub27, PvStub28, PvStub29, PvStub30, PvStub31, PvStub32, PvStub33, PvStub34, PvStub35, PvStub36,
    PvStub37, PvStub38, PvStub39, PvStub40, PvStub41, PvStub42};

// one site at `at` calling `target` (0 = an entry): the game's, or a selftest dummy of the same shape. 1 hooked, 2 counted
// inside an owner stub, 0 skipped
static int PvInstallAt(int id, DWORD at, DWORD target) {
    const PvSite& s = kPv[id];
    u8 want[8];
    PvExpect(s, at, target, want);
    const u8* p = (const u8*)(uintptr_t)at;
    if (!Matches(p, want, s.len)) {
        for (int r = 0; r < RG_SITES; r++)
            if (!target && g_rgTramp[r] && kRgSites[r].addr == at) {
                g_pvRg[r] = id + 1;
                Log("values %s: counted inside the route gate stub at 0x%08lX", s.name, at);
                return 2;
            }
        char hex[64];
        if (Readable(p, s.len)) Hex(p, s.len, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
        Log("SKIP values %s: bytes at 0x%08lX differ (found %s)", s.name, at, hex);
        return 0;
    }
    if (!target) {
        char name[64];
        sprintf_s(name, "%s (values)", s.name);
        return (g_pvJmp[id] = InstallDetour(name, at, s.pro, s.len, (void*)kPvStubs[id], &g_pvJmp[id])) != NULL;
    }
    g_pvJmp[id] = (void*)(uintptr_t)target;
    const LONG rel = (LONG)((DWORD)(uintptr_t)kPvStubs[id] - (at + 5));
    return Write((void*)(p + 1), &rel, 4) ? 1 : 0;
}

// --- poll thread: load-time and drifting values ---
static const DWORD* g_pvWorldSlot = (const DWORD*)(uintptr_t)0x011D9128;  // routing world (RoutingWorld_Get 0x836200)
static const DWORD* g_pvWmSlot = (const DWORD*)(uintptr_t)0x011ECBC4;     // world manager
static const DWORD kPvGuards[3] = {0x011CF010, 0x011CF02C, 0x011CF030};    // docs/101 §2.2 #12, #14
static const DWORD PV_NO_VALUE = 0x7FC00000;                              // quiet NaN bits: object missing

static inline void PvFnv(DWORD& h, const u8* p, DWORD n) { for (DWORD i = 0; i < n; i++) { h ^= p[i]; h *= 16777619u; } }

// R (docs/99 §1.2, §1.4): per chunk the u16 count, per record n, flags & keep, bx, bz, the n refs and (flag 0x40) n edge
// bytes; the surface byte (+2) and record offsets are left out, so a version-1 file and its converted memory form agree.
// keep 0x7F masks only the blocked bit; 0x60 also masks the type bits (shoreline rewrites, docs/100 §3.2 step 8).
// out: [0] sum (keep 0x7F), [1] sum (keep 0x60), [2] waypoints, [3] blocked, [4] hash of (chunk, record) of every blocked
// waypoint (sees flips that keep the count, docs/112 §3.1). False when R is empty or out of bounds.
static bool PvRSum(const u8* r, DWORD* out) {
    __try {
        const DWORD w = *(const DWORD*)r, h = *(const DWORD*)(r + 4);
        const DWORD* tbl = *(const DWORD* const*)(r + 8);
        const u8* raw = *(const u8* const*)(r + 0x0C);
        const DWORD size = *(const DWORD*)(r + 0x10);
        if (!tbl || !raw || !w || !h || w > 256 || h > 256) return false;
        DWORD a = 2166136261u, b = 2166136261u, blk = 2166136261u, wps = 0, blocked = 0;
        for (DWORD c = 0; c < w * h; c++) {
            const DWORD off = tbl[c];
            if (off > size || size - off < 2) return false;
            const u8* ch = raw + off;
            const DWORD cnt = *(const unsigned short*)ch;
            if (size - off < 2 + 2 * cnt) return false;
            PvFnv(a, ch, 2);
            PvFnv(b, ch, 2);
            for (DWORD i = 0; i < cnt; i++) {
                const DWORD ro = ((const unsigned short*)(ch + 2))[i];
                if (size - off < ro + 5) return false;
                const u8* p = ch + ro;
                const DWORD n = p[0], len = 2 * n + ((p[1] & 0x40) ? n : 0);
                if (size - off - ro - 5 < len) return false;
                const u8 ha[4] = {p[0], (u8)(p[1] & 0x7F), p[3], p[4]}, hb[4] = {p[0], (u8)(p[1] & 0x60), p[3], p[4]};
                PvFnv(a, ha, 4);
                PvFnv(a, p + 5, len);
                PvFnv(b, hb, 4);
                PvFnv(b, p + 5, len);
                wps++;
                if (p[1] & 0x80) {
                    blocked++;
                    const DWORD key[2] = {c, i};
                    PvFnv(blk, (const u8*)key, 8);
                }
            }
        }
        out[0] = a;
        out[1] = b;
        out[2] = wps;
        out[3] = blocked;
        out[4] = blk;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static DWORD PvF(const void* p) { DWORD v; return PvRead(p, &v) ? v : PV_NO_VALUE; }

// drifting values: sea height, WM+0x1B4, world+0x983, G header (logged when they change; first = right after a snapshot)
static void PvPollDrift(const u8* world, const u8* wm, bool first) {
    Ts3nCounters& k = ts3native_counters;
    DWORD sea = PV_NO_VALUE, obj = 0, dirty = 0, b983 = 0, g[5] = {0, 0, 0, 0, 0};
    if (wm && PvRead(wm + 0x60, &obj) && obj) sea = PvF((const u8*)(uintptr_t)obj + 0x14);
    if (wm) PvRead(wm + 0x1B4, &dirty);
    PvRead(world + 0x983, &b983);
    const u8* G = world + 0x8C;
    static const DWORD kGOff[5] = {0x00, 0x0C, 0x10, 0x14, 0x368};  // built, cells, water cells, bodies, blockedNodeMap size
    for (int i = 0; i < 5; i++) PvRead(G + kGOff[i], &g[i]);
    g[0] &= 0xFF;
    if (!first && (DWORD)k.pvSeaY != sea) {
        k.pvSeaChanges++;
        Log("values sea height 0x%08lX -> 0x%08lX", (DWORD)k.pvSeaY, sea);
    }
    k.pvSeaY = (LONG)sea;
    if (first || (DWORD)k.pvWmDirty != dirty || (DWORD)k.pvWorld983 != (b983 & 0xFF)) Log("values WM+0x1B4 %lu, world+0x983 %lu", dirty, b983 & 0xFF);
    k.pvWmDirty = (LONG)dirty;
    k.pvWorld983 = (LONG)(b983 & 0xFF);
    if (first || (DWORD)k.pvG[0] != g[0] || (DWORD)k.pvG[1] != g[1] || (DWORD)k.pvG[2] != g[2] || (DWORD)k.pvG[3] != g[3])
        Log("values G built %lu cells %lu water %lu bodies %lu blocked %lu", g[0], g[1], g[2], g[3], g[4]);
    for (int i = 0; i < 4; i++) k.pvG[i] = (LONG)g[i];
    if (first || (LONG)g[4] > k.pvG[4]) k.pvG[4] = (LONG)g[4];
}

// once per world: planner tuning, origin and bounds, guards, R checksum
static void PvSnapshot(const u8* world, const u8* wm, const u8* W) {
    Ts3nCounters& k = ts3native_counters;
    static const u8 kTuneOff[13] = {0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3C, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0x60};
    for (int i = 0; i < 13; i++) k.pvTune[i] = (LONG)PvF(W + 0x18 + kTuneOff[i]);
    for (int i = 0; i < 4; i++) k.pvWm[i] = wm ? (LONG)PvF(wm + 0xB4 + 4 * i) : (LONG)PV_NO_VALUE;
    for (int i = 0; i < 3; i++) k.pvGuard[i] = (LONG)PvF((const void*)(uintptr_t)kPvGuards[i]);
    DWORD r[5] = {0, 0, 0, 0, 0};
    PvRSum(world + 0x54, r);
    for (int i = 0; i < 4; i++) k.pvR[i] = (LONG)r[i];
    k.pvRBlk[0] = k.pvRBlk[1] = (LONG)r[4];
    k.pvWorlds++;
    Log("values world %p W %p: tune 0x%08lX | 0x%08lX 0x%08lX 0x%08lX 0x%08lX 0x%08lX 0x%08lX | 0x%08lX 0x%08lX 0x%08lX 0x%08lX 0x%08lX 0x%08lX",
        world, W, k.pvTune[0], k.pvTune[1], k.pvTune[2], k.pvTune[3], k.pvTune[4], k.pvTune[5], k.pvTune[6], k.pvTune[7],
        k.pvTune[8], k.pvTune[9], k.pvTune[10], k.pvTune[11], k.pvTune[12]);
    Log("values world %p: origin 0x%08lX 0x%08lX max 0x%08lX 0x%08lX, guards 0x%lX 0x%lX 0x%lX, R sum 0x%08lX / 0x%08lX, %lu waypoints, %lu blocked",
        world, k.pvWm[0], k.pvWm[1], k.pvWm[2], k.pvWm[3], k.pvGuard[0], k.pvGuard[1], k.pvGuard[2], r[0], r[1], r[2], r[3]);
}

static DWORD WINAPI PvPoll(void*) {
    Ts3nCounters& k = ts3native_counters;
    DWORD last = 0, lastRaw = 0, seen = 0, rTick = 0, rPrev = 0, blkPrev = 0;
    LONG gSnap = 0;  // g_pvGDone at this world's snapshot; -1 once R was re-baselined after a later G build
    for (;;) {
        Sleep(g_pvPollMs);
        DWORD world = 0, wm = 0, W = 0, raw = 0;
        if (!PvRead(g_pvWorldSlot, &world) || !world) { last = 0; continue; }
        PvRead(g_pvWmSlot, &wm);
        PvRead((const u8*)(uintptr_t)world + 0x960, &W);
        PvRead((const u8*)(uintptr_t)world + 0x54 + 0x0C, &raw);
        const u8* wp = (const u8*)(uintptr_t)world;
        if (world != last || raw != lastRaw) { last = world; lastRaw = raw; seen = 0; }
        if (!W || !raw) continue;
        if (++seen < 2) continue;
        if (seen == 2) {
            PvSnapshot(wp, (const u8*)(uintptr_t)wm, (const u8*)(uintptr_t)W);
            rTick = GetTickCount();
            rPrev = (DWORD)k.pvR[0];
            blkPrev = (DWORD)k.pvRBlk[0];
            gSnap = g_pvGDone;
        }
        PvPollDrift(wp, (const u8*)(uintptr_t)wm, seen == 2);
        DWORD r[5];
        // the load's G build rewrites R's type bits (docs/112 §3.1); when it finished after the snapshot, the R baseline for
        // the change checks is retaken once (pvR[0..1] keep the snapshot for the docs/99 F-2 compare with the world file)
        if (gSnap >= 0 && g_pvGDone != gSnap && PvRSum(wp + 0x54, r)) {
            gSnap = -1;
            rPrev = r[0];
            blkPrev = r[4];
            k.pvRBlk[0] = k.pvRBlk[1] = (LONG)r[4];
            k.pvR[3] = (LONG)r[3];
            k.pvRRebase++;
            rTick = GetTickCount();
            Log("values R baseline after the G build: sum 0x%08lX / 0x%08lX, %lu waypoints, %lu blocked, blocked hash 0x%08lX", r[0], r[1], r[2], r[3], r[4]);
        }
        if (GetTickCount() - rTick >= 60000) {  // R stays fixed (docs/91 §6.6): only bit 7 may change
            rTick = GetTickCount();
            if (PvRSum(wp + 0x54, r)) {
                if (r[0] != rPrev) {
                    k.pvR[4]++;
                    Log("values R changed: sum 0x%08lX / 0x%08lX, %lu waypoints, %lu blocked (snapshot 0x%08lX)", r[0], r[1], r[2], r[3], (DWORD)k.pvR[0]);
                    rPrev = r[0];
                }
                if (r[4] != blkPrev) {
                    k.pvRBlk[2]++;
                    Log("values R blocked bits changed: hash 0x%08lX -> 0x%08lX, %lu blocked", blkPrev, r[4], r[3]);
                    blkPrev = r[4];
                }
                k.pvR[3] = (LONG)r[3];  // blocked waypoints now (bit 7 is the one field that may change)
                k.pvRBlk[1] = (LONG)r[4];
            }
        }
    }
}

// [ProbeValues] Enabled=1: every site checked and hooked now (DllMain), the poll thread reads the rest
static bool ApplyProbeValues(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"ProbeValues", L"Enabled", 0, ini)) { Log("probe values off (Enabled=0)"); return false; }
    Ts3nCounters& k = ts3native_counters;
    g_pvPollMs = (DWORD)Clamp(GetPrivateProfileIntW(L"ProbeValues", L"PollSec", 2, ini), 1, 60) * 1000;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    for (int i = 0; i < PV_N; i++) {
        const int r = PvInstallAt(i, kPv[i].at, kPv[i].target);
        if (r) k.pvHooks++; else k.pvSkipped++;
    }
    g_pvOn = 1;
    k.pvReady = k.pvHooks ? 1 : 0;
    HANDLE th = CreateThread(NULL, 64 * 1024, PvPoll, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (th) { CloseHandle(th); k.pvReady |= 2; } else Log("FAIL probe values: poll thread (err %lu)", GetLastError());
    Log("%s probe values: %ld of %d sites hooked (%ld skipped), poll every %lu s", k.pvSkipped ? "PART" : "OK  ", k.pvHooks, PV_N,
        k.pvSkipped, g_pvPollMs / 1000);
    return true;
}
