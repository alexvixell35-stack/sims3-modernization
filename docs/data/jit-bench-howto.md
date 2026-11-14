# Bench one MonoJit piece (host) and write its jit-gains.csv row

One agent, one piece. Host only: no game run, no game queue job. Tool: `tools/jit-piece-bench.ts` (docs/537 §25). Ledger: `docs/data/jit-gains.csv`, totals `node tools/jit-gains-total.ts`.

## 0. Before you start

- Work in your own worktree from master (`git worktree add -b feature/jitbench-<piece> ../s3m-wt-<piece> master`). The main checkout stays on master.
- Build monohost in the worktree, against the main checkout's game files:
  `cmd /c "<worktree>\native\monohost\build.cmd <main checkout>\game-copy\assemblies"` (builds `native/monohost/out/monohost.exe` and `MonoHostTest.dll`; ~2 min).
- CPU: the bench pins one core at high priority. If a game queue runs (`TS3W.exe` alive), use a core outside the game's mask (the tool refuses one inside it; the harness game mask has been `0xFFF0`, so cores 0-3 are free). Say in your report which core and whether a game ran.

## 1. Pick or add the bench case

The cases are `HostTest.JitBench(c, n)` in `native/monohost/HostTest.cs`, named in `kJitCase` (and `kStep`) in `native/monohost/monohost-jit.cpp`. Each case is a loop of `n` iterations over one shape; the managed callee bodies are the game's own (mscorlib, SimIFace, Sims3GameplaySystems from `game-copy/assemblies`), so EA's interpreter and our compiled code run the real method bodies. Case 20 is the empty loop: the tool subtracts it, per process. A piece that changes the interpreter itself (`[MintFuse]`, `MONOHOST_MINT*`) also speeds up that loop, so compare raw per-iteration times or subtract the base arm's loop from both arms (docs/139 §4.7: a fake +6% on `ObjectGuid !=`).

