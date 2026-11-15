// Toy 32-bit target for rewind-spike.exe (docs/270): a deterministic "sim" thread (busy counter, RNG, private-region churn)
// that parks through the cooperation block, plus the things a rewind must put back or refuse: a thread in
// WaitForSingleObject, one in Sleep, a window thread in GetMessage, a named-pipe server parked in ConnectNamedPipe on a
// synchronous pipe (FSCTL_PIPE_LISTEN, like TS3W's launcher pipe), a thread pool, mapped views (with and without a section
// handle, file-backed, at an offset), file handles with positions, an ETW registration, a read-only and a no-access page.
// A command thread runs ToyCmd codes the host writes into ToyState.cmd; C_AUDIT checks that the world still matches the
// state block. Prints "state <address>" on stdout, then runs up to 2 min.
//   rewind-toy.exe [nocoop|nopark|audio|audioflag|extshare|avx|rearmreg|rearmdead|ts3npark]
#include <windows.h>
#include <mmsystem.h>
#include <evntprov.h>
#include <immintrin.h>
#include <stdio.h>
#include <string.h>
#include "rewind-toy.h"

static const unsigned K = 2654435761u;
static ToyState s;
static RewindCoop localCoop;
static RewindCoop* coop = &localCoop;
static volatile int bridgeRefuse;
// Fake bridge client: stats[0] = the op, so the selftest sees which op arrived.
static void bridgeOp() {
  uint32_t op = coop->op;
  if (!op || !coop->bridge) return;
  coop->bridgeStats[0] = op;
  coop->bridgeResult = bridgeRefuse && op != 1 ? (int32_t)0x80004005 : 0;
  coop->op = 0;
}
static LONGLONG qpf;
static bool nopark, avx;
static char filePath[MAX_PATH];
static const unsigned VIEW = 262144;

static LONGLONG qpc() { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart; }
static void timeCheck() {  // the clock the toy trusts: real time minus the host's skew; a jump over 300 ms is counted
  LONGLONG v = qpc() - coop->skewQpc;
  if (v - s.lastV > qpf * 3 / 10) s.timeJumps = s.timeJumps + 1;
  s.lastV = v;
}

