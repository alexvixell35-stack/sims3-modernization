// [GC] MarkHelpers (docs/287 §4.3, §5, §6, §7): helper pool for the Boehm mark phase (P2) and its live wiring (P3,
// ApplyGcMarkHelpers at the end). Nothing is created or patched unless [GC] MarkHelpers > 0 (default 0: no thread, no
// byte written). Included by ts3native.cpp after gcgate.cpp (ts3native_counters, UsTk, TicksSince, g_qpf, GctClear,
// PatchCalls, Matches, Write).
//
// Coordinator API, one thread only (the sim thread in P3): G5WindowEnter / G5WindowLeave around the window (0xE49F49 in
// P3), G5ParMarkFrom in place of GC_mark_from 0xE4C3E0 (cdecl (top, base, limit) -> new top; empty = top < base),
// G5Reset at a cycle restart (0xE49FDF). Helpers never write the game's mark state: a helper overflow (0xE4C390 detour,
// G5HelperOverflow) or fault only raises a flag, and the coordinator writes state 5 (+ too_small for an overflow) in
// G5ApplyLk at every G5ParMarkFrom return and after each window join (docs/287 §7, S1 fix 3).
// Entries live in four places: the coordinator's (game's) stack, the shared pool, the private stacks, and inside a running
// fn call. Invariant I1 (docs/287 §4.3): G5ParMarkFrom never returns an empty stack while the pool or a private stack
// holds an entry or a helper is inside fn. Helpers take entries only under g_g5.lock and set busy there, so "pool empty
// and no helper busy" read under the lock means closure.

struct G5E {
    DWORD start, descr;  // one mark-stack entry (8 bytes, Boehm's mse)
};
typedef G5E*(__cdecl* G5MarkFromFn)(G5E* top, G5E* base, G5E* limit);

struct G5Helper {
    G5E *base, *top, *limit;  // private stack, allocated by the helper on its first window; top = base - 1: empty
    volatile LONG busy;       // holds private entries or is inside fn; set to 1 only under g_g5.lock
    volatile LONG ovf;        // set by G5HelperOverflow on this helper (its fn dropped 512 entries); the coordinator applies it
    HANDLE th;
    DWORD tid;
};
static struct {
    G5MarkFromFn fn;
    volatile LONG *state, *tooSmall;  // the game's mark state [0x11F3078] and GC_mark_stack_too_small [0x11F307C] (P3)
    G5Helper h[4];
    LONG n, privCap, minSplit;
    G5E* pool;  // shared chunk pool (LIFO), under lock
    volatile LONG poolN;  // read without the lock as a hint only
    LONG poolCap;
    SRWLOCK lock;
    HANDLE wake;  // manual reset: set while a window is open
    volatile LONG active, inside, quit, wantDonate, avPending;
    LARGE_INTEGER winT0;
} g_g5;

static LONG G5Depth(const G5E* top, const G5E* base) { return (LONG)(top - base) + 1; }
static LONG G5ModeOn() { return *(volatile LONG*)&ts3native_counters.g5Mode; }

// under lock: moves up to n of the oldest entries of [base, top] into the pool; returns the new top
static G5E* G5DonateLk(G5E* top, G5E* base, LONG n) {
    Ts3nCounters& k = ts3native_counters;
    if (n > g_g5.poolCap - g_g5.poolN) n = g_g5.poolCap - g_g5.poolN;
    if (n <= 0) return top;
    memcpy(g_g5.pool + g_g5.poolN, base, n * sizeof(G5E));
    memmove(base, base + n, (G5Depth(top, base) - n) * sizeof(G5E));
    g_g5.poolN += n;
    k.g5Donated++;
    if (g_g5.poolN > k.g5PoolHigh) k.g5PoolHigh = g_g5.poolN;
    return top - n;
}
// under lock: pops up to n pool entries onto top; returns the new top
static G5E* G5TakeLk(G5E* top, LONG n) {
    if (n > g_g5.poolN) n = g_g5.poolN;
    g_g5.poolN -= n;
    memcpy(top + 1, g_g5.pool + g_g5.poolN, n * sizeof(G5E));
    return top + n;
}

static G5E* G5RunFn(G5Helper* h, int* av) {
    __try {
        return g_g5.fn(h->top, h->base, h->limit);
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        *av = 1;
        return h->base - 1;
    }
}

