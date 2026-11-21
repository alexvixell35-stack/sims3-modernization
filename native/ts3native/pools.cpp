// [RouteSnap] private pools for the replay clone and the P4 clone run (docs/94 §16.6 asks for them: I-106-6 refuted;
// §16.7 status; counters v53). Included by ts3native.cpp before replay.cpp; installed by ApplyReplay (Lots=replay).
//
// The game's quadtree leaf / inner node pools, the planner's footprint map node pool and the A*, goal, cell and quadtree
// cell pools are unlocked free lists (pool object 0x20 bytes: +0 node size, +4 alignment, +8 nodes per block, +0xC block
// list, +0x10 free head, +0x14 block alloc cdecl(size, ctx), +0x18 block free, +0x1C ctx; node +0 next; refill 0x4E42F0,
// carve 0x4E4290) that ~70 entry points use without +0x918, one on the main thread, and PathPlanner_Reset 0x635B70 frees
// and recreates. The clone must not pop or push them. Each pool's pop and push read the pool pointer with one
// `mov eax, [global]` (A1 imm32, 5 bytes) [verified static, Steam 1.67]: that instruction becomes `call PlPop<i>` /
// `call PlPush<i>`, which hand back the pool to use:
//   pop:  the private pool when this thread is the replay op thread (g_rpOpTid: clone build, rebase, drain; tree pools) or
//         the P4 job thread (g_plJobTid, PoolsEnterJob / PoolsLeaveJob; every pool), else the game's;
//   push: the private pool when the node lies in our region (whatever the thread), else the game's. Counted bugs: an owner
//         thread pushing a game node (rpLivePool), a thread that owns nothing pushing a private node (plForeign).
// Private pools take the live pool object's parameters (or the known node size when the game has none yet), no blocks and
// our block callback, which carves from one reserved region (kPlRegionMB, committed as it grows, never handed back: the
// free lists recycle it). The map pool has no site: the clone's map M gets a private pool at M+0x1C (inline allocator
// object of 0x624A10 / 0x624AB0 / 0x6281A0: +0 the 0x18-byte node pool, +4 the EA sub-allocator for buckets, locked heap).
// Only eax and flags change in the stubs; flags are free at every site (the next instruction writes them).
// One private pool set serves both owners: the replay op and a P4 job must not overlap (plOverlap counts PoolsEnterJob
// while the other switch is held).

enum { PL_LEAF, PL_INNER, PL_ASTAR, PL_GOAL, PL_CELL, PL_QCELL, PL_N, PL_MAP = PL_N };
static DWORD g_plPopAt[PL_N] = {TS3A(0x00667920), TS3A(0x00667990), TS3A(0x0061AC90), TS3A(0x0061AE80), TS3A(0x0061EB90), TS3A(0x0066BB50)};
static DWORD g_plPushAt[PL_N] = {TS3A(0x00668962), TS3A(0x006679D8), TS3A(0x0061AD13), TS3A(0x0061B533), TS3A(0x0061EBE5), TS3A(0x0066BFAE)};
static DWORD g_plGlobal[PL_N] = {TS3A(0x011CF42C), TS3A(0x011CF430), TS3A(0x011CEF50), TS3A(0x011CEF68), TS3A(0x011CEF90), TS3A(0x011CF448)};  // the tests point them at their own
static const u8 kPlPushReg[PL_N] = {0, 1, 0, 0, 0, 0};  // node register at the push site: 0 esi, 1 ecx
static const DWORD kPlElem[PL_N + 1] = {0x14, 0x1C, 0, 0, 0, 0, 0x18};  // node size when the live pool object is not there yet
static volatile LONG g_rpOpTid;    // replay clone work (build, rebase, drain); set inside the +0x918 hold
static volatile LONG g_plJobTid;   // a P4 clone run (PoolsEnterJob)
static bool g_plJobOwn;            // that run took g_plSet (job thread only)
static __declspec(align(16)) u8 g_plPool[PL_N + 1][0x20];
static volatile LONG g_plMade[PL_N + 1];
static LONG g_plOn;                // tree sites patched and the region reserved (replay may run)
static u8* g_plBase;               // region [base, top) handed out, [top, end) reserved (the first region)
static u8* volatile g_plTop;
static u8* g_plEnd;                // end of the region the bump pointer is in (the first or the newest chunk)
static u8* g_plCommit;             // committed up to here in that region
static volatile LONG g_plLock;
static const DWORD kPlRegionMB = 96;
// [RouteSnap] PoolChunkMB (0.36.73, docs/66 §7 A1): 0 = one kPlRegionMB reserve at install (as before); 16-48 = the first
// region is that size and more regions of that size are reserved only when it fills, up to kPlRegionMB in all, so the
// unused part of the 96 MB stays in the free address space. Regions after the first: [lo, hi) with lo set before g_plNReg
// publishes it and hi only growing, so a push stub on any thread never sees a half-made range (the first region keeps
// g_plBase / g_plTop: the stubs' fast check is unchanged).
static DWORD g_plChunkMB;
static const int kPlMaxReg = 12;
static u8* g_plRegLo[kPlMaxReg];
static u8* volatile g_plRegHi[kPlMaxReg];
static volatile LONG g_plNReg;     // regions after the first
static DWORD g_plResMB, g_plComBytes;

