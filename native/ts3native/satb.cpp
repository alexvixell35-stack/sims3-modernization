// [AutShadow] Satb (docs/206 option 2b, §7): copy on first write between the cut and the worker's end, so the worker can run
// while the sim interprets the next tick and still read the managed state of the cut. Included by autshadow.cpp before
// autshadow-run.cpp (part 2 calls SatbBegin / SatbEnd at the cut and the window close; monohost gets it the same way).
//
// The log (one per job, single writer = the sim thread, lock-free readers = the worker):
//   ent[]  insertion-ordered (word address, value before the first post-cut write); its used part is pushed as GC roots
//          while a job may read, so the old values (pointers) stay alive and the logged words' objects are not reused;
//   idx[]  open-addressing hash, slot = (job epoch16 << 16) | (entry + 1): a slot of an older job reads as empty;
//   page[] one byte per 4 KB page of the 4 GB space = job epoch8 when the page has an entry (the worker's fast test).
// Order (x86 TSO, docs/51 I-398-4): the sim writes the entry, then its slot, then the page byte, then the field; the
// worker loads the field, then the page byte, then the slot. A worker that saw the new field value sees the entry; one
// that saw the old value takes the same value from the entry or keeps its own.
// A new job is O(1): both epochs move on (8-bit wrap clears page[], 16-bit wrap clears idx[]).
//
// Modes (ini [AutShadow] Satb): 0 off; 1 probe (S0): the store barrier logs on the sim for one worker-job length of the
// tick after a finished job (the worker never runs concurrently: today's window/wait flow), counts stores, first-written
// words, pages, sim loads and IC calls; 2 act: barrier + worker read redirect while the worker runs concurrently with the
// next tick (see autshadow-run.cpp). Every unknown store path (log full, a store over SATB_BIGW words, an IC that may
// write shared memory through a pointer argument) makes the sim stop the worker and wait for it to leave (act) or only
// counts (probe): the worker never reads a word whose cut value was lost.

static const DWORD SATB_CAP = 0xFFFE;                  // entries per job (entry + 1 fits 16 bits)
static const DWORD SATB_HN = 1u << 17, SATB_HMASK = SATB_HN - 1;
static const DWORD SATB_BIGW = 1024;                   // larger single stores (cpblk, initblk, big vt) wait instead
struct SatbEnt { volatile DWORD addr, old; };
struct SatbMem {
    SatbEnt ent[SATB_CAP + 2];
    volatile DWORD idx[SATB_HN];
    volatile u8 page[1u << 20];
};
enum { SATB_OFF, SATB_PROBE, SATB_ACT };
enum { SATB_ST_FULL = 1, SATB_ST_BIG = 2, SATB_ST_IC = 4, SATB_ST_RT = 8, SATB_ST_CONFLICT = 16, SATB_ST_TIMEOUT = 32, SATB_ST_GIVEUP = 64,
       SATB_ST_CLASS = 128 };
static int g_satbMode;
static SatbMem* g_satb;
static volatile LONG g_satbN;            // entries used in this job
static volatile DWORD g_satbE8, g_satbE16;  // current job epochs (0 = no job yet)
static volatile LONG g_satbLive;         // a job may read the log: push the entries as roots
static volatile LONG g_satbStale;        // SATB_ST_* seen in this job (act: the job was stopped before the event)
static volatile DWORD g_satbWTid;        // worker thread id the stubs test (0 = none)
// docs/206 §11: the sim's load path reads the worker id and counts its loads on every heap load of a span; both sit on their
// own cache lines (the neighbours above are written by the worker during a job: a shared line would bounce per load)
struct __declspec(align(64)) SatbLine { volatile DWORD v; DWORD pad[15]; };
static SatbLine g_satbWTidL;              // = g_satbWTid (set together in SatbBegin), read by the load thunks
static SatbLine g_satbSimLoadsL;          // sim loads through the load thunks (folded into satbLoads when read)
static DWORD g_satbGcNo0;                 // GC_gc_no at SatbBegin (docs/206 §15, weak handles)
static volatile const DWORD* g_satbGcNo = (volatile const DWORD*)(uintptr_t)0x011F2F90;  // GC_gc_no (docs/66 §6.1); selftest repoints
static void SatbLogTicks();               // below: Simulator/SimClock sSimTickCnt at the cut
static DWORD g_satbWStaticAt;             // below (SatbWStaticView): worker static copies, reset per span
static LONG g_satbPageN, g_satbJobWords;  // pages / words first marked in this job
static void SatbHookGrow();               // below: the worker's arena chunks taken during a job
static void SatbGiveOpLog();              // below: worker give-ups by op, logged at span end
static void SatbColdNameLog();            // below: methods the worker transformed first, named at span end
static void SatbBodyLog();                // below: binder bodies seen in spans (docs/206 §15)
static volatile LONG g_satbWArenaN;      // chunks the worker took in this job (SatbHookGrow)

