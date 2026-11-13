// TestEarlyOut ([RouteEarlyOut] probe and Mode 2, docs/142): included by selftest.cpp. A fake R (two chunks), a fake A*
// container, fake cost functions and circle prepass, and a fake search that does what the game's A* does (docs/142 §9.1: the
// circle rerun through the detoured entry, the context writes, h0, *best = start then the smallest heuristic of every generated
// node, the closed bits; mode-2 byte-3 rule, a wall set for the other modes). The game's own A* on fake worlds is steerro-test
// K6E.

static u8 g_eoR[0x30], g_eoRec[256], g_eoC[0x80], g_eoBits[40];
static DWORD g_eoTable[2], g_eoCTable[3], g_eoFirst[2];
static signed char g_eoCol[5];
static DWORD g_eoWalls;       // bit per waypoint (chunk 0: 0..2, chunk 1: 3..4): edges into it are walls (modes 1, 3, 4)
static int g_eoFakeCalls, g_eoFakeNested;
static bool g_eoCircle;       // the fake circle prepass accepts (r^2 0 at entry)
static DWORD g_eoFakeSkew;    // != 0: the fake search reports this *best (a forced shadow difference)

static int EoGi(DWORD ref) { return (int)((ref & 0xFFFF) * 3 + (ref >> 16)); }

// record: n, flags (0x40 | type), surface, bx, bz, refs, edge bytes; refs here stay in the row (dz 0)
static u8* EoRec(u8* p, DWORD n, const DWORD* wp, const int* dx, const u8* eb) {
    p[0] = (u8)n, p[1] = 0x40 | 0x10, p[2] = p[3] = p[4] = 0;
    for (DWORD j = 0; j < n; j++) ((unsigned short*)(p + 5))[j] = (unsigned short)(wp[j] | (DWORD)(dx[j] + 3) << 10 | 3u << 13);
    memcpy(p + 5 + 2 * n, eb, n);
    return p + 5 + 3 * n;
}

// chunk 0: w0 -3- w1 (byte 3 both ways), w1 -7- c1w0, w2 alone; chunk 1: w0 -3- w1, w0 -7- c0w1
static void EoFakeWorld() {
    memset(g_eoRec, 0, sizeof(g_eoRec));
    u8* p = g_eoRec;
    g_eoTable[0] = 0;
    ((unsigned short*)p)[0] = 3;
    u8* r = p + 8;
    const DWORD a0[] = {1}; const int d0[] = {0}; const u8 e0[] = {3};
    ((unsigned short*)p)[1] = (unsigned short)(r - p); r = EoRec(r, 1, a0, d0, e0);
    const DWORD a1[] = {0, 0}; const int d1[] = {0, 1}; const u8 e1[] = {3, 7};
    ((unsigned short*)p)[2] = (unsigned short)(r - p); r = EoRec(r, 2, a1, d1, e1);
    ((unsigned short*)p)[3] = (unsigned short)(r - p); r = EoRec(r, 0, NULL, NULL, NULL);
    u8* q = r + (4 - ((r - g_eoRec) & 3)) % 4;
    g_eoTable[1] = (DWORD)(q - g_eoRec);
    ((unsigned short*)q)[0] = 2;
    r = q + 6;
    const DWORD b0[] = {1, 1}; const int f0[] = {0, -1}; const u8 h0[] = {3, 7};
    ((unsigned short*)q)[1] = (unsigned short)(r - q); r = EoRec(r, 2, b0, f0, h0);
    const DWORD b1[] = {0}; const int f1[] = {0}; const u8 h1[] = {3};
    ((unsigned short*)q)[2] = (unsigned short)(r - q); r = EoRec(r, 1, b1, f1, h1);
    memset(g_eoR, 0, sizeof(g_eoR));
    *(DWORD*)g_eoR = 2, *(DWORD*)(g_eoR + 4) = 1;
    *(DWORD**)(g_eoR + 8) = g_eoTable, *(u8**)(g_eoR + 0xC) = g_eoRec, *(DWORD*)(g_eoR + 0x10) = (DWORD)(r - g_eoRec);
    // the game's flood colours: {c0w0, c0w1, c1w0, c1w1} = 0, c0w2 = 1 (chunk 1's first index 3)
    g_eoFirst[0] = 0, g_eoFirst[1] = 3;
    const signed char col[] = {0, 0, 1, 0, 0};
    memcpy(g_eoCol, col, 5);
    g_eoR[0x14] = 1;
    *(DWORD**)(g_eoR + 0x18) = g_eoFirst, *(DWORD**)(g_eoR + 0x1C) = g_eoFirst + 2;
    *(signed char**)(g_eoR + 0x28) = g_eoCol, *(signed char**)(g_eoR + 0x2C) = g_eoCol + 5;
    // container: closed bits 32 bytes of virtual nodes, then 1 byte per chunk; factors 1.0 (slots 6-8 0, as the game's tuning),
    // threshold 3: goals nearer than that take the SHORT set
    memset(g_eoC, 0, sizeof(g_eoC));
    g_eoCTable[0] = 0, g_eoCTable[1] = 32, g_eoCTable[2] = 33;
    *(DWORD*)(g_eoC + 8) = 34, *(DWORD**)(g_eoC + 0xC) = g_eoCTable, *(u8**)(g_eoC + 0x10) = g_eoBits;
    *(float*)(g_eoC + 0x24) = 3.0f;
    for (int s = 0; s < 2; s++)
        for (int i = 0; i < 6; i++) ((float*)(g_eoC + 0x28 + s * 0x24))[i] = s ? 2.0f : 1.0f;
}

