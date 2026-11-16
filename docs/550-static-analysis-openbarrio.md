# 550. Static analysis of OpenBarrio for EA logic bugs (2026-11-14)

OpenBarrio (GPL-3.0, commit 4aff52f) rewrites TS3W.exe 1.67 functions one by one and keeps EA's logic, bugs included
(docs/547). Running analysers over it is a cheap way to find EA bugs: an analyser warning on a faithful transcription
is a warning on the game. This page triages every warning kind that could point at a hang, stall, routing failure,
leak or crash. Nothing from OpenBarrio is copied here; logic is described in our words, addresses and names are
theirs (`docs/engine/FUNCTIONS.csv`), verdicts are ours.

Tags: **[verified]** = we read the code (OpenBarrio source, and where marked, the bytes of our repack copy of TS3W.exe
via capstone); **[inferred]** = reasoning about reachability or in-game effect, not checked in the game.

Already covered elsewhere and not repeated here: SACS `Partition 0x982980` empty-pivot hang, `ReadRows 0xF20D10`, JPEG
upsample `0x4287B0`, KNN sort `0xB7BA00`, `SampleArc 0x75B610` (docs/549 §1, §4.5), the resource manager items and the
audio state leak `0x862CC0` (docs/549 §4-5), and the kept-bug table in docs/547.

## Tools and how to rerun

| Tool | Version / mode | Scope |
|---|---|---|
| clang-tidy | LLVM 23, target `i686-pc-windows-msvc`, `-fms-extensions -fms-compatibility -std=c++20`, MSVC CRT/SDK headers from xwin | one run per source file (638 files) |
| cppcheck | 2.22 | whole `src/` tree, all severities |
| CodeQL | 2.27, C++ database in build-mode none, standard C++ suite, CSV output | whole repo |
| Semgrep | 1.185, three custom rules | whole `src/` tree |

Rerun: the clang-tidy command is kept in the session scratchpad as `obtidy/run1.sh` (call it with a source path from
the OpenBarrio root; it writes one `.txt` per source). It uses the clang-tidy binary in `~/.local/share/venvs/ts3/bin/`,
the xwin headers under `~/.local/opt/xwin/` (`crt/include`, `sdk/include/{ucrt,um,shared}`), include paths
`src/core/include`, `src/core`, `third_party/EASTL/include`, `third_party/EABase/include/Common`, and these checks:
`bugprone-infinite-loop, bugprone-too-small-loop-variable, bugprone-branch-clone, bugprone-redundant-branch-condition,
misc-redundant-expression, bugprone-misplaced-widening-cast, bugprone-sizeof-expression, bugprone-signed-char-misuse,
bugprone-suspicious-memset-usage, bugprone-incorrect-roundings, clang-analyzer-core.*, clang-analyzer-deadcode.*,
clang-analyzer-cplusplus.NewDelete*, clang-analyzer-unix.Malloc, clang-analyzer-security.FloatLoopCounter`.
The Semgrep rules are in the scratchpad `ob-rules.yml`: `lock-return-before-unlock`, `loop-bound-unsigned-subtraction`,
`endless-loop-no-exit`.

**Semgrep: 0 real hits.** 3 raw hits, all false positives on reading: `Mutex::Lock` returns while holding the lock by
design (`thread/mutex.cpp`); the render loop in `renderer/clu_718cb0_Renderer.cpp` and the CAS bucket walk in
`cas/clu_5a37e0_CAS.cpp` leave by `goto`. No hit for the unsigned-subtraction loop bound.

## Counts by tool and kind

clang-tidy counts are unique (file, line, check) after removing repeats from shared headers. "Noise" = the code reads
game memory through constant addresses and wrapper calls (`ReadPtr`, `CallGameMethod<..., 0xADDR>`), or keeps EA's
inline x87 asm, which the analysers cannot see into.

