// docs/537 §17: the host gates of the method compiler. Included from monohost-jit.cpp (right after JitFind); nothing here runs
// or emits unless its switch is set, so with both off JitCompiled's code is byte for byte what it was.
//   MONOHOST_JITCOVER=1  every position starts with a call to JitCoverHit; the mint branch edges (both sides, switch arms) of every
//                        compiled method are counted while the corpus runs compiled. Output jit-cover.txt beside the exe, read by tools/jit-coverage.ts.
//   MONOHOST_JITBENCH=1  (MONOHOST_JIT=on) self time per call of every compiled method over whole corpus passes, interpreter vs
//                        compiled, MONOHOST_JITBENCH_REPS passes per arm, pinned. Output jit-bench.txt, read by tools/jit-bench.ts.
// Bench method: word 0 and each ret* position carry a stamp in both arms. In the compiled arm the stamp is in the body; in the
// interpreter arm only those words are JIT_OP and their native code is stamp + exit to EA's handler. A stamp pair books the time
// between entry and return minus the listed callees' whole time, so an unlisted callee counts as self in both arms. A frame that
// an exception unwinds has no return stamp and is dropped: exception paths are not in the numbers.
static bool g_jitCover, g_jitBench;
static void JitSetPatched(bool on);

// ---- coverage ----
struct CovPos { int meth; unsigned short w, op; int e0, en; volatile LONG n; };
struct CovEdge { int to, arm; volatile LONG n; };  // arm 0 = fall through / switch default, 1 = taken, switch arm k = k + 1
static CovPos* g_covPos;
static CovEdge* g_covEdge;
static int g_covPosN, g_covEdgeN, g_covFrom = -1;
static const int COV_POS = 400000, COV_EDGE = 200000;

static void __cdecl JitCoverHit(int pi) {  // a branch has no call or throw in it, so the next position this thread runs is its successor
    if (g_covFrom >= 0) {
        const CovPos& f = g_covPos[g_covFrom];
        for (int i = f.e0; i < f.e0 + f.en; i++) if (g_covEdge[i].to == pi) g_covEdge[i].n++;
        g_covFrom = -1;
    }
    CovPos& p = g_covPos[pi];
    p.n++;
    if (p.en) g_covFrom = pi;
}
// mint's conditional branches are 0xA0-0xE7 (docs/data/mint-opcodes.csv), short forms 0xA6-0xAB and 0xCA-0xE7, then switch 0xE8
static bool MintCondBranch(DWORD op) { return op >= 0xA0 && op <= 0xE7; }
static bool MintShortBranch(DWORD op) { return (op >= 0xA6 && op <= 0xAB) || op >= 0xCA; }

// ---- bench ----
struct StampFrame { DWORD frame; unsigned __int64 tIn, t0, child; };
static StampFrame g_stamp[8192];
static int g_stampN;
struct BenchMeth { unsigned __int64 cycles; LONG calls; };
static BenchMeth g_benchM[4096];
static void __cdecl JitStampEnter(int, DWORD frame) {
    unsigned __int64 tIn = __rdtsc();
    while (g_stampN && g_stamp[g_stampN - 1].frame == frame) g_stampN--;  // a branch back to word 0, or a frame an exception left here
    if (g_stampN >= 8192) return;
    StampFrame& s = g_stamp[g_stampN++];
    s.frame = frame, s.tIn = tIn, s.child = 0, s.t0 = __rdtsc();
}
static void __cdecl JitStampLeave(int m, DWORD frame) {
    const unsigned __int64 t = __rdtsc();
    while (g_stampN && g_stamp[g_stampN - 1].frame != frame) g_stampN--;  // callees an exception unwound
    if (!g_stampN) return;
    StampFrame& s = g_stamp[--g_stampN];
    g_benchM[m].cycles += t - s.t0 - s.child, g_benchM[m].calls++;
    if (g_stampN) g_stamp[g_stampN - 1].child += __rdtsc() - s.tIn;  // the caller's child time holds this frame and both stamps
}
struct BenchSite { JitEnt* en; unsigned short* word; u8* stub; u8* label; };
static BenchSite* g_benchSite;
static int g_benchSiteN;
static void EmitCall2(Em& e, const void* fn, int a, bool frame) {  // fn(a, [ebp-8]) or fn(a)
    if (frame) e.op(0xFF, 6, EBP, -8);  // push dword [ebp-8], the frame's MonoInvocation
    e.b(0x68); e.d((DWORD)a);
    e.b(0xE8); e.d((DWORD)(uintptr_t)fn - ((DWORD)(uintptr_t)e.p + 4));
    e.b(0x83); e.b(0xC4); e.b(frame ? 8 : 4);
}
static bool JitStamped(int k, DWORD op) { return k == 0 || g_opInfo[op].kind == K_RET; }