static DWORD WINAPI G5Thread(void* p) {
    G5Helper* const h = (G5Helper*)p;
    Ts3nCounters& k = ts3native_counters;
    for (;;) {
        WaitForSingleObject(g_g5.wake, INFINITE);
        if (g_g5.quit) return 0;
        InterlockedIncrement(&g_g5.inside);  // before reading active: G5WindowLeave clears active, then waits for inside == 0
        if (!g_g5.active) {
            InterlockedDecrement(&g_g5.inside);
            continue;
        }
        if (!h->base) {
            h->base = (G5E*)VirtualAlloc(NULL, g_g5.privCap * sizeof(G5E), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
            if (!h->base) {  // this helper never works; the others and the coordinator still do
                InterlockedIncrement(&k.g5Fallback);
                InterlockedDecrement(&k.g5Threads);
                InterlockedDecrement(&g_g5.inside);
                return 1;
            }
            h->top = h->base - 1, h->limit = h->base + g_g5.privCap;
        }
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        int spins = 0;
        while (g_g5.active) {
            const LONG d = G5Depth(h->top, h->base);
            if (d > 0 && G5ModeOn()) {
                int av = 0;
                h->top = G5RunFn(h, &av);
                if (av) {  // entries of this stack are lost: the coordinator writes state 5 (rescan) before the mark can end
                    h->top = h->base - 1;
                    InterlockedExchange(&g_g5.avPending, 1);
                    InterlockedIncrement(&k.g5HelperAv);
                }
                const LONG d2 = G5Depth(h->top, h->base);
                if (d2 > 1 && (d2 > g_g5.privCap / 2 || g_g5.wantDonate)) {
                    AcquireSRWLockExclusive(&g_g5.lock);
                    h->top = G5DonateLk(h->top, h->base, d2 / 2);
                    ReleaseSRWLockExclusive(&g_g5.lock);
                }
                if (h->top < h->base) InterlockedExchange(&h->busy, 0);
                spins = 0;
                continue;
            }
            if (d > 0) {  // mode 0 in an open window: hand everything back through the pool
                AcquireSRWLockExclusive(&g_g5.lock);
                h->top = G5DonateLk(h->top, h->base, d);
                ReleaseSRWLockExclusive(&g_g5.lock);
                if (h->top < h->base) InterlockedExchange(&h->busy, 0);
                else SwitchToThread();  // pool full: the coordinator drains it
                continue;
            }
            if (G5ModeOn() && g_g5.poolN) {
                int got = 0;
                AcquireSRWLockExclusive(&g_g5.lock);
                LONG n = g_g5.poolN / 2 > g_g5.minSplit ? g_g5.poolN / 2 : g_g5.minSplit;
                if (n > g_g5.privCap / 2) n = g_g5.privCap / 2;
                if (n < 1) n = 1;
                if (g_g5.poolN) h->top = G5TakeLk(h->top, n), h->busy = 1, got = 1;
                ReleaseSRWLockExclusive(&g_g5.lock);
                if (got) {
                    InterlockedIncrement(&k.g5Taken);
                    continue;
                }
            }
            if (++spins < 64) YieldProcessor();
            else SwitchToThread();
        }
        UsAdd(&k.g5HelperUs, TicksSince(t0));
        InterlockedDecrement(&g_g5.inside);
    }
}

// n helper threads (1..4), fn = the marker, state / tooSmall = the game's mark state and overflow flag (written only by
// the coordinator); privCap / poolCap in entries. Returns the number of helpers started (0 = serial only; G5ParMarkFrom
// then just calls fn). fn is set even then and never cleared (G5Shutdown keeps it), so a retargeted call site always has
// a marker; a G5Init after one that started 0 helpers tries again.
int G5Init(int n, G5MarkFromFn fn, volatile LONG* state, volatile LONG* tooSmall, LONG privCap = 65536, LONG poolCap = 65536,
           LONG minSplit = 64) {
    Ts3nCounters& k = ts3native_counters;
    if (g_g5.n) return g_g5.n;
    g_g5.fn = fn, g_g5.state = state, g_g5.tooSmall = tooSmall, g_g5.privCap = privCap, g_g5.poolCap = poolCap, g_g5.minSplit = minSplit;
    if (n < 1 || n > 4 || privCap < 4 || !state || !tooSmall) return 0;
    InitializeSRWLock(&g_g5.lock);
    g_g5.pool = (G5E*)VirtualAlloc(NULL, poolCap * sizeof(G5E), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    g_g5.wake = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!g_g5.pool || !g_g5.wake) {  // nothing half-created stays behind
        if (g_g5.pool) VirtualFree(g_g5.pool, 0, MEM_RELEASE);
        if (g_g5.wake) CloseHandle(g_g5.wake);
        g_g5.pool = NULL, g_g5.wake = NULL;
        return k.g5Fallback++, 0;
    }
    for (int i = 0; i < n; i++) {
        G5Helper& h = g_g5.h[i];
        h.th = CreateThread(NULL, 256 << 10, G5Thread, &h, STACK_SIZE_PARAM_IS_A_RESERVATION, &h.tid);
        if (!h.th) {
            k.g5Fallback++;
            break;
        }
        g_g5.n++;
    }
    k.g5Threads = g_g5.n, k.g5Ready = g_g5.n > 0, k.g5Mode = g_g5.n > 0;
    return g_g5.n;
}

static void G5Join() {  // after this no helper is inside fn or touches a private stack until the next G5WindowEnter
    InterlockedExchange(&g_g5.active, 0);
    ResetEvent(g_g5.wake);
    for (int i = 0; g_g5.inside; i++)
        if (i < 256) YieldProcessor();
        else SwitchToThread();
}
void G5WindowEnter() {
    if (!g_g5.n || g_g5.active || !G5ModeOn()) return;
    ts3native_counters.g5Windows++;
    QueryPerformanceCounter(&g_g5.winT0);
    InterlockedExchange(&g_g5.active, 1);
    SetEvent(g_g5.wake);
}
// a helper's fn call ran the game's overflow routine 0xE4C390 (through G5OvfStub): only this helper is marked. Returns 0
// on any other thread (the coordinator then runs EA's code, which writes state 5 and too_small itself).
int __cdecl G5HelperOverflow() {
    const DWORD me = GetCurrentThreadId();
    for (LONG i = 0; i < g_g5.n; i++)
        if (g_g5.h[i].tid == me) {
            InterlockedExchange(&g_g5.h[i].ovf, 1);
            InterlockedIncrement(&ts3native_counters.g5PrivOverflow);
            return 1;
        }
    return 0;
}

// coordinator only: what a helper overflow or fault means for the mark, written as EA's overflow routine writes it (state
// 5 = rescan every marked object and the roots; too_small = grow the stack). Runs at every G5ParMarkFrom return (after its
// last idle check) and after each window join, so a mark can only end after every flag raised before it was applied.
static void G5ApplyLk() {
    Ts3nCounters& k = ts3native_counters;
    LONG ov = 0;
    for (LONG i = 0; i < g_g5.n; i++) ov |= InterlockedExchange(&g_g5.h[i].ovf, 0);
    const LONG av = InterlockedExchange(&g_g5.avPending, 0);
    if (!ov && !av) return;
    if (!*g_g5.state) {  // the mark ended with entries dropped: impossible by I1; never fake a running mark, stop the helpers
        k.g5Anomaly++, InterlockedExchange(&k.g5Mode, 0);
        Log("FAIL GC mark helpers: helper %s pending after the mark ended (g5Anomaly %ld)", ov ? "overflow" : "fault", k.g5Anomaly);
        return;
    }
    *g_g5.state = 5;
    if (ov) *g_g5.tooSmall = 1;
    k.g5StateApply++;
    if (av && k.g5HelperAv >= 3 && k.g5Mode) {
        InterlockedExchange(&k.g5Mode, 0);
        Log("GC mark helpers: %ld helper faults this session, g5Mode latched 0 (serial marker, patches stay)", k.g5HelperAv);
    }
}

void G5WindowLeave() {
    if (!g_g5.active) return;
    G5Join();
    G5ApplyLk();
    ts3native_counters.g5WindowUs += UsTk(&ts3native_counters.g5WindowUs, TicksSince(g_g5.winT0));
}

// coordinator: moves pool entries (and, with no window open, parked private entries) onto top while the stack stays
// under room entries; *idle = nothing is left outside the coordinator's stack
static G5E* G5Fill(G5E* top, G5E* base, LONG room, int* idle) {
    Ts3nCounters& k = ts3native_counters;
    AcquireSRWLockExclusive(&g_g5.lock);
    LONG d = G5Depth(top, base);
    if (g_g5.poolN && d < room) top = G5TakeLk(top, room - d), d = G5Depth(top, base), k.g5HandBack++;
    int busy = 0;
    for (LONG i = 0; i < g_g5.n; i++) {
        G5Helper& h = g_g5.h[i];
        if (!g_g5.active && h.base && h.top >= h.base && d < room) {  // helpers are joined: their stacks are ours
            LONG m = G5Depth(h.top, h.base);
            if (m > room - d) m = room - d;
            memcpy(top + 1, h.top - m + 1, m * sizeof(G5E));
            top += m, h.top -= m, d += m, k.g5HandBack++;
            if (h.top < h.base) h.busy = 0;
        }
        busy |= h.busy;
    }
    *idle = !g_g5.poolN && !busy;
    ReleaseSRWLockExclusive(&g_g5.lock);
    return top;
}

G5E* __cdecl G5ParMarkFrom(G5E* top, G5E* base, G5E* limit) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_g5.n) return g_g5.fn(top, base, limit);
    const LONG cap = (LONG)(limit - base), room = cap - (cap > 1024 ? 512 : cap / 2);  // keep the game's 512-entry overflow drop free
    int idle;
    if (g_g5.active && G5ModeOn()) {
        const LONG d = G5Depth(top, base);
        if (d > g_g5.minSplit && g_g5.poolN < g_g5.minSplit * g_g5.n) {
            AcquireSRWLockExclusive(&g_g5.lock);
            top = G5DonateLk(top, base, d / 2);
            ReleaseSRWLockExclusive(&g_g5.lock);
        }
    } else
        top = G5Fill(top, base, room, &idle);  // mode 0 or no window: hand back what fits first
    top = g_g5.fn(top, base, limit);
    if (top >= base) return G5ApplyLk(), top;
    LARGE_INTEGER t0 = {};
    for (int i = 0;; i++) {  // I1: empty only when nothing is left outside
        top = G5Fill(top, base, room, &idle);
        if (top >= base || idle) break;
        if (!t0.QuadPart) QueryPerformanceCounter(&t0), k.g5SimWaits++;
        InterlockedExchange(&g_g5.wantDonate, 1);
        if (i < 64) YieldProcessor();
        else SwitchToThread();
    }
    if (t0.QuadPart) InterlockedExchange(&g_g5.wantDonate, 0), k.g5SimWaitUs += UsTk(&k.g5SimWaitUs, TicksSince(t0));
    G5ApplyLk();
    return top;
}

