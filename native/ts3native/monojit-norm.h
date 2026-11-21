// [MonoJit] CoreModMatch (docs/537 §34, default 0): the token-normalized IL hash. Included by monojit.cpp (needs ilpatch.cpp's
// IlpDecode and IlpSha256). A core mod (simler90's gameplay core mod: Sims3GameplaySystems; NRaas ErrorTrap: ScriptCore) re-emits
// EA's assembly, so every metadata token number in an unchanged body differs and the exact IL hash refuses it. JnNormSha hashes the
// IL with each token operand replaced by what the token names (types by full name, members by owner + name + signature, strings by
// their #US bytes), read from the assembly's own metadata. Same body up to token numbers = same normalized hash. The rows
// (kJitNormRows, monojit-list.h) come from EA's DLLs through the same code (`selftest.exe --jitnorm`, tools/jit-hot.ts --emit-native).
// What the compiler then runs is exact either way: JitCompile works on the mint code EA's transform built, whose data items EA
// resolved through the loaded module (call targets, field offsets, classes); no EA token value is baked in (docs/537 §34.2).
// Input: the assembly bytes as Mono holds them, MonoImage +0x08 raw_data / +0x0C raw_data_len [verified: 0xE847F3 / 0xE847F6 in
// mono_image_open_from_data_full 0xE84780, docs/51 I-905-1]; the body must lie inside them (checked), else no hash.

struct JnMd {
    const BYTE* b; DWORD n;
    const BYTE *str, *us, *blob; DWORD strN, usN, blobN;
    const BYTE* tab[45]; DWORD rows[45], rowSz[45];
    BYTE colOff[45][10], colSz[45][10];
};
// ECMA-335 II.22 columns: 0x00-0x2C a simple index into that table, 0x40 + k coded index k, 0x80 u16, 0x81 u32, 0x82 #Strings,
// 0x83 #GUID, 0x84 #Blob, 0xFF end
static const BYTE kJnCols[45][10] = {
    {0x80, 0x82, 0x83, 0x83, 0x83, 0xFF}, {0x4B, 0x82, 0x82, 0xFF}, {0x81, 0x82, 0x82, 0x40, 0x04, 0x06, 0xFF}, {0x04, 0xFF},
    {0x80, 0x82, 0x84, 0xFF}, {0x06, 0xFF}, {0x81, 0x80, 0x80, 0x82, 0x84, 0x08, 0xFF}, {0x08, 0xFF}, {0x80, 0x80, 0x82, 0xFF},
    {0x02, 0x40, 0xFF}, {0x45, 0x82, 0x84, 0xFF}, {0x80, 0x41, 0x84, 0xFF}, {0x42, 0x4A, 0x84, 0xFF}, {0x43, 0x84, 0xFF},
    {0x80, 0x44, 0x84, 0xFF}, {0x80, 0x81, 0x02, 0xFF}, {0x81, 0x04, 0xFF}, {0x84, 0xFF}, {0x02, 0x14, 0xFF}, {0x14, 0xFF},
    {0x80, 0x82, 0x40, 0xFF}, {0x02, 0x17, 0xFF}, {0x17, 0xFF}, {0x80, 0x82, 0x84, 0xFF}, {0x80, 0x06, 0x46, 0xFF},
    {0x02, 0x47, 0x47, 0xFF}, {0x82, 0xFF}, {0x84, 0xFF}, {0x80, 0x48, 0x82, 0x1A, 0xFF}, {0x81, 0x04, 0xFF}, {0x81, 0x81, 0xFF},
    {0x81, 0xFF}, {0x81, 0x80, 0x80, 0x80, 0x80, 0x81, 0x84, 0x82, 0x82, 0xFF}, {0x81, 0xFF}, {0x81, 0x81, 0x81, 0xFF},
    {0x80, 0x80, 0x80, 0x80, 0x81, 0x84, 0x82, 0x82, 0x84, 0xFF}, {0x81, 0x23, 0xFF}, {0x81, 0x81, 0x81, 0x23, 0xFF},
    {0x81, 0x82, 0x84, 0xFF}, {0x81, 0x81, 0x82, 0x82, 0x49, 0xFF}, {0x81, 0x81, 0x82, 0x49, 0xFF}, {0x02, 0x02, 0xFF},
    {0x80, 0x80, 0x4C, 0x82, 0xFF}, {0x47, 0x84, 0xFF}, {0x2A, 0x40, 0xFF}};