static bool SatbAlloc() {
    if (!g_satb) g_satb = (SatbMem*)VirtualAlloc(NULL, sizeof(SatbMem), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    return g_satb != NULL;
}
static __forceinline DWORD SatbHash(DWORD a) { return ((a >> 2) * 0x9E3779B1u) >> (32 - 17); }
// Which words the barrier logs [verified, docs/data/satb-handlers.csv]: the interpreter's frame arena chunks are GC heap
// objects (0xD718A0 refill, GC_malloc_explicitly_typed) handed between threads through one shared pool, and primitive
// statics live in the domain mempool (not a heap section), so no address range separates shared from private memory.
// The barrier logs every word a store opcode writes; the only filter is the sim's own stack and current arena chain (a
// cost filter: the worker never reads those words). The worker's side of a reused chunk: SatbWArena* below.

// sim: a new job. Only while the worker is idle (no reader), from the sim thread (the only writer).
static void SatbReset() {
    SatbMem& m = *g_satb;
    if (++g_satbE8 > 255) { memset((void*)m.page, 0, sizeof(m.page)); g_satbE8 = 1; }
    if (++g_satbE16 > 0xFFFF) { memset((void*)m.idx, 0, sizeof(m.idx)); g_satbE16 = 1; }
    g_satbN = 0, g_satbStale = 0, g_satbPageN = 0, g_satbJobWords = 0;
}

// sim: the value before the first write of word a (4-aligned) since the job began; false = the log is full
static bool SatbLogWord(DWORD a) {
    SatbMem& m = *g_satb;
    const DWORD tag = g_satbE16 << 16;
    DWORD h = SatbHash(a);
    if (m.page[a >> 12] == (u8)g_satbE8) {
        for (;;) {
            const DWORD v = m.idx[h];
            if ((v & 0xFFFF0000u) != tag) break;
            if (m.ent[(v & 0xFFFF) - 1].addr == a) return true;
            h = (h + 1) & SATB_HMASK;
        }
    } else {
        while ((m.idx[h] & 0xFFFF0000u) == tag) h = (h + 1) & SATB_HMASK;
    }
    const LONG n = g_satbN;
    if ((DWORD)n >= SATB_CAP) return false;
    m.ent[n].old = *(volatile DWORD*)(uintptr_t)a;
    m.ent[n].addr = a;
    _ReadWriteBarrier();
    m.idx[h] = tag | (DWORD)(n + 1);
    g_satbN = n + 1;
    _ReadWriteBarrier();
    if (m.page[a >> 12] != (u8)g_satbE8) m.page[a >> 12] = (u8)g_satbE8, g_satbPageN++, ts3native_autshadow.satbPages++;
    g_satbJobWords++, ts3native_autshadow.satbWords++;
    return true;
}

// worker: the logged value of word a, if the sim changed it in this job
static __forceinline bool SatbFind(DWORD a, DWORD* old) {
    const SatbMem& m = *g_satb;
    if (m.page[a >> 12] != (u8)g_satbE8) return false;
    const DWORD tag = g_satbE16 << 16;
    for (DWORD h = SatbHash(a);; h = (h + 1) & SATB_HMASK) {
        const DWORD v = m.idx[h];
        if ((v & 0xFFFF0000u) != tag) return false;
        const SatbEnt& e = m.ent[(v & 0xFFFF) - 1];
        if (e.addr == a) { *old = e.old; return true; }
    }
}

// worker: buf holds the live bytes [addr, addr + n) just loaded; put back every logged word's cut bytes. Returns hits.
static int SatbMerge(DWORD addr, DWORD n, u8* buf) {
    int hits = 0;  // no shared-page test: the sim logs shared pages only, so any entry is one
    for (DWORD w = addr & ~3u; w < addr + n; w += 4) {
        DWORD old;
        if (!SatbFind(w, &old)) continue;
        hits++;
        for (DWORD b = 0; b < 4; b++) {
            const DWORD at = w + b;
            if (at >= addr && at < addr + n) buf[at - addr] = (u8)(old >> (8 * b));
        }
    }
    return hits;
}

static LONG SatbUs(const LARGE_INTEGER& t0) {
    static double perTick;
    if (!perTick) { LARGE_INTEGER f; QueryPerformanceFrequency(&f); perTick = 1e6 / (double)f.QuadPart; }
    LARGE_INTEGER t1;
    QueryPerformanceCounter(&t1);
    return (LONG)((t1.QuadPart - t0.QuadPart) * perTick);
}
static void SatbMax(LONG& m, LONG v) { if (v > m) m = v; }

// ---- jump-table swap (docs/130 §1.2: head 0xE544D5 `jmp [op*4 + 0xE59880]`, 0x1C1 entries, shared by every thread) ----
// Wrapped entries point at our stubs only while a span is on; outside spans the table is the game's (zero cost).
static const DWORD SATB_NOP = 0x1C1;
static volatile DWORD* g_satbTable = (volatile DWORD*)(uintptr_t)0x00E59880;  // selftest / monohost may point elsewhere
static DWORD g_satbHead = 0x00E544D5;          // dispatch head: a stub that emulated an instruction continues here
static void* g_satbOrig[SATB_NOP];             // the table's entries at install
static void* g_satbMine[SATB_NOP];             // our stub per wrapped opcode (span on)
static u8 g_satbKind[SATB_NOP];                // SATB_K_*
static WORD g_satbBase[SATB_NOP];              // superinstruction -> its base sequence's first opcode (worker de-fuse)
enum { SATB_K_NONE, SATB_K_STORE, SATB_K_LOAD, SATB_K_SUPER, SATB_K_ADDR };
static volatile LONG g_satbOn;                 // stubs in the table
static LARGE_INTEGER g_satbT0;
static bool g_satbTestNoBarrier;  // monohost negative control: the concurrent flow without stubs or wrappers
static void SatbSwap(bool in) {
    if (in && g_satbTestNoBarrier) return;
    for (DWORD op = 0; op < SATB_NOP; op++)
        if (g_satbMine[op] && (g_satbMode == SATB_ACT || g_satbKind[op] != SATB_K_SUPER))
            g_satbTable[op] = (DWORD)(uintptr_t)(in ? g_satbMine[op] : g_satbOrig[op]);
    g_satbOn = in ? 1 : 0;
    g_asSatbIc = in ? 1 : 0;  // sim IC calls checked while the span is on
}

// ---- sim: wait for the worker to leave before a write the log cannot cover (act); probe only counts ----
static bool(__cdecl* g_satbWorkerBusy)() = NULL;  // part 2: the job is still inside the worker
static void SatbSimWait(int why) {
    Ts3nAutShadow& k = ts3native_autshadow;
    InterlockedOr(&g_satbStale, why);
    if (g_satbMode != SATB_ACT || !g_satbWTid || !g_satbWorkerBusy || !g_satbWorkerBusy()) return;
    InterlockedExchange(&g_asStop, 1);  // the driver leaves after its current meta object; worker ICs abort `stop`
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    k.satbWaits++;
    for (int spin = 0; g_satbWorkerBusy(); spin++) {
        if (spin < 64) YieldProcessor(); else SwitchToThread();
        // 100 ms: a class-init lock the sim holds could block the worker; give up (the job is dropped as stale)
        if ((spin & 63) == 63 && SatbUs(t0) > 100000) { k.satbWaitTimeouts++; InterlockedOr(&g_satbStale, SATB_ST_TIMEOUT); break; }
    }
    k.satbWaitUs += SatbUs(t0);
}

// sim thread: its own stack, or a chunk of its current interpreter state's arena chain (chunk +0 top, +4 end, +8 previous;
// docs/130 §1.3, the 0xD71900 room test [verified]). No managed reference into either can sit in the heap.
static bool SatbSimPrivate(DWORD a) {
    if (a < __readfsdword(4) && a >= __readfsdword(8)) return true;
    const DWORD* st = g_curState ? (const DWORD*)(uintptr_t)*g_curState : NULL;
    if (!st) return false;
    int n = 0;
    for (const DWORD* c = (const DWORD*)(uintptr_t)st[0x13C / 4]; c && n < 64; c = (const DWORD*)(uintptr_t)c[2], n++)
        if (a >= (DWORD)(uintptr_t)c && a < c[1]) return true;
    return false;
}

// sim: log every word of [addr, addr + n) before the store lands
static void SatbSimRange(DWORD addr, DWORD n) {
    if (addr < 0x10000 || !n) return;  // null + offset: the game's handler throws; nothing is written
    if (SatbSimPrivate(addr)) { ts3native_autshadow.satbSimArena++; return; }
    const DWORD lo = addr & ~3u, hi = (addr + n + 3) & ~3u;
    if ((hi - lo) / 4 > SATB_BIGW) { ts3native_autshadow.satbBig++; SatbSimWait(SATB_ST_BIG); return; }
    for (DWORD w = lo; w < hi; w += 4)
        if (!SatbLogWord(w)) { ts3native_autshadow.satbOverflow++; SatbSimWait(SATB_ST_FULL); return; }
}

// ---- EA ICs on the sim during a span (guard thunk, autshadow.cpp AsGuardEntry): the body can write managed memory through
// its byref/out and array arguments. Each such argument into shared memory has its writable range logged before the body
// runs (docs/206 §9): byref = the parameter type's size, array = its elements, byref array = the slot and the current
// array's elements. Only the parameter's own argument dword is read (satb-ics.h: dword index, kind, size; 0.36.125 scanned
// every dword up to the last byref parameter, so object ids and floats >= 0x10000 counted as shared pointers: S0 BP 102,962
// of 102,962 calls). A range the log cannot take (over SATB_IC_MAXW words, log full) or a parameter of unknown layout: wait
// for the worker (act), count (probe). An argument in a thread's stack or the interpreter's frame arena is private.
// [inferred: EA IC bodies write managed memory only through their pointer arguments and the hidden struct-return slot, which
// mint puts on its eval stack (no IC of the 180 returns a struct); native field writes go through 0xE69820, hooked in
// SatbInstallRt; an array argument's length and element size do not change during the call]
#include "satb-ics.h"
static const DWORD SATB_IC_MAXW = 16384;  // one IC argument logs at most this many words (the log holds SATB_CAP)
static short g_satbIcRow[AS_MAXIC];       // kSatbIcs row + 1 per guard index (0 = no byref/array parameter, also before the mapping)
static LONG g_satbIcSiteLog[AS_MAXIC], g_satbIcSiteWait[AS_MAXIC];  // per IC: arguments logged / waits (log lines at 1, 10, 100, ...)
static void SatbIcSiteNote(DWORD idx, LONG* tab, const char* what) {
    const LONG n = ++tab[idx];
    for (LONG p = 1; p <= n && p <= 100000000; p *= 10)
        if (n == p) { Log("autshadow satb ic %s: %s x%ld", what, g_asName[idx] ? g_asName[idx] : "?", n); break; }
}
// sim: log [addr, addr + n) for an IC argument; false = the log cannot take it (the caller waits)
static bool SatbIcRange(DWORD addr, DWORD n) {
    const DWORD lo = addr & ~3u, hi = (addr + n + 3) & ~3u;
    if ((hi - lo) / 4 > SATB_IC_MAXW) return false;
    Ts3nAutShadow& k = ts3native_autshadow;
    for (DWORD w = lo; w < hi; w += 4) {
        if (!SatbLogWord(w)) { k.satbOverflow++; return false; }
        k.satbIcLogWords++;
    }
    return true;
}
static DWORD SatbEs(DWORD arr);  // below (runtime icalls): an array's element size from its class
// sim: one byref/array argument p: 1 logged, 0 cannot be logged (wait), -1 nothing to log (null or private)
static int SatbIcArgLog(const SatbIcArg& a, DWORD p) {
    if (p < 0x10000 || SatbSimPrivate(p)) return -1;  // null (the body throws or skips it) or a local of the sim's own frame
    if (a.kind == 1) return SatbIcRange(p, a.size);
    if (a.kind == 3) {  // byref array: the slot (a new array may land there), then the elements of the array it holds now
        if (!SatbIcRange(p, 4)) return 0;
        if ((p = *(const DWORD*)(uintptr_t)p) < 0x10000 || SatbSimPrivate(p)) return 1;
    }
    const DWORD es = a.size ? a.size : SatbEs(p), len = *(const DWORD*)(uintptr_t)(p + 8);  // MonoArray: max_length +8, data +0x10
    if (!es || (unsigned __int64)len * es > SATB_IC_MAXW * 4) return 0;
    return !len || SatbIcRange(p + 0x10, len * es);
}
static void __cdecl SatbIcSim(DWORD idx, const DWORD* args) {
    if (GetCurrentThreadId() == g_satbWTid || idx >= (DWORD)AS_MAXIC) return;
    Ts3nAutShadow& k = ts3native_autshadow;
    k.satbIcCalls++;
    const int row = g_satbIcRow[idx] - 1;
    if (row < 0) return;
    k.satbIcCallsAll++;
    const SatbIcRow& r = kSatbIcs[row];
    for (int i = 0; i < r.n; i++) {
        const SatbIcArg& a = r.a[i];
        const int got = a.kind == 4 ? 0 : SatbIcArgLog(a, args[a.at]);  // kind 4: layout unknown, the argument cannot be found
        if (got < 0) continue;
        if (!got) { k.satbIcShared++, SatbIcSiteNote(idx, g_satbIcSiteWait, "wait"); SatbSimWait(SATB_ST_IC); return; }
        k.satbIcLogged++, SatbIcSiteNote(idx, g_satbIcSiteLog, "logged");
    }
}
static int SatbMapIcs() {
    int n = 0;
    for (int i = 0; i < AS_MAXIC; i++) {
        g_satbIcRow[i] = 0;
        if (i >= g_asIcN || !g_asName[i]) continue;
        for (int j = 0; j < SATB_NICS; j++)
            if (!strcmp(g_asName[i], kSatbIcs[j].name)) { g_satbIcRow[i] = (short)(j + 1); n++; break; }
    }
    return n;
}

// ---- GC roots: the used entries while a job may read (P2 push hook, world stopped; no allocation, no lock) ----
static void __cdecl SatbPushRoots() {
    const LONG n = g_satbN;
    if (g_satbLive && n > 0 && g_satb) g_mtPushStack((DWORD)(uintptr_t)&g_satb->ent[0], (DWORD)(uintptr_t)&g_satb->ent[n]);
}

// ---- probe timer (S0): the counts at one job length after the span start ----
static HANDLE g_satbTimerEv;
static volatile LONG g_satbTimerUs, g_satbTimerArmed;
static LONG g_satbBase0[6];  // stores, words, pages, loads, unlogged IC / runtime events, IC arguments logged at span start
static void SatbSnapNow(LONG* out) {
    const Ts3nAutShadow& k = ts3native_autshadow;
    out[0] = k.satbStores, out[1] = k.satbWords, out[2] = k.satbPages, out[3] = (LONG)g_satbSimLoadsL.v, out[4] = k.satbIcShared + k.satbRtWaits;
    out[5] = k.satbIcLogged;
}
static void SatbJobSnap() {
    Ts3nAutShadow& k = ts3native_autshadow;
    LONG now[6];
    SatbSnapNow(now);
    k.satbJobSpans++, k.satbJobUs += g_satbTimerUs;
    k.satbJobStores += now[0] - g_satbBase0[0], k.satbJobWords += now[1] - g_satbBase0[1];
    k.satbJobPages += now[2] - g_satbBase0[2], k.satbJobLoads += now[3] - g_satbBase0[3], k.satbJobIc += now[4] - g_satbBase0[4];
    k.satbJobIcLogged += now[5] - g_satbBase0[5];
    SatbMax(k.satbJobMaxWords, now[1] - g_satbBase0[1]);
}
static DWORD WINAPI SatbTimerMain(void*) {
    HANDLE t = CreateWaitableTimerExW(NULL, NULL, 0x2 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, TIMER_ALL_ACCESS);
    if (!t) t = CreateWaitableTimerW(NULL, TRUE, NULL);
    for (;;) {
        WaitForSingleObject(g_satbTimerEv, INFINITE);
        LARGE_INTEGER due;
        due.QuadPart = -(LONGLONG)g_satbTimerUs * 10;
        if (t && SetWaitableTimer(t, &due, 0, NULL, NULL, FALSE)) WaitForSingleObject(t, 100);
        if (InterlockedExchange(&g_satbTimerArmed, 0)) SatbJobSnap();  // else the span ended first: SatbEnd counted it
    }
}

// sim, worker idle: a span starts (act: right before the submit; probe: at the Simulate entry after a job, for jobUs)
static void SatbBegin(DWORD workerTid, LONG jobUs) {
    if (g_satbMode <= SATB_OFF || g_satbOn) return;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    SatbReset();
    SatbHookGrow();
    g_satbWTid = workerTid;  // probe too: the worker's own stores and loads are never counted as the sim's
    g_satbWTidL.v = workerTid;
    g_satbGcNo0 = *g_satbGcNo;
    g_satbWStaticAt = 0;  // the worker's static copies (SatbWStaticView)
    SatbLogTicks();  // docs/206 §15: the tick statics at the cut
    g_satbWArenaN = 0;
    g_satbLive = 1;
    if (g_satbMode == SATB_ACT) g_mtNoSharedFallback = 1, g_mtFallbackExc = g_asFmtExc;  // X03: no F1 fallback onto the sim's chain
    SatbSwap(true);
    QueryPerformanceCounter(&g_satbT0);
    Ts3nAutShadow& k = ts3native_autshadow;
    k.satbSpans++;
    SatbMax(k.satbSwapUs, SatbUs(t0));
    if (g_satbMode == SATB_PROBE && g_satbTimerEv && jobUs > 0) {
        SatbSnapNow(g_satbBase0);
        g_satbTimerUs = jobUs;
        InterlockedExchange(&g_satbTimerArmed, 1);
        SetEvent(g_satbTimerEv);
    }
}
// any thread: stubs out (act: the worker at its job end; probe: the sim at the end of the tick). The log stays readable
// until the next SatbBegin; the roots stop with g_satbLive (SatbEnd).
static void SatbOff() {
    if (g_satbOn) SatbSwap(false);
}
// sim, worker idle: the span's accounting
static void SatbEnd() {
    if (g_satbMode <= SATB_OFF || !g_satbLive) return;
    SatbOff();
    g_satbLive = 0;
    g_mtNoSharedFallback = 0;
    if (InterlockedExchange(&g_satbTimerArmed, 0)) SatbJobSnap();  // the tick ended within one job length
    Ts3nAutShadow& k = ts3native_autshadow;
    k.satbLoads = (LONG)g_satbSimLoadsL.v;
    k.satbSpanUs += SatbUs(g_satbT0);
    SatbMax(k.satbMaxWords, g_satbJobWords);
    SatbMax(k.satbMaxPages, g_satbPageN);
    if (g_satbStale) k.satbStale++;
    SatbGiveOpLog();
    SatbColdNameLog();
    SatbBodyLog();
}

// ---- decode (docs/data/satb-handlers.csv, every row from the disassembly). The stubs pass their pushad frame:
// r[0] edi = mint ip (u16 words), r[1] esi = eval stack pointer (8-byte slots, S1 = [esi-8]), r[2] ebp of
// ves_exec_method_with_context, r[5] edx = opcode. Frame: [ebp-4] ip mirror (every handler stores the new ip there),
// [ebp-0xC] vt stack pointer, [ebp-0x10] RuntimeMethod (+0x14 data items), [ebp+0xC] ThreadContext (+0 domain). ----
enum { SR_EDI = 0, SR_ESI = 1, SR_EBP = 2, SR_EDX = 5 };
static __forceinline DWORD SatbS(const DWORD* r, int k) { return *(const DWORD*)(uintptr_t)(r[SR_ESI] - 8 * k); }  // S_k low dword
static __forceinline DWORD SatbW1(const DWORD* r) { return *(const WORD*)(uintptr_t)(r[SR_EDI] + 2); }
static __forceinline DWORD SatbW2(const DWORD* r) { return *(const WORD*)(uintptr_t)(r[SR_EDI] + 4); }
static __forceinline DWORD SatbD1(const DWORD* r) { return *(const DWORD*)(uintptr_t)(r[SR_EDI] + 4); }
static __forceinline const u8* SatbItem(const DWORD* r, DWORD i) {
    const DWORD* items = *(const DWORD* const*)(uintptr_t)(*(const DWORD*)(uintptr_t)(r[SR_EBP] - 0x10) + 0x14);
    return (const u8*)(uintptr_t)items[i];
}
static __forceinline DWORD SatbDomain(const DWORD* r) { return **(const DWORD* const*)(uintptr_t)(r[SR_EBP] + 0xC); }
static __forceinline DWORD& SatbVtsp(const DWORD* r) { return *(DWORD*)(uintptr_t)(r[SR_EBP] - 0xC); }
static __forceinline DWORD SatbAlign8(DWORD n) { return (n + 7) & ~7u; }
static void SatbAdvance(DWORD* r, DWORD ipBytes, int spBytes) {
    r[SR_EDI] += ipBytes, r[SR_ESI] += spBytes;
    *(DWORD*)(uintptr_t)(r[SR_EBP] - 4) = r[SR_EDI];
}

// a MonoType's value size (stackval_to_data / from_data cases: type byte +6, byref bit 30 of +4), 0 = unknown
static DWORD SatbTypeSize(const u8* t) {
    if (!t) return 0;
    if (*(const DWORD*)(t + 4) & 0x40000000) return 4;
    switch (t[6]) {
    case 0x02: case 0x04: case 0x05: return 1;
    case 0x03: case 0x06: case 0x07: return 2;
    case 0x08: case 0x09: case 0x0C: case 0x0E: case 0x0F: case 0x10: case 0x12: case 0x14: case 0x18: case 0x19: case 0x1B: case 0x1C: case 0x1D: return 4;
    case 0x0A: case 0x0B: case 0x0D: return 8;
    case 0x11: {  // value type (enums too): mono_class_value_size 0xE6F5A0 = instance size - 4 once the layout is set (+0x14 & 4)
        const u8* k = *(const u8* const*)t;
        return k && (k[0x14] & 4) ? *(const DWORD*)(k + 0x54) - 4 : 0;
    }
    default: return 0;  // generic instance, typedbyref, type variables: not decoded
    }
}
static DWORD SatbValueSize(const u8* klass) { return klass && (klass[0x14] & 4) ? *(const DWORD*)(klass + 0x54) - 4 : 0; }
// mono_class_vtable 0xEA2AD0 fast path [verified]: runtime info [klass+0xB8], u16 max domain at +0, vtables from +4; 0 = miss
static DWORD SatbVtable(DWORD domain, const u8* klass) {
    const u8* info = klass ? *(const u8* const*)(klass + 0xB8) : NULL;
    const DWORD id = *(const DWORD*)(uintptr_t)(domain + 0x58);
    if (!info || (int)*(const WORD*)info < (int)id) return 0;
    return *(const DWORD*)(info + id * 4 + 4);
}
typedef DWORD(__cdecl* SatbHashLookupFn)(DWORD hash, const void* key);
static SatbHashLookupFn g_satbHashLookup = (SatbHashLookupFn)(uintptr_t)0x00E45F00;  // g_hash_table_lookup
// a static field's address: vtable initialised (+0x18 & 2), not a thread/context static (domain +0x88 hash); 0 = none
static DWORD SatbStaticAddr(const DWORD* r, const u8* field, DWORD vt, bool* threadStatic) {
    *threadStatic = false;
    if (!vt || !(*(const u8*)(uintptr_t)(vt + 0x18) & 2)) return 0;
    const DWORD h = *(const DWORD*)(uintptr_t)(SatbDomain(r) + 0x88);
    if (h && g_satbHashLookup(h, field)) { *threadStatic = true; return 0; }
    return *(const DWORD*)(uintptr_t)(vt + 0xC) + *(const DWORD*)(field + 8);
}

// the heap range one store opcode writes. 1 = range set; 0 = nothing to log (null object / index out of range: the game's
// handler throws before writing; a thread static); -1 = unknown (act: wait for the worker first). ipB/spB/vtB: the
// instruction's ip advance, stack change and vt stack pop (a worker store that must be skipped).
static int SatbStoreRange(const DWORD* r, DWORD op, DWORD* addr, DWORD* n, DWORD* ipB, int* spB, DWORD* vtB) {
    static const u8 kElem[8] = {4, 1, 2, 4, 8, 4, 8, 4};  // stelem.i i1 i2 i4 i8 r4 r8 ref
    *vtB = 0;
    switch (op) {
    case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F: case 0x60: case 0x61: case 0x62: case 0x63: {
        static const u8 kSz[10] = {1, 1, 2, 2, 4, 8, 4, 8, 4, 4};
        const DWORD obj = SatbS(r, 2);
        *ipB = op == 0x63 ? 8 : 4, *spB = -16;
        if (op == 0x63) *n = SatbD1(r), *vtB = SatbAlign8(*n); else *n = kSz[op - 0x59];
        if (!obj) return 0;
        *addr = obj + SatbW1(r);
        return 1;
    }
    case 0x64: case 0x65: {  // strmfld: field = items[w1] (+0 type, +8 offset); a transparent proxy never exists in this build
        const DWORD obj = SatbS(r, 2);
        const u8* f = SatbItem(r, SatbW1(r));
        *ipB = op == 0x65 ? 8 : 4, *spB = -16;
        *n = op == 0x65 ? SatbD1(r) : SatbTypeSize(*(const u8* const*)f);
        if (op == 0x65) *vtB = SatbAlign8(*n);
        if (!obj) return 0;
        if (!*n || **(const DWORD* const*)(uintptr_t)obj == *(const DWORD*)(uintptr_t)0x01220EC0) return -1;
        *addr = obj + *(const DWORD*)(f + 8);
        return 1;
    }
    case 0x6A: case 0x6B: {  // stsfld: the vtable lookup runs every time; a miss or an uninitialised class -> unknown
        const u8* f = SatbItem(r, SatbW1(r));
        *ipB = op == 0x6B ? 8 : 4, *spB = -8;
        if (op == 0x6B) *vtB = SatbAlign8(SatbD1(r));
        bool ts;
        *addr = SatbStaticAddr(r, f, SatbVtable(SatbDomain(r), *(const u8* const*)(f + 0x14)), &ts);
        if (ts) return 0;
        *n = SatbTypeSize(*(const u8* const*)f);
        return *addr && *n ? 1 : -1;
    }
    case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97: case 0x98: {
        static const u8 kSz[8] = {1, 2, 4, 8, 4, 4, 8, 4};
        *ipB = 2, *spB = -16, *n = kSz[op - 0x91], *addr = SatbS(r, 2);
        return 1;
    }
    case 0xF5: *ipB = 6, *spB = -8, *addr = SatbS(r, 1), *n = *(const DWORD*)(uintptr_t)(r[SR_EDI] + 2); return 1;  // initobj
    case 0xFF: {  // stobj (non-VT classes; VT uses stobj.vt)
        const u8* k = SatbItem(r, SatbW1(r));
        *ipB = 4, *spB = -16, *addr = SatbS(r, 2), *n = SatbTypeSize(k + 0xA0);
        return *n ? 1 : -1;
    }
    case 0x100: {
        const u8* k = SatbItem(r, SatbW1(r));
        *ipB = 4, *spB = -16, *addr = SatbS(r, 2), *n = SatbValueSize(k), *vtB = SatbAlign8(*n);
        return *n ? 1 : -1;
    }
    case 0x101: case 0x102: *ipB = 2, *spB = -24, *addr = SatbS(r, 3), *n = SatbS(r, 1); return 1;  // cpblk, initblk
    case 0x112: {  // stelem.any.vt
        const DWORD arr = SatbS(r, 3), idx = SatbS(r, 2), d1 = SatbD1(r);
        *ipB = 8, *spB = -24, *n = d1, *vtB = SatbAlign8(d1);
        if (!arr || idx >= *(const DWORD*)(uintptr_t)(arr + 8)) return 0;
        *addr = arr + 0x10 + idx * d1;
        return 1;
    }
    case 0x113: case 0x114: case 0x115: case 0x116: case 0x117: case 0x118: case 0x119: case 0x11A: {
        const DWORD arr = SatbS(r, 3), idx = SatbS(r, 2);
        *ipB = 2, *spB = -24, *n = kElem[op - 0x113];
        if (!arr || idx >= *(const DWORD*)(uintptr_t)(arr + 8)) return 0;
        *addr = arr + 0x10 + idx * *n;
        return 1;
    }
    }
    return -1;
}

static void __cdecl SatbSimStore(DWORD* r) {
    Ts3nAutShadow& k = ts3native_autshadow;
    k.satbStores++;
    DWORD addr = 0, n = 0, ipB, vtB;
    int spB;
    const int how = SatbStoreRange(r, r[SR_EDX] & 0xFFFF, &addr, &n, &ipB, &spB, &vtB);
    if (how > 0) SatbSimRange(addr, n);
    else if (how < 0) SatbSimWait(SATB_ST_CLASS);
}

// ---- worker side (act) ----
// give-ups by op (docs/206 §12: satb-act's last-op counter hid the mix): the worker counts, the sim logs at span end
static volatile LONG g_satbGiveOp[16], g_satbGiveN[16], g_satbGiveLogged[16];
static void SatbGiveOpNote(DWORD op) {
    for (int i = 0; i < 16; i++) {
        if (!g_satbGiveOp[i]) InterlockedCompareExchange(&g_satbGiveOp[i], (LONG)op, 0);
        if (g_satbGiveOp[i] == (LONG)op) { InterlockedIncrement(&g_satbGiveN[i]); return; }
    }
}
static void SatbGiveOpLog() {  // sim, worker idle: 'autshadow satb give-up: op 0x... xN' at 1, 10, 100, ...
    for (int i = 0; i < 16 && g_satbGiveOp[i]; i++)
        for (LONG n = g_satbGiveN[i], p = 1; p <= n && p <= 100000000; p *= 10)
            if (p > g_satbGiveLogged[i]) { g_satbGiveLogged[i] = p; Log("autshadow satb give-up: op 0x%lX x%ld", g_satbGiveOp[i], n); }
}
static void SatbWGiveUp(DWORD op) {  // this object's reads are no longer the cut's: abort it and stop the job (KeepPartial)
    Ts3nAutShadow& k = ts3native_autshadow;
    k.satbWGiveUp++, k.satbWGiveUpOp = (LONG)op;
    SatbGiveOpNote(op);
    InterlockedOr(&g_satbStale, SATB_ST_GIVEUP);
    InterlockedExchange(&g_asStop, 1);
    AsAbort(AS_AB_STOP, (DWORD)-1);
}
// arena chunks the worker took during this job (filled by SatbGrowWrap; the worker is the only writer)
static const LONG SATB_WARENA = 256;  // distinct chunks a job can take (0.36.140: deduplicated; was 64 grows)
static DWORD g_satbWArena[SATB_WARENA][2];
static bool SatbWInArena(DWORD a) {
    for (LONG i = 0; i < g_satbWArenaN; i++) if (a >= g_satbWArena[i][0] && a < g_satbWArena[i][1]) return true;
    return false;
}
// load, then check (docs/206 §3.3): the live bytes first, then every logged word of the range takes its cut value. A range
// inside an arena chunk the worker took in this job is its own frame: live is right there (the log may hold the sim's
// earlier use of the chunk).
static void SatbWRead(DWORD addr, DWORD n, u8* out) {
    memcpy(out, (const void*)(uintptr_t)addr, n);
    _ReadWriteBarrier();
    Ts3nAutShadow& k = ts3native_autshadow;
    k.satbWLoads++;
    bool any = false;
    for (DWORD p = addr >> 12; p <= (addr + n - 1) >> 12 && !any; p++) any = g_satb->page[p] == (u8)g_satbE8;
    if (!any) return;
    if (g_satbWArenaN && SatbWInArena(addr)) { k.satbWArenaIgn++; return; }
    if (SatbMerge(addr, n, out)) k.satbWHits++;
}
static __declspec(naked) void SatbFldF(const void* /*src*/, void* /*dst*/) {  // fld dword / fstp qword, as the handlers do
    __asm {
        mov eax, [esp + 4]
        mov ecx, [esp + 8]
        fld dword ptr [eax]
        fstp qword ptr [ecx]
        ret
    }
}
static __declspec(naked) void SatbFldD(const void* /*src*/, void* /*dst*/) {  // fld qword / fstp qword (x87 copy)
    __asm {
        mov eax, [esp + 4]
        mov ecx, [esp + 8]
        fld qword ptr [eax]
        fstp qword ptr [ecx]
        ret
    }
}
// write a loaded value into a stack slot the way the handler does. kind: 'b' movsx byte, 'B' movzx byte, 'w', 'W', 'd' low
// dword only, 'q' both dwords, 'f' float -> double (x87), 'D' double x87 copy
static void SatbPut(DWORD slot, const u8* b, char kind) {
    DWORD* s = (DWORD*)(uintptr_t)slot;
    switch (kind) {
    case 'b': s[0] = (DWORD)(int)(signed char)b[0]; break;
    case 'B': s[0] = b[0]; break;
    case 'w': s[0] = (DWORD)(int)*(const short*)b; break;
    case 'W': s[0] = *(const WORD*)b; break;
    case 'd': s[0] = *(const DWORD*)b; break;
    case 'q': s[0] = *(const DWORD*)b, s[1] = *(const DWORD*)(b + 4); break;
    case 'f': SatbFldF(b, s); break;
    case 'D': SatbFldD(b, s); break;
    }
}
typedef void(__cdecl* SatbFromDataFn)(const void* type, void* result, const void* data, int pinvoke);
static SatbFromDataFn g_satbFromData = (SatbFromDataFn)(uintptr_t)0x00E521A0;  // stackval_from_data [verified: 4 args, cdecl]
typedef int(__cdecl* SatbIsVtFn)(const void* klass);
static SatbIsVtFn g_satbIsVt = (SatbIsVtFn)(uintptr_t)0x00E52160;              // ldobj's value-type test (reads only)

// The callvirt inline cache (data item pair: class, target) is written with two plain stores (0xE54B1D, 0xE54B22): a
// thread reading it while another writes can pair the new class with the old target [verified, docs/data/satb-handlers.csv].
// The worker therefore never reads or writes it: its callvirt family runs the tail of the handler from 0xE54ABF here and
// looks the target up itself (0xE51FC0: ecx = receiver, ebx = RuntimeMethod, eax = target [verified]), then continues at
// 0xE54B28 (eax = receiver) as a hit would. 3 = continue at 0xE54B28, 4 = null receiver (0xE593DF, as the handler).
static DWORD g_satbVLookup = 0x00E51FC0;
static __declspec(naked) DWORD __cdecl SatbVLookup(DWORD /*recv*/, DWORD /*rm*/) {
    __asm {
        push ebx
        mov ecx, [esp + 8]
        mov ebx, [esp + 12]
        call dword ptr [g_satbVLookup]
        pop ebx
        ret
    }
}
enum { SR_EAX = 7 };
static int SatbWCallvirtTail(DWORD* r, const u8* rm) {
    const DWORD ebp = r[SR_EBP];
    DWORD esi = r[SR_ESI];
    *(DWORD*)(uintptr_t)esi = SatbVtsp(r);
    *(DWORD*)(uintptr_t)(ebp - 0x54) = esi;
    esi -= rm[0x40] * 8;
    *(DWORD*)(uintptr_t)(ebp - 0x48) = esi;
    const DWORD recv = *(const DWORD*)(uintptr_t)(esi - 8);
    esi -= 8;
    *(DWORD*)(uintptr_t)(ebp - 0x28) = recv, *(DWORD*)(uintptr_t)(ebp - 0x50) = recv;
    r[SR_ESI] = esi, r[SR_EAX] = recv;
    if (!recv || !*(const DWORD*)(uintptr_t)recv) return 4;
    *(DWORD*)(uintptr_t)(ebp - 0x58) = SatbVLookup(recv, (DWORD)(uintptr_t)rm);
    return 3;
}

// docs/127 H11 shelter answer (worker, act only): ts3fixes arms one callvirt (AutShadowNative.RainArm: receiver = the pair's
// definition, site = the interface method InteractionDefinition.CreateInstance(IGameObject, IActor, InteractionPriority,
// bool, bool)) right before it scores a rain/hail outdoor pair. The matching callvirt in Autonomy.CalculateScoreForObjectInteraction
// is not made: receiver and arguments are popped and a null reference is the result, as if the callee had returned null
// (the callee writes its result into the receiver's slot, sp = that slot + 8 [inferred: mint 1.2 call convention, as
// SatbWCallvirtTail]; a valuetype argument's vt stack goes with the VTRESULT after the call). So no InteractionInstance,
// no InteractionInstance.sNextId++ on the worker; ts3fixes' IsShelteredFromPrecipitation hook answers from the cut's bit.
static DWORD g_asRainDef, g_asRainMm;  // worker only: armed receiver and site MonoMethod (0 = disarmed)
// per-search feature mask, native bits (ts3fixes AutLiveCore.Feat maps them): cut work and job deltas (autshadow-run.cpp)
enum { AS_FEAT_PRELEARN = 1, AS_FEAT_TIERREQ = 2, AS_FEAT_SATB = 4, AS_FEAT_COLD = 8 };
static LONG g_asJobBits, g_asLastJobBits, g_asJobLoads0, g_asJobCold0, g_asMarkReq, g_asMarkMask;
static char g_asMarkDef[64];
static LONG g_asRainSkips;
static int SatbRainSkip(DWORD* r, const u8* rm) {
    const DWORD at = r[SR_ESI] - rm[0x40] * 8 - 8;  // receiver slot = the result slot
    *(DWORD*)(uintptr_t)at = 0, *(DWORD*)(uintptr_t)(at + 4) = 0;
    r[SR_ESI] = at;
    SatbAdvance(r, 8, 8);  // callvirt is 4 words (op, cache, -, method item)
    g_asRainSkips++;  // stays armed: every such call in the pair is skipped and counted (ts3fixes accepts exactly one)
    ts3native_autshadow.rainSkip++;
    return 1;
}

// 1 = emulated (r[] advanced, continue at the dispatch head); 3 / 4 = callvirt family after its load, without the inline
// cache (continue at 0xE54B28 / the null path 0xE593DF); 0 = the game's handler (an exception path, a thread static,
// probe mode, or a give-up)
static int __cdecl SatbWorkerLoad(DWORD* r) {
    if (g_satbMode != SATB_ACT) return 0;
    const DWORD op = r[SR_EDX] & 0xFFFF;
    u8 b[16];
    switch (op) {
    case 0x3E: case 0x3F: case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47: {  // ldfld.*
        static const u8 kSz[10] = {1, 1, 2, 2, 4, 8, 4, 8, 4, 4};
        static const char kK[11] = "bBwWdqfDdd";
        const DWORD obj = SatbS(r, 1);
        if (!obj) return 0;
        SatbWRead(obj + SatbW1(r), kSz[op - 0x3E], b);
        SatbPut(r[SR_ESI] - 8, b, kK[op - 0x3E]);
        SatbAdvance(r, 4, 0);
        return 1;
    }
    case 0x48: case 0x4A: {  // ldfld.vt / ldrmfld.vt: S1 = vtsp, copy, vtsp += align8(d1)
        const DWORD obj = SatbS(r, 1), d1 = SatbD1(r);
        if (!obj) return 0;
        DWORD off = SatbW1(r);
        if (op == 0x4A) {
            if (**(const DWORD* const*)(uintptr_t)obj == *(const DWORD*)(uintptr_t)0x01220EC0) { SatbWGiveUp(op); return 0; }
            off = *(const DWORD*)(SatbItem(r, off) + 8);  // the stray first copy of ldrmfld.vt (docs/data/satb-handlers.csv) is skipped
        }
        DWORD& vtsp = SatbVtsp(r);
        const DWORD dst = vtsp;
        *(DWORD*)(uintptr_t)(r[SR_ESI] - 8) = dst;
        SatbWRead(obj + off, d1, (u8*)(uintptr_t)dst);
        vtsp = dst + SatbAlign8(d1);
        SatbAdvance(r, 8, 0);
        return 1;
    }
    case 0x49: {  // ldrmfld: stackval_from_data(field type, S1, address)
        const DWORD obj = SatbS(r, 1);
        if (!obj) return 0;
        if (**(const DWORD* const*)(uintptr_t)obj == *(const DWORD*)(uintptr_t)0x01220EC0) { SatbWGiveUp(op); return 0; }
        const u8* f = SatbItem(r, SatbW1(r));
        const u8* t = *(const u8* const*)f;
        const DWORD n = SatbTypeSize(t);
        if (!n || n > 8) { SatbWGiveUp(op); return 0; }
        SatbWRead(obj + *(const DWORD*)(f + 8), n, b);
        SatbAdvance(r, 4, 0);
        g_satbFromData(t, (void*)(uintptr_t)(r[SR_ESI] - 8), b, 0);
        return 1;
    }
    case 0x66: case 0x67: case 0x68: {  // ldsfld (from_data), ldsfld.i4 / .o (low dword, no special-static lookup); vt = items[w2]
        const u8* f = SatbItem(r, SatbW1(r));
        const DWORD vt = (DWORD)(uintptr_t)SatbItem(r, SatbW2(r));
        if (!(*(const u8*)(uintptr_t)(vt + 0x18) & 2)) { SatbWGiveUp(op); return 0; }  // a class constructor on the worker
        DWORD addr;
        if (op == 0x66) {
            bool ts;
            addr = SatbStaticAddr(r, f, vt, &ts);
            if (ts) return 0;  // thread/context static: the worker's own copy, live is right
        } else addr = *(const DWORD*)(uintptr_t)(vt + 0xC) + *(const DWORD*)(f + 8);
        const DWORD slot = r[SR_ESI];
        if (op == 0x66) {
            const u8* t = *(const u8* const*)f;
            const DWORD n = SatbTypeSize(t);
            if (!n || n > 8) { SatbWGiveUp(op); return 0; }
            SatbWRead(addr, n, b);
            SatbAdvance(r, 6, 8);
            g_satbFromData(t, (void*)(uintptr_t)slot, b, 0);
        } else {
            SatbWRead(addr, 4, b);
            SatbPut(slot, b, 'd');
            SatbAdvance(r, 6, 8);
        }
        return 1;
    }
    case 0x69: {  // ldsfld.vt: vtable lookup every time; [esi] = vtsp, vtsp += align8(d1), from_data copies the value there
        const u8* f = SatbItem(r, SatbW1(r));
        bool ts;
        const DWORD addr = SatbStaticAddr(r, f, SatbVtable(SatbDomain(r), *(const u8* const*)(f + 0x14)), &ts);
        if (ts) return 0;
        const u8* t = *(const u8* const*)f;
        const DWORD n = SatbTypeSize(t);
        u8 big[256];
        if (!addr || !n || n > sizeof(big)) { SatbWGiveUp(op); return 0; }
        SatbWRead(addr, n, big);
        DWORD& vtsp = SatbVtsp(r);
        const DWORD slot = r[SR_ESI];
        *(DWORD*)(uintptr_t)slot = vtsp;
        vtsp += SatbAlign8(SatbD1(r));
        SatbAdvance(r, 8, 8);
        g_satbFromData(t, (void*)(uintptr_t)slot, big, 0);
        return 1;
    }
    case 0x86: case 0x87: case 0x88: case 0x89: case 0x8A: case 0x8B: case 0x8C: case 0x8D: case 0x8E: case 0x8F: case 0x90: {  // ldind.*
        static const u8 kSz[11] = {1, 1, 2, 2, 4, 4, 8, 4, 4, 8, 4};
        static const char kK[12] = "bBwWddqdfDd";
        const DWORD addr = SatbS(r, 1);
        if (addr < 0x10000) return 0;  // the game's handler faults the same way
        SatbWRead(addr, kSz[op - 0x86], b);
        SatbPut(r[SR_ESI] - 8, b, kK[op - 0x86]);
        SatbAdvance(r, 2, 0);
        return 1;
    }
    case 0xFE: {  // ldobj
        const DWORD addr = SatbS(r, 1);
        const u8* k = SatbItem(r, SatbW1(r));
        if (addr < 0x10000) return 0;
        if (g_satbIsVt(k)) {  // value type: S1 = vtsp, copy value size, vtsp += align8(size)
            const DWORD n = SatbValueSize(k);
            if (!n) { SatbWGiveUp(op); return 0; }
            DWORD& vtsp = SatbVtsp(r);
            const DWORD dst = vtsp;
            *(DWORD*)(uintptr_t)(r[SR_ESI] - 8) = dst;
            SatbWRead(addr, n, (u8*)(uintptr_t)dst);
            vtsp = dst + SatbAlign8(n);
            SatbAdvance(r, 4, 0);
            return 1;
        }
        const DWORD n = SatbTypeSize(k + 0xA0);
        if (!n || n > 8) { SatbWGiveUp(op); return 0; }
        SatbWRead(addr, n, b);
        SatbAdvance(r, 4, 0);
        g_satbFromData(k + 0xA0, (void*)(uintptr_t)(r[SR_ESI] - 8), b, 0);
        return 1;
    }
    case 0x105: case 0x106: case 0x107: case 0x108: case 0x109: case 0x10A: case 0x10B: case 0x10C: case 0x10D: case 0x10E: case 0x10F: {
        static const u8 kSz[11] = {4, 1, 1, 2, 2, 4, 4, 8, 4, 8, 4};
        static const char kK[12] = "dbBwWddqfDd";
        const DWORD arr = SatbS(r, 2), idx = SatbS(r, 1);
        if (!arr || idx >= *(const DWORD*)(uintptr_t)(arr + 8)) return 0;
        const DWORD sz = kSz[op - 0x105];
        SatbWRead(arr + 0x10 + idx * sz, sz, b);
        SatbPut(r[SR_ESI] - 16, b, kK[op - 0x105]);
        SatbAdvance(r, 2, -8);
        return 1;
    }
    case 0x111: {  // ldelem.any.vt: old S2 = vtsp, vtsp += align8(d1), copy d1 bytes
        const DWORD arr = SatbS(r, 2), idx = SatbS(r, 1), d1 = SatbD1(r);
        if (!arr || idx >= *(const DWORD*)(uintptr_t)(arr + 8)) return 0;
        DWORD& vtsp = SatbVtsp(r);
        const DWORD dst = vtsp;
        *(DWORD*)(uintptr_t)(r[SR_ESI] - 16) = dst;
        vtsp = dst + SatbAlign8(d1);
        SatbWRead(arr + 0x10 + idx * d1, d1, (u8*)(uintptr_t)dst);
        SatbAdvance(r, 8, -8);
        return 1;
    }
    case 0x196: {  // getchr: chars are mutable in place (StringBuilder buffers)
        const DWORD str = SatbS(r, 2), idx = SatbS(r, 1);
        if (!str || (int)idx < 0 || (int)idx >= *(const int*)(uintptr_t)(str + 4)) return 0;
        SatbWRead(str + 8 + 2 * idx, 2, b);
        SatbPut(r[SR_ESI] - 16, b, 'W');
        SatbAdvance(r, 2, -8);
        return 1;
    }
    case 0x197: {  // strlen
        const DWORD str = SatbS(r, 1);
        if (!str) return 0;
        SatbWRead(str + 4, 4, b);
        SatbPut(r[SR_ESI] - 8, b, 'd');
        SatbAdvance(r, 2, 0);
        return 1;
    }
    case 0xED: case 0xEE: {  // con.callvirt / con.boxcallvirt: the hidden load through the `this` address, then the callvirt
        // join 0xE54ABF (the rest reloads from the frame: [ebp-0x58] method, [ebp-0x24] cache pair, [ebp-4] ip) [verified]
        const DWORD ip0 = r[SR_EDI], ebp = r[SR_EBP];
        const DWORD cache = (DWORD)(uintptr_t)&((const DWORD*)(uintptr_t)*(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(ebp - 0x10) + 0x14))[SatbW1(r)];
        const u8* klass = SatbItem(r, *(const WORD*)(uintptr_t)(ip0 + 6));
        const u8* rm = SatbItem(r, *(const WORD*)(uintptr_t)(ip0 + 8));
        const DWORD slot = r[SR_ESI] - (rm[0x40] * 8 + 8), addr = *(const DWORD*)(uintptr_t)slot;
        const bool vt = op == 0xEE || g_satbIsVt(klass);
        const DWORD n = vt ? SatbValueSize(klass) : SatbTypeSize(klass + 0xA0);
        u8 big[256];
        if (addr < 0x10000 || !n || n > (vt ? sizeof(big) : 8)) { SatbWGiveUp(op); return 0; }
        *(DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(ebp - 8) + 0x1C) = ip0;
        *(DWORD*)(uintptr_t)(ebp - 0x24) = cache, *(DWORD*)(uintptr_t)(ebp - 0x58) = (DWORD)(uintptr_t)rm;
        *(DWORD*)(uintptr_t)(ebp - 0x18) = addr;
        r[SR_EDI] = ip0 + 10;
        *(DWORD*)(uintptr_t)(ebp - 4) = r[SR_EDI];
        if (op == 0xEE) {  // mono_value_box 0xEA3140(domain, klass, data) copies the value into a new box
            SatbWRead(addr, n, big);
            *(DWORD*)(uintptr_t)slot = ((DWORD(__cdecl*)(DWORD, const void*, const void*))(uintptr_t)0x00EA3140)(SatbDomain(r), klass, big);
        } else if (vt) {
            DWORD& vtsp = SatbVtsp(r);
            const DWORD dst = vtsp;
            *(DWORD*)(uintptr_t)(ebp - 0x1C) = n;
            *(DWORD*)(uintptr_t)slot = dst;
            SatbWRead(addr, n, (u8*)(uintptr_t)dst);
            vtsp = dst + SatbAlign8(n);
        } else {
            SatbWRead(addr, n, b);
            g_satbFromData(klass + 0xA0, (void*)(uintptr_t)slot, b, 0);
        }
        return SatbWCallvirtTail(r, rm);
    }
    case 0xEC: {  // callvirt: the prefix of 0xE549EB, then the tail without the inline cache
        const DWORD ip0 = r[SR_EDI], ebp = r[SR_EBP];
        const u8* rm = SatbItem(r, *(const WORD*)(uintptr_t)(ip0 + 6));
        const DWORD recv = *(const DWORD*)(uintptr_t)(r[SR_ESI] - rm[0x40] * 8 - 8);
        // H11: by site, any receiver: while a pair is armed no CreateInstance(5) call runs on the worker (a skip that was not
        // the branch's own leaves the hook unanswered: ts3fixes aborts the pair)
        if (g_asRainDef && *(const DWORD*)(rm + 8) == g_asRainMm) return SatbRainSkip(r, rm);
        if (!recv || !*(const DWORD*)(uintptr_t)recv) return 0;  // the handler's null path comes before its cache
        *(DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(ebp - 8) + 0x1C) = ip0;
        *(DWORD*)(uintptr_t)(ebp - 0x24) = (DWORD)(uintptr_t)&((const DWORD*)(uintptr_t)*(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(ebp - 0x10) + 0x14))[SatbW1(r)];
        *(DWORD*)(uintptr_t)(ebp - 0x58) = (DWORD)(uintptr_t)rm;
        r[SR_EDI] = ip0 + 8;
        *(DWORD*)(uintptr_t)(ebp - 4) = r[SR_EDI];
        return SatbWCallvirtTail(r, rm);
    }
    }
    return 0;
}

// worker store (act). The worker's view of memory is the cut plus its own writes; the sim's view must never see a worker
// write to a word the sim changed in this job. Emulated kinds (scalar stfld, stind, stelem but ref, stsfld of a scalar):
// per word, a word the sim logged takes the worker's bytes in its log entry (the worker reads them back from there; live
// memory keeps the sim's value), other words are written live, and a word the sim logged meanwhile gets the worker's
// bytes in its entry too (write, then re-check: closes the race with a concurrent first write). Other kinds with a logged
// word in their range: skipped, the object aborted and the job stopped (KeepPartial). Words in an arena chunk the worker
// took in this job are its own frame: always live.
static __declspec(naked) void SatbFstpF(const void* /*src*/, void* /*dst*/) {  // fld qword / fstp dword: double -> float as the handlers
    __asm {
        mov eax, [esp + 4]
        mov ecx, [esp + 8]
        fld qword ptr [eax]
        fstp dword ptr [ecx]
        ret
    }
}
// the bytes a store opcode writes, converted as its handler does (value = S1); 0 = not emulated here
static DWORD SatbStoreBytes(const DWORD* r, DWORD op, u8* out) {
    const u8* s1 = (const u8*)(uintptr_t)(r[SR_ESI] - 8);
    char kind = 0;
    switch (op) {
    case 0x59: case 0x5A: case 0x91: case 0x114: kind = '1'; break;
    case 0x5B: case 0x5C: case 0x92: case 0x115: kind = '2'; break;
    case 0x5D: case 0x61: case 0x62: case 0x93: case 0x95: case 0x98: case 0x113: case 0x116: kind = '4'; break;
    case 0x5E: case 0x94: case 0x117: kind = '8'; break;
    case 0x5F: case 0x96: case 0x118: kind = 'f'; break;
    case 0x60: case 0x97: case 0x119: kind = 'D'; break;
    case 0x6A: {  // stsfld through stackval_to_data: integer and reference types only (its float conversion is not read here)
        const u8* t = *(const u8* const*)SatbItem(r, SatbW1(r));
        if (*(const DWORD*)(t + 4) & 0x40000000) { kind = '4'; break; }
        switch (t[6]) {
        case 0x02: case 0x04: case 0x05: kind = '1'; break;
        case 0x03: case 0x06: case 0x07: kind = '2'; break;
        case 0x08: case 0x09: case 0x0E: case 0x0F: case 0x12: case 0x14: case 0x18: case 0x19: case 0x1B: case 0x1C: case 0x1D: kind = '4'; break;
        case 0x0A: case 0x0B: kind = '8'; break;
        }
        break;
    }
    }
    switch (kind) {
    case '1': out[0] = s1[0]; return 1;
    case '2': memcpy(out, s1, 2); return 2;
    case '4': memcpy(out, s1, 4); return 4;
    case '8': memcpy(out, s1, 8); return 8;
    case 'f': SatbFstpF(s1, out); return 4;
    case 'D': SatbFldD(s1, out); return 8;
    }
    return 0;
}
// worker: the entry of word w in this job's log, NULL = none
static SatbEnt* SatbEntOf(DWORD w) {
    SatbMem& m = *g_satb;
    if (m.page[w >> 12] != (u8)g_satbE8) return NULL;
    const DWORD tag = g_satbE16 << 16;
    for (DWORD h = SatbHash(w);; h = (h + 1) & SATB_HMASK) {
        const DWORD v = m.idx[h];
        if ((v & 0xFFFF0000u) != tag) return NULL;
        SatbEnt& e = m.ent[(v & 0xFFFF) - 1];
        if (e.addr == w) return &e;
    }
}
static void SatbEntPut(SatbEnt* e, DWORD w, DWORD addr, DWORD n, const u8* bytes) {  // the worker's bytes into an entry
    DWORD v = e->old;
    for (DWORD b = 0; b < 4; b++)
        if (w + b >= addr && w + b < addr + n) v = (v & ~(0xFFu << (8 * b))) | ((DWORD)bytes[w + b - addr] << (8 * b));
    e->old = v;
}
static DWORD g_satbDbgObj, g_satbDbgAddr;  // the last skipped store (diagnostic)
static int __cdecl SatbWorkerStore(DWORD* r) {
    if (g_satbMode != SATB_ACT) return 0;
    Ts3nAutShadow& k = ts3native_autshadow;
    k.satbWStores++;
    const DWORD op = r[SR_EDX] & 0xFFFF;
    DWORD addr = 0, n = 0, ipB = 0, vtB = 0;
    int spB = 0;
    if (SatbStoreRange(r, op, &addr, &n, &ipB, &spB, &vtB) <= 0 || addr < 0x10000 || !n || n > SATB_BIGW * 4) return 0;
    if (g_satbWArenaN && SatbWInArena(addr)) return 0;
    u8 val[8];
    const DWORD vn = n <= 8 ? SatbStoreBytes(r, op, val) : 0;
    if (vn != n) {  // not emulated: only a logged word in the range matters
        for (DWORD w = addr & ~3u; w < addr + n; w += 4) {
            if (!SatbEntOf(w)) continue;
            k.satbWConflict++;
            g_satbDbgObj = op >= 0x59 && op <= 0x65 ? SatbS(r, 2) : 0, g_satbDbgAddr = w;
            if (k.satbWConflict <= 8) Log("autshadow satb: worker store op 0x%03lX to %08lX (object %08lX) the sim changed in this job: skipped, job stopped", op, w, g_satbDbgObj);
            InterlockedOr(&g_satbStale, SATB_ST_CONFLICT);
            SatbWGiveUp(op);
            SatbVtsp(r) -= vtB;
            SatbAdvance(r, ipB, spB);
            return 1;
        }
        return 0;
    }
    bool overlay = false;
    for (DWORD w = addr & ~3u; w < addr + n; w += 4) {
        SatbEnt* e = SatbEntOf(w);
        if (e) { SatbEntPut(e, w, addr, n, val); overlay = true; continue; }
        for (DWORD b = 0; b < 4; b++)
            if (w + b >= addr && w + b < addr + n) *(volatile u8*)(uintptr_t)(w + b) = val[w + b - addr];
        _ReadWriteBarrier();
        if ((e = SatbEntOf(w)) != NULL) SatbEntPut(e, w, addr, n, val), overlay = true;  // the sim logged it meanwhile
    }
    if (overlay) k.satbWConflict++;
    SatbAdvance(r, ipB, spB);
    return 1;
}

static DWORD g_satbWRet;                    // SatbWorkerLoad's answer for the stub (worker thread only)
static DWORD g_satbJoin = 0x00E54B28;       // callvirt after its cache step (eax = receiver) [verified]
static DWORD g_satbNre = 0x00E593DF;        // callvirt's null-receiver path [verified]
// ---- stubs (naked; entry state of every handler: eax = edx = opcode, edi ip, esi sp; flags dead, ebx/ecx not live
// [verified]; every path back into a game handler keeps eax and edx) ----
static __declspec(naked) void SatbStoreStub() {
    __asm {
        push eax
        mov eax, dword ptr fs:[0x24]
        cmp eax, dword ptr [g_satbWTid]
        pop eax
        je worker
        pushad
        push esp
        call SatbSimStore
        add esp, 4
        popad
        jmp dword ptr [g_satbOrig + edx * 4]
    worker:
        pushad
        push esp
        call SatbWorkerStore
        add esp, 4
        test eax, eax
        popad
        jnz skip
        jmp dword ptr [g_satbOrig + edx * 4]
    skip:
        jmp dword ptr [g_satbHead]
    }
}
// Load opcodes: the sim pays this test on every heap load of a span (S0b: 16.6k / 19.7k loads a job at ~9 ns = most of the
// barrier). Per-opcode thunks (SatbMakeLoadThunk, docs/206 §11) do it with direct jumps: ecx (not live at handler entry)
// takes the thread id, the worker id and the count live on their own cache lines, and the jump to the game's handler is a
// direct `jmp rel32` per opcode instead of one shared `jmp [g_satbOrig + edx*4]` that all 41 load opcodes went through.
// SatbLoadStub is the same test with the shared jump (used when the thunk page cannot be allocated).
static __declspec(naked) void SatbLoadWorker() {
    __asm {
        pushad
        push esp
        call SatbWorkerLoad
        add esp, 4
        mov dword ptr [g_satbWRet], eax
        popad
        cmp dword ptr [g_satbWRet], 1
        je done
        cmp dword ptr [g_satbWRet], 3
        je join
        cmp dword ptr [g_satbWRet], 4
        je nre
        jmp dword ptr [g_satbOrig + edx * 4]
    done:
        jmp dword ptr [g_satbHead]
    join:
        jmp dword ptr [g_satbJoin]
    nre:
        jmp dword ptr [g_satbNre]
    }
}
static __declspec(naked) void SatbLoadStub() {
    __asm {
        mov ecx, dword ptr fs:[0x24]
        cmp ecx, dword ptr [g_satbWTidL]
        je worker
        add dword ptr [g_satbSimLoadsL], 1  // sim loads (sim thread only)
        jmp dword ptr [g_satbOrig + edx * 4]
    worker:
        jmp SatbLoadWorker
    }
}
// One thunk per wrapped load opcode (32 bytes): mov ecx, fs:[0x24]; cmp ecx, [tid]; je worker; add [count], 1; jmp handler.
static u8* g_satbThunks;  // RWX page, SATB_NOP x 32 bytes worst case (only load opcodes get one)
static void SatbPutRel(u8* at, const void* target) { *(DWORD*)at = (DWORD)(uintptr_t)target - (DWORD)(uintptr_t)(at + 4); }
static void* SatbMakeLoadThunk(DWORD op, const void* handler, const void* worker, volatile DWORD* tid, volatile DWORD* count) {
    if (!g_satbThunks) g_satbThunks = (u8*)VirtualAlloc(NULL, SATB_NOP * 32, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!g_satbThunks) return NULL;
    u8* t = g_satbThunks + op * 32;
    static const u8 kHead[7] = {0x64, 0x8B, 0x0D, 0x24, 0x00, 0x00, 0x00};  // mov ecx, fs:[0x24]
    memcpy(t, kHead, 7);
    t[7] = 0x3B, t[8] = 0x0D, *(DWORD*)(t + 9) = (DWORD)(uintptr_t)tid;          // cmp ecx, [tid]
    t[13] = 0x0F, t[14] = 0x84, SatbPutRel(t + 15, worker);                      // je worker
    t[19] = 0x83, t[20] = 0x05, *(DWORD*)(t + 21) = (DWORD)(uintptr_t)count, t[25] = 1;  // add dword [count], 1
    t[26] = 0xE9, SatbPutRel(t + 27, handler);                                    // jmp handler
    t[31] = 0xCC;
    FlushInstructionCache(GetCurrentProcess(), t, 32);
    return t;
}
// worker: a superinstruction runs as its base sequence (EA's peephole replaced only the first word, docs/data/satb-handlers.csv)
static __declspec(naked) void SatbSuperStub() {
    __asm {
        push eax
        mov eax, dword ptr fs:[0x24]
        cmp eax, dword ptr [g_satbWTid]
        pop eax
        jne orig
        movzx eax, word ptr [g_satbBase + edx * 2]
        mov edx, eax
        mov ecx, dword ptr [g_satbTable]
        jmp dword ptr [ecx + edx * 4]
    orig:
        jmp dword ptr [g_satbOrig + edx * 4]
    }
}

// ---- the worker's arena chunks taken during a job: a chunk the sim used earlier in the job can come back through the
// shared pool [verified: 0xD71900 takes the pool head], with log entries at addresses that are now the worker's frame.
// P3 sends grow through g_mtTramp[13] on both gate paths; after it, the state's chain head is the chunk in use. ----
typedef void*(__cdecl* SatbGrowFn)(DWORD* state, DWORD size);
static SatbGrowFn g_satbGrowNext;
static LONG g_satbWGrows;  // worker grows in act spans (monohost reads it)
// worker: one chunk taken by a grow. satb-act on satb4 (0.36.139): ~11,100 give-ups at 0xD71900, the 64-entry list filled
// within a job because every grow was a new entry, while a deep call path takes the same few pool chunks again and again
// (a return gives its chunk back to the pool head, the next grow takes it) [inferred from the counts: 1,121 jobs, a
// handful of chunks in the pool]. A chunk already listed is not added again; a list past SATB_WARENA distinct chunks still
// gives up (its frames could not be told from logged words).
static void SatbWArenaAdd(DWORD lo, DWORD hi) {
    const LONG n = g_satbWArenaN;
    for (LONG i = 0; i < n; i++)
        if (g_satbWArena[i][0] == lo) { if (hi > g_satbWArena[i][1]) g_satbWArena[i][1] = hi; return; }
    if (n < SATB_WARENA) g_satbWArena[n][0] = lo, g_satbWArena[n][1] = hi, g_satbWArenaN = n + 1;
    else SatbWGiveUp(0xD71900);
}
static void* __cdecl SatbGrowWrap(DWORD* st, DWORD size) {
    void* r = g_satbGrowNext(st, size);
    if (g_satbLive && st && GetCurrentThreadId() == g_satbWTid) {
        const DWORD* c = (const DWORD*)(uintptr_t)st[0x13C / 4];
        g_satbWGrows++;
        if (c) SatbWArenaAdd((DWORD)(uintptr_t)c, c[1]);
    }
    return r;
}
static void SatbHookGrow() {
    if (g_satbGrowNext || !(ts3native_mtprobe.p3Ready & 1) || !g_mtTramp[13]) return;
    g_satbGrowNext = (SatbGrowFn)g_mtTramp[13];
    g_mtTramp[13] = (void*)SatbGrowWrap;
}

// ---- runtime internal calls (mscorlib icalls, static table 0x10B5838: 613 function pointers, read when a method is first
// resolved [verified]). Entries are swapped at install for wrappers: on the sim during a span the written range is logged
// first (or the worker is stopped first for a writer we cannot size); on the worker the read range is patched from the log
// after the copy, or the object gives up on a logged word. cdecl, `this` first [verified callee bodies]. ----
typedef DWORD(__cdecl* SatbEsFn)(const void* klass);
static SatbEsFn g_satbElemSize = (SatbEsFn)(uintptr_t)0x00E6B690;  // array element size from the array's class
static DWORD SatbEs(DWORD arr) { return arr ? g_satbElemSize(*(const void* const*)(uintptr_t)*(const DWORD*)(uintptr_t)arr) : 0; }  // vtable -> class
static __forceinline bool SatbSimSide() { return g_satbOn && GetCurrentThreadId() != g_satbWTid; }
static __forceinline bool SatbWorkerSide() { return g_satbOn && g_satbMode == SATB_ACT && GetCurrentThreadId() == g_satbWTid; }
static void SatbRtLog(DWORD addr, DWORD n) { ts3native_autshadow.satbRtCalls++; SatbSimRange(addr, n); }
static void SatbRtUnknown() { Ts3nAutShadow& k = ts3native_autshadow; k.satbRtCalls++, k.satbRtWaits++; SatbSimWait(SATB_ST_RT); }
// worker: dst received a copy of [src, src + n): put back the cut bytes of every logged source word
static void SatbWPatchCopy(DWORD src, DWORD n, u8* dst) {
    Ts3nAutShadow& k = ts3native_autshadow;
    if (!n) return;
    if (g_satbWArenaN && SatbWInArena(src)) return;
    for (DWORD w = src & ~3u; w < src + n; w += 4) {
        DWORD old;
        if (!SatbFind(w, &old)) continue;
        k.satbWRtFix++;
        for (DWORD b = 0; b < 4; b++)
            if (w + b >= src && w + b < src + n) dst[w + b - src] = (u8)(old >> (8 * b));
    }
}
static bool SatbWAnyLogged(DWORD addr, DWORD n) {
    for (DWORD w = addr & ~3u; w < addr + n; w += 4) { DWORD old; if (SatbFind(w, &old)) return true; }
    return false;
}

enum { SRT_CLEAR, SRT_FASTCOPY, SRT_SETVALUE, SRT_GETVALUE, SRT_GETGENERIC, SRT_CLONE, SRT_BLOCKCOPY, SRT_SETBYTE, SRT_STRCPY1, SRT_STRCPY2,
       SRT_STRCPY3, SRT_STRCPY4, SRT_COPYTO, SRT_INC, SRT_DEC, SRT_XCHG, SRT_XCHGF, SRT_CMPX, SRT_CMPXF, SRT_ADD, SRT_ADD64, SRT_INC64,
       SRT_DEC64, SRT_XCHG64, SRT_XCHGD, SRT_CMPX64, SRT_CMPXD, SRT_FSET, SRT_FGET, SRT_MCOPY, SRT_MWB, SRT_MW16, SRT_MW32, SRT_MW64,
       SRT_INITARR, SRT_CLONEOBJ, SRT_PTRTOSTRUCT, SRT_PTRTOSTRUCT2, SRT_PROPINFO, SRT_LCID, SRT_INVOKE, SRT_GCTARGET, SRT_N };
static const DWORD kSatbRtAddr[SRT_N] = {0xE7A040, 0xE7A070, 0xE793D0, 0xE79210, 0xE7A300, 0xEA3800, 0xE81280, 0xE81260,
    0xEB92F0, 0xEB9320, 0xEB9350, 0xEB9380, 0xEB9040, 0xE66E10, 0xF4F560, 0xEBA1F0, 0xEBA210, 0xEBA2B0, 0xEBA2D0, 0xEBA390,
    0xEBA3C0, 0xEBA190, 0xEBA1C0, 0xEBA240, 0xEBA280, 0xEBA340, 0xEBA300, 0xE7B320, 0xE7B150, 0xE8DB40, 0xE8DC70, 0xE8DC90,
    0xE8DC80, 0xE8DCB0, 0xE7A330, 0xE7A400, 0xE99A50, 0xE99B10, 0xE7B4D0, 0xE874B0, 0xE7CDC0, 0xE78FD0};
static void* g_satbRt[SRT_N];  // the original function of each wrapper
#define SATB_RT(i, T) ((T)g_satbRt[i])
typedef DWORD(__cdecl* SatbRt2)(DWORD, DWORD);
typedef DWORD(__cdecl* SatbRt3)(DWORD, DWORD, DWORD);
typedef DWORD(__cdecl* SatbRt4)(DWORD, DWORD, DWORD, DWORD);
typedef DWORD(__cdecl* SatbRt5)(DWORD, DWORD, DWORD, DWORD, DWORD);
typedef DWORD(__cdecl* SatbRt1)(DWORD);
typedef unsigned __int64(__cdecl* SatbRtQ)(DWORD, DWORD, DWORD, DWORD, DWORD);  // 64-bit Interlocked: edx:eax result, args as dwords

static DWORD __cdecl SatbRtClear(DWORD arr, DWORD idx, DWORD len) {
    if (SatbSimSide() && arr) { const DWORD es = SatbEs(arr); SatbRtLog(arr + 0x10 + idx * es, len * es); }
    return SATB_RT(SRT_CLEAR, SatbRt3)(arr, idx, len);
}
static DWORD __cdecl SatbRtFastCopy(DWORD src, DWORD si, DWORD dst, DWORD di, DWORD len) {
    const DWORD es = dst ? SatbEs(dst) : 0;
    if (SatbSimSide() && dst) SatbRtLog(dst + 0x10 + di * es, len * es);
    const DWORD r = SATB_RT(SRT_FASTCOPY, SatbRt5)(src, si, dst, di, len);
    if (SatbWorkerSide() && (r & 0xFF) && src && dst) SatbWPatchCopy(src + 0x10 + si * es, len * es, (u8*)(uintptr_t)(dst + 0x10 + di * es));
    return r;
}
static DWORD __cdecl SatbRtSetValue(DWORD arr, DWORD value, DWORD pos) {
    if (SatbSimSide() && arr) { const DWORD es = SatbEs(arr); SatbRtLog(arr + 0x10 + pos * es, es); }
    return SATB_RT(SRT_SETVALUE, SatbRt3)(arr, value, pos);
}
static DWORD __cdecl SatbRtGetValue(DWORD arr, DWORD pos) {
    const DWORD r = SATB_RT(SRT_GETVALUE, SatbRt2)(arr, pos);
    if (SatbWorkerSide() && arr) { const DWORD es = SatbEs(arr); if (SatbWAnyLogged(arr + 0x10 + pos * es, es)) SatbWGiveUp(0xE79210); }
    return r;
}
static DWORD __cdecl SatbRtGetGeneric(DWORD arr, DWORD pos, DWORD out) {
    const DWORD r = SATB_RT(SRT_GETGENERIC, SatbRt3)(arr, pos, out);
    if (SatbWorkerSide() && arr && out) { const DWORD es = SatbEs(arr); SatbWPatchCopy(arr + 0x10 + pos * es, es, (u8*)(uintptr_t)out); }
    return r;
}
static DWORD __cdecl SatbRtClone(DWORD arr) {
    const DWORD r = SATB_RT(SRT_CLONE, SatbRt1)(arr);
    if (SatbWorkerSide() && arr && r) SatbWPatchCopy(arr + 0x10, *(const DWORD*)(uintptr_t)(arr + 8) * SatbEs(arr), (u8*)(uintptr_t)(r + 0x10));
    return r;
}
static DWORD __cdecl SatbRtBlockCopy(DWORD src, DWORD so, DWORD dst, DWORD dO, DWORD count) {
    if (SatbSimSide() && dst) SatbRtLog(dst + 0x10 + dO, count);
    const DWORD r = SATB_RT(SRT_BLOCKCOPY, SatbRt5)(src, so, dst, dO, count);
    if (SatbWorkerSide() && (r & 0xFF) && src && dst) SatbWPatchCopy(src + 0x10 + so, count, (u8*)(uintptr_t)(dst + 0x10 + dO));
    return r;
}
static DWORD __cdecl SatbRtSetByte(DWORD arr, DWORD idx, DWORD v) {
    if (SatbSimSide() && arr) SatbRtLog(arr + 0x10 + idx, 1);
    return SATB_RT(SRT_SETBYTE, SatbRt3)(arr, idx, v);
}
static DWORD __cdecl SatbRtStrcpy1(DWORD dest, DWORD pos, DWORD src) {
    if (SatbSimSide() && dest && src) SatbRtLog(dest + 8 + 2 * pos, 2 * *(const DWORD*)(uintptr_t)(src + 4));
    const DWORD r = SATB_RT(SRT_STRCPY1, SatbRt3)(dest, pos, src);
    if (SatbWorkerSide() && dest && src) SatbWPatchCopy(src + 8, 2 * *(const DWORD*)(uintptr_t)(src + 4), (u8*)(uintptr_t)(dest + 8 + 2 * pos));
    return r;
}
static DWORD __cdecl SatbRtStrcpy2(DWORD dest, DWORD pos, DWORD src, DWORD sp, DWORD count) {
    if (SatbSimSide() && dest) SatbRtLog(dest + 8 + 2 * pos, 2 * count);
    const DWORD r = SATB_RT(SRT_STRCPY2, SatbRt5)(dest, pos, src, sp, count);
    if (SatbWorkerSide() && dest && src) SatbWPatchCopy(src + 8 + 2 * sp, 2 * count, (u8*)(uintptr_t)(dest + 8 + 2 * pos));
    return r;
}
static DWORD __cdecl SatbRtStrcpy3(DWORD dest, DWORD pos, DWORD chars) {
    if (SatbSimSide() && dest && chars) SatbRtLog(dest + 8 + 2 * pos, 2 * *(const DWORD*)(uintptr_t)(chars + 8));
    const DWORD r = SATB_RT(SRT_STRCPY3, SatbRt3)(dest, pos, chars);
    if (SatbWorkerSide() && dest && chars) SatbWPatchCopy(chars + 0x10, 2 * *(const DWORD*)(uintptr_t)(chars + 8), (u8*)(uintptr_t)(dest + 8 + 2 * pos));
    return r;
}
static DWORD __cdecl SatbRtStrcpy4(DWORD dest, DWORD pos, DWORD chars, DWORD sp, DWORD count) {
    if (SatbSimSide() && dest) SatbRtLog(dest + 8 + 2 * pos, 2 * count);
    const DWORD r = SATB_RT(SRT_STRCPY4, SatbRt5)(dest, pos, chars, sp, count);
    if (SatbWorkerSide() && dest && chars) SatbWPatchCopy(chars + 0x10 + 2 * sp, 2 * count, (u8*)(uintptr_t)(dest + 8 + 2 * pos));
    return r;
}
static DWORD __cdecl SatbRtCopyTo(DWORD str, DWORD si, DWORD dest, DWORD di, DWORD count) {
    if (SatbSimSide() && dest) SatbRtLog(dest + 0x10 + 2 * di, 2 * count);
    const DWORD r = SATB_RT(SRT_COPYTO, SatbRt5)(str, si, dest, di, count);
    if (SatbWorkerSide() && str && dest) SatbWPatchCopy(str + 8 + 2 * si, 2 * count, (u8*)(uintptr_t)(dest + 0x10 + 2 * di));
    return r;
}
// Interlocked and the other point writers: log the location first (sim); on the worker a logged location is a conflict
static void SatbRtPoint(DWORD loc, DWORD n, bool write) {
    if (SatbSimSide()) { if (write) SatbRtLog(loc, n); return; }
    if (SatbWorkerSide() && loc && SatbWAnyLogged(loc, n)) SatbWGiveUp(0xEBA000);
}
static DWORD __cdecl SatbRtInc(DWORD loc) { SatbRtPoint(loc, 4, true); return SATB_RT(SRT_INC, SatbRt1)(loc); }
static DWORD __cdecl SatbRtDec(DWORD loc) { SatbRtPoint(loc, 4, true); return SATB_RT(SRT_DEC, SatbRt1)(loc); }
static DWORD __cdecl SatbRtXchg(DWORD loc, DWORD v) { SatbRtPoint(loc, 4, true); return SATB_RT(SRT_XCHG, SatbRt2)(loc, v); }
static DWORD __cdecl SatbRtCmpx(DWORD loc, DWORD v, DWORD c) { SatbRtPoint(loc, 4, true); return SATB_RT(SRT_CMPX, SatbRt3)(loc, v, c); }
static DWORD __cdecl SatbRtAdd(DWORD loc, DWORD v) { SatbRtPoint(loc, 4, true); return SATB_RT(SRT_ADD, SatbRt2)(loc, v); }
// 64-bit variants: up to 5 argument dwords passed through, result in edx:eax (double results of Exchange/CompareExchange(double)
// come back in st0 [inferred from the signature]: those two keep the x87 return by a plain tail call in the naked wrappers below)
static unsigned __int64 __cdecl SatbRtAdd64(DWORD loc, DWORD a, DWORD b, DWORD c, DWORD d) { SatbRtPoint(loc, 8, true); return SATB_RT(SRT_ADD64, SatbRtQ)(loc, a, b, c, d); }
static unsigned __int64 __cdecl SatbRtInc64(DWORD loc, DWORD a, DWORD b, DWORD c, DWORD d) { SatbRtPoint(loc, 8, true); return SATB_RT(SRT_INC64, SatbRtQ)(loc, a, b, c, d); }
static unsigned __int64 __cdecl SatbRtDec64(DWORD loc, DWORD a, DWORD b, DWORD c, DWORD d) { SatbRtPoint(loc, 8, true); return SATB_RT(SRT_DEC64, SatbRtQ)(loc, a, b, c, d); }
static unsigned __int64 __cdecl SatbRtXchg64(DWORD loc, DWORD a, DWORD b, DWORD c, DWORD d) { SatbRtPoint(loc, 8, true); return SATB_RT(SRT_XCHG64, SatbRtQ)(loc, a, b, c, d); }
static unsigned __int64 __cdecl SatbRtCmpx64(DWORD loc, DWORD a, DWORD b, DWORD c, DWORD d) { SatbRtPoint(loc, 8, true); return SATB_RT(SRT_CMPX64, SatbRtQ)(loc, a, b, c, d); }
// float and double results come back in st0: these four log, then tail-jump (no C return value in between)
static void __cdecl SatbRtPoint4(DWORD loc) { SatbRtPoint(loc, 4, true); }
static void __cdecl SatbRtPoint8(DWORD loc) { SatbRtPoint(loc, 8, true); }
static_assert(SRT_XCHGF == 16 && SRT_CMPXF == 18 && SRT_XCHGD == 24 && SRT_CMPXD == 26, "naked wrappers index g_satbRt by hand");
static __declspec(naked) void SatbRtXchgF() {  // Exchange(ref float, float)
    __asm {
        pushad
        push dword ptr [esp + 36]
        call SatbRtPoint4
        add esp, 4
        popad
        jmp dword ptr [g_satbRt + 64]
    }
}
static __declspec(naked) void SatbRtCmpxF() {  // CompareExchange(ref float, float, float)
    __asm {
        pushad
        push dword ptr [esp + 36]
        call SatbRtPoint4
        add esp, 4
        popad
        jmp dword ptr [g_satbRt + 72]
    }
}
static __declspec(naked) void SatbRtXchgD() {  // Exchange(ref double, double)
    __asm {
        pushad
        push dword ptr [esp + 36]
        call SatbRtPoint8
        add esp, 4
        popad
        jmp dword ptr [g_satbRt + 96]
    }
}
static __declspec(naked) void SatbRtCmpxD() {  // CompareExchange(ref double, double, double)
    __asm {
        pushad
        push dword ptr [esp + 36]
        call SatbRtPoint8
        add esp, 4
        popad
        jmp dword ptr [g_satbRt + 104]
    }
}
// MonoField.SetValueInternal 0xE7B320 (this, obj, value) [verified, disassembly, docs/206 §10]: cf = this[+8] (MonoClassField:
// +0 MonoType*, +8 offset, +0x14 parent class). Static (type attrs +4 & 0x10): vtable = mono_class_vtable(this->vtable->domain,
// cf->parent) 0xEA2AD0, dest = vtable[+0xC] + offset (0xEA12E0); instance: dest = obj + offset (0xEA1290). set_value 0xEA10A0
// writes by the MonoType: byref flag = a pointer; the kind byte +6 (table 0xEA1274); enum = its base type; valuetype and a
// valuetype generic instance = mono_class_value_size (class +0x54 instance size - 4-byte header, 0xE6F5A0). Logged before the
// call (0.36.136); an unknown kind or a class whose size is not set yet still waits (satbRtWaits).
typedef DWORD(__cdecl* SatbPtrFn1)(DWORD);
typedef DWORD(__cdecl* SatbPtrFn2)(DWORD, DWORD);
static SatbPtrFn1 g_satbClassOfType = (SatbPtrFn1)(uintptr_t)0x00E70D50;  // mono_class_from_mono_type
static SatbPtrFn2 g_satbClassVtable = (SatbPtrFn2)(uintptr_t)0x00EA2AD0;  // mono_class_vtable(domain, class): finds or makes it
static SatbPtrFn1 g_satbIsNullable = (SatbPtrFn1)(uintptr_t)0x00E6AD10;   // mono_class_is_nullable(class) [verified, 0xE7B259]
static DWORD SatbValueSize(DWORD klass) {  // mono_class_value_size without its init call: 0 = layout not set yet
    const u8* k = (const u8*)(uintptr_t)klass;
    return k && (k[0x14] & 4) && *(const DWORD*)(k + 0x54) > 4 ? *(const DWORD*)(k + 0x54) - 4 : 0;
}
static DWORD SatbTypeSize(DWORD type) {
    const u8* t = (const u8*)(uintptr_t)type;
    if (*(const DWORD*)(t + 4) & 0x40000000) return 4;  // byref: the slot holds a pointer
    u8 kind = t[6];
    const DWORD data = *(const DWORD*)t;  // class (valuetype) or generic class (generic instance)
    if (kind == 0x15) {  // generic instance: the container's own kind
        if (((const u8*)(uintptr_t)*(const DWORD*)(uintptr_t)(data + 4))[0xA6] == 0x11) return SatbValueSize(g_satbClassOfType(type));
        return 4;
    }
    if (kind == 0x11) {
        if (!(((const u8*)(uintptr_t)data)[0x14] & 0x10)) return SatbValueSize(g_satbClassOfType(type));
        kind = ((const u8*)(uintptr_t)*(const DWORD*)(uintptr_t)(data + 4))[6];  // enum: the base type's kind
    }
    switch (kind) {
    case 0x02: case 0x04: case 0x05: return 1;                  // bool, i1, u1
    case 0x03: case 0x06: case 0x07: return 2;                  // char, i2, u2
    case 0x0A: case 0x0B: case 0x0D: return 8;                  // i8, u8, r8
    case 0x08: case 0x09: case 0x0C: case 0x0E: case 0x0F: case 0x12: case 0x14: case 0x18: case 0x19: case 0x1B: case 0x1C:
    case 0x1D: return 4;                                        // i4 u4 r4 string ptr class array i u fnptr object szarray
    default: return 0;                                          // the game's set_value refuses these: unknown, wait
    }
}
// [dest, dest + size) the call writes; 0 size = unknown
static DWORD SatbFieldDest(DWORD self, DWORD obj, DWORD* size) {
    const DWORD cf = *(const DWORD*)(uintptr_t)(self + 8), type = cf ? *(const DWORD*)(uintptr_t)cf : 0;
    *size = 0;
    if (!type) return 0;
    const DWORD off = *(const DWORD*)(uintptr_t)(cf + 8);
    DWORD base = obj;
    if (*(const DWORD*)(uintptr_t)(type + 4) & 0x10) {  // static: the class's vtable data (made now if missing, as the call does)
        const DWORD dom = *(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)self + 8);
        const DWORD vt = g_satbClassVtable(dom, *(const DWORD*)(uintptr_t)(cf + 0x14));
        base = vt ? *(const DWORD*)(uintptr_t)(vt + 0xC) : 0;
    }
    if (base < 0x10000) return 0;  // null object: the call throws, nothing to log
    *size = SatbTypeSize(type);
    return base + off;
}
static DWORD __cdecl SatbRtFieldSet(DWORD self, DWORD obj, DWORD value) {
    if (SatbSimSide()) {
        ts3native_autshadow.satbRtFset++;
        DWORD n = 0;
        const DWORD at = self ? SatbFieldDest(self, obj, &n) : 0;
        if (n) SatbRtLog(at, n);
        else if (at || !self) SatbRtUnknown();  // a kind or layout not sized: the worker leaves first
    }
    return SATB_RT(SRT_FSET, SatbRt3)(self, obj, value);
}
// MonoField.GetValueInternal 0xE7B150 (this, obj) [verified, disassembly, docs/206 §12]: the same cf; a reference-kind field
// (string, class, array, object, szarray, reference generic instance; table 0xE7B304 entry 1) returns the field's pointer
// as read (0xEA1350 instance / 0xEA2510 static); a value kind (primitives, i/u, valuetype, enum) returns a new box with the
// field's bytes at box + 4; Nullable<T> (0xE6AD10) goes through mono_nullable_box 0xEA3AC0. On the worker the result is
// patched to the cut: a logged pointer is answered from the log, a logged value is copied into the box from the log.
// Nullable / other generic structs, pointers and unknown kinds with a logged word: give up as before (satbWGiveUp).
// bp-satb-act-1 (0.36.136): 4,336 give-ups at 0xE7B150 from ts3fixes' worker-side FieldInfo.GetValue reads stopped all 1,087
// jobs (satbStale 1,087, A4 `stop` 99%).
static DWORD __cdecl SatbRtFieldGet(DWORD self, DWORD obj) {
    const DWORD r = SATB_RT(SRT_FGET, SatbRt2)(self, obj);
    if (!SatbWorkerSide() || !self) return r;
    DWORD n = 0;
    const DWORD at = SatbFieldDest(self, obj, &n);  // static: the vtable the call just used (a lookup now)
    if (at && n && !SatbWAnyLogged(at, n)) return r;  // the common case: the field was not written since the cut
    const DWORD cf = *(const DWORD*)(uintptr_t)(self + 8), type = *(const DWORD*)(uintptr_t)cf;
    const u8 kind = ((const u8*)(uintptr_t)type)[6];
    const bool ref = kind == 0x0E || kind == 0x12 || kind == 0x14 || kind == 0x1C || kind == 0x1D ||
                     (kind == 0x15 && ((const u8*)(uintptr_t)*(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)type + 4))[0xA6] != 0x11);
    const bool val = (kind >= 0x02 && kind <= 0x0D) || kind == 0x18 || kind == 0x19 || kind == 0x11;
    DWORD old;
    if (at && n == 4 && ref && !(at & 3) && SatbFind(at, &old)) { ts3native_autshadow.satbWRtFix++; return old; }
    if (at && n && val && r && !g_satbIsNullable(g_satbClassOfType(type))) { SatbWPatchCopy(at, n, (u8*)(uintptr_t)(r + 4)); return r; }
    SatbWGiveUp(0xE7B150);
    return r;
}
// Marshal.copy_from_unmanaged(src, startIndex, dest array, length): writes dest's elements [startIndex, startIndex + length)
// (docs/data/satb-handlers.csv: length x element size); the body checks the bounds and throws before writing [inferred]
static DWORD __cdecl SatbRtMcopy(DWORD src, DWORD start, DWORD dest, DWORD len) {
    if (SatbSimSide() && dest) { const DWORD es = SatbEs(dest); SatbRtLog(dest + 0x10 + start * es, len * es); }
    return SATB_RT(SRT_MCOPY, SatbRt4)(src, start, dest, len);
}
static DWORD __cdecl SatbRtMwB(DWORD p, DWORD o, DWORD v) { if (SatbSimSide()) SatbRtLog(p + o, 1); return SATB_RT(SRT_MWB, SatbRt3)(p, o, v); }
static DWORD __cdecl SatbRtMw16(DWORD p, DWORD o, DWORD v) { if (SatbSimSide()) SatbRtLog(p + o, 2); return SATB_RT(SRT_MW16, SatbRt3)(p, o, v); }
static DWORD __cdecl SatbRtMw32(DWORD p, DWORD o, DWORD v) { if (SatbSimSide()) SatbRtLog(p + o, 4); return SATB_RT(SRT_MW32, SatbRt3)(p, o, v); }
static DWORD __cdecl SatbRtMw64(DWORD p, DWORD o, DWORD lo, DWORD hi) { if (SatbSimSide()) SatbRtLog(p + o, 8); return SATB_RT(SRT_MW64, SatbRt4)(p, o, lo, hi); }
static DWORD __cdecl SatbRtInitArr(DWORD arr, DWORD h) {
    if (SatbSimSide() && arr) SatbRtLog(arr + 0x10, *(const DWORD*)(uintptr_t)(arr + 8) * SatbEs(arr));
    return SATB_RT(SRT_INITARR, SatbRt2)(arr, h);
}
static DWORD __cdecl SatbRtCloneObj(DWORD obj) {
    const DWORD r = SATB_RT(SRT_CLONEOBJ, SatbRt1)(obj);
    if (SatbWorkerSide() && obj && r) {
        const u8* klass = **(const u8* const* const*)(uintptr_t)obj;
        const DWORD size = klass ? *(const DWORD*)(klass + 0x54) : 0;
        if (!size || SatbWAnyLogged(obj + 4, size - 4)) SatbWGiveUp(0xE7A400);  // arrays and strings go elsewhere; keep it simple
    }
    return r;
}
// Marshal.PtrToStructure [verified, disassembly, docs/206 §10]: 0xE99A50 = (intptr, object): writes the existing object, through
// the class's ptr-to-struct wrapper (0xE99770) run by mono_runtime_invoke 0xEA1090 = the interpreter (its stores pass the store
// stubs too); value types are refused before (kind 0x11 check). Logged: the object's fields [obj + 4, obj + instance size).
// 0xE99B10 = (intptr, Type): mono_object_new 0xEA36F0, then the same wrapper on the new object (no cut word: nothing to log).
static DWORD __cdecl SatbRtPtrToStruct(DWORD a, DWORD b) {
    if (SatbSimSide() && b >= 0x10000) {
        ts3native_autshadow.satbRtPtrStruct++;
        const DWORD klass = **(const DWORD* const*)(uintptr_t)b, size = klass ? *(const DWORD*)(uintptr_t)(klass + 0x54) : 0;
        if (size > 4) SatbRtLog(b + 4, size - 4);
        else SatbRtUnknown();
    }
    return SATB_RT(SRT_PTRTOSTRUCT, SatbRt2)(a, b);
}
static DWORD __cdecl SatbRtPtrToStruct2(DWORD a, DWORD b) { return SATB_RT(SRT_PTRTOSTRUCT2, SatbRt2)(a, b); }  // new object only
// docs/206 §15 (PR #105 row 8) [verified, disassembly]: three icalls the table had left unwrapped, now sized writers.
// MonoPropertyInfo.get_property_info 0xE7B4D0 (property, info*, req): fills the 5-pointer struct `info` (often the caller's
// frame: then private, skipped).
static DWORD __cdecl SatbRtPropInfo(DWORD prop, DWORD info, DWORD req) {
    if (SatbSimSide() && info) SatbRtLog(info, 20);
    return SATB_RT(SRT_PROPINFO, SatbRt3)(prop, info, req);
}
// CultureInfo.construct_internal_locale_from_lcid 0xE874B0 (this, lcid): refills this CultureInfo's fields
static DWORD SatbObjFields(DWORD obj) {  // instance size - 4-byte header, 0 = unknown
    const DWORD klass = obj >= 0x10000 ? **(const DWORD* const*)(uintptr_t)obj : 0;
    const DWORD size = klass ? *(const DWORD*)(uintptr_t)(klass + 0x54) : 0;
    return size > 4 ? size - 4 : 0;
}
static DWORD __cdecl SatbRtLcid(DWORD self, DWORD lcid) {
    if (SatbSimSide() && self >= 0x10000) { const DWORD n = SatbObjFields(self); if (n) SatbRtLog(self + 4, n); else SatbRtUnknown(); }
    return SATB_RT(SRT_LCID, SatbRt2)(self, lcid);
}
// MonoMethod.InternalInvoke 0xE7CDC0 (this, obj, params): writes out/ref values back into `params`' elements
static DWORD __cdecl SatbRtInvoke(DWORD self, DWORD obj, DWORD params) {
    if (SatbSimSide() && params >= 0x10000) SatbRtLog(params + 0x10, *(const DWORD*)(uintptr_t)(params + 8) * 4);
    return SATB_RT(SRT_INVOKE, SatbRt3)(self, obj, params);
}
// GCHandle.GetTarget 0xE78FD0 (row 10): a weak handle the sim's collection cleared during the job reads null on the worker
// where the cut had an object. The worker gives up if a collection ran since the cut (GC_gc_no [0x11F2F90], docs/66 §6.1).
static DWORD __cdecl SatbRtGcTarget(DWORD handle) {
    const DWORD r = SATB_RT(SRT_GCTARGET, SatbRt1)(handle);
    if (SatbWorkerSide() && *g_satbGcNo != g_satbGcNo0) SatbWGiveUp(0xE78FD0);
    return r;
}
static void* const kSatbRtWrap[SRT_N] = {(void*)SatbRtClear, (void*)SatbRtFastCopy, (void*)SatbRtSetValue, (void*)SatbRtGetValue,
    (void*)SatbRtGetGeneric, (void*)SatbRtClone, (void*)SatbRtBlockCopy, (void*)SatbRtSetByte, (void*)SatbRtStrcpy1, (void*)SatbRtStrcpy2,
    (void*)SatbRtStrcpy3, (void*)SatbRtStrcpy4, (void*)SatbRtCopyTo, (void*)SatbRtInc, (void*)SatbRtDec, (void*)SatbRtXchg,
    (void*)SatbRtXchgF, (void*)SatbRtCmpx, (void*)SatbRtCmpxF, (void*)SatbRtAdd, (void*)SatbRtAdd64, (void*)SatbRtInc64, (void*)SatbRtDec64,
    (void*)SatbRtXchg64, (void*)SatbRtXchgD, (void*)SatbRtCmpx64, (void*)SatbRtCmpxD, (void*)SatbRtFieldSet, (void*)SatbRtFieldGet,
    (void*)SatbRtMcopy, (void*)SatbRtMwB, (void*)SatbRtMw16, (void*)SatbRtMw32, (void*)SatbRtMw64, (void*)SatbRtInitArr,
    (void*)SatbRtCloneObj, (void*)SatbRtPtrToStruct, (void*)SatbRtPtrToStruct2, (void*)SatbRtPropInfo, (void*)SatbRtLcid, (void*)SatbRtInvoke,
    (void*)SatbRtGcTarget};