static bool PlChunk(DWORD size) {  // g_plLock held: reserve the next region
    Ts3nCounters& k = ts3native_counters;
    const DWORD need = (size + (1u << 20) - 1) >> 20, mb = g_plChunkMB > need ? g_plChunkMB : need;
    if (!g_plChunkMB || g_plNReg >= kPlMaxReg || g_plResMB + mb > kPlRegionMB) return false;
    u8* b = (u8*)VirtualAlloc(NULL, mb << 20, MEM_RESERVE, PAGE_NOACCESS);
    if (!b) return false;
    g_plRegLo[g_plNReg] = g_plRegHi[g_plNReg] = b;
    MemoryBarrier();
    InterlockedIncrement(&g_plNReg);
    g_plCommit = b, g_plEnd = b + (mb << 20);
    g_plResMB += mb;
    k.plChunks = g_plNReg + 1, k.plResKB = (LONG)(g_plResMB << 10), k.plSavedKB = (LONG)((kPlRegionMB - g_plResMB) << 10);
    return true;
}

// block callback of the private pools (cdecl(size, ctx), as 0x624590): bump from the region; NULL when full (the game's
// pop then returns 0: a fault in our clone work, caught there)
static void* __cdecl PlBlockAlloc(DWORD size, DWORD) {
    Ts3nCounters& k = ts3native_counters;
    while (InterlockedExchange(&g_plLock, 1)) YieldProcessor();
    for (;;) {
        const LONG r = g_plNReg;
        u8* p = (u8*)(((uintptr_t)(r ? g_plRegHi[r - 1] : g_plTop) + 15) & ~(uintptr_t)15);
        u8* e = p + size;
        if (!g_plBase || e > g_plEnd || e < p) {
            if (g_plBase && PlChunk(size)) continue;
            InterlockedExchange(&g_plLock, 0);
            InterlockedIncrement(&k.plFull);
            return NULL;
        }
        if (e > g_plCommit) {
            u8* c = (u8*)(((uintptr_t)e + 0xFFFF) & ~(uintptr_t)0xFFFF);
            if (c > g_plEnd) c = g_plEnd;
            if (!VirtualAlloc(g_plCommit, c - g_plCommit, MEM_COMMIT, PAGE_READWRITE)) {
                InterlockedExchange(&g_plLock, 0);
                InterlockedIncrement(&k.plFull);
                return NULL;
            }
            g_plComBytes += (DWORD)(c - g_plCommit);
            g_plCommit = c;
            k.plKB = (LONG)(g_plComBytes >> 10);
        }
        if (r) g_plRegHi[r - 1] = e; else g_plTop = e;
        InterlockedExchange(&g_plLock, 0);
        return p;
    }
}
static void __cdecl PlBlockFree(void*, DWORD) {}  // never called: nothing destroys our pool objects