static const u8* EoRecOf(DWORD ref) {
    const u8* ch = g_eoRec + g_eoTable[ref & 0xFFFF];
    return ch + ((const unsigned short*)(ch + 2))[ref >> 16];
}

// positions 2^gi: |pos(a) - pos(goal)| differs for every a (no heuristic ties)
static double EoFakeH(const DWORD* a, const DWORD* b) { return fabs((double)(1 << EoGi(*a)) - (double)(1 << EoGi(*b))); }
static double __cdecl EoFakeCost1(const u8*, const u8*, const DWORD* f, const DWORD* t, DWORD edge, DWORD, u8* o, void*) {
    if (!edge) return EoFakeH(f, t) * ((const float*)(o + 0x64))[0];
    return *(const float*)(o + 0xA0) != 0.0f || ((g_eoWalls >> EoGi(*t)) & 1) ? 1e9 : 1.0;  // the fake circle: start only
}
static double __cdecl EoFakeCost2(const u8*, const u8*, const DWORD* f, const DWORD* t, DWORD edge, DWORD eb, u8* o, void*) {
    if (!edge) return EoFakeH(f, t);
    return *(const float*)(o + 0xA0) != 0.0f || eb != 3 ? 1e9 : 1.0;
}
static char __stdcall EoFakeCircle(const u8*, u8* o, const DWORD*, const DWORD*) {
    *(float*)(o + 0x90) = 7.0f;  // the centre: written whether or not the circle pass runs
    if (!g_eoCircle) return 0;
    *(float*)(o + 0xA0) = 1.0f;
    return 1;
}
static const float kEoFakeK = 1.0f;
static const DWORD kEoFakeFltMax = 0x7F7FFFFF;

