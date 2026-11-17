// rewind-spike.exe (x64, test-only, docs/270; design: Dalexlol's 2026-11-14 rewind notes): snapshot a running 32-bit test
// process and put it back to that state in place, so many test games can start from one world load. Never for players.
// Only targets rewind-toy.exe, or TS3W.exe with --allow-game. A rewind is either checked equal to the snapshot or refused
// before anything is changed (exit 2, the target keeps running: reload the world instead). A rewind that fails after the
// first change ends the target (exit 3): a half-rewound process never runs on.
//   rewind-spike selftest <rewind-toy.exe>            every guard on the toy: its success path and its refuse path
//   rewind-spike cycle <pid> <runMs> <n> [options]    snapshot now, then n times: run runMs, rewind
//   rewind-spike serve <pid> [options]                commands on stdin: snapshot | rewind | plan | ticks <n> | park | quit
//     park: ts3native's route worker into its idle wait while the game still runs (before the caller pauses it); held until
//     the next op ends (docs/270 §21)
// Options: --allow-game  --plan-only (no memory copy, rewinds only list what they would do and why they would refuse)
//   --no-coop (do not require the cooperation block; the caller has paused the target)  --quiesce-ms N  --no-time-fix
//   --reap-workers  --recreate-views  --no-view-probe  --allow-shared-views  --allow-audio  --allow-gui  --allow-module <name>
//   --allow-gpu (a Vulkan loader or driver module in the target is otherwise refused: the GPU device is then in this process)
//   --catch-ms N (time a rewind may spend catching busy threads in their snapshot wait, default 2000; 0 = off; docs/270 §18)
// Under the RTX Remix bridge (docs/270 §12) the bridge client publishes the cooperation block: the tool sends it the ops
// snapshot / plan / rewind for the bridge server and lets the bridge's own shared views pass the shared-view guard.
// Each operation prints one line: result {"op":..,"code":0|2|3,"why":[..],"ms":{..},"stats":{..}}.
#define NOMINMAX
#include <windows.h>
#include <winternl.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include <algorithm>
#include <map>
#include <set>
#include <string>
#include <vector>
#include "rewind-toy.h"

// ---------------------------------------------------------------- small helpers
template <class F> static F nt(const char* n) { return (F)(void*)GetProcAddress(GetModuleHandleA("ntdll.dll"), n); }
typedef LONG (NTAPI* NtQuery5)(HANDLE, ULONG, PVOID, ULONG, PULONG);
static const NtQuery5 NtQueryInformationProcess_ = nt<NtQuery5>("NtQueryInformationProcess");
static const NtQuery5 NtQueryInformationThread_ = nt<NtQuery5>("NtQueryInformationThread");
static const NtQuery5 NtQueryObject_ = nt<NtQuery5>("NtQueryObject");
static const NtQuery5 NtQuerySection_ = nt<NtQuery5>("NtQuerySection");
static const NtQuery5 NtQueryEvent_ = nt<NtQuery5>("NtQueryEvent");
static const NtQuery5 NtQuerySemaphore_ = nt<NtQuery5>("NtQuerySemaphore");
static const NtQuery5 NtQueryMutant_ = nt<NtQuery5>("NtQueryMutant");
static const auto NtMapViewOfSection_ = nt<LONG (NTAPI*)(HANDLE, HANDLE, PVOID*, ULONG_PTR, SIZE_T, PLARGE_INTEGER, PSIZE_T, DWORD, ULONG, ULONG)>("NtMapViewOfSection");
static const auto NtGetNextThread_ = nt<LONG (NTAPI*)(HANDLE, HANDLE, ACCESS_MASK, ULONG, ULONG, PHANDLE)>("NtGetNextThread");
static const auto NtUnmapViewOfSection_ = nt<LONG (NTAPI*)(HANDLE, PVOID)>("NtUnmapViewOfSection");
static const auto NtCreateThreadEx_ = nt<LONG (NTAPI*)(PHANDLE, ACCESS_MASK, PVOID, HANDLE, PVOID, PVOID, ULONG, SIZE_T, SIZE_T, SIZE_T, PVOID)>("NtCreateThreadEx");
static const auto NtGetWriteWatch_ =nt<LONG (NTAPI*)(HANDLE, ULONG, PVOID, SIZE_T, PVOID*, PULONG_PTR, PULONG)>("NtGetWriteWatch");
static const auto CompareObjectHandles_ = (BOOL (WINAPI*)(HANDLE, HANDLE))(void*)GetProcAddress(GetModuleHandleA("kernelbase.dll"), "CompareObjectHandles");

static LONGLONG qpc() { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart; }
static LONGLONG qpf() { LARGE_INTEGER t; QueryPerformanceFrequency(&t); return t.QuadPart; }
template <class T> static T rd(HANDLE p, ULONG64 a) { T v{}; ReadProcessMemory(p, (void*)a, &v, sizeof v, nullptr); return v; }
// Bulk writes: NtWriteVirtualMemory alone. WriteProcessMemory also queries the protection and flushes the target's
// instruction cache on every call; per page run that made most of a game rewind's 11-18 s (docs/270 §26). The restore
// flushes once per executable region instead.
static const auto NtWriteVirtualMemory_ = nt<LONG (NTAPI*)(HANDLE, PVOID, const void*, SIZE_T, PSIZE_T)>("NtWriteVirtualMemory");
static bool wvm(HANDLE p, ULONG_PTR a, const void* src, size_t n) { SIZE_T got = 0; return NtWriteVirtualMemory_(p, (PVOID)a, src, n, &got) >= 0 && got == n; }
template <class T> static bool wr(HANDLE p, ULONG64 a, T v) { return WriteProcessMemory(p, (void*)a, &v, sizeof v, nullptr) != 0; }
static std::string lower(std::string s) { for (auto& c : s) c = (char)tolower((unsigned char)c); return s; }
static std::string fmt(const char* f, ...) {
  char b[1024]; va_list a; va_start(a, f); vsnprintf(b, sizeof b, f, a); va_end(a);
  return b;
}
static uint64_t hash64(const unsigned char* d, size_t n, uint64_t h) {  // word-wise multiply-xorshift, for reports
  for (size_t i = 0; i + 8 <= n; i += 8) { uint64_t w; memcpy(&w, d + i, 8); h = (h ^ w) * 0x9E3779B97F4A7C15ull; h ^= h >> 29; }
  return h;
}
static bool writable(DWORD p) {
  p &= 0xFF;
  return p == PAGE_READWRITE || p == PAGE_WRITECOPY || p == PAGE_EXECUTE_READWRITE || p == PAGE_EXECUTE_WRITECOPY;
}
// A written copy-on-write page reads back as plain read-write: the same thing for a comparison.
static DWORD normProt(DWORD p) {
  DWORD b = p & 0xFF;
  if (b == PAGE_WRITECOPY) b = PAGE_READWRITE;
  if (b == PAGE_EXECUTE_WRITECOPY) b = PAGE_EXECUTE_READWRITE;
  return (p & ~0xFFu) | b;
}
static DWORD rwFor(DWORD p) { return (p & 0xF0) ? PAGE_EXECUTE_READWRITE : PAGE_READWRITE; }

struct Opt {
  bool allowGame = false, coop = true, timeFix = true, reapWorkers = false, recreateViews = false, allowShared = false;
  bool allowAudio = false, allowGui = false, planOnly = false, viewProbe = true;
  DWORD quiesceMs = 5000;
  int retries = 40;
  DWORD catchMs = 2000;  // per rewind: polling single threads until each is back in its snapshot wait (docs/270 §18)
  std::vector<std::string> forbid = {"frida-agent"};                                           // modules that must not be loaded
  std::vector<std::string> gpu = {"vulkan-1", "nvoglv", "amdvlk", "igvk"};                    // Vulkan loader and drivers: the GPU device is in the target
  bool allowGpu = false;
  std::vector<std::string> audio = {"dsound", "audioses", "xaudio", "openal", "wdmaud", "mmdevapi"};  // thread start modules
  // modules allowed to import GetWriteWatch / ResetWriteWatch: their only user is a Boehm-style GC, which needs the dirty
  // bits to be a superset only (TS3W.exe: Mono's statically linked Boehm GC, docs/270 §15)
  std::vector<std::string> wwOwners = {"ts3w.exe", "rewind-toy.exe", "kernel32.dll", "kernelbase.dll", "ntdll.dll"};
  bool wwRedirty = true;  // selftest only: false shows that the toy GC catches missing dirty bits
};
struct Result {
  const char* op = "";
  int code = 0;  // 0 ok, 2 refused (nothing changed), 3 dirty (target ended)
  std::vector<std::string> why, notes;  // notes: things left as they are, said once (not refusals)
  std::vector<std::pair<std::string, double>> ms, stats;
  void refuse(const std::string& s) { if (code < 2) code = 2; if (why.size() < 40) why.push_back(s); }
  double stat(const char* k) const { for (auto& s : stats) if (s.first == k) return s.second; return 0; }
  void add(const char* k, double v) { for (auto& s : stats) if (s.first == k) { s.second += v; return; } stats.push_back({k, v}); }
  void print() const {
    printf("result {\"op\":\"%s\",\"code\":%d,\"why\":[", op, code);
    for (size_t i = 0; i < why.size(); ++i) {
      std::string e;
      for (char c : why[i]) { if (c == '"' || c == '\\') e += '\\'; e += c; }
      printf("%s\"%s\"", i ? "," : "", e.c_str());
    }
    printf("],\"notes\":[");
    for (size_t i = 0; i < notes.size(); ++i) {
      std::string e;
      for (char c : notes[i]) { if (c == '"' || c == '\\') e += '\\'; e += c; }
      printf("%s\"%s\"", i ? "," : "", e.c_str());
    }
    printf("],\"ms\":{");
    for (size_t i = 0; i < ms.size(); ++i) printf("%s\"%s\":%.1f", i ? "," : "", ms[i].first.c_str(), ms[i].second);
    printf("},\"stats\":{");
    for (size_t i = 0; i < stats.size(); ++i) printf("%s\"%s\":%.0f", i ? "," : "", stats[i].first.c_str(), stats[i].second);
    printf("}}\n");
    fflush(stdout);
  }
};
struct Lap {
  Result& r; LONGLONG t = qpc();
  void operator()(const char* k) { LONGLONG n = qpc(); r.ms.push_back({k, (n - t) * 1000.0 / qpf()}); t = n; }
};

// ---------------------------------------------------------------- watchdog: what happens if this host dies mid-way
// phase 1 = threads suspended, target unchanged: resume them. phase 2 = target half rewound: end it.
struct Guard { volatile LONG phase; volatile LONG n; DWORD tids[16384]; };
static Guard localGuard;
static Guard* guard = &localGuard;

static int watchdogMain(HANDLE host, DWORD targetPid, HANDLE map) {  // both handles are inherited from the host
  Guard* g = (Guard*)MapViewOfFile(map, FILE_MAP_READ, 0, 0, 0);
  if (!g) return 1;
  WaitForSingleObject(host, INFINITE);
  if (g->phase == 1)
    for (LONG i = 0; i < g->n; ++i) {
      HANDLE t = OpenThread(THREAD_SUSPEND_RESUME, FALSE, g->tids[i]);
      if (t) { ResumeThread(t); CloseHandle(t); }
    }
  if (g->phase == 2) {
    HANDLE t = OpenProcess(PROCESS_TERMINATE, FALSE, targetPid);
    if (t) TerminateProcess(t, 0xDEAD);
  }
  return 0;
}
static void startWatchdog(DWORD targetPid) {
  SECURITY_ATTRIBUTES sa{sizeof sa, nullptr, TRUE};
  HANDLE map = CreateFileMappingA(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, sizeof(Guard), nullptr);
  Guard* g = map ? (Guard*)MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, 0) : nullptr;
  char self[MAX_PATH]; GetModuleFileNameA(nullptr, self, MAX_PATH);
  HANDLE me = nullptr;
  DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(), &me, SYNCHRONIZE, TRUE, 0);
  std::string cmd = fmt("\"%s\" watchdog %llu %lu %llu", self, (unsigned long long)(ULONG_PTR)me, targetPid, (unsigned long long)(ULONG_PTR)map);
  STARTUPINFOA si{sizeof si}; PROCESS_INFORMATION pi{};
  if (!g || !CreateProcessA(nullptr, cmd.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
    fprintf(stderr, "warning: no watchdog (error %lu): a host crash would leave the target suspended\n", GetLastError());
    return;
  }
  CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
  guard = g;
}

// ---------------------------------------------------------------- target state
struct Reg { ULONG_PTR base, size, abase; DWORD state, prot, aprot, type; size_t off; bool copied; };
struct TS {  // one thread
  DWORD tid = 0, prevSuspend = 0, start = 0, stub = 0, nr = 0, nargs = 10;  // nargs: from the stub's ret n
  ULONG64 rsp64 = 0;      // in a system call: the 64-bit stack pointer
  int prio = 0;
  WOW64_CONTEXT ctx{};
  bool kernel = false, transit = false, avx = false, parked = false;
  std::string name;       // syscall the thread is inside, when known
  DWORD sig[17]{};        // eip, esp, ebp, ebx, esi, edi, 11 stack words (in a system call: return address + 10 arguments)
  std::vector<std::pair<DWORD, std::string>> xs;  // running 32-bit code: the extended vector state in use (XSTATE feature id, bytes)
  ULONG64 teb = 0, slots[6]{};  // 64-bit TEB and its first WOW64 TLS slots: 0 = 64-bit stack pointer, 2 = in 32-bit code, 5 = user callback chain
};
struct HRec {  // one handle of the target
  ULONG_PTR value; ULONG access, attrs, type;
  HANDLE pin = nullptr, strong = nullptr;  // our duplicates: same access, and one that can query / map / signal
  std::wstring kind;
  bool disk = false, posOk = false; LONGLONG pos = 0;
  ULONGLONG secSize = 0; ULONG secAttr = 0;
  LONG syncA = 0, syncB = 0; bool syncOk = false; DWORD owner = 0;  // event state / semaphore count / mutant count + owner
};
struct View { ULONG_PTR base, size; DWORD aprot, type; std::wstring file; int sec = -1; ULONGLONG off = 0; bool anon = false; };
struct Th { DWORD tid; HANDLE h; DWORD prev; };
struct Snap {
  bool have = false, copied = false;
  std::vector<Reg> regs; std::vector<TS> threads; std::vector<HRec> handles; std::vector<View> views;
  HANDLE file = INVALID_HANDLE_VALUE, map = nullptr; unsigned char* store = nullptr; size_t bytes = 0; uint64_t hash = 0;
  LONGLONG vAtFreeze = 0; uint32_t tick = 0; DWORD gdi = 0, user = 0;
  std::map<ULONG_PTR, ULONG_PTR> ww;  // allocations with write watch: base -> end
  std::map<ULONG_PTR, std::pair<ULONG_PTR, std::string>> foreign;  // views shared with another process: base -> end, description
  std::vector<std::pair<HANDLE, ULONG>> poolMin;  // worker factories held at their thread count: our handle, the minimum to put back (docs/270 §25)
  std::map<ULONG_PTR, std::string> sysViews;  // the window system's read-only views (win32k maps them into every GUI process): base -> what
  std::set<ULONG_PTR> audioViews;  // foreign views of the audio engine: other holder audiodg.exe, or one the tool cannot open with a sound module's object keeping the view (docs/270 §23)
};
struct Sess {
  HANDLE p = nullptr; DWORD pid = 0; Opt o; Snap s; ULONG64 coop = 0; std::string name;
  bool bridge = false;                           // the cooperation block comes from a renderer bridge client (docs/270 §12)
  std::map<DWORD, std::string> stubs;            // 32-bit syscall stub address -> export name
  std::map<ULONG, std::wstring> kinds;           // object type index -> name
  std::vector<Th> frozen;
  std::map<DWORD, HANDLE> hold;                  // caught threads kept suspended until the next freeze takes them over
  std::set<DWORD> poolStarts;                    // thread-pool worker starts (TppWorkerThread) seen idle
  RewindWorkerPark* tn = nullptr;                // ts3native [Rewind] Park block, mapped here (docs/270 §19)
};

static std::vector<Reg> regions(HANDLE p) {
  std::vector<Reg> out;
  MEMORY_BASIC_INFORMATION m;
  for (ULONG_PTR a = 0; a < 0x100000000ull && VirtualQueryEx(p, (void*)a, &m, sizeof m) == sizeof m; a = (ULONG_PTR)m.BaseAddress + m.RegionSize)
    if (m.State != MEM_FREE) out.push_back({(ULONG_PTR)m.BaseAddress, m.RegionSize, (ULONG_PTR)m.AllocationBase, m.State, m.Protect, m.AllocationProtect, m.Type, 0, false});
  return out;
}
// Regions merged where nothing but the split differs, so two maps compare equal when the address space is the same.
static std::vector<Reg> flat(const std::vector<Reg>& v) {
  std::vector<Reg> out;
  for (Reg r : v) {
    r.prot = r.state == MEM_COMMIT ? normProt(r.prot) : 0; r.aprot = normProt(r.aprot); r.off = 0; r.copied = false;
    if (!out.empty() && out.back().base + out.back().size == r.base && out.back().state == r.state && out.back().prot == r.prot &&
        out.back().type == r.type && out.back().abase == r.abase && out.back().aprot == r.aprot) out.back().size += r.size;
    else out.push_back(r);
  }
  return out;
}
static bool sameMap(const std::vector<Reg>& a, const std::vector<Reg>& b, std::string& why) {
  auto x = flat(a), y = flat(b);
  for (size_t i = 0; i < x.size() || i < y.size(); ++i) {
    if (i < x.size() && i < y.size() && x[i].base == y[i].base && x[i].size == y[i].size && x[i].abase == y[i].abase && x[i].state == y[i].state && x[i].prot == y[i].prot && x[i].aprot == y[i].aprot && x[i].type == y[i].type) continue;
    Reg none{};
    const Reg& s = i < x.size() ? x[i] : none; const Reg& n = i < y.size() ? y[i] : none;
    why = fmt("region map differs: snapshot %p +%zx state %lx prot %lx type %lx alloc %p/%lx, now %p +%zx state %lx prot %lx type %lx alloc %p/%lx", (void*)s.base, (size_t)s.size,
              s.state, s.prot, s.type, (void*)s.abase, s.aprot, (void*)n.base, (size_t)n.size, n.state, n.prot, n.type, (void*)n.abase, n.aprot);
    return false;
  }
  return true;
}
// Whether the snapshot keeps the content of a region: everything committed, except the kernel's shared page and views
// mapped without write permission (not this process's to change).
// The view shared with another process that holds this address, or null.
static const std::pair<ULONG_PTR, std::string>* foreignAt(const Snap& s, ULONG_PTR a) {
  auto it = s.foreign.upper_bound(a);
  if (it == s.foreign.begin()) return nullptr;
  --it;
  return a < it->second.first ? &it->second : nullptr;
}
static bool copies(const Reg& r) { return r.state == MEM_COMMIT && r.base != 0x7FFE0000 && (r.type != MEM_MAPPED || writable(r.aprot)); }