// EA's field-store helper 0xE69820 (callers 0xD79AF0 and ScriptHost_InvokeDelegate, docs/118 D11): `*(arg2) = arg3`, cdecl,
// 11 bytes [verified]. Replaced whole (InstallDetour) while Satb is on: the sim logs the word first; a worker store to a
// word the sim changed goes into the log entry (the worker's view), as SatbWorkerStore does.
static const u8 kSatbFieldStore[8] = {0x8B, 0x44, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x08};  // mov eax,[esp+0xC]; mov ecx,[esp+8]
static void __cdecl SatbFieldStore(DWORD, DWORD* ptr, DWORD v) {
    if (SatbSimSide()) SatbRtLog((DWORD)(uintptr_t)ptr, 4);
    else if (SatbWorkerSide()) {
        SatbEnt* e = SatbEntOf((DWORD)(uintptr_t)ptr & ~3u);
        if (e && !((DWORD)(uintptr_t)ptr & 3)) { e->old = v; ts3native_autshadow.satbWConflict++; return; }
    }
    *ptr = v;
}
// ---- cold paths under concurrency (docs/206 §8, docs/data/satb-races.csv X11, X13): runtime growth that is unlocked or
// lock-ordered against a quiescent sim (method transform, native-call thunk hash, vtable creation incl. the special-static
// registry, class-init slow path, image load). Entry detours (act only): on the worker, the current object gives up and the
// job stops (the warm-up reference should have run every one of these first; each hit is counted, the op in
// satbWGiveUpOp = 0xC000 + site); on the sim during a span, vtable creation waits for the worker to leave first.
struct SatbColdSite { DWORD at; u8 len; u8 bytes[8]; };
static const SatbColdSite kSatbCold[5] = {
    {0x00E643B0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x78}},              // transform (IL -> mint)
    {0x00E51400, 6, {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x14}},              // tramp_create_thunk (thunk hash [0x11F4128])
    {0x00EA1E30, 5, {0x83, 0xEC, 0x30, 0x56, 0x57}},                    // vtable creation (statics, special-static hash)
    {0x00EA3370, 8, {0x83, 0xEC, 0x18, 0x53, 0x8B, 0x5C, 0x24, 0x20}},  // class init slow path (a .cctor runs)
    {0x00D6FCB0, 6, {0x81, 0xEC, 0x24, 0x02, 0x00, 0x00}},              // image load (temp string refcount [0x11CB920])
};
static void* g_satbColdNext[5];
// docs/206 §14: the methods the worker transforms first (site 0: arg0 = RuntimeMethod, +8 = MonoMethod [verified, 0xE643B7])
// are kept by the worker and named on the sim at span end (SatbGiveOpLog), so the warm-up can be given them
static volatile LONG g_satbColdRmN;
static volatile LONG g_satbColdGiveUps;  // worker cold-path give-ups (all sites), read by the feature mask
static DWORD g_satbColdRm[16];
static LONG g_satbColdNamed;
static void __cdecl SatbColdEnter(int site, DWORD arg0) {
    if (!g_satbLive || g_satbMode != SATB_ACT) return;
    if (GetCurrentThreadId() == g_satbWTid) {
        if (site == 0 && arg0 && g_satbColdRmN < 16) g_satbColdRm[g_satbColdRmN] = arg0, g_satbColdRmN++;
        g_satbColdGiveUps++;  // per-search feature mask: a cold give-up in this job
        SatbWGiveUp(0xC000 + site);
    } else if (site == 2 && g_satbOn) SatbSimWait(SATB_ST_CLASS);
}
// sim, worker idle (SatbEnd): 'autshadow satb cold transform on the worker: <method>' once per method (warm-up list input).
// 0xE72010 = mono_method_full_name(method, signature) [verified args, docs/130 G-I1]; its string is not freed (a few per run).
typedef const char*(__cdecl* SatbNameFn)(DWORD method, int signature);
static SatbNameFn g_satbMethodName = (SatbNameFn)(uintptr_t)0x00E72010;
static void SatbColdNameLog() {
    for (; g_satbColdNamed < g_satbColdRmN; g_satbColdNamed++) {
        const DWORD rm = g_satbColdRm[g_satbColdNamed], mm = rm ? *(const DWORD*)(uintptr_t)(rm + 8) : 0;
        const char* nm = mm ? g_satbMethodName(mm, 0) : NULL;
        Log("autshadow satb cold transform on the worker: %s (runtime method %08lX)", nm ? nm : "?", rm);
    }
}
#define SATB_COLD_HOOK(i)                                  \
    static __declspec(naked) void SatbCold##i() {          \
        __asm { pushad }                                   \
        __asm { pushfd }                                   \
        __asm { push dword ptr [esp + 40] }                \
        __asm { push i }                                   \
        __asm { call SatbColdEnter }                       \
        __asm { add esp, 8 }                               \
        __asm { popfd }                                    \
        __asm { popad }                                    \
        __asm { jmp dword ptr [g_satbColdNext + i * 4] }   \
    }