static bool PlRegion() {
    if (g_plBase) return true;
    const DWORD want = g_plChunkMB ? g_plChunkMB : kPlRegionMB;
    DWORD mb = want;  // smaller when the address space has no such block (offline tests map the whole exe)
    for (; mb >= 16 && !(g_plBase = (u8*)VirtualAlloc(NULL, mb << 20, MEM_RESERVE, PAGE_NOACCESS)); mb /= 2) {}
    if (!g_plBase) return false;
    g_plTop = g_plCommit = g_plBase;
    g_plEnd = g_plBase + (mb << 20);
    g_plResMB = mb;
    Ts3nCounters& k = ts3native_counters;
    k.plChunkMB = (LONG)g_plChunkMB, k.plChunks = 1, k.plResKB = (LONG)(mb << 10), k.plSavedKB = (LONG)((kPlRegionMB - mb) << 10);
    k.plReady |= 4;
    if (mb != want) Log("private pools: region %lu MB (no %lu MB block free)", mb, want);
    return true;
}
static bool __cdecl PlOwnsChunk(const u8* q) {  // the regions after the first
    for (LONG i = 0, n = g_plNReg; i < n; i++) if (q >= g_plRegLo[i] && q < g_plRegHi[i]) return true;
    return false;
}
static inline bool PlOwns(const void* p) { return ((const u8*)p >= g_plBase && (const u8*)p < g_plTop) || (g_plNReg && PlOwnsChunk((const u8*)p)); }

// the private pool i, made on first use from the live pool object's parameters; NULL when it cannot be made (counted)
static u8* PlPrivate(int i, const u8* live) {
    if (g_plMade[i]) return g_plPool[i];
    if ((!live && !kPlElem[i]) || !g_plBase) { InterlockedIncrement(&ts3native_counters.rpLivePool); return NULL; }
    while (InterlockedExchange(&g_plLock, 1)) YieldProcessor();
    if (!g_plMade[i]) {
        u8* p = g_plPool[i];
        if (live) memcpy(p, live, 0x20);  // +0 size, +4 alignment, +8 per block: the live pool's parameters
        else *(DWORD*)p = kPlElem[i], *(DWORD*)(p + 4) = 8, *(DWORD*)(p + 8) = 0x80;  // as 0x62D710 builds the map pool
        *(DWORD*)(p + 0xC) = 0, *(DWORD*)(p + 0x10) = 0;  // no blocks, empty free list
        *(void**)(p + 0x14) = (void*)PlBlockAlloc, *(void**)(p + 0x18) = (void*)PlBlockFree, *(DWORD*)(p + 0x1C) = 0;
        g_plMade[i] = 1;
    }
    InterlockedExchange(&g_plLock, 0);
    return g_plPool[i];
}
// pop stub slow path: the private pool, else the game's (the game's pop then works as before; rpLivePool counted)
static u8* __cdecl PlPopSlow(int i) {
    const u8* live = *(const u8* const*)(uintptr_t)g_plGlobal[i];
    u8* p = PlPrivate(i, live);
    return p ? p : (u8*)live;
}

// a game-layout pool's pop and push in C, as 0x667920 (refill 0x4E42F0, carve 0x4E4290) and 0x6679D0 do them; our clone
// build pops the private pools with it directly; replay-fake.h has the push and the stub calls for the tests' stand-ins
static void* PlPopFrom(u8* pool) {
    if (!pool) return NULL;
    u8** head = (u8**)(pool + 0x10);
    if (!*head) {
        const DWORD elem = *(DWORD*)pool, align = *(DWORD*)(pool + 4), size = (*(DWORD*)(pool + 8) + 2) * elem + 0xC;
        u8* b = (u8*)((void*(__cdecl*)(DWORD, DWORD))*(void**)(pool + 0x14))(size, *(DWORD*)(pool + 0x1C));
        if (!b) return NULL;
        *(DWORD*)b = *(DWORD*)(pool + 0xC), *(DWORD*)(b + 4) = size;
        *(u8**)(pool + 0xC) = b;
        u8* p = (u8*)(((uintptr_t)b + align + 7) & ~(uintptr_t)(align - 1));
        u8* const end = b + size - 2 * elem;
        if (p > end) return NULL;
        *head = p;
        for (u8* n;; p = n) { n = p + elem; *(u8**)p = n; if (n > end) { p = n; break; } }
        *(u8**)p = NULL;
    }
    u8* n = *head;
    *head = *(u8**)n;
    return n;
}
// a node from private pool i (replay.cpp's clone build and rebase); NULL = none (the build fails)
static void* PlAlloc(int i) { return PlPopFrom(PlPrivate(i, *(const u8* const*)(uintptr_t)g_plGlobal[i])); }