// coded indices (II.24.2.6): tag bits, tables by tag (0xFF unused)
struct JnCoded { BYTE bits, n, t[22]; };
static const JnCoded kJnCoded[13] = {
    {2, 3, {0x02, 0x01, 0x1B}},                                                                                   // 0 TypeDefOrRef
    {2, 3, {0x04, 0x08, 0x17}},                                                                                   // 1 HasConstant
    {5, 22, {0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B}},  // 2 HasCustomAttribute
    {1, 2, {0x04, 0x08}},                                                                                         // 3 HasFieldMarshal
    {2, 3, {0x02, 0x06, 0x20}},                                                                                   // 4 HasDeclSecurity
    {3, 5, {0x02, 0x01, 0x1A, 0x06, 0x1B}},                                                                       // 5 MemberRefParent
    {1, 2, {0x14, 0x17}},                                                                                         // 6 HasSemantics
    {1, 2, {0x06, 0x0A}},                                                                                         // 7 MethodDefOrRef
    {1, 2, {0x04, 0x06}},                                                                                         // 8 MemberForwarded
    {2, 3, {0x26, 0x23, 0x27}},                                                                                   // 9 Implementation
    {3, 5, {0xFF, 0xFF, 0x06, 0x0A, 0xFF}},                                                                       // 10 CustomAttributeType
    {2, 4, {0x00, 0x1A, 0x23, 0x01}},                                                                             // 11 ResolutionScope
    {1, 2, {0x02, 0x06}}};                                                                                        // 12 TypeOrMethodDef