| Tool | Kind | Hits | a / b / c | Why the rest is noise |
|---|---|---:|---|---|
| clang-tidy | deadcode.DeadStores | 178 | not triaged | noise: EA stale-register stores kept for byte equality |
| clang-tidy | core.NullDereference / NullPointerArithm / NonNullParamChecker | 124 / 76 / 3 | not triaged | noise: constant-address reads |
| clang-tidy | core.uninitialized.UndefReturn | 88 | not triaged | noise: values written by inline asm |
| clang-tidy | core.UndefinedBinaryOperatorResult | 21 | 0 / 0 / 21 | inline asm writes (`fnstcw`, `seta`, `fstp`) the analyser cannot see |
| clang-tidy | core.uninitialized.Assign | 18 | 2 / 0 / 16 | same; the 2 real ones are findings 2 and 4 below (6 hits in the font parser count as one) |
| clang-tidy | core.CallAndMessage | 19 | 0 / 0 / 19 | 13 asm outputs, 6 null-object on constant addresses |
| clang-tidy | bugprone-branch-clone | 14 | 1 / 0 / 13 | 7 are `__asm` blocks the check sees as empty; the rest are deliberate repeated stores; 1 led to finding 1 |
| clang-tidy | bugprone-sizeof-expression | 10 | 0 / 0 / 10 | `memcpy` of a pointer-sized link field; byte span / `sizeof` is a correct element count |
| clang-tidy | core.StackAddressEscape | 8 | 0 / 0 / 8 | EA's stale EAX return (the callers ignore it), kept on purpose |
| clang-tidy | bugprone-signed-char-misuse | 5 | 0 / 0 / 5 | a hash that sign-extends key bytes; the same on insert and lookup |
| clang-tidy | misc-redundant-expression | 4 | 0 / 0 / 4 | `0x04 / 4` slot constants, a deliberate `x*y - x*y` NaN lane, EASTL |
| clang-tidy | security.FloatLoopCounter | 4 | 4 / 0 / 0 | finding 5 (one function family) |
| cppcheck | invalidPointerCast / uninitMemberVarNoCtor / dangerousTypeCast | 450 / 411 / 246 | not triaged | noise: layout views of game objects |
| cppcheck | nullPointer* / ctunullpointer / passedByValue | 299 / 4 / 39 | not triaged | noise |
| cppcheck | shiftTooManyBitsSigned | 50 | 0 / 0 / 50 | `1 << 31` sign masks; MSVC defines the result |
| cppcheck | identicalConditionAfterEarlyExit | 18 | 0 / 0 / 18 | a stream "good" flag re-read after a call that updates it (cppcheck does not see the write); 1 redundant compare EA really has (`0x80546E`, harmless) |
| cppcheck | memsetClassFloat | 12 | 0 / 0 / 12 | zero bytes are 0.0f on x86 |
| cppcheck | objectIndex / ctuArrayIndex / pointerOutOfBounds | 8 / 1 / 1 | 0 / 0 / 10 | struct-as-array views of contiguous fields |
| cppcheck | uninitvar | 7 | 0 / 0 / 7 | asm outputs, a loop that does initialise, `memcpy` fills; one failed-stream read that returns garbage with the stream already marked bad |
| cppcheck | identicalInnerCondition / oppositeInnerCondition | 7 / 3 | 0 / 0 / 10 | EA's "release, then re-check the field" pattern (the call between can clear it) |
| cppcheck | autoVariables / returnDanglingLifetime / danglingLifetime | 2 / 2 / 2 | 2 / 0 / 4 | the 2 autoVariables are finding 6; the rest are stale-EAX returns and an arena size out-parameter |
| cppcheck | incompleteArrayFill | 4 | 0 / 0 / 4 | deliberate one-dword copies |
| cppcheck | zerodivcond | 1 | 0 / 0 / 1 | [verified] `BuildGnpTexture 0x417720`: the divisor is 255 for every colour type that reaches the loop |
| cppcheck | incorrectLogicOperator / bitwiseOnBoolean / pointerSize / signConversion / arrayIndexOutOfBoundsCond | 1 / 5 / 1 / 1 / 1 | 0 / 0 / 9 | cppcheck misreads a hex-float range test; bool `&` is correct; split body/tail store |
| CodeQL | Variable used in its own initializer | 2 | 0 / 0 / 2 | build-mode none parses an assignment as an initialiser |
| CodeQL | Expression has no effect | 6 | 0 / 0 / 6 | `uint32_t{...}` brace temporaries misparsed |
| CodeQL | Equality test on floating-point values | 18 | 0 / 0 / 18 | exact compares are intended: empty-rectangle tests, "scale is exactly 1" skips, matrix-changed tests |
| CodeQL | style kinds (short global name, unused static, hides global, ...) | 3,037 | not triaged | style |

