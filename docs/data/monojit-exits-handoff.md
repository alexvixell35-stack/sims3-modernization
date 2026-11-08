# Mono compiler: call exits (A) and value-type opcodes (B), handoff

Branch `feature/monojit-exits` (A1-A3 and B1/B3 merged on master, see "A4 / merged base" below; the older branches
`feature/monojit-exits-vt`, `-a`, `-b1` are history). State 2026-11-08. Host only so far:
nothing in `native/ts3native` changed yet, no version taken in `docs/data/builds.json`, no counters reserved, no queue.
The final write-up goes to docs/537 §20; this file is the working handoff and can be deleted once §20 exists.

Task (from the launching brief): the two biggest open compiler gaps. A: most selected hot-method time is held back
where compiled code exits to the interpreter at `call` / `callvirt`. B: value-type opcodes, so the guarded BCL edges
(Dictionary / List generic paths, docs/537 §15/C1) stop bailing. Host first (corpus, verify, replay, census, bench),
new off-by-default `[MonoJit]` keys, counters via `node tools/registry.ts reserve`, native selftests, then the game
port (pair `monojit1108`, next free version past 0.36.196) and correctness jobs (verify + count on BP/IP/SV, ~15 min
each, not run). Measured statically only (docs/207 census model, `tools/jit-hot.ts`, `tools/jit-bench.ts`).

## Status per part

| Part | Switch (host env / game ini) | State |
|---|---|---|
| A call exits | `MONOHOST_JITCALLEXIT=1` / `[MonoJit] CallExit=1` (game key in pair monojit1108b, A5b) | host code in, corpus green, census gain measured; game port built + selftested, not run in game |
| A1-A3 | branch `feature/monojit-exits-a` (on top of d572517d) | done, host only; see "A1-A3 results" below |
| B vt opcodes | `MONOHOST_JITVTOPS=1` / `[MonoJit] VtOps=1` (game key not added yet) | B1 + B3 host code in (branch `feature/monojit-exits-b1`), corpus + replay green; B2 measured 0.0 ms/s held back, not built (see B2 below; newobj.vt 9.0 ms/s is the next candidate); B4 bench rerun, B5 open |
| B2b newobj.vt | `MONOHOST_JITNEWVT=1` / `[MonoJit] NewVt` (game key not added yet) | host code in (branch `feature/monojit-exits-b2b`), corpus + replay green, census 40.8 -> 33.0 ms/s; B5 game port after A5 |
| A4 (merged base) | branch `feature/monojit-exits` = master + `-a` + `-b1` | corpus, replay, census done; jit-bench timing still open (quiet machine); next B2 on this base |

### A5b CallExit, game port (2026-11-08, branch `feature/monojit-a5b` on `feature/monojit-a5` be05ee50) [built, selftested; not run in game]

