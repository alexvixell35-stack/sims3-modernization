# Queue mono-offload-rmretest-rerun1-1116-1116b 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-offload-rmretest-rerun1-1116-1116b-2026-11-16.jsonl`.

## 2026-11-16T12:23 UTC, build 7147daea2, jobs bp-rmr-snapback

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rmr-snapback | 12:23 (20:23) | 18.1 | PASS | PASS: profile 16 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1; ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet)<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 0 | 1718/1639.9/1723.7 -> 1790.1/1708.2/1705.5 | rmPasses +29271, rmPassesRm +20291, rmK_1 +28004, rmK_2 +178, rmK_3 +31, rmK_4 +50, rmK_5 +43, rmK_6 +31, rmK_7 +934, rmWalks +22888, rmCallsAll +61694, rmLateRm +129, rmActSum +13855691, rmActMax +518, rmRmSum +57766, rmRmMax +8, rmSampled +1829, rmW1Us +2193219, rmTsN +865769, rmTsUs +73649, rmTailN_0 +564, rmTailN_1 +1265, rmTailUs_0 +9985, rmTailUs_1 +283910, rmSWalks +1422, rmWalkUs +157876,  | timing: invalid (noise medium); build: asi 011f124f4797 ts3native.asi.master-1116b ts3native 0.36.266-mb1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b (expected game-copy/ts3fixes.package.master-1116b); noise: medium (other apps 26.7% of game CPUs); others on game CPUs 26.7%; clock avg/min 4005/3486 MHz; Correctness first (Mode 1 fixed, not A/B): repro unseen-snapback (a<br>sim ms/s 89.1; p99 <50 ms, p99.9 <50, max 113, >=300 0, >=1000 0 (9.4 min) |