static bool readMem(HANDLE p, ULONG_PTR base, size_t size, DWORD prot, unsigned char* dst) {
  bool dance = (prot & PAGE_GUARD) || (prot & 0xFF) == PAGE_NOACCESS;  // read it without tripping it, then put the protection back
  DWORD old;
  if (dance && !VirtualProtectEx(p, (void*)base, size, PAGE_READONLY, &old)) return false;
  SIZE_T got = 0;
  bool ok = ReadProcessMemory(p, (void*)base, dst, size, &got) && got == size;
  if (dance && !VirtualProtectEx(p, (void*)base, size, prot, &old)) return false;
  return ok;
}

// Live threads of the target, as handles (NtGetNextThread walks the process's own list: no system-wide snapshot).
static std::vector<HANDLE> threadHandles(HANDLE p) {
  std::vector<HANDLE> out;
  HANDLE prev = nullptr, next = nullptr;
  while (NtGetNextThread_(p, prev, THREAD_ALL_ACCESS, 0, 0, &next) >= 0) { out.push_back(next); prev = next; }
  return out;
}
static void releaseHolds(Sess& S) {
  for (auto& [tid, h] : S.hold) { ResumeThread(h); CloseHandle(h); }
  S.hold.clear();
}
static bool freeze(Sess& S) {
  guard->n = 0;
  for (auto& [tid, h] : S.hold) guard->tids[guard->n++] = tid;  // caught threads stay covered by the watchdog
  guard->phase = 1;
  std::set<DWORD> have;
  bool ok = false;
  for (int round = 0; round < 50; ++round) {  // again until no thread was missed: one may start while the others stop
    bool added = false;
    for (HANDLE h : threadHandles(S.p)) {
      DWORD tid = GetThreadId(h);
      DWORD prev = have.count(tid) ? (DWORD)-1 : Wow64SuspendThread(h);
      if (prev == (DWORD)-1) { CloseHandle(h); continue; }  // already held, or exiting
      auto hd = S.hold.find(tid);
      if (hd != S.hold.end()) { ResumeThread(hd->second); CloseHandle(hd->second); S.hold.erase(hd); --prev; }  // the catch's suspension becomes this one
      else if (guard->n < 16384) { guard->tids[guard->n] = tid; guard->n = guard->n + 1; }
      have.insert(tid); S.frozen.push_back({tid, h, prev}); added = true;
    }
    if (!added) { ok = !S.frozen.empty(); break; }
  }
  releaseHolds(S);  // a caught thread that ended meanwhile
  return ok;
}
static void thaw(Sess& S) {
  for (auto& t : S.frozen) { ResumeThread(t.h); CloseHandle(t.h); }
  S.frozen.clear(); guard->n = 0; guard->phase = 0;
}
// A rewind failed after it began to change the target: end the target, so nothing runs on in a mixed state.
static void dirty(Sess& S, Result& r, const std::string& why) {
  r.code = 3; r.why.push_back(why + " (target ended)");
  TerminateProcess(S.p, 0xDEAD);
  for (auto& t : S.frozen) CloseHandle(t.h);
  S.frozen.clear(); guard->phase = 0; S.s.have = false;
}

// Guard: test processes only. Returns the image base name or "" when refused.
static std::string checkTarget(HANDLE p, bool allowGame) {
  char path[MAX_PATH]; DWORD n = MAX_PATH;
  if (!QueryFullProcessImageNameA(p, 0, path, &n)) return "";
  const char* base = strrchr(path, '\\'); base = base ? base + 1 : path;
  BOOL wow = FALSE; IsWow64Process(p, &wow);
  if (!wow) { fprintf(stderr, "refused: %s is not a 32-bit process\n", base); return ""; }
  if (_stricmp(base, "rewind-toy.exe") == 0) return base;
  if (_stricmp(base, "TS3W.exe") == 0 && allowGame) return base;
  fprintf(stderr, "refused: %s is not a rewind test target (rewind-toy.exe, or TS3W.exe with --allow-game)\n", base);
  return "";
}

static std::wstring mappedFile(HANDLE p, ULONG_PTR a) {
  wchar_t b[1024];
  DWORD n = K32GetMappedFileNameW(p, (void*)a, b, 1024);
  return std::wstring(b, n);
}
static bool hasAny(const std::string& s, const std::vector<std::string>& subs) { for (auto& k : subs) if (s.find(k) != std::string::npos) return true; return false; }
static std::string baseName(const std::wstring& w) {
  size_t i = w.find_last_of(L'\\');
  std::string s;
  for (wchar_t c : w.substr(i == std::wstring::npos ? 0 : i + 1)) s += (char)c;
  return lower(s);
}
static std::vector<View> viewsOf(HANDLE p, const std::vector<Reg>& regs) {
  std::vector<View> out;
  for (auto& r : regs) {
    if (r.type != MEM_MAPPED && r.type != MEM_IMAGE) continue;
    if (!out.empty() && out.back().base == r.abase) { out.back().size = r.base + r.size - r.abase; continue; }
    View v{r.abase, r.base + r.size - r.abase, r.aprot, r.type};
    v.file = mappedFile(p, r.abase);
    out.push_back(v);
  }
  return out;
}
static const View* viewAt(const std::vector<View>& vs, ULONG_PTR a) {
  for (auto& v : vs) if (a >= v.base && a < v.base + v.size) return &v;
  return nullptr;
}

// Export names of the 32-bit ntdll and win32u: which system call a thread sits in.
static void loadStubs(Sess& S, const std::vector<View>& views) {
  for (auto& v : views) {
    std::string b = baseName(v.file);
    if (v.type != MEM_IMAGE || (b != "ntdll.dll" && b != "win32u.dll")) continue;
    IMAGE_DOS_HEADER dos = rd<IMAGE_DOS_HEADER>(S.p, v.base);
    IMAGE_NT_HEADERS32 nth = rd<IMAGE_NT_HEADERS32>(S.p, v.base + dos.e_lfanew);
    if (dos.e_magic != IMAGE_DOS_SIGNATURE || nth.FileHeader.Machine != IMAGE_FILE_MACHINE_I386) continue;
    IMAGE_EXPORT_DIRECTORY ed = rd<IMAGE_EXPORT_DIRECTORY>(S.p, v.base + nth.OptionalHeader.DataDirectory[0].VirtualAddress);
    if (!ed.NumberOfNames || ed.NumberOfNames > 20000) continue;
    std::vector<DWORD> names(ed.NumberOfNames), funcs(ed.NumberOfFunctions);
    std::vector<WORD> ords(ed.NumberOfNames);
    ReadProcessMemory(S.p, (void*)(v.base + ed.AddressOfNames), names.data(), names.size() * 4, nullptr);
    ReadProcessMemory(S.p, (void*)(v.base + ed.AddressOfNameOrdinals), ords.data(), ords.size() * 2, nullptr);
    ReadProcessMemory(S.p, (void*)(v.base + ed.AddressOfFunctions), funcs.data(), funcs.size() * 4, nullptr);
    for (DWORD i = 0; i < ed.NumberOfNames; ++i) {
      char nm[64]{};
      ReadProcessMemory(S.p, (void*)(v.base + names[i]), nm, 63, nullptr);
      if (!strncmp(nm, "Nt", 2) && ords[i] < funcs.size()) S.stubs[(DWORD)(v.base + funcs[ords[i]])] = nm;
    }
  }
}
// System calls that only wait: calling them again with the same arguments is the same as still being in them.
static bool restartable(const std::string& n) {
  static const char* ok[] = {"NtWaitForSingleObject", "NtWaitForMultipleObjects", "NtWaitForMultipleObjects32", "NtDelayExecution",
                             "NtRemoveIoCompletion", "NtRemoveIoCompletionEx", "NtWaitForWorkViaWorkerFactory", "NtWaitForAlertByThreadId",
                             "NtWaitForKeyedEvent", "NtUserMsgWaitForMultipleObjectsEx", "NtUserGetMessage", "NtUserWaitMessage"};
  for (auto* k : ok) if (n == k) return true;
  return false;
}

// A system call that waits for another process and keeps no state of its own (docs/270 §14): a named-pipe server in
// ConnectNamedPipe (FSCTL_PIPE_LISTEN) or a client in WaitNamedPipe (FSCTL_PIPE_WAIT) on a synchronous handle. It cannot
// be restarted from outside (a listen issued again would not be the same call), so a rewind leaves it alone: allowed only
// when the thread still sits in the same call with the same context and the same handle.
static bool parkedWait(const TS& t) {
  return t.kernel && t.name == "NtFsControlFile" && (t.sig[12] == 0x110008 || t.sig[12] == 0x110018);
}
static bool sameParked(const TS& o, const TS& n) {
  const WOW64_CONTEXT &a = o.ctx, &b = n.ctx;
  return n.kernel && n.name == o.name && n.nr == o.nr && !memcmp(o.sig, n.sig, sizeof o.sig) && a.Eax == b.Eax && a.Ecx == b.Ecx &&
         a.Edx == b.Edx && a.EFlags == b.EFlags && a.SegCs == b.SegCs && a.SegSs == b.SegSs && a.SegFs == b.SegFs;
}

static bool classify(Sess& S, const Th& t, TS& o) {
  o.tid = t.tid; o.prevSuspend = t.prev; o.prio = GetThreadPriority(t.h);
  o.ctx.ContextFlags = WOW64_CONTEXT_ALL;
  if (!Wow64GetThreadContext(t.h, &o.ctx)) return false;
  DWORD64 feat = GetEnabledXStateFeatures() & (XSTATE_MASK_AVX | XSTATE_MASK_AVX512);
  DWORD flags = CONTEXT_CONTROL | CONTEXT_INTEGER | (feat ? CONTEXT_XSTATE : 0), len = 0;
  InitializeContext(nullptr, flags, nullptr, &len);
  std::vector<unsigned char> buf(len);
  PCONTEXT c = nullptr;
  if (!InitializeContext(buf.data(), flags, &c, &len)) return false;
  if (feat) SetXStateFeaturesMask(c, feat);
  if (!GetThreadContext(t.h, c)) return false;
  DWORD64 used = 0;
  if (feat) GetXStateFeaturesMask(c, &used);
  o.avx = (used & feat) != 0;  // upper vector halves in use: a WOW64 context cannot carry them, the native XSTATE does
  if (o.avx && c->SegCs == 0x23)
    for (DWORD id = 2; id < 8; ++id) {
      DWORD n = 0; void* at = (used >> id & 1) ? LocateXStateFeature(c, id, &n) : nullptr;
      if (at && n) o.xs.push_back({id, std::string((char*)at, n)});
    }
  if (c->SegCs != 0x23) {      // in 64-bit code: inside a system call (just behind a syscall instruction) or passing through
    unsigned short ins = rd<unsigned short>(S.p, c->Rip - 2);
    o.kernel = ins == 0x050F; o.transit = !o.kernel; o.rsp64 = c->Rsp;
  }
  if (o.kernel) {  // the 32-bit stub: mov eax, n / mov edx, gate / call edx, with eip just behind the call
    unsigned char st[12]{};
    ReadProcessMemory(S.p, (void*)(ULONG_PTR)(o.ctx.Eip - 12), st, 12, nullptr);
    if (st[0] == 0xB8 && st[5] == 0xBA && st[10] == 0xFF && st[11] == 0xD2) {
      o.stub = o.ctx.Eip - 12; memcpy(&o.nr, st + 1, 4);
      unsigned char ret[3]{};
      ReadProcessMemory(S.p, (void*)(ULONG_PTR)o.ctx.Eip, ret, 3, nullptr);
      if (ret[0] == 0xC3) o.nargs = 0;
      else if (ret[0] == 0xC2) o.nargs = std::min(10, (ret[1] | ret[2] << 8) / 4);
      auto it = S.stubs.find(o.stub);
      o.name = it == S.stubs.end() ? fmt("syscall@%08lx", o.stub) : it->second;
    } else o.name = fmt("eip@%08lx", o.ctx.Eip);
  }
  DWORD sig[17] = {o.ctx.Eip, o.ctx.Esp, o.ctx.Ebp, o.ctx.Ebx, o.ctx.Esi, o.ctx.Edi};
  ReadProcessMemory(S.p, (void*)(ULONG_PTR)o.ctx.Esp, sig + 6, 44, nullptr);
  memcpy(o.sig, sig, sizeof sig);
  struct { LONG status; PVOID teb; ULONG_PTR pid, tid, affinity; LONG prio, basePrio; } tbi{};
  NtQueryInformationThread_(t.h, 0, &tbi, sizeof tbi, nullptr);
  o.teb = (ULONG64)tbi.teb;
  ReadProcessMemory(S.p, (void*)(o.teb + 0x1480), o.slots, sizeof o.slots, nullptr);
  ULONG64 start = 0;
  NtQueryInformationThread_(t.h, 9, &start, sizeof start, nullptr);
  o.start = (DWORD)start;
  o.parked = parkedWait(o);
  return true;
}
static std::string describe(const TS& t) { return t.transit ? "between 32-bit and 64-bit code" : t.kernel ? "in " + t.name : "running"; }

