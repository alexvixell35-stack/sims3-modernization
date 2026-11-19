// docs/667: host test of floodmemo.cpp's search-side logic (FmCellEnter / FmPop / FmCellLeave) and floodmemo-core.h without
// Windows, Wine or a libc: the same cases as the Windows selftest (floodmemo-test.h is included as is), on fake 32-bit nodes
// and cells, plus the player-command rule with marks. Build and run on Linux (32-bit, as the game's pointers are):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/fm-host native/ts3native/floodmemo-host-test.cpp && /tmp/fm-host; echo $?
//   0 = pass; else 1 and "assert line N" (of floodmemo-test.h or this file) on stderr.
// Not covered here: the install checks and the loop stub's assembly (plancand.cpp), which need the game's code.
#define FLOODMEMO_HOST_TEST
#define __declspec(x)
#define NULL 0
typedef unsigned char u8;
typedef unsigned long DWORD;
typedef long LONG;
typedef long long LONGLONG;
typedef unsigned int size_t32;
union LARGE_INTEGER { LONGLONG QuadPart; };
enum { MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, PAGE_READWRITE = 4 };

extern "C" void* memset(void* d, int c, unsigned int n) {
    volatile unsigned char* p = (unsigned char*)d;
    while (n--) *p++ = (unsigned char)c;
    return d;
}
extern "C" void* memcpy(void* d, const void* s, unsigned int n) {
    volatile unsigned char* p = (unsigned char*)d;
    const unsigned char* q = (const unsigned char*)s;
    while (n--) *p++ = *q++;
    return d;
}
static long Sys(long nr, long a, long b, long c) {
    long r;
    __asm__ volatile("int $0x80" : "=a"(r) : "a"(nr), "b"(a), "c"(b), "d"(c) : "memory");
    return r;
}
[[noreturn]] static void Exit(long code) {
    Sys(1, code, 0, 0);
    __builtin_unreachable();
}
[[noreturn]] static void Fail(int line) {
    char b[32] = "assert line ";
    int at = 12;
    char d[12];
    int n = 0;
    do { d[n++] = (char)('0' + line % 10); line /= 10; } while (line);
    while (n) b[at++] = d[--n];
    b[at++] = '\n';
    Sys(4, 2, (long)b, at);
    Exit(1);
}
#define assert(x) do { if (!(x)) Fail(__LINE__); } while (0)
static int printf(const char*, ...) { return 0; }
static void Log(const char*, ...) {}

static LONG InterlockedIncrement(volatile LONG* p) { return ++*p; }
static LONGLONG g_fakeQpc;
static void QueryPerformanceCounter(LARGE_INTEGER* q) { q->QuadPart = (g_fakeQpc += 1000); }
static void QueryPerformanceFrequency(LARGE_INTEGER* q) { q->QuadPart = 1000000; }
static LARGE_INTEGER g_qpf;
static char g_fakeHeap[1 << 20] __attribute__((aligned(16)));
static int g_fakeAllocs;
static void* VirtualAlloc(void*, unsigned int size, int, int) { return !g_fakeAllocs++ && size <= sizeof(g_fakeHeap) ? g_fakeHeap : NULL; }
static DWORD GetCurrentThreadId() { return 1; }

// what floodmemo.cpp takes from the rest of ts3native.cpp
struct PdSess { bool open; LONG scope; DWORD handle; };
static PdSess t_pd;
static volatile DWORD g_pdTid;
struct AsyncSlot { DWORD handle; };
static AsyncSlot g_async;
static LONG PccScopeNow(bool* worker) {
    *worker = false;
    return t_pd.open && GetCurrentThreadId() == g_pdTid ? t_pd.scope : 0;
}
static bool g_fakeTracked;
static DWORD g_fakeCmd;
static bool NpmCmdHandle(DWORD handle, bool* tracked) {
    *tracked = g_fakeTracked;
    return g_fakeTracked && handle && handle == g_fakeCmd;
}
static volatile LONG g_nmClearSeq, g_snWriteSeq;
static LONG t_fmHostSeq;

#include "floodmemo.cpp"
#include "floodmemo-test.h"

// FloodMemoAll=1 with ts3fixes' command marks: a household plan that is not a command is served, a marked one never
static void TestFmCommand() {
    FloodMemoCounters& c = ts3native_floodmemo;
    memset((void*)&c, 0, sizeof(c));
    FmFakeWorld();
    c.fmMode = 1;
    g_fmAll = true;
    t_pd.open = true, t_pd.scope = 1, t_pd.handle = 0x25000001, g_pdTid = 1;
    FmFakeSetup(0, 0, -1);
    assert(FmFakeSearch(0, 0, 2, 0) == 4 && c.fmStores == 1);
    FmFakeSetup(1, 1, -1);
    g_fakeTracked = false;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmCmdMiss == 1 && c.fmHits == 0);  // no marks: may be a command
    g_fakeTracked = true, g_fakeCmd = 0x25000001;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmCmdMiss == 2 && c.fmHits == 0);  // this plan is the player's command
    g_fakeCmd = 0x25000002;
    assert(FmFakeSearch(1, 1, 2, 0) == -1 && c.fmHits == 1);                     // marks on, another route: served
    t_pd.handle = 0;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmCmdMiss == 3 && c.fmHits == 1);  // no route handle: unknown, runs
    g_fmAll = false;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmScopeMiss == 1 && c.fmHits == 1);
    assert(c.fmChecks == 4 && c.fmMismatch == 0);
    t_pd.open = false;
}

extern "C" [[noreturn]] void _start() {
    TestFloodMemo();
    TestFmCommand();
    Exit(0);
}