SATB_COLD_HOOK(0)
SATB_COLD_HOOK(1)
SATB_COLD_HOOK(2)
SATB_COLD_HOOK(3)
SATB_COLD_HOOK(4)
static int SatbInstallCold() {
    static void (*const hooks[5])() = {SatbCold0, SatbCold1, SatbCold2, SatbCold3, SatbCold4};
    int n = 0;
    for (int i = 0; i < 5; i++)
        if (!g_satbColdNext[i] && (g_satbColdNext[i] = InstallDetour("satb cold path", kSatbCold[i].at, kSatbCold[i].bytes, kSatbCold[i].len,
                                                                     (void*)hooks[i])) != NULL) n++;
    return n;
}

static DWORD* g_satbRtTable = (DWORD*)(uintptr_t)0x010B5838;  // mono's icall function table (613 entries)
static const int SATB_RT_TABLE_N = 613;
// before any runtime icall is resolved (DllMain; monohost: before its first managed call). Returns entries swapped.
static int SatbInstallRt() {
    int n = 0;
    DWORD old;
    if (!VirtualProtect(g_satbRtTable, SATB_RT_TABLE_N * 4, PAGE_READWRITE, &old)) return -1;
    for (int i = 0; i < SRT_N; i++) {
        g_satbRt[i] = (void*)(uintptr_t)kSatbRtAddr[i];
        for (int j = 0; j < SATB_RT_TABLE_N; j++)
            if (g_satbRtTable[j] == kSatbRtAddr[i]) g_satbRtTable[j] = (DWORD)(uintptr_t)kSatbRtWrap[i], n++;
    }
    VirtualProtect(g_satbRtTable, SATB_RT_TABLE_N * 4, old, &old);
    if (InstallDetour("satb field store 0xE69820", 0x00E69820, kSatbFieldStore, 8, (void*)SatbFieldStore)) n++;
    return n;
}