// ---------------------------------------------------------------- handles
struct HEnt { ULONG_PTR value; ULONG access, type, attrs; };
static std::vector<HEnt> handleList(HANDLE p) {
  std::vector<unsigned char> buf(1 << 16);
  ULONG need = 0;
  LONG st;
  while ((st = NtQueryInformationProcess_(p, 51, buf.data(), (ULONG)buf.size(), &need)) == (LONG)0xC0000004) buf.resize(need + 4096);
  std::vector<HEnt> out;
  if (st < 0) return out;
  struct E { ULONG_PTR value, handles, pointers; ULONG access, type, attrs, reserved; };
  ULONG_PTR n = *(ULONG_PTR*)buf.data();
  const E* e = (const E*)(buf.data() + 2 * sizeof(ULONG_PTR));
  for (ULONG_PTR i = 0; i < n; ++i) out.push_back({e[i].value, e[i].access, e[i].type, e[i].attrs});
  return out;
}
// Section handles of every other process (one pass per snapshot, duplicated here), so each shared-looking view can be matched to
// the processes that hold it. The target is included: a section only the target holds is its own memory.
struct SecHolder { DWORD pid; std::string exe; HANDLE h; };
static std::vector<SecHolder> sectionHolders(ULONG sectionType) {
  std::vector<SecHolder> out;
  // this process first, before any duplicate exists: its own handles (pins) are counted like everyone else's
  for (auto& e : handleList(GetCurrentProcess())) {
    HANDLE t = nullptr;
    if ((!sectionType || e.type == sectionType) && DuplicateHandle(GetCurrentProcess(), (HANDLE)e.value, GetCurrentProcess(), &t, 0, FALSE, DUPLICATE_SAME_ACCESS))
      out.push_back({GetCurrentProcessId(), "(tool)", t});
  }
  HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  PROCESSENTRY32W pe{sizeof pe};
  for (BOOL more = snap != INVALID_HANDLE_VALUE && Process32FirstW(snap, &pe); more; more = Process32NextW(snap, &pe)) {
    if (pe.th32ProcessID == GetCurrentProcessId()) continue;
    HANDLE p = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, FALSE, pe.th32ProcessID);
    if (!p) continue;
    std::string exe;
    for (wchar_t c : std::wstring(pe.szExeFile)) exe += (char)c;
    for (auto& e : handleList(p)) {
      HANDLE t = nullptr;
      if (sectionType && e.type != sectionType) continue;
      if (DuplicateHandle(p, (HANDLE)e.value, GetCurrentProcess(), &t, 0, FALSE, DUPLICATE_SAME_ACCESS)) out.push_back({pe.th32ProcessID, exe, t});
    }
    CloseHandle(p);
  }
  if (snap != INVALID_HANDLE_VALUE) CloseHandle(snap);
  return out;
}
// Processes holding the same object as `pin` in the system handle table (SystemExtendedHandleInformation, 64): this
// names a holder the tool cannot open (a protected process such as audiodg) without opening it. Empty when the table
// hides object addresses (no SeDebugPrivilege on newer Windows).
static std::map<DWORD, int> objectHolders(HANDLE pin) {
  std::map<DWORD, int> out;
  static const auto NtQSI = nt<LONG (NTAPI*)(ULONG, PVOID, ULONG, PULONG)>("NtQuerySystemInformation");
  std::vector<unsigned char> buf(1 << 20);
  ULONG need = 0;
  LONG st;
  while ((st = NtQSI(64, buf.data(), (ULONG)buf.size(), &need)) == (LONG)0xC0000004) buf.resize(std::max<size_t>(need, buf.size()) + (1 << 20));
  if (st < 0) return out;
  struct E { PVOID obj; ULONG_PTR pid, value; ULONG access; USHORT trace, type; ULONG attrs, reserved; };
  ULONG_PTR n = *(ULONG_PTR*)buf.data();
  const E* e = (const E*)(buf.data() + 2 * sizeof(ULONG_PTR));
  PVOID obj = nullptr;
  for (ULONG_PTR i = 0; i < n && !obj; ++i)
    if (e[i].pid == GetCurrentProcessId() && e[i].value == (ULONG_PTR)pin) obj = e[i].obj;
  if (!obj) return out;
  for (ULONG_PTR i = 0; i < n; ++i)
    if (e[i].obj == obj) out[(DWORD)e[i].pid]++;
  return out;
}
static std::string exeOf(DWORD pid) {
  HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  PROCESSENTRY32W pe{sizeof pe};
  std::string exe = "?";
  for (BOOL more = snap != INVALID_HANDLE_VALUE && Process32FirstW(snap, &pe); more; more = Process32NextW(snap, &pe))
    if (pe.th32ProcessID == pid) { exe.clear(); for (wchar_t c : std::wstring(pe.szExeFile)) exe += (char)c; break; }
  if (snap != INVALID_HANDLE_VALUE) CloseHandle(snap);
  return exe;
}
// Holders of one section: "exe xN" per process, and whether any process but the target (and this tool) holds it. Every
// handle seen also has its duplicate here, so a section with more than twice the handles seen is also held where the tool
// cannot look (a protected process such as audiodg): foreign too, named through the system handle table when it shows
// object addresses. `audio`: the only other holder is audiodg.exe.
static std::string sectionOwners(const std::vector<SecHolder>& all, HANDLE pin, DWORD target, bool& foreign, bool* audio = nullptr) {
  std::map<std::string, int> n;
  std::set<DWORD> opened;
  foreign = false;
  ULONG seen = 0;
  bool others = false;
  for (auto& x : all)
    if (CompareObjectHandles_ && CompareObjectHandles_(x.h, pin)) {
      ++seen;
      opened.insert(x.pid);
      if (x.pid == GetCurrentProcessId()) continue;
      n[x.pid == target ? x.exe + " (the target)" : fmt("%s pid %lu", x.exe.c_str(), x.pid)]++;
      foreign |= x.pid != target;
      others |= x.pid != target;
    }
  struct { ULONG attr, access, handles, pointers; ULONG rest[10]; } obi{};
  NtQueryObject_(pin, 0, &obi, sizeof obi, nullptr);
  bool onlyAudiodg = false;
  if (obi.handles > 2 * seen) {
    foreign = true;
    int named = 0, audiodg = 0;
    for (auto& [pid, c] : objectHolders(pin)) {
      if (opened.count(pid)) continue;
      std::string exe = exeOf(pid);
      n[fmt("%s pid %lu (the tool cannot open it)", exe.c_str(), pid)] += c;
      named += c;
      audiodg += _stricmp(exe.c_str(), "audiodg.exe") == 0 ? c : 0;
    }
    onlyAudiodg = named && audiodg == named && !others;
    if (!named) n[fmt("processes the tool cannot open (%lu handles)", obi.handles - 2 * seen)] = 1;
  }
  if (audio) *audio = onlyAudiodg;
  std::string out;
  for (auto& [k, c] : n) out += fmt("%s%s x%d", out.empty() ? "" : ", ", k.c_str(), c);
  return out.empty() ? "no other process found" : out;
}
// Where the target keeps a pointer to a view: the first image module data word equal to its base (who made it, roughly).
static std::string pointedFrom(HANDLE p, const std::vector<View>& views, ULONG_PTR base) {
  std::vector<DWORD> buf;
  for (auto& r : regions(p)) {
    if (r.type != MEM_IMAGE || r.state != MEM_COMMIT || !writable(r.prot) || (r.prot & PAGE_GUARD)) continue;
    buf.resize(r.size / 4);
    SIZE_T got = 0;
    if (!ReadProcessMemory(p, (void*)r.base, buf.data(), r.size, &got)) continue;
    for (size_t k = 0; k < got / 4; ++k)
      if (buf[k] == (DWORD)base) {
        const View* v = viewAt(views, r.base + 4 * k);
        return v ? fmt("%s+%lx", baseName(v->file).c_str(), (unsigned long)(r.base + 4 * k - v->base)) : fmt("%08lx", (unsigned long)(r.base + 4 * k));
      }
  }
  return "no module";
}
// Which module's object keeps the view: heap words equal to its base, each with the nearest vtable before it (a word that
// points into an image module whose first entry points into the same module). Works without opening the other holder.
static std::set<std::string> heapOwners(HANDLE p, const std::vector<View>& views, ULONG_PTR base) {
  std::set<std::string> out;
  std::vector<DWORD> buf;
  for (auto& r : regions(p)) {
    if (r.type != MEM_PRIVATE || r.state != MEM_COMMIT || !writable(r.prot) || (r.prot & PAGE_GUARD) || out.size() >= 4) continue;
    buf.resize(r.size / 4);
    SIZE_T got = 0;
    if (!ReadProcessMemory(p, (void*)r.base, buf.data(), r.size, &got)) continue;
    for (size_t k = 0; k < got / 4 && out.size() < 4; ++k) {
      if (buf[k] != (DWORD)base) continue;
      for (size_t j = k; j-- > 0 && k - j <= 64;) {
        const View* m = viewAt(views, buf[j]);
        DWORD first = 0;
        if (!m || m->type != MEM_IMAGE || !ReadProcessMemory(p, (void*)(ULONG_PTR)buf[j], &first, 4, nullptr) || viewAt(views, first) != m) continue;
        out.insert(baseName(m->file));
        break;
      }
    }
  }
  return out;
}
static bool closeIn(HANDLE p, ULONG_PTR value) { return DuplicateHandle(p, (HANDLE)value, nullptr, nullptr, 0, FALSE, DUPLICATE_CLOSE_SOURCE) != 0; }
static bool sameObject(HANDLE p, ULONG_PTR value, HANDLE pin) {
  HANDLE t = nullptr;
  if (!CompareObjectHandles_ || !DuplicateHandle(p, (HANDLE)value, GetCurrentProcess(), &t, 0, FALSE, DUPLICATE_SAME_ACCESS)) return false;
  bool same = CompareObjectHandles_(t, pin) != 0;
  CloseHandle(t);
  return same;
}
// File type and position through a helper thread with a deadline: a query on a synchronous handle that another (now
// suspended) thread is reading from would wait forever.
struct FileJob { HANDLE go, done, h; int op; LONGLONG pos; bool ok; wchar_t name[260]; };
static DWORD WINAPI fileWorker(void* a) {
  FileJob* j = (FileJob*)a;
  for (;;) {
    WaitForSingleObject(j->go, INFINITE);
    if (j->op == 2) {  // object name (a pipe: \Device\NamedPipe\<name>)
      unsigned char b[1200]{}; UNICODE_STRING* u = (UNICODE_STRING*)b;
      j->ok = NtQueryObject_(j->h, 1, b, sizeof b, nullptr) >= 0;
      size_t n = j->ok && u->Buffer ? std::min<size_t>(u->Length / 2, 259) : 0;
      if (n) memcpy(j->name, u->Buffer, n * 2);
      j->name[n] = 0;
      SetEvent(j->done);
      continue;
    }
    LARGE_INTEGER to, at{}; to.QuadPart = j->op ? j->pos : 0;
    j->ok = (j->op || GetFileType(j->h) == FILE_TYPE_DISK) && SetFilePointerEx(j->h, to, &at, j->op ? FILE_BEGIN : FILE_CURRENT);
    j->pos = at.QuadPart;
    SetEvent(j->done);
  }
}
static bool fileOp(HANDLE h, int op, LONGLONG& pos, std::wstring* name = nullptr) {
  static FileJob* j; static HANDLE thread;
  if (!j) { j = new FileJob{CreateEventA(nullptr, FALSE, FALSE, nullptr), CreateEventA(nullptr, FALSE, FALSE, nullptr)}; thread = CreateThread(nullptr, 0, fileWorker, j, 0, nullptr); }
  j->h = h; j->op = op; j->pos = pos; j->ok = false;
  SetEvent(j->go);
  if (WaitForSingleObject(j->done, 300) != WAIT_OBJECT_0) {
    CancelSynchronousIo(thread);
    if (WaitForSingleObject(j->done, 2000) != WAIT_OBJECT_0) j = nullptr;  // stuck for good: leave that worker behind
    return false;
  }
  pos = j->pos;
  if (name) *name = j->name;
  return j->ok;
}
static void syncState(HRec& h) {
  HANDLE q = h.strong ? h.strong : h.pin;
  LONG v[2]{};
  h.syncOk = false; h.owner = 0;
  if (h.kind == L"Event") { h.syncOk = NtQueryEvent_(q, 0, v, 8, nullptr) >= 0; h.syncA = v[1]; }
  else if (h.kind == L"Semaphore") { h.syncOk = NtQuerySemaphore_(q, 0, v, 8, nullptr) >= 0; h.syncA = v[0]; }
  else if (h.kind == L"Mutant") {
    ULONG_PTR cid[2]{};
    h.syncOk = NtQueryMutant_(q, 0, v, 8, nullptr) >= 0 && NtQueryMutant_(q, 1, cid, sizeof cid, nullptr) >= 0;
    h.syncA = v[0]; h.owner = (DWORD)cid[1];
  }
}
static void pinHandles(Sess& S, Snap& s, Result& r) {
  for (auto& e : handleList(S.p)) {
    HRec h{e.value, e.access, e.attrs, e.type};
    if (DuplicateHandle(S.p, (HANDLE)e.value, GetCurrentProcess(), &h.pin, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
      if (!S.kinds.count(e.type)) {
        unsigned char b[1024]{};
        if (NtQueryObject_(h.pin, 2, b, sizeof b, nullptr) >= 0) { UNICODE_STRING* u = (UNICODE_STRING*)b; S.kinds[e.type] = std::wstring(u->Buffer, u->Length / 2); }
      }
      h.kind = S.kinds[e.type];
      bool sync = h.kind == L"Event" || h.kind == L"Semaphore" || h.kind == L"Mutant";
      if (sync) DuplicateHandle(S.p, (HANDLE)e.value, GetCurrentProcess(), &h.strong, e.access | 3 | SYNCHRONIZE, FALSE, 0);
      if (h.kind == L"Section") {
        if (!DuplicateHandle(S.p, (HANDLE)e.value, GetCurrentProcess(), &h.strong, e.access | SECTION_QUERY | SECTION_MAP_READ, FALSE, 0))
          DuplicateHandle(S.p, (HANDLE)e.value, GetCurrentProcess(), &h.strong, 0, FALSE, DUPLICATE_SAME_ACCESS);
        struct { PVOID base; ULONG attr; LARGE_INTEGER size; } sbi{};
        if (h.strong && NtQuerySection_(h.strong, 0, &sbi, sizeof sbi, nullptr) >= 0) { h.secSize = sbi.size.QuadPart; h.secAttr = sbi.attr; }
      }
      if (h.kind == L"File") { h.disk = h.posOk = fileOp(h.pin, 0, h.pos); r.add("diskFiles", h.disk); }
      if (sync) { syncState(h); r.add("syncObjects", h.syncOk); }
    } else r.add("handlesNotPinned", 1);
    s.handles.push_back(h);
  }
  r.add("handles", (double)s.handles.size());
}
// Thread-pool worker factory of the target (NtQueryInformationWorkerFactory, WorkerFactoryBasicInformation, the x64
// layout this tool sees): minimum and total worker threads.
static const auto NtQueryInformationWorkerFactory_ = nt<LONG (NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG)>("NtQueryInformationWorkerFactory");
static const auto NtSetInformationWorkerFactory_ = nt<LONG (NTAPI*)(HANDLE, ULONG, PVOID, ULONG)>("NtSetInformationWorkerFactory");
static bool poolCounts(HANDLE f, ULONG& minimum, ULONG& total) {
  unsigned char b[120]{};  // 3 timeouts, 7 flags, then BindingCount, ThreadMinimum, ThreadMaximum, Pending, Waiting, Total at 32
  if (!NtQueryInformationWorkerFactory_ || NtQueryInformationWorkerFactory_(f, 7, b, sizeof b, nullptr) < 0) return false;
  minimum = *(ULONG*)(b + 36); total = *(ULONG*)(b + 52);
  return true;
}
static bool poolSetMin(HANDLE f, ULONG minimum) { return NtSetInformationWorkerFactory_ && NtSetInformationWorkerFactory_(f, 4, &minimum, sizeof minimum) >= 0; }
// Idle pool workers end after about 130 s idle (toy: 8 threads to 1 at 130-135 s). The kernel worker factory counts its
// threads and a rewind cannot put an ended thread back, so while a snapshot is held each factory's minimum is its thread
// count at the snapshot: no worker ends (toy: minimum 7 kept all 7 past 150 s). Put back when the snapshot is dropped.
static void poolHold(Snap& s, Result& r) {
  for (auto& h : s.handles) {
    ULONG mn = 0, tot = 0;
    HANDLE f = nullptr;
    if (h.kind != L"TpWorkerFactory" || !h.pin || !poolCounts(h.pin, mn, tot) || tot <= mn) continue;
    if (!DuplicateHandle(GetCurrentProcess(), h.pin, GetCurrentProcess(), &f, 0, FALSE, DUPLICATE_SAME_ACCESS)) continue;
    if (!poolSetMin(f, tot)) { CloseHandle(f); r.add("poolHoldFailed", 1); continue; }
    s.poolMin.push_back({f, mn});
    r.add("poolFactoriesHeld", 1); r.add("poolWorkersHeld", tot);
  }
}
static void freeSnap(Snap& s) {
  for (auto& [f, mn] : s.poolMin) { poolSetMin(f, mn); CloseHandle(f); }
  for (auto& h : s.handles) { if (h.pin) CloseHandle(h.pin); if (h.strong) CloseHandle(h.strong); }
  if (s.store) UnmapViewOfFile(s.store);
  if (s.map) CloseHandle(s.map);
  if (s.file != INVALID_HANDLE_VALUE) CloseHandle(s.file);
  s = Snap{};
}

// ---------------------------------------------------------------- which view belongs to which section
// No read-only call tells which section a view maps (QueryWorkingSetEx share counts read 7 for every section page on
// Windows 11 26300, measured). So with the target frozen: a file-backed view matches a section handle of the same file
// whose bytes are equal at exactly one 64 KB step; a pagefile-backed writable view gets its last byte flipped and put
// back, and the one section whose byte follows both times is the same memory (--no-view-probe skips this one write).
static bool sameBytes(const unsigned char* a, const unsigned char* b, size_t n) {
  __try { return !memcmp(a, b, n); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static int peek(const unsigned char* a) { __try { return *a; } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; } }
struct SecMap { int idx; unsigned char* m; ULONGLONG size; std::wstring file; };
static std::vector<SecMap> mapSections(const Snap& s) {
  std::vector<SecMap> out;
  for (size_t i = 0; i < s.handles.size(); ++i) {
    auto& h = s.handles[i];
    if (!h.strong || h.kind != L"Section" || (h.secAttr & SEC_IMAGE) || !h.secSize) continue;
    unsigned char* m = (unsigned char*)MapViewOfFile(h.strong, FILE_MAP_READ, 0, 0, 0);
    if (m) out.push_back({(int)i, m, h.secSize, mappedFile(GetCurrentProcess(), (ULONG_PTR)m)});
  }
  return out;
}
// The (section, offset) pairs whose byte under the view's last byte reads `want`.
static std::vector<std::pair<int, ULONGLONG>> byteHits(const std::vector<SecMap>& secs, const View& v, int want, int only = -1, ULONGLONG onlyOff = 0) {
  std::vector<std::pair<int, ULONGLONG>> hits;
  for (auto& sm : secs) {
    if (!sm.file.empty() || (only >= 0 && sm.idx != only)) continue;
    for (ULONGLONG off = only >= 0 ? onlyOff : 0; off + v.size <= sm.size; off += 65536) {
      if (peek(sm.m + off + v.size - 1) == want) hits.push_back({sm.idx, off});
      if (only >= 0) break;
    }
  }
  return hits;
}
static bool flipProbe(HANDLE p, const std::vector<SecMap>& secs, const View& v, int& sec, ULONGLONG& off, int only = -1, ULONGLONG onlyOff = 0) {
  ULONG_PTR at = v.base + v.size - 1;
  MEMORY_BASIC_INFORMATION m{};
  if (!VirtualQueryEx(p, (void*)at, &m, sizeof m) || m.State != MEM_COMMIT || !writable(m.Protect) || (m.Protect & PAGE_GUARD)) return false;
  unsigned char b0 = 0, b1;
  if (!ReadProcessMemory(p, (void*)at, &b0, 1, nullptr)) return false;
  b1 = b0 ^ 0x5A;
  if (!WriteProcessMemory(p, (void*)at, &b1, 1, nullptr)) return false;
  auto flipped = byteHits(secs, v, b1, only, onlyOff);
  WriteProcessMemory(p, (void*)at, &b0, 1, nullptr);
  auto back = byteHits(secs, v, b0, only, onlyOff);
  std::vector<std::pair<int, ULONGLONG>> both;
  for (auto& f : flipped) if (std::find(back.begin(), back.end(), f) != back.end()) both.push_back(f);
  if (both.size() != 1) return false;
  sec = both[0].first; off = both[0].second;
  return true;
}
static void matchViews(Sess& S, Snap& s, Result& r) {
  auto secs = mapSections(s);
  std::vector<unsigned char> tmp;
  for (auto& v : s.views) {
    if (v.type != MEM_MAPPED) continue;
    v.anon = v.file.empty() && writable(v.aprot);
    if (!v.file.empty()) {
      tmp.resize(v.size);
      SIZE_T got = 0;
      int hits = 0;
      if (ReadProcessMemory(S.p, (void*)v.base, tmp.data(), v.size, &got) && got == v.size)
        for (auto& sm : secs)
          for (ULONGLONG off = 0; sm.file == v.file && off + v.size <= sm.size; off += 65536)
            if (sameBytes(tmp.data(), sm.m + off, v.size)) { ++hits; v.sec = sm.idx; v.off = off; }
      if (hits != 1) v.sec = -1;
    } else if (v.anon && S.o.viewProbe) {
      if (!flipProbe(S.p, secs, v, v.sec, v.off)) v.sec = -1;
    }
    r.add(v.sec >= 0 ? "viewsPinned" : v.anon ? "viewsAnonNotPinned" : "viewsNotPinned", 1);
  }
  for (auto& sm : secs) UnmapViewOfFile(sm.m);
  // writable memory that looks shared with another process (a named section, or handles held outside the target): IPC or
  // a renderer bridge, whose other side is not rewound
  std::vector<SecHolder> holders;
  bool scanned = false;
  struct Close { std::vector<SecHolder>& h; ~Close() { for (auto& x : h) CloseHandle(x.h); } } closeHolders{holders};
  for (auto& v : s.views) {
    if (v.type != MEM_MAPPED || !writable(v.aprot) || v.sec < 0) continue;
    HRec& h = s.handles[v.sec];
    unsigned char nb[1024]{};
    UNICODE_STRING* u = (UNICODE_STRING*)nb;
    std::wstring name;
    if (NtQueryObject_(h.pin, 1, nb, sizeof nb, nullptr) >= 0 && u->Buffer) name.assign(u->Buffer, u->Length / 2);
    struct { ULONG attr, access, handles, pointers; ULONG rest[10]; } obi{};
    NtQueryObject_(h.pin, 0, &obi, sizeof obi, nullptr);
    // once a view was scanned, this tool holds a duplicate of every section handle in the system: not counted, or a later
    // view of the target's own unnamed section looks shared (viewsSharedSelf 2 when it lies above the first shared view)
    for (auto& x : holders) obi.handles -= CompareObjectHandles_ && CompareObjectHandles_(x.h, h.pin) ? 1 : 0;
    ULONG ours = 0;
    for (auto& o : s.handles) ours += o.pin && o.kind == L"Section" && CompareObjectHandles_ && CompareObjectHandles_(o.pin, h.pin) ? (o.strong ? 3 : 2) : 0;
    bool coop = name.find(L"rewind-coop-") != std::wstring::npos || name.find(L"rewind-ts3n-") != std::wstring::npos;
    // the bridge names its channels <name>Channel_<guid> (util_ipcchannel.h, util_sharedmemory.cpp); its server follows our ops
    if (S.bridge && (name.find(L"Channel_") != std::wstring::npos || name.find(L"\\QueryResults_") != std::wstring::npos)) { r.add("viewsBridge", 1); coop = true; }
    if (!coop && (!name.empty() || obi.handles > ours)) {
      if (!scanned) {
        ULONG secType = 0;
        for (auto& [idx, kind] : S.kinds) if (kind == L"Section") secType = idx;
        holders = sectionHolders(secType);
        scanned = true;
      }
      bool foreign = false, audio = false;
      std::string owners = sectionOwners(holders, h.pin, S.pid, foreign, &audio);
      if (!foreign) { r.add("viewsSharedSelf", 1); continue; }  // only the target holds it (a second handle there): its own memory
      r.add("viewsShared", 1);
      if (S.o.allowShared) continue;
      // not ours and not the bridge's: a rewind leaves it as it is and refuses if its bytes changed since the snapshot
      // (docs/270 §20); the note names the processes that also hold it and where the target points at it
      // a holder the tool cannot open and cannot name: who keeps the view in the target (the audio client's object for
      // the audio engine's buffers, docs/270 §23)
      std::string kept;
      if (owners.find("processes the tool cannot open") != std::string::npos && owners.find(" pid ") == std::string::npos)
        for (auto& m : heapOwners(S.p, s.views, v.base)) {
          kept += (kept.empty() ? "" : ", ") + m;
          audio |= hasAny(m, S.o.audio);
        }
      std::string d = fmt("%p (+%zx, section %ls, %lu handles, %lu ours; held by %s; pointed to from %s%s%s)", (void*)v.base, (size_t)v.size,
                          name.empty() ? L"unnamed" : name.c_str(), obi.handles, ours, owners.c_str(), pointedFrom(S.p, s.views, v.base).c_str(),
                          kept.empty() ? "" : "; kept by objects of ", kept.c_str());
      s.foreign[v.base] = {v.base + v.size, d};
      r.add("viewsForeign", 1);
      if (audio) { s.audioViews.insert(v.base); r.add("viewsAudio", 1); }
      r.notes.push_back("writable view shared with another process, left alone at a rewind: " + d);
    }
  }
}
// At a rewind: is the view still that section? A file view keeps its file name (checked by the caller); a pagefile view is probed again.
static bool stillSame(Sess& S, const View& v) {
  if (!v.file.empty()) return true;
  SecMap sm{v.sec, (unsigned char*)MapViewOfFile(S.s.handles[v.sec].strong, FILE_MAP_READ, 0, 0, 0), S.s.handles[v.sec].secSize, L""};
  if (!sm.m) return false;
  int sec; ULONGLONG off;
  bool same = flipProbe(S.p, {sm}, v, sec, off, v.sec, v.off);
  UnmapViewOfFile(sm.m);
  return same;
}

// The window system's shared read-only views in the target: server info and the USER handle table (user32's gSharedInfo,
// 64-bit layout under WOW64: psi, aheList) and the desktop heap (pDeskInfo in each thread's 64-bit TEB CLIENTINFO, +0x800 +0x20).
// win32k maps them and commits pages there when any process on the desktop makes windows; a rewind never restores them, so
// pages committed there after the snapshot are not the target's memory. Only mapped, read-only views without a file and
// without a section handle in the target count.
static void systemViews(Sess& S, Snap& s, Result& r) {
  std::vector<std::pair<ULONG64, const char*>> at;
  for (auto& v : s.views) {
    if (v.type != MEM_IMAGE || baseName(v.file) != "user32.dll") continue;
    IMAGE_DOS_HEADER dos = rd<IMAGE_DOS_HEADER>(S.p, v.base);
    IMAGE_NT_HEADERS32 nth = rd<IMAGE_NT_HEADERS32>(S.p, v.base + dos.e_lfanew);
    if (dos.e_magic != IMAGE_DOS_SIGNATURE || nth.FileHeader.Machine != IMAGE_FILE_MACHINE_I386) continue;
    IMAGE_EXPORT_DIRECTORY ed = rd<IMAGE_EXPORT_DIRECTORY>(S.p, v.base + nth.OptionalHeader.DataDirectory[0].VirtualAddress);
    if (!ed.NumberOfNames || ed.NumberOfNames > 20000) continue;
    std::vector<DWORD> names(ed.NumberOfNames), funcs(ed.NumberOfFunctions);
    std::vector<WORD> ords(ed.NumberOfNames);
    ReadProcessMemory(S.p, (void*)(v.base + ed.AddressOfNames), names.data(), names.size() * 4, nullptr);
    ReadProcessMemory(S.p, (void*)(v.base + ed.AddressOfNameOrdinals), ords.data(), ords.size() * 2, nullptr);
    ReadProcessMemory(S.p, (void*)(v.base + ed.AddressOfFunctions), funcs.data(), funcs.size() * 4, nullptr);
    for (DWORD i = 0; i < ed.NumberOfNames; ++i) {
      char nm[16]{};
      ReadProcessMemory(S.p, (void*)(v.base + names[i]), nm, 15, nullptr);
      if (strcmp(nm, "gSharedInfo") || ords[i] >= funcs.size()) continue;
      ULONG64 si[2]{};
      ReadProcessMemory(S.p, (void*)(v.base + funcs[ords[i]]), si, sizeof si, nullptr);
      at.push_back({si[0], "server info"});
      at.push_back({si[1], "USER handle table"});
    }
  }
  for (auto& t : s.threads)
    if (t.teb) at.push_back({rd<ULONG64>(S.p, t.teb + 0x800 + 0x20), "desktop heap"});
  for (auto& [a, what] : at) {
    MEMORY_BASIC_INFORMATION m{};
    if (!a || a >= 0x100000000ull || !VirtualQueryEx(S.p, (void*)a, &m, sizeof m) || m.Type != MEM_MAPPED || m.AllocationProtect != PAGE_READONLY) continue;
    const View* v = viewAt(s.views, (ULONG_PTR)m.AllocationBase);
    if (!v || v->base != (ULONG_PTR)m.AllocationBase || !v->file.empty() || v->sec >= 0 || s.sysViews.count(v->base)) continue;
    s.sysViews[v->base] = what;
    r.add("viewsSystem", 1);
  }
}

// ---------------------------------------------------------------- cooperation block
static ULONG64 coopFind(DWORD pid) {
  HANDLE m = OpenFileMappingA(FILE_MAP_READ, FALSE, fmt("Local\\rewind-coop-%lu", pid).c_str());
  if (!m) return 0;
  RewindCoop* c = (RewindCoop*)MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0);
  ULONG64 a = c && c->magic == REWIND_COOP_MAGIC && c->version == 1 ? c->self : 0;
  if (c) UnmapViewOfFile(c);
  CloseHandle(m);
  return a;
}
#define COOP(f) (S.coop + offsetof(RewindCoop, f))
// ts3native [Rewind] Park (docs/270 §19): the route worker back in its idle wait before the bridge parks the sim thread, whose
// managed calls a running job needs. Absent block = nothing to do.
static bool parkWorker(Sess& S, Result& r) {
  if (!S.tn) {
    HANDLE m = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, fmt("Local\\rewind-ts3n-%lu", S.pid).c_str());
    if (!m) return true;
    S.tn = (RewindWorkerPark*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(RewindWorkerPark));
    CloseHandle(m);
    if (S.tn && (S.tn->magic != REWIND_TS3N_MAGIC || S.tn->version != 1)) { UnmapViewOfFile(S.tn); S.tn = nullptr; }
    if (!S.tn) return true;
  }
  S.tn->request = 1;
  int stable = 0;  // idle in 5 reads 2 ms apart: not a worker passing through between two jobs
  for (LONGLONG end = qpc() + S.o.quiesceMs * qpf() / 1000; stable < 5 && qpc() < end; Sleep(2)) stable = S.tn->idle ? stable + 1 : 0;
  if (stable < 5) {
    S.tn->request = 0;
    r.refuse(fmt("the ts3native route worker did not come back to its idle wait within %lu ms ([Rewind] Park)", S.o.quiesceMs));
    return false;
  }
  r.add("workerParked", 1);
  return true;
}
static bool park(Sess& S, Result& r) {
  if (!parkWorker(S, r)) return false;  // also under --no-coop: the game jobs pause the sim through the agent, not this block
  if (!S.o.coop) return true;
  if (!S.coop) S.coop = coopFind(S.pid);
  if (!S.coop) { r.refuse("no cooperation block: the target cannot confirm it is quiet (ts3native [Rewind] off?)"); return false; }
  if (rd<int32_t>(S.p, COOP(audioOn)) && !S.o.allowAudio) { r.refuse("the target reports an open sound device"); return false; }
  const uint32_t tick0 = rd<uint32_t>(S.p, COOP(tick));
  wr<int32_t>(S.p, COOP(request), 1);
  for (LONGLONG end = qpc() + S.o.quiesceMs * qpf() / 1000; qpc() < end; Sleep(1))
    if (rd<int32_t>(S.p, COOP(parked)) == 1) { S.bridge = rd<int32_t>(S.p, COOP(bridge)) == 1; return true; }
  wr<int32_t>(S.p, COOP(request), 0);
  if (S.tn) S.tn->request = 0;
  // the block's tick counts the target's park points (a bridge client: Presents): it tells a target that no longer
  // reaches them (a stuck renderer) from one that reaches them and does not park (docs/270 §26)
  const uint32_t ticks = rd<uint32_t>(S.p, COOP(tick)) - tick0;
  r.add("parkTicksSeen", ticks);
  r.refuse(fmt("the target did not park within %lu ms (%s)", S.o.quiesceMs, ticks ? fmt("it passed its park point %u times", ticks).c_str()
                                                                            : "it never reached its park point: a bridge client presented no frame"));
  return false;
}
static void unpark(Sess& S) {
  if (S.o.coop && S.coop) wr<int32_t>(S.p, COOP(request), 0);
  if (S.tn) S.tn->request = 0;
}
// Sends one op (1 snapshot, 2 rewind, 3 plan) to the bridge server through the parked bridge client and waits for the
// acknowledgement. False = refused or no answer (reason in r); the caller decides whether that is harmless.
static bool bridgeOp(Sess& S, Result& r, uint32_t op) {
  wr<uint32_t>(S.p, COOP(op), op);
  LONGLONG end = qpc() + 20 * qpf();
  while (rd<uint32_t>(S.p, COOP(op)) != 0 && qpc() < end) Sleep(1);
  if (rd<uint32_t>(S.p, COOP(op)) != 0) { wr<uint32_t>(S.p, COOP(op), 0); r.refuse(fmt("the bridge client did not answer op %u within 20 s", op)); return false; }
  static const char* names[8] = {"bridgeKept", "bridgeRevive", "bridgeDrop", "bridgeBuffers", "bridgeSurfaces", "bridgeSavedKB", "bridgeNotRestored", "bridgeRefused"};
  if (op != 2) for (int i = 0; i < 8; ++i) r.add(names[i], rd<uint32_t>(S.p, COOP(bridgeStats) + 4 * i));
  int32_t res = rd<int32_t>(S.p, COOP(bridgeResult));
  if (res != 0) { r.refuse(fmt("the bridge server refused op %u (result %08lx; its reason is in the bridge server log, [Rewind] lines)", op, (unsigned long)res)); return false; }
  if (op == 2) r.add("bridgeRewound", 1);
  return true;
}

