// [MonoJit] Time=1 (game, Mode=2 only) / MONOHOST_JITTIME=1 (host, MONOHOST_JIT=verify): paired timing of the verify passes
// (docs/handoff/jit-paired-timing.md). A verified call already runs twice from the same state: pass 1 compiled, pass 2 EA's
// interpreter (JitVerifySlow). This header times both passes with rdtsc and sums them per compiled method, so one session gives
// "compiled vs interpreted, same call, same arguments" for every method the verifier samples.
//   Window: pass 1 = from the end of the fresh-call snapshot to the compiled ret's verifier entry; pass 2 = from the restore to
//   the interpreted ret's verifier entry. Cycles spent inside JitVerifySlow during a window (nested entries, its own tail) are
//   subtracted (g_jtOver). Not subtracted: the stub + JitEnter probe around each verifier entry (~a few dozen cycles; every
//   position of a compiled method is a patched word, so pass 2 pays it once per interpreted instruction). The rows carry the
//   entry counts per pass (ent1, ent2) so tools/jit-time.ts subtracts a per-entry constant (--stub-cycles, calibrated in the host).
//   Order bias: pass 2 always runs second (warm caches, same objects just touched), which understates the gain; the verify design
//   cannot swap the order (pass 2 is the reference the compare and the retry rely on), so it is measured in the host instead.
// Includer contract (monojit.cpp, monohost-jit.cpp): JitMeth with `name`, g_jitMeth, g_jitMethN, JIT_VD defined; JT_NL ("" game,
// "\n" host); JtSnap(LONG s[JT_TAGS]) fills the compile counters per tag; JT_CALLS(i) = total calls of method i (or 0).
// Off (g_jt == NULL): JitVerifySlow pays one test; Mode=1 pays nothing (the verifier is not on its path).
#include <intrin.h>
#if !defined(JT_NL) || !defined(JT_CALLS)
#error define JT_NL and JT_CALLS(i) (and JtSnap) before including monojit-time.h
#endif

static const int JT_TAGS = 8, JT_VD = 64, JT_LINES = 3000;
// tag bits: what the compile of a method used (counters that moved during its JitCompile, plus the clause / lazy-body facts).
// Direct = lazy call sites, Guard = guarded lazy sites (where BoundCall acts when on), Clause = exception clauses (Wide/ClauseLazy;
// never verified, so never a timed root), LazyBody = it got a lazy body (a Direct callee: its calls inside a caller's tree are
// timed in the caller's row). VtOps / NewVt / Ldsflda / CallExit have no compile counter: not tagged.
static const char* const kJtTag[JT_TAGS] = {"Direct", "Guard", "Inline", "Wide", "RetTd", "Clause", "LazyBody", "FastCopy"};
struct JtRow { unsigned __int64 jit, interp, ent1, ent2; LONG n, drop, nest; DWORD tags; volatile LONG guards; };  // guards: lazy-site guard sequences run (every call, compiled code)
struct JtPass { unsigned __int64 t0, o0, t2, o2, jit; LONG e0, e1, e2, n0, nest; };
static JtRow* g_jt;                        // one row per method table slot; NULL = timing off
static int g_jtCap;
static JtPass g_jtP[JT_VD];                // per verified depth (the verifier's g_jitV index)
static unsigned __int64 g_jtOver, g_jtStart;  // cycles inside the verifier so far; where the current entry's uncounted part starts
static LONG g_jtEnt, g_jtNest, g_jtDrop, g_jtWrap, g_jtReports;
static LONG g_jtSnap0[JT_TAGS];
static LARGE_INTEGER g_jtQ0;
static unsigned __int64 g_jtT0;
static DWORD g_jtLastTick;
// shortcut: a fixed cut (~40 ms at 2.5 GHz) drops passes hit by a GC or a page-fault burst; calibrate per machine if it drops many
static const unsigned __int64 JT_DROP = 100000000ull;
static void JtSnap(LONG* s);

