// [IlPatch] (docs/506 §2, docs/526, counters v907 ilp*): mid-method core fixes applied to EA's IL when Mono transforms the
// method. Default OFF, one switch per fix. Included by ts3native.cpp before autshadow.cpp: satb.cpp's cold site 0 (the same
// transform entry) becomes a client of this detour when it owns the site.
//
// Mono's IL -> mint transform 0xE643B0 (cdecl: RuntimeMethod* rm, void* ctx; returns the thrown object or NULL) reads the
// method's IL through mono_method_get_header 0xE85390 (cdecl, MonoMethod*), which parses the body once and caches it at
// MonoMethod+0x20 [verified: 0xE853C7-0xE85435, loader lock 0x11F43A8 only on a miss]. The cached header: +0 code size,
// +4 code, +8 max stack (16-bit; the transform sizes the eval stack from it at 0xE6494C), +0xC bits 0-14 clause count
// (0xE5CBED), +0x10 locals count, +0x14 the clause array (24-byte records: flags, try offset, try length, handler offset,
// handler length, class or filter offset; 0xE5CC06-0xE5CC7C), +0x18 locals [verified: capstone of TS3W.exe 1.67 at those
// sites, 2026-11-02]. The generate step 0xE5CA90 (MonoPatcher's ReplaceIL site) reads the same cached header.
//
// For a listed method (name match, then the exact body size + SHA-256 of a build the spec knows) the detour decodes EA's
// body, writes a new one with our instructions spliced in (every branch in its long form, the exception clauses moved), checks
// that the new code's SHA-256 is the converter's patchedSha256 for that build (tools/ilpatch-convert.ts and this relocator
// must agree byte for byte), then points the cached header at the new code (size, code, max stack, the clause records in
// place) for that one transform call and puts EA's values back right after it, also when the transform faults. The G-M6
// hash and reflection read the cached header too, but on the sim thread, which is inside this call while the swap lasts.
// Anything unexpected leaves EA's body and counts a skip; a fault inside the patcher restores the header and turns the spec
// off for the session. The patched body is kept per spec, so a repeat transform of the same method (MonoPatcher's ForceJIT)
// costs one name compare and reuses it.

struct IlpOp { WORD code; BYTE kind; };
struct IlpIns { WORD op; BYTE kind; BYTE pad; DWORD v; };  // one of our instructions: kind below; v = operand, token index, offset or line index
struct IlpEdit { DWORD at; BYTE mode, entry, nExpect, nIns; WORD maxStack, drop; const WORD* expect; const IlpIns* ins; };
struct IlpBuild { const char* label; DWORD size; BYTE sha[32]; BYTE patched[32]; const DWORD* tokens; DWORD nTokens; };
struct IlpSpec { const char* id; const wchar_t* key; const char* ns; const char* cls; const char* name; const char* method; DWORD nBuilds, nEdits; const IlpBuild* builds; const IlpEdit* edits; };
#include "ilpatch-specs.h"  // kIlpOps (ECMA-335 opcodes: code, operand kind), kIlpSpecs, kIlpSpecN
enum { IK_NONE, IK_I8, IK_U8, IK_U16, IK_I32, IK_I64, IK_F32, IK_F64, IK_BR8, IK_BR32, IK_TOK, IK_SWITCH, IK_BR_ORIG, IK_BR_OURS, IK_BAD = 0xFF };
static const BYTE kIlpKindSize[12] = {0, 1, 1, 2, 4, 8, 4, 8, 1, 4, 4, 0};
static const DWORD IL_XFORM = 0x00E643B0, IL_HEADER = 0x00E85390;
static const BYTE kIlpXformPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x78};

