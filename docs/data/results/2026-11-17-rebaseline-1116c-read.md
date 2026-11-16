# Read: queue rebaseline-1116c (2026-11-17)

Queue `rebaseline-1116c` (chain-1116c, blocks b1 + b2), set master-1116c (ts3native 0.36.267-mc1116, asi 17a2fcf3b5a3,
ts3fixes tuning c64820e723fc), Bin state `apex` (Apex Radiance 4.0.1 + RTX Remix bridge, S3SS, S3PP as in Bin), release ini
ON bundle, seed 12345, story progression off, rewind off, BP 4 / IP 8 / SV 4 jobs of 10 min. Rows:
`2026-11-16-queue-rebaseline-1116c.md`, smoke `2026-11-16-queue-rebaseline-1116c-smoke.md`.
Tools: `run-verdict.ts rebaseline-1116c`, `arm-ms.ts rebaseline-1116c`, `hitch-ab.ts --pool <w> rebaseline-1116c-2026-11-16-<w>-apex-on`,
`probe-out/queue/rebaseline-1116c-*-machine.jsonl`.

## 1. Validity

- Smoke PASS (version, ON bundle, Apex/bridge lines read back, noSamplers) [measured].
- 15 PASS, 1 FAIL (ip-apex-on-1: one new EA script error; triage: does not invalidate timing, known-errors entry on
  `bugfix/ea-boat-intowater-sacs-1117` 4805808a5) [measured / triage].
- Timing verdict of every row, smoke included: **invalid (noise medium)**, because `others on game CPUs` (og) is 23-31%
  (>= 3% = medium; docs/168 §10 and docs/TESTING.md take only `low` for timing) [measured].
