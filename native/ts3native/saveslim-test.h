// TestSaveSlim ([SaveSlim], saveslim.cpp, docs/15 phase 11). Included by selftest.cpp. Made-up packages in a temp folder.
struct StEnt { DWORD type, group; u64 inst; u8 body[48]; bool comp; };

static DWORD StRefpack(u8* out, const u8* p, DWORD n) {  // literal-only RefPack stream
    u8* o = out;
    *o++ = 0x10, *o++ = 0xFB, *o++ = (u8)(n >> 16), *o++ = (u8)(n >> 8), *o++ = (u8)n;
    for (; n >= 4;) {
        const DWORD k = n / 4 > 28 ? 28 : n / 4;
        *o++ = (u8)(0xE0 | (k - 1));
        memcpy(o, p, k * 4), o += k * 4, p += k * 4, n -= k * 4;
    }
    *o++ = (u8)(0xFC | n);
    memcpy(o, p, n);
    return (DWORD)(o + n - out);
}

// A DBPF 2.0 file like the game's saves: gap dead bytes before each body, dead bytes before the index, then the index.
static void StWritePkg(const wchar_t* path, const StEnt* es, int n, DWORD gap, DWORD deadTail, WORD badComp = 0) {
    const DWORD cap = 96 + n * (gap + 128) + deadTail + 4 + n * 32;
    u8* f = (u8*)calloc(cap, 1);
    DWORD pos = 96;
    u8 idx[4 + 32 * 32] = {};
    for (int i = 0; i < n; i++) {
        memset(f + pos, 0xEE, gap), pos += gap;
        const StEnt& e = es[i];
        const DWORD len = e.comp ? StRefpack(f + pos, e.body, 48) : 48;
        if (!e.comp) memcpy(f + pos, e.body, 48);
        DWORD* x = (DWORD*)(idx + 4 + i * 32);
        x[0] = e.type, x[1] = e.group, x[2] = (DWORD)(e.inst >> 32), x[3] = (DWORD)e.inst, x[4] = pos, x[5] = len | 0x80000000u, x[6] = 48;
        *(WORD*)(idx + 4 + i * 32 + 28) = (WORD)(i == n - 1 && badComp ? badComp : e.comp ? 0xFFFF : 0);
        *(WORD*)(idx + 4 + i * 32 + 30) = 1;
        pos += len;
    }
    memset(f + pos, 0xEE, deadTail), pos += deadTail;
    memcpy(f + pos, idx, 4 + n * 32);
    memcpy(f, "DBPF", 4);
    *(DWORD*)(f + 4) = 2, *(DWORD*)(f + 36) = n, *(DWORD*)(f + 44) = 4 + n * 32, *(DWORD*)(f + 60) = 3, *(DWORD*)(f + 64) = pos;
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    DWORD w;
    assert(h != INVALID_HANDLE_VALUE && WriteFile(h, f, pos + 4 + n * 32, &w, NULL));
    CloseHandle(h);
    free(f);
}

// Meta.data's shape: one entry, index flags 7 (type, group, instance-high once after the flags word, 20-byte entries).
// body = NULL: an empty package (96-byte header, no index), as some TravelDB.package files are.
static void StWriteCompact(const wchar_t* path, const u8* body, DWORD n) {
    u8 f[96 + 64 + 36] = {};
    memcpy(f, "DBPF", 4);
    *(DWORD*)(f + 4) = 2, *(DWORD*)(f + 60) = 3;
    DWORD len = 96;
    if (body) {
        memcpy(f + 96, body, n);
        DWORD* x = (DWORD*)(f + 96 + n);
        x[0] = 7, x[1] = 0x0000BEEF, x[2] = 0, x[3] = 0, x[4] = 1, x[5] = 96, x[6] = n | 0x80000000u, x[7] = n, x[8] = 0x00010000;
        *(DWORD*)(f + 36) = 1, *(DWORD*)(f + 44) = 36, *(DWORD*)(f + 64) = 96 + n;
        len = 96 + n + 36;
    }
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    DWORD w;
    assert(h != INVALID_HANDLE_VALUE && WriteFile(h, f, len, &w, NULL));
    CloseHandle(h);
}

static u8* StRead(const wchar_t* path, DWORD* n) {
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    assert(h != INVALID_HANDLE_VALUE);
    *n = GetFileSize(h, NULL);
    u8* b = (u8*)malloc(*n + 1);
    DWORD r;
    assert(ReadFile(h, b, *n, &r, NULL) && r == *n);
    CloseHandle(h);
    return b;
}