OpenBarrio transcription mistakes (verdict b): **none found** among the triaged hits. Where a hit looked like one, the
OpenBarrio comment and (for findings 1 and 3) our disassembly agreed with the transcription.

## Findings (verdict a: EA logic bugs)

| # | EA address | OpenBarrio file:line | Kind | In-game effect | Priority |
|---|---|---|---|---|---|
| 1 | `0x5A44A0` CacheMap::GetCachedPart (collect loop `0x5A4893`..`0x5A48E2`) | `src/core/cas/clu_5a37e0_CAS.cpp:315` | branch-clone | Fallback pick for part types 53/54 takes a **random part of any type** when the request has all four `0x1E000000` bits | medium |
| 2 | `0x9822E0` SACS InsertionSortTail (sort entry `0x982DF0`, wrapper `0x982E80`) | `src/core/sacs/clu_4ff680_SACS.cpp:148` | branch-clone | An empty-keyed record past the first 28 shifts with no stop: walks off the array front, **crash** | medium-low |
| 3 | `0x485BC0` / `0x485D90` font DICT parsers | `src/core/misc/cls_4853f0_x.cpp:276`, `:384` | uninitialized.Assign | Stack buffer of 64 operands with no bound: a malformed font overwrites the stack | low (mod fonts) |
| 4 | `0x84D7E0` AnimatedSimBody Refresh (locomotion) | `src/core/anim/m05_M134.cpp:45`, `:105`, `:117` | uninitialized.Assign / UndefinedBinaryOperatorResult | Stale or uninitialised stack float merged into the body's maximum duration (+360) | low-medium |
| 5 | `0x50F900` TileBoth, `0x50FB10` TileHorizontal, `0x50FC30` TileVertical | `src/core/ui/m08_B09.cpp:306`, `:317`, `:326` | FloatLoopCounter | Float loop counter: endless loop on infinite or very large (>= ~2^24 x tile) bounds | low |
| 6 | `0x50EB20` UI Traverse (UTFWin draw-list walk) | `src/core/ui/m08_B09.cpp:62`, `:86` | autoVariables | Stores the address of its own stack matrix in the renderer (+0x4C) and never clears it | low |

### 1. CAS part fallback picks from the whole cache [verified bytes; effect inferred]
`GetCachedPart` first searches for a part whose type (`& 0xCF00`, with 0x400/0x500 treated as partners and 0xCF00 as a
wildcard) and part type match and whose age bits (`& 0x1E000000`) match or the request asks for all of them. If that
search fails, a second "collect" loop gathers candidates and one is picked with the RNG. In that second loop the type
and part-type mismatch branches jump straight to the "request has all age bits" test (`0x5A48B2 jne 0x5A48DC`,
`0x5A48C9 je 0x5A48DC`, `0x5A48CE jne 0x5A48DC`), so the condition is effectively `(type && partType && ages) || allAges`
rather than `type && partType && (ages || allAges)`. The first search loop (`0x5A4647 jne 0x5A467D`) skips correctly.
Effect: for part types 53/54, when no matching part exists and the request covers all ages, every entry in the cache
becomes a candidate and a random unrelated part is returned with success. Also builds a vector of the whole cache on
each such miss. Callers: `CasPartCache::GetPart 0x5AEF40` (`0x5AEFBF`, `0x5AEFE9`). What part types 53/54 are and how
often a miss happens is not traced [inferred]; a symptom to look for is a wrong accessory/part appearing on an outfit
where CC or a missing pack removed the expected part.

### 2. SACS tail insertion sort walks off the array [verified bytes; reachability inferred]
The sort wrapper `0x982DF0` runs the introsort pass, then a guarded insertion sort on the first 28 records
(`lea esi,[edi+0x188E0]` = 28 x 0xE08) and the unguarded one, `0x9822E0`, on the rest. In `0x9822E0` a saved record
with an empty key (byte at +0x208 is 0) takes the shift branch with no predecessor test and no lower bound
(`0x98232D je 0x982356` -> shift -> `jmp 0x982321`, which re-tests the same saved key). It copies 0xE08-byte records
backwards through memory until it faults. So a range with more than 28 empty-keyed records (or any empty key that the
partition left outside the first 28) crashes instead of hanging. This is the crash twin of the empty-pivot hang in
`Partition 0x982980` (docs/549 §4.5). The wrapper `0x982E80` has 6 call sites (`0x882438`, `0x882471`, `0x88249D`,
`0x8825CA`, `0x88A178`, `0x98330E`; the first reads a table from global `0x11D9850`), not one as docs/549 lists for
the inner entry. Whether that table can hold empty-keyed records in play is not checked [inferred].

