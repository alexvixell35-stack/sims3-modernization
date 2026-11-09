# 541. Lot message pool: a world-load peak that never comes back, and `[LotPool] Trim` (2026-11-08)

Static RE on Steam TS3W.exe 1.67 (Capstone disassembly), a read-only census of the live pool (ReadProcessMemory, no lock,
no writes) on kit5 and Bridgeport, `native/ts3native/lptrim.cpp` with its selftest, and one game run with the trim on (al323,
Bridgeport, 3 trips to France and back: PASS, 6 trims, none skipped).

Tags: [verified] = read in the disassembly or logged in a run; [measured] = a run's numbers; [inferred] = reasoning, needs a
probe. Ledger: docs/51 §947. Counters: v913 `lp*` (docs/registry.md).

## 0. Short answer

- **The pool:** EA fixed pool `0x11EB590`, 272-byte lot message slots, 129 per 35,372-byte core, cores from the main EA heap
  [verified]. It grows at a world load to whatever the load needs at once and never gives a core back before exit
  [verified code; measured].
- **Size:** Bridgeport 599 cores = 20.2 MiB, 99.3-99.7% of the slots free, 494-553 cores (16.7-18.7 MiB) with every slot
  free; kit5 132 cores = 4.45 MiB, 128 of them completely free [measured, al320 and al317 census]. Trips did not raise the peak.
- **Fix:** `[LotPool] Trim=1` gives the completely free cores, all but `KeepCores`, back to the EA heap, under the pool's own
  lock, `SettleSec` after each world load. Off by default. The EA heap keeps the memory for its other allocations, so what this
  buys is less EA heap growth later in the session, not lower `priv` at once.
