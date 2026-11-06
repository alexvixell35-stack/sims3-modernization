// docs/207 P0: method compiler prototype for the embedded mint interpreter, the host side (the compiler core is
// native/ts3native/monojit.cpp, shared with the game's [MonoJit]).
// Env MONOHOST_JIT=off|dump|on|verify (default off), MONOHOST_JITLIST=<comma list of "Class.Method" suffixes> (default kJitList).
// Included from monohost.cpp after the docs/139 mint code (uses Log, mt::Write, MintName).
#define MJ_MT mt::
#define JitLog(...) (Log(__VA_ARGS__), Log("\n"))
static void* __cdecl JitTransformHook(u8* rm, void* ctx);
#include "../ts3native/monojit.cpp"

static const char* const kJitList[] = {
    // docs/207 §3 top never-yielding methods that run offline (BCL + SimIFace); Sims3Gameplay* accessors need world objects
    "Dictionary`2.GetPrev", "Dictionary`2.TryGetValue", "Dictionary`2.GetSlot", "Dictionary`2.ContainsKey", "Dictionary`2.set_Item",
    "Dictionary`2.DoAdd", "Enumerator.MoveNext", "Enumerator.get_Current", "Enumerator..ctor", "Enumerator.CurrentSlot",
    "Enum.Equals", "Enum.GetHashCode", "RuntimeTypeHandle.get_Value", "Type.GetTypeFromHandle", "Array.Resize", "Array.IndexOf",
    "DefaultComparer.Equals", "DefaultComparer.GetHashCode", "IEquatableOfTEqualityComparer`1.GetHashCode",
    "IEquatableOfTEqualityComparer`1.Equals", "List`1.GetEnumerator", "List`1.Add", "List`1.get_Item", "List`1.GrowIfNeeded",
    "List`1.get_Count", "UInt64.GetHashCode", "UInt64.Equals", "Int32.GetHashCode", "Int32.Equals", "ObjectGuid.op_Inequality",
    "ObjectGuid.op_Equality", "CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags", "Single.IsNaN", "Math.Max", "Math.Min",
    "HostTest.JitId", "HostTest.JitOut", "HostTest.JitSum", "HostTest.JitInl", "HostTest.JitLz", "HostTest.LzClamp", "LzBox.Get",
    "HostTest.JitLzChain", "HostTest.LzMid", "HostTest.JitLzVirt", "LzSq.Area",
};
static char g_jitListBuf[4096];
static void JitDumpMethod(const u8* rm, const char* name) {
    if (!g_jitDump) return;
    const unsigned short* code = *(const unsigned short* const*)rm;
    int nw = *(const unsigned short*)(rm + 0x3A);
    fprintf(g_jitDump, "== %s rm %p words %d flags %08lX params %d clauses %d arena %u eval@%u areas %u %u\n", name, rm, nw,
            *(const DWORD*)(rm + 0x44), rm[0x40], rm[0x41], *(const DWORD*)(rm + 0x38), *(const unsigned short*)(rm + 0x3E),
            *(const unsigned short*)(rm + 0x34), *(const unsigned short*)(rm + 0x36));
    char b[16];
    for (int i = 0; i < nw;) {
        int len = code[i] == 0xE8 ? JitSwitchLen(code + i) : JitLen(code + i);
        fprintf(g_jitDump, "  %04X %-16s", i, MintName(code[i], b));
        for (int k = 1; k < len && k < 8; k++) fprintf(g_jitDump, " %04X", code[i + k]);
        fprintf(g_jitDump, "\n");
        if (len <= 0) { fprintf(g_jitDump, "  (stop: length %d)\n", len); break; }
        i += len;
    }
    fflush(g_jitDump);
}
static u8* g_xformTramp;
static void* __cdecl JitTransformHook(u8* rm, void* ctx) {
    void* exc = ((void*(__cdecl*)(u8*, void*))(void*)g_xformTramp)(rm, ctx);
    JitNoteDomain(ctx);
    if (exc || !rm || !*(void**)rm) return exc;
    char name[256];
    JitMethodName(rm, name, sizeof(name));
    if (!JitListed(name)) return exc;
    EnterCriticalSection(&g_jitCs);
    if (g_jitMode >= 2 && (g_jitInline || g_jitLazy)) JitPreTransform(rm, ctx);  // first: callees dump and compile before rm
    JitDumpMethod(rm, name);
    if (g_jitMode >= 2) JitCompiled(rm);
    LeaveCriticalSection(&g_jitCs);
    return exc;
}