// ---------------------------------------------------------------- snapshot
// For a refusal: where the thread started, the system call with its number and arguments, the handle's name when the call
// works on a file, and the first return-address-like stack words inside modules (who made the call).
static std::string detail(Sess& S, const TS& t, const std::vector<View>& views) {
  auto at = [&](DWORD a) {
    const View* v = viewAt(views, a);
    return v && v->type == MEM_IMAGE ? fmt("%s+%lx", baseName(v->file).c_str(), (unsigned long)(a - v->base)) : fmt("%08lx", a);
  };
  std::string d = "start " + at(t.start);
  if (t.kernel) {
    d += fmt(", syscall %lx, args", t.nr);
    for (int i = 7; i < 17; ++i) d += fmt(" %lx", t.sig[i]);
    if (t.name == "NtFsControlFile") d += fmt(", fsctl %lx", t.sig[12]);
    static const char* fileCalls[] = {"NtFsControlFile", "NtDeviceIoControlFile", "NtReadFile", "NtWriteFile", "NtFlushBuffersFile", "NtQueryDirectoryFile"};
    HANDLE dup = nullptr;
    for (auto* k : fileCalls)
      if (t.name == k && DuplicateHandle(S.p, (HANDLE)(ULONG_PTR)t.sig[7], GetCurrentProcess(), &dup, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
        std::wstring name; LONGLONG pos = 0;
        d += fileOp(dup, 2, pos, &name) ? fmt(", handle %lx = %ls", t.sig[7], name.c_str()) : fmt(", handle %lx (name query timed out)", t.sig[7]);
        CloseHandle(dup);
      }
  }
  DWORD w[64]{};
  ReadProcessMemory(S.p, (void*)(ULONG_PTR)t.ctx.Esp, w, sizeof w, nullptr);
  int k = 0;
  d += ", stack";
  for (DWORD x : w) { const View* v = viewAt(views, x); if (v && v->type == MEM_IMAGE && k < 6) { d += " " + at(x); ++k; } }
  return d;
}

// Extended vector state (AVX, AVX-512) of a thread in 32-bit code: a WOW64 context carries only the lower 128 bits, the
// native context with CONTEXT_XSTATE has the rest. Put back after the WOW64 context; a feature the snapshot did not have
// in use goes back to zero. set = false compares the thread's state with the snapshot's.
static DWORD64 xfeat() { return GetEnabledXStateFeatures() & (XSTATE_MASK_AVX | XSTATE_MASK_AVX512); }
static bool xstate(HANDLE h, const TS& o, bool set) {
  DWORD64 feat = xfeat();
  if (!feat) return true;
  DWORD len = 0;
  InitializeContext(nullptr, CONTEXT_XSTATE, nullptr, &len);
  std::vector<unsigned char> buf(len);
  PCONTEXT c = nullptr;
  if (!InitializeContext(buf.data(), CONTEXT_XSTATE, &c, &len) || !SetXStateFeaturesMask(c, feat)) return false;
  auto want = [&](DWORD id, DWORD n) {
    std::string b(n, '\0');
    for (auto& [i, x] : o.xs) if (i == id && x.size() == n) b = x;
    return b;
  };
  if (set) {
    for (DWORD id = 2; id < 8; ++id) { DWORD n = 0; void* p = (feat >> id & 1) ? LocateXStateFeature(c, id, &n) : nullptr; if (p) memcpy(p, want(id, n).data(), n); }
    return SetThreadContext(h, c) != 0;
  }
  if (!GetThreadContext(h, c)) return false;
  DWORD64 used = 0;
  GetXStateFeaturesMask(c, &used);
  for (DWORD id = 2; id < 8; ++id) {
    DWORD n = 0; void* p = (feat >> id & 1) ? LocateXStateFeature(c, id, &n) : nullptr;
    if (!p) continue;
    std::string now = (used >> id & 1) ? std::string((char*)p, n) : std::string(n, '\0');
    if (now != want(id, n)) return false;
  }
  return true;
}

// Freezes at a moment where every thread is in a state a rewind can put back: running 32-bit code, inside a system call
// that only waits, or parked in a wait for another process (parkedWait). Tries again a few times; false = refused (reasons
// in r, with how often each thread was caught there), threads resumed.
static bool freezeClean(Sess& S, Result& r, std::vector<TS>& out) {
  std::map<DWORD, int> badTries;
  for (int attempt = 0; attempt <= S.o.retries; ++attempt) {
    std::vector<std::pair<size_t, std::string>> bad;
    std::vector<std::string> noCtx;
    out.clear();
    if (!freeze(S)) { thaw(S); r.refuse("cannot suspend the target"); return false; }
    for (auto& t : S.frozen) {
      TS ts;
      if (!classify(S, t, ts)) { noCtx.push_back(fmt("thread %lu: no context (error %lu)", t.tid, GetLastError())); continue; }
      out.push_back(ts);
    }
    if (S.stubs.empty()) loadStubs(S, viewsOf(S.p, regions(S.p)));
    for (auto& ts : out)
      if (ts.kernel && ts.name.rfind("syscall@", 0) == 0) { auto it = S.stubs.find(ts.stub); if (it != S.stubs.end()) ts.name = it->second; }
    // thread-pool workers: the start of any thread idle in NtWaitForWorkViaWorkerFactory (ntdll's TppWorkerThread). One with
    // that start doing anything else is inside a work item; a snapshot there leaves the rewind a worker it cannot put back
    // into a work item that has ended meanwhile (docs/270 §21), so the snapshot waits for a moment when all are idle
    std::set<DWORD>& poolStarts = S.poolStarts;  // kept across tries and ops: at one moment every worker may be busy
    for (auto& ts : out) if (ts.kernel && ts.name == "NtWaitForWorkViaWorkerFactory") poolStarts.insert(ts.start);
    for (size_t i = 0; i < out.size(); ++i) {
      TS& ts = out[i];
      ts.parked = parkedWait(ts);
      std::string why = ts.transit ? "is between 32-bit and 64-bit code" : ts.kernel && !restartable(ts.name) && !ts.parked ? "is inside " + ts.name : "";
      if (why.empty() && poolStarts.count(ts.start) && !(ts.kernel && ts.name == "NtWaitForWorkViaWorkerFactory"))
        why = "is a thread-pool worker inside a work item (the stack modules below name its callback)";
      if (!why.empty()) { bad.push_back({i, why}); ++badTries[ts.tid]; }
    }
    if (bad.empty() && noCtx.empty()) { r.add("freezeAttempts", attempt + 1); return true; }
    if (attempt == S.o.retries) {  // last try: say who and what while the threads are still held
      auto views = viewsOf(S.p, regions(S.p));
      for (auto& [i, why] : bad) r.refuse(fmt("thread %lu %s (%s; caught there in %d of %d tries)", out[i].tid, why.c_str(), detail(S, out[i], views).c_str(), badTries[out[i].tid], attempt + 1));
      for (auto& w : noCtx) r.refuse(w);
    }
    thaw(S);
    if (attempt < S.o.retries) Sleep(5 + attempt);
  }
  return false;
}

// ---------------------------------------------------------------- write watch (docs/270 §15)
// Whether a 32-bit module imports GetWriteWatch or ResetWriteWatch (by name; a GetProcAddress user is not seen).
static bool importsWriteWatch(HANDLE p, ULONG_PTR base) {
  IMAGE_DOS_HEADER dos = rd<IMAGE_DOS_HEADER>(p, base);
  IMAGE_NT_HEADERS32 nth = rd<IMAGE_NT_HEADERS32>(p, base + dos.e_lfanew);
  if (dos.e_magic != IMAGE_DOS_SIGNATURE || nth.FileHeader.Machine != IMAGE_FILE_MACHINE_I386) return false;
  DWORD imp = nth.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
  for (int d = 0; imp && d < 512; ++d) {
    IMAGE_IMPORT_DESCRIPTOR id = rd<IMAGE_IMPORT_DESCRIPTOR>(p, base + imp + d * sizeof id);
    if (!id.Name) break;
    DWORD thunk = id.OriginalFirstThunk ? id.OriginalFirstThunk : id.FirstThunk;
    for (int k = 0; k < 4096; ++k) {
      DWORD t = rd<DWORD>(p, base + thunk + 4 * k);
      if (!t) break;
      if (t & 0x80000000) continue;
      char nm[20]{};
      ReadProcessMemory(p, (void*)(base + t + 2), nm, 19, nullptr);
      if (!strcmp(nm, "GetWriteWatch") || !strcmp(nm, "ResetWriteWatch")) return true;
    }
  }
  return false;
}
// Dirty pages the kernel reports for [a, a + n) without resetting them; -1 = no write watch there.
static long wwDirty(HANDLE p, ULONG_PTR a, size_t n) {
  std::vector<PVOID> hit(n / 4096 + 1);
  ULONG_PTR cnt = hit.size(); ULONG gran = 0;
  return NtGetWriteWatch_(p, 0, (void*)a, n, hit.data(), &cnt, &gran) < 0 ? -1 : (long)cnt;
}
// Writes every committed page of [a, e) back with its own content, so the kernel records a write on each: the dirty bits
// become a superset of what the GC saw at the snapshot (content unchanged). Returns committed pages, or -1 on a failure.
static long wwTouch(HANDLE p, ULONG_PTR a, ULONG_PTR e) {
  long pages = 0;
  std::vector<unsigned char> buf;
  MEMORY_BASIC_INFORMATION m;
  for (ULONG_PTR x = a; x < e && VirtualQueryEx(p, (void*)x, &m, sizeof m) == sizeof m; x = (ULONG_PTR)m.BaseAddress + m.RegionSize) {
    size_t n = std::min<ULONG_PTR>((ULONG_PTR)m.BaseAddress + m.RegionSize, e) - x;
    if (m.State != MEM_COMMIT) continue;
    buf.resize(n);
    if (!readMem(p, x, n, m.Protect, buf.data()) || !wvm(p, x, buf.data(), n)) return -1;
    pages += (long)(n / 4096);
  }
  return pages;
}

// Before any change: every committed write-watch page can be written by this tool, and one clean page shows that such a
// write sets its dirty bit (the write is content-equal, harmless to a superset-safe owner). Not run by --plan-only.
static void wwProbe(Sess& S, Result& r) {
  bool proved = false;
  MEMORY_BASIC_INFORMATION m;
  for (auto& [b, e] : S.s.ww)
    for (ULONG_PTR x = b; x < e && VirtualQueryEx(S.p, (void*)x, &m, sizeof m) == sizeof m; x = (ULONG_PTR)m.BaseAddress + m.RegionSize) {
      if (m.State != MEM_COMMIT) continue;
      if (!writable(m.Protect) || (m.Protect & PAGE_GUARD)) { r.refuse(fmt("write-watch pages at %p have protection %lx: they cannot be marked dirty", m.BaseAddress, m.Protect)); return; }
      for (ULONG_PTR pg = x; !proved && pg < (ULONG_PTR)m.BaseAddress + m.RegionSize && pg < x + 64 * 4096; pg += 4096) {
        if (wwDirty(S.p, pg, 4096) != 0) continue;
        if (wwTouch(S.p, pg, pg + 4096) != 1 || wwDirty(S.p, pg, 4096) != 1) { r.refuse(fmt("a write from this tool does not set the write-watch bit (page %p)", (void*)pg)); return; }
        proved = true; r.add("writeWatchProbe", 1);
      }
    }
}

static Result snapshot(Sess& S) {
  Result r; r.op = "snapshot";
  Lap lap{r};
  freeSnap(S.s);
  Snap& s = S.s;
  // module guards first, before the target is parked or frozen: a thread check would refuse too, for a less useful reason
  for (auto& v : viewsOf(S.p, regions(S.p))) {
    if (v.type != MEM_IMAGE) continue;
    if (hasAny(baseName(v.file), S.o.forbid)) r.refuse("module " + baseName(v.file) + " is loaded (detach the probe first)");
    if (!S.o.allowGpu && hasAny(baseName(v.file), S.o.gpu)) r.refuse("module " + baseName(v.file) + " is loaded: the GPU device sits in this process and is not rewound (run under the RTX Remix bridge)");
  }
  if (r.code && !S.o.planOnly) return r;
  if (!park(S, r)) return r;
  if (S.bridge && !bridgeOp(S, r, 1)) { unpark(S); return r; }
  lap("park");
  if (!freezeClean(S, r, s.threads)) { unpark(S); return r; }
  LONGLONG tFreeze = qpc();
  lap("freeze");
  s.regs = regions(S.p);
  s.views = viewsOf(S.p, s.regs);
  for (auto& t : s.threads) {
    const View* v = viewAt(s.views, t.start);
    if (v && hasAny(baseName(v->file), S.o.audio)) { r.add("audioThreads", 1); if (!S.o.allowAudio) r.refuse(fmt("thread %lu belongs to the sound module %s", t.tid, baseName(v->file).c_str())); }
  }
  for (auto& g : s.regs) {
    if (g.type != MEM_PRIVATE || g.base != g.abase) continue;
    PVOID hit[1]; ULONG_PTR n = 1; ULONG gran = 0;
    if (NtGetWriteWatch_(S.p, 0, (void*)g.base, 4096, hit, &n, &gran) < 0) continue;
    ULONG_PTR end = g.base;
    for (auto& x : s.regs) if (x.abase == g.base) end = std::max(end, x.base + x.size);
    s.ww[g.base] = end;
  }
  if (!s.ww.empty()) {  // who reads the dirty bits: only a superset-safe owner may (docs/270 §15)
    r.add("writeWatchAllocs", (double)s.ww.size());
    for (auto& v : s.views) {
      std::string b = baseName(v.file);
      if (v.type == MEM_IMAGE && importsWriteWatch(S.p, v.base) && std::find(S.o.wwOwners.begin(), S.o.wwOwners.end(), b) == S.o.wwOwners.end())
        r.refuse(fmt("module %s imports GetWriteWatch: not known to need only a superset of dirty bits after a rewind (%zu write-watch allocations)", b.c_str(), s.ww.size()));
    }
  }
  for (auto& g : s.regs) if ((g.copied = copies(g)) != 0) { g.off = s.bytes; s.bytes += g.size; }
  if (r.code == 0 && !S.o.planOnly) {
    char dir[MAX_PATH], path[MAX_PATH];
    GetTempPathA(MAX_PATH, dir);
    sprintf_s(path, "%srewind-%lu-%llx.snap", dir, S.pid, (unsigned long long)tFreeze);
    s.file = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
    s.map = s.file == INVALID_HANDLE_VALUE ? nullptr : CreateFileMappingA(s.file, nullptr, PAGE_READWRITE, (DWORD)((ULONG64)s.bytes >> 32), (DWORD)s.bytes, nullptr);
    s.store = s.map ? (unsigned char*)MapViewOfFile(s.map, FILE_MAP_ALL_ACCESS, 0, 0, 0) : nullptr;
    if (!s.store) r.refuse(fmt("no room for the %zu MB snapshot file in %s (error %lu)", s.bytes >> 20, dir, GetLastError()));
    for (auto& g : s.regs) {
      if (r.code || !g.copied) continue;
      if (!readMem(S.p, g.base, g.size, g.prot, s.store + g.off)) r.refuse(fmt("cannot read region %p (+%zx, protection %lx)", (void*)g.base, (size_t)g.size, g.prot));
    }
    if (r.code == 0) { s.hash = hash64(s.store, s.bytes, 1); s.copied = true; }
  }
  lap("copy");
  pinHandles(S, s, r);
  lap("handles");
  matchViews(S, s, r);
  systemViews(S, s, r);
  lap("views");
  s.gdi = GetGuiResources(S.p, GR_GDIOBJECTS); s.user = GetGuiResources(S.p, GR_USEROBJECTS);
  if (S.coop) {
    s.tick = rd<uint32_t>(S.p, COOP(tick));
    s.vAtFreeze = tFreeze - rd<int64_t>(S.p, COOP(skewQpc));
    if (S.o.timeFix) wr<int64_t>(S.p, COOP(skewQpc), qpc() - s.vAtFreeze);  // no time passed for the target while it stood still
  }
  r.add("threads", (double)s.threads.size()); r.add("regions", (double)s.regs.size()); r.add("mb", (double)(s.bytes >> 20));
  for (auto& t : s.threads) { r.add(t.kernel ? "threadsWaiting" : "threadsRunning", 1); r.add("threadsParked", t.parked); r.add("threadsXState", !t.xs.empty()); }
  thaw(S);
  unpark(S);
  lap("resume");
  if (r.code && !S.o.planOnly) freeSnap(s); else s.have = true;
  if (!r.code && !S.o.planOnly) poolHold(s, r);  // --plan-only keeps a refused snapshot: its plans still list their reasons
  return r;
}

// ---------------------------------------------------------------- rewind
struct Plan {
  std::vector<std::pair<const TS*, const Th*>> set, leave, late;  // threads: write the context back / already in the same wait / in a short Sleep
  std::vector<std::pair<const TS*, const Th*>> alert;            // in an ntdll alert wait at another depth: snapshot context, then alerted
  int deadRegs = 0;                                              // left although callee-saved registers differ (dead at the return site)
  std::vector<const Th*> reap;
  std::vector<ULONG_PTR> closeHandles, unmap, freeAllocs;
  std::vector<const HRec*> reinsert;
  std::vector<const View*> remap, recreate;
  std::vector<std::pair<ULONG_PTR, ULONG_PTR>> reserve;  // base, end
  std::vector<std::string> retry;                          // thread states that may be different in a moment
  int rearmed = 0;                                         // left in the same wait, entered again after the snapshot
  std::vector<DWORD> catchable;                            // retried only because they were not in their snapshot wait
  std::vector<std::pair<const TS*, std::vector<DWORD>>> waitOn;  // left in a wait: each handle it waits on must still be the same object
};

// Inside Sleep with at most 1 s to go: the call returns by itself, touches nothing, and a context set meanwhile takes
// effect at that return.
static bool shortSleep(Sess& S, const TS& n) {
  if (!n.kernel || n.name != "NtDelayExecution") return false;
  LONGLONG interval = rd<LONGLONG>(S.p, n.sig[8]);
  return interval < 0 && interval >= -10000000;
}
// Bytes of the snapshot's memory copy at a target address (false when that range was not copied).
static bool snapBytes(const Snap& s, ULONG_PTR a, size_t n, void* out) {
  if (!s.store) return false;  // --plan-only: no memory copy
  for (auto& g : s.regs)
    if (g.copied && a >= g.base && a + n <= g.base + g.size) { memcpy(out, s.store + g.off + (a - g.base), n); return true; }
  return false;
}
// Inputs of the wait-only calls (argument numbers from 1): the handle waited on, a handle array and its count, a timeout.
struct WaitShape { const char* name; int handle, array, count, timeout; };
static const WaitShape waitShapes[] = {
  {"NtWaitForSingleObject", 1, 0, 0, 3}, {"NtWaitForMultipleObjects", 0, 2, 1, 5}, {"NtWaitForMultipleObjects32", 0, 2, 1, 5},
  {"NtDelayExecution", 0, 0, 0, 2}, {"NtRemoveIoCompletion", 1, 0, 0, 5}, {"NtRemoveIoCompletionEx", 1, 0, 0, 5},
  {"NtWaitForWorkViaWorkerFactory", 1, 0, 0, 0}, {"NtWaitForAlertByThreadId", 0, 0, 0, 2}, {"NtWaitForKeyedEvent", 1, 0, 0, 4},
  {"NtUserMsgWaitForMultipleObjectsEx", 0, 2, 1, 0}, {"NtFsControlFile", 1, 0, 0, 0},
};
// A thread in a wait at the snapshot and in a wait now: is it the same call, so that leaving it alone equals the snapshot?
// Same stub, the same eip / esp / ebp / ebx / esi / edi (what the caller sees after the return), the same return address and
// arguments (count from the stub's ret n), the same 64-bit stack pointer, and the same inputs behind pointer arguments
// (handle array, timeout) compared with the snapshot's memory copy. Stack words above the arguments may differ: they are
// memory, put back by the rewind, and the kernel does not hold them. A thread that woke and went back into the same wait
// at the same loop site passes. `handles` gets the handles waited on: planRest requires each to be the snapshot's object.
// Registers (bit = x86 register number) that the code at `a` writes before it reads them, on the straight line from `a`.
// Decodes only mov / xor-self / test / push / pop / mov imm forms and stops at anything else: what is not proven dead is
// live. A wait whose caller's registers differ is still the same state when every differing register is dead at the
// return site: the caller's own value lives in memory (a saved-register slot), which the rewind puts back. Example
// [verified, Windows 11 26300 SysWOW64]: KernelBase WaitForSingleObjectEx and WaitForMultipleObjectsEx return from the
// system call into `mov esi, eax` and restore the caller's esi with `pop esi` from their frame.
static unsigned deadAt(HANDLE p, DWORD a) {
  unsigned char c[64]{};
  ReadProcessMemory(p, (void*)(ULONG_PTR)a, c, sizeof c, nullptr);
  unsigned dead = 0, read = 0;
  auto use = [&](int r) { if (!(dead >> r & 1)) read |= 1u << r; };
  auto def = [&](int r) { if (!(read >> r & 1)) dead |= 1u << r; };
  for (size_t i = 0; i + 6 < sizeof c;) {
    unsigned char op = c[i];
    if (op == 0x8B || op == 0x89 || op == 0x33 || op == 0x31 || op == 0x85) {
      int m = c[i + 1], mod = m >> 6, reg = m >> 3 & 7, rm = m & 7;
      if (mod == 3) {
        if ((op == 0x33 || op == 0x31) && reg == rm) def(reg);
        else if (op == 0x8B) { use(rm); def(reg); }
        else if (op == 0x89) { use(reg); def(rm); }
        else { use(reg); use(rm); }
        i += 2; continue;
      }
      if (rm == 4) break;  // SIB form: not decoded
      if (mod == 0 && rm == 5) { if (op == 0x8B) def(reg); else use(reg); i += 6; continue; }
      use(rm);
      if (op == 0x8B) def(reg); else use(reg);
      i += 2 + (mod == 1 ? 1 : mod == 2 ? 4 : 0); continue;
    }
    if (op >= 0xB8 && op <= 0xBF) { def(op - 0xB8); i += 5; continue; }
    if (op >= 0x58 && op <= 0x5F) { use(4); def(op - 0x58); i += 1; continue; }
    if (op >= 0x50 && op <= 0x57) { use(op - 0x50); use(4); i += 1; continue; }
    break;
  }
  return dead;
}
static bool sameWait(Sess& S, const TS& o, const TS& n, std::string& diff, std::vector<DWORD>& handles, bool* deadRegs = nullptr) {
  diff.clear(); handles.clear();
  if (!n.kernel || n.stub != o.stub || n.name != o.name) { diff = " another call"; return false; }
  static const char* reg[] = {"eip", "esp", "ebp", "ebx", "esi", "edi", "return address"};
  static const int x86[] = {-1, -1, -1, 3, 6, 7, -1};  // ebx, esi, edi: may differ when dead at the return site
  unsigned dead = ~0u;
  for (int i = 0; i < 7; ++i) {
    if (o.sig[i] == n.sig[i]) continue;
    if (x86[i] >= 0 && o.sig[6] == n.sig[6]) {
      if (dead == ~0u) dead = deadAt(S.p, o.sig[6]);
      if (dead >> x86[i] & 1) { if (deadRegs) *deadRegs = true; continue; }
    }
    diff += fmt(" %s %lx->%lx", reg[i], o.sig[i], n.sig[i]);
  }
  for (DWORD k = 1; k <= o.nargs; ++k) if (o.sig[6 + k] != n.sig[6 + k]) diff += fmt(" arg%lu %lx->%lx", k, o.sig[6 + k], n.sig[6 + k]);
  if (o.rsp64 != n.rsp64) diff += " 64-bit stack pointer";
  if (!diff.empty()) return false;
  const WaitShape* w = nullptr;
  for (auto& x : waitShapes) if (o.name == x.name) w = &x;
  if (!w) return true;  // NtUserGetMessage, NtUserWaitMessage: no inputs
  auto arg = [&](int k) { return o.sig[6 + k]; };
  if (w->handle && arg(w->handle) && arg(w->handle) < 0xFFFFFFF0) handles.push_back(arg(w->handle));
  bool copy = S.s.store != nullptr;  // --plan-only has no memory copy to compare inputs with: a plan only lists
  if (copy && w->timeout && arg(w->timeout)) {
    LONGLONG was = 0, is = rd<LONGLONG>(S.p, arg(w->timeout));
    if (!snapBytes(S.s, arg(w->timeout), 8, &was) || was != is) diff += " timeout value";
  }
  if (w->array) {
    DWORD cnt = std::min<DWORD>(arg(w->count), 64);
    std::vector<DWORD> was(cnt), is(cnt);
    if (cnt) ReadProcessMemory(S.p, (void*)(ULONG_PTR)arg(w->array), is.data(), cnt * 4, nullptr);
    if (copy && cnt && (!snapBytes(S.s, arg(w->array), cnt * 4, was.data()) || was != is)) diff += " handle array";
    for (DWORD h : is) if (h && h < 0xFFFFFFF0) handles.push_back(h);
  }
  return diff.empty();
}
// A thread in NtWaitForAlertByThreadId at the snapshot and again now, but at another depth (another lock or address).
// Every user of that call is ntdll's own wait-on-address, SRW lock and condition variable code, built to take a
// spurious return: it re-checks its condition and waits again. The rewind gives the thread its snapshot context at the
// start of the stub (WOW64 keeps it until the current wait returns) and alerts it by thread id: the current wait
// returns, the thread enters the snapshot's wait with the snapshot's arguments, its wait block is in the restored ntdll
// state, and a waker that sees it there alerts it as before. Only when both return addresses are in the 32-bit ntdll
// and the 64-bit stack pointer is the same (same WOW64 dispatch path). Counted objects (events, semaphores) never.
static bool realert(Sess& S, const TS& o, const TS& n) {
  if (!o.kernel || !n.kernel || o.name != "NtWaitForAlertByThreadId" || n.name != o.name || n.stub != o.stub || n.rsp64 != o.rsp64 || !o.xs.empty()) return false;
  auto inNtdll = [&](DWORD a) { const View* v = viewAt(S.s.views, a); return v && v->type == MEM_IMAGE && baseName(v->file) == "ntdll.dll"; };
  return inNtdll(o.sig[6]) && inNtdll(n.sig[6]);
}
static void planThreads(Sess& S, Result& r, Plan& pl, std::vector<TS>& now) {
  const Snap& s = S.s;
  for (auto& o : s.threads) {
    const TS* n = nullptr; const Th* th = nullptr;
    for (size_t i = 0; i < now.size(); ++i) if (now[i].tid == o.tid) { n = &now[i]; th = &S.frozen[i]; }
    if (!n) {  // never allowed (docs/270 §32): the thread cannot be made again, and the kernel already decommitted its TEB
      // pages in the TEB region, which cannot be committed from outside (error 87). Named by start module, so the log says
      // which worker ended: combase/mswsock workers also keep a user-mode list or count that the memory would put back.
      const View* v = viewAt(s.views, o.start);
      std::string mod = v && v->type == MEM_IMAGE ? baseName(v->file) : "";
      std::string where = mod.empty() ? fmt("%08lx", o.start) : fmt("%s+%lx", mod.c_str(), (unsigned long)(o.start - v->base));
      const char* what = mod == "combase.dll" ? ", COM RPC cache worker (ends after 30 s idle)" : mod == "mswsock.dll" ? ", Winsock async worker (ends after 300 s idle)"
                       : mod == "ntdll.dll" ? ", thread-pool worker" : "";
      r.refuse(fmt("thread %lu (start %s%s) exited after the snapshot", o.tid, where.c_str(), what));
      continue;
    }
    if (n->slots[5] != o.slots[5]) { pl.retry.push_back(fmt("thread %lu is at another user-callback depth than at the snapshot", o.tid)); continue; }
    std::string diff; std::vector<DWORD> hs;
    if (o.parked) {  // never restarted: only left where it is
      if (sameParked(o, *n) && sameWait(S, o, *n, diff, hs)) { pl.leave.push_back({&o, th}); pl.waitOn.push_back({&o, hs}); }
      else { pl.retry.push_back(fmt("thread %lu left the %s wait it was parked in at the snapshot (now %s)", o.tid, o.name.c_str(), describe(*n).c_str())); pl.catchable.push_back(o.tid); }
      continue;
    }
    bool deadRegs = false;
    bool same = o.kernel && n->kernel && sameWait(S, o, *n, diff, hs, &deadRegs);
    if (same) { pl.leave.push_back({&o, th}); pl.waitOn.push_back({&o, hs}); pl.rearmed += memcmp(o.sig, n->sig, sizeof o.sig) != 0; pl.deadRegs += deadRegs; continue; }
    if (realert(S, o, *n)) { pl.alert.push_back({&o, th}); continue; }
    if (!n->kernel && !n->transit) {
      if (!o.kernel && n->slots[0] != o.slots[0]) pl.retry.push_back(fmt("thread %lu is on another 64-bit stack frame than at the snapshot", o.tid));
      else pl.set.push_back({&o, th});
      continue;
    }
    if (shortSleep(S, *n) && o.xs.empty()) { pl.late.push_back({&o, th}); continue; }  // a late context cannot carry the vector state
    std::string where;
    if (o.kernel && n->kernel) {  // which code waits: start and return sites, so the next run names the owner
      auto views = viewsOf(S.p, regions(S.p));
      auto at = [&](DWORD a) { const View* v = viewAt(views, a); return v && v->type == MEM_IMAGE ? fmt("%s+%lx", baseName(v->file).c_str(), (unsigned long)(a - v->base)) : fmt("%08lx", a); };
      where = fmt("; start %s, returns to %s (snapshot %s)", at(o.start).c_str(), at(n->sig[6]).c_str(), at(o.sig[6]).c_str());
    }
    if (o.kernel) pl.catchable.push_back(o.tid);
    pl.retry.push_back(fmt("thread %lu is %s; at the snapshot it was %s%s%s%s", o.tid, describe(*n).c_str(), describe(o).c_str(),
                           diff.empty() ? "" : "; differs:", diff.c_str(), where.c_str()));
  }
  for (size_t i = 0; i < now.size(); ++i) {
    const TS& n = now[i];
    bool known = false;
    for (auto& o : s.threads) known |= o.tid == n.tid;
    if (known) continue;
    auto views = viewsOf(S.p, regions(S.p));  // the module may have been loaded after the snapshot
    const View* v = viewAt(views, n.start);
    std::string where = v && v->type == MEM_IMAGE ? fmt("%s+%lx", baseName(v->file).c_str(), (unsigned long)(n.start - v->base)) : fmt("%08lx", n.start);
    bool idleWorker = n.kernel && n.name == "NtWaitForWorkViaWorkerFactory" && v && baseName(v->file) == "ntdll.dll";
    // Any thread made after the snapshot that sits in a wait-only call: everything it did is in memory (put back to before it
    // existed) or in handles made after the snapshot (closed by the rewind); a mutex it owns or window objects refuse.
    bool waiting = n.kernel && restartable(n.name);
    bool ownsMutex = false;
    for (auto& h : s.handles) if (h.kind == L"Mutant") { HRec q = h; syncState(q); ownsMutex |= q.owner == n.tid; }
    if ((idleWorker || waiting) && !ownsMutex && S.o.reapWorkers) pl.reap.push_back(&S.frozen[i]);
    else if (idleWorker && !ownsMutex) r.refuse(fmt("thread %lu is a pool worker made after the snapshot (--reap-workers ends idle ones)", n.tid));
    else if (waiting && !ownsMutex) r.refuse(fmt("thread %lu (start %s, %s) was made after the snapshot (--reap-workers ends threads in a wait)", n.tid, where.c_str(), describe(n).c_str()));
    else if (n.kernel && n.name == "NtWaitForWorkViaWorkerFactory") pl.retry.push_back(fmt("pool worker %lu made after the snapshot is not idle and clean", n.tid));
    else r.refuse(fmt("thread %lu (start %s, %s) was made after the snapshot", n.tid, where.c_str(), describe(n).c_str()));
  }
}

// "Kind \name" of a pinned handle, for refusal and verify messages (a file name through fileOp: a query can block)
static std::string handleWhat(const HRec& h) {
  std::wstring n;
  if (h.kind == L"File") { LONGLONG pos = 0; fileOp(h.pin, 2, pos, &n); }
  else if (h.pin) {
    unsigned char b[1200]{}; UNICODE_STRING* u = (UNICODE_STRING*)b;
    if (NtQueryObject_(h.pin, 1, b, sizeof b - 2, nullptr) >= 0 && u->Buffer) n.assign(u->Buffer, u->Length / 2);
  }
  std::string k, a;  // names are ASCII in practice; enough for a log line
  for (wchar_t c : h.kind) k += (char)(c < 128 ? c : '?');
  for (wchar_t c : n) a += (char)(c < 128 ? c : '?');
  return (k.empty() ? fmt("type %lu", h.type) : k) + (a.empty() ? "" : " " + a);
}
// What verify will check on a reinserted handle (type, access, object), tried before any change: one duplicate of the pin
// into the target, read back, closed again. A rewind applied without this failed verify on the one reinserted handle and
// ended the game (rewind-games-smoke-1116-1116c, handle 7a8/7ac, docs/270 §35). '' = it comes back as the snapshot's.
static std::string reinsertProbe(Sess& S, const HRec& h) {
  HANDLE got = nullptr;
  if (!DuplicateHandle(GetCurrentProcess(), h.pin, S.p, &got, 0, (h.attrs & 2) != 0, DUPLICATE_SAME_ACCESS)) return fmt("cannot be duplicated back (error %lu)", GetLastError());
  std::string why = "its duplicate is not in the handle list";
  for (auto& e : handleList(S.p))
    if (e.value == (ULONG_PTR)got)
      why = e.type != h.type || e.access != h.access ? fmt("comes back as type %lu access %lx (snapshot type %lu access %lx)", e.type, e.access, h.type, h.access)
          : !sameObject(S.p, e.value, h.pin) ? "its duplicate is not the snapshot's object" : "";
  closeIn(S.p, (ULONG_PTR)got);
  return why;
}
// Type and object name of a handle the target made after the snapshot (pinHandles' type lookup, then handleWhat), so a
// refusal names it (docs/270 §36: 654/1c1c were only numbers)
static std::string newHandleWhat(Sess& S, const HEnt& e) {
  HRec h{e.value, e.access, e.attrs, e.type};
  if (DuplicateHandle(S.p, (HANDLE)e.value, GetCurrentProcess(), &h.pin, 0, FALSE, DUPLICATE_SAME_ACCESS) && !S.kinds.count(e.type)) {
    unsigned char b[1024]{};
    if (NtQueryObject_(h.pin, 2, b, sizeof b, nullptr) >= 0) { UNICODE_STRING* u = (UNICODE_STRING*)b; S.kinds[e.type] = std::wstring(u->Buffer, u->Length / 2); }
  }
  if (S.kinds.count(e.type)) h.kind = S.kinds[e.type];
  std::string w = handleWhat(h);
  if (h.pin) CloseHandle(h.pin);
  return w;
}
static void planRest(Sess& S, Result& r, Plan& pl, const std::vector<Reg>& now) {
  const Snap& s = S.s;
  // handles
  std::map<ULONG_PTR, HEnt> cur;
  for (auto& e : handleList(S.p)) cur[e.value] = e;
  std::set<ULONG_PTR> kept;
  for (auto& h : s.handles) {
    auto it = cur.find(h.value);
    bool same = it != cur.end() && it->second.type == h.type && it->second.access == h.access && (!h.pin || sameObject(S.p, h.value, h.pin));
    if (same) { kept.insert(h.value); if (!h.pin) r.add("handlesUnverified", 1); continue; }
    if (!h.pin) { r.refuse(fmt("handle %zx (type %lu, cannot be duplicated) was closed after the snapshot", (size_t)h.value, h.type)); continue; }
    std::string why = reinsertProbe(S, h);
    if (why.empty()) pl.reinsert.push_back(&h);
    else r.refuse(fmt("handle %zx (%s) was closed or replaced after the snapshot and would not come back as the snapshot's object: %s", (size_t)h.value, handleWhat(h).c_str(), why.c_str()));
  }
  for (auto& [o, hs] : pl.waitOn)
    for (DWORD h : hs)
      if (!kept.count(h)) r.refuse(fmt("thread %lu %s in %s on handle %lx, which is no longer the snapshot's object", o->tid, o->parked ? "is parked" : "waits", o->name.c_str(), h));
  for (auto& [v, e] : cur) {
    if (kept.count(v)) continue;
    if (e.attrs & 1) r.refuse(fmt("handle %zx (%s) made after the snapshot is protected from close (window-system state of a thread)", (size_t)v, newHandleWhat(S, e).c_str()));
    pl.closeHandles.push_back(v);
  }
  for (auto& h : s.handles) {
    if (h.kind != L"Mutant" || !h.syncOk) continue;
    HRec q = h; syncState(q);
    if (q.owner != h.owner || q.syncA != h.syncA) r.refuse(fmt("mutex %zx changed owner (thread %lu, was %lu): ownership cannot be put back from outside", (size_t)h.value, q.owner, h.owner));
  }
  // views
  auto nowViews = viewsOf(S.p, now);
  std::set<ULONG_PTR> keptViews;
  for (auto& v : s.views) {
    const View* n = nullptr;
    for (auto& w : nowViews) if (w.base == v.base) n = &w;
    bool same = n && n->size == v.size && n->type == v.type && normProt(n->aprot) == normProt(v.aprot) && n->file == v.file;
    if (same && v.sec >= 0 && !stillSame(S, v)) same = false;  // same place, other memory (or no longer provable): map it again
    if (same) { keptViews.insert(v.base); if (v.sec < 0 && v.type == MEM_MAPPED && writable(v.aprot)) r.add("viewsUnverified", 1); continue; }
    if (v.sec >= 0) pl.remap.push_back(&v);
    else if (v.type == MEM_MAPPED && v.anon && S.o.recreateViews) pl.recreate.push_back(&v);
    else r.refuse(fmt("%s %p (+%zx) was unmapped after the snapshot and no section handle is left to map it again%s", v.type == MEM_IMAGE ? "module" : "view",
                      (void*)v.base, (size_t)v.size, v.anon ? " (--recreate-views makes a fresh one)" : ""));
  }
  for (auto& w : nowViews) if (!keptViews.count(w.base)) pl.unmap.push_back(w.base);
  // private allocations: base -> end
  std::map<ULONG_PTR, ULONG_PTR> was, is;
  for (auto& g : s.regs) if (g.type == MEM_PRIVATE) was[g.abase] = g.base + g.size;
  for (auto& g : now) if (g.type == MEM_PRIVATE) is[g.abase] = g.base + g.size;
  for (auto& [b, e] : is) if (!was.count(b) || was[b] != e) pl.freeAllocs.push_back(b);
  for (auto& [b, e] : was) if (!is.count(b) || is[b] != e) pl.reserve.push_back({b, e});
  // pages committed inside a kept view cannot be taken back
  for (auto& g : s.regs)
    if (g.type != MEM_PRIVATE && g.state == MEM_RESERVE && keptViews.count(g.abase))
      for (auto& c : now) {
        if (c.state != MEM_COMMIT || c.base >= g.base + g.size || g.base >= c.base + c.size) continue;
        if (auto sv = s.sysViews.find(g.abase); sv != s.sysViews.end()) {
          r.add("viewsSystemGrew", 1);
          r.notes.push_back(fmt("pages at %p were committed after the snapshot in the window system's %s (read-only view %p, shared by every GUI "
                                "process on the desktop): not the target's memory, left alone", (void*)c.base, sv->second.c_str(), (void*)g.abase));
        } else {
          const View* vw = viewAt(s.views, g.abase);
          r.refuse(fmt("pages at %p were committed inside a mapped view after the snapshot (view %p +%zx, protection %lx, %ls)", (void*)c.base,
                       (void*)g.abase, vw ? (size_t)vw->size : 0, vw ? vw->aprot : 0, vw && !vw->file.empty() ? vw->file.c_str() : L"no file"));
        }
        break;
      }
  for (auto& v : nowViews)
    if (v.type == MEM_IMAGE && hasAny(baseName(v.file), S.o.forbid)) r.refuse("module " + baseName(v.file) + " is loaded (detach the probe first)");
  DWORD gdi = GetGuiResources(S.p, GR_GDIOBJECTS), user = GetGuiResources(S.p, GR_USEROBJECTS);
  r.add("gdiDelta", (double)gdi - s.gdi); r.add("userDelta", (double)user - s.user);
  if ((gdi != s.gdi || user != s.user) && !S.o.allowGui) r.refuse(fmt("window-system objects changed (GDI %lu -> %lu, USER %lu -> %lu): they are not rewound", s.gdi, gdi, s.user, user));
}

// Puts the wanted handle values back into the target: duplicate until the wanted value comes up, close the rest.
static bool reinsert(Sess& S, const std::vector<const HRec*>& want) {
  std::map<ULONG_PTR, const HRec*> open;
  for (auto* h : want) open[h->value] = h;
  std::vector<ULONG_PTR> junk;
  for (int tries = 0; !open.empty() && tries < 200000; ++tries) {
    const HRec* h = open.begin()->second;
    HANDLE got = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), h->pin, S.p, &got, 0, (h->attrs & 2) != 0, DUPLICATE_SAME_ACCESS)) break;
    auto it = open.find((ULONG_PTR)got);
    if (it == open.end()) { junk.push_back((ULONG_PTR)got); continue; }
    if (it->second != h) {  // a wanted value, but for another object: swap the right one in
      closeIn(S.p, (ULONG_PTR)got);
      HANDLE again = nullptr;
      DuplicateHandle(GetCurrentProcess(), it->second->pin, S.p, &again, 0, (it->second->attrs & 2) != 0, DUPLICATE_SAME_ACCESS);
      if (again != got) { junk.push_back((ULONG_PTR)again); continue; }
    }
    open.erase(it);
  }
  for (ULONG_PTR j : junk) closeIn(S.p, j);
  return open.empty();
}

