// Crash S task-rooting evidence (docs/67 §22.4, ts3native 0.36.96). Included by ts3native.cpp (one translation unit).
//
// Crash S (docs/67 §22-§22.3) reads a freed delegate that only a yielded task's interpreter frames held: one collection
// missed the whole task. Rooting as read statically (docs/67 §22.3) [verified bytes]:
//   - host+0xB84 -> task container: GC_malloc_uncollectable(0x1A0) in MonoScriptHost_Init (0xD7E625): 101 bucket heads
//     at +0, count +0x198; insert 0xD727F0 / remove 0xD71EB0 hash the key by % 101; node = GC_malloc(8) {next, state}.
//   - task state +0x13C = current arena chunk; chunk +0 top, +4 end, +8 previous, data from +0xC. EA's mark procedure
//     0xE69520 pushes [chunk+8, top), so a frame at or above its chunk's top is not scanned.
//   - ves_exec 0xE53EB0 keeps the previous current state at [ebp-0x60] (0xE53ECC) and sets [0x11F4170] to its own.
//   - the running task's root state: host+0xBC8 -> slot -> state; task id host+0xBD0.
// Block state from EA's libgc headers (docs/199 §2): top index -> bottom -> header; +0x10 map (free block = invalid map),
// +0x14 kind byte, +0x1C mark bits, +0x9C EA's free bits (set while the object sits on a free list).
//
// GrFaultText: one more line after "mono fault:" (container membership of the current, outer and root state, their
// chains, every frame's arena base vs its chunk's top, the receiver's block state). [GcRootCheck] Enabled=1 (off): at
// every stopped mark (GC_notify_event 1, after GC_stop_world, before the final root push) walks the container and
// checks each state's chain, plus the running task's frames; first 16 mismatches logged; minute line "gc root check:".
// 0.36.97 (docs/67 §22.5): GrAudit at event 3 (marks complete) checks every rooted frame word's object is marked.
// 0.36.98 (docs/67 §22.6): the audit splits unmarked words by block stamp (this cycle = EA keeps the block); the descriptor watch at
// event 1 checks every container, current and pooled chunk's last word against EA's descriptor; RestoreDesc=1 rewrites it.
// 0.36.99 (docs/67 §22.6.1): event 1 runs inside EA's abortable stopped mark (stop function 0xD717B0, budget ~1 ms, docs/13
// Q3): 0.36.98's second container pass plus a VirtualQuery per chunk pushed attempts past it, so after 40 aborts EA
// finished with never_stop (~700 ms full marks, the agent timed out). One pass now (watch folded into GrOnGc), chunk reads
// under SEH instead of VirtualQuery, states capped per walk, event-1 time counted (minute line, one line per slow call).

static const DWORD GR_BUCKETS = 101;
static const int GR_STATES_MAX = 8192;  // per walk over the container (real: ~400); a cyclic bucket list stops here
static const DWORD* g_grDesc = (const DWORD*)(uintptr_t)TS3A(0x011F424C);  // EA's arena chunk descriptor (0xE695E8), selftest: a fake

// append to d (room bytes) at *n, truncating (sprintf_s would abort on overflow)
static void GrAp(char* d, int room, int* n, const char* fmt, ...) {
    if (*n >= room - 1) return;
    va_list ap;
    va_start(ap, fmt);
    const int w = _vsnprintf_s(d + *n, room - *n, _TRUNCATE, fmt, ap);
    va_end(ap);
    *n = w < 0 ? room - 1 : *n + w;
}

// '?' no header, 'B' free block, 'F' on a free list, 'M' marked, '-' neither; kind = header kind byte (-1 unknown)
static char GrBlock(DWORD p, int* kind) {
    *kind = -1;
    __try {
        const DWORD* bot = p ? (const DWORD*)(uintptr_t)g_mx.top[p >> 22] : NULL;
        const u8* h = bot ? (const u8*)(uintptr_t)bot[(p >> 12) & 0x3FF] : NULL;
        if ((uintptr_t)h < 0x1000) return '?';  // none, or a forward count inside a large object
        if (*(const DWORD*)(h + 0x10) == *g_mx.invalidMap) return 'B';
        *kind = h[0x14];
        const DWORD w = (p & 0xFFF) >> 2, bit = 1u << (w & 31), off = (w >> 5) * 4;
        if (*(const DWORD*)(h + 0x9C + off) & bit) return 'F';
        return *(const DWORD*)(h + 0x1C + off) & bit ? 'M' : '-';
    } __except (EXCEPTION_EXECUTE_HANDLER) { return '?'; }
}