static bool InstallJitHook() {
    static const u8 kPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x78};
    if (memcmp((void*)(uintptr_t)JIT_XFORM, kPro, 6)) { Log("jit: transform prologue differs\n"); return false; }
    g_xformTramp = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    memcpy(g_xformTramp, kPro, 6);
    g_xformTramp[6] = 0xE9;
    *(DWORD*)(g_xformTramp + 7) = JIT_XFORM + 6 - ((DWORD)(uintptr_t)g_xformTramp + 11);
    return Jmp(JIT_XFORM, (const void*)JitTransformHook);
}

static const int JIT_CASES = 29;
static const char* const kJitCase[JIT_CASES] = {
    "Dictionary<int,Shape>.TryGetValue", "Dictionary<ulong,int>.TryGetValue", "Dictionary<ObjectGuid,int>.TryGetValue",
    "List foreach (per element)", "List get_Item + Count", "List.Add (+GrowIfNeeded)", "boxed Enum.Equals + GetHashCode",
    "GetType() == typeof", "Array.Resize<int>", "Array.IndexOf<int> (64)", "ObjectGuid !=", "CASUtils flags",
    "Dictionary foreach (per element)", "ContainsKey + set_Item", "JitId (compiled, trivial)", "JitPlain (interpreted twin)",
    "JitOut (compiled -> interpreted)", "JitOutPlain (interpreted twin)", "JitSum 64 (compiled loop, per 64)",
    "JitSumPlain 64 (interpreted twin)", "empty loop", "JitInl 64 (compiled, tiny callees)", "JitInlPlain 64 (interpreted twin)",
    "JitLz 64 (compiled, direct calls to leaves)", "JitLzPlain 64 (interpreted twin)",
    "JitLzChain 64 (compiled, lazy chain)", "JitLzChainPlain 64 (interpreted twin)", "JitLzVirt 64 (compiled, class-guarded callvirt)",
    "JitLzVirtPlain 64 (interpreted twin)"};
static int JitCase(int c, int n) {
    void* a[2] = {&c, &n};
    return I32(T("JitBench", 2, a));
}
static double JitTime(int c, int n, int* sum) {
    LARGE_INTEGER fq, t0, t1;
    QueryPerformanceFrequency(&fq);
    QueryPerformanceCounter(&t0);
    *sum = JitCase(c, n);
    QueryPerformanceCounter(&t1);
    return 1e9 * (t1.QuadPart - t0.QuadPart) / fq.QuadPart / n;  // ns per iteration
}
static void JitStats() {
    int nat = 0, ex = 0, bytes = 0;
    for (int m = 0; m < g_jitMethN; m++) nat += g_jitMeth[m].nativeOps, ex += g_jitMeth[m].exitOps, bytes += g_jitMeth[m].bytes;
    Log("jit: %d methods compiled (%d instantiations incl. generics), %d native ops, %d exits, code %d bytes (%.1f KB, pool use %.1f KB), "
        "table %d entries (%u KB), shadow %d words\n", g_jitMethN, g_jitMethN, nat, ex, bytes, bytes / 1024.0, g_jitCodeUsed / 1024.0,
        g_jitEntN, (unsigned)(sizeof(JitEnt) * (JIT_TAB_N + 4096) / 1024), g_jitPosN);
    Log("jit: satb barrier: %ld compiled heap stores logged in spans, %ld worker entries sent back to the interpreter\n", g_jitSatbStores, g_jitSatbBails);
    if (g_jitInline) Log("jit: inlined call sites %ld, sites refused by the inliner %ld\n", g_jitInlined, g_jitInlineNo);
    if (g_jitLazy) {
        int gf = 0;
        for (int i = 0; i < g_jitGuardN; i++) gf += g_jitGuard[i].entry != NULL;
        Log("jit lazy: %d lazy bodies, %d lazy call sites (%d through a class guard, %d of them filled), %d callees seen; lazy calls %ld "
            "(count mode: %ld from a lazy body, %ld through a guard), exits that wrote frames %ld (%ld frames)\n",
            g_jitLazyBodies, g_jitLazySites, g_jitLazyGuardSites, gf, g_jitLazyCellN, g_jitLazyCalls, g_jitLazyChain, g_jitLazyGuard,
            g_jitLazyMats, g_jitLazyMatFrames);
    }
}
// B1 exit census (count mode): every exit site that ran, with its method and EA opcode, and calls per compiled method.
// tools/jit-exit-census.ts reads these lines ("jitcalls" / "jitexit") plus jit-dump.txt.
static void JitCountReport() {
    char nb[64];
    // every compiled method and every exit site, run or not: the static census (docs/207 §11) needs the zeros too
    for (int m = 0; m < g_jitMethN; m++)
        Log("jitcalls %d %ld %ld %d %s\n", m, g_jitCalls[m], g_jitPosRun[m], g_jitMeth[m].posN - g_jitMeth[m].pos0, g_jitMeth[m].name);
    for (int s = 0; s < g_jitSiteN; s++)
        if (g_jitSite[s].meth < g_jitMethN)
            Log("jitexit %d %X %s %s %ld\n", g_jitSite[s].meth, g_jitSite[s].op, MintName(g_jitSite[s].op, nb), g_jitSite[s].slow ? "slow" : "op",
                g_jitSite[s].n);
    // B3 call edges: callee (callvirt: the inline cache's method at the end of the run, else the declared one), the
    // callee's compiled index (-1 if not compiled), receiver-class misses, flags +0x44, native body +0x24, words, clauses
    for (int s = 0; s < g_jitSiteN; s++) {
        const JitSite& S = g_jitSite[s];
        if (S.meth >= g_jitMethN || S.slow || (S.op != 0xEA && S.op != 0xEC && S.op != 0xED)) continue;
        const DWORD* items = *(const DWORD* const*)(g_jitMeth[S.meth].rm + 0x14);
        const u8* c = (const u8*)(uintptr_t)(S.op == 0xEA ? items[S.ip[1]] : items[S.ip[1] + 1] ? items[S.ip[1] + 1] : items[S.op == 0xEC ? S.ip[3] : S.ip[4]]);
        if (!c) continue;
        int cm = -1;
        for (int m = 0; m < g_jitMethN && cm < 0; m++) if (g_jitMeth[m].rm == c) cm = m;
        char cn[128];
        Log("jitedge %d %d %X %ld %ld %d %X %X %d %d %s\n", S.meth, s, S.op, S.n, S.miss, cm, *(const DWORD*)(c + 0x44),
            *(const DWORD*)(c + 0x24), *(const unsigned short*)(c + 0x3A), c[0x41], JitMethodName(c, cn, sizeof(cn)));
    }
}

