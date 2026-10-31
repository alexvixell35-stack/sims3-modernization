// [SaveSlim] (docs/15 phase 11): after a normal save, the saved ExportDB without dead space and without the outfit copies
// nothing names any more. Included by ts3native.cpp after savewrite.cpp (hooks, g_fj) and decompress.cpp (RefpackFast).
//
// Every Sim who leaves a lot is packed again (PackOutfitToDisk -> SaveTravelSim): the game writes a new outfit
// (0x025ED6F4) and its textures (0x033A1435, 0x0341ACC9) into <world>ExportDB.package and never deletes the old copy, and
// its package writer appends without reusing freed bytes. So a lived-in save's ExportDB only grows (a 491-day save: 101 MB,
// 297,629 entries, 16% dead bytes), and every index entry costs EA heap at load.
// The game plays from the working copy CurrentGame.sims3 and only copies the saved folder <Saves>\<name>.sims3: step 9
// of the save writes it, the load copy (0xAB16ED) reads it back [verified code]. It holds none of its files open in play
// [verified: share-mode-0 opens of each file succeed in a running session]. So after a save that worked (not a travel
// save) a worker at background priority rewrites each <world>ExportDB.package of that folder: kept entries keep their
// bytes and index fields, packed after the header, index last (the layout of the offline cleaner whose copies loaded).
// Mode 2 also drops each outfit whose instance appears as 8 raw bytes in no other saved body or file (every .nhd, the
// other packages, Meta.data, ...), in no kept outfit or texture, and as the instance of no other index entry; and each
// group-0 texture that only dropped outfits name (directly or through other textures). Textures nothing names stay.
// Any 8 bytes equal to an instance keep it, so the test can only err towards keeping.
// The new file is written to <TS3 user folder>\ts3native-slim.tmp, read back and compared (header, index, every kept
// byte) with the source, which is held without write sharing until it is renamed over it (same volume). Anything
// unexpected (header field, compression, failed check) leaves that file as the game wrote it; a body or file that cannot
// be read anywhere turns Mode 2 into Mode 1 for that save (references unknown). The next save (also travel) and the load
// copy wait for the worker. A game exit while it runs leaves at most the temp file.

static const DWORD kSlimSimo = 0x025ED6F4, kSlimTxtc = 0x033A1435, kSlimTxtf = 0x0341ACC9;
static DWORD g_slimWin = 8u << 20;            // read window per file (the selftest makes it small)
static const DWORD kSlimMaxBody = 256u << 20;  // larger bodies count as unreadable
static const DWORD kSlimOutBuf = 1u << 20;
static const int kSlimMaxFiles = 64, kSlimMaxDb = 8;
enum { SF_OUTFIT = 1, SF_TEX = 2, SF_KEPT = 4, SF_DROPREACH = 8 };

struct SlimView {  // a read-only file seen through one mapped window at a time
    HANDLE file, map;
    const u8* base;
    DWORD at, len, size;
};

static bool SvOpen(SlimView& v, const wchar_t* path) {
    memset(&v, 0, sizeof(v));
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    v.file = h;
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(h, &sz) || sz.QuadPart >= 0x7FFFFFFF) return false;
    v.size = (DWORD)sz.QuadPart;
    if (v.size) v.map = CreateFileMappingW(h, NULL, PAGE_READONLY, 0, 0, NULL);
    return !v.size || v.map;
}

static void SvUnmap(SlimView& v) {
    if (v.base) UnmapViewOfFile(v.base);
    v.base = NULL;
}

static void SvClose(SlimView& v) {
    SvUnmap(v);
    if (v.map) CloseHandle(v.map);
    if (v.file) CloseHandle(v.file);
    memset(&v, 0, sizeof(v));
}

