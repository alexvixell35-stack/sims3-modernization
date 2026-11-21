// [FootstepRO] (issue #156, 2026-11-03): footstep sounds answered from a small surface cache while an async route job holds
// +0x918, so the pose pass stops waiting for the job inside the terrain-footprint lot lookup. Included by ts3native.cpp after
// gvro.cpp (uses g_async, PatchCalls, UsSince, Max, Clamp).
//
// FootstepAudio_Surface 0x8693E0 (run from the clip events of the pose pass 0x571B90) calls World_SurfaceTypeAtPoint 0xAB9580
// at 0x869713 (E8; thiscall world, (point, flags 0x27), ret 8, the surface type in eax; the point is {float, float, s8 level at
// +8}; no write through it) [verified disassembly 2026-11-03]. Some calls go on to RoutingWorld_SurfaceAtPoint 0x834B80 ->
// EnsureTerrainFootprints 0x8341D0 -> LotTable_GetIfPresent 0x62FB60 (gate 12, wait key 0x62FB74 <- 0x834201): with the
// eleven read-only pieces on, that path took 14.2 ms/s of sim waits (old save, P1b; 0.9 with them off); 7,166 of 7,232
// SurfaceAtPoint calls ran inside the pose pass, all from footsteps (Frida drill, 840 s) [measured].
//
// The stub keeps, on the sim (owner) thread only, the game's last answer per (0.5 m cell of the two point floats, level,
// flags) in a 4096-slot direct-mapped table. While a job is A_RUNNING a fresh entry (younger than MaxAgeMs) answers without
// the game's call. A miss: MissNear=1 first takes a fresh entry of one of the 8 neighbouring cells (a walking Sim's previous
// step is usually next door); then MissLast=1 answers the last surface the game gave on this thread; else the game is called
// (today's wait). With no job every call goes to the game, refreshes the entry, and checks what each answer would have
// been against the game's (fsChk*, fsNearChk*, fsLastChk*). Only the footstep sound reads the answer (0x869130 maps it to the
// sound name); a wrong answer picks another surface's sound for one step. Other threads: the game's call.
// Counters v901 (+0x5B30, after upstream's v163 block): fsMode (+0x5B34, --ab footstepro).
#include <math.h>

static const DWORD FS_SITE = TS3A(0x00869713), FS_TARGET = TS3A(0x00AB9580);
typedef DWORD(__fastcall* FsFn)(u8* world, void* edx, const u8* pt, DWORD flags);  // ret 8
static FsFn g_fsOrig = (FsFn)(uintptr_t)FS_TARGET;
struct FsEnt { LONG kx, kz, lv; DWORD flags, val, tick; };
static FsEnt g_fsc[4096];  // owner thread only
static DWORD g_fsMaxAgeMs = 60000, g_fsLastVal;
static bool g_fsMissLast, g_fsMissNear, g_fsHaveLast;