static const DWORD* GrTable() {
    __try {
        const u8* host = (const u8*)(uintptr_t)*g_hostPtr;
        return host ? *(const DWORD* const*)(host + 0xB84) : NULL;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
}
static DWORD GrRootState() {  // running task's root state, 0 between tasks
    __try {
        const u8* host = (const u8*)(uintptr_t)*g_hostPtr;
        const DWORD* slot = host ? *(const DWORD* const*)(host + 0xBC8) : NULL;
        return slot ? *slot : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// 1 = state in the container, 0 = not, -1 = no container or unreadable
static int GrInTable(const DWORD* tbl, DWORD state) {
    if (!tbl || !state) return -1;
    __try {
        int n = 0;
        for (const DWORD* nd = (const DWORD*)(uintptr_t)tbl[state % GR_BUCKETS]; nd && n < 65536; nd = (const DWORD*)(uintptr_t)nd[0], n++)
            if (nd[1] == state) return 1;
        return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}

// chunk index (0 = current) of state's chain whose data [c+0xC, end) holds addr, -1 none; *top = that chunk's top
static int GrChunkOf(DWORD state, DWORD addr, DWORD* top) {
    if (!state) return -1;
    __try {
        int i = 0;
        for (const DWORD* c = (const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)state)[0x13C / 4]; c && i < 64; c = (const DWORD*)(uintptr_t)c[2], i++)
            if (addr >= (DWORD)(uintptr_t)c + 0xC && addr < c[1]) { *top = c[0]; return i; }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return -1;
}

// First bad chunk of state's chain into why (NULL = none): unreadable, top outside [data, end], freed block, loop
static const char* GrChainBad(DWORD state, int* chunks, DWORD* bad) {
    *chunks = 0, *bad = 0;
    __try {
        const DWORD* c = (const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)state)[0x13C / 4];
        for (; c; c = (const DWORD*)(uintptr_t)c[2]) {
            *bad = (DWORD)(uintptr_t)c;
            if (++*chunks > 256) return "chain longer than 256 (loop?)";
            DWORD w[2];  // SEH, not VirtualQuery (Readable): this runs inside EA's ~1 ms stopped mark (docs/67 §22.6.1)
            __try { w[0] = c[0], w[1] = c[1]; } __except (EXCEPTION_EXECUTE_HANDLER) { return "chunk unreadable"; }
            if (w[1] <= (DWORD)(uintptr_t)c + 0xC || w[0] < (DWORD)(uintptr_t)c + 0xC || w[0] > w[1]) return "top outside the chunk";
            int kind;
            const char b = GrBlock((DWORD)(uintptr_t)c, &kind);
            if (b == 'F' || b == 'B') return "chunk on a GC free list";
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return "state unreadable"; }
    *bad = 0;
    return NULL;
}

static int GrChainText(char* d, int room, DWORD state) {  // "C top T end E < C2 top ..." up to 4 chunks
    int n = 0;
    d[0] = 0;
    __try {
        const DWORD* c = state ? (const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)state)[0x13C / 4] : NULL;
        if (!c) GrAp(d, room, &n, "empty");
        for (int i = 0; c && i < 4; c = (const DWORD*)(uintptr_t)c[2], i++)
            GrAp(d, room, &n, "%s%08lX top %08lX end %08lX", i ? " < " : "", (DWORD)(uintptr_t)c, c[0], c[1]);
        if (c) GrAp(d, room, &n, " < ...");
    } __except (EXCEPTION_EXECUTE_HANDLER) { GrAp(d, room, &n, "%sunreadable", n ? " < " : ""); }
    return n;
}

// Frames of this thread's interpreter context (innermost first): "base:<state letter><chunk>" and '!' when the base is at
// or above that chunk's top (not scanned); "-" = in none of the given chains. Returns frames read; *above counts '!'.
static int GrFramesText(char* d, int room, const DWORD* states, const char* letters, int ns, int* above) {
    int got = 0, n = 0;
    *above = 0;
    d[0] = 0;
    __try {
        const u8* ctx = g_lgCtx();
        for (const DWORD* f = ctx ? *(const DWORD* const*)(ctx + 8) : NULL; f && got < 16; f = (const DWORD*)(uintptr_t)f[0], got++) {
            const DWORD base = f[4];  // MonoInvocation +0x10: arena base (args, locals, eval stack)
            int s = 0, ci = -1;
            DWORD top = 0;
            for (; s < ns && (ci = GrChunkOf(states[s], base, &top)) < 0; s++) {}
            if (ci < 0) GrAp(d, room, &n, "%s%08lX:-", got ? " " : "", base);
            else {
                const bool hi = base >= top;
                *above += hi;
                GrAp(d, room, &n, "%s%08lX:%c%d%s", got ? " " : "", base, letters[s], ci, hi ? "!" : "");
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { GrAp(d, room, &n, " unreadable"); }
    return got;
}

static void GrStateText(char* d, int room, int* n, const char* what, DWORD state, const DWORD* tbl) {
    int kind;
    const char b = GrBlock(state, &kind);
    char ch[200];
    GrChainText(ch, sizeof(ch), state);
    GrAp(d, room, n, "%s %08lX (container %d, gc %c/k%d; chain %s)", what, state, GrInTable(tbl, state), b, kind, ch);
}

// docs/67 §22.4: the task line after "mono fault:" (interpreter range only, as MonoFaultText)
static bool GrFaultText(const CONTEXT* c, char* d, int room) {
    if (c->Eip < TS3A(0x00E51000) || c->Eip >= TS3A(0x00E5A600)) return false;
    const DWORD* tbl = GrTable();
    DWORD cur = 0, outer = 0, recv = 0, cnt = 0, id[2] = {0, 0};
    __try { cur = *g_curState; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { outer = *(const DWORD*)(uintptr_t)(c->Ebp - 0x60); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (c->Eip >= TS3A(0x00E51FC0) && c->Eip < TS3A(0x00E520C0)) {
        __try { recv = *(const DWORD*)(uintptr_t)(c->Ebp - 0x28); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    __try { if (tbl) cnt = tbl[0x198 / 4]; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try {
        const u8* host = (const u8*)(uintptr_t)*g_hostPtr;
        if (host) id[0] = *(const DWORD*)(host + 0xBD0), id[1] = *(const DWORD*)(host + 0xBD4);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const DWORD root = GrRootState();
    const DWORD states[3] = {cur, outer, root};
    char fr[420];
    int above, rk;
    GrFramesText(fr, sizeof(fr), states, "cor", 3, &above);
    const char rb = GrBlock(recv, &rk);
    int n = 0;
    d[0] = 0;
    GrAp(d, room, &n, "mono fault task: container %08lX (%lu states), task %08lX%08lX; ", (DWORD)(uintptr_t)tbl, cnt, id[1], id[0]);
    GrStateText(d, room, &n, "current", cur, tbl);
    GrStateText(d, room, &n, "; outer", outer, tbl);
    GrStateText(d, room, &n, "; root", root, tbl);
    GrAp(d, room, &n, "; frames (c/o/r chunk, ! = at or above top) %s; %d above top; receiver %08lX gc %c/k%d", fr, above, recv, rb, rk);
    // docs/67 §22.5: mark bit under the free bit (GC_free sets it, a sweep leaves it clear), block stamp vs GC_gc_no, and
    // whether each current-chain chunk still ends in EA's descriptor
    __try {
        const DWORD* bot = recv ? (const DWORD*)(uintptr_t)g_mx.top[recv >> 22] : NULL;
        const u8* h = bot ? (const u8*)(uintptr_t)bot[(recv >> 12) & 0x3FF] : NULL;
        if ((uintptr_t)h >= 0x1000) {
            const DWORD w = (recv & 0xFFF) >> 2;
            GrAp(d, room, &n, " (mark bit %d, block stamp %lu, gc %lu)", (*(const DWORD*)(h + 0x1C + (w >> 5) * 4) >> (w & 31)) & 1,
                 *(const DWORD*)(h + 0x18), *g_mx.gcNo);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try {
        int i = 0;
        GrAp(d, room, &n, "; current chain descriptors");
        for (const DWORD* k = cur ? (const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)cur)[0x13C / 4] : NULL; k && i < 8; k = (const DWORD*)(uintptr_t)k[2], i++) {
            const DWORD ch = (DWORD)(uintptr_t)k, sz = *(const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)g_mx.top[ch >> 22])[(ch >> 12) & 0x3FF];
            const DWORD lv = *(const DWORD*)(uintptr_t)(ch + sz * 4 - 4);
            if (lv == *g_grDesc) GrAp(d, room, &n, " ok");
            else GrAp(d, room, &n, " %08lX", lv);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { GrAp(d, room, &n, " unreadable"); }
    return true;
}

// --- [GcRootCheck] ---
static LONG g_grGcs, g_grGcsInTask, g_grMismatch, g_grStatesMax, g_grChunksMax, g_grLines;
typedef void(__cdecl* GrNotifyFn)(int ev);
static GrNotifyFn g_grNext;

static void GrMismatch(const char* why, DWORD state, DWORD chunk) {
    g_grMismatch++;
    if (g_grLines >= 16) return;
    g_grLines++;
    char m[120] = "?", ch[200];
    __try { if (state) FbName(m, sizeof(m), ((const DWORD*)(uintptr_t)state)[0]); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    GrChainText(ch, sizeof(ch), state);
    Log("gc root check: collection %ld: %s, state %08lX (method %s) chunk %08lX; chain %s", g_grGcs, why, state, m, chunk, ch);
}

static void GrDescChain(DWORD state);
static void GrDescRest(const DWORD* tbl);

// One stopped mark: returns mismatches found in this call (selftest); watch = also the descriptor watch (docs/67 §22.6)
// in the same container pass
static int GrOnGc(bool watch = false) {
    const LONG before = g_grMismatch;
    g_grGcs++;
    const DWORD root = GrRootState();
    if (root) g_grGcsInTask++;
    const DWORD* tbl = GrTable();
    if (!tbl) return 0;
    LONG states = 0, chunks = 0;
    __try {
        for (DWORD b = 0; b < GR_BUCKETS && states < GR_STATES_MAX; b++) {
            for (const DWORD* nd = (const DWORD*)(uintptr_t)tbl[b]; nd && states < GR_STATES_MAX; nd = (const DWORD*)(uintptr_t)nd[0]) {
                int kind, nc;
                DWORD bad;
                if (GrBlock((DWORD)(uintptr_t)nd, &kind) == 'F') GrMismatch("container node on a GC free list", nd[1], 0);
                const DWORD s = nd[1];
                states++;
                if (!s) continue;
                const char sb = GrBlock(s, &kind);
                if (sb == 'F' || sb == 'B') { GrMismatch("state on a GC free list", s, 0); continue; }
                if (const char* why = GrChainBad(s, &nc, &bad)) GrMismatch(why, s, bad);
                else if (watch) GrDescChain(s);
                chunks += nc;
            }
        }
        if (states >= GR_STATES_MAX) GrMismatch("container walk capped (loop?)", 0, 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) { GrMismatch("container unreadable", 0, 0); }
    if (states > g_grStatesMax) g_grStatesMax = states;
    if (chunks > g_grChunksMax) g_grChunksMax = chunks;
    if (watch) GrDescRest(tbl);
    if (root) {  // the running task: root in the container, live frames below their chunk's top
        if (GrInTable(tbl, root) == 0) GrMismatch("running task's root state not in the container", root, 0);
        DWORD cur = 0;
        __try { cur = *g_curState; } __except (EXCEPTION_EXECUTE_HANDLER) {}
        const DWORD st[2] = {cur, root};
        char fr[420];
        int above;
        GrFramesText(fr, sizeof(fr), st, "cr", 2, &above);
        if (above) {
            GrMismatch("running task frame at or above its chunk's top", root, 0);
            if (g_grLines < 16) g_grLines++, Log("gc root check: collection %ld frames (c/r chunk, ! = at or above top): %s", g_grGcs, fr);
        }
    }
    return (int)(g_grMismatch - before);
}

// --- docs/67 §22.5: frame-word mark audit at event 3 (marks complete, before GC_finalize / reclaim) ---
// Invariant: EA's mark proc 0xE69520 pushes every word of [chunk+8, top) of every chunk reached from a rooted state through
// 0xE508D0, which marks each word's object by the map rule below unless it is on a free list. So after the mark, a word
// in that range that resolves to an object that is neither marked nor free-listed was never pushed: that object is swept
// now while a frame holds it (Crash S). Per chunk the split: the chunk block itself unmarked (not reached from its state),
// its last word not EA's descriptor [0x11F424C] (0xE50DE0 stores it at chunk + size - 4 = +0x7FC; overwritten = the
// collector reads another descriptor for this chunk), or both fine and still a word unmarked (marker / mark-stack loss).
static LONG g_grAudits, g_grWords, g_grUnmarked, g_grChunkUnmarked, g_grDescBad, g_grAuditFaults, g_grOffThread, g_grAuditLines;
static DWORD g_grTid;

// word p's object by 0xE508D0's rule (hdr+0x10 byte map: word displacement, 0xFE = modulo size, more = none):
// 'F' free-listed, 'M' marked, '-' neither, '?' not a GC object. *start = object start, *kind = header kind byte.
static char GrObj(DWORD p, DWORD* start, int* kind) {
    *start = 0, *kind = -1;
    const DWORD* bot = p ? (const DWORD*)(uintptr_t)g_mx.top[p >> 22] : NULL;
    const u8* h = bot ? (const u8*)(uintptr_t)bot[(p >> 12) & 0x3FF] : NULL;
    if ((uintptr_t)h < 0x1000 || *(const DWORD*)(h + 0x10) == *g_mx.invalidMap) return '?';
    const DWORD d = (*(const u8* const*)(h + 0x10))[p & 0xFFF];
    DWORD w = (p & 0xFFF) >> 2;
    if (d <= 0xFD) w -= d;
    else if (d == 0xFE && *(const DWORD*)h) {
        const DWORD sz = *(const DWORD*)h;
        w -= w % sz;
        if (w + sz > 0x400) return '?';
    } else return '?';
    *start = (p & ~0xFFFu) + w * 4, *kind = h[0x14];
    const DWORD bit = 1u << (w & 31), off = (w >> 5) * 4;
    if (*(const DWORD*)(h + 0x9C + off) & bit) return 'F';
    return *(const DWORD*)(h + 0x1C + off) & bit ? 'M' : '-';
}

static DWORD GrStamp(DWORD p) {  // block stamp (hdr +0x18, GC_gc_no at the last allocation or mark clear); caller holds SEH
    const DWORD* bot = (const DWORD*)(uintptr_t)g_mx.top[p >> 22];
    return *(const DWORD*)(uintptr_t)(bot[(p >> 12) & 0x3FF] + 0x18);
}
static LONG g_grWindow;

// --- docs/67 §22.6: chunk descriptor watch at every stopped mark (event 1) ---
// The 5th Crash S (0.36.97) had its frames' chunk ending in 0 instead of [0x11F424C]: the collector then marks that chunk
// by descriptor 0 (length 0) and frees what only its frames hold. A frame never covers the last word (0xD71900 and the
// method-entry bump 0xE563B2 keep frames below end = chunk + 0x7FC), so writing the descriptor back is safe; done before
// GC_initiate_gc (0xE4C2A0) reads the write-watch bits, so the chunk is rescanned in this stopped mark.
static LONG g_grDescChecks, g_grDescBadEv1, g_grDescPool, g_grDescFixed, g_grDescLines, g_grDescPoolCapped;
static LONG g_grEv1Us, g_grEv1MaxUs, g_grEv1Slow;  // event-1 handler time (EA's stopped-mark budget ~1000 us, docs/13 Q3)
static int g_grRestoreDesc;
static DWORD* g_grPool = (DWORD*)(uintptr_t)TS3A(0x011EE508);  // EA's chunk pool: sentinel node, node {next, prev, chunk}

static DWORD GrDescAt(DWORD chunk) {  // address of chunk's last word (block size from its header); caller holds SEH
    const DWORD* bot = (const DWORD*)(uintptr_t)g_mx.top[chunk >> 22];
    return chunk + *(const DWORD*)(uintptr_t)bot[(chunk >> 12) & 0x3FF] * 4 - 4;
}

// Yielded frames (state +0x140 = deepest saved depth, cells at +0x3C + 4*depth: cell +0 RuntimeMethod, +0x20 arena base)
// inside chunk c: "Method base+size" each, '*' when the frame ends at the chunk's end (docs/130 §10.5)
static void GrCellsText(char* d, int room, DWORD state, DWORD c, DWORD end) {
    int n = 0;
    d[0] = 0;
    __try {
        const int depth = state ? (int)((const DWORD*)(uintptr_t)state)[0x140 / 4] : -1;
        for (int i = 0; i <= depth && i < 64; i++) {
            const DWORD* cell = (const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)state)[0x3C / 4 + i];
            if (!cell) continue;
            const DWORD rm = cell[0], base = cell[0x20 / 4];
            if (base < c + 0xC || base >= end) continue;
            const DWORD size = *(const WORD*)(uintptr_t)(rm + 0x38);
            char m[100];
            FbName(m, sizeof(m), *(const DWORD*)(uintptr_t)(rm + 8));
            GrAp(d, room, &n, "%s%d:%s %08lX+%lX%s", n ? ", " : "", i, m, base, size, base + size >= end ? "*" : "");
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { GrAp(d, room, &n, " unreadable"); }
    if (!n) GrAp(d, room, &n, "none");
}

static void GrDescOne(DWORD state, DWORD c) {  // caller holds SEH
    g_grDescChecks++;
    const DWORD at = GrDescAt(c), v = *(const DWORD*)(uintptr_t)at, want = *g_grDesc;
    if (v == want) return;
    g_grDescBadEv1++;
    if (!state) g_grDescPool++;
    if (g_grDescLines < 8) {
        g_grDescLines++;
        const DWORD* w = (const DWORD*)(uintptr_t)at;
        char m[120] = "pool", cells[1100];  // 0.36.99 cut the list after 6 frames (docs/67 §22.7)
        if (state) FbName(m, sizeof(m), ((const DWORD*)(uintptr_t)state)[0]);
        GrCellsText(cells, sizeof(cells), state, c, ((const DWORD*)(uintptr_t)c)[1]);
        Log("gc desc watch: collection %ld: chunk %08lX ends in %08lX (want %08lX)%s, state %08lX (method %s), top %08lX end %08lX, "
            "words before %08lX %08lX %08lX; saved frames in it (depth:method base+size, * = ends at end) %s",
            g_grGcs, c, v, want, g_grRestoreDesc ? ", restored" : "", state, m, ((const DWORD*)(uintptr_t)c)[0],
            ((const DWORD*)(uintptr_t)c)[1], w[-3], w[-2], w[-1], cells);
    }
    if (g_grRestoreDesc) *(DWORD*)(uintptr_t)at = want, g_grDescFixed++;
}

static void GrDescChain(DWORD state) {  // a chain GrChainBad passed (<= 256 chunks)
    __try {
        for (const DWORD* c = (const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)state)[0x13C / 4]; c; c = (const DWORD*)(uintptr_t)c[2])
            GrDescOne(state, (DWORD)(uintptr_t)c);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (++g_grAuditFaults <= 4) Log("gc desc watch: collection %ld: read fault at state %08lX", g_grGcs, state);
    }
}

// the current state when not in the container (GrOnGc's pass covers the rest) and every chunk in EA's pool (<= 4096 nodes)
static void GrDescRest(const DWORD* tbl) {
    DWORD state = 0;
    __try {
        state = *g_curState;
        int nc, kind;
        DWORD bad;
        if (state && GrInTable(tbl, state) != 1 && GrBlock(state, &kind) != 'F' && !GrChainBad(state, &nc, &bad)) GrDescChain(state);
        state = 0;
        const DWORD sent = *g_grPool;
        int n = 0;
        for (const DWORD* nd = sent ? (const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)sent)[0] : NULL; nd && (DWORD)(uintptr_t)nd != sent && n < 4096;
             nd = (const DWORD*)(uintptr_t)nd[0], n++)
            if (nd[2]) GrDescOne(0, nd[2]);
        if (n >= 4096) g_grDescPoolCapped++;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (++g_grAuditFaults <= 4) Log("gc desc watch: collection %ld: read fault at state %08lX", g_grGcs, state);
    }
}

static void GrAuditLine(const char* why, DWORD state, DWORD chunk, DWORD at, DWORD val, int kind) {
    if (g_grAuditLines >= 16) return;
    g_grAuditLines++;
    char m[120] = "?", ch[200];
    __try { if (state) FbName(m, sizeof(m), ((const DWORD*)(uintptr_t)state)[0]); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    GrChainText(ch, sizeof(ch), state);
    int ck;
    const char cb = GrBlock(chunk, &ck);
    Log("gc frame audit: collection %ld: %s, state %08lX (method %s) chunk %08lX (gc %c/k%d) word %08lX = %08lX (k%d); chain %s",
        g_grGcs, why, state, m, chunk, cb, ck, at, val, kind, ch);
}

// One completed mark: returns the findings of this call (selftest)
static int GrAudit() {
    const LONG before = g_grUnmarked + g_grChunkUnmarked + g_grDescBad;
    g_grAudits++;
    const DWORD* tbl = GrTable();
    if (!tbl) return 0;
    DWORD state = 0, chunk = 0;
    __try {
        const DWORD desc = *g_grDesc;
        int states = 0;
        for (DWORD b = 0; b < GR_BUCKETS; b++) {
            for (const DWORD* nd = (const DWORD*)(uintptr_t)tbl[b]; nd && states < GR_STATES_MAX; nd = (const DWORD*)(uintptr_t)nd[0], states++) {
                state = nd[1];
                int nc, kind;
                DWORD bad;
                if (!state || GrBlock(state, &kind) == 'F' || GrChainBad(state, &nc, &bad)) continue;  // [GcRootCheck]'s mismatches
                for (const DWORD* c = (const DWORD*)(uintptr_t)((const DWORD*)(uintptr_t)state)[0x13C / 4]; c; c = (const DWORD*)(uintptr_t)c[2]) {
                    chunk = (DWORD)(uintptr_t)c;
                    DWORD s;
                    const char cb = GrObj(chunk, &s, &kind);
                    if (cb != 'M' || s != chunk) { g_grChunkUnmarked++, GrAuditLine("chunk not marked", state, chunk, chunk, 0, kind); continue; }
                    const DWORD* bot = (const DWORD*)(uintptr_t)g_mx.top[chunk >> 22];
                    const DWORD sz = *(const DWORD*)(uintptr_t)bot[(chunk >> 12) & 0x3FF];  // header +0: size in words
                    const DWORD last = chunk + sz * 4 - 4, lv = *(const DWORD*)(uintptr_t)last;
                    if (lv != desc) g_grDescBad++, GrAuditLine("chunk descriptor overwritten", state, chunk, last, lv, kind);
                    for (DWORD at = chunk + 8; at < c[0]; at += 4) {
                        const DWORD v = *(const DWORD*)(uintptr_t)at;
                        g_grWords++;
                        if (GrObj(v, &s, &kind) != '-' || kind == 2 || kind == 3) continue;  // 2/3 uncollectable: never swept
                        // docs/67 §22.6: EA defers the finish past the mark (0xE49DA0 bumps GC_gc_no, 0xE49F10 finishes on a
                        // later call); every allocation stamps its block with GC_gc_no (0xE4C1E0, new blocks 0xE48FB0) and
                        // reclaim skips blocks stamped this cycle (0xE500B6, 0xE50182): such a word is not swept now
                        if (GrStamp(v) == *g_mx.gcNo) g_grWindow++;
                        else g_grUnmarked++, GrAuditLine("frame word to an unmarked object", state, chunk, at, v, kind);
                    }
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (++g_grAuditFaults <= 4) Log("gc frame audit: collection %ld: read fault at state %08lX chunk %08lX", g_grGcs, state, chunk);
    }
    return (int)(g_grUnmarked + g_grChunkUnmarked + g_grDescBad - before);
}

static void __cdecl GrNotify(int ev) {
    if (ev == 1) {  // after GC_stop_world in GC_stopped_mark, before the final root push (docs/199 §2)
        const DWORD me = GetCurrentThreadId();
        if (!g_grTid) g_grTid = me;
        else if (me != g_grTid && ++g_grOffThread <= 4) Log("gc root check: collection %ld on thread %lu (first collector %lu)", g_grGcs + 1, me, g_grTid);
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        GrOnGc(true);  // + docs/67 §22.6 descriptor watch: before GC_initiate_gc reads the dirty bits
        const LONG us = UsSince(t0);
        g_grEv1Us += us;
        Max(&g_grEv1MaxUs, us);
        if (us >= 300 && ++g_grEv1Slow <= 4)  // docs/67 §22.6.1: EA aborts the stopped mark past ~1000 us
            Log("gc root check: collection %ld: event 1 took %ld us (states max %ld, chunks max %ld)", g_grGcs, us, g_grStatesMax, g_grChunksMax);
    }
    if (ev == 3) GrAudit();  // top of GC_finish_collection: marks complete, nothing reclaimed yet (docs/199 §2)
    if (g_grNext) g_grNext(ev);
}

static void GrLogCounters() {
    if (!g_grNext) return;
    Log("gc root check: collections %ld (task running %ld, other thread %ld), states max %ld, chunks max %ld, mismatches %ld", g_grGcs,
        g_grGcsInTask, g_grOffThread, g_grStatesMax, g_grChunksMax, g_grMismatch);
    Log("gc frame audit: marks %ld, frame words %ld, unmarked %ld (block stamped before this cycle: swept), window %ld (block stamped this "
        "cycle: EA keeps it), chunks not marked %ld, descriptors overwritten %ld, read faults %ld",
        g_grAudits, g_grWords, g_grUnmarked, g_grWindow, g_grChunkUnmarked, g_grDescBad, g_grAuditFaults);
    Log("gc desc watch: chunk checks %ld, bad descriptors %ld (pool %ld), restored %ld (RestoreDesc=%d), pool walks capped %ld; event 1 "
        "time %ld ms, max %ld us, >= 300 us %ld", g_grDescChecks, g_grDescBadEv1, g_grDescPool, g_grDescFixed, g_grRestoreDesc,
        g_grDescPoolCapped, g_grEv1Us / 1000, g_grEv1MaxUs, g_grEv1Slow);
}

// After ApplyMemExp: chains whatever the GC_notify_event store (or the pointer, if init already ran) holds.
static bool ApplyGcRootCheck(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"GcRootCheck", L"Enabled", 0, ini)) { Log("gc root check disabled (Enabled=0)"); return false; }
    g_grRestoreDesc = GetPrivateProfileIntW(L"GcRootCheck", L"RestoreDesc", 0, ini) ? 1 : 0;  // docs/67 §22.6
    static const u8 kStore[6] = {0xC7, 0x05, 0x60, 0x43, 0x23, 0x01};  // mov [GC_notify_event], imm32 (0xD7E62A)
    u8* site = (u8*)(uintptr_t)TS3A(0x00D7E62A);
    DWORD* slot = (DWORD*)(uintptr_t)TS3A(0x01234360);
    const DWORD mine = (DWORD)(uintptr_t)GrNotify;
    bool ok = false;
    if (Readable(slot, 4) && *slot) g_grNext = (GrNotifyFn)(uintptr_t)*slot, ok = Write(slot, &mine, 4);
    else if (Matches(site, kStore, 6)) g_grNext = (GrNotifyFn)(uintptr_t)*(const DWORD*)(site + 6), ok = Write(site + 6, &mine, 4);
    if (!ok) { g_grNext = NULL; Log("SKIP gc root check: GC_notify_event store differs"); return false; }
    Log("OK   gc root check: GC_notify_event chained (next %08lX), RestoreDesc=%d", (DWORD)(uintptr_t)g_grNext, g_grRestoreDesc);
    return true;
}
