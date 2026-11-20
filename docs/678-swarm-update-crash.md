# 678. The Swarm effect update crash: one word of a live effect overwritten; `[SwarmGuard]`

2026-11-20. Static only (two run logs, one EA exception report, objdump and Ghidra on a copy of the Steam 1.67 exe). The
guard is built and checked offline; it has NOT RUN in the game. Marks: **[verified]** = read in a log, report,
disassembly or decompile; **[inferred]** = reasoning.

## 1. Summary

- The crash is a call through a bad pointer at the start of the Swarm effect update `0x92E0C0`, on the main thread.
- The object is a live `cVisualEffect` (0x280 bytes). Its own vtable (`+0`), the second interface vtable (`+8`), its
  reference count and its name are intact. Only the word at `+4`, the first interface vtable, is wrong: it holds a heap
  address.
- The effect was not freed and reused. Somebody else wrote one pointer-sized word into it. The writer is not identified.
- Built: `[SwarmGuard]` (default off), a guard at the one call site, with an exact test and an optional repair. It is a
  guard, not the true fix.

## 2. The code [verified]

| Address | What |
|---|---|
| `0x71E640` | `SwarmSystem_Update`, main loop `0x59ED59` |
| `0x9160B0` | `SwarmManager_UpdateFrame`: every effect group |
| `0x91BAB0` | `SwarmManager_UpdateEffectGroup` (`this` = the group, kept in `ebx`) |
| `0x91BC0A` | in it: `mov ecx, edi` / `call 0x92E0C0` for an active effect whose flag bit 2 (`+0x44`) is set |
| `0x92E0C0` | the effect's update. First lines: clear bit 2 of `+0x44`, `eax = [this+4]`, `call [eax+0x44]` with `ecx = this+4`. Thiscall, no stack argument, plain `ret`. Four callers: `0x91BC0A` and three interface methods (`0x92F9AC`, `0x92F9BF`, `0x92F9F3`) |
| `0x92FA00` | the `cVisualEffect` constructor. Writes `+0 = 0x1042570`, `+4 = 0x1042528`, `+8 = 0x1042520`; the only place that writes `0x1042570` or `0x1042520` |
| `0x91A8B9` | the only allocation: `0x280` bytes (`0x4E3F90`), then the constructor |
| `0x930230` | slot `+0x44` of vtable `0x1042528`, the call that faults: returns `[iface+0xC]`, or the effect itself when that is 0 |