static FsEnt* FsSlot(LONG kx, LONG kz, LONG lv, DWORD fl) {
    const DWORD h = (DWORD)kx * 73856093u ^ (DWORD)kz * 19349663u ^ (DWORD)lv * 83492791u ^ fl * 2654435761u;
    return &g_fsc[(h ^ (h >> 13)) & 4095];
}
// signed age: an entry stored in the same GetTickCount step has tick = now | 1, one past an even now (unsigned, that read as
// ~49 days old: stale for up to one 16 ms step; the selftest caught it 2026-11-03)
static bool FsYoung(const FsEnt* e, DWORD now) { return (LONG)(now - e->tick) <= (LONG)g_fsMaxAgeMs; }
static bool FsFresh(const FsEnt* e, LONG kx, LONG kz, LONG lv, DWORD fl, DWORD now) {
    return e->tick && e->kx == kx && e->kz == kz && e->lv == lv && e->flags == fl && FsYoung(e, now);
}
// a fresh entry of one of the 8 cells around (kx, kz), same level and flags; the cell itself is not looked at
static bool FsNear(LONG kx, LONG kz, LONG lv, DWORD fl, DWORD now, DWORD* val) {
    for (LONG dz = -1; dz <= 1; dz++)
        for (LONG dx = -1; dx <= 1; dx++) {
            if (!dx && !dz) continue;
            const FsEnt* n = FsSlot(kx + dx, kz + dz, lv, fl);
            if (FsFresh(n, kx + dx, kz + dz, lv, fl, now)) { *val = n->val; return true; }
        }
    return false;
}
static DWORD FsStore(FsEnt* e, LONG kx, LONG kz, LONG lv, DWORD fl, DWORD v, DWORD now) {
    e->kx = kx, e->kz = kz, e->lv = lv, e->flags = fl, e->val = v, e->tick = now | 1;  // tick 0 = empty slot
    g_fsLastVal = v, g_fsHaveLast = true;
    InterlockedIncrement(&ts3native_counters.fsStored);
    return v;
}
static DWORD __fastcall FsStub(u8* world, void* edx, const u8* pt, DWORD flags) {
    Ts3nCounters& k = ts3native_counters;
    if (!k.fsMode || !pt || GetCurrentThreadId() != g_async.owner) return g_fsOrig(world, edx, pt, flags);
    InterlockedIncrement(&k.fsCalls);
    const float x = *(const float*)pt, z = *(const float*)(pt + 4);
    if (!(fabsf(x) < 1e6f) || !(fabsf(z) < 1e6f)) return g_fsOrig(world, edx, pt, flags);  // NaN or far out: no key
    const LONG kx = (LONG)floorf(x * 2.0f), kz = (LONG)floorf(z * 2.0f), lv = (LONG)(signed char)pt[8];
    FsEnt* e = FsSlot(kx, kz, lv, flags);
    const DWORD now = GetTickCount();
    const bool same = e->tick && e->kx == kx && e->kz == kz && e->lv == lv && e->flags == flags;
    DWORD nv;
    if (g_async.state != A_RUNNING) {
        // no job: the game answers; how often a fresh entry (a hit), a neighbour (MissNear) or the last answer (MissLast) would
        // have differed
        const DWORD v = g_fsOrig(world, edx, pt, flags);
        if (same && FsYoung(e, now)) { InterlockedIncrement(&k.fsChk); if (e->val != v) InterlockedIncrement(&k.fsChkDiff); }
        if (FsNear(kx, kz, lv, flags, now, &nv)) { InterlockedIncrement(&k.fsNearChk); if (nv != v) InterlockedIncrement(&k.fsNearDiff); }
        if (g_fsHaveLast) { InterlockedIncrement(&k.fsLastChk); if (g_fsLastVal != v) InterlockedIncrement(&k.fsLastDiff); }
        return FsStore(e, kx, kz, lv, flags, v, now);
    }
    InterlockedIncrement(&k.fsJob);
    if (same && FsYoung(e, now)) { InterlockedIncrement(&k.fsHit); return e->val; }
    if (same) InterlockedIncrement(&k.fsStale);
    if (g_fsMissNear && FsNear(kx, kz, lv, flags, now, &nv)) { InterlockedIncrement(&k.fsNear); return nv; }
    if (g_fsMissLast && g_fsHaveLast) { InterlockedIncrement(&k.fsLast); return g_fsLastVal; }
    InterlockedIncrement(&k.fsMiss);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD v = g_fsOrig(world, edx, pt, flags);
    const LONG us = UsSince(t0);
    InterlockedExchangeAdd(&k.fsJobGameUs, us);
    Max(&k.fsJobGameMaxUs, us);
    return FsStore(e, kx, kz, lv, flags, v, now);
}

static const CallSite kFsSite = {FS_SITE, FS_TARGET, (void*)FsStub};

// [FootstepRO] Enabled=1: needs the async route path (the stub only reads g_async)
static void InstallFootstepRO(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"FootstepRO", L"Enabled", 0, ini)) { Log("footstep surface cache off (Enabled=0)"); return; }
    if (!k.asyncReady) { Log("SKIP footstep surface cache: needs the async route path ([RouteAsync])"); return; }
    if (!PatchCalls("footstep surface cache (0x8693E0 -> World_SurfaceTypeAtPoint)", &kFsSite, 1)) return;
    k.fsMode = Clamp(GetPrivateProfileIntW(L"FootstepRO", L"Mode", 1, ini), 0, 1);
    g_fsMissNear = GetPrivateProfileIntW(L"FootstepRO", L"MissNear", 0, ini) != 0;
    g_fsMissLast = GetPrivateProfileIntW(L"FootstepRO", L"MissLast", 0, ini) != 0;
    g_fsMaxAgeMs = (DWORD)Clamp(GetPrivateProfileIntW(L"FootstepRO", L"MaxAgeMs", 60000, ini), 100, 600000);
    k.fsReady = 1;
    Log("OK   footstep surface cache: 0x869713 (Mode=%ld MissNear=%d MissLast=%d MaxAgeMs=%lu)", k.fsMode, g_fsMissNear ? 1 : 0,
        g_fsMissLast ? 1 : 0, g_fsMaxAgeMs);
}