static void sign(unsigned* v, unsigned words, unsigned sig) { v[0] = sig; v[words - 1] = ~sig; }
static bool signedOk(const unsigned* v, unsigned words, unsigned sig) {
  __try { return v[0] == sig && v[words - 1] == ~sig; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

__declspec(noinline) static unsigned codeTarget() { return 0x11223344; }
static unsigned (*volatile codeFn)() = codeTarget;

__declspec(noinline) static unsigned deep(int n) {
  volatile char pad[4096];
  pad[0] = (char)n;
  return n ? deep(n - 1) + pad[0] : 0;
}
// avx mode: the upper YMM halves stay live, so a WOW64 context alone cannot save this thread. Lane 7 (upper half) must
// always equal the plain sum: a rewind that puts back memory and registers but not the vector state breaks it.
__declspec(noinline) static void avxLoop() {
  __m256i y = _mm256_set1_epi32(1);
  unsigned sum = 1;
  while (!s.quit) {
    y = _mm256_add_epi32(y, _mm256_set1_epi32((int)s.tick)); sum += s.tick; s.tick = s.tick + 1;
    if ((unsigned)_mm256_extract_epi32(y, 7) != sum) s.bad |= B_INV;
  }
  s.a = (unsigned)_mm256_extract_epi32(y, 7);
}

static DWORD WINAPI waiter(void*) {
  for (;;) {
    WaitForSingleObject((HANDLE)(UINT_PTR)s.hEvt, INFINITE);
    s.wakes = s.wakes + 1;
    while (s.spin) s.spinCount = s.spinCount + 1;
    if (s.block2) { WaitForSingleObject((HANDLE)(UINT_PTR)s.hEvt2, INFINITE); s.block2 = 0; }
  }
}
static DWORD WINAPI sleeper(void*) {
  while (!s.t3exit) { Sleep(50); s.sleeps = s.sleeps + 1; while (s.sleeperSpin) s.spinCount = s.spinCount + 1; }
  return 0;
}
static DWORD WINAPI idle(void*) { Sleep(INFINITE); return 0; }
static char pipeName[64];
static HANDLE pipeClient;
// Named-pipe server on a synchronous handle: ConnectNamedPipe blocks in NtFsControlFile(FSCTL_PIPE_LISTEN); with a client
// it blocks in ReadFile until the client writes, then disconnects and listens again from the same place.
// Toy GC on a write-watch heap (like Boehm's GWW mode, docs/270 §15): each run takes the pages the kernel reports dirty
// (and resets the bits) into its shadow copy, then checks the shadow equals the heap. A page written but never reported
// leaves the shadow stale: B_GC. Dirty bits that are a superset only cost copying.
static unsigned char shadow[65536];
static void gc() {
  PVOID hit[16]; ULONG_PTR n = 16; ULONG gran;
  unsigned char* heap = (unsigned char*)(UINT_PTR)s.wwBase;
  if (GetWriteWatch(WRITE_WATCH_FLAG_RESET, heap, 65536, hit, &n, &gran)) { s.bad |= B_SETUP; return; }
  for (ULONG_PTR i = 0; i < n; ++i) memcpy(shadow + ((unsigned char*)hit[i] - heap), hit[i], 4096);
  s.gcRuns = s.gcRuns + 1;
  if (memcmp(shadow, heap, 65536)) s.bad |= B_GC;
}
// Worker that wakes and goes back into the same wait at the same loop site, calling NtWaitForSingleObject itself so the
// stack is known: mode 0 keeps a counter in the stack word just above the arguments (changes every round, memory),
// mode 1 keeps it in ebx (a register the caller sees after the return), mode 2 puts it in esi and overwrites esi right
// after the return (dead there, like KernelBase's WaitForSingleObjectEx).
typedef LONG (NTAPI* NtWait)(HANDLE, BOOLEAN, PLARGE_INTEGER);
static DWORD WINAPI rearm(void* arg) {
  int mode = (int)(UINT_PTR)arg;
  NtWait f = (NtWait)(void*)GetProcAddress(GetModuleHandleA("ntdll.dll"), "NtWaitForSingleObject");
  HANDLE h = (HANDLE)(UINT_PTR)(mode ? s.hEvtR1 : s.hEvtR0);
  int slot = mode ? 1 : 0;
  volatile unsigned n = 0;  // in stack memory: only mode 1 copies it into a register
  for (;;) {
    if (mode == 2) {
      __asm {
        mov esi, n
        push 0
        push 0
        push h
        call f
        mov esi, eax
      }
    } else if (mode) {
      __asm {
        mov ebx, n
        push 0
        push 0
        push h
        call f
      }
    } else {
      __asm {
        push n
        push 0
        push 0
        push h
        call f
        add esp, 4
      }
    }
    ++n;
    if (s.rearms[slot] + 1 != n) s.bad |= B_REARM;  // the stack local and the state block go back together
    s.rearms[slot] = n;
  }
}
// Busy worker (docs/270 §18): idle in a shallow WaitForMultipleObjects; when busy, each pulse sends it into a frame
// 0x1600 bytes deeper that waits there, so it is back at the shallow wait only between two pulses (mode 2: ~300 us of
// each ~20 ms; mode 1: hardly ever).
__declspec(noinline) static void busyDeep(HANDLE* hs) {
  volatile char pad[0x1600];
  pad[0] = 0;
  WaitForMultipleObjects(2, hs, FALSE, INFINITE);
  s.busyRuns = s.busyRuns + 1 + pad[0];
}
static RewindWorkerPark* tn;  // ts3npark mode: the worker park block, as ts3native [Rewind] Park=1 publishes it
static DWORD WINAPI busyWorker(void*) {
  HANDLE shallow[2] = {(HANDLE)(UINT_PTR)s.hEvtW0, (HANDLE)(UINT_PTR)s.hEvtW2};
  HANDLE deep[2] = {(HANDLE)(UINT_PTR)s.hEvtW1, (HANDLE)(UINT_PTR)s.hEvtW2};
  volatile unsigned n = 0;  // in stack memory: goes back with the state block
  for (;;) {
    if (tn) tn->idle = 1;
    WaitForMultipleObjects(2, shallow, FALSE, INFINITE);
    if (tn) tn->idle = 0;
    busyDeep(deep);
    ++n;
    if (s.busyRuns != n) s.bad |= B_REARM;
  }
}
static DWORD WINAPI busyPulser(void*) {
  for (;;) {
    if (!s.busy) { Sleep(5); continue; }
    if (tn && tn->request) {  // parked: no new work; the one under way ends, then the worker is back at the shallow wait
      if (!tn->idle) SetEvent((HANDLE)(UINT_PTR)s.hEvtW1);
      Sleep(5);
      continue;
    }
    SetEvent((HANDLE)(UINT_PTR)s.hEvtW1);  // the deep wait ends: back to the shallow one
    if (s.busy == 2) { LARGE_INTEGER a, b; QueryPerformanceCounter(&a); do QueryPerformanceCounter(&b); while ((b.QuadPart - a.QuadPart) * 1000000 / qpf < 300); }
    SetEvent((HANDLE)(UINT_PTR)s.hEvtW0);  // the shallow wait ends: into the deep one
    Sleep(20);
  }
}
// Condition-variable waiter (SleepConditionVariableSRW waits in NtWaitForAlertByThreadId, called from ntdll): waits for A,
// then for B one frame deeper, so its wait depth differs between rounds.
static SRWLOCK cvLock = SRWLOCK_INIT;
static CONDITION_VARIABLE cvA = CONDITION_VARIABLE_INIT, cvB = CONDITION_VARIABLE_INIT;
__declspec(noinline) static void waitB() {
  volatile char pad[512];
  pad[0] = 0;
  while (!s.addrB) SleepConditionVariableSRW(&cvB, &cvLock, INFINITE, 0);
  s.addrB = pad[0];
}
static DWORD WINAPI cvWaiter(void*) {
  AcquireSRWLockExclusive(&cvLock);
  for (;;) {
    while (!s.addrA) SleepConditionVariableSRW(&cvA, &cvLock, INFINITE, 0);
    s.addrA = 0; s.addrRuns = s.addrRuns + 1;
    waitB();
  }
}
static void cvSet(volatile uint32_t* v, CONDITION_VARIABLE* cv) {
  AcquireSRWLockExclusive(&cvLock); *v = 1; ReleaseSRWLockExclusive(&cvLock);
  WakeConditionVariable(cv); Sleep(50);
}
static DWORD WINAPI pipeServer(void* h) {
  for (;;) {
    if (!ConnectNamedPipe(h, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED) { s.bad |= B_SETUP; return 1; }
    s.pipeConns = s.pipeConns + 1;
    char b; DWORD got;
    ReadFile(h, &b, 1, &got, nullptr);
    DisconnectNamedPipe(h);
  }
}
static DWORD WINAPI tpWork(void* arg) {
  if (arg) Sleep(300);
  InterlockedIncrement((volatile LONG*)&s.tpRuns);
  return 0;
}
static LRESULT CALLBACK wndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
  if (m == WM_APP) { s.cbCount = s.cbCount + 1; while (s.cbSpin) s.spinCount = s.spinCount + 1; return 0; }
  return DefWindowProcA(h, m, w, l);
}
static DWORD WINAPI ui(void*) {
  WNDCLASSA wc{}; wc.lpfnWndProc = wndProc; wc.lpszClassName = "rewind-toy"; wc.hInstance = GetModuleHandleA(nullptr);
  RegisterClassA(&wc);
  s.hwnd = (uint32_t)(UINT_PTR)CreateWindowA("rewind-toy", "", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, wc.hInstance, nullptr);
  MSG msg;
  while (GetMessageA(&msg, nullptr, 0, 0) > 0) DispatchMessageA(&msg);
  return 0;
}

static void* mapNew(HANDLE* keep) {
  HANDLE h = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, VIEW, nullptr);
  void* v = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, 0);
  if (keep) *keep = h; else CloseHandle(h);
  return v;
}