The group (`0x91BAB0`'s `this`):

| Offset | What |
|---|---|
| `+0x30` / `+0x34` | vector of `cVisualEffect*`: every effect the group ever made. The vector holds a reference (`+0x14`, plain `inc` / `dec`, not interlocked: `0x92C5E0` / `0x92C5F0`) |
| `+0x40` | vector of indices: just created |
| `+0x50` / `+0x54` | vector of indices: active effects. The update loop walks it and reads `[+0x30][index]` |
| `+0x60` | vector of indices: free for reuse (`0x91A870` takes one before it allocates) |

Effects are pooled. `0x91A7C0` (called in the same update) drops finished effects from the active list: it calls slot
`+4` of the interface vtable (`0x930380`, the reset) and puts the index on the free list. The object stays allocated
and in `+0x30`. So an effect is not freed while its group lives, and "a freed effect reused by another allocation"
does not fit this crash: a new constructor run would have written `+4` again.

Script calls (`IC_Swarm_*`, sim thread) find the effect by a handle (`0x939560`) and post commands (`0x945AE0`); the
handle wrapper's count is interlocked. No sim-thread write to the effect object was found in the functions read
(`0x776E40`, `0x7766E0`) [verified for these two; the other ICs were not read].

## 3. The two samples of this stack

| | 2026-11-11 (`phase-20261111-2105`, ts3native 0.36.240) | 2026-11-20 (`phase-20261120-0347`) |
|---|---|---|
| Stack code refs | `0092E0DB 0071CAE4 0091BC0F 009162EB 0071E6D2 0059ED59` | `0092E0DB 006FD425 0071CAE4 0091BC0F 009162EB 0071E6D2 0059ED59` |
| Effect (`esi`) | `0x06751990` | `0x3BADF7E0` |
| Word at `+4` (`eax`) | `0x68675704` | `0x3C55FD54` |
| `[eax+0x44]` (`edx` = fault address) | `0xFFDBD1B5` | `0x000015F0` |
| `esp` | `0x0160FB90` | `0x0160FB90` |

Both [verified log]. The 2026-11-20 EA report also shows the memory [verified]:

- effect: `+0 = 0x1042570`, `+4 = 0x3C55FD54`, `+8 = 0x1042520`, `+0xC = 0`, `+0x10 = 0`, `+0x14 = 2` (references),
  `+0x18..+0x20` = a heap string of 28 characters (the constructor leaves the empty string `0x11C7E54`): a named, live,
  referenced effect.
- at `0x3C55FD54`: `3BF98864, 3BADF7E4, 75296650, 77426BE0, 2BA3DDA0, 2C8E08F0, 6DB82BD0, 3BFD3590`. The second word
  is `effect+4`.

Reading [inferred]:

- `[effect+4] = Y` and `[Y+4] = effect+4` is the shape of a doubly linked list with `next` at `+0` and `prev` at `+4`:
  the node at `Y` believes the node before it sits at `effect+4`, and that node's `next` was set to `Y`. The effect's
  `+8` (where that node's `prev` would be) is untouched, so the write was a neighbour's link update (`prev->next = Y`
  on insert or remove), not an insert of `effect+4` itself.
- In both samples the bad word ends in `4` and the effect is 16-byte aligned: both sides look like "object + 4", an
  object with a vtable at `+0` and a list node at `+4`.
- `effect+4` is also exactly the interface pointer every user of the effect holds. So either (a) some list links an
  object that used to live at this address and was freed while still linked, and the effect's block took its place
  (the stale neighbour then writes into the effect), or (b) code holding the interface pointer treats it as a list
  node. The report does not show the word before `Y` (the class of the neighbour), so the list is not named.
- Not a race on the group's vectors: that would give a wrong effect or index, not a heap pointer in one word.
- The two other family samples (docs/67 §2 of 2026-10-14, §9 of 2026-11-17) have other stacks (`0x91A46E` release,
  `0x91AD03`); they are also calls through a dead or overwritten Swarm object. A stale list neighbour that writes one
  word into whatever now lives at that address would explain all four, but that link is not shown.
- Nothing in either log names an effect start or stop before the fault; ts3native has no line for them.

Confidence: the object, the offsets and the "one foreign word, effect otherwise intact" picture are verified for
2026-11-20 and match the registers of 2026-11-11. The writer and the list are unknown.

## 4. Fix class

The true fix (stop the writer: unlink before the free, or defer a destroy past the update loop, as engines do with a
kill list flushed at frame end, or handles with a generation instead of raw pointers) needs the writer. It is not
known, so this is the fallback: a guard at the faulting call whose test is exact.

`[SwarmGuard]` (ts3native `swarmguard.cpp`, default off), one call-site retarget of `0x91BC0A`:

- Test: the object's `+0` is `0x1042570` and its `+8` is `0x1042520`. Only the `cVisualEffect` constructor writes that
  pair, so the object is a `cVisualEffect` past its constructor and not yet in a base destructor, and its `+4` has one
  valid value, `0x1042528`. If `+4` differs, the effect is bad. This is stricter than "readable, a vtable in `.rdata`,
  slot in `.text`": no other value passes.
- Any object with another `+0` or `+8` is not judged; the game's code runs as before.
- `Repair=0` (default): the update of that one effect is skipped for this frame. Its flag bit stays set, so the next
  frame tries again. Limit: the same word is used by every interface call on that effect (script commands, the reset
  in `0x91A7C0`), so the game can still crash there.
- `Repair=1`: `0x1042528` is written back and the update runs. The effect is whole again. Limit: the foreign list
  still thinks a node lives at `effect+4`; a later link update can write the word again (the guard catches it at the
  next update), and a walk of that list now reads the vtable as a node. Untested.
- `Scan=1`: the group's whole effect vector is checked at most once a millisecond (one compare per effect), so a bad
  word is found within a frame of the write. The line "scan found ... last clean scan N ms ago" brackets the write.
- Per first sight of an (effect, word) pair, two log lines (64 a session): the effect's three words, references,
  flags, name, what was done; then the word before the bad pointer and 8 words at it, `[word+0x44]`, and whether its
  second word points back at `effect+4`. The word before it is the neighbour's vtable if (a) holds: it names the
  class, and with it the list. Counter line once a minute: `swarm guard: updates N, bad +4 N (skipped N, put back N),
  other classes N, scans N (bad found N), dumps N`.

Cost: three compares per updated effect.

## 5. Checks

- Offline [verified]: clang-cl `/Zs /W4` of `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: no error,
  no warning in the new files. `node tools/selftest-all.ts --ci`.
- Selftest (`swarmguard-test.h`, no timer): the decisions on fake effects (good, bad, another class, mid-destruction,
  null word, skip and repair, the scan with null entries, the once-per-pair dump); the call-site stub against a fake
  update (runs with `ecx` = the effect, caller's `ebx` / `esi` / `edi` / `esp` kept, not run when skipped, runs after a
  repair). The stub test is compiled by MSVC only; it has not been executed here.
- EA 1.69: five rows with no address yet; the section logs SKIP there.
- Not verified: anything in the game. The crash is about 1 in 100 sessions, so one run proves only that the hook is
  harmless.

## 6. How to tell in a run

- `OK   swarm guard: Repair=.. Scan=..` at start.
- The minute line with `updates` rising and `bad +4 0`: installed, nothing found.
- A hit: `swarm guard: update found effect ...` (or `scan found`) plus the memory line, and the game keeps running
  past the point where it used to crash. Send both lines: the first word of the memory line names the writer's class.
- Repro entry: `swarm-guard-storm` (the Isla Paradiso storm, where the 2026-11-20 sample came from).

## 7. Next

1. Runs with `[SwarmGuard] Enabled=1 Scan=1` (and `Repair=1` in one arm) on the storm jobs; any hit names the class.
2. With the class: find its list, the unlink it misses, and fix that (the true fix). Then the guard can go.
3. Keep EA's `xcpt` report with each crash.

## 8. Second pass (2026-11-20, `feat/swarm-writer`): the writer is still not proven; `Trace=1`

Static only again (the three reports and logs, objdump). Nothing here ran in the game.

### 8.1 What the samples add

- **The 2026-11-17 sample (docs/67 §9, `phase-20261117-0832`) is the same corruption** [verified, EA report
  `xcpt ... 26-11-17 08.34.31`]: `esi = 0x746DC090` is a `cVisualEffect` with `+0 = 0x1042570`, `+4 = 0x370F8B54`,
  `+8 = 0x1042520`, references 1, empty name. Its stack is different only because the bad word was used somewhere
  else: `0x91AD01` in `0x91AC60` is `call [[effect+4]+0]` (interface slot 0) right after `0x91A870` returned the effect.
  So three of the four family samples are one bug: 2026-11-11, 2026-11-17, 2026-11-20.
- In that sample the effect came out of `0x91A870`. A new allocation runs the constructor, which writes `+4`, so the
  effect was taken from the group's free list (`+0x60`): **the word can be overwritten while the effect sits idle in
  the pool** [inferred, strong]. The update guard alone does not see that path; `Scan=1` does (it walks the whole
  vector), and `0x91AD01` is one more place the game can crash with `Repair=0`.
- The three bad words: `0x68675704`, `0x370F8B54`, `0x3C55FD54`. All are 4 past a 16-byte boundary, two end in `0x54`.
- 2026-11-20, memory at the bad word `Y` [verified]: `[Y] = 0x3BF98864` (again "16-aligned + 4"), `[Y+4] = effect+4`,
  then six heap pointers. Reading [inferred]: `Y` was put in a list in front of the node `N = 0x3BF98864`
  (`Y.next = N; Y.prev = N.prev; N.prev.next = Y; N.prev = Y`), and `N.prev` was the stale value `effect+4`. An insert
  after `effect+4` is excluded: it would also write `[0x1042528+4]`, read-only data. `N` is the next node or the list's
  head in its owner; the report does not show it.
- 2026-11-17, memory at `Y+0xD..Y+0x2C` [verified]: `+0x10 = 0`, `+0x14 = 0x770195C8`, `+0x18 = 0x064AA20C`,
  `+0x1C = 4`, `+0x20 = 0`, `+0x24 = 0x00FF39F8`, `+0x28 = 0`. `0xFF39F8` is a vtable written by 149 sites all over the
  exe (a common small base class or allocator object), so it does not name `Y`'s class. The fault there is the game
  running heap bytes at `Y+0x1E` (no NX).
- No report shows `[Y-4]`: EA's register dump starts at the register's address. `Y`'s class is still unknown in all
  three samples. The 2026-11-11 run has no EA report.
- docs/67 §2 (2026-10-14) was not re-analysed beyond its text: there the object behind an entry's `+0x38` was freed and
  reused by an XML buffer, with `[Alloc]` on. It may be a different bug.

### 8.2 What is not known

Which list, which class, which missing unlink. No true fix is built: a fix for an unproven writer would be a guess in
the game's memory. Candidates that remain: (a) a list of objects with a node at `+4` whose earlier member lived at the
effect's address and was freed while linked; the class can be anything allocated from the same heap, not only Swarm;
(b) code that holds the interface pointer `effect+4` and links it as if it were a node.

### 8.3 `[SwarmGuard] Trace=1` (default 0)

Forensic only; the game's behaviour does not change.

- A second call-site retarget, `0x91A8CC` (`call 0x4E3F90` in `0x91A870`: cdecl, six stack arguments, the only
  allocation of an effect). The naked stub pushes the six arguments again, calls the game's allocator with `ecx` / `edx`
  untouched, keeps `eax` and all registers and flags across the note. The note stores address, tick, thread and the
  block's first 8 words **before the constructor** in a ring of 1024.
- A hit (update or scan) then logs four more lines:
  1. `effect ... born N ms ago (allocation k of n, thread t); its block before the constructor: 8 words`: what the last
     owner of that address left. For (a), word 2 (`+8`) is that owner's other link and word 0 may still be its vtable
     (the heap may have overwritten words 0 and 1 with its own free-list links). Or `no birth record`.
  2. `bad word Y: 9 words; nearest vtable word below: V at -0xD`: the first 4-aligned word in the image's read-only data
     within 0x100 bytes below `Y`. `V` names the class that holds `Y` and `D` the offset of the node in it.
  3. `bad word +0x24..+0x44`.
  4. `its first word (next node or list head) N: ...` with the same vtable search: the neighbour, or the list's owner.
- Counter line: `births N`.
- With `V` (and the owner's vtable from line 4) the list is named: find the vtable's constructor and destructor, the
  insert and the remove, and the free that skips the remove.

Checks [verified offline]: clang-cl `/Zs /W4` of `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: no error,
warning counts as before (170 / 172 / 217, none in the swarmguard files). Selftest additions: the ring (newest record
wins, wrap), the vtable search, and (MSVC only, not executed here) the allocation stub against a fake six-argument
allocator, including a null return. EA 1.69: one more 0 row (`0x91A8CC`); the section logs SKIP there.

Run with `[SwarmGuard] Enabled=1 Scan=1 Trace=1` on the storm jobs.
