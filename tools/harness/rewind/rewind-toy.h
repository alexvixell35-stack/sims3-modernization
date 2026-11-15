// Shared by rewind-spike.cpp (x64 host) and rewind-toy.cpp (x86 target), docs/270: the cooperation block a target publishes
// so the host can quiesce it, and the toy's state block and command codes for the selftest.
#pragma once
#include <stdint.h>
#pragma pack(push, 4)

// Named mapping "Local\rewind-coop-<pid>", created by the target (the toy here; ts3native [Rewind] design in docs/270 §9).
struct RewindCoop {
  uint32_t magic, version;       // 'RWND', 1
  uint32_t self;                 // address of this block inside the target
  volatile int32_t request;      // host -> target: 1 = park at the next tick boundary
  volatile int32_t parked;       // target -> host: 1 = parked (sim thread at a tick boundary, render and streaming idle)
  volatile int32_t audioOn;      // target -> host: 1 = a sound device is open (the host refuses)
  volatile uint32_t tick;        // target: sim ticks so far
  volatile uint32_t parkAtTick;  // host -> target: park when tick reaches this (0 = off), for equal-length runs
  volatile int64_t skewQpc;      // host -> target: QueryPerformanceCounter ticks the target subtracts from its clock
  // Renderer bridge (docs/270 §12). A bridge client that publishes this block sets bridge = 1; the host then writes an op and
  // the target sends it to the bridge server, stores the answer and clears op as the acknowledgement.
  volatile uint32_t op;          // host -> target: 1 snapshot, 2 rewind, 3 plan (report only)
  volatile int32_t bridge;       // target -> host: 1 = a bridge client answers here
  volatile int32_t bridgeResult; // target -> host: the server's HRESULT for op, negative = refused or no answer
  volatile uint32_t bridgeStats[8];  // kept, revive, drop, buffers, surfaces, saved KB, writes not restored, refused
};
static const uint32_t REWIND_COOP_MAGIC = 0x444E5752;  // 'RWND'
// Named mapping "Local\rewind-ts3n-<pid>", created by ts3native with [Rewind] Park=1 (docs/270 §19): the host asks that the
// route worker sit in its idle wait; no new job starts meanwhile, running ones finish.
struct RewindWorkerPark {
  uint32_t magic, version;       // 'RWTN', 1
  volatile int32_t request;      // host -> target: 1 = park the worker
  volatile int32_t idle;         // target -> host: 1 = the worker is in its idle wait
  volatile int32_t held;         // target: job starts turned away while asked
};
static const uint32_t REWIND_TS3N_MAGIC = 0x4E545752;  // 'RWTN'

enum ToyCmd : uint32_t {
  C_AUDIT = 1, C_MAPNEW, C_UNMAP_A, C_UNMAP_A_CLOSE, C_REMAP_A_OTHER, C_UNMAP_B, C_SCRIBBLE, C_FILE_OPEN, C_FILE_SEEK,
  C_FILE_CLOSE, C_THREAD_NEW, C_THREAD_EXIT, C_SIGNAL, C_SPIN_ON, C_BLOCK2, C_UNBLOCK2, C_STACK, C_PROTECT, C_PATCH, C_TP,
  C_TP_BURST, C_ETW_CLOSE, C_CB_ON, C_CB_OFF, C_SYNC_DRAIN, C_SYNC_UP, C_MUTEX, C_GUI, C_SLEEPER_SPIN,
  C_BRIDGE_ON, C_BRIDGE_REFUSE, C_PIPE_CONNECT, C_PIPE_DONE, C_WW_DIRTY, C_GC, C_REARM, C_ADDR_A, C_ADDR_B,
  C_BUSY_NOGAP, C_BUSY_GAP, C_TP_SLEEP,
};
enum ToyBad : uint32_t {  // bits of ToyState.bad
  B_INV = 1, B_BLK = 2, B_ALLOC = 4, B_VIEW_A = 8, B_VIEW_B = 0x10, B_FILE = 0x20, B_RO = 0x40, B_CODE = 0x80, B_TP = 0x100,
  B_SETUP = 0x200, B_VIEW_F = 0x400, B_SYNC = 0x800, B_GC = 0x1000, B_REARM = 0x2000,
};

struct ToyState {
  volatile uint32_t a, b, bad, allocs;   // offsets 0, 4, 8, 12: the busy counter, its invariant, failed checks, block churn
  uint32_t blk[8], sig[8];
  volatile uint32_t tick, rng, quit;
  volatile uint32_t cmd;                 // host writes a ToyCmd, the command thread clears it when done
  volatile uint32_t wakes, sleeps, spin, spinCount, block2, t3exit, sleeperSpin;
  volatile uint32_t cbSpin, cbCount, tpRuns, timeJumps, audioOpen;
  volatile int64_t lastV;                // virtual clock at the last check
  uint32_t viewA, viewA2, hSecA, viewB, viewF, hFileA, posA, hEvt, hEvt2, hwnd, roPage, naPage, sigV;
  uint32_t etwLo, etwHi, hSem, hEvtM, hMtx;
  volatile uint32_t pipeConns;           // connections the pipe server took (it parks in ConnectNamedPipe in between)
  uint32_t wwBase, hEvtR0, hEvtR1;       // write-watch heap of the toy GC; events of the two re-arming waiters
  volatile uint32_t wwGen, gcRuns, rearms[2];
  volatile uint32_t addrA, addrB, addrRuns;  // condition-variable waiter: woken on A, then waits on B one frame deeper
  uint32_t hEvtW0, hEvtW1, hEvtW2;           // busy worker: shallow wait, deep wait, never signalled
  volatile uint32_t busy, busyRuns;          // busy: 0 idle, 1 no gap at the shallow wait, 2 a ~300 us gap there
  uint32_t bigPriv, bigPrivMB, bigWW, bigWWMB;  // mode big: game-sized private memory and write-watch heap (restore timing)
};
#pragma pack(pop)