// the map: the clone's M keeps its +4.. (bucket sub-allocator, locked heap) and gets the private node pool at +0x1C
static u8* PlMapPool(const u8* livePool) { return g_plOn ? PlPrivate(PL_MAP, livePool) : NULL; }

// pop stub: `tree` = eax for the tree pools (the compare passes: the replay op thread too), 0 for the others (never a tid)
#define PL_POP(i, tree) static __declspec(naked) void PlPop##i() { __asm { \
    __asm mov eax, dword ptr fs:[24h] __asm cmp eax, g_plJobTid __asm je priv \
    __asm cmp eax, tree __asm jne live __asm cmp eax, g_rpOpTid __asm je priv \
    __asm live: __asm mov eax, dword ptr [g_plGlobal + i * 4] __asm mov eax, [eax] __asm ret \
    __asm priv: __asm push ecx __asm push edx __asm push i __asm call PlPopSlow __asm add esp, 4 __asm pop edx __asm pop ecx __asm ret } }
// The sink: a pool object nobody pops. A P4 clone run (worker, no +0x918) pushing a game node, or a thread that owns no clone
// work pushing a private A* family node, would write an unlocked free list that another thread pops at the same time; such
// a node is leaked here instead (still counted: rpLivePool / plForeign must stay 0). docs/67 §8: the 2026-10-19 bp-snap-p4
// crash on 0.31.4 was an A* graph node on a free list while the sim thread's A* still held it.
static __declspec(align(16)) u8 g_plSink[0x20];
static volatile LONG g_plSinkLogged;
static void __cdecl PlSinkNote(int i, int foreign, void* node) {
    if (InterlockedIncrement(&g_plSinkLogged) <= 8)
        Log("private pools: pool %d node 0x%p leaked to the sink (%s, thread %lu)", i, node,
            foreign ? "a private node pushed by a thread owning no clone work" : "a game node pushed by the P4 clone run", GetCurrentThreadId());
}
// push stub: node in esi or ecx; a private node goes to the private pool, counted when no owner thread pushes it
// (`fdest`: the tree pools keep pushing those to the private pool, the A* family leaks them to the sink); a game node pushed
// by the P4 job thread always goes to the sink
#define PL_PUSH(i, tree, nodereg, fdest) static __declspec(naked) void PlPush##i() { __asm { \
    __asm mov eax, nodereg __asm cmp eax, g_plBase __asm jb chunk __asm cmp eax, g_plTop __asm jb own \
    __asm chunk: __asm cmp g_plNReg, 0 __asm je live \
    __asm push ecx __asm push edx __asm push nodereg __asm call PlOwnsChunk __asm add esp, 4 __asm pop edx __asm pop ecx \
    __asm test al, al __asm je live \
    __asm own: __asm mov eax, dword ptr fs:[24h] __asm cmp eax, g_plJobTid __asm je mine \
    __asm cmp eax, tree __asm jne foreign __asm cmp eax, g_rpOpTid __asm je mine \
    __asm foreign: __asm lock inc ts3native_counters.plForeign __asm jmp fdest \
    __asm mine: __asm lea eax, [g_plPool + i * 0x20] __asm ret \
    __asm fsink: __asm push ecx __asm push edx __asm push nodereg __asm push 1 __asm jmp note \
    __asm live: __asm mov eax, dword ptr fs:[24h] __asm cmp eax, g_plJobTid __asm je jobbad \
    __asm cmp eax, tree __asm jne game __asm cmp eax, g_rpOpTid __asm je bad \
    __asm game: __asm mov eax, dword ptr [g_plGlobal + i * 4] __asm mov eax, [eax] __asm ret \
    __asm bad: __asm lock inc ts3native_counters.rpLivePool __asm jmp game \
    __asm jobbad: __asm lock inc ts3native_counters.rpLivePool __asm push ecx __asm push edx __asm push nodereg __asm push 0 \
    __asm note: __asm push i __asm call PlSinkNote __asm add esp, 12 __asm pop edx __asm pop ecx \
    __asm lea eax, [g_plSink] __asm ret } }
