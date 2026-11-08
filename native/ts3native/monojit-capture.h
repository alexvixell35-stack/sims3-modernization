// [MonoJit] Capture=1 (docs/537 §17): samples the real arguments of compiled methods at entry, for tools/jit-replay.ts and the host's
// replay. Not for players, default off, not in a release ini. Included by monojit.cpp and native/monohost/monohost-jit.cpp, so the host
// runs this same code on its corpus: that is the dummy that hits the path before the game does (docs probe safety).
//
// One call: at word 0 of a compiled method (JitEnter, interpreter dispatch), at the entry of a lazy body (Direct=1: JcLazyStub, emitted
// only while capture is on) and at an inlined call site (Inline=1|2: JcInlineStub, after the site's tests). First N calls per method,
// counted over all three paths, then a reservoir of R slots: a later sample in a slot replaces the earlier one
// when the reader reads the file. What a sample holds: this and the raw argument slots, then a breadth-first walk to Depth levels of
// the objects those words name, as raw bytes with the class of each, plus the static fields the method's mint code loads (ldsfld*).
// Reference fields are not typed here: as in the verifier (JitSnapGraph) a word that is the start of a GC object is a reference.
//
// Sim-thread rules: no GC allocation, no suspend, no call into EA's runtime, no save data. Memory comes from VirtualAlloc once. A sample
// is bounded by MaxObjs, MaxBytes and ObjMax; a walk that passes a bound records a cut, not a partial object graph it cannot name.
// Cost: CaptureBudgetUs per second of sampling time, past that samples are skipped and counted. The file is written from JitTick.
// Anything odd (no class, a free cell, a pointer outside the frames) is counted by kind and skipped, and SEH drops a sample that faults.
//
// File (little endian): "JCAP" u32 version, then records u32 tag, u32 bytes, payload:
//   1 class:  id, vtable, instance size, element size, flags (1 scan for references, 2 string, 4 array), name
//   2 method: id, token, parameters, class name, name
//   3 static: id, class name, field name
//   4 sample: method, slot, path (version 2: 0 JitEnter, 1 lazy body, 2 inlined), calls seen, parameters, this, objects, cuts, cuts lost,
//             statics, 8 bytes per parameter,
//             (static id, object index) per static, (address, class id or 0 for a blob, bytes here, full bytes) per object,
//             (object index, offset, class id or 0) per cut, then the bytes of the objects, 4-aligned
// Strings: u32 length, bytes, padded to 4. A write ends on a record, so a killed game leaves a readable file.
#if !defined(JC_LOG_NL) || !defined(JC_OPNAME) || !defined(JC_ON_SIM) || !defined(JC_NEXT) || !defined(JC_METH) || !defined(JC_RM) || !defined(JC_METH_RM)
#error define JC_LOG_NL ("" game, "\n" host), JC_OPNAME(op), JC_ON_SIM, JC_NEXT(r, e), JC_METH(e), JC_RM(e) and JC_METH_RM(rm) before including monojit-capture.h
#endif

static const DWORD JC_VERSION = 2;
enum { JC_VIA_ENTER = 0, JC_VIA_LAZY = 1, JC_VIA_INLINE = 2 };
static const int JC_CLASSES = 4096, JC_METHS = 4096, JC_STATICS = 8, JC_CUTS = 512, JC_NAME = 512;
static const DWORD JC_BUF = 8u << 20, JC_BLOB = 64;
enum { JC_F_SCAN = 1, JC_F_STRING = 2, JC_F_ARRAY = 4 };
struct JcClassRow { DWORD vt; int id; u8 flags; DWORD es, is; };  // id 0: an empty row; -1: refused, not a class
struct JcMethRow { DWORD seen, taken, rng; bool named; int nStatic; const u8* field[JC_STATICS]; int sid[JC_STATICS]; };
struct JcCut { DWORD obj, off; int cls; };
struct JcStats { DWORD samples, first, reservoir, viaLazy, viaInline, budget, room, faults, fileFull, objects, cuts, cutsLost, noClass, freeCell, outside, trunc, statics, staticSkip, bytes, maxUs; ULONGLONG costUs; };
struct JcState {
    HANDLE file;
    u8* buf; DWORD n, written, maxFile;
    JitSnap* s;
    DWORD* cls, *full;       // per region of s: class id, bytes the object really has
    JcClassRow* classes;
    JcMethRow* meths;
    JcCut cut[JC_CUTS];
    int nCut, cutLost, nClass, nSid;
    int first, res, depth, maxObjs;
    DWORD objMax, maxBytes, budgetUs, usedUs, flushTick, logTick;
    LONGLONG qpf, win;
    JcStats st;
};
static JcState* g_jc;