// B4 differential corpus (MONOHOST_JITCORPUS=1, modes on / verify): MonoHostTest's JitCorpusRun(c) returns one hash per
// case (results and caught exceptions). Each case runs twice on EA's interpreter (unpatched; the two must agree, else
// the case is "unstable" and skipped), then once compiled (in verify mode with the per-call check on). The case names
// are in tools/jit-corpus.ts, which also reads these lines.
// one case: its hash, or for a managed exception that left Run (old Mono raises runtime-thrown exceptions such as newarr's
// by unwinding to the invoke boundary, past interpreted catch blocks) a hash of its type with the top bit set; false on a
// CPU fault (host state suspect)
static bool g_corpusEscaped;  // the last JitCorpusCall's exception left Run
static bool JitCorpusCall(void* m, int c, int* h) {
    void* a[1] = {&c};
    void* exc = NULL; DWORD code = 0;
    void* r = SafeInvoke(m, NULL, a, &exc, &code);
    if (code) return false;
    g_corpusEscaped = exc != NULL;
    if (exc) {
        char t[128] = "", msg[256] = ""; void* in = NULL;
        Describe(exc, t, msg, &in);
        unsigned x = 0x5EED;
        for (const char* p = t; *p; p++) x = x * 31 + (u8)*p;
        *h = (int)(x | 0x80000000u);
        return true;
    }
    *h = I32(r);
    return true;
}
static void JitCorpusTrail(void* tm, int c, const char* arm) {
    void* a[1] = {&c};
    void* exc = NULL; DWORD code = 0;
    void* s = tm ? SafeInvoke(tm, NULL, a, &exc, &code) : NULL;
    char out[1200] = "";
    if (s && !exc && !code) Narrow(s, out, sizeof(out));
    else if (exc) {  // the exception escaped the case body (thrown before its try: the case's own transform, a fixture's type load)
        char t[128] = "", msg[256] = "", ti[128] = "", mi[256] = ""; void* in = NULL, *in2 = NULL;
        Describe(exc, t, msg, &in);
        if (in) Describe(in, ti, mi, &in2);
        _snprintf_s(out, sizeof(out), _TRUNCATE, "(escaped) %s: %s%s%s%s%s", t, msg, in ? " <- " : "", ti, in ? ": " : "", mi);
    }
    Log("jit corpus trail case %d %s: %s\n", c, arm, out[0] ? out : code ? "(trail run faulted)" : "(no exceptions)");
}
static void JitCorpusRun() {
    int n = I32(T("JitCorpusCount", 0, NULL));
    void* m = Meth(g_testK, "JitCorpusRun", 1), *tm = Meth(g_testK, "JitCorpusTrail", 1);
    if (n <= 0 || n == INT_MIN || !m) { Log("jit corpus: no JitCorpusCount / JitCorpusRun in MonoHostTest.dll\n"); return; }
    const int vOn = g_jitVerifyOn;
    const LONG mm0 = g_jvMismatch, ab0 = g_jvAbandoned, ok0 = g_jvOk;
    int bad = 0, unstable = 0, c0 = 0, c1 = n - 1;
    char rv[16] = "";
    if (GetEnvironmentVariableA("MONOHOST_JITCORPUS_FROM", rv, sizeof(rv))) c0 = atoi(rv);
    if (GetEnvironmentVariableA("MONOHOST_JITCORPUS_TO", rv, sizeof(rv))) c1 = atoi(rv);
    const bool trailAll = GetEnvironmentVariableA("MONOHOST_JITCORPUS_TRAIL", rv, sizeof(rv)) && atoi(rv) > 0;  // every case's interpreted trail (round 10 C5)
    if (c0 > 0 || c1 < n - 1) Log("jit corpus: cases %d to %d of %d\n", c0, c1, n);
    // the internal calls the accessor fixtures (JitAccessors.cs) reach answer zero (no task, no parent, no world name) for the
    // corpus only: the stub's NotImplementedException escapes the case body and ended six accessor cases early (round 10 C5)
    static const char* const kCorpusZero[] = {"::Simulator_GetTaskImpl", "::Slots_GetParentIdImpl", "::GameUtils_GetWorldName"};
    char allow0[sizeof(g_icAllow) / sizeof(g_icAllow[0])];
    memcpy(allow0, g_icAllow, sizeof(allow0));
    for (int i = 0; i < g_icCount; i++) for (const char* z : kCorpusZero) if (!g_icAllow[i] && strstr(g_icNames[i], z)) g_icAllow[i] = 1;
    for (int c = c0; c <= c1 && c < n; c++) {
        int ref = 0, ref2 = 0, got = 0;
        g_jitVerifyOn = 0, JitSetPatched(false);
        const bool ok = JitCorpusCall(m, c, &ref) && JitCorpusCall(m, c, &ref2);
        JitSetPatched(true), g_jitVerifyOn = g_jitMode == 3 ? 1 : 0;
        if (!ok || !JitCorpusCall(m, c, &got)) {  // a CPU fault: stop, the interpreter state is suspect
            Log("jit corpus FAULT case %d: %s arm faulted; corpus stopped\n", c, ok ? "compiled" : "interpreted");
            bad++;
            break;
        }
        if (ref != ref2) unstable++, Log("jit corpus UNSTABLE case %d: interpreted %08X then %08X\n", c, ref, ref2);
        else if (got != ref) bad++, Log("jit corpus MISMATCH case %d: compiled %08X interpreted %08X\n", c, got, ref);
        else if (!trailAll) continue;
        else { g_jitVerifyOn = 0, JitSetPatched(false); JitCorpusTrail(tm, c, "interpreted"); JitSetPatched(true), g_jitVerifyOn = vOn; continue; }
        g_jitVerifyOn = 0, JitSetPatched(false);
        JitCorpusTrail(tm, c, "interpreted");
        JitSetPatched(true);
        JitCorpusTrail(tm, c, "compiled");
    }
    JitSetPatched(true);
    g_jitVerifyOn = vOn;
    memcpy(g_icAllow, allow0, sizeof(allow0));
    Log("jit corpus: %d cases, %d differ, %d unstable; per-call verify during the corpus: ok %ld, MISMATCH %ld, abandoned (exception) %ld\n",
        n, bad, unstable, g_jvOk - ok0, g_jvMismatch - mm0, g_jvAbandoned - ab0);
}
// round 10 C4 (docs/207 §14): every corpus case on the M5 worker thread with the compiled words patched, from M5 while the
// worker is idle (the corpus proper runs after the worker stopped). Each entry of a compiled method there takes JitEnter's
// other-thread path (EA's handler for the original opcode), so the worker's hash must be the interpreted one. A case is run
// compiled on this thread first (that compiles it here, not on the worker), then interpreted for the reference. A case whose
// compiled hash already differs, or whose exception leaves Run (the worker entry drops it), is skipped. MONOHOST_JITCORPUS_WORKER=0 skips.
static void JitCorpusWorkerPass() {
    char rv[16] = "";
    if (g_jitMode < 2 || !(GetEnvironmentVariableA("MONOHOST_JITCORPUS", rv, sizeof(rv)) && rv[0] == '1')) return;
    if (GetEnvironmentVariableA("MONOHOST_JITCORPUS_WORKER", rv, sizeof(rv)) && rv[0] == '0') return;
    int n = I32(T("JitCorpusCount", 0, NULL));
    void* m = Meth(g_testK, "JitCorpusRun", 1);
    if (n <= 0 || n == INT_MIN || !m) return;
    if (mt::ts3native_mtprobe.workerState != 2) { Log("jit corpus worker: no idle worker (state %ld), skipped\n", mt::ts3native_mtprobe.workerState); return; }
    const int vOn = g_jitVerifyOn;
    g_jitVerifyOn = 0;
    const LONG ot0 = g_jitOtherTid;
    int wn = 0, wbad = 0, wskip = 0, wesc = 0;
    for (int c = 0; c < n; c++) {
        int ref = 0, got = 0;
        JitSetPatched(true);
        const bool okC = JitCorpusCall(m, c, &got);
        JitSetPatched(false);
        const bool okI = okC && JitCorpusCall(m, c, &ref);
        JitSetPatched(true);
        if (!okI) { Log("jit corpus worker FAULT case %d on this thread; pass stopped\n", c); wbad++; break; }
        if (g_corpusEscaped) { wesc++; continue; }
        if (got != ref) { wskip++; continue; }
        int passes = 0; DWORD ms = 0; int cc = c; void* a[1] = {&cc};
        const int j = WorkerJob("JitCorpusWorker", a, MainIdle, &passes, &ms);
        const int w = I32(T("GetLastWorker", 0, NULL));
        wn++;
        if (j != 0 || w != ref) { wbad++; Log("jit corpus worker MISMATCH case %d: worker %08X (job %d) interpreted %08X\n", c, w, j, ref); LogWorkerError("JitCorpusWorker"); }
    }
    g_jitVerifyOn = vOn;
    Log("jit corpus worker: %d cases, %d differ; skipped: %d escaping, %d compiled differs; other-thread entries %ld\n", wn, wbad, wesc, wskip, g_jitOtherTid - ot0);
}