// joins, drops every pool and private entry and every pending helper flag, reopens; returns what was dropped (entries + flags)
static LONG G5Drop() {
    const LONG was = g_g5.active;
    if (was) G5Join();
    AcquireSRWLockExclusive(&g_g5.lock);
    LONG n = g_g5.poolN + InterlockedExchange(&g_g5.avPending, 0);
    g_g5.poolN = 0;
    for (LONG i = 0; i < g_g5.n; i++) {
        G5Helper& h = g_g5.h[i];
        if (h.base) n += G5Depth(h.top, h.base), h.top = h.base - 1;
        n += InterlockedExchange(&h.ovf, 0);
        h.busy = 0;
    }
    ReleaseSRWLockExclusive(&g_g5.lock);
    if (was) InterlockedExchange(&g_g5.active, 1), SetEvent(g_g5.wake);
    return n;
}
// cycle restart (the GC_clear_marks site): drop every pool and private entry
void G5Reset() {
    if (!g_g5.n) return;
    G5Drop();
    ts3native_counters.g5Resets++;
}

// entries outside the coordinator's stack plus busy helpers (I1 instrumentation; exact only with helpers idle or joined)
LONG G5OutsideN() {
    AcquireSRWLockExclusive(&g_g5.lock);
    LONG n = g_g5.poolN;
    for (LONG i = 0; i < g_g5.n; i++) n += g_g5.h[i].busy + (g_g5.h[i].base ? G5Depth(g_g5.h[i].top, g_g5.h[i].base) : 0);
    ReleaseSRWLockExclusive(&g_g5.lock);
    return n;
}