static const BYTE* JnAt(const JnMd& m, DWORD off, DWORD len) { return off <= m.n && len <= m.n - off ? m.b + off : NULL; }
static DWORD JnRvaOff(const BYTE* b, DWORD n, DWORD rva) {  // file offset of an RVA, 0 = none
    const DWORD pe = n >= 0x40 ? *(const DWORD*)(b + 0x3C) : 0;
    if (!pe || pe > n - 24) return 0;
    const WORD ns = *(const WORD*)(b + pe + 6), opt = *(const WORD*)(b + pe + 20);
    const DWORD st = pe + 24 + opt;
    for (WORD i = 0; i < ns; i++) {
        if (st + 40 * (i + 1) > n) return 0;
        const BYTE* s = b + st + 40 * i;
        const DWORD vs = *(const DWORD*)(s + 8), va = *(const DWORD*)(s + 12), rs = *(const DWORD*)(s + 16), ro = *(const DWORD*)(s + 20);
        if (rva >= va && rva - va < (vs > rs ? vs : rs)) return rva - va + ro < n ? rva - va + ro : 0;
    }
    return 0;
}
// false = not a CLI assembly this reader knows (#~ only, tables 0x00-0x2C, no Ptr tables)
static bool JnOpen(JnMd& m, const BYTE* b, DWORD n) {
    memset(&m, 0, sizeof(m));
    m.b = b, m.n = n;
    if (n < 0x200 || b[0] != 'M' || b[1] != 'Z') return false;
    const DWORD pe = *(const DWORD*)(b + 0x3C);
    if (pe > n - 0x100 || *(const DWORD*)(b + pe) != 0x4550 || *(const WORD*)(b + pe + 24) != 0x10B) return false;
    const DWORD cli = JnRvaOff(b, n, *(const DWORD*)(b + pe + 24 + 208));  // data directory 14 (PE32)
    const BYTE* ch = cli ? JnAt(m, cli, 16) : NULL;
    const DWORD root = ch ? JnRvaOff(b, n, *(const DWORD*)(ch + 8)) : 0;
    const BYTE* r = root ? JnAt(m, root, 16) : NULL;
    if (!r || *(const DWORD*)r != 0x424A5342) return false;
    const DWORD vl = *(const DWORD*)(r + 12);
    if (vl > 256 || !JnAt(m, root + 16 + vl, 4)) return false;
    const WORD sn = *(const WORD*)(r + 16 + vl + 2);
    DWORD q = root + 20 + vl;
    const BYTE* tabs = NULL;
    for (WORD i = 0; i < sn; i++) {
        const BYTE* h = JnAt(m, q, 9);
        if (!h) return false;
        const DWORD off = *(const DWORD*)h, sz = *(const DWORD*)(h + 4);
        const char* nm = (const char*)(h + 8);
        const BYTE* d = JnAt(m, root + off, sz);
        if (!d) return false;
        if (!strcmp(nm, "#~")) tabs = d;
        else if (!strcmp(nm, "#Strings")) m.str = d, m.strN = sz;
        else if (!strcmp(nm, "#US")) m.us = d, m.usN = sz;
        else if (!strcmp(nm, "#Blob")) m.blob = d, m.blobN = sz;
        else if (!strcmp(nm, "#-")) return false;
        DWORD l = 0;
        while (l < 32 && JnAt(m, q + 8 + l, 1) && h[8 + l]) l++;
        q += 8 + ((l + 4) & ~3u);
    }
    if (!tabs || !m.str || !m.blob) return false;
    const BYTE hs = tabs[6];
    const unsigned long long valid = *(const unsigned long long*)(tabs + 8);
    if (valid >> 45) return false;
    const BYTE* p = tabs + 24;
    for (int t = 0; t < 45; t++) if (valid >> t & 1) m.rows[t] = *(const DWORD*)p, p += 4;
    if (hs & 0x40) p += 4;  // extra data
    if (m.rows[0x03] || m.rows[0x05] || m.rows[0x07] || m.rows[0x13] || m.rows[0x16]) return false;
    BYTE codedSz[13];
    for (int k = 0; k < 13; k++) {
        DWORD mx = 0;
        for (int i = 0; i < kJnCoded[k].n; i++) if (kJnCoded[k].t[i] != 0xFF && m.rows[kJnCoded[k].t[i]] > mx) mx = m.rows[kJnCoded[k].t[i]];
        codedSz[k] = (BYTE)(mx < (1u << (16 - kJnCoded[k].bits)) ? 2 : 4);
    }
    for (int t = 0; t < 45; t++) {
        DWORD o = 0;
        for (int c = 0; c < 10 && kJnCols[t][c] != 0xFF; c++) {
            const BYTE k = kJnCols[t][c];
            const BYTE s = (BYTE)(k == 0x80 ? 2 : k == 0x81 ? 4 : k == 0x82 ? (hs & 1 ? 4 : 2) : k == 0x83 ? (hs & 2 ? 4 : 2) : k == 0x84 ? (hs & 4 ? 4 : 2)
                         : k >= 0x40 ? codedSz[k - 0x40] : (m.rows[k] < 65536 ? 2 : 4));
            m.colOff[t][c] = (BYTE)o, m.colSz[t][c] = s, o += s;
        }
        m.rowSz[t] = o;
        m.tab[t] = p;
        if ((DWORD)(p - b) > n || (unsigned long long)o * m.rows[t] > n - (DWORD)(p - b)) return false;
        p += o * m.rows[t];
    }
    return true;
}
static DWORD JnCol(const JnMd& m, int t, DWORD row, int c) {  // row is 1-based; 0 = out of range
    if (!row || row > m.rows[t]) return 0;
    const BYTE* p = m.tab[t] + (row - 1) * m.rowSz[t] + m.colOff[t][c];
    return m.colSz[t][c] == 2 ? *(const WORD*)p : *(const DWORD*)p;
}
static bool JnCuint(const BYTE*& p, const BYTE* e, DWORD* v) {  // II.23.2 compressed unsigned
    if (p >= e) return false;
    if (!(p[0] & 0x80)) { *v = *p++; return true; }
    if ((p[0] & 0xC0) == 0x80) { if (e - p < 2) return false; *v = (DWORD)(p[0] & 0x3F) << 8 | p[1]; p += 2; return true; }
    if ((p[0] & 0xE0) == 0xC0) { if (e - p < 4) return false; *v = (DWORD)(p[0] & 0x1F) << 24 | (DWORD)p[1] << 16 | (DWORD)p[2] << 8 | p[3]; p += 4; return true; }
    return false;
}
static bool JnBlob(const JnMd& m, DWORD idx, const BYTE** p, const BYTE** e) {
    if (idx >= m.blobN) return false;
    const BYTE* q = m.blob + idx;
    DWORD len;
    if (!JnCuint(q, m.blob + m.blobN, &len) || len > (DWORD)(m.blob + m.blobN - q)) return false;
    *p = q, *e = q + len;
    return true;
}