// Every entry of `now` is an entry of `was` with the same fields and stored bytes (only the offset differs), packed after the
// header; returns their count.
static DWORD StSameEntries(const u8* was, const u8* now, DWORD nowLen) {
    const DWORD n = SeU32(now, 36), pos = SeU32(now, 64), wn = SeU32(was, 36), wpos = SeU32(was, 64);
    assert(!memcmp(now, was, 36) && !memcmp(now + 40, was + 40, 4) && !memcmp(now + 48, was + 48, 16) && !memcmp(now + 68, was + 68, 28));
    assert(SeU32(now, 44) == 4 + n * 32 && pos + 4 + n * 32 == nowLen && !SeU32(now, pos));
    DWORD next = 96;
    for (DWORD i = 0; i < n; i++) {
        const u8* e = now + pos + 4 + i * 32;
        bool found = false;
        for (DWORD k = 0; k < wn && !found; k++) {
            const u8* o = was + wpos + 4 + k * 32;
            if (memcmp(e, o, 16) || memcmp(e + 20, o + 20, 12)) continue;
            found = true;
            assert(!memcmp(now + SeU32(e, 16), was + SeU32(o, 16), SeLen(e)));
        }
        assert(found);
        next += SeLen(e);
    }
    assert(next == pos);  // packed: no dead bytes
    return n;
}

static bool StHas(const u8* pkg, DWORD type, u64 inst) {
    const DWORD n = SeU32(pkg, 36), pos = SeU32(pkg, 64);
    for (DWORD i = 0; i < n; i++) {
        const u8* e = pkg + pos + 4 + i * 32;
        if (SeU32(e, 0) == type && SeU32(e, 8) == (DWORD)(inst >> 32) && SeU32(e, 12) == (DWORD)inst) return true;
    }
    return false;
}