static const u8* SvAt(SlimView& v, DWORD off, DWORD n) {  // bytes [off, off + n); NULL past the end or without address space
    static const u8 kNone = 0;
    if (off > v.size || n > v.size - off) return NULL;
    if (!n) return &kNone;
    if (v.base && off >= v.at && off - v.at <= v.len && n <= v.len - (off - v.at)) return v.base + (off - v.at);
    SvUnmap(v);
    const DWORD a = off & ~0xFFFFu;  // allocation granularity
    DWORD len = off - a + n;
    if (len < g_slimWin) len = g_slimWin;
    if (len > v.size - a) len = v.size - a;
    v.base = (const u8*)MapViewOfFile(v.map, FILE_MAP_READ, 0, a, len);
    if (!v.base) return NULL;
    v.at = a, v.len = len;
    return v.base + (off - a);
}

static DWORD SeU32(const u8* p, int o) { return *(const DWORD*)(p + o); }
// index entry (32 bytes): type, group, instance high, instance low, offset, stored size | 0x80000000, size, compression
static DWORD SeLen(const u8* e) { return SeU32(e, 20) & 0x7FFFFFFF; }
static bool SeTex(const u8* e) { return (SeU32(e, 0) == kSlimTxtc || SeU32(e, 0) == kSlimTxtf) && !SeU32(e, 4); }
static bool SeCandidate(const u8* e) { return SeU32(e, 0) == kSlimSimo || SeTex(e); }

struct SlimFile {
    wchar_t path[MAX_PATH];
    SlimView v;
    u8 hdr[96];
    u8* idx;  // the index entries (without the index type word), 32 bytes each even when the file's index is compact
    DWORD count, ixFlags, ixBytes;
    int kind;  // 0 = not a package (scanned raw), 1 = package, 2 = <world>ExportDB.package, -1 = not readable
};

struct SlimDbStats {
    wchar_t name[64];
    DWORD entries[2], outfits[2], textures[2], bytes[2], dead;
    const char* result;  // "rewritten", or why not
};

struct SlimStats {
    int mode, files, unreadable, ndb;
    bool drops;  // Mode 0 or 2 and every reference readable (Mode 0 writes nothing)
    DWORD outfits, nodes, edges, outfitsDropped, texDropped, texOrphans;
    double scanMB;
    LONG scanMs, writeMs, totalMs;
    const char* fail;
    SlimDbStats db[kSlimMaxDb];
};

struct SlimJob {
    HANDLE heap;
    SlimFile f[kSlimMaxFiles];
    int nf;
    // candidates (outfits and group-0 textures of the ExportDB packages): one node per instance
    DWORD *lo, *hi, *stamp, *slot, *bits, *ef, *et;
    u8* flag;
    DWORD nn, shift, mask, ne, eCap, serial;
    u8* buf;
    DWORD bufCap;
    SlimStats* st;
};

static DWORD SlimHash(DWORD lo, DWORD hi) { return (lo ^ (hi * 0x85EBCA6Bu)) * 0x9E3779B1u; }

static int SlimFind(const SlimJob& j, DWORD lo, DWORD hi, DWORD h) {
    for (DWORD s = h >> j.shift;; s = (s + 1) & j.mask) {
        const DWORD k = j.slot[s];
        if (!k) return -1;
        if (j.lo[k - 1] == lo && j.hi[k - 1] == hi) return (int)(k - 1);
    }
}

static int SlimFindEntry(const SlimJob& j, const u8* e) {
    const DWORD lo = SeU32(e, 12), hi = SeU32(e, 8);
    return SlimFind(j, lo, hi, SlimHash(lo, hi));
}

static void SlimAdd(SlimJob& j, DWORD lo, DWORD hi, u8 fl) {
    const DWORD h = SlimHash(lo, hi);
    DWORD s = h >> j.shift;
    for (; j.slot[s]; s = (s + 1) & j.mask)
        if (j.lo[j.slot[s] - 1] == lo && j.hi[j.slot[s] - 1] == hi) { j.flag[j.slot[s] - 1] |= fl; return; }
    j.lo[j.nn] = lo, j.hi[j.nn] = hi, j.flag[j.nn] = fl;
    j.slot[s] = ++j.nn;
    j.bits[h >> 13] |= 1u << ((h >> 8) & 31);
}

static void* SlimAlloc(SlimJob& j, size_t n) { return HeapAlloc(j.heap, HEAP_ZERO_MEMORY, n ? n : 1); }

