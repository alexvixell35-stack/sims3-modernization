// Host-side self-check of exitdiag's pure parts. Optional argument: a Steam 1.67 TS3W.exe copy, whose bytes at every site
// must equal the site table (skipped when not given or not found).
#include "exitdiag-core.h"
#include <stdlib.h>

static int g_fail;
#define CHECK(c) do { if (!(c)) { printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); g_fail++; } } while (0)

struct Fake { uint32_t base; uint8_t mem[0x400]; };
static bool FakeRd(uint32_t a, void* d, uint32_t n, void* ctx) {
    Fake* f = (Fake*)ctx;
    if (a < f->base || a + n > f->base + sizeof(f->mem)) return false;
    memcpy(d, f->mem + (a - f->base), n);
    return true;
}
static void Put(Fake& f, uint32_t a, uint32_t v) { memcpy(f.mem + (a - f.base), &v, 4); }

static void TestSites() {
    for (int i = 0; i < S_COUNT; i++) {
        CHECK(kSites[i].len >= 5 && kSites[i].len <= 8);  // a jmp rel32 fits, patch buffer holds it
        for (int j = 0; j < i; j++)  // no overlap
            CHECK(kSites[i].addr + kSites[i].len <= kSites[j].addr || kSites[j].addr + kSites[j].len <= kSites[i].addr);
    }
    uint8_t b[8];
    memcpy(b, kSites[S_WIN1].bytes, 5);
    CHECK(SiteBytesMatch(b, kSites[S_WIN1]));
    b[3] = 0xE9;  // someone else's patch
    CHECK(!SiteBytesMatch(b, kSites[S_WIN1]));
    // 0x58941D call decodes to 0x59F060, the stub's jump target
    int32_t rel;
    memcpy(&rel, kSites[S_MGR].bytes + 1, 4);
    CHECK(kSites[S_MGR].addr + 5 + rel == 0x59F060);
}

static void TestExe(const char* path) {
    FILE* f = NULL;
    if (fopen_s(&f, path, "rb")) f = NULL;
    if (!f) { printf("skip exe check: %s not found\n", path); return; }
    fseek(f, 0, SEEK_END);
    const long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t* d = (uint8_t*)malloc(n);
    CHECK(d && fread(d, 1, n, f) == (size_t)n);
    fclose(f);
    if (!d) return;
    const uint32_t pe = *(uint32_t*)(d + 0x3C);
    CHECK(*(uint32_t*)(d + pe + 8) == kExeStamp);
    CHECK(*(uint32_t*)(d + pe + 24 + 56) == kExeImageSize);
    const uint16_t nsec = *(uint16_t*)(d + pe + 6), opt = *(uint16_t*)(d + pe + 20);
    const uint8_t* sec = d + pe + 24 + opt;
    for (int i = 0; i < S_COUNT; i++) {
        const uint32_t rva = kSites[i].addr - 0x400000;
        bool found = false;
        for (int s = 0; s < nsec; s++) {
            const uint8_t* h = sec + s * 40;
            const uint32_t va = *(uint32_t*)(h + 12), vs = *(uint32_t*)(h + 8), raw = *(uint32_t*)(h + 20);
            if (rva >= va && rva + kSites[i].len <= va + vs) {
                found = true;
                if (!SiteBytesMatch(d + raw + (rva - va), kSites[i])) { printf("FAIL site %s bytes differ in the exe\n", kSites[i].name); g_fail++; }
            }
        }
        CHECK(found);
    }
    free(d);
    printf("exe check: %d sites match %s\n", S_COUNT, path);
}

static void TestMods() {
    ModRange t[2] = {{0x400000, 0xE5A000, "TS3W.exe"}, {0x10000000, 0x1000, "a.dll"}};
    CHECK(ModFind(t, 2, 0x400000) == 0 && ModFind(t, 2, 0x400000 + 0xE59FFF) == 0 && ModFind(t, 2, 0x400000 + 0xE5A000) == -1);
    CHECK(ModFind(t, 2, 0x10000FFF) == 1 && ModFind(t, 2, 0x3FFFFF) == -1);
    char s[112];
    ModFmt(t, 2, 0x7F0F50, s, sizeof(s));
    CHECK(!strcmp(s, "007F0F50 (TS3W.exe+0x3F0F50)"));
    ModFmt(t, 2, 0xA8EAB8A2, s, sizeof(s));
    CHECK(!strcmp(s, "A8EAB8A2 (not in a module)"));
}