PL_POP(0, eax) PL_POP(1, eax) PL_POP(2, 0) PL_POP(3, 0) PL_POP(4, 0) PL_POP(5, 0)
#pragma warning(push)
#pragma warning(disable : 4102)  // the tree pools never jump to fsink
PL_PUSH(0, eax, esi, mine) PL_PUSH(1, eax, ecx, mine)
#pragma warning(pop)
PL_PUSH(2, 0, esi, fsink) PL_PUSH(3, 0, esi, fsink) PL_PUSH(4, 0, esi, fsink) PL_PUSH(5, 0, esi, fsink)
static void* const kPlPopStub[PL_N] = {(void*)PlPop0, (void*)PlPop1, (void*)PlPop2, (void*)PlPop3, (void*)PlPop4, (void*)PlPop5};
static void* const kPlPushStub[PL_N] = {(void*)PlPush0, (void*)PlPush1, (void*)PlPush2, (void*)PlPush3, (void*)PlPush4, (void*)PlPush5};
// pools [first, last]: every pop and push site must read `mov eax, [global]` first (all or none); then call-site redirects
static bool PlPatch(int first, int last, const char* what) {
    char hex[32];
    for (int i = first; i <= last; i++)
        for (int s = 0; s < 2; s++) {
            const u8* p = (const u8*)(uintptr_t)(s ? g_plPushAt[i] : g_plPopAt[i]);
            u8 want[5] = {0xA1};
            *(DWORD*)(want + 1) = g_plGlobal[i];
            if (Matches(p, want, 5)) continue;
            if (Readable(p, 5)) Hex(p, 5, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
            Log("SKIP private pools (%s): %s site 0x%08lX bytes %s differ, nothing patched", what, s ? "push" : "pop", (DWORD)(uintptr_t)p, hex);
            return false;
        }
    if (!PlRegion()) { Log("SKIP private pools (%s): no %lu MB region", what, kPlRegionMB); return false; }
    for (int i = first; i <= last; i++)
        for (int s = 0; s < 2; s++) {
            u8* p = (u8*)(uintptr_t)(s ? g_plPushAt[i] : g_plPopAt[i]);
            u8 b[5] = {0xE8};
            *(LONG*)(b + 1) = (LONG)((u8*)(s ? kPlPushStub[i] : kPlPopStub[i]) - (p + 5));
            if (!Write(p, b, 5)) { Log("FAIL private pools (%s): write at 0x%p (err %lu)", what, p, GetLastError()); return false; }
        }
    return true;
}
// the live pool objects the clone was built against; a change (PathPlanner_Reset 0x635B70 recreates them) is a new epoch
struct PlSeen { DWORD leaf, inner, map; };
static PlSeen PlSeenNow(const u8* p) {
    PlSeen s = {0, 0, 0};
    if (!g_plOn) return s;  // no sites: the globals may not even be mapped (offline tests)
    __try {
        s.leaf = *(const DWORD*)(uintptr_t)g_plGlobal[PL_LEAF], s.inner = *(const DWORD*)(uintptr_t)g_plGlobal[PL_INNER];
        if (p) s.map = *(const DWORD*)(p + 0x28);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return s;
}
static bool PlMoved(const PlSeen& was, const u8* p) {
    if (!g_plOn) return false;
    const PlSeen now = PlSeenNow(p);
    return now.leaf != was.leaf || now.inner != was.inner || now.map != was.map;
}

// replay clone work: this thread's tree pool pops are private between RpOpEnter and RpOpLeave (the caller holds +0x918).
// Functions, not a scope object: the callers use __try.
// 0.35.12 (docs/94 §22.15, I-94-460): the private pool set has one owner at a time (g_plSet). Before, the sim's cut
// (rebase, clone build: tree and map pool pops under +0x918) ran while a Mode=1 shadow on the worker (no +0x918, private
// pools by g_plJobTid) popped and pushed the same unlocked free lists; plOverlap saw only the job-enters-second order
// (BP/IP 0.35.9: 2 / 4). Now the owner holds g_plSet: the sim only tries (RpOpTry: never a wait while it holds +0x918,
// which a P5 ask takes inside a run), the consumer tries too (busy = retry), a P4 run waits for it (PoolsEnterJob).
static SRWLOCK g_plSet = SRWLOCK_INIT;
static bool RpOpTry(LONG* prev) {
    const LONG me = (LONG)GetCurrentThreadId();
    if (g_rpOpTid == me || g_plJobTid == me) { *prev = me; return true; }  // nested: this thread owns the set already
    if (!TryAcquireSRWLockExclusive(&g_plSet)) return false;
    *prev = InterlockedExchange(&g_rpOpTid, me);
    return true;
}
static inline LONG RpOpEnter() {  // the tests (inline: the DLL no longer calls it, 0.35.13)
    LONG prev;
    while (!RpOpTry(&prev)) Sleep(0);
    return prev;
}
static void RpOpLeave(LONG prev) {
    const LONG me = (LONG)GetCurrentThreadId();
    if (prev == me) return;  // nested
    if (g_rpOpTid != me) return;  // not this thread's (a test's stray leave)
    InterlockedExchange(&g_rpOpTid, prev);
    ReleaseSRWLockExclusive(&g_plSet);
}

// P4 (p4.cpp, docs/94 §16.6 P4 note): around a clone run on the worker, every pool this thread pops is private (A*, goal,
// cell, quadtree cell, tree). false = the sites are not in place: run no clone search. The run must not overlap replay clone
// work on another thread (one private pool set; plOverlap counts it) and must free its nodes on this thread before
// PoolsLeaveJob (a push from any other thread counts plForeign).
static bool PoolsEnterJob() {
    Ts3nCounters& k = ts3native_counters;
    if ((k.plReady & 3) != 3) return false;
    const LONG me = (LONG)GetCurrentThreadId();
    if (g_plJobTid == me) { InterlockedIncrement(&k.plOverlap); return true; }  // nested enter: a pairing bug of ours
    const bool own = g_rpOpTid != me;  // this thread's replay op owns the set already
    if (own) AcquireSRWLockExclusive(&g_plSet);  // the sim's cut holds it for a few ms at most; no lock of ours inside
    const LONG op = g_rpOpTid;
    if (InterlockedExchange(&g_plJobTid, me) || (op && op != me)) InterlockedIncrement(&k.plOverlap);
    g_plJobOwn = own;
    InterlockedIncrement(&k.plJobs);
    return true;
}
static void PoolsLeaveJob() {
    if (g_plJobTid != (LONG)GetCurrentThreadId()) return;
    const bool own = g_plJobOwn;
    InterlockedExchange(&g_plJobTid, 0);
    if (own) ReleaseSRWLockExclusive(&g_plSet);
}
static void PoolsEnterJobHook() { PoolsEnterJob(); }
extern void (*g_p4PoolsEnterJob)();  // p4.cpp: called around each clone run (NULL = the run uses the game's pools)
extern void (*g_p4PoolsLeaveJob)();

// from ApplyReplay: tree sites (required, else no replay) and the A* family (P4 jobs; optional)
static bool PlInstall() {
    Ts3nCounters& k = ts3native_counters;
    if (!(k.plReady & 1)) {
        if (!PlPatch(PL_LEAF, PL_INNER, "tree pools")) return false;
        k.plReady |= 1;
        g_plOn = 1;
    }
    if (!(k.plReady & 2) && PlPatch(PL_ASTAR, PL_QCELL, "A*, goal, cell and quadtree cell pools")) k.plReady |= 2;
    if (k.plReady & 2) g_p4PoolsEnterJob = PoolsEnterJobHook, g_p4PoolsLeaveJob = PoolsLeaveJob;
    Log("OK   private pools (docs/94 §16.7): tree sites 0x667920 0x668962 0x667990 0x6679D8%s, %lu MB region%s",
        k.plReady & 2 ? ", A* family 0x61AC90 0x61AD13 0x61AE80 0x61B533 0x61EB90 0x61EBE5 0x66BB50 0x66BFAE" : " (A* family SKIP: P4 clone runs keep the game's A* pools)",
        (DWORD)((g_plEnd - g_plBase) >> 20), g_plChunkMB ? " (PoolChunkMB: more regions of that size as it fills, up to 96 MB)" : "");
    return true;
}