// Every 8 bytes of b equal to a candidate instance (little-endian, any alignment): from < 0 keeps it, else an edge.
static bool SlimScan(SlimJob& j, const u8* b, DWORD n, int from) {
    j.serial++;
    j.st->scanMB += n / 1048576.0;
    for (DWORD i = 0; i + 8 <= n; i++) {
        const DWORD lo = *(const DWORD*)(b + i), hi = *(const DWORD*)(b + i + 4), h = SlimHash(lo, hi);
        if (!(j.bits[h >> 13] & (1u << ((h >> 8) & 31)))) continue;
        const int k = SlimFind(j, lo, hi, h);
        if (k < 0 || k == from) continue;
        if (from < 0) { j.flag[k] |= SF_KEPT; continue; }
        if (j.stamp[k] == j.serial) continue;
        j.stamp[k] = j.serial;
        if (j.ne == j.eCap) {
            const DWORD cap = j.eCap ? j.eCap * 2 : 65536;
            DWORD* ef = (DWORD*)(j.ef ? HeapReAlloc(j.heap, 0, j.ef, cap * 4) : SlimAlloc(j, cap * 4));
            if (ef) j.ef = ef;
            DWORD* et = ef ? (DWORD*)(j.et ? HeapReAlloc(j.heap, 0, j.et, cap * 4) : SlimAlloc(j, cap * 4)) : NULL;
            if (!et) return false;
            j.et = et, j.eCap = cap;
        }
        j.ef[j.ne] = (DWORD)from, j.et[j.ne++] = (DWORD)k;
    }
    return true;
}

// The body as the game reads it (decoded when compressed); NULL when it does not decode to the size its entry gives.
static const u8* SlimBody(SlimJob& j, SlimFile& f, DWORD i, DWORD* n) {
    const u8* e = f.idx + i * 32;
    const u8* src = SvAt(f.v, SeU32(e, 16), SeLen(e));
    if (!src) return NULL;
    if (*(const WORD*)(e + 28) != 0xFFFF) { *n = SeLen(e); return src; }
    const DWORD mem = SeU32(e, 24);
    if (mem > kSlimMaxBody) return NULL;
    if (mem + 64 > j.bufCap) {
        if (j.buf) HeapFree(j.heap, 0, j.buf);
        j.bufCap = mem + 64 > (1u << 20) ? mem + 64 : (1u << 20);
        j.buf = (u8*)HeapAlloc(j.heap, 0, j.bufCap);
        if (!j.buf) { j.bufCap = 0; return NULL; }
    }
    if (RefpackFast(j.buf, mem, src, SeLen(e)) != (int)mem) return NULL;
    *n = mem;
    return j.buf;
}

// The DBPF 2.0 layouts of the save packages seen (2026-10-31, every file of 191 saves): index flags 0 (ExportDB, .nhd,
// TravelDB) or 7 (Meta.data: type, group and instance-high stored once after the flags word, entries 20 bytes), an empty
// package (96-byte header, no index: some TravelDBs), no holes, compression 0 or 0xFFFF. 1 = read, 0 = not a DBPF file,
// -1 = a DBPF file this code does not know.
static int SlimReadIndex(SlimJob& j, SlimFile& f) {
    const u8* h = SvAt(f.v, 0, 96);
    if (!h || memcmp(h, "DBPF", 4)) return 0;
    memcpy(f.hdr, h, 96);
    const DWORD count = SeU32(h, 36), size = SeU32(h, 44), pos = SeU32(h, 64);
    if (SeU32(h, 4) != 2 || SeU32(h, 40) || SeU32(h, 48) || count > 0x3FFFFFF) return -1;
    if (!(f.idx = (u8*)HeapAlloc(j.heap, 0, count * 32 + 1))) return -1;
    f.count = count, f.ixFlags = 0, f.ixBytes = size;
    if (!count && !size) return 1;
    const u8* x = SvAt(f.v, pos, size);
    const DWORD flags = x && size >= 4 ? SeU32(x, 0) : ~0u, nConst = (flags & 1) + (flags >> 1 & 1) + (flags >> 2 & 1);
    if (flags > 7 || size != 4 + 4 * nConst + count * (32 - 4 * nConst)) return -1;
    DWORD c[3] = {};
    const u8* p = x + 4;
    for (int b = 0; b < 3; b++)
        if (flags >> b & 1) c[b] = SeU32(p, 0), p += 4;
    for (DWORD i = 0; i < count; i++) {  // to the 32-byte form: type, group, instance hi, instance lo, offset, size, ...
        u8* e = f.idx + i * 32;
        for (int b = 0; b < 3; b++)
            if (flags >> b & 1) memcpy(e + b * 4, &c[b], 4);
            else memcpy(e + b * 4, p, 4), p += 4;
        memcpy(e + 12, p, 20), p += 20;
    }
    f.ixFlags = flags;
    for (DWORD i = 0; i < count; i++) {
        const u8* e = f.idx + i * 32;
        const WORD comp = *(const WORD*)(e + 28);
        if (SeU32(e, 16) > f.v.size || SeLen(e) > f.v.size - SeU32(e, 16) || (comp && comp != 0xFFFF)) return -1;
    }
    return 1;
}

