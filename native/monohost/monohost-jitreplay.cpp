// Argument replay (docs/537 §17): MONOHOST_JITREPLAY=<file from tools/jit-replay.ts> runs captured game inputs through EA's interpreter
// and the compiled code, as JitCorpusRun does for the written cases: interpreted twice (they must agree, else unstable), compiled once
// with the per-call verifier on under MONOHOST_JIT=verify. The hash covers the return value, the exception, and the rebuilt objects.
// JitReplay.cs resolves types and allocates; here the captured bytes go in. Objects keep their address over the arms: a default
// GetHashCode is the address, so a rebuild per arm would make every identity dictionary differ. A word that named a captured object
// now names its rebuilt copy, a word past the walk's bound is null, and what cannot be rebuilt is counted per method with a reason.
// Included by monohost-jit.cpp.
static void* g_jrK;
struct JrObj { int type, len; DWORD n, full; u8* data; u8* addr; bool blob, gone; };
struct JrFix { int obj; DWORD off; int to; };  // to -1: null
struct JrRow { char name[96]; int inputs, ran, differ, unstable, skipped, cuts; };
struct JrWhy { int meth; char why[80]; int n; };
static const int JR_OBJS = 8192, JR_FIX = 200000, JR_MAX_METH = 1024, JR_WHY = 512, JR_HASH = 1 << 14;
static const DWORD JR_POOL = 32u << 20;
static JrObj* g_jrObj;
static JrFix* g_jrFix;
static JrRow* g_jrRow;
static JrWhy* g_jrWhy;
static int g_jrWhyN, g_jrObjN, g_jrFixN;
static u8* g_jrPool;
static DWORD g_jrPoolN;
static DWORD* g_jrHk;  // rebuilt address -> index + 1
static int* g_jrHv;
static u8* g_jrSnap;   // whole blocks of the rebuilt objects, for the restore before each arm
static DWORD* g_jrSnapOff;