// B1: compile methods that cannot run offline (Sims3Gameplay accessors) so jit-dump.txt shows their mint code and P0's
// native / exit split. MONOHOST_JITPREP=<file>, one "assembly|namespace|class|method|params" line each (the method must
// also be in MONOHOST_JITLIST). Each is invoked once with this = null and zeroed arguments: the runtime-invoke entry
// transforms first (docs/130 §2), which compiles it; the run itself then mostly stops at the first field read (NRE).
static void JitPrepRun() {
    char path[MAX_PATH] = "";
    if (!GetEnvironmentVariableA("MONOHOST_JITPREP", path, sizeof(path))) return;
    FILE* f = NULL;
    if (fopen_s(&f, path, "r") || !f) { Log("jitprep: cannot open %s\n", path); return; }
    static u8 zero[8][16];
    void* args[8];
    for (int i = 0; i < 8; i++) args[i] = zero[i];
    char line[512];
    int n = 0, compiled = 0;
    while (fgets(line, sizeof(line), f)) {
        char* p[5] = {line};
        int k = 1;
        for (char* c = line; *c && k < 5; c++) if (*c == '|') *c = 0, p[k++] = c + 1;
        if (k < 5) continue;
        p[4][strcspn(p[4], "\r\n")] = 0;
        void* m = Meth(Cls(LoadImageByName(p[0]), p[1], p[2]), p[3], atoi(p[4]));
        n++;
        if (!m) { Log("jitprep %s.%s.%s/%s: not found\n", p[1], p[2], p[3], p[4]); continue; }
        memset(zero, 0, sizeof(zero));
        const int before = g_jitMethN;
        void* exc = NULL; DWORD code = 0;
        SafeInvoke(m, NULL, atoi(p[4]) ? args : NULL, &exc, &code);
        char t[128] = "", msg[256] = ""; void* in = NULL;
        if (exc) Describe(exc, t, msg, &in);
        compiled += g_jitMethN > before;
        Log("jitprep %s.%s.%s/%s: compiled %s, %s %s\n", p[1], p[2], p[3], p[4], g_jitMethN > before ? "yes" : "no",
            exc ? "exception" : code ? "fault" : "returned", exc ? t : "");
    }
    fclose(f);
    Log("jitprep: %d methods, %d compiled\n", n, compiled);
}