// ---- docs/206 §15: native writers and readers the barrier did not see (contributor PR #105, our review
// docs/data/upstream/dalexlol-prs-104-107-review.md §1.2). All act only (Satb=2), installed with the cold detours. ----

// Row 1 [verified, disassembly]: MonoScriptHost_Simulate stores the new tick into Simulator.sSimTickCnt and
// SimClock.sSimTickCnt (8 bytes each) at 0xD81D39 / 0xD81D49 through the host's pointers +0xC20 / +0xC24 (set at 0xD7E93D /
// 0xD7E98D): raw native stores, unlogged. Logged here at the cut, the log keeps tick t: the worker's managed reads (load
// stubs) and its static-helper reads (SatbStaticGet) answer tick t while the sim has moved on.
static void SatbLogTicks() {
    const DWORD host = g_mtHostThis;
    if (!host || !g_satb) return;
    for (int i = 0; i < 2; i++) {
        const DWORD p = *(const DWORD*)(uintptr_t)(host + 0xC20 + 4 * i);
        if (p >= 0x10000) SatbLogWord(p), SatbLogWord(p + 4);
    }
}

// worker-private memory: its own stack or a frame-arena chunk it took in this job
static bool SatbWPrivate(DWORD a) {
    return (a < __readfsdword(4) && a >= __readfsdword(8)) || (g_satbWArenaN && SatbWInArena(a));
}