Existing cases worth reusing: 0-2 `Dictionary<int|ulong|ObjectGuid,..>.TryGetValue`, 3/12 `List` / `Dictionary` foreach, 4 `List` get_Item + Count, 5 `List.Add`, 6 boxed `Enum.Equals` + `GetHashCode`, 7 `GetType() == typeof`, 8 `Array.Resize`, 9 `Array.IndexOf`, 10 `ObjectGuid !=`, 13 `ContainsKey` + `set_Item`, 14 trivial compiled call (the smoke case), 21-30 inline / lazy-call shapes. 35-40 List<Vector3> / List<ObjectGuid> / Dictionary<ulong,Vector3> (VtOps shapes), 41-43 SimClock.ElapsedTimeInMinutes, DesireCurve.GetYFromX, GameObject.GetComponent<C> (Wide shapes; they need `MONOHOST_JITLISTADD=SimClock.ElapsedTimeInMinutes,DesireCurve.GetYFromX,GameObject.GetComponent` in the shell: names added to the host list, which does not have them), 44 case 1 with spread keys (case 1's keys all hash to 0: one 256-long chain, so its time is chain walking).

If your piece's shape is not there, add one case: a new `case N:` in `HostTest.JitBench` (keep `acc` as the checksum so the compiled run is checked against the interpreter), append its name to `kJitCase`, its loop step to `kStep`, and bump `JIT_CASES`. Arguments: take them from the game where you can.
- Game-captured arguments: `probe-out/jit-capture-<bp|ip|sv>-c3g.txt` (docs/537 §17/§19; `node tools/jit-replay.ts` rebuilds them for the replay gate). Copy the representative values (sizes, key kinds, string lengths) into the case's setup (`JitSetup()` in HostTest.cs builds the shared maps and lists once).
- Otherwise representative synthetic data, and say so in the row's `note`.

## 2. Run

```
node tools/jit-piece-bench.ts --cases <N>[,<M>] --arm base:<base env> --arm <piece>:<piece env> [--common <env for every arm>] \
     --rounds 4 --reps 9 --core 2 --game-copy <main checkout>/game-copy --json probe-out/jitbench-<piece>.json
```

- Columns: `interp` = EA's interpreter (same process, words unpatched), first arm's `compiled` = compiled without your piece, later arms' `compiled` = compiled + your piece (the guarded path). `vs base` = median difference to the first arm. Median and p95 over all reps of all rounds; arms run A B B A over rounds. A `WARN ... CHECKSUM DIFFERS` line means compiled != interpreter: stop, that is a bug.
- Env per piece (host switch = game ini key):

| Piece | Game switch | Host env for the piece arm | Needs in `--common` |
|---|---|---|---|
| lazy frames | `[MonoJit] Direct=1` | `MONOHOST_JITLAZY=1` | |
| chains / guarded callvirt | `DirectLeaf=0` | `MONOHOST_JITDIRECTLEAF=0` (base `=1`) | `MONOHOST_JITLAZY=1` |
| inliner | `Inline=1|2` | `MONOHOST_JITINLINE=1` or `2` | |
| wide ops | `Wide=1` | `MONOHOST_JITWIDE=1` | |
| value-type ops | `VtOps=1` | `MONOHOST_JITVTOPS=1` | |
| call exits | `CallExit=1` | `MONOHOST_JITCALLEXIT=1` | `MONOHOST_JITLAZY=1` |
| newobj.vt | `NewVt=1` | `MONOHOST_JITNEWVT=1` | `MONOHOST_JITLAZY=1` |
| FastCopy | `FastCopy=1` | `MONOHOST_JITFASTCOPY=1` | |
| ldsflda | `Ldsflda=1` | `MONOHOST_JITLDSFLDA=1` | |
| BoundCall | `BoundCall=1` | `MONOHOST_JITBOUNDCALL=3` (both arms `MONOHOST_JITBIND=1`) | `MONOHOST_JITLAZY=1,MONOHOST_JITDIRECTLEAF=0,MONOHOST_JITBIND=1` |
| template groups | `Groups` | `MONOHOST_JITC2=<mask>` | |
| the compiler itself | `Enabled=1 Mode=1` | one arm `base:`: its interp vs compiled columns | |

- Smoke first (`--rounds 1 --reps 3`, ~10 s an arm), then the real run. Example smoke (2026-11-13, game running on 0xFFF0, core 2): `node tools/jit-piece-bench.ts --cases 14 --rounds 2 --reps 5 --core 2 --arm base: --arm lazy:MONOHOST_JITLAZY=1` -> case 14 interp 14.3 (p95 21.9), compiled 13.7 (16.3), lazy 14.7 (17.3, +1.0) ns.

## 3. Calls per second in game

ns saved per call x game calls/s = modelled ms/s. Take calls/s from the latest count run of the bodies your piece touches (`[MonoJit] Count=1`), per world:
- `node tools/jit-hot.ts --game-count probe-out/queue/<job>-ts3native.log` (jitcalls / jitexit / jitbail per method and site; counters are cumulative, use the difference of two report blocks over their seconds).
- Runs to use (2026-11-13): `probe-out/queue/jit-bailcensus-2026-11-13-bp-jit-bailcensus-1053-ts3native.log` (BP, Count=1, BoundCall=0, 0.36.251), the C0 counts `probe-out/queue/correctness-1109-2026-11-08-<w>-jit-exits-count-*-ts3native.log` (BP/IP/SV, region full: lower bound, docs/537 §20.0), the BoundCall A/B `probe-out/queue/boundcall-2026-11-13-<w>-boundcall-ab-*-native.json` (bcHits). Result tables: `docs/data/results/2026-11-13-queue-*.md`.
- No count for a world: leave its `calls_s_*` and `ms_s_*` empty, do not copy another world's.

## 4. Write the row

One row in `docs/data/jit-gains.csv`, every field in double quotes, no quote inside a field (the parser does not unescape):

| Column | What |
|---|---|
| piece, switch, default | name; game ini key (`[MonoJit] X=1`); `on` / `off` = the repo ini (`native/ts3native/ts3native.ini`) default |
| interp_ns, compiled_ns, guarded_ns | medians per call from the bench (interp, base arm compiled, piece arm compiled); add the p95s and the case number to `note` |
| calls_s_bp / _ip / _sv, calls_source | game calls/s of the touched sites, and the run file they came from |
| ms_s_bp / _ip / _sv | (compiled_ns - guarded_ns) x calls/s / 1e6 (for the compiler itself: interp_ns - compiled_ns) |
| guard_cost_ms_s | what the piece's guards cost where it does not apply (an off-path or fallback bench case, or `0` with the reason in `note`) |
| verdict | `ON` (default on, gain, no penalty), `off` (with reason), `pending` (bench done, gate open), `parked` (not built; no bench, estimate source in `calls_source`) |
| bench_commit | the `commit` the tool printed (required for ON / off) |

Then `node tools/jit-gains-total.ts` (must print no ERROR) and `node tools/jit-gains-total.ts --self-check`. Commit the row with your bench case (if any) on your branch: gitmoji, body ending `Assisted with AI`, no tool trailers. Do not merge or push.