static void audit() {
  if (!signedOk((unsigned*)(UINT_PTR)s.viewA, VIEW / 4, s.sigV) || !signedOk((unsigned*)(UINT_PTR)s.viewA2, 16384, s.sigV + 1)) s.bad |= B_VIEW_A;
  if (!signedOk((unsigned*)(UINT_PTR)s.viewB, VIEW / 4, s.sigV + 2)) s.bad |= B_VIEW_B;
  if (!signedOk((unsigned*)(UINT_PTR)s.viewF, 16384, 0)) s.bad |= B_VIEW_F;  // file words are their own index: 0 .. ~0 would fail, see setup
  HANDLE f = (HANDLE)(UINT_PTR)s.hFileA;
  unsigned word = 0; DWORD got = 0;
  if (SetFilePointer(f, 0, nullptr, FILE_CURRENT) != s.posA || !ReadFile(f, &word, 4, &got, nullptr) || got != 4 || word != s.posA / 4) s.bad |= B_FILE;
  SetFilePointer(f, (LONG)s.posA, nullptr, FILE_BEGIN);
  if (!signedOk((unsigned*)(UINT_PTR)s.roPage, 1024, s.sigV + 3)) s.bad |= B_RO;
  DWORD old;
  void* na = (void*)(UINT_PTR)s.naPage;
  VirtualProtect(na, 4096, PAGE_READONLY, &old);
  if (!signedOk((unsigned*)na, 1024, s.sigV + 4)) s.bad |= B_RO;
  VirtualProtect(na, 4096, PAGE_NOACCESS, &old);
  if (codeFn() != 0x11223344) s.bad |= B_CODE;
  int n = 0;  // semaphore count 2 and the manual event signaled, as set up
  while (WaitForSingleObject((HANDLE)(UINT_PTR)s.hSem, 0) == WAIT_OBJECT_0) ++n;
  if (n) ReleaseSemaphore((HANDLE)(UINT_PTR)s.hSem, n, nullptr);
  if (n != 2 || WaitForSingleObject((HANDLE)(UINT_PTR)s.hEvtM, 0) != WAIT_OBJECT_0) s.bad |= B_SYNC;
}