static void JitBenchRun() {
    JitPrepRun();
    char cv[8] = "";
    if (g_jitMode >= 2 && GetEnvironmentVariableA("MONOHOST_JITCORPUS", cv, sizeof(cv)) && cv[0] == '1') JitCorpusRun();
    if (g_jitMode < 2) {
        if (g_jitMode) for (int c = 0; c < JIT_CASES; c++) Log("jitbench warm case %d (%s): checksum %d\n", c, kJitCase[c], JitCase(c, 4096));
        return;
    }
    if (g_jitMode == 3) g_jitVerifyOn = 1, JitAtomic(true);  // MONOHOST_JITVERIFY=bench starts verifying here
    for (int c = 0; c < JIT_CASES; c++) Log("jitbench warm case %d (%s): checksum %d\n", c, kJitCase[c], JitCase(c, g_jitMode == 3 ? 1024 : 4096));
    if (g_jitMode == 3) {  // verify also the docs/139 MintBench workload, then every checksum against EA's untouched code
        int n = 300;
        void* a[1] = {&n};
        int mb = I32(T("MintBench", 1, a));
        g_jitVerifyOn = 0;
        JitSetPatched(false);
        int bad = 0;
        for (int c = 0; c < JIT_CASES; c++) {
            int ref = JitCase(c, 1024);
            g_jitVerifyOn = 1, JitSetPatched(true);
            int got = JitCase(c, 1024);  // again with verify on: the state after the first run must not have drifted
            g_jitVerifyOn = 0, JitSetPatched(false);
            if (got != ref) bad++, Log("jit verify CHECKSUM case %d: compiled+verify %d, interpreter %d\n", c, got, ref);
        }
        int mbRef = I32(T("MintBench", 1, a));
        JitSetPatched(true), g_jitVerifyOn = 1;
        Log("jit verify checksums: %d of %d cases differ from the interpreter; MintBench(300) %d vs interpreter %d\n", bad, JIT_CASES, mb, mbRef);
    }
    JitStats();
    if (g_jitMode == 3) {
        Log("jit verify: calls %ld, verified ok %ld, MISMATCH %ld, unverified (clauses / branch to 0) %ld, abandoned (exception) %ld, "
            "alloc-equal words %ld, nested activations unverified %ld, graph too large (compiled only) %ld, calls inside a verified "
            "caller %ld; retried after a first-attempt difference %ld (then equal %ld); snapshot skipped free blocks %ld, dead objects %ld; "
            "a collection during the pass %ld (no restore), new-object set full %ld\n", g_jvCalls, g_jvOk, g_jvMismatch, g_jvUnverified,
            g_jvAbandoned, g_jvAllocEq, g_jvNested, g_jvLarge, g_jvInner, g_jvRetried, g_jvRetryOk, g_jvFreeSkip, g_jvDeadSkip, g_jvGcSkip, g_jvNewFull);
        for (int m = 0; m < g_jitMethN; m++)
            if (g_jitMeth[m].verified || g_jitMeth[m].mismatches || g_jitMeth[m].skipped || g_jitMeth[m].inner)
                Log("jit verify   %-70s ok %ld mismatch %ld unverified %ld, inside a verified caller %ld\n", g_jitMeth[m].name,
                    g_jitMeth[m].verified, g_jitMeth[m].mismatches, g_jitMeth[m].skipped, g_jitMeth[m].inner);
        return;
    }
    if (g_jitVerifyOn == 2) {  // count mode: re-entries and exits per iteration of each case
        for (int c = 0; c < JIT_CASES; c++) {
            g_jitEnterN = g_jitExitN = 0;
            JitCase(c, 20000);
            Log("jitcount case %2d %-40s enters %.2f exits %.2f per iteration\n", c, kJitCase[c], g_jitEnterN / 20000.0, g_jitExitN / 20000.0);
        }
        JitCountReport();
        return;
    }
    SetThreadAffinityMask(GetCurrentThread(), 1 << 2);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    char rv[16] = "";
    GetEnvironmentVariableA("MONOHOST_JITREPS", rv, sizeof(rv));
    const int reps = rv[0] ? atoi(rv) : 7;
    double off[JIT_CASES], on[JIT_CASES];
    for (int c = 0; c < JIT_CASES; c++) {
        const int n = 200000;
        double bOff = 1e9, bOn = 1e9;
        int sOff = 0, sOn = 0;
        for (int r = 0; r < reps; r++) {
            JitSetPatched(false);
            double t = JitTime(c, n, &sOff);
            if (t < bOff) bOff = t;
            JitSetPatched(true);
            t = JitTime(c, n, &sOn);
            if (t < bOn) bOn = t;
        }
        off[c] = bOff, on[c] = bOn;
        if (sOff != sOn) Log("jitbench case %d CHECKSUM DIFFERS: off %d on %d\n", c, sOff, sOn);
    }
    const double loop = (off[20] + on[20]) / 2;
    // the empty loop's cost per iteration; a case whose loop steps by 64 or 256 (HostTest.JitBench) pays it that many times less
    static const int kStep[JIT_CASES] = {1, 1, 1, 256, 1, 1, 1, 1, 1, 1, 1, 1, 256, 1, 1, 1, 1, 1, 64, 64, 1, 64, 64, 64, 64, 64, 64, 64, 64};
    Log("jitbench (best of %d, n = 200000, pinned; ns per iteration, loop overhead %.1f ns / the case's step subtracted):\n", reps, loop);
    for (int c = 0; c < JIT_CASES; c++)
        if (c != 20) Log("jitbench case %2d %-40s interp %7.1f ns  compiled %7.1f ns  speedup %.2fx  saved %6.1f ns\n", c, kJitCase[c], off[c] - loop / kStep[c],
            on[c] - loop / kStep[c], (off[c] - loop / kStep[c]) / (on[c] - loop / kStep[c]), off[c] - on[c]);
    // whole MintBench (docs/139), JIT off vs on, interleaved
    int n = 3000, s0 = 0, s1 = 0;
    void* args[1] = {&n};
    LARGE_INTEGER fq, t0, t1;
    QueryPerformanceFrequency(&fq);
    double b0 = 1e9, b1 = 1e9;
    for (int r = 0; r < reps; r++) {
        for (int on2 = 0; on2 < 2; on2++) {
            JitSetPatched(on2 == 1);
            QueryPerformanceCounter(&t0);
            int s = I32(T("MintBench", 1, args));
            QueryPerformanceCounter(&t1);
            double ms = 1000.0 * (t1.QuadPart - t0.QuadPart) / fq.QuadPart;
            if (on2) { s1 = s; if (ms < b1) b1 = ms; } else { s0 = s; if (ms < b0) b0 = ms; }
        }
    }
    Log("jitbench MintBench(3000): interp %.1f ms, compiled %.1f ms (%+.1f%%), checksums %d / %d%s\n", b0, b1, 100.0 * (b1 - b0) / b0, s0, s1,
        s0 == s1 ? "" : " DIFFER");
    JitSetPatched(true);
}