static void TestSaveSlim() {
    QueryPerformanceFrequency(&g_qpf);
    g_slimWin = 0x10000;  // windows remap inside every file
    const u64 A = 0x1A0000000000A001ull, B = 0x1B0000000000B002ull, E = 0x1E0000000000E003ull, D = 0x1D0000000000D004ull,
              H = 0x1F0000000000F005ull, T1 = 0x2100000000001006ull, T2 = 0x2200000000002007ull, G = 0x2300000000003008ull,
              F1 = 0x3100000000004009ull, F2 = 0x320000000000500Aull, F3 = 0x330000000000600Bull, O = 0x340000000000700Cull;
    StEnt db[] = {{kSlimSimo, 0, A, {}, true},  {kSlimSimo, 0, B, {}, false}, {kSlimSimo, 0, E, {}, false},
                  {kSlimSimo, 0, D, {}, false}, {kSlimSimo, 0, H, {}, true},  {kSlimTxtc, 0, T1, {}, true},
                  {kSlimTxtc, 0, T2, {}, false}, {kSlimTxtc, 1, G, {}, false}, {kSlimTxtf, 0, F1, {}, false},
                  {kSlimTxtf, 0, F2, {}, true}, {kSlimTxtf, 0, F3, {}, false}, {kSlimTxtf, 0, O, {}, false}};
    const int nDb = sizeof(db) / sizeof(db[0]);
    for (int i = 0; i < nDb; i++) memset(db[i].body, 0x55, 48);
    memcpy(db[0].body + 5, &T1, 8), memcpy(db[0].body + 21, &E, 8);  // A names T1 and outfit E
    memcpy(db[1].body + 3, &T2, 8);                                   // B (named by nothing) names T2
    memcpy(db[5].body + 9, &F1, 8);                                   // T1 names F1
    memcpy(db[6].body + 0, &F2, 8), memcpy(db[6].body + 30, &F1, 8);  // T2 names F2 and F1
    memcpy(db[7].body + 7, &F3, 8);                                   // G (group 1: not a candidate) names F3
    StEnt nhd[] = {{0x12345678, 0, 1, {}, false}, {0x0580A2CE, 0, 2, {}, true}, {0x0580A2CD, 0, 3, {}, false}};
    for (int i = 0; i < 3; i++) memset(nhd[i].body, 0x66, 48);
    memcpy(nhd[0].body + 13, &A, 8);  // the world names outfit A at an odd offset
    StEnt travel[] = {{0x04F88964, 0, D, {}, false}};  // D: only an index key of another package names it
    memset(travel[0].body, 0x77, 48);
    u8 meta[40];
    memset(meta, 0x11, sizeof(meta));
    memcpy(meta + 11, &H, 8);  // H: only Meta.data's body names it

    wchar_t root[MAX_PATH], dir[MAX_PATH], tmp[MAX_PATH], pdb[MAX_PATH], pnhd[MAX_PATH], p[MAX_PATH];
    GetTempPathW(MAX_PATH, root);
    swprintf_s(root + wcslen(root), MAX_PATH - wcslen(root), L"ts3n-slim-%lu", GetCurrentProcessId());
    CreateDirectoryW(root, NULL);
    Join(dir, root, L"x.sims3");
    Join(tmp, root, L"ts3native-slim.tmp");
    Join(pdb, dir, L"W_0x1ExportDB.package");
    Join(pnhd, dir, L"W_0x1.nhd");
    // case: 0 = Mode 0, 1 = Mode 1, 2 = Mode 2, 3 = Mode 2 on case 2's result (nothing left to gain), 4 = Mode 2 with an
    // unknown compression in the .nhd, 5 = Mode 2 with a compressed .nhd body that does not decode
    for (int c = 0; c <= 5; c++) {
        if (c != 3) {
            RemoveTree(dir);
            CreateDirectoryW(dir, NULL);
            StWritePkg(pdb, db, nDb, 100, (1u << 20) + 4096);
            StWritePkg(pnhd, nhd, 3, 7, 300, c == 4 ? 0x5550 : 0);
            if (c == 5) {  // a literal run of 112 bytes in a 55-byte stream: the decoder fails
                DWORD n;
                u8* f = StRead(pnhd, &n);
                const u8* e = f + SeU32(f, 64) + 4 + 32;
                f[SeU32(e, 16) + 5] = 0xFB;
                HANDLE h = CreateFileW(pnhd, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
                DWORD w;
                assert(h != INVALID_HANDLE_VALUE && WriteFile(h, f, n, &w, NULL));
                CloseHandle(h);
                free(f);
            }
            Join(p, dir, L"TravelDB.package");
            StWritePkg(p, travel, 1, 0, 0);
            Join(p, dir, L"Meta.data");
            StWriteCompact(p, meta, sizeof(meta));
            Join(p, dir, L"Empty.package");
            StWriteCompact(p, NULL, 0);
            Join(p, dir, L"1a5tf1l3.dat");  // a plain file: scanned raw
            HANDLE h = CreateFileW(p, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
            DWORD w;
            assert(h != INVALID_HANDLE_VALUE && WriteFile(h, "plain file, not a package", 25, &w, NULL));
            CloseHandle(h);
        }
        DWORD n0, n1;
        u8* was = StRead(pdb, &n0);
        SlimStats s;
        SlimRun(dir, tmp, c == 0 ? 0 : c == 1 ? 1 : 2, &s);
        u8* now = StRead(pdb, &n1);
        assert(!s.fail && s.ndb == 1 && s.files == 6 && GetFileAttributesW(tmp) == INVALID_FILE_ATTRIBUTES);
        const bool drops = c == 0 || c == 2;
        assert(s.drops == (c == 0 || c == 2 || c == 3) && (s.unreadable != 0) == (c >= 4));
        assert(s.outfitsDropped == (drops ? 1u : 0u) && s.texDropped == (drops ? 2u : 0u));
        if (c == 0 || c == 3) assert(n0 == n1 && !memcmp(was, now, n0));  // Mode 0, and nothing left to gain
        if (c == 0) assert(!strcmp(s.db[0].result, "left (Mode 0: measured only)") && s.db[0].entries[1] == 9);
        if (c == 3) assert(!strcmp(s.db[0].result, "left (under 1 MB to gain)") && s.texOrphans == 1);
        if (c != 0 && c != 3) {
            assert(!strcmp(s.db[0].result, "rewritten") && n1 < n0 - (1u << 20));
            const DWORD kept = StSameEntries(was, now, n1);
            assert(kept == (c == 2 ? 9u : 12u) && s.db[0].entries[1] == kept);
            const bool gone = c == 2;  // B, T2 and F2; everything else stays
            assert(StHas(now, kSlimSimo, B) != gone && StHas(now, kSlimTxtc, T2) != gone && StHas(now, kSlimTxtf, F2) != gone);
            assert(StHas(now, kSlimSimo, A) && StHas(now, kSlimSimo, E) && StHas(now, kSlimSimo, D) && StHas(now, kSlimSimo, H));
            assert(StHas(now, kSlimTxtc, T1) && StHas(now, kSlimTxtc, G) && StHas(now, kSlimTxtf, F1) && StHas(now, kSlimTxtf, F3));
            assert(StHas(now, kSlimTxtf, O));  // named by nothing: kept
        }
        free(was), free(now);
    }
    // a package the code does not know is never rewritten: index type word not 0
    StWritePkg(pdb, db, nDb, 100, (1u << 20) + 4096);
    DWORD n0, n1;
    u8* was = StRead(pdb, &n0);
    was[SeU32(was, 64)] = 1;
    HANDLE h = CreateFileW(pdb, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    DWORD w;
    assert(h != INVALID_HANDLE_VALUE && WriteFile(h, was, n0, &w, NULL));
    CloseHandle(h);
    SlimStats s;
    SlimRun(dir, tmp, 2, &s);
    u8* now = StRead(pdb, &n1);
    assert(s.ndb == 0 && !s.drops && n0 == n1 && !memcmp(was, now, n0));
    free(was), free(now);
    RemoveTree(root);
    RemoveDirectoryW(root);
    g_slimWin = 8u << 20;
    printf("selftest save slim OK\n");
}