static bool SlimRefs(SlimJob& j) {  // Mode 2: candidates, roots and edges; false = references unknown (no drops)
    DWORD maxNodes = 0;
    for (int i = 0; i < j.nf; i++)
        if (j.f[i].kind == 2) maxNodes += j.f[i].count;
    DWORD size = 1024, bits = 10;
    while (size < maxNodes * 2) size <<= 1, bits++;
    j.mask = size - 1, j.shift = 32 - bits;
    j.slot = (DWORD*)SlimAlloc(j, size * 4);
    j.bits = (DWORD*)SlimAlloc(j, (1u << 19) * 4);  // 2^24-bit filter on the hash's top bits
    j.lo = (DWORD*)SlimAlloc(j, maxNodes * 4);
    j.hi = (DWORD*)SlimAlloc(j, maxNodes * 4);
    j.stamp = (DWORD*)SlimAlloc(j, maxNodes * 4);
    j.flag = (u8*)SlimAlloc(j, maxNodes);
    if (!j.slot || !j.bits || !j.lo || !j.hi || !j.stamp || !j.flag) { j.st->fail = "out of memory (candidates)"; return false; }
    for (int i = 0; i < j.nf; i++) {
        if (j.f[i].kind != 2) continue;
        for (DWORD k = 0; k < j.f[i].count; k++) {
            const u8* e = j.f[i].idx + k * 32;
            if (SeU32(e, 0) == kSlimSimo) j.st->outfits++;
            if (SeCandidate(e)) SlimAdd(j, SeU32(e, 12), SeU32(e, 8), (u8)(SeU32(e, 0) == kSlimSimo ? SF_OUTFIT : SF_TEX));
        }
    }
    for (int i = 0; i < j.nf; i++) {
        SlimFile& f = j.f[i];
        if (f.kind == 0) {  // plain files (1a5tf1l3.dat): every byte
            const u8* b = SvAt(f.v, 0, f.v.size);
            if (!b || f.v.size > kSlimMaxBody) { j.st->unreadable++; return false; }
            if (!SlimScan(j, b, f.v.size, -1)) { j.st->fail = "out of memory (edges)"; return false; }
        }
        for (DWORD k = 0; k < f.count; k++) {
            const u8* e = f.idx + k * 32;
            const bool cand = f.kind == 2 && SeCandidate(e);
            if (!cand) {  // the instance of any other entry keeps a candidate with the same number (e.g. .nhd thumbnails)
                const int n = SlimFindEntry(j, e);
                if (n >= 0) j.flag[n] |= SF_KEPT;
            }
            DWORD len;
            const u8* b = SlimBody(j, f, k, &len);
            if (!b) { j.st->unreadable++; return false; }
            if (!SlimScan(j, b, len, cand ? SlimFindEntry(j, e) : -1)) { j.st->fail = "out of memory (edges)"; return false; }
        }
        SvUnmap(f.v);
    }
    j.st->nodes = j.nn, j.st->edges = j.ne;
    // closure over the edges: kept from the roots, then "named by a dropped outfit" from the outfits not kept
    DWORD* start = (DWORD*)SlimAlloc(j, (j.nn + 1) * 4);
    DWORD* adj = (DWORD*)SlimAlloc(j, j.ne * 4);
    DWORD* queue = (DWORD*)SlimAlloc(j, j.nn * 4);
    if (!start || !adj || !queue) { j.st->fail = "out of memory (closure)"; return false; }
    for (DWORD i = 0; i < j.ne; i++) start[j.ef[i] + 1]++;
    for (DWORD i = 0; i < j.nn; i++) start[i + 1] += start[i];
    for (DWORD i = 0; i < j.ne; i++) adj[start[j.ef[i]]++] = j.et[i];  // start[x] now = end of x's edges
    for (DWORD pass = 0; pass < 2; pass++) {
        const u8 mark = (u8)(pass ? SF_DROPREACH : SF_KEPT);
        DWORD qn = 0;
        for (DWORD i = 0; i < j.nn; i++) {
            if (pass && (j.flag[i] & (SF_OUTFIT | SF_KEPT)) == SF_OUTFIT) j.flag[i] |= SF_DROPREACH;
            if (j.flag[i] & mark) queue[qn++] = i;
        }
        while (qn) {
            const DWORD x = queue[--qn];
            for (DWORD a = x ? start[x - 1] : 0; a < start[x]; a++)
                if (!(j.flag[adj[a]] & (SF_KEPT | mark))) j.flag[adj[a]] |= mark, queue[qn++] = adj[a];
        }
    }
    return true;
}

