// docs/676 §14: host test of [WorldPrecompute] SmoothMemo (smoothmemo.cpp's three hooks and smoothmemo-core.h) without
// Windows, Wine or a libc: smoothmemo-test.h is included as is, plus the command rule with marks. Build and run on Linux
// (32-bit, as the game's pointers are):
//   clang++ -m32 -O1 -ffreestanding -nostdlib -fno-exceptions -fno-rtti -fno-stack-protector -static -Wno-everything \
//     -o /tmp/sm-host native/ts3native/smoothmemo-host-test.cpp && /tmp/sm-host; echo $?
//   0 = pass; else 1 and "assert line N" (of smoothmemo-test.h or this file) on stderr.
// Not covered here: the install checks (they need the game's code) and the pass hook's __finally (MSVC).
#define SMOOTHMEMO_HOST_TEST
#define __declspec(x)
#define __fastcall __attribute__((fastcall))
#define NULL 0
typedef unsigned char u8;
typedef unsigned long DWORD;
typedef long LONG;
typedef long long LONGLONG;
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
static LONGLONG InterlockedExchangeAdd64(volatile LONGLONG* p, LONGLONG v) { const LONGLONG o = *p; *p += v; return o; }
static LONGLONG g_fakeQpc;
static void QueryPerformanceCounter(LARGE_INTEGER* q) { q->QuadPart = (g_fakeQpc += 1000); }
static LARGE_INTEGER g_qpf = {1000000};
static char g_fakeHeap[512 << 10] __attribute__((aligned(16)));
static int g_fakeAllocs;
static void* VirtualAlloc(void*, unsigned int size, int, int) { return !g_fakeAllocs++ && size <= sizeof(g_fakeHeap) ? g_fakeHeap : NULL; }
static DWORD GetCurrentThreadId() { return 1; }

// what smoothmemo.cpp takes from the rest of ts3native.cpp
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
static volatile LONG g_snWriteSeq, g_snOn;
static LONG t_fmHostSeq;

#include "smoothmemo.cpp"
#include "smoothmemo-test.h"

// SmoothMemoAll=1 with ts3fixes' command marks: a household plan that is not a command is served, a marked one never
static void TestSmCommand() {
    SmoothMemoCounters& c = ts3native_smoothmemo;
    memset((void*)&c, 0, sizeof(c));
    g_smMultOrig = SmtMult, g_smClassOrig = SmtClass, g_smPassOrig = SmtPass;
    SmtWorld();
    c.smMode = 1, g_smAll = true, g_snOn = 1;
    t_pd.open = true, t_pd.scope = 1, t_pd.handle = 0x25000001, g_pdTid = 1;
    LONG runs = 0, refRuns = 0;
    const DWORD ref = SmtRefRun(200, 21, &refRuns);
    g_fakeTracked = false;
    assert(SmtRun(200, 21, &runs) == ref && c.smHits == 0 && c.smScopePasses == 1);  // no marks: may be a command
    g_fakeTracked = true, g_fakeCmd = 0x25000001;
    assert(SmtRun(200, 21, &runs) == ref && c.smHits == 0 && c.smScopePasses == 2);  // this plan is the player's command
    g_fakeCmd = 0x25000002;
    assert(SmtRun(200, 21, &runs) == ref && c.smHits > 0 && c.smScopePasses == 2 && runs < refRuns);  // marks on, another route: served
    const LONG h = c.smHits;
    g_smAll = false;
    assert(SmtRun(200, 21, &runs) == ref && c.smHits == h && c.smScopePasses == 3);
    assert(c.smMismatch == 0 && c.smChecks > 0);
    t_pd.open = false;
}

extern "C" [[noreturn]] __attribute__((force_align_arg_pointer)) void _start() {
    TestSmoothMemo();
    TestSmCommand();
    Exit(0);
}