// MONOHOST_DIVGUARD=1: the two jump-table stubs of [EaCrashFixes] MintDivGuard (ts3native eafix.cpp, docs/213 §8), so
// INT_MIN / -1 throws OverflowException and INT_MIN % -1 gives 0, as in the game with the fix, instead of the CPU divide
// error that kills the host (#114). Installed before the handler table is copied, so compiled exits use the stubs too.
static DWORD g_dgDiv = 0xE55B4C, g_dgRem = 0xE55C0F, g_dgOvf = 0xE57AA5, g_dgNext = 0xE544D5;
static __declspec(naked) void DgDivStub() {
    __asm {
        cmp dword ptr [esi - 8], -1
        jne game
        cmp dword ptr [esi - 0x10], 0x80000000
        jne game
        jmp dword ptr [g_dgOvf]
    game:
        jmp dword ptr [g_dgDiv]
    }
}
static __declspec(naked) void DgRemStub() {
    __asm {
        cmp dword ptr [esi - 8], -1
        jne game
        cmp dword ptr [esi - 0x10], 0x80000000
        jne game
        add esi, -8
        mov dword ptr [esi - 8], 0
        add edi, 2
        mov dword ptr [ebp - 4], edi
        jmp dword ptr [g_dgNext]
    game:
        jmp dword ptr [g_dgRem]
    }
}
static bool InstallDivGuard() {
    DWORD* t = (DWORD*)(uintptr_t)MINT_TABLE;
    if (t[0x127] != g_dgDiv || t[0x143] != g_dgRem) return false;
    const DWORD dv = (DWORD)(uintptr_t)DgDivStub, rv = (DWORD)(uintptr_t)DgRemStub;
    return mt::Write(t + 0x127, &dv, 4) && mt::Write(t + 0x143, &rv, 4);
}