typedef void*(__cdecl* IlpXformFn)(BYTE* rm, void* ctx);
typedef BYTE*(__cdecl* IlpHeaderFn)(BYTE* mm);
static IlpXformFn g_ilpNext;                      // EA's transform (the detour's trampoline); non-NULL = this file owns 0xE643B0
static IlpHeaderFn g_ilpHeader = (IlpHeaderFn)(uintptr_t)IL_HEADER;
static void (*g_ilpColdEnter)(int site, DWORD arg0);  // satb's cold-site client (SatbColdEnter), called first as its own detour did
static bool g_ilpArmed[kIlpSpecN];
static volatile LONG g_ilpLogged[kIlpSpecN];      // the once-per-spec lines
struct IlpPatched {  // the patched body of one spec, built at the first transform of its method
    BYTE* rm;        // the runtime method it was built for (a repeat transform reuses it)
    const IlpBuild* build;
    BYTE* code; DWORD size; WORD maxStack;
    DWORD nClauses; DWORD* clauses0; DWORD* clauses1;  // EA's clause records and ours (6 DWORD each)
};
static IlpPatched g_ilpPatched[kIlpSpecN];

// ---- SHA-256 (bcrypt, loaded on first use; the same way AutShadowNative::ImageSha hashes a module) ----
typedef LONG(WINAPI* IlpBcOpenFn)(void**, LPCWSTR, LPCWSTR, ULONG);
typedef LONG(WINAPI* IlpBcHashFn)(void*, PUCHAR, ULONG, PUCHAR, ULONG, PUCHAR, ULONG);
static IlpBcHashFn g_ilpBcHash;
static void* g_ilpBcSha;
static bool IlpShaInit() {
    if (g_ilpBcHash) return true;
    HMODULE b = LoadLibraryW(L"bcrypt.dll");
    IlpBcOpenFn open = b ? (IlpBcOpenFn)(void*)GetProcAddress(b, "BCryptOpenAlgorithmProvider") : NULL;
    IlpBcHashFn hash = b ? (IlpBcHashFn)(void*)GetProcAddress(b, "BCryptHash") : NULL;
    if (open && hash && open(&g_ilpBcSha, L"SHA256", NULL, 0) == 0) g_ilpBcHash = hash;
    return g_ilpBcHash != NULL;
}
static bool IlpSha256(const BYTE* p, DWORD n, BYTE out[32]) {
    return g_ilpBcHash && g_ilpBcHash(g_ilpBcSha, NULL, 0, (PUCHAR)p, n, out, 32) == 0;
}
static const char* IlpHex(const BYTE* p, int n, char* buf) {  // first n bytes as hex (buf >= 2n + 1)
    for (int i = 0; i < n; i++) sprintf_s(buf + 2 * i, 3, "%02x", p[i]);
    return buf;
}

// ---- decoder ----
struct IlpDec { DWORD at; WORD op; BYTE kind; BYTE size; DWORD v; };  // v: branch target (absolute), switch case count, else unused
static BYTE g_ilpKindOf[0x120];  // by opcode: one-byte codes, then 0xFE xx at 0x100 + xx; IK_BAD = not an opcode
static bool g_ilpKindReady;
static void IlpKindTable() {
    if (g_ilpKindReady) return;
    g_ilpKindReady = true;
    memset(g_ilpKindOf, IK_BAD, sizeof(g_ilpKindOf));
    for (const IlpOp& o : kIlpOps) g_ilpKindOf[o.code > 0xFF ? 0x100 + (o.code & 0xFF) : o.code] = o.kind;
}
static BYTE IlpKind(WORD op) { return op > 0xFF ? (op & 0xFF) < 0x20 ? g_ilpKindOf[0x100 + (op & 0xFF)] : IK_BAD : g_ilpKindOf[op]; }
// Decodes code[0..n) into out (cap entries). Returns the count, or -1 (unknown opcode, operand past the end, over cap).
static int IlpDecode(const BYTE* code, DWORD n, IlpDec* out, DWORD cap) {
    IlpKindTable();
    DWORD p = 0, k = 0;
    while (p < n) {
        if (k >= cap) return -1;
        IlpDec& d = out[k];
        d.at = p; d.op = code[p++]; d.v = 0;
        if (d.op == 0xFE) { if (p >= n) return -1; d.op = (WORD)(0xFE00 | code[p++]); }
        d.kind = IlpKind(d.op);
        if (d.kind == IK_BAD) return -1;
        DWORD osz = kIlpKindSize[d.kind];
        if (d.kind == IK_SWITCH) {
            if (p + 4 > n) return -1;
            d.v = *(const DWORD*)(code + p);
            if (d.v > 0xFFFF) return -1;
            osz = 4 + 4 * d.v;
        }
        if (p + osz > n) return -1;
        if (d.kind == IK_BR8) d.v = p + 1 + (DWORD)(int)(signed char)code[p];
        else if (d.kind == IK_BR32) d.v = p + 4 + (DWORD)*(const int*)(code + p);
        p += osz;
        d.size = (BYTE)(p - d.at);
        k++;
    }
    return (int)k;
}