// Rows 2, 3, 5, 6, 12: EA binder native bodies, all reached through 0xE525C0(MonoInvocation* frame, ThreadContext* ctx)
// [verified: `call [[frame+4]+0x24](frame, ctx)`; MonoInvocation +0x04 RuntimeMethod, +0x0C this, +0x14 stack args (8-byte
// slots), docs/130 §1; RuntimeMethod +0x24 invoker, +0x28 body, docs/119 §9.4]. Bodies by address (+0x24 or +0x28), from the
// PR's table [theirs; seen counts in ts3native.log confirm the match in game]:
enum { SB_PQ_ADD, SB_PQ_REMOVEAT, SB_PQ_SETCAP, SB_HT_REHASH, SB_ELAPSED_MIN, SB_ELAPSED_HRS, SB_CURVE_FX, SB_N };
static const DWORD kSatbBodies[SB_N] = {0xE0D450, 0xE0C640, 0xE0D290, 0xDCA870, 0xE0A6D0, 0xE0A790, 0xE0A950};
static const char* const kSatbBodyNames[SB_N] = {"PriorityQueue.Add", "PriorityQueue.RemoveAt", "PriorityQueue.SetCapacity",
    "Hashtable.Rehash", "SimClock.ElapsedTimeInMinutes(ref float)", "SimClock.ElapsedTimeHours(ref float)", "Curve.Fx"};
