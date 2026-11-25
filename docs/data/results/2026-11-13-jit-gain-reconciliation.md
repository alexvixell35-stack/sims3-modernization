# JIT gain reconciliation, 2026-11-13

Static + existing data. No game run, no host bench run, no build. Question (Rodney): early estimates gave the Mono compiler ~30-45 ms/s (PLAN.md S1 ~10-20, S2/S3 ~10-25, S4 ~3-8, S5 ~3-8; P0 projection realistic ~20-27), today's per-piece ledger totals ~7-8.5 ms/s BP. The ledger counts only incremental pieces; the base gain of compiling bodies at all (S1) is in no row. How big is S1, and why did the totals fall short?

Tags: **[measured]** game or host measurement as run, **[benched]** host bench ns x game counts, **[modelled]** arithmetic on those, **[inferred]** judgment.

## 1. Answer in five lines

1. S1 base (compiled vs interpreted bodies) is **~4-6 ms/s per world, range ~3.5-11** [modelled]: BP 5.3 (3.8-9.6), IP 6.0 (4.7-11.2), SV ~4.9 (3.8-9.1, lazy part assumed BP-like). Design said 10-20.
2. The design numbers were built from **sampled self time** (58.6 ms/s on the list, 66.0 after C5, of 177.3 managed). Game counts say the compiled bodies run **~150k entries/s + ~200k lazy calls/s**, where the self-time model implied **~1.25 M calls/s** (8x too many). The sampler books EA's call/return protocol, native-to-managed transitions and the n2m round trip to the callee frame; compiling a body removes only the body.
3. The bodies are tiny and call/box bound. Host bench of the game's own bodies: median interpreter 241 ns -> compiled 225 ns per case (-7%), not the 1.4-3.4x of P0's call-free loops.
4. **No valid game on/off number exists.** `jit-ab` was parked on 2026-11-06 and never run; every compiler game job since is a correctness or count job with timing flagged invalid.
5. Best guess for the whole compiler as built, no double counting: **BP ~10 ms/s (6-17), IP ~11, SV ~9** [modelled], i.e. about a quarter of the 30-45 design figure. `[Binder] TypeHandleDirect` (E1, est. 5-7, not run) is separate and would come on top.

## 2. S1 base gain, per world

### 2.1 Method

Per compiled method: game executions/s x (interpreter ns - compiled ns) from the host bench of the game's own bodies.