static bool JtOn(int cap) {
    g_jt = (JtRow*)VirtualAlloc(NULL, sizeof(JtRow) * cap, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!g_jt) return false;
    g_jtCap = cap;
    QueryPerformanceCounter(&g_jtQ0);
    g_jtT0 = __rdtsc();
    g_jtLastTick = GetTickCount();
    return true;
}
// compile: tag the method that JitCompile just added (index meth) with the counters that moved since JtTagBegin
static void JtTagBegin() { if (g_jt) JtSnap(g_jtSnap0); }
static void JtTagEnd(int meth, DWORD extra) {
    if (!g_jt || meth < 0 || meth >= g_jtCap) return;
    LONG s[JT_TAGS];
    JtSnap(s);
    DWORD t = extra;
    for (int i = 0; i < JT_TAGS; i++) if (s[i] != g_jtSnap0[i]) t |= 1u << i;
    g_jt[meth].tags = t;
}
// verifier hooks (sim / main thread only, like the verifier). Pass ends read g_jtStart = this verifier entry's first cycle.
static void JtPass1Start(DWORD d) {
    if (!g_jt) return;
    JtPass& p = g_jtP[d];
    p.t0 = g_jtStart = __rdtsc(), p.o0 = g_jtOver, p.e0 = g_jtEnt, p.n0 = g_jtNest;
}
static void JtPass1End(DWORD d) {
    if (!g_jt) return;
    JtPass& p = g_jtP[d];
    p.jit = g_jtStart - p.t0 - (g_jtOver - p.o0), p.e1 = g_jtEnt - p.e0, p.nest = g_jtNest - p.n0;
}
static void JtPass2Start(DWORD d) {
    if (!g_jt) return;
    JtPass& p = g_jtP[d];
    p.t2 = g_jtStart = __rdtsc(), p.o2 = g_jtOver, p.e2 = g_jtEnt;
}
// a verified-equal call: add its pair to the method's row
static void JtPass2End(DWORD d, int meth) {
    if (!g_jt || meth < 0 || meth >= g_jtCap) return;
    const JtPass& p = g_jtP[d];
    const unsigned __int64 interp = g_jtStart - p.t2 - (g_jtOver - p.o2);
    JtRow& w = g_jt[meth];
    if (p.jit > JT_DROP || interp > JT_DROP) { w.drop++, g_jtDrop++, g_jtWrap += (p.jit | interp) >> 62 != 0; return; }  // wrap: a window shorter than its subtracted verifier time (a bug)
    w.jit += p.jit, w.interp += interp, w.ent1 += (unsigned)p.e1, w.ent2 += (unsigned)(g_jtEnt - p.e2), w.nest += p.nest, w.n++;
}
static double JtTscKhz() {
    LARGE_INTEGER q, f;
    QueryPerformanceCounter(&q), QueryPerformanceFrequency(&f);
    const double s = (double)(q.QuadPart - g_jtQ0.QuadPart) / (double)f.QuadPart;
    return s > 0 ? (double)(__rdtsc() - g_jtT0) / s / 1000.0 : 0;
}
// the report: `jittime: begin`, one `jittime: m` row per method with a timed pair, `jittime: end`. Cumulative (the reader takes
// the last complete block). m row: index tags(hex) calls samples jitCycles interpCycles nested ent1 ent2 dropped guards name
static void JtReportTo(const char* why) {
    LARGE_INTEGER q, f;
    QueryPerformanceCounter(&q), QueryPerformanceFrequency(&f);
    const unsigned long ms = (unsigned long)((q.QuadPart - g_jtQ0.QuadPart) * 1000 / f.QuadPart);
    LONG n = 0, rows = 0;
    for (int i = 0; i < g_jitMethN && i < g_jtCap; i++) n += g_jt[i].n, rows += g_jt[i].n > 0 || g_jt[i].guards > 0;
    Log("jittime: begin %s #%ld, %lu ms, tsc %.0f kHz, %ld samples, %ld dropped (%ld wrapped), %ld entries, %ld rows, %d methods" JT_NL, why, ++g_jtReports, ms,
        JtTscKhz(), n, g_jtDrop, g_jtWrap, g_jtEnt, rows, g_jitMethN);
    int lines = 0;
    for (int i = 0; i < g_jitMethN && i < g_jtCap && lines < JT_LINES; i++) {
        const JtRow& w = g_jt[i];
        if (!w.n && !w.guards) continue;
        Log("jittime: m %d %lX %ld %ld %I64u %I64u %ld %I64u %I64u %ld %ld %s" JT_NL, i, w.tags, (LONG)(JT_CALLS(i)), w.n, w.jit, w.interp, w.nest, w.ent1,
            w.ent2, w.drop, w.guards, g_jitMeth[i].name);
        lines++;
    }
    Log("jittime: end %s #%ld, %d rows%s" JT_NL, why, g_jtReports, lines, rows > lines ? " (cut at JT_LINES)" : "");
}
static void JtReport(const char* why) {  // the game's tables can be half torn down at exit: a fault ends the report, never the game
    if (!g_jt) return;
    __try { JtReportTo(why); } __except (EXCEPTION_EXECUTE_HANDLER) { Log("jittime: fault 0x%08lX in the %s report" JT_NL, GetExceptionCode(), why); }
}