static bool JcReadable(const void* p, DWORD n) {
    MEMORY_BASIC_INFORMATION mi;
    if (!p || !VirtualQuery(p, &mi, sizeof(mi)) || mi.State != MEM_COMMIT || (mi.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
    return (const u8*)p + n <= (const u8*)mi.BaseAddress + mi.RegionSize;
}
static void JcW(const void* p, DWORD n) { memcpy(g_jc->buf + g_jc->n, p, n); g_jc->n += n; }
static void JcW32(DWORD v) { JcW(&v, 4); }
static void JcWStr(const char* s) {
    const DWORD l = (DWORD)strlen(s), pad = (4 - (l & 3)) & 3, zero = 0;
    JcW32(l);
    JcW(s, l);
    JcW(&zero, pad);
}
static bool JcRoom(DWORD n) { return g_jc->n + n + 64 <= JC_BUF; }
static DWORD JcBegin(DWORD tag) { const DWORD at = g_jc->n; JcW32(tag); JcW32(0); return at; }
static void JcEnd(DWORD at) { *(DWORD*)(g_jc->buf + at + 4) = g_jc->n - at - 8; }

// ---- names: "Ns.Outer`2+Inner[[Arg],[Arg]]", the form tools and JitReplay.cs parse. MonoClass +0x34 name, +0x38 namespace, +0x28 nested_in,
// +0xA0 byval_arg (MonoType: +0 data, +6 type) [verified: docs/104, docs/213]; MonoGenericClass +0 class inst, +4 container; MonoGenericInst
// +4 argc, +8 pointer to the arguments [verified in the host: a dump of these words, then the corpus capture, docs/537 §17].
static bool JcAppend(char* out, DWORD cap, DWORD* at, const char* s) {
    MEMORY_BASIC_INFORMATION mi;  // metadata words were read from memory this walk cannot vouch for: no strlen over an unknown end
    if (!VirtualQuery(s, &mi, sizeof(mi)) || mi.State != MEM_COMMIT || (mi.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
    const size_t room = (const char*)mi.BaseAddress + mi.RegionSize - s, l = strnlen(s, room < 256 ? room : 256);
    if (l >= 256 || l == room || *at + l + 1 > cap) return false;
    memcpy(out + *at, s, l + 1);
    *at += (DWORD)l;
    return true;
}
static bool JcTypeName(const u8* t, char* out, DWORD cap, DWORD* at, int depth);
static bool JcRawClass(const u8* k, char* out, DWORD cap, DWORD* at, int depth) {
    if (depth > 6 || !JcReadable(k, 0xB0)) return false;
    const u8* outer = *(const u8* const*)(k + 0x28);
    if (outer) {
        if (!JcRawClass(outer, out, cap, at, depth + 1) || !JcAppend(out, cap, at, "+")) return false;
    } else {
        const char* ns = *(const char* const*)(k + 0x38);
        if (ns && *ns && (!JcAppend(out, cap, at, ns) || !JcAppend(out, cap, at, "."))) return false;
    }
    const char* nm = *(const char* const*)(k + 0x34);
    return nm && JcAppend(out, cap, at, nm);
}
static bool JcClassName(const u8* k, char* out, DWORD cap, DWORD* at, int depth) {
    if (depth > 6 || !JcReadable(k, 0xB0)) return false;
    const u8* t = k + 0xA0;
    return t[6] == 0x15 || t[6] == 0x1D || t[6] == 0x14 ? JcTypeName(t, out, cap, at, depth + 1) : JcRawClass(k, out, cap, at, depth);
}
static bool JcTypeName(const u8* t, char* out, DWORD cap, DWORD* at, int depth) {
    static const char* const kPrim[] = {"System.Void", "System.Boolean", "System.Char", "System.SByte", "System.Byte", "System.Int16", "System.UInt16",
                                        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.String"};
    if (depth > 6 || !JcReadable(t, 8)) return false;
    const BYTE b = t[6];
    const u8* data = *(const u8* const*)t;
    if (b >= 1 && b <= 0x0E) return JcAppend(out, cap, at, kPrim[b - 1]);
    if (b == 0x18) return JcAppend(out, cap, at, "System.IntPtr");
    if (b == 0x19) return JcAppend(out, cap, at, "System.UIntPtr");
    if (b == 0x1C) return JcAppend(out, cap, at, "System.Object");
    if (b == 0x11 || b == 0x12) return JcRawClass(data, out, cap, at, depth);
    if (b == 0x1D) return JcClassName(data, out, cap, at, depth) && JcAppend(out, cap, at, "[]");
    if (b != 0x15 || !JcReadable(data, 8)) return false;  // 0x14 ARRAY (rank > 1), VAR, MVAR, and the rest: no name
    const u8* inst = *(const u8* const*)data;
    if (!JcReadable(inst, 12) || !JcRawClass(*(const u8* const*)(data + 4), out, cap, at, depth) || !JcAppend(out, cap, at, "[")) return false;
    const DWORD argc = *(const DWORD*)(inst + 4) & 0x3FFFFF;
    const u8* argv = *(const u8* const*)(inst + 8);
    if (argc > 16 || !JcReadable(argv, 4 * argc)) return false;
    for (DWORD i = 0; i < argc; i++) {
        if (i && !JcAppend(out, cap, at, ",")) return false;
        if (!JcAppend(out, cap, at, "[") || !JcTypeName(*(const u8* const*)(argv + 4 * i), out, cap, at, depth + 1) || !JcAppend(out, cap, at, "]")) return false;
    }
    return JcAppend(out, cap, at, "]");
}

// a vtable word: exact identity through the class's runtime info (MonoClass +0xB8: u16 max domain, vtables from +4; docs/199 §1), then the
// class row, written once. A word that fails stays refused: `cls` of a block is never guessed.
static JcClassRow* JcClassOf(DWORD vt) {
    JcState& g = *g_jc;
    DWORD i = (vt >> 3) & (JC_CLASSES - 1);
    for (int probe = 0; probe < 64; probe++, i = (i + 1) & (JC_CLASSES - 1)) {
        JcClassRow& c = g.classes[i];
        if (c.vt == vt) return c.id > 0 ? &c : NULL;
        if (c.vt) continue;
        char name[JC_NAME];
        DWORD at = 0;
        name[0] = 0;
        const u8* k = JcReadable((void*)(uintptr_t)vt, 16) ? *(const u8* const*)(uintptr_t)vt : NULL;
        const u8* ri = k && JcReadable(k, 0xC0) ? *(const u8* const*)(k + 0xB8) : NULL;
        bool ok = ri && JcReadable(ri, 8) && *(const char* const*)(k + 0x34) != NULL;
        if (ok) {
            const DWORD md = *(const unsigned short*)ri;
            ok = false;
            if (md < 16 && JcReadable(ri, 8 + 4 * md))
                for (DWORD d = 0; d <= md; d++) ok = ok || *(const DWORD*)(ri + 4 + 4 * d) == vt;
        }
        ok = ok && JcClassName(k, name, sizeof(name), &at, 0) && JcRoom(JC_NAME + 32);
        c.vt = vt;
        if (!ok) { c.id = -1; return NULL; }
        const u8* t = k + 0xA0;
        c.id = ++g.nClass;
        c.is = *(const DWORD*)(k + 0x54);
        c.es = *(const DWORD*)(k + 0x5C);
        c.flags = JC_F_SCAN;
        if (t[6] == 0x0E) c.flags = JC_F_STRING;
        else if (t[6] == 0x1D) {
            const u8* ek = *(const u8* const*)t;  // element class: a primitive element has no references to follow
            const BYTE eb = JcReadable(ek, 0xB0) ? ek[0xA0 + 6] : 0;
            c.flags = (u8)(JC_F_ARRAY | (eb >= 2 && eb <= 0x0D ? 0 : JC_F_SCAN));
        }
        const DWORD rec = JcBegin(1);
        JcW32((DWORD)c.id); JcW32(vt); JcW32(c.is); JcW32(c.es); JcW32(c.flags); JcWStr(name);
        JcEnd(rec);
        return &c;
    }
    return NULL;
}
// the object's own bytes: the slack of a GC cell is not its state (JitAllocEqual's rule)
static DWORD JcUsed(const u8* b, const JcClassRow& c, DWORD block) {
    DWORD used = block;
    if (c.flags & JC_F_STRING) used = 8 + 2 * *(const DWORD*)(b + 4);
    else if (c.flags & JC_F_ARRAY) used = 0x10 + *(const DWORD*)(b + 8) * c.es;
    else if (c.is >= 4) used = c.is;
    return used <= block ? used : block;
}
static bool JcAddObj(DWORD v) {
    JcState& g = *g_jc;
    JitSnap& s = *g.s;
    if (s.Contains(v)) return true;
    const u8* b = (const u8*)(uintptr_t)v;
    if (JitFreeCell(b)) { g.st.freeCell++; return false; }
    const DWORD block = GcSize(b);
    if (!block || s.n >= g.maxObjs) return false;
    const JcClassRow* c = JcClassOf(*(const DWORD*)b);
    if (!c) { g.st.noClass++; return false; }
    const DWORD used = JcUsed(b, *c, block), n = used < g.objMax ? used : g.objMax;
    if (n < used) g.st.trunc++;
    if (!s.Add((u8*)b, n)) return false;
    g.cls[s.n - 1] = (DWORD)c->id, g.full[s.n - 1] = used;
    return true;
}
static void JcAddBlob(const u8* p, DWORD n) {
    JcState& g = *g_jc;
    if (g.s->n < g.maxObjs && g.s->Add((u8*)p, n)) g.cls[g.s->n - 1] = 0, g.full[g.s->n - 1] = n;
}
// a this or argument word: an object, a value in a frame's arena, or a pointer into a GC object. Frames first: a chunk of arena is one
// GC block, so GC_base says "object" for a byref into it. Value types travel as a pointer, so a slot pointing into a frame is one.
// vtThis: the value size of a value-type this. EA's native invoke passes such a this as a pointer to C-stack memory, in no frame and
// no GC block (RuntimeTypeHandle.get_Value: every sample read "null this", docs/537 §19); its bytes become a blob.
static void JcRoot(DWORD w, const u8* inv, DWORD vtThis = 0) {
    if (w < 0x10000) return;
    for (const u8* f = inv; f; f = *(const u8* const*)f) {
        const u8* frm = *(const u8* const*)(f + 4);
        const u8* base = *(const u8* const*)(f + 0x10);
        const DWORD sz = frm ? *(const unsigned short*)(frm + 0x38) : 0;
        if (base && w >= (DWORD)(uintptr_t)base && w < (DWORD)(uintptr_t)base + sz) {
            const DWORD n = (DWORD)(uintptr_t)base + sz - w;
            JcAddBlob((const u8*)(uintptr_t)w, n < JC_BLOB ? n : JC_BLOB);
            return;
        }
    }
    const u8* b = GcBase((void*)(uintptr_t)w);
    const DWORD vn = vtThis < JC_BLOB ? vtThis : JC_BLOB;
    if (!b && vn && JcReadable((const void*)(uintptr_t)w, vn)) { JcAddBlob((const u8*)(uintptr_t)w, vn); return; }
    if (!b) { g_jc->st.outside++; return; }
    if (JcAddObj((DWORD)(uintptr_t)b) || b == (const u8*)(uintptr_t)w) return;  // the reader turns a pointer into a kept object into a blob
    const DWORD block = GcSize(b), off = (DWORD)((const u8*)(uintptr_t)w - b), n = block > off ? block - off : 0;  // the rest of an arena block
    if (n) JcAddBlob((const u8*)(uintptr_t)w, n < JC_BLOB ? n : JC_BLOB);
}
static void JcWalk() {
    JcState& g = *g_jc;
    JitSnap& s = *g.s;
    int lo = 0;
    for (int depth = 0; depth <= g.depth; depth++) {
        const int hi = s.n;
        for (int i = lo; i < hi; i++) {
            const JcClassRow* c = g.cls[i] ? JcClassOf(*(const DWORD*)s.r[i].at) : NULL;
            if (g.cls[i] && !(c && (c->flags & JC_F_SCAN))) continue;
            const u8* p = s.r[i].at;
            for (DWORD o = g.cls[i] ? 4 : 0; o + 4 <= s.r[i].n; o += 4) {
                const DWORD v = *(const DWORD*)(p + o);
                if (v < 0x10000 || (v & 3) || GcBase((void*)(uintptr_t)v) != (u8*)(uintptr_t)v) continue;
                if (depth < g.depth && JcAddObj(v)) continue;
                if (s.Contains(v)) continue;
                g.st.cuts++;
                if (g.nCut >= JC_CUTS) { g.cutLost++; g.st.cutsLost++; continue; }
                g.cut[g.nCut++] = {(DWORD)i, o, 0};
            }
        }
        lo = hi;
    }
}

// the static fields this method's mint code loads: once per method, from the positions JitCompile recorded. Item = MonoClassField
// (+0 type, +8 offset, +0xC name, +0x14 parent) [verified: monojit.cpp's K_LDSFLD for +0, +8, +0x14; +0xC from a dump in the host: s_f, s_d, s_i].
// The vtable comes from the runtime info: mono_class_vtable can allocate, and a class with no vtable yet has no value to read.
static const u8* JcStaticBase(const u8* k) {
    const u8* ri = JcReadable(k, 0xC0) ? *(const u8* const*)(k + 0xB8) : NULL;
    if (!ri || !JcReadable(ri, 8)) return NULL;
    const DWORD md = *(const unsigned short*)ri;
    for (DWORD d = 0; d <= md && d < 16; d++) {
        const u8* vt = *(const u8* const*)(ri + 4 + 4 * d);
        if (vt && JcReadable(vt, 16)) return *(const u8* const*)(vt + 0xC);
    }
    return NULL;
}
static void JcScanStatics(JcMethRow& mr, const JitMeth& m) {
    JcState& g = *g_jc;
    const DWORD* items = *(const DWORD* const*)(m.rm + 0x14);
    for (int k = m.pos0; k < m.posN && mr.nStatic < JC_STATICS; k++) {
        const DWORD op = g_jitOrigW[k];
        const char* nm = op <= 0x1A8 ? JC_OPNAME(op) : NULL;
        if (!nm || strncmp(nm, "ldsfld", 6)) continue;
        const u8* f = (const u8*)(uintptr_t)items[m.code[g_jitPos[k] + 1]];
        bool dup = !JcReadable(f, 0x18);
        for (int j = 0; j < mr.nStatic && !dup; j++) dup = mr.field[j] == f;
        if (dup) continue;
        char cn[JC_NAME];
        DWORD at = 0;
        const char* fname = *(const char* const*)(f + 0xC);
        const u8* parent = *(const u8* const*)(f + 0x14);
        if (!fname || !JcReadable(fname, 1) || !JcClassName(parent, cn, sizeof(cn), &at, 0) || !JcRoom(JC_NAME * 2)) { g.st.staticSkip++; continue; }
        mr.field[mr.nStatic] = f;
        mr.sid[mr.nStatic++] = ++g.nSid;
        const DWORD rec = JcBegin(3);
        JcW32((DWORD)g.nSid); JcWStr(cn); JcWStr(fname);
        JcEnd(rec);
    }
}
// the field's value, as a blob, into the walk; returns its object index or -1
static int JcAddStatic(const u8* f) {
    JcState& g = *g_jc;
    const u8* type = *(const u8* const*)f;
    const int off = *(const int*)(f + 8);
    if (off < 0 || JitSpecialStatic(f)) { g.st.staticSkip++; return -1; }
    const u8* base = JcStaticBase(*(const u8* const*)(f + 0x14));
    if (!base) { g.st.staticSkip++; return -1; }
    DWORD n = 0;
    switch (JitTypeWidth(type)) {
    case 1: case 2: n = 1; break;
    case 3: case 4: n = 2; break;
    case 5: case 7: n = 4; break;
    case 6: n = 8; break;
    default:
        if (type[6] == 0x11 || type[6] == 0x15) {  // a value type: the class's size less the object header
            const u8* k = type[6] == 0x11 ? *(const u8* const*)type : NULL;
            n = k && JcReadable(k, 0x58) ? *(const DWORD*)(k + 0x54) - 4 : 0;
        }
    }
    if (!n || n > JC_BLOB) { g.st.staticSkip++; return -1; }
    const int before = g.s->n;
    JcAddBlob(base + off, n);
    return g.s->n > before ? before : -1;
}

// self and sargs (8 bytes per parameter) come from the path: the MonoInvocation (JitEnter), the call site's block (lazy body), the
// caller's eval stack (inlined). inv is the innermost written frame in all three; a lazy chain's pending frames are arena blocks.
static void JcSampleBody(JitRegs* r, JcMethRow& mr, const JitMeth& m, DWORD slot, DWORD via, DWORD self, const u8* sargs) {
    JcState& g = *g_jc;
    JitSnap& s = *g.s;
    const u8* rm = m.rm;
    const u8* inv = (const u8*)(uintptr_t)*(const DWORD*)(uintptr_t)(r->ebp - 8);
    const DWORD np = sargs ? rm[0x40] : 0;
    int stIdx[JC_STATICS];
    s.Clear(), g.nCut = 0, g.cutLost = 0;
    if (!mr.named) {
        char cn[JC_NAME];
        DWORD at = 0;
        const u8* mm = *(const u8* const*)(rm + 8);
        if (!JcRoom(JC_NAME + 128)) { g.st.room++; return; }
        if (!JcClassName(*(const u8* const*)(mm + 8), cn, sizeof(cn), &at, 0)) cn[0] = 0;
        const DWORD rec = JcBegin(2);
        JcW32((DWORD)(&m - g_jitMeth)); JcW32(*(const DWORD*)(mm + 4)); JcW32(rm[0x40]); JcWStr(cn); JcWStr(m.name);
        JcEnd(rec);
        JcScanStatics(mr, m);
        mr.named = true;
    }
    DWORD vtThis = 0;
    if (*(const DWORD*)(rm + 0x44) & 0x400) {  // a value-type this (the rm flag EmitLazyCall reads as vtm): the class's size less the header
        const u8* k = *(const u8* const*)(*(const u8* const*)(rm + 8) + 8);
        vtThis = k && JcReadable(k, 0x58) && *(const DWORD*)(k + 0x54) > 4 ? *(const DWORD*)(k + 0x54) - 4 : 0;
    }
    JcRoot(self, inv, vtThis);
    for (DWORD i = 0; i < np; i++) JcRoot(*(const DWORD*)(sargs + 8 * i), inv);
    for (int i = 0; i < mr.nStatic; i++) stIdx[i] = JcAddStatic(mr.field[i]);
    JcWalk();
    for (int i = 0; i < g.nCut; i++) {  // the class of what was left out, for the reader's per-method reasons
        const DWORD v = *(const DWORD*)(s.r[g.cut[i].obj].at + g.cut[i].off);
        const DWORD w0 = *(const DWORD*)(uintptr_t)v;
        const JcClassRow* c = w0 && !GcBase((void*)(uintptr_t)w0) ? JcClassOf(w0) : NULL;
        g.cut[i].cls = c ? c->id : 0;
    }
    DWORD body = s.used + 4 * s.n;
    for (int i = 0; i < s.n; i++) body += 16;
    if (!JcRoom(body + 12 * g.nCut + 8 * (JC_STATICS + np) + 64)) { g.st.room++; return; }
    const DWORD rec = JcBegin(4);
    JcW32((DWORD)(&m - g_jitMeth)); JcW32(slot); JcW32(via); JcW32(mr.seen); JcW32(np); JcW32(self);
    JcW32((DWORD)s.n); JcW32((DWORD)g.nCut); JcW32((DWORD)g.cutLost);
    int nst = 0;
    for (int i = 0; i < mr.nStatic; i++) nst += stIdx[i] >= 0;
    JcW32((DWORD)nst);
    if (np) JcW(sargs, 8 * np);
    for (int i = 0; i < mr.nStatic; i++) if (stIdx[i] >= 0) { JcW32((DWORD)mr.sid[i]); JcW32((DWORD)stIdx[i]); }
    for (int i = 0; i < s.n; i++) { JcW32((DWORD)(uintptr_t)s.r[i].at); JcW32(g.cls[i]); JcW32(s.r[i].n); JcW32(g.full[i]); }
    for (int i = 0; i < g.nCut; i++) { JcW32(g.cut[i].obj); JcW32(g.cut[i].off); JcW32((DWORD)g.cut[i].cls); }
    for (int i = 0; i < s.n; i++) {
        const DWORD zero = 0, at = g.n;
        const JcClassRow* c = g.cls[i] ? JcClassOf(*(const DWORD*)s.r[i].at) : NULL;
        JcW(s.r[i].at, s.r[i].n);
        JcW(&zero, (4 - (s.r[i].n & 3)) & 3);
        if (g.cls[i] && !(c && (c->flags & JC_F_SCAN))) continue;
        // a reference past the bound reads null in the file: the cut list is capped, and a game address must never reach the replay
        for (DWORD o = g.cls[i] ? 4 : 0; o + 4 <= s.r[i].n; o += 4) {
            DWORD* w = (DWORD*)(g.buf + at + o);
            if (*w >= 0x10000 && !(*w & 3) && !s.Contains(*w) && GcBase((void*)(uintptr_t)*w) == (u8*)(uintptr_t)*w) *w = 0;
        }
    }
    JcEnd(rec);
    g.st.samples++, g.st.objects += s.n, g.st.statics += nst, g.st.bytes += s.used;
    g.st.viaLazy += via == JC_VIA_LAZY, g.st.viaInline += via == JC_VIA_INLINE;
}
static LONG g_jcFaultLog;
static int JcFault(const EXCEPTION_POINTERS* x, const JitMeth& m) {
    if (g_jcFaultLog++ < 8) {
        const EXCEPTION_RECORD& er = *x->ExceptionRecord;
        Log("jit capture: fault %08lX at %08lX, data %08lX, in a sample of %s" JC_LOG_NL, er.ExceptionCode, (DWORD)(uintptr_t)er.ExceptionAddress,
            er.NumberParameters > 1 ? (DWORD)er.ExceptionInformation[1] : 0, m.name);
    }
    return EXCEPTION_EXECUTE_HANDLER;
}
static void JcSampleAt(JitRegs* r, JitMeth* mp, DWORD via, DWORD self, const u8* sargs) {
    JcState& g = *g_jc;
    if (!mp) return;
    JitMeth& m = *mp;
    const int id = (int)(&m - g_jitMeth);
    if (id >= JC_METHS) return;
    JcMethRow& mr = g.meths[id];
    mr.seen++;
    DWORD slot;
    if (mr.taken < (DWORD)g.first) slot = mr.taken;
    else {
        mr.rng ^= mr.rng << 13, mr.rng ^= mr.rng >> 17, mr.rng ^= mr.rng << 5;
        const DWORD j = mr.rng % mr.seen;
        if (j >= (DWORD)g.res) return;
        slot = g.first + j;
    }
    if (g.written + g.n >= g.maxFile) { g.st.fileFull++; return; }
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    if (t0.QuadPart - g.win >= g.qpf) g.win = t0.QuadPart, g.usedUs = 0;
    if (g.usedUs >= g.budgetUs) { g.st.budget++; return; }
    const DWORD start = g.n;
    __try {
        JcSampleBody(r, mr, m, slot, via, self, sargs);
        if (mr.taken < (DWORD)g.first) mr.taken++, g.st.first++;
        else g.st.reservoir++;
    } __except (JcFault(GetExceptionInformation(), m)) {
        g.n = start;  // class rows written before the fault stay: their ids are in the table, and a partial sample is not a record
        g.st.faults++;
    }
    QueryPerformanceCounter(&t1);
    const DWORD us = (DWORD)((t1.QuadPart - t0.QuadPart) * 1000000 / g.qpf);
    g.usedUs += us, g.st.costUs += us;
    if (us > g.st.maxUs) g.st.maxUs = us;
}
// JitEnter: the callee's frame is written, this and the arguments are in its MonoInvocation. A frame that cannot be read is a fault.
static void JcSample(JitRegs* r, JitEnt* e) {
    volatile DWORD self = 0, sargs = 0;  // volatile: the reads stay inside the __try
    __try {
        const u8* inv = (const u8*)(uintptr_t)*(const volatile DWORD*)(uintptr_t)(r->ebp - 8);
        self = *(const volatile DWORD*)(inv + 0xC), sargs = *(const volatile DWORD*)(inv + 0x14);
    } __except (EXCEPTION_EXECUTE_HANDLER) { g_jc->st.faults++; return; }
    JcSampleAt(r, JC_METH(e), JC_VIA_ENTER, self, (const u8*)(uintptr_t)sargs);
}
static void* __cdecl JcSlow(JitRegs* r, JitEnt* e) {
    if (*(const DWORD*)JC_RM(e) == r->edi && JC_ON_SIM) JcSample(r, e);
    return JC_NEXT(r, e);
}
static __declspec(naked) void JcEnterStub() {  // eax = table entry, edi = ip
    __asm {
        sub esp, 4
        pushad
        mov ecx, esp
        push eax
        push ecx
        call JcSlow
        add esp, 8
        mov [esp + 32], eax
        popad
        ret
    }
}
// Direct=1: the first instruction of a lazy body while capture is on (JitCompile emits the call only then). The call site's block
// holds rm, this and the argument pointer (EmitLazyCall: B_RM, B_THIS, B_ARGS); nothing of the callee's frame is written yet.
static void __cdecl JcLazySlow(JitRegs* r, const u8* block) {
    if (!g_jc || !JC_ON_SIM) return;
    const u8* rm = *(const u8* const*)(block + B_RM);
    JcSampleAt(r, JC_METH_RM(rm), JC_VIA_LAZY, *(const DWORD*)(block + B_THIS), *(const u8* const*)(block + B_ARGS));
}
static __declspec(naked) void JcLazyStub() {  // [esp] into the lazy body, [esp+4] into the call site, [esp+8] the block; every register kept
    __asm {
        pushad
        mov ecx, esp
        lea eax, [esp + 40]
        push eax
        push ecx
        call JcLazySlow
        add esp, 8
        popad
        ret
    }
}
// Inline=1|2: at an inlined call site, after its tests (null this, ForceJIT, room in the chunk), before the body. Receiver and
// arguments are still on the caller's eval stack: this at esi - (n + 1) * 8, the arguments from esi - n * 8 (JitTryInline's thisOff).
static void __cdecl JcInlineSlow(JitRegs* r, const u8* rm) {
    if (!g_jc || !JC_ON_SIM) return;
    const int n = rm[0x40];
    const u8* args = (const u8*)(uintptr_t)(r->esi - 8 * n);
    JcSampleAt(r, JC_METH_RM(rm), JC_VIA_INLINE, *(const DWORD*)(args - 8), n ? args : NULL);
}
static __declspec(naked) void JcInlineStub() {  // [esp] the return, [esp+4] the callee's rm (the site pushes it); every register kept
    __asm {
        pushad
        mov ecx, esp
        push dword ptr [esp + 36]
        push ecx
        call JcInlineSlow
        add esp, 8
        popad
        ret 4
    }
}
static void JcLog() {
    const JcStats& t = g_jc->st;
    Log("jit capture: %lu samples (%lu first, %lu reservoir; %lu in lazy bodies, %lu inlined), %lu objects, %lu KB; skipped budget %lu, no buffer room %lu, file full %lu, faults %lu; cuts %lu (%lu not listed), truncated %lu; no class %lu, free cell %lu, outside the frames %lu; statics %lu, skipped %lu; cost %llu us, worst %lu us" JC_LOG_NL,
        t.samples, t.first, t.reservoir, t.viaLazy, t.viaInline, t.objects, t.bytes >> 10, t.budget, t.room, t.fileFull, t.faults, t.cuts, t.cutsLost, t.trunc, t.noClass, t.freeCell, t.outside, t.statics, t.staticSkip, t.costUs, t.maxUs);
}
static void JcFlush() {
    JcState& g = *g_jc;
    if (!g.n) return;
    DWORD w = 0;
    WriteFile(g.file, g.buf, g.n, &w, NULL);
    g.written += g.n, g.n = 0;
}
// per iteration of the sim loop, on the sim thread: the file write, once a second or at 1 MB
static void JcTick() {
    JcState* g = g_jc;
    if (!g) return;
    const DWORD now = GetTickCount();
    if (g->n >= (1u << 20) || now - g->flushTick >= 1000) {
        LARGE_INTEGER t0, t1;
        QueryPerformanceCounter(&t0);
        g->flushTick = now;
        JcFlush();
        QueryPerformanceCounter(&t1);
        const DWORD us = (DWORD)((t1.QuadPart - t0.QuadPart) * 1000000 / g->qpf);
        g->st.costUs += us;
        if (us > g->st.maxUs) g->st.maxUs = us;
    }
    if (now - g->logTick >= 60000) g->logTick = now, JcLog();
}
static bool JcInit(const wchar_t* path, int first, int res, int depth, int maxObjs, DWORD maxBytes, DWORD objMax, DWORD budgetUs, DWORD maxFile) {
    static_assert(sizeof(JitMeth) > 0 && sizeof(JitEnt) == 16, "JcEnterStub relies on the table entry only through JitEnt*");
    const size_t need = sizeof(JcState) + sizeof(JitSnap) + 2 * sizeof(DWORD) * JIT_SNAP_OBJS + sizeof(JcClassRow) * JC_CLASSES + sizeof(JcMethRow) * JC_METHS + JC_BUF;
    u8* p = (u8*)VirtualAlloc(NULL, need, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!p) return false;
    JcState* g = (JcState*)p;
    p += sizeof(JcState);
    g->s = (JitSnap*)p, p += sizeof(JitSnap);
    g->cls = (DWORD*)p, p += sizeof(DWORD) * JIT_SNAP_OBJS;
    g->full = (DWORD*)p, p += sizeof(DWORD) * JIT_SNAP_OBJS;
    g->classes = (JcClassRow*)p, p += sizeof(JcClassRow) * JC_CLASSES;
    g->meths = (JcMethRow*)p, p += sizeof(JcMethRow) * JC_METHS;
    g->buf = p;
    g->s->cap = maxBytes, g->first = first, g->res = res, g->depth = depth;
    g->maxObjs = maxObjs < JIT_SNAP_OBJS ? maxObjs : JIT_SNAP_OBJS, g->maxBytes = maxBytes, g->objMax = objMax, g->budgetUs = budgetUs, g->maxFile = maxFile;
    for (int i = 0; i < JC_METHS; i++) g->meths[i].rng = 0x9E3779B9u ^ (DWORD)(i + 1) * 2654435761u;
    LARGE_INTEGER q;
    QueryPerformanceFrequency(&q);
    g->qpf = q.QuadPart;
    g->flushTick = g->logTick = GetTickCount();
    g->file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (g->file == INVALID_HANDLE_VALUE) { VirtualFree(g, 0, MEM_RELEASE); return false; }
    g_jc = g;
    JcW("JCAP", 4);
    JcW32(JC_VERSION);
    g_jcOn = 1;
    return true;
}