- **Not this pool:** the 47-60 MB AllocSites row at `0x5C32E2` (the pool's alloc callback). That callback is the plain
  main-heap Malloc and other pools share it [inferred: the census of this pool alone is 20.2 MiB in the same world].

## 1. Layout [verified disassembly]

| Address | What |
|---|---|
| `0x11EB590` | pool header: `+0` item size (0x110), `+4` align (0x10), `+8` items per core (0x80), `+0xC` core list, `+0x10` free list, `+0x14` alloc callback, `+0x18` free callback, `+0x1C` callback argument |
| `0x11EB5B0` | the pool's mutex (EA wrapper over a CRITICAL_SECTION; byte `+0x24` = 1, locking on, read in game) |
| `0xF8C2C0` -> `0x4E43D0` | init (static constructor) |
| `0xAE00A0` | pop: lock (deadline `[0x1068820]` = -1), add a core if the free list is empty, pop, unlock; 270 callers |
| `0xAE00F0` | push: lock, push, unlock; 19 callers |
| `0x4E42F0` | add-core: `(items + 2) * item + 0xC` bytes from `[+0x14](bytes, [+0x1C])`, core `+0` next, `+4` bytes, linked at the head, then the carve |
| `0x4E4290` | carve: first slot `(core + align + 7) & ~(align - 1)`, then one every `item` bytes while `<= core + bytes - 2 * item`, plus one more; sets the free list to the new core's slots |
| `0x5C32D0` / `0x4E8590` | alloc / free callbacks: main EA heap (`[0x11CB864]`) Malloc `0x4E69D0` / Free `0x4E5920` |
| `0xF94150`, `0xF94160` -> `0x4E4250` | exit: mutex teardown; free-all (walks `+0xC`, frees each core through `[+0x18]`, no lock) |

All references to the free list field `0x11EB5A0` are in the pop and the push (4), and to the pool base in the init, the
pop's add-core and the exit (3). So the free list changes only under the mutex, and cores only at add-core and exit.

## 2. Census [measured, read-only]

| Run | World | When | Cores | MiB | Slots | Free | Completely free cores |
|---|---|---|---|---|---|---|---|
| al317 | kit5 (old save) | play | 132 | 4.45 | 17,028 | 99.5% | 128 |
| al320 | Bridgeport | before the load | 0 | 0 | 0 | - | - |
| al320 | Bridgeport | after the load | 599 | 20.21 | 77,271 | 99.7% | 553 (18.65 MiB) |
| al320 | Bridgeport | after 3 trips to France and back | 599 | 20.21 | 77,271 | 99.3% | 494 (16.66 MiB) |

## 3. `[LotPool] Trim` (`lptrim.cpp`)

- **When:** `SettleSec` (60) after each world load, seen as a new ts3fixes `world:` line (`g_udWorlds`), so `[UserDiag]` must
  be on. Its own thread (64 KB stack, wakes once a second).
- **Under the pool's mutex** (the game's own lock and unlock, the pop's deadline): pass 1 reads the core list and walks the
  free list, counting free slots per core and checking each one sits on a carve position of its core. Then the completely
  free cores are chosen, all but `KeepCores` (16). Pass 2 relinks the free list without their slots, keeping the order.
  Pass 3 unlinks those cores. Then unlock.
- **After the unlock:** each core goes to the pool's own free callback. Nothing in the game can reach them now.
- **World quit cancels it:** a ts3fixes `world quit` line (`g_udQuits`, userdiag.cpp; `World.OnWorldQuit`: menu, travel,
  quit to desktop) drops a pending trim; none runs until the next world load.
- **Apex stand-down:** with Apex Radiance loaded and `[Compat] ApexStandDown=1` the trim does nothing (`lpBad`, `lptrim:
  skipped (... stood down: Apex Radiance loaded)`, at most 5 lines), as `rctrim.cpp` does: memory is Apex's area.
- **Skips, nothing changed, `lpBad` and one `lptrim: skipped` line:** a header that is not the one in §1, more than 8,192
  cores, a free slot outside every core or off the carve positions, a free list longer than the slots, a fault while reading.
- **Lines:** `lptrim: #n world load #w world=<name> cores a -> b, slots s free f (p%), completely free c: freed d (x MiB), kept k; locked t ms`.
- **Counters (v913):** `lpOn`, `lpTrims`, `lpCoresFreed`, `lpFreedKB`, `lpCoresLeft`, `lpKept`, `lpLastUs`, `lpBad`.
- **Selftest:** a fake pool built the way add-core and the carve build one (cores 16-aligned, as the EA heap returns them).
  It checks:
  - five cores, three completely free, one kept, two freed;
  - live slots untouched, every listed free slot in a listed core;
  - the pool drains and grows again after the trim;
  - KeepCores holds;
  - the four skip cases, plus the Apex stand-down (before the header and the lock).

## 4. Risks

- **Exit race (fixed in 0.36.221, review of PR #313):** EA's atexit destroys the pool mutex (`0xF94150`) and frees every
  core without it (`0xF94160` -> `0x4E4250`) while the trim thread still lives; a trim overlapping that could free a core
  twice or lock a deleted mutex. Now a world quit cancels the pending trim. Quit to desktop from a world runs
  `InWorldToQuitState.Startup` -> `GameUtils.TransitionToNotInWorld` (the same call as quit to menu and travel, whose
  `world quit` lines are in every travel job log), which raises `World.OnWorldQuit`; only its `OnEnterNotInWorld` moves to
  `QuitState`, whose `GameUtils.TransitionToQuit` ends the game loop, and EA's atexit runs after that [verified, decompiled
  scripts]. Left: an exit that skips the world quit (the process killed; then no atexit runs either), and a trim already
  past its check when the line arrives (it holds the lock a few ms; the world unload before atexit takes seconds) [inferred].
- **A burst after the trim:** the pool grows again, core by core, through the normal pop path. `KeepCores` covers the first
  2,064 slots.
- **Other code holding a slot pointer in a freed core:** only a free slot is in a freed core. A use after free in the game
  would already hit a slot it had pushed back [inferred].

## 5. Test

al323: the al320 flow (`mem-travel-x3.json`: Baseline-BP, 3 trips to France and back, mem marks) with `LotPool.Trim=1`,
requiring at least 4 `lptrim: #` lines and no `lptrim: skipped`, plus a read-only census of the pool every 30 s. PASS in
10 min; the game quit normally after the 7th world load (its trim was not due yet) [measured]:

| Trim | World | Cores before -> after | Slots free | Completely free | Freed | Locked |
|---|---|---|---|---|---|---|
| 1 | Bridgeport | 542 -> 56 | 99.6% | 502 | 486 (16.39 MiB) | 2.30 ms |
| 2 | France | 56 -> 53 | 98.0% | 19 | 3 (0.10 MiB) | 0.37 ms |
| 3 | Bridgeport | 600 -> 82 | 98.3% | 534 | 518 (17.47 MiB) | 2.57 ms |
| 4 | France | 292 -> 80 | 99.0% | 228 | 212 (7.15 MiB) | 1.72 ms |
| 5 | Bridgeport | 80 -> 80 | 96.4% | 15 | 0 | 0.72 ms |
| 6 | France | 80 -> 80 | 95.8% | 13 | 0 | 0.76 ms |

- **Between loads** the pool held 53-82 cores (1.8-2.8 MiB) instead of al320's 599 (20.2 MiB). The census never saw it grow
  in play: the most slots in use at once was 1,441 of 10,320 (86% free).
- **Loads differ:** the first two Bridgeport loads grew the pool to 542 and 600 cores; the third did not grow it at all.
- **Process numbers do not move in a run this short** (mark #42: `priv` 2344 vs 2308, `big` 888 vs 902, `eaRes` 716 both,
  al320 vs al323). The EA heap keeps its reserve; the freed cores become free space inside it. The gain is EA heap growth
  avoided later in a long session (the part compositors alone add about 24 MiB/h, #285). A long Bridgeport A/B (trim off
  and on, `eaRes` at the end) has not been run.