static DWORD __fastcall EoFakeAs(u8* c, void* e, const u8* R, const u8* G, const DWORD* s, const DWORD* g, DWORD m, u8* o, DWORD* b) {
    g_eoFakeCalls++;
    float& r2 = *(float*)(o + 0xA0);
    if (r2 == 0.0f && g_eoCircleFn(R, o, s, g)) {  // circle pass through the detoured entry, as 0x6762B4 does
        g_eoFakeNested++;
        if (EoAsHook(c, e, R, G, s, g, m, o, b)) return 1;
        r2 = 0.0f;
    }
    *(DWORD*)(o + 0xA4) = *s, *(DWORD*)(o + 0xB4) = *g;
    memcpy(o + 0x64, c + 0x4C, 36);
    memset(g_eoBits, 0, sizeof(g_eoBits));
    const EoCostFn fn = (EoCostFn)(uintptr_t)g_eoCostFn[m - 1];
    const double h0 = fn(R, G, s, g, 0, 0, o, NULL);
    if (h0 <= *(const float*)(c + 0x24)) memcpy(o + 0x64, c + 0x28, 36);
    float* k = (float*)(o + 0x64);
    for (int i = 0; i < 3; i++) if (k[2 + i] != 0.0f) k[6 + i] = *g_eoRecipK / k[2 + i];
    if (*o & 0x40) *(DWORD*)(o + 0x68) = *g_eoRetryTrespass;
    *b = *s;
    float best = (float)h0;
    DWORD stack[16], n = 0;
    stack[n++] = *s;
    const DWORD* tab = *(const DWORD* const*)(c + 0xC);
    auto closed = [&](DWORD at) -> u8& { return g_eoBits[tab[(at & 0xFFFF) + 1] + ((at >> 16) >> 3)]; };
    DWORD r = 0;
    while (n && !r) {
        const DWORD at = stack[--n];
        if (closed(at) & (1 << ((at >> 16) & 7))) continue;
        closed(at) |= (u8)(1 << ((at >> 16) & 7));
        if (at == *g) { r = 1; break; }
        const u8* rec = EoRecOf(at);
        for (DWORD j = 0; j < rec[0]; j++) {
            const DWORD ref = ((const unsigned short*)(rec + 5))[j];
            const DWORD to = (DWORD)((int)(at & 0xFFFF) + (int)((ref >> 10) & 7) - 3) | (ref & 0x3FF) << 16;
            if (closed(to) & (1 << ((to >> 16) & 7))) continue;
            if (fn(R, G, &at, &to, 1, rec[5 + 2 * rec[0] + j], o, NULL) >= 1e8) continue;
            const float h = (float)fn(R, G, &to, g, 0, 0, o, NULL);
            stack[n++] = to;
            if (h < best) best = h, *b = to;
        }
    }
    if (!r && g_eoFakeSkew) *b = g_eoFakeSkew;
    return r;
}

static DWORD EoCall(DWORD s, DWORD g, DWORD m, u8* o, DWORD* best = NULL) {
    DWORD b;
    return EoAsHook(g_eoC, NULL, g_eoR, NULL, &s, &g, m, o, best ? best : &b);
}
static DWORD EoRef(DWORD chunk, DWORD wp) { return chunk | wp << 16; }

// one call's whole answer: result, *best, the context the A* writes (+0x64..+0xBB, circle centre included), the closed bits
struct EoAns { DWORD r, best; u8 ctx[0x58]; u8 bits[sizeof(g_eoBits)]; };
static EoAns EoAnswer(DWORD s, DWORD g, DWORD m, const u8* oIn) {
    u8 o[0xC8];
    memcpy(o, oIn, sizeof(o));
    EoAns a;
    a.r = EoCall(s, g, m, o, &a.best);
    memcpy(a.ctx, o + 0x64, sizeof(a.ctx));
    memcpy(a.bits, g_eoBits, sizeof(a.bits));
    return a;
}
static bool EoSame(const EoAns& x, const EoAns& y) {
    return x.r == y.r && x.best == y.best && !memcmp(x.ctx, y.ctx, sizeof(x.ctx)) && !memcmp(x.bits, y.bits, sizeof(x.bits));
}