static LONG g_satbBodySeen[SB_N][2], g_satbBodyLogged[SB_N][2];  // [body][sim, worker]: calls in spans; log lines at 1, 10, 100, ...
static LONG g_satbBindGiveUps;
static void* g_satbBindNext;
static const u8 kSatbBind[6] = {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x48};  // push ebp; mov ebp,esp; sub esp,0x48
typedef void(__cdecl* SatbBindFn)(DWORD frame, DWORD ctx);
static int SatbBodyOf(DWORD rm) {
    if (rm < 0x10000) return -1;
    const DWORD a = *(const DWORD*)(uintptr_t)(rm + 0x24), b = *(const DWORD*)(uintptr_t)(rm + 0x28);
    for (int i = 0; i < SB_N; i++) if (kSatbBodies[i] == a || kSatbBodies[i] == b) return i;
    return -1;
}
// sim: an object's fields, and with arrays = true the elements of an array its first field holds (PriorityQueue.mItems:
// +4 mItems, +8 mCapacity, +0xC mCount [verified: 0xE0D4AA-0xE0D4B5]); array test: class rank byte +0x10, element size +0x5C
// [verified: 0xE6B690]
static void SatbLogObj(DWORD obj, bool arrays) {
    const DWORD n = SatbObjFields(obj);
    if (!n) { if (obj >= 0x10000) SatbRtUnknown(); return; }
    SatbRtLog(obj + 4, n);
    const DWORD items = arrays ? *(const DWORD*)(uintptr_t)(obj + 4) : 0;
    if (items < 0x10000) return;
    const u8* k = (const u8*)(uintptr_t)**(const DWORD* const*)(uintptr_t)items;
    if (!k || !k[0x10]) return;
    SatbRtLog(items + 0x10, *(const DWORD*)(uintptr_t)(items + 8) * *(const DWORD*)(k + 0x5C));
}
static void __cdecl SatbBindCall(DWORD frame, DWORD ctx) {
    const SatbBindFn next = (SatbBindFn)g_satbBindNext;
    if (!g_satbOn) { next(frame, ctx); return; }
    const DWORD rm = *(const DWORD*)(uintptr_t)(frame + 4), self = *(const DWORD*)(uintptr_t)(frame + 0xC);
    const DWORD args = *(const DWORD*)(uintptr_t)(frame + 0x14);
    const int b = SatbBodyOf(rm);
    if (SatbSimSide()) {
        if (b >= 0) g_satbBodySeen[b][0]++;
        if (b == SB_PQ_ADD || b == SB_PQ_REMOVEAT || b == SB_PQ_SETCAP) SatbLogObj(self, true);  // row 3: header + items
        else if (b == SB_HT_REHASH) SatbLogObj(self, false);  // row 5: buckets / count / modificationCount (the new array is fresh)
        else if ((b == SB_ELAPSED_MIN || b == SB_ELAPSED_HRS) && args) {  // row 2: the ref float the body stores (0xE0A755)
            const DWORD p = *(const DWORD*)(uintptr_t)args;
            if (p >= 0x10000) SatbRtLog(p, 4);
        }
        next(frame, ctx);
        return;
    }
    if (!SatbWorkerSide()) { next(frame, ctx); return; }
    if (b >= 0) g_satbBodySeen[b][1]++;
    if (b == SB_ELAPSED_MIN || b == SB_ELAPSED_HRS) {
        // the body stores through the ref into shared memory (Autonomy.mLastTime and the like): the worker must not
        const DWORD p = args ? *(const DWORD*)(uintptr_t)args : 0;
        if (p >= 0x10000 && !SatbWPrivate(p)) { g_satbBindGiveUps++; SatbWGiveUp(kSatbBodies[b]); return; }  // object aborted: skip it
    } else if (b == SB_CURVE_FX && self >= 0x10000) {
        // row 6: Fx caches its argument in m_x (+0x14, store 0xE0AD21) on a shared tuning curve; Fx itself never reads it back
        // [verified]. The worker's store is taken back unless the sim stored meanwhile [inferred: no managed reader of m_x
        // was checked, so the worker leaves the sim's value as it found it]
        volatile LONG* mx = (volatile LONG*)(uintptr_t)(self + 0x14);
        const LONG saved = *mx;
        next(frame, ctx);
        const LONG now = *mx;
        if (now != saved) InterlockedCompareExchange(mx, saved, now);
        return;
    } else if (self >= 0x10000 && !SatbWPrivate(self)) {
        // row 12 (b): a body reads `this` natively, past the load redirect; any word of it the sim changed since the cut
        const DWORD n = SatbObjFields(self);
        if (n && n <= 4 * SATB_IC_MAXW && SatbWAnyLogged(self + 4, n)) { g_satbBindGiveUps++; SatbWGiveUp(0xE525C0); }
    }
    next(frame, ctx);
}
// sim, span end: one line per body and side at 1, 10, 100, ... calls (confirms the address match in game)
static void SatbBodyLog() {
    for (int i = 0; i < SB_N; i++)
        for (int s = 0; s < 2; s++)
            for (LONG n = g_satbBodySeen[i][s], p = 1; p <= n && p <= 100000000; p *= 10)
                if (p > g_satbBodyLogged[i][s]) { g_satbBodyLogged[i][s] = p; Log("autshadow satb binder body on the %s: %s x%ld", s ? "worker" : "sim", kSatbBodyNames[i], n); }
}