static bool SlimDrop(const SlimJob& j, const u8* e) {
    if (!j.st->drops || !SeCandidate(e)) return false;
    const int k = SlimFindEntry(j, e);
    if (k < 0 || (j.flag[k] & SF_KEPT)) return false;
    return SeU32(e, 0) == kSlimSimo || (j.flag[k] & SF_DROPREACH);
}

struct SlimOut { HANDLE h; u8* buf; DWORD n, pos; bool ok; };
static void SoPut(SlimOut& o, const u8* p, DWORD n) {
    o.pos += n;
    while (n && o.ok) {
        const DWORD k = n < kSlimOutBuf - o.n ? n : kSlimOutBuf - o.n;
        memcpy(o.buf + o.n, p, k);
        o.n += k, p += k, n -= k;
        DWORD w;
        if (o.n == kSlimOutBuf) o.ok = WriteFile(o.h, o.buf, o.n, &w, NULL) && w == o.n, o.n = 0;
    }
}

static int __cdecl SlimCmpU64(const void* a, const void* b) {
    const u64 x = *(const u64*)a, y = *(const u64*)b;
    return x < y ? -1 : x > y;
}

// One ExportDB: the kept entries (data in source order, index in index order) to tmp, compared back, renamed over it.
static const char* SlimRewrite(SlimJob& j, SlimFile& f, const u8* keep, DWORD nk, const wchar_t* tmp) {
    u64* order = (u64*)SlimAlloc(j, nk * 8);
    DWORD* newOff = (DWORD*)SlimAlloc(j, f.count * 4);
    u8* idx = (u8*)SlimAlloc(j, 4 + nk * 32);
    SlimOut o = {INVALID_HANDLE_VALUE, (u8*)SlimAlloc(j, kSlimOutBuf), 0, 0, true};
    if (!order || !newOff || !idx || !o.buf) return "out of memory";
    DWORD n = 0, pos = 96;
    for (DWORD i = 0; i < f.count; i++)
        if (keep[i]) {
            order[n++] = ((u64)SeU32(f.idx + i * 32, 16) << 32) | i;
            if (SeLen(f.idx + i * 32) > 0x7FFFFFFF - pos) return "too large";
            pos += SeLen(f.idx + i * 32);
        }
    qsort(order, nk, 8, SlimCmpU64);
    if (nk * 32 + 4 > 0x7FFFFFFF - pos) return "too large";
    u8 hdr[96];
    memcpy(hdr, f.hdr, 96);
    *(DWORD*)(hdr + 36) = nk, *(DWORD*)(hdr + 44) = 4 + nk * 32, *(DWORD*)(hdr + 64) = pos;
    const DWORD total = pos + 4 + nk * 32;
    o.h = CreateFileW(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (o.h == INVALID_HANDLE_VALUE) return "temp file not created";
    SoPut(o, hdr, 96);
    for (DWORD k = 0; k < nk && o.ok; k++) {
        const DWORD i = (DWORD)order[k];
        const u8* e = f.idx + i * 32;
        const u8* src = SvAt(f.v, SeU32(e, 16), SeLen(e));
        if (!src) { o.ok = false; break; }
        newOff[i] = o.pos;
        SoPut(o, src, SeLen(e));
    }
    SvUnmap(f.v);
    DWORD m = 4;
    for (DWORD i = 0; i < f.count; i++)
        if (keep[i]) memcpy(idx + m, f.idx + i * 32, 32), *(DWORD*)(idx + m + 16) = newOff[i], m += 32;
    SoPut(o, idx, m);
    DWORD w;
    if (o.ok && o.n) o.ok = WriteFile(o.h, o.buf, o.n, &w, NULL) && w == o.n;
    o.ok = o.ok && o.pos == total && FlushFileBuffers(o.h);
    CloseHandle(o.h);
    const char* why = o.ok ? NULL : "write failed";
    SlimView t = {};
    if (!why && (!SvOpen(t, tmp) || t.size != total)) why = "temp file not readable";
    if (!why) {  // read back: header, index, every kept byte against the source
        const u8* p = SvAt(t, 0, 96);
        if (!p || memcmp(p, hdr, 96)) why = "header differs";
        p = why ? NULL : SvAt(t, pos, m);
        if (!why && (!p || memcmp(p, idx, m))) why = "index differs";
        for (DWORD k = 0; k < nk && !why; k++) {
            const u8* e = f.idx + (DWORD)order[k] * 32;
            const u8* a = SvAt(f.v, SeU32(e, 16), SeLen(e));
            const u8* b = SvAt(t, newOff[(DWORD)order[k]], SeLen(e));
            if (!a || !b || memcmp(a, b, SeLen(e))) why = "data differs";
        }
    }
    SvClose(t);
    SvClose(f.v);  // the source stays unwritable up to here
    if (!why && !MoveFileExW(tmp, f.path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) why = "rename failed";
    if (why) DeleteFileW(tmp);
    return why ? why : "rewritten";
}

static void SlimJobRun(SlimJob& j, const wchar_t* dir, const wchar_t* tmp) {
    SlimStats& st = *j.st;
    wchar_t pat[MAX_PATH];
    WIN32_FIND_DATAW fd;
    HANDLE h = Join(pat, dir, L"*") ? FindFirstFileW(pat, &fd) : INVALID_HANDLE_VALUE;
    if (h == INVALID_HANDLE_VALUE) { st.fail = "save folder not listed"; return; }
    bool known = true;  // every file of the folder readable
    do {
        if (IsDots(fd.cFileName)) continue;
        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || j.nf == kSlimMaxFiles) { known = false; continue; }
        SlimFile& f = j.f[j.nf++];
        st.files++;
        if (!Join(f.path, dir, fd.cFileName) || !SvOpen(f.v, f.path)) { f.kind = -1, known = false; continue; }
        const int r = SlimReadIndex(j, f);
        f.kind = r < 0 ? -1 : r == 0 ? 0 : EndsWithI(fd.cFileName, L"ExportDB.package") ? 2 : 1;
        if (f.kind < 0) known = false;
        if (f.kind == 2 && st.ndb < kSlimMaxDb) {
            SlimDbStats& d = st.db[st.ndb++];
            wcsncpy_s(d.name, Tail(f.path), _TRUNCATE);
            d.entries[0] = f.count, d.bytes[0] = f.v.size;
        }
        SvUnmap(f.v);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    if (!known) st.unreadable++;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    st.drops = st.mode != 1 && known && st.ndb && SlimRefs(j);
    st.scanMs = UsSince(t0) / 1000;
    QueryPerformanceCounter(&t0);
    for (int i = 0, d = 0; i < j.nf && d < st.ndb; i++) {
        SlimFile& f = j.f[i];
        if (f.kind != 2) continue;
        SlimDbStats& s = st.db[d++];
        u8* keep = (u8*)SlimAlloc(j, f.count);
        if (!keep) { s.result = "out of memory"; continue; }
        DWORD nk = 0, live = 0;
        for (DWORD k = 0; k < f.count; k++) {
            const u8* e = f.idx + k * 32;
            const bool outfit = SeU32(e, 0) == kSlimSimo, tex = SeTex(e);
            keep[k] = !SlimDrop(j, e);
            live += SeLen(e);
            s.outfits[0] += outfit, s.textures[0] += tex;
            if (keep[k]) nk++, s.outfits[1] += outfit, s.textures[1] += tex, s.bytes[1] += SeLen(e);
            else st.outfitsDropped += outfit, st.texDropped += tex;
            if (tex && st.drops && keep[k]) {
                const int n = SlimFindEntry(j, e);
                if (n >= 0 && !(j.flag[n] & (SF_KEPT | SF_DROPREACH))) st.texOrphans++;
            }
        }
        s.dead = f.v.size > 96 + live + f.ixBytes ? f.v.size - 96 - live - f.ixBytes : 0;
        s.entries[1] = nk, s.bytes[1] += 96 + 4 + nk * 32;
        if (f.ixFlags) s.result = "left (compact index: not rewritten)";  // never seen on an ExportDB; the rewrite writes flags 0
        else if (nk == f.count && s.dead < (1u << 20)) s.result = "left (under 1 MB to gain)";
        else if (st.mode == 0) s.result = "left (Mode 0: measured only)";
        else s.result = SlimRewrite(j, f, keep, nk, tmp);
    }
    st.writeMs = UsSince(t0) / 1000;
}

// The [SaveSlim] job on one save folder (worker, or saveslim-tool.exe); all memory from a private heap, freed at the end.
static void SlimRun(const wchar_t* dir, const wchar_t* tmp, int mode, SlimStats* st) {
    memset(st, 0, sizeof(*st));
    st->mode = mode;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    SlimJob* j = NULL;
    HANDLE heap = HeapCreate(HEAP_NO_SERIALIZE, 0, 0);
    if (heap) j = (SlimJob*)HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(SlimJob));
    if (!j) st->fail = "out of memory";
    else {
        j->heap = heap, j->st = st;
        SlimJobRun(*j, dir, tmp);
        for (int i = 0; i < j->nf; i++) SvClose(j->f[i].v);
    }
    if (heap) HeapDestroy(heap);
    st->totalMs = UsSince(t0) / 1000;
}

static void SlimLog(const SlimStats& s) {
    static const char* kMode[] = {"measure only", "compact", "compact + drop"};
    Log("save slim: %s, %d files, %d ExportDB, %s; outfits %lu, dropped %lu outfits + %lu textures (orphan textures kept %lu); "
        "scan %.0f MB, %lu instances, %lu links, %ld ms; write %ld ms; total %ld ms%s%s",
        kMode[s.mode], s.files, s.ndb, s.drops ? "references read" : s.mode != 1 ? "references NOT read, no drops" : "no drops",
        s.outfits, s.outfitsDropped, s.texDropped, s.texOrphans, s.scanMB, s.nodes, s.edges, s.scanMs, s.writeMs, s.totalMs,
        s.fail ? "; " : "", s.fail ? s.fail : "");
    for (int i = 0; i < s.ndb; i++) {
        const SlimDbStats& d = s.db[i];
        Log("save slim: %ls: entries %lu -> %lu, outfits %lu -> %lu, textures %lu -> %lu, %.1f -> %.1f MB (dead %.1f MB): %s", d.name,
            d.entries[0], d.entries[1], d.outfits[0], d.outfits[1], d.textures[0], d.textures[1], d.bytes[0] / 1048576.0,
            d.bytes[1] / 1048576.0, d.dead / 1048576.0, d.result ? d.result : "not reached");
    }
}

// --- in game: the worker after the save, the waits ---
static bool g_slOn;
static LONG g_slMode;
static struct { wchar_t dir[MAX_PATH], tmp[MAX_PATH]; HANDLE thread, after; bool want; } g_sl;

static void SlimNote(const unsigned char* info) {  // SaveInfoHook (sim thread), normal saves: the saved folder of this save
    g_sl.want = false;
    if (!g_slOn) return;
    const wchar_t* nhd = (const wchar_t*)(info + kInfoNhdPath);
    if (!Readable(nhd, 520 * sizeof(wchar_t)) || wcsnlen(nhd, 520) >= MAX_PATH || !EndsWithI(nhd, L".nhd")) return;
    wchar_t user[MAX_PATH];
    wcscpy_s(g_sl.dir, nhd);
    CutLast(g_sl.dir);  // <Saves>\<name>.sims3
    wcscpy_s(user, g_sl.dir);
    CutLast(user);
    if (!EndsWithI(g_sl.dir, L".sims3") || !EndsWithI(user, L"\\Saves")) return;
    CutLast(user);  // the TS3 user folder: same volume as Saves (rename works), not listed as a save
    g_sl.want = Join(g_sl.tmp, user, L"ts3native-slim.tmp");
}

static DWORD WINAPI SlimMain(LPVOID) {
    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);  // low CPU and I/O priority
    if (g_sl.after) {  // [SaveFlush]'s worker still flushing the same files
        WaitForSingleObject(g_sl.after, INFINITE);
        CloseHandle(g_sl.after);
    }
    SlimStats s;
    SlimRun(g_sl.dir, g_sl.tmp, (int)g_slMode, &s);
    SlimLog(s);
    return 0;
}