### 3. Font DICT parsers have no operand bound [verified bytes for `0x485BC0`; source for `0x485D90`]
The two record parsers read a byte stream where bytes 28-30 and 32+ are operands and 0-27 are operators, with 12 as
an escape (the CFF DICT layout). Operands go into a 64-dword stack array indexed by a counter that is only reset by an
operator (`0x485CE7` / `0x485CFC` store at `[esp+ebx*4+0x18]` with no compare on `ebx`; frame is 0x108 bytes). The
`0x485D90` escape-24 case also reads `values[count-3]` and copies `count-4` dwords, so fewer than 4 operands index before
the array, and an operator with no operand reads a stale slot (OpenBarrio notes the stale first value as an EA bug).
Shipped fonts are well-formed; a damaged or hand-made replacement font (font mods, language packs) could corrupt the
stack. Callers `0x489EBD` / `0x489EDF` [verified]. Low priority unless a crash dump shows `0x485xxx`.

### 4. Locomotion duration from an unset stack slot [verified in OpenBarrio source; bytes not re-read]
`Refresh 0x84D7E0` (single caller `0x95667F`, locomotion: allocations tagged "Locomotion/AnimatedSimBody/...") evaluates
speed-in / slow-out curves. The evaluate step writes the out-duration only when the evaluator is not a scalar and the
controller lookup succeeds; otherwise the shared stack float keeps whatever it held (uninitialised on the first use).
The merge step then takes `max(old, factor * duration)` by the sign bit of the difference and stores it at body +360.
A NaN or garbage value there makes the body's maximum transition duration wrong for that style [inferred]. OpenBarrio
documents this as an EA behaviour. Worth a read-only probe of body +360 for absurd values before thinking about a fix.

### 5. UI tile loops count with floats [verified in source; reachability inferred]
The three tile fillers step a float coordinate by the tile size (at least 1 px, they return early below 1) until it
passes the destination edge. With an infinite edge, or an edge beyond about 2^24 tile sizes, `x + w == x` and the loop
never ends while drawing a tile per pass (main-thread hang in UI drawing). NaN edges exit at once. Callers: 4 each
(`0x51066D`, `0x52151D`, `0x53DC2D`, `0x53DE42` for `0x50F900`, and the `0x51004F`..`0x510607` group for the other
two) [verified call scan]. Needs a window rectangle of inf/huge size, e.g. from a zero scale in a layout [inferred].

### 6. UI Traverse leaves a stack pointer in the renderer [verified bytes]
`Traverse 0x50EB20` (UTFWin_DrawDisplayLists, docs/547) builds its transform on the stack, stores the address in the
renderer at +0x4C (`0x50EC1D`) and returns (`ret 8` at `0x50EDDE`) without clearing it; per-node geometry is likewise
pointed at a stack copy. Draw helpers such as `0x50E880` and `0x50F900` read +0x4C. Harmless while they run inside
Traverse; any draw through the same renderer outside a Traverse would transform with dead stack data (garbled UI
geometry, not a crash) [inferred: no such outside call identified]. Single caller `0x50B2ED`.

## Not findings, noted for completeness [verified source]
- The route-key hash in `clu_c398c0_x` (`0xC3A9D0` etc.) ignores key byte 5 while equality checks all six bytes: more
  collisions for keys that differ only in byte 5, a speed issue only.
- `ScriptState805460` (`0x805460`) repeats an emptiness test; harmless.

## Next steps
1. Finding 1: log `GetCachedPart` collect-path calls with part type 53/54 and the all-ages mask (read-only detour,
   MSVC build) to see if it fires in play; a fix would skip the all-ages shortcut when type or part type mismatch.
2. Finding 2: add `0x9822E0` / `0x982980` to the hang/crash signature list next to docs/549 §4.5, and identify the table
   at `0x11D9850`.
3. Finding 4: read-only probe of body +360 after `0x84D7E0` for NaN or huge values.