// Rows 7, 11: EA's static-field helpers in native bodies [verified, disassembly]: set 0xD89BF0(field, value*), address-of
// 0xD89C60(field, out*), get 0xD89CD0(field, out*): domain 0xE75260, vtable 0xEA2AD0(domain, field+0x14), class init 0xEA3370
// when vtable+0x18 bit 1 is clear, special (thread/context) statics through the domain's +0x88 hash 0xE45F00 -> 0xEBAB30,
// else vtable+0xC + field+8; then stackval_to_data 0xE52350(type, value, dest, 0) / stackval_from_data 0xE521A0(type, out,
// data, 0). Replaced whole (same steps), plus: the sim logs a set's destination (and an address-of's: the caller writes
// through it); the worker's get answers a logged static from the log; the worker's set and a logged address-of give up.
// Special statics are per thread: never shared, never logged.
typedef DWORD(__cdecl* SatbFn0)();
typedef DWORD(__cdecl* SatbFn2)(DWORD, DWORD);
typedef void(__cdecl* SatbStvFn)(DWORD type, DWORD a, DWORD b, int pinvoke);
static const u8 kSatbStSet[8] = {0x53, 0x56, 0x57, 0xE8, 0x68, 0xB6, 0x0E, 0x00};  // push ebx/esi/edi; call 0xE75260
static const u8 kSatbStAddr[8] = {0x53, 0x56, 0x57, 0xE8, 0xF8, 0xB5, 0x0E, 0x00};
static const u8 kSatbStGet[8] = {0x53, 0x56, 0x57, 0xE8, 0x88, 0xB5, 0x0E, 0x00};
static LONG g_satbStaticFix, g_satbStaticN;
static DWORD SatbStaticAddr(DWORD cf, bool* special) {
    const DWORD dom = ((SatbFn0)(uintptr_t)0x00E75260)();
    const DWORD vt = ((SatbFn2)(uintptr_t)0x00EA2AD0)(dom, *(const DWORD*)(uintptr_t)(cf + 0x14));
    if (!(*(const u8*)(uintptr_t)(vt + 0x18) & 2)) ((SatbPtrFn1)(uintptr_t)0x00EA3370)(vt);
    const DWORD h = *(const DWORD*)(uintptr_t)(dom + 0x88);
    if (h) {
        const DWORD r = ((SatbFn2)(uintptr_t)0x00E45F00)(h, cf);
        if (r) { *special = true; return ((SatbPtrFn1)(uintptr_t)0x00EBAB30)(r); }
    }
    *special = false;
    return *(const DWORD*)(uintptr_t)(vt + 0xC) + *(const DWORD*)(uintptr_t)(cf + 8);
}
static void __cdecl SatbStaticSet(DWORD cf, DWORD val) {
    bool sp;
    const DWORD dest = SatbStaticAddr(cf, &sp), type = *(const DWORD*)(uintptr_t)cf;
    if (!sp && g_satbOn) {
        if (SatbSimSide()) { const DWORD n = SatbTypeSize(type); if (n) SatbRtLog(dest, n); else SatbRtUnknown(); }
        else if (SatbWorkerSide()) { SatbWGiveUp(0xD89BF0); return; }  // a worker store into a shared static: never
    }
    ((SatbStvFn)(uintptr_t)0x00E52350)(type, val, dest, 0);
}
// worker, address-of a static the sim changed since the cut: the caller gets a worker-private copy holding the cut bytes
// (it reads through the pointer, or writes into the copy, never into the shared static); an unchanged static keeps its own
// address. satb7 (0.36.143) gave up here instead, and the tick statics are logged at every cut (row 1), so every job
// whose SimClock body took their address stopped: bp-satb-act-1 1,009 of 1,017 jobs stale (docs/206 §16). Copies live
// until the next span (SatbBegin resets the buffer; the worker is idle then).
static u8 g_satbWStatic[16384];
static LONG g_satbWStaticCopies;
static DWORD SatbWStaticView(DWORD a, DWORD n) {
    if (!SatbWAnyLogged(a, n)) return a;
    const DWORD at = (g_satbWStaticAt + 7) & ~7u;
    if (at + n > sizeof(g_satbWStatic)) return 0;
    u8* p = g_satbWStatic + at;
    g_satbWStaticAt = at + n;
    memcpy(p, (const void*)(uintptr_t)a, n);
    SatbWPatchCopy(a, n, p);
    g_satbWStaticCopies++;
    return (DWORD)(uintptr_t)p;
}
static void __cdecl SatbStaticAddrOf(DWORD cf, DWORD* out) {
    bool sp;
    DWORD a = SatbStaticAddr(cf, &sp);
    if (!sp && g_satbOn) {
        const DWORD n = SatbTypeSize(*(const DWORD*)(uintptr_t)cf);
        if (SatbSimSide()) { if (n) SatbRtLog(a, n); else SatbRtUnknown(); }
        else if (SatbWorkerSide()) {
            const DWORD v = n ? SatbWStaticView(a, n) : 0;
            if (v) a = v;
            else SatbWGiveUp(0xD89C60);  // unknown size or the copy buffer full
        }
    }
    *out = a;
}
static void __cdecl SatbStaticGet(DWORD cf, DWORD out) {
    bool sp;
    const DWORD src = SatbStaticAddr(cf, &sp), type = *(const DWORD*)(uintptr_t)cf;
    if (!sp && SatbWorkerSide()) {
        g_satbStaticN++;
        const DWORD n = SatbTypeSize(type);
        if (!n || SatbWAnyLogged(src, n)) {
            u8 tmp[64];
            if (!n || n > sizeof(tmp)) { SatbWGiveUp(0xD89CD0); }
            else {  // the cut value: live bytes with every logged word put back, converted the way the game does
                memcpy(tmp, (const void*)(uintptr_t)src, n);
                SatbWPatchCopy(src, n, tmp);
                g_satbStaticFix++;
                ((SatbStvFn)(uintptr_t)0x00E521A0)(type, out, (DWORD)(uintptr_t)tmp, 0);
                return;
            }
        }
    }
    ((SatbStvFn)(uintptr_t)0x00E521A0)(type, out, src, 0);
}
static int SatbInstallNative() {
    int n = 0;
    if (!g_satbBindNext && (g_satbBindNext = InstallDetour("satb binder bodies 0xE525C0", 0x00E525C0, kSatbBind, sizeof(kSatbBind), (void*)SatbBindCall)) != NULL) n++;
    static bool st;
    if (!st) {
        st = true;
        // replaced whole: the trampolines (a copied relative call) are never used
        n += InstallDetour("satb static set 0xD89BF0", 0x00D89BF0, kSatbStSet, 8, (void*)SatbStaticSet) != NULL;
        n += InstallDetour("satb static address 0xD89C60", 0x00D89C60, kSatbStAddr, 8, (void*)SatbStaticAddrOf) != NULL;
        n += InstallDetour("satb static get 0xD89CD0", 0x00D89CD0, kSatbStGet, 8, (void*)SatbStaticGet) != NULL;
    }
    return n;
}

// ---- install: the wrapped jump-table entries must still be the game's handlers (docs/data/satb-handlers.csv) ----
struct SatbOpRow { WORD op; u8 kind; DWORD handler; WORD base; };
static const SatbOpRow kSatbOps[] = {
    {0x59, SATB_K_STORE, 0xE56B06}, {0x5A, SATB_K_STORE, 0xE56B06}, {0x5B, SATB_K_STORE, 0xE56B29}, {0x5C, SATB_K_STORE, 0xE56B29},
    {0x5D, SATB_K_STORE, 0xE56B4E}, {0x5E, SATB_K_STORE, 0xE56B71}, {0x5F, SATB_K_STORE, 0xE56B9B}, {0x60, SATB_K_STORE, 0xE56BC4},
    {0x61, SATB_K_STORE, 0xE56BE7}, {0x62, SATB_K_STORE, 0xE56BE7}, {0x63, SATB_K_STORE, 0xE56C0A}, {0x64, SATB_K_STORE, 0xE56C43},
    {0x65, SATB_K_STORE, 0xE56CA9}, {0x6A, SATB_K_STORE, 0xE56F52}, {0x6B, SATB_K_STORE, 0xE56FEC}, {0x91, SATB_K_STORE, 0xE559BD},
    {0x92, SATB_K_STORE, 0xE559D3}, {0x93, SATB_K_STORE, 0xE559A7}, {0x94, SATB_K_STORE, 0xE55A01}, {0x95, SATB_K_STORE, 0xE559EB},
    {0x96, SATB_K_STORE, 0xE55A1D}, {0x97, SATB_K_STORE, 0xE55A39}, {0x98, SATB_K_STORE, 0xE559A7}, {0xF5, SATB_K_STORE, 0xE589F7},
    {0xFF, SATB_K_STORE, 0xE570C8}, {0x100, SATB_K_STORE, 0xE57089}, {0x101, SATB_K_STORE, 0xE58A17}, {0x102, SATB_K_STORE, 0xE58A49},
    {0x112, SATB_K_STORE, 0xE575CF}, {0x113, SATB_K_STORE, 0xE574DA}, {0x114, SATB_K_STORE, 0xE574DA}, {0x115, SATB_K_STORE, 0xE574DA},
    {0x116, SATB_K_STORE, 0xE574DA}, {0x117, SATB_K_STORE, 0xE574DA}, {0x118, SATB_K_STORE, 0xE574DA}, {0x119, SATB_K_STORE, 0xE574DA},
    {0x11A, SATB_K_STORE, 0xE574DA},
    {0x3E, SATB_K_LOAD, 0xE566A8}, {0x3F, SATB_K_LOAD, 0xE566C9}, {0x40, SATB_K_LOAD, 0xE566EA}, {0x41, SATB_K_LOAD, 0xE5670B},
    {0x42, SATB_K_LOAD, 0xE5672C}, {0x43, SATB_K_LOAD, 0xE5674C}, {0x44, SATB_K_LOAD, 0xE56773}, {0x45, SATB_K_LOAD, 0xE56793},
    {0x46, SATB_K_LOAD, 0xE5672C}, {0x47, SATB_K_LOAD, 0xE5672C}, {0x48, SATB_K_LOAD, 0xE567B3}, {0x49, SATB_K_LOAD, 0xE567DA},
    {0x4A, SATB_K_LOAD, 0xE56835}, {0x66, SATB_K_LOAD, 0xE56DA1}, {0x67, SATB_K_LOAD, 0xE56E1E}, {0x68, SATB_K_LOAD, 0xE56E66},
    {0x69, SATB_K_LOAD, 0xE56EAE}, {0x86, SATB_K_LOAD, 0xE558DF}, {0x87, SATB_K_LOAD, 0xE558F3}, {0x88, SATB_K_LOAD, 0xE55907},
    {0x89, SATB_K_LOAD, 0xE5591B}, {0x8A, SATB_K_LOAD, 0xE5592F}, {0x8B, SATB_K_LOAD, 0xE5592F}, {0x8C, SATB_K_LOAD, 0xE55942},
    {0x8D, SATB_K_LOAD, 0xE5595B}, {0x8E, SATB_K_LOAD, 0xE5596E}, {0x8F, SATB_K_LOAD, 0xE55981}, {0x90, SATB_K_LOAD, 0xE55994},
    {0xFE, SATB_K_LOAD, 0xE560CD}, {0x105, SATB_K_LOAD, 0xE573D7}, {0x106, SATB_K_LOAD, 0xE573D7}, {0x107, SATB_K_LOAD, 0xE573D7},
    {0x108, SATB_K_LOAD, 0xE573D7}, {0x109, SATB_K_LOAD, 0xE573D7}, {0x10A, SATB_K_LOAD, 0xE573D7}, {0x10B, SATB_K_LOAD, 0xE573D7},
    {0x10C, SATB_K_LOAD, 0xE573D7}, {0x10D, SATB_K_LOAD, 0xE573D7}, {0x10E, SATB_K_LOAD, 0xE573D7}, {0x10F, SATB_K_LOAD, 0xE573D7},
    {0x111, SATB_K_LOAD, 0xE57382}, {0x196, SATB_K_LOAD, 0xE572CE}, {0x197, SATB_K_LOAD, 0xE57303}, {0xEC, SATB_K_LOAD, 0xE549EB}, {0xED, SATB_K_LOAD, 0xE549EB},
    {0xEE, SATB_K_LOAD, 0xE549EB},
    {0x1AB, SATB_K_SUPER, 0xE58A73, 0x24}, {0x1AD, SATB_K_SUPER, 0xE58AC2, 0x24}, {0x1AF, SATB_K_SUPER, 0xE58B18, 0x46},
    {0x1B0, SATB_K_SUPER, 0xE58B71, 0xA6}, {0x1B3, SATB_K_SUPER, 0xE58BD6, 0x24}, {0x1B4, SATB_K_SUPER, 0xE58C0A, 0x75},
    {0x1B6, SATB_K_SUPER, 0xE58C72, 0x24}, {0x1B7, SATB_K_SUPER, 0xE58C8F, 0x84}, {0x1BB, SATB_K_SUPER, 0xE58E37, 0x75},
    {0x1BC, SATB_K_SUPER, 0xE58E98, 0x75}, {0x1BE, SATB_K_SUPER, 0xE58F55, 0x24}, {0x1BF, SATB_K_SUPER, 0xE58FC8, 0x15},
    {0x1C0, SATB_K_SUPER, 0xE5904D, 0x39},
};
static const int SATB_NOPS = (int)(sizeof(kSatbOps) / sizeof(kSatbOps[0]));
static int g_satbIcMapped, g_satbRtSwapped, g_satbColdN, g_satbNativeN;
static void __cdecl SatbN2mRefused() { SatbWGiveUp(0xD8A8E0); }  // X03
// [AutShadow] Satb=0/1/2. ini NULL: monohost / selftest set g_satbMode themselves. Returns the mode in force.
static int SatbInstall(int mode) {
    Ts3nAutShadow& k = ts3native_autshadow;
    g_satbMode = SATB_OFF, k.satbMode = 0;
    if (mode <= 0) return 0;
    if (!SatbAlloc()) { Log("FAIL autshadow satb: no memory for the log"); return 0; }
    for (int i = 0; i < SATB_NOPS; i++)
        if (g_satbTable[kSatbOps[i].op] != kSatbOps[i].handler && !McOwns(kSatbOps[i].op, g_satbTable[kSatbOps[i].op])) {  // [MonoFast] CallPath callvirt: chained
            Log("SKIP autshadow satb: jump-table entry 0x%03X is 0x%08lX, not the game's 0x%08lX (another hook owns it)", kSatbOps[i].op,
                g_satbTable[kSatbOps[i].op], kSatbOps[i].handler);
            k.satbMode = -1;
            return 0;
        }
    DWORD old;
    if (!VirtualProtect((void*)g_satbTable, SATB_NOP * 4, PAGE_EXECUTE_READWRITE, &old)) { k.satbMode = -1; return 0; }
    for (int i = 0; i < SATB_NOPS; i++) {
        const SatbOpRow& o = kSatbOps[i];
        // the entry as found: the game's handler, or [MonoFast] CallPath's callvirt (mintcall.cpp, installed before us), which the
        // sim side then keeps; the worker side never reaches it (SatbWCallvirtTail runs EA's tail without the inline cache)
        g_satbOrig[o.op] = (void*)(uintptr_t)g_satbTable[o.op], g_satbKind[o.op] = o.kind, g_satbBase[o.op] = o.base;
        g_satbMine[o.op] = o.kind == SATB_K_STORE ? (void*)SatbStoreStub : o.kind == SATB_K_LOAD ? (void*)SatbLoadStub : (void*)SatbSuperStub;
        if (o.kind == SATB_K_LOAD) {  // docs/206 §11: direct-jump thunk per opcode (the shared stub if the page is refused)
            void* t = SatbMakeLoadThunk(o.op, g_satbOrig[o.op], (void*)SatbLoadWorker, &g_satbWTidL.v, &g_satbSimLoadsL.v);
            if (t) g_satbMine[o.op] = t;
        }
    }
    Log("autshadow satb: load thunks %s", g_satbThunks ? "per opcode" : "refused (shared stub)");
    g_satbMode = mode >= 2 ? SATB_ACT : SATB_PROBE;
    k.satbMode = g_satbMode;
    g_asSatbIcFn = SatbIcSim;
    g_satbIcMapped = SatbMapIcs();
    g_mtPushExtra = SatbPushRoots;
    if (g_satbMode == SATB_ACT) g_satbColdN = SatbInstallCold(), g_mtNoFallbackHook = SatbN2mRefused, g_satbNativeN = SatbInstallNative();
    if (g_satbMode == SATB_PROBE && !g_satbTimerEv) {
        g_satbTimerEv = CreateEventW(NULL, FALSE, FALSE, NULL);
        HANDLE th = g_satbTimerEv ? CreateThread(NULL, 64 << 10, SatbTimerMain, NULL, 0, NULL) : NULL;
        if (th) CloseHandle(th); else g_satbTimerEv = NULL;
    }
    return g_satbMode;
}