static void TestMask() {
    wchar_t o[512];
    MaskPath(L"C:\\Users\\<user>\\AppData\\x.dll", L"D:\\Games\\The Sims 3", L"C:\\Users\\<user>", L"Bob", o, 512);
    CHECK(!wcscmp(o, L"%USERPROFILE%\\AppData\\x.dll"));
    MaskPath(L"d:\\games\\the sims 3\\Game\\Bin\\TS3W.exe", L"D:\\Games\\The Sims 3", L"C:\\Users\\<user>", L"Bob", o, 512);
    CHECK(!wcscmp(o, L"<game>\\Game\\Bin\\TS3W.exe"));
    MaskPath(L"E:\\bob-stuff\\BOB\\y.asi", L"D:\\G", L"C:\\Users\\<user>", L"Bob", o, 512);
    CHECK(!wcscmp(o, L"E:\\<user>-stuff\\<user>\\y.asi"));
    MaskPath(L"D:\\Games\\The Sims 3x\\a", L"D:\\Games\\The Sims 3", NULL, NULL, o, 512);  // prefix must end at a separator
    CHECK(!wcscmp(o, L"D:\\Games\\The Sims 3x\\a"));
    MaskPath(L"C:\\Windows\\SYSTEM32\\ntdll.dll", NULL, NULL, L"", o, 512);
    CHECK(!wcscmp(o, L"C:\\Windows\\SYSTEM32\\ntdll.dll"));
}

static void TestHooks() {
    static Fake f;
    f.base = 0x20000000;
    memset(f.mem, 0, sizeof(f.mem));
    const uint32_t svc = f.base, buckets = f.base + 0x100, n1 = f.base + 0x200, n2 = f.base + 0x240, n3 = f.base + 0x280;
    Put(f, svc + 0x14, buckets);
    Put(f, svc + 0x18, 3);
    Put(f, svc + 0x1C, 3);
    Put(f, buckets + 0, n1);  // bucket 0: n1 -> n2
    Put(f, buckets + 8, n3);  // bucket 2: n3; bucket 1 empty
    Put(f, buckets + 12, 0xFFFFFFFF);  // EASTL sentinel after the last bucket: never read
    Put(f, n1, 0x9);  Put(f, n1 + 0xC, 1); Put(f, n1 + 0x18, 0x5166B754); Put(f, n1 + 0x1C, 0x10); Put(f, n1 + 0x24, n2);
    Put(f, n2, 0xA);  Put(f, n2 + 0x18, 0x5166B754); Put(f, n2 + 0x20, 0x44A3C810);
    Put(f, n3, 0xD);  Put(f, n3 + 0x1C, 0x11);
    HookNode out[8];
    uint32_t elems;
    CHECK(WalkHooks(FakeRd, &f, svc, out, 8, &elems) == 3 && elems == 3);
    CHECK(out[0].handle == 9 && out[0].dC == 1 && out[0].window == 0x5166B754 && (out[0].flags & 0x10) && out[0].next == n2);
    CHECK(out[1].handle == 0xA && out[1].obj20 == 0x44A3C810 && out[2].handle == 0xD && out[2].addr == n3);
    CHECK(WalkHooks(FakeRd, &f, svc, out, 2, &elems) == 2);  // max respected
    Put(f, n2 + 0x24, 0x7FFF0000);  // chain into unreadable memory: stop that chain, keep the rest
    CHECK(WalkHooks(FakeRd, &f, svc, out, 8, &elems) == 3);
    Put(f, svc + 0x18, 0);  // empty / torn-down map header
    CHECK(WalkHooks(FakeRd, &f, svc, out, 8, &elems) == -2);
    CHECK(WalkHooks(FakeRd, &f, 0x7FFF0000, out, 8, &elems) == -1);
}

static void TestCollect() {
    wchar_t o[2048];
    CHECK(BuildCollectList(L"D:\\G\\Game\\Bin", L"C:\\D\\Electronic Arts\\The Sims 3", o, 2048) == 5);
    CHECK(wcsstr(o, L"D:\\G\\Game\\Bin\\ApexRadiance_*.txt\r\n") && wcsstr(o, L"D:\\G\\Game\\Bin\\ts3native*.log\r\n"));
    CHECK(wcsstr(o, L"The Sims 3\\Apex Radiance\\ApexRadiance_*.txt\r\n") && wcsstr(o, L"The Sims 3\\xcpt*.txt\r\n"));
    CHECK(BuildCollectList(L"", L"C:\\D", o, 2048) == 3 && !wcsstr(o, L"ts3native*.log"));
}

int main(int argc, char** argv) {
    TestSites();
    TestMods();
    TestMask();
    TestHooks();
    TestCollect();
    if (argc > 1) TestExe(argv[1]);
    printf(g_fail ? "exitdiag-test: %d FAIL\n" : "exitdiag-test: all passed\n", g_fail);
    return g_fail ? 1 : 0;
}
