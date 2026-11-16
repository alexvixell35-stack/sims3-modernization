// Toy selftest of rewind-spike.cpp (included there): one case per guard, each with the path that must work and the path
// that must be refused. Every case starts its own rewind-toy.exe.
static int fails;
static void check(bool ok, const char* what) { printf("  %s %s\n", ok ? "ok  " : "FAIL", what); fails += !ok; }
#define CHECK(c) check((c), #c)

struct Toy {
  PROCESS_INFORMATION pi{}; ULONG64 st = 0; Sess S;
  bool start(const char* exe, const char* mode) {
    SECURITY_ATTRIBUTES sa{sizeof sa, nullptr, TRUE};
    HANDLE rdEnd, wrEnd;
    CreatePipe(&rdEnd, &wrEnd, &sa, 0);
    SetHandleInformation(rdEnd, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOA si{sizeof si}; si.dwFlags = STARTF_USESTDHANDLES; si.hStdOutput = wrEnd; si.hStdError = wrEnd;
    std::string cmd = fmt("\"%s\" %s", exe, mode);
    if (!CreateProcessA(nullptr, cmd.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) { printf("  FAIL cannot start %s\n", exe); ++fails; return false; }
    CloseHandle(wrEnd);
    char line[128]{}; DWORD got = 0;
    ReadFile(rdEnd, line, sizeof line - 1, &got, nullptr);
    CloseHandle(rdEnd);
    if (sscanf_s(line, "state %llx", &st) != 1) { printf("  FAIL no state line: %s\n", line); ++fails; return false; }
    S.p = pi.hProcess;
    S.o.retries = 15;
    if (!attach(S, pi.dwProcessId, false)) { ++fails; return false; }
    Sleep(150);
    if (state().bad) { printf("  FAIL toy setup, bad=%x\n", state().bad); ++fails; return false; }
    return true;
  }
  ToyState state() { return rd<ToyState>(S.p, st); }
  bool cmd(uint32_t c) {
    wr<uint32_t>(S.p, st + offsetof(ToyState, cmd), c);
    for (int i = 0; i < 6000; ++i, Sleep(1)) if (!state().cmd) return true;
    return false;
  }
  bool alive() { DWORD c = 0; return GetExitCodeProcess(pi.hProcess, &c) && c == STILL_ACTIVE; }
  bool advancing() { uint32_t a = state().a; Sleep(30); return alive() && state().a != a; }
  bool audit() { return cmd(C_AUDIT) && state().bad == 0 && alive(); }
  bool parkedAt(uint32_t tick) {  // waits until the toy parked itself at that tick
    for (int i = 0; i < 5000; ++i, Sleep(1)) if (rd<int32_t>(S.p, S.coop + offsetof(RewindCoop, parked)) == 1 && state().tick >= tick) return true;
    return false;
  }
  ~Toy() { freeSnap(S.s); if (pi.hProcess) { TerminateProcess(pi.hProcess, 0); CloseHandle(pi.hThread); } }
};
static bool ok(const Result& r) { if (r.code) r.print(); return r.code == 0; }
static bool refused(const Result& r, const char* word) {
  bool hit = false;
  for (auto& w : r.why) hit |= w.find(word) != std::string::npos;
  if (r.code != 2 || !hit) r.print();
  return r.code == 2 && hit;
}

static void caseBasic(const char* exe) {  // memory, registers, region map; equal runs from one snapshot; the clock
  Toy t; if (!t.start(exe, "")) return;
  Result s = snapshot(t.S); s.print();
  CHECK(ok(s) && s.stat("viewsPinned") >= 4 && s.stat("threadsWaiting") >= 3);
  const uint32_t N = 2000000;
  ToyState run[2];
  for (int i = 0; i < 2; ++i) {
    Sleep(450);
    Result r = rewind(t.S, false, N);
    if (i == 0) r.print();
    CHECK(ok(r) && r.stat("hashEqual") == 1 && r.stat("pagesWritten") > 0);
    CHECK(t.parkedAt(t.S.s.tick + N));
    run[i] = t.state();
  }
  CHECK(run[0].tick == t.S.s.tick + N && run[0].a == run[0].tick);
  CHECK(run[0].tick == run[1].tick && run[0].a == run[1].a && run[0].rng == run[1].rng && run[0].allocs == run[1].allocs);
  CHECK(!memcmp(run[0].blk, run[1].blk, sizeof run[0].blk));
  CHECK(ok(rewind(t.S, false)) && t.advancing() && t.audit());
  CHECK(t.state().timeJumps == 0);
}
static void caseViews(const char* exe) {
  Toy t; if (!t.start(exe, "")) return;
  CHECK(ok(snapshot(t.S)));
  CHECK(t.cmd(C_SCRIBBLE) && t.cmd(C_MAPNEW) && t.cmd(C_UNMAP_A));
  Result r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("viewsRemapped") == 2 && r.stat("viewsUnmapped") == 2 && t.audit());
  CHECK(t.cmd(C_UNMAP_A_CLOSE));
  r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("viewsRemapped") == 2 && r.stat("handlesReinserted") == 1 && t.audit());
  CHECK(t.cmd(C_REMAP_A_OTHER));
  r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("viewsRemapped") == 1 && t.audit());
  CHECK(t.cmd(C_UNMAP_B));  // no section handle left for B
  CHECK(refused(rewind(t.S, false), "no section handle is left") && t.advancing());
  t.S.o.recreateViews = true;
  r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("viewsRecreated") == 1 && t.audit());
}
static void caseFiles(const char* exe) {
  Toy t; if (!t.start(exe, "")) return;
  Result s = snapshot(t.S);
  CHECK(ok(s) && s.stat("diskFiles") >= 2);
  CHECK(t.cmd(C_FILE_OPEN) && t.cmd(C_FILE_SEEK));
  Result r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("handlesClosed") >= 1 && t.audit());
  CHECK(t.cmd(C_FILE_CLOSE));
  r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("handlesReinserted") == 1 && r.stat("handlesClosed") >= 3 && t.audit());
  if (s.stat("handlesNotPinned") > 0) {  // a handle kind that cannot be duplicated (ETW registration): closing it is refused
    CHECK(t.cmd(C_ETW_CLOSE));
    CHECK(refused(rewind(t.S, false), "cannot be duplicated") && t.advancing());
  } else printf("  skip: every handle kind of the toy can be duplicated here\n");
}
static void caseWaits(const char* exe) {
  Toy t; if (!t.start(exe, "")) return;
  CHECK(ok(snapshot(t.S)));
  {  // the bridge's event names are <base>_<guid> (docs/270 §27, §28): real names match, near misses do not
    const wchar_t* yes[] = {L"\\Sessions\\1\\BaseNamedObjects\\RewindDone_3f2a9c10-7b1d-4e2a-9f00-0a1b2c3d4e5f",
                            L"\\BaseNamedObjects\\RewindApplied_3F2A9C10-7B1D-4E2A-9F00-0A1B2C3D4E5F",
                            L"RewindResume_00000000-0000-0000-0000-000000000000"};
    const wchar_t* no[] = {L"\\BaseNamedObjects\\RewindDone", L"\\BaseNamedObjects\\Rewind_3f2a9c10-7b1d-4e2a-9f00-0a1b2c3d4e5f",
                           L"\\BaseNamedObjects\\RewindDone_3f2a9c10-7b1d-4e2a-9f00-0a1b2c3d4e5fx",
                           L"\\BaseNamedObjects\\RewindDone_3f2a9c10x7b1d-4e2a-9f00-0a1b2c3d4e5f",
                           L"\\BaseNamedObjects\\xRewindDone_3f2a9c10-7b1d-4e2a-9f00-0a1b2c3d4e5f",
                           L"\\BaseNamedObjects\\rewind-toy-12-RewindApplied", L""};
    for (auto n : yes) CHECK(bridgeHandshakeName(n));
    for (auto n : no) CHECK(!bridgeHandshakeName(n));
  }
  {  // a bridge handshake event set after the snapshot (as the server's RewindDone) is not put back (docs/270 §27)
    HANDLE ev = OpenEventA(EVENT_ALL_ACCESS, FALSE, fmt("Local\\RewindDone_%08lx-0000-0000-0000-000000000000", t.S.pid).c_str());
    CHECK(ev && SetEvent(ev));
    Result a = rewind(t.S, false);
    CHECK(ok(a) && a.stat("bridgeEventsKept") == 1 && WaitForSingleObject(ev, 0) == WAIT_OBJECT_0);
    if (ev) CloseHandle(ev);
  }
  uint32_t w0 = t.state().wakes;
  CHECK(t.cmd(C_SIGNAL) && t.state().wakes == w0 + 1);
  Result r = rewind(t.S, false);  // the waiter is in the same wait again: left alone
  CHECK(ok(r) && r.stat("threadsLeft") >= 1 && t.state().wakes == w0);
  CHECK(t.cmd(C_SPIN_ON));        // the waiter now runs: put back into its wait
  r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("threadsRestarted") >= 1 && t.state().spin == 0 && t.state().wakes == w0);
  CHECK(t.cmd(C_SIGNAL) && t.state().wakes == w0 + 1 && t.audit());
  CHECK(t.cmd(C_BLOCK2));         // the waiter sits in another wait: nothing can move it
  t.S.o.retries = 3;
  CHECK(refused(rewind(t.S, false), "at the snapshot it was") && t.advancing());
  CHECK(t.cmd(C_UNBLOCK2) && ok(rewind(t.S, false)) && t.audit());
  CHECK(t.cmd(C_SLEEPER_SPIN) && ok(snapshot(t.S)));  // snapshot with the sleeper running; then it sleeps again
  wr<uint32_t>(t.S.p, t.st + offsetof(ToyState, sleeperSpin), 0);
  Sleep(150);
  r = rewind(t.S, false);         // a thread in a short Sleep takes the snapshot context when the Sleep returns
  uint32_t spins = t.state().spinCount;
  Sleep(200);
  CHECK(ok(r) && r.stat("threadsSetLate") == 1 && t.state().sleeperSpin == 1 && t.state().spinCount != spins && t.audit());
  wr<uint32_t>(t.S.p, t.st + offsetof(ToyState, sleeperSpin), 0);
  Sleep(150);
  CHECK(ok(snapshot(t.S)));
  CHECK(t.cmd(C_CB_ON));          // the window thread is inside a callback from the kernel
  CHECK(refused(rewind(t.S, false), "user-callback depth") && t.advancing());
  CHECK(t.cmd(C_CB_OFF) && ok(rewind(t.S, false)) && t.audit());
  CHECK(t.cmd(C_GUI));            // a window made after the snapshot: window-system objects are not rewound
  CHECK(refused(rewind(t.S, false), "window-system objects changed") && t.advancing());
  t.S.o.allowGui = true;
  CHECK(ok(rewind(t.S, false)) && t.audit());
}
static void caseThreads(const char* exe) {
  { Toy t; if (!t.start(exe, "")) return;
    CHECK(ok(snapshot(t.S)) && t.cmd(C_THREAD_NEW));
    CHECK(refused(rewind(t.S, false), "was made after the snapshot") && t.advancing()); }
  { Toy t; if (!t.start(exe, "")) return;
    CHECK(ok(snapshot(t.S)) && t.cmd(C_THREAD_EXIT));
    CHECK(refused(rewind(t.S, false), "exited after the snapshot") && t.advancing()); }
  Toy t; if (!t.start(exe, "")) return;
  CHECK(ok(snapshot(t.S)));
  uint32_t runs = t.state().tpRuns;
  CHECK(t.cmd(C_TP_BURST));
  Sleep(100);
  CHECK(refused(rewind(t.S, false), "pool worker") && t.advancing());
  t.S.o.reapWorkers = true;
  Result r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("workersReaped") >= 1 && t.state().tpRuns == runs);
  CHECK(t.cmd(C_TP) && t.state().tpRuns == runs + 1 && t.audit());
  // a pool worker inside a work item (docs/270 §21): the snapshot waits until it is idle again, or refuses naming it
  t.S.o.retries = 2;
  CHECK(t.cmd(C_TP_SLEEP));
  CHECK(refused(snapshot(t.S), "thread-pool worker inside a work item") && t.advancing());
  t.S.o.retries = 40;
  Sleep(400);
  CHECK(t.cmd(C_TP_SLEEP));
  Result p = snapshot(t.S); p.print();
  CHECK(ok(p) && p.stat("freezeAttempts") > 1 && t.advancing());
  // while the snapshot is held, each worker factory's minimum is its thread count: idle workers do not end (docs/270 §25)
  CHECK(p.stat("poolFactoriesHeld") >= 1 && p.stat("poolWorkersHeld") >= 1);
  ULONG mn = 0, tot = 0, held = 0, orig = 0;
  HANDLE f = t.S.s.poolMin.empty() ? nullptr : t.S.s.poolMin[0].first;
  CHECK(f && poolCounts(f, mn, tot) && mn == tot && tot >= 1);
  HANDLE f2 = nullptr;
  CHECK(DuplicateHandle(GetCurrentProcess(), f, GetCurrentProcess(), &f2, 0, FALSE, DUPLICATE_SAME_ACCESS));
  held = mn; orig = t.S.s.poolMin[0].second;
  freeSnap(t.S.s);  // dropping the snapshot puts the minimum back
  CHECK(poolCounts(f2, mn, tot) && mn == orig && held > orig);
  CloseHandle(f2);
}
static void caseMemory(const char* exe) {  // stack growth, protection changes, patched code, kernel object states
  Toy t; if (!t.start(exe, "")) return;
  CHECK(ok(snapshot(t.S)));
  CHECK(t.cmd(C_STACK) && t.cmd(C_PROTECT) && t.cmd(C_PATCH));
  Result r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("decommitted") >= 1 && t.audit());
  CHECK(t.cmd(C_STACK) && t.advancing());
  CHECK(t.cmd(C_SYNC_DRAIN));
  r = rewind(t.S, false);
  CHECK(ok(r) && r.stat("semaphoresReset") == 1 && r.stat("eventsReset") == 1 && t.audit());
  CHECK(t.cmd(C_SYNC_UP) && ok(rewind(t.S, false)) && t.audit());
  CHECK(t.cmd(C_MUTEX));
  CHECK(refused(rewind(t.S, false), "changed owner") && t.advancing());
  CHECK(t.cmd(C_MAPNEW));  // --plan-only: lists the work, changes nothing
  t.S.o.allowGui = true;
  Result p1 = rewind(t.S, true), p2 = rewind(t.S, true);
  CHECK(p1.code == 2 && p1.stat("viewsUnmapped") == 2 && p2.stat("viewsUnmapped") == 2);
}
static void caseRefusals(const char* exe) {  // states no snapshot is taken in
  { Toy t; if (!t.start(exe, "nopark")) return;
    t.S.o.quiesceMs = 300;
    CHECK(refused(snapshot(t.S), "passed its park point") && t.advancing()); }
  { Toy t; if (!t.start(exe, "nocoop")) return;
    CHECK(refused(snapshot(t.S), "no cooperation block") && t.advancing());
    t.S.o.coop = false;  // the caller vouches for a quiet target: the busy thread is caught wherever it is
    CHECK(ok(snapshot(t.S)));
    Sleep(100);
    Result r = rewind(t.S, false);
    CHECK(ok(r) && r.stat("threadsSet") >= 1 && t.advancing() && t.audit()); }
  { Toy t; if (!t.start(exe, "audioflag")) return;
    CHECK(refused(snapshot(t.S), "open sound device") && t.advancing()); }
  { Toy t; if (!t.start(exe, "audio")) return;
    if (t.state().audioOpen) {
      CHECK(refused(snapshot(t.S), "sound module") && t.advancing());
      t.S.o.allowAudio = true;  // the sound stack also shares memory with the audio service: found as a shared view, left alone
      Result a = snapshot(t.S); a.print();
      CHECK(ok(a) && a.stat("viewsForeign") >= 1 && !a.notes.empty() && t.advancing());
      // the holder the tool cannot open is named from the system handle table when it shows object addresses: audiodg.exe,
      // the audio engine. A change there (the engine moving its stream) does not stop the rewind under --allow-audio.
      if (!t.S.s.audioViews.empty()) {
        ULONG_PTR av = *t.S.s.audioViews.begin();
        unsigned char x = 0;
        CHECK(ReadProcessMemory(t.S.p, (void*)av, &x, 1, nullptr));
        x ^= 0x5A;
        CHECK(WriteProcessMemory(t.S.p, (void*)av, &x, 1, nullptr));
        t.S.o.reapWorkers = true;  // the sound stack starts idle pool workers of its own after the snapshot now and then
        Result r = rewind(t.S, false);
        CHECK(ok(r) && r.stat("viewsAudioMoved") == 1 && t.advancing());
        t.S.o.allowAudio = false;  // without it, the same change (still there: the view is not put back) refuses before any change
        CHECK(refused(rewind(t.S, false), "changed since the snapshot"));
      } else printf("  skip: audio view holder not named (system handle table hides object addresses)\n");
    } else printf("  skip: no sound device to open\n"); }
  { Toy t; if (!t.start(exe, "extshare")) return;
    HANDLE m = OpenFileMappingA(FILE_MAP_READ, FALSE, fmt("Local\\rewind-toy-ext-%lu", t.S.pid).c_str());
    volatile unsigned char* v = m ? (unsigned char*)MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0) : nullptr;
    CHECK(v && v[0] == 1);
    // held only by the target (and this host, which is the tool): the target's own memory, restored like the rest
    Result a = snapshot(t.S);
    CHECK(ok(a) && a.stat("viewsSharedSelf") == 1 && a.stat("viewsForeign") == 0 && a.notes.empty());
    // a second process holds it: left alone at a rewind, which goes on only while its bytes are as at the snapshot
    STARTUPINFOA si{sizeof si}; PROCESS_INFORMATION pi{};
    std::string peer = fmt("\"%s\" nocoop", exe);
    CHECK(CreateProcessA(nullptr, peer.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi) != 0);
    HANDLE inPeer = nullptr;
    CHECK(pi.hProcess && DuplicateHandle(GetCurrentProcess(), m, pi.hProcess, &inPeer, 0, FALSE, DUPLICATE_SAME_ACCESS));
    a = snapshot(t.S); a.print();
    CHECK(ok(a) && a.stat("viewsForeign") == 1 && !a.notes.empty() && a.notes[0].find(fmt("rewind-toy.exe pid %lu x1", pi.dwProcessId)) != std::string::npos);
    Result b = rewind(t.S, false);
    CHECK(ok(b) && b.stat("viewsForeignSame") >= 1 && b.stat("hashEqual") == 1 && t.advancing() && t.audit());
    HANDLE mw = OpenFileMappingA(FILE_MAP_WRITE, FALSE, fmt("Local\\rewind-toy-ext-%lu", t.S.pid).c_str());
    unsigned char* w = mw ? (unsigned char*)MapViewOfFile(mw, FILE_MAP_WRITE, 0, 0, 0) : nullptr;
    CHECK(w != nullptr);
    if (w) w[0] = 2;  // the other side writes: the rewind refuses before any change
    b = rewind(t.S, false); b.print();
    CHECK(refused(b, "changed since the snapshot and is not put back") && b.why[0].find("rewind-toy-ext-") != std::string::npos && t.advancing() && v[0] == 2);
    if (w) UnmapViewOfFile(w);
    if (mw) CloseHandle(mw);
    t.S.o.allowShared = true;  // the old switch: restored with the rest
    CHECK(ok(snapshot(t.S)));
    if (pi.hProcess) { TerminateProcess(pi.hProcess, 0); CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
    if (v) UnmapViewOfFile((void*)v);
    if (m) CloseHandle(m); }
  { Toy t; if (!t.start(exe, "")) return;  // bridge protocol on the fake bridge client: snapshot = op 1, rewind = op 3 (plan) then op 2
    CHECK(t.cmd(C_BRIDGE_ON));
    Result a = snapshot(t.S);
    CHECK(ok(a) && a.stat("bridgeKept") == 1);
    Sleep(100);
    Result b = rewind(t.S, false);
    CHECK(ok(b) && b.stat("bridgeKept") == 3 && b.stat("bridgeRewound") == 1 && b.stat("hashEqual") == 1 && t.advancing());
    CHECK(t.cmd(C_BRIDGE_REFUSE));
    CHECK(refused(rewind(t.S, false), "bridge server") && t.advancing()); }
  { Toy t; if (!t.start(exe, "")) return;  // a GPU driver module in the target: the device is not rewound
    t.S.o.gpu = {"winmm"};
    CHECK(refused(snapshot(t.S), "GPU device") && t.advancing()); }
  { Toy t; if (!t.start(exe, "")) return;
    t.S.o.forbid = {"winmm"};
    CHECK(refused(snapshot(t.S), "is loaded") && t.advancing());
    t.S.o.planOnly = true;  // --plan-only: the refused snapshot stays, so plans can still be listed; a real rewind cannot run
    CHECK(refused(snapshot(t.S), "is loaded") && t.S.s.have && refused(rewind(t.S, true), "is loaded") && refused(rewind(t.S, false), "no memory copy")); }
  { Toy t; if (!t.start(exe, "")) return;  // without the clock fix the toy sees the jump: the detector works
    t.S.o.timeFix = false;
    CHECK(ok(snapshot(t.S)));
    Sleep(900);
    CHECK(ok(rewind(t.S, false)));
    Sleep(100);
    CHECK(t.state().timeJumps >= 1); }
}
// Threads parked in a wait for another process (docs/270 §14): the pipe server in ConnectNamedPipe and the waiter in
// WaitForSingleObject are left alone; a pipe server that moved on is refused; a thread with live AVX state is put back whole.
static void caseParked(const char* exe) {
  { Toy t; if (!t.start(exe, "")) return;
    uint32_t c0 = t.state().pipeConns;
    Result s = snapshot(t.S); s.print();
    CHECK(ok(s) && s.stat("threadsParked") == 1 && s.stat("threadsWaiting") >= 4);
    Sleep(100);
    Result r = rewind(t.S, false); r.print();
    CHECK(ok(r) && r.stat("threadsLeft") >= 2 && r.stat("hashEqual") == 1 && t.advancing() && t.audit());
    CHECK(t.cmd(C_PIPE_CONNECT) && t.state().pipeConns == c0 + 1);  // the server took a client and now sits in ReadFile
    t.S.o.retries = 3;
    CHECK(refused(rewind(t.S, false), "left the NtFsControlFile wait") && t.advancing());
    Result n = snapshot(t.S);  // a server inside ReadFile is not a parked wait: refused, with who and where
    CHECK(refused(n, "is inside NtReadFile") && n.why[0].find("rewind-toy") != std::string::npos && n.why[0].find("start rewind-toy.exe+") != std::string::npos);
    printf("  refusal: %s\n", n.why.empty() ? "" : n.why[0].c_str());
    t.S.o.gpu = {"winmm"};  // the module guard runs before the thread checks: its reason alone, nothing frozen (refuse-gpu)
    Result g = snapshot(t.S);
    CHECK(refused(g, "GPU device") && g.why.size() == 1 && g.ms.empty() && t.advancing());
    t.S.o.gpu = Opt().gpu;
    CHECK(t.cmd(C_PIPE_DONE) && ok(snapshot(t.S)));  // back in its listen at the same place: snapshot and rewind work again
    CHECK(t.cmd(C_PIPE_CONNECT) && t.cmd(C_PIPE_DONE) && t.state().pipeConns == c0 + 2);
    r = rewind(t.S, false);  // same call, same context, same handle after a whole connection: left alone
    CHECK(ok(r) && t.state().pipeConns == c0 + 1 && t.advancing() && t.audit());
    CHECK(t.cmd(C_PIPE_CONNECT) && t.state().pipeConns == c0 + 2 && t.cmd(C_PIPE_DONE) && t.audit()); }  // the pipe still works
  { Toy t; if (!t.start(exe, "avx")) return;
    t.S.o.coop = false; t.S.o.retries = 5;
    Result s = snapshot(t.S);
    CHECK(ok(s) && s.stat("threadsXState") >= 1);
    for (int i = 0; i < 3; ++i) {
      Sleep(100);
      uint32_t tick = t.state().tick;
      Result r = rewind(t.S, false);
      if (i == 0) r.print();
      CHECK(ok(r) && t.state().tick < tick);
    }
    Sleep(200);
    CHECK(t.alive() && t.state().bad == 0); }
}
// Write watch (docs/270 §15): the toy GC reads and resets dirty bits; after a rewind every write-watch page must report
// dirty again (a superset), else the GC misses the page written before the snapshot. Owner guard: an unknown importer.
static void caseWriteWatch(const char* exe) {
  for (int redirty = 1; redirty >= 0; --redirty) {
    Toy t; if (!t.start(exe, "")) return;
    t.S.o.wwRedirty = redirty != 0;
    CHECK(t.cmd(C_WW_DIRTY));        // page 3 written, the GC has not seen it yet
    Result s = snapshot(t.S);
    CHECK(ok(s) && s.stat("writeWatchAllocs") >= 1);
    CHECK(t.cmd(C_GC) && t.audit()); // the GC takes page 3 and resets the bits
    Result r = rewind(t.S, false);   // the heap is unchanged since the snapshot: no page of it is written back
    if (redirty) r.print();
    CHECK(ok(r) && (!redirty || (r.stat("writeWatchPages") == 16 && r.stat("writeWatchProbe") == 1)));
    CHECK(t.cmd(C_GC));
    if (redirty) CHECK(t.audit() && t.state().gcRuns >= 1);
    else CHECK((t.state().bad & B_GC) != 0);  // control: without re-dirtying the GC keeps a stale copy of page 3
  }
  Toy t; if (!t.start(exe, "")) return;
  t.S.o.wwOwners = {"kernel32.dll", "kernelbase.dll", "ntdll.dll"};
  CHECK(refused(snapshot(t.S), "rewind-toy.exe imports GetWriteWatch") && t.advancing());
}
// Waits left and taken up again (docs/270 §16): a worker that woke and went back into the same wait, and threads made
// after the snapshot that sit in a wait.
static void caseRearm(const char* exe) {
  { Toy t; if (!t.start(exe, "")) return;
    CHECK(ok(snapshot(t.S)));
    uint32_t r0 = t.state().rearms[0];
    CHECK(t.cmd(C_REARM) && t.state().rearms[0] == r0 + 1);  // woke, back in the same wait; the word above its arguments changed
    Result r = rewind(t.S, false); r.print();
    CHECK(ok(r) && r.stat("threadsRearmed") >= 1 && t.state().rearms[0] == r0 && t.audit());
    CHECK(t.cmd(C_REARM) && t.state().rearms[0] == r0 + 1 && t.state().bad == 0);  // wakes from the left wait, consistent
    CHECK(ok(snapshot(t.S)) && t.cmd(C_THREAD_NEW));
    CHECK(refused(rewind(t.S, false), "ends threads in a wait") && t.advancing());
    t.S.o.reapWorkers = true;
    r = rewind(t.S, false);
    CHECK(ok(r) && r.stat("workersReaped") == 1 && t.advancing() && t.audit()); }
  { Toy t; if (!t.start(exe, "rearmdead")) return;  // esi differs but is overwritten right after the return: left
    CHECK(ok(snapshot(t.S)));
    uint32_t r1 = t.state().rearms[1];
    CHECK(t.cmd(C_REARM) && t.state().rearms[1] == r1 + 1);
    Result r = rewind(t.S, false); r.print();
    CHECK(ok(r) && r.stat("threadsDeadRegs") >= 1 && t.state().rearms[1] == r1);
    CHECK(t.cmd(C_REARM) && t.state().rearms[1] == r1 + 1 && t.state().bad == 0 && t.audit()); }
  { Toy t; if (!t.start(exe, "")) return;  // condition-variable waiter at another depth: snapshot context, then alerted
    CHECK(ok(snapshot(t.S)));
    uint32_t a0 = t.state().addrRuns;
    CHECK(t.cmd(C_ADDR_A) && t.state().addrRuns == a0 + 1);  // now waits on B, one frame deeper
    Result r = rewind(t.S, false); r.print();
    CHECK(ok(r) && r.stat("threadsRealerted") == 1 && t.state().addrRuns == a0);
    Sleep(100);
    CHECK(t.cmd(C_ADDR_A) && t.state().addrRuns == a0 + 1);  // back in the snapshot's wait on A: the waker finds it there
    CHECK(t.cmd(C_ADDR_B) && t.cmd(C_ADDR_A) && t.state().addrRuns == a0 + 2 && t.advancing() && t.audit()); }
  Toy t; if (!t.start(exe, "rearmreg")) return;
  CHECK(ok(snapshot(t.S)) && t.cmd(C_REARM));
  t.S.o.retries = 3;
  Result r = rewind(t.S, false);
  CHECK(refused(r, "differs: ebx") && r.why[0].find("returns to rewind-toy.exe+") != std::string::npos && t.advancing());
}
// Busy threads (docs/270 §18): a worker at its shallow wait at the snapshot that later sits mostly in a deeper wait. The
// catch polls it alone until it is back in the snapshot's wait, holds it, and freezes the rest around it.
static void caseCatch(const char* exe) {
  { Toy t; if (!t.start(exe, "")) return;  // no gap: never back at the shallow wait; the catch gives up, nothing changed
    CHECK(ok(snapshot(t.S)));
    const uint32_t bs = t.state().busyRuns;  // at the snapshot: an exact rewind comes back to this count, not to b0
    CHECK(t.cmd(C_BUSY_NOGAP));
    t.S.o.retries = 3; t.S.o.catchMs = 500;
    uint32_t b0 = t.state().busyRuns;
    Result r = rewind(t.S, false); r.print();
    // the shallow wait still shows up for a few us now and then; when the catch hits one, the rewind is exact instead
    if (ok(r)) CHECK(r.stat("threadsCaught") >= 1 && t.state().busyRuns == bs && t.audit() && t.advancing());
    else {
      CHECK(refused(r, "differs: esp") && r.stat("catchPolls") > 0 && r.stat("threadsCaught") == 0);
      CHECK(t.state().busyRuns > b0 && t.advancing() && t.state().bad == 0);
    } }
  Toy t; if (!t.start(exe, "")) return;  // ~300 us gap per pulse: caught there
  CHECK(ok(snapshot(t.S)));
  uint32_t b0 = t.state().busyRuns;
  CHECK(t.cmd(C_BUSY_GAP) && t.state().busyRuns > b0);
  t.S.o.retries = 3;  // a lucky first freeze needs no catch
  Result r = rewind(t.S, false); r.print();
  CHECK(ok(r) && (r.stat("threadsCaught") >= 1 || r.stat("freezeAttempts") == 1) && t.state().busy == 0 && t.state().busyRuns == b0 && t.audit());
  Sleep(100);
  CHECK(t.state().busyRuns == b0 && t.cmd(C_BUSY_GAP) && t.state().busyRuns > b0 && t.state().bad == 0 && t.advancing());
}
// ts3native [Rewind] Park (docs/270 §19): the busy worker of the no-gap case above, but the target publishes the park
// block: the worker comes back to its idle wait before the freeze, no catch needed.
static void caseWorkerPark(const char* exe) {
  Toy t; if (!t.start(exe, "ts3npark")) return;
  CHECK(ok(snapshot(t.S)));
  uint32_t b0 = t.state().busyRuns;
  CHECK(t.cmd(C_BUSY_NOGAP) && t.state().busyRuns > b0);
  t.S.o.retries = 3; t.S.o.catchMs = 0;
  Result r = rewind(t.S, false); r.print();
  CHECK(ok(r) && r.stat("workerParked") == 1 && t.state().busyRuns == b0 && t.audit());
  CHECK(t.cmd(C_BUSY_NOGAP) && t.state().busyRuns > b0 && t.state().bad == 0 && t.advancing());  // unparked: works again
  // --no-coop (the game smoke jobs): the worker is parked all the same, at the snapshot and at the plan
  t.S.o.coop = false;
  Result a = snapshot(t.S);
  CHECK(ok(a) && a.stat("workerParked") == 1);
  uint32_t b1 = t.state().busyRuns;
  CHECK(t.cmd(C_BUSY_NOGAP) && t.state().busyRuns > b1);
  r = rewind(t.S, true); r.print();
  CHECK(ok(r) && r.stat("workerParked") == 1 && r.stat("catchPolls") == 0 && t.advancing());
}
// Run by caseWatchdog in a second host: freezes the toy, marks the phase, and dies without cleaning up.
static int crashtest(DWORD pid, int phase) {
  Sess S;
  if (!attach(S, pid)) return 1;
  if (!freeze(S)) return 1;
  guard->phase = phase;
  TerminateProcess(GetCurrentProcess(), 99);
  return 0;
}
static void caseWatchdog(const char* exe) {
  for (int phase = 1; phase <= 2; ++phase) {
    Toy t; if (!t.start(exe, "")) return;
    char self[MAX_PATH]; GetModuleFileNameA(nullptr, self, MAX_PATH);
    std::string cmd = fmt("\"%s\" crashtest %lu %d", self, t.S.pid, phase);
    STARTUPINFOA si{sizeof si}; PROCESS_INFORMATION pi{};
    CHECK(CreateProcessA(nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi) != 0);
    WaitForSingleObject(pi.hProcess, 10000);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    Sleep(500);
    if (phase == 1) CHECK(t.advancing());  // host died with the target only suspended: resumed
    else CHECK(!t.alive());                // host died mid-rewind: the target is ended
  }
}