static bool InstallJit() {
    char v[16] = "";
    GetEnvironmentVariableA("MONOHOST_JIT", v, sizeof(v));
    g_jitMode = !strcmp(v, "dump") ? 1 : !strcmp(v, "on") ? 2 : !strcmp(v, "verify") ? 3 : 0;
    if (!g_jitMode) return true;
    char dg[4] = "";
    if (GetEnvironmentVariableA("MONOHOST_DIVGUARD", dg, sizeof(dg)) && dg[0] == '1')
        Log("jit: div guard (MintDivGuard stubs) %s\n", InstallDivGuard() ? "installed" : "NOT installed: table entries differ");
    InitializeCriticalSection(&g_jitCs);
    if (g_jitMode >= 2) {
        char x[8] = "";
        // MONOHOST_JITC2=<mask> keeps only these template groups (JitSetup; default all)
        unsigned c2 = 255;
        if (GetEnvironmentVariableA("MONOHOST_JITC2", x, sizeof(x))) c2 = (unsigned)atoi(x);
        if (!JitSetup(c2, g_jitMode == 3)) { Log("jit: setup failed\n"); return false; }
        g_jitPatched = true;
        g_jitExitAll = GetEnvironmentVariableA("MONOHOST_JITEXITALL", x, sizeof(x)) && x[0] == '1';
        g_jitCountOn = GetEnvironmentVariableA("MONOHOST_JITCOUNT", x, sizeof(x)) && x[0] == '1';
        if (g_jitCountOn) g_jitSite = (JitSite*)calloc(65536, sizeof(JitSite));
        g_jitInline = GetEnvironmentVariableA("MONOHOST_JITINLINE", x, sizeof(x)) && x[0] == '1';
        g_jitLazy = GetEnvironmentVariableA("MONOHOST_JITLAZY", x, sizeof(x)) && x[0] == '1';
        g_jitDumpCode = GetEnvironmentVariableA("MONOHOST_JITDUMPCODE", x, sizeof(x)) && x[0] == '1';
        g_jitVerifyOn = g_jitMode == 3 ? 1 : g_jitCountOn ? 2 : 0;
        if (g_jitMode == 3 && GetEnvironmentVariableA("MONOHOST_JITVERIFY", x, sizeof(x)) && !strcmp(x, "bench")) g_jitVerifyOn = 0;
        g_jitMainTid = GetCurrentThreadId();
        if (g_jitMode == 3 && !JitAtomic(true)) { Log("jit: atomic alloc redirect failed\n"); return false; }
        DWORD ent = (DWORD)(uintptr_t)JitEnter;
        if (!mt::Write((DWORD*)(uintptr_t)MINT_TABLE + JIT_OP, &ent, 4)) { Log("jit: setup failed\n"); return false; }
        Log("jit: JIT_OP 0x%X -> JitEnter, %s%s\n", JIT_OP, g_jitExitAll ? "every instruction an exit, " : "", g_jitCountOn ? "counting" : "");
    }
    if (GetEnvironmentVariableA("MONOHOST_JITLIST", g_jitListBuf, sizeof(g_jitListBuf))) {
        char* ctx = NULL;
        for (char* t = strtok_s(g_jitListBuf, ",", &ctx); t && g_jitNameN < 128; t = strtok_s(NULL, ",", &ctx)) g_jitNames[g_jitNameN++] = t;
    } else
        for (const char* n : kJitList) g_jitNames[g_jitNameN++] = n;
    char path[MAX_PATH];
    GetModuleFileNameA(NULL, path, MAX_PATH);
    strcpy_s(strrchr(path, '\\') + 1, 64, "jit-dump.txt");
    fopen_s(&g_jitDump, path, "w");
    Log("jit: mode %s, %d listed names\n", v, g_jitNameN);
    return InstallJitHook();
}