// the first thing at position k, in both passes of JitCompiled
static void JitGateEmit(Em& e, int k, DWORD op) {
    if (g_jitCover) EmitCall2(e, (const void*)JitCoverHit, g_covPosN + k, false);
    if (g_jitBench && JitStamped(k, op)) EmitCall2(e, k ? (const void*)JitStampLeave : (const void*)JitStampEnter, g_jitMethN, true);
}
// after the last pass: the interpreter arm's native code of the stamped positions
static void JitGateStubs(Em& e, int n, const int* posW, const unsigned short* posOp, const unsigned short* code, u8** stub) {
    for (int k = 0; g_jitBench && k < n; k++) {
        if (!JitStamped(k, posOp[k])) continue;
        stub[k] = e.p;
        EmitCall2(e, k ? (const void*)JitStampLeave : (const void*)JitStampEnter, g_jitMethN, true);
        EmitExit(e, code + posW[k], code[posW[k]], false);  // the original word: position 0 can be a superop
    }
}
// the method is published (table entries added, g_jitMethN not yet moved)
static void JitGatePublish(int n, const int* posW, const unsigned short* posOp, const int* wordToPos, const unsigned short* code, u8** label, u8** stub, int words) {
    if (g_jitCover && g_covPosN + n <= COV_POS) {
        for (int k = 0; k < n; k++) {
            CovPos& p = g_covPos[g_covPosN + k];
            p.meth = g_jitMethN, p.w = (unsigned short)posW[k], p.op = posOp[k], p.e0 = g_covEdgeN, p.en = 0, p.n = 0;
            const unsigned short* ip = code + posW[k];
            const auto edge = [&](int tw, int arm) {
                const int tp = tw >= 0 && tw < words ? wordToPos[tw] : -1;
                if (tp >= 0 && g_covEdgeN < COV_EDGE) g_covEdge[g_covEdgeN].to = g_covPosN + tp, g_covEdge[g_covEdgeN].arm = arm, g_covEdge[g_covEdgeN++].n = 0, p.en++;
            };
            if (MintCondBranch(p.op) && k + 1 < n) {
                edge(posW[k + 1], 0);
                edge(posW[k] + (MintShortBranch(p.op) ? (short)ip[1] : *(const int*)(ip + 1)), 1);
            } else if (p.op == 0xE8 && k + 1 < n) {
                const int cnt = *(const int*)(ip + 1), endW = posW[k] + 3 + 2 * cnt;
                edge(posW[k + 1], 0);
                for (int c = 0; c < cnt; c++) edge(endW + ((const int*)(ip + 3))[c], c + 2);
            }
        }
        g_covPosN += n;
    }
    for (int k = 0; g_jitBench && k < n; k++)
        if (JitStamped(k, posOp[k]) && g_benchSiteN < 65536) {
            BenchSite& s = g_benchSite[g_benchSiteN++];
            s.en = JitFind((DWORD)(uintptr_t)(code + posW[k])), s.word = (unsigned short*)code + posW[k], s.stub = stub[k], s.label = label[k];
        }
}