// stops and frees everything except fn (selftest; never from DllMain: it waits for threads)
void G5Shutdown() {
    if (!g_g5.fn) return;
    const G5MarkFromFn fn = g_g5.fn;
    G5WindowLeave();
    InterlockedExchange(&g_g5.quit, 1);
    if (g_g5.wake) SetEvent(g_g5.wake);
    for (LONG i = 0; i < 4; i++)
        if (g_g5.h[i].th) {
            WaitForSingleObject(g_g5.h[i].th, INFINITE);
            CloseHandle(g_g5.h[i].th);
            if (g_g5.h[i].base) VirtualFree(g_g5.h[i].base, 0, MEM_RELEASE);
        }
    if (g_g5.wake) CloseHandle(g_g5.wake);
    if (g_g5.pool) VirtualFree(g_g5.pool, 0, MEM_RELEASE);
    memset(&g_g5, 0, sizeof g_g5);
    g_g5.fn = fn;
    ts3native_counters.g5Ready = ts3native_counters.g5Threads = ts3native_counters.g5Mode = 0;
}

// ---- P3: live wiring, [GC] MarkHelpers=N (docs/287 §7; default 0 = nothing created, nothing written) ----
// Steam 1.67 sites, all byte-checked before the first write (one mismatch: nothing written, SKIP, g5Fallback):
// - 8 mark-bit sites [verified, S2]: `mov ecx,[edi+edx*4+1Ch]; test ecx,eax; jne skip; or eax,ecx; mov [edi+edx*4+1Ch],eax`
//   (edi = header, esi = bit index from hdr+0x1C, eax = 1 << (esi & 31), edx = esi >> 5) -> `lock bts [edi+1Ch],esi;
//   jc skip` + 7 nops (rel8 + 1: the jc ends one byte earlier). eax / ecx / flags are dead on both paths at all 8.
// - 2 more in-window mark-word writers [verified, S1]: GC_mark_and_push_stack 0xE4CE3D (root pushes, also MonoThread's
//   GC_push_one) and GC_set_mark_bit 0xE4C17E -> `lock or`, same eax / ecx / flags after.
// - GC_signal_mark_stack_overflow 0xE4C390 [verified bytes]: `cmp [GC_print_stats],0; mov [state],5; mov [too_small],1;
//   je; (log); mov eax,[esp+4]; add eax,-1000h; ret`. Detour G5OvfStub: on a helper only the flag (G5HelperOverflow) and
//   the same return value; on any other thread EA's code from 0xE4C397 with the flags of the replayed cmp.
// - call retargets: GC_mark_from 0xE4C3E0 at 0xE4D2B7 / 0xE4D33B (cdecl top, base, limit) -> G5ParMarkFrom;
//   alloc_mark_stack 0xE4C950 (size in edi) at 0xE4D3A3 (mark done) / 0xE4D3FF (state 5 rescan start) -> quiesce first
//   (S3 b: the old array is freed; drop the pool, exact in state 5); GC_clear_marks at 0xE49FDF (cycle restart; chained
//   after [GC] SubTimers' GctClear) -> G5Reset first; last the window: GC_collect_a_little_inner 0xE49DA0 (cdecl n, stop)
//   at 0xE49F49 -> G5Little.
struct G5BitSite {
    DWORD at;
    u8 rel;  // EA's jne rel8
};
static const G5BitSite kG5BitSites[8] = {{TS3A(0x00E4C5F9), 0x3B}, {TS3A(0x00E4C797), 0x39}, {TS3A(0x00E4C8E0), 0x39},
                                         {TS3A(0x00E4CD21), 0xC4}, {TS3A(0x00E506B9), 0x30}, {TS3A(0x00E5085D), 0x30},
                                         {TS3A(0x00E509C9), 0x30}, {TS3A(0x00E50B43), 0x30}};
static void G5SiteOld(u8 rel, u8* out) {
    const u8 b[14] = {0x8B, 0x4C, 0x97, 0x1C, 0x85, 0xC1, 0x75, rel, 0x0B, 0xC1, 0x89, 0x44, 0x97, 0x1C};
    memcpy(out, b, 14);
}
static void G5SiteNew(u8 rel, u8* out) {
    const u8 b[14] = {0xF0, 0x0F, 0xAB, 0x77, 0x1C, 0x72, (u8)(rel + 1), 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90};
    memcpy(out, b, 14);
}
static const u8 kG5PushOld[11] = {0xB8, 0x01, 0x00, 0x00, 0x00, 0xD3, 0xE0, 0x09, 0x44, 0x96, 0x1C};  // 0xE4CE3D
static const u8 kG5PushNew[11] = {0x31, 0xC0, 0x40, 0xD3, 0xE0, 0xF0, 0x09, 0x44, 0x96, 0x1C, 0x90};
static const u8 kG5SetOld[5] = {0x5E, 0x09, 0x10, 0xC3, 0xCC};  // 0xE4C17E (the int3 pad before 0xE4C190 takes the ret)
static const u8 kG5SetNew[5] = {0x5E, 0xF0, 0x09, 0x10, 0xC3};
static const u8 kG5OvfTail[10] = {0x8B, 0x44, 0x24, 0x04, 0x05, 0x00, 0xF0, 0xFF, 0xFF, 0xC3};  // 0xE4C3CA