// the normalized stream (HeapAlloc'ed, grows); bad = a token or signature this reader cannot name: no hash
struct JnOut { BYTE* p; DWORD n, cap; bool bad; };
static void JnPut(JnOut& o, const void* d, DWORD n) {
    if (o.bad) return;
    if (o.n + n > o.cap) {
        DWORD c = o.cap ? o.cap : 4096;
        while (c < o.n + n) c *= 2;
        if (c > (16u << 20)) { o.bad = true; return; }
        BYTE* q = (BYTE*)(o.p ? HeapReAlloc(GetProcessHeap(), 0, o.p, c) : HeapAlloc(GetProcessHeap(), 0, c));
        if (!q) { o.bad = true; return; }
        o.p = q, o.cap = c;
    }
    memcpy(o.p + o.n, d, n), o.n += n;
}
static void JnPutB(JnOut& o, BYTE v) { JnPut(o, &v, 1); }
static void JnPutU(JnOut& o, DWORD v) { JnPut(o, &v, 4); }
static void JnPutStr(JnOut& o, const JnMd& m, DWORD idx) {  // #Strings entry, length-prefixed
    if (idx >= m.strN) { o.bad = true; return; }
    const char* s = (const char*)m.str + idx;
    const DWORD l = (DWORD)strnlen(s, m.strN - idx);
    JnPutU(o, l), JnPut(o, s, l);
}
static void JnType(JnOut& o, const JnMd& m, const BYTE*& p, const BYTE* e, int depth);
static void JnTypeDefOrRef(JnOut& o, const JnMd& m, DWORD coded, int depth);
static void JnTypeDef(JnOut& o, const JnMd& m, DWORD row, int depth) {  // "ns" "name", nested: the enclosing type first
    if (!row || row > m.rows[0x02] || depth > 16) { o.bad = true; return; }
    for (DWORD i = 1; i <= m.rows[0x29]; i++)
        if (JnCol(m, 0x29, i, 0) == row) { JnPutB(o, '/'); JnTypeDef(o, m, JnCol(m, 0x29, i, 1), depth + 1); break; }
    JnPutStr(o, m, JnCol(m, 0x02, row, 2)), JnPutStr(o, m, JnCol(m, 0x02, row, 1));
}
static void JnTypeRef(JnOut& o, const JnMd& m, DWORD row, int depth) {  // same text as the TypeDef it names (assembly scope left out)
    if (!row || row > m.rows[0x01] || depth > 16) { o.bad = true; return; }
    const DWORD sc = JnCol(m, 0x01, row, 0);
    if ((sc & 3) == 3) { JnPutB(o, '/'); JnTypeRef(o, m, sc >> 2, depth + 1); }
    JnPutStr(o, m, JnCol(m, 0x01, row, 2)), JnPutStr(o, m, JnCol(m, 0x01, row, 1));
}
static void JnTypeSpec(JnOut& o, const JnMd& m, DWORD row, int depth) {
    const BYTE *p, *e;
    if (!row || row > m.rows[0x1B] || !JnBlob(m, JnCol(m, 0x1B, row, 0), &p, &e)) { o.bad = true; return; }
    JnPutB(o, '['), JnType(o, m, p, e, depth + 1), JnPutB(o, ']');
}
static void JnTypeDefOrRef(JnOut& o, const JnMd& m, DWORD coded, int depth) {
    JnPutB(o, 'T');
    switch (coded & 3) {
        case 0: JnTypeDef(o, m, coded >> 2, depth); break;
        case 1: JnTypeRef(o, m, coded >> 2, depth); break;
        case 2: JnTypeSpec(o, m, coded >> 2, depth); break;
        default: o.bad = true;
    }
}
static void JnMethodSig(JnOut& o, const JnMd& m, const BYTE*& p, const BYTE* e, int depth) {
    if (p >= e) { o.bad = true; return; }
    const BYTE cc = *p++;
    JnPutB(o, cc);
    DWORD v, n;
    if ((cc & 0x10) && JnCuint(p, e, &v)) JnPutU(o, v);
    if (!JnCuint(p, e, &n)) { o.bad = true; return; }
    JnPutU(o, n);
    JnType(o, m, p, e, depth);
    for (DWORD i = 0; i < n && !o.bad; i++) {
        if (p < e && *p == 0x41) JnPutB(o, *p++);  // sentinel (vararg)
        JnType(o, m, p, e, depth);
    }
}
static void JnType(JnOut& o, const JnMd& m, const BYTE*& p, const BYTE* e, int depth) {
    if (p >= e || depth > 32) { o.bad = true; return; }
    const BYTE t = *p++;
    JnPutB(o, t);
    DWORD v, n;
    switch (t) {
        case 0x0F: case 0x10: case 0x1D: case 0x45: JnType(o, m, p, e, depth + 1); return;  // ptr, byref, szarray, pinned
        case 0x11: case 0x12:  // valuetype, class
            if (!JnCuint(p, e, &v)) { o.bad = true; return; }
            JnTypeDefOrRef(o, m, v, depth + 1); return;
        case 0x13: case 0x1E: if (JnCuint(p, e, &v)) JnPutU(o, v); else o.bad = true; return;  // !n, !!n
        case 0x14:  // array: type, rank, sizes, lower bounds (raw numbers)
            JnType(o, m, p, e, depth + 1);
            for (int part = 0; part < 3 && !o.bad; part++) {
                if (!JnCuint(p, e, &n)) { o.bad = true; return; }
                JnPutU(o, n);
                if (part == 0) continue;
                for (DWORD i = 0; i < n; i++) if (JnCuint(p, e, &v)) JnPutU(o, v); else { o.bad = true; return; }
            }
            return;
        case 0x15:  // generic instance
            JnType(o, m, p, e, depth + 1);
            if (!JnCuint(p, e, &n)) { o.bad = true; return; }
            JnPutU(o, n);
            for (DWORD i = 0; i < n && !o.bad; i++) JnType(o, m, p, e, depth + 1);
            return;
        case 0x1B: JnMethodSig(o, m, p, e, depth + 1); return;  // fnptr
        case 0x1F: case 0x20:  // cmod: its type, then the modified type
            if (!JnCuint(p, e, &v)) { o.bad = true; return; }
            JnTypeDefOrRef(o, m, v, depth + 1), JnType(o, m, p, e, depth + 1); return;
        default:
            if ((t >= 0x01 && t <= 0x0E) || t == 0x16 || t == 0x18 || t == 0x19 || t == 0x1C) return;
            o.bad = true;
    }
}
static void JnSigBlob(JnOut& o, const JnMd& m, DWORD idx) {  // a field, method or method-instantiation signature
    const BYTE *p, *e;
    if (!JnBlob(m, idx, &p, &e) || p >= e) { o.bad = true; return; }
    if (*p == 0x06) { JnPutB(o, *p++); JnType(o, m, p, e, 0); return; }
    if (*p == 0x0A) {
        DWORD n;
        JnPutB(o, *p++);
        if (!JnCuint(p, e, &n)) { o.bad = true; return; }
        JnPutU(o, n);
        for (DWORD i = 0; i < n && !o.bad; i++) JnType(o, m, p, e, 0);
        return;
    }
    JnMethodSig(o, m, p, e, 0);
}
static DWORD JnOwner(const JnMd& m, DWORD row, int listCol) {  // the TypeDef whose field / method list holds row
    DWORD best = 0;
    for (DWORD t = 1; t <= m.rows[0x02]; t++) if (JnCol(m, 0x02, t, listCol) <= row) best = t; else break;
    return best;
}
static void JnToken(JnOut& o, const JnMd& m, DWORD tok, int depth);
static void JnMember(JnOut& o, const JnMd& m, DWORD tok, int depth) {  // 'M' / 'F': owner, name, signature (MethodDef and MemberRef alike)
    const DWORD t = tok >> 24, row = tok & 0xFFFFFF;
    const BYTE *p, *e;
    DWORD sig, name;
    if (t == 0x06) sig = JnCol(m, 0x06, row, 4), name = JnCol(m, 0x06, row, 3);
    else if (t == 0x04) sig = JnCol(m, 0x04, row, 2), name = JnCol(m, 0x04, row, 1);
    else sig = JnCol(m, 0x0A, row, 2), name = JnCol(m, 0x0A, row, 1);
    if (!row || row > m.rows[t] || !JnBlob(m, sig, &p, &e) || p >= e || depth > 8) { o.bad = true; return; }
    JnPutB(o, *p == 0x06 ? 'F' : 'M');
    if (t == 0x06) JnPutB(o, 'T'), JnTypeDef(o, m, JnOwner(m, row, 5), depth);
    else if (t == 0x04) JnPutB(o, 'T'), JnTypeDef(o, m, JnOwner(m, row, 4), depth);
    else {
        const DWORD par = JnCol(m, 0x0A, row, 0);  // MemberRefParent
        switch (par & 7) {
            case 0: JnTypeDefOrRef(o, m, (par >> 3) << 2 | 0, depth); break;
            case 1: JnTypeDefOrRef(o, m, (par >> 3) << 2 | 1, depth); break;
            case 2: JnPutB(o, 'D'); JnPutStr(o, m, JnCol(m, 0x1A, par >> 3, 0)); break;  // ModuleRef (global function)
            case 3: JnToken(o, m, 0x06000000 | (par >> 3), depth + 1); break;           // a vararg call site of a MethodDef
            case 4: JnTypeDefOrRef(o, m, (par >> 3) << 2 | 2, depth); break;
            default: o.bad = true;
        }
    }
    JnPutStr(o, m, name), JnSigBlob(o, m, sig);
}
static void JnToken(JnOut& o, const JnMd& m, DWORD tok, int depth) {
    const DWORD row = tok & 0xFFFFFF;
    switch (tok >> 24) {
        case 0x01: case 0x02: case 0x1B: JnTypeDefOrRef(o, m, row << 2 | (tok >> 24 == 0x02 ? 0 : tok >> 24 == 0x01 ? 1 : 2), depth); return;
        case 0x04: case 0x06: case 0x0A: JnMember(o, m, tok, depth); return;
        case 0x2B: {  // method instantiation: the generic method, then its arguments
            const DWORD md = JnCol(m, 0x2B, row, 0);
            if (!md) { o.bad = true; return; }
            JnPutB(o, 'G'), JnToken(o, m, (md & 1 ? 0x0A000000 : 0x06000000) | md >> 1, depth + 1), JnSigBlob(o, m, JnCol(m, 0x2B, row, 1));
            return;
        }
        case 0x11: {  // calli's stand-alone signature
            if (!row || row > m.rows[0x11]) { o.bad = true; return; }
            JnPutB(o, 'S'), JnSigBlob(o, m, JnCol(m, 0x11, row, 0));
            return;
        }
        case 0x70: {  // ldstr: the #US bytes
            const BYTE* p = m.us && row < m.usN ? m.us + row : NULL;
            DWORD len;
            if (!p || !JnCuint(p, m.us + m.usN, &len) || len > (DWORD)(m.us + m.usN - p)) { o.bad = true; return; }
            JnPutB(o, 'U'), JnPutU(o, len), JnPut(o, p, len);
            return;
        }
        default: o.bad = true;
    }
}
// the normalized hash of code[0..size) of assembly bytes b[0..n); false = cannot name a token, not a body of this assembly, or bad IL
static bool JnNormSha(const BYTE* b, DWORD n, const BYTE* code, DWORD size, BYTE sha[32]) {
    if (!b || !code || code < b || size > n || code - b > (ptrdiff_t)(n - size)) return false;
    JnMd m;
    if (!JnOpen(m, b, n)) return false;
    static IlpDec dec[16384];  // under g_jitCs (the post-pass) or the selftest's one thread
    const int k = IlpDecode(code, size, dec, 16384);
    if (k < 0) return false;
    JnOut o = {};
    JnPut(o, "jn1", 3);  // the form's version: a change of the text below needs new rows
    for (int i = 0; i < k && !o.bad; i++) {
        const IlpDec& d = dec[i];
        if (d.kind != IK_TOK) { JnPut(o, code + d.at, d.size); continue; }
        const DWORD osz = d.op > 0xFF ? 2 : 1;
        JnPut(o, code + d.at, osz);
        JnPutB(o, '('), JnToken(o, m, *(const DWORD*)(code + d.at + osz), 0), JnPutB(o, ')');
    }
    const bool ok = !o.bad && IlpSha256(o.p, o.n, sha);
    if (o.p) HeapFree(GetProcessHeap(), 0, o.p);
    return ok;
}
// the method's assembly bytes, MonoMethod +8 class -> MonoClass +0 image -> +0x08 / +0x0C (docs/104 §4 [verified], I-905-1 [verified])
static bool JnImageBytes(const BYTE* mm, const BYTE** b, DWORD* n) {
    const BYTE* cls = mm ? *(const BYTE* const*)(mm + 8) : NULL;
    const BYTE* img = cls ? *(const BYTE* const*)cls : NULL;
    if (!img) return false;
    *b = *(const BYTE* const*)(img + 8), *n = *(const DWORD*)(img + 0xC);
    return *b && *n;
}