static bool restoreMemory(Sess& S, Result& r, std::string& err) {
  const Snap& s = S.s;
  std::vector<unsigned char> buf;
  auto now = regions(S.p);
  size_t ni = 0;
  for (auto& g : s.regs) {
    if (s.sysViews.count(g.abase)) continue;  // the window system's read-only view: win32k commits and decommits there, never restored
    // what is at this range now
    bool anyCommit = false, anyOther = false, uniform = true; DWORD p0 = 0; bool first = true;
    while (ni < now.size() && now[ni].base + now[ni].size <= g.base) ++ni;
    ULONG_PTR covered = g.base;
    for (size_t i = ni; i < now.size() && now[i].base < g.base + g.size; ++i) {
      if (now[i].base > covered) anyOther = true;
      covered = now[i].base + now[i].size;
      if (now[i].state == MEM_COMMIT) { anyCommit = true; if (first) { p0 = now[i].prot; first = false; } else if (now[i].prot != p0) uniform = false; }
      else anyOther = true;
    }
    if (covered < g.base + g.size) anyOther = true;
    if (g.state == MEM_RESERVE) {
      if (anyCommit) {
        if (g.type != MEM_PRIVATE || !VirtualFreeEx(S.p, (void*)g.base, g.size, MEM_DECOMMIT)) { err = fmt("decommit %p: error %lu", (void*)g.base, GetLastError()); return false; }
        r.add("decommitted", 1);
      }
      continue;
    }
    DWORD rw = rwFor(g.prot);
    bool tmp = false;
    if (anyOther) {
      if (!VirtualAllocEx(S.p, (void*)g.base, g.size, MEM_COMMIT, g.type == MEM_PRIVATE ? rw : normProt(g.prot) & 0xFF)) { err = fmt("commit %p (+%zx): error %lu", (void*)g.base, (size_t)g.size, GetLastError()); return false; }
      r.add("recommitted", 1); uniform = false;
    }
    if (!g.copied || foreignAt(s, g.base)) {  // a view shared with another process is left as it is (docs/270 §20)
      DWORD old;
      if ((!uniform || p0 != g.prot) && g.base != 0x7FFE0000 && !VirtualProtectEx(S.p, (void*)g.base, g.size, g.prot, &old)) { err = fmt("protect %p: error %lu", (void*)g.base, GetLastError()); return false; }
      continue;
    }
    DWORD old, cur = p0;
    if (!uniform || (p0 & PAGE_GUARD) || (p0 & 0xFF) == PAGE_NOACCESS) {
      if (VirtualProtectEx(S.p, (void*)g.base, g.size, rw, &old)) cur = rw;
      else if (VirtualProtectEx(S.p, (void*)g.base, g.size, PAGE_READONLY, &old)) cur = PAGE_READONLY;
      else { err = fmt("protect %p for reading: error %lu", (void*)g.base, GetLastError()); return false; }
      tmp = true;
    }
    const size_t CH = 8 << 20;
    bool wrote = false;
    for (size_t at = 0; at < g.size; at += CH) {
      size_t n = std::min(CH, (size_t)g.size - at);
      buf.resize(n);
      SIZE_T got = 0;
      if (!ReadProcessMemory(S.p, (void*)(g.base + at), buf.data(), n, &got) || got != n) { err = fmt("read %p: error %lu", (void*)(g.base + at), GetLastError()); return false; }
      const unsigned char* want = s.store + g.off + at;
      for (size_t pg = 0; pg < n;) {
        if (!memcmp(buf.data() + pg, want + pg, 4096)) { pg += 4096; continue; }
        size_t end = pg + 4096;
        while (end < n && memcmp(buf.data() + end, want + end, 4096)) end += 4096;
        if (!writable(cur)) {
          if (!VirtualProtectEx(S.p, (void*)g.base, g.size, rw, &old)) { err = fmt("protect %p for writing: error %lu", (void*)g.base, GetLastError()); return false; }
          cur = rw; tmp = true;
        }
        if (!wvm(S.p, g.base + at + pg, want + pg, end - pg)) { err = fmt("write %p (+%zx) failed", (void*)(g.base + at + pg), end - pg); return false; }
        r.add("pagesWritten", (double)((end - pg) / 4096));
        wrote = true;
        pg = end;
      }
    }
    if ((tmp || p0 != g.prot) && !VirtualProtectEx(S.p, (void*)g.base, g.size, g.prot, &old)) { err = fmt("protect %p back to %lx: error %lu", (void*)g.base, g.prot, GetLastError()); return false; }
    if (wrote && (g.prot & 0xF0)) { FlushInstructionCache(S.p, (void*)g.base, g.size); r.add("codeRegionsFlushed", 1); }  // PAGE_EXECUTE*
  }
  return true;
}

