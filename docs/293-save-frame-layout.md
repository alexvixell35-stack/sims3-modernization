# 293. Saved task frames and the mint code layout (2026-11-19)

Cause of the RC crash at `McCall+0x42` (`ts3native+0x4862`, Baseline-BP-Spring, 7 of 7 loads), what it means for save
portability, and the fix. Static work plus `native/monohost`; game runs are the queues in §7. Ledger: docs/51 §538.

## 1. What a save holds

A parked script task is saved as `ScriptCore.TaskFrame` records: method, IL checksum, `mIPOffset`, stack offsets, `this`,
eval stack. `mIPOffset` is the **raw word index into the method's transformed (mint) code** [verified: yield save
`0xE53620` cell +8; `TaskControl_SetTaskContext` `0xD844D4` copies element +8 into cell +8; the resume `0xE53F18` adds it
to the code the method has at that time]. The only guard is the checksum over the IL. So a frame resumes at the right
instruction only if the transform produces the same word layout as in the session that saved it.

## 2. The layout change, named

EA's transform inlines some callees at the call site instead of emitting a call [verified, capstone of `GenerateCode`
`0xE5DE08`-`0xE5E3A9`]. With no exception clause and no local (not tested for shape 1):

| Shape | Callee body (IL) | Emitted at the call site | Words | A call takes |
|---|---|---|---|---|
| 1 | starts with `ret` | one `pop` per argument (`cknull` first for a `callvirt`) | 0 and up | 2 |
| 2 | `ldsfld; ret`, no parameters | `ldsfld` (after a `pop` for an instance method) | 3 or 4 | 2 |
| 3 | `ldarg.0; ldfld; ret` | `ldfld` | 2 (4 for a struct field) | 2 |
| 4 | `ldarg.0; ldarg.1; stfld; ret` | `stfld` | 2 (4 for a struct field) | 2 |
| 5 | `volatile. ldsfld; ret` | as shape 2 | 3 or 4 | 2 |
| 6 | `ldsfld; ldfld; ret` | `ldsfld` + `ldfld` | 5 or 6 | 2 |

Not inlined: a virtual callee reached by `callvirt`, pinvoke and internal calls. A `callvirt` to a non-virtual method is
a plain 2-word `call` [inferred: no `cknull` before a `call` in 60,000 dumped methods].