static int g_jrThrew, g_jrFaulted;
static void* JrCall(const char* name, int n, void** a, DWORD* code = NULL) {
    void* m = Meth(g_jrK, name, n), *exc = NULL;
    DWORD c = 0;
    void* r = m ? SafeInvoke(m, NULL, a, &exc, &c) : NULL;
    if (code) *code = c;
    if (!m || exc || c) {
        g_jrThrew++, g_jrFaulted += c != 0;
        char t[128] = "", msg[256] = ""; void* in = NULL;
        if (exc) Describe(exc, t, msg, &in);
        Log("jit replay: %s: %s %08lX %s %s\n", name, m ? "failed" : "not found", c, t, msg);
    }
    return r;
}
static int JrField(const char* name, char* out, int n) {  // a public static string of JitReplay, through its getter
    void* s = JrCall(name, 0, NULL);
    Narrow(s, out, n);
    return s != NULL;
}
static void JrReason(int meth, const char* why) {
    for (int i = 0; i < g_jrWhyN; i++) if (g_jrWhy[i].meth == meth && !strcmp(g_jrWhy[i].why, why)) { g_jrWhy[i].n++; return; }
    if (g_jrWhyN >= JR_WHY) return;
    g_jrWhy[g_jrWhyN].meth = meth, g_jrWhy[g_jrWhyN].n = 1;
    strncpy_s(g_jrWhy[g_jrWhyN++].why, sizeof(g_jrWhy[0].why), why, _TRUNCATE);
}
static void JrWhyNow(int meth, char* out, int n) {
    if (!JrField("WhyNow", out, n) || !out[0]) strcpy_s(out, n, "unknown");
    JrReason(meth, out);
}
static void JrIndex(const u8* a, int i) {
    if (!a) return;
    DWORD k = (DWORD)(uintptr_t)a, h = (k >> 3) & (JR_HASH - 1);
    while (g_jrHk[h]) h = (h + 1) & (JR_HASH - 1);
    g_jrHk[h] = k, g_jrHv[h] = i + 1;
}
static int JrLookup(DWORD k) {
    for (DWORD h = (k >> 3) & (JR_HASH - 1); g_jrHk[h]; h = (h + 1) & (JR_HASH - 1)) if (g_jrHk[h] == k) return g_jrHv[h] - 1;
    return -1;
}
static inline unsigned JrMix(unsigned h, DWORD v) { return (h ^ v) * 16777619u; }
// an object a call made: its class and contents, not its address; three levels, as JitAllocEqual
static unsigned JrHashNew(unsigned h, DWORD w, int depth, int* budget);
static unsigned JrHashWords(unsigned h, const u8* p, DWORD n, int depth, int* budget) {
    for (DWORD o = 4; o + 4 <= n && *budget > 0; o += 4, --*budget) {
        const DWORD w = *(const DWORD*)(p + o);
        const int i = w >= 0x10000 && !(w & 3) ? JrLookup(w) : -1;
        if (i >= 0) h = JrMix(h, 0x40000000u | (DWORD)i);
        else if (w >= 0x10000 && !(w & 3) && depth < 3 && GcBase((void*)(uintptr_t)w) == (u8*)(uintptr_t)w) h = JrHashNew(h, w, depth + 1, budget);
        else h = JrMix(h, w);
    }
    return h;
}
static unsigned JrHashNew(unsigned h, DWORD w, int depth, int* budget) {
    const u8* b = (const u8*)(uintptr_t)w;
    const DWORD block = GcSize(b), vt = *(const DWORD*)b;
    if (!block || !vt || GcBase((void*)(uintptr_t)vt)) return JrMix(h, 0x20000000u);
    const u8* kl = *(const u8* const*)(uintptr_t)vt;
    const char* nm = *(const char* const*)(kl + 0x34), *ns = *(const char* const*)(kl + 0x38);
    const size_t nl = strlen(nm);
    DWORD used = block;
    if (nl && nm[nl - 1] == ']') used = 0x10 + *(const DWORD*)(b + 8) * *(const DWORD*)(kl + 0x5C);
    else if (!strcmp(nm, "String") && !strcmp(ns, "System")) used = 8 + 2 * *(const DWORD*)(b + 4);
    else if (*(const DWORD*)(kl + 0x54) >= 4) used = *(const DWORD*)(kl + 0x54);
    if (used > block) used = block;
    return JrHashWords(JrMix(h, vt), b, used, depth, budget);
}
static void JrRestore() {
    for (int i = 0; i < g_jrObjN; i++) if (g_jrObj[i].addr) memcpy(g_jrObj[i].addr, g_jrSnap + g_jrSnapOff[i], GcSize(g_jrObj[i].addr));
}
// one arm: false on a CPU fault
static bool JrArm(unsigned* out) {
    JrRestore();
    void* run = Meth(g_jrK, "Run", 0), *exc = NULL;
    DWORD code = 0;
    void* r = SafeInvoke(run, NULL, NULL, &exc, &code);
    if (code) return false;
    unsigned h = 2166136261u;
    if (exc) {  // old Mono's runtime-thrown exceptions unwind to the invoke boundary (JitCorpusCall)
        char t[128] = "", msg[256] = ""; void* in = NULL;
        Describe(exc, t, msg, &in);
        for (const char* p = t; *p; p++) h = JrMix(h, (u8)*p);
    } else h = JrMix(h, (DWORD)I32(r));
    int budget = 1 << 20;
    for (int i = 0; i < g_jrObjN; i++) {
        const JrObj& o = g_jrObj[i];
        if (!o.addr) continue;
        const DWORD block = GcSize(o.addr);
        h = JrHashWords(JrMix(h, (DWORD)i), o.addr, !o.blob && o.n < block ? o.n : block, 0, &budget);
    }
    void* keep = JrCall("KeepNow", 0, NULL), *args = JrCall("ArgsNow", 0, NULL);
    if (keep) h = JrHashNew(h, (DWORD)(uintptr_t)keep, 0, &budget);
    if (args) h = JrHashNew(h, (DWORD)(uintptr_t)args, 0, &budget);
    *out = h;
    return true;
}
static int JrHex(const char* s, u8* to) {
    int n = 0;
    if (s[0] == '-' && !s[1]) return 0;
    const auto v = [](char c) { return c <= '9' ? c - '0' : (c | 32) - 'a' + 10; };
    for (; s[0] > ' ' && s[1] > ' '; s += 2) to[n++] = (u8)(v(s[0]) << 4 | v(s[1]));
    return n;
}
struct JrInput { int id, meth, hasExpect, expect; char selfKind; int selfI; int nArg; int nStatic;
                 struct { int k; DWORD lo, hi; char kind; int i; } arg[32], st[8]; };