// ---- relocator (the C++ copy of tools/ilpatch-convert.ts apply(): same items, same offsets, same bytes) ----
struct IlpReloc {  // the result; code is HeapAlloc'ed and kept (mint may keep reading it), the rest describes it
    BYTE* code; DWORD size; WORD maxStack;
    DWORD clauses[64 * 6];  // the moved clause records (at most 64 clauses)
    const char* why;        // NULL = ok, else the reason it was refused
};
static WORD IlpLongOp(WORD op) { return op == 0xDE ? 0xDD : (WORD)(op + 0x0D); }  // br.s -> br family, leave.s -> leave
// The innermost-region test of docs/506 I-506-3: both offsets lie in the same set of try and handler ranges.
static bool IlpSameRegion(const DWORD* cl, DWORD n, DWORD a, DWORD b) {
    for (DWORD i = 0; i < n; i++) {
        const DWORD* c = cl + 6 * i;
        const bool ta = a >= c[1] && a < c[1] + c[2], tb = b >= c[1] && b < c[1] + c[2];
        const bool ha = a >= c[3] && a < c[3] + c[4], hb = b >= c[3] && b < c[3] + c[4];
        if (ta != tb || ha != hb) return false;
    }
    return true;
}
struct IlpItem { DWORD off, size; int orig; int edit, line; };  // orig >= 0: that decoded instruction; else edit's line
// cl: the original clause records (6 DWORD each, n of them). Allocates r.code; r.why names a refusal (nothing allocated then).
static void IlpApply(const BYTE* code, DWORD n, WORD maxStack, const DWORD* cl, DWORD nCl, const IlpSpec& s, const IlpBuild& b, IlpReloc& r) {
    memset(&r, 0, sizeof(r));
    if (nCl > 64) { r.why = "more than 64 exception clauses"; return; }
    HANDLE heap = GetProcessHeap();
    IlpDec* dec = (IlpDec*)HeapAlloc(heap, 0, sizeof(IlpDec) * (n + 1));
    if (!dec) { r.why = "out of memory"; return; }
    const int nd = IlpDecode(code, n, dec, n + 1);
    if (nd < 0) { HeapFree(heap, 0, dec); r.why = "the body does not decode"; return; }
    // index of the instruction at an offset (nd = the end), -1 = not an instruction start
    struct Idx { static int of(const IlpDec* d, int nd, DWORD n, DWORD at) {
        if (at == n) return nd;
        int lo = 0, hi = nd - 1;
        while (lo <= hi) { const int m = (lo + hi) / 2; if (d[m].at == at) return m; if (d[m].at < at) lo = m + 1; else hi = m - 1; }
        return -1;
    } };
    // check every edit: at is an instruction start with the expected opcodes; our branches stay in at's region
    int editAt[16] = {0}; WORD addMax = 0;
    if (s.nEdits > 16) { HeapFree(heap, 0, dec); r.why = "more than 16 edits"; return; }
    for (DWORD e = 0; e < s.nEdits; e++) {
        const IlpEdit& ed = s.edits[e];
        const int i = Idx::of(dec, nd, n, ed.at);
        editAt[e] = i;
        if (i < 0) { HeapFree(heap, 0, dec); r.why = "an edit offset is not an instruction start"; return; }
        for (int k = 0; k < ed.nExpect; k++)
            if (i + k >= nd || dec[i + k].op != ed.expect[k]) { HeapFree(heap, 0, dec); r.why = "the opcodes at an edit offset are not the expected ones"; return; }
        if (ed.maxStack > addMax) addMax = ed.maxStack;
        for (int k = 0; k < ed.nIns; k++) {
            const IlpIns& x = ed.ins[k];
            if (x.kind == IK_BR_ORIG) {
                if (Idx::of(dec, nd, n, x.v) < 0) { HeapFree(heap, 0, dec); r.why = "our branch target is not an instruction start"; return; }
                if (!IlpSameRegion(cl, nCl, ed.at, x.v)) { HeapFree(heap, 0, dec); r.why = "our branch leaves the exception region of its edit"; return; }
            } else if (x.kind == IK_BR_OURS) { if ((x.v >> 16) >= s.nEdits || (x.v & 0xFFFF) >= s.edits[x.v >> 16].nIns) { HeapFree(heap, 0, dec); r.why = "our branch names a line the spec has not"; return; } }  // v = edit << 16 | line
            else if (x.kind == IK_TOK) { if (x.v >= b.nTokens) { HeapFree(heap, 0, dec); r.why = "a token index past the build's token list"; return; } }
            else if (x.kind > IK_I32 && x.kind != IK_TOK) { HeapFree(heap, 0, dec); r.why = "an operand kind the native table does not carry"; return; }
        }
    }
    // the item list: original instructions (minus a replace's dropped ones) with our lines before an edit's at; end inserts last
    DWORD nItems = (DWORD)nd; for (DWORD e = 0; e < s.nEdits; e++) nItems += s.edits[e].nIns;
    IlpItem* it = (IlpItem*)HeapAlloc(heap, 0, sizeof(IlpItem) * (nItems + 1));
    if (!it) { HeapFree(heap, 0, dec); r.why = "out of memory"; return; }
    DWORD ni = 0;
    for (int i = 0; i < nd; i++) {
        int e = -1; for (DWORD k = 0; k < s.nEdits; k++) if (editAt[k] == i) e = (int)k;
        if (e >= 0) {
            const IlpEdit& ed = s.edits[e];
            for (int k = 0; k < ed.nIns; k++) { const IlpIns& x = ed.ins[k]; IlpItem& m = it[ni++]; m.orig = -1; m.edit = e; m.line = k; m.size = 1 + (x.op > 0xFF ? 1 : 0) + (x.kind == IK_BR_ORIG || x.kind == IK_BR_OURS ? 4 : kIlpKindSize[x.kind]); }
            if (ed.drop) { i += ed.drop - 1; continue; }
        }
        IlpItem& m = it[ni++]; m.orig = i; m.edit = -1; m.line = 0;
        m.size = dec[i].kind == IK_SWITCH ? dec[i].size : 1 + (dec[i].op > 0xFF ? 1 : 0) + (dec[i].kind == IK_BR8 ? 4 : kIlpKindSize[dec[i].kind]);
    }
    for (DWORD e = 0; e < s.nEdits; e++) if (editAt[e] == nd) { const IlpEdit& ed = s.edits[e]; for (int k = 0; k < ed.nIns; k++) { const IlpIns& x = ed.ins[k]; IlpItem& m = it[ni++]; m.orig = -1; m.edit = (int)e; m.line = k; m.size = 1 + (x.op > 0xFF ? 1 : 0) + (x.kind == IK_BR_ORIG || x.kind == IK_BR_OURS ? 4 : kIlpKindSize[x.kind]); } }
    DWORD off = 0; for (DWORD k = 0; k < ni; k++) { it[k].off = off; off += it[k].size; }
    const DWORD newLen = off;
    // old offset -> new offset, as the converter's map(): per the edit at that offset and who asks
    DWORD* origOff = (DWORD*)HeapAlloc(heap, 0, sizeof(DWORD) * (nd + 1)); DWORD oursOff[16] = {0}; DWORD* lineOff = (DWORD*)HeapAlloc(heap, 0, sizeof(DWORD) * (nItems + 1));
    if (!origOff || !lineOff) { if (origOff) HeapFree(heap, 0, origOff); if (lineOff) HeapFree(heap, 0, lineOff); HeapFree(heap, 0, it); HeapFree(heap, 0, dec); r.why = "out of memory"; return; }
    for (int i = 0; i <= nd; i++) origOff[i] = 0xFFFFFFFF;
    for (DWORD k = 0; k < ni; k++) { if (it[k].orig >= 0) origOff[it[k].orig] = it[k].off; else { if (it[k].line == 0) oursOff[it[k].edit] = it[k].off; } }
    DWORD lineBase[16] = {0}; { DWORD acc = 0; for (DWORD e = 0; e < s.nEdits; e++) { lineBase[e] = acc; acc += s.edits[e].nIns; } }
    for (DWORD k = 0; k < ni; k++) if (it[k].orig < 0) lineOff[lineBase[it[k].edit] + it[k].line] = it[k].off;
    struct Map { static DWORD of(DWORD old, const IlpSpec& s, const IlpDec* dec, int nd, DWORD n, DWORD newLen, const DWORD* origOff, const DWORD* oursOff, const char** why) {
        if (old == n) return newLen;
        int e = -1; for (DWORD k = 0; k < s.nEdits; k++) if (s.edits[k].at == old) e = (int)k;
        if (e >= 0 && (s.edits[e].entry == 1 || s.edits[e].mode == 1)) return oursOff[e];  // a fallthrough insert's own branch to "at": the original instruction after the block (b69 brtrue.s IL_007d)
        const int i = Idx::of(dec, nd, n, old);
        if (i < 0 || origOff[i] == 0xFFFFFFFF) { *why = "a branch target is inside a replaced range or not an instruction start"; return 0; }
        return origOff[i];
    } };
    const char* why = NULL;
    BYTE* out = (BYTE*)HeapAlloc(heap, 0, newLen + 1);
    if (!out) why = "out of memory";
    for (DWORD k = 0; k < ni && !why; k++) {
        const IlpItem& m = it[k]; DWORD p = m.off;
        if (m.orig >= 0) {
            const IlpDec& d = dec[m.orig];
            const WORD op = d.kind == IK_BR8 ? IlpLongOp(d.op) : d.op;
            if (op > 0xFF) { out[p++] = 0xFE; out[p++] = (BYTE)op; } else out[p++] = (BYTE)op;
            const BYTE* src = code + d.at + (d.op > 0xFF ? 2 : 1);
            switch (d.kind) {
                case IK_BR8: case IK_BR32: { const DWORD t = Map::of(d.v, s, dec, nd, n, newLen, origOff, oursOff, &why); *(int*)(out + p) = (int)(t - (p + 4)); break; }
                case IK_SWITCH: { *(DWORD*)(out + p) = d.v; const DWORD base = p + 4 + 4 * d.v; for (DWORD c = 0; c < d.v && !why; c++) { const DWORD old = d.at + d.size + (DWORD)*(const int*)(src + 4 + 4 * c); const DWORD t = Map::of(old, s, dec, nd, n, newLen, origOff, oursOff, &why); *(int*)(out + p + 4 + 4 * c) = (int)(t - base); } break; }
                default: memcpy(out + p, src, kIlpKindSize[d.kind]); break;
            }
        } else {
            const IlpEdit& ed = s.edits[m.edit]; const IlpIns& x = ed.ins[m.line];
            if (x.op > 0xFF) { out[p++] = 0xFE; out[p++] = (BYTE)x.op; } else out[p++] = (BYTE)x.op;
            switch (x.kind) {
                case IK_NONE: break;
                case IK_I8: case IK_U8: out[p] = (BYTE)x.v; break;
                case IK_U16: *(WORD*)(out + p) = (WORD)x.v; break;
                case IK_I32: *(DWORD*)(out + p) = x.v; break;
                case IK_TOK: *(DWORD*)(out + p) = b.tokens[x.v]; break;
                case IK_BR_ORIG: { const DWORD t = Map::of(x.v, s, dec, nd, n, newLen, origOff, oursOff, &why); *(int*)(out + p) = (int)(t - (p + 4)); break; }
                case IK_BR_OURS: *(int*)(out + p) = (int)(lineOff[lineBase[x.v >> 16] + (x.v & 0xFFFF)] - (p + 4)); break;
                default: why = "an operand kind the native table does not carry"; break;
            }
        }
    }
    for (DWORD i = 0; i < nCl && !why; i++) {  // every clause through the same map; a filter's offset too
        const DWORD* c = cl + 6 * i; DWORD* o = r.clauses + 6 * i;
        o[0] = c[0];
        o[1] = Map::of(c[1], s, dec, nd, n, newLen, origOff, oursOff, &why); o[2] = Map::of(c[1] + c[2], s, dec, nd, n, newLen, origOff, oursOff, &why) - o[1];
        o[3] = Map::of(c[3], s, dec, nd, n, newLen, origOff, oursOff, &why); o[4] = Map::of(c[3] + c[4], s, dec, nd, n, newLen, origOff, oursOff, &why) - o[3];
        o[5] = c[0] == 1 ? Map::of(c[5], s, dec, nd, n, newLen, origOff, oursOff, &why) : c[5];
    }
    HeapFree(heap, 0, lineOff); HeapFree(heap, 0, origOff); HeapFree(heap, 0, it); HeapFree(heap, 0, dec);
    if (why) { if (out) HeapFree(heap, 0, out); memset(&r, 0, sizeof(r)); r.why = why; return; }
    r.code = out; r.size = newLen; r.maxStack = (WORD)(maxStack + addMax);
}