static void run(unsigned c) {
  DWORD old; HANDLE h;
  switch (c) {
    case C_AUDIT: audit(); break;
    case C_MAPNEW: sign((unsigned*)mapNew(&h), VIEW / 4, 7); sign((unsigned*)mapNew(nullptr), VIEW / 4, 8); break;
    case C_UNMAP_A: UnmapViewOfFile((void*)(UINT_PTR)s.viewA); UnmapViewOfFile((void*)(UINT_PTR)s.viewA2); break;
    case C_UNMAP_A_CLOSE: run(C_UNMAP_A); CloseHandle((HANDLE)(UINT_PTR)s.hSecA); CreateEventA(nullptr, 0, 0, nullptr); break;
    case C_REMAP_A_OTHER: {  // another section at view A's base
      UnmapViewOfFile((void*)(UINT_PTR)s.viewA);
      h = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, VIEW, nullptr);
      void* v = MapViewOfFileEx(h, FILE_MAP_ALL_ACCESS, 0, 0, 0, (void*)(UINT_PTR)s.viewA);
      if (v) memset(v, 0xEE, VIEW);
      break;
    }
    case C_UNMAP_B: UnmapViewOfFile((void*)(UINT_PTR)s.viewB); break;
    case C_SCRIBBLE: memset((void*)(UINT_PTR)s.viewA, 0xEE, VIEW); memset((void*)(UINT_PTR)s.viewB, 0xEE, VIEW); break;
    case C_FILE_OPEN: CreateFileA(filePath, GENERIC_READ, 7, nullptr, OPEN_EXISTING, 0, nullptr); break;
    case C_FILE_SEEK: { char buf[256]; DWORD got; ReadFile((HANDLE)(UINT_PTR)s.hFileA, buf, sizeof buf, &got, nullptr); SetFilePointer((HANDLE)(UINT_PTR)s.hFileA, 8192, nullptr, FILE_CURRENT); break; }
    case C_FILE_CLOSE: CloseHandle((HANDLE)(UINT_PTR)s.hFileA); for (int i = 0; i < 3; ++i) CreateEventA(nullptr, 0, 0, nullptr); break;  // the freed value gets reused
    case C_THREAD_NEW: CreateThread(nullptr, 0, idle, nullptr, 0, nullptr); Sleep(50); break;
    case C_THREAD_EXIT: s.t3exit = 1; Sleep(200); break;
    case C_SIGNAL: SetEvent((HANDLE)(UINT_PTR)s.hEvt); Sleep(50); break;
    case C_SPIN_ON: s.spin = 1; SetEvent((HANDLE)(UINT_PTR)s.hEvt); Sleep(50); break;
    case C_BLOCK2: s.block2 = 1; SetEvent((HANDLE)(UINT_PTR)s.hEvt); Sleep(50); break;
    case C_UNBLOCK2: SetEvent((HANDLE)(UINT_PTR)s.hEvt2); Sleep(50); break;
    case C_STACK: deep(100); break;  // ~400 KB of stack: the guard page moves down
    case C_PROTECT:
      VirtualProtect((void*)(UINT_PTR)s.roPage, 4096, PAGE_READWRITE, &old); *(unsigned*)(UINT_PTR)s.roPage = 0xBAD; VirtualProtect((void*)(UINT_PTR)s.roPage, 4096, PAGE_READONLY, &old);
      VirtualProtect((void*)(UINT_PTR)s.naPage, 4096, PAGE_READWRITE, &old); *(unsigned*)(UINT_PTR)s.naPage = 0xBAD; VirtualProtect((void*)(UINT_PTR)s.naPage, 4096, PAGE_NOACCESS, &old);
      break;
    case C_PATCH: {  // what a hook installed after the snapshot does: rewrite code bytes
      unsigned char* p = (unsigned char*)codeTarget;
      for (int i = 0; i < 24; ++i)
        if (*(unsigned*)(p + i) == 0x11223344) {
          VirtualProtect(p + i, 4, PAGE_EXECUTE_READWRITE, &old); *(unsigned*)(p + i) = 0x55667788; VirtualProtect(p + i, 4, old, &old);
          FlushInstructionCache(GetCurrentProcess(), p, 32);
          break;
        }
      break;
    }
    case C_TP_SLEEP: QueueUserWorkItem(tpWork, (void*)1, WT_EXECUTEDEFAULT); Sleep(20); break;  // a work item that sleeps 300 ms
    case C_TP: case C_TP_BURST: {
      unsigned want = s.tpRuns + (c == C_TP ? 1 : 8);
      for (unsigned i = s.tpRuns; i < want; ++i) QueueUserWorkItem(tpWork, (void*)(UINT_PTR)(c == C_TP_BURST), c == C_TP_BURST ? WT_EXECUTELONGFUNCTION : WT_EXECUTEDEFAULT);
      for (int i = 0; i < 3000 && s.tpRuns < want; ++i) Sleep(1);
      if (s.tpRuns < want) s.bad |= B_TP;
      break;
    }
    case C_ETW_CLOSE: EventUnregister(((REGHANDLE)s.etwHi << 32) | s.etwLo); break;
    case C_CB_ON: s.cbSpin = 1; SendNotifyMessageA((HWND)(UINT_PTR)s.hwnd, WM_APP, 0, 0); Sleep(50); break;
    case C_CB_OFF: s.cbSpin = 0; Sleep(50); break;
    case C_SYNC_DRAIN: WaitForSingleObject((HANDLE)(UINT_PTR)s.hSem, 0); WaitForSingleObject((HANDLE)(UINT_PTR)s.hSem, 0); ResetEvent((HANDLE)(UINT_PTR)s.hEvtM); break;
    case C_SYNC_UP: ReleaseSemaphore((HANDLE)(UINT_PTR)s.hSem, 5, nullptr); break;
    case C_SLEEPER_SPIN: s.sleeperSpin = 1; Sleep(120); break;
    case C_BRIDGE_ON: coop->bridge = 1; break;       // act as a bridge client: answer ops (docs/270 §12)
    case C_BRIDGE_REFUSE: bridgeRefuse = 1; break;  // the fake server refuses plan and rewind
    case C_PIPE_CONNECT: pipeClient = CreateFileA(pipeName, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr); Sleep(50); break;
    case C_PIPE_DONE: { DWORD put; WriteFile(pipeClient, "x", 1, &put, nullptr); CloseHandle(pipeClient); Sleep(50); break; }
    case C_WW_DIRTY: s.wwGen = s.wwGen + 1; ((unsigned*)(UINT_PTR)(s.wwBase + 3 * 4096))[5] = s.wwGen; break;
    case C_GC: gc(); break;
    case C_ADDR_A: cvSet(&s.addrA, &cvA); break;
    case C_ADDR_B: cvSet(&s.addrB, &cvB); break;
    case C_REARM: SetEvent((HANDLE)(UINT_PTR)s.hEvtR0); SetEvent((HANDLE)(UINT_PTR)s.hEvtR1); Sleep(50); break;
    case C_BUSY_NOGAP: case C_BUSY_GAP:  // into the deep wait first: each pulse then lets it out to the shallow wait and back
      SetEvent((HANDLE)(UINT_PTR)s.hEvtW0); Sleep(5); s.busy = c == C_BUSY_GAP ? 2 : 1; Sleep(100); break;
    case C_GUI: CreateWindowA("rewind-toy", "", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, GetModuleHandleA(nullptr), nullptr); break;
    case C_MUTEX: WaitForSingleObject((HANDLE)(UINT_PTR)s.hMtx, 0); break;  // the command thread now owns it
  }
}
static DWORD WINAPI commands(void*) {
  IsGUIThread(TRUE);  // window-system thread from the start, not from the first message it sends
  for (;;) {
    while (!s.cmd) Sleep(1);
    run(s.cmd);
    s.cmd = 0;
  }
}