- `native/ts3native/monojit.cpp`, behind `[MonoJit] CallExit` (default 0; the OK line ends `VtOps=0|1, CallExit=0|1`; ini key in
  `ts3native.ini`). Ported from the host's d572517d, emitter bytes copied as they are (the game's guard block, `EmitExit` lazy pass
  and exit handling match the host's layout):
  1. lazyOk loop: with CallExit a hot K_EXIT and ret.vt keep the lazy body (the exit goes through JitLazyMat). Game-only difference:
     under `DirectLeaf=1` a call site (0xEA-0xED) still clears lazyOk, so DirectLeaf keeps meaning "leaves only" (the host has no
     DirectLeaf; jit-exits runs DirectLeaf=0, where this is the host's rule). Pure icall sites do not clear lazyOk (native, not a call).
  2. Pure icalls: `kPureGetLength/Lower/Rank`, `JitPureIcallsOk` (install, after the exe gate), `JitPureIcall`, `EmitPureIcall`, at
     call / callvirt sites before inline and lazy calls, in both passes. Not counted in `ts3native_counters` (no layout change);
     `jit: compiled ...` ends `, pure icalls` for a method that has one. A mismatch logs `jit: CallExit: the Array icall bytes differ`.
  3. Boxed vt receiver: `JitGuardSite.cls` (the cache's class word) + `unbox`, set in `JitGuardFill`; the guard skips the vt-bit
     bail when CallExit and op 0xEC, then `add ecx, [site.unbox]`. con.callvirt (0xED) keeps the bail.
  Count-mode parts of the host (lazy exit sites, `jitbail`, `jitmat`) are not ported: the game has no count mode.
- Game disassembly checks (game-copy TS3W.exe, Steam 1.67) [verified]: the three icall bodies byte-equal the host's arrays (56 / 57 /
  13 bytes compared by script); dwords at 0x10B58D4 / D8 / DC = 0xE79FC0 / 0xE7A000 / 0xE79FB0. GetLength / GetLowerBound: dim < 0
  or >= [[[this]]+0x10] raise via 0xE772A0 + 0xEA18F0, bounds [this+4] NULL -> [this+8] / 0, else [bounds + dim*8 (+4)]; GetRank
  = byte [[[this]]+0x10]. callvirt 0xE54AD6-0xE54B41: this = [esi_args-8] into [ebp-0x50], null / null vtable -> 0xE593DF, cache
  [ebp-0x24] class compare with [vtable], then `test byte [class+0x14],8` and `test [target+0x44],0x400` -> [ebp-0x50] = this + 4
  (the eval slot keeps the box; ours changes only ecx = B_THIS).
- Selftest `TestMonoJitCallExit` (`monojit-test.h`, after Capture): pure icalls szarray / rank 2 length and lower bound, GetRank by
  call and callvirt, result slot high dword = the word above the args; dim past rank, -1, null this exit at the call with stack and
  esi unchanged; CallExit=0 leaves it an EA call; a callee with a hot exit gets a lazy body only with CallExit and mats at the exit
  (DirectLeaf caller still gets none); guarded callvirt with a boxed receiver: override (0x400) reads box + 4 (unbox 4), inherited
  method reads the box (unbox 0), null receiver exits at the callvirt with nothing changed, CallExit=0 bails. build.cmd selftest ok;
  `tools/selftest-all.ts` 212 PASS 0 FAIL 5 SKIP (the 5 need game-copy/assemblies, absent in the worktree); `tools/hook-sites.ts
  --check` pass (docs/68 row added for the six read addresses).
- Pair `monojit1108b` in builds.json: asi d7f1e4425ff7 (0.36.201; 0.36.200 = monojit1108), ts3fixes 55828c22b72c (= monojit1108).
- Queue `tools/harness/queues/jit-exits.json`: `bp-jit-exits-ce1`, `ip-jit-exits-ce1` (15 min, VtOps=1 CallExit=1, pair
  monojit1108b, gates as vt1 plus OK line `VtOps=1, CallExit=1`). `--dry-run` parses. Not run.
- Merge state: not merged by this agent; the merge agent takes `feature/monojit-a5b` (tip after this line) into master in a held window.
- Next: run jit-exits (smoke, vt0/vt1, ce1); B2 (host) then its game port.

### A5 + B5 vt ops, game port (2026-11-08, branch `feature/monojit-a5`) [built, selftested; not run in game]

- Merged to master first: `feature/monojit-exits` (ea287ba4) and `docs/houseboat-row-verify` (f181c4b8), both clean, no job running.
- `native/ts3native/monojit.cpp`: `EmitLazyCall` B_LVL push `4 + B_LVL` -> `8 + B_LVL` (game push order checked: B_VT is pushed right
  before B_LVL, as in the host; the depth test `opEsp(0x03, EAX, 4 + B_LVL)` runs before any push and stays 4). Not gated.
- `[MonoJit] VtOps` (default 0): kinds K_STFLDVT..K_STOBJVT before K_FCMP, the five `kJitTmpl` rows, group 1024 in `JitTemplates`, the
  emitter cases copied from the host byte for byte (null / bounds exit first, `e.satb` before heap stores, ldobj / stobj.vt size at compile,
  no layout or not a value type = permanent exit). The OK line ends `VtOps=0|1` (switch read-back). ini key documented in `ts3native.ini`.
- CallExit (A design items 1-3) is NOT ported yet: no `[MonoJit] CallExit` key in the game; pure icalls and boxed-vt guard receivers stay host only.
- Selftest `TestMonoJitVtOps` (`monojit-test.h`): group 1024 outside 1023, each op copy / cursor / esi, null, index 3 and -1, enum and
  no-layout ldobj exits. build.cmd selftest ok; `tools/selftest-all.ts` 217 PASS 0 FAIL 0 SKIP; `tools/hook-sites.ts --check` pass.
- Pair `monojit1108` in builds.json: asi 56edfb9a1b73 (0.36.200; 0.36.199 = tier2rows1108), ts3fixes 55828c22b72c (= castrimdiag1108b).
- Queue `tools/harness/queues/jit-exits.json`: bp-jit-exits-smoke (5 min, VtOps=1, stopQueueOnFail), bp/ip-jit-exits-vt0/vt1 (15 min);
  jit ini as jit-batch.json; gates no crash, 0 `jit verify MISMATCH`, scriptErrorsMax 0, `jit: compiled` + OK line `VtOps=<arm>`, nativeRequire
  as jit-batch. `--dry-run` parses. Not run (Rodney schedules it).
- Next: B2 (other agent, host); game CallExit port after it; run jit-exits.

### A4 / merged base (2026-11-08, branch `feature/monojit-exits`) [measured, host]

- Merge order: `feature/monojit-exits-a` (e7486fcf), then `feature/monojit-exits-b1` (24e2e525). Corpus case order: the
  old 212 generator cases keep their indices, then A1 `EXIT_CASES` (212-219), then B3 `VTOPS_CASES` (220-224); monohost
  reports 246 cases (233 at d572517d). `jitList()` = ... `KA_LIST`, `VTOPS_LIST`.
- Corpus verify, 246 cases, `MONOHOST_JITINLINE=2 MONOHOST_JITWIDE=1`, each 0 differ 0 unstable:

  | mode | per-call verify ok | MISMATCH |
  |---|---|---|
  | VtOps=1 CallExit=1 lazy on | 42987 | 0 |
  | VtOps=1 CallExit=1 lazy off | 43936 | 0 |
  | VtOps=0 CallExit=1 lazy on | 42987 | 0 |
  | VtOps=1 CallExit=0 lazy on | 43243 | 0 |

- Replay `probe-out/jit-replay-all.txt --mode verify --lazy`, CallExit=1, INLINE=2, WIDE=1: VtOps=1 and VtOps=0 each 5468
  inputs, 5282 ran, 0 differ, 0 unstable, 186 skipped, 0 faulted, ok 1413749 MISMATCH 0. The first VtOps=0 run stopped at
  input 1703 with "host fault C0000005 while rebuilding" (EIP in monohost.exe +0x25610, the replay's object rebuild, before
  any compiled code ran); the rerun of the same build and flags passed all inputs. A flaky host rebuild, not a compiler
  result [inferred: one fault in 3 full runs, in rebuild code]; look at it if it repeats.
- Census held back (`tools/jit-hot.ts --count`, threshold 1.0 ms/s, same 105.1 ms/s sampled in 32 methods); CSVs from
  `tools/jit-exit-census.ts --census` in `docs/data/results/2026-11-08-jit-exit-census-{d572517d,merged}-ce{0,1}.csv`:

  | group | d572517d CE=0 | d572517d CE=1 | merged CE=0 | merged CE=1 |
  |---|---|---|---|---|
  | callvirt | 28.3 (8) | 22.9 (4) | 15.4 (7) | 10.0 (3) |
  | call | 17.1 (6) | 12.3 (5) | 12.3 (5) | 12.3 (5) |
  | newobj | 7.8 (2) | 9.0 (3) | 7.8 (2) | 9.0 (3) |
  | newarr | - | 4.8 (1) | 4.8 (1) | 4.8 (1) |
  | box / con.boxcallvirt | 2.8 (box) | 2.8 (con.boxcallvirt) | 2.8 (box) | 2.8 (box) |
  | con.callvirt | 2.0 | 2.0 | 2.0 | 2.0 |
  | unbox | 0.5 | 0.5 | 0.5 | 0.5 |
  | **total** | **57.9** (18) | **53.7** (15) | **45.0** (17) | **40.8** (14) |

  Merged = branch `-a` numbers (B1 adds 0.0 ms/s held back, as its own census said). DefaultComparer.Equals (2.8) books
  as `box` on the merged build, as `con.boxcallvirt` at d572517d CE=1.
- jit-bench: `--self-check` OK; timing not run (machine noisy), run `tools/jit-bench.ts --run` on a quiet machine
  (A1 bench cases 29/30 `JitLzAlloc` / `JitLzAllocPlain` and the B1 VtOps 0/1 pair).
- Next: B2 (vt call args / returns in `EmitLazyCall`, lazy ret.vt) on this merged base. A5 game port must also fix the
  identical `4 + B_LVL` push in `native/ts3native/monojit.cpp` (~line 573) to `8 + B_LVL`.

### B2 measured first, not built (2026-11-08, branch `feature/monojit-exits-b2` from 870655d8) [measured, host]

Brief: measure the vt-arg / vt-return call share first; under ~1 ms/s held back, stop and report. Source: the merged base's
count run `probe-out/jx/m-count-ce1.log` (worktree `monojit-exits`; CallExit=1, VtOps=1, LAZY=1 INLINE=2 WIDE=1), no new runs.

- Held back by vt-arg / vt-return calls (`tools/jit-hot.ts --count`, threshold 1.0, CallExit=1): **0.0 ms/s of 40.8**. The 8
  held-back call / callvirt methods (GetComponent 4.5, InteractionObjectPair.Equals 3.7, Array.Copy 3.1, GetObject 3.0,
  SimClock.ElapsedTimeInMinutes 2.7, get_Parent 2.2, get_ObjectId 1.7, GetActorsOfType 1.3) bail at `no lazy body` (callee not
  compiled; Array.Copy = FastCopy icall), `cache class`, `null this`, `depth slot`: none is a vt-signature reason. `boxed vt`
  bails: 16 in the whole run.
- vt args need no work: they are pointers into the caller's vt area, passed as any stackval, and the return-slot word is
  already the caller's vt cursor (EmitLazyCall commit) with `vtresult` compiled since C1.
- Lazy ret.vt (EmitLazyRetVt): with CallExit=1 a ret.vt in a lazy body already keeps the body lazy and mats at the ret.
  ret.vt mats in the run: **501 of 691778 mats (0.07%)**, 16.0 M lazy calls; 441 are corpus fixtures (JitKI.Def 160, JitKB.Get
  120, JitKB.At 81, JitKT.MkBig 80), the only real callee is List`1.get_Item 60 (of 72370 calls). Normal-body ret.vt exits
  (29927) are frame ends of bodies entered from the interpreter, which a lazy ret.vt does not change. Only CallExit=0 would
  gain (ret.vt blocks lazyOk there: 789 vs 1031 lazy bodies, not all vt), and CallExit=0 is not the target mode.
- Census CSV model (`est_ms_s_behind`, merged CE=1): ret.vt 1.98 ms/s in 8 rows, but 1.21 of it is static basis (get_ObjectId,
  Slots.GetParentId, Corners.get_Item: host-only receivers) and the run-basis rows are the normal-body ends above.
- Decision: B2 as specified not built (gain below the ~1 ms/s bar). No code, corpus or JitCorpus.cs change on this branch.
- Where vt time really sits: **newobj of a value type**, held back newobj 9.0 ms/s in 3 methods (Enumerator.get_Current 5.5,
  0.5 exits per execution; List`1.GetEnumerator 2.3, 1.0; Slots.GetParentId 1.2, 1.1). That these newobj sites construct
  structs (KeyValuePair, List`1.Enumerator, ObjectGuid) is [inferred: Mono 2.x class library source, not checked in the mint
  code]; confirm with a per-site class print in count mode before building. EA path: newobj 0xE561F9 sets [rec+0x20] = vt
  cursor and runs the call block 0xE56287 (see B design). Candidate next piece "B2b: newobj.vt", not started.

### B1 / B3 (2026-11-08, branch `feature/monojit-exits-b1` from d572517d) [measured, host]

- B1 ops, group 1024, kinds K_STFLDVT..K_STOBJVT before K_FCMP: `stfld.vt` 0x063, `ldelem.any.vt` 0x111, `stelem.any.vt`
  0x112, `ldobj` 0x0FE (value-type classes only), `stobj.vt` 0x100. Handlers re-read [verified, disassembly]: stelem.any.vt
  does check null (0xE5947E) and bounds (unsigned, 0xE5949D); stobj.vt and ldobj call 0xE6F5A0(class, 0), which is
  [class+0x54]-4 once `[class+0x14] & 4` is set, else the layout init 0xE6EE20 runs. Ours: null / bounds exit before any
  change; ldobj / stobj.vt read the size once at compile time and compile as an exit for good if the layout flag is clear or
  the class fails EA's value-type test 0xE52160 (replicated inline: type 0x11 and not enum flag 0x10, or 0x15 with a
  value-type container). EA has no barrier on these stores; ours call `e.satb(0, base, disp, size)` first.
  Not covered: `ldelema` (0x110, 40 sites), `vtldfld.*` (0x4B-0x55, no exits seen in host), ldobj of a non-value type.
- B3: one block before `cases()` in `tools/jit-corpus.ts` (`KB1`, `VTOPS_CASES`, `VTOPS_LIST`), class `JitKB` + structs
  `JitB1` (1 byte), `JitBOdd` (7), `JitBRef` (refs); 5 cases appended last (StFld/null, Elem + bounds/null, Refs ldobj /
  stobj via ref locals / array elements / heap fields / generic ref T of int, string, enum, Gen T[], Coll List / Dictionary).
- Corpus verify (238 cases, `JITLAZY=1 JITINLINE=2 JITWIDE=1 JITCALLEXIT=1`): VtOps=1 0 differ 0 unstable, ok 43156
  MISMATCH 0; VtOps=0 same; VtOps=1 lazy off ok 43794 MISMATCH 0; VtOps=1 CallExit=0 ok 43251 MISMATCH 0.
- Replay `probe-out/jit-replay-all.txt --mode verify --lazy`, CallExit=1: VtOps=1 5468 inputs, 5282 ran, 0 differ, 0 faulted,
  ok 1413749 MISMATCH 0; VtOps=0 identical (List.get_Item / Add, Dictionary.TryGetValue inputs are in it).
- Count run (same flags): exits at the five ops 5593 -> 122 (120 = ldobj of int / string / enum by design, 2 null / bounds);
  non-ret exits 970244 -> 964786. List`1.get_Item 71 -> 2 exits, Enumerator.get_Current 31894 -> 30342, TryGetValue 173 -> 113,
  List`1.Add 844 -> 568. Census held back unchanged: 53.7 ms/s in 15 methods, 0.0 ms/s attributable to any vt op before
  and after (the brief's "ldfld.vt 12.9 ms/s" is GetPrev, held back by callvirt: part A).
- Bench (`jit-bench.ts --run --reps 5 --no-write`): total modelled 19.4 (VtOps=0) vs 18.6 ms/s (VtOps=1), within noise: a
  game job (bp-jit-batch) and the A agent's host runs were on, every "old" ns rose ~20% between the arms. Rerun quiet.

## Measurements so far [measured, host]

Census model: held back = sampled self ms/s of hot methods whose exits per execution exceed 0.1, not counting ret
(`tools/jit-hot.ts`). Runs: `MONOHOST_JIT=on MONOHOST_JITCOUNT=1 MONOHOST_JITLAZY=1 MONOHOST_JITINLINE=2 MONOHOST_JITWIDE=1`.

- Census undercount fixed: before this branch, exits taken inside lazy bodies were not registered as exit sites in
  count mode (EmitExit, lazy pass). Master census: held back 41.7 ms/s. Fair "before" (this build, CallExit=0):
  **57.9 ms/s** (callvirt 28.3 in 8 methods, call 17.1 in 6, newobj 7.8, box 2.8, con.callvirt 2.0, unbox 0.5).
- CallExit=1: **53.7 ms/s** (callvirt 22.9 in 4, call 12.3 in 5, newobj 9.0, newarr 4.8, con.boxcallvirt 2.8,
  con.callvirt 2.0, unbox 0.5). Lazy bodies 707 to 931, guard sites filled 100 to 151, pure icall sites 22,
  boxed value-type guard sites filled 4. Enum.GetHashCode 24685 to 339 exits; get_LotCurrent now compiles;
  Array.Copy exits 2.0 to 1.0 per execution.
- Corpus, verify mode, CallExit=1: lazy on 233 cases 0 differ 0 unstable, per-call verify ok 42585 MISMATCH 0;
  lazy off ok 42783 MISMATCH 0. CallExit=0, same build, lazy on: 233 cases 0 differ, ok 42590 MISMATCH 0.
- Not run yet: replay (`tools/jit-replay.ts --mode verify --lazy` on the merged jit-capture files), `tools/jit-bench.ts`,
  `tools/jit-exit-census.ts` before/after CSVs.

Scratch helpers (git-ignored): `probe-out/jx/run.ts <tag> K=V...` (one monohost run, log to `probe-out/jx/<tag>.log`),
`probe-out/jx/x86d.py <exe> 0xVA count` (capstone disassembly; do not rename to dis.py, it shadows the stdlib).

## A: design (done)

1. Hot exits keep a lazy body. `JitCompiled` lazyOk loop: with CallExit a hot K_EXIT (and a value ret) no longer
   clears lazyOk; the exit writes the frames first (JitLazyMat), as the cold throw exits already did. Gain: callers
   keep calling the callee lazily, the frame is written only on the run that really exits.
2. Pure Array icalls inline (`JitPureIcallsOk`, `JitPureIcall`, `EmitPureIcall`): GetLength 0xE79FC0,
   GetLowerBound 0xE7A000, GetRank 0xE79FB0 [verified bytes, checked at install; icall table 0x10B58D4..DC]. Null this
   and dim >= rank exit to EA (EA raises). Sites: `call`/`callvirt` with iflags 0x1000, not virtual, class System.Array.
3. Boxed value-type receiver through a class-guarded callvirt: EA's callvirt passes box + 4 when the receiver class is
   a value type and the target has 0x400 (0xE54B2C-41 [verified]). `JitGuardSite.unbox` (4 or 0) is set in
   `JitGuardFill`; the site adds it to ecx after the entry check. con.callvirt (0xED) keeps the bail.
4. Count mode: lazy exits are exit sites of their own (EmitExit lazy pass), so the census sees them.

Files: `native/monohost/monohost-jit.cpp` only (globals after `g_jitLazy`; `JitGuardSite`; `JitGuardFill`; EmitExit
lazy pass; block before `EmitFcmp`; `EmitLazyCall`; emitter default branch after the C5 icall block; `JitCompiled`
lazyOk loop; `InstallJit`; `JitCountReport` line `jit callexit: ...`).

## A: findings from the bail census (debug code removed again)

Per-bail counters on lazy call sites (one host count run, CallExit=1):

- Class-guarded callvirt sites: 856270 entries from normal bodies, 171265 from lazy bodies; bails: depth slot 22492,
  no lazy body 431, cache-class mismatch 302. The depth bail is `depth + 1 (+ pending) >= [ebp-0x30]`: no
  MonoInvocation slot at that depth yet. EA fills that slot by alloca on ves_exec's C stack (0xE562E1: [ebp-0x30] =
  depth+1, `mov eax,0x28; call 0xF7E8E6`, [rec] = esp, [eax+0x10] = 0 [verified]). A lazy site cannot do that from
  inside its call block. [inferred] mostly a host artifact (short ves_exec frames per runtime invoke); not checked in game.
- con.callvirt guard sites (0xED): 27633 bails at "no lazy body": the target has none (String.GetHashCode-like
  callees that are not compiled).
- Direct sites: 596856 bails at "no lazy body", almost all `System.ValueType.InternalEquals` (565508, an icall from
  ValueType.Equals), then Array.FastCopy 7383, Array.Copy 5884, Type.internal_from_handle 387.
- Lazy calls 16.1 M; exits that wrote frames 622349 (653069 frames). Dictionary`2.GetPrev keeps 102531 callvirt exits
  per 133925 executions although its guard sites bail only ~23k times: the rest are mats that start in the callee
  (IEquatableOfTEqualityComparer`1.GetHashCode, 48276 ret exits in one instance) and are booked at the caller's call
  site. Next step A2 below.

## A1-A3 results (branch feature/monojit-exits-a) [measured, host]

**A2 cause [verified: count run + code]: the B_LVL push read the wrong word.** In a lazy body's call site the block's level
is pushed as `push [esp + 4 + B_LVL]; inc [esp]`, but B_VT (round 10 C1) is pushed one word earlier, so it read the outer
block's B_CIP (an ip pointer) instead of its B_LVL. Every lazy body at chain level 2 or deeper got a huge level; its own call
sites then failed the depth test (`depth + level + 1 >= [ebp-0x30]`) on every call and wrote frames there. This was
GetPrev's ~102k callvirt exits (all "depth slot" bails at level 2) and nearly all of the old "depth slot 22492". JitLazyMatC
still stopped at the right block (it stops at a level <= 1), so it was a lost-speed bug, not a correctness bug. Fix: `8 + B_LVL`
(`EmitLazyCall`). **The game port has the same line** (`native/ts3native/monojit.cpp` ~573, `4 + B_LVL`): fix it in A5 (or
before, as its own change). It is not gated by CallExit, so it also helps CallExit=0. Chains of level 3+ really run now (corpus
JitKA.Chain5*).

Count-mode census added (kept, count mode only): `jitbail <caller> <word> <op> <lazy|normal> <guard|direct> <reason>=<n>...`
(one counter per bail reason of each lazy call site) and `jitmat <n> <op> <word> <callee>` (lazy-body exits that wrote frames,
by callee and exit position). After the fix the mats left are: ValueType.DefaultEquals 585503 at its `call` to the icall
InternalEquals (no lazy body: an icall that allocates, so no lazy native call; 94% of all mats, but not a sampled hot method),
Array.Copy 25164 (FastCopy icall), Array.Resize 24945 (newarr), IEquatableOfTEqualityComparer`1.GetHashCode/Equals 18007/9403
(con.callvirt to a target with no lazy body). Bails by reason (CallExit=1): no lazy body 766342, depth slot 1560, cache class 449,
boxed vt 16.

**A3 decision: no slot fill; documented.** With B_LVL right, the depth test bails 1560 times in 16.0 M host lazy calls. A
normal-body-only fill (EA's alloca, 0xE562E1-0xE5631F [verified]: d >= [ebp-0x30] -> [rec] = 0, [ebp-0x30] = d + 1; [rec] == 0
-> esp -= 0x28, [rec] = esp, [esp+0x10] = 0; caller depth < 0x3F) was built and measured: depth bails 1559 -> 21, census
unchanged (40.8 both), corpus green, but 5 more instructions on every normal-body site. Dropped; one comment at the depth
test. The remaining bails are fresh ves_exec frames (every runtime invoke) before EA's first call that deep; the same will hold
in game for native-to-managed calls. Not a docs/51 inference: both numbers are measured.

Census, held back (threshold 1.0 ms/s, same sampled ms/s; count runs on this branch):

| build | CallExit=0 | CallExit=1 |
|---|---|---|
| d572517d (handoff) | 57.9 | 53.7 |
| feature/monojit-exits-a | 45.0 | **40.8** (call 12.3 in 5, callvirt 10.0 in 3, newobj 9.0, newarr 4.8, con.boxcallvirt 2.8, con.callvirt 2.0, unbox 0.5) |

GetPrev (12.9 ms/s) is now "compile". Left in callvirt: GameObject.GetComponent 4.5, InteractionObjectPair.Equals 3.7,
ScriptObject.get_ObjectId 1.7 (accessor fixtures; host-only receivers). Lazy calls from lazy bodies 1.02 M -> 1.88 M.

A1 corpus (`tools/jit-corpus.ts`, one block "docs/537 §20 A1" before the accessor table, cases appended last as EXIT_CASES,
list entries KA_LIST): 8 cases, 241 in all: pure icalls (szarray, rank 2/3, non-zero lower bounds, Rank; dim past the rank,
negative dim, null this), boxed struct receivers (override = box + 4, inherited ValueType.GetHashCode = the box, interface
call that mutates the box, polymorphic interface site, runs of one class, null receiver), lazy callee with a newobj exit,
a five-deep lazy chain (exit at level 5), DivideByZeroException at level 5 after a frame write (thrown and caught in the
compiled caller), exception after the mat. JitKA.Pure takes its arrays from the case body: allocating Array.CreateInstance
inside the verified method gave one verify MISMATCH in every mode (fresh-object pairing of the verifier, case hash equal).
Bench case 29/30 `JitLzAlloc 64` / `JitLzAllocPlain 64` (HostTest.LzAlloc: newobj at the first instruction of every call),
added to `CALL_CASES` in `tools/jit-bench.ts`; not timed yet (A4, quiet machine).

Corpus verify on the final build, 241 cases, 0 differ, 0 unstable in each mode: CallExit=1 lazy on ok 42552 MISMATCH 0;
CallExit=1 lazy off ok 42854 MISMATCH 0; CallExit=0 lazy on ok 42637 MISMATCH 0. Still open for A4: replay verify --lazy,
jit-exit-census CSVs, jit-bench (the game ran correctness jobs, bp/ip-jit-batch, while these host runs ran).

## B: design (not started; for a fresh agent)

Group: c2 mask 1024 (`if (g_jitVtOps) c2 |= 1024;`), new kinds inserted **before K_FCMP** (kinds >= K_FARITH must stay
last, group 512). Already compiled in host and game (C1, #220): ldfld.vt, ldarg.vt, ldloc/stloc.vt, starg.vt,
stinarg.vt, dup.vt, vtresult, ldsfld.vt. Note: the "ldfld.vt 12.9 ms/s" in the brief equals GetPrev's sampled ms/s;
in the host census GetPrev's top exit is callvirt (part A), not a vt op.

EA handlers [verified by disassembly]:
- stfld.vt 0xE56C0A: obj = [esi-16], null -> NRE 0xE5947E; dst = obj + word ip[1]; src = [esi-8]; size = DWORD at
  ip+2; memcpy, no barrier in EA (ours: satb on heap stores per the SATB rule); vt cursor -= align8; esi -= 16; ip += 4.
- ldobj 0xE560CD: class = items[ip[1]]; vt test 0xE52160 (type byte +0xA6 == 0x11 and not enum flag 0x10, or
  GENERICINST via container); size 0xE6F5A0 = [class+0x54]-4, needs class flag 4 (else class init runs: exit);
  [esi-8] = cursor; memcpy; cursor += align8; no null check.
- ldelem.any.vt 0xE57382: arr = [esi-16] null -> NRE; idx = [esi-8] unsigned >= [arr+8] -> IOOR 0xE5949D;
  size = DWORD at ip+2; [esi-16] = cursor; cursor += align8; memcpy(cursor, arr + idx*size + 0x10); esi -= 8.
- stelem.any.vt 0xE575CF: arr = [esi-24], idx = [esi-16], src = [esi-8]; cursor -= align8;
  memcpy(arr + idx*size + 0x10, src); esi -= 24; no barrier in EA.
- ret.vt 0xE5923E: memcpy([inv+8] -> data.p, [esi-8], DWORD at ip+1), leftover check against [inv+0x18], frame return
  0xE543C7. Lazy version (EmitLazyRetVt): `lea eax,[ebx+w3E+8]; cmp esi,eax; ja slow`; dst = [[esp+4+B_RSLOT]],
  src = [esi-8], size = DWORD at ip+1, VtCopy; arena pop [chunk] = ebx; ret. Then allow ret.vt in the lazyOk loop.
- newobj of a value type 0xE561F9: [rec+0x20] = vt cursor then the call block 0xE56287; deep, skipped so far.
Helpers to reuse: `VtLoad/VtStore/VtAdd/VtCopy` (memcpy thunk 0xF7E8A6), `e.satb(w, base, disp, bytes)` (clobbers
edx/eax/ecx, keeps the base), exits via `exitRel[]` before any change.

## Remaining work, split

| Piece | What | Touches | Conflicts with |
|---|---|---|---|
| A1 | corpus kernels: pure icalls (szarray, multi-dim with bounds, bad dim, null), boxed-vt receivers through callvirt (override / no override / polymorphic site), callees with hot exits under lazy (newobj in callee, chains, exception after mat); bench case: always-exiting callee | `tools/jit-corpus.ts` (new list after ACCESSOR_CASES in `cases()`, kernel consts, `jitList()` and its self-check string) | B3 (same file: append-only, merge by hand) |
| A2 | why callee lazy bodies mat so often (622k mats): per-method mat counter in count mode, then fix the top cause (e.g. String.GetHashCode / ValueType.InternalEquals as pure or EA calls without mat) | `JitLazyMat`, `JitCountReport`, maybe `EmitLazyCall` direct branch | B2 (EmitLazyCall) |
| A3 | depth-slot bail: decide (document as host artifact with a docs/51 entry, or a normal-body-only slot fill like EA's alloca) | `EmitLazyCall` depth test | B2 |
| A4 | final host gates for A: corpus verify/on, lazy on/off, replay verify --lazy, jit-exit-census before/after CSV, jit-bench | none (runs) | none; done on the merged base except jit-bench timing |
| A5 | game port of A: `[MonoJit] CallExit`, counters reserved, selftests in `monojit-test.h`, build, version + pair, queue `tools/harness/queues/jit-exits.json` | `native/ts3native/monojit.cpp`, `monojit-test.h`, ini reader, `docs/data/builds.json`, docs/registry.md | B5 (same files: do sequentially, A5 first) |
| B1 | vt field / element / object ops: stfld.vt, ldelem.any.vt, stelem.any.vt, ldobj/stobj vt, kinds + table + c2 1024 | enum before K_FCMP, `g_opInfo` table, emitter switch cases in `JitCompiled`, `InstallJit` env line | A only at `InstallJit` (one line each) |
| B2 | vt call args / returns: lazy ret.vt (EmitLazyRetVt), lazyOk ret.vt, call sites with vt args or returns (vt cursor and return slot word in EmitLazyCall) | `EmitLazyRet` (new sibling), lazyOk loop, `EmitLazyCall` commit part | A2, A3 (EmitLazyCall, lazyOk loop) |
| B3 | B corpus kernels (struct fields, struct arrays, generic Dictionary/List with struct keys, ret of structs through lazy chains) | `tools/jit-corpus.ts` | A1 (append-only) |
| B4 | B host gates (as A4) | runs | none |
| B5 | game port of B (`[MonoJit] VtOps`) | as A5 | A5 |
| D | docs: docs/537 §20, PLAN.md line, docs/index.md line, docs/51 entries (reserve via `node tools/registry.ts reserve ledger`) | docs | each part adds its own paragraph |

Parallel without touching the same functions: A1, A4 (after A1), B1, B3, B4 (after B1). A2/A3 and B2 both edit
`EmitLazyCall` and the lazyOk loop: run them one after the other, or B2 waits for A2/A3. A5 and B5 share the game files:
sequential. Builds: no builds or CPU-heavy host benches while a timing job runs (probe-out/queue/job-running against
golive-unseen-gate2*, hitch-foreign, f1-callpath, final-bundle).

### B2b-measure: which class each held-back newobj builds (2026-11-08, branch `feature/monojit-exits-b2b` from 21f4cdba) [measured, host]

Code: `native/monohost/monohost-jit.cpp` `JitCountReport` only, count mode only (zero cost otherwise): one line per newobj
(0xF4) exit site, `jitnew <meth> <mint word> <exits> <vt> <class flags +0x14> <class>..ctor`. vt = EA's test 0xE52160 (byval type
0x11 and not an enum, or 0x15 with a value-type container), the same formula as `ldobj`. The ctor / class come from word 1 of the
op: `items[ip[1]]`, class = `[[item+8]+8]` [verified: handler 0xE56152 reads `items[ip[1]]`, `[+8]`, `[+8]` = class, then
0xE561F9 tests `[class+0x14] & 8` and calls 0xE52160]. Run = the merged-base recipe (`MONOHOST_JIT=on JITCOUNT=1 JITLAZY=1
JITINLINE=2 JITWIDE=1 JITVTOPS=1 JITCALLEXIT=1`; log `probe-out/jx/b2b-count-ce1.log`, 596 newobj sites). Census unchanged (40.8 ms/s
held back in 14 methods, newobj 9.0 in 3). IL offsets are not printed (the site key is the mint word; the IL offset needs the dump
map). Per instance counts are summed over the generic instances (Enumerator.get_Current has 14 KeyValuePair instances).

| method | mint word | class | value type | exits (run) | held back ms/s |
|---|---|---|---|---|---|
| Enumerator.get_Current (Dictionary`2) | 11 | `KeyValuePair`2` | **yes** (flags A1040D, bit 8) | 27436 (14 inst) | **5.5** (0.5 exits per execution; the other newobj words here are exception throws, <= 17 exits each) |
| List`1.GetEnumerator | 1 | `List`1.Enumerator` | **yes** | 226 (24 inst) | **2.3** (mostly static basis: 1.0 exits per execution, 141 calls in the run) |
| Slots.GetParentId | 10 | `ObjectGuid` | **yes** | 4 (2 inst) | **1.2** (static basis only: host-only receivers, almost never run) |
| Members.GetActorsOfType | 9 | `List`1` | no | 5 | 1.27 (static basis; the sampled-method row says 10.2 exits per execution at the call site, not this newobj) |
| Dictionary`2.set_Item / DoAdd | 73 / 48 / 89 | `Slot` (flags A00405, a reference class here) | no | 5383 / 1634 / 258 | 0.43 each, under the bar |
| Array.Resize (newarr, not printed) | - | element type of T[] | n/a | - | 4.8 (0.5 exits per execution); only newarr row >= 0.5, `Array.Resize` can also raise (docs/537 §20) |

Read: all 8.0 ms/s of the three named newobj methods build **value types** (KeyValuePair, List.Enumerator, ObjectGuid); the earlier
[inferred] is now [verified]. 5.5 of it is one site (Dictionary Enumerator.get_Current, KeyValuePair) with real run traffic; the
2.3 and 1.2 rows come from the static basis (method sampled in game, exits barely run in the host), so only the 5.5 is host-proven.
Corpus verify (CallExit=1 VtOps=1 lazy on, INLINE=2 WIDE=1): 246 cases, 0 differ, 0 unstable, ok 42987, MISMATCH 0 (identical to the
merged base); `node tools/selftest-all.ts`: 217 PASS, 0 FAIL.

What a native `newobj.vt` needs [inferred from the 0xE561F9 handler read, not built]:
1. Size at compile time: `[class+0x54]-4` (same as ldobj); compile as a permanent exit if class flag 4 (layout) is clear or 0xE52160 says not a value type.
2. Zero-init: EA does not zero the struct in the handler; the ctor writes it (`[rec+0x20]` = vt cursor, `[ebp-0x50]` = cursor). We must `rep stos`/mov-zero the `align8(size)` bytes (KeyValuePair is 8 to 16, Enumerator ~16, ObjectGuid 8) to be exact against reused vt arena bytes only if the ctor leaves fields unset.
3. Cursor: ctor `this` = the new cursor value, the vt cursor then advances by `align8(size)` and the result slot gets that pointer (the 0xE561AB-0xE561CC tail copies `[rec+0x20]` to the stack slot); that advance sits in the call block, not read yet.
4. Ctor call goes through the existing lazy `EmitLazyCall` direct path (this = the vt pointer, args from the stack, no class guard); a ctor with no lazy body (e.g. KeyValuePair`2..ctor not compiled) bails, so KeyValuePair/Enumerator/ObjectGuid ctors must be in the compile list first.
5. Exits stay exact: null-class / init / exception paths exit before any cursor change (as B1), ret.vt of the caller keeps the lazy body (B2 note: only 0.07% of mats), game port repeats the `8 + B_LVL` fix in the same piece.

### B2b-build: native newobj of a value type (2026-11-08, branch `feature/monojit-exits-b2b`) [measured, host]

Switch: host env `MONOHOST_JITNEWVT=1` (default off; needs `MONOHOST_JITLAZY=1`); game key `[MonoJit] NewVt` not added yet.

EA facts read for this piece (TS3W.exe disassembly, `x86d.py`):
- [verified] newobj 0xE56152: `[inv+0x1C]` = ip; ctor = `items[ip[1]]`, class = `[[ctor+8]+8]`, written to the caller record
  `[rec+0x18]` (rec = `[ebp-0x2C]`); ip += 2 words. `[class+0x24] == [0x1220E8C]` takes the array-ctor path 0xE56188 (not ours).
  0xE561F9: `[class+0x14] & 8` and 0xE52160 (VALUETYPE and not enum, or GENERICINST of a value-type container) -> 0xE5620C:
  this = `[ebp-0x50]` = `[rec+0x20]` = the vt cursor `[ebp-0xC]`. A valuetype class failing 0xE52160 (enum / primitive) gets an
  8-byte zeroed `[rec+0x20]` as this (0xE56217): not compiled here.
- [verified] **No zero fill**: the handler does not clear the struct bytes; the ctor writes them. Ours matches (no fill).
- [verified] 0xE5625F: esi -= params*8; stack_args = esi, or **0 with no params** (0xE56277); `[ebp-0x28]` = 1 then the call block
  0xE56287 writes it to the callee record `+0x14` (0xE56346) = "newobj pending". The caller record keeps esi = args start, ip + 2
  and the cursor **unchanged** (0xE562BC).
- [verified] Frame return 0xE543C7: restores esi / cursor from the caller record; callee record `+0x14` != 0 jumps to the newobj
  post 0xE561B0 (skips the return-value copy at 0xE54486): class `[rec+0x18]` valuetype and not enum -> `[esi]` = `[rec+0x20]`,
  `[esi+4]` = `[rec+0x24]` (not written by this newobj: a stale word of that record), esi += 8.
- [verified, jit-dump] **The cursor advance is not in newobj or the call block**: the transform puts `vtresult <size> 0 0` right
  after every value-type newobj (List`1.GetEnumerator `vtresult 000C`, Slots.GetParentId `vtresult 0008`, ...). vtresult (compiled
  since C1) moves nothing (the value already sits at the cursor) and adds align8(size). So the size at compile time is not needed;
  layout flag 4 is kept only as a conservative gate.
- [inferred] `[inv+8]` (retval) for a vt newobj is the stale `[ebp-0x54]`; a void ctor does not read it (ours: B_RSLOT as usual).

Built (`native/monohost/monohost-jit.cpp`):
- `JitNewVtClass` (the tests above at compile time: not the array path, flag 8, flag 4, 0xE52160); `JitLazySite` op 0xF4 = a
  direct site of the ctor (transformed, instance, value-type this, no native body); a class failing any test stays EA's newobj for good.
- `EmitLazyCall` `nvt`: this = cursor (VtLoad), no receiver slot (rsp = args start), no return-slot word, B_ARGS = 0 with no
  params, ripW 2, no return copy; after the native ctor: `[esi]` = cursor, `[esi+4]` = `[slot(depth (+ own level in a lazy body)) + 0x24]`
  (EA's stale high word), esi += 8. All bails (no lazy body, ForceJIT, depth slot, MonoInvocation, arena) run before any write and
  take EA's newobj.
- `JitLazyMatC`: a block whose call ip is a newobj (JitOrigWord == 0xF4) also gets caller record `+0x18` = class, `+0x20` = this and
  callee record `+0x14` = 1, so EA's own frame return does the newobj post when the ctor exits after the native call (throw, any exit).
- `JitPreTransform` transforms value-type ctors of newobj sites; lazyOk loop and the emitter default branch take 0xF4 as a lazy site.
- `kJitList` + `ObjectGuid..ctor` (KeyValuePair`2..ctor and Enumerator..ctor were listed). Count report line `jit newvt: ... sites N`.

Corpus (`tools/jit-corpus.ts`, block `KB2` / `NEWVT_CASES` / `NEWVT_LIST` appended after the B3 block, older indices unchanged;
JitCorpus.cs regenerated): JitKN.Sizes (structs of 1, 7, 12, 20 bytes and one with references; result stored, passed, returned,
boxed, in arrays and fields; a `: this()` ctor), Thr / CatchThr (ctor throws ArgumentException / DivideByZeroException, caught
outside and in the compiled caller), Chain / ChainThrow (newobj inside ctors four deep under a lazy chain three deep; innermost ctor
divides by zero), Guid (ObjectGuid and an ObjectGuid-like struct), Coll (foreach over List / Dictionary: Enumerator and
KeyValuePair newobj, KeyValuePair of struct key and value). Two fixture lessons: `"Class.*"` never matches `Class..ctor`
(JitListed splits at the last dot), so the struct ctors are listed by full name (first runs had every corpus ctor as "no lazy body");
a `Dictionary<ObjectGuid, int>` made inside the verified method gave one verify MISMATCH with NewVt on **and** off (fresh-object
pairing, as JitKA.Pure), so Guid takes an array from the case body. New self-check: the compile list fits monohost (122 of 128 names).
Count run: corpus ctor mats seen at JitNThr..ctor div.i4 and JitNMid..ctor vtldfld.u1 (the mat newobj path runs).

Corpus verify, 251 cases (monohost count), `JITINLINE=2 JITWIDE=1 JITVTOPS=1`, each 0 differ 0 unstable:

| NewVt | CallExit | lazy | per-call verify ok | MISMATCH |
|---|---|---|---|---|
| 1 | 1 | on | 60329 | 0 |
| 1 | 1 | off | 62592 | 0 |
| 1 | 0 | on | 60738 | 0 |
| 1 | 0 | off | 62593 | 0 |
| 0 | 1 | on | 61034 | 0 |
| 0 | 1 | off | 62593 | 0 |
| 0 | 0 | on | 61353 | 0 |
| 0 | 0 | off | 62593 | 0 |

An earlier matrix run (before the Guid fixture change) also had one `System.Type.GetTypeFromHandle` MISMATCH (Single[] region) in
NewVt=1 lazy off, where no newobj site compiles (needs lazy); it did not repeat in the final matrix [inferred: flaky, unrelated].

Replay `probe-out/jit-replay-all.txt --mode verify --lazy`, CallExit=1 INLINE=2 WIDE=1 VtOps=1: NewVt=1 and NewVt=0 each 5468 inputs,
5282 ran, 0 differ, 0 unstable, 186 skipped, 0 faulted, ok 1413749, MISMATCH 0 (final build, two runs each). Logged flake: one
earlier NewVt=0 replay stopped "on a fault" at input 3407 (no compiled-code fault line; rerun of the same build passed all inputs),
the known host rebuild flake.

Census held back (`tools/jit-hot.ts --count`, threshold 1.0, CallExit=1, same 105.1 ms/s sampled in 32 methods); CSVs
`docs/data/results/2026-11-08-jit-exit-census-newvt{0,1}-ce1.csv`:

| group | NewVt=0 | NewVt=1 |
|---|---|---|
| call | 12.3 (5) | 13.5 (6) |
| callvirt | 10.0 (3) | 10.0 (3) |
| newobj | **9.0 (3)** | **-** |
| newarr | 4.8 (1) | 4.8 (1) |
| box | 2.8 | 2.8 |
| con.callvirt | 2.0 | 2.0 |
| unbox | 0.5 | 0.5 |
| **total** | **40.8** (14) | **33.0** (12) |

Enumerator.get_Current (5.5) and List`1.GetEnumerator (2.3) now "compile"; Slots.GetParentId (1.2, static basis, host-only receivers)
moves from newobj to its `call` (0.6 exits per execution). Value-type newobj exits in the run: 29733 -> 15 (BCL KeyValuePair /
Enumerator / ObjectGuid sites 28275 -> 5: depth-slot bails); 132 newvt sites. `node tools/selftest-all.ts`: 217 PASS, 0 FAIL.
No jit-bench timing (per brief).

Next:
- **B5 port: newobj.vt to game after A5 lands** (`native/ts3native/monojit.cpp`, `[MonoJit] NewVt`, the JitLazyMatC newobj fields,
  `ObjectGuid..ctor` in the built-in list; same files as A5, so after it).
- jit-bench on a quiet machine (A4 open item) can include a newobj.vt case.

## C: remaining call exits plan (2026-11-08, planning only, no compiler code) [measured from existing host logs unless marked]

Sources (no new runs; no monohost run was needed, `probe-out/queue/job-running` names a bp-castrim smoke, not a jit-batch):
`probe-out/jx/m-count-ce1.log` (worktree `monojit-exits`, merged base) and `probe-out/jx/b2b-count-ce1.log` (worktree
`monojit-exits-b2b`, adds `jitnew`), `tools/jit-hot.ts --count`, census CSV `docs/data/results/2026-11-08-jit-exit-census-merged-ce1.csv`,
callee lists from `game-copy/analysis/callgraph/index.json` (callee set per method; the log does not print the callee, and the
bail word does not say which IL site it is, so site-to-callee pairs below are [inferred] from op kind and callee kind).
Bail row format: `jitbail <caller> <mint word> <EA call | EB callvirt | EC class-guarded callvirt | ED con.callvirt | F4 newobj> <lazy|normal> <direct|guard> <reason>=<n>`.
Branches `feature/monojit-exits-b2` / `-b2b` (not on master) hold the B2 measurement ("B2 measured first, not built": vt-arg and
vt-return calls hold back 0.0 ms/s) and the "B2b-measure" jitnew section, plus an uncommitted newobj.vt edit in `EmitLazyCall`
(worktree `monojit-exits-b2b`, `nvt`, 54 lines). This plan assumes that edit is piece C1.

### What the data says

- **Two numbers per row.** "Held back" (census, 40.8 ms/s) is the whole sampled self time of each method that exits. The model's
  "behind the exit" (CSV `est_ms_s_behind`) is what compiling the exit can plausibly save: summed over all non-ret, non-cold rows it
  is **8.6 ms/s** (call 3.3, callvirt 3.2, newobj 0.8, con.boxcallvirt 0.4, box 0.4, ldelema 0.15, newarr 0.12, con.callvirt 0.09,
  unbox 0.06). The honest ceiling for all of C is about 8.6 ms/s, not 40.8 [model, static]. Pieces below quote both.
- **Seven of the eight call methods never ran in the host.** GetComponent, InteractionObjectPair.Equals, GetObject,
  ElapsedTimeInMinutes, get_Parent, get_ObjectId, GetActorsOfType are absent from the log's `jitcalls`; their bail rows come from
  the accessor fixtures (counts 1 to 35) and their ms/s is "static basis" (e.g. "18 of 111 positions"). Only Array.Copy has real traffic
  (54196 calls). So their reasons are shape, not frequency; a game count run (C0) is the only way to get real exits per site.
- **Whole-run bail traffic by reason** (b2b-count-ce1.log, sum of all sites): `no lazy body` over 99% (EA lazy direct 611219, EC normal
  direct 72951, EA normal direct 54800, ED lazy guard 27475); `depth slot` 1437; `cache class` 442; `null this` 1; `boxed vt` 16.
  Biggest single sites: ValueType.DefaultEquals w16 (call to the InternalEquals icall) 585503 + 803; Array.Copy w80 28065 and w44
  25166 + 1347 (FastCopy icall); IEquatableOfTEqualityComparer`1.GetHashCode w4 18007 / Equals w17 9403 (ED guard). The `JitInl` /
  `JitOut` rows (about 24100 each) are corpus fixtures, ignore.

### Per method and site

| Method (held back / model behind, ms/s) | Site (word, op) and exact bail | Callee | What compiling takes | Risk |
|---|---|---|---|---|
| GameObject.GetComponent 4.5 / 0.98 | w7 EA normal direct `depth slot`=1 | `Type.GetTypeFromHandle` (has a lazy body, `jitlazy chain`) | Nothing: A3 decided depth slot is a host artifact (1559 of 15.8 M lazy calls). Recheck in game (C0). | none |
| | w35 EC normal guard `cache class`=2, `no lazy body`=1 [inferred: the virtual callee] | `ObjectComponent.get_BaseType` (virt, one override per component class); also List`1.get_Item / get_Count (cv, have bodies once the List<ObjectComponent> instance runs) | Compile the overrides (trivial `typeof` getters) so each class has a lazy body: list addition only (C2). A site that sees several component classes thrashes EA's one-class cache: second-level guard (C6) only if C0 shows `cache class` above 1% of the site. | low (C2), medium (C6) |
| InteractionObjectPair.Equals 3.7 / 0.35 (+1.06 ret) | w17 EC normal guard `null this`=1 | `get_Target` / `get_InteractionDefinition` (cv), `Object.Equals` (virt) | `null this` is the exact exit (EA throws NRE): a fixture hit, never compile it. The rest is `ret` frame teardown, counted outside held back. Nothing to build. | none |
| Array.Copy 3.1 / 0.14 (run basis, 54196 calls, 1.0 exit per call) | w80 EA normal direct `no lazy body`=28065; w44 EA lazy direct `no lazy body`=25166 (+1347 normal) [two overloads share the name; w80 is the 5-arg Copy calling FastCopy, w44 the same call seen inside a lazy chain or the 3-arg Copy] | `Array.FastCopy` (icall, returns bool) and, for w44, `Array.Copy` (5-arg) | FastCopy is an icall that does not allocate but writes element memory: a pure icall (like GetLength, `JitPureIcall`) only for same-element-type non-reference arrays, with null, bounds and type checks exiting first; reference arrays need `e.satb` per copied range (SATB rule), so they exit. The 5-arg Copy then gets its own lazy body. | medium (barrier, type-compat exactness) |
| GameObject.GetObject 3.0 / 1.06 | w12 EA normal direct `depth slot`=1 | `ObjectGuid.op_Inequality` or `Simulator.GetProxy` | depth slot: no action (as above). | none |
| | w24 EA normal direct `no lazy body`=2 | `Simulator.GetProxy` (engine icall bound through ScriptCore) and `ScriptProxy.get_Target` (ifaceN) | GetProxy is an engine native (allocates / re-enters the engine): the exit stays. Compile `get_Target` only; low value because the exit stays at GetProxy. | low value |
| SimClock.ElapsedTimeInMinutes 2.7 / 0.78 | w3 EA normal direct `no lazy body`=35 | `SimClock.ElapsedTime(float&)` (static, not in the compile list) | Add `ElapsedTime` to the compile list; the x87-exact float templates exist (census "L: x87-exact float (B2)"). List addition plus an accessor corpus case. | low |
| GameObject.get_Parent 2.2 / 1.76 | w1 call, 2 mats in a lazy chain (`jitmat 2 call 1`), no bail row | `ScriptObject.get_ObjectId` (ret.vt, ObjectGuid), `Slots.GetParentId` (engine native, vt return), `GameObject.GetObject` | get_ObjectId into the compile list (C2); GetParentId stays an engine call (exit). The B2 measurement says vt returns are not the blocker (ret.vt mats 501 of 691778). | low |
| ScriptObject.get_ObjectId 1.7 / 1.04 | w16 EC normal guard `no lazy body`=2 | `ScriptObject.get_Proxy` (call), then `ScriptProxy.get_ObjectId` (ifaceN, 4 implementers: ScriptProxy, PrepareTask, StateMachine, AutoSave) | Compile `ScriptProxy.get_ObjectId` and `get_Proxy` (C2). Polymorphic only in theory: in game the receiver is almost always ScriptProxy [inferred]. | low |
| Members.GetActorsOfType 1.3 / 0.26 | w5 EA, w37 EC, w69 EB, all normal direct `no lazy body` (5, 7, 5) | `GetPrunedSimDescriptionList`, `List<SimDescription>` GetEnumerator / get_Current / MoveNext / Add / `.ctor`, `SimDescription.HasSameCASAGSFlags` / `get_CreatedSim` | In the host the `List<SimDescription>` instances never ran, so no lazy body exists: host artifact [inferred]. In game the generic bodies exist. Left over: `GetPrunedSimDescriptionList` (big, not hot) and the ref `List..ctor` newobj (allocation, C4). Nothing separate to build; re-measure after C1 and C0. | none |
| Array.Resize (newarr) 4.8 / 0.12 (+ ret 0.23) | calls at w20 / w51 `depth slot` (1 to 19 per instance); newarr itself exits (`M: allocation helper`, 0.5 exits per call) | `Array.Copy` (5-arg), `Math.Min` | newarr needs the allocator call (C4). The depth slot bails are the A3 artifact. | high (GC) |
| box / con.boxcallvirt 2.8 (DefaultComparer.Equals) / 0.88 | w13 / w25 `box` exits (324 + 798 mats); ED guard w17 `boxed vt`=16 | `box` allocates; `Object.Equals` callee | C4 (allocation), then C5 (ED boxed receiver). | high |
| con.callvirt 2.0 (IEquatableOfTEqualityComparer`1) / 0.09 | ED lazy guard `no lazy body`: GetHashCode w4 18007, Equals w17 9403; EB w6 165, w25 268 | `T.GetHashCode` / `T.Equals` of the closed generic T | T-specific callees without lazy bodies (host instances). C0 names the real key types in game; then list additions (C2). | low |
| newobj 9.0 (Enumerator.get_Current 5.5, List.GetEnumerator 2.3, Slots.GetParentId 1.2) / 0.83 | F4 exits; all three build value types (KeyValuePair, List`1.Enumerator, ObjectGuid) [verified, jitnew] | the struct ctors (must be in the compile list) | C1: `newobj.vt`, the `nvt` path in `EmitLazyCall` ("What a native newobj.vt needs" in the B2b section). | medium (vt cursor, high dword of the record) |
| unbox 0.5 / 0.06 | `Int32.Equals` class check with cache | none | skip (under the 1 ms/s bar) | - |

### Build pieces

| Piece | What | Touches | Sequential / parallel-safe | Model | Est. wall | Gain: held back / model | Risk |
|---|---|---|---|---|---|---|---|
| C0 | Game count run with `CallExit=1 VtOps=1` on BP/IP/SV: real exits per site and callees (the 7 host-silent methods; cache class vs no lazy body in game). Needs the A5 port (`feature/monojit-a5`, ts3native 0.36.200, not on master) with count output; schedule from 18:00 PHT, not during a timing job. | queue json, `tools/jit-hot.ts --count` | parallel-safe (a game job, no edits) | Sonnet | 10 min agent + 3 x 15 min game | decides C2 and C6 scope | low |
| C1 | Finish `newobj.vt`: take the uncommitted `nvt` edit from worktree `monojit-exits-b2b`; corpus cases (KeyValuePair / Enumerator / ObjectGuid ctors, null class, class-init exit); gates (corpus verify, replay, census). | `EmitLazyCall` (nvt branch), `tools/jit-corpus.ts` | sequential: owns `EmitLazyCall`; C5 and C6 wait for it | Opus (cursor, high-dword semantics) | 45-75 min | 9.0 / 0.83 | medium |
| C2 | Compile-list additions only: `SimClock.ElapsedTime`, `ObjectComponent.get_BaseType` overrides, `ScriptObject.get_Proxy`, `ScriptProxy.get_ObjectId`, `get_Target`, the struct ctors C1 needs, T-specific comparer targets C0 names; regenerate `monojit-list.h` with `tools/jit-hot.ts --emit-native`; one accessor corpus case each. | `docs/data/compile-candidates.csv`, `kJitList` in `native/monohost/monohost-jit.cpp`, `tools/jit-corpus.ts` accessors | parallel-safe (no emitter function); append-only merge in jit-corpus.ts with C1 | Sonnet | 30-45 min | GetComponent 4.5, ElapsedTime 2.7, get_Parent 2.2, get_ObjectId 1.7 / about 3.0 | low |
| C3 | Pure icall for `Array.FastCopy` (non-reference same-element arrays; null, bounds, type mismatch exit first; reference arrays exit), then 5-arg Copy gets its own lazy body. | `JitPureIcall` / `EmitPureIcall` (GetLength sibling), corpus `EXIT_CASES` | parallel-safe with C1 (other function); sequential with C4 if C4 reuses `EmitPureIcall` | Opus (barrier and type-compat exactness) | 60-90 min | 3.1 / 0.14 | medium |
| C4 | Allocation helper for `newarr`, `box`, ref-class `newobj`: call EA's allocator from compiled code with frame state flushed, exits before any change. Hard part: GC reentrancy and the SATB / frame-reader rules with a lazy frame pending. | `EmitExit` allocation path, `EmitLazyCall` (shares vt cursor and mat code with C1) | sequential after C1 | Opus | 2-4 h (design first) | newarr 4.8 + box 2.8 / about 0.6 | high |
| C5 | `con.callvirt` / `con.boxcallvirt`: unbox the boxed receiver for `ED` guards (today the `boxed vt` bail stays for ED), after C4 lets `box` compile. | `EmitLazyCall` guard block (the `op == 0xED` lines) | sequential after C1 and C4 | Opus | 45-60 min | 2.0 + 2.8 / 0.5 | medium |
| C6 | Second-level guard for polymorphic `cache class` sites (small per-site class table beside EA's one-class cache). Build only if C0 shows `cache class` above 1% of a hot site; in the host it is 442 bails. | `EmitLazyCall` guard block, `JitGuardFill` | sequential after C1 | Opus | 60-90 min | unknown until C0 (GetComponent w35 is the candidate) | medium |
| C7 | Study `ValueType.DefaultEquals` -> `InternalEquals` (585503 mats, 94% of all mats; Enum.Equals 9.8 ms/s is mostly `ret` frame teardown, not counted as held back): does the icall allocate, can it be a pure icall, or can the call stay in the lazy body without a mat? Measure first, build only if mat cost is real. | read-only; a build touches `JitPureIcall` | parallel-safe (study); a build is sequential with C3 | Sonnet (study), Opus (build) | 20-30 min study | 0 listed ms/s, mat cost unknown | medium |
| C8 | Gates and port: host gates (corpus, replay, census, bench) after each build piece; game port of CallExit / VtOps / newobj.vt in `native/ts3native/monojit.cpp` (the A5 / B5 files), queue `jit-exits`. | runs; `monojit.cpp`, `monojit-test.h`, `docs/data/builds.json` | sequential, last | Sonnet (gates), Opus (port review) | 30 min gates per piece, 1-2 h port | - | medium |

Order: C0 (measurement, in parallel) + C1 + C2 + C3 + C7 study in parallel (C1 owns `EmitLazyCall`, C2 is data, C3 is `EmitPureIcall`);
then C5 / C6 on top of C1 if C0 supports them; C4 last and only if C0 or the census says allocation exits still matter (model ceiling
about 0.6 ms/s). Expected after C1-C3 is about 4 ms/s of the 8.6 model ceiling; C4-C6 add about 1.2 at higher risk. No build or host
bench while a timing job runs; builds wait for the slot after the timing that starts about 18:00 PHT.

### C7 result (study, no new run) [docs only]

**Verdict: do not build.** At most ~0.2 to 1 ms/s [inferred], below the build risk; nothing listed at stake.

- **Who reaches `ValueType.DefaultEquals`** [verified: `monojit-exits-b2b/probe-out/jx/b2b-count-ce1.log`]: one caller, `System.Enum.Equals` (`jitcalls 57` 585491 calls; `jitedge 57 885/887 EA` -> DefaultEquals, 5 calls elsewhere). `DefaultEquals` itself ran 585555 times; its only exit is the `call` to the `InternalEquals` icall (`jitedge 56 866 EA 585503`, `jitmat 585503 call 16`; another 803 from a normal-body copy). So the 585k are one workload: boxed enum compares (`MintBench`: List/Dictionary generics, boxed `Enum.Equals`, docs/139, docs/207 §6 case 6). Host-only traffic; the game count run (C0) has not been done, so game call counts are unknown.
- **Types** [inferred]: the receivers are boxed enums, not user structs. They already have their own `Enum.Equals`, which falls to `ValueType.DefaultEquals` (that is why the frame is the 4-instruction body in docs/207 §5). "Compile a field-wise Equals per struct type" has no target here: the game's hot struct `Equals` calls (`ObjectGuid`, `InteractionObjectPair.Equals`) are other methods and rows. A pure icall for `InternalEquals` (same class, no reference fields -> memcmp, like the `GetLength` pure icalls in `JitPureIcall`) would remove the mat, but it needs the receiver class layout test in compiled code [inferred; Mono 1.2 `InternalEquals` source not read, so "allocates" in the A2 note above is unchecked: it only needs a fields array for reference-field structs].
- **Cost of a mat** [measured, existing]: boxed `Enum.Equals` + `Enum.GetHashCode` is 679.0 ns interpreted vs 691.9 ns compiled (0.98x, 6.0 switches per call; docs/207 case 6), and the compiled lazy body already carries the 585k mats. So the whole mat plus exit is inside a ~0.34 us call and costs the compiled side at most ~2% of it. No isolated ns per mat exists; none was timed.
- **Sampled game weight** [measured, docs/207 rows, docs/537]: `Enum.Equals` is 9.8 ms/s self (docs/207 #2; 1.7 in the docs/537 run), and `DefaultEquals` / `InternalEquals` appear in no sampler table (the sampler has no icall frames; the icall time sits inside the `Enum.Equals` self row) [inferred]. The Enum.Equals row is mostly interpreter dispatch, call and `ret` teardown, which docs/139 §5 (call/return fast path) addresses, not a mat fix.
- **At stake**: even if the mat were free and the compiled side reached the 1.0x to 1.1x of other call-bound methods, that is <= ~1 ms/s on the 9.8 [inferred upper bound]; from the 0.98x measurement, ~0.2 ms/s. The big enum-compare win is already at the source: the walk-style hook (docs/04) replaced the boxed `Enum.Equals` lookups, and typed comparers (docs/74) are the lever for the rest (~1.0% spread over ~10 dictionaries).
- **Proposed piece**: none. Optional only after C0: if the game count run shows `Enum.Equals` with real exits at `DefaultEquals` w16 above ~10 per ms of sample, add a pure-icall kind 4 `InternalEquals` (no-ref same-class memcmp) beside C3; otherwise drop C7. The 94% mat share is a host-bench artifact, not a game finding.

### C2 status (2026-11-08, branch `feature/monojit-c2`, not merged) [measured, host]

Data only; no emitter edit. Added callees (lazy bodies): `SimClock.ElapsedTime` (3 overloads), `ScriptObject.get_Proxy`,
`ScriptProxy.get_ObjectId`, `ObjectGuid..ctor`, and the 37 `ObjectComponent.get_BaseType` overrides in Sims3GameplaySystems
(one name per class; the abstract base has no body; `get_Target` left out, low value). Struct ctors: `KeyValuePair`2..ctor` and
`Enumerator..ctor` were already in the host list; they are new in the game list (the `Enumerator..ctor` row widens to every
nested Enumerator ctor of the five assemblies, as `Enumerator.MoveNext` does).
- Host: `kJitList` (`monohost-jit.cpp`), plus capacity only: `g_jitNames` 128 to 256, `g_jitListBuf` 4096 to 8192 (the corpus
  `MONOHOST_JITLIST` was at 107 names). `tools/jit-corpus.ts` `C2_LIST` (`FxCompA/B/C.get_BaseType`, `FxProxy.get_ObjectId`) lists the fixture
  stand-ins, so the GetComponent / get_ObjectId accessor cases run through lazy callees; no new fixture code.
- Game list (not built, left for the next port): `LAZY_ROWS` in `tools/jit-hot.ts`, 42 new compile rows appended to
  `docs/data/jit-hot.csv` (host-count columns blank), `native/ts3native/monojit-list.h` regenerated with
  `node tools/jit-hot.ts --emit-native game-copy/assemblies` (58 to 125 bodies; the baseline regenerated byte-identical first).
- Corpus verify (CallExit=1 VtOps=1 lazy on, INLINE=2 WIDE=1): 246 cases, 0 differ, 0 unstable, per-call ok 42987 to 58584, MISMATCH 0.
- Replay `jit-replay-all.txt --mode verify --lazy` (flags as above, list = replay methods + the new names): 5468 inputs, 5282 ran,
  0 differ, 0 unstable, 186 skipped, 0 faulted, ok 1413749, MISMATCH 0 (identical to before: no captured input reaches the new
  names, so the replay proves no regression only). No host rebuild fault this time.
- Census at 1.0 ms/s (`tools/jit-hot.ts --count`, CSVs `2026-11-08-jit-exit-census-c2-{base,after}.csv`): held back 40.8 ms/s in
  14 methods to **36.3 in 12** (call 12.3 to 9.5, callvirt 10.9 to 9.2). The exit census model (`jit-exit-census.ts`) is unchanged
  (static, same sampled methods).
- Bail diff (count log): gone GetComponent w35 `no lazy body`, ScriptObject.get_ObjectId w16 `no lazy body`; DefaultComparer
  w17 48 to 43. Moved: ElapsedTimeInMinutes w3 `no lazy body`=35 now sits in `SimClock.ElapsedTime` (lazy w2, normal w6, 35 each):
  its callees `get_CurrentTicks` and `ConvertFromTicks` (static, float math) have no lazy body; next data row if wanted. Host-artifact
  `depth slot`: Math.Min w6 830 to 890, fixture `FxCompA.get_BaseType` 3 (A3 artifact, as before); GetTypeFromHandle w8 +2. No new reason class.
- `node tools/selftest-all.ts`: 217 PASS, 0 FAIL.

### C2b status (2026-11-08, branch `feature/monojit-c2b` from b0a7b8ad, not merged) [measured, host]

Data only. Added lazy bodies: `SimClock.get_CurrentTicks` and `SimClock.ConvertFromTicks` (one overload each; the only callees of
the three `ElapsedTime` overloads, `ConvertFromTicks` calls nothing). Files: `kJitList` (`monohost-jit.cpp`), `LAZY_ROWS` (`tools/jit-hot.ts`),
rows 173-174 in `docs/data/jit-hot.csv` (host columns blank), `native/ts3native/monojit-list.h` regenerated (125 to 127 bodies). ts3native not built.
- Corpus verify (CallExit=1 VtOps=1 lazy, INLINE=2 WIDE=1): 246 cases, 0 differ, 0 unstable, per-call ok 58605, MISMATCH 0.
- Replay `jit-replay-all.txt --mode verify --lazy`: 5468 inputs, 5282 ran, 0 differ, 0 unstable, 186 skipped, 0 faulted, ok 1413749, MISMATCH 0.
  The first run stopped at input 3406 with `host fault C0000005 while rebuilding` (replay object rebuild, the tool does not resume on it); an identical rerun completed.
- Census at 1.0 ms/s (count run with corpus + prep list, CSVs `2026-11-08-jit-exit-census-c2b-{base,after}.csv`; base = same build, list without the 2 names):
  `jit-hot.ts` held back 46.9 ms/s in 15 methods before and after (this prep list adds 2 methods the C2 run did not have, so it is not comparable with 36.3).
  `SimClock.ElapsedTime` call exits per call 1.991 to 0.009; bail rows `no lazy body` under ElapsedTime (105 + 1 + 105) are gone, one `depth slot` remains (host artifact).
- Watch: `get_CurrentTicks` compiles with an `ldsflda` exit (1.22 exits per call) plus `ldfld.i8` slow path; in game it is a lazy body that leaves through that
  exit on every call, so the real gain of C2b needs an `ldsflda` template (or may be a loss) [inferred]. Check in the C0 game count before shipping the list.
- `selftest-all`: 216 PASS, 1 FAIL (`tools/harness/vanilla.ts --self-check`: `probe-out/queue/job-running exists`, a game job was running; unrelated).

### C-int + B5: host integration and newobj.vt game port (2026-11-08, branch `feature/monojit-c-int`) [measured, host; built, selftested; not run in game]

Branch from `feature/monojit-a5b` (96782819; A5b is not on master yet). Merged: `feature/monojit-exits-b2b` (7be580ce, clean),
`feature/monojit-c2` at b0a7b8ad only (its tip 3ef0ceac, a stray merge of monojit-a5, left out), `feature/monojit-c2b` (ec2f4e4c, clean).
- Conflicts (C2 merge): `kJitList` (C2 list kept; `ObjectGuid..ctor` once), `tools/jit-corpus.ts` (`C2_LIST` const before the KB2 block;
  `jitList()` order KA_LIST, VTOPS_LIST, C2_LIST, NEWVT_LIST; case order unchanged: C2 adds no cases), the B2b self-check now reads
  "compile list fits monohost (g_jitNames[256], g_jitListBuf[8192])", this file (both sections). JitCorpus.cs regenerated.
- Game list: `SimClock.get_CurrentTicks` (C2b) is kept out: jit-hot.csv row 173 verdict `template first: ldsflda` (the game has a K_LDSFLDA
  template, but the C2b host count shows 1.22 ldsflda exits per call in this method, so a lazy body would leave on every call; cause not
  read [inferred: special static or class-init exit]). Host kJitList keeps it. `monojit-list.h` regenerated with `--emit-native`: 71 methods,
  126 bodies (127 before; the rest byte-identical).

Host gates, one build (corpus 251 cases, INLINE=2 WIDE=1 VtOps=1), each 0 differ, 0 unstable, MISMATCH 0:

| NewVt | CallExit | lazy | per-call verify ok |
|---|---|---|---|
| 1 | 1 | on | 60350 |
| 1 | 1 | off | 62614 |
| 0 | 0 | on | 61374 |
| 0 | 0 | off | 62614 |

(The other four combinations also ran in the same chain, all 0 / 0 / MISMATCH 0; by the 2026-11-08 bundle rule only all-on vs all-off count.)
Replay `jit-replay-all.txt --mode verify --lazy`, all on (CallExit=1 NewVt=1 VtOps=1): 5468 inputs, 5282 ran, 0 differ, 0 unstable,
186 skipped, 0 faulted, ok 1413749, MISMATCH 0 (first run, no flake). Census at the end, all on (`tools/jit-hot.ts --count`, 1.0 ms/s,
same 105.1 ms/s sampled in 32 methods): **held back 28.6 ms/s in 10 methods** (call 10.8 in 5, callvirt 9.2 in 3, newarr 4.8, box 2.8,
con.callvirt 2.0, unbox 0.5, ldsflda 0.0 in 1); 40.8 on the merged base, 33.0 after B2b alone, 36.3 after C2 alone. CSV
`docs/data/results/2026-11-08-jit-exit-census-cint-all.csv`.

B5 port (`native/ts3native/monojit.cpp`), `[MonoJit] NewVt` (default 0, needs Direct=1; OK line ends `..., CallExit=%d, NewVt=%d`; ini key
documented): `JitNewVtClass`, `JitLazySite` op 0xF4, `EmitLazyCall` nvt (this = cursor, no receiver slot, B_ARGS 0 with no params, no
return-slot word, no return copy, post `[esi]` = cursor, `[esi+4]` = caller record +0x24, esi += 8), `JitLazyMatC` newobj state (caller
+0x18 class, +0x20 this, callee +0x14 = 1), pre-transform of listed value-type ctors (game gate: `JitListed`, as the call sites), lazyOk
loop and emitter take 0xF4. Emitted bytes copied from the host lines; the only game difference is the existing reader-trap window around
the call (Trap=1 only). The array-ctor class word is a global `g_jitArrCtorP = TS3A(0x01220E8C)` (selftest redirects it).
- EA facts re-read in the game exe [verified, disassembly of `game-copy/bin/TS3W.exe`, sha256 equal to the host's `game-copy/monohost/TS3W.exe`,
  so every handoff address holds in game]: 0xE56152 (ip at inv+0x1C, ctor = items[ip[1]], class = [[ctor+8]+8] -> caller rec +0x18, ip += 2
  words), 0xE5617D `cmp [class+0x24], [0x1220E8C]` (array-ctor path), 0xE561F9 flag 8 + call 0xE52160, 0xE5620C this = [ebp-0xC] -> [ebp-0x50]
  and rec +0x20, 0xE56277 stack_args = 0 with no params, 0xE56280 [ebp-0x28] = 1 -> callee rec +0x14 at 0xE56346, post 0xE561B0-0xE561C9
  (flag 8 and not 0x10: [esi] = rec +0x20, [esi+4] = rec +0x24, esi += 8).
- Selftest `TestMonoJitNewVt` (`monojit-test.h`): no-param and one-param ctors (result = cursor, high dword = record +0x24, ctor's field write
  lands at the cursor, cursor unchanged, arena back), enum and array-ctor classes stay EA's newobj, depth-slot bail before any write, a ctor
  exit under CallExit (mat writes caller +0x18 / +0x20, callee +0x14 = 1, inv this = cursor, args 0), NewVt=0 = EA's newobj. build.cmd
  `monojit: ok`, `selftest ok`; `tools/selftest-all.ts` 217 PASS 0 FAIL; `tools/hook-sites.ts --check` pass (1023 addresses, 0 not in docs/68).
- Pair **monojit1108c**: ts3native **0.36.204** (sha 6f6712cd73e6, commit f1290c28; 0.36.203 left free for dancediag1108), ts3fixes = monojit1108b
  (55828c22b72c, no mods/ change). Files `game-copy/ts3native.asi.monojit1108c`, `game-copy/ts3fixes.package.monojit1108c`.
- Game count mode: none (no jitbail / jitmat / jitnew / jitexit lines in ts3native; host only), so no C0 count arm. C0 needs a count-mode
  port first (its own piece).

Queue `tools/harness/queues/jit-exits.json`, rebuilt for the bundle rule (Rodney 2026-11-08: no per-switch arms; old per-switch arms removed):
`bp-jit-exits-smoke` (5 min, all on, stopQueueOnFail) -> `bp/ip/sv-jit-exits-capture` (15 min, Mode=1 Capture=1, all switches on, files
`probe-out/jit-capture-<w>-cint.jitcap`) -> host replay of each fresh capture with all switches on (queue.ts has no host step: the command
is in the queue note; gate 0 differ, 0 faulted, MISMATCH 0) -> `bp/ip/sv-jit-exits-bundle-on` / `-off` (15 min, Mode=2 verify, jit set as
jit-batch.json; logRequire the OK line with all three switch values). `--dry-run` parses (10 jobs). Not run.

Follow-up piece (not built here, deadline): `tools/jit-replay.ts`
1. `--switches VtOps=1,CallExit=0,NewVt=1` (maps to the MONOHOST_JIT* env of the run; default all from the environment), so a failing bundle
   is bisected offline per switch on the same capture; print the set in the summary line.
2. `--coverage`: per compiled method of the list (`jit: compiled` / `jitlazy` rows of the run log) the captured input count, and per method
   the IL branches (`tools/jit-coverage.ts` ilBranches) with at least one input; methods with 0 inputs = the "rare path" list the game
   bundle must target (written to the handoff and as a CSV).
Provisional rare-path list [verified from the C2 replay note: no captured input reaches them in `jit-replay-all.txt`]: `SimClock.ElapsedTime`
(3 overloads), `SimClock.ConvertFromTicks`, `ScriptObject.get_Proxy`, `ScriptProxy.get_ObjectId`, `ObjectGuid..ctor`, the 37
`ObjectComponent.get_BaseType` overrides; the B2b newvt sites in game (KeyValuePair / Enumerator / ObjectGuid ctors) were never captured as
newobj.vt either (the old capture ran without NewVt). Redo the list from the fresh captures with `--coverage`.

Merge state: not merged to master (A5b is not on master; a hold was set: "branch fix: main checkout must be master before next job").

## Replay host fault "C0000005 while rebuilding" (2026-11-08, branch bugfix/monohost-rebuild-flake) [verified, host]

- **Where** [verified: dumpbin of the monohost.exe copies kept in the four faulting replay temp folders]: all four logged faults
  (input 1703 twice, input 3406 twice; EIP +0x24660 / +0x25610 / +0x2562C / +0x25B5C, one per build) sit in `JrHashNew`
  (`monohost-jitreplay.cpp`, the arm's result hash walk, with `GcSize` inlined), not in the object rebuild. The outer `__except`
  around `JrRun` covers the arms' hash too, so the message said "rebuilding". Two shapes: the vtable word pointed to memory whose
  word 0 was 0x81 / 0x9E1, so the class read `kl + 0x34` faulted (read 0xB5 / 0xA15 = eax + 0x34); or word 0 itself was
  0xF0B9F01F (unaligned, unmapped), so the vtable read faulted.
- **Cause** [verified for the site, inferred for the source of the word]: `JrHashWords` follows any word that equals a GC cell start
  (`GcBase(w) == w`), and `JrHashNew` only rejected a word 0 that is 0 or inside the GC heap. A data word (int, float, a stale
  value) that equals the start of a cell holding no object (a non-object GC allocation, a reused cell) leads to a word 0 that is
  no vtable. Which word it is depends on heap layout, so it showed in about 1 run in 3 and passed on rerun. Not a GC lifetime
  bug: the rebuilt objects are rooted in `JitReplay.s_live` from `New` until the next `Reset`, Boehm does not move objects, and
  no host thread writes them during an input.
- **Fix**: `JrClassOf` accepts word 0 as a vtable only when `klass->runtime_info` (+0xB8) names that vtable for its domain
  (`vtable +8` domain, `domain +0x58` id; layout as `native/worldimage/wimage.h`). It reads under SEH, because the reads before
  the check can hit unmapped memory. A cell that fails is hashed as a placeholder, and the totals line counts it
  (`the hash walk reached N GC cells with no vtable`). At replay start a self-check runs: a real object passes, and the two
  fault shapes are refused. A host fault now logs the stage (new / fixup / copy / arguments / snapshot / restore / run / hash),
  the object index, type, length, address, EIP and the fault address.
- **Runs** (`jit-replay-all.txt --mode verify --lazy`, CallExit=1 INLINE=2 WIDE=1, master list): 3 runs each with VtOps=0 and
  VtOps=1, 6/6 clean: 5468 inputs, 5282 ran, 0 differ, 0 unstable, 186 skipped, 0 faulted, ok 1413749, MISMATCH 0. 0 cells
  refused (the round trip accepts every real object, so the offsets hold). The original fault did not come back in these 6 runs,
  so the runs show no regression only; the self-check shows that the fault shapes are refused. Corpus verify (CallExit=1 VtOps=1
  lazy on): 246 cases, 0 differ, 0 unstable, ok 42987, MISMATCH 0.

## C4 design: allocation in compiled bodies (newarr, box, reference newobj) (2026-11-08, design only, no compiler code)

Static reads only: capstone on `game-copy/bin/TS3W.exe` (Steam 1.67, same sha256 as the host copy, C-int section), plus the
host source. Tags as everywhere: [verified] = disassembly or source read for this section, [inferred] = not checked.
Reach claims come from a direct-call BFS (linear disassembly per function until `int3`, depth 6, no indirect calls): a
"reaches" is real, a "does not reach" is only as good as that walk.

### C4.1 What EA's handlers do

| Op (word) | Handler | Steps [verified] |
|---|---|---|
| `box` 0xF9 | 0xE57200 | class = `items[ip[1]]` (`[[ebp-0x10]+0x14]`). Value-type test 0xE52160. **Value type:** size = 0xE6F5A0(class, 0); `mono_value_box` 0xEA3140(domain = `[[ebp+0xC]]`, class, `[esi-8]` = pointer into the vt area); vt cursor `[ebp-0xC]` -= align8(size) *after* the call; `[esi-8]` = box; ip += 2 words. **Enum or primitive** (0xE57252): 0xE52350(`class+0xA0`, `esi-8`, `esi-8`, 0) turns the stack slot into the raw value in place, then `mono_value_box`(domain, class, `esi-8`); no cursor change. Neither path writes `[ebp-4]`, `inv+0x1C` or ThreadContext `+0x15` before the call. |
| `newarr` 0xF8 | 0xE57284 | eclass = `items[ip[1]]`; `mono_array_new` 0xEA30F0(domain, eclass, n = `[esi-8]`); `[esi-8]` = array; ip += 2 words. Nothing written before the call. |
| `newobj` 0xF4, reference class | 0xE56152 -> 0xE56226 | `inv+0x1C` = ip; ctor / class as in B2b; ip += 2 words, `[ebp-4]` = ip; string class `[0x1220E84]` and array ctor `[0x1220E8C]` take their own paths; else ThreadContext `+0x15` = 0, `mono_object_new` 0xEA36F0(domain, class), `+0x15` = 1, caller record `+0x20` = obj, `[ebp-0x50]` = this = obj, then the common call block 0xE5625F/0xE56287 with `[ebp-0x28]` = 1; the post 0xE561EC pushes `+0x20` (one word, esi += 8). |

Allocator chain [verified]:
- `mono_object_new` 0xEA36F0 = `mono_class_vtable` 0xEA2AD0 + `mono_object_new_specific` 0xEA2DA0.
- 0xEA2AD0 fast path: rti = `[class+0xB8]`, id = `[domain+0x58]`; rti, `word [rti] >= id` and `[rti+4+4*id]` != 0 -> that
  vtable. Else 0xEA1E30 creates it.
- 0xEA2DA0: vtable `+0x18 & 1` or class `+0x14 & 0x4000000` -> the proxy path (class lookup, method lookup,
  0xE6EE20 class init); else 0xEA2690.
- 0xEA2690 (object alloc): class `+0x14 & 0x800000` clear -> 0xEA14B0 = `GC_malloc_atomic` 0xE4BDC0(size) + `rep stosd`
  zero; set -> `GC_malloc_explicitly_typed` 0xE50DE0(size, vtable `+4`) when the descriptor is non-zero, else `GC_malloc`
  0xE4BE30. Word 0 = vtable, `[0x1220F40]`++, class `+0x14 & 0x20000` -> 0xE78F60 (finalizer registration), then the
  profiler hook 0xEA4C30.
- `mono_array_new` 0xEA30F0 = 0xE70B50(eclass, 1) (array class get -> 0xE706A0, may create the class) + 0xEA2AD0 +
  `mono_array_new_specific` 0xEA2770.
- 0xEA2770: n < 0 -> 0xE77080 (overflow exception) raised through `[0x1195B64]`; element size 0xE6B690; size overflow
  -> 0xE78610 (the OOM raise); bytes = es*n + 0x18; atomic + `memset` 0 or typed / `GC_malloc`; `+4` = 0 (bounds),
  `+8` = n, profiler hook.
- `mono_value_box` 0xEA3140 = 0xEA2AD0 + 0xE6F580 (instance size) + 0xEA2690 + 0xEA4C30 + a size switch copy to `obj+4`.
- Profiler hook 0xEA4C30: calls `[0x11F45F0]` only when `[0x1220E2C] & 0x80`. No ts3native or monohost source names
  either address [verified: grep].
- Every function above is cdecl (`add esp` after the call in the handlers) [verified].

What can leave these calls other than by `ret` [verified reach, conditions not read]:
- **Managed code:** 0xEA1E30 (vtable creation) -> 0xE71450 -> `mono_runtime_invoke` 0xEA1090. 0xE70B50 -> 0xE706A0 ->
  0xE6EE20 (class init) -> 0xEB6080 -> 0xEB6020 -> 0xEA1090. 0xEA2DA0 reaches 0xEA1090 only through the proxy path
  (0xE6EE20 at 0xEA2DEA). From 0xEA2690 and 0xE78F60, the walk finds no `mono_runtime_invoke` and no raise.
- **Raises:** n < 0 and size overflow (0xEA2770); OOM: `GC_oom_fn` `[0x11949BC]` = 0xE78610 -> `mono_raise_exception`
  0xEA18F0 -> `[0x1195B64]` = 0xE53740 (docs/120 §10.2). 0xE53740 reads TLS ThreadContext `+8` (the current frame),
  builds the trace with 0xE529A0, sets env frame `+0x20` = exc and ctx `+0x14` = 1, then `longjmp`s to the `setjmp3` in
  0xE59FD0. **A raise reads the frame chain**, and monojit.cpp `kJitReaders` already lists it ("native raise, trace").
- **Finalizers:** `GC_finalize_on_demand` = 1, notifier NULL: no finalizer runs inside an allocation [verified docs/30
  §12.3, docs/119 §11].

### C4.2 What a GC during the allocation sees of an open lazy frame

- **Arena (args, locals, eval stack, vt area):** chunks come from `GC_malloc_explicitly_typed(0x7FC, [0x11F424C])`
  (0xD718BB, 0xD719E5) [verified]. `mono_gc_base_init` stores `(GC_new_proc(0xE69520) << 2) | 2` in `[0x11F424C]`
  (0xE695CA-0xE695E8) [verified]. The mark proc 0xE69520 pushes `[chunk+8, [chunk])`, i.e. up to the chunk's top, and no
  further [verified]. `EmitLazyCall` commits `[chunk] = top + size` before the callee runs (monohost-jit.cpp ~931), and it
  bails before any write when the frame does not fit ("no arena chunk", "arena full") [verified source]. So every lazy
  frame of the chain sits below top and is scanned, conservatively. That includes the box source struct in the vt area
  (EA pops the cursor only after the call, and ours must too) and dead slots above esi.
- **C stack:** the root hook `[0x11949CC]` -> 0xD71820 pushes the calling thread from its current page to the end of the
  region (docs/119 §11). The lazy call blocks (`B_THIS`, `B_ARGS`, `B_RSLOT`, `B_VT`, ...), the native return addresses and
  the callee-saved registers of our call sit there, so they are scanned too. The record array `ebp-0x4C74` is on that stack
  as well: dead slots, and the Trap=1 poison (`0xBADF00xx`), are only read as non-pointers.
- **Not seen:** caller-saved registers (eax/ecx/edx) across the call. Rule I2 below.
- **The vt cursor** is a plain integer in `B_VT` and is only scanned as a word. **Frames** (ThreadContext `+8`, depth
  `state+0x140`, records) are not read by Boehm's marker. The marker does read the state object's words, `+0x140`
  included, because the state is a heap cell.
- **Boehm does not move objects** [verified docs/537 §11, docs/119]. No pointer needs fixing after a collection, so a
  precise root map buys nothing.
- **Our own tools in a collection:** the gcroots / satb P2 push hooks read their own tables. The sampler, AsLgFrames,
  AllocSites and the crash report see the chain shortened to the caller (docs/537 §2): that changes attribution, not
  results. satb: new objects are fresh and need no pre-image (satb.cpp: "the new array is fresh"). Compiled stores into
  the new object keep `e.satb`.

### C4.3 Options

| Option | What | Verdict |
|---|---|---|
| O1 materialize, then allocate, then resume native | write the frames (JitLazyMat), call EA's allocator, continue in the method's normal body | Pays a mat per allocation, and the mat is what an exit already costs. The gain would be only the rest of the method staying native. Rejected as the main path; kept as the miss path (see below). |
| O2 GC-safe point with a precise root map | per-site live-ref map, GC callback reads it | Boehm is conservative and non-moving, and the frames are already scanned (C4.2). It solves nothing and adds a GC hook. Rejected. |
| O3 call EA's allocator inside the lazy window, guarded | call only the leaf allocators that cannot create classes or run managed code; every slow or raising path exits before any change; one interceptor for the raise that cannot be pre-checked (OOM) writes the frames before EA's raise reads them | **Chosen.** |
| O4 pinned shadow frame | write only the innermost record / MonoInvocation before the call | Gives a partial chain to a raise (wrong for depth > 1) at partial-mat cost. Rejected. |
| O5 own allocator (inline `GC_malloc` replica) | skip 0xEA2690 | Duplicates EA's kind selection, finalizer and profiler logic for a few ns. Rejected: call EA's leaf. |

### C4.4 Chosen design (O3)

One switch: host `MONOHOST_JITALLOC=1`, game `[MonoJit] Alloc` (default 0, needs `Direct=1`; the OK line ends `..., Alloc=0|1`).
It works in both body kinds. In a normal body the frames exist, so only the miss path and pre-checks apply.

1. **Per-site vtable cache** (one dword per site, carved like the guard cells; 0 = unknown). A non-zero cache is the proof
   that the vtable exists. EA never frees a vtable in the game's one domain [inferred: no domain unload in the game].
   - box: the class's vtable;
   - newarr: the rank-1 array class's vtable, plus `maxN` = (0xFFFFFFE7 - 0x18) / es, computed at fill time with
     es = 0xE6B690(aclass);
   - newobj: the class's vtable. Filled only if `vt+0x18 & 1` and `class+0x14 & 0x4000000` are both clear; otherwise the
     site is marked as a permanent exit.
2. **Fast path (cache != 0), no class creation, no managed code:**
   - box, value type: `push [esi-8]; push class; push domain; call 0xEA3140`. 0xEA2AD0 takes its fast path, because the
     vtable exists. Then `[esi-8]` = eax and cursor -= align8(size). size is taken at compile time from 0xE6F5A0's
     formula, gated on layout flag 4 as B1; no layout means a permanent exit.
   - box, enum / primitive: only the stack-slot forms whose 0xE52350 conversion is a no-op or a plain narrow (i4 / u1 /
     i2 / u2 / i8 / r8 types by `class+0xA6`). Emit that narrow inline, then the same `mono_value_box` call. Other types
     are permanent exits.
   - newarr: `n < 0` or `n > maxN` (unsigned compare) -> exit before any change, and EA raises. Else `call 0xEA2770(vt, n)`;
     `[esi-8]` = eax.
   - newobj (reference): string / array-ctor / proxy classes are permanent exits at compile time. The ctor must have a lazy
     body: every `EmitLazyCall` bail runs **before** the allocation. Then ctx `+0x15` = 0, `call 0xEA2DA0(vt)`, `+0x15` = 1
     (EA writes it; vestigial per docs/130 §10.3, written anyway for exactness), this = eax. The ctor goes through the
     B2b direct path with a reference `this`, then the post pushes `this` (one word, esi += 8). JitLazyMatC newobj state:
     the caller record `+0x18` = class and `+0x20` = obj, and the callee `+0x14` = 1, as B2b, so a ctor exit after the
     native call gets EA's post.
3. **Miss path (cache == 0): "learn exit".** This is the ordinary exit, with one C call in front of EA's handler:
   JitLazyMat (lazy body) writes the frames, then `JitAllocLearn(site)` runs with the frames real and the ves_exec locals
   written. It does what EA's handler is about to do anyway:
   - box / newobj: 0xEA2AD0(domain, class);
   - newarr: 0xE70B50(eclass, 1) + 0xEA2AD0, then es.

   It stores the cache, then jumps to EA's handler, which repeats the lookup on its fast path. Class or vtable creation,
   a TypeLoad raise or managed code from class init run at the same op and in the same frame state as in vanilla, a few
   instructions earlier. The cost is one exit, once per site (per domain).
4. **Raise interceptor (OOM and anything unforeseen in the fast path).** At install, if `[0x1195B64]` == 0xE53740, replace
   it with `JitRaiseHook`; otherwise `Alloc` stays off and logs it. Each lazy-body fast-path site writes a window record
   before its call, `g_jitAw` = {esp at body level ([esp] = return into the caller's site, block above), ebx (arena base),
   ebp, site ip, op, thread id}, and clears `esp` after the call: 2-3 stores each side. `JitRaiseHook(exc)`: on the
   window's thread with `esp != 0`, build JitRegs from the record, call `JitLazyMatC`, write `inv+0x1C` = site ip for the
   innermost frame, clear the record, then tail-jump to 0xE53740. EA then reads a full chain and `longjmp`s past our native
   frames, which is fine: nothing of ours needs unwinding, and the mat performs no allocation. Normal-body sites write no
   record: their frames are real.
5. **Install gates:** C4 stays off with a log line when `[0x1220E2C] & 0x80` (profiler alloc hook) is set, when the raise
   slot is not 0xE53740, or when the 0xEA2690 / 0xEA2770 / 0xEA3140 / 0xEA2DA0 prologue bytes differ from the host's
   (the B1 / A5b byte-check pattern).

Not covered (stay exits): `newarr` of a multi-dimensional class (that is `newobj` on the array ctor), `ldelema` with
allocation, `con.boxcallvirt` (C5 builds on this box path), `mono_newobj` 0x1A3, `newobj` of String, and every class
whose cache could not be filled.

### C4.5 Invariants (each gets a selftest or a corpus case)

- I1 Every lazy frame of the chain lies below its chunk's top at the call (the commit order in `EmitLazyCall`; no new
  arena writes in C4).
- I2 No object reference lives only in eax/ecx/edx across an allocator call: arguments are loaded from eval slots or
  constants right before the push, and the result is stored to its eval slot as the first instruction after the call.
- I3 box: the vt cursor pops only after `mono_value_box` returns. The source bytes stay below top during the collection.
- I4 Inside a window, no EA code that reads frames runs except 0xE53740 behind `JitRaiseHook`, after the mat. That means
  no 0xEA1E30, 0xE70B50 or 0xE6EE20 on the fast path: the cache proves the fast paths.
- I5 No managed code inside a window: proxy classes excluded; finalizers off by `GC_finalize_on_demand`; class init only on
  the miss path, with frames real.
- I6 Every bail and pre-check runs before the first change: allocation, cursor, esi, ctx `+0x15`, record writes.
- I7 Results equal EA: the same allocator leaf with the same arguments, so the same object kind, zeroing, finalizer
  registration and profiler call; the same eval slot and cursor effects; ip effects through the existing exit rules.
- I8 Household rule: not affected. The compiler changes no game decision, and exactness is the gate. No Sim-scope code.

### C4.6 Corpus cases (`tools/jit-corpus.ts`, one block `KC4` / `ALLOC_CASES` / `ALLOC_LIST` appended last)

1. box: structs of 1, 7, 12, 20 bytes and one with reference fields; enums of i4, u1, i8; int, long, double, bool, char;
   box then `Equals` / `GetHashCode`; box in a lazy chain three deep; box of a struct read from an array and from a field.
2. newarr: int[0], int[1], byte[1000], object[n], struct[n] with references, T[] in a generic instance, `Array.Resize`
   growth loop; n = -1 (OverflowException caught in the compiled caller and outside); n = 0x7FFFFFFF of a long (size
   overflow -> the OOM raise, caught).
3. newobj reference: empty ctor, ctor with args, ctor that throws (caught outside and in a compiled caller), ctor with an
   exit after the native call (newobj post through EA), a class with a finalizer, a MarshalByRefObject subclass (must stay
   an exit), string and multi-dim array ctors (stay exits), `List<T>..ctor` and `Dictionary` `Slot` (the census rows).
4. Miss path: the first execution of each site in a fresh corpus process (class created at the op), then a hit.
5. GC under an open lazy frame: a corpus-only test env `MONOHOST_JITALLOCGC=N` calls `GC_gcollect` 0xE4A0B0 right before
   every Nth fast-path allocation (both arms, so verify stays paired). Cases keep fresh objects only in eval slots and
   locals of level-3 lazy frames and check them afterwards.
6. Raise interceptor: native selftest only (corpus verify cannot inject a raise in one arm). A fake allocator raises
   through the hook inside a level-2 window, and the test checks the chain, depth, records and `inv+0x1C` against an EA
   raise at the same op.
7. Bench: `JitLzAlloc` / `JitLzAllocPlain` (bench cases 29/30, already in `CALL_CASES`) plus box and newarr cases.

### C4.7 Host gates, then game gates

- Host, per the bundle rule: corpus verify all-on vs all-off (Alloc, CallExit, NewVt, VtOps), lazy on and off, 0 differ,
  0 unstable, MISMATCH 0. The same with `MONOHOST_JITALLOCGC=64`. Replay `--mode verify --lazy` all on: 0 differ,
  0 faulted. Census `tools/jit-hot.ts --count`: newarr and box rows gone or reduced, and no new bail reason.
  `jit-bench.ts` on a quiet machine. `selftest-all` green.
- Count line (count mode): `jit alloc: sites N box/newarr/newobj, learned N, permanent exits N (reason), raises
  intercepted N`.
- Game: selftests `TestMonoJitAlloc` in `monojit-test.h` (fast path, miss path, pre-check exits, interceptor with fakes,
  install refusal on bad bytes or a set profiler flag, Alloc=0 = EA).
- `jit-reader-trap` with `Alloc=1`: a collection inside a window reads `state+0x140` (a heap word the marker scans), so
  `JitTrapReader` needs a "GC mark" range. Take it from `functions.csv` (Boehm `0xE4A000`-`0xE51000` [inferred bounds]),
  counted as known in windows only when the EIP is in that range. Without it the gate fails on the first collection.
- Then the jit-exits bundle (Mode=2 verify, BP/IP/SV, 15 min) with Alloc in the all-on set.

### C4.8 Risks

| Risk | Where | Mitigation |
|---|---|---|
| A missed path from a fast-path leaf into class init or managed code (the BFS skips indirect calls) | 0xEA2690, 0xEA2770, 0xEA3140 fast path | C4a reads those three bodies fully, indirect calls included, and records the result in the docs/51 ledger (reserve). The interceptor catches raises, and the trap catches frame reads. |
| OOM inside the window | `GC_oom_fn` | Interceptor mat-on-raise; selftest. It is the Error 12 case anyway. |
| Trap false unknowns from GC marking | reader trap | GC-mark range (C4.7). |
| Stack-trace text differs (`inv+0x1C` of the innermost frame) | raise path | Lazy bodies have no clauses, so catch search is unchanged [verified docs/537 §10: clause methods get no lazy body]. Only trace text can differ [inferred]. |
| Exactness of enum / primitive box narrowing | box primitive path | Only no-op or plain-narrow types; corpus case 1. |
| Gain small | census | Model ceiling about 0.6 ms/s (box 0.88 + newarr 0.12 behind, newobj rows < 0.5 each); box also unlocks C5 (2.8 + 2.0 held back, ~0.5 model). |

### C4.9 Build split

| Piece | What | Files | Order | Model | Est. |
|---|---|---|---|---|---|
| C4a | Read 0xEA2690 / 0xEA2770 / 0xEA3140 / 0xE6B690 completely (indirect calls too) and confirm I4/I5; ledger entry; the byte prologue arrays | docs, docs/51 | first; static tools, daytime | Opus | 30-45 min |
| C4b | Host: site cache cells, learn exit (`JitAllocLearn`), raise interceptor + window record, install gates, count line | `monohost-jit.cpp` (EmitExit, new emit helpers, InstallJit, JitCountReport) | after C4a | Opus | 60-90 min |
| C4c | Host box (vt + enum/primitive) and newarr fast paths | `monohost-jit.cpp` emitter cases | after C4b | Opus | 45-60 min |
| C4d | Corpus `KC4` cases 1-5 + bench cases; `MONOHOST_JITALLOCGC` test hook | `tools/jit-corpus.ts`, JitCorpus.cs regen, `tools/jit-bench.ts`, one env line in monohost | parallel with C4b/C4c (append-only) | Sonnet | 30-45 min |
| C4e | Host reference newobj (ctor through the B2b direct path with a reference `this`, `+0x15`, JitLazyMatC newobj state) | `EmitLazyCall`, `JitLazyMatC` | after C4c; never at the same time as C5/C6 (same function) | Opus | 60-90 min |
| C4f | Host gates (C4.7), census CSVs, handoff lines | runs | after each of C4c, C4e | Sonnet | 30 min per round |
| C4g | Game port `[MonoJit] Alloc`, selftests, trap GC-mark range, pair + version, bundle queue entry | `monojit.cpp`, `monojit-test.h`, `ts3native.ini`, `builds.json` | last, after C-int is on master | Opus (port), Sonnet (queue) | 1-2 h |

Total about 5-7 h of agent time, plus one host gate night-free window and the game bundle (3 x 15 min plus smoke). Order
stays as in the C plan: build C4 only after C0 (a game count, which needs the count-mode port) shows box or newarr exits
with real traffic in game. If C0 shows none, park C4 and C5 together. C4a is cheap and can run first either way.