static void SlimStart(DWORD r) {  // SaveGameHook (sim thread), after the save returned r (0 = saved)
    const bool want = g_sl.want;
    g_sl.want = false;
    if (!want || r) return;
    g_sl.after = NULL;
    if (g_fj.thread) DuplicateHandle(GetCurrentProcess(), g_fj.thread, GetCurrentProcess(), &g_sl.after, SYNCHRONIZE, FALSE, 0);
    g_sl.thread = CreateThread(NULL, 256 * 1024, SlimMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (g_sl.thread) return;
    if (g_sl.after) CloseHandle(g_sl.after);
    Log("save slim: no worker thread (err %lu), the save stays as written", GetLastError());
}

static void SlimWait() {  // sim thread: before the next save or the load copy touches a save folder
    if (!g_sl.thread) return;
    if (WaitForSingleObject(g_sl.thread, 0) == WAIT_TIMEOUT) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        UdWait("the save slim worker");
        WaitForSingleObject(g_sl.thread, INFINITE);
        UdWait(NULL);
        Log("save slim: waited %ld ms for the worker", UsSince(t0) / 1000);
    }
    CloseHandle(g_sl.thread);
    g_sl.thread = NULL;
}

// [SaveSlim] Enabled=1 (needs [SaveWrite] hooked): the worker above after each normal save. Mode 0 = Mode 2 measured and
// logged, nothing written; 1 = rewrite without dead space; 2 = also drop the outfits nothing names and the textures only
// they name.
static void ApplySaveSlim(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"SaveSlim", L"Enabled", 0, ini)) { Log("save slim off (Enabled=0)"); return; }
    if (!ts3native_counters.saveReady) { Log("SKIP save slim: needs the [SaveWrite] hooks (Enabled=1)"); return; }
    g_slMode = Clamp(GetPrivateProfileIntW(L"SaveSlim", L"Mode", 2, ini), 0, 2);
    static const CallSite kLoadCopy = {LOAD_COPY_SITE, SAVE_COPY, (void*)LoadCopyHook};
    if (!ts3native_counters.flushReady && !PatchCalls("save slim (load copy wait)", &kLoadCopy, 1)) return;
    g_slOn = true;
    Log("save slim settings: Mode=%ld", g_slMode);
}