// The bridge's rewind handshake events (patch 0015: <id>RewindDone, <id>RewindApplied, <id>RewindResume) belong to
// the op the server just ran, not to the snapshot. Putting RewindApplied back to its snapshot state (unset) after the
// server set it hid the rewind from the client: it never sent RewindResume, the server stopped reading commands for
// 60 s and the client stopped presenting (rerun 8, bridge64.log; docs/270 §27).
// The bridge names its events <base>_<guid> (util_guid.h toString), under ...\BaseNamedObjects\.
static bool bridgeHandshakeName(const std::wstring& full) {
  std::wstring n = full.substr(full.find_last_of(L'\\') + 1);
  for (const wchar_t* k : {L"RewindDone_", L"RewindApplied_", L"RewindResume_"}) {
    size_t kl = wcslen(k);
    if (n.size() != kl + 36 || n.compare(0, kl, k) != 0) continue;
    bool g = true;
    for (size_t i = 0; i < 36; i++) { wchar_t c = n[kl + i]; g &= (i == 8 || i == 13 || i == 18 || i == 23) ? c == L'-' : iswxdigit(c) != 0; }
    if (g) return true;
  }
  return false;
}
static bool bridgeHandshake(HANDLE h) {
  unsigned char nb[1024]{};
  UNICODE_STRING* u = (UNICODE_STRING*)nb;
  if (NtQueryObject_(h, 1, nb, sizeof nb, nullptr) < 0 || !u->Buffer) return false;
  return bridgeHandshakeName(std::wstring(u->Buffer, u->Length / 2));
}
static bool verify(Sess& S, Result& r, const Plan& pl, std::string& err) {
  const Snap& s = S.s;
  auto mine = [&](std::vector<Reg> v) { v.erase(std::remove_if(v.begin(), v.end(), [&](const Reg& g) { return s.sysViews.count(g.abase) > 0; }), v.end()); return v; };  // the window system's views change on their own
  if (!sameMap(mine(s.regs), mine(regions(S.p)), err)) return false;
  std::vector<unsigned char> buf;
  uint64_t h = 1;
  for (auto& g : s.regs) {
    if (!g.copied) continue;
    if (foreignAt(s, g.base)) { h = hash64(s.store + g.off, g.size, h); continue; }  // left alone: the other process may write it
    buf.resize(g.size);
    if (!readMem(S.p, g.base, g.size, g.prot, buf.data())) { err = fmt("verify: cannot read %p", (void*)g.base); return false; }
    if (memcmp(buf.data(), s.store + g.off, g.size)) { err = fmt("verify: memory at %p (+%zx) differs from the snapshot", (void*)g.base, (size_t)g.size); return false; }
    h = hash64(buf.data(), g.size, h);
  }
  if (h != s.hash) { err = "verify: memory hash differs from the snapshot"; return false; }
  r.add("hashEqual", 1);
  // handles: the same values, and the same objects where we hold a pin
  auto cur = handleList(S.p);
  if (cur.size() != s.handles.size()) { err = fmt("verify: %zu handles, %zu at the snapshot", cur.size(), s.handles.size()); return false; }
  std::set<ULONG_PTR> back;
  for (auto* rh : pl.reinsert) back.insert(rh->value);
  for (auto& hr : s.handles) {
    const HEnt* at = nullptr;
    for (auto& e : cur) if (e.value == hr.value) at = &e;
    std::string bad = !at ? "no handle at that value" : at->type != hr.type || at->access != hr.access ? fmt("type %lu access %lx, snapshot type %lu access %lx", at->type, at->access, hr.type, hr.access)
                    : hr.pin && !sameObject(S.p, hr.value, hr.pin) ? "another object" : "";
    if (!bad.empty()) { err = fmt("verify: handle %zx (%s, %s) is not the snapshot's object: %s", (size_t)hr.value, handleWhat(hr).c_str(), back.count(hr.value) ? "reinserted" : "kept by the plan", bad.c_str()); return false; }
    if (hr.disk && !(hr.access & (FILE_WRITE_DATA | FILE_APPEND_DATA))) {
      LONGLONG pos = 0;
      if (!fileOp(hr.pin, 0, pos) || pos != hr.pos) { err = fmt("verify: file handle %zx is at %lld, snapshot %lld", (size_t)hr.value, pos, hr.pos); return false; }
    }
    if (hr.syncOk) { HRec q = hr; syncState(q); if (!q.syncOk || ((q.syncA != hr.syncA || q.owner != hr.owner) && !(hr.kind == L"Event" && bridgeHandshake(hr.pin)))) { err = fmt("verify: state of %ls %zx differs from the snapshot", hr.kind.c_str(), (size_t)hr.value); return false; } }
  }
  // threads: the same set, and each context as planned
  auto ids = threadHandles(S.p);
  size_t live = 0;
  for (HANDLE th : ids) { live += WaitForSingleObject(th, 0) == WAIT_TIMEOUT; CloseHandle(th); }
  if (live != s.threads.size()) { err = fmt("verify: %zu threads, %zu at the snapshot", live, s.threads.size()); return false; }
  for (auto& [o, th] : pl.set) {
    WOW64_CONTEXT c{}; c.ContextFlags = WOW64_CONTEXT_ALL;
    WOW64_CONTEXT want = o->ctx;
    if (o->kernel) want.Eip = o->stub;
    if (!Wow64GetThreadContext(th->h, &c) || c.Eip != want.Eip || c.Esp != want.Esp || c.Ebp != want.Ebp || c.Ebx != want.Ebx || c.Esi != want.Esi || c.Edi != want.Edi ||
        (!o->kernel && (c.Eax != want.Eax || c.Ecx != want.Ecx || c.Edx != want.Edx || ((c.EFlags ^ want.EFlags) & 0xCD5) ||
                        memcmp(c.ExtendedRegisters + 160, want.ExtendedRegisters + 160, 128) || memcmp(c.ExtendedRegisters + 24, want.ExtendedRegisters + 24, 4)))) {
      err = fmt("verify: context of thread %lu differs from the snapshot", o->tid); return false;
    }
    if (!xstate(th->h, *o, false)) { err = fmt("verify: vector state of thread %lu differs from the snapshot", o->tid); return false; }
  }
  return true;
}