static DWORD g_g5OvfResume, g_g5OvfPrintStats, g_g5GrowOrig;
static __declspec(naked) void G5OvfStub() {
    __asm {
        push eax
        push ecx
        push edx
        call G5HelperOverflow
        test eax, eax
        pop edx
        pop ecx
        pop eax
        jnz helper
        push eax
        mov eax, g_g5OvfPrintStats
        cmp dword ptr [eax], 0
        pop eax
        jmp dword ptr [g_g5OvfResume]
    helper:
        mov eax, dword ptr [esp + 4]
        sub eax, 0x1000
        ret
    }
}

// before alloc_mark_stack: stop the helpers and drop every entry outside the game's stack. At the state-5 site the rescan
// that follows re-pushes every marked object; at the mark-done site I1 says nothing is outside (else g5Anomaly).
static void __cdecl G5Quiesce(int doneSite) {
    if (!g_g5.n) return;
    Ts3nCounters& k = ts3native_counters;
    if (!G5Drop()) return;
    k.g5GrowDrop++;
    if (doneSite) {  // I1 broken: the mark already ended; stop the helpers for the session
        k.g5Anomaly++, InterlockedExchange(&k.g5Mode, 0);
        Log("FAIL GC mark helpers: entries outside at the mark-done stack growth (g5Anomaly %ld), g5Mode latched 0", k.g5Anomaly);
    }

}
static __declspec(naked) void G5GrowDone() {
    __asm {
        pushad
        push 1
        call G5Quiesce
        add esp, 4
        popad
        jmp dword ptr [g_g5GrowOrig]
    }
}
static __declspec(naked) void G5GrowInvalid() {
    __asm {
        pushad
        push 0
        call G5Quiesce
        add esp, 4
        popad
        jmp dword ptr [g_g5GrowOrig]
    }
}

typedef int(__cdecl* G5LittleFn)(int, void*);
typedef int(__cdecl* G5ClearFn)();
static G5LittleFn g_g5LittleOrig;
static G5ClearFn g_g5ClearNext;

// ---- P4: [GC] MarkHelpersVerify=N (docs/287 §8; debug, default 0 = no code below runs; correctness queues only) ----
// Every Nth incremental cycle, right after the collect-a-little call whose stop-the-world finish completed (finish
// pending [0x11F2F94] 0 -> 1 at 0xE49EEC, mark state 0: the cycle's marks M_par are final, nothing is swept, no managed
// code ran since; notify event 3 can come a tick later, after allocations), with the world stopped again: from cleared
// marks one full mark with a helper window (M_par2) and one without (M_ser), each on a scrubbed stack.
// - M_par2 != M_ser: g5VerifyBad, g5Mode latched 0 (the helper marker is not exact).
// - M_ser not inside M_par: g5VerifySub, no latch (the cycle missed a live object, or a conservative root differs).
// Marks, incomplete black lists and mark globals are restored; the mark stack may have grown (the game's own growth).
// Inside the stopped world: no log, no heap call, no lock a suspended thread could hold.
struct G5VEnv {
    const DWORD* biHead;                          // GC_all_bottom_indices [0x11F3054]: index[1024], +0x1000 next, +0x1008 key
    const DWORD* invalidMap;                      // [0x11F30D8]: hdr+0x10 of a free block
    volatile LONG *scanPtr, *objMarked, *nResc;   // [0x11F3070], [0x11F3080], [0x123433C]
    volatile LONG *worldStopped, *finishPending;  // [0x11F2FA8], [0x11F2F94]
    G5E **stkBase, **stkTop;                      // [0x1234338], [0x1234340] (empty: top = base - 1)
    u8** bl[2];                                   // incomplete black lists [0x1234350] / [0x1234358], 0x2000 bytes each
    int(__cdecl* markSome)(void* cold);           // GC_mark_some 0xE4D460 (SEH wrapper), 1 = mark done
    void(__cdecl* stopWorld)();                   // 0xE511A0
    void(__cdecl* startWorld)();                  // 0xE51290
};
struct G5VRec {
    DWORD *hdr, blk;
    DWORD par[32], par2[32];  // hdr+0x1C mark words: the cycle's, the helper mark's
};
struct G5VDiff {
    DWORD blk, hdr, sz, kind, word, a, b;
};
static G5VEnv g_g5v;
static LONG g_g5vEvery, g_g5vCycle;
static LONG g_g5vMaxMs = 60000;  // time limit of one full mark inside a check