// ---- the detour ----
// True when the class k is spec class cls ("Outer+Inner" for a nested type) in namespace ns. Mono gives a nested class its
// own name only (name_space ""), the enclosing class is nested_in (+0x28: monohost read Door+PrankDoorbellDitch, 2026-11-02; name +0x34 and name_space +0x38 in
// this Mono's MonoClass); the namespace is the outermost class's. Until 2026-11-02 the compare was name and name_space
// only, so no nested-type fix ever applied (smoke 22: b17-2/3, b64, b68-apple, b72, b76, b77, b81 touched, none applied).
static bool IlpClassIs(const BYTE* k, const char* cls, const char* ns) {
    const char* end = cls + strlen(cls);
    for (int depth = 0; k && depth < 8; depth++) {
        const char* plus = NULL; for (const char* p = cls; p < end; p++) if (*p == '+') plus = p;
        const char* seg = plus ? plus + 1 : cls; const size_t segLen = (size_t)(end - seg);
        const char* cn = *(const char* const*)(k + 0x34);
        if (!cn || strncmp(cn, seg, segLen) || cn[segLen]) return false;
        if (!plus) { const char* kns = *(const char* const*)(k + 0x38); return !strcmp(kns ? kns : "", ns); }
        end = plus; k = *(const BYTE* const*)(k + 0x28);
    }
    return false;
}
static int IlpMatch(BYTE* rm) {  // the armed spec whose names are this runtime method's, or -1 (the same reads as monohost's JitMethodName)
    __try {
        const BYTE* mm = *(BYTE* const*)(rm + 8);
        const BYTE* k = mm ? *(const BYTE* const*)(mm + 8) : NULL;
        const char* mn = mm ? *(const char* const*)(mm + 0x18) : NULL;
        if (!k || !mn) return -1;
        for (int i = 0; i < kIlpSpecN; i++) {
            if (!g_ilpArmed[i] || strcmp(mn, kIlpSpecs[i].name)) continue;
            if (IlpClassIs(k, kIlpSpecs[i].cls, kIlpSpecs[i].ns)) return i;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return -1;
}
// Builds the patched body of spec si from the cached header hdr. false = left alone (counted and logged once); the spec stays
// armed only for a hash miss (another method of the same name, or a build we do not know, may still come).
static bool IlpBuildPatched(int si, BYTE* rm, const BYTE* hdr) {
    const IlpSpec& s = kIlpSpecs[si]; IlpPatched& P = g_ilpPatched[si];
    const DWORD size = *(const DWORD*)hdr; const BYTE* code = *(const BYTE* const*)(hdr + 4);
    const WORD maxStack = (WORD)(*(const WORD*)(hdr + 8) & 0x7FFF); const DWORD nCl = *(const DWORD*)(hdr + 0xC) & 0x7FFF;
    const DWORD* cl = *(const DWORD* const*)(hdr + 0x14);
    char hex[65];
    BYTE sha[32];
    if (!code || !size || size > (1u << 20) || !IlpSha256(code, size, sha)) {
        InterlockedIncrement(&ts3native_counters.ilpSkipCheck);
        if (InterlockedIncrement(&g_ilpLogged[si]) == 1) Log("il patch: %s: %s has no readable body (%lu bytes): left alone", s.id, s.method, size);
        return false;
    }
    const IlpBuild* b = NULL;
    for (DWORD i = 0; i < s.nBuilds; i++) if (s.builds[i].size == size && !memcmp(s.builds[i].sha, sha, 32)) b = &s.builds[i];
    if (!b) {
        InterlockedIncrement(&ts3native_counters.ilpSkipHash);
        const IlpBuild* done = NULL;  // a core that already carries this fix in the converter's form (the contributor's patched core)
        for (DWORD i = 0; i < s.nBuilds; i++) if (!memcmp(s.builds[i].patched, sha, 32)) done = &s.builds[i];
        if (InterlockedIncrement(&g_ilpLogged[si]) == 1) {
            if (done) Log("il patch: %s: %s is %lu bytes, sha256 %s: already carries the fix (%s patched form), left alone", s.id, s.method, size, IlpHex(sha, 8, hex), done->label);
            else Log("il patch: %s: %s is %lu bytes, sha256 %s: not a build this fix knows, left alone", s.id, s.method, size, IlpHex(sha, 8, hex));
        }
        return false;
    }
    IlpReloc r;
    IlpApply(code, size, maxStack, cl, nCl, s, *b, r);
    if (r.why) {
        InterlockedIncrement(&ts3native_counters.ilpSkipCheck);
        if (InterlockedIncrement(&g_ilpLogged[si]) == 1) Log("il patch: %s: %s (%s) refused: %s", s.id, s.method, b->label, r.why);
        g_ilpArmed[si] = false;
        return false;
    }
    BYTE sha2[32]; char hex2[65];
    if (!IlpSha256(r.code, r.size, sha2) || memcmp(sha2, b->patched, 32)) {
        InterlockedIncrement(&ts3native_counters.ilpSkipMismatch);
        if (InterlockedIncrement(&g_ilpLogged[si]) == 1)
            Log("il patch: %s: %s (%s): the patched body's sha256 %s is not the converter's %s: left alone (relocator mismatch, report it)", s.id, s.method, b->label, IlpHex(sha2, 8, hex), IlpHex(b->patched, 8, hex2));
        HeapFree(GetProcessHeap(), 0, r.code);
        g_ilpArmed[si] = false;
        return false;
    }
    if (P.code) HeapFree(GetProcessHeap(), 0, P.code);  // a different runtime method of the same name: rebuilt
    if (P.clauses0) HeapFree(GetProcessHeap(), 0, P.clauses0);
    P.clauses0 = nCl ? (DWORD*)HeapAlloc(GetProcessHeap(), 0, 2 * 24 * nCl) : NULL;
    if (nCl && !P.clauses0) { HeapFree(GetProcessHeap(), 0, r.code); P.code = NULL; return false; }
    P.clauses1 = nCl ? P.clauses0 + 6 * nCl : NULL;
    if (nCl) { memcpy(P.clauses0, cl, 24 * nCl); memcpy(P.clauses1, r.clauses, 24 * nCl); }
    P.rm = rm; P.build = b; P.code = r.code; P.size = r.size; P.maxStack = r.maxStack; P.nClauses = nCl;
    return true;
}
// The transform with the patched body in the cached header; EA's values are back before this returns, fault or not.
static void* IlpTransformPatched(int si, BYTE* rm, void* ctx, BYTE* hdr, bool first) {
    IlpPatched& P = g_ilpPatched[si];
    const DWORD size0 = *(DWORD*)hdr; BYTE* const code0 = *(BYTE**)(hdr + 4); const WORD ms0 = *(WORD*)(hdr + 8);
    DWORD* const cl = *(DWORD**)(hdr + 0x14);
    void* r = NULL;
    *(DWORD*)hdr = P.size; *(BYTE**)(hdr + 4) = P.code; *(WORD*)(hdr + 8) = (WORD)((ms0 & 0x8000) | P.maxStack);
    if (P.nClauses) memcpy(cl, P.clauses1, 24 * P.nClauses);
    __try { r = g_ilpNext(rm, ctx); }
    __finally {
        *(DWORD*)hdr = size0; *(BYTE**)(hdr + 4) = code0; *(WORD*)(hdr + 8) = ms0;
        if (P.nClauses) memcpy(cl, P.clauses0, 24 * P.nClauses);
    }
    if (first) {
        InterlockedIncrement(&ts3native_counters.ilpApplied);
        char hex[65];
        Log("il patch: %s applied to %s (%s, %lu -> %lu bytes, max stack %u -> %u, %lu clauses, patched sha256 %s as the converter's)",
            kIlpSpecs[si].id, kIlpSpecs[si].method, P.build->label, size0, P.size, ms0 & 0x7FFF, P.maxStack, P.nClauses, IlpHex(P.build->patched, 6, hex));
    } else InterlockedIncrement(&ts3native_counters.ilpReused);
    return r;
}
static void* __cdecl IlpXform(BYTE* rm, void* ctx) {
    if (g_ilpColdEnter) g_ilpColdEnter(0, (DWORD)(uintptr_t)rm);
    const int si = rm ? IlpMatch(rm) : -1;
    if (si < 0) return g_ilpNext(rm, ctx);
    IlpPatched& P = g_ilpPatched[si];
    BYTE* hdr = NULL;
    bool ok = false, first = false;
    __try {
        BYTE* mm = *(BYTE**)(rm + 8);
        hdr = mm ? g_ilpHeader(mm) : NULL;
        if (hdr) {
            if (P.code && P.rm == rm) ok = true;
            else ok = first = IlpBuildPatched(si, rm, hdr);
        } else {
            InterlockedIncrement(&ts3native_counters.ilpSkipCheck);
            if (InterlockedIncrement(&g_ilpLogged[si]) == 1) Log("il patch: %s: %s has no method header: left alone", kIlpSpecs[si].id, kIlpSpecs[si].method);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        InterlockedIncrement(&ts3native_counters.ilpFaults);
        g_ilpArmed[si] = false;
        Log("il patch: %s: fault 0x%08lX while reading %s: off for the session", kIlpSpecs[si].id, GetExceptionCode(), kIlpSpecs[si].method);
        ok = false;
    }
    if (!ok) return g_ilpNext(rm, ctx);
    return IlpTransformPatched(si, rm, ctx, hdr, first);
}

static bool ApplyIlPatch(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"IlPatch", L"Enabled", 0, ini)) return false;
    int armed = 0;
    for (int i = 0; i < kIlpSpecN; i++) {
        g_ilpArmed[i] = GetPrivateProfileIntW(L"IlPatch", kIlpSpecs[i].key, 0, ini) != 0;
        if (g_ilpArmed[i]) { armed++; Log("il patch: %s armed ([IlPatch] %ls=1): %s, %lu known build(s), %lu edit(s)", kIlpSpecs[i].id, kIlpSpecs[i].key, kIlpSpecs[i].method, kIlpSpecs[i].nBuilds, kIlpSpecs[i].nEdits); }
    }
    if (!armed) { Log("il patch: Enabled=1 but no fix switch is set ([IlPatch] <switch>=1 per fix): nothing to do"); return false; }
    if (!IlpShaInit()) { Log("SKIP il patch: SHA-256 (bcrypt.dll) unavailable: no fix applied"); return false; }
    g_ilpNext = (IlpXformFn)InstallDetour("il patch transform 0xE643B0", IL_XFORM, kIlpXformPro, sizeof(kIlpXformPro), (void*)IlpXform, &g_ilpNext);
    if (!g_ilpNext) { Log("SKIP il patch: the transform prologue differs (another mod or probe owns 0xE643B0): no fix applied"); return false; }
    ts3native_counters.ilpOn = 1;
    ts3native_counters.ilpArmed = armed;
    Log("OK   il patch (docs/506 §2, docs/526): %d of %d fixes armed; each is applied at the first transform of its method, on an exact body hash only", armed, kIlpSpecN);
    return true;  // keep the log open for the apply / skip lines
}