// Catch (docs/270 §18): a busy thread that is rarely in its snapshot wait is seldom there at the moment all threads stop.
// Poll it alone (suspend, compare, resume) until it sits in that wait, then keep it suspended while the next freeze takes
// the rest. Nothing in the target changes: a thread suspended in a wait is what freeze() makes too, and the same sameWait
// checks decide. On a miss within the budget every held thread is resumed and the plain retries go on.
static bool catchWaits(Sess& S, const std::vector<DWORD>& tids, DWORD budget, Result& r) {
  LARGE_INTEGER f, t0, t; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
  auto ms = [&] { QueryPerformanceCounter(&t); return (double)(t.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart; };
  int polls = 0;
  bool all = true;
  for (DWORD tid : tids) {
    const TS* o = nullptr;
    for (auto& x : S.s.threads) if (x.tid == tid) o = &x;
    HANDLE h = o ? OpenThread(THREAD_ALL_ACCESS, FALSE, tid) : nullptr;
    bool got = false;
    while (h && !got && ms() < budget) {
      DWORD prev = Wow64SuspendThread(h);
      if (prev == (DWORD)-1) break;
      ++polls;
      TS n; std::string diff; std::vector<DWORD> hs; bool dead = false;
      if (classify(S, Th{tid, h, prev}, n) && n.kernel && n.slots[5] == o->slots[5]) {
        auto it = S.stubs.find(n.stub); if (it != S.stubs.end()) n.name = it->second;
        got = o->parked ? sameParked(*o, n) && sameWait(S, *o, n, diff, hs) : sameWait(S, *o, n, diff, hs, &dead);
      }
      if (got) break;
      ResumeThread(h);
      double until = ms() + 0.05;  // ~50 us between polls: short enough for a wait the thread sits in for a fraction of a ms
      while (ms() < until) YieldProcessor();
    }
    if (!got) { if (h) CloseHandle(h); all = false; break; }
    S.hold[tid] = h;
    if (guard->n < 16384) { guard->tids[guard->n] = tid; guard->n = guard->n + 1; }
    guard->phase = 1;  // a host crash resumes the caught threads
  }
  r.add("catchPolls", polls); r.add("catchMs", ms());
  if (all) { r.add("threadsCaught", (double)tids.size()); return true; }
  releaseHolds(S); guard->n = 0; guard->phase = 0;
  return false;
}

// Views shared with another process are not put back (docs/270 §20): the rewind goes on only while their bytes still equal
// the snapshot's, so the rewound target finds them as it left them. Checked before any change; plan-only has no copy.
static void foreignCheck(Sess& S, Result& r) {
  const Snap& s = S.s;
  std::vector<unsigned char> buf;
  for (auto& g : s.regs) {
    const auto* f = foreignAt(s, g.base);
    if (!f || !g.copied) continue;
    if (!s.store) { r.add("viewsForeignUnchecked", 1); continue; }
    buf.resize(g.size);
    if (!readMem(S.p, g.base, g.size, g.prot, buf.data()) || memcmp(buf.data(), s.store + g.off, g.size)) {
      // the audio engine's buffer (samples and stream positions the sound stack and audiodg share; no game state):
      // under --allow-audio it goes on as the engine left it, at worst a click (docs/270 §23)
      if (S.o.allowAudio && s.audioViews.count(std::prev(s.foreign.upper_bound(g.base))->first)) { r.add("viewsAudioMoved", 1); continue; }
      r.refuse("a writable view shared with another process changed since the snapshot and is not put back: " + f->second);
    } else r.add("viewsForeignSame", 1);
  }
}

static Result rewind(Sess& S, bool planOnly, uint32_t runTicks = 0) {
  Result r; r.op = planOnly ? "plan" : "rewind";
  Snap& s = S.s;
  Lap lap{r};
  if (!s.have) { r.refuse("no snapshot"); return r; }
  if (!planOnly && !s.copied) { r.refuse("the snapshot was taken with --plan-only: it has no memory copy"); return r; }
  if (!park(S, r)) return r;
  if (S.bridge && !bridgeOp(S, r, 3)) { unpark(S); return r; }  // the server only reports: nothing changed yet
  lap("park");
  Plan pl;
  std::vector<TS> now;
  std::vector<Reg> nowRegs;
  double catchSpent = 0;
  for (int pass = 0;; ++pass) {  // under the bridge the plan runs twice: before and after the server went back
  for (int attempt = 0;; ++attempt) {
    pl = Plan{}; now.clear();
    Result t;
    if (!freeze(S)) { thaw(S); unpark(S); r.refuse("cannot suspend the target"); return r; }
    bool ok = true;
    for (auto& th : S.frozen) { TS ts; ok &= classify(S, th, ts); now.push_back(ts); }
    for (auto& ts : now) if (ts.kernel) { auto it = S.stubs.find(ts.stub); if (it != S.stubs.end()) ts.name = it->second; }
    if (!ok) pl.retry.push_back("a thread gave no context");
    planThreads(S, t, pl, now);
    bool caughtOut = S.o.catchMs && catchSpent >= S.o.catchMs && !pl.catchable.empty() && pl.catchable.size() == pl.retry.size();
    if (pl.retry.empty() || attempt >= S.o.retries || t.code || caughtOut) {  // caughtOut: the catch already polled for its budget
      for (auto& w : t.why) r.refuse(w);
      for (auto& w : pl.retry) r.refuse(w);
      r.add("freezeAttempts", attempt + 1);
      break;
    }
    thaw(S);
    // only busy threads not in their snapshot wait stand in the way: catch them one by one, then freeze the rest around them
    if (S.o.catchMs && catchSpent < S.o.catchMs && !pl.catchable.empty() && pl.catchable.size() == pl.retry.size()) {
      Result c;
      bool caught = catchWaits(S, pl.catchable, (DWORD)(S.o.catchMs - catchSpent), c);
      catchSpent += c.stat("catchMs");
      for (auto& [k, v] : c.stats) r.add(k.c_str(), v);
      if (caught) continue;
    }
    Sleep(5 + attempt);
  }
  lap("freeze");
  nowRegs = regions(S.p);
  planRest(S, r, pl, nowRegs);
  if (pass == 0) foreignCheck(S, r);
  if (!planOnly && S.o.wwRedirty && !r.code) wwProbe(S, r);
  lap("plan");
  if (pass == 1) {
    if (r.code) { dirty(S, r, "the bridge server went back to the snapshot, then the plan for the game process refused"); return r; }
    break;
  }
  r.add("threadsSet", (double)pl.set.size()); r.add("threadsLeft", (double)pl.leave.size()); r.add("threadsSetLate", (double)pl.late.size()); r.add("workersReaped", (double)pl.reap.size()); r.add("threadsRearmed", pl.rearmed); r.add("threadsDeadRegs", pl.deadRegs); r.add("threadsRealerted", (double)pl.alert.size());
  r.add("handlesClosed", (double)pl.closeHandles.size()); r.add("handlesReinserted", (double)pl.reinsert.size());
  r.add("viewsUnmapped", (double)pl.unmap.size()); r.add("viewsRemapped", (double)pl.remap.size()); r.add("viewsRecreated", (double)pl.recreate.size());
  r.add("allocsFreed", (double)pl.freeAllocs.size()); r.add("allocsReserved", (double)pl.reserve.size());
  for (auto& [o, th] : pl.set) r.add("threadsRestarted", o->kernel);
  if (r.code || planOnly) { thaw(S); unpark(S); return r; }
  if (!S.bridge) break;
  // The bridge client must run to send the rewind op: resume (its thread stays parked), let the server go back, plan again.
  thaw(S);
  if (!bridgeOp(S, r, 2)) {
    if (rd<int32_t>(S.p, COOP(bridgeResult)) == (int32_t)0x80004005 || rd<int32_t>(S.p, COOP(bridgeResult)) == (int32_t)0x80004001) { unpark(S); return r; }  // refused before any change
    if (!freeze(S)) thaw(S);
    dirty(S, r, "no clear answer to the rewind op: the bridge server state is unknown"); return r;
  }
  }

  // Threads in an alert wait at another depth (realert): before any change, give each its snapshot context at the stub
  // (WOW64 keeps it until the current wait returns) and have a short helper thread inside the target call the 32-bit
  // NtAlertThreadByThreadId(tid) (from another process it is refused with STATUS_ACCESS_DENIED, verified on the toy; a
  // special user APC does not end the wait, verified too). The helper starts without DLL thread-attach calls and ends at
  // once; the plan of handles, views and allocations is then made again, so its traces are put back with the rest. If
  // the helper fails, the contexts are put back and the rewind is refused (exit 2): the alerted thread then sees one
  // spurious return, which ntdll's alert waits re-check by design.
  if (!pl.alert.empty()) {
    DWORD alert32 = 0;
    for (auto& [a, n] : S.stubs) if (n == "NtAlertThreadByThreadId") alert32 = a;
    std::string why;
    for (auto& [o, th] : pl.alert) {
      WOW64_CONTEXT c = o->ctx;
      c.Eip = o->stub;
      if (!Wow64SetThreadContext(th->h, &c)) { why = fmt("cannot set the context of alert-waiting thread %lu", o->tid); break; }
      HANDLE h = nullptr;
      LONG st = alert32 ? NtCreateThreadEx_(&h, THREAD_ALL_ACCESS, nullptr, S.p, (PVOID)(ULONG_PTR)alert32, (PVOID)(ULONG_PTR)o->tid, 0x6, 0, 0, 0, nullptr) : -1;
      DWORD code = 1;
      if (st >= 0 && WaitForSingleObject(h, 2000) == WAIT_OBJECT_0) GetExitCodeThread(h, &code);
      else if (st >= 0) TerminateThread(h, 1);
      if (h) CloseHandle(h);
      if (code != 0) { why = fmt("the helper that wakes alert-waiting thread %lu failed (status %lx, exit %lx)", o->tid, st, code); break; }
    }
    if (!why.empty()) {
      for (auto& [o, th] : pl.alert) for (auto& n : now) if (n.tid == o->tid) Wow64SetThreadContext(th->h, &n.ctx);
      r.refuse(why); thaw(S); unpark(S); return r;
    }
    pl.closeHandles.clear(); pl.unmap.clear(); pl.freeAllocs.clear(); pl.reinsert.clear(); pl.remap.clear(); pl.recreate.clear(); pl.reserve.clear(); pl.waitOn.clear();
    for (const char* k : {"handlesUnverified", "viewsUnverified", "gdiDelta", "userDelta"})  // counted again by the new plan
      r.stats.erase(std::remove_if(r.stats.begin(), r.stats.end(), [&](auto& x) { return x.first == k; }), r.stats.end());
    nowRegs = regions(S.p);
    planRest(S, r, pl, nowRegs);
    if (r.code) { for (auto& [o, th] : pl.alert) for (auto& n : now) if (n.tid == o->tid) Wow64SetThreadContext(th->h, &n.ctx); thaw(S); unpark(S); return r; }
  }
  // ---- from here the target is changed: any failure ends it
  guard->phase = 2;
  std::string err;
  for (auto* th : pl.reap) {
    if (!TerminateThread(th->h, 0) || WaitForSingleObject(th->h, 3000) != WAIT_OBJECT_0) { dirty(S, r, fmt("cannot end pool worker %lu", th->tid)); return r; }
  }
  for (ULONG_PTR v : pl.closeHandles) if (!closeIn(S.p, v)) { dirty(S, r, fmt("cannot close handle %zx: error %lu", (size_t)v, GetLastError())); return r; }
  if (!reinsert(S, pl.reinsert)) { dirty(S, r, "cannot put a closed handle back at its value"); return r; }
  lap("handles");
  for (ULONG_PTR b : pl.unmap) if (NtUnmapViewOfSection_(S.p, (void*)b) < 0) { dirty(S, r, fmt("cannot unmap view %p", (void*)b)); return r; }
  for (ULONG_PTR b : pl.freeAllocs) {
    MEMORY_BASIC_INFORMATION m{};
    VirtualQueryEx(S.p, (void*)b, &m, sizeof m);
    if (m.State == MEM_FREE || (ULONG_PTR)m.AllocationBase != b) continue;  // gone with a reaped thread (its stack)
    if (!VirtualFreeEx(S.p, (void*)b, 0, MEM_RELEASE)) { dirty(S, r, fmt("cannot free %p: error %lu", (void*)b, GetLastError())); return r; }
  }
  for (auto& [b, e] : pl.reserve) {
    DWORD ap = PAGE_READWRITE;
    for (auto& g : s.regs) if (g.abase == b) { ap = g.aprot; break; }
    if (VirtualAllocEx(S.p, (void*)b, e - b, MEM_RESERVE | (s.ww.count(b) ? MEM_WRITE_WATCH : 0), ap) != (void*)b) { dirty(S, r, fmt("cannot reserve %p (+%zx) again: error %lu", (void*)b, (size_t)(e - b), GetLastError())); return r; }
  }
  for (auto* v : pl.remap) {
    PVOID base = (PVOID)v->base; SIZE_T size = v->size; LARGE_INTEGER off; off.QuadPart = v->off;
    LONG st = NtMapViewOfSection_(s.handles[v->sec].strong, S.p, &base, 0, 0, &off, &size, 2, 0, v->aprot);
    if (st < 0 || (ULONG_PTR)base != v->base) { dirty(S, r, fmt("cannot map view %p again: status %lx", (void*)v->base, st)); return r; }
  }
  for (auto* v : pl.recreate) {
    bool reserve = false;
    for (auto& g : s.regs) reserve |= g.abase == v->base && g.state == MEM_RESERVE;
    HANDLE sec = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE | (reserve ? SEC_RESERVE : 0), (DWORD)((ULONG64)v->size >> 32), (DWORD)v->size, nullptr);
    PVOID base = (PVOID)v->base; SIZE_T size = v->size;
    LONG st = sec ? NtMapViewOfSection_(sec, S.p, &base, 0, 0, nullptr, &size, 2, 0, v->aprot) : -1;
    if (sec) CloseHandle(sec);
    if (st < 0 || (ULONG_PTR)base != v->base) { dirty(S, r, fmt("cannot make view %p again: status %lx", (void*)v->base, st)); return r; }
  }
  lap("map");
  if (!restoreMemory(S, r, err)) { dirty(S, r, err); return r; }
  lap("memory");
  // the GC may have read and reset dirty bits after the snapshot: make every write-watch page dirty (a superset)
  for (auto& [b, e] : s.ww) {
    if (!S.o.wwRedirty) break;
    long pages = wwTouch(S.p, b, e), got = wwDirty(S.p, b, e - b);
    if (pages < 0 || got != pages) { dirty(S, r, fmt("write-watch allocation %p: %ld of %ld committed pages report dirty after the rewind", (void*)b, got, pages)); return r; }
    r.add("writeWatchPages", (double)pages);
  }
  lap("writeWatch");
  for (auto& h : s.handles) {
    if (h.disk && !(h.access & (FILE_WRITE_DATA | FILE_APPEND_DATA))) { LONGLONG pos = h.pos; fileOp(h.pin, 1, pos); }
    if (!h.syncOk || !h.strong) continue;
    HRec q = h; syncState(q);
    if (h.kind == L"Event" && q.syncA != h.syncA && bridgeHandshake(h.pin)) { r.add("bridgeEventsKept", 1); continue; }
    if (h.kind == L"Event" && q.syncA != h.syncA) { h.syncA ? SetEvent(h.strong) : ResetEvent(h.strong); r.add("eventsReset", 1); }
    if (h.kind == L"Semaphore" && q.syncA != h.syncA) {
      for (LONG i = q.syncA; i > h.syncA; --i) WaitForSingleObject(h.strong, 0);
      if (h.syncA > q.syncA) ReleaseSemaphore(h.strong, h.syncA - q.syncA, nullptr);
      r.add("semaphoresReset", 1);
    }
  }
  for (auto& [o, th] : pl.set) {
    WOW64_CONTEXT c = o->ctx;
    if (o->kernel) c.Eip = o->stub;  // back into the wait it was in
    if (!Wow64SetThreadContext(th->h, &c)) { dirty(S, r, fmt("cannot set the context of thread %lu: error %lu", o->tid, GetLastError())); return r; }
    if (!xstate(th->h, *o, true)) { dirty(S, r, fmt("cannot set the vector state of thread %lu: error %lu", o->tid, GetLastError())); return r; }
  }
  for (auto& th : S.frozen)
    for (auto& o : s.threads)
      if (o.tid == th.tid) {
        for (DWORD c = th.prev; c < o.prevSuspend; ++c) SuspendThread(th.h);
        for (DWORD c = th.prev; c > o.prevSuspend; --c) ResumeThread(th.h);
        if (GetThreadPriority(th.h) != o.prio) SetThreadPriority(th.h, o.prio);
      }
  FlushInstructionCache(S.p, nullptr, 0);
  lap("threads");
  if (!verify(S, r, pl, err)) { dirty(S, r, err); return r; }
  lap("verify");
  // after the check: the few words that must differ from the snapshot
  for (auto& [o, th] : pl.late) {  // a thread in a short Sleep takes the snapshot context when the Sleep returns (WOW64 keeps it in memory until then)
    WOW64_CONTEXT c = o->ctx, got{};
    got.ContextFlags = WOW64_CONTEXT_CONTROL;
    if (o->kernel) c.Eip = o->stub;
    if (!Wow64SetThreadContext(th->h, &c) || !Wow64GetThreadContext(th->h, &got) || got.Eip != c.Eip || got.Esp != c.Esp) { dirty(S, r, fmt("cannot set the context of sleeping thread %lu", o->tid)); return r; }
  }
  for (auto& [o, th] : pl.alert) {  // realert(): the memory restore wrote the snapshot's WOW64 state back: the stub context again
    WOW64_CONTEXT c = o->ctx, got{};
    got.ContextFlags = WOW64_CONTEXT_CONTROL;
    c.Eip = o->stub;
    if (!Wow64SetThreadContext(th->h, &c) || !Wow64GetThreadContext(th->h, &got) || got.Eip != c.Eip || got.Esp != c.Esp) { dirty(S, r, fmt("cannot set the context of alert-waiting thread %lu", o->tid)); return r; }
  }
  for (auto* list : {&pl.set, &pl.late})
    for (auto& [o, th] : *list)
      for (auto& n : now)  // where the thread's mode differs from the snapshot's, keep the live WOW64 mode slots (64-bit stack pointer, in-32-bit flag)
        if (n.tid == o->tid && n.kernel != o->kernel) { wr<ULONG64>(S.p, o->teb + 0x1480, n.slots[0]); wr<ULONG64>(S.p, o->teb + 0x1490, n.slots[2]); }
  if (S.coop) {
    if (S.o.timeFix) wr<int64_t>(S.p, COOP(skewQpc), qpc() - s.vAtFreeze);
    wr<uint32_t>(S.p, COOP(parkAtTick), runTicks ? s.tick + runTicks : 0);
  }
  guard->phase = 1;
  thaw(S);
  unpark(S);
  lap("resume");
  return r;
}