- That og is not external load [measured]: other processes 2.5-3.2% of the machine in every job (< 5%, OG_OTHER_MIN, so
  never `high`); clock avg 4117-4144 MHz, min >= 3935 MHz, no dips; TS3W pinned 21/21 samples in every job; GPU 3D of
  other apps 0%. og stays at ~24% on every Apex/bridge job since 11-15 (68 jobs on 11-16, median 25.0%) and was 9-34% on
  DXVK days too; no job since 2026-10-24 has reached `low` (docs/168 §10 scan). It is unowned kernel/driver time on the
  game CPUs [inferred: busy time minus every process's own CPU], not another app.
- Rerun: **none needed** under the external-load rule (other CPU >= 10% or clock drop = rerun): no job meets it. A rerun
  would read `medium` again, so it cannot turn these rows `valid`. ip-apex-on-1 can stay in the IP pool (timing tool
  counts it; its numbers sit inside the spread); excluding it moves IP sim ms/s 163.7 -> 165.4.
- Harness gap to fix (not done here, harness .ts during a chain): the `medium` og rule makes every timing job invalid on
  this machine. Options: pin NvRemixBridge to the game CPUs so og gets exact (machine-load.ts `shortcut:` line 78), or
  let og-only `medium` (other < 5%, clock ok) count as timing-valid. Rodney's 2026-10-24 rule says low only, so this
  needs his call.

## 2. Arms

- The queue file has 32 jobs: B (`*-apex-on-*`, Apex + bridge) 16 and A (`*-apex-off-*`, plain DXVK x32) 16, copied from
  rebaseline-apex401-1115. chain-1116c ran only blocks b1 + b2 = the 16 B jobs; the A block was skipped because plain
  DXVK is dropped from testing since 2026-11-15 [verified: results md has only on rows; arm-ms shows arm `on`/`all` only].
- So this is a **baseline-only run** of the shipped stack: no off arm, no per-switch arm, nothing to subtract.
- Prior Apex re-baseline `rebaseline-apex401-1115` never produced a row: stopped inside bp-apex-on-1 (log ends 04:50
  UTC 11-16 on test_env lines; smoke PASS only). rebaseline-1116 / -1116b never ran. So **this is the first complete
  Apex baseline**; there is no earlier one to compare with.
- Nearest single comparator: `mono-measure-equalsfast-1115-mm` bp-eqfast-correct (master-mono-1115, Apex + bridge, same
  save/seed, 10 min): BP sum 203.9 ms/s vs 204.9 now (+1.0, one run, inside the BP spread 202.4-207.8) [measured].
- docs/05 has no ms/s; only memory (DXVK, 2 sim days): BP largest free min 1.52 GB, live 1.56-1.82 GB; SV 1.65 GB. Now:
  BP 1.52 GB min, SV 1.71 GB min (below).

## 3. Per world (pooled, all 10 min jobs; first minute and load out)

| world | runs | sim ms/s (range) | script / route / animPose / locomotion | it/s | p99 ms | p99.9 ms | max ms | >=300 | >=500 | >=1000 | >=300 per 10 min [95% CI] | largest free MB after (min / mean) | commit MB after |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BP | 4 | 204.9 (202.4-207.8) | 12.9 / 65.3 / 64.5 / 62.2 | 53.8 | 86 | 148 | 474 | 3 | 0 | 0 | 1.1 [0.2, 3.3] | 1522 / 1593 | 1949-2009 |
| IP | 8 | 163.7 (151.7-177.0) | 12.2 / 52.7 / 53.5 / 45.3 | 60.3 | 117 | 267 | 1231 | 121 | 7 | 1 | 19.5 [16.2, 23.3] | 1768 / 1795 | 1745-1788 |
| SV | 4 | 121.1 (119.8-122.5) | 15.2 / 10.6 / 48.9 / 46.4 | 79.1 | 55 | 122 | 1197 | 12 | 8 | 1 | 4.7 [2.4, 8.1] | 1707 / 1728 | 1814-1853 |

All [measured], timing class invalid (noise medium, see §1); use as the shipped-stack reference, not as A/B evidence.
Visible freezes >= 300 ms per 10 min: BP 3.4, IP 20.3, SV 6.6; >= 500 ms: BP 0.8, IP 1.6, SV 3.9.
IP drifts up through the night: on-1..on-8 = 151.7, 155.5, 163.9, 164.5, 162.1, 167.3, 167.6, 177.0 ms/s, mostly route
(44.3 -> 64.2 ms/s); BP and SV are flat, clock and og flat [measured]. Cause not known [inferred candidates: IP route
variance from boats, Windows file cache]; worth a look before IP is used for small A/Bs.
Threaded work: BP 601, IP 609, SV 560 ms/s (taskObjects 75 / 84 / 93) [measured].

## 4. Handover step 4: ON/off per switch

This queue has no per-switch arms, so it decides none of them. State per switch:

| switch | ini now (master-1116c) | rule | evidence | call |
|---|---|---|---|---|
| JIT `[MonoJit] Enabled=1` (+ FaultRecover) | ON | JIT ships on static gain; FaultRecover ON with the JIT (decisions 11-15) | jit-bundle-ab2-1114 no penalty (IP +0.5 ms/s, medium); jitfault correctness PASS 4/4 (handover) | stays ON; no timing needed |
| `[EqualsFast] Enabled=0` | off | compiler work: ON on static gain unless a valid A/B shows a penalty (jit-ship-on-static-gain names EqualsFast) | host corpus 61159 pairs, 0 mismatches (docs/258 §7); bp-eqfast-correct PASS; bp-iclock-eqfast-ab on - off **-1.9 ms/s** (on faster; one order, BP only, IcLockCheck on, noise medium = indicative) | qualifies for ON under that rule (no penalty seen); flip in the ini on its own branch. Timing would need `mono-measure-equalsfast-1115` bp/ip-eqfast-ab, repinned to master-1116c |
| GC lane (C5 / D8) | no switch (`[GC] Enabled=1` unchanged) | - | mono-gc-1115-mm 3/3 PASS (c5-release, c5-verify, d8-allocs) | nothing to flip |
| unseenpark `kUnseenPark` (ts3fixes) | False | ON once its timing shows the gain (decisions 11-15) | none valid: unseenpark-1113 invalid (noiseCheck off, mixed slices) | needs `mono-park-timing-1115` (smoke + separate-load ABBA BP/IP/SV, 12 jobs, ~2.6 h) |

Queues needed and readiness:
- `mono-park-timing-1115.json`: NOT ready: pinned to master-merge-1115 (asi) and the mono-park-1115 ts3fixes/agent;
  repin to the master-1116c set (park lane is on master since merge/mono-1115) and run it on Bin state apex through a
  chain (turbo-compat.ts). Its noise check will read `medium` like every job here, so it gives no `valid` row until the
  §1 harness question is settled.
- `mono-measure-equalsfast-1115.json`: NOT ready: pinned to master-dalexlol-417-1115; the -mm copy holds only the 2
  correctness jobs. Repin bp-eqfast-ab + ip-eqfast-ab (in-load `--ab equalsfast`, 13.5 min) to master-1116c if a
  timing check is still wanted.
- `mono-measure-mintpairs-1115-mm`: diag tool only (bp-mintpairs-jiton FAIL: wrong version regex), not a switch gate.
- JIT timing queues (`jit-time-1114`, `mono-jitsmall-time-*`): not needed for the ON decision (static-gain rule);
  jitsmall-time runs had samplers on (heavy) so their timing is invalid anyway.