// used blocks as GC_apply_to_all_blocks 0xE4B850 finds them (entries <= 0xFFF: nil / forwarding; free: hb_map invalid).
// r NULL: count only. -1: more than cap.
static LONG G5VBlocks(G5VRec* r, LONG cap) {
    LONG n = 0;
    for (const DWORD* bi = (const DWORD*)(uintptr_t)*g_g5v.biHead; bi; bi = (const DWORD*)(uintptr_t)bi[0x400])
        for (DWORD j = 0; j < 0x400; j++) {
            DWORD* const h = (DWORD*)(uintptr_t)bi[j];
            if (bi[j] <= 0xFFF || h[4] == *g_g5v.invalidMap) continue;
            if (r) {
                if (n >= cap) return -1;
                r[n].hdr = h, r[n].blk = (bi[0x402] << 10 | j) << 12;
            }
            n++;
        }
    return n;
}
static bool G5VUncollectable(const DWORD* hdr) { return (((const u8*)hdr)[0x14] & 0xFE) == 2; }  // kinds 2 / 3: marks never cleared (0xE4C117)
// zeroes the stack the marker's frames are about to use, so its uninitialised slots hold no old pointer (conservative scan)
static __declspec(noinline) void G5VScrub() {
    volatile char pad[32768];
    for (int i = 0; i < (int)sizeof pad; i++) pad[i] = 0;
}
// one full mark from cleared marks (what GC_clear_marks 0xE4C270 + GC_invalidate_mark_state 0xE4C370 set up: state 5,
// objects_are_marked 0 -> uncollectable blocks, then all roots). 0 = gave up (time limit): nothing is left on any stack.
static __declspec(noinline) int G5VMark(const G5VRec* r, LONG n, int helpers) {
    for (LONG i = 0; i < n; i++)
        if (G5VUncollectable(r[i].hdr)) memcpy(r[i].hdr + 7, r[i].par, 0x80);
        else memset(r[i].hdr + 7, 0, 0x80);
    *g_g5v.scanPtr = 0, *g_g5v.objMarked = 0, *g_g5.state = 5;
    G5VScrub();
    if (helpers) G5WindowEnter();
    const DWORD t0 = GetTickCount();
    void* cold = NULL;
    int done = 0;
    for (LONG i = 1; (done = g_g5v.markSome(&cold)) == 0; i++)
        if (!(i & 0x3FF) && GetTickCount() - t0 > (DWORD)g_g5vMaxMs) break;
    if (helpers) G5WindowLeave();
    if (done && !*g_g5.state && *g_g5v.stkTop == *g_g5v.stkBase - 1 && !(g_g5.n && G5OutsideN())) return 1;
    if (g_g5.n) G5Drop();
    *g_g5v.stkTop = *g_g5v.stkBase - 1;
    return 0;
}
static void G5Verify() {
    Ts3nCounters& k = ts3native_counters;
    const G5VEnv& e = g_g5v;
    if (*g_g5.state || *e.stkTop != *e.stkBase - 1 || (g_g5.n && G5OutsideN())) {  // not a finished mark: nothing to compare
        k.g5VerifySkip++;
        return;
    }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    LONG n = -1;
    __try {
        n = G5VBlocks(NULL, 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const LONG cap = n + 64;
    G5VRec* const r = n < 0 ? NULL : (G5VRec*)VirtualAlloc(NULL, (SIZE_T)cap * sizeof(G5VRec) + 0x4000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!r) {
        k.g5VerifySkip++;
        Log("GC mark verify: skipped (%s)", n < 0 ? "block list unreadable" : "no memory for the mark copies");
        return;
    }
    u8* const blSave = (u8*)(r + cap);
    G5VDiff d[16];
    LONG nBad = 0, nSub = 0;
    volatile LONG saved = 0, ok = 0;  // saved = blocks copied + 1 (read after a fault)
    e.stopWorld();
    *e.worldStopped = 1;
    const LONG g[4] = {*e.scanPtr, *e.objMarked, *e.nResc, *g_g5.tooSmall};
    __try {
        n = G5VBlocks(r, cap);
        if (n >= 0) {
            for (LONG i = 0; i < n; i++) memcpy(r[i].par, r[i].hdr + 7, 0x80);
            for (int b = 0; b < 2; b++) memcpy(blSave + b * 0x2000, *e.bl[b], 0x2000);
            saved = n + 1;
            if (G5VMark(r, n, 1)) {
                for (LONG i = 0; i < n; i++) memcpy(r[i].par2, r[i].hdr + 7, 0x80);
                if (G5VMark(r, n, 0)) {
                    for (LONG i = 0; i < n; i++) {
                        const DWORD* const ser = r[i].hdr + 7;
                        const DWORD kind = ((const u8*)r[i].hdr)[0x14];
                        for (DWORD w = 0; w < 32; w++) {
                            if (ser[w] != r[i].par2[w] && nBad++ < 8) d[nBad - 1] = {r[i].blk, (DWORD)(uintptr_t)r[i].hdr, r[i].hdr[0], kind, w, r[i].par2[w], ser[w]};
                            if ((ser[w] & ~r[i].par[w]) && nSub++ < 8) d[8 + nSub - 1] = {r[i].blk, (DWORD)(uintptr_t)r[i].hdr, r[i].hdr[0], kind, w, r[i].par[w], ser[w]};
                        }
                    }
                    ok = 1;
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ok = 0;
    }
    if (saved) {  // back to the cycle's result, whatever happened above
        if (!ok && g_g5.n) G5WindowLeave(), G5Drop();
        for (LONG i = 0; i < saved - 1; i++) memcpy(r[i].hdr + 7, r[i].par, 0x80);
        for (int b = 0; b < 2; b++) memcpy(*e.bl[b], blSave + b * 0x2000, 0x2000);
        *e.stkTop = *e.stkBase - 1;
        *g_g5.state = 0, *e.scanPtr = g[0], *e.objMarked = g[1], *e.nResc = g[2], *g_g5.tooSmall = g[3];
    }
    *e.worldStopped = 0;
    e.startWorld();
    VirtualFree(r, 0, MEM_RELEASE);
    const LONG ms = UsSince(t0) / 1000;
    if (!ok) {
        k.g5VerifySkip++;
        Log("GC mark verify: gave up after %ld ms (%s), marks restored", ms, saved ? "time limit or fault" : "block list changed or unreadable");
        return;
    }
    k.g5VerifyN++;
    if (nSub) k.g5VerifySub++;
    if (nBad) k.g5VerifyBad++;
    Log("%s GC mark verify %ld: %ld blocks, %ld ms, g5Mode %ld; helper mark vs serial mark: %ld words differ; serial mark outside the cycle's "
        "marks: %ld words",
        nBad ? "FAIL" : nSub ? "WARN" : "OK  ", k.g5VerifyN, n, ms, k.g5Mode, nBad, nSub);
    for (LONG i = 0; i < 16; i++)
        if (i < 8 ? i < nBad : i - 8 < nSub)
            Log("  %s: block 0x%08lX hdr 0x%08lX kind %lu size %lu words, mark word %lu: %s 0x%08lX, serial 0x%08lX", i < 8 ? "differ" : "outside",
                d[i].blk, d[i].hdr, d[i].kind, d[i].sz, d[i].word, i < 8 ? "helper" : "cycle", d[i].a, d[i].b);
    if (nBad && InterlockedExchange(&k.g5Mode, 0)) Log("FAIL GC mark helpers: verify mismatch, g5Mode latched 0 (serial marker, patches stay)");
}

// 0xE49F49: the one call that continues a running mark (S3): helpers work only while it runs
static int __cdecl G5Little(int n, void* stop) {
    const LONG pend0 = g_g5vEvery ? *g_g5v.finishPending : 1;
    if (*g_g5.state) G5WindowEnter();  // no-op with g5Mode 0
    const int r = g_g5LittleOrig(n, stop);
    G5WindowLeave();  // joins, then applies any helper overflow / fault (state 5)
    if (!pend0 && *g_g5v.finishPending && ++g_g5vCycle % g_g5vEvery == 0) G5Verify();  // P4: this call finished a cycle's mark
    return r;
}
static int __cdecl G5Clear() {
    G5Reset();
    return g_g5ClearNext();
}

static bool G5CallIs(DWORD site, DWORD target) {
    const u8* p = (const u8*)(uintptr_t)site;
    return Readable(p, 5) && p[0] == 0xE8 && (DWORD)(site + 5 + *(const LONG*)(p + 1)) == target;
}

// [GC] MarkHelpers=N (1..4 helper threads, default 0), MarkHelpersMinSplit (entries, default 64). g5Mode is the run-time
// switch (--ab gcmarkhelpers): 0 = serial marker through the same patched code (atomic bits).
static bool ApplyGcMarkHelpers(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const int want = GetPrivateProfileIntW(L"GC", L"MarkHelpers", 0, ini);
    if (want <= 0) { Log("GC mark helpers off (MarkHelpers=0)"); return false; }
    const int n = Clamp(want, 1, 4);
    LONG every = Clamp(GetPrivateProfileIntW(L"GC", L"MarkHelpersVerify", 0, ini), 0, 1000000);
    const LONG minSplit = Clamp(GetPrivateProfileIntW(L"GC", L"MarkHelpersMinSplit", 64, ini), 8, 4096);
    if (GetPrivateProfileIntW(L"MonoThread", L"Enabled", 0, ini) && GetPrivateProfileIntW(L"MonoThread", L"Mode", 0, ini) >= 1 &&
        !GetPrivateProfileIntW(L"MonoThread", L"GcLock", 0, ini)) {
        k.g5Fallback++;
        Log("SKIP GC mark helpers: [MonoThread] worker without its GC lock gate (GcLock=0), docs/287 §7");
        return false;
    }
    // every byte check first
    DWORD badAt = 0;
    const char* bad = NULL;
    for (const G5BitSite& s : kG5BitSites) {
        u8 old[14];
        G5SiteOld(s.rel, old);
        if (!bad && !Matches((void*)(uintptr_t)s.at, old, 14)) bad = "mark-bit site", badAt = s.at;
    }
    const DWORD push = TS3A(0x00E4CE3D), set = TS3A(0x00E4C17E), ovf = TS3A(0x00E4C390);
    if (!bad && !Matches((void*)(uintptr_t)push, kG5PushOld, sizeof kG5PushOld)) bad = "GC_mark_and_push_stack", badAt = push;
    if (!bad && !Matches((void*)(uintptr_t)set, kG5SetOld, sizeof kG5SetOld)) bad = "GC_set_mark_bit", badAt = set;
    const DWORD ps = TS3A(0x011F30B4), st = TS3A(0x011F3078), ts = TS3A(0x011F307C);
    u8 head[29] = {0x83, 0x3D, 0, 0, 0, 0, 0x00, 0xC7, 0x05, 0, 0, 0, 0, 0x05, 0, 0, 0, 0xC7, 0x05, 0, 0, 0, 0, 0x01, 0, 0, 0, 0x74, 0x1D};
    memcpy(head + 2, &ps, 4), memcpy(head + 9, &st, 4), memcpy(head + 19, &ts, 4);  // this build's data addresses (retail differs)
    if (!bad && !(Readable((void*)(uintptr_t)ovf, 0x44) && !memcmp((void*)(uintptr_t)ovf, head, sizeof head) &&
                  !memcmp((void*)(uintptr_t)(ovf + 0x3A), kG5OvfTail, sizeof kG5OvfTail)))
        bad = "GC_signal_mark_stack_overflow", badAt = ovf;
    const DWORD markFrom = TS3A(0x00E4C3E0), grow = TS3A(0x00E4C950), little = TS3A(0x00E49DA0), clearAt = TS3A(0x00E49FDF);
    const u8* cp = (const u8*)(uintptr_t)clearAt;
    const DWORD clearCur = Readable(cp, 5) && cp[0] == 0xE8 ? (DWORD)(clearAt + 5 + *(const LONG*)(cp + 1)) : 0;
    const CallSite calls[] = {{TS3A(0x00E4D2B7), markFrom, (void*)G5ParMarkFrom}, {TS3A(0x00E4D33B), markFrom, (void*)G5ParMarkFrom},
                              {TS3A(0x00E4D3A3), grow, (void*)G5GrowDone},        {TS3A(0x00E4D3FF), grow, (void*)G5GrowInvalid},
                              {clearAt, clearCur, (void*)G5Clear},               {TS3A(0x00E49F49), little, (void*)G5Little}};  // window last
    if (!bad && clearCur != TS3A(0x00E4C270) && clearCur != (DWORD)(uintptr_t)GctClear) bad = "GC_clear_marks call", badAt = clearAt;
    for (const CallSite& c : calls)
        if (!bad && !G5CallIs(c.site, c.target)) bad = "call site", badAt = c.site;
    volatile LONG* const state = (volatile LONG*)(uintptr_t)st;
    if (!bad && !(Readable((const void*)state, 4) && *state == 0)) bad = "mark state not idle at install", badAt = st;
    if (bad) {
        k.g5Fallback++;
        Log("SKIP GC mark helpers: %s differs at 0x%08lX, nothing written", bad, badAt);
        return false;
    }
    if (every) {  // P4: the three functions as the game's own stopped mark calls them, and the finish-pending store
        const DWORD markSome = TS3A(0x00E4D460), stopW = TS3A(0x00E511A0), startW = TS3A(0x00E51290), pend = TS3A(0x011F2F94);
        u8 store[5] = {0xA3};  // 0xE49EEC mov [finish pending],eax
        memcpy(store + 1, &pend, 4);
        const G5VEnv v = {(const DWORD*)(uintptr_t)TS3A(0x011F3054), (const DWORD*)(uintptr_t)TS3A(0x011F30D8),
                          (volatile LONG*)(uintptr_t)TS3A(0x011F3070), (volatile LONG*)(uintptr_t)TS3A(0x011F3080),
                          (volatile LONG*)(uintptr_t)TS3A(0x0123433C), (volatile LONG*)(uintptr_t)TS3A(0x011F2FA8),
                          (volatile LONG*)(uintptr_t)pend, (G5E**)(uintptr_t)TS3A(0x01234338), (G5E**)(uintptr_t)TS3A(0x01234340),
                          {(u8**)(uintptr_t)TS3A(0x01234350), (u8**)(uintptr_t)TS3A(0x01234358)},
                          (int(__cdecl*)(void*))(uintptr_t)markSome, (void(__cdecl*)())(uintptr_t)stopW, (void(__cdecl*)())(uintptr_t)startW};
        if (G5CallIs(TS3A(0x00E499D5), markSome) && G5CallIs(TS3A(0x00E49981), stopW) && G5CallIs(TS3A(0x00E49A3E), startW) &&
            Matches((void*)(uintptr_t)TS3A(0x00E49EEC), store, sizeof store) && Readable(v.biHead, 4) && Readable(v.invalidMap, 4) &&
            Readable(v.stkBase, 12) && Readable(v.bl[0], 12) && Readable((const void*)v.nResc, 4))
            g_g5v = v;
        else {
            every = 0, k.g5Fallback++;
            Log("SKIP GC mark verify ([GC] MarkHelpersVerify): a stopped-mark call site differs, the helpers install without it");
        }
    }
    // threads and stacks first, then the mark-word patches (no mark runs before the game's first tick), then the calls
    if (G5Init(n, (G5MarkFromFn)(uintptr_t)markFrom, state, (volatile LONG*)(uintptr_t)ts, 65536, 65536, minSplit) <= 0) {
        k.g5Fallback++;
        Log("SKIP GC mark helpers: no helper thread started, nothing written");
        return false;
    }
    k.g5Ready = 0;
    g_g5OvfResume = ovf + 7, g_g5OvfPrintStats = ps, g_g5GrowOrig = grow;
    g_g5LittleOrig = (G5LittleFn)(uintptr_t)little, g_g5ClearNext = (G5ClearFn)(uintptr_t)clearCur;
    g_g5vEvery = every, g_g5vCycle = 0;
    u8 det[7] = {0xE9, 0, 0, 0, 0, 0x90, 0x90};
    const LONG rel = (LONG)((uintptr_t)G5OvfStub - (ovf + 5));
    memcpy(det + 1, &rel, 4);
    DWORD failAt = 0;
    for (const G5BitSite& s : kG5BitSites) {
        u8 nb[14];
        G5SiteNew(s.rel, nb);
        if (!failAt && !Write((void*)(uintptr_t)s.at, nb, 14)) failAt = s.at;
    }
    if (!failAt && !Write((void*)(uintptr_t)push, kG5PushNew, sizeof kG5PushNew)) failAt = push;
    if (!failAt && !Write((void*)(uintptr_t)set, kG5SetNew, sizeof kG5SetNew)) failAt = set;
    if (!failAt && !Write((void*)(uintptr_t)ovf, det, sizeof det)) failAt = ovf;
    if (failAt || !PatchCalls("GC mark helpers call sites", calls, _countof(calls))) {
        // what was written is the serial-equivalent atomic form; no window opens without the 0xE49F49 retarget
        InterlockedExchange(&k.g5Mode, 0), k.g5Fallback++;
        Log("FAIL GC mark helpers: write failed at 0x%08lX (err %lu); g5Mode 0, serial marker", failAt, GetLastError());
        return true;
    }
    k.g5Ready = 1;
    Log("OK   GC mark helpers: MarkHelpers=%d (threads %ld) MinSplit=%ld Verify=%ld g5Mode=%ld: 8 mark-bit sites lock bts, 2 lock or, overflow "
        "deferral 0xE4C390, 6 call sites (0xE49FDF %s)",
        want, k.g5Threads, minSplit, every, k.g5Mode, clearCur == TS3A(0x00E4C270) ? "direct" : "chained after SubTimers");
    return true;
}