// a value slot: 'n' nothing, 'o' a rebuilt object, 'b' a blob. Returns the managed value or NULL; *ok false = the input cannot run
static void* JrValue(char kind, int i, int meth, void* box, bool* ok) {
    if (kind == 'n') return NULL;
    JrObj& o = g_jrObj[i];
    if (kind == 'o') {
        if (!o.addr) *ok = false;
        return o.addr;
    }
    if (box) {  // a value type: into a box of the parameter's type; a value wider than the blob keeps zeros and says so
        const DWORD room = *(const DWORD*)(*(const u8* const*)(uintptr_t)*(const DWORD*)box + 0x54) - 4;  // MonoClass +0x54 instance size
        if (o.n < room) JrReason(meth, "value blob shorter than the type");
        memcpy((u8*)box + 4, o.data, o.n < room ? o.n : room);
        o.addr = (u8*)box, o.blob = true;
        return box;
    }
    return *(void**)o.data;  // a reference held in the slot (ref T, out T): refs are already fixed up
}
static void JrFixUp() {
    for (int f = 0; f < g_jrFixN; f++) {
        JrFix& x = g_jrFix[f];
        JrObj& o = g_jrObj[x.obj];
        if (x.off + 4 <= o.n) *(DWORD*)(o.data + x.off) = x.to >= 0 ? (DWORD)(uintptr_t)g_jrObj[x.to].addr : 0;
    }
}
// no box: a reference held in the slot, or a value type that was refused; the second has no value to pass, its slot holds a native word
static bool JrBoxOk(void* box, int m) {
    char why[80] = "";
    if (box) return true;
    if (JrField("WhyNow", why, sizeof(why)) && why[0]) { JrReason(m, why); return false; }
    return true;
}
static void JrRun(JrInput& in, int* tot) {
    JrRow& row = g_jrRow[in.meth];
    row.inputs++;
    tot[0]++;
    g_jrThrew = 0;
    int m = in.meth;
    char why[80] = "";
    const auto skip = [&](const char* w) { if (w) JrReason(m, w); row.skipped++; tot[4]++; };
    {
        void* a[1] = {&m};
        void* w = JrCall("MethWhy", 1, a);
        if (w) { Narrow(w, why, sizeof(why)); skip(why); return; }
    }
    JrCall("Reset", 0, NULL);
    for (int i = 0; i < g_jrObjN; i++) {  // the objects first: everything else refers to them
        JrObj& o = g_jrObj[i];
        if (o.type < 0) continue;
        void* a[2] = {&o.type, &o.len};
        o.addr = (u8*)JrCall("New", 2, a);
        if (!o.addr) { JrWhyNow(m, why, sizeof(why)); o.gone = true; row.cuts++; }
    }
    JrFixUp();
    for (int i = 0; i < g_jrObjN; i++) {
        JrObj& o = g_jrObj[i];
        if (!o.addr) continue;
        const DWORD block = GcSize(o.addr), n = o.n < block ? o.n : block;
        if (n > 4) memcpy(o.addr + 4, o.data + 4, n - 4);  // not word 0: the vtable is this process's
    }
    bool ok = true;
    void* box = NULL;
    if (in.selfKind == 'b') {
        int k = -1;
        void* a[2] = {&m, &k};
        box = JrCall("ArgBlob", 2, a);
        if (!JrBoxOk(box, m)) ok = false;
    }
    void* self = JrValue(in.selfKind, in.selfI, m, box, &ok);
    if (!ok) { skip("this cannot be rebuilt"); return; }
    {
        int na = in.nArg;
        void* a[3] = {&m, self, &na};
        JrCall("Begin", 3, a);
    }
    for (int j = 0; j < in.nArg && ok; j++) {
        auto& s = in.arg[j];
        box = NULL;
        if (s.kind == 'b') {
            void* a[2] = {&m, &s.k};
            box = JrCall("ArgBlob", 2, a);
            if (!JrBoxOk(box, m)) ok = false;
        }
        void* v = JrValue(s.kind, s.i, m, box, &ok);
        void* a[4] = {&s.k, &s.lo, &s.hi, v};
        if (ok) JrCall("Arg", 4, a);
    }
    if (!ok) { skip("an argument cannot be rebuilt"); return; }
    for (int j = 0; j < in.nStatic && ok; j++) {
        auto& s = in.st[j];
        box = NULL;
        if (s.kind == 'b') { void* a[1] = {&s.k}; box = JrCall("StaticBlob", 1, a); if (!JrBoxOk(box, m)) ok = false; }
        void* v = JrValue(s.kind, s.i, m, box, &ok);
        void* a[4] = {&s.k, &s.lo, &s.hi, v};
        if (ok) JrCall("StaticSet", 4, a);
    }
    if (!ok) { skip("a static cannot be rebuilt"); return; }
    if (g_jrThrew) {
        Log("jit replay: input %d %s: a harness call failed, %s\n", in.id, row.name, g_jrFaulted ? "replay stopped" : "input skipped");
        skip("a harness call failed");
        if (g_jrFaulted) tot[5] = 1;
        return;
    }
    void* ready = JrCall("Ready", 0, NULL);
    if (!ready || !*((u8*)ready + 4)) { JrWhyNow(m, why, sizeof(why)); skip(NULL); return; }
    memset(g_jrHk, 0, sizeof(DWORD) * JR_HASH);
    DWORD snapN = 0;
    for (int i = 0; i < g_jrObjN; i++) {
        JrObj& o = g_jrObj[i];
        JrIndex(o.addr, i);
        g_jrSnapOff[i] = snapN;
        if (!o.addr) continue;
        const DWORD block = GcSize(o.addr);
        if (snapN + block > JR_POOL) { o.addr = NULL; skip("rebuilt objects over the snapshot size"); return; }
        memcpy(g_jrSnap + snapN, o.addr, block);
        snapN += block;
    }
    const int vOn = g_jitVerifyOn;
    unsigned warm = 0, ref = 0, ref2 = 0, got = 0;
    g_jitVerifyOn = 0, JitSetPatched(false);
    const bool ok2 = JrArm(&warm) && JrArm(&ref) && JrArm(&ref2);  // warm: the first run's class inits and transforms are not state to compare
    JitSetPatched(true), g_jitVerifyOn = g_jitMode == 3 ? 1 : 0;
    if (!ok2 || !JrArm(&got)) {
        Log("jit replay FAULT input %d %s: %s arm faulted; replay stopped\n", in.id, row.name, ok2 ? "compiled" : "interpreted");
        g_jitVerifyOn = vOn;
        tot[5] = 1;
        return;
    }
    row.ran++;
    tot[1]++;
    if (in.hasExpect) {  // the fixture knows the answer: the rebuild itself is under test, not only interpreted against compiled
        void* li = JrCall("LastInt", 0, NULL);
        if (I32(li) != in.expect) row.differ++, tot[3]++, Log("jit replay MISMATCH input %d %s: result %d, the fixture expects %d\n", in.id, row.name, I32(li), in.expect);
    }
    if (ref != ref2) row.unstable++, tot[2]++, Log("jit replay UNSTABLE input %d %s: interpreted %08X then %08X\n", in.id, row.name, ref, ref2);
    else if (got != ref) row.differ++, tot[3]++, Log("jit replay MISMATCH input %d %s: compiled %08X interpreted %08X\n", in.id, row.name, got, ref);
    g_jitVerifyOn = vOn;
}
static void JitReplayRun() {
    char path[MAX_PATH] = "";
    if (!GetEnvironmentVariableA("MONOHOST_JITREPLAY", path, sizeof(path))) return;
    if (g_jitMode < 2) { Log("jit replay: needs MONOHOST_JIT=on or verify\n"); return; }
    void* img = LoadImageByName("MonoHostTest.dll");
    g_jrK = Cls(img, "MonoHostTest", "JitReplay");
    FILE* f = NULL;
    if (!g_jrK || fopen_s(&f, path, "r") || !f) { Log("jit replay: no JitReplay class or no file %s\n", path); return; }
    g_jrObj = (JrObj*)calloc(JR_OBJS, sizeof(JrObj));
    g_jrFix = (JrFix*)calloc(JR_FIX, sizeof(JrFix));
    g_jrRow = (JrRow*)calloc(JR_MAX_METH, sizeof(JrRow));
    g_jrWhy = (JrWhy*)calloc(JR_WHY, sizeof(JrWhy));
    g_jrHk = (DWORD*)calloc(JR_HASH, 4), g_jrHv = (int*)calloc(JR_HASH, 4), g_jrSnapOff = (DWORD*)calloc(JR_OBJS, 4);
    g_jrPool = (u8*)VirtualAlloc(NULL, JR_POOL, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    g_jrSnap = (u8*)VirtualAlloc(NULL, JR_POOL, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    u8* nameBuf = (u8*)JrCall("BufNow", 0, NULL) + 0x10;
    const auto toBuf = [nameBuf](const char* s) { int n = (int)strcspn(s, "\r\n"); if (n > 4095) n = 4095; memcpy(nameBuf, s, n); return n; };
    const LONG mm0 = g_jvMismatch, ab0 = g_jvAbandoned, ok0 = g_jvOk;
    static char line[1 << 22];
    static JrInput in;
    int metaN = 0, tot[6] = {};  // inputs, ran, unstable, differ, skipped, stop
    while (!tot[5] && fgets(line, sizeof(line), f)) {
        char* p = line;
        char tag[16] = "";
        int used = 0;
        sscanf_s(p, "%15s%n", tag, (unsigned)sizeof(tag), &used);
        p += used;
        if (!strcmp(tag, "type")) {
            int id = 0, skip = 0;
            sscanf_s(p, "%d %n", &id, &skip);
            int n = toBuf(p + skip);
            void* a[1] = {&n};
            JrCall("AddTypeN", 1, a);
        } else if (!strcmp(tag, "sfield")) {
            int id = 0, ty = 0, skip = 0;
            sscanf_s(p, "%d %d %n", &id, &ty, &skip);
            int n = toBuf(p + skip);
            void* a[2] = {&ty, &n};
            JrCall("AddStaticN", 2, a);
        } else if (!strcmp(tag, "meth")) {
            int id = 0, ty = 0, tok = 0; char nm[96] = "";
            sscanf_s(p, "%d %d %d %95[^\r\n]", &id, &ty, &tok, nm, (unsigned)sizeof(nm));
            int n = toBuf(nm);
            void* a[3] = {&ty, &tok, &n};
            JrCall("AddMeth", 3, a);
            if (id < JR_MAX_METH) strcpy_s(g_jrRow[id].name, nm), metaN = max(metaN, id + 1);
        } else if (!strcmp(tag, "input")) {
            memset(&in, 0, sizeof(in));
            in.selfKind = 'n';
            sscanf_s(p, "%d %d %d %d", &in.id, &in.meth, &in.hasExpect, &in.expect);
            g_jrObjN = 0, g_jrFixN = 0, g_jrPoolN = 0;
        } else if (!strcmp(tag, "obj") && g_jrObjN < JR_OBJS) {
            JrObj& o = g_jrObj[g_jrObjN];
            int idx = 0, consumed = 0;
            memset(&o, 0, sizeof(o));
            sscanf_s(p, "%d %d %d %lu %n", &idx, &o.type, &o.len, &o.full, &consumed);
            if (g_jrPoolN + (1u << 21) > JR_POOL) { Log("jit replay: input %d over the pool, skipped\n", in.id); tot[4]++; continue; }
            o.data = g_jrPool + g_jrPoolN;
            o.n = (DWORD)JrHex(p + consumed, o.data);
            g_jrPoolN += (o.n + 7) & ~3u;
            g_jrObjN++;
        } else if (!strcmp(tag, "ref") && g_jrFixN < JR_FIX) {
            JrFix& x = g_jrFix[g_jrFixN++];
            sscanf_s(p, "%d %lu %d", &x.obj, &x.off, &x.to);
        } else if (!strcmp(tag, "cut") && g_jrFixN < JR_FIX) {
            JrFix& x = g_jrFix[g_jrFixN++];
            sscanf_s(p, "%d %lu", &x.obj, &x.off);
            x.to = -1;
        } else if (!strcmp(tag, "self")) {
            sscanf_s(p, " %c %d", &in.selfKind, 1, &in.selfI);
        } else if (!strcmp(tag, "arg") && in.nArg < 32) {
            auto& s = in.arg[in.nArg++];
            s.i = -1;
            sscanf_s(p, "%d %lx %lx %c %d", &s.k, &s.lo, &s.hi, &s.kind, 1, &s.i);
        } else if (!strcmp(tag, "static") && in.nStatic < 8) {
            auto& s = in.st[in.nStatic++];
            s.i = -1;
            sscanf_s(p, "%d %lx %lx %c %d", &s.k, &s.lo, &s.hi, &s.kind, 1, &s.i);
        } else if (!strcmp(tag, "end") && in.meth < JR_MAX_METH) {
            __try { JrRun(in, tot); }
            __except (Log("jit replay: input %d: host fault %08lX while rebuilding\n", in.id, GetExceptionCode()), EXCEPTION_EXECUTE_HANDLER) { g_jrRow[in.meth].skipped++, tot[4]++, tot[5] = 1; }
        }
    }
    fclose(f);
    JitSetPatched(true);
    for (int m = 0; m < metaN; m++) {
        const JrRow& r = g_jrRow[m];
        if (r.inputs) Log("jit replay method %s: inputs %d, ran %d, differ %d, unstable %d, skipped %d, objects not rebuilt %d\n", r.name, r.inputs, r.ran, r.differ, r.unstable, r.skipped, r.cuts);
    }
    for (int i = 0; i < g_jrWhyN; i++) Log("jit replay reason %s: %s x%d\n", g_jrRow[g_jrWhy[i].meth].name, g_jrWhy[i].why, g_jrWhy[i].n);
    Log("jit replay: %d inputs, %d ran, %d differ, %d unstable, %d skipped%s; per-call verify: ok %ld, MISMATCH %ld, abandoned (exception) %ld\n",
        tot[0], tot[1], tot[3], tot[2], tot[4], tot[5] ? ", STOPPED on a fault" : "", g_jvOk - ok0, g_jvMismatch - mm0, g_jvAbandoned - ab0);
}