// ---------------------------------------------------------------- command line
static bool attach(Sess& S, DWORD pid, bool watchdog = true) {
  S.pid = pid;
  if (!S.p) S.p = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
  if (!S.p) { printf("FAIL: open %lu: error %lu\n", pid, GetLastError()); return false; }
  S.name = checkTarget(S.p, S.o.allowGame);
  if (S.name.empty()) return false;
  if (watchdog) startWatchdog(pid);
  return true;
}
static Opt options(int argc, char** argv, int from) {
  Opt o;
  for (int i = from; i < argc; ++i) {
    std::string a = argv[i];
    if (a == "--allow-game") o.allowGame = true;
    else if (a == "--plan-only") o.planOnly = true;
    else if (a == "--no-coop") o.coop = false;
    else if (a == "--no-time-fix") o.timeFix = false;
    else if (a == "--reap-workers") o.reapWorkers = true;
    else if (a == "--recreate-views") o.recreateViews = true;
    else if (a == "--allow-shared-views") o.allowShared = true;
    else if (a == "--allow-audio") o.allowAudio = true;
    else if (a == "--allow-gui") o.allowGui = true;
    else if (a == "--allow-gpu") o.allowGpu = true;
    else if (a == "--no-view-probe") o.viewProbe = false;
    else if (a == "--quiesce-ms" && i + 1 < argc) o.quiesceMs = strtoul(argv[++i], nullptr, 10);
    else if (a == "--catch-ms" && i + 1 < argc) o.catchMs = strtoul(argv[++i], nullptr, 10);
    else if (a == "--allow-module" && i + 1 < argc) { std::string m = lower(argv[++i]); o.forbid.erase(std::remove(o.forbid.begin(), o.forbid.end(), m), o.forbid.end()); }
    else { fprintf(stderr, "unknown option %s\n", a.c_str()); exit(2); }
  }
  return o;
}
static int cycle(DWORD pid, DWORD runMs, int n, const Opt& o) {
  Sess S; S.o = o;
  if (!attach(S, pid)) return 1;
  Result r = snapshot(S);
  r.print();
  for (int i = 0; i < n && S.s.have; ++i) {
    Sleep(runMs);
    r = rewind(S, o.planOnly);
    r.print();
    if (r.code == 3) break;
  }
  freeSnap(S.s);
  return r.code;
}
// A combase RPC cache worker idle in CRpcThread::WorkerLoop: WaitCoalesced on its own event plus the coalescing timer,
// alertable, so WaitForMultipleObjectsEx(2, ..., alertable 1) (docs/270 §36) [verified, static, combase 10.0.26100.9818].
// The worker's other WaitCoalesced user, the COM remote-object ping loop (CROIDTable worker), waits on its timer alone and
// not alertable (count 1, alertable 0): the thread is lent to that loop while the process holds out-of-process COM proxies.
static bool comIdleWait(const TS& t) { return t.kernel && t.name == "NtWaitForMultipleObjects" && t.sig[7] == 2 && t.sig[10] == 1; }
static bool comPingWait(const TS& t) { return t.kernel && t.name == "NtWaitForMultipleObjects" && t.sig[7] == 1 && t.sig[10] == 0; }
// Names a combase worker's wait in settle notes: the ping-loop shape (count 1, not alertable) is the §36 blocker.
static std::string comWaitShape(const TS& t) {
  if (!t.kernel || t.name != "NtWaitForMultipleObjects") return describe(t);
  bool ping = comPingWait(t);
  return fmt("in NtWaitForMultipleObjects count %lu alertable %lu%s", t.sig[7], t.sig[10], ping ? ", the remote-object ping loop" : "");
}
// Waits without a freeze until no live thread of the target starts in module mod, at most secs (docs/270 §32): combase's
// RPC cache workers end after 30 s idle and cannot be kept alive one by one, so the snapshot is taken after they ended.
// busy (combase kept alive by ts3native, docs/270 §36): waits instead until every such thread is in its idle wait; a worker
// lent to the remote-object ping loop at the snapshot is back idle at the rewind, a different wait that refuses.
static Result settle(Sess& S, unsigned secs, const std::string& mod, bool busy = false) {
  Result r; r.op = "settle";
  ULONGLONG t0 = GetTickCount64();
  auto views = viewsOf(S.p, regions(S.p));
  if (busy && S.stubs.empty()) loadStubs(S, views);
  double left = 0, ping = 0;  // ping: of left, the threads lent to the remote-object ping loop
  std::string last;
  for (;;) {
    left = 0; ping = 0; last.clear();
    for (HANDLE th : threadHandles(S.p)) {
      ULONG64 st = 0;
      if (WaitForSingleObject(th, 0) == WAIT_TIMEOUT && NtQueryInformationThread_(th, 9, &st, sizeof st, nullptr) >= 0) {
        const View* v = viewAt(views, (DWORD)st);
        bool in = v && v->type == MEM_IMAGE && baseName(v->file) == mod;
        if (in && busy) {  // a short suspend to read where it waits; the watchdog resumes it if this host dies meanwhile
          TS t; DWORD prev = Wow64SuspendThread(th);
          if (prev != (DWORD)-1) {
            guard->n = 1; guard->tids[0] = GetThreadId(th); guard->phase = 1;
            bool got = classify(S, {GetThreadId(th), th, prev}, t);
            ResumeThread(th); guard->phase = 0; guard->n = 0;
            in = !got || !comIdleWait(t);
            if (in) last += fmt(" %lu (%s)", GetThreadId(th), got ? comWaitShape(t).c_str() : "context not read");
            ping += in && got && comPingWait(t);
          } else in = false;  // exiting
        }
        left += in;
      }
      CloseHandle(th);
    }
    if (!left || GetTickCount64() - t0 >= secs * 1000ull) break;
    Sleep(500);
  }
  r.add("settleMs", (double)(GetTickCount64() - t0)); r.add("settleLeft", left);
  if (busy) r.add("settlePing", ping);
  if (left && busy) r.notes.push_back(fmt("%.0f %s thread(s) still out of their idle wait after %u s:%s; the snapshot is taken anyway (a rewind refuses if one is back idle)", left, mod.c_str(), secs, last.c_str()));
  else if (left) r.notes.push_back(fmt("%.0f %s thread(s) still there after %u s: the snapshot is taken anyway (a rewind refuses if one ends)", left, mod.c_str(), secs));
  return r;
}
// Waits without a freeze until no forbidden module (default frida-agent, --allow-module drops one) is mapped, at most secs
// (docs/270 §36): after the probe detaches its agent unloads on its own thread a little later, and any other Frida session
// keeps it loaded. The harness polled tasklist /M before, which lists only the 64-bit WOW64 modules of a 32-bit process and
// so always said "unloaded after 0 s". Refused (code 2, nothing changed) when one is still loaded at the end.
static Result gone(Sess& S, unsigned secs) {
  Result r; r.op = "gone";
  ULONGLONG t0 = GetTickCount64();
  std::string left;
  for (;;) {
    left.clear();
    for (auto& v : viewsOf(S.p, regions(S.p)))
      if (v.type == MEM_IMAGE && hasAny(baseName(v.file), S.o.forbid)) { left = baseName(v.file); break; }
    if (left.empty() || GetTickCount64() - t0 >= secs * 1000ull) break;
    Sleep(200);
  }
  r.add("goneMs", (double)(GetTickCount64() - t0));
  if (!left.empty()) r.refuse(fmt("module %s is still loaded %u s after the probe detached: another Frida session is in the game (a kept frames session?)", left.c_str(), secs));
  return r;
}
static int serve(DWORD pid, const Opt& o) {
  Sess S; S.o = o;
  if (!attach(S, pid)) return 1;
  char line[128];
  int code = 0;
  while (code != 3 && fgets(line, sizeof line, stdin)) {
    unsigned ticks = 0;
    if (!strncmp(line, "snapshot", 8)) { Result r = snapshot(S); r.print(); code = r.code; }
    else if (!strncmp(line, "rewind", 6)) { Result r = rewind(S, false); r.print(); code = r.code; }
    else if (!strncmp(line, "plan", 4)) { Result r = rewind(S, true); r.print(); code = r.code; }
    else if (sscanf_s(line, "ticks %u", &ticks) == 1) { Result r = rewind(S, false, ticks); r.print(); code = r.code; }
    else if (sscanf_s(line, "settle %u", &ticks) == 1) {  // settle <s> [module] [busy], default combase.dll
      char mod[64] = "combase.dll", how[16] = "";
      sscanf_s(line, "settle %*u %63s %15s", mod, (unsigned)sizeof mod, how, (unsigned)sizeof how);
      settle(S, ticks, mod, !strcmp(how, "busy")).print();
    }
    else if (sscanf_s(line, "gone %u", &ticks) == 1) gone(S, ticks).print();  // gone <s>: forbidden modules unloaded
    else if (!strncmp(line, "park", 4)) { Result r; r.op = "park"; parkWorker(S, r); r.print(); }  // held until the next op ends
    else if (!strncmp(line, "quit", 4)) break;
    else printf("result {\"op\":\"?\",\"code\":1,\"why\":[\"commands: snapshot | rewind | plan | ticks <n> | settle <s> [module] [busy] | gone <s> | park | quit\"]}\n"), fflush(stdout);
  }
  freeSnap(S.s);
  return code;
}

#include "rewind-selftest.inl"

int main(int argc, char** argv) {
  if (argc >= 5 && !strcmp(argv[1], "watchdog")) return watchdogMain((HANDLE)(ULONG_PTR)strtoull(argv[2], nullptr, 10), strtoul(argv[3], nullptr, 10), (HANDLE)(ULONG_PTR)strtoull(argv[4], nullptr, 10));
  if (argc >= 3 && !strcmp(argv[1], "selftest")) return selftest(argv[2], argc > 3 ? argv[3] : "");
  if (argc >= 4 && !strcmp(argv[1], "crashtest")) return crashtest(strtoul(argv[2], nullptr, 10), atoi(argv[3]));
  if (argc >= 5 && !strcmp(argv[1], "cycle")) return cycle(strtoul(argv[2], nullptr, 10), strtoul(argv[3], nullptr, 10), atoi(argv[4]), options(argc, argv, 5));
  if (argc >= 3 && !strcmp(argv[1], "serve")) return serve(strtoul(argv[2], nullptr, 10), options(argc, argv, 3));
  fprintf(stderr, "usage: rewind-spike selftest <rewind-toy.exe> [case] | cycle <pid> <runMs> <n> [options] | serve <pid> [options]\n");
  return 2;
}