// one body: index, words, clause count, IL size, name, then its IL in hex (the tools key a body by the SHA-256 of that IL, as the game does)
static void JitGateBody(FILE* f, int m, LONG calls) {
    const u8* rm = g_jitMeth[m].rm;
    void* mm = *(void* const*)(rm + 8);
    const u8* hdr = mm ? ((const u8*(__cdecl*)(void*))(uintptr_t)0xE85390)(mm) : NULL;  // mono_method_get_header (docs/527 §4.2)
    const DWORD size = hdr ? *(const DWORD*)hdr : 0;
    const u8* il = hdr ? *(const u8* const*)(hdr + 4) : NULL;
    fprintf(f, "B %d %ld %d %d %lu %s\nI ", m, calls, g_jitMeth[m].words, rm[0x41], il ? size : 0, g_jitMeth[m].name);
    for (DWORD i = 0; il && i < size; i++) fprintf(f, "%02x", il[i]);
    fprintf(f, "\n");
}
static FILE* JitGateFile(const char* name) {
    char path[MAX_PATH];
    GetModuleFileNameA(NULL, path, MAX_PATH);
    strcpy_s(strrchr(path, '\\') + 1, 64, name);
    FILE* f = NULL;
    fopen_s(&f, path, "w");
    return f;
}
static void JitCoverReport() {
    FILE* f = JitGateFile("jit-cover.txt");
    if (!f) { Log("jit cover: cannot write jit-cover.txt\n"); return; }
    int edges = 0, hit = 0;
    for (int p = 0, m = -1; p < g_covPosN; p++) {
        const CovPos& c = g_covPos[p];
        if (c.meth != m) { m = c.meth; JitGateBody(f, m, c.n); }
        for (int e = c.e0; e < c.e0 + c.en; e++) {
            fprintf(f, "E %d %d %X %d %ld\n", c.w, g_covPos[g_covEdge[e].to].w, c.op, g_covEdge[e].arm, g_covEdge[e].n);
            edges++, hit += g_covEdge[e].n > 0;
        }
    }
    fclose(f);
    Log("jit cover: %d methods, %d mint branch edges, %d hit (jit-cover.txt)\n", g_jitMethN, edges, hit);
}
// whole corpus passes per arm, the order swapped each rep; rows R <arm 0 interpreter, 1 compiled> <rep> <method> <calls> <cycles>
static void JitBenchArm(bool compiled) {
    JitSetPatched(compiled);
    for (int i = 0; i < g_benchSiteN; i++) {
        BenchSite& s = g_benchSite[i];
        s.en->native = compiled ? s.label : s.stub;
        if (!compiled) *s.word = (unsigned short)JIT_OP;
    }
}
static bool JitCorpusCall(void* m, int c, int* h);
static void JitBenchRun2(void* m, int c0, int c1) {
    FILE* f = JitGateFile("jit-bench.txt");
    if (!f) { Log("jit bench: cannot write jit-bench.txt\n"); return; }
    char rv[16] = "";
    const int reps = GetEnvironmentVariableA("MONOHOST_JITBENCH_REPS", rv, sizeof(rv)) ? atoi(rv) : 9;
    const DWORD_PTR aff = SetThreadAffinityMask(GetCurrentThread(), 1 << 2);
    const int pri = GetThreadPriority(GetCurrentThread());
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    LARGE_INTEGER fq, q0, q1;
    QueryPerformanceFrequency(&fq);
    QueryPerformanceCounter(&q0);
    const unsigned __int64 t0 = __rdtsc();
    for (int r = 0; r < reps; r++)
        for (int a = 0; a < 2; a++) {
            const int arm = a ^ (r & 1);
            JitBenchArm(arm == 1);
            memset(g_benchM, 0, sizeof(g_benchM));
            g_stampN = 0;
            int h = 0;
            bool ok = true;
            for (int c = c0; c <= c1 && ok; c++) ok = JitCorpusCall(m, c, &h);
            if (!ok) Log("jit bench: arm %d rep %d stopped by a fault\n", arm, r);
            for (int k = 0; k < g_jitMethN; k++)
                if (g_benchM[k].calls) fprintf(f, "R %d %d %d %ld %I64u\n", arm, r, k, g_benchM[k].calls, g_benchM[k].cycles);
        }
    QueryPerformanceCounter(&q1);
    const double hz = (double)(__rdtsc() - t0) / ((double)(q1.QuadPart - q0.QuadPart) / fq.QuadPart);
    // the stamp pair's own floor: what an empty method books (the tools subtract it from both arms)
    memset(g_benchM, 0, sizeof(g_benchM));
    g_stampN = 0;
    for (int i = 0; i < 1000000; i++) JitStampEnter(0, 1), JitStampLeave(0, 1);
    fprintf(f, "H %.0f %.2f\n", hz, (double)g_benchM[0].cycles / g_benchM[0].calls);
    for (int k = 0; k < g_jitMethN; k++) JitGateBody(f, k, 0);
    fclose(f);
    JitBenchArm(true);
    memset(g_benchM, 0, sizeof(g_benchM));
    SetThreadAffinityMask(GetCurrentThread(), aff);
    SetThreadPriority(GetCurrentThread(), pri);
    Log("jit bench: %d passes per arm, %.2f s, tsc %.0f Hz (jit-bench.txt)\n", reps, (double)(q1.QuadPart - q0.QuadPart) / fq.QuadPart, hz);
}
static void JitGateInit() {
    char x[8] = "";
    g_jitCover = GetEnvironmentVariableA("MONOHOST_JITCOVER", x, sizeof(x)) && x[0] == '1';
    g_jitBench = GetEnvironmentVariableA("MONOHOST_JITBENCH", x, sizeof(x)) && x[0] == '1';
    if (g_jitCover) g_covPos = (CovPos*)calloc(COV_POS, sizeof(CovPos)), g_covEdge = (CovEdge*)calloc(COV_EDGE, sizeof(CovEdge));
    if (g_jitBench) g_benchSite = (BenchSite*)calloc(65536, sizeof(BenchSite));
}
static void JitGateBeforeCorpus() {  // coverage counts the corpus, not the selftest around it
    for (int i = 0; i < g_covPosN; i++) g_covPos[i].n = 0;
    for (int i = 0; i < g_covEdgeN; i++) g_covEdge[i].n = 0;
}
// the corpus ran; c0..c1 are its cases, m its entry
static void JitGateAfterCorpus(void* m, int c0, int c1) {
    if (g_jitCover) JitCoverReport();
    if (g_jitBench) JitBenchRun2(m, c0, c1);
}