static void TestEarlyOut() {
    static_assert(offsetof(Ts3nCounters, eoMode) == 0x3260 && offsetof(Ts3nCounters, eoProvWhy) == 0x3284 &&
                  offsetof(Ts3nCounters, eoSlowSaved) == 0x32B0, "counters v78 layout (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, eoAct) == 0x3664 && offsetof(Ts3nCounters, eoShadowUs) == 0x3684 &&
                  offsetof(Ts3nCounters, eoActRefused) == 0x36A4 && offsetof(Ts3nCounters, eoActTie) == 0x36A8 && sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v86 layout after v85 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    QueryPerformanceFrequency(&g_qpf);
    InitializeCriticalSection(&g_eoCs);
    EoFakeWorld();
    g_eoAsOrig = EoFakeAs;
    const DWORD savedFns[5] = {g_eoCostFn[0], g_eoCostFn[1], g_eoCostFn[2], g_eoCostFn[3], g_eoCostFn[4]};
    const EoCircleFn savedCircle = g_eoCircleFn;
    const float* savedK = g_eoRecipK;
    const DWORD* savedFm = g_eoRetryTrespass;
    g_eoCostFn[0] = g_eoCostFn[2] = g_eoCostFn[3] = g_eoCostFn[4] = (DWORD)(uintptr_t)EoFakeCost1;
    g_eoCostFn[1] = (DWORD)(uintptr_t)EoFakeCost2;
    g_eoCircleFn = EoFakeCircle, g_eoRecipK = &kEoFakeK, g_eoRetryTrespass = &kEoFakeFltMax;
    k.eoMode = 1;
    u8 o[0xC8] = {0};
    EoReq saved;

    // outside a request: pass-through, nothing counted
    g_eoCircle = true;
    assert(EoCall(EoRef(0, 0), EoRef(0, 1), 1, o) == 1 && k.eoProv == 0 && t_eo.as == 0);

    // car proof: byte-3 components {c0w0, c0w1}, {c1w0, c1w1}, {c0w2}; the circle rerun inside is not counted twice
    EoBegin(saved);
    g_eoFakeCalls = g_eoFakeNested = 0;
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 2, o) == 0);
    assert(t_eo.as == 1 && t_eo.asFail == 1 && t_eo.prov == 1 && t_eo.why[0] == 1 && g_eoFakeCalls == 2 && g_eoFakeNested == 1);
    assert(k.eoLabelBuilds == 1 && g_eoCar.ok);
    assert(EoCall(EoRef(0, 0), EoRef(0, 1), 2, o) == 1 && t_eo.prov == 1);  // same component: no proof, path
    assert(EoCall(EoRef(1, 1), EoRef(1, 0), 2, o) == 1 && t_eo.prov == 1 && k.eoLabelBuilds == 1);  // labels kept per R
    // colour proof (pedestrian, no water flags); with the water flag the colours prove nothing
    assert(EoCall(EoRef(0, 0), EoRef(0, 2), 1, o) == 0 && t_eo.why[1] == 1);
    *(DWORD*)o = 0x80;
    assert(EoCall(EoRef(0, 0), EoRef(0, 2), 1, o) == 0 && t_eo.why[1] == 1);
    *(DWORD*)o = 0;
    EoEnd(saved, 0);
    assert(!t_eo.active && k.eoReqs == 1 && k.eoAs == 5 && k.eoProv == 2 && k.eoProvWhy[0] == 1 && k.eoProvWhy[1] == 1 && k.eoWrong == 0);

    // memo: c1w0 walled; from c0w0 the search reaches {c0w0, c0w1}: goal c1w1 fails (no proof yet: same colour), its closed
    // set is kept; c1w0 is then proven, c0w1 is not (inside the set). The factors' 1/x slots are 0 in the container, as in the
    // game's tuning: v78 refused every memo for that (docs/142 §9.2)
    g_eoWalls = 1u << 3;
    EoBegin(saved);
    assert(EoCall(EoRef(0, 0), EoRef(1, 1), 1, o) == 0 && t_eo.prov == 0 && t_eo.memo);
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 1, o) == 0 && t_eo.prov == 1 && t_eo.why[2] == 1);
    assert(EoCall(EoRef(0, 0), EoRef(0, 1), 1, o) == 1 && t_eo.prov == 1);
    // the success kept the closed bits of a partial search, but the memo is the copy of the failed one
    assert(EoCall(EoRef(0, 0), EoRef(1, 1), 1, o) == 0 && t_eo.prov == 2);
    // another wall context (an opts dword), another start, another mode: no proof
    o[0x34] = 1;
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 1, o) == 0 && t_eo.prov == 2);  // and this failure replaces the memo
    o[0x34] = 0;
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 1, o) == 0 && t_eo.prov == 2);
    assert(EoCall(EoRef(0, 1), EoRef(1, 0), 1, o) == 0 && t_eo.prov == 2);
    assert(EoCall(EoRef(0, 1), EoRef(1, 1), 3, o) == 0 && t_eo.prov == 2);
    // the goal point (+0xB8) and goal ref (+0xB4) are not part of the context
    *(DWORD*)(o + 0xB8) = 7;
    assert(EoCall(EoRef(0, 1), EoRef(1, 1), 3, o) == 0 && t_eo.prov == 3);
    // a wall that opens behind the memo's back: the proof is wrong and counted (the live check of docs/142 §3.3)
    g_eoWalls = 0;
    assert(EoCall(EoRef(0, 1), EoRef(1, 0), 3, o) == 1 && t_eo.wrong == 1 && k.eoWrong == 1);
    EoEnd(saved, 1);
    assert(!t_eo.memo && k.eoWrong == 1);

    // a new request starts without a memo; restricted searches (circle set at entry) and capped ones store none
    g_eoWalls = 1u << 3;
    g_eoCircle = false;
    EoBegin(saved);
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 1, o) == 0 && t_eo.prov == 0);
    EoEnd(saved, 0);
    EoBegin(saved);
    *(float*)(o + 0xA0) = 4.0f;
    assert(EoCall(EoRef(0, 0), EoRef(1, 1), 1, o) == 0 && !t_eo.memo);
    *(float*)(o + 0xA0) = 0.0f;
    t_budget.capped = 1;
    assert(EoCall(EoRef(0, 0), EoRef(1, 1), 1, o) == 0 && !t_eo.memo);
    t_budget.capped = 0;
    assert(EoCall(EoRef(0, 0), EoRef(1, 1), 1, o) == 0 && t_eo.memo);
    // a restricted search with a memo from an unrestricted one: still proven (it reaches less)
    *(float*)(o + 0xA0) = 4.0f;
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 1, o) == 0 && t_eo.why[2] == 1);
    *(float*)(o + 0xA0) = 0.0f;
    // a factor 0 in a cost set: no memo (SHORT and LONG could give other walls); so does a factor product near the wall
    ((float*)(g_eoC + 0x28))[3] = 0.0f;
    assert(EoCall(EoRef(0, 1), EoRef(1, 1), 1, o) == 0 && !t_eo.memo);
    ((float*)(g_eoC + 0x28))[3] = 1.0f;
    ((float*)(g_eoC + 0x4C))[1] = 999000.0f;  // trespass x 1000 x 182 m >= 1e8
    assert(!EoCostSetsPlain(g_eoC));
    ((float*)(g_eoC + 0x4C))[1] = 2.0f;
    assert(EoCostSetsPlain(g_eoC));
    // a nested request (a plan inside a managed call) resumes the outer counts and drops its memo
    assert(EoCall(EoRef(0, 0), EoRef(1, 1), 1, o) == 0 && t_eo.memo);
    const LONG asOuter = t_eo.as;
    EoReq inner;
    EoBegin(inner);
    assert(t_eo.as == 0 && !t_eo.memo && t_eo.active);
    assert(EoCall(EoRef(1, 1), EoRef(0, 0), 2, o) == 0 && t_eo.why[0] == 1);
    EoEnd(inner, 0);
    assert(t_eo.active && t_eo.as == asOuter && !t_eo.memo);
    // slow request: logged and counted; the proofs' share decides "saved"
    const LONG logUs = g_eoLogUs, slow0 = k.eoSlow;
    g_eoLogUs = 1;
    EoEnd(saved, 0);
    assert(k.eoSlow == slow0 + 1 && !t_eo.active);
    g_eoLogUs = logUs;

    // mode 0: pass-through inside a request too
    k.eoMode = 0;
    EoBegin(saved);
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 2, o) == 0 && t_eo.as == 0);
    EoEnd(saved, 0);
    k.eoMode = 1;

    // CallPlan opens the scope only with the detour ready and eoMode on; the plan's searches count inside it
    const LONG reqs0 = k.eoReqs;
    g_planOrig = [](void*, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) -> u8 {
        u8 oo[0xC8] = {0};
        return (u8)(EoCall(EoRef(0, 0), EoRef(1, 0), 2, oo) | EoCall(EoRef(0, 0), EoRef(0, 1), 2, oo));
    };
    u8 opts[0x38] = {0};
    int list[2];
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 1 && k.eoReqs == reqs0);  // eoReady 0
    k.eoReady = 1;
    const LONG prov0 = k.eoProv, as0 = k.eoAs;
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 1);
    assert(k.eoReqs == reqs0 + 1 && k.eoAs == as0 + 2 && k.eoProv == prov0 + 1 && !t_eo.active);
    k.eoReady = 0;

    // labels refuse an R with a reference outside the chunk table (the proof must cover every edge the search can take)
    u8* farRec = (u8*)EoRecOf(EoRef(0, 0));
    const unsigned short keep = *(unsigned short*)(farRec + 5);
    *(unsigned short*)(farRec + 5) = (unsigned short)(1 | 6u << 10 | 3u << 13);  // dx +3: chunk 3 of 2
    *(DWORD*)(g_eoR + 0x10) += 1;  // another size: rebuild
    assert(EoCarApart(g_eoR, EoRef(0, 0), EoRef(1, 0)) == 0 && !g_eoCar.ok && k.eoLabelBuilds == 2);
    *(unsigned short*)(farRec + 5) = keep;
    *(DWORD*)(g_eoR + 0x10) -= 1;
    assert(EoCarApart(g_eoR, EoRef(0, 0), EoRef(1, 0)) == 1 && g_eoCar.ok && k.eoLabelBuilds == 3);

    // ---- Mode 2 (docs/142 §9): acting. Reference answers come from the fake game with eoMode 0 (no hook work at all) ----
    g_eoMemoMinUs = 0;  // the fake searches take microseconds
    g_eoShadowFirst = 0, g_eoShadowEvery = 0;  // no shadows unless a case sets them
    k.eoAct = 2;
    for (int circle = 0; circle < 2; circle++) {
        g_eoCircle = circle != 0;
        memset(o, 0, sizeof(o));
        *(DWORD*)(o + 0xB8) = 0x1234;  // the goal point: not written by the A*, compared as part of the context
        // (a) car, unreachable: answered without the game, identical to the game's answer (best = c0w1, the nearer node of the
        // start's street component; LONG set: h0 7 > threshold 3)
        k.eoMode = 0;
        const EoAns ref = EoAnswer(EoRef(0, 0), EoRef(1, 0), 2, o);
        assert(ref.r == 0 && ref.best == EoRef(0, 1));
        k.eoMode = 1;
        EoBegin(saved);
        g_eoFakeCalls = 0;
        const LONG acted0 = k.eoActed;
        const EoAns act = EoAnswer(EoRef(0, 0), EoRef(1, 0), 2, o);
        assert(EoSame(ref, act) && g_eoFakeCalls == 0 && k.eoActed == acted0 + 1 && k.eoActedWhy[0] >= 1 && t_eo.acted == 1);
        // (b) car, reachable: never cut (the labels say same component; the game answers)
        assert(EoAnswer(EoRef(0, 0), EoRef(0, 1), 2, o).r == 1 && g_eoFakeCalls == (circle ? 2 : 1) && k.eoActed == acted0 + 1);
        // (c) memo: c1w0 walled, mode 1. The first failure runs the game and keeps its closed set; the second goal outside it
        // is answered from the memo, identical to the game (start c0w0, goal c1w1 = pos 16: best c0w1; SHORT set for c1w0? no:
        // h0 = 7 > 3, LONG, heuristic x keep 2)
        g_eoWalls = 1u << 3;
        assert(EoAnswer(EoRef(0, 0), EoRef(1, 1), 1, o).r == 0 && t_eo.memo);
        k.eoMode = 0;
        const EoAns mref = EoAnswer(EoRef(0, 0), EoRef(1, 0), 1, o);
        k.eoMode = 1;
        g_eoFakeCalls = 0;
        const EoAns mact = EoAnswer(EoRef(0, 0), EoRef(1, 0), 1, o);
        assert(mref.r == 0 && EoSame(mref, mact) && g_eoFakeCalls == 0 && t_eo.actWhy[1] == 1);
        // (d) memo, goal inside the reached set: never cut
        assert(EoAnswer(EoRef(0, 0), EoRef(0, 1), 1, o).r == 1 && g_eoFakeCalls == (circle ? 2 : 1) && t_eo.actWhy[1] == 1);
        // (e) stale epoch: a record's flags byte moved (blocked bit) since the memo: no act, the game answers; with the wall
        // opened as well the game finds the path (a stale memo would have said "no path")
        assert(EoAnswer(EoRef(0, 0), EoRef(1, 1), 1, o).r == 0 && t_eo.memo);
        u8* flags = (u8*)EoRecOf(EoRef(1, 0)) + 1;
        const LONG stale0 = k.eoStale[1];
        *flags ^= 0x80;
        g_eoFakeCalls = 0;
        assert(EoAnswer(EoRef(0, 0), EoRef(1, 0), 1, o).r == 0 && g_eoFakeCalls >= 1 && k.eoStale[1] == stale0 + 1);
        assert(EoAnswer(EoRef(0, 0), EoRef(1, 1), 1, o).r == 0 && t_eo.memo);  // memo again, with the new flags
        *flags ^= 0x80, g_eoWalls = 0;
        const LONG wrong0 = k.eoWrong;
        assert(EoAnswer(EoRef(0, 0), EoRef(1, 0), 1, o).r == 1 && k.eoStale[1] == stale0 + 2 && k.eoWrong == wrong0);
        g_eoWalls = 1u << 3;
        EoEnd(saved, 0);
    }
    // (f) refused: a colour proof (no reachable set), a restricted entry, a capped budget: the game answers
    g_eoCircle = false;
    EoBegin(saved);
    memset(o, 0, sizeof(o));
    const LONG ref0 = k.eoActRefused, acted1 = k.eoActed;
    g_eoFakeCalls = 0;
    assert(EoCall(EoRef(0, 0), EoRef(0, 2), 1, o) == 0 && k.eoActRefused == ref0 + 1);
    *(float*)(o + 0xA0) = 4.0f;
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 2, o) == 0 && k.eoActRefused == ref0 + 2);
    *(float*)(o + 0xA0) = 0.0f;
    t_budget.on = 1, t_budget.capped = 1;
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 2, o) == 0 && k.eoActRefused == ref0 + 3);
    t_budget.on = 0, t_budget.capped = 0;
    assert(g_eoFakeCalls == 3 && k.eoActed == acted1);
    // (g) stale car labels: an edge byte changed in place (R's pointer and size unchanged, labels not rebuilt): the live BFS
    // reaches the goal, no act and no proof; the game finds the path, eoWrong unchanged
    u8* rec01 = (u8*)EoRecOf(EoRef(0, 1));
    const LONG wrong1 = k.eoWrong, staleCar = k.eoStale[0];
    rec01[5 + 2 * 2 + 1] = 3;  // c0w1 -> c1w0 becomes a street edge
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 2, o) == 1 && k.eoStale[0] == staleCar + 1 && k.eoWrong == wrong1);
    rec01[5 + 2 * 2 + 1] = 7;
    EoEnd(saved, 0);
    // (h) shadow (Mode 2 Act 0): every proof answered both ways, the game's answer returned, no difference
    k.eoAct = 1;
    EoBegin(saved);
    g_eoFakeCalls = 0;
    const LONG sh0 = k.eoShadow, acted2 = k.eoActed;
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 2, o) == 0 && g_eoFakeCalls == 1 && k.eoShadow == sh0 + 1 && k.eoActDiff == 0);
    assert(k.eoActed == acted2 && k.eoShadowNodes[0] >= 2);
    // a forced difference (the fake reports another *best): counted, acting latched off, later proofs run as in Mode 1
    g_eoFakeSkew = EoRef(0, 2);
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 2, o) == 0 && k.eoActDiff == 1 && k.eoActOff == 1);
    g_eoFakeSkew = 0;
    k.eoAct = 2;
    g_eoFakeCalls = 0;
    assert(EoCall(EoRef(0, 0), EoRef(1, 0), 2, o) == 0 && g_eoFakeCalls == 1 && k.eoActed == acted2 && k.eoShadow == sh0 + 2);
    k.eoActOff = 0;
    // (i) schedule: the first ShadowFirst proofs are shadowed, then every ShadowEvery-th; acts in between
    g_eoActN = 0, g_eoShadowFirst = 1, g_eoShadowEvery = 3;
    g_eoFakeCalls = 0;
    for (int i = 0; i < 6; i++) assert(EoCall(EoRef(0, 0), EoRef(1, 0), 2, o) == 0);
    assert(g_eoFakeCalls == 3 && k.eoShadow == sh0 + 5 && k.eoActed == acted2 + 3 && k.eoActDiff == 1);  // n = 1, 3, 6 shadowed
    EoEnd(saved, 0);
    // Mode=2 Act=1 from the ini: eoMode 1, eoAct 2
    {
        wchar_t ini[MAX_PATH];
        GetTempPathW(MAX_PATH, ini);
        wcscat_s(ini, L"eo-selftest.ini");
        WritePrivateProfileStringW(L"RouteEarlyOut", L"Enabled", L"1", ini);
        WritePrivateProfileStringW(L"RouteEarlyOut", L"Mode", L"2", ini);
        WritePrivateProfileStringW(L"RouteEarlyOut", L"Act", L"1", ini);
        DeleteCriticalSection(&g_eoCs);
        assert(ReadEarlyOut(ini) && k.eoMode == 1 && k.eoAct == 2 && g_eoShadowFirst == 32 && g_eoShadowEvery == 16 && g_eoMemoMinUs == 1000);
        WritePrivateProfileStringW(L"RouteEarlyOut", L"Act", L"0", ini);
        DeleteCriticalSection(&g_eoCs);
        assert(ReadEarlyOut(ini) && k.eoAct == 1);
        WritePrivateProfileStringW(L"RouteEarlyOut", L"Mode", L"1", ini);
        DeleteCriticalSection(&g_eoCs);
        assert(ReadEarlyOut(ini) && k.eoMode == 1 && k.eoAct == 0);
        DeleteFileW(ini);
    }

    k.eoMode = 0;
    k.eoReqs = k.eoAs = k.eoAsFail = k.eoAsMs = k.eoAsMaxUs = k.eoProv = k.eoProvMs = k.eoWrong = k.eoReachMax = 0;
    k.eoProvWhy[0] = k.eoProvWhy[1] = k.eoProvWhy[2] = k.eoLabelBuilds = k.eoLabelUs = 0;
    k.eoSlow = k.eoSlowMs = k.eoSlowAsMs = k.eoSlowProvMs = k.eoSlowSaved = 0;
    memset(&k.eoAct, 0, offsetof(Ts3nCounters, eoActTie) + 4 - offsetof(Ts3nCounters, eoAct));
    for (int i = 0; i < 5; i++) g_eoCostFn[i] = savedFns[i];
    g_eoCircleFn = savedCircle, g_eoRecipK = savedK, g_eoRetryTrespass = savedFm;
    g_eoShadowFirst = 32, g_eoShadowEvery = 16, g_eoMemoMinUs = 1000, g_eoActN = 0;
    g_eoAsOrig = NULL;
    g_planOrig = NULL;
}