static void setup(const char* mode) {
  LARGE_INTEGER f; QueryPerformanceFrequency(&f); qpf = f.QuadPart;
  nopark = !strcmp(mode, "nopark"); avx = !strcmp(mode, "avx");
  if (strcmp(mode, "nocoop")) {
    char name[64]; sprintf_s(name, "Local\\rewind-coop-%lu", GetCurrentProcessId());
    HANDLE m = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(RewindCoop), name);
    coop = (RewindCoop*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    if (!coop) { coop = &localCoop; s.bad |= B_SETUP; }
    coop->magic = REWIND_COOP_MAGIC; coop->version = 1; coop->self = (uint32_t)(UINT_PTR)coop;
  }
  s.sigV = 0xA110C8ED;
  // a temp file whose 32-bit words are their own index; one write handle (kept), one read handle with a position
  char dir[MAX_PATH]; GetTempPathA(MAX_PATH, dir);
  sprintf_s(filePath, "%srewind-toy-%lu.bin", dir, GetCurrentProcessId());
  HANDLE w = CreateFileA(filePath, GENERIC_READ | GENERIC_WRITE, 7, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
  static unsigned words[16384];
  for (unsigned i = 0; i < 16384; ++i) words[i] = i;
  words[16383] = ~0u;  // so signedOk(view, 16384, 0) holds for the file view
  DWORD put; WriteFile(w, words, sizeof words, &put, nullptr);
  HANDLE r = CreateFileA(filePath, GENERIC_READ, 7, nullptr, OPEN_EXISTING, 0, nullptr);
  s.hFileA = (uint32_t)(UINT_PTR)r; s.posA = 4000;
  SetFilePointer(r, (LONG)s.posA, nullptr, FILE_BEGIN);
  HANDLE fm = CreateFileMappingA(r, nullptr, PAGE_READONLY, 0, 0, nullptr);
  s.viewF = (uint32_t)(UINT_PTR)MapViewOfFile(fm, FILE_MAP_READ, 0, 0, 0);
  // view A + A2 (64 KB at offset 64 KB of the same section, handle kept), view B (handle closed: cannot be pinned)
  HANDLE a; void* va = mapNew(&a);
  s.hSecA = (uint32_t)(UINT_PTR)a; s.viewA = (uint32_t)(UINT_PTR)va;
  void* va2 = MapViewOfFile(a, FILE_MAP_ALL_ACCESS, 0, 65536, 65536);
  s.viewA2 = (uint32_t)(UINT_PTR)va2;
  void* vb = mapNew(nullptr);
  s.viewB = (uint32_t)(UINT_PTR)vb;
  if (w == INVALID_HANDLE_VALUE || r == INVALID_HANDLE_VALUE || !s.viewF || !va || !va2 || !vb) { s.bad |= B_SETUP; return; }
  sign((unsigned*)va, VIEW / 4, s.sigV); sign((unsigned*)va2, 16384, s.sigV + 1); sign((unsigned*)vb, VIEW / 4, s.sigV + 2);
  DWORD old;
  unsigned* ro = (unsigned*)VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
  sign(ro, 1024, s.sigV + 3); VirtualProtect(ro, 4096, PAGE_READONLY, &old); s.roPage = (uint32_t)(UINT_PTR)ro;
  unsigned* na = (unsigned*)VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
  sign(na, 1024, s.sigV + 4); VirtualProtect(na, 4096, PAGE_NOACCESS, &old); s.naPage = (uint32_t)(UINT_PTR)na;
  s.hEvt = (uint32_t)(UINT_PTR)CreateEventA(nullptr, FALSE, FALSE, nullptr);
  s.hEvt2 = (uint32_t)(UINT_PTR)CreateEventA(nullptr, FALSE, FALSE, nullptr);
  {  // as the bridge's handshake event (RewindDone_<guid>, util_guid.h, auto-reset, set by the server); the pid stands in for Data1
    char name[80]; sprintf_s(name, "Local\\RewindDone_%08lx-0000-0000-0000-000000000000", GetCurrentProcessId());
    CreateEventA(nullptr, FALSE, FALSE, name);
  }
  s.hSem = (uint32_t)(UINT_PTR)CreateSemaphoreA(nullptr, 2, 100, nullptr);
  s.hEvtM = (uint32_t)(UINT_PTR)CreateEventA(nullptr, TRUE, TRUE, nullptr);
  s.hMtx = (uint32_t)(UINT_PTR)CreateMutexA(nullptr, FALSE, nullptr);
  static const GUID provider = {0x5e1f0c1a, 0x7b2d, 0x4c3e, {0x9a, 0x11, 0x52, 0x45, 0x57, 0x49, 0x4e, 0x44}};
  REGHANDLE reg = 0; EventRegister(&provider, nullptr, nullptr, &reg);
  s.etwLo = (uint32_t)reg; s.etwHi = (uint32_t)(reg >> 32);
  if (!strcmp(mode, "audio")) {
    WAVEFORMATEX fmt{WAVE_FORMAT_PCM, 2, 44100, 176400, 4, 16, 0};
    HWAVEOUT out;
    s.audioOpen = waveOutOpen(&out, WAVE_MAPPER, &fmt, 0, 0, CALLBACK_NULL) == MMSYSERR_NOERROR;
  }
  if (!strcmp(mode, "audioflag")) coop->audioOn = 1;
  if (!strcmp(mode, "big")) {  // about the game's sizes (docs/270 §26): 900 MB private, 380 MB write-watch, all pages touched
    s.bigPrivMB = 900; s.bigWWMB = 380;
    s.bigPriv = (uint32_t)(UINT_PTR)VirtualAlloc(nullptr, (SIZE_T)s.bigPrivMB << 20, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    s.bigWW = (uint32_t)(UINT_PTR)VirtualAlloc(nullptr, (SIZE_T)s.bigWWMB << 20, MEM_COMMIT | MEM_RESERVE | MEM_WRITE_WATCH, PAGE_READWRITE);
    if (!s.bigPriv || !s.bigWW) s.bad |= B_SETUP;
    else {
      for (uint32_t i = 0; i < (s.bigPrivMB << 8); ++i) ((uint32_t*)(UINT_PTR)s.bigPriv)[i * 1024] = i * 2654435761u;
      for (uint32_t i = 0; i < (s.bigWWMB << 8); ++i) ((uint32_t*)(UINT_PTR)s.bigWW)[i * 1024] = i;
    }
  }
  s.wwBase = (uint32_t)(UINT_PTR)VirtualAlloc(nullptr, 65536, MEM_COMMIT | MEM_RESERVE | MEM_WRITE_WATCH, PAGE_READWRITE);
  if (!s.wwBase) s.bad |= B_SETUP;
  else { for (unsigned i = 0; i < 16; ++i) ((unsigned*)(UINT_PTR)s.wwBase)[i * 1024] = i; gc(); }
  s.hEvtR0 = (uint32_t)(UINT_PTR)CreateEventA(nullptr, FALSE, FALSE, nullptr);
  s.hEvtR1 = (uint32_t)(UINT_PTR)CreateEventA(nullptr, FALSE, FALSE, nullptr);
  CreateThread(nullptr, 0, rearm, (void*)0, 0, nullptr);
  if (!strcmp(mode, "rearmreg")) CreateThread(nullptr, 0, rearm, (void*)1, 0, nullptr);
  if (!strcmp(mode, "rearmdead")) CreateThread(nullptr, 0, rearm, (void*)2, 0, nullptr);
  CreateThread(nullptr, 0, cvWaiter, nullptr, 0, nullptr);
  s.hEvtW0 = (uint32_t)(UINT_PTR)CreateEventA(nullptr, FALSE, FALSE, nullptr);
  s.hEvtW1 = (uint32_t)(UINT_PTR)CreateEventA(nullptr, FALSE, FALSE, nullptr);
  s.hEvtW2 = (uint32_t)(UINT_PTR)CreateEventA(nullptr, TRUE, FALSE, nullptr);
  if (!strcmp(mode, "ts3npark")) {
    char name[64]; sprintf_s(name, "Local\\rewind-ts3n-%lu", GetCurrentProcessId());
    HANDLE m = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(RewindWorkerPark), name);
    tn = m ? (RewindWorkerPark*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, 0) : nullptr;
    if (tn) { tn->version = 1; tn->magic = REWIND_TS3N_MAGIC; } else s.bad |= B_SETUP;
  }
  CreateThread(nullptr, 0, busyWorker, nullptr, 0, nullptr);
  CreateThread(nullptr, 0, busyPulser, nullptr, 0, nullptr);
  if (!strcmp(mode, "extshare")) {
    char name[64]; sprintf_s(name, "Local\\rewind-toy-ext-%lu", GetCurrentProcessId());
    HANDLE m = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 65536, name);
    unsigned* v = (unsigned*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    if (v) v[0] = 1; else s.bad |= B_SETUP;
  }
  CreateThread(nullptr, 0, waiter, nullptr, 0, nullptr);
  CreateThread(nullptr, 0, sleeper, nullptr, 0, nullptr);
  CreateThread(nullptr, 0, ui, nullptr, 0, nullptr);
  CreateThread(nullptr, 0, commands, nullptr, 0, nullptr);
  sprintf_s(pipeName, "\\\\.\\pipe\\rewind-toy-%lu", GetCurrentProcessId());
  HANDLE pipe = CreateNamedPipeA(pipeName, PIPE_ACCESS_INBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 64, 64, 0, nullptr);
  if (pipe == INVALID_HANDLE_VALUE) s.bad |= B_SETUP;
  else CreateThread(nullptr, 0, pipeServer, pipe, 0, nullptr);
  run(C_TP);  // the pool and its first worker exist before any snapshot
  for (int i = 0; i < 2000 && !s.hwnd; ++i) Sleep(1);
  if (!s.hwnd) s.bad |= B_SETUP;
}

int main(int argc, char** argv) {
  setup(argc > 1 ? argv[1] : "");
  printf("state %p\n", (void*)&s);
  fflush(stdout);
  s.lastV = qpc() - coop->skewQpc;
  s.rng = 12345;
  if (avx) avxLoop();
  for (DWORD end = GetTickCount() + 120000; !s.quit && GetTickCount() < end;) {
    s.tick = s.tick + 1; coop->tick = s.tick;
    s.a = s.a + 1;
    s.b = s.a * K;
    if (s.b != s.a * K) s.bad |= B_INV;
    s.rng = s.rng * 1664525u + 1013904223u;
    if (s.tick % 4096 == 0) {
      int i = (int)(s.allocs % 8);
      unsigned* old = (unsigned*)(UINT_PTR)s.blk[i];
      if (old) {
        if (old[0] != s.sig[i] || old[16383] != ~s.sig[i]) s.bad |= B_BLK;  // block content survived
        VirtualFree(old, 0, MEM_RELEASE);
      }
      unsigned* blk = (unsigned*)VirtualAlloc(nullptr, 65536, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
      s.blk[i] = (uint32_t)(UINT_PTR)blk;
      if (!blk) { s.bad |= B_ALLOC; continue; }
      s.sig[i] = s.a; sign(blk, 16384, s.a);
      s.allocs = s.allocs + 1;
    }
    if ((s.tick & 255) == 0) timeCheck();
    if (!nopark && (coop->request || (coop->parkAtTick && s.tick >= coop->parkAtTick))) {
      bridgeOp();
      coop->parked = 1;
      while (coop->request || (coop->parkAtTick && s.tick >= coop->parkAtTick)) { bridgeOp(); Sleep(1); timeCheck(); }
      coop->parked = 0;
    }
  }
  return s.bad ? 10 : 0;
}