- ns per body: `docs/data/jit-bench.csv` rows `kind=method` (`old_ns` = EA interpreter, `new_ns` = compiled, Direct=0, EA's call path; self ns per call over whole corpus passes, pinned core, 21 passes, stamp floor removed) [benched, host, corpus inputs]. 20 of the list's methods have a row; the unbenched top entry, `ObjectGuid..ctor` (~10k/s), is a ~10 ns body.
- Executions/s: `jitcalls` rows of the game count logs, interval #N minus #N-1 over 300 s, summed by method name (`tools/jit-hot.ts --game-count` format). Entries from the interpreter only; lazy compiled-to-compiled calls are not entries and are added separately (2.3).
- Cross-check: ledger row "compiler itself" (`probe-out/jit-bench/base.csv`): medians of per-case medians over host cases 0-13, interp 241.3, compiled 224.9 ns, 147k/153.5k/151.5k calls/s (BP/IP/SV) -> **2.4 / 2.5 / 2.5 ms/s** [benched]. This is the S1-entries number again; it is a row in the bench output but not in `docs/data/jit-gains.csv` on master (that file lives on feature branches; master has none).

### 2.2 Entries part (calls from the interpreter into a compiled body)

| World | Count run (source) | Entries/s | Benched-method share | S1 entries ms/s | Note |
|---|---|---:|---:|---:|---|
| BP | `jit-bailcensus-2026-11-13-bp-jit-bailcensus-1053-ts3native.log`, Count=1, 4096 KB region | 121k parsed (147k per ledger; the log drops 561 lines at its 6000-line cap) | 109.6k of 121.2k | 2.63 parsed, ~3.2 scaled to 147k | central 2.9 |
| BP | `correctness-1109-2026-11-08-bp-jit-exits-count-2022-ts3native.log`, region full | 103.0k | 93.5k | 2.25 | low |
| IP | `...-ip-jit-exits-count-2041-...`, region full | 153.6k | 138.1k | 3.35 | all intervals complete, 0 lines left out |
| SV | `...-sv-jit-exits-count-2059-...`, region full | 103.0k | 93.1k | 2.22 | |

Top contributors BP (ms/s): `RuntimeTypeHandle.get_Value` 0.42, `Dictionary.TryGetValue` 0.40, `GetSlot` 0.36, comparer `Equals` 0.34, `GetPrev` 0.19, comparer `GetHashCode` 0.18, `CASUtils.CASAGS...` 0.12. `get_Value` is a native-to-managed call of ~660 ns where the body is ~26 ns (docs/537 §20.3): compile cannot recover the rest. `Enum.Equals`, `Type.GetTypeFromHandle`, `SimClock.*`, one `Math.Min` are EA binder bodies and never run as compiled (docs/537 §20.0): 0 entries, though they carry 12.6 of the sampled ms/s.

### 2.3 Lazy-callee part (compiled to compiled, not entries)

With `Direct=1 DirectLeaf=0`, calls between compiled bodies run lazily and never appear in `jitcalls`. Their callee bodies are still compiled instead of interpreted, so S1 applies to them too. Pool: 203.9k (BP) and 217.9k (IP) lazy calls/s (`boundcall-ab` 2026-11-13 native.json `jitLazyCalls`, ledger row "Chains") [measured count]; SV has no run. Body saving on those callees (tiny leaf and accessor bodies: `UInt64.GetHashCode` 8.7, `Math.Min` 3-13, `GetSlot` 33, `Int32.Equals` 16 ns) is **8 / 12 / 20 ns** low/central/high [inferred from the same csv].

### 2.4 Result (low / central / high)

High = entries x 2 for the code-region and method-table limit (3), lazy at 20 ns. Low = region-full counts, lazy at 8 ns.

| World | Entries ms/s | Lazy-callee ms/s | **S1 total** | Sources |
|---|---|---|---|---|
| BP | 2.2 / 2.9 / 5.5 | 1.6 / 2.4 / 4.1 | **3.8 / 5.3 / 9.6** | bailcensus 11-13, C0 11-08, boundcall-ab 11-13 |
| IP | 3.0 / 3.4 / 6.8 | 1.7 / 2.6 / 4.4 | **4.7 / 6.0 / 11.2** | C0 11-08, boundcall-ab 11-13 |
| SV | 2.2 / 2.5 / 5.0 | 1.6 / 2.4 / 4.1 (assumed BP-like, no count) | **3.8 / 4.9 / 9.1** | C0 11-08 |

[modelled]. Compared with the interpreter's ~245-270 ms/s (PLAN.md 494) this is ~2%.

### 2.5 Why the table was full, and what it changes

- Code region (2 MB default, 4 MB in the 11-13 BP census): 2033-2045 of 2048 KB in every game job since 11-08 (docs/537 §20.0). The 4096 KB bailcensus run still logged `refused: code region full` for `Dictionary.GetPrev` and 1858 refusals in total (`jitRefused`, `jitFullN` 1830) with Count=1 counters inflating each body.
- Method table: `jitMethods` = 2048 in BP and IP of the `boundcall-ab` runs (ledger row "Method table above 2048", parked, no refusal count by method).
- Effect: the counted entries are a **lower bound**. The refused bodies are not ranked, so the x2 in the high column is a guess [inferred]; a refusal log by method would settle it (docs/537 §20.0 fix, not built).

## 3. Game measurements of compiler on vs off

| Source | What it is | Valid for on vs off? |
|---|---|---|
| `tools/harness/queues/jit-ab.json` | In-load `--ab jit` 2-min slices, BP/IP/SV, off-first and on-first, Direct=0 then 1. **Parked 2026-11-06 (Rodney, timing trim), never run** | no data |
| `jit-batch` (11-08), `jit-exits` bundle on/off (11-08), `jit-verify` (11-06), `jit-capture`, `jit-region` 2048 vs 4096 (11-13) | Compiler on in both arms (bundle off = VtOps/CallExit/NewVt/FastCopy/Ldsflda off, compiler still on); Mode=2 verify, Count or Capture counters, heavy samplers. Result docs: `timing: invalid (noise medium)` or job VOID (noisy machine) | **no**: not on/off, noisy, instrumented |
| `boundcall-ab` (11-13) | In-load flip of `BoundCall` only, compiler on in both arms | no (piece, not base) |
| `mintcall-ab` 10-30, 10-31, 11-04 and `f1-callpath` 11-10 | `[MonoFast] CallPath`, before the compiler existed | not the compiler. CallPath: noisy pairs BP ~7.7 / IP ~17.7 ms/s (10-31, `timing: invalid`), 11-04 void; F1 IP both orders, `timing: valid-half`, script ms/s 8.1 on / 8.1 off and 7.8 / 7.3 (+0.5): **no gain** [measured, PR #345 import] |
| Release builds | `[MonoJit] Enabled=0` in the repo ini ("not in a release") | no cross-build baseline either; a build-to-build comparison would be confounded |
| P0 monohost (10-30) | Host `MintBench`: stock 286 ms -> compiler 241 (-15.9% with fast path, -14.8% stock + compiler), -18.0% with inlining | [measured, host only]; synthetic mix, not game weighted |

Nothing in this table is a valid game on/off number. The cheapest valid one: `jit-ab.json` BP/IP on a quiet machine (the queue is written; ~85 min) or a `--fixedwork` pair as used by `f1-callpath` (docs/244). Not run here.

## 4. Estimate vs actual per stage

| Stage | Design est. (ms/s) | Today | Tag | Main reason for the gap |
|---|---:|---|---|---|
| S1 body compiler | 10-20 | **~4-6** (3.5-11) per world | [modelled] | Estimate came from sampled self time of the list (58.6 -> 66.0 ms/s), which implies ~1.25 M calls/s; game counts give 103-154k entries/s + ~204k lazy calls/s. Sampler time includes call/ret protocol and n2m transitions that compile leaves alone (`get_Value` 7.4 sampled vs 0.4 saved). Real bodies are call/box bound: host per-case median -7% (241 -> 225 ns), not P0's 1.4-3.4x on call-free loops (`Dictionary` ops 1.14-1.22x). Binder bodies (12.6 sampled ms/s) never run compiled. Region and table full: counts are a lower bound. |
| S2/S3 lazy frames (Direct + chains) | 10-25 | **~2.5 BP / 2.6 IP** (Direct alone 1.76 BP upper bound; chains 0.7-1.5 BP incremental) | [benched] | A lazy call saves 12-17 ns of an EA call (host cases 23/25/27: 30.8 -> 23.5 -> 11.7 ns). The pool is ~204k lazy calls/s, so 204k x 13-17 ns = 2.5-3.3 less guard cost. Design assumed the call path (not the body) was the large cost over many more calls and a protocol cheaper than EA's; the frame write stays store-bound (PLAN.md 372, docs/139 §8.5) and 23% of guarded sites bail (depth slot, arena full, no lazy body: 23.5 M `no lazy body` bails over the BP run). |
| S4 inlining | 3-8 | **~0** | [benched] | Host case 21: -2.2 / -3.6 ns per 64 tiny callees (noise level). Lazy calls already cost ~12 ns and the callees are tiny; no calls/s count for inlined sites (424-600 sites inlined, 1300-1532 refused). No ledger row. |
| S5 exit templates / Wide / VtOps / CallExit / NewVt | 3-8 | **~0-0.5** | [benched] | Host bench deltas mixed sign: `callexit.csv` cases 6/8/9 +91/+106/+174 ns, cases 0/2/3 -25/-36/-33; `vtops.csv`, `wide.csv` within +-20 ns except case 13 (+46). No calls/s for the winning shapes; `Enum.Equals` (case 6's callee) is a binder body that never runs compiled. |
| CallPath (call/return fast path) | 15-30 design, 5-10 revised | **0 in game** | [measured] | F1 IP both orders +0.0 / +0.5 ms/s (inside noise); host `MintBench` -4.7%, static call 14.3 -> 14.6 ns, virtual 14.2 -> 13.1 ns. Instructions per call+return 209-233 -> 191-201 but the frame protocol is store-bound (PLAN.md 372). |
| MintFuse Sub | 2-8 | **no game number** (0-2 [inferred]) | [benched] | `jit-bench` mintfuse set: interpreter `List` foreach -13.3% (paired median), every other case within +-7%, compiled columns ~0. No runtime switch, so no in-load A/B; two-load A/B not run. |
| BoundCall (string comparer) | not in the design | 1.66 BP / 1.96 IP / 1.44 SV | [benched] | Case 36 `TryGetValue` 422 -> 257 ns; 16.0k / 18.9k / 13.9k comparer bails/s. Pending, default off. |
| ClauseLazy | not in the design | 0.27 BP | [benched] | Upper bound; parked, off (docs/537 §20.1). |
| E1 `[Binder] TypeHandleDirect` | not in the design | est. 5-7 per world | [inferred] | Not a compiler piece; `get_Value` n2m round trip. Not run (queue `binder-typehandle.json`). |

## 5. Best-guess full JIT total, no double counting

Components that are additive: S1 (bodies, 2.4) + Direct and chains as one pool (the lazy-call pool, net of guard cost) + BoundCall (removes guard bails, incremental over chains) + ClauseLazy + S4/S5 (~0). Direct's 1.76 BP is a subset of the 2.5 pool; the pool is counted once. CallPath and MintFuse are outside the compiler.

| World | S1 | Direct + chains | BoundCall | ClauseLazy + S4 + S5 | **Total** | Range |
|---|---:|---:|---:|---:|---:|---|
| BP | 5.3 | 2.5 | 1.7 | ~0.3 | **~9.8** | ~6-17 |
| IP | 6.0 | 2.6 | 2.0 | ~0.3 | **~10.9** | ~8-17 |
| SV | 4.9 | ~2.5 (assumed) | 1.4 | ~0.3 | **~9.1** | ~6-15 |

[modelled]. The range adds the S1 low/high, chains 1.8-3.3, BoundCall 0.5-3.6. With E1 confirmed (+5-7) a BP total of ~15-17 is reachable; E1 is a Binder patch, not compiler work, and is unmeasured. If the ledger's 7-8.5 ms/s BP total includes E1, `base.csv` S1 (2.4) and this document's S1 (5.3, which also counts the lazy-callee bodies) are both missing from it.

## 6. Why the sum fell short, in order of size

1. **Self time is not call count x body ns.** The sampler credits the method frame with EA's call/ret protocol, n2m round trips and children inlined by the interpreter. Compile removes only the body. Implied 1.25 M calls/s vs 103-154k counted entries (+~204k lazy) is the single biggest miss (~8x on call count).
2. **What is hot is tiny.** The list is BCL generics and accessors (`Dictionary`, `List`, enumerators, `Enum`, handles). The big-win shapes (call-free loops, bit tests) are not what the game runs. Script assemblies and mod assemblies stay interpreted (docs/223 §5); the list covers 66 of 177.3 sampled managed ms/s, not the ~120-135 ms/s ceiling at 2x that docs/207 assumed for the whole never-yielding 72%.
3. **EA call protocol is store-bound.** Every cheaper-call idea (CallPath, lazy frames, inlining) tops out at the frame stores that must stay bit-exact: lazy frames save 12-17 ns of ~25-30, CallPath nothing in game.
4. **Binder and n2m bodies.** 12.6 + 7.4 sampled ms/s sit in bodies compile cannot touch (E1 targets them).
5. **Coverage.** Region and 2048-method table full in BP and IP; 1858 refusals even at 4 MB with Count=1. Lower bound on every counted figure here; size is unknown until refusals are logged by method.
6. **Bails.** 23.5 M `no lazy body`, 3.2 M `arena full`, 2.4 M `depth slot` guarded-site bails over the BP census run: each costs EA's call minus a lazy call (22-33 ns, docs/537 §21).

## 7. Open, cheapest first

- Log region/table refusals under their own cap, by method and reason (docs/537 §20.0 fix; ~hour of agent work, then one count run) -> replaces the x2 guess.
- One `jit-ab` BP and IP pair on a quiet machine (`tools/harness/queues/jit-ab.json`) or a `--fixedwork` pair -> the only valid on/off number; the model above predicts ~5-6 ms/s S1 + ~4-6 for the rest, which a ~10 ms/s noise floor on script ms/s (8 ms/s total in F1's script column) would barely resolve, so use fixed-work.
- Count run on SV with `Direct=1 DirectLeaf=0` for the lazy pool (row left empty on purpose in the ledger).
