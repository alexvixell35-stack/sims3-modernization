// Offline check for design S2 (docs/84 §3.6). Maps TS3W.exe at its own addresses in this process (all sections; the game's
// code runs read-only on fake data), with only these replaced: the GeneralAllocator entry points (CRT heap), Lock_Acquire /
// Lock_Release (no-ops), 0x85AE60 (copies the node point, level from its w) and World_LotLookup (rectangles). It builds random
// lots, lot tables, quadtrees and footprints with the game's own vtables, then:
//   A. compares the game's Locomotion_ObstacleCheck 0x85FD10 with the read-only clone SrRun (appended ids, in order) and the
//      game's RoutingWorld_ObstacleQuery 0x82EDB0 with the clone's point query (footprint sub-objects, in order);
//   A2. buffer limits (0.21.0): worlds past the 0.13.0 limits must compare equal, past the 0.21.0 ones must give up at the site;
//   B. installs the S2 detour on the mapped 0x85FD10 and checks the hook (job running: clone; trampoline otherwise), shadow mode
//      and every fallback reason;
//   C. installs the 12 worker writer stubs on the mapped W1-W6 prologues (the bytes must match) and runs the game's
//      Footprint_EnsureLevelPolygons 0x80E5B0 through its stub on a "worker" thread: QL is held exclusive inside, released
//      after; while it is held the hook falls back (QL busy) and still answers the same ids;
//   D. checks the lot transforms 0x62ED40 / 0x62EF30 against SrXform bit for bit;
//   E. design L1 (lotfd.cpp, docs/84 §4.6): the game's LotRouting_FrontDoorPortal 0x631800 vs LtFrontDoor on random lot
//      tables (EDX:EAX, *out, portal counts unchanged; AddRef == Release through the T-L3 thunks in vtable 0xFED048), then the
//      L1 detour on the mapped 0x631800 (job: clone, else the game's code), shadow mode, and QL busy -> the game's code;
//   F. tripwires T4 / T-S7 (0.15.0): the 11 stubs on the game's prologues; the game's 0x807110 (another thread) and 0x61ED10
//      (worker outside QL) run through them and block inside: the hook drops the clone, the game's call gives the same ids.
//   F2. arm / disarm races (0.18.0): disarmed site not counted; writer gone before the arm; writer entering after the arm
//      but before A_RUNNING; the job ending (and the next arming) inside the clone with a writer in the disarmed site.
//   G. fix D-X; H. design H2 (see the sections);
//   DG. design G standalone ([Discourage] DestroyPark): the game's DestroyById / FindById / release on a fake object service,
//      a job thread holding raw pointers across the destroys (park on: no freed read, no job-thread destructor; off: both);
//   J. [ObstructedRO] (0.22.1): the game's IC_Route_IsPointObstructedBySim 0x751480 vs OrRun (fakes for its phase-1 callees
//      0x74B2C0, 0xADD860, 0xB015B0, 0x74A8F0 and the route's vtable pair), then its detour: job, shadow, QL busy.
//   SNAP. route snapshot P3 (snap.cpp, docs/94 §4.4): K1, K2, K5 on the fake world of snap-fake.h, high-cost stamps and
//      removes through the game's WaterGrid_MarkB 0x670750.
//   K3 / K4 / K5 (docs/94 §13.3, gate G4): the game's InRoomPlanToPoint 0x632630 on live P vs the replay clone's P (sweeps,
//      a tie lot, writes between plans, uncut writes, a clone path outliving its lot copy).
//   K6 (docs/94 §13.3, G4): the game's world A* WorldAStar_Search 0x676260 on a fake R / G, live vs the working copy that
//      p4clone.cpp's PcR / PcHighCost / PcBnm build from a P3 reader cut (sweeps, a tie lattice, writes between cuts).
//   K7 (crash H): RoomGraph_FirstUse on clone lots' unbuilt rooms.
//   K8 (docs/94 §13.3): the planner above the world search (PathPlan_WithRetries 0x661B50 -> 0x65E0D0: lot exits, closest
//      waypoints, lot legs, water legs, node passes) on live W vs PcAcquire's W' over K6's world with the REPLAY world's lots,
//      the live data on no-access pages; the world manager, terrain, physics and partition queries faked (g_k8Sites).
// Built by build.cmd, not run there (needs the exe):  out\steerro-test.exe ["<path>\TS3W.exe"] [cases, default 400] [seed]
#define TS3NATIVE_NO_DLLMAIN
#include "ts3native.cpp"
#include <assert.h>
#include "snap-fake.h"
#include "replay-fake.h"

static DWORD g_seed = 12345;
static DWORD Rnd(DWORD n) { g_seed = g_seed * 1103515245 + 12345; return ((g_seed >> 8) & 0xFFFFFF) % n; }
static float Rf(float lo, float hi) { return lo + (hi - lo) * (float)Rnd(1 << 20) / (float)(1 << 20); }

static const DWORD IMG_BASE = 0x00400000, IMG_HI = 0x0125A000;  // SizeOfImage 0xE5A000 (Steam 1.67)
static const DWORD IMG_LO = IMG_BASE;

// Windows maps small regions (NLS, apiset) at the lowest free addresses during process start, i.e. at 0x400000 when the exe
// sits elsewhere. So the parent starts this program suspended, reserves the image range in the child first, then resumes it.
static int RunChild() {
    STARTUPINFOA si = {sizeof(si)};
    PROCESS_INFORMATION pi;
    SetEnvironmentVariableA("SRT_CHILD", "1");
    if (!CreateProcessA(NULL, GetCommandLineA(), NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, NULL, &si, &pi)) return 2;
    if (VirtualAllocEx(pi.hProcess, (void*)(uintptr_t)IMG_LO, IMG_HI - IMG_LO, MEM_RESERVE, PAGE_EXECUTE_READWRITE) != (void*)(uintptr_t)IMG_LO) {
        printf("cannot reserve 0x%08lX-0x%08lX in the child (err %lu)\n", IMG_LO, IMG_HI, GetLastError());
        TerminateProcess(pi.hProcess, 2);
        return 2;
    }
    ResumeThread(pi.hThread);
    DWORD rc = 2;
    WaitForSingleObject(pi.hProcess, INFINITE);
    GetExitCodeProcess(pi.hProcess, &rc);
    return (int)rc;
}

static bool Reserve() {
    u8* r = (u8*)VirtualAlloc((void*)(uintptr_t)IMG_LO, IMG_HI - IMG_LO, MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    if (r == (u8*)(uintptr_t)IMG_LO) return true;
    MEMORY_BASIC_INFORMATION m = {};
    VirtualQuery((void*)(uintptr_t)IMG_LO, &m, sizeof(m));
    printf("cannot map the image at 0x%08lX (err %lu): region at %p size 0x%lX\n", IMG_LO, GetLastError(), m.AllocationBase, (DWORD)m.RegionSize);
    return false;
}

static void __cdecl TrapReport(DWORD from) {
    printf("unexpected import call from 0x%08lX\n", from);
    fflush(stdout);
    ExitProcess(3);
}
static void __declspec(naked) ImportTrap() {
    __asm {
        push dword ptr [esp]
        call TrapReport
    }
}

static bool MapImage(const u8* img) {
    const DWORD pe = *(const DWORD*)(img + 0x3C), base = *(const DWORD*)(img + pe + 0x34);
    const WORD ns = *(const WORD*)(img + pe + 6), oh = *(const WORD*)(img + pe + 20);
    if (base != IMG_BASE || *(const DWORD*)(img + pe + 0x50) != IMG_HI - IMG_BASE) { printf("not the Steam 1.67 exe\n"); return false; }
    for (int i = 0; i < ns; i++) {
        const u8* s = img + pe + 24 + oh + i * 40;
        const DWORD rs = *(const DWORD*)(s + 16), vs = *(const DWORD*)(s + 8);
        DWORD va = base + *(const DWORD*)(s + 12), raw = *(const DWORD*)(s + 20), n = rs < vs ? rs : vs;
        if (va < IMG_LO) { const DWORD cut = IMG_LO - va < n ? IMG_LO - va : n; va += cut, raw += cut, n -= cut; }
        memcpy((void*)(uintptr_t)va, img + raw, n);
    }
    // imports: memmove/memcpy/memset/memcmp from the CRT, anything else stops the test with its caller
    const DWORD imp = base + *(const DWORD*)(img + pe + 0x80);
    for (const DWORD* d = (const DWORD*)(uintptr_t)imp; d[3]; d += 5) {
        DWORD* iat = (DWORD*)(uintptr_t)(base + d[4]);
        const DWORD* names = (const DWORD*)(uintptr_t)(base + (d[0] ? d[0] : d[4]));
        for (int j = 0; names[j]; j++) {
            const char* nm = (names[j] & 0x80000000) ? "" : (const char*)(uintptr_t)(base + names[j] + 2);
            void* f = (void*)ImportTrap;
            if (!strcmp(nm, "memmove")) f = (void*)memmove;
            else if (!strcmp(nm, "memcpy")) f = (void*)memcpy;
            else if (!strcmp(nm, "memset")) f = (void*)memset;
            else if (!strcmp(nm, "memcmp")) f = (void*)memcmp;
            else if (!strcmp(nm, "ceil")) f = (void*)(double(__cdecl*)(double))ceil;  // hash table growth
            else if (!strcmp(nm, "floor")) f = (void*)(double(__cdecl*)(double))floor;
            iat[j] = (DWORD)(uintptr_t)f;
        }
    }
    return true;
}

static void Jmp(DWORD at, const void* to) {
    u8* p = (u8*)(uintptr_t)at;
    p[0] = 0xE9;
    *(LONG*)(p + 1) = (LONG)((const u8*)to - (p + 5));
}

// --- fakes for the replaced game functions ---
static void* __fastcall FkMalloc(void*, void*, DWORD size, DWORD) { return _aligned_malloc(size ? size : 1, 16); }
static void* __fastcall FkMemalign(void*, void*, DWORD size, DWORD align, DWORD, DWORD) { return _aligned_malloc(size ? size : 1, align > 16 ? align : 16); }
static void __fastcall FkFree(void*, void*, void* p) { _aligned_free(p); }
static LONG __fastcall FkLock(void*, void*, const DWORD*) { return 1; }
static LONG __fastcall FkUnlock(void*, void*) { return 0; }
static int g_convCalls;
static void __cdecl FkConv(const float* src, u8* out) {
    g_convCalls++;
    float* o = (float*)out;
    o[0] = src[0], o[1] = src[1], o[2] = src[2], o[3] = 1.0f;
    o[4] = o[5] = o[6] = o[7] = 0;
    o[8] = 1.0f;
    *(LONG*)(out + 0x24) = (LONG)src[3];
    *(DWORD*)(out + 0x28) = *(DWORD*)(out + 0x2C) = *(DWORD*)(out + 0x30) = 0;
}
struct LotRect { u8* lot; float x0, z0, x1, z1; };
static LotRect g_rects[4];
static int g_nRects;
static void* g_lookNone;  // AV: this world manager finds no lot (the consumer's wm apart from the query's wmw)
static u8* __fastcall FkLotLookup(void* m, void*, float x, float z) {
    if (m && m == g_lookNone) return NULL;
    for (int i = 0; i < g_nRects; i++)
        if (x >= g_rects[i].x0 && x < g_rects[i].x1 && z >= g_rects[i].z0 && z < g_rects[i].z1) return g_rects[i].lot;
    return NULL;
}

static long g_twAfter;  // F: read-only answers once the tripwire writer had left
static void* A(size_t n) {
    void* p = _aligned_malloc(n, 16);
    if (!p) { printf("out of memory (worlds are never freed: fewer cases)\n"); exit(4); }
    memset(p, 0, n);
    return p;
}

// --- the fake world ---
static const DWORD kClasses[9] = {0xFEBBC0, 0xFEB848, 0xFEAE08, 0xFEBA98, 0xFEBE38, 0xFEB5F8, 0xFEB720, 0xFEBD10, 0xFEB970};
static DWORD g_subVt[2];  // sub-object vtables: [0] type via +0x35 (0x624ED0), [1] constant 0xA (0x620E50)
static const DWORD kSentinel = 0x05E117E1;

struct World {
    u8* world; u8* wm; u8* p; u8* act;
    u8* lots[3];       // [0], [1] in the table, [2] not
    u8* entries[2];
    u8* qs[2][2];      // per lot entry, two levels
    u8* fps[2400];  // A2 adds its big leaf's footprints after the random ones
    u8* kid[2400];  // 0.37: fps[i]'s child footprint or NULL (PickFp hands it out instead of fps[i] half the time)
    int nfp;
};
static World W;

static u8* NewFootprint(u8* cachePolys, int nPolys) {
    u8* f = (u8*)A(0x80);
    const DWORD vt = kClasses[Rnd(9)];
    *(DWORD*)f = vt;
    const bool constType = *(const DWORD*)(uintptr_t)(vt + 0xC) == 0x0070AE20;
    *(DWORD*)(f + 4) = g_subVt[constType ? 1 : 0];
    *(LONG*)(f + 8) = 100;  // references: never reach 0 (the deleting destructor is not ours to run)
    *(DWORD*)(f + 0x14) = 0x1000 + Rnd(40);  // ids collide on purpose (ids, ignore and exclude lists)
    *(DWORD*)(f + 0x18) = Rnd(3) ? 0 : 7;
    if (Rnd(4) == 0) *(DWORD*)(f + 0x14) = *(DWORD*)(f + 0x18) = 0;  // id 0: the obstacle query skips it (record id), EditPath not
    static const u8 kTypes[] = {0x14, 0x1F, 9, 0x11, 0xC, 0xD, 0x17, 0x13, 0x18, 0x1E, 2, 5, 0xA, 3, 3, 3, 4, 4};
    f[0x34] = (u8)(Rnd(5) == 0 ? 0xD : Rnd(2) ? 3 : Rnd(4));  // class byte (3 passes EditPath's class filter)
    f[0x35] = kTypes[Rnd(sizeof(kTypes))];
    *(DWORD*)(f + 0x40) = Rnd(4) == 0 ? 0 : Rnd(1000);  // 0x80F840 compares +0x40/+0x44 with the record's id (0)
    *(DWORD*)(f + 0x44) = 0;
    f[0x48] = (u8)Rnd(2);  // 0x620DD0
    u8* cache = (u8*)A(0x24 * 3);
    for (int l = 0; l < 3; l++) {  // levels 1 and 2 for the classes with 3 (slot 0 0x83F5F0, [AvoidRO]'s +0xB8 loop)
        *(u8**)(cache + l * 0x24) = cachePolys;
        *(u8**)(cache + l * 0x24 + 4) = cachePolys + nPolys * 16;
        cache[l * 0x24 + 0x20] = 1;
    }
    *(u8**)(f + 0x38) = cache;
    return f;
}

// 0.37: the child footprint class 0xFEBF90 ("Routing/ExtensionFootprint", built by 0x807400 on a parent): +0xC the parent,
// +8 references, +0x10 a float, +0x18/+0x1C the type mask (-1 as made, else set), +0x20 forward-class byte. 0x80 bytes
// although the game's are 0x28. Children draw from their own generator, so the worlds of every other section (and their
// statistical thresholds) come out of g_seed exactly as before 0.37; they reach the leaves only through PickFp.
static DWORD g_kidSeed = 99991;
static DWORD RndK(DWORD n) { g_kidSeed = g_kidSeed * 1103515245 + 12345; return ((g_kidSeed >> 8) & 0xFFFFFF) % n; }
static u8* NewChild(u8* parent) {
    u8* c = (u8*)A(0x80);
    *(DWORD*)c = 0x00FEBF90;
    *(DWORD*)(c + 4) = 0x00FEBF6C;
    *(LONG*)(c + 8) = 100;
    *(u8**)(c + 0xC) = parent;
    *(float*)(c + 0x10) = 1.0f;
    const DWORD m = RndK(3);
    *(DWORD*)(c + 0x18) = m == 0 ? 0xFFFFFFFF : m == 1 ? 0 : RndK(0xFFFFFF) * 251;
    *(DWORD*)(c + 0x1C) = m == 0 ? 0xFFFFFFFF : m == 1 ? 0 : RndK(0xFFFFFF) * 131;
    c[0x20] = (u8)(RndK(3) != 0);  // mostly forwarding (the parent's class): mask-classed children are 0 or 2 and add no ids
    return c;
}
static long g_kids;  // child footprints built (all worlds)
static u8* PickFp() {  // a leaf entry's footprint: g_seed draws as before 0.37, the child (if any) half the time
    const int i = (int)Rnd(W.nfp);
    return W.kid[i] && RndK(2) ? W.kid[i] : W.fps[i];
}
static void RefsOk() {  // the game's references are balanced (parents and children)
    for (int i = 0; i < W.nfp; i++) assert(*(LONG*)(W.fps[i] + 8) == 100 && (!W.kid[i] || *(LONG*)(W.kid[i] + 8) == 100));
}

static u8* Polys(int* n) {  // 1-2 quads somewhere in the 64 x 64 lot
    *n = 1 + Rnd(2);
    u8* p = (u8*)A(16 * *n);
    for (int i = 0; i < *n; i++) {
        float* v = (float*)A(4 * 8);
        const bool big = Rnd(2) == 0;  // half of them cover most of the lot, so the level-0 test passes often
        const float x = big ? Rf(-8, 8) : Rf(0, 60), y = big ? Rf(-8, 8) : Rf(0, 60), w = big ? Rf(50, 80) : Rf(1, 30), h = big ? Rf(50, 80) : Rf(1, 30);
        v[0] = x, v[1] = y, v[2] = x + w, v[3] = y, v[4] = x + w, v[5] = y + h, v[6] = x, v[7] = y + h;
        *(float**)(p + i * 16) = v;
        *(float**)(p + i * 16 + 4) = v + 8;
    }
    return p;
}

static u8* NewEntry(u8* fp) {
    u8* e = (u8*)A(0x20);
    *(DWORD*)e = 0x00FEAC7C;
    *(u8**)(e + 8) = fp;
    e[0x10] = (u8)Rnd(4);
    return e;
}

static u8* NewLeaf() {
    u8* n = (u8*)A(0x20);
    n[4] = (u8)(Rnd(4) == 0 ? 2 : 0);  // kind 0 leaf, sometimes 2
    const int cnt = Rnd(5) == 0 ? 1 : Rnd(9);
    if (cnt == 1 && Rnd(2)) {  // inline entry (bit 0 clear)
        u8* fp = PickFp();
        *(u8**)(n + 8) = NewEntry(fp);
        *(DWORD*)(n + 0xC) = Rnd(0xFFFFFFFF) | Rnd(0xFFFF) << 16;
        *(DWORD*)(n + 0x10) = Rnd(0xFFFFFF) << 8;
        return n;
    }
    u8* list = (u8*)A(12 * (cnt ? cnt : 1));
    for (int i = 0; i < cnt; i++) {
        *(u8**)(list + i * 12) = Rnd(12) ? NewEntry(PickFp()) : NULL;
        *(DWORD*)(list + i * 12 + 4) = Rnd(2) ? Rnd(0xFFFFFF) * 251 : 0xFFFFFFFF;
        *(DWORD*)(list + i * 12 + 8) = Rnd(2) ? Rnd(0xFFFFFF) * 251 : 0xFFFFFFFF;
    }
    *(DWORD*)(n + 8) = (DWORD)(uintptr_t)list | 1;
    *(DWORD*)(n + 0xC) = cnt;
    return n;
}

static u8* NewTree(int depth) {
    if (depth >= 2 || Rnd(4) == 0) return NewLeaf();
    u8* n = (u8*)A(0x20);
    n[4] = 1;
    for (int i = 0; i < 4; i++) *(u8**)(n + 0xC + i * 4) = Rnd(5) ? NewTree(depth + 1) : NULL;
    return n;
}

static u8* NewQ() {
    u8* q = (u8*)A(0x60);
    *(float*)(q + 4) = 0, *(float*)(q + 8) = 0;
    *(u8**)(q + 0x24) = NewTree(0);
    *(float*)(q + 0x38) = 64.0f;
    *(LONG*)(q + 0x44) = (LONG)Rnd(3);
    *(LONG*)(q + 0x48) = (LONG)Rnd(3);  // 0: QueryPoint calls the flush, which returns at once (flag 0)
    return q;
}

// a lot table node: key at +0, entry at +0x10 (key again), level base +0x40, Q vector +0x74/+0x78, rows +0xA0..+0xD0
static u8* NewLotNode(DWORD lo, DWORD hi, u8* q0, u8* q1, float ang, float tx, float tz) {
    u8* node = (u8*)A(0x200);
    *(DWORD*)node = lo, *(DWORD*)(node + 4) = hi;
    u8* e = node + 0x10;
    *(DWORD*)e = lo, *(DWORD*)(e + 4) = hi;
    *(LONG*)(e + 0x40) = 0;
    u8** qv = (u8**)A(8);
    qv[0] = q0, qv[1] = q1;
    *(u8***)(e + 0x74) = qv;
    *(u8***)(e + 0x78) = qv + 2;
    float* r = (float*)(e + 0xA0);
    const float c = cosf(ang), s = sinf(ang);
    r[0] = c, r[1] = 0, r[2] = s, r[3] = 0;
    r[4] = 0, r[5] = 1, r[6] = 0, r[7] = 0;
    r[8] = -s, r[9] = 0, r[10] = c, r[11] = 0;
    r[12] = tx, r[13] = 0, r[14] = tz, r[15] = 1;
    return node;
}

static void BuildWorld() {
    memset(&W, 0, sizeof(W));
    W.world = (u8*)A(0xA00);
    W.wm = (u8*)A(0x200);
    W.p = (u8*)A(0x200);
    W.act = (u8*)A(0x60);
    W.world[0x974] = 1;
    *(u8**)(W.world + 0x95C) = W.p;
    *(u8**)(W.world + 0x964) = W.wm;
    *(u8**)(W.wm + 0xB0) = W.act;
    *(DWORD*)(W.act + 0x48) = 0x99, *(DWORD*)(W.act + 0x4C) = 0;
    *(u8**)(uintptr_t)0x011D9128 = W.world;  // RoutingWorld_Get 0x8292D0 -> 0x836200 reads it
    *(u8**)(uintptr_t)0x011ECBC4 = W.wm;
    W.nfp = 24 + Rnd(60);
    for (int i = 0; i < W.nfp; i++) {
        int np;
        u8* polys = Polys(&np);
        W.fps[i] = NewFootprint(polys, np);
    }
    for (int i = 0, n = W.nfp; i < n; i++)  // a child on about a third of them (the leaves pick from all W.fps)
        if (RndK(3) == 0) { W.kid[i] = NewChild(W.fps[i]); g_kids++; }
    // two lots in the table (one of them sometimes the active lot), one that is not
    for (int i = 0; i < 3; i++) {
        W.lots[i] = (u8*)A(0x420);  // the IC 0x751480 reads lot +0x408/+0x40C (section I)
        *(DWORD*)(W.lots[i] + 0x48) = 0x10 + i;
        *(DWORD*)(W.lots[i] + 0x4C) = 0;
    }
    if (Rnd(6) == 0) *(DWORD*)(W.act + 0x48) = 0x11;  // lot 1 is the active lot: Contains says no
    const DWORD cnt = 1 + Rnd(3);
    DWORD* bk = (DWORD*)A((cnt + 1) * 4);
    bk[cnt] = kSentinel;
    for (int i = 0; i < 2; i++) {
        W.qs[i][0] = NewQ();
        W.qs[i][1] = Rnd(3) ? NewQ() : NULL;
        u8* node = NewLotNode(0x10 + i, 0, W.qs[i][0], W.qs[i][1], Rf(-3.1f, 3.1f), Rf(-5, 5), Rf(-5, 5));
        W.entries[i] = node + 0x10;
        const DWORD b = (0x10 + i) % cnt;  // chain: insert at the head
        *(DWORD*)(node + 0x190) = bk[b];
        bk[b] = (DWORD)(uintptr_t)node;
    }
    *(DWORD**)(W.p + 0x4C) = bk;
    *(DWORD*)(W.p + 0x50) = cnt;
    g_nRects = 3;
    g_rects[0] = {W.lots[0], -100, -100, 0, 100};
    g_rects[1] = {W.lots[1], 0, -100, 100, 100};
    g_rects[2] = {W.lots[2], 100, -100, 200, 100};
}

// A2 (0.21.0): buffer limits. One leaf with g_big.objs footprints (unique ids, a covering quad) replaces level 0 of both lots;
// g_big.cls2 of them have class byte 2, and one has type 0x1F (so the segment gather keeps the class-2 ones); agents get
// g_big.nodes flagged nodes in one lot
struct Big { int objs, cls2, nodes; bool noRadius; };
static Big g_big;
static void BigLeaf() {
    const int cnt = g_big.objs;
    u8* list = (u8*)A(12 * cnt);
    for (int i = 0; i < cnt; i++) {
        u8* p = (u8*)A(16);
        float* v = (float*)A(4 * 8);
        v[0] = -8, v[1] = -8, v[2] = 72, v[3] = -8, v[4] = 72, v[5] = 72, v[6] = -8, v[7] = 72;
        *(float**)p = v;
        *(float**)(p + 4) = v + 8;
        u8* f = NewFootprint(p, 1);
        *(DWORD*)(f + 0x14) = 0x100000 + i;  // unique: every footprint can add an id
        *(DWORD*)(f + 0x18) = 0;
        f[0x34] = i < g_big.cls2 ? 2 : 3;
        f[0x35] = i == cnt - 1 ? 0x1F : 3;
        f[0x48] = 0;
        W.fps[W.nfp++] = f;
        *(u8**)(list + i * 12) = NewEntry(f);
        *(DWORD*)(list + i * 12 + 4) = *(DWORD*)(list + i * 12 + 8) = 0xFFFFFFFF;
    }
    u8* n = (u8*)A(0x20);
    *(DWORD*)(n + 8) = (DWORD)(uintptr_t)list | 1;
    *(DWORD*)(n + 0xC) = cnt;
    for (int i = 0; i < 2; i++) *(u8**)(W.qs[i][0] + 0x24) = n;
}

// local (u, v) in lot i -> world (x, z): the inverse of the rows (rotation by ang, translation)
static void WorldPoint(int lot, float u, float v, float* x, float* z) {
    const float* r = (const float*)(W.entries[lot] + 0xA0);
    const float c = r[0], s = r[2], tx = r[12], tz = r[14];
    // u = x c - z s + tx, v = x s + z c + tz
    const float du = u - tx, dv = v - tz;
    *x = c * du + s * dv;
    *z = -s * du + c * dv;
}

struct Agent { u8* a; u8* nodes[80]; int nn; };
static Agent NewAgent(bool otherLot) {
    Agent ag = {};
    ag.a = (u8*)A(0x100);
    u8* head = ag.a + 8;
    ag.nn = g_big.nodes ? g_big.nodes : 1 + Rnd(5);
    u8* prev = head;
    const int lot = Rnd(2);
    for (int i = 0; i < ag.nn; i++) {
        u8* n = (u8*)A(0x40);
        ag.nodes[i] = n;
        *(u8**)prev = n;
        prev = n;
        float x, z;
        WorldPoint(lot, Rf(-4, 68), Rf(-4, 68), &x, &z);
        if (lot == 0 && x >= 0) x = -0.5f;  // keep it in lot 0's rectangle
        if (lot == 1 && x < 0) x = 0.5f;
        if (otherLot && Rnd(2)) x += lot == 0 ? 50 : 150;  // lot 2 (not in the table) or lot 1 seen from lot 0's frame
        float* p = (float*)(n + 0x10);
        p[0] = x, p[1] = Rf(0, 2), p[2] = z, p[3] = (float)(Rnd(5) == 0 ? 1 : 0);
        n[0x30] = (u8)(g_big.nodes || Rnd(3) != 0);
    }
    *(u8**)prev = head;
    *(float*)(ag.a + 0x40) = Rnd(4) && !g_big.noRadius ? Rf(0.2f, 6) : -1.0f;  // -1: the obstacle query finds nothing, so nothing is excluded
    *(DWORD*)(ag.a + 0xB8) = Rnd(0x10000);
    return ag;
}

struct IdVec { DWORD* b; DWORD* e; DWORD* c; };
static void NewIds(IdVec* v, int n) {
    v->b = (DWORD*)_aligned_malloc(8 * (n + 1), 16);  // growth goes through the game's allocator (our fake)
    v->e = v->b;
    v->c = v->b + 2 * (n + 1);
    for (int i = 0; i < n; i++) { *v->e++ = 0x1000 + Rnd(40); *v->e++ = 0; }
}

typedef void(__fastcall* GameLocoFn)(u8* agent, void* edx, IdVec* ids, const DWORD* ign1, DWORD n1, const DWORD* ign2, DWORD n2);

static int g_dbgOnce;
static void Dump(const char* what, const DWORD* a, int na, const DWORD* b, int nb) {
    if (g_dbgOnce++ > 3) return;
    printf("%s\n  game :", what);
    for (int i = 0; i < na; i++) printf(" %lX", a[i * 2]);
    printf("\n  clone:");
    for (int i = 0; i < nb; i++) printf(" %lX", b[i * 2]);
    printf("\n");
}

// one case: the game's 0x85FD10 vs SrRun; returns the clone's fallback reason (0 = compared)
static long g_cases, g_withIds, g_idsTotal, g_withExcl, g_fbCases[9], g_gatherObjs, g_withKid;
static int RunCase(const Agent& ag);
static int OneCase() {
    BuildWorld();
    return RunCase(NewAgent(Rnd(4) == 0));
}
static int RunCase(const Agent& ag) {
    DWORD ign1[8], ign2[8];
    const DWORD n1 = Rnd(3), n2 = Rnd(3);
    for (DWORD i = 0; i < 8; i++) { ign1[i] = 0x1000 + Rnd(40); ign2[i] = 0x1000 + Rnd(40); }
    for (DWORD i = 0; i < 8; i += 2) ign1[i + 1] = ign2[i + 1] = 0;
    DWORD ign1v[16], ign2v[16];
    for (int i = 0; i < 8; i++) { ign1v[i * 2] = ign1[i]; ign1v[i * 2 + 1] = 0; ign2v[i * 2] = ign2[i]; ign2v[i * 2 + 1] = 0; }
    IdVec ids;
    NewIds(&ids, Rnd(4));
    const int n0 = (int)(ids.e - ids.b) / 2;
    static DWORD before[512];
    memcpy(before, ids.b, n0 * 8);
    // the clone first (it must not change anything the game's call reads)
    static SrOut out;
    static SrCtx ctx;
    int why = SrRun(ag.a, before, n0, ign1v, n1, ign2v, n2, &out, &ctx);
    g_fbCases[why]++;
    if (!why) g_gatherObjs += ctx.n;  // the last gather's footprints
    // the game's call
    ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &ids, ign1v, n1, ign2v, n2);
    const int added = (int)(ids.e - ids.b) / 2 - n0;
    RefsOk();  // the game's references are balanced
    g_cases++;
    if (why) return why;
    if (added != out.nIds || memcmp(ids.b + n0 * 2, out.ids, (size_t)added * 8) != 0) {
        Dump("MISMATCH appended ids", ids.b + n0 * 2, added, &out.ids[0][0], out.nIds);
        printf("case %ld, n0 %d, ign %lu/%lu, radius %g, excl subs %d\n", g_cases, n0, n1, n2, *(const float*)(ag.a + 0x40), out.nSubs);
        {
            u8* first = SrFirstFlagged(ag.a);
            __declspec(align(16)) u8 pt[0x40] = {};
            FkConv((const float*)(first + 0x10), pt);
            DWORD vec[4] = {0, 0, 0, kSrVecTag};
            ((SrQueryFn)(uintptr_t)0x0082EDB0)(W.world, NULL, pt, *(const float*)(ag.a + 0x40), vec, 0, *(const DWORD*)(ag.a + 0xB8));
            printf(" game obstacle query:");
            for (DWORD* s = (DWORD*)(uintptr_t)vec[0]; s != (DWORD*)(uintptr_t)vec[1]; s++) printf(" %p", (u8*)(uintptr_t)*s - 4);
            printf("\n");
        }
        for (int i = 0; i < ag.nn; i++) {  // per flagged node: the game's gather 0x82F0B0 vs the clone's
            if (!ag.nodes[i][0x30]) continue;
            __declspec(align(16)) u8 pt[0x40] = {};
            FkConv((const float*)(ag.nodes[i] + 0x10), pt);
            DWORD vec[4] = {0, 0, 0, kSrVecTag};
            ((u8(__fastcall*)(u8*, void*, const void*, const void*, void*, DWORD, DWORD, float))(uintptr_t)0x0082F0B0)(
                W.world, NULL, pt, pt, vec, 1, *(const DWORD*)(ag.a + 0xB8), 0.0f);
            const u8* lot = FkLotLookup(NULL, NULL, ((float*)pt)[0], ((float*)pt)[2]);
            SrQuery(&ctx, W.p, lot ? *(DWORD*)(lot + 0x48) : 0, 0, *(SrPt*)pt, false, 0.0f, NULL);
            printf(" node %d (%.2f, %.2f) lot %p: game gather", i, ((float*)pt)[0], ((float*)pt)[2], lot);
            for (DWORD* s = (DWORD*)(uintptr_t)vec[0]; s != (DWORD*)(uintptr_t)vec[1]; s++) {
                const u8* o = (const u8*)(uintptr_t)*s - 4;
                printf(" %p[id %lX t %X c %X]", o, *(DWORD*)(o + 0x14), o[0x35], o[0x34]);
            }
            printf("\n   clone gather");
            for (int j = 0; j < ctx.n; j++) printf(" %p", ctx.objs[j]);
            printf("\n");
            const float* raw = (const float*)(ag.nodes[i] + 0x10);
            const u8* lotE = FkLotLookup(NULL, NULL, raw[0], raw[2]);
            float u = 0, v = 0;
            if (lotE) ((void(__fastcall*)(u8*, void*, DWORD, DWORD, float, float, float*, float*))(uintptr_t)0x0062EF30)(W.p, NULL, *(DWORD*)(lotE + 0x48), 0, raw[0], raw[2], &u, &v);
            for (int j = 0; j < ctx.n; j++) {
                const u8* o = ctx.objs[j];
                const void* const* vt = *(const void* const* const*)o;
                __declspec(align(16)) float a[4] = {u, v, u, u};
                const u8 c0 = ((SlotBoolFn)vt[0xC0 / 4])(o, NULL);
                const u8 e8 = c0 ? ((SlotSegFn)vt[0xE8 / 4])(o, NULL, 0, a, a) : 9;
                printf("   obj %p id %lX:%lX vt %08lX c0 %d e8 %d lotE %p uv %g %g ign2[0..1] %lX %lX\n", o, *(DWORD*)(o + 0x14), *(DWORD*)(o + 0x18),
                       *(DWORD*)o, c0, e8, lotE, u, v, ign2v[0], ign2v[2]);
            }
        }
        exit(1);
    }
    g_withIds += added > 0;
    g_idsTotal += added;
    g_withExcl += out.nSubs > 0;
    // the obstacle query itself vs the clone's point query
    u8* first = SrFirstFlagged(ag.a);
    __declspec(align(16)) u8 pt[0x40] = {};
    FkConv((const float*)(first + 0x10), pt);
    DWORD vec[4] = {0, 0, 0, kSrVecTag};
    ((SrQueryFn)(uintptr_t)0x0082EDB0)(W.world, NULL, pt, *(const float*)(ag.a + 0x40), vec, 0, *(const DWORD*)(ag.a + 0xB8));
    const int n = (int)((vec[1] - vec[0]) / 4);
    if (n != out.nSubs || (n && memcmp((const void*)(uintptr_t)vec[0], out.subs, (size_t)n * 4) != 0)) {
        printf("MISMATCH obstacle query: game %d footprints, clone %d (case %ld)\n", n, out.nSubs, g_cases);
        exit(1);
    }
    for (int i = 0; i < n; i++)  // 0.37: a child footprint was in the compared answer (before 0.37 an SRS_SLOT fallback)
        if (*(const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)vec[0])[i] == 0x00FEBF6C) { g_withKid++; break; }
    ((SrVecRelFn)(uintptr_t)0x00AAC090)(vec, NULL, (void*)(uintptr_t)vec[0], (void*)(uintptr_t)vec[1]);
    if (vec[0]) _aligned_free((void*)(uintptr_t)vec[0]);
    _aligned_free(ids.b);
    return 0;
}

// --- C: worker writer stubs ---
static HANDLE g_wIn, g_wGo;
static int g_cbSeen;
static void __fastcall CbRebuild(u8*, void*, DWORD) {
    // inside Footprint_EnsureLevelPolygons on the worker: QL is ours, exclusive
    assert(t_qlDepth == 1 && !TryAcquireSRWLockShared(&g_ql));
    g_cbSeen++;
    if (g_wIn) { SetEvent(g_wIn); WaitForSingleObject(g_wGo, 5000); }
}
static u8* g_wFp;
// C-A: the fake +0x918 for the sim-thread rebuild; the acquire must come before 0x80E5B0 marks the level valid
static u8 g_caLock[0x28];
static int g_caAcq, g_caRebuilds;
static LONG __fastcall CaFakeAcquire(void* lock, void*, const DWORD*) {
    assert((*(u8**)(g_wFp + 0x38))[0x20] == 0);  // level still invalid, polygons not freed yet
    g_caAcq++;
    return ++*(LONG*)((u8*)lock + 0x20);
}
static LONG __fastcall CaFakeRelease(void* lock, void*) { return --*(LONG*)((u8*)lock + 0x20); }
static void __fastcall CbRebuildSim(u8*, void*, DWORD) {
    assert(*(LONG*)(g_caLock + 0x20) == 1 && t_qlDepth == 0);  // inside the rebuild: +0x918 held, no QL
    g_caRebuilds++;
}
// F: the callees that block inside a tripwire writer (thiscall: the lock-key deleting destructor, ret 4; a footprint release)
static void __fastcall TwKeyDtor(u8*, void*, DWORD flag) {
    assert(flag == 1);
    SetEvent(g_wIn);
    WaitForSingleObject(g_wGo, 5000);
}
static void __fastcall TwFpRelease(u8*, void*) {
    SetEvent(g_wIn);
    WaitForSingleObject(g_wGo, 5000);
}
// a "job" as ServeAsync runs it (0.18.0): the tripwire stubs are armed before A_RUNNING and disarmed after A_IDLE
static void Job(bool on) {
    if (on) { TwArm(true); g_async.state = A_RUNNING; }
    else { g_async.state = A_IDLE; TwArm(false); }
}
// F2: the game's 0x807110 on a plain thread (T-S7 site 4), blocking in TwKeyDtor until g_wGo
static u8 g_kOld[0x10], g_kNew[0x10], g_kFp[0x80];
static DWORD g_kVt[4];
static void KeyReset() {
    g_kVt[0] = (DWORD)(uintptr_t)TwKeyDtor;
    *(DWORD**)g_kOld = g_kVt, *(LONG*)(g_kOld + 4) = 1;
    *(DWORD**)g_kNew = g_kVt, *(LONG*)(g_kNew + 4) = 1;
    *(u8**)(g_kFp + 0x20) = g_kOld;
}
static DWORD WINAPI KeyWriter(void*) {
    ((void(__fastcall*)(u8*, void*, u8*))(uintptr_t)0x00807110)(g_kFp, NULL, g_kNew);
    assert(*(u8**)(g_kFp + 0x20) == g_kNew && t_rg.n == 0 && t_twDepth == 0);
    return 0;
}
// F2: inside the clone (its active-lot lookup in phase 2): the job ends (disarm), a writer enters the now disarmed site and
// blocks; mode 2 also arms the next job before the clone goes on
static int g_f2Mode, g_f2Runs;
static HANDLE g_f2Writer;
static const u8* __cdecl F2ActiveLot() {
    if (g_f2Mode && !g_f2Writer) {
        TwArm(false);
        KeyReset();
        g_f2Writer = CreateThread(NULL, 0, KeyWriter, NULL, 0, NULL);
        WaitForSingleObject(g_wIn, INFINITE);
        if (g_f2Mode == 2) TwArm(true);
        g_f2Runs++;
    }
    return ((SrActiveLotFn)(uintptr_t)0x00C88FD0)();
}

// I: fakes for the IC's phase-1 callees (RouteHandle_Lookup, the route's vtable pair, 0xADD860, 0xB015B0, 0x74A8F0)
struct I {
    static u8* __fastcall Route(const void*, void*, DWORD h) { return h == 0x25000001 ? route : NULL; }
    static void __fastcall Ref(u8*, void*) { refIn++; }
    static void __fastcall Unref(u8*, void*) { refOut++; }
    static void __cdecl Level(const u8*, const float* pt, float* io) {  // the point's level: 0 mostly, 1 or -1 sometimes
        const DWORD h = (DWORD)(LONG)(pt[0] * 7.0f + pt[2] * 13.0f);
        io[0] = io[1] = 0.5f;
        *(LONG*)&io[2] = h % 7 == 0 ? 1 : h % 11 == 0 ? -1 : 0;
    }
    static u8 __cdecl Inside(const u8*, const void*) { return 0; }
    static float __cdecl Height(float x, float z, LONG, DWORD, DWORD) { return x * 0.5f - z; }
    static u8* route;
    static int refIn, refOut;
};
u8* I::route;
int I::refIn, I::refOut;

// AV ([AvoidRO]): fakes for RouteFollow_A's phase-1 callees the image cannot run on the fake world (the goal level 0x8468C0
// reads lot objects, the height 0x829470 the terrain) and for its Sim scan's inputs (agent slots +0x10 / +0x3C)
struct AVF {
    static LONG __cdecl Level(const float* g) { return (LONG)g[3]; }  // as FkConv: the level rides in w
    static float __fastcall Height(void*, void*, float, float, LONG, DWORD, DWORD) { return 0.5f; }
    static u64 __fastcall AgId(u8*, void*) { return 0x5555; }
    static void __fastcall AgBox(u8*, void*, float*) {}
    static u8* __fastcall PathOpts(u8* path, void*) { return *(u8**)(path + 4); }
};

static DWORD WINAPI WorkerThread(void*) {
    g_workerTid = GetCurrentThreadId();
    ((void(__fastcall*)(u8*, void*, DWORD))(uintptr_t)0x0080E5B0)(g_wFp, NULL, 0);
    assert(t_qlDepth == 0 && t_rg.n == 0);
    return 0;
}

// --- REPLAY K1 world: replay-fake.h's world built from game objects. The FootprintMapEntry pool and the general new/free are
// pointed at RfAlloc / RfFreeP for the section (random pads); the tree node and map node pools run the game's code on pool
// objects of the fake world, with pools.cpp's pop / push sites patched in (the clone on private pools, docs/94 §16.7) ---
struct RkSite { DWORD at; const void* to; u8 saved[5]; };
static RkSite g_rkSites[] = {
    {0x0061ED80, (const void*)RfFmePool},                                                                       // FootprintMapEntry pool
    {0x004E3F90, (const void*)RfNew}, {0x004E3F70, (const void*)RfFreeP},                                       // leaf arrays
};
static u8* g_rkWm;
static DWORD g_rkSavedWm;
static void RkPatch() {
    for (RkSite& s : g_rkSites) { memcpy(s.saved, (void*)(uintptr_t)s.at, 5); Jmp(s.at, s.to); }
    if (!(ts3native_counters.plReady & 1)) assert(PlInstall());  // all 12 pool sites on the image (byte check first)
    g_rkSavedWm = *(DWORD*)(uintptr_t)0x011ECBC4;
    g_rkWm = (u8*)RfAlloc(0x200);
    memset(g_rkWm, 0, 0x200);
    u8* act = (u8*)RfAlloc(0x60);
    memset(act, 0, 0x60);
    *(DWORD*)(act + 0x48) = 0x99;  // the active lot (LotTable_Contains says no for it) is none of ours
    *(u8**)(g_rkWm + 0xB0) = act;
    *(u8**)(uintptr_t)0x011ECBC4 = g_rkWm;
}
static void RkUnpatch() {
    for (RkSite& s : g_rkSites) memcpy((void*)(uintptr_t)s.at, s.saved, 5);
    *(DWORD*)(uintptr_t)0x011ECBC4 = g_rkSavedWm;
}
static u8* RkQ(u64 key) {  // LotQuadTree_ctor 0x664650 + SetBounds 0x6646F0 as LotRouting_QuadTreeBuild calls them: 64 x 64, c 1
    u8* q = (u8*)RfAlloc(0x60);
    memset(q, 0, 0x60);
    ((void(__fastcall*)(u8*, void*, DWORD, DWORD))(uintptr_t)0x00664650)(q, NULL, (DWORD)key, (DWORD)(key >> 32));
    ((void(__fastcall*)(u8*, void*, u8*, float, float, float, float, float, DWORD, DWORD, DWORD))(uintptr_t)0x006646F0)(
        q, NULL, g_rf.p, 0.0f, 0.0f, 64.0f, 64.0f, 1.0f, 0, 0, 0);
    *(LONG*)(q + 0x34) = 1000;  // held by its lot (the game's release at <= 1 deletes it)
    // default cell class 0 (walkable), multiplier 1, as QuadTreeBuild's 0x662CD0 call at 0x62FABD for a lot with a default in
    // P+0x68 (SetBounds leaves 12 = blocked); K3's in-room searches need open ground
    ((void(__fastcall*)(u8*, void*, DWORD, float))(uintptr_t)0x00662CD0)(q, NULL, 0, 1.0f);
    return q;
}
// + the middle class 0x1020AE8 (LotEdgeFootprint / PolygonFootprint, 0x40 bytes; 0.29.0's copy did not know it)
static const DWORD kRkCls[7][2] = {{0xFEBBC0, 0xFEBB98}, {0xFEBA98, 0xFEBA74}, {0xFEB848, 0xFEB820},
                                   {0xFEB970, 0xFEB94C}, {0xFEB5F8, 0xFEB5D4}, {0xFEB720, 0xFEB6F8},
                                   {0x1020AE8, 0x1020AC4}};  // polygon rebuild is a leaf or empty
static u8* RkFp(DWORD id) {
    const int c = (int)RfRnd(7);
    u8* f = (u8*)RfAlloc(0x50);
    memset(f, 0, 0x50);
    *(DWORD*)f = kRkCls[c][0], *(DWORD*)(f + 4) = kRkCls[c][1];
    *(LONG*)(f + 8) = 1000;  // held by its object: the game's release never deletes it here
    *(DWORD*)(f + 0x14) = id, *(DWORD*)(f + 0x18) = RfRnd(3) ? 0 : 5;
    *(u8**)(f + 0x24) = f + 0x24, *(u8**)(f + 0x28) = f + 0x24;
    f[0x34] = 3;
    const DWORD lv = RpLevels(f);
    u8* cache = (u8*)RfAlloc(lv * 0x24);
    memset(cache, 0, lv * 0x24);
    for (DWORD l = 0; l < lv; l++) {  // one quad per level, valid, bounds set
        u8* e = cache + l * 0x24;
        u8* polys = RfVec(e, 16);
        float* v = (float*)RfVec(polys, 4 * 8);
        *(DWORD*)(polys + 12) = 0;
        const float x = (float)(2 + RfRnd(50)), z = (float)(2 + RfRnd(50)), w = (float)(1 + RfRnd(10)), h = (float)(1 + RfRnd(10));
        v[0] = x, v[1] = z, v[2] = x + w, v[3] = z, v[4] = x + w, v[5] = z + h, v[6] = x, v[7] = z + h;
        *(float*)(e + 0x10) = x, *(float*)(e + 0x14) = z, *(float*)(e + 0x18) = x + w, *(float*)(e + 0x1C) = z + h;
        e[0x20] = 1;
    }
    *(u8**)(f + 0x38) = cache;
    return f;
}
// H20 (docs/109 §8.9): a portal object with the game's vtable 0xFED048 (count +0x70; the deleting slot +8 is never reached:
// every portal keeps at least one live hold)
static u8* RkPortal() {
    u8* p = (u8*)RfAlloc(0x80);
    memset(p, 0, 0x80);
    *(DWORD*)p = LT_PORTAL_VT;
    return p;
}
static void RkSetup(DWORD seed) {
    g_rfMakeQ = RkQ, g_rfMakeFp = RkFp, g_rfIdentity = true;
    RfSetup(seed, true);  // RfStep's new lots, Qs and footprints are game objects too
    RfCut();              // first cut: the clone
    assert(ts3native_counters.rpEpochs > 0 && g_rpClone && g_rpClone->nLots == g_rf.nLots);
}

// --- K3 / K4 / K5 (docs/94 §4.4, gate G4): the game's in-room search InRoomPlanToPoint 0x632630 (cell A* QuadTreeCellAStar
// 0x61E500 inside, docs/98) on the REPLAY world, once on the live planner P and once on the replay clone's P, each with its own
// cAStarSearch object (ctor 0x61BF60) at P+0xBC and in its trees' +0x28 as the P4 provider's acquire sets them (p4clone.cpp).
// Faked: FindLotById 0xC62D40 (a dummy lot object; the search only hands it on), Lot_RoomIdAtPoint 0x624780 (every point in
// the outside room of its level, 4) and the start room's portal lock refresh 0x632460 (it rewrites portal footprints in the
// trees; no portal footprints here). The routing pools the search pops (goal / start / cell node / cell object) get the game's
// own pool objects (their init functions). Answers compared: return code and every 64-byte node except +0x3C (stale stack,
// docs/98 §10.1). ---
typedef DWORD(__fastcall* K3PlanFn)(u8* P, void*, u8* X, DWORD keyLo, DWORD keyHi, const void* start, const void* target, u8* path,
                                    DWORD noNudge);
static const K3PlanFn kK3Plan = (K3PlanFn)(uintptr_t)0x00632630;
static u8 g_k3LotObj[0x600];
static u8* __fastcall K3FindLot(void*, void*, DWORD, DWORD, DWORD) { return g_k3LotObj; }
static DWORD __fastcall K3RoomAt(void*, void*, const void*, const float*) { return 4; }
static void __fastcall K3Refresh(void*, void*, void*, void*, DWORD) {}
static RkSite g_k3Sites[] = {{0x00C62D40, (const void*)K3FindLot}, {0x00624780, (const void*)K3RoomAt}, {0x00632460, (const void*)K3Refresh}};
static u8* K3Astar() {
    u8* a = (u8*)RfAlloc(0x9C90);
    memset(a, 0, 0x9C90);
    ((void(__fastcall*)(u8*, void*))(uintptr_t)0x0061BF60)(a, NULL);
    *(LONG*)(a + 4) = 1 << 24;  // held: no tree release deletes it
    return a;
}
static u8 *g_k3ALive, *g_k3AClone;
// Clone defect found by K3 (docs/94 §13.3, I-94-193): replay.cpp RpTree copies an inner node's 0x1C bytes with +8, the parent
// link, still pointing at the LIVE parent; the game's cell neighbour step (0x66A220: pos+0xC = node+8, pos+8 = parent+8) climbs
// those links, so a clone run walks into the live tree. Stand-in for the replay.cpp fix: relink before each search; counts
// what it had to fix (0 once RpTree sets the clone parent).
static long g_k3LiveParents;
static void K3Relink(u8* n, u8* parent) {
    if (!n || !(n[4] & 1)) return;
    if (parent && RpP(n + 8) != parent) *(u8**)(n + 8) = parent, g_k3LiveParents++;
    for (int k = 0; k < 4; k++) K3Relink(RpP(n + 0xC + 4 * k), n);
}
static void K3Point() {  // as PcAcquire: P+0xBC and every tree's +0x28 -> that side's A* object (lots and trees change per cut)
    *(u8**)(g_rf.p + 0xBC) = g_k3ALive;
    for (int l = 0; l < g_rf.nLots; l++)
        for (u8** q = *(u8***)(g_rf.lots[l] + 0x10 + 0x74); q < *(u8***)(g_rf.lots[l] + 0x10 + 0x78); q++) *(u8**)(*q + 0x28) = g_k3ALive;
    *(u8**)(g_rpClone->p + 0xBC) = g_k3AClone;
    for (int i = 0; i < g_rpClone->nLots; i++)
        for (int j = 0; j < g_rpClone->lots[i].nQ; j++)
            if (g_rpClone->lots[i].q[j]) {
                *(u8**)(g_rpClone->lots[i].q[j] + 0x28) = g_k3AClone;
                if (!GetEnvironmentVariableA("K3_NORELINK", NULL, 0)) K3Relink(RpP(g_rpClone->lots[i].q[j] + 0x24), NULL);
            }
}
struct K3Ans { DWORD ret; int n; u8 nodes[256][64]; };
static long g_k3Plans, g_k3Ok, g_k3Nodes, g_k3Detours;
// one plan on one side: the hierarchical plan's Q reset first (0x625400 -> AStarSearch_Reset 0x61E320, arg 0), then the search
static void K3Run(u8* P, u64 key, const float* s, const float* t, K3Ans* a) {
    ((void(__fastcall*)(u8*, void*, DWORD))(uintptr_t)0x0061E320)(*(u8**)(P + 0xBC), NULL, 0);
    __declspec(align(16)) u8 X[0xC0];
    ((void(__fastcall*)(u8*, void*))(uintptr_t)0x006298F0)(X, NULL);
    *(DWORD*)(X + 0xA0) = 0x00FEBF48;  // heuristic object (slot 0 0x61ECD0), as LotPlan_Inner 0x63F7C1
    *(u8**)(X + 0x40) = NULL;           // no agent options
    *(u8**)(X + 0x44) = P;
    *(u8**)(X + 0x48) = X + 0xA0;
    *(DWORD*)(X + 0x4C) = 0;
    *(DWORD*)(X + 0x50) = 0x10000;
    *(u8**)(X + 0x54) = g_k3LotObj;
    u8 path[0x100] = {};
    a->ret = kK3Plan(P, NULL, X, (DWORD)key, (DWORD)(key >> 32), s, t, path, 0);
    const u8* b = *(u8**)(path + 8);
    const u8* e = *(u8**)(path + 0xC);
    a->n = b ? (int)((e - b) / 64) : 0;
    assert(a->n <= 256);
    if (a->n) memcpy(a->nodes, b, a->n * 64);
    for (int i = 0; i < a->n; i++) memset(a->nodes[i] + 0x3C, 0, 4);  // stale stack (docs/98 §10.1)
}
static bool K3Same(const K3Ans& x, const K3Ans& y) { return x.ret == y.ret && x.n == y.n && !memcmp(x.nodes, y.nodes, x.n * 64); }
static void K3Dump(const char* what, const K3Ans& g, const K3Ans& c) {
    printf("K3 MISMATCH %s: ret %lu/%lu, nodes %d/%d\n", what, g.ret, c.ret, g.n, c.n);
    for (int i = 0; i < g.n || i < c.n; i++) {
        const float* p = (const float*)g.nodes[i < g.n ? i : 0];
        const float* q = (const float*)c.nodes[i < c.n ? i : 0];
        printf("  %d: game", i);
        for (int f = 0; f < 16; f++) printf(" %g", p[f]);
        printf(" | clone");
        for (int f = 0; f < 16; f++) printf(" %g", q[f]);
        printf("\n");
    }
}
// a sweep of start / target pairs on every live lot and level: live answer vs clone answer (K3); returns the live answers
static int K3Sweep(int pairs, K3Ans* keep, int nKeep, u64* keys, float (*pts)[6]) {
    K3Point();
    int kept = 0;
    static K3Ans g, c;
    for (int l = 0; l < g_rf.nLots; l++) {
        const u8* gr = g_rf.lots[l] + 0x10;
        const u64 key = *(DWORD*)gr;
        const int nq = (int)(*(u8***)(gr + 0x78) - *(u8***)(gr + 0x74));
        for (int i = 0; i < pairs; i++) {
            const float lv = (float)(*(LONG*)(gr + 0x40) + (int)RfRnd(nq));
            float s[3] = {Rf(1, 63), Rf(1, 63), 0}, t[3] = {Rf(1, 63), Rf(1, 63), 0};
            *(LONG*)&s[2] = *(LONG*)&t[2] = (LONG)lv;  // {x, z, level:int}
            K3Run(g_rf.p, key, s, t, &g);
            K3Run(g_rpClone->p, key, s, t, &c);
            g_k3Plans++;
            g_k3Ok += g.ret == 0;
            g_k3Nodes += g.n;
            g_k3Detours += g.n > 3;  // start, start cell, goal cell, goal: more crosses at least one more cell
            if (!K3Same(g, c)) {
                static K3Ans g2, c2;
                K3Run(g_rf.p, key, s, t, &g2);
                K3Run(g_rpClone->p, key, s, t, &c2);
                printf("K3 debug: live again %s, clone again %s, live again vs clone again %s\n", K3Same(g, g2) ? "same" : "DIFFERS",
                       K3Same(c, c2) ? "same" : "DIFFERS", K3Same(g2, c2) ? "same" : "DIFFERS");
                K3Dump("sweep", g, c);
                assert(false);
            }
            if (kept < nKeep) keep[kept] = g, keys[kept] = key, memcpy(pts[kept], s, 12), memcpy(pts[kept] + 3, t, 12), kept++;
        }
    }
    return kept;
}
// the tie lot: one blocking square (class 0xFEB5F8, byte +0x34 = 3, docs/98 §5.1) centred on x = 32 and z = 32 in an otherwise
// empty lot, so the two ways round it cost exactly the same (mirror images in exact binary coordinates); the game breaks the tie
// by insertion order (docs/98 §8.3), which the clone must keep
static u8* K3Box() {
    u8* f = (u8*)RfAlloc(0x50);
    memset(f, 0, 0x50);
    *(DWORD*)f = 0xFEB5F8, *(DWORD*)(f + 4) = 0xFEB5D4;
    *(LONG*)(f + 8) = 1000;
    *(DWORD*)(f + 0x14) = 0xB0B0, *(DWORD*)(f + 0x18) = 0;
    *(u8**)(f + 0x24) = f + 0x24, *(u8**)(f + 0x28) = f + 0x24;
    f[0x34] = 3;
    const DWORD lv = RpLevels(f);
    u8* cache = (u8*)RfAlloc(lv * 0x24);
    memset(cache, 0, lv * 0x24);
    for (DWORD l = 0; l < lv; l++) {
        u8* e = cache + l * 0x24;
        u8* polys = RfVec(e, 16);
        float* v = (float*)RfVec(polys, 4 * 8);
        *(DWORD*)(polys + 12) = 0;
        v[0] = 28, v[1] = 28, v[2] = 36, v[3] = 28, v[4] = 36, v[5] = 36, v[6] = 28, v[7] = 36;
        *(float*)(e + 0x10) = 28, *(float*)(e + 0x14) = 28, *(float*)(e + 0x18) = 36, *(float*)(e + 0x1C) = 36;
        e[0x20] = 1;
    }
    *(u8**)(f + 0x38) = cache;
    return f;
}
static long g_k3Ties;
static void K3TieCases(u64 key) {
    static K3Ans g, c;
    static const float kPairs[4][4] = {{32, 8, 32, 56}, {8, 32, 56, 32}, {32, 56, 32, 8}, {56, 32, 8, 32}};
    K3Point();
    for (int i = 0; i < 4; i++) {
        float s[3] = {kPairs[i][0], kPairs[i][1], 0}, t[3] = {kPairs[i][2], kPairs[i][3], 0};
        K3Run(g_rf.p, key, s, t, &g);
        K3Run(g_rpClone->p, key, s, t, &c);
        if (!K3Same(g, c)) {
            K3Dump("tie", g, c);
            assert(false);
        }
        if (!(g.ret == 0 && g.n > 2)) K3Dump("tie: not round the box", g, c);
        assert(g.ret == 0 && g.n > 2);  // found, round the box
        // one side only: every inner node on one side of the axis (the tie broken one way, not a straight line through)
        const int ax = kPairs[i][0] == kPairs[i][2] ? 0 : 2;
        int lo = 0, hi = 0;
        for (int n = 1; n + 1 < g.n; n++) { const float v = ((const float*)g.nodes[n])[ax]; lo += v < 32, hi += v > 32; }
        if (!(lo == 0 || hi == 0)) K3Dump("tie side", g, c);
        assert((lo == 0) != (hi == 0));
        g_k3Ties++;
    }
}

// --- K6 (docs/94 §13.3, gate G4): the game's world A* WorldAStar_Search 0x676260 (thiscall on the PathContainer W+0x18;
// R, G, &start ref, &goal ref, cost mode, opts, &best; ret 0x1C) on a fake routing world in the game's layouts (docs/99
// §1.2-§1.4, §2.1-§2.4): R chunks and records (types, edge bytes, blocked bits), G's water-link trees +0x30 / +0x4C, the body
// table +0x3A0 and the blockedNodeMap +0x35C, the lot-edge group hash W+0xA0, the container with the game's closed list
// (0x6735C0). R and G are read from W+0x10 / W+0x14 as the planner passes them (0x65F7F4..0x65F806). Each plan runs once on
// live and once on the working copy p4clone.cpp's PcSnap (the cut) and PcAcquire (the worker) make from the P3 version of the
// cut, through the W' that 0x829C00 hands out on the copy (0.32.0: I-94-220, I-94-221 fixed in PcAcquire). Every live structure
// the world search could reach (W, R's table and records, G's tree nodes, the group hash, the blockedNodeMap) sits on pages
// that are PAGE_NOACCESS during the acquire and each copy run: a read or write of live memory there fails K6. Not driven: the
// segment test 0x627780 (edge bytes 2 and 7) and the point test 0xC6BAB0 (lot-edge targets) answer "clear" without a world
// manager ([0x11ECBC4] = 0 here); mode 3's lot-bound water entry (RoutingWorld_Get, lot lookup 0xC68990, sea level 0xC61670)
// is left out (no flag 0x80 in mode 3). ---
typedef DWORD*(__fastcall* K6SearchFn)(u8* ctr, void*, const u8* R, const u8* G, const DWORD* s, const DWORD* g, DWORD mode, DWORD* opts,
                                       DWORD* best);
static const K6SearchFn kK6Search = (K6SearchFn)(uintptr_t)0x00676260;
static const int kK6Lat = 15;  // 4 x 4 chunks of 32 m; chunk 15 is the tie lattice (4 x 4 waypoints, 4 m apart)
struct K6Wp { int c, i; u8 type; u8 bx, bz; bool noEdge; int n; int nb[24]; u8 eb[24]; DWORD rec; };
static K6Wp g_k6Wp[16 * 16];
static int g_k6N;
static u8* g_k6Wc;             // W': the working copy's planner (PcAcquire's)
static P4Clone g_k6Cl;         // the acquired working copy (held until the next cut)
static RpClone g_k6Clone;      // a lot-less replay clone: PcAcquire's clone P
static u8* g_k6Live;           // live W, R, G tree nodes, group hash, blockedNodeMap: guarded pages
static DWORD g_k6LiveUsed;
static const DWORD kK6LiveSz = 1 << 20;
static DWORD g_k6Job;          // the job parameter block's first word (opts +0x8C -> it): the boat AGS code test
static DWORD g_k6Slots[64];    // water-link foot / boat slot values (blockedNodeMap keys)
static int g_k6NSlots;
static const DWORD kK6BnmEnd = 0x13579BDF, kK6GrpEnd = 0x2468ACE0;  // bucket sentinels (compared, never read through)

static void* K6L(DWORD n) {  // live memory on the guarded pages (bump, freed with the world)
    const DWORD at = (g_k6LiveUsed + 15) & ~15u;
    assert(at + n <= kK6LiveSz);
    g_k6LiveUsed = at + n;
    return g_k6Live + at;
}
static void K6Guard(bool on) {
    DWORD old;
    assert(VirtualProtect(g_k6Live, kK6LiveSz, on ? PAGE_NOACCESS : PAGE_READWRITE, &old));
}
static DWORD K6Ref(const K6Wp& w) { return (DWORD)w.c | ((DWORD)w.i << 16); }  // chunk | waypoint << 16 (docs/99 §2.2)
static void K6Pos(const K6Wp& w, float* x, float* z) { *x = (w.c % 4) * 32.0f + w.bx * 0.125f, *z = (w.c / 4) * 32.0f + w.bz * 0.125f; }
static u8 K6Edge(u8 a, u8 b) {  // edge byte by the type pairing of docs/99 §1.6
    if (a == 0x0D && b == 0x0D) return 3;
    if (a == 0x07 && b == 0x07) return 5;
    if (a == 0x0E && b == 0x0E) return 6;
    const bool la = a == 2 || a == 0x15, lb = b == 2 || b == 0x15;
    if ((a == 1 && lb) || (b == 1 && la)) return 1;
    if (la && lb) return 2;
    return Rnd(20) ? 7 : 4;
}
static void K6Link(int a, int b, u8 e) {
    K6Wp &x = g_k6Wp[a], &y = g_k6Wp[b];
    if (a == b || x.n == 24 || y.n == 24) return;
    for (int k = 0; k < x.n; k++) if (x.nb[k] == b) return;
    x.nb[x.n] = b, x.eb[x.n++] = e;
    y.nb[y.n] = a, y.eb[y.n++] = e;
}
// a balanced red-black-shaped tree of 0x20-byte nodes (EASTL rbtree_node_base: right +0, left +4, parent +8, colour +0xC;
// value from +0x10) from records in key order; anchor = &G+0x30 or &G+0x4C: right = rightmost, left = leftmost, parent = root
static u8* K6TreeBuild(u8* nodes, int lo, int hi, u8* parent) {
    if (lo > hi) return NULL;
    const int m = (lo + hi) / 2;
    u8* n = nodes + m * 0x20;
    *(u8**)(n + 8) = parent;
    *(u8**)(n + 4) = K6TreeBuild(nodes, lo, m - 1, n);
    *(u8**)n = K6TreeBuild(nodes, m + 1, hi, n);
    return n;
}
static void K6TreeSet(u8* anchor, const u8* recs, int n) {  // recs: n x 16 bytes (the node's +0x10..+0x1F)
    u8* nodes = (u8*)K6L(0x20 * (n ? n : 1));
    for (int i = 0; i < n; i++) memcpy(nodes + i * 0x20 + 0x10, recs + i * 16, 16);
    memset(anchor, 0, 0x14);
    if (!n) { *(u8**)anchor = anchor, *(u8**)(anchor + 4) = anchor; return; }
    u8* root = K6TreeBuild(nodes, 0, n - 1, anchor);
    *(u8**)anchor = nodes + (n - 1) * 0x20, *(u8**)(anchor + 4) = nodes, *(u8**)(anchor + 8) = root, *(DWORD*)(anchor + 0x10) = n;
}
static bool K6BnmHas(const u8* g, DWORD key) {  // as 0x66F890 reads it: key % count, chain +8
    const DWORD* bk = *(DWORD**)(g + 0x360);
    for (const u8* nd = (const u8*)(uintptr_t)bk[key % *(DWORD*)(g + 0x364)]; nd; nd = *(u8**)(nd + 8))
        if (*(DWORD*)nd == key) return true;
    return false;
}
static void K6BnmToggle(u8* g, DWORD key) {
    DWORD* bk = *(DWORD**)(g + 0x360);
    for (DWORD* at = &bk[key % *(DWORD*)(g + 0x364)]; *at; at = (DWORD*)(uintptr_t)(*at + 8))
        if (*(DWORD*)(uintptr_t)*at == key) { *at = *(DWORD*)(uintptr_t)(*at + 8); (*(DWORD*)(g + 0x368))--; return; }
    u8* nd = (u8*)K6L(16);
    *(DWORD*)nd = key, nd[4] = 1;
    *(DWORD*)(nd + 8) = bk[key % *(DWORD*)(g + 0x364)];
    bk[key % *(DWORD*)(g + 0x364)] = (DWORD)(uintptr_t)nd;
    (*(DWORD*)(g + 0x368))++;
}
// a PathContainer (docs/99 §2.1; +0 is the vtable 0xFEF990, R at +4): pool anchor +0x14/+0x18 self-linked, "Routing/PathHeap"
// at +0x1C, empty free list +0x20, the compiled tuning defaults of 0x673C70 (threshold 50; SHORT and LONG sets, I-91 row 11 /
// docs/108 §7 F-1), then the game's closed list for R (0x6735C0 -> 0x673500)
static void K6Ctr(u8* c, const u8* R) {
    static const float kShort[9] = {1.3f, 2, 2, 2, 2, 2, 0, 0, 0}, kLong[9] = {1.3f, 2, 4, 10, 10, 10, 0, 0, 0};
    memset(c, 0, 0x70);
    *(DWORD*)c = 0x00FEF990;
    *(u8**)(c + 0x14) = c + 0x14, *(u8**)(c + 0x18) = c + 0x14;
    *(const char**)(c + 0x1C) = "Routing/PathHeap";
    *(float*)(c + 0x24) = 50.0f;
    memcpy(c + 0x28, kShort, 36), memcpy(c + 0x4C, kLong, 36);
    ((void(__fastcall*)(u8*, void*, const u8*, DWORD))(uintptr_t)0x006735C0)(c, NULL, R, 0);
}

// the world: snap-fake.h's (G, P, lots, W, WM; the snapshot on), then R, G's water links, the blockedNodeMap and W replaced
static void K6Build(DWORD seed) {
    SfSetup(seed);
    g_seed = seed * 7919 + 13;
    u8* world = g_sf.world;
    if (!g_k6Live) g_k6Live = (u8*)VirtualAlloc(NULL, kK6LiveSz, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    assert(g_k6Live);
    g_k6LiveUsed = 0;
    memset(g_k6Live, 0, kK6LiveSz);
    u8* g = world + 0x8C;
    g_k6N = 0;
    for (int c = 0; c < 16; c++) {
        const int n = c == kK6Lat ? 16 : 5 + (int)Rnd(8);
        for (int i = 0; i < n; i++) {
            K6Wp& w = g_k6Wp[g_k6N++];
            memset(&w, 0, sizeof(w));
            w.c = c, w.i = i;
            if (c == kK6Lat) { w.type = 0x10, w.bx = (u8)(64 + 32 * (i % 4)), w.bz = (u8)(64 + 32 * (i / 4)); continue; }
            static const u8 kTypes[24] = {0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x11, 0x07, 0x07, 0x0D,
                                          0x0D, 0x0D, 0x0E, 0x02, 0x02, 0x01, 0x13, 0x15};
            w.type = kTypes[Rnd(24)];
            w.bx = (u8)(16 * Rnd(16)), w.bz = (u8)(16 * Rnd(16));  // a 2 m grid: many exact equal-cost alternatives
            w.noEdge = Rnd(30) == 0;                                 // a record without edge bytes (flag 0x40 clear): byte 0
        }
    }
    // neighbours: the 1-4 nearest compatible waypoints in the same or an adjacent chunk (both directions, one edge byte)
    for (int a = 0; a < g_k6N; a++) {
        const K6Wp& x = g_k6Wp[a];
        if (x.c == kK6Lat) continue;
        static int cand[256];
        static float d2[256];
        int nc = 0;
        float ax, az;
        K6Pos(x, &ax, &az);
        for (int b = 0; b < g_k6N; b++) {
            const K6Wp& y = g_k6Wp[b];
            if (b == a || y.c == kK6Lat || abs(y.c % 4 - x.c % 4) > 1 || abs(y.c / 4 - x.c / 4) > 1 || (x.type == 0x0E) != (y.type == 0x0E)) continue;
            float bx, bz;
            K6Pos(y, &bx, &bz);
            int k = nc++;
            const float d = (bx - ax) * (bx - ax) + (bz - az) * (bz - az);
            while (k && d2[k - 1] > d) cand[k] = cand[k - 1], d2[k] = d2[k - 1], k--;
            cand[k] = b, d2[k] = d;
        }
        const int want = 2 + (int)Rnd(3);
        for (int k = 0; k < nc && k < want; k++) K6Link(a, cand[k], K6Edge(x.type, g_k6Wp[cand[k]].type));
        for (int k = 0; k < nc; k++)  // streets: each 0x0D also to its nearest 0x0D (a car-only network for mode 2)
            if (x.type == 0x0D && g_k6Wp[cand[k]].type == 0x0D) { K6Link(a, cand[k], 3); break; }
    }
    // chunk to chunk: the nearest compatible pair of each chunk and its right and lower neighbour (a connected walk network)
    for (int c = 0; c < 16; c++)
        for (int nbc = c + 1; nbc <= c + 4; nbc += 3) {
            if (c == kK6Lat || nbc == kK6Lat || nbc > 15 || (nbc == c + 1 && c % 4 == 3)) continue;
            int ba = -1, bb = -1;
            float bd = 1e30f;
            for (int a = 0; a < g_k6N; a++)
                for (int b = 0; b < g_k6N; b++) {
                    const K6Wp &x = g_k6Wp[a], &y = g_k6Wp[b];
                    if (x.c != c || y.c != nbc || x.type == 0x0E || y.type == 0x0E) continue;
                    float ax, az, bx, bz;
                    K6Pos(x, &ax, &az), K6Pos(y, &bx, &bz);
                    const float d = (bx - ax) * (bx - ax) + (bz - az) * (bz - az);
                    if (d < bd) bd = d, ba = a, bb = b;
                }
            if (ba >= 0) K6Link(ba, bb, K6Edge(g_k6Wp[ba].type, g_k6Wp[bb].type));
        }
    int lat0 = -1;
    for (int a = 0; a < g_k6N; a++)
        if (g_k6Wp[a].c == kK6Lat) {
            if (lat0 < 0) lat0 = a;
            const int i = g_k6Wp[a].i;
            if (i % 4 < 3) K6Link(a, a + 1, 7);
            if (i / 4 < 3) K6Link(a, a + 4, 7);
        }
    for (int b = 0; b < g_k6N; b++)  // one bridge from the lattice into chunk 10 (walk network)
        if (g_k6Wp[b].c == 10 && g_k6Wp[b].type == 0x10) { K6Link(lat0, b, 7); break; }
    // a lot-edge waypoint lists its hub first (docs/99 §1.5), so the blocked rule compares hub references
    for (int a = 0; a < g_k6N; a++) {
        K6Wp& x = g_k6Wp[a];
        if (x.type != 2 && x.type != 0x15) continue;
        for (int k = 1; k < x.n; k++)
            if (g_k6Wp[x.nb[k]].type == 1) { int t = x.nb[0]; x.nb[0] = x.nb[k], x.nb[k] = t; u8 e = x.eb[0]; x.eb[0] = x.eb[k], x.eb[k] = e; break; }
    }
    // R: chunk = u16 count, u16 record offsets, records (n, flags, surface, bx, bz, refs, edge bytes)
    DWORD size = 0;
    for (int c = 0, a = 0; c < 16; c++) {
        int cnt = 0;
        while (a + cnt < g_k6N && g_k6Wp[a + cnt].c == c) cnt++;
        size += 2 + 2 * cnt;
        for (int k = 0; k < cnt; k++) size += 5 + 2 * g_k6Wp[a + k].n + (g_k6Wp[a + k].noEdge ? 0 : g_k6Wp[a + k].n);
        a += cnt;
    }
    DWORD* tbl = (DWORD*)K6L(16 * 4);
    u8* raw = (u8*)K6L(size);
    DWORD at = 0;
    for (int c = 0, a = 0; c < 16; c++) {
        int cnt = 0;
        while (a + cnt < g_k6N && g_k6Wp[a + cnt].c == c) cnt++;
        tbl[c] = at;
        *(WORD*)(raw + at) = (WORD)cnt;
        DWORD r = at + 2 + 2 * cnt;
        for (int k = 0; k < cnt; k++) {
            K6Wp& x = g_k6Wp[a + k];
            ((WORD*)(raw + at + 2))[k] = (WORD)(r - at);
            x.rec = r;
            u8* p = raw + r;
            p[0] = (u8)x.n, p[1] = (u8)(x.type | (x.noEdge ? 0 : 0x40)), p[2] = 0, p[3] = x.bx, p[4] = x.bz;
            for (int j = 0; j < x.n; j++) {
                const K6Wp& y = g_k6Wp[x.nb[j]];
                const int dx = y.c % 4 - x.c % 4, dz = y.c / 4 - x.c / 4;
                ((WORD*)(p + 5))[j] = (WORD)(y.i | (dx + 3) << 10 | (dz + 3) << 13);
                if (!x.noEdge) p[5 + 2 * x.n + j] = x.eb[j];
            }
            r += 5 + 2 * x.n + (x.noEdge ? 0 : x.n);
        }
        at = r, a += cnt;
    }
    assert(at == size);
    *(DWORD*)(world + 0x54) = 4, *(DWORD*)(world + 0x58) = 4, *(DWORD**)(world + 0x5C) = tbl, *(u8**)(world + 0x60) = raw, *(DWORD*)(world + 0x64) = size;
    world[0x974] = 1;  // routing on: 0x829C00 hands out W
    // G: water links (types 0x13 / 0x15) in +0x30 (key chunk << 16 | wp: body byte +0x14, foot slot +0x18, boat slot +0x1C;
    // -1 = none) and +0x4C (key body byte: ref +0x14, slots), body values +0x3A0 + 8 * body, blockedNodeMap on the slots
    static u8 r30[256 * 16], r4c[256 * 16];
    int n30 = 0, n4c = 0;
    g_k6NSlots = 0;
    for (int a = 0; a < g_k6N; a++) {
        const K6Wp& x = g_k6Wp[a];
        if (x.type != 0x13 && x.type != 0x15) continue;
        const DWORD body = Rnd(4), key = (DWORD)x.c << 16 | (DWORD)x.i;
        const DWORD foot = Rnd(5) ? 2000 + a : 0xFFFFFFFF, boat = Rnd(3) ? 3000 + a : 0xFFFFFFFF;
        if (foot != 0xFFFFFFFF && g_k6NSlots < 64) g_k6Slots[g_k6NSlots++] = foot;
        if (boat != 0xFFFFFFFF && g_k6NSlots < 64) g_k6Slots[g_k6NSlots++] = boat;
        DWORD* e = (DWORD*)(r30 + 16 * n30++);
        e[0] = key, e[1] = body, e[2] = foot, e[3] = boat;
        e = (DWORD*)(r4c + 16 * n4c++);
        e[0] = body, e[1] = key, e[2] = foot, e[3] = boat;
    }
    qsort(r30, n30, 16, [](const void* p, const void* q) { const DWORD a = *(const DWORD*)p, b = *(const DWORD*)q; return a < b ? -1 : a > b; });
    for (int i = 1; i < n4c; i++)  // stable by body (a multimap keeps insertion order among equal keys [inferred, docs/99 §2.5])
        for (int j = i; j && *(DWORD*)(r4c + 16 * (j - 1)) > *(DWORD*)(r4c + 16 * j); j--) {
            u8 t[16]; memcpy(t, r4c + 16 * j, 16); memcpy(r4c + 16 * j, r4c + 16 * (j - 1), 16); memcpy(r4c + 16 * (j - 1), t, 16);
        }
    K6TreeSet(g + 0x30, r30, n30);
    K6TreeSet(g + 0x4C, r4c, n4c);
    for (int b = 0; b < 4; b++) *(DWORD*)(g + 0x3A0 + 8 * b) = 1 + b;
    DWORD* bk = (DWORD*)K6L(8 * 4);
    bk[7] = kK6BnmEnd;
    *(DWORD**)(g + 0x360) = bk, *(DWORD*)(g + 0x364) = 7, *(DWORD*)(g + 0x368) = 0;
    for (int i = 0; i < g_k6NSlots; i++) if (Rnd(3) == 0) K6BnmToggle(g, g_k6Slots[i]);
    // W: P, R, G as 0x654060 sets them, the container, the lot-edge group hash (+0xA0: buckets +0xA4, count +0xA8; node key,
    // group, next; key chunk << 16 | wp as 0x653200 builds it)
    u8* Wl = (u8*)K6L(0x200);  // on the guarded pages
    memcpy(Wl, g_sf.W, 0x200);
    g_sf.W = Wl, *(u8**)(world + 0x960) = Wl;
    *(u8**)(Wl + 8) = g_sf.p, *(u8**)(Wl + 0x10) = world + 0x54, *(u8**)(Wl + 0x14) = g;
    *(DWORD*)(Wl + 0x9C) = 0x1234567 + seed;  // the RNG word (0x4FA800): W' takes it from the cut
    *(DWORD*)(Wl + 0xD8) = 0x61, *(DWORD*)(Wl + 0x114) = 0x61;  // next-resize words of live caches that have grown (crash G)
    K6Ctr(Wl + 0x18, world + 0x54);
    DWORD* gk = (DWORD*)K6L(6 * 4);
    gk[5] = kK6GrpEnd;
    *(DWORD*)(Wl + 0xAC) = 0;
    for (int a = 0; a < g_k6N; a++) {
        const K6Wp& x = g_k6Wp[a];
        if (x.type != 2 && x.type != 0x15) continue;
        DWORD* nd = (DWORD*)K6L(16);
        nd[0] = (DWORD)x.c << 16 | (DWORD)x.i, nd[1] = 2 * x.c + Rnd(2), nd[2] = gk[nd[0] % 5];
        gk[nd[0] % 5] = (DWORD)(uintptr_t)nd;
        (*(DWORD*)(Wl + 0xAC))++;  // size
    }
    *(DWORD**)(Wl + 0xA4) = gk, *(DWORD*)(Wl + 0xA8) = 5;
    // W+0xC = [world+0x964]: a live object read for the world origin (+0xB4 / +0xB8) by the ObjectFootprints fill 0x6580F0;
    // read only in a plan (I-94-231 row), so it stays off the guarded pages
    static u8 wmLike[0x100];
    *(float*)(wmLike + 0xB4) = 0, *(float*)(wmLike + 0xB8) = 0;
    *(u8**)(Wl + 0xC) = wmLike;
    // G's highCostFootprints tree (anchor G+0x7D4, size +0x7E4; node: links +0..+0xC, key +0x10..+0x1F compared hi/lo/hi/lo as
    // 0x66F390 does, cell vector +0x20/+0x24) with 3 live nodes on the guarded pages: a byte copy would hand them to 0x670510
    u8* hca = g + 0x7D4;
    u8* hn = (u8*)K6L(3 * 0x30);
    for (int i = 0; i < 3; i++) *(DWORD*)(hn + i * 0x30 + 0x14) = 0x100 + i;
    u8 *n0 = hn, *n1 = hn + 0x30, *n2 = hn + 0x60;
    *(u8**)(n1 + 4) = n0, *(u8**)n1 = n2, *(u8**)(n0 + 8) = n1, *(u8**)(n2 + 8) = n1, *(u8**)(n1 + 8) = hca;
    *(u8**)hca = n2, *(u8**)(hca + 4) = n0, *(u8**)(hca + 8) = n1, *(DWORD*)(hca + 0x10) = 3;
    g_k6Wc = NULL;
}

// one writer step on live, with the signal the real site gives (snap-fake.h's site numbers): an R blocked bit (ApplyLotUpdate /
// RestoreLotBlockedBits), a blockedNodeMap slot (grid add, lot pointer arg 2), a high-cost polygon (0x6733C0 -> 0x670750)
static int g_k6WriteKind[3];
static void K6Write() {
    DWORD* s = SfSlot();
    const int lot = (int)Rnd(g_sf.nLots);
    switch (Rnd(3)) {
        case 0: {
            const K6Wp& x = g_k6Wp[(Rnd(12) * 37) % g_k6N];  // 12 waypoints flip, so only a few are blocked at a time
            (*(u8**)(g_sf.world + 0x60))[x.rec + 1] ^= 0x80;
            s[1] = (DWORD)(uintptr_t)g_sf.lotObj[lot];
            SfSignal(Rnd(2) ? 18 : 19, s);
            g_k6WriteKind[0]++;
            break;
        }
        case 1: {
            if (!g_k6NSlots) break;
            K6BnmToggle(g_sf.world + 0x8C, g_k6Slots[Rnd(g_k6NSlots)]);
            s[1] = 0, s[2] = (DWORD)(uintptr_t)g_sf.lotObj[lot];
            SfSignal(20, s);
            g_k6WriteKind[1]++;
            break;
        }
        default: {
            if (g_sf.nPolys == 64) break;
            const float cx = (float)Rnd(g_sf.gw * 4), cz = (float)Rnd(g_sf.gh * 4), r = 1.0f + (float)Rnd(30);
            u8* vec = (u8*)A(12);
            float* pts = (float*)SfVec(vec, 4 * 8);
            for (int i = 0; i < 4; i++) pts[2 * i] = cx + (i == 1 || i == 2 ? r : -r), pts[2 * i + 1] = cz + (i >= 2 ? r : -r);
            DWORD key[4] = {0x5000 + (DWORD)g_sf.nPolys, 0, 0x6000 + Rnd(50), 0};
            memcpy(s + 1, key, 16);
            s[5] = (DWORD)(uintptr_t)vec;
            SfSignal(22, s);
            SfPolyStamp(s, vec);
            memcpy(g_sf.polyKey[g_sf.nPolys], key, 16);
            g_sf.polys[g_sf.nPolys++] = vec;
            g_k6WriteKind[2]++;
        }
    }
}

// I-94-231 (docs/94 §21.13.1): the pieces of the hierarchical planner 0x65E0D0 that run without a world manager, terrain or
// lots, on W' with the live pages guarded after every acquire: the lot-exit masks 0x64FEA0 (W'+0x11C/+0x120/+0x124 from the
// routing world RoutingWorld_Get hands out, the copy as in a clone run), the "ObjectFootprints" cache fill 0x6580F0 (W'+0xC0
// from the copy's R and the world origin [W+0xC]+0xB4/+0xB8) on every chunk whose type-0x0E first-neighbour chains close
// (the game walks them until it is back at the start), and the highCostFootprints erase 0x670510 on the copy's G with a key
// the live tree holds (a byte-copied anchor hands it the live node: read on a guarded page = K6 FAIL). Then the clone-run stop
// on the world-level writer leaves (routesnap.cpp SnEnter: P4Lazy before 0x62D9F0, 0x670510, 0x636BF0, 0x6733C0, ...)
static long g_k6Masks, g_k6ObjFp, g_k6ObjFpPts, g_k6HcfMiss, g_k6Stops;
static DWORD g_k6Fault;  // the address a guarded copy run touched
typedef void(__fastcall* K6MasksFn)(u8* W, void*);
typedef u8*(__fastcall* K6ObjFpFn)(u8* W, void*, DWORD chunk);  // returns the cache value: a vector of 16-byte point vectors
typedef void(__fastcall* K6HcfFn)(u8* G, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);  // owner id, footprint id, map mask
static u8* g_k6ObjFpOut;
static int K6PlannerTry(int what, u8* a, DWORD b) {
    __try {
        if (what == 0) ((K6MasksFn)(uintptr_t)0x0064FEA0)(a, NULL);
        else if (what == 1) g_k6ObjFpOut = ((K6ObjFpFn)(uintptr_t)0x006580F0)(a, NULL, b);
        else ((K6HcfFn)(uintptr_t)0x00670510)(a, NULL, 0, b, 0, 0, 0xFFFFFFFF, 0xFFFFFFFF);
        return 0;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
                    ? (g_k6Fault = (DWORD)GetExceptionInformation()->ExceptionRecord->ExceptionInformation[1], EXCEPTION_EXECUTE_HANDLER)
                    : EXCEPTION_CONTINUE_SEARCH) {
        return 1;
    }
}
static void K6PlannerRun(const char* what, int kind, u8* a, DWORD b) {
    K6Guard(true);
    const int f = K6PlannerTry(kind, a, b);
    K6Guard(false);
    if (f) { printf("K6 FAIL (I-94-231): %s on the working copy touched live memory at %08lX\n", what, g_k6Fault); assert(false); }
}
static int K6Cycle(int a) {  // length of a's first-neighbour chain back to a (0: it does not come back)
    int n = 1;
    for (int x = g_k6Wp[a].nb[0]; x != a; x = g_k6Wp[x].nb[0], n++)
        if (n > 64 || !g_k6Wp[x].n) return 0;
    return n;
}
static void K6Planner(PcWork& w) {
    u8* const world = g_sf.world;
    u8** const rw = (u8**)(uintptr_t)0x011D9128;
    // lot-exit masks: RoutingWorld_Get = the copy (T4 in a clone run)
    *rw = w.world;
    K6PlannerRun("lot-exit masks 0x64FEA0", 0, g_k6Wc, 0);
    *rw = NULL;
    DWORD m0 = world[0x4A] ? 0x11 : 0x10, m1 = world[0x4D] ? 9 : 8;
    if (world[0x4B]) m0 |= 2;
    if (world[0x4C]) m0 |= 0x100;
    if (world[0x4E]) m1 |= 2;
    if (world[0x4F]) m1 |= 0x100;
    const float t = *(float*)(world + 0x24);
    assert(*(DWORD*)(g_k6Wc + 0x11C) == m0 && *(DWORD*)(g_k6Wc + 0x120) == m1 && *(float*)(g_k6Wc + 0x124) == t * t + t);
    g_k6Masks++;
    // ObjectFootprints on W' (a chunk already in the cache comes back as is)
    for (int c = 0; c < 16; c++) {
        bool ok = true, any = false;
        for (int a = 0; a < g_k6N; a++)
            if (g_k6Wp[a].c == c && g_k6Wp[a].type == 0x0E) { any = true; ok = ok && g_k6Wp[a].n && K6Cycle(a); }
        if (!ok) continue;
        K6PlannerRun("ObjectFootprints fill 0x6580F0", 1, g_k6Wc, (DWORD)c);
        const DWORD* v = (const DWORD*)g_k6ObjFpOut;
        assert(v && (v[1] - v[0]) % 16 == 0 && (!any || v[1] > v[0]));
        for (const DWORD* in = (const DWORD*)(uintptr_t)v[0]; in != (const DWORD*)(uintptr_t)v[1]; in += 4) {
            const float* p = (const float*)(uintptr_t)in[0];
            const int np = (int)((in[1] - in[0]) / 8);
            int hit = -1;
            for (int a = 0; a < g_k6N && hit < 0; a++) {
                float x, z;
                K6Pos(g_k6Wp[a], &x, &z);
                if (g_k6Wp[a].c == c && g_k6Wp[a].type == 0x0E && x == p[0] && z == p[1]) hit = a;
            }
            assert(hit >= 0 && np == K6Cycle(hit));  // the start waypoint, then its chain, from the copy's R
            g_k6ObjFpPts += np;
        }
        g_k6ObjFp++;
    }
    // highCostFootprints: the copy's tree is empty (p4clone.cpp PcWxApply), the live one keeps its 3 nodes
    const u8* lca = world + 0x8C + 0x7D4;
    const u8* cca = w.world + 0x8C + 0x7D4;
    K6PlannerRun("highCostFootprints erase 0x670510", 2, w.world + 0x8C, 0x101);
    assert(*(const u8* const*)cca == cca && !*(const DWORD*)(cca + 0x10) && *(const DWORD*)(lca + 0x10) == 3);
    g_k6HcfMiss++;
    // the clone-run stop on the world-level writer leaves
    for (int i = 0; i < SN_N; i++) {
        const int lf = kSn[i].leaf;
        if (lf != SL_REMOVELOT && lf != SL_APPLYLOT && lf != SL_GRIDADD && lf != SL_GRIDREMOVE && lf != SL_HIGHCOST && lf != SL_MARKREMOVE &&
            lf != SL_GBUILD)
            continue;
        DWORD slot[8] = {0};
        const auto ab = g_p4Job.abort;
        bool stopped = false;
        t_snapRun = 1;
        __try { SnEnter(i, slot); } __except (GetExceptionCode() == kP4LazyCode ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) { stopped = true; }
        t_snapRun = 0;
        g_p4Job.abort = ab;
        assert(stopped);
        g_k6Stops++;
    }
}

// a reader cut (an armed P4 START: every tile built), p4clone.cpp's snapshot of it (PcSnap, as PcCutFn takes it) and the
// working copy from p4clone.cpp's PcAcquire on a lot-less replay clone, with the live pages guarded. Checks: audit 0 mismatches;
// I-94-220: 0x829C00 on the copy hands out W' (not the live W) whose P / R / G are the clone's / the copy's; I-94-221: the
// game's tree step 0x4E0B20 from the copy's last +0x30 node ends at the copy's anchor; the copy's R, high-cost maps,
// blockedNodeMap, trees and group hash == live at the cut
static long g_k6Cuts, g_k6HcCells, g_k6TreeEnds, g_k6Guarded, g_k6CacheIns, g_k6Drops;
static void K6Cut() {
    u8* world = g_sf.world;
    u8* g = world + 0x8C;
    if (g_k6Wc) PcRelease(&g_k6Cl, 0, NULL, 0);  // the previous copy
    assert(SpCut(world, true) && SpTilesAll(g_spCur));
    const LONG m0 = SfMisSum();
    SpAudit(g_spCur, world);
    assert(SfMisSum() == m0);
    PcCut& c = g_pcCut;
    SpVerRel(c.v);
    assert(PcSnap(c, world, g_sf.p));
    c.v = SpVerRef(g_spCur), c.epoch = g_spEpoch, c.active = PcActiveLot(), c.grid = NULL, c.keys = NULL;
    c.ver = ++g_rpPub, c.ok = 1;
    // the replay side PcAcquire expects: the consumer at the position, the clone (no lots: the world search reads none)
    g_rpOn = 1, g_rpHave = true, g_rpThreaded = true, g_rpNeedEpoch = 0, g_rpBad = 0, g_rpTail = g_rpHead = c.ver;
    if (!g_k6Clone.p) g_k6Clone.p = (u8*)A(0x110);
    g_rpClone = &g_k6Clone;
    K6Guard(true);
    const int why = PcAcquire(c.ver, &g_k6Cl);
    K6Guard(false);
    g_rpOn = 0;
    if (why >= 0) { printf("K6 FAIL: PcAcquire refused (%d; a fault in the copy build reads live memory?)\n", why); assert(false); }
    PcWork& w = g_pcWork;
    assert(g_k6Cl.world == w.world);
    u8* cg = w.world + 0x8C;
    // I-94-220: RoutingJob_Execute takes W through 0x829C00 (0x81B3E7) on the world RoutingWorld_Get hands it (the copy in a
    // clone run); the planner reads P (+8), R (+0x10), G (+0x14) from it and writes its container and caches
    g_k6Wc = ((u8*(__fastcall*)(const u8*, void*))(uintptr_t)0x00829C00)(w.world, NULL);
    if (g_k6Wc == g_sf.W || !g_k6Wc) { printf("K6 FAIL (I-94-220): the working copy's [world+0x960] is the live W\n"); assert(false); }
    assert(g_k6Wc == w.wp && *(u8**)(g_k6Wc + 8) == g_k6Clone.p && *(u8**)(g_k6Wc + 0x10) == w.world + 0x54 && *(u8**)(g_k6Wc + 0x14) == cg);
    assert(*(u8**)(g_k6Wc + 0x18 + 4) == w.world + 0x54 && *(u8**)(g_k6Wc + 0x18 + 0x10) != *(u8**)(g_sf.W + 0x18 + 0x10));  // own closed list
    assert(*(u8**)(g_k6Wc + 0xEC) == g_k6Clone.p && *(u8**)(g_k6Wc + 0xF0) == g_k6Wc && *(u8**)(g_k6Wc + 0xE0) != g_k6Wc + 0xE0);  // FindClosest: 10 blocks
    assert(*(DWORD*)(g_k6Wc + 0x9C) == *(DWORD*)(g_sf.W + 0x9C) && !memcmp(g_k6Wc + 0x18 + 0x24, g_sf.W + 0x18 + 0x24, 0x4C));
    // the group hash: private, every live entry found with its value
    const DWORD* gbk = *(DWORD**)(g_k6Wc + 0xA4);
    assert(gbk != *(DWORD**)(g_sf.W + 0xA4) && *(DWORD*)(g_k6Wc + 0xAC) == *(DWORD*)(g_sf.W + 0xAC) && gbk[5] == kK6GrpEnd);
    for (DWORD b = 0; b < 5; b++)
        for (const DWORD* n = (const DWORD*)(uintptr_t)(*(DWORD**)(g_sf.W + 0xA4))[b]; n; n = (const DWORD*)(uintptr_t)n[2]) {
            const DWORD* m = (const DWORD*)(uintptr_t)gbk[n[0] % 5];
            while (m && m[0] != n[0]) m = (const DWORD*)(uintptr_t)m[2];
            assert(m && m != n && m[1] == n[1]);
        }
    // I-94-221: the copy's trees end at the copy's anchors and hold the live values in order
    for (int t = 0; t < 2; t++) {
        const u8 *la = g + (t ? 0x4C : 0x30), *ca = cg + (t ? 0x4C : 0x30);
        static u8 lv[256 * 16], cv[256 * 16];
        const int n = PcTreeWalk(la, lv);
        assert(n >= 0 && n <= 256 && PcTreeWalk(ca, cv) == n && !memcmp(lv, cv, n * 16));
        const u8* last = *(u8**)ca;
        if (n >= 2) {
            if (((const u8*(__cdecl*)(const u8*))(uintptr_t)0x004E0B20)(last) != ca) {
                printf("K6 FAIL (I-94-221): the game's tree step from the copy's last node left the copy\n");
                assert(false);
            }
            g_k6TreeEnds++;
        }
    }
    // the copy == live at the cut: R (table and records), every high-cost map, every blockedNodeMap slot
    const DWORD rsz = *(DWORD*)(world + 0x64);
    assert(*(u8**)(w.world + 0x5C) != *(u8**)(world + 0x5C) && !memcmp(*(u8**)(w.world + 0x5C), *(u8**)(world + 0x5C), 64) &&
           !memcmp(*(u8**)(w.world + 0x60), *(u8**)(world + 0x60), rsz));
    const DWORD cells = g_sf.gw * g_sf.gh;
    for (DWORD m = 0; m < c.nHc; m++) {
        const u8* lm = *(u8**)(*(u8**)(g + 0x7C0) + m * 16);
        const u8* cm = *(u8**)(*(u8**)(cg + 0x7C0) + m * 16);
        assert(lm != cm && !memcmp(lm, cm, cells));
        for (DWORD i = 0; i < cells; i++) g_k6HcCells += lm[i] != 0;
    }
    assert(*(u8**)(cg + 0x360) != *(u8**)(g + 0x360));
    for (int i = 0; i < g_k6NSlots; i++) assert(K6BnmHas(g, g_k6Slots[i]) == K6BnmHas(cg, g_k6Slots[i]));
    // crash G (docs/67 §9): the planner's lazy "ObjectFootprints" cache W'+0xC0 inserts through the game's 0x657BC0 (as
    // 0x6580F0 at 0x658129). On the first insert into W' the hash must rehash off the game's shared empty bucket array
    // 0x114C900; with the live next-resize word it linked its node there (every empty hash then had it in bucket 0)
    {
        DWORD* const shared = (DWORD*)(uintptr_t)0x0114C900;
        const DWORD key = 0x10000 + (DWORD)g_k6Cuts, n0 = *(DWORD*)(g_k6Wc + 0xCC);
        assert(!shared[0]);
        ((void*(__fastcall*)(u8*, void*, const DWORD*))(uintptr_t)0x00657BC0)(g_k6Wc + 0xC0, NULL, &key);
        if (shared[0] || *(DWORD*)(g_k6Wc + 0xC4) == kPcEmptyBk) {
            printf("K6 FAIL (crash G): the insert into W'+0xC0 linked node 0x%08lX into the shared empty bucket array 0x114C900\n", shared[0]);
            assert(false);
        }
        assert(*(DWORD*)(g_k6Wc + 0xCC) == n0 + 1);
        g_k6CacheIns++;
    }
    K6Planner(w);
    g_k6Cuts++;
}

struct K6Ans { int found, n; DWORD best, closedN; DWORD refs[512]; u8 closed[1024]; };
struct K6Q { DWORD s, g, mode, job; DWORD opts[64]; };
// one search on one planner W: R and G from W+0x10 / W+0x14 as 0x65F7F4..0x65F806 pass them, opts +0x88 = W; the answer is
// the world path (u32 refs, 0x676130), the best (closest-heuristic) ref and the closed bits the search left
static void K6Run(u8* Wp, const K6Q& q, K6Ans* a) {
    __declspec(align(16)) DWORD o[64];  // the circle prepass 0x673600 stores the centre with movaps at opts+0x90
    DWORD s = q.s, g = q.g, best = 0;
    memcpy(o, q.opts, sizeof(o));
    o[0x22] = (DWORD)(uintptr_t)Wp;
    g_k6Job = q.job;
    const DWORD* v = kK6Search(Wp + 0x18, NULL, *(u8**)(Wp + 0x10), *(u8**)(Wp + 0x14), &s, &g, q.mode, o, &best);
    a->found = v != NULL, a->n = 0, a->best = best;
    if (v) {  // shortcut: the path vector is not freed (test process)
        a->n = (int)(((const DWORD*)(uintptr_t)v[1]) - ((const DWORD*)(uintptr_t)v[0]));
        assert(a->n <= 512);
        memcpy(a->refs, (const void*)(uintptr_t)v[0], a->n * 4);
    }
    a->closedN = *(DWORD*)(Wp + 0x18 + 8);
    assert(a->closedN <= sizeof(a->closed));
    memcpy(a->closed, *(u8**)(Wp + 0x18 + 0x10), a->closedN);
}
// a run on the working copy: the live pages guarded, so any read or write of live W, R, G trees, group hash or blockedNodeMap
// by the world search faults here and fails K6
static int K6RunCopyTry(const K6Q& q, K6Ans* a) {
    __try {
        K6Run(g_k6Wc, q, a);
        return 0;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
                    ? (g_k6Fault = (DWORD)GetExceptionInformation()->ExceptionRecord->ExceptionInformation[1], EXCEPTION_EXECUTE_HANDLER)
                    : EXCEPTION_CONTINUE_SEARCH) {
        return 1;
    }
}
static void K6RunCopy(const K6Q& q, K6Ans* a) {
    K6Guard(true);
    const int f = K6RunCopyTry(q, a);
    K6Guard(false);
    if (f) {
        printf("K6 FAIL (I-94-220): a world search on the working copy touched live memory at %08lX (live %08lX..%08lX)\n", g_k6Fault,
               (DWORD)(uintptr_t)g_k6Live, (DWORD)(uintptr_t)g_k6Live + kK6LiveSz);
        assert(false);
    }
    g_k6Guarded++;
}
static bool K6Same(const K6Ans& x, const K6Ans& y) {
    return x.found == y.found && x.n == y.n && x.best == y.best && x.closedN == y.closedN && !memcmp(x.refs, y.refs, x.n * 4) &&
           !memcmp(x.closed, y.closed, x.closedN);
}
static void K6Dump(const char* what, const K6Q& q, const K6Ans& l, const K6Ans& c) {
    printf("K6 MISMATCH %s: %08lX -> %08lX mode %lu flags %lX: found %d/%d, nodes %d/%d, best %08lX/%08lX, closed %s\n", what, q.s, q.g, q.mode,
           q.opts[0], l.found, c.found, l.n, c.n, l.best, c.best, l.closedN == c.closedN && !memcmp(l.closed, c.closed, l.closedN) ? "same" : "DIFFERS");
    for (int i = 0; i < l.n || i < c.n; i++)
        if (i >= l.n || i >= c.n || l.refs[i] != c.refs[i]) { printf("  first differing node %d: %08lX / %08lX\n", i, i < l.n ? l.refs[i] : 0, i < c.n ? c.refs[i] : 0); break; }
}
static K6Q K6Query(int sa, int ga, DWORD mode, DWORD flags) {
    K6Q q;
    memset(&q, 0, sizeof(q));
    const K6Wp &s = g_k6Wp[sa], &g = g_k6Wp[ga];
    q.s = K6Ref(s), q.g = K6Ref(g), q.mode = mode;
    q.opts[0] = flags, q.opts[1] = 0xFFFFFFFF, q.opts[0xD] = 0xFFFFFFFF;  // no start / goal lot hub
    K6Pos(s, (float*)&q.opts[2], (float*)&q.opts[3]), K6Pos(g, (float*)&q.opts[0xE], (float*)&q.opts[0xF]);
    K6Pos(s, (float*)&q.opts[0x2A], (float*)&q.opts[0x2B]), K6Pos(g, (float*)&q.opts[0x2E], (float*)&q.opts[0x2F]);
    q.opts[0x23] = (DWORD)(uintptr_t)&g_k6Job;
    return q;
}
static long g_k6Plans, g_k6Found, g_k6Nodes, g_k6Water, g_k6Mode[5];
// random pairs over every waypoint, modes 1-4, option bits: 0x40 retry (trespass = wall), 0x80 water (modes 1 and 4), 0x100
// boat with 0x80 (AGS code 0xC00 or 0 in the job block), 0x200 (no lot-edge targets), sometimes a start lot hub with 0x10
static int K6Sweep(int pairs, K6Q* keep, K6Ans* keepAns, int nKeep) {
    static K6Ans l, c;
    int kept = 0;
    for (int p = 0; p < pairs; p++) {
        const DWORD mode = 1 + Rnd(4);
        DWORD flags = 0;
        if (Rnd(5) == 0) flags |= 0x40;
        if ((mode == 1 || mode == 4) && Rnd(3)) flags |= 0x80;
        if ((flags & 0x80) && Rnd(2)) flags |= 0x100;
        if (Rnd(5) == 0) flags |= 0x200;
        int sa = (int)Rnd(g_k6N), ga = (int)Rnd(g_k6N);
        for (int t = 0; mode == 2 && t < 50 && g_k6Wp[sa].type != 0x0D; t++) sa = (int)Rnd(g_k6N);  // car-only: street to street
        for (int t = 0; mode == 2 && t < 50 && g_k6Wp[ga].type != 0x0D; t++) ga = (int)Rnd(g_k6N);
        K6Q q = K6Query(sa, ga, mode, flags);
        if (Rnd(6) == 0)
            for (int a = 0; a < g_k6N; a++) if (g_k6Wp[a].type == 1) { q.opts[1] = K6Ref(g_k6Wp[a]), q.opts[0] |= 0x10; break; }
        q.job = Rnd(2) ? 0xC00 : 0;
        K6Run(g_sf.W, q, &l);
        K6RunCopy(q, &c);
        if (!K6Same(l, c)) { K6Dump("sweep", q, l, c); assert(false); }
        g_k6Plans++, g_k6Mode[mode]++;
        if (l.found) {
            g_k6Found++, g_k6Nodes += l.n;
            for (int i = 0; i < l.n; i++) if ((l.refs[i] & 0xFFFF) == 0xFFFF) { g_k6Water++; break; }
        }
        if (kept < nKeep && l.found && l.n > 2) keep[kept] = q, keepAns[kept++] = c;
    }
    return kept;
}
// ties: the lattice chunk (4 x 4 type 0x10, 4 m apart, edge byte 7) has many shortest paths of exactly equal cost; live ==
// copy on every corner pair, and a third planner on an R whose lattice records list their neighbours in reverse order
// (otherwise the same bytes) must pick another path of the same length in some of them: the tie is decided by record order,
// which the image keeps
static long g_k6Ties, g_k6TieFlip;
static void K6TieCases() {
    static K6Ans l, c, r;
    u8* world = g_sf.world;
    const DWORD rsz = *(DWORD*)(world + 0x64);
    u8* raw = (u8*)A(rsz);
    memcpy(raw, *(u8**)(world + 0x60), rsz);
    for (int a = 0; a < g_k6N; a++) {
        const K6Wp& x = g_k6Wp[a];
        if (x.c != kK6Lat) continue;
        u8* p = raw + x.rec;
        for (int j = 0; j < x.n / 2; j++) {
            WORD* rf = (WORD*)(p + 5);
            const WORD t = rf[j]; rf[j] = rf[x.n - 1 - j], rf[x.n - 1 - j] = t;
            u8* e = p + 5 + 2 * x.n;
            const u8 te = e[j]; e[j] = e[x.n - 1 - j], e[x.n - 1 - j] = te;
        }
    }
    u8* rv = (u8*)A(0x40);
    memcpy(rv, world + 0x54, 0x14);
    *(u8**)(rv + 0xC) = raw;
    u8* wr = (u8*)A(kPcWSz);  // shortcut: shares W''s caches (the search reads none of them)
    memcpy(wr, g_k6Wc, kPcWSz);
    *(u8**)(wr + 0x10) = rv;
    K6Ctr(wr + 0x18, rv);
    int lat = 0;
    while (g_k6Wp[lat].c != kK6Lat) lat++;
    for (int sa = 0; sa < 16; sa++)
        for (int ga = 0; ga < 16; ga++) {
            const int di = abs(sa % 4 - ga % 4), dj = abs(sa / 4 - ga / 4);
            if (!di || !dj) continue;
            for (DWORD mode = 1; mode <= 4; mode += 3) {
                const K6Q q = K6Query(lat + sa, lat + ga, mode, 0);
                K6Run(g_sf.W, q, &l);
                K6RunCopy(q, &c);
                K6Run(wr, q, &r);
                if (!K6Same(l, c)) { K6Dump("tie", q, l, c); assert(false); }
                assert(l.found && l.n == di + dj + 1 && r.found && r.n == l.n);  // shortest on the lattice either way
                g_k6Ties++;
                g_k6TieFlip += memcmp(l.refs, r.refs, l.n * 4) != 0;
            }
        }
}

// --- K6E (docs/142 §9): [RouteEarlyOut] Mode 2 against the game's own world A* on the K6 world. Per random pair (modes and
// flags as K6Sweep): the game's answer with no hook (result, *best, opts +0x64..+0xBB, closed bits), then the same call through
// EoAsHook in act mode (no shadows) inside a request: an acted answer must be identical, a reachable goal never acted. Memo: the
// hook's failed search answers a second goal with the same opts. Stale: an R flags byte flipped in place after the memo: the
// memo is not used (eoStale) and the game answers. ---
struct K6EAns { DWORD r, best, closedN; u8 ctx[0x58]; u8 closed[1024]; };
static long g_k6eAct[2], g_k6eSame, g_k6eReach, g_k6eStale, g_k6eCarFail, g_k6eMemoTry;
static void K6ERun(const K6Q& q, DWORD g, bool hook, K6EAns* a) {
    __declspec(align(16)) DWORD o[64];  // the circle prepass 0x673600 stores the centre with movaps at opts+0x90
    memcpy(o, q.opts, sizeof(o));
    u8* Wp = g_sf.W;
    o[0x22] = (DWORD)(uintptr_t)Wp;
    g_k6Job = q.job;
    DWORD s = q.s, best = 0;
    u8* c = Wp + 0x18;
    const u8 *R = *(u8**)(Wp + 0x10), *G = *(u8**)(Wp + 0x14);
    a->r = hook ? EoAsHook(c, NULL, R, G, &s, &g, q.mode, (u8*)o, &best)
                : (DWORD)(uintptr_t)kK6Search(c, NULL, R, G, &s, &g, q.mode, o, &best);  // shortcut: path vectors leak (test)
    a->r = a->r != 0;
    a->best = best;
    memcpy(a->ctx, (u8*)o + 0x64, sizeof(a->ctx));
    a->closedN = *(DWORD*)(c + 8);
    assert(a->closedN <= sizeof(a->closed));
    memcpy(a->closed, *(u8**)(c + 0x10), a->closedN);
}
static void K6ECheck(const char* what, const K6Q& q, DWORD g, const K6EAns& x, const K6EAns& y) {
    if (x.r == y.r && x.best == y.best && x.closedN == y.closedN && !memcmp(x.ctx, y.ctx, sizeof(x.ctx)) && !memcmp(x.closed, y.closed, x.closedN)) {
        g_k6eSame++;
        return;
    }
    printf("K6E MISMATCH %s: %08lX -> %08lX mode %lu flags %lX: game %lu best %08lX, early-out %lu best %08lX, context %s, closed %s\n", what,
           q.s, g, q.mode, q.opts[0], x.r, x.best, y.r, y.best, memcmp(x.ctx, y.ctx, sizeof(x.ctx)) ? "DIFFERS" : "same",
           x.closedN == y.closedN && !memcmp(x.closed, y.closed, x.closedN) ? "same" : "DIFFER");
    assert(false);
}
static void K6EarlyOut(int pairs) {
    Ts3nCounters& k = ts3native_counters;
    static K6EAns ref, act;
    const u8* raw = *(u8**)(*(u8**)(g_sf.W + 0x10) + 0xC);
    for (int p = 0; p < pairs; p++) {
        const DWORD mode = 1 + Rnd(4);
        DWORD flags = 0;
        if (Rnd(5) == 0) flags |= 0x40;
        if ((mode == 1 || mode == 4) && Rnd(3)) flags |= 0x80;
        if ((flags & 0x80) && Rnd(2)) flags |= 0x100;
        if (Rnd(5) == 0) flags |= 0x200;
        int sa = (int)Rnd(g_k6N), ga = (int)Rnd(g_k6N);
        for (int t = 0; mode == 2 && t < 50 && g_k6Wp[sa].type != 0x0D; t++) sa = (int)Rnd(g_k6N);
        for (int t = 0; mode == 2 && t < 50 && g_k6Wp[ga].type != 0x0D; t++) ga = (int)Rnd(g_k6N);
        K6Q q = K6Query(sa, ga, mode, flags);
        q.job = Rnd(2) ? 0xC00 : 0;
        K6ERun(q, q.g, false, &ref);
        EoReq saved;
        EoBegin(saved);
        LONG a0 = k.eoActed;
        K6ERun(q, q.g, true, &act);
        K6ECheck("first", q, q.g, ref, act);
        if (ref.r) { assert(k.eoActed == a0); g_k6eReach++; }
        else if (k.eoActed != a0) g_k6eAct[0]++;
        else if (mode == 2) g_k6eCarFail++;  // car-only failures the act path did not answer (start = goal, refused)
        // memo: the hook's search failed and was unrestricted: a second goal with the same opts
        if (!ref.r && mode != 2 && t_eo.memo) {
            g_k6eMemoTry++;
            const DWORD g2 = K6Ref(g_k6Wp[Rnd(g_k6N)]);
            K6ERun(q, g2, false, &ref);
            a0 = k.eoActed;
            const LONG m0 = k.eoActedWhy[1];
            K6ERun(q, g2, true, &act);
            K6ECheck("memo", q, g2, ref, act);
            if (ref.r) { assert(k.eoActed == a0); g_k6eReach++; }
            if (k.eoActedWhy[1] != m0) {
                g_k6eAct[1]++;
                // stale epoch: a record's flags byte (blocked bit) flipped in place since the memo: the game answers
                u8* f = (u8*)raw + g_k6Wp[Rnd(g_k6N)].rec + 1;
                *f ^= 0x80;
                const LONG s0 = k.eoStale[1];
                K6ERun(q, g2, false, &ref);
                a0 = k.eoActed;
                K6ERun(q, g2, true, &act);
                *f ^= 0x80;
                K6ECheck("stale", q, g2, ref, act);
                assert(k.eoActed == a0 && k.eoStale[1] == s0 + 1);
                g_k6eStale++;
            }
        }
        EoEnd(saved, 0);
    }
}

// --- K8 (docs/94 §13.3): the route planner above the world search, PathPlan_WithRetries 0x661B50 -> PathPlanner_PlanHierarchical
// 0x65E0D0 (lot lookups, start/goal heights, closest waypoints 0x65BB90, lot exits 0x654CC0, lot legs LotPlan 0x6410B0 ->
// LotPlan_Inner 0x63F6E0 / LotSearch_Entry 0x640E00, the world A*, water legs, the node passes 0x64E380), called as
// RoutingJob_Execute calls it (0x81BF33..0x81BF79) once on the live W and once on PcAcquire's W' with the live routing data
// on PAGE_NOACCESS pages. The world manager, terrain, physics and partition (room) queries are fakes (K8Sites). ---
typedef DWORD(__fastcall* K8PlanFn)(u8* W, void*, u8* job, u8* starts, const float* goal, float prio, u8* goals, u8** path,
                                    DWORD* sIdx, DWORD* gIdx, DWORD flags, void* ids, DWORD mode, DWORD a12);  // ret 0x30
static const K8PlanFn kK8Plan = (K8PlanFn)(uintptr_t)0x00661B50;
struct K8Lot { u8* obj; float x0, z0, x1, z1; };  // a world manager lot object and its world rectangle
static K8Lot g_k8Lots[16];
static int g_k8NLots;
static long g_k8Calls[32];  // fake calls by kind (what the planner asked the world manager / physics)
static u8* __fastcall K8LotAt(void*, void*, float x, float z) {
    g_k8Calls[0]++;
    for (int i = 0; i < g_k8NLots; i++)
        if (x >= g_k8Lots[i].x0 && x < g_k8Lots[i].x1 && z >= g_k8Lots[i].z0 && z < g_k8Lots[i].z1) return g_k8Lots[i].obj;
    return NULL;
}
static float __fastcall K8Terrain(void*, void*, float, float, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD) { g_k8Calls[1]++; return 0.0f; }
static float __fastcall K8HeightAt(void*, void*, float, float, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD) { g_k8Calls[2]++; return 0.0f; }
static float __fastcall K8Sea(void*, void*) { g_k8Calls[3]++; return -100.0f; }
static void* __fastcall K8Pond(void*, void*, float, float, DWORD) { g_k8Calls[4]++; return NULL; }
static char __fastcall K8Routable(void*, void*, float, float) { g_k8Calls[5]++; return 1; }
static char __fastcall K8Routable2(void*, void*, float, float) { g_k8Calls[6]++; return 1; }
static char __fastcall K8B87D30(void*, void*, float, float, DWORD, DWORD) { g_k8Calls[7]++; return 0; }
// the world manager's lot near a point (x, z, radius; ret 0xC): its lot grid (+0x1D0 cell size) is not built here
static u8* __fastcall K8LotNear(void*, void*, float x, float z, float r) {
    g_k8Calls[13]++;
    for (int i = 0; i < g_k8NLots; i++)
        if (x + r >= g_k8Lots[i].x0 && x - r < g_k8Lots[i].x1 && z + r >= g_k8Lots[i].z0 && z - r < g_k8Lots[i].z1) return g_k8Lots[i].obj;
    return NULL;
}
static char __fastcall K8TerrObj(void*, void*, float, float, DWORD, DWORD, void*, DWORD, DWORD) { g_k8Calls[12]++; return 0; }
static char __fastcall K8TerrOk(void*, void*, float, float, DWORD) { g_k8Calls[14]++; return 1; }  // terrain +0x5C point test 0xB7E020
static char __fastcall K8RayV(void*, void*, float, float, void*) { g_k8Calls[8]++; return 0; }
static u8 g_k8Phys[0x40];
static u8* __cdecl K8PhysGet() { g_k8Calls[9]++; return g_k8Phys; }
static WORD __fastcall K8RoomAt(void*, void*, const float*, DWORD, DWORD) { g_k8Calls[10]++; return 0; }  // partition: outside
// the world manager's segment-to-lot query (10 args, ret 0x28): clears the two out bytes args 5 and 8 first (0xC697B3,
// 0xC697C7); no lot on the segment here
static u8* __fastcall K8SegLot(void*, void*, float, float, float, float, u8* o5, DWORD, DWORD, u8* o8, DWORD, DWORD) {
    g_k8Calls[11]++;
    *o5 = 0, *o8 = 0;
    return NULL;
}
static u8* __fastcall K8FindLot(void*, void*, DWORD lo, DWORD, DWORD);
// Lot (world manager object) methods: sea level relative to the lot 0xAB3380 (through its vtable +0x64 position)
static float __fastcall K8LotSea(void*, void*) { g_k8Calls[16]++; return -100.0f; }
// the lot's terrain / structure query at a point (cdecl lot, point, 2 outs; 0xB01470 from the node height pass 0x6415F0): none
static char __cdecl K8LotPoint(void*, const void*, void*, void*) { g_k8Calls[17]++; return 0; }
static RkSite g_k8Sites[] = {
    {0x00B01470, (const void*)K8LotPoint},
    {0x00AB3380, (const void*)K8LotSea},
    {0x00C62D40, (const void*)K8FindLot},  {0x00C69790, (const void*)K8SegLot},   {0x00C68B00, (const void*)K8LotNear}, {0x00B7E020, (const void*)K8TerrOk},   {0x00B793F0, (const void*)K8TerrObj},
    {0x00C68990, (const void*)K8LotAt},    {0x00C6A540, (const void*)K8Terrain}, {0x00C6A250, (const void*)K8HeightAt},
    {0x00C61670, (const void*)K8Sea},      {0x00C60E70, (const void*)K8Pond},    {0x00C6BAB0, (const void*)K8Routable},
    {0x00C6BBC0, (const void*)K8Routable2}, {0x00B87D30, (const void*)K8B87D30}, {0x00686CC0, (const void*)K8RayV},
    {0x00686A90, (const void*)K8PhysGet},  {0x00B499D0, (const void*)K8RoomAt},
};
struct K8Ans { DWORD ret, sIdx, gIdx; int n; u8 nodes[512][64]; };
struct K8Q { float s[3], g[3]; DWORD mode, flags; };
static DWORD g_k8Fault, g_k8FaultEip;
static long g_k8Plans, g_k8Found, g_k8Nodes, g_k8Guarded, g_k8Ret[16];
// one plan: the job parameter block (0x30, as 0x81B4C0..0x81B534 builds it: +0 the AGS / boat word job+0xC0, +0x28 the
// RoutingOptions object job+0x40, test 0x81C880 reads +0x28..+0x37), one start record (0x20, as 0x6563E0 copies it: x, z, level,
// direction, -999, 0, index) and one goal record (0x1C, 0x62A8B0: x, z, level, direction, index, priority)
// fail fast: a plan that runs 10 s is a hang (a loop on fake data); the watchdog names where the main thread is
static volatile ULONGLONG g_k8Since;
static HANDLE g_k8Main;
static DWORD WINAPI K8Watch(void*) {
    for (;;) {
        Sleep(500);
        const ULONGLONG t = g_k8Since;
        if (!t || GetTickCount64() - t < 10000) continue;
        SuspendThread(g_k8Main);  // no CRT call from here on: the main thread may hold a CRT lock
        CONTEXT c;
        c.ContextFlags = CONTEXT_FULL;
        GetThreadContext(g_k8Main, &c);
        char b[512];
        static const char kHead[] = "K8 FAIL: a plan ran 10 s (hang): eip, stack:";
        int n = sizeof(kHead) - 1;
        memcpy(b, kHead, n);
        auto hex = [&](DWORD v) { b[n++] = ' '; for (int d = 7; d >= 0; d--) b[n++] = "0123456789ABCDEF"[(v >> (4 * d)) & 15]; };
        hex(c.Eip);
        const DWORD* sp = (const DWORD*)(uintptr_t)c.Esp;
        for (int i = 0, k = 0; i < 2048 && k < 24; i++)
            if (sp[i] >= 0x00401000 && sp[i] < 0x00F95000) { hex(sp[i]); k++; }
        b[n++] = 10;
        DWORD w;
        WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), b, (DWORD)n, &w, NULL);
        ExitProcess(3);
    }
}
// I-94-322: the planner's search options (stack struct at 0x65E828, ctor 0x652BF0) leave the start / goal lot hub refs +4 / +0x34
// unset for a start / goal on open ground, and the cost functions compare a hub with +0x34 without testing flag 0x20
// (RouteCost_Mode1 0x674F7B, Mode3 0x67438C / 0x6745A6, Mode4 0x675989 / 0x6759F0): an answer then depends on the stack an
// earlier call left. Both runs get a cleared stack below K8Run, so live and copy compare the planner, not two stack histories
// 0.34.1 [OptsInit] check: with g_k8Dirty the copy side keeps the stack earlier calls left (live cleared), as two threads'
// histories would; with the options ctor patch (optsinit.cpp) every plan must still match
static bool g_k8Dirty;
static bool g_k8Rjp;  // STEERRO_PHASES=1 (docs/150 §7 fix): every K8 plan runs as a [RouteJobPhases] job of this thread
static bool g_k8Sp;   // STEERRO_SYNCPHASES=1 (docs/164): every K8 plan runs as a SyncPlans session of this thread instead
// STEERRO_PLANDEADLINE=1 (docs/164 §7): K8 plans by a hash of the query (the same for the live and the copy run) in a
// [RouteAsync] PlanDeadlineMs session already past its deadline: class 0 none, 1 everything (world A* budget deadline + cell +
// water stubs), 2 cell A* stub only, 3 water stub only; live and copy must still agree, per class found / skips counted
static bool g_k8Pd;
static long g_k8PdN[4], g_k8PdFound[4], g_k8PdWater[4], g_k8PdCapped;
static int K8PdClass(const void* s, const void* g, DWORD flags) {
    const DWORD h = *(const DWORD*)s * 2654435761u ^ *(const DWORD*)g * 40503u ^ flags * 2246822519u;
    return (int)((h >> 9) & 3);
}
static int g_k8PdCur;
static __declspec(noinline) void K8PdBegin(const void* s, const void* g, DWORD flags) {
    if (t_pd.open) PdClose(false);  // a copy run that faulted inside the planner left its session open
    if ((g_k8PdCur = K8PdClass(s, g, flags)) == 0) return;
    PdOpen(1, 60000000);
    t_pd.dl = 1;  // already past
    g_pdSkipMask = g_k8PdCur == 1 ? 3 : g_k8PdCur == 2 ? 1 : 2;
    if (g_k8PdCur == 1) { const Budget b = {1, 0, {}, 0, 0, 0, 1, 0}; t_budget = b; }  // CallPlanBudget's Budget with the deadline
}
static __declspec(noinline) void K8PdEnd(DWORD ret) {
    if (!g_k8PdCur) return;
    if (g_k8PdCur == 1 && t_budget.capped) g_k8PdCapped++;
    const Budget z = {};
    t_budget = z;
    PdClose(ret == 0);
}
static __declspec(noinline) void K8ZeroStack(DWORD fill = 0) {
    volatile DWORD z[16 * 1024];
    for (int i = 0; i < 16 * 1024; i++) z[i] = fill;
}
static void K8Run(u8* Wp, u8* world, const K8Q& q, K8Ans* a) {
    *(u8**)(uintptr_t)0x011D9128 = world;  // RoutingWorld_Get: the copy in a clone run (T4)
    g_k8Since = GetTickCount64();
    static u8 opts[0x40];
    __declspec(align(16)) u8 job[0x40] = {};
    *(u8**)(job + 0x28) = opts;
    __declspec(align(16)) float st[8] = {q.s[0], q.s[1], 0, 1, 0, -999.0f, 0, 0};
    *(DWORD*)&st[2] = *(const DWORD*)&q.s[2], *(DWORD*)&st[7] = 0;
    __declspec(align(16)) float gl[7] = {q.g[0], q.g[1], 0, 1, 0, 0, 0};
    *(DWORD*)&gl[2] = *(const DWORD*)&q.g[2];
    u8 sv[16], gv[16];
    *(float**)sv = st, *(float**)(sv + 4) = *(float**)(sv + 8) = st + 8, *(DWORD*)(sv + 12) = 0x00FED4BC;
    *(float**)gv = gl, *(float**)(gv + 4) = *(float**)(gv + 8) = gl + 7, *(DWORD*)(gv + 12) = 0x010212BC;
    const float goal[3] = {q.g[0], q.g[1], q.g[2]};
    u8* path = NULL;
    DWORD si = 0, gi = 0xFFFFFFFF;  // *gIdx < 0: no preselected goal record, the goal point (arg 3) is used (0x65EAB8)
    if (!g_k8Dirty || Wp == g_sf.W) K8ZeroStack(0);  // dirty: the copy side keeps what earlier calls left (the I-94-322 shape)
    if (g_k8Rjp) RjpJobBegin(0);
    // docs/159: the door stub sees K8's plan as a worker job (DoorMemo=2 shadow: every repeat asked and compared)
    const DWORD k8Worker = g_workerTid;
    if (g_k8Rjp) t_hcJob = ++g_hcJobSeq, g_workerTid = GetCurrentThreadId();
    if (g_k8Sp) {
        if (t_syp.open) SypEnd();  // a copy run that faulted inside the planner left its session open
        static DWORD spSlot;
        if (!SypBegin(SYP_IC, &spSlot)) assert(!"SyncPlans session refused");
    }
    if (g_k8Pd) K8PdBegin(q.s, q.g, q.flags);  // out of line: K8Run's frame (the dirty-stack check reads it) stays as it was
    a->ret = kK8Plan(Wp, NULL, job, sv, goal, 0.0f, gv, &path, &si, &gi, q.flags, NULL, q.mode, 0);
    if (g_k8Pd) K8PdEnd(a->ret);
    if (g_k8Sp) SypEnd();
    if (g_k8Rjp) t_hcJob = 0, g_workerTid = k8Worker;
    if (g_k8Rjp) RjpJobEnd(0);
    g_k8Since = GetTickCount64();
    a->sIdx = si, a->gIdx = gi, a->n = 0;
    if (path) {  // shortcut: the path object is not released (test process)
        const u8* b = *(u8**)(path + 8);
        const u8* e = *(u8**)(path + 0xC);
        a->n = b ? (int)((e - b) / 64) : 0;
        assert(a->n <= 512);
        if (a->n) memcpy(a->nodes, b, a->n * 64);
    }
}
static int K8RunCopyTry(const K8Q& q, K8Ans* a) {
    __try {
        K8Run(g_k6Wc, g_pcWork.world, q, a);
        return 0;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION
                    ? (g_k8Fault = (DWORD)GetExceptionInformation()->ExceptionRecord->ExceptionInformation[1],
                       g_k8FaultEip = (DWORD)GetExceptionInformation()->ContextRecord->Eip, EXCEPTION_EXECUTE_HANDLER)
                    : EXCEPTION_CONTINUE_SEARCH) {
        return 1;
    }
}
static u8* g_k8Arena;  // the live replay world (RfAlloc's arena, K8Build)
static const DWORD kK8ArenaSz = 8 << 20;
static void K8Guard(bool on) {
    DWORD old;
    K6Guard(on);
    assert(VirtualProtect(g_sfPages, 0x2000, on ? PAGE_NOACCESS : PAGE_READWRITE, &old));
    if (g_k8Arena) assert(VirtualProtect(g_k8Arena, kK8ArenaSz, on ? PAGE_NOACCESS : PAGE_READWRITE, &old));
}
static void K8RunCopy(const K8Q& q, K8Ans* a) {
    K8Guard(true);
    const int f = K8RunCopyTry(q, a);
    K8Guard(false);
    if (f) {
        printf("K8 FAIL: a plan on the working copy touched live memory at %08lX (eip %08lX)\n", g_k8Fault, g_k8FaultEip);
        assert(false);
    }
    g_k8Guarded++;
}
static bool K8Same(const K8Ans& x, const K8Ans& y) {
    if (x.ret != y.ret || x.sIdx != y.sIdx || x.gIdx != y.gIdx || x.n != y.n) return false;
    // PathNode_Init 0x6412F0 leaves +0x36 (u16) and +0x3C unset: stale stack in both runs (docs/98 §10.1)
    for (int i = 0; i < x.n; i++) if (memcmp(x.nodes[i], y.nodes[i], 0x36) || memcmp(x.nodes[i] + 0x38, y.nodes[i] + 0x38, 4)) return false;
    return true;
}
static void K8Dump(const char* what, const K8Q& q, const K8Ans& l, const K8Ans& c) {
    printf("K8 MISMATCH %s: (%.2f %.2f %ld) -> (%.2f %.2f %ld) mode %lu flags %lX: ret %lu/%lu idx %lu,%lu/%lu,%lu nodes %d/%d\n", what,
           q.s[0], q.s[1], *(const LONG*)&q.s[2], q.g[0], q.g[1], *(const LONG*)&q.g[2], q.mode, q.flags, l.ret, c.ret, l.sIdx, l.gIdx,
           c.sIdx, c.gIdx, l.n, c.n);
    int shown = 0;
    for (int i = 0; i < l.n && i < c.n && shown < 12; i++)
        for (int o = 0; o < 0x3C; o += 4)
            if (memcmp(l.nodes[i] + o, c.nodes[i] + o, o == 0x34 ? 2 : 4) && shown++ < 12)
                printf("  node %d +0x%02X: live %08lX (%g) copy %08lX (%g)\n", i, o, *(const DWORD*)(l.nodes[i] + o), *(const float*)(l.nodes[i] + o),
                       *(const DWORD*)(c.nodes[i] + o), *(const float*)(c.nodes[i] + o));
}
// the live W as the game makes it: K6's W holds snap-fake.h's random tuning bytes and no FindClosest object, enough for the
// world A* only. Here the game's ctor 0x657C50 (its container ctor 0x673C70 stubbed: it registers tuning variables by name)
// gives +0 and +0x88..+0x197 (caches, FindClosest list, lot-exit masks, BestLotEdgePortals sets); K6Build's P, R, G, container,
// RNG word and group hash stay. Then the FindClosest init 0x652C50 as the W init 0x654101 calls it (P and W set first: no
// AddRef through them)
static u8* __fastcall K8NoCtr(u8* c, void*) { return c; }
static void K8LiveW(u8* Wl) {
    u8 saved[5];
    memcpy(saved, (void*)(uintptr_t)0x00673C70, 5);
    Jmp(0x00673C70, (const void*)K8NoCtr);
    __declspec(align(16)) u8 w[kPcWSz];
    memset(w, 0, sizeof(w));
    ((void(__fastcall*)(u8*, void*))(uintptr_t)0x00657C50)(w, NULL);
    memcpy((void*)(uintptr_t)0x00673C70, saved, 5);
    const DWORD rng = *(DWORD*)(Wl + 0x9C);
    u8 grp[0x10];
    memcpy(grp, Wl + 0xA0, 0x10);
    memcpy(Wl, w, 4);
    memcpy(Wl + 0x88, w + 0x88, kPcWSz - 0x88);
    *(DWORD*)(Wl + 0x9C) = rng;
    memcpy(Wl + 0xA0, grp, 0x10);
    *(LONG*)(Wl + 4) = 1;
    *(u8**)(Wl + 0xE0) = *(u8**)(Wl + 0xE4) = Wl + 0xE0;  // the FindClosest list anchor (the ctor linked it to its own buffer)
    u8* P = *(u8**)(Wl + 8);
    *(u8**)(Wl + 0xEC) = P, *(u8**)(Wl + 0xF0) = Wl;
    ((PcFcInitFn)(uintptr_t)0x00652C50)(Wl + 0xE0, NULL, *(DWORD*)(Wl + 0xC), Wl, P, *(u8**)(Wl + 0x10));
}
// object-footprint rings: the planner's ring cache 0x6580F0 walks a type-0x0E waypoint's first-neighbour chain until it is back
// at the start (K6 filled only chunks whose chains close). K6's R leaves some chains open, which the game never builds: K8
// retypes those waypoints to 0x10 (walk) in the records
static int K8FixRings() {
    int n = 0;
    u8* raw = *(u8**)(g_sf.world + 0x60);
    for (int a = 0; a < g_k6N; a++)
        if (g_k6Wp[a].type == 0x0E && !(g_k6Wp[a].n && K6Cycle(a))) {
            g_k6Wp[a].type = 0x10;
            raw[g_k6Wp[a].rec + 1] = (u8)((raw[g_k6Wp[a].rec + 1] & 0xE0) | 0x10);
            n++;
        }
    return n;
}
// the K8 world: K6's routing world (world object and P pages, R, G, W, world manager) with the REPLAY world's planner P and its
// lots (game quadtrees, footprints, portals, rooms) in place of snap-fake.h's P: world+0x95C = W+8 = the replay P, the snapshot's
// lot strategy the replay (SpCut -> RpBegin -> RpCut on this world), so PcAcquire's clone P holds the replay clone's lots. The
// live replay world comes from an arena (RfAlloc) that K8Guard makes no-access with the rest of the live routing data. Lots
// 0 and 1 sit at world (0, 0) and (64, 64) (64 x 64 m each, lot -> world translation in the transforms g+0xA0 / g+0xE0); the
// other lots of the table have no world manager lot object (never looked up)
static __declspec(align(16)) u8 g_k8LotObj[2][0x600];  // 16-aligned: Lot_WorldToLocal2D 0xAB854F reads +0x190 with movaps
static u8 g_k8Part[0x100];  // a lot's partition object (+0x298): only handed to the room fake
static u8* __fastcall K8FindLot(void*, void*, DWORD lo, DWORD, DWORD) {
    g_k8Calls[15]++;
    for (int i = 0; i < g_k8NLots; i++) if (*(DWORD*)(g_k8Lots[i].obj + 0x48) == lo) return g_k8Lots[i].obj;
    return NULL;
}
static u8* K8Portal() {  // a portal object (game vtable 0xFED048): shared with the clone by design (H20), so not on the arena
    u8* const a = g_rfArena;
    g_rfArena = NULL;
    u8* p = RkPortal();
    g_rfArena = a;
    return p;
}
// R and the lot graph for lot l's exits, in the layouts the planner reads [verified in the disassembly unless marked]:
// - R (docs/99 §1.5, §1.6): a walk waypoint inside the lot becomes the lot's hub (type 1) and its 2-4 neighbours the lot-edge
//   waypoints (type 2, each with at least 4 neighbours: the exit gather skips a type-2 waypoint with 3 or fewer, 0x654FDC);
//   edge bytes as the game's: hub <-> lot-edge 1, lot-edge <-> lot-edge of the same lot 2, the rest 7; each lot-edge record
//   lists its hub first (0x650030 maps a lot-edge waypoint to its lot through the first neighbour's position, World_LotLookup);
//   a group-hash entry W+0xA0 for each lot-edge waypoint (as K6Build gives every type-2 waypoint)
// - rooms g+0x90 (0x44-byte records, indexed by room id; the outside room of level 0 is id 4 = level + 4, Lot_RoomIdAtPoint
//   0x624780 with no partition room) with g+0x16C = 4; the outside room's portal vector +4 holds the exits, its lists +0x24 /
//   +0x34 built by the game's RoomGraph_FirstUse 0x63D2F0 (live, before the first cut)
// - each exit portal (vtable 0xFED048, count +0x70): +0x14 / +0x18 / +0x1C lot-local point and level on the room side, +0x20 /
//   +0x24 / +0x28 the other side, +0x48 / +0x4A the room ids (0xFFFF: none, 0x633EA0), +0x4C the R reference (bits 0-13 chunk,
//   14-23 waypoint; 28-30 flags), +0x50 / +0x54 world point, +0x58 bit 1 enabled (0x654E59), +0x34 kind (node +0x2C) [inferred
//   from LotPath_AddPortalNodes' node writes, docs/99 §3]
// - g+0x120: hash (buckets +4, count +8, size +0xC; node key, value byte, next +8; find 0xD38B00) from a lot-edge waypoint key
//   (chunk << 16 | waypoint) to its portal's index in the outside room's list, bounded by the byte g+0x10 (0x653082); g+8 the
//   lot's hub reference (chunk << 16 | waypoint, LotTable_HubRef 0x631BB0)
static int g_k8Exits;
static void K8LotExits(int l) {
    u8* g = g_rf.lots[l] + 0x10;
    const K8Lot& lr = g_k8Lots[l];
    u8* raw = *(u8**)(g_sf.world + 0x60);
    auto inLot = [&](const K6Wp& w) { float x, z; K6Pos(w, &x, &z); return x >= lr.x0 + 1 && x < lr.x1 - 1 && z >= lr.z0 + 1 && z < lr.z1 - 1; };
    auto walk = [&](const K6Wp& w) { return (w.type == 0x10 || w.type == 0x11 || w.type == 0x07) && !w.noEdge && w.c != kK6Lat; };
    int hub = -1;
    // the hub: a walk waypoint inside the lot with a walk neighbour of 4+ neighbours (an exit); its other neighbours (other
    // types) are unlinked both ways, so every hub link is a hub <-> lot-edge link
    auto unlink = [&](int a, int b) {  // drop b from a's record (refs then edge bytes, compacted) and its mirror
        K6Wp& w = g_k6Wp[a];
        u8* p = raw + w.rec;
        int at = 0;
        while (at < w.n && w.nb[at] != b) at++;
        if (at == w.n) return;
        u8 eb[24];
        memcpy(eb, p + 5 + 2 * w.n, w.n);
        for (int k = at; k + 1 < w.n; k++) ((WORD*)(p + 5))[k] = ((WORD*)(p + 5))[k + 1], w.nb[k] = w.nb[k + 1], w.eb[k] = w.eb[k + 1], eb[k] = eb[k + 1];
        w.n--, p[0] = (u8)w.n;
        memcpy(p + 5 + 2 * w.n, eb, w.n);
    };
    int best = 0;  // the hub with the most exits
    for (int a = 0; a < g_k6N; a++) {
        const K6Wp& h = g_k6Wp[a];
        if (!inLot(h) || !walk(h) || h.n < 2) continue;
        bool ok = true;
        int wide = 0;
        for (int k = 0; k < h.n && ok; k++) {
            const K6Wp& e = g_k6Wp[h.nb[k]];
            if (!walk(e)) continue;  // unlinked below
            wide += e.n >= 4;
            for (int j = 0; j < e.n && ok; j++) ok = g_k6Wp[e.nb[j]].type != 1;  // not next to another lot's hub
        }
        if (ok && wide > best) hub = a, best = wide;
    }
    if (hub >= 0)
        for (int k = g_k6Wp[hub].n - 1; k >= 0; k--) {
            const int b = g_k6Wp[hub].nb[k];
            if (!walk(g_k6Wp[b])) unlink(hub, b), unlink(b, hub);
        }
    int ex[4], n = 0;
    if (hub >= 0) {
        K6Wp& h = g_k6Wp[hub];
        h.type = 1;
        raw[h.rec + 1] = (u8)((raw[h.rec + 1] & 0xE0) | 1);
        int le[24], nl = 0;  // every hub neighbour is a lot-edge waypoint; those with 4+ neighbours get an exit portal
        for (int k = 0; k < h.n; k++) h.eb[k] = 1, raw[h.rec + 5 + 2 * h.n + k] = 1, le[nl++] = h.nb[k];
        for (int i = 0; i < nl; i++) {
            K6Wp& e = g_k6Wp[le[i]];
            if (e.n >= 4 && n < 4) ex[n++] = le[i];
            e.type = 2;
            u8* p = raw + e.rec;
            p[1] = (u8)((p[1] & 0xE0) | 2);
            int at = 0;
            while (e.nb[at] != hub) at++;
            if (at) {  // the hub first (record and mirror)
                const WORD r = ((WORD*)(p + 5))[0];
                ((WORD*)(p + 5))[0] = ((WORD*)(p + 5))[at], ((WORD*)(p + 5))[at] = r;
                const int t = e.nb[0]; e.nb[0] = e.nb[at], e.nb[at] = t;
            }
            for (int k = 0; k < e.n; k++) {
                bool sib = false;
                for (int j = 0; j < nl; j++) sib |= e.nb[k] == le[j];
                e.eb[k] = k == 0 ? 1 : sib ? 2 : 7;
                p[5 + 2 * e.n + k] = e.eb[k];
            }
            DWORD* gk = *(DWORD**)(g_sf.W + 0xA4);  // the live group hash: one more entry
            DWORD* nd = (DWORD*)K6L(16);
            nd[0] = (DWORD)e.c << 16 | (DWORD)e.i, nd[1] = 0x1000 + (DWORD)l, nd[2] = gk[nd[0] % 5];  // one group per lot [inferred]
            gk[nd[0] % 5] = (DWORD)(uintptr_t)nd;
            (*(DWORD*)(g_sf.W + 0xAC))++;
        }
    }
    // rooms: 5 records, the outside room 4 with the exits
    const int nr = 4 + (int)(*(u8***)(g + 0x78) - *(u8***)(g + 0x74));  // ids 0-3 unused, 4 + level per quadtree level
    u8* rooms = RfVec(g + 0x90, nr * 0x44);
    memset(rooms, 0, nr * 0x44);
    for (int r = 0; r < nr; r++) RfVec(rooms + r * 0x44 + 4, r == 4 ? 4 * n : 0), RfVec(rooms + r * 0x44 + 0x14, 0),
                                 RfVec(rooms + r * 0x44 + 0x24, 0), RfVec(rooms + r * 0x44 + 0x34, 0);
    *(WORD*)(g + 0x16C) = 4;
    {  // +0x34: one 16-byte list per portal, as LotGraph_Build sizes it (FirstUse fills them; an empty +0x34 builds nothing, 0x63D384)
        u8* lists = RfVec(rooms + 4 * 0x44 + 0x34, 16 * n);
        for (int i = 0; i < n; i++) memset(lists + 16 * i, 0, 12), *(DWORD*)(lists + 16 * i + 12) = 0x00FEAEE0;
    }
    u8** pv = *(u8***)(rooms + 4 * 0x44 + 4);
    DWORD* bk = (DWORD*)RfAlloc(4 * 4);
    bk[0] = bk[1] = bk[2] = 0, bk[3] = 0x2468ACE1;  // 3 buckets and the end marker
    for (int i = 0; i < n; i++) {
        const K6Wp& w = g_k6Wp[ex[i]];
        float x, z;
        K6Pos(w, &x, &z);
        u8* p = K8Portal();
        *(LONG*)(p + 0x70) = 1000;
        *(DWORD*)(p + 8) = 0x7700 + 4 * l + i;
        *(float*)(p + 0x14) = x - lr.x0, *(float*)(p + 0x18) = z - lr.z0;
        *(float*)(p + 0x20) = x - lr.x0, *(float*)(p + 0x24) = z - lr.z0;
        *(WORD*)(p + 0x48) = 4, *(WORD*)(p + 0x4A) = 0xFFFF;
        *(DWORD*)(p + 0x4C) = (DWORD)w.c | (DWORD)w.i << 14;
        *(float*)(p + 0x50) = x, *(float*)(p + 0x54) = z;
        p[0x58] = 2;
        *(DWORD*)(p + 0x34) = 0xB828;
        pv[i] = p;
        DWORD* nd = (DWORD*)RfAlloc(12);
        nd[0] = (DWORD)w.c << 16 | (DWORD)w.i, nd[1] = (DWORD)i, nd[2] = bk[nd[0] % 3];
        bk[nd[0] % 3] = (DWORD)(uintptr_t)nd;
    }
    *(DWORD**)(g + 0x124) = bk, *(DWORD*)(g + 0x128) = 3, *(DWORD*)(g + 0x12C) = (DWORD)n;
    DWORD* bk2 = (DWORD*)RfAlloc(2 * 4);  // g+0x140, the lot build's second hash (0x63C326): empty, one bucket and the end marker
    bk2[0] = 0, bk2[1] = 0x2468ACE2;
    *(DWORD**)(g + 0x144) = bk2, *(DWORD*)(g + 0x148) = 1, *(DWORD*)(g + 0x14C) = 0;
    g[0x10] = (u8)n;
    *(DWORD*)(g + 8) = n ? ((DWORD)g_k6Wp[g_k6Wp[ex[0]].nb[0]].c << 16 | (DWORD)g_k6Wp[g_k6Wp[ex[0]].nb[0]].i) : 0xFFFFFFFF;
    *(DWORD*)(lr.obj + 0x174) = 64, *(DWORD*)(lr.obj + 0x178) = 64;  // the lot's size in metres (0x654F9D)
    // the outside room built by the game, as a plan's first use builds it
    const char built = g_rpFirstUse(g_rf.p, NULL, g, 4);
    assert(built);
    g_k8Exits += n;
}
static void K8Build(DWORD seed) {
    g_sfPages = (u8*)VirtualAlloc(NULL, 0x2000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);  // live world and P: guarded
    assert(g_sfPages);
    K6Build(seed);
    {  // the world manager W+0xC (= world+0x964, live by design): origin +0xB4 / +0xB8 (water cells), size +0xBC / +0xC0 (the
       // closest-waypoint chunk gather 0x654838 / 0x654896 stops there; 128 m = R's 4 x 4 chunks of 32 m); no terrain object
       // +0x5C, no sea object +0x60, an empty pond list +0x100 / +0x104 (0xC60F50 walks it)
        static u8 wm[0x400];
        memset(wm, 0, sizeof(wm));
        *(float*)(wm + 0xBC) = 128.0f, *(float*)(wm + 0xC0) = 128.0f;
        *(u8**)(g_sf.W + 0xC) = wm, *(u8**)(g_sf.world + 0x964) = wm;
    }
    // the replay world on the arena (the clone is cut later, off the arena)
    if (!g_k8Arena) g_k8Arena = (u8*)VirtualAlloc(NULL, kK8ArenaSz, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    assert(g_k8Arena);
    g_rfArena = g_k8Arena, g_rfArenaUsed = 0, g_rfArenaSz = kK8ArenaSz;
    g_rfMakeQ = RkQ, g_rfMakeFp = RkFp, g_rfIdentity = true, g_rfMakePortal = K8Portal;
    RfSetup(seed, true);
    u8* P = g_rf.p;
    u8* astar = K3Astar();  // the live planner's cAStarSearch: P+0xBC and every live Q's +0x28 (as the P init 0x62E9F1)
    *(u8**)(P + 0xBC) = astar;
    for (int l = 0; l < g_rf.nLots; l++)
        for (u8** q = *(u8***)(g_rf.lots[l] + 0x10 + 0x74); q < *(u8***)(g_rf.lots[l] + 0x10 + 0x78); q++) *(u8**)(*q + 0x28) = astar;
    g_rfArena = NULL, g_rfMakePortal = NULL;
    g_rfWorldSlot = g_sf.world;  // the snapshot's world: this one
    *(u8**)(g_sf.world + 0x95C) = P, *(u8**)(g_sf.W + 8) = P;
    P[0x10C] = 1;  // lot planning on (LotPlan_Inner 0x63F6E9)
    g_k8NLots = 0;
    for (int l = 0; l < 2 && l < g_rf.nLots; l++) {
        u8* g = g_rf.lots[l] + 0x10;
        const float ox = 64.0f * l, oz = 64.0f * l;
        float* xf = (float*)(g + 0xA0);
        xf[12] = -ox, xf[14] = -oz;          // world -> lot: row 3 (0x62ED40)
        xf[16 + 12] = ox, xf[16 + 14] = oz;  // lot -> world (0x62ECD0)
        u8* o = g_k8LotObj[l];
        memset(o, 0, sizeof(g_k8LotObj[l]));
        *(DWORD*)(o + 0x48) = *(DWORD*)g, *(DWORD*)(o + 0x4C) = 0;
        *(u8**)(o + 0x298) = g_k8Part;
        *(float*)(o + 0x190) = ox, *(float*)(o + 0x198) = oz;  // the lot's world origin x / z, angle +0x1A0 = 0 (0xAB8549)
        g_k8Lots[g_k8NLots++] = K8Lot{o, ox, oz, ox + 64.0f, oz + 64.0f};
    }
    K8LiveW(g_sf.W);
    g_sf.W[0x88] = 1;  // R usable: Route_PlanLocked copies world+0x97D there (0x82C753)
    // G's water cell grid (+0x1C, 2 bytes per 4 m cell, +0xC cells): its row length +4 (WaterGrid_CellIndex 0x66D887, the water
    // path 0x6719FA); snap-fake.h leaves it 0 (its checks never index the grid by row)
    *(DWORD*)(g_sf.world + 0x8C + 4) = g_sf.gw;
    K8FixRings();
    g_rfArena = g_k8Arena;
    for (int l = 0; l < g_k8NLots; l++) K8LotExits(l);
    g_rfArena = NULL;
    RfCut();  // the first clone, off the arena
}
// a reader cut as PcCutFn takes it, then PcAcquire on the replay clone (live pages guarded) and W' from 0x829C00 on the copy
static long g_k8Cuts, g_k8Hashes;
static void K8Cut() {
    u8* world = g_sf.world;
    if (g_k6Wc) PcRelease(&g_k6Cl, 0, NULL, 0);
    assert(SpCut(world, true) && SpTilesAll(g_spCur));
    PcCut& c = g_pcCut;
    SpVerRel(c.v);
    assert(PcSnap(c, world, g_rf.p));
    c.v = SpVerRef(g_spCur), c.epoch = g_spEpoch, c.active = PcActiveLot(), c.grid = NULL, c.keys = NULL;
    c.ver = g_rpPub, c.ok = 1;
    RpDrain(false);  // the consumer reaches the cut's position (its own thread in the game, before the worker acquires)
    assert(g_rpTail == c.ver && g_rpClone && g_rpClone->nLots == g_rf.nLots);
    g_rpThreaded = true;  // PcAcquire does not drain (that reads live) under the guard
    K8Guard(true);
    const int why = PcAcquire(c.ver, &g_k6Cl);
    K8Guard(false);
    g_rpThreaded = false;
    if (why >= 0) { printf("K8 FAIL: PcAcquire refused (%d)\n", why); assert(false); }
    g_k6Wc = ((u8*(__fastcall*)(const u8*, void*))(uintptr_t)0x00829C00)(g_pcWork.world, NULL);
    assert(g_k6Wc == g_pcWork.wp && *(u8**)(g_k6Wc + 8) == g_rpClone->p);
    // I-94-321: every clone lot's two hashes g+0x120 / g+0x140 are private (own bucket arrays and nodes), chains equal to live
    for (int l = 0; l < g_rf.nLots; l++) {
        const u8* lg = g_rf.lots[l] + 0x10;
        const RpLot* cl = RpFindLot(g_rpClone, *(const DWORD*)lg);
        assert(cl && cl->node);
        const u8* cg = cl->node + 0x10;
        for (int h = 0; h < 2; h++) {
            const DWORD* lb = *(const DWORD* const*)(lg + 0x124 + 0x20 * h);
            const DWORD* cb = *(const DWORD* const*)(cg + 0x124 + 0x20 * h);
            const DWORD nb = *(const DWORD*)(lg + 0x128 + 0x20 * h);
            if (!lb) { assert(!cb); continue; }
            assert(cb != lb && *(const DWORD*)(cg + 0x128 + 0x20 * h) == nb && cb[nb] == lb[nb]);
            for (DWORD b = 0; b < nb; b++) {
                const u8 *x = (const u8*)(uintptr_t)lb[b], *y = (const u8*)(uintptr_t)cb[b];
                for (; x && y; x = *(u8**)(x + 8), y = *(u8**)(y + 8)) assert(x != y && !memcmp(x, y, 8));
                assert(!x && !y);
            }
            g_k8Hashes++;
        }
    }
    g_k8Cuts++;
}
static void K8Drop() {
    PcRelease(&g_k6Cl, 0, NULL, 0);
    g_k6Wc = NULL;
    PcWork& pw = g_pcWork;
    PcAstarDrop(pw);
    PcWpDrop(pw);
    RpFree(pw.r), RpFree(pw.bnm), RpFree((void*)pw.tileImg), RpFree(pw.world), RpFree(pw.wx);
    for (int m = 0; m < 16; m++) RpFree(pw.hc[m]);
    SpVerRel(pw.v);
    if (pw.csInit) DeleteCriticalSection(&pw.cs);
    memset(&pw, 0, sizeof(pw));
    SpVerRel(g_pcCut.v), PcWxRel(g_pcCut.wx), PcWxRel(g_pcWxLast);
    g_pcCut.v = NULL, g_pcCut.wx = NULL, g_pcWxLast = NULL;
    RfTeardown();
    SfTeardown();
    VirtualFree(g_sfPages, 0, MEM_RELEASE);
    g_sfPages = NULL;
}
// coverage: the closest-waypoint search 0x65BB90, the lot exit gather 0x654CC0 and the world A* 0x676260 behind counting
// detours (both runs; the counts say which planner parts the sweep reached)
typedef char(__fastcall* K8CwFn)(void*, void*, float, float, float, float, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
static K8CwFn g_k8CwOrig;
static long g_k8Cw[2];
static char __fastcall K8CwCount(void* t, void* e, float a, float b, float c, float d, DWORD a5, DWORD a6, DWORD a7, DWORD a8, DWORD a9, DWORD a10,
                                 DWORD a11) {
    const char r = g_k8CwOrig(t, e, a, b, c, d, a5, a6, a7, a8, a9, a10, a11);
    g_k8Cw[r != 0]++;
    return r;
}
typedef void(__fastcall* K8GaFn)(void*, void*, u8*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD*);  // ret 0x20; arg 8 = the set
static K8GaFn g_k8GaOrig;
static long g_k8Ga[3];
static void __fastcall K8GaCount(void* t, void* e, u8* lot, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7, DWORD* out) {
    g_k8GaOrig(t, e, lot, a2, a3, a4, a5, a6, a7, out);
    const long n = (long)((out[1] - out[0]) / 0x1C);  // 28-byte candidates (docs/99 §4.4)
    g_k8Ga[n ? 1 : 0]++, g_k8Ga[2] += n;
}
typedef DWORD*(__fastcall* K8AsFn)(u8*, void*, const u8*, const u8*, const DWORD*, const DWORD*, DWORD, DWORD*, DWORD*);
static K8AsFn g_k8AsOrig;
static long g_k8As[2];
static DWORD* __fastcall K8AsCount(u8* c, void* e, const u8* R, const u8* G, const DWORD* s, const DWORD* g, DWORD m, DWORD* o, DWORD* b) {
    DWORD* r = g_k8AsOrig(c, e, R, G, s, g, m, o, b);
    g_k8As[r != NULL]++;
    return r;
}
// random pairs: kinds = open ground / lot 0 / lot 1 for start and goal (every combination), the lot's level drawn from its
// quadtrees (a level without stairs to the other: no route), cost modes 1-4, planner flag words from the job's set (0x8000,
// 0x1000000, 0x8000000: water planning, docs/99 §2.3; 0x40; 0x800000: the retry)
static long g_k8Kind[9][2], g_k8Water, g_k8LotLeg, g_k8CrossLevel, g_k8BadFree;
// a random request; returns its start / goal kind (0..8)
static int K8RandQ(K8Q& q) {
    memset(&q, 0, sizeof(q));
    // where: 0 open ground (the two quadrants without a lot), 1 / 2 lot 0 / 1 at one of its quadtree levels
    auto pt = [](int where, float* o) {
        if (!where) { const bool a = Rnd(2) != 0; o[0] = Rf(1, 63) + (a ? 64 : 0), o[1] = Rf(1, 63) + (a ? 0 : 64); return; }
        o[0] = Rf(1, 63) + 64.0f * (where - 1), o[1] = Rf(1, 63) + 64.0f * (where - 1);
        const u8* g = g_rf.lots[where - 1] + 0x10;
        *(LONG*)&o[2] = Rnd(4) ? 0 : (LONG)Rnd((DWORD)(*(u8***)(g + 0x78) - *(u8***)(g + 0x74)));
    };
    const int sw = (int)Rnd(3), gw = (int)Rnd(3), kind = sw * 3 + gw;
    pt(sw, q.s), pt(gw, q.g);
    g_k8CrossLevel += sw && sw == gw && *(LONG*)&q.s[2] != *(LONG*)&q.g[2];
    q.mode = 1 + Rnd(4);
    if (q.mode == 2 && Rnd(2)) q.mode = 1;
    static const DWORD kFlags[6] = {0x8000, 0x1000000, 0x8000000, 0x40, 0x800000, 0x9008000};
    if (Rnd(2)) q.flags = kFlags[Rnd(6)];
    if (Rnd(3) == 0) q.flags = 0x9008000, q.mode = Rnd(2) ? 1 : 4;  // water planning in the modes that allow it (docs/94 §13.3 K6)
    return kind;
}
// [OptsInit] pass: dirty stacks that differ per side; returns the plans whose live and copy answers differ
static long g_k8OiPlans, g_k8OiDiffOff;
static bool g_k8OiClone;  // 0.35.2: the copy side runs marked as a clone run ([OptsInit] Enabled=2 zeroes only there)
static int K8OiPass(int pairs) {
    static K8Ans l, c;
    int diff = 0;
    for (int p = 0; p < pairs; p++) {
        K8Q q;
        K8RandQ(q);
        g_k8Dirty = true;
        K8Run(g_sf.W, g_sf.world, q, &l);
        if (g_k8OiClone) t_snapRun = 1;
        K8RunCopy(q, &c);
        t_snapRun = 0;
        g_k8Dirty = false;
        diff += !K8Same(l, c);
        g_k8OiPlans++, g_k8Guarded--;  // not a K8 sweep plan (its count check)
    }
    return diff;
}
static void K8Sweep(int pairs) {
    static K8Ans l, c;
    for (int p = 0; p < pairs; p++) {
        K8Q q;
        const int kind = K8RandQ(q);
        const LONG bad0 = g_rfBadFree;
        K8Run(g_sf.W, g_sf.world, q, &l);
        const LONG bad1 = g_rfBadFree;
        K8RunCopy(q, &c);
        if (g_rfBadFree != bad1 || bad1 != bad0) {
            printf("K8 FAIL: %s run freed %ld blocks the game did not allocate (EA free 0x4E3F70 on a non-EA block)\n",
                   g_rfBadFree != bad1 ? "a copy" : "a live", g_rfBadFree != bad1 ? g_rfBadFree - bad1 : bad1 - bad0);
            assert(false);
        }
        if (!K8Same(l, c)) {
            static K8Ans l2, c2;  // run both again: a side that changes its own answer is history, not the copy
            K8Run(g_sf.W, g_sf.world, q, &l2);
            K8RunCopy(q, &c2);
            printf("K8 debug: plan %ld; live again %s, copy again %s\n", g_k8Plans, K8Same(l, l2) ? "same" : "DIFFERS", K8Same(c, c2) ? "same" : "DIFFERS");
            K8Dump("sweep", q, l, c);
            assert(false);
        }
        g_k8Plans++, g_k8Ret[l.ret < 16 ? l.ret : 15]++;
        g_k8Kind[kind][l.ret ? 1 : 0]++;
        if (!l.ret) {
            g_k8Found++, g_k8Nodes += l.n;
            bool water = false, leg = false;
            for (int i = 0; i < l.n; i++) {
                const DWORD t = *(const DWORD*)(l.nodes[i] + 0x1C), wt = *(const DWORD*)(l.nodes[i] + 0x38);
                water |= wt == 0x14, leg |= wt == 2 && t != 6 && t != 7;  // a virtual water node; a lot exit (lot-edge waypoint) node
            }
            g_k8Water += water, g_k8LotLeg += leg;
            if (g_k8Pd) g_k8PdWater[K8PdClass(q.s, q.g, q.flags)] += water;
        }
        if (g_k8Pd) {
            const int pc = K8PdClass(q.s, q.g, q.flags);
            g_k8PdN[pc]++, g_k8PdFound[pc] += !l.ret;
        }
    }
}

// negative check (I-94-321): the byte copy of a lot hash before 0.33.4, i.e. lot 0's clone graph with the LIVE bucket array
// of g+0x120 again; a copy run from lot 0 to open ground (lot exits: LotEdge_WaypointPosition 0x653020) must touch the guarded
// live arena. The copy is dropped afterwards (the fault left the run half done)
static long g_k8Neg;
static void K8Negative() {
    u8* cg = RpFindLot(g_rpClone, *(DWORD*)(g_rf.lots[0] + 0x10))->node + 0x10;
    const DWORD saved = *(DWORD*)(cg + 0x124);
    if (!*(DWORD*)(g_rf.lots[0] + 0x10 + 0x124)) return;
    *(DWORD*)(cg + 0x124) = *(DWORD*)(g_rf.lots[0] + 0x10 + 0x124);
    bool hit = false;
    for (int i = 0; i < 40 && !hit; i++) {
        K8Q q;
        memset(&q, 0, sizeof(q));
        q.s[0] = Rf(1, 63), q.s[1] = Rf(1, 63), q.g[0] = Rf(65, 127), q.g[1] = Rf(1, 63), q.mode = 1;
        static K8Ans a;
        K8Guard(true);
        const int f = K8RunCopyTry(q, &a);
        K8Guard(false);
        g_k8Since = GetTickCount64();
        hit = f && g_k8Fault >= (DWORD)(uintptr_t)g_k8Arena && g_k8Fault < (DWORD)(uintptr_t)g_k8Arena + kK8ArenaSz;
        assert(!f || hit);  // any other fault is a test bug
    }
    *(DWORD*)(cg + 0x124) = saved;
    if (!hit) { printf("K8 FAIL (I-94-321 negative): no copy run read lot 0's hash with the live bucket array in place\n"); assert(false); }
    g_k8Neg++;
}

// 0.36.2 (I-94-508, docs/94 §22.31): the game's ApplyRemove 0x666750 / leaf erase 0x666010 on a live Q of the REPLAY world, run
// by replay.cpp's RpRemCovered: a footprint inserted by the game's ApplyInsert, its +0x2C then answering a rect far outside
// every Q (the drifted-copy shape: a plain remove would keep its leaves), removed with the cover table: no entry of it and
// no orphan left. Returns the footprints checked (the selftest has the negative control on the stand-ins)
static float* __fastcall RkFarBounds(const u8*, void*, float* out, DWORD, float) {
    out[0] = out[1] = 1.0e6f, out[2] = out[3] = 1.0e6f + 1.0f;
    return out;
}
static long RkCoverRemove() {
    long n = 0;
    static DWORD farVt[8][0x80];
    // replay.cpp caches cover tables by table address (the game's class vtables are fixed); farVt[n] holds another class each
    // seed, so a cached copy of last seed's class (3 levels) walked a 1-level cache past its end: the 0x80ED25 / 0x61EE3C faults
    g_rpNCoverVt = 0;
    for (int j = 0; j < g_rf.nFps && n < 4; j++) {
        u8* fp = g_rf.fps[j];
        u8* q = RfQOf(j % g_rf.nLots, j);
        const DWORD lo = *(DWORD*)(fp + 0x14), hi = *(DWORD*)(fp + 0x18);
        g_rpRem(q, NULL, lo, hi);  // not in q before (live only; no record: the clone is not compared after this)
        g_rpIns(q, NULL, fp, 0);
        int had = 0;
        RfOrphans(g_rf.m, q, fp, &had);
        if (!had) { g_rpRem(q, NULL, lo, hi); continue; }  // no leaves in this Q's rect
        const DWORD vt = *(DWORD*)fp;
        DWORD* fv = farVt[n];
        memcpy(fv, (const void*)(uintptr_t)vt, sizeof(farVt[0]));
        fv[0x2C / 4] = (DWORD)(uintptr_t)RkFarBounds;
        *(DWORD*)fp = (DWORD)(uintptr_t)fv;
        const DWORD* ct = (const DWORD*)(uintptr_t)RpCoverVtFor((DWORD)(uintptr_t)fv);
        assert(ct && !memcmp(ct, fv, 0x2C));  // the cover table is this class's (not the previous seed's class in farVt[n])
        u8* cov[1] = {fp};
        RpRemCovered(q, lo, hi, cov, 1);
        assert(*(DWORD*)fp == (DWORD)(uintptr_t)fv);  // the table put back
        *(DWORD*)fp = vt;
        int left = 0;
        const int orph = RfOrphans(g_rf.m, q, fp, &left);
        if (orph || left) printf("REPLAY cover remove: footprint %d class 0x%08lX: %d entries before, %d left, %d orphans\n", j, vt, had, left, orph);
        assert(!orph && !left);
        n++;
    }
    return n;
}

int main(int argc, char** argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    AddVectoredExceptionHandler(1, [](EXCEPTION_POINTERS* e) -> LONG {
        const DWORD code = e->ExceptionRecord->ExceptionCode;
        if (code == 0x406D1388 || code == 0xE06D7363) return EXCEPTION_CONTINUE_SEARCH;
        const CONTEXT* cx = e->ContextRecord;
        printf("exception %08lX at %08lX esp %08lX (eax %08lX ebx %08lX ecx %08lX edx %08lX esi %08lX edi %08lX ebp %08lX tid %lu); stack:", code,
               cx->Eip, cx->Esp, cx->Eax, cx->Ebx, cx->Ecx, cx->Edx, cx->Esi, cx->Edi, cx->Ebp, GetCurrentThreadId());
        const DWORD* sp = (const DWORD*)(uintptr_t)e->ContextRecord->Esp;
        for (int i = 0, k = 0; i < 256 && k < 10; i++)
            if (sp[i] >= 0x00401000 && sp[i] < 0x00F95000) { printf(" %08lX", sp[i]); k++; }
        printf("\n");
        return EXCEPTION_CONTINUE_SEARCH;
    });
    char child[4] = "";
    if (!GetEnvironmentVariableA("SRT_CHILD", child, sizeof(child))) return RunChild();
    if (!Reserve()) return 2;
    QueryPerformanceFrequency(&g_qpf);  // StartWorker sets it in the game (the async path needs the worker)
    const char* exe = argc > 1 ? argv[1] : "..\\..\\..\\game-copy\\bin\\TS3W.exe";
    const int cases = argc > 2 ? atoi(argv[2]) : 400;
    if (argc > 3) g_seed = (DWORD)atoi(argv[3]);
    const DWORD seedArg = g_seed;  // SNAP K: the sections before change g_seed
    HANDLE f = CreateFileA(exe, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) { printf("cannot open %s\n", exe); return 2; }
    const DWORD size = GetFileSize(f, NULL);
    u8* img = (u8*)malloc(size);
    DWORD got;
    if (!img || !ReadFile(f, img, size, &got, NULL) || got != size) { printf("cannot read %s\n", exe); return 2; }
    CloseHandle(f);
    if (!MapImage(img)) return 2;
    free(img);
    // every address the clone calls, unchanged in the image (the same bytes the game runs)
    assert(Matches((const void*)(uintptr_t)STEER_LOCO, kLocoPro, sizeof(kLocoPro)));
    for (int i = 0; i < QL_N; i++) assert(Matches((const void*)(uintptr_t)kQlSites[i].addr, kQlSites[i].pro, kQlSites[i].len));
    for (int i = 0; i < TW_N; i++) assert(Matches((const void*)(uintptr_t)kTwSites[i].addr, kTwSites[i].pro, kTwSites[i].len));
    // design U phase 2 (docs/89 §12): both detour prologues and the two flush call sites on the image
    assert(Matches((const void*)(uintptr_t)FT_FN, kFtPro, sizeof(kFtPro)) && Matches((const void*)(uintptr_t)UOL_FN, kUolPro, sizeof(kUolPro)));
    for (DWORD s : {PFR_SITE, PFA_SITE}) {
        const u8* p = (const u8*)(uintptr_t)s;
        assert(p[0] == 0xE8 && (DWORD)(s + 5 + *(const LONG*)(p + 1)) == (s == PFR_SITE ? PFR_FN : PFA_FN));
    }
    for (const CallSite& c : kRtoSites) {  // RouteToObjRO (docs/140 §12): the three calls in 0x831530
        const u8* p = (const u8*)(uintptr_t)c.site;
        assert(p[0] == 0xE8 && (DWORD)(c.site + 5 + *(const LONG*)(p + 1)) == c.target);
    }
    for (const CallSite& c : kTuSites) {  // TailUnlock (docs/150 §11): the four calls in 0x64E380 InstallTailUnlock retargets
        const u8* p = (const u8*)(uintptr_t)c.site;
        assert(p[0] == 0xE8 && (DWORD)(c.site + 5 + *(const LONG*)(p + 1)) == c.target);
    }
    {  // ReplanDedupe (docs/150 §13): the IC's replan count it repeats when serving, the follower-position call Replan's start
       // builder makes (0x75D93E -> 0x752480), and the destination list walk the goal key copies (0x751FF0: list at +0x18,
       // destination = node - 4)
        static const u8 kAdd[] = {0x83, 0x86, 0xF4, 0x01, 0x00, 0x00, 0x01};  // add dword ptr [esi+0x1F4],1
        assert(Matches((const void*)(uintptr_t)0x007654BA, kAdd, sizeof(kAdd)) && Matches((const void*)(uintptr_t)0x00765589, kAdd, sizeof(kAdd)));
        const u8* p = (const u8*)(uintptr_t)0x0075D93E;
        assert(p[0] == 0xE8 && (DWORD)(0x0075D93E + 5 + *(const LONG*)(p + 1)) == 0x00752480);
        static const u8 kList[] = {0x8D, 0x59, 0x18}, kNode[] = {0x8D, 0x4E, 0xFC};  // lea ebx,[ecx+0x18]; lea ecx,[esi-4]
        assert(Matches((const void*)(uintptr_t)0x00751FFA, kList, 3) && Matches((const void*)(uintptr_t)0x00752018, kNode, 3));
    }
    // P10 / G10-C (p10.cpp, docs/87): the three IC stubs' prologues, Route_PlanCommon has the goal step's prologue (site 10
    // moves there), and the call edges the P1b row and the G10-C keys rely on: 0x760BFA -> goal step, 0x764298 / 0x7643FB ->
    // Route_PlanCommon (returns 0x76429D / 0x764400)
    for (int i = 1; i < 4; i++) assert(Matches((const void*)(uintptr_t)kP10Ic[i], kP10Pro[i], sizeof(kP10Pro[i])));
    assert(Matches((const void*)(uintptr_t)ROUTE_PLAN_COMMON, kRgSites[10].pro, kRgSites[10].len) && kRgSites[10].addr == 0x0075C2D0);
    assert(Matches((const void*)(uintptr_t)kRgSites[10].addr, kRgSites[10].pro, kRgSites[10].len));
    auto callsTo = [](DWORD at, DWORD to) { const u8* p = (const u8*)(uintptr_t)at; return p[0] == 0xE8 && at + 5 + *(const DWORD*)(p + 1) == to; };
    assert(callsTo(0x00760BFA, 0x0075C2D0) && callsTo(0x00764298, ROUTE_PLAN_COMMON) && callsTo(0x007643FB, ROUTE_PLAN_COMMON));
    printf("P10 / G10-C: IC prologues, Route_PlanCommon prologue and the three call edges match\n");
    // [ProbeValues] (values.cpp, docs/108): every entry prologue and call edge of the table, on the image
    for (int i = 0; i < PV_N; i++) {
        u8 want[8];
        PvExpect(kPv[i], kPv[i].at, kPv[i].target, want);
        if (!Matches((const void*)(uintptr_t)kPv[i].at, want, kPv[i].len)) printf("values site %s 0x%08lX differs\n", kPv[i].name, kPv[i].at);
        assert(Matches((const void*)(uintptr_t)kPv[i].at, want, kPv[i].len));
    }
    assert(callsTo(PV_INROOM_RET - 5, 0x0064F550));  // the in-room path's Portal_IsLocked call (0x632554)
    printf("values: %d sites match\n", (int)PV_N);
    // [LoadProf] (loadprof.cpp, docs/39 §10): every entry prologue and call edge of the table, on the image
    for (int i = 0; i < LP_N; i++) {
        u8 want[8];
        LpExpect(kLpSites[i], kLpSites[i].at, kLpSites[i].target, want);
        if (!Matches((const void*)(uintptr_t)kLpSites[i].at, want, kLpSites[i].len)) printf("loadprof site %s 0x%08lX differs\n", kLpSites[i].name, kLpSites[i].at);
        assert(Matches((const void*)(uintptr_t)kLpSites[i].at, want, kLpSites[i].len));
    }
    printf("loadprof: %d sites match\n", (int)LP_N);
    // P4 part A (p4.cpp, docs/94 §12.2, §18): the five thunk sites, the door call, and the list release P4FreeList calls
    // (0x866E90: push ecx; push ebx; mov ebx,ecx; push esi; mov esi,[ebx] = thiscall on the list head, no stack argument)
    for (int i = 0; i < P4_SITES; i++) {
        if (!Matches((const void*)(uintptr_t)g_p4At[i], kP4Pro[i], kP4Len[i])) printf("P4 site T%d 0x%08lX differs\n", i, g_p4At[i]);
        assert(Matches((const void*)(uintptr_t)g_p4At[i], kP4Pro[i], kP4Len[i]));
    }
    assert(callsTo(P4_DOOR_AT, P4_DOOR_TARGET));
    assert(Matches((const void*)(uintptr_t)0x00866E90, (const u8*)"\x51\x53\x8B\xD9\x56\x8B\x33", 7));
    printf("P4: 5 thunk sites, the door call and the list release prologue match\n");
    // P4 part B (p4clone.cpp, 0.29.0): ApplyPendingUpdates and FlushDeferred are thiscall with no stack argument (both end in
    // a plain ret); the three display level writers carry the prologues P4InstallDl checks; the Los option test
    assert(Matches((const void*)(uintptr_t)0x0082C420, (const u8*)"\x57\x8B\xF9\x8B\x87\x5C\x09\x00\x00", 9));  // push edi; mov edi,ecx; mov eax,[edi+0x95C]
    {  // docs/247 §13.1 L: FbApplySplit (ts3native.cpp) writes this body out, so every step it copies must still be here
        assert(Matches((const void*)(uintptr_t)0x0082C429, (const u8*)"\x80\xB8\x0E\x01\x00\x00\x00", 7));  // cmp byte [eax+0x10E],0
        assert(Matches((const void*)(uintptr_t)0x0082C432, (const u8*)"\x8B\x8F\x64\x09\x00\x00\x53\x56\x6A\x00", 10));  // ecx=[edi+0x964]; push 0
        assert(callsTo(0x0082C43C, 0x00C68960) && Matches((const void*)(uintptr_t)0x00C68985, (const u8*)"\xC2\x04\x00", 3));
        assert(Matches((const void*)(uintptr_t)0x0082C441, (const u8*)"\x8B\x30\x8B\x58\x04", 5));  // esi = begin, ebx = end (read once)
        assert(Matches((const void*)(uintptr_t)0x0082C450, (const u8*)"\x8B\x06\x85\xC0\x74\x21", 6));  // NULL lot skipped
        assert(Matches((const void*)(uintptr_t)0x0082C456, (const u8*)"\x8B\x50\x48\x8B\x40\x4C\x8B\x8F\x5C\x09\x00\x00", 12));  // [lot+0x48], [lot+0x4C], P re-read
        assert(callsTo(0x0082C464, 0x00631A60) && Matches((const void*)(uintptr_t)0x00631A79, (const u8*)"\xC2\x08\x00", 3) &&
               Matches((const void*)(uintptr_t)0x00631A90, (const u8*)"\xC2\x08\x00", 3));
        assert(Matches((const void*)(uintptr_t)0x0082C46D, (const u8*)"\x8B\x0E\x51\x8B\xCF", 5) && callsTo(0x0082C472, 0x0082B590));
        assert(Matches((const void*)(uintptr_t)0x0082C47E, (const u8*)"\x8B\x97\x5C\x09\x00\x00\x5E\xC6\x82\x0E\x01\x00\x00\x00\x5B", 15));  // P+0x10E = 0
        printf("FbApplySplit: the 0x82C420 walk it copies matches\n");
    }
    assert(Matches((const void*)(uintptr_t)0x00666A40, (const u8*)"\x55\x8B\xEC\x83\xE4\xF8", 6));
    for (int i = 0; i < 3; i++) assert(Matches((const void*)(uintptr_t)kP4Dl[i].at, kP4Dl[i].pro, kP4Dl[i].len));
    assert(callsTo(0x0075AB9C, 0x0081C880) && Matches((const void*)(uintptr_t)0x0075AB95, (const u8*)"\x68\x00\x00\x00\x02", 5));  // push 0x2000000
    printf("P4 part B: pending / flush callees, 3 display level writer prologues and the Los option test match\n");
    // P5 M2-3 (p5.cpp g_p5L2, docs/107 §2.2): MonoPatcher's five sites hold the vanilla bytes p5.cpp compares on the image
    for (int i = 0; i < 5; i++) assert(Matches((const void*)(uintptr_t)g_p5L2[i].at, g_p5L2[i].b, g_p5L2[i].len));
    printf("P5: the 5 MonoPatcher L2 sites hold the vanilla bytes\n");
    // P4 H20 (0.30.0, docs/109 §8.9): the layouts the portal reference fix relies on, on the image
    {
        const DWORD* vt = (const DWORD*)(uintptr_t)LT_PORTAL_VT;
        assert(vt[0] == 0x0064E8E0 && vt[1] == 0x0064E8F0 && vt[2] == 0x0064F1A0);  // AddRef, Release, deleting dtor
        assert(Matches((const void*)(uintptr_t)0x0064E8E0, (const u8*)"\x83\x41\x70\x01\x8B\x41\x70\xC3", 8));  // add [ecx+0x70],1; mov eax,[ecx+0x70]; ret
        assert(Matches((const void*)(uintptr_t)0x0064E8F0, (const u8*)"\x8B\x41\x70\x83\xF8\x01\x7F", 7));       // mov eax,[ecx+0x70]; cmp eax,1; jg
        // nothing calls or jumps to the pair directly: the vtable slots are the only way in, so the slot swap covers every caller
        int direct = 0;
        for (DWORD a = 0x00401000; a + 5 < 0x00F95000; a++) {
            const u8* q = (const u8*)(uintptr_t)a;
            if (q[0] != 0xE8 && q[0] != 0xE9) continue;
            const DWORD t = a + 5 + *(const DWORD*)(q + 1);
            if (t == 0x0064E8E0 || t == 0x0064E8F0) direct++;
        }
        assert(direct == 0);
        // room record copy 0x6223C0: vectors +4 and +0x14 copied through 0x9EC420 (AddRef per element); destroy 0x621D20:
        // release through 0x5BF1E0 for +0x14 and +4
        assert(callsTo(0x006223FB, 0x009EC420) && callsTo(0x00622437, 0x009EC420) && callsTo(0x00621D3E, 0x005BF1E0));
        assert(Matches((const void*)(uintptr_t)0x00621D47, (const u8*)"\xE9", 1) && 0x00621D47 + 5 + *(const DWORD*)(uintptr_t)0x00621D48 == 0x005BF1E0);
        assert(Matches((const void*)(uintptr_t)0x009EC44E, (const u8*)"\x8B\x11\x8B\x02\xFF\xD0", 6));  // AddRef = slot 0
        assert(Matches((const void*)(uintptr_t)0x005BF1F6, (const u8*)"\x8B\x01\x8B\x50\x04\xFF\xD2", 7));  // Release = slot +4
        // unpin 0x639A80: pending adds +0x54 (4-byte pointers) into +0x44, pending removes +0x64 walked as 8-byte ids (sar edi,3)
        assert(Matches((const void*)(uintptr_t)0x00639A87, (const u8*)"\x8B\x6B\x58\x56\x8B\x73\x54", 7));
        assert(Matches((const void*)(uintptr_t)0x00639AFA, (const u8*)"\x8B\x7B\x68\x2B\x7B\x64\x33\xF6\xC1\xFF\x03", 11));
        assert(callsTo(0x00639AC6, 0x008B70E0) && callsTo(0x00639B26, 0x00635EE0));
        // LotRouting_AddPortal: +0x54 only while the planner's pin count P+0xC8 is set (0x637BAF), else +0x44
        assert(Matches((const void*)(uintptr_t)0x00637BAF, (const u8*)"\x83\xBF\xC8\x00\x00\x00\x00", 7));
        // H20a: P4PortalRefs puts the interlocked pair in the vtable (and the clone's references on)
        DWORD* wvt = (DWORD*)(uintptr_t)LT_PORTAL_VT;
        assert(P4PortalRefs() && wvt[0] == (DWORD)(uintptr_t)PortalAddRefX && wvt[1] == (DWORD)(uintptr_t)PortalReleaseX &&
               g_rpPortalRefs && ts3native_counters.slRefSwap == 1);
        g_rpPortalRefs = false, ts3native_counters.slRefSwap = 0;
        wvt[0] = 0x0064E8E0, wvt[1] = 0x0064E8F0;
        printf("P4 H20: portal vtable 0xFED048 pair (no direct caller), room copy / destroy vectors +4 / +0x14, unpin +0x54 / +0x64 "
               "(8-byte ids), AddPortal pin branch match; P4PortalRefs installs the interlocked pair\n");
    }
    // [RouteSnap] (routesnap.cpp, docs/94 P1): every entry prologue and call edge, and every IC slot holds its expected fn
    for (int i = 0; i < SN_N; i++) {
        const SnSite& s = kSn[i];
        const bool ok = s.target ? callsTo(s.at, s.target) : Matches((const void*)(uintptr_t)s.at, s.pro, s.len);
        if (!ok) printf("routesnap site %s 0x%08lX differs\n", s.name, s.at);
        assert(ok);
    }
    for (int i = 0; i < SN_IC_N; i++) {
        const DWORD have = *(const DWORD*)(uintptr_t)kSnIcDef[i].slot;
        if (have != kSnIcDef[i].fn) printf("routesnap IC slot %s 0x%08lX holds 0x%08lX\n", kSnIcDef[i].name, kSnIcDef[i].slot, have);
        assert(have == kSnIcDef[i].fn);
    }
    printf("routesnap: %d sites and %d IC slots match\n", (int)SN_N, (int)SN_IC_N);
    Jmp(0x004E69D0, (void*)FkMalloc);
    Jmp(0x004E6A20, (void*)FkMemalign);
    Jmp(0x004E5920, (void*)FkFree);
    Jmp(0x004E16F0, (void*)FkLock);
    Jmp(0x004E17B0, (void*)FkUnlock);
    Jmp(0x0085AE60, (void*)FkConv);
    Jmp(0x00C68990, (void*)FkLotLookup);
    // sub-object vtables (the scan for 0x620E60 at +4, docs/84 §3.6): one per type getter
    static const DWORD kSubs[] = {0xFEADE0, 0xFEB5D4, 0xFEB6F8, 0xFEB820, 0xFEB94C, 0xFEBA74, 0xFEBB98, 0xFEBCE8, 0xFEBE10};
    for (DWORD s : kSubs) {
        const DWORD* v = (const DWORD*)(uintptr_t)s;
        assert(v[1] == 0x00620E60 && (v[2] == 0x00624ED0 || v[2] == 0x00620E50));
        g_subVt[v[2] == 0x00620E50] = s;
    }
    assert(g_subVt[0] && g_subVt[1]);

    // D. lot transforms, bit for bit (lot table hit: no insert)
    for (int i = 0; i < 2000; i++) {
        if (i % 100 == 0) BuildWorld();
        const int lot = Rnd(2);
        const float x = Rf(-300, 300), z = Rf(-300, 300);
        __declspec(align(16)) float rec[4] = {x, z, 0, 0};
        float o3[3], ox, oy, u, v;
        ((void(__fastcall*)(u8*, void*, DWORD, DWORD, const float*, float*))(uintptr_t)0x0062ED40)(W.p, NULL, 0x10 + lot, 0, rec, o3);
        ((void(__fastcall*)(u8*, void*, DWORD, DWORD, float, float, float*, float*))(uintptr_t)0x0062EF30)(W.p, NULL, 0x10 + lot, 0, x, z, &ox, &oy);
        SrXform(W.entries[lot], x, z, &u, &v);
        assert(memcmp(&u, &o3[0], 4) == 0 && memcmp(&v, &o3[1], 4) == 0 && memcmp(&u, &ox, 4) == 0 && memcmp(&v, &oy, 4) == 0);
    }
    printf("transforms: 2000 x 2 bit-exact\n");

    // A. the game's call vs the clone
    for (int i = 0; i < cases; i++) OneCase();
    printf("A: %ld cases, %ld compared; with ids %ld (%ld ids), with obstacle footprints %ld; clone gave up: lot not in table %ld, "
           "deferred %ld, shape %ld, other %ld\n", g_cases, g_fbCases[0], g_withIds, g_idsTotal, g_withExcl, g_fbCases[SR_FB_LOTMISS],
           g_fbCases[SR_FB_DEFERRED], g_fbCases[SR_FB_SHAPE], g_cases - g_fbCases[0] - g_fbCases[SR_FB_LOTMISS] - g_fbCases[SR_FB_DEFERRED] - g_fbCases[SR_FB_SHAPE]);
    printf("   gathered footprints %ld; visitor branches so far: masked %ld, leaf %ld, children %ld\n", g_gatherObjs, g_srVisitKind[0],
           g_srVisitKind[1], g_srVisitKind[2]);
    printf("   0.37 child footprints 0xFEBF90: %ld built so far, in %ld compared obstacle queries (each a shape fallback before)\n",
           g_kids, g_withKid);
    assert(g_fbCases[0] > cases / 2 && g_withIds > cases / 20 && g_withExcl > cases / 10 && g_srVisitKind[0] > cases);
    assert(g_withKid > cases / 40 && g_fbCases[SR_FB_SHAPE] == 0);

    // A2. buffer limits (0.21.0): past the 0.13.0 sizes the clone must answer as the game's call; past the 0.21.0 ones it must
    // give up with SR_FB_SHAPE at that limit's site (RunCase still runs the game's call on the same world)
    {
        Ts3nCounters& kc = ts3native_counters;
        struct Lim { const char* name; int old, now, site; LONG* seen; Big lo, hi; };
        Lim lims[] = {
            {"objects", 512, kSrCap, SRS_OBJCAP, &kc.s2MaxObjs, {700, 0, 0, false}, {1200, 0, 0, false}},
            {"local", 64, kSrLocal, SRS_LOCALCAP, &kc.s2MaxLocal, {300, 100, 3, true}, {300, 200, 3, true}},
            {"nodes", 17, kSrNodes + 1, SRS_NODECAP, &kc.s2MaxNodes, {30, 0, 26, false}, {30, 0, 40, false}},
            {"ids", 256, kSrIds, SRS_IDCAP, &kc.s2MaxIds, {450, 0, 3, true}, {900, 0, 3, true}},
        };
        const int per = cases / 40 > 10 ? cases / 40 : 10;
        for (Lim& l : lims) {
            int okOld = 0, fbNew = 0, overOld = 0, overNew = 0;
            LONG most = 0;
            for (int side = 0; side < 2; side++)
                for (int i = 0; i < per; i++) {
                    g_big = side ? l.hi : l.lo;
                    BuildWorld();
                    BigLeaf();
                    Agent ag = NewAgent(false);
                    *l.seen = 0;
                    t_srShape = -1;
                    const int why = RunCase(ag);  // exits on a mismatch
                    const LONG seen = *l.seen;
                    if (seen > most) most = seen;
                    if (seen > l.now) {  // past the new limit: never an answer, and the site is this limit's
                        overNew++;
                        assert(why != SR_OK);
                        if (why == SR_FB_SHAPE) { assert(t_srShape == l.site); fbNew++; }
                    } else if (seen > l.old) {
                        overOld++;
                        okOld += why == SR_OK;
                    }
                }
            printf("A2: %-7s old %d new %d: %d cases past the old limit (%d compared equal), %d past the new (%d gave up at its "
                   "site), most seen %ld\n", l.name, l.old, l.now, overOld, okOld, overNew, fbNew, most);
            assert(okOld > 0 && fbNew > 0);
        }
        g_big = Big{};
    }

    // B. the S2 hook on the mapped 0x85FD10
    Ts3nCounters& k = ts3native_counters;
    g_locoOrig = (LocoFn)InstallDetour("Locomotion_ObstacleCheck", STEER_LOCO, kLocoPro, sizeof(kLocoPro), (void*)LocoHook, &g_locoOrig);
    assert(g_locoOrig);
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me;
    g_rtSimTidSlot = &simTid;
    g_async.owner = me;
    k.steerMode = 1;
    long taken = 0, compared = 0;
    for (int i = 0; i < cases; i++) {
        // the same world twice: the hook during a "job", then the game's call
        const DWORD seed = g_seed;
        BuildWorld();
        Agent ag = NewAgent(false);
        IdVec a, b;
        NewIds(&a, 2);
        b = a;
        b.b = (DWORD*)_aligned_malloc(8 * 3, 16);
        memcpy(b.b, a.b, 16);
        b.e = b.b + 4, b.c = b.b + 6;
        const LONG t0 = k.steerTaken;
        Job(true);
        ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &a, NULL, 0, NULL, 0);
        Job(false);
        ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &b, NULL, 0, NULL, 0);  // no job: the trampoline
        taken += k.steerTaken - t0;
        assert(a.e - a.b == b.e - b.b && memcmp(a.b, b.b, (a.e - a.b) * 4) == 0);
        compared++;
        _aligned_free(a.b);
        _aligned_free(b.b);
        (void)seed;
    }
    printf("B: hook %ld calls, %ld read-only answers, all equal to the game's\n", compared, taken);
    assert(taken > cases / 2);
    // shadow: no job, the game's call and the clone compared inside the hook
    k.steerShadow = 1;
    const LONG sh0 = k.steerShadowRuns;
    for (int i = 0; i < cases; i++) {
        BuildWorld();
        Agent ag = NewAgent(false);
        IdVec a;
        NewIds(&a, 2);
        ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &a, NULL, 0, NULL, 0);
        _aligned_free(a.b);
    }
    printf("B: shadow %ld runs, mismatch %ld, order %ld, query mismatch %ld\n", k.steerShadowRuns - sh0, k.steerShadowMismatch,
           k.steerShadowOrder, k.steerShadowQueryMismatch);
    assert(k.steerShadowRuns - sh0 > cases / 2 && k.steerShadowMismatch == 0 && k.steerShadowOrder == 0 && k.steerShadowQueryMismatch == 0);
    k.steerShadow = 0;
    // fallbacks: each reason calls the game's code (here: its answer is still the reference)
    {
        BuildWorld();
        Agent ag = NewAgent(false);
        while (ag.nn < 2) ag = NewAgent(false);  // EditPath runs from the second node on
        for (int i = 0; i < ag.nn; i++) ag.nodes[i][0x30] = 1;
        static SrOut out;
        static SrCtx ctx;
        W.wm[0x1CC] = 1;
        assert(SrRun(ag.a, NULL, 0, NULL, 0, NULL, 0, &out, &ctx) == SR_FB_DIRTY);
        W.wm[0x1CC] = 0;
        // a lot that is not in the lot table: EditPath's GetOrInsert would insert it (the gather's Contains says no first)
        for (int i = 0; i < ag.nn; i++) { float* p = (float*)(ag.nodes[i] + 0x10); p[0] = 150; p[2] = 0; }
        assert(SrRun(ag.a, NULL, 0, NULL, 0, NULL, 0, &out, &ctx) == SR_FB_LOTMISS);
        // the hook's state checks: lock owner, inside a job callback
        IdVec v;
        NewIds(&v, 0);
        const LONG st0 = k.steerFb[SR_FB_STATE - 1];
        Job(true);
        for (int i = 0; i < ag.nn; i++) { float* p = (float*)(ag.nodes[i] + 0x10); p[0] = -0.5f; p[2] = 0; }
        k.rgOwnerTid = (LONG)me;
        ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &v, NULL, 0, NULL, 0);
        k.rgOwnerTid = 0;
        g_inInvoke = 1;
        ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &v, NULL, 0, NULL, 0);
        g_inInvoke = 0;
        Job(false);
        assert(k.steerFb[SR_FB_STATE - 1] == st0 + 2);
        _aligned_free(v.b);
    }
    {  // A2 through the hook during a job: past kSrCap the fallback is counted under its site and the game's call answers
        int atSite = 0, ids = 0;
        for (int i = 0; i < 20; i++) {
            g_big = Big{1200, 0, 3, true};
            BuildWorld();
            BigLeaf();
            Agent ag = NewAgent(false);
            g_big = Big{};
            IdVec a, b;
            NewIds(&a, 0);
            NewIds(&b, 0);
            const LONG s0 = k.s2Shape[SRS_OBJCAP], f0 = k.steerFb[SR_FB_SHAPE - 1], t0 = k.steerTaken;
            Job(true);
            ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &a, NULL, 0, NULL, 0);
            Job(false);
            ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &b, NULL, 0, NULL, 0);
            if (k.s2Shape[SRS_OBJCAP] != s0) {  // a gather past kSrCap: no answer, one shape fallback, the game's call
                assert(k.s2Shape[SRS_OBJCAP] == s0 + 1 && k.steerFb[SR_FB_SHAPE - 1] == f0 + 1 && k.steerTaken == t0);
                atSite++;
            }
            assert(a.e - a.b == b.e - b.b && memcmp(a.b, b.b, (a.e - a.b) * 4) == 0);
            ids += (int)(a.e - a.b) / 2;
            _aligned_free(a.b);
            _aligned_free(b.b);
        }
        printf("B: past kSrCap through the hook, 20 calls: %d shape fallbacks at its site (no answer taken), %d ids from the game's call\n",
               atSite, ids);
        assert(atSite > 0);
    }
    printf("B: fallbacks %ld/%ld/%ld/%ld/%ld/%ld/%ld/%ld (state, QL busy, dirty, lot miss, deferred, cache, shape, tripwire)\n",
           k.steerFb[0], k.steerFb[1], k.steerFb[2], k.steerFb[3], k.steerFb[4], k.steerFb[5], k.steerFb[6], k.steerFb[7]);

    // C. the worker writer stubs on the game's prologues; W6a end to end on a worker thread
    for (int i = 0; i < QL_N; i++) {
        g_qlTramp[i] = InstallDetour(kQlSites[i].name, kQlSites[i].addr, kQlSites[i].pro, kQlSites[i].len, (void*)kQlStubs[i], &g_qlTramp[i]);
        assert(g_qlTramp[i]);
    }
    g_qlOn = 1;
    static DWORD cbVt[0x50];
    memcpy(cbVt, (const void*)(uintptr_t)0x00FEBBC0, sizeof(cbVt));
    cbVt[0xF8 / 4] = cbVt[0xFC / 4] = (DWORD)(uintptr_t)CbRebuild;
    g_wFp = (u8*)A(0x80);
    *(DWORD**)g_wFp = cbVt;
    u8* cache = (u8*)A(0x24 * 3);  // level 0 invalid: 0x80E5B0 rebuilds through +0xF8 / +0xFC
    *(u8**)(g_wFp + 0x38) = cache;
    HANDLE th = CreateThread(NULL, 0, WorkerThread, NULL, 0, NULL);
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(g_cbSeen == 2 && cache[0x20] == 1 && k.steerQlHolds == 1 && TryAcquireSRWLockShared(&g_ql));
    ReleaseSRWLockShared(&g_ql);
    // the worker holds QL (inside the rebuild) while a job runs: the hook falls back, the game's answer is the same
    cache[0x20] = 0;
    g_wIn = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_wGo = CreateEventW(NULL, FALSE, FALSE, NULL);
    th = CreateThread(NULL, 0, WorkerThread, NULL, 0, NULL);
    WaitForSingleObject(g_wIn, INFINITE);
    {
        BuildWorld();
        Agent ag = NewAgent(false);
        ag.nodes[0][0x30] = 1;
        IdVec a, b;
        NewIds(&a, 0);
        NewIds(&b, 0);
        const LONG busy0 = k.steerFb[SR_FB_TRY - 1];
        Job(true);
        ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &a, NULL, 0, NULL, 0);
        assert(k.steerFb[SR_FB_TRY - 1] == busy0 + 1);
        // a sim-thread cache rebuild during a job is counted (here on a valid cache: no rebuild, the stub still counts)
        const LONG sc0 = k.steerSimCacheWrite;
        u8* fp = W.fps[0];
        ((void(__fastcall*)(u8*, void*, DWORD))(uintptr_t)0x0080E5B0)(fp, NULL, 0);
        assert(k.steerSimCacheWrite == sc0 + 1);
        Job(false);
        SetEvent(g_wGo);
        WaitForSingleObject(th, INFINITE);
        ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &b, NULL, 0, NULL, 0);
        assert(a.e - a.b == b.e - b.b && memcmp(a.b, b.b, (a.e - a.b) * 4) == 0);
    }
    CloseHandle(th);
    assert(k.steerQlHolds == 2 && k.steerQlWaitUs == 0 && k.steerUntracked == 0 && k.steerSeqSkew == 0 && k.steerSimWriteInRo == 0);
    printf("C: 12 worker writer stubs on the game's prologues; W6a holds QL exclusive on the worker; QL busy -> fallback, same ids\n");

    // C-A on the game's 0x80E5B0: sim thread, a job runs, invalid level: +0x918 is taken in the stub before the body
    {
        static DWORD simVt[0x50];
        memcpy(simVt, cbVt, sizeof(simVt));
        simVt[0xF8 / 4] = simVt[0xFC / 4] = (DWORD)(uintptr_t)CbRebuildSim;
        *(DWORD**)g_wFp = simVt;
        g_caClasses[0] = (DWORD)(uintptr_t)simVt;
        g_lockOrig = CaFakeAcquire;
        g_rgRelOrig = CaFakeRelease;
        g_rgLock = g_caLock;
        g_async.lock = g_caLock;
        g_async.owner = me;
        g_workerTid = me + 7;
        g_caOn = 1;
        k.caMode = 1;
        cache[0x20] = 0;
        Job(true);
        ((void(__fastcall*)(u8*, void*, DWORD))(uintptr_t)0x0080E5B0)(g_wFp, NULL, 0);
        Job(false);
        assert(g_caAcq == 1 && g_caRebuilds == 2 && cache[0x20] == 1 && *(LONG*)(g_caLock + 0x20) == 0 && t_rg.n == 0);
        assert(k.caSimInJob[0] == 1 && k.caWaits == 1 && k.caQlHeld == 0);
        // a valid level: no lock, nothing counted
        Job(true);
        ((void(__fastcall*)(u8*, void*, DWORD))(uintptr_t)0x0080E5B0)(g_wFp, NULL, 0);
        Job(false);
        assert(g_caAcq == 1 && k.caSimInJob[0] == 1);
        printf("C-A: the game's 0x80E5B0 on the sim thread during a job took +0x918 before marking the level valid, released on return\n");
    }
    // E. design L1 (lotfd.cpp): the game's 0x631800 vs LtFrontDoor on random lot tables, then the L1 detour on it, with the
    // T-L3 counting thunks in the game's portal vtable 0xFED048 (the game's own AddRef 0x64E8E0 / Release 0x64E8F0 behind them)
    {
        assert(Matches((const void*)(uintptr_t)LT_FD, kFdPro, sizeof(kFdPro)));
        assert(InstallPortalRefThunks((DWORD*)(uintptr_t)LT_PORTAL_VT));
        static u8 fwm[0x200], fact[0x60];
        *(u8**)(uintptr_t)0x011ECBC4 = fwm;
        *(u8**)(fwm + 0xB0) = fact;
        struct FdLot { DWORD lo, hi; };
        static FdLot lots[64];
        static u8* portals[64 * 5];
        static LONG counts[64 * 5];
        auto build = [&](int* nLots, int* nPortals) -> u8* {
            u8* p = (u8*)A(0x200);
            const DWORD cnt = 1 + Rnd(16);
            DWORD* bk = (DWORD*)A((cnt + 1) * 4);
            bk[cnt] = kSentinel;
            *(DWORD**)(p + 0x4C) = bk;
            *(DWORD*)(p + 0x50) = cnt;
            *nLots = 1 + Rnd(64);
            *nPortals = 0;
            for (int i = 0; i < *nLots; i++) {
                lots[i].lo = 1 + Rnd(cnt * 4) + (DWORD)i * 0x100;  // distinct, several per bucket
                lots[i].hi = Rnd(4) ? 0 : Rnd(3);
                u8* node = (u8*)A(0x200);
                *(DWORD*)node = lots[i].lo, *(DWORD*)(node + 4) = lots[i].hi;
                u8* e = node + 0x10;
                *(DWORD*)e = Rnd(40) ? lots[i].lo : lots[i].lo + 1;  // rarely a node whose entry holds another key
                *(DWORD*)(e + 4) = lots[i].hi;
                const int n = (int)Rnd(5);
                if (n) {
                    DWORD* v = (DWORD*)A(4 * n);
                    for (int j = 0; j < n; j++) {
                        if (Rnd(6) == 0) continue;  // a NULL slot
                        u8* pt = (u8*)A(0x80);
                        *(DWORD*)pt = LT_PORTAL_VT;
                        *(DWORD*)(pt + 8) = Rnd(0xFFFFFF) * 7, *(DWORD*)(pt + 0xC) = Rnd(0xFFFF);
                        *(LONG*)(pt + 0x70) = 1 + (LONG)Rnd(3);
                        portals[*nPortals] = pt;
                        counts[(*nPortals)++] = *(LONG*)(pt + 0x70);
                        v[j] = (DWORD)(uintptr_t)pt;
                    }
                    *(DWORD**)(e + 0x44) = v, *(DWORD**)(e + 0x48) = v + n;
                }
                *(DWORD*)(e + 0x18) = Rnd(4) ? Rnd(n + 2) : 0xFFFFFFFF;  // sometimes n or n + 1: out of range
                *(DWORD*)(e + 0x1C) = Rnd(4) ? Rnd(n + 2) : 0xFFFFFFFF;
                const DWORD b = lots[i].lo % cnt;
                *(DWORD*)(node + 0x190) = bk[b];
                bk[b] = (DWORD)(uintptr_t)node;
            }
            *(DWORD*)(fact + 0x48) = Rnd(8) ? 0x77777777 : lots[Rnd(*nLots)].lo;  // sometimes a table lot is the active lot
            *(DWORD*)(fact + 0x4C) = Rnd(8) ? 0 : lots[0].hi;
            return p;
        };
        auto pick = [&](int nLots, DWORD* lo, DWORD* hi) {
            const DWORD r = Rnd(20);
            if (r == 0) *lo = *hi = 0;
            else if (r == 1) *lo = *hi = 0xFFFFFFFF;
            else if (r == 2) { *lo = *(DWORD*)(fact + 0x48); *hi = *(DWORD*)(fact + 0x4C); }
            else if (r < 6) { *lo = Rnd(0x10000); *hi = Rnd(2); }  // mostly not in the table
            else { const FdLot& l = lots[Rnd(nLots)]; *lo = l.lo; *hi = l.hi; }
        };
        typedef u64(__fastcall * GameFdFn)(const u8*, void*, DWORD, DWORD, DWORD, const u8**);
        const u8* const kOutMark = (const u8*)(uintptr_t)0x0BAD0BAD;
        g_async.owner = me;
        long cmp = 0, withDoor = 0, perType[5] = {};
        for (int c = 0; c < cases * 5; c++) {
            int nLots, nPortals;
            const u8* p = build(&nLots, &nPortals);
            for (int q = 0; q < 8; q++) {
                DWORD lo, hi;
                pick(nLots, &lo, &hi);
                const DWORD type = Rnd(5);  // 3 and 4: no door
                const u8* out1 = kOutMark;
                const bool withOut = Rnd(2) != 0;  // the IC passes NULL; the out path is checked anyway
                Job(true);  // T-L3 counts only during a job
                const u64 r1 = ((GameFdFn)(uintptr_t)LT_FD)(p, NULL, lo, hi, type, withOut ? &out1 : NULL);
                Job(false);
                for (int i = 0; i < nPortals; i++) assert(*(LONG*)(portals[i] + 0x70) == counts[i]);  // the game's net change is 0
                u64 r2;
                const u8* pt;
                assert(LtFrontDoor(p, lo, hi, type, &r2, &pt) == LT_OK);
                assert(r1 == r2 && out1 == (withOut && pt ? pt : kOutMark) && (pt || r2 == 0));
                cmp++;
                withDoor += pt != NULL;
                perType[type] += pt != NULL;
            }
        }
        printf("E: game 0x631800 vs clone: %ld calls, all equal; with a door %ld (type 0 %ld, 1 %ld, 2 %ld); portal AddRef %ld = Release %ld\n",
               cmp, withDoor, perType[0], perType[1], perType[2], k.portalRef[0], k.portalRef[4]);
        assert(withDoor > cmp / 10 && perType[3] == 0 && perType[4] == 0 && k.portalRef[0] == withDoor && k.portalRef[4] == withDoor);
        // the L1 detour on the mapped 0x631800: job -> clone (no AddRef/Release), no job -> the game's code; shadow; QL busy
        g_fdOrig = (FdFn)InstallDetour("LotRouting_FrontDoorPortal", LT_FD, kFdPro, sizeof(kFdPro), (void*)FdHook, &g_fdOrig);
        assert(g_fdOrig);
        k.ltMode = 1;
        const LONG ref0 = k.portalRef[0];
        long ltTaken = 0;
        for (int c = 0; c < cases; c++) {
            int nLots, nPortals;
            const u8* p = build(&nLots, &nPortals);
            DWORD lo, hi;
            pick(nLots, &lo, &hi);
            const DWORD type = Rnd(3);
            const LONG t0 = k.ltTaken;
            Job(true);
            const u64 a = ((GameFdFn)(uintptr_t)LT_FD)(p, NULL, lo, hi, type, NULL);
            Job(false);
            const u64 b = ((GameFdFn)(uintptr_t)LT_FD)(p, NULL, lo, hi, type, NULL);
            assert(a == b && k.ltTaken == t0 + 1);
            ltTaken++;
        }
        assert(k.portalRef[0] == ref0);  // read-only answers took no reference
        k.ltShadow = 1;
        for (int c = 0; c < cases; c++) {
            int nLots, nPortals;
            const u8* p = build(&nLots, &nPortals);
            DWORD lo, hi;
            pick(nLots, &lo, &hi);
            const u8* o = kOutMark;
            ((GameFdFn)(uintptr_t)LT_FD)(p, NULL, lo, hi, Rnd(4), &o);
        }
        assert(k.ltShadowRuns == cases && k.ltShadowMismatch == 0 && g_rgOffSimOpen == 0);
        // a worker writer holds QL: the hook falls back to the game's code, same answer
        g_wIn = CreateEventW(NULL, FALSE, FALSE, NULL);
        g_wGo = CreateEventW(NULL, FALSE, FALSE, NULL);
        HANDLE qh = CreateThread(NULL, 0, [](void*) -> DWORD {
            AcquireSRWLockExclusive(&g_ql);
            SetEvent(g_wIn);
            WaitForSingleObject(g_wGo, 5000);
            ReleaseSRWLockExclusive(&g_ql);
            return 0;
        }, NULL, 0, NULL);
        WaitForSingleObject(g_wIn, INFINITE);
        int nLots, nPortals;
        const u8* p = build(&nLots, &nPortals);
        const DWORD lo = lots[0].lo, hi = lots[0].hi;
        Job(true);
        const u64 a = ((GameFdFn)(uintptr_t)LT_FD)(p, NULL, lo, hi, 0, NULL);
        Job(false);
        SetEvent(g_wGo);
        WaitForSingleObject(qh, INFINITE);
        CloseHandle(qh);
        assert(k.ltFb[LT_FB_TRY - 1] == 1 && a == ((GameFdFn)(uintptr_t)LT_FD)(p, NULL, lo, hi, 0, NULL));
        printf("E: L1 hook %ld read-only answers equal to the game's; shadow %ld runs, 0 mismatches; QL busy -> game's call\n", ltTaken,
               k.ltShadowRuns);
        k.ltMode = k.ltShadow = 0;
    }
    // F. tripwires T4 and T-S7 (0.15.0) on the game's prologues: the game's own writer runs on another thread through its stub
    // and blocks inside (a fake callee); meanwhile the S2 hook drops the clone and runs the game's call; same ids as with no job
    {
        assert(InstallTripwires() == TW_N && k.steerTwStubs == TW_N);
        g_async.owner = me;
        g_workerTid = me + 7;
        k.steerMode = 1;
        k.steerShadow = 0;
        g_wIn = CreateEventW(NULL, FALSE, FALSE, NULL);
        g_wGo = CreateEventW(NULL, FALSE, FALSE, NULL);
        // a fake lock-key object: vtable slot 0 = deleting destructor (0x807110 tail-jumps to it on the last reference)
        static DWORD keyVt[4], relVt[0x30];
        keyVt[0] = (DWORD)(uintptr_t)TwKeyDtor;
        relVt[0x94 / 4] = (DWORD)(uintptr_t)TwFpRelease;  // footprint release from the entry dtor
        static u8 keyOld[0x10], keyNew[0x10], keyFp[0x80], entry[0x20], relObj[0x10];
        auto oneCase = [&](DWORD (WINAPI* writer)(void*), int site) {
            BuildWorld();
            Agent ag = NewAgent(false);
            ag.nodes[0][0x30] = 1;
            IdVec a, b;
            NewIds(&a, 1);
            b = a;
            b.b = (DWORD*)_aligned_malloc(8 * 2, 16);
            memcpy(b.b, a.b, 8);
            b.e = b.b + 2, b.c = b.b + 4;
            const LONG fb0 = k.steerFb[SR_FB_TRIP - 1], tk0 = k.steerTaken, d0 = k.steerTripDiscard, s0 = k.steerTwSite[site];
            Job(true);
            HANDLE th = CreateThread(NULL, 0, writer, NULL, 0, NULL);
            WaitForSingleObject(g_wIn, INFINITE);
            assert(g_twActive == 1 && k.steerTwSite[site] == s0 + 1);
            ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &a, NULL, 0, NULL, 0);  // job running, writer inside: game's call
            assert(k.steerFb[SR_FB_TRIP - 1] == fb0 + 1 && k.steerTripDiscard == d0 + 1 && k.steerTaken == tk0);
            SetEvent(g_wGo);
            WaitForSingleObject(th, INFINITE);
            CloseHandle(th);
            assert(g_twActive == 0);
            Job(false);
            ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &b, NULL, 0, NULL, 0);  // no job: the trampoline
            assert(a.e - a.b == b.e - b.b && memcmp(a.b, b.b, (a.e - a.b) * 4) == 0);
            // the same world with the writer gone: the clone answers again, and the same
            IdVec c = b;
            c.b = (DWORD*)_aligned_malloc(8 * 2, 16);
            memcpy(c.b, a.b, 8);
            c.e = c.b + 2, c.c = c.b + 4;
            Job(true);
            ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &c, NULL, 0, NULL, 0);
            Job(false);
            assert(c.e - c.b == b.e - b.b && memcmp(c.b, b.b, (c.e - c.b) * 4) == 0);
            g_twAfter += k.steerTaken - tk0;  // read-only again (or an ordinary fall-back such as a lot miss)
            const long added = (long)(a.e - a.b) / 2 - 1;
            _aligned_free(a.b);
            _aligned_free(b.b);
            _aligned_free(c.b);
            return added;
        };
        // T-S7: the game's Footprint_SetLockKeyObject 0x807110 on a plain thread replaces fp+0x20; the old key's last reference
        long idsS7 = 0, idsT4 = 0;
        for (int i = 0; i < 200; i++) {
            *(DWORD**)keyOld = keyVt, *(LONG*)(keyOld + 4) = 1;
            *(DWORD**)keyNew = keyVt, *(LONG*)(keyNew + 4) = 1;
            *(u8**)(keyFp + 0x20) = keyOld;
            idsS7 += oneCase([](void*) -> DWORD {
                ((void(__fastcall*)(u8*, void*, u8*))(uintptr_t)0x00807110)(keyFp, NULL, keyNew);
                assert(*(u8**)(keyFp + 0x20) == keyNew && *(LONG*)(keyNew + 4) == 2 && *(LONG*)(keyOld + 4) == 1);
                assert(t_rg.n == 0 && t_twDepth == 0);  // left through the leave thunk (the destructor's ret 4 came back to it)
                return 0;
            }, 4);
        }
        // T4: the game's FootprintMapEntry_DeletingDtor 0x61ED10 (flag 0: no pool write) on the worker outside QL; its footprint
        // release (+0x94) blocks. Inside a W frame (QL depth 1) the same call is not counted.
        for (int i = 0; i < 200; i++) {
            *(DWORD*)entry = 0x00FEAC7C;
            *(u8**)(entry + 8) = relObj;
            *(DWORD**)relObj = relVt;
            idsT4 += oneCase([](void*) -> DWORD {
                g_workerTid = GetCurrentThreadId();
                ((void(__fastcall*)(u8*, void*, DWORD))(uintptr_t)0x0061ED10)(entry, NULL, 0);
                assert(*(DWORD*)entry == 0x0107396C && t_rg.n == 0 && t_twDepth == 0);
                const LONG bare = ts3native_counters.steerT4Bare;
                t_qlDepth = 1;
                *(u8**)(entry + 8) = NULL;
                ((void(__fastcall*)(u8*, void*, DWORD))(uintptr_t)0x0061ED10)(entry, NULL, 0);
                assert(ts3native_counters.steerT4Bare == bare && t_rg.n == 0);
                t_qlDepth = 0;
                g_workerTid = 0x7FFFFFFF;
                return 0;
            }, 3);
        }
        // F2 (0.18.0): arm / disarm races, the writer is the game's 0x807110 on a plain thread. Every answer given while a
        // writer that entered at or after the arm still runs must be dropped; a disarmed site must cost nothing.
        {
            const LONG f0 = k.steerS7Foreign;
            long beforeArm = 0, armedEarly = 0, endDrop = 0, f2Ids = 0;
            for (int i = 0; i < 100; i++) {
                // (a) no job, disarmed: the site goes straight to the trampoline (nothing counted, held or timed)
                const LONG tw0 = k.s2TwCalls, site0 = k.steerTwSite[4];
                KeyReset();
                SetEvent(g_wGo);  // TwKeyDtor does not block this time
                HANDLE wt = CreateThread(NULL, 0, KeyWriter, NULL, 0, NULL);
                WaitForSingleObject(wt, INFINITE);
                CloseHandle(wt);
                ResetEvent(g_wIn);
                assert(g_twArmed == 0 && g_twActive == 0 && k.s2TwCalls == tw0 && k.steerTwSite[4] == site0);
                // (b) that writer ran and left before the arm: the clone answers, and the same as the game's call
                BuildWorld();
                Agent ag = NewAgent(false);
                ag.nodes[0][0x30] = 1;
                IdVec a, b;
                NewIds(&a, 1);
                b = a;
                b.b = (DWORD*)_aligned_malloc(8 * 2, 16);
                memcpy(b.b, a.b, 8);
                b.e = b.b + 2, b.c = b.b + 4;
                ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &b, NULL, 0, NULL, 0);  // the reference: no job
                IdVec c = a;
                c.b = (DWORD*)_aligned_malloc(8 * 2, 16);
                memcpy(c.b, a.b, 8);
                c.e = c.b + 2, c.c = c.b + 4;
                LONG tk0 = k.steerTaken;
                Job(true);
                ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &c, NULL, 0, NULL, 0);
                Job(false);
                beforeArm += k.steerTaken - tk0;
                assert(c.e - c.b == b.e - b.b && memcmp(c.b, b.b, (c.e - c.b) * 4) == 0);
                // (c) a writer that enters after the arm but before A_RUNNING (back-off window) is counted: the clone drops
                KeyReset();
                TwArm(true);
                wt = CreateThread(NULL, 0, KeyWriter, NULL, 0, NULL);
                WaitForSingleObject(g_wIn, INFINITE);
                assert(g_twActive == 1);
                g_async.state = A_RUNNING;
                tk0 = k.steerTaken;
                const LONG d0 = k.steerTripDiscard;
                memcpy(c.b, a.b, 8);
                c.e = c.b + 2;
                ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &c, NULL, 0, NULL, 0);
                assert(k.steerTaken == tk0 && k.steerTripDiscard == d0 + 1);
                assert(c.e - c.b == b.e - b.b && memcmp(c.b, b.b, (c.e - c.b) * 4) == 0);
                armedEarly++;
                SetEvent(g_wGo);
                WaitForSingleObject(wt, INFINITE);
                CloseHandle(wt);
                Job(false);
                // (d) the job ends during the clone and a writer enters the disarmed site (mode 1), or the next job arms
                // again before the clone goes on (mode 2): the generation moved, the answer is dropped
                for (g_f2Mode = 1; g_f2Mode <= 2; g_f2Mode++) {
                    memcpy(c.b, a.b, 8);
                    c.e = c.b + 2;
                    tk0 = k.steerTaken;
                    const LONG e0 = k.s2JobEndDrop, r0 = g_f2Runs;
                    g_srActiveLot = F2ActiveLot;
                    Job(true);
                    ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &c, NULL, 0, NULL, 0);
                    g_srActiveLot = (SrActiveLotFn)(uintptr_t)0x00C88FD0;
                    if (g_f2Runs != r0) {  // the clone reached its lot lookup with the writer inside: dropped, the game's call ran
                        assert(k.steerTaken == tk0 && k.s2JobEndDrop == e0 + 1);
                        endDrop++;
                        SetEvent(g_wGo);
                        WaitForSingleObject(g_f2Writer, INFINITE);
                        CloseHandle(g_f2Writer);
                        g_f2Writer = NULL;
                    }
                    Job(false);
                    assert(c.e - c.b == b.e - b.b && memcmp(c.b, b.b, (c.e - c.b) * 4) == 0);
                }
                g_f2Mode = 0;
                f2Ids += (long)(b.e - b.b) / 2 - 1;
                _aligned_free(a.b);
                _aligned_free(b.b);
                _aligned_free(c.b);
            }
            printf("F2: arm/disarm 100 x: disarmed site not counted; writer gone before the arm -> %ld read-only answers, same ids; "
                   "writer entering after the arm, before A_RUNNING -> %ld dropped; job ending inside the clone with a writer in "
                   "the disarmed site -> %ld dropped (s2JobEndDrop %ld); %ld ids; contaminated answers taken 0\n", beforeArm,
                   armedEarly, endDrop, k.s2JobEndDrop, f2Ids);
            assert(beforeArm > 30 && armedEarly == 100 && endDrop > 30 && k.steerS7Foreign == f0 + 100 && g_twActive == 0 &&
                   g_twArmed == 0 && f2Ids > 5);
        }
        // the sim thread's own lock-key write during a job: counted apart, nothing dropped
        *(LONG*)(keyOld + 4) = 5;
        *(u8**)(keyFp + 0x20) = keyOld;
        const LONG sim0 = k.steerS7Sim, f0 = k.steerS7Foreign;
        Job(true);
        ((void(__fastcall*)(u8*, void*, u8*))(uintptr_t)0x00807110)(keyFp, NULL, keyNew);
        Job(false);
        assert(k.steerS7Sim == sim0 + 1 && k.steerS7Foreign == f0 && *(u8**)(keyFp + 0x20) == keyNew && g_twActive == 0);
        printf("F: 11 tripwire stubs on the game's prologues; T-S7 (0x807110 on another thread) and T4 (0x61ED10 on the worker "
               "outside QL) each 200 x: clone dropped, the game's call ran, same ids as with no job (%ld / %ld ids); T-S7 %ld, T4 %ld, "
               "sim %ld, dropped %ld; read-only again after the writer left %ld / 400\n", idsS7, idsT4, k.steerS7Foreign, k.steerT4Bare,
               k.steerS7Sim, k.steerTripDiscard, g_twAfter);
        assert(k.steerS7Foreign == 300 && k.steerT4Bare == 200 && k.steerTripDiscard == 500 && k.steerUntracked == 0 && g_twAfter > 200 &&
               idsS7 + idsT4 > 10);
        k.steerMode = 0;
    }
    // G. fix D-X (0.16.0, discourage.cpp, docs/84 §7.2): our call sites into the game's own ObjectService_FindByIdAddRef
    //    0x9395A0 (empty and one-object id hash), object release 0x7460F0, RoutingWorld_RemoveDiscourageFootprint 0x834860 (an
    //    object without a footprint component: lock, component lookup, unlock, ret 4), the lock-key find 0x82AD60 and
    //    LotRouting_RemoveObjectFootprints 0x635700 with lot 0 on fake id maps (the dead id's routing record has lost its lot:
    //    the erase path; a quadtree hit needs the lot quadtrees and is left to the game run)
    {
        struct GxLock {
            static LONG __fastcall Acq(void* lock, void*, const DWORD*) { ++*(LONG*)((u8*)lock + 0x20); return 1; }
            static LONG __fastcall Rel(void* lock, void*) { return --*(LONG*)((u8*)lock + 0x20); }
        };
        g_lockOrig = GxLock::Acq;
        g_rgRelOrig = GxLock::Rel;
        *(u8**)(uintptr_t)0x011D9128 = NULL;  // RoutingWorld_Get: the lot mark 0x62D9F0 at the end of 0x635700 returns at once
        u8* svc = (u8*)A(0x1040);             // ObjectService: 0x409 buckets at +0x10, end node [+0x1034] = 0
        *(u8**)(uintptr_t)0x011DC350 = svc;   // ObjectService_Get 0x938B50 reads it
        u8* world = (u8*)A(0xA00);
        world[0x974] = 1;
        u8* p = (u8*)A(0x200);
        *(u8**)(world + 0x95C) = p;
        const DWORD dead = 0x5151, live = 0x6262;
        // P+0x3C and P+0x24: id maps (buckets +4, bucket count +8, size +0xC; node: key +0/+4, value +8, next +0x10)
        auto newMap = [](DWORD n, DWORD size) {
            u8* m = (u8*)A(0x20);
            DWORD* bk = (DWORD*)A((n + 1) * 4);
            bk[n] = kSentinel;
            *(DWORD**)(m + 4) = bk;
            *(DWORD*)(m + 8) = n;
            *(DWORD*)(m + 0xC) = size;
            return m;
        };
        u8* m3c = newMap(3, 0);
        u8* m24 = newMap(3, 1);
        *(u8**)(p + 0x3C) = m3c;
        *(u8**)(p + 0x24) = m24;
        u8* pool = (u8*)A(0x20);     // node pool of the map (0x6281A0 pushes freed nodes on its list +0x10)
        *(u8**)(m24 + 0x1C) = pool;
        u8* rec = (u8*)A(0x20);      // the id's per-lot routing record: owner +0xC gone
        *(LONG*)(rec + 4) = 100;     // references (never reach 0: its deleting destructor is not ours to run)
        u8* node = (u8*)A(0x20);
        *(DWORD*)node = dead;
        *(u8**)(node + 8) = rec;
        (*(DWORD**)(m24 + 4))[dead % 3] = (DWORD)(uintptr_t)node;
        // the lock-key map world+0x8B8 (buckets +0x8BC, count +0x8C0, size +0x8C4; node: key +0/+4, next +0x20): holds the dead id
        DWORD* kb = (DWORD*)A(3 * 4);
        u8* kn = (u8*)A(0x28);
        *(DWORD*)kn = dead;
        kb[dead % 2] = (DWORD)(uintptr_t)kn;
        kb[2] = kSentinel;
        *(DWORD**)(world + 0x8BC) = kb;
        *(DWORD*)(world + 0x8C0) = 2;
        *(DWORD*)(world + 0x8C4) = 1;
        world[0x994] = 1;
        // a live object without a footprint component, in the id hash
        u8* obj = (u8*)A(0x80);
        *(LONG*)(obj + 4) = 5;
        *(DWORD*)(obj + 8) = live;
        *(DWORD*)(svc + 0x10 + (live % 0x409) * 4) = (DWORD)(uintptr_t)obj;
        u8* deadObj = (u8*)A(0x80);  // the destroyed object: no longer in the hash
        *(DWORD*)(deadObj + 8) = dead;
        g_discRemoveOrig = (DiscRemoveFn)(uintptr_t)DISC_REMOVE;  // the game's code (no detour on the mapped image)
        g_rgLock = world + 0x918;
        g_async.owner = GetCurrentThreadId();
        Job(false);
        k.discMode = 1;
        k.discDestroyMode = 1;
        k.discSweepMode = 1;
        // the dead id's Remove at the drain: dropped, the sweep runs the game's 0x635700 (erase path, nothing removed)
        Job(true);
        assert(DiscRemoveHook(world, NULL, deadObj) == 1 && g_discCount == 1);
        Job(false);
        DiscPoll();
        assert(g_discCount == 0 && k.discDropRemove == 1 && k.discDeadSweep == 1 && k.discDeadHit == 0 && k.discDeadKeyMap == 1);
        assert((*(DWORD**)(m24 + 4))[dead % 3] == 0 && *(DWORD*)(m24 + 0xC) == 0 && world[0x994] == 0);
        assert(*(LONG*)(rec + 4) == 99 && *(u8**)(pool + 0x10) == node);  // the map's reference dropped, node back in the pool
        assert(*(LONG*)(world + 0x918 + 0x20) == 0);
        // the destroy gate on the live object: the game's Remove runs (returns 0 with no footprint component), reference balanced
        Job(true);
        DiscRemoveHook(world, NULL, obj);
        DiscRemoveHook(world, NULL, deadObj);
        Job(false);
        DiscOnDestroy(live, 0, true);
        assert(k.discDestroyApplied == 1 && k.discApplied == 1 && g_discCount == 1 && *(LONG*)(obj + 4) == 5);
        assert(*(LONG*)(world + 0x918 + 0x20) == 0);
        DiscPoll();  // the dead id again: the map no longer has it, the sweep finds nothing
        assert(g_discCount == 0 && k.discDeadSweep == 2 && k.discDeadHit == 0 && k.discDropRemove == k.discDeadSweep);
        printf("G: D-X on the game's code: dead-id sweep 0x635700 (lot 0, erase path) + lock-key find 0x82AD60 (%ld key-map "
               "hit); destroy gate ran the game's Remove 0x834860 through FindByIdAddRef / release, references balanced; T-D5 %ld = %ld\n",
               k.discDeadKeyMap, k.discDropRemove, k.discDeadSweep);
        g_async.owner = 0;
        g_rgLock = NULL;
        k.discMode = 0;
    }
    // G2. the bp-snap-p4 crash on 0.31.2 (docs/94 §21.11): LotRouting_RemoveObjectFootprints 0x635700 with lot 0, on the game's
    //    code. Id X has one FME (Q1) in map M, the next node in bucket order is another id's (Q2), and X's remove on Q1 is
    //    pending in P+0x3C. The game steps over X's FME twice (pending check + Q_FindFootprint 0x6649C0 answering 0), passes the
    //    range end, runs to the bucket sentinel and reads [0xFFFFFFFF + 8] at 0x63576A: the live fault (eip 0x63576A, address
    //    7, esi -1). Then the 0.31.4 guard SnLotRemovePending: flushes Q1 first (0x666A40 swapped for a fake that does what the
    //    flush does to these maps) and the game's call returns; an iterated Q1 (+0x48 > 0) is counted and not flushed.
    {
        *(u8**)(uintptr_t)0x011D9128 = NULL;  // the lot mark 0x62D9F0 at the end returns at once
        const DWORD X = 0x10, Y = 0x11;       // buckets 1 and 2 of 3
        auto newMap = [](DWORD n, DWORD size) {
            u8* m = (u8*)A(0x20);
            DWORD* bk = (DWORD*)A((n + 1) * 4);
            bk[n] = 0xFFFFFFFF;  // the game's EASTL bucket sentinel (the live esi at the fault)
            *(DWORD**)(m + 4) = bk, *(DWORD*)(m + 8) = n, *(DWORD*)(m + 0xC) = size;
            return m;
        };
        auto node = [](DWORD lo, void* v) { u8* n = (u8*)A(0x18); *(DWORD*)n = lo; *(void**)(n + 8) = v; return n; };
        u8* world = (u8*)A(0xA00);
        u8* p = (u8*)A(0x200);
        *(u8**)(world + 0x95C) = p;
        static const u8* s_world;
        s_world = world;
        g_spWorldSlot = &s_world;
        u8 *q1 = (u8*)A(0x58), *q2 = (u8*)A(0x58), *fx = (u8*)A(0x14), *fy = (u8*)A(0x14);
        *(u8**)(q1 + 0x20) = p, *(u8**)(q2 + 0x20) = p;
        *(LONG*)(fx + 4) = 5, *(u8**)(fx + 0xC) = q1;
        *(LONG*)(fy + 4) = 5, *(u8**)(fy + 0xC) = q2;
        static u8 *s_m24, *s_m3c, *s_q1;
        static int s_flushes;
        auto setup = [&](LONG iter) {
            s_m24 = newMap(3, 2), s_m3c = newMap(3, 1), s_q1 = q1;
            (*(DWORD**)(s_m24 + 4))[X % 3] = (DWORD)(uintptr_t)node(X, fx);
            (*(DWORD**)(s_m24 + 4))[Y % 3] = (DWORD)(uintptr_t)node(Y, fy);
            (*(DWORD**)(s_m3c + 4))[X % 3] = (DWORD)(uintptr_t)node(X, q1);
            *(u8**)(p + 0x24) = s_m24, *(u8**)(p + 0x3C) = s_m3c, *(u8**)(p + 0x30) = newMap(3, 0);
            *(LONG*)(q1 + 0x48) = iter, q1[0x4C] = 1;
        };
        struct G2 {
            static DWORD Run(DWORD id) {  // the game's 0x635700 (lot 0); returns the fault address read, 0 = returned
                DWORD at = 0, addr = 0;
                __try { g_lotRemove(*(void* const*)(s_world + 0x95C), NULL, 0, 0, id, 0); }
                __except (at = (DWORD)(uintptr_t)GetExceptionInformation()->ExceptionRecord->ExceptionAddress,
                          addr = (DWORD)GetExceptionInformation()->ExceptionRecord->ExceptionInformation[1], EXCEPTION_EXECUTE_HANDLER) {
                    return at == 0x0063576A ? addr : 0xDEAD;
                }
                return 0;
            }
            static void __fastcall Flush(void* q, void*) {  // the flush's effect here: X's pending node and X's FME leave
                s_flushes++;
                assert(q == s_q1);
                (*(DWORD**)(s_m3c + 4))[0x10 % 3] = 0, *(DWORD*)(s_m3c + 0xC) = 0;
                (*(DWORD**)(s_m24 + 4))[0x10 % 3] = 0, *(DWORD*)(s_m24 + 0xC) = 1;
                ((u8*)q)[0x4C] = 0;
            }
        };
        setup(0);
        const DWORD crashAddr = G2::Run(X);
        assert(crashAddr == 7);  // reproduced: the live fault, reading 0x00000007 at 0x0063576A
        g_snFlushDeferred = G2::Flush;
        setup(0);
        SnLotRemovePending(X, 0);
        assert(s_flushes == 1 && k.lrPendFlush == 1 && k.lrPendBusy == 0 && G2::Run(X) == 0);
        SnLotRemovePending(Y, 0);  // no pending remove for Y: nothing flushed
        assert(s_flushes == 1 && k.lrPendFlush == 1);
        setup(1);  // Q1 iterated: not flushed, counted; the game's walk still runs off (the precondition stays)
        SnLotRemovePending(X, 0);
        assert(s_flushes == 1 && k.lrPendBusy == 1 && G2::Run(X) == 7);
        // 0.31.5 (docs/94 §21.12): design D's own lot-0 call on the same state, through the [LotRemoveGuard] hook as live (the
        // detour sits on 0x635700's entry; g_lrOrig = the game's code here). X's Remove is deferred during a job; at the drain
        // X is gone, so the dead-id sweep runs 0x635700 (lot 0) with X's remove still pending on Q1: the guard flushes Q1 first
        // and the game's walk returns (the same call without the guard is G2::Run above: reads 7 at 0x63576A).
        {
            u8* deadX = (u8*)A(0x80);
            *(DWORD*)(deadX + 8) = X;  // not in G's id hash: dropped at the drain, swept
            world[0x974] = 1;
            g_rgLock = world + 0x918;
            g_async.owner = GetCurrentThreadId();
            k.discMode = 1, k.discSweepMode = 1;
            g_lotRemove = LrGuardHook;
            setup(0);
            const LONG f0 = k.lrPendFlush, s0 = k.discDeadSweep;
            Job(true);
            assert(DiscRemoveHook(world, NULL, deadX) == 1 && g_discCount == 1);
            Job(false);
            DiscPoll();
            assert(g_discCount == 0 && k.discDeadSweep == s0 + 1 && k.lrPendFlush == f0 + 1 && s_flushes == 2);
            assert(*(DWORD*)(s_m3c + 0xC) == 0 && *(DWORD*)(s_m24 + 0xC) == 1 && *(LONG*)(world + 0x918 + 0x20) == 0);
            g_lotRemove = (LotRemoveFn)(uintptr_t)0x00635700;
            g_async.owner = 0;
            g_rgLock = NULL;
            k.discMode = 0;
            printf("G2/D: dead-id sweep 0x635700 (lot 0) from DiscDrain with X's remove pending: [LotRemoveGuard] flushed Q1, "
                   "the game's walk returned\n");
        }
        g_snFlushDeferred = (SnQFn)(uintptr_t)0x00666A40;
        g_spWorldSlot = (const u8* const*)(uintptr_t)0x011D9128;
        printf("G2: 0x635700 lot 0 with a pending remove on the id's last FME: the game's walk faults reading 0x%08lX at 0x0063576A "
               "(the bp-snap-p4 crash); after SnLotRemovePending's flush it returns; an iterated Q is counted (%ld) and not flushed\n",
               crashAddr, k.lrPendBusy);
    }
    // DG. design G standalone ([Discourage] DestroyPark, p5c.cpp P5cPark / P5cFlush outside a P5c window; docs/88 §6, §10):
    //     the game's own ObjectService_DestroyById 0x939420, FindById 0x939560, FindByIdAddRef 0x9395A0 (through P5cPark) and
    //     release 0x7460F0 on a fake object service (0x409 buckets at +0x10, end value [+0x1034], count [+0x1038]). A job
    //     thread looks objects up by id (raw pointers; some also take a reference, the 0x643010 pattern), the main (owner)
    //     thread destroys some of them, then the job reads them and drops its references. Park on: no read of a destroyed-and-
    //     freed block, no destructor on the job thread, each destroyed object freed exactly once at the flush. Park off without
    //     the wait (option P): the negative control must see both hazards. (Revived from the unmerged 0.30.0 branch 14a25ef.)
    {
        typedef u8(__fastcall* DestroyFn)(void* svc, void*, DWORD lo, DWORD hi);   // ret 8
        typedef u8*(__fastcall* FindFn)(void* svc, void*, DWORD lo, DWORD hi);     // ret 8, NULL on a miss
        static const DestroyFn destroyById = (DestroyFn)(uintptr_t)0x00939420;
        static const FindFn findById = (FindFn)(uintptr_t)0x00939560;
        assert(Matches((const void*)(uintptr_t)0x00939420, kRtSites[7].pro, kRtSites[7].len) && kRtSites[7].kind == RT_DESTROY);
        assert(*(const DWORD*)(uintptr_t)(0x011DC2A8 + 5 * 4) == 0 && *(const DWORD*)(uintptr_t)(0x011DC2A8 + 6 * 4) == 0);  // no event 5/6 handlers
        static const int kN = 32;
        struct DgJob {
            u8* svc; DWORD ids[kN]; int n; bool ref[kN]; HANDLE go, looked, go2, done; DWORD mainTid;
            volatile LONG poisoned, jobDtors, frees, stop;
        };
        static DgJob J;
        struct DgFk {
            static void __fastcall Dtor(u8* obj, void*, DWORD flag) {  // vtable slot 0, deleting destructor (ret 4)
                assert(flag == 1);
                *(DWORD*)(obj + 0x18) = 0xDDDDDDDD;  // poisoned, not freed: the negative control keeps running
                InterlockedIncrement(&J.frees);
                if (GetCurrentThreadId() != J.mainTid) InterlockedIncrement(&J.jobDtors);
            }
            static void* __cdecl Svc() { return J.svc; }
            static DWORD WINAPI Job(void*) {
                u8* p[kN];
                for (;;) {
                    WaitForSingleObject(J.go, INFINITE);
                    if (J.stop) return 0;
                    for (int i = 0; i < J.n; i++) {
                        p[i] = findById(J.svc, NULL, J.ids[i], 0);
                        assert(p[i]);
                        if (J.ref[i]) InterlockedIncrement((LONG*)(p[i] + 4));
                    }
                    SetEvent(J.looked);
                    WaitForSingleObject(J.go2, INFINITE);
                    for (int i = 0; i < J.n; i++) {
                        if (*(volatile DWORD*)(p[i] + 0x18) == 0xDDDDDDDD) InterlockedIncrement(&J.poisoned);
                        if (J.ref[i]) g_objRelease((void**)&p[i], NULL);
                    }
                    SetEvent(J.done);
                }
            }
        };
        static DWORD vt[1] = {(DWORD)(uintptr_t)DgFk::Dtor};
        static u8 objs[kN][0x40];
        J.svc = (u8*)A(0x1200);
        *(DWORD*)(J.svc + 0x1034) = kSentinel;  // the bucket walk of the erase stops on it (non-zero)
        J.mainTid = GetCurrentThreadId();
        J.go = CreateEventW(NULL, FALSE, FALSE, NULL);
        J.looked = CreateEventW(NULL, FALSE, FALSE, NULL);
        J.go2 = CreateEventW(NULL, FALSE, FALSE, NULL);
        J.done = CreateEventW(NULL, FALSE, FALSE, NULL);
        HANDLE dgTh = CreateThread(NULL, 0, DgFk::Job, NULL, 0, NULL);
        const SvcFn svcSave = g_objSvc;
        g_objSvc = DgFk::Svc;
        g_async.owner = J.mainTid;
        g_discCount = 0;
        g_p5cJob = 0, g_p5bWin = 0;  // standalone: no P5c window
        k.rtClosing = 0;
        k.dpReady = 1;
        long keptTotal = 0, offPoisoned = 0, offJobDtors = 0;
        const LONG low0 = k.p5cCountLow;
        const int runs = cases + cases / 4;
        for (int c = 0; c < runs; c++) {
            const bool on = c < cases;  // the last quarter: park off, no wait (negative control)
            J.n = 4 + (int)Rnd(kN - 3);
            bool kill[kN];
            int kills = 0;
            for (int i = 0; i < J.n; i++) {
                u8* o = objs[i];
                memset(o, 0, 0x40);
                *(DWORD**)o = vt;
                *(LONG*)(o + 4) = 1;  // the hash's reference
                J.ids[i] = (Rnd(0x10000) << 8) | (DWORD)i;  // unique per case (low byte), random bucket
                *(DWORD*)(o + 8) = J.ids[i];
                *(DWORD*)(o + 0x18) = 0x5AFE0000 | i;
                DWORD* bucket = (DWORD*)(J.svc + 0x10 + (J.ids[i] % 0x409) * 4);
                *(DWORD*)(o + 0x10) = *bucket;
                *bucket = (DWORD)(uintptr_t)o;
                ++*(DWORD*)(J.svc + 0x1038);
                J.ref[i] = Rnd(2) != 0;
                kill[i] = Rnd(2) != 0;
                kills += kill[i] ? 1 : 0;
            }
            J.poisoned = J.jobDtors = J.frees = 0;
            k.dpMode = on ? 1 : 0;
            g_async.state = A_RUNNING;
            SetEvent(J.go);
            WaitForSingleObject(J.looked, INFINITE);
            const LONG parked0 = k.dpParked;
            for (int i = 0; i < J.n; i++) {
                if (!kill[i]) continue;
                assert(P5cPark(J.ids[i], 0) == on);
                assert(destroyById(J.svc, NULL, J.ids[i], 0) == 1);
                assert(findById(J.svc, NULL, J.ids[i], 0) == NULL);
            }
            if (on) assert(J.frees == 0 && k.dpParked - parked0 == kills && g_p5cGyN == kills);
            P5cFlush();  // a flush point while the job runs: refused
            if (on) assert(J.frees == 0 && g_p5cGyN == kills);
            SetEvent(J.go2);
            WaitForSingleObject(J.done, INFINITE);
            if (on) assert(J.frees == 0);  // still parked: no flush since the job
            g_async.state = A_IDLE;
            P5cFlush();
            assert(g_p5cGyN == 0 && *(DWORD*)(J.svc + 0x1038) == (DWORD)(J.n - kills));
            if (on) {
                assert(J.poisoned == 0 && J.jobDtors == 0 && J.frees == kills);
                keptTotal += kills;
            } else {
                offPoisoned += J.poisoned;
                offJobDtors += J.jobDtors;
            }
            for (int i = 0; i < J.n; i++) {
                if (kill[i]) { assert(*(DWORD*)(objs[i] + 0x18) == 0xDDDDDDDD); continue; }  // freed
                assert(*(LONG*)(objs[i] + 4) == 1 && *(DWORD*)(objs[i] + 0x18) == (0x5AFE0000 | (DWORD)i));
                assert(destroyById(J.svc, NULL, J.ids[i], 0) == 1);  // no job: survivors go as today
            }
            assert(*(DWORD*)(J.svc + 0x1038) == 0 && J.frees == J.n);
            for (int b = 0; b < 0x409; b++) assert(((DWORD*)(J.svc + 0x10))[b] == 0);
        }
        assert(offPoisoned > 0 && offJobDtors > 0 && k.p5cCountLow == low0);
        J.stop = 1;
        SetEvent(J.go);
        WaitForSingleObject(dgTh, INFINITE);
        CloseHandle(dgTh);
        g_objSvc = svcSave;
        g_async.owner = 0;
        k.dpMode = 0, k.dpReady = 0;
        printf("DG: design G standalone on the game's DestroyById / FindById / FindByIdAddRef / release: %d cases, %ld objects "
               "parked and freed once at the flush, 0 freed reads, 0 job-thread destructors; negative control (no wait, no park, "
               "%d cases): %ld freed reads, %ld job-thread destructors\n", cases, keptTotal, cases / 4, offPoisoned, offJobDtors);
    }
    // K. design SH (slotro.cpp, docs/90): the gate-skip decision and the interlocked portal count pair on the game's own code.
    // SH runs the game's own 0x76F0A0 body under a QL-shared hold; the hold changes no computation, it only lets RgEnter skip
    // the +0x918 wait at gate 12 (LotTable_Contains) and gate-0 hits (GetOrInsert). So equal answers are structural; the real
    // correctness is (a) SlGate's decision matches the game's Contains / GetOrInsert on random tables, and (b) PortalAddRefX /
    // PortalReleaseX behave as the game's 0x64E8E0 / 0x64E8F0 under contention. Both run the mapped game functions. Seeds 1-4.
    {
        typedef u8(__fastcall * ContainsFn)(const u8*, void*, DWORD, DWORD);        // 0x628A90, ret 8
        typedef const u8*(__fastcall * GetOrInsertFn)(const u8*, void*, const DWORD*);  // 0x62EAB0 hit, ret 4
        typedef LONG(__fastcall * RefFn)(u8*, void*);
        assert(InstallPortalRefX());  // the interlocked pair into vtable 0xFED048
        assert(k.slRefSwap == 0);     // set only by InstallSlotRo; here we installed the pair directly
        const RefFn gAdd = (RefFn)(uintptr_t)0x0064E8E0, gRel = (RefFn)(uintptr_t)0x0064E8F0;
        static u8 fwm[0x200], fact[0x60];
        *(u8**)(uintptr_t)0x011ECBC4 = fwm;
        *(u8**)(fwm + 0xB0) = fact;
        long gateCases = 0, pass12 = 0, pass0 = 0, drop = 0, refCases = 0;
        for (int seed = 1; seed <= 4; seed++) {
            g_seed = (DWORD)seed;
            for (int c = 0; c < 3000; c++) {
                // a random table: cnt buckets, several lots, each a node whose entry (node+0x10) repeats the key
                const DWORD cnt = 1 + Rnd(8);
                u8* p = (u8*)A(0x200);
                DWORD* bk = (DWORD*)A((cnt + 1) * 4);
                bk[cnt] = kSentinel;
                *(DWORD**)(p + 0x4C) = bk;
                *(DWORD*)(p + 0x50) = cnt;
                const int nLots = 1 + (int)Rnd(8);
                DWORD lo[8], hi[8];
                for (int i = 0; i < nLots; i++) {
                    lo[i] = 1 + Rnd(cnt * 4) + (DWORD)i * 0x100;
                    hi[i] = Rnd(3) ? 0 : Rnd(2);
                    u8* node = (u8*)A(0x200);
                    *(DWORD*)node = lo[i], *(DWORD*)(node + 4) = hi[i];
                    u8* e = node + 0x10;
                    *(DWORD*)e = lo[i], *(DWORD*)(e + 4) = hi[i];
                    const int np = (int)Rnd(4);
                    if (np) {
                        DWORD* v = (DWORD*)A(4 * np);
                        for (int j = 0; j < np; j++) {
                            u8* pt = (u8*)A(0x80);
                            *(DWORD*)pt = LT_PORTAL_VT;
                            *(DWORD*)(pt + 8) = 0x5000 + Rnd(2), *(DWORD*)(pt + 0xC) = 7;
                            *(LONG*)(pt + 0x70) = Rnd(8) ? 1 + (LONG)Rnd(3) : 0;  // rarely a count the clone must not Release
                            v[j] = (DWORD)(uintptr_t)pt;
                        }
                        *(DWORD**)(e + 0x44) = v, *(DWORD**)(e + 0x48) = v + np;
                    }
                    const DWORD b = lo[i] % cnt;
                    *(DWORD*)(node + 0x190) = bk[b];
                    bk[b] = (DWORD)(uintptr_t)node;
                }
                *(DWORD*)(fact + 0x48) = Rnd(8) ? 0x77777777 : lo[Rnd(nLots)];
                *(DWORD*)(fact + 0x4C) = 0;
                // (a) gate 12: the game's Contains vs LtFind's hit/miss; a hit also feeds SlGate via a PortalsForObject frame
                DWORD qlo, qhi;
                const DWORD r = Rnd(10);
                if (r == 0) qlo = qhi = 0;
                else if (r == 1) { qlo = *(DWORD*)(fact + 0x48); qhi = *(DWORD*)(fact + 0x4C); }
                else if (r < 4) { qlo = Rnd(0x10000); qhi = Rnd(2); }
                else { const int i = (int)Rnd(nLots); qlo = lo[i]; qhi = hi[i]; }
                const bool gameHas = ((ContainsFn)(uintptr_t)0x00628A90)(p, NULL, qlo, qhi) != 0;
                const u8* e;
                const int why = LtFind(p, qlo, qhi, &e);
                assert(why == LT_OK && (e != NULL) == gameHas);  // the clone's find matches the game's Contains
                DWORD fr[16] = {};
                DWORD* slot = fr + 4;
                slot[-2] = (DWORD)(uintptr_t)p, slot[0] = SL_PFO_RET, slot[1] = qlo, slot[2] = qhi, slot[9] = 0x5000, slot[10] = 7;
                t_slHold = 1;  // SlGate drops the hold on a bad portal; set it each time
                t_qlShared = 1;
                AcquireSRWLockShared(&g_ql);
                const bool p12 = SlGate(12, slot);
                if (t_slHold == 1) { t_qlShared = 0; ReleaseSRWLockShared(&g_ql); t_slHold = 0; }
                // a pass is sound whenever the object's portals (if the lot is present) are the interlocked class with count >= 1
                bool badPortal = false;
                if (e)
                    for (const DWORD* it = *(const DWORD* const*)(e + 0x44); it && it != *(const DWORD* const*)(e + 0x48); it++) {
                        const u8* pt = (const u8*)(uintptr_t)*it;
                        if (pt && *(const DWORD*)(pt + 8) == slot[9] && *(const DWORD*)(pt + 0xC) == 7 &&
                            *(const LONG*)(pt + 0x70) < 1)
                            badPortal = true;
                    }
                assert(p12 == !badPortal);
                p12 ? pass12++ : drop++;
                gateCases++;
                // (b) gate 0: on a present key, the game's GetOrInsert hit returns node+0x10; SlGate must pass with the same entry
                if (e) {
                    DWORD key[2] = {qlo, qhi};
                    const u8* gi = ((GetOrInsertFn)(uintptr_t)0x0062EAB0)((const u8*)(p + 0x48), NULL, key);
                    assert(gi == e);  // the game's hit node+0x10 equals LtFind's entry
                    DWORD f0[16] = {};
                    DWORD* s0 = f0 + 4;
                    s0[-2] = (DWORD)(uintptr_t)(p + 0x48), s0[0] = 0x0062F7A2, s0[1] = (DWORD)(uintptr_t)key;
                    t_slHold = 1;
                    t_qlShared = 1;
                    AcquireSRWLockShared(&g_ql);
                    const bool g0ok = SlGate(0, s0);
                    if (t_slHold == 1) { t_qlShared = 0; ReleaseSRWLockShared(&g_ql); t_slHold = 0; }
                    assert(g0ok);
                    pass0++;
                }
                // (c) the interlocked count pair vs the game's, on a live portal
                u8* pt = (u8*)A(0x80);
                *(DWORD*)pt = LT_PORTAL_VT;
                *(LONG*)(pt + 0x70) = 1;
                const LONG a1 = gAdd(pt, NULL), a2 = PortalAddRefX(pt, NULL);  // 2 then 3
                assert(a1 == 2 && a2 == 3);
                const LONG r1 = PortalReleaseX(pt, NULL), r2 = gRel(pt, NULL);  // 2 then 1
                assert(r1 == 2 && r2 == 1 && *(LONG*)(pt + 0x70) == 1);
                refCases++;
            }
        }
        // restore the plain pair so later code (none here) sees the game's vtable
        DWORD* vt = (DWORD*)(uintptr_t)LT_PORTAL_VT;
        vt[0] = 0x0064E8E0, vt[1] = 0x0064E8F0;
        printf("K: SH gate decision on the game's Contains / GetOrInsert: %ld cases (gate-12 pass %ld, drop %ld; gate-0 pass %ld), "
               "interlocked portal pair vs the game's: %ld, all equal\n", gateCases, pass12, drop, pass0, refCases);
        assert(gateCases == 4 * 3000 && pass12 > gateCases / 4 && drop > 0 && pass0 > 0);
        g_seed = 12345;
    }
    // H. design H2 (locowin.cpp, docs/86 §6): InstallLocoWindow on the game's bytes at 0xD81C5C (refused when one differs);
    // the patched rel32 called the way MonoScriptHost_Simulate calls it (ecx, one float pushed, callee pops it); the mapped
    // 0x83CED0 replaced by a fake pass that runs the game's Locomotion_ObstacleCheck: answers equal to the call without the
    // window, the window up only inside (Mode 1) or never (Mode 0), nothing left open after
    {
        static const u8 kUpdatePro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0x81, 0xEC, 0x84, 0x00, 0x00, 0x00};
        assert(Matches((const void*)(uintptr_t)LOCO_UPDATE, kUpdatePro, sizeof(kUpdatePro)));
        assert(Matches((const void*)(uintptr_t)0x0083D383, (const u8*)"\xC2\x04\x00", 3));  // its one return: ret 4
        wchar_t ini[MAX_PATH];
        GetTempPathW(MAX_PATH, ini);
        swprintf_s(ini + wcslen(ini), MAX_PATH - wcslen(ini), L"steerro-test-h2-%lu.ini", GetCurrentProcessId());  // parallel runs
        WritePrivateProfileStringW(L"LocoWindow", L"Enabled", L"1", ini);
        WritePrivateProfileStringW(L"LocoWindow", L"Mode", L"1", ini);
        const LONG enf = k.rgEnforce, ready = k.asyncReady;
        k.rgEnforce = k.asyncReady = 1;
        u8* site = (u8*)(uintptr_t)LOCO_CALL;
        const LONG rel0 = *(LONG*)(site + 1);
        site[-4] = 0x90;  // `push ecx` changed: refused, nothing written
        InstallLocoWindow(ini);
        assert(k.g0wReady == 0 && *(LONG*)(site + 1) == rel0);
        site[-4] = 0x51;
        InstallLocoWindow(ini);
        DeleteFileW(ini);
        const DWORD target = LOCO_CALL + 5 + *(LONG*)(site + 1);
        assert(k.g0wReady == 1 && k.g0wMode == 1 && target == (DWORD)(uintptr_t)LocoWinThunk && g_g0wOrig == (LocoUpdateFn)(uintptr_t)LOCO_UPDATE);
        // the caller's sequence: mov ecx,self; fld [dt]; push ecx; fstp [esp]; call <the site's target>; ret
        static float hDt = 0.25f;
        static void* const kSelf = (void*)0x5A5A0001;
        u8* stub = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        u8* p = stub;
        *p++ = 0xB9, *(void**)p = kSelf, p += 4;
        *p++ = 0xD9, *p++ = 0x05, *(float**)p = &hDt, p += 4;
        *p++ = 0x51, *p++ = 0xD9, *p++ = 0x1C, *p++ = 0x24;
        *p++ = 0xE8, *(LONG*)p = (LONG)(target - (DWORD)(uintptr_t)(p + 4)), p += 4;
        *p++ = 0xC3;
        static u8* hAgent;
        static IdVec* hIds;
        static long hRuns;
        struct H {
            static void __fastcall Pass(void* self, void*, float dt) {
                const Ts3nCounters& c = ts3native_counters;
                assert(self == kSelf && dt == hDt && g_g0wIn == 1 && g_g0Win == (c.g0wMode == 1 ? 1 : 0) && t_g0wDepth == 1);
                ((GameLocoFn)(uintptr_t)STEER_LOCO)(hAgent, NULL, hIds, NULL, 0, NULL, 0);
                hRuns++;
            }
        };
        Jmp(LOCO_UPDATE, (void*)H::Pass);
        const int hCases = cases;
        long equal = 0, withIds = 0;
        for (int mode = 1; mode >= 0; mode--) {
            k.g0wMode = mode;
            for (int i = 0; i < hCases; i++) {
                BuildWorld();
                Agent ag = NewAgent(Rnd(4) == 0);
                IdVec a, b;
                NewIds(&a, 2);
                b = a;
                b.b = (DWORD*)_aligned_malloc(8 * 3, 16);
                memcpy(b.b, a.b, 16);
                b.e = b.b + 4, b.c = b.b + 6;
                hAgent = ag.a;
                hIds = &a;
                ((void(__cdecl*)())stub)();
                ((GameLocoFn)(uintptr_t)STEER_LOCO)(ag.a, NULL, &b, NULL, 0, NULL, 0);  // the same call, no window
                assert(a.e - a.b == b.e - b.b && memcmp(a.b, b.b, (a.e - a.b) * 4) == 0);
                assert(g_g0Win == 0 && g_g0wIn == 0 && t_g0wDepth == 0);
                equal++;
                withIds += a.e - a.b > 4;
                _aligned_free(a.b);
                _aligned_free(b.b);
            }
        }
        assert(hRuns == 2 * hCases && k.g0wPasses == 2 * hCases && withIds > 0 && g_rgOffSimOpen == 0);
        assert(k.g0wJobsHeld == 0 && k.g0wJobStartInPass == 0 && k.g0wDrop[0] == 0 && k.g0wDrop[1] == 0 && k.g0wWatchdog == 0);
        printf("H: loco window on the game's call site 0xD81C5C (refused on a changed byte); %ld passes through the patched rel32 "
               "(Mode 1 and 0), the game's 0x85FD10 inside: %ld answers equal to the call without the window (%ld with new ids); "
               "window closed after each\n", hRuns, equal, withIds);
        k.rgEnforce = enf;
        k.asyncReady = ready;
        k.g0wReady = 0;
    }
    // I. design A10 (a10.cpp, docs/87 §6): the four route ICs' bytes, Route_IsActive reads route+0x60 and
    // Route_StopFollowingRoute's helper clears it; InstallA10 on the game's prologues (refused per IC when a byte differs);
    // each patched IC called the way Mono calls an internal call (cdecl, hidden result pointer, floats by value) reaches our
    // hook with its arguments and, with no job, the game's own IC through the trampoline (route manager 0x11D40C4 is NULL in
    // the mapped image: the IC's own "no manager" path, status 2), stack balanced, result pointer returned. The job queue
    // itself (kinds, collect rules, D, destroy, random order) runs in selftest TestA10 on fake originals.
    {
        assert(Matches((const void*)(uintptr_t)IC_ROUTE_PLAN, kIcPlanPrologue, sizeof(kIcPlanPrologue)));
        static const u8 kIsActive[] = {0x8B, 0x0D, 0xC4, 0x40, 0x1D, 0x01, 0x85, 0xC9, 0x56, 0x74, 0x2B};
        assert(Matches((const void*)(uintptr_t)0x0074CFC0, kIsActive, sizeof(kIsActive)));
        assert(Matches((const void*)(uintptr_t)0x0074CFEA, (const u8*)"\x8A\x5E\x60", 3));          // mov bl,[esi+60h]
        assert(Matches((const void*)(uintptr_t)0x00753294, (const u8*)"\xC6\x47\x60\x00", 4));      // mov byte [edi+60h],0
        assert(callsTo(0x0075766C, 0x00753230));  // in IC_Route_StopFollowingRoute 0x757640
        assert(callsTo(0x007654C8, 0x007641D0) && callsTo(0x007655C3, 0x007642C0) && callsTo(0x0076542C, 0x007642C0));
        assert(*(const DWORD*)(uintptr_t)0x011D40C4 == 0);
        wchar_t ini[MAX_PATH];
        GetTempPathW(MAX_PATH, ini);
        swprintf_s(ini + wcslen(ini), MAX_PATH - wcslen(ini), L"steerro-test-a10-%lu.ini", GetCurrentProcessId());  // parallel runs
        WritePrivateProfileStringW(L"RouteAsync", L"Kinds", L"7", ini);
        const LONG ready = k.asyncReady;
        const IcPlanFn savedIc = g_icPlanOrig;
        k.asyncReady = 1;
        g_icPlanOrig = (IcPlanFn)(uintptr_t)IC_ROUTE_PLAN;
        u8* p3 = (u8*)(uintptr_t)kP10Ic[3];
        p3[5] = 0xF8;  // ReplanFromPosition's `and esp,-16` changed: that IC alone is refused
        InstallA10(ini);
        assert(k.arReady == (kArBuilt & 3) && k.arMode == 1 && p3[0] == 0x55);
        p3[5] = 0xF0;
        WritePrivateProfileStringW(L"RouteAsync", L"Kinds", L"4", ini);
        InstallA10(ini);
        DeleteFileW(ini);
        assert(k.arReady == kArBuilt);
        for (int i = 1; i < 4; i++) {
            if (!(kArBuilt >> (i - 1) & 1)) continue;
            const u8* e = (const u8*)(uintptr_t)kP10Ic[i];
            assert(e[0] == 0xE9 && kP10Ic[i] + 5 + *(const LONG*)(e + 1) == (DWORD)(uintptr_t)kArHooks[i]);
            assert(memcmp(g_arOrig[i], kP10Pro[i], 6) == 0);
        }
        static u8 res[0x48];
        static u8* resAddr = res;  // pushed as a value: clang-cl 23 crashes on `push offset <static>`
        static DWORD iEsp0, iEsp1, iOut, iFn;
        static float fx = 1.5f, fy = -2.0f, fz = 3.25f;
        for (int i = 1; i < 4; i++) {
            if (!(kArBuilt >> (i - 1) & 1)) continue;
            memset(res, 0xCC, sizeof(res));
            iFn = kP10Ic[i];
            if (i == 1) {
                __asm {
                    mov iEsp0, esp
                    push 0x25000077
                    push resAddr
                    call iFn
                    add esp, 8
                    mov iOut, eax
                    mov iEsp1, esp
                }
            } else {
                __asm {
                    mov iEsp0, esp
                    push fz
                    push fy
                    push fx
                    push 0x25000077
                    push resAddr
                    call iFn
                    add esp, 20
                    mov iOut, eax
                    mov iEsp1, esp
                }
            }
            assert(iEsp0 == iEsp1 && iOut == (DWORD)(uintptr_t)res && *(const DWORD*)res == 2 && *(const DWORD*)(res + 4) == 0);
            assert(k.arSyncN[i - 1] == 1 && k.arStarted[i - 1] == 0);
        }
        printf("I: A10 detours on the game's three IC prologues (refused on a changed byte); IC calls through them reach the "
               "hook and, with no job, the game's IC via the trampoline (status 2, stack balanced); Route_IsActive / "
               "route+0x60 bytes and the start-point call edges match\n");
        k.asyncReady = ready;
        g_icPlanOrig = savedIc;
        memset(&k.arReady, 0, sizeof(LONG) * 32);
    }
    // J. [ObstructedRO] (obstructro.cpp, docs/86 §10): the game's IC_Route_IsPointObstructedBySim 0x751480 (its own transform
    // 0x62EF30, obstacle query 0x82EDB0, ignore list 0x81C970, point test 0x80F030 -> 0x67B130) vs OrRun on the section A
    // worlds; then the detour on the mapped 0x751480: job -> clone, no job -> the game's code, shadow, QL busy -> the game's code
    {
        assert(Matches((const void*)(uintptr_t)OR_IC, kOrPro, sizeof(kOrPro)));
        // the IC's call edges the clone mirrors (rel32 targets read from the image)
        assert(callsTo(0x007514A3, 0x0074B2C0) && callsTo(0x007514BF, 0x008292D0) && callsTo(0x007514CA, 0x00829BC0) &&
               callsTo(0x0075156E, 0x00C68990) && callsTo(0x007515A0, 0x00ADD860) && callsTo(0x0075167A, 0x0062EF30) &&
               callsTo(0x007516B3, 0x0082EDB0) && callsTo(0x00751739, 0x0081C970));
        Jmp(0x0074B2C0, (void*)I::Route);
        Jmp(0x00ADD860, (void*)I::Level);
        Jmp(0x00B015B0, (void*)I::Inside);
        Jmp(0x0074A8F0, (void*)I::Height);
        static u8 rmObj[0x10];
        *(u8**)(uintptr_t)0x011D40C4 = rmObj;
        static DWORD rvt[8];
        rvt[4] = (DWORD)(uintptr_t)I::Ref;
        rvt[5] = (DWORD)(uintptr_t)I::Unref;
        I::route = (u8*)A(0x200);
        *(DWORD**)I::route = rvt;
        static DWORD ign[8];
        auto newCase = [&](OrV3* at) {
            BuildWorld();
            for (int i = 0; i < W.nfp; i++) if (Rnd(2)) W.fps[i][0x35] = 0x14;  // more Sims (classes with the +0x35 type)
            const int lot = Rnd(2);
            float x, z;
            WorldPoint(lot, Rf(-4, 68), Rf(-4, 68), &x, &z);
            if (lot == 0 && x >= 0) x = -0.5f;
            if (lot == 1 && x < 0) x = 0.5f;
            if (Rnd(8) == 0) x += lot == 0 ? 50 : 150;  // another lot's frame, or lot 2 (not in the table)
            *at = OrV3{x, Rf(0, 2), z};
            u8* r = I::route;
            *(DWORD*)(r + 0x3C) = Rnd(0x10000);
            const u8* own = W.fps[Rnd(W.nfp)];  // the route's own Sim: one of the ids in play, sometimes
            *(DWORD*)(r + 0x40) = Rnd(3) ? *(const DWORD*)(own + 0x14) : 0x7777;
            *(DWORD*)(r + 0x44) = Rnd(3) ? *(const DWORD*)(own + 0x18) : 0;
            const int ni = (int)Rnd(4);
            for (int i = 0; i < ni; i++) { ign[i * 2] = 0x1000 + Rnd(40); ign[i * 2 + 1] = Rnd(3) ? 0 : 7; }
            *(DWORD**)(r + 0x158 + 0x38) = ign;
            *(DWORD**)(r + 0x158 + 0x3C) = ign + ni * 2;
        };
        g_async.owner = me;
        g_workerTid = me + 7;
        long cmp = 0, hits = 0, fb[8] = {};
        for (int c = 0; c < cases; c++) {
            OrV3 at;
            newCase(&at);
            u64 mine;
            const int i0 = I::refIn, o0 = I::refOut;
            const int why = OrRun(0x25000001, at, false, &mine);  // the clone first: it must not change what the game reads
            assert(I::refIn - i0 == 1 && I::refOut - o0 == 1);
            const u64 game = ((OrFn)(uintptr_t)OR_IC)(0x25000001, at);
            assert(I::refIn - i0 == 2 && I::refOut - o0 == 2);
            RefsOk();  // the game's references are balanced
            fb[why < 8 ? why : 7]++;
            if (why) continue;
            if (game != mine) {
                printf("MISMATCH obstructed: case %d at (%.3f, %.3f): game %08lX:%08lX, clone %08lX:%08lX\n", c, at.x, at.z, (DWORD)(game >> 32),
                       (DWORD)game, (DWORD)(mine >> 32), (DWORD)mine);
                exit(1);
            }
            cmp++;
            hits += game != 0;
        }
        assert(((OrFn)(uintptr_t)OR_IC)(0x25000009, OrV3{0, 0, 0}) == 0);  // no route: 0, nothing else read
        printf("I: game 0x751480 vs clone: %ld cases, %ld compared equal (%ld name a Sim); clone gave up: lot %ld, deferred %ld, "
               "cache %ld, shape %ld, other %ld\n", (long)cases, cmp, hits, fb[SR_FB_LOTMISS], fb[SR_FB_DEFERRED], fb[SR_FB_CACHE],
               fb[SR_FB_SHAPE], cases - cmp - fb[SR_FB_LOTMISS] - fb[SR_FB_DEFERRED] - fb[SR_FB_CACHE] - fb[SR_FB_SHAPE]);
        assert(cmp > cases / 2 && hits > cases / 20);
        // the detour on the mapped 0x751480 (tripwire and W stubs in place since C and F): job -> clone, no job -> the game's code
        g_orOrig = (OrFn)InstallDetour("IC_Route_IsPointObstructedBySim", OR_IC, kOrPro, sizeof(kOrPro), (void*)OrHook, &g_orOrig);
        assert(g_orOrig);
        k.orMode = 1;
        long orN = 0;
        for (int c = 0; c < cases; c++) {
            OrV3 at;
            newCase(&at);
            const LONG t0 = k.orTaken;
            Job(true);
            const u64 a = ((OrFn)(uintptr_t)OR_IC)(0x25000001, at);
            Job(false);
            const u64 b = ((OrFn)(uintptr_t)OR_IC)(0x25000001, at);  // no job: the trampoline
            assert(a == b);
            orN += k.orTaken - t0;
        }
        assert(orN > cases / 2 && I::refIn == I::refOut);
        k.orShadow = 1;
        for (int c = 0; c < cases; c++) {
            OrV3 at;
            newCase(&at);
            ((OrFn)(uintptr_t)OR_IC)(0x25000001, at);
        }
        assert(k.orShadowRuns > cases / 2 && k.orShadowMismatch == 0 && g_rgOffSimOpen == 0);
        k.orShadow = 0;
        // a worker writer holds QL: the hook falls back to the game's code, same answer
        g_wIn = CreateEventW(NULL, FALSE, FALSE, NULL);
        g_wGo = CreateEventW(NULL, FALSE, FALSE, NULL);
        HANDLE qh = CreateThread(NULL, 0, [](void*) -> DWORD {
            AcquireSRWLockExclusive(&g_ql);
            SetEvent(g_wIn);
            WaitForSingleObject(g_wGo, 5000);
            ReleaseSRWLockExclusive(&g_ql);
            return 0;
        }, NULL, 0, NULL);
        WaitForSingleObject(g_wIn, INFINITE);
        long busy = 0;
        for (int c = 0; c < 50; c++) {
            OrV3 at;
            newCase(&at);
            const LONG f0 = k.orFb[OR_FB_TRY - 1];
            Job(true);
            const u64 a = ((OrFn)(uintptr_t)OR_IC)(0x25000001, at);
            Job(false);
            busy += k.orFb[OR_FB_TRY - 1] - f0;
            assert(a == ((OrFn)(uintptr_t)OR_IC)(0x25000001, at));  // no job: the trampoline
        }
        SetEvent(g_wGo);
        WaitForSingleObject(qh, INFINITE);
        CloseHandle(qh);
        printf("I: hook %ld read-only answers equal to the game's; shadow %ld runs, 0 mismatches; QL busy %ld x -> game's call; route "
               "pair balanced (%d / %d)\n", orN, k.orShadowRuns, busy, I::refIn, I::refOut);
        assert(busy > 25);
        k.orMode = 0;
    }
    // AV. [AvoidRO] (avoidro.cpp, docs/137 §7): G-AV1 the bytes and call edges; G-AV2 the point walk with mode 1 (the game's
    // RoutingWorld_ObstacleQuery 0x82EDB0 vs SrQuery(mode1), radius 0 and > 0); G-AV3 the game's RouteFollow_A 0x846B70
    // (its own query, consumer, 0x62EF30, polygon tests 0x80E8A0 and Sim scan) vs AvRunA; G-AV4 the game's RouteFollow_B
    // 0x85B470 vs AvRunB; G-AV5 the A-site and both detours on the mapped image: job -> clone (a miss runs the game's body
    // with the site skipped), no job -> the game's code, shadow with 0 mismatches, QL busy -> the game's code. Worlds of
    // section A (with 0.37's child footprints); 0x8468C0 and 0x829470 faked (lot objects / terrain), the Sim scan runs on
    // an empty locomotion list.
    {
        // worlds are never freed: bounded so that 3000-case runs stay inside the 32-bit address space
        const int avN = cases * 4 < 1600 ? cases * 4 : 1600, avH = cases < 400 ? cases : 400;
        const DWORD seedAv = g_seed;  // put back at the end: the sections after this one draw what they drew before 0.37
        assert(Matches((const void*)(uintptr_t)AV_A, kAvPro, sizeof(kAvPro)) && Matches((const void*)(uintptr_t)AV_B, kAvPro, sizeof(kAvPro)) &&
               Matches((const void*)(uintptr_t)0x0085FF30, kAvPro, sizeof(kAvPro)) &&
               Matches((const void*)(uintptr_t)0x0081D090, kAvAssignPro, sizeof(kAvAssignPro)));
        assert(callsTo(AV_SITE, AV_QUERY) && callsTo(0x00846B9F, 0x008292D0) && callsTo(0x00846BB6, 0x00829BC0) &&
               callsTo(0x00846BC3, 0x008468C0) && callsTo(0x00846C0D, 0x00829470) && callsTo(0x00846C64, 0x00C68990) &&
               callsTo(0x00846CF8, 0x00953690) && callsTo(0x00846D74, 0x0085AAA0) && callsTo(0x00846D99, 0x0081C970) &&
               callsTo(0x00846DB3, 0x0081C930) && callsTo(0x00846E09, 0x0062EF30) && callsTo(0x00846F07, 0x00AAC090));
        assert(callsTo(0x0085B4D2, 0x0085AE60) && callsTo(0x0085B507, AV_QUERY) && callsTo(0x0085B536, 0x00C68990) &&
               callsTo(0x0085B579, 0x0062EF30));
        // QueryPoint 0x667480: the mode byte picks 0x662DD0 (flag 1) or 0x662DB0 (flag 0), nothing else reads it
        assert(Matches((const void*)(uintptr_t)0x00667493, (const u8*)"\x80\x7C\x24\x20\x00\xB8\xD0\x2D\x66\x00\x75\x05\xB8\xB0\x2D\x66\x00", 17));
        u8 savedLevel[5], savedHeight[5];  // put back at the end (later sections run the planner on the image)
        memcpy(savedLevel, (const void*)(uintptr_t)0x008468C0, 5);
        memcpy(savedHeight, (const void*)(uintptr_t)0x00829470, 5);
        Jmp(0x008468C0, (void*)AVF::Level);
        Jmp(0x00829470, (void*)AVF::Height);
        static u8 loco[0x80];
        *(u8**)(loco + 0x40) = loco + 0x40;  // empty locomotion agent list: the Sim scan finds no one
        u8* const savedLoco = *(u8**)(uintptr_t)0x011D9140;
        *(u8**)(uintptr_t)0x011D9140 = loco;
        static DWORD agVt[0x20], pathVt[4];
        agVt[0x10 / 4] = (DWORD)(uintptr_t)AVF::AgId;
        agVt[0x3C / 4] = (DWORD)(uintptr_t)AVF::AgBox;
        pathVt[3] = (DWORD)(uintptr_t)AVF::PathOpts;
        static u8 agBase[0x80], path[8], opts[0x80];
        *(DWORD**)(agBase + 8) = agVt;
        *(DWORD**)path = pathVt;
        *(u8**)(path + 4) = opts;
        static DWORD ids38[8], ids58[8], ign[8];
        static __declspec(align(16)) float goal[4];
        static u8* wmw2;
        if (!wmw2) wmw2 = (u8*)A(0x200);
        struct ACase { u8* path; u8* agent; DWORD ni; DWORD opt; };
        auto newCase = [&](ACase* a) {
            BuildWorld();
            const int lot = Rnd(2);
            float x, z;
            WorldPoint(lot, Rf(-4, 68), Rf(-4, 68), &x, &z);
            if (lot == 0 && x >= 0) x = -0.5f;
            if (lot == 1 && x < 0) x = 0.5f;
            if (Rnd(8) == 0) x += lot == 0 ? 50 : 150;  // another lot's frame, or lot 2 (not in the table)
            goal[0] = x, goal[1] = Rf(0, 2), goal[2] = z, goal[3] = (float)(Rnd(5) == 0 ? 1 : 0);
            g_lookNone = NULL;
            if (Rnd(6) == 0) {  // the consumer's wm finds no lot, the query's (another manager) does: the first survivor
                *(u8**)(W.world + 0x964) = wmw2;
                g_lookNone = W.wm;
            }
            for (int i = 0; i < 4; i++) {
                ids38[i * 2] = 0x1000 + Rnd(40), ids38[i * 2 + 1] = Rnd(3) ? 0 : 7;
                ids58[i * 2] = 0x1000 + Rnd(40), ids58[i * 2 + 1] = Rnd(3) ? 0 : 7;
                ign[i * 2] = 0x1000 + Rnd(40), ign[i * 2 + 1] = Rnd(3) ? 0 : 7;
            }
            const DWORD n38 = Rnd(3), n58 = Rnd(3);
            *(DWORD**)(opts + 0x38) = ids38, *(DWORD**)(opts + 0x3C) = ids38 + n38 * 2;
            *(DWORD**)(opts + 0x58) = ids58, *(DWORD**)(opts + 0x5C) = ids58 + n58 * 2;
            a->path = Rnd(4) ? path : NULL;
            a->agent = Rnd(5) ? agBase + 8 : NULL;
            *(DWORD*)(agBase + 0x4C) = Rnd(0x10000), *(DWORD*)(agBase + 0x54) = Rnd(4) ? 0 : Rnd(0x10000) << 8;
            a->ni = Rnd(4);
            a->opt = Rnd(0x10000);
        };
        auto refsOk = [&]() { RefsOk(); };
        // G-AV2: the mode-1 point walk
        long lCmp = 0, lObjs = 0, lKids = 0, lFb[9] = {};
        for (int c = 0; c < avN; c++) {  // mode 1 keeps only blocking classes: few footprints per walk
            ACase a;
            newCase(&a);
            SrPt pt;
            memset(&pt, 0, sizeof(pt));
            pt.v[0] = goal[0], pt.v[1] = goal[1], pt.v[2] = goal[2], pt.v[3] = goal[3];
            pt.one = 1.0f;
            pt.level = (LONG)goal[3];
            const float r = Rnd(4) == 0 ? 0.0f : Rf(0.2f, 6);  // A and B use 0; larger radii reach more cells and classes
            const u8* ql = FkLotLookup(*(u8**)(W.world + 0x964), NULL, goal[0], goal[2]);
            DWORD vec[4] = {0, 0, 0, kSrVecTag};
            const u8 gotQ = ((SrQueryFn)(uintptr_t)AV_QUERY)(W.world, NULL, &pt, r, vec, 1, a.opt);
            assert((gotQ != 0) == (ql != NULL));
            static SrCtx ctx;
            __declspec(align(16)) u8 rec[0x30] = {};
            *(DWORD*)rec = a.opt;
            rec[0x24] = 1;
            ctx.n = 0;
            const int why = ql ? SrQuery(&ctx, W.p, *(const DWORD*)(ql + 0x48), *(const DWORD*)(ql + 0x4C), pt, true, r, rec, true) : SR_OK;
            const int n = (int)((vec[1] - vec[0]) / 4);
            lFb[why]++;
            if (!why) {
                bool same = n == ctx.n;
                for (int i = 0; same && i < n; i++) same = ((const DWORD*)(uintptr_t)vec[0])[i] == (DWORD)(uintptr_t)(ctx.objs[i] + 4);
                if (!same) {
                    printf("MISMATCH mode-1 point walk: case %d, game %d footprints, clone %d, r %g\n", c, n, ctx.n, r);
                    exit(1);
                }
                lCmp++;
                lObjs += n;
                for (int i = 0; i < n; i++) if (*(const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)vec[0])[i] == 0x00FEBF6C) { lKids++; break; }
            }
            ((SrVecRelFn)(uintptr_t)0x00AAC090)(vec, NULL, (void*)(uintptr_t)vec[0], (void*)(uintptr_t)vec[1]);
            if (vec[0]) _aligned_free((void*)(uintptr_t)vec[0]);
            refsOk();
        }
        printf("AV G-AV2: 0x82EDB0 mode 1 vs SrQuery(mode1): %d cases, %ld compared equal (%ld footprints, %ld with a child footprint); "
               "gave up: lot %ld, deferred %ld, shape %ld\n", avN, lCmp, lObjs, lKids, lFb[SR_FB_LOTMISS], lFb[SR_FB_DEFERRED], lFb[SR_FB_SHAPE]);
        assert(lCmp > avN / 2 && lObjs > avN / 8 && lKids > avN / 400 && lFb[SR_FB_SHAPE] == 0);
        // G-AV3: RouteFollow_A
        long aCmp = 0, aHits = 0, aMiss = 0, aFirst = 0, aKids = 0, aFb[9] = {};
        for (int c = 0; c < avN; c++) {
            ACase a;
            newCase(&a);
            static AvIn in;
            u64 mine;
            bool none;
            const int why = AvRunA(a.path, a.agent, goal, ign, a.ni, false, &mine, &in, &none);  // first: it must change nothing
            refsOk();
            const u64 game = ((AvAFn)(uintptr_t)AV_A)(a.path, a.agent, goal, ign, a.ni);
            refsOk();
            assert(!none);
            aFb[why]++;
            if (why) continue;
            if (game != mine) {
                printf("MISMATCH RouteFollow_A: case %d at (%.3f, %.3f) level %d: game %08lX:%08lX, clone %08lX:%08lX (lot %p, query lot %p)\n",
                       c, goal[0], goal[2], (int)goal[3], (DWORD)(game >> 32), (DWORD)game, (DWORD)(mine >> 32), (DWORD)mine, in.lotE, in.qlot);
                exit(1);
            }
            aCmp++;
            if (game) aHits++; else aMiss++;
            aFirst += !in.lotE && game;
            if (game && (DWORD)(game >> 32) != 0 && (DWORD)(game >> 32) != 7) aKids++;  // ~(parent id): a child answered
        }
        printf("AV G-AV3: game 0x846B70 vs AvRunA: %d cases, %ld compared equal (%ld footprint ids, %ld of them with no goal lot, "
               "%ld from a child; %ld none -> Sim scan); gave up: lot %ld, deferred %ld, cache %ld, shape %ld, other %ld\n", avN, aCmp,
               aHits, aFirst, aKids, aMiss, aFb[SR_FB_LOTMISS], aFb[SR_FB_DEFERRED], aFb[SR_FB_CACHE], aFb[SR_FB_SHAPE],
               avN - aCmp - aFb[SR_FB_LOTMISS] - aFb[SR_FB_DEFERRED] - aFb[SR_FB_CACHE] - aFb[SR_FB_SHAPE]);
        assert(aCmp > avN / 2 && aHits > avN / 40 && aMiss > avN / 80 && aFirst > 0 && aFb[SR_FB_SHAPE] == 0);
        // G-AV4: RouteFollow_B
        long bCmp = 0, bOnes = 0, bFb[9] = {};
        for (int c = 0; c < avN; c++) {
            ACase a;
            newCase(&a);
            static AvIn in;
            u8 mine;
            const int why = AvRunB(W.wm, W.world, W.p, goal, a.opt, false, &mine, &in);
            refsOk();
            const u8 game = ((AvBFn)(uintptr_t)AV_B)(W.wm, W.world, W.p, goal, a.opt);
            refsOk();
            bFb[why]++;
            if (why) continue;
            if (game != mine) {
                printf("MISMATCH RouteFollow_B: case %d at (%.3f, %.3f): game %d, clone %d\n", c, goal[0], goal[2], game, mine);
                exit(1);
            }
            bCmp++;
            bOnes += game;
        }
        printf("AV G-AV4: game 0x85B470 vs AvRunB: %d cases, %ld compared equal (%ld answer 1); gave up: lot %ld, deferred %ld, shape %ld\n",
               avN, bCmp, bOnes, bFb[SR_FB_LOTMISS], bFb[SR_FB_DEFERRED], bFb[SR_FB_SHAPE]);
        assert(bCmp > avN / 2 && bOnes > avN / 80 && bCmp - bOnes > avN / 80 && bFb[SR_FB_SHAPE] == 0);
        // G-AV5: the A-site and the two detours on the mapped image
        const CallSite cs = {AV_SITE, AV_QUERY, (void*)AvSite};
        assert(PatchCalls("avoid RO A-site", &cs, 1));
        g_avAOrig = (AvAFn)InstallDetour("RouteFollow_A", AV_A, kAvPro, sizeof(kAvPro), (void*)AvAHook, &g_avAOrig);
        g_avBOrig = (AvBFn)InstallDetour("RouteFollow_B", AV_B, kAvPro, sizeof(kAvPro), (void*)AvBHook, &g_avBOrig);
        assert(g_avAOrig && g_avBOrig);
        g_async.owner = me;
        k.avMode = 1;
        for (int c = 0; c < avH; c++) {
            ACase a;
            newCase(&a);
            Job(true);
            const u64 x = ((AvAFn)(uintptr_t)AV_A)(a.path, a.agent, goal, ign, a.ni);
            const u8 bx = ((AvBFn)(uintptr_t)AV_B)(W.wm, W.world, W.p, goal, a.opt);
            Job(false);
            const u64 y = ((AvAFn)(uintptr_t)AV_A)(a.path, a.agent, goal, ign, a.ni);  // no job: the trampoline, site passes through
            const u8 by = ((AvBFn)(uintptr_t)AV_B)(W.wm, W.world, W.p, goal, a.opt);
            assert(x == y && bx == by);
            refsOk();
        }
        assert(k.avTakenHit > avH / 25 && k.avTakenMiss > avH / 2 && k.avSiteSkip == k.avTakenMiss && k.avTakenB > avH / 2 &&
               t_avMiss == 0);
        k.avShadow = 1;
        for (int c = 0; c < avH; c++) {
            ACase a;
            newCase(&a);
            ((AvAFn)(uintptr_t)AV_A)(a.path, a.agent, goal, ign, a.ni);
            ((AvBFn)(uintptr_t)AV_B)(W.wm, W.world, W.p, goal, a.opt);
            refsOk();
        }
        assert(k.avShadowRuns > avH && k.avShadowMismatch == 0 && k.avShadowMismatchB == 0 && k.avShadowQueryMismatch == 0 &&
               k.avShadowOrder == 0 && g_rgOffSimOpen == 0 && k.avSiteSkip == k.avTakenMiss);
        k.avShadow = 0;
        // a worker writer holds QL: the hook falls back to the game's code, same answer
        g_wIn = CreateEventW(NULL, FALSE, FALSE, NULL);
        g_wGo = CreateEventW(NULL, FALSE, FALSE, NULL);
        HANDLE qh = CreateThread(NULL, 0, [](void*) -> DWORD {
            AcquireSRWLockExclusive(&g_ql);
            SetEvent(g_wIn);
            WaitForSingleObject(g_wGo, 5000);
            ReleaseSRWLockExclusive(&g_ql);
            return 0;
        }, NULL, 0, NULL);
        WaitForSingleObject(g_wIn, INFINITE);
        const LONG busy0 = k.avFb[AV_FB_TRY];
        for (int c = 0; c < 50; c++) {
            ACase a;
            newCase(&a);
            Job(true);
            const u64 x = ((AvAFn)(uintptr_t)AV_A)(a.path, a.agent, goal, ign, a.ni);
            Job(false);
            assert(x == ((AvAFn)(uintptr_t)AV_A)(a.path, a.agent, goal, ign, a.ni));
        }
        SetEvent(g_wGo);
        WaitForSingleObject(qh, INFINITE);
        CloseHandle(qh);
        printf("AV G-AV5: hook A %ld hits + %ld misses (site skipped %ld) and B %ld answers equal to the game's; shadow %ld runs, 0 "
               "mismatches (%ld answered by the Sim scan); QL busy %ld x -> game's call; fallbacks lot %ld cache %ld shape %ld\n",
               k.avTakenHit, k.avTakenMiss, k.avSiteSkip, k.avTakenB, k.avShadowRuns, k.avShadowSimScan, k.avFb[AV_FB_TRY] - busy0,
               k.avFb[AV_FB_LOTMISS], k.avFb[AV_FB_CACHE], k.avFb[AV_FB_SHAPE]);
        assert(k.avFb[AV_FB_TRY] - busy0 > 25);
        k.avMode = 0;
        g_lookNone = NULL;
        memcpy((void*)(uintptr_t)0x008468C0, savedLevel, 5);
        memcpy((void*)(uintptr_t)0x00829470, savedHeight, 5);
        *(u8**)(uintptr_t)0x011D9140 = savedLoco;
        g_seed = seedAv;
    }
    printf("visitor branches: masked %ld, leaf %ld, children %ld\n", g_srVisitKind[0], g_srVisitKind[1], g_srVisitKind[2]);
    // SNAP K1, K2, K5 (snap.cpp, docs/94 §4.4, phase P3): the snapshot's dirty marks, cut, versions and audit on the fake
    // routing world of snap-fake.h, with the game's own high-cost rasteriser WaterGrid_MarkB 0x670750 (the writer of every
    // high-cost cell, called per polygon edge as 0x6733C0 does) checking the dirty-tile rule of docs/94 §9.2 on the real code
    {
        auto callsTo2 = [](DWORD at, DWORD to) { const u8* p = (const u8*)(uintptr_t)at; return p[0] == 0xE8 && at + 5 + *(const DWORD*)(p + 1) == to; };
        assert(callsTo2(0x00673479, 0x00670750));  // 0x6733C0's per-edge call (arg 5 = the polygon read at 0x673411)
        g_sfRaster = (SfRasterFn)(uintptr_t)0x00670750;
        g_log = GetStdHandle(STD_OUTPUT_HANDLE);  // "snap audit MISMATCH" lines name the input and offset
        long cuts = 0, writes = 0;
        SnapK1(seedArg, cases, &cuts, &writes);
        long marked = 0;  // the game's rasteriser did write cells (else the tile rule was not exercised)
        for (int m = 0; m < 10; m++) {
            const u8* map = *(u8**)(*(u8**)(g_sf.world + 0x8C + 0x7C0) + m * 16);
            for (DWORD i = 0; i < g_sf.gw * g_sf.gh; i++) marked += map[i] != 0;
        }
        assert(marked > 0);
        SnapK2(seedArg + 1);
        SnapK5(seedArg + 2);
        printf("SNAP K1 (seed %lu): %ld cuts over %ld writes (game rasteriser 0x670750 for high-cost stamps and removes): audit 0 mismatches, "
               "incremental == full at every cut, unsignalled writes found (%ld lot, %ld tile); %ld tile and %ld lot images (reference lot strategy) rebuilt\n",
               seedArg, cuts, writes, k.spMis[SP_LOTS], k.spMis[SP_GHC], k.spCopies[SP_GHC], k.spCopies[SP_LOTS]);
        printf("SNAP K2: pending lot update, deferred flush, pin counter and lock byte at the cut: counted (%ld, %ld), not imaged; "
               "K5: version held across cuts and a lot removal, images kept, references back to 0\n", k.spPendingAtCut, k.spDeferredAtCut);
        g_sfRaster = SfRasterC;
    }
    // SNAP LAZY (snap.cpp lazy Gtiles, docs/94 §21.9): a cut after every write, a reader every 16 cuts, the game's rasteriser
    // for the high-cost writes. Every cut: carried tiles == live, no tile built without a reader; reader cuts: audit 0
    // mismatches, version == full rebuild. Then the same writes with a reader at every cut (= the eager build) for comparison
    {
        g_sfRaster = (SfRasterFn)(uintptr_t)0x00670750;
        long r = 0, b = 0, e = 0, r1 = 0, b1 = 0, e1 = 0;
        const LONG m0 = k.spMis[SP_GHC];
        SnapLazy(seedArg, cases, 16, &r, &b, &e);
        SnapLazy(seedArg, cases, 1, &r1, &b1, &e1);
        assert(b < e && b1 == e1 && e1 == e && k.spMis[SP_GHC] == m0);
        printf("SNAP LAZY (seed %lu): %d cuts, %ld reader cuts: %ld tiles built (%.2f per cut, %.2f per reader) against %ld eager "
               "(%.2f per cut); reader at every cut: %ld built == %ld dirty; carried tiles == live at every cut, audit 0 mismatches\n",
               seedArg, cases, r, b, (double)b / cases, r ? (double)b / r : 0.0, e, (double)e / cases, b1, e1);
        g_sfRaster = SfRasterC;
    }
    // REPLAY K1 (replay.cpp, docs/94 §11 B, §16): random writer sequences through P1's stubs, the game's own ApplyInsert
    // 0x665C50 / ApplyRemove 0x666750 on the live world and the same functions replayed on the clone; every allocation has a
    // random pad (RfAlloc), so live and clone objects sit at unrelated addresses; the audit compares every lot's hash
    {
        long cuts = 0, steps = 0;
        DWORD ents = 0;  // live leaf entries at the end of each seed (the game's trees were filled)
        long poolNodes = 0, midCopies = 0, covered = 0;
        g_log = GetStdHandle(STD_OUTPUT_HANDLE);  // "snap replay ..." lines (a mismatch names its lot)
        char nopad[4] = "";
        if (GetEnvironmentVariableA("RF_NOPAD", nopad, sizeof(nopad))) g_rfPad = 0;
        const bool cutAll = GetEnvironmentVariableA("RF_CUTALL", nopad, sizeof(nopad)) != 0;  // a cut after every step
        RkPatch();
        for (DWORD seed = seedArg; seed < seedArg + 2; seed++) {
            RkSetup(seed);
            Ts3nCounters& kr = ts3native_counters;
            const LONG mis0 = kr.rpMis, dv0 = kr.rpDiverge, ep0 = kr.rpEpochs, cf0 = kr.rpCopyFail;
            for (int i = 0; i < cases; i++) {
                RfStep();
                steps++;
                if (RfRnd(4) == 0 || cutAll) {
                    RfCut();
                    cuts++;
                    if (kr.rpMis != mis0 || kr.rpFaults) {
                        printf("REPLAY seed %lu step %d (kind %lu, lot %d): mismatches %ld faults %ld (copies refused %ld, skips %ld %ld %ld %ld)\n", seed, i,
                               g_rfOp, g_rfOpLot, kr.rpMis - mis0, kr.rpFaults, kr.rpCopyFail, kr.rpSkip[0], kr.rpSkip[1], kr.rpSkip[2], kr.rpSkip[3]);
                        for (int l = 0; l < g_rf.nLots; l++) RfDiffLot(*(DWORD*)g_rf.lots[l]);
                    }
                    assert(kr.rpMis == mis0 && kr.rpFaults == 0);
                }
            }
            RfCut();
            assert(kr.rpMis == mis0 && kr.rpDiverge == dv0 && kr.rpEpochs == ep0 && g_rpTail == g_rpPub);
            assert(kr.rpCopyFail == cf0 && kr.rpExclLots == 0);  // every class copied, the middle class 0x1020AE8 too
            for (DWORD i = 0; i < g_rpClone->fps.cap; i++)
                if (g_rpClone->fps.k[i] > 1 && *(DWORD*)(uintptr_t)g_rpClone->fps.v[i] == 0x1020AE8) midCopies++;
            for (int l = 0; l < g_rf.nLots; l++) { DWORD e = 0; RpLotHash(g_rf.lots[l], &e); ents += e; }
            poolNodes += RfPoolCheck();  // clone nodes private, live nodes the game's (pools.cpp)
            RfIns(RfQOf(0, 0), g_rf.fps[0], 1, false);  // negative control: an insert with no record
            RfCut();
            assert(kr.rpMis > mis0);
            covered += RkCoverRemove();
            RfTeardown();
            assert(g_rpBytes == 0 && kr.rpFps == 0);
        }
        // H20b on game objects: portals with the game's vtable 0xFED048 (the interlocked pair in it, H20a) in every lot's +0x44
        // and rooms; the clone's references exact after a race with sim-side AddRef / Release, released at the teardown
        {
            DWORD* vt = (DWORD*)(uintptr_t)LT_PORTAL_VT;
            assert(InstallPortalRefX());
            g_rfMakePortal = RkPortal;
            const LONG n = RfPortalRefsRun(RkSetup, seedArg, cases < 600 ? cases : 600);
            g_rfMakePortal = NULL;
            vt[0] = 0x0064E8E0, vt[1] = 0x0064E8F0;
            printf("REPLAY H20b: game portal objects (vtable 0xFED048, interlocked pair): clone references exact (9 per lot copy) "
                   "after %ld sim AddRef/Release pairs raced over rebases and frees; all released at the teardown\n", n);
        }
        // K3 / K4 / K5 (docs/94 §4.4, G4): the game's in-room search on live P and on the clone's P
        {
            for (RkSite& s : g_k3Sites) { memcpy(s.saved, (void*)(uintptr_t)s.at, 5); Jmp(s.at, s.to); }
            static const DWORD kPoolInit[4] = {0x0061AD30, 0x0061AEE0, 0x0061EC00, 0x0066BBD0};  // goal, cell node, start, cell object
            DWORD savedPool[4];
            static const DWORD kPoolGlobal[4] = {0x011CEF50, 0x011CEF68, 0x011CEF90, 0x011CF448};
            for (int i = 0; i < 4; i++) savedPool[i] = *(DWORD*)(uintptr_t)kPoolGlobal[i], ((void(__cdecl*)())(uintptr_t)kPoolInit[i])();
            static K3Ans kept[64];
            static u64 keptKey[64];
            static float keptPts[64][6];
            long k4Cuts = 0, k4Version = 0, k4Moved = 0, k5 = 0;
            for (DWORD seed = seedArg; seed < seedArg + 2; seed++) {
                RkSetup(seed);
                g_k3ALive = K3Astar(), g_k3AClone = K3Astar();
                K3Sweep(4, kept, 0, keptKey, keptPts);  // K3 on the freshly built clone
                // tie lot: an empty lot with the box, recorded like any lot add and insert (the clone gets it at the cut)
                u8* tl = RfLot(0x7E0 + seed);
                g_rf.lots[g_rf.nLots++] = tl;
                RfRelink();
                RfIns(**(u8***)(tl + 0x10 + 0x74), K3Box(), 0, true);
                RfCut();
                // K3: a sweep on the cut
                K3Sweep(cases / 40 + 4, kept, 0, keptKey, keptPts);
                K3TieCases(0x7E0 + seed);
                // K4: random writes through P1's stubs between plans, the consumer applies them at each cut; after a cut
                // clone == live, and between cuts the clone keeps answering for its version, not the moved live data
                for (int r = 0; r < 12; r++) {
                    for (int i = 0; i < 25; i++) RfStep();
                    RfCut();
                    k4Cuts++;
                    const int nk = K3Sweep(3, kept, 64, keptKey, keptPts);
                    for (int i = 0; i < 25; i++) RfStep();  // live moves on, no cut
                    K3Point();
                    static K3Ans c, g;
                    for (int i = 0; i < nk; i++) {
                        bool lotThere = false;  // a lot removed since the cut: the clone still has it, the live table not
                        for (int l = 0; l < g_rf.nLots; l++) lotThere |= *(DWORD*)(g_rf.lots[l] + 0x10) == (DWORD)keptKey[i];
                        K3Run(g_rpClone->p, keptKey[i], keptPts[i], keptPts[i] + 3, &c);
                        if (!K3Same(kept[i], c)) { K3Dump("K4 version", kept[i], c); assert(false); }
                        k4Version++;
                        if (lotThere) { K3Run(g_rf.p, keptKey[i], keptPts[i], keptPts[i] + 3, &g); k4Moved += !K3Same(kept[i], g); }
                    }
                }
                // K5: a path built on the clone outlives the clone's lot (I-94-66: nodes hold values only): the lot is removed live,
                // the cut frees the clone's copy, the path bytes are unchanged; memory back to 0 at the teardown (RfTeardown)
                {
                    RfCut();
                    K3Point();
                    static K3Ans c, c2;
                    u8* gone = g_rf.lots[0];
                    const u64 key = *(DWORD*)(gone + 0x10);
                    for (int i = 0; i < 8 && (i == 0 || c.ret); i++) {  // a found path (start and target walkable)
                        const float s[3] = {Rf(1, 63), Rf(1, 63), 0}, t[3] = {Rf(1, 63), Rf(1, 63), 0};
                        K3Run(g_rpClone->p, key, s, t, &c);
                    }
                    c2 = c;
                    for (u8** qp = *(u8***)(gone + 0x10 + 0x74); qp < *(u8***)(gone + 0x10 + 0x78); qp++)
                        for (int i = 0; i < g_rf.nFps; i++) RfRem(*qp, g_rf.fps[i], true);
                    g_rf.lots[0] = g_rf.lots[--g_rf.nLots];
                    RfRelink();
                    RfCut();
                    for (int i = 0; i < g_rpClone->nLots; i++) assert(g_rpClone->lots[i].key != key);  // the clone's copy is gone
                    assert(K3Same(c, c2));
                    k5 += c.ret == 0;
                }
                RfTeardown();
                assert(g_rpBytes == 0 && ts3native_counters.rpFps == 0);
            }
            for (int i = 0; i < 4; i++) *(DWORD*)(uintptr_t)kPoolGlobal[i] = savedPool[i];  // shortcut: the game's pool objects leak
            for (RkSite& s : g_k3Sites) memcpy((void*)(uintptr_t)s.at, s.saved, 5);
            assert(g_k3Ok > 0 && g_k3Detours > 0 && k4Moved > 0);
            printf("K3 (seeds %lu-%lu): the game's InRoomPlanToPoint 0x632630 on live P vs the replay clone's P: %ld plans equal "
                   "(%ld found, %ld crossing more than 2 cells, %ld nodes); tie lot: %ld cases equal, the tie broken to one side the same way\n",
                   seedArg, seedArg + 1, g_k3Plans, g_k3Ok, g_k3Detours, g_k3Nodes, g_k3Ties);
            if (g_k3LiveParents)
                printf("K3 WARNING (I-94-193): %ld clone inner tree nodes had +8 (parent) pointing at the live tree (replay.cpp RpTree "
                       "copies it); relinked by the test before each search. Without that the first sweep plan differs (K3_NORELINK=1)\n",
                       g_k3LiveParents);
            printf("K4: %ld cuts with writes between plans: clone == live after each; %ld plans on the clone after 25 more uncut "
                   "writes equal the version's answers (%ld live answers had moved); K5: %ld clone paths unchanged after their lot "
                   "was removed and the clone's copy freed, clone memory back to 0\n", k4Cuts, k4Version, k4Moved, k5);
        }
        // K7 (crash H, docs/67 §10): the game's RoomGraph_FirstUse 0x63D2F0 on a clone lot's unbuilt room, as a clone run's lot
        // planner reaches it (0x63F488 -> 0x63D516). The live room: built byte 0 as 0x630F70 leaves it, one portal, an empty +0x24
        // (the grow 0x5D1D60 frees the old buffer) and one +0x34 list whose buffer sits on a PAGE_NOACCESS page during the call
        // (0x61EE20 frees it). Pass: no free of a block that is not an EA one (RfFreeP stands in for 0x4E3F70), no live access,
        // the clone's room built with index 0 in +0x24, the live room untouched.
        {
            assert(InstallPortalRefX());
            DWORD* vt = (DWORD*)(uintptr_t)LT_PORTAL_VT;
            u8* page = (u8*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            assert(page);
            long k7 = 0;
            for (DWORD seed = seedArg; seed < seedArg + 2; seed++) {
                g_rfMakePortal = RkPortal;
                RkSetup(seed);
                g_rfMakePortal = NULL;
                for (int l = 0; l < g_rf.nLots; l++) {
                    u8* r = RpP(g_rf.lots[l] + 0x10 + 0x90);  // room 0
                    *(u8**)(r + 8) = RpP(r + 4) + 4;         // one portal (the second keeps its hold: no release here)
                    r[0] = 0;
                    *(u8**)(r + 0x28) = RpP(r + 0x24), *(u8**)(r + 0x2C) = RpP(r + 0x24);
                    u8* lists = (u8*)RfAlloc(16);
                    u8* buf = page + 0x40 * (l + 1);  // a list entry of the room's last build, live
                    memset(buf, 0x11, 8);
                    *(u8**)lists = buf, *(u8**)(lists + 4) = *(u8**)(lists + 8) = buf + 8, *(DWORD*)(lists + 12) = 0x00FEAEE0;
                    *(u8**)(r + 0x34) = lists, *(u8**)(r + 0x38) = *(u8**)(r + 0x3C) = lists + 16;
                }
                g_rpForceAll = 1;
                RfCut();  // the clone's lots copied again with these rooms
                const LONG bad0 = g_rfBadFree;
                const DWORD savedA = RpD(g_rpClone->p + 0xBC);
                *(DWORD*)(g_rpClone->p + 0xBC) = 0;  // no Q reset (the room-to-room A* needs 2+ portals; one here)
                DWORD old;
                for (int i = 0; i < g_rpClone->nLots; i++) {
                    u8* g = RpGraph(g_rpClone->lots[i].node);
                    u8* r = RpP(g + 0x90);
                    assert(r && !r[0]);
                    assert(VirtualProtect(page, 0x1000, PAGE_NOACCESS, &old));
                    char built = 0;
                    __try { built = g_rpFirstUse(g_rpClone->p, NULL, g, 0); }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        VirtualProtect(page, 0x1000, PAGE_READWRITE, &old);
                        printf("K7 FAIL (crash H): RoomGraph_FirstUse on clone lot %d faulted (%08lX): it touched a live room buffer\n", i,
                               GetExceptionCode());
                        assert(false);
                    }
                    assert(VirtualProtect(page, 0x1000, PAGE_READWRITE, &old));
                    if (g_rfBadFree != bad0) {
                        printf("K7 FAIL (crash H): RoomGraph_FirstUse on clone lot %d EA-freed %ld blocks that are not EA blocks\n", i,
                               g_rfBadFree - bad0);
                        assert(false);
                    }
                    assert(built == 1 && r[0] == 1 && RpP(r + 0x28) - RpP(r + 0x24) == 2 && *(WORD*)RpP(r + 0x24) == 0);
                    k7++;
                }
                *(DWORD*)(g_rpClone->p + 0xBC) = savedA;
                for (int l = 0; l < g_rf.nLots; l++) {  // live rooms untouched
                    const u8* r = RpP(g_rf.lots[l] + 0x10 + 0x90);
                    assert(!r[0] && RpP(r + 0x24) == RpP(r + 0x28) && RpP(RpP(r + 0x34)) == page + 0x40 * (l + 1));
                    *(u8**)RpP(r + 0x34) = NULL, *(u8**)(RpP(r + 0x34) + 4) = *(u8**)(RpP(r + 0x34) + 8) = NULL;  // not a heap block
                }
                RfTeardown();
                assert(g_rpBytes == 0 && g_rfBadFree == bad0);
            }
            vt[0] = 0x0064E8E0, vt[1] = 0x0064E8F0;
            VirtualFree(page, 0, MEM_RELEASE);
            printf("K7 (crash H): the game's RoomGraph_FirstUse 0x63D2F0 built %ld unbuilt clone rooms (list grow 0x5D1D60, list clear "
                   "0x61EE20) on private EA blocks: no free of a foreign block, live room buffers on no-access pages untouched\n", k7);
        }
        RkUnpatch();
        assert(ents > 100 && midCopies > 0 && covered > 0);
        printf("REPLAY cover remove (0.36.2): %ld game footprints with leaves, +0x2C answering far outside every Q, removed by the "
               "game's ApplyRemove with the cover table: no entry and no orphan left\n", covered);
        printf("REPLAY K1 (seeds %lu-%lu): %ld cuts over %ld writes through the game's ApplyInsert / ApplyRemove, random addresses: "
               "clone == live at every cut (%ld lot audits, %ld rebased lots, %lu leaf entries at the ends), unrecorded insert found; "
               "no copy refused, %ld middle-class 0x1020AE8 blocks in the final clones\n",
               seedArg, seedArg + 1, cuts, steps, ts3native_counters.rpAudits, ts3native_counters.rpRebaseLots, ents, midCopies);
        if (ts3native_counters.rpLivePool || ts3native_counters.plForeign)
            printf("REPLAY private pools: rpLivePool %ld plForeign %ld\n", ts3native_counters.rpLivePool, ts3native_counters.plForeign);
        assert(ts3native_counters.rpLivePool == 0 && ts3native_counters.plForeign == 0 && poolNodes > 100);
        printf("REPLAY private pools: the game's pool code on the fake world's pool objects, 12 sites patched on the image; %ld tree and map "
               "nodes checked (clone private, live the game's), rpLivePool 0, plForeign 0, region %ld KB\n", poolNodes, ts3native_counters.plKB);
    }
    // K6 (docs/94 §13.3, G4): the game's world A* on live R/G vs the working copy from the P3 version (see K6Build / K6Cut)
    {
        static const u8 kSearchPro[] = {0x83, 0xEC, 0x3C, 0x53, 0x8B, 0x5C, 0x24, 0x58};  // sub esp,0x3C; push ebx; mov ebx,[esp+0x58]
        assert(Matches((const void*)(uintptr_t)0x00676260, kSearchPro, sizeof(kSearchPro)));
        assert(Matches((const void*)(uintptr_t)0x006735C0, (const u8*)"\x83\xC1\x04\xE9", 4));          // add ecx,4; jmp 0x673500
        assert(Matches((const void*)(uintptr_t)0x00829C00, (const u8*)"\x51\x33\xD2\x89\x14\x24", 6));  // W getter
        assert(Matches((const void*)(uintptr_t)0x004E0B20, (const u8*)"\x8B\x4C\x24\x04\x8B\x01", 6));  // tree step (cdecl)
        auto callsTo3 = [](DWORD at, DWORD to) { const u8* p = (const u8*)(uintptr_t)at; return p[0] == 0xE8 && at + 5 + *(const DWORD*)(p + 1) == to; };
        assert(callsTo3(0x0081B3E7, 0x00829C00) && callsTo3(0x0065F809, 0x00676260));  // job: W from the world; planner: the A*
        const DWORD savedRw = *(DWORD*)(uintptr_t)0x011D9128, savedWm = *(DWORD*)(uintptr_t)0x011ECBC4;
        *(DWORD*)(uintptr_t)0x011D9128 = 0, *(DWORD*)(uintptr_t)0x011ECBC4 = 0;  // no routing world / world manager: 0x627780 and 0xC6BAB0 skip
        g_sfRaster = (SfRasterFn)(uintptr_t)0x00670750;
        const PcCtorFn savedAstar = g_pcAstarCtor;
        g_pcAstarCtor = NULL;  // no lot-level search here: no private cAStarSearch object
        // W' entry points as p4clone.cpp calls them (prologues unchanged in the image)
        assert(Matches((const void*)(uintptr_t)0x00652C50, (const u8*)"\x8B\x44\x24\x04\x53\x55", 6));      // FindClosest init
        assert(Matches((const void*)(uintptr_t)0x00654110, (const u8*)"\x83\xEC\x0C\x55\x56", 5));          // FindClosest reset
        assert(Matches((const void*)(uintptr_t)0x006741D0, (const u8*)"\x53\x56\x8B\xD9\x8B\x73\x14", 7));  // container clear
        assert(callsTo3(0x00654101, 0x00652C50) && callsTo3(0x00657ADA, 0x007F3790) && callsTo3(0x00657B47, 0x00657650) &&
               callsTo3(0x00657FDF, 0x006741D0) && callsTo3(0x00657FEA, 0x00654110));
        static K6Q keep[64];
        static K6Ans keepAns[64], k6c, k6l;
        long k4Version = 0, k4Moved = 0;
        // K6E: [RouteEarlyOut] Mode 2 on the game's A* (earlyout.cpp's hook, the game's cost functions and circle prepass)
        InitializeCriticalSection(&g_eoCs);
        QueryPerformanceFrequency(&g_qpf);
        g_eoAsOrig = (EoAsFn)(uintptr_t)0x00676260;
        ts3native_counters.eoMode = 1, ts3native_counters.eoAct = 2;
        g_eoShadowFirst = 0, g_eoShadowEvery = 0, g_eoMemoMinUs = 0;
        for (DWORD seed = seedArg; seed < seedArg + 2; seed++) {
            K6Build(seed);
            K6Cut();
            K6Sweep(cases / 20 + 10, keep, keepAns, 0);
            K6TieCases();
            K6EarlyOut(cases / 10 + 20);
            // writes between cuts (K4 on the world search): after each cut copy == live; after 25 uncut writes the copy still
            // answers for its version while live has moved
            for (int r = 0; r < 12; r++) {
                for (int i = 0; i < 25; i++) K6Write();
                K6Cut();
                const int nk = K6Sweep(cases / 60 + 6, keep, keepAns, 64);
                for (int i = 0; i < 25; i++) K6Write();
                for (int i = 0; i < nk; i++) {
                    K6RunCopy(keep[i], &k6c);
                    if (!K6Same(keepAns[i], k6c)) { K6Dump("K4 version", keep[i], keepAns[i], k6c); assert(false); }
                    k4Version++;
                    K6Run(g_sf.W, keep[i], &k6l);
                    k4Moved += !K6Same(keepAns[i], k6l);
                }
                K6EarlyOut(cases / 60 + 6);  // after blocked-bit writes
            }
            // teardown: the copy released, W' freed through the game's clear functions, the working copy and the cut dropped
            PcRelease(&g_k6Cl, 0, NULL, 0);
            g_k6Wc = NULL;
            PcWork& pw = g_pcWork;
            {  // W' (its cache now holds the inserted nodes) dropped on another thread, as the worker's DISCARD does
                struct T { static DWORD WINAPI Run(void* p) { PcWpDrop(*(PcWork*)p); return 0; } };
                const LONG f0 = ts3native_counters.snPcFaults;
                HANDLE dropTh = CreateThread(NULL, 0, T::Run, &pw, 0, NULL);
                assert(dropTh && WaitForSingleObject(dropTh, 10000) == WAIT_OBJECT_0);
                CloseHandle(dropTh);
                assert(!pw.wp && ts3native_counters.snPcFaults == f0 && !*(DWORD*)(uintptr_t)0x0114C900);
                g_k6Drops++;
            }
            RpFree(pw.r), RpFree(pw.bnm), RpFree((void*)pw.tileImg), RpFree(pw.world), RpFree(pw.wx);
            for (int m = 0; m < 16; m++) RpFree(pw.hc[m]);
            SpVerRel(pw.v);
            if (pw.csInit) DeleteCriticalSection(&pw.cs);
            memset(&pw, 0, sizeof(pw));
            SpVerRel(g_pcCut.v), PcWxRel(g_pcCut.wx), PcWxRel(g_pcWxLast);
            g_pcCut.v = NULL, g_pcCut.wx = NULL, g_pcWxLast = NULL, g_rpClone = NULL, g_rpHave = false;
            SfTeardown();
        }
        VirtualFree(g_k6Live, 0, MEM_RELEASE);
        g_k6Live = NULL;
        g_pcAstarCtor = savedAstar;
        g_sfRaster = SfRasterC;
        *(DWORD*)(uintptr_t)0x011D9128 = savedRw, *(DWORD*)(uintptr_t)0x011ECBC4 = savedWm;
        printf("K6 (seeds %lu-%lu): the game's WorldAStar_Search 0x676260 on live R/G vs the working copy (p4clone.cpp's PcSnap and "
               "PcAcquire on the P3 version, W' from 0x829C00 on the copy): %ld plans equal (path, best, closed bits; modes 1-4: "
               "%ld/%ld/%ld/%ld; %ld found, %ld nodes, %ld through water links); %ld reader cuts after R blocked-bit, blockedNodeMap and "
               "high-cost (0x670750) writes (%ld/%ld/%ld): audit 0 mismatches, copy == live at each\n",
               seedArg, seedArg + 1, g_k6Plans, g_k6Mode[1], g_k6Mode[2], g_k6Mode[3], g_k6Mode[4], g_k6Found, g_k6Nodes, g_k6Water, g_k6Cuts,
               g_k6WriteKind[0], g_k6WriteKind[1], g_k6WriteKind[2]);
        printf("K6 ties: %ld lattice cases equal (shortest either way); the same R with reversed neighbour order picks another equal-length "
               "path in %ld; K4: %ld copy plans after 25 uncut writes equal the version's answers (%ld live answers had moved)\n",
               g_k6Ties, g_k6TieFlip, k4Version, k4Moved);
        printf("K6 W' (I-94-220, I-94-221): at every one of %ld acquires 0x829C00 on the copy returned PcAcquire's W' (P = the clone's, R / G "
               "= the copy's, own closed list and FindClosest blocks, group hash private and equal); %ld copy trees end at the copy's anchor "
               "(0x4E0B20); %ld copy runs with live W, R, G trees, group hash and blockedNodeMap on PAGE_NOACCESS pages: no access\n",
               g_k6Cuts, g_k6TreeEnds, g_k6Guarded);
        printf("K6E (docs/142 §9): [RouteEarlyOut] Mode 2 vs the game's WorldAStar_Search: %ld answers identical (result, best, context "
               "+0x64..+0xBB, closed bits); acted car %ld memo %ld (of %ld memo tries); %ld reachable goals never acted; %ld car-only "
               "failures not acted; %ld stale memos (R flags byte flipped) answered by the game; eoWrong %ld, refused %ld, heuristic ties not acted %ld, stale car %ld\n",
               g_k6eSame, g_k6eAct[0], g_k6eAct[1], g_k6eMemoTry, g_k6eReach, g_k6eCarFail, g_k6eStale, ts3native_counters.eoWrong,
               ts3native_counters.eoActRefused, ts3native_counters.eoActTie, ts3native_counters.eoStale[0]);
        assert(g_k6eAct[0] > 0 && g_k6eAct[1] > 0 && g_k6eStale > 0 && g_k6eReach > 0 && ts3native_counters.eoWrong == 0 &&
               ts3native_counters.eoStale[0] == 0);
        ts3native_counters.eoMode = 0, ts3native_counters.eoAct = 0;
        g_eoAsOrig = NULL;
        printf("K6 crash G: %ld inserts into W'+0xC0 through the game's 0x657BC0, the shared empty bucket array 0x114C900 untouched; "
               "%ld W' drops on another thread with a filled cache, no fault\n", g_k6CacheIns, g_k6Drops);
        printf("K6 planner pieces (I-94-231): %ld lot-exit mask builds (0x64FEA0 on the copy world), %ld ObjectFootprints fills "
               "(0x6580F0, %ld points from the copy's R), %ld highCostFootprints erases (0x670510) on the copy's empty tree, all on W' with "
               "the live pages guarded: no access; %ld clone-run stops on world-level writer leaves\n",
               g_k6Masks, g_k6ObjFp, g_k6ObjFpPts, g_k6HcfMiss, g_k6Stops);
        assert(g_k6Masks == g_k6Cuts && g_k6HcfMiss == g_k6Cuts && g_k6ObjFp > 0 && g_k6ObjFpPts > 0 && g_k6Stops == g_k6Cuts * 11);
        assert(g_k6CacheIns == g_k6Cuts && g_k6Drops == 2 &&g_k6Found > g_k6Plans / 4 && g_k6Water > 0 && g_k6TieFlip > 0 && k4Moved > 0 && g_k6HcCells > 0 && g_k6TreeEnds > 0 &&
               g_k6Guarded >= g_k6Plans);
    }
    // K8 (docs/94 §13.3): the planner above the world search on live W vs PcAcquire's W'
    {
        const DWORD savedRw = *(DWORD*)(uintptr_t)0x011D9128, savedWm = *(DWORD*)(uintptr_t)0x011ECBC4;
        *(DWORD*)(uintptr_t)0x011D9128 = 0, *(DWORD*)(uintptr_t)0x011ECBC4 = 0;
        g_sfRaster = (SfRasterFn)(uintptr_t)0x00670750;
        for (RkSite& s : g_k8Sites) { memcpy(s.saved, (void*)(uintptr_t)s.at, 5); Jmp(s.at, s.to); }
        RkPatch();  // EA alloc / free -> RfNew / RfFreeP (a free of a block that is not an EA one is counted), the private pools,
                    // the global world manager [0x11ECBC4] with an active lot that is none of ours (LotTable_Contains, 0xC88FD0)
        // the routing pools the lot searches pop (goal, cell node, start, cell object): the game's pool objects, as K3
        static const DWORD kPoolInit[4] = {0x0061AD30, 0x0061AEE0, 0x0061EC00, 0x0066BBD0};
        static const DWORD kPoolGlobal[4] = {0x011CEF50, 0x011CEF68, 0x011CEF90, 0x011CF448};
        DWORD savedPool[4];
        for (int i = 0; i < 4; i++) savedPool[i] = *(DWORD*)(uintptr_t)kPoolGlobal[i], ((void(__cdecl*)())(uintptr_t)kPoolInit[i])();
        {
            static const u8 kCw[] = {0x51, 0x53, 0x8B, 0x5C, 0x24, 0x2C}, kAs[] = {0x83, 0xEC, 0x3C, 0x53, 0x8B, 0x5C, 0x24, 0x58};
            g_k8CwOrig = (K8CwFn)InstallDetour("K8 count 0x65BB90", 0x0065BB90, kCw, sizeof(kCw), (void*)K8CwCount, &g_k8CwOrig);
            g_k8AsOrig = (K8AsFn)InstallDetour("K8 count 0x676260", 0x00676260, kAs, sizeof(kAs), (void*)K8AsCount, &g_k8AsOrig);
            static const u8 kGa[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};
            g_k8GaOrig = (K8GaFn)InstallDetour("K8 count 0x654CC0", 0x00654CC0, kGa, sizeof(kGa), (void*)K8GaCount, &g_k8GaOrig);
        }
        // STEERRO_PHASES=1 (docs/150 §7 fix): [RouteJobPhases] stubs on every planner site whose prologue is still the game's
        // (the allocator, Lock_Acquire and world A* are K8's own fakes/counters here: skipped); the old RjpPop crashed on the
        // first nested ret-N leave
        // STEERRO_SYNCPHASES=1 (docs/164): the same stubs, each K8 plan a SyncPlans session; the world A* site is timed inside K8's
        // counter (as inside [RouteEarlyOut] in game), so the loop split has to find the game's return address above that frame
        g_k8Sp = GetEnvironmentVariableA("STEERRO_SYNCPHASES", NULL, 0) > 0;
        g_k8Rjp = !g_k8Sp && GetEnvironmentVariableA("STEERRO_PHASES", NULL, 0) > 0;
        g_k8Pd = !g_k8Sp && !g_k8Rjp && GetEnvironmentVariableA("STEERRO_PLANDEADLINE", NULL, 0) > 0;
        if (g_k8Pd) {  // docs/164 §7: the game's cost-function sites get the budget stubs, cell A* and water the deadline stubs
            if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
            assert(PatchCostSites(kCostSites, kCostFns));
            PdInstallAt(0x0061E500, 0x00671930);
            assert(g_pdOrig[0] && g_pdOrig[1]);
            ts3native_counters.pdMode = 1;
        }
        int rjpSites = 0;
        if (g_k8Rjp) {
            for (int i = 0; i < RJS_GS0; i++) rjpSites += RjpInstallAt(i, kRjpSites[i].at, NULL);  // P-GS (v103) not on K8
            ts3native_counters.rjpReady = 1;
            if (HcInstallDoor(P4_DOOR_AT, P4_DOOR_TARGET)) g_hcOn = 1, ts3native_counters.dmMode = 2;  // docs/159 door stub, shadow
        }
        if (g_k8Sp) {
            for (int i = 0; i < RJS_GS0; i++)
                rjpSites += RjpInstallAt(i, kRjpSites[i].at, i == RJS_WORLD ? (void**)&g_k8AsOrig : NULL);
            ts3native_counters.rfbLogMs = 0;  // no split lines from K8
        }
        DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &g_k8Main, 0, FALSE, DUPLICATE_SAME_ACCESS);
        g_rfSites = GetEnvironmentVariableA("STEERRO_ALLOCSITES", NULL, 0) > 0;  // docs/150 §14: EA allocator calls by game call site (off by default)
        g_k8Since = GetTickCount64();  // from here on: 10 s without a finished plan = a hang (cuts and builds included)
        CloseHandle(CreateThread(NULL, 0, K8Watch, NULL, 0, NULL));
        for (DWORD seed = seedArg; seed < seedArg + 2; seed++) {
            K8Build(seed);
            K8Cut();
            K8Sweep(cases / 20 + 10);
            // I-94-322 / [OptsInit] (0.34.1): dirty stacks without the patch may differ (counted); with it every plan matches
            {
                u8 saved[5];
                memcpy(saved, (void*)(uintptr_t)0x00652BF0, 5);
                g_k8OiDiffOff += K8OiPass(cases / 20 + 10);
                assert(OiInstall());
                const int on = K8OiPass(cases / 20 + 10);
                if (on) { printf("K8 WARNING: [OptsInit] on, %d plans still differ with dirty stacks\n", on); assert(false); }
                // 0.35.2 Enabled=2: only the copy side (run as a clone run, t_snapRun) is zeroed; live keeps its cleared stack
                const LONG oc0 = ts3native_counters.oiClone;
                g_oiMode = 2, g_k8OiClone = true;
                const int on2 = K8OiPass(cases / 20 + 10);
                g_oiMode = 1, g_k8OiClone = false;
                if (on2 || ts3native_counters.oiClone == oc0) {
                    printf("K8 WARNING: [OptsInit] Enabled=2, %d plans differ, %ld clone-run zeroings\n", on2, ts3native_counters.oiClone - oc0);
                    assert(false);
                }
                DWORD old;
                VirtualProtect((void*)(uintptr_t)0x00652BF0, 5, PAGE_EXECUTE_READWRITE, &old);
                memcpy((void*)(uintptr_t)0x00652BF0, saved, 5);  // off again for the next seed (the trampoline leaks)
                VirtualProtect((void*)(uintptr_t)0x00652BF0, 5, old, &old);
                g_oiJmp = NULL;
            }
            K8Negative();
            K8Drop();
        }
        g_k8Since = 0;
        if (g_rfSites) {  // one line per game call site (return address - 5 = the call); plans = K8's live + copy + [OptsInit] runs
            g_rfSites = false;
            LONG nAl = 0, nFr = 0;
            for (int i = 0; i < 1024; i++) nAl += g_rfSiteN[i], nFr += g_rfSiteFree[i];
            printf("K8 alloc sites (STEERRO_ALLOCSITES): %ld allocs, %ld frees, %ld K8 plans, table full %ld\n", nAl, nFr, g_k8Plans, g_rfSiteFull);
            for (int i = 0; i < 1024; i++)
                if (g_rfSiteAt[i])
                    printf("K8 alloc site 0x%08lX %s %ld bytes %ld\n", g_rfSiteAt[i] - 5, g_rfSiteN[i] ? "alloc" : "free",
                           g_rfSiteN[i] ? g_rfSiteN[i] : g_rfSiteFree[i], g_rfSiteBytes[i]);
        }
        printf("K8 [OptsInit] (I-94-322): %ld plans with dirty stacks (copy side hub-like words): %ld differ without the options ctor "
               "patch, 0 with it\n", g_k8OiPlans, g_k8OiDiffOff);
        for (RkSite& s : g_k8Sites) memcpy((void*)(uintptr_t)s.at, s.saved, 5);
        for (int i = 0; i < 4; i++) *(DWORD*)(uintptr_t)kPoolGlobal[i] = savedPool[i];  // shortcut: the game's pool objects leak
        RkUnpatch();
        VirtualFree(g_k6Live, 0, MEM_RELEASE);
        g_k6Live = NULL;
        VirtualFree(g_k8Arena, 0, MEM_RELEASE);
        g_k8Arena = NULL;
        g_sfRaster = SfRasterC;
        *(DWORD*)(uintptr_t)0x011D9128 = savedRw, *(DWORD*)(uintptr_t)0x011ECBC4 = savedWm;
        long kinds = 0;
        for (int i = 0; i < 9; i++) kinds += g_k8Kind[i][0] > 0;
        printf("K8 (seeds %lu-%lu): PathPlan_WithRetries 0x661B50 (planner 0x65E0D0) on live W vs PcAcquire's W' with the live routing "
               "world, P, lots, R, G trees, W and replay world on PAGE_NOACCESS pages: %ld plans equal (return code, start / goal index, "
               "every node but the unset +0x36 / +0x3C), %ld guarded copy runs with no live access and no free of a non-EA block; "
               "%ld found (%ld nodes; codes 0:%ld 4:%ld 5:%ld 6:%ld), %ld through a lot exit, %ld with a water leg, %d of 9 start / goal "
               "kinds found, %ld same-lot pairs across levels\n",
               seedArg, seedArg + 1, g_k8Plans, g_k8Guarded, g_k8Found, g_k8Nodes, g_k8Ret[0], g_k8Ret[4], g_k8Ret[5], g_k8Ret[6], g_k8LotLeg,
               g_k8Water, (int)kinds, g_k8CrossLevel);
        printf("K8 reached: closest waypoints 0x65BB90 %ld (%ld found), lot exit gathers 0x654CC0 %ld (%ld with candidates, %ld "
               "candidates), world A* 0x676260 %ld (%ld found); %ld lot exits built, %ld cuts; I-94-321: %ld clone lot hashes "
               "(g+0x120 / g+0x140) private and equal at each cut, %ld negative runs (the byte copy before 0.33.4) read the live hash\n",
               g_k8Cw[0] + g_k8Cw[1], g_k8Cw[1], g_k8Ga[0] + g_k8Ga[1], g_k8Ga[1], g_k8Ga[2], g_k8As[0] + g_k8As[1], g_k8As[1], (long)g_k8Exits,
               g_k8Cuts, g_k8Hashes, g_k8Neg);
        printf("K8 fakes (world manager, terrain, physics, partition): lot lookups %ld, terrain heights %ld / %ld, sea %ld, ponds %ld, "
               "routable %ld, lot by id %ld, lot near %ld, segment lots %ld, terrain object %ld / %ld / %ld, partition rooms %ld, lot sea %ld, "
               "lot point %ld\n",
               g_k8Calls[0], g_k8Calls[1], g_k8Calls[2], g_k8Calls[3], g_k8Calls[4], g_k8Calls[5] + g_k8Calls[6], g_k8Calls[15], g_k8Calls[13],
               g_k8Calls[11], g_k8Calls[12], g_k8Calls[14], g_k8Calls[7], g_k8Calls[10], g_k8Calls[16], g_k8Calls[17]);
        // STEERRO_PLANDEADLINE: 3 of 4 classes cut a phase on purpose; the found rate and the 9 kinds hold for the uncut class only
        assert(g_k8Guarded == g_k8Plans && (g_k8Pd ? g_k8PdFound[0] * 4 > g_k8PdN[0] : g_k8Found > g_k8Plans / 3 && kinds == 9) && g_k8LotLeg > 0 && g_k8Neg == 2 &&
               g_k8Hashes > 0 && g_k8Ga[1] > 0 && g_k8Cw[1] > 0);
        if (g_k8Pd) {
            if (t_pd.open) PdClose(false);
            const Ts3nCounters& pc = ts3native_counters;
            printf("K8 [RouteAsync] PlanDeadlineMs (STEERRO_PLANDEADLINE), sweep plans by class (live == copy for all): none %ld (%ld "
                   "found, %ld water), all cut %ld (%ld found, %ld world A* capped), cell only %ld (%ld found), water only %ld (%ld "
                   "found, %ld water); stub skips cell %ld water %ld; sessions %ld past the deadline %ld\n",
                   g_k8PdN[0], g_k8PdFound[0], g_k8PdWater[0], g_k8PdN[1], g_k8PdFound[1], g_k8PdCapped, g_k8PdN[2], g_k8PdFound[2],
                   g_k8PdN[3], g_k8PdFound[3], g_k8PdWater[3], pc.pdSkipCell, pc.pdSkipWater, pc.pdSessions[1], pc.pdHit);
            assert(g_k8PdN[1] > 0 && g_k8PdN[2] > 0 && g_k8PdN[3] > 0 && pc.pdSkipCell > 0 && pc.pdSkipWater > 0 && g_k8PdCapped > 0);
            assert(g_k8PdFound[1] == 0 && g_k8PdWater[3] == 0);  // everything cut: no path; water cut: no found path has a water leg
        }
        if (g_k8Sp) {
            if (t_syp.open) SypEnd();  // the last copy run that faulted inside the planner (K8RunCopyTry)
            const Ts3nCounters& rc = ts3native_counters;
            const LONG plans = rc.sypPlans[0] + rc.sypPlans[1] + rc.sypPlans[2], us = rc.sypUs[0] + rc.sypUs[1] + rc.sypUs[2];
            LONG sum = 0;
            for (int i = 0; i < RJP_N; i++) sum += rc.sypPhaseUs[i];
            printf("K8 [RouteJobPhases] SyncPlans (STEERRO_SYNCPHASES): %d of %d sites, %ld sessions (%ld us, phases %ld us); world "
                   "A* calls entry loop %ld, open ground %ld, pre-pass %ld, other caller %ld; self us cand %ld, lot leg %ld, cell %ld, "
                   "world %ld, insert %ld, post %ld; dropped (unwound) %ld; sessions opened %ld, K8 plans %ld\n",
                   rjpSites, (int)RJS_GS0, plans, us, sum, rc.sypLoopN[0], rc.sypLoopN[1], rc.sypLoopN[2], rc.sypLoopN[3],
                   rc.sypPhaseUs[RJP_CAND], rc.sypPhaseUs[RJP_LOTLEG], rc.sypPhaseUs[RJP_CELL], rc.sypPhaseUs[RJP_WORLD],
                   rc.sypPhaseUs[RJP_INSERT], rc.sypPhaseUs[RJP_POST], rc.rjpDropped, rc.sypEntry[0], (LONG)g_k8Plans);
            assert(plans >= g_k8Plans && rc.sypEntry[0] == plans && rc.sypLoopN[0] + rc.sypLoopN[1] > 0 && rc.sypLoopN[3] == 0 &&
                   rc.sypPhaseUs[RJP_CAND] > 0 && rc.sypPhaseUs[RJP_LOTLEG] > 0 && sum <= us && sum >= us - plans * RJP_N);
            for (int i = 0; i < RJP_N; i++) assert(rc.rjpN[0][i] == 0);  // no worker rows
        }
        if (g_k8Rjp) {
            const Ts3nCounters& rc = ts3native_counters;
            printf("K8 [RouteJobPhases] (STEERRO_PHASES): %d of %d sites, %ld jobs; entries cand %ld, lot leg %ld, cell %ld, portal %ld, "
                   "insert %ld, post %ld, lot graph %ld, water %ld; dropped (unwound) %ld\n",
                   rjpSites, (int)RJS_GS0, rc.rjpJobs[0], rc.rjpN[0][RJP_CAND], rc.rjpN[0][RJP_LOTLEG], rc.rjpN[0][RJP_CELL],
                   rc.rjpN[0][RJP_PORTAL], rc.rjpN[0][RJP_INSERT], rc.rjpN[0][RJP_POST], rc.rjpN[0][RJP_LOTGRAPH], rc.rjpN[0][RJP_WATER],
                   rc.rjpDropped);
            assert(rc.rjpJobs[0] >= g_k8Plans && rc.rjpN[0][RJP_CAND] > 0 && rc.rjpN[0][RJP_LOTLEG] > 0 && rc.rjpN[0][RJP_INSERT] > 0);
            printf("K8 door stub (docs/159, DoorMemo=2 shadow): %s, asks %ld, repeats in a plan %ld, shadow compares %ld, mismatches %ld, "
                   "answer changes %ld\n", g_hcOn ? "installed" : "NOT installed", rc.hcDoorQ, rc.hcDoorRepeatJob, rc.dmShadow, rc.dmMismatch,
                   rc.hcDoorRepeatDiff);
            assert(g_hcOn && rc.dmMismatch == 0 && rc.hcDoorRepeatDiff == 0);
        }
    }
    printf("steerro-test ok\n");
    return 0;
}