ts3fixes replaces a game method by copying a hook's method header over it. When the game method has an inlined shape
and the hook has not (or the reverse), **every caller transformed after the swap gets another layout from that site on**.
The case that crashed: `visitcar` (docs/584) hooks the static getter `Lot.get_ChanceOfGoingToCommunityLotByCar`
(shape 2). `MetaRabbitHoleInteraction<,>.Run` reads it once before `DoLoop`: the game without the hook builds
`ldsfld` (3 words), with the hook `call` (2 words). Vanilla code: 211 words, `call DoLoop` returns at word 118 [verified,
monohost dump, all six instantiations]; the RC build: 210 words, return at 117 [verified, yielddiag log]. Baseline-BP-Spring
holds five diners parked in `Run` at word 118 (the vanilla layout: `visitcar` did not exist on 2026-10-24). The RC resumed
them one word late: the operand of `stloc` ran as an opcode, the next call found one stack slot, receiver = 1.
`unseenloopskip` (PR #540) only exposed it by ending the diners' `DoLoop` seconds after the load.

Ruled out [verified]: `[MintFuse] Sub` and EA's own superops (they rewrite the first opcode word in place), the method
compiler (compiled words in place, never a yielding method), a repeat transform (0 `yielddiag retransform` lines),
`[IlPatch]` (no spec on this method; but see §6).

Census of the hook table (`docs/data/swap-stack-audit.csv` against the vanilla dump, 1,185 rows, 92 not matched by
name): 7 targets with an inlined shape. Two are instance field getters with the same 2-word length (kept):
`BakeSaleTable.get_GetSeller`, `SpaceSimulator.get_RideOver`. Five move their callers:

| Hook | Target | Shape | Feature part lost while refused |
|---|---|---|---|
| `VisitCarHook.ChanceHook` | `Lot.get_ChanceOfGoingToCommunityLotByCar` | 2 | `visitcar`: unseen townie visits always by car |
| `LsCapHook.DayHook` / `NightHook` | `Autonomy.get_MaxTimeForLowLODInteractionDuringTheDay` / `Night` | 2 | `unseenloopskip` option (b), the low-LOD cap; option (a) step stays |
| `SlResetHook.ResetHook` | `Situation.OnReset` | 1 | `serviceleave`: re-arm after a reset |
| `W86TimerHook.RemoveHook` | `ActiveTimerNode.RemoveListenerFromTracker` | 1 | `timeralarmleak`: alarm removed when the node leaves |

No method named `*Hook` in `Ts3Fixes.dll` (7,656 methods) has an inlined shape.

## 3. What it means

- A save written by a build with such a hook holds moved indexes for frames parked behind the site. It resumes wrong in
  the game without the mod (uninstall), and in any build with another hook set.
- A vanilla save, or one from a build without the hook, resumes wrong in a build with it (this crash).
- Builds: `visitcar` since 2026-11-16, the loop-skip cap since 2026-11-18 (test and experimental builds only).
  `serviceleave` and `timeralarmleak`: which release first carried them is open (§8). Public v0.2.x saves hold the
  vanilla layout wherever none of the five hooks was installed.
- Any other mod that replaces an inlined method (MonoPatcher `ReplaceMethod`, S3MM) does the same to EA's format.

## 4. Fix part 1: ts3fixes never moves the layout

`Fixes.Patch` (the one place a game method's body is replaced) computes the inline shape of the game method and of the
hook body (`Fixes.InlineShape`, a port of EA's test) and refuses the swap when either would change the length of the
call site (`InlineShapeMovesLayout`: every shape but the 2-word field getter / setter of a non-struct type). The line in
`ts3fixes.applied` holds `REFUSED:`; counter `[1263]` counts them. With this, a build's layout equals the game's own
for every method ts3fixes can influence, so saved indexes are the vanilla ones in both directions and no translation at
save or load is needed. The five hooks above are refused until they are rebuilt on a method the game does not inline
(a caller, or a non-inlined method on the same path): §8.

Why not translate `mIPOffset` at save and load instead: the translation needs the vanilla layout of every method on a
saved stack at run time; with the cause known the layouts can simply be kept equal, which also covers a save loaded
after the mod is removed (nothing of ours runs then).

## 5. Fix part 2: `[EaCrashFixes] SavedFrameGuard=1` (ts3native, default 1)

For saves that already hold a moved index, and for the same change by anything else. `saveframes.cpp` detours
`TaskControl_SetTaskContext` `0xD84360` (cdecl: task id, `TaskContext*`; managed `TaskFrame[]` at +4, 0x28-byte elements
from +0x10, method +0, ip +8 [verified]). Per frame the method is transformed if it was not yet, and its code is
walked from word 0 with EA's instruction lengths (`0x1195088`, superop descriptors, `switch`):

- the saved index is an instruction boundary: untouched (every frame of a healthy save);
- it is inside an instruction: if exactly one boundary that follows a call-family instruction lies within 3 words, the
  index is set to it (`ea fix: SavedFrameGuard: ... set to the call return at word N`); otherwise the IC answers false
  without touching the task, as EA does for a frame it cannot restore: the object starts a fresh task (the Sim is reset)
  and the line says so;
- code that cannot be walked (a compiled entry word, an unknown opcode): left alone.

Applies to every Sim: a dropped household stack replaces a certain bad resume, never a healthy one. A moved index that
still lands on a call return (two moved sites before one parked frame, or `call; call` back to back) is not detectable;
part 1 keeps new saves out of that case. Counters line: `saved frame guard: N frames checked at load, ...`.

`[MonoDiag] YieldDiag=1` (default 0, log only) stays as the detector: resume ips that follow no call, repeat transforms.

## 6. Limits

- Steam 1.67 only: `0xD84360` and `0xE52B90` have no EA 1.69 / retail rows; those builds log `SKIP saved frame guard`.
  Part 1 is managed and works on every edition.
- `[IlPatch]` specs (all default 0) change the IL the transform reads, so a patched method that yields saves indexes of
  the patched layout while the checksum stays EA's. No spec may go on by default before its method is checked for yields
  or the guard grows an IL-offset map.
- Shape 1 targets that are virtual are only inlined at non-virtual `call` sites (`base.X()`); they are refused all the
  same, the sites cannot be listed at patch time.

## 7. Tests

- `native/ts3native` selftest `TestSavedFrames`: the classifier on hand-made code (right index, one word off either
  way, ambiguous, unwalkable).
- `native/monohost` selftest `SF-shape`, `SF-moved`, `SF-roundtrip`: EA's transform builds two callers of a static getter,
  one with the getter as it is (inlined) and one after a ts3fixes-style header swap (called): 28 and 27 words; each call
  return saved under one layout maps to the same call under the other, both directions, and is left alone under its own.
- `MONOHOST_LAYOUT=<file>` (selftest mode): transforms every method of the loaded assemblies (closed generic bases
  included, `LayoutDump.cs`) and writes name, word count, code words, inline shape, runtime name. 272,753 methods dumped
  from the Steam 1.67 assemblies (the host stops at one UI method whose transform does not return there;
  `MONOHOST_LAYOUT_SKIP=<substring>` skips a method).
- Queues: `save-frames-smoke-1119` (Baseline-BP-Spring: the old package with the new native must log the repair and run;
  the new package must log no repair), `save-portability-1119` (save, quit and reload mid-interaction across switch sets
  and without the mod).

- Walker check [measured]: with the guard's length rules (EA's table `0x1195088`, the 22 superop descriptors, `switch`) a walk
  from word 0 ends exactly at the word count for all 272,753 dumped vanilla methods (1,238,598 call instructions passed),
  0 overshoots, 0 unknown words.

### 7.1 First game run: `save-frames-smoke-1119` (2026-11-19, Baseline-BP-Spring, 10 min each, both PASS)

| Job | ts3fixes | Result |
|---|---|---|
| `bp-sfguard-repair` | RC package `rc-1118n` (hooks still move the layout) | PASS. 8 repairs at load: the 5 diners in `MetaRabbitHoleInteraction.Run` word 118 of 210 -> 117, and 3 Sims in `StudentUnionLibrary.VisitCommunityLotLabLecture.RunOrig` word 1236 of 1478 -> 1235 (the same getter, one word; nobody had seen these three). Every `yielddiag resume` of `Run` at word 117, no drop, no crash (this job crashed 7 of 7 before). |
| `bp-sfguard-refused` | new package (layout-moving swaps refused) | PASS. `Run` is 211 words and resumes at 118; `saved frame guard: 561 frames checked at load, 0 repaired, 0 stacks dropped, 0 not walked, 0 boundaries that follow no call`: every saved frame of this save sits on a call return of the build's code, i.e. the layout is the save's (vanilla) one. |

## 8. Open

1. Rebuild the five refused hooks on non-inlined methods (each ~30-60 min + its repro).
2. In-game layout census: log (name, words) per transform and compare with the vanilla dump, to prove no other path
   (class word of a swapped struct method, flags) moves a layout. 1 job + a compare tool.
3. EA 1.69 and retail rows for `0xD84360`, `0xE52B90`.
4. Which public release first shipped `serviceleave` / `timeralarmleak`; if one did, say in the release notes that the
   guard repairs those saves on load.