// Restore timing at about the game's sizes (docs/270 §26): 340 MB of private pages changed, a 380 MB write-watch heap.
// Only when named (build.cmd speed): the toy takes 1.3 GB.
static void caseSpeed(const char* exe) {
  Toy t; if (!t.start(exe, "big")) return;
  ToyState st = t.state();
  CHECK(st.bigPriv && st.bigWW);
  Result a = snapshot(t.S); a.print();
  CHECK(ok(a));
  std::vector<unsigned char> one(1);
  // one byte in every second page: 87040 separate page runs, as the game's 86802 changed pages
  for (uint32_t i = 0; i < (340u << 8); ++i) {
    one[0] = (unsigned char)(i % 255 + 1);  // never the page's 0 at +8
    wvm(t.S.p, st.bigPriv + i * 8192 + 8, one.data(), 1);
  }
  Result r = rewind(t.S, false); r.print();
  CHECK(ok(r) && r.stat("pagesWritten") >= 87040 && r.stat("hashEqual") == 1 && t.audit());
  // a second and third rewind to the same snapshot (docs/270 §26)
  for (int k = 0; k < 2; ++k) {
    Sleep(200);
    wvm(t.S.p, st.bigPriv + 8, one.data(), 1);
    Result q = rewind(t.S, false);
    CHECK(ok(q) && q.stat("hashEqual") == 1 && t.advancing() && t.audit());
  }
}
static int selftest(const char* exe, const char* only) {
  struct { const char* name; void (*fn)(const char*); } cases[] = {
    {"basic", caseBasic}, {"views", caseViews}, {"files", caseFiles}, {"waits", caseWaits}, {"threads", caseThreads},
    {"memory", caseMemory}, {"refusals", caseRefusals}, {"parked", caseParked}, {"writewatch", caseWriteWatch},
    {"rearm", caseRearm}, {"catch", caseCatch}, {"workerpark", caseWorkerPark}, {"watchdog", caseWatchdog},
    {"speed", caseSpeed},
  };
  for (auto& c : cases) {
    if (*only ? strcmp(only, c.name) != 0 : !strcmp(c.name, "speed")) continue;
    printf("case %s\n", c.name);
    fflush(stdout);
    c.fn(exe);
  }
  printf("%s\n", fails ? "FAIL" : "PASS");
  return fails ? 1 : 0;
}
