# 2026-11-13: [EaCrashFixes] HeightsInfLoop + BridgeHeightNaN, contributor results (Dalexlol, PR #394, issue #384)

Contributor repro on his machine with this repo's harness (AGENTS.md "Contributor repros count"). Numbers copied from PR #394 and docs/67 §36/§37; not rerun here. Our check: `tools/harness/queues/bridge-height-1113.json` (prepared, not run).

| Run | Setup | Result |
|---|---|---|
| 3 hang captures (2026-11-06, 11-10, 11-12) | Baseline-IP, no fix | sim thread in post pass 16 `0x64CF22..0x64CF55`; kill dump `ts3w-11860-20261112T095758.dmp`: `xmm2 = inf` [verified by him in the dump] |
| IP seed 777 world load | no fix | freeze (reviewer summary of his runs) |
| nh1 | Baseline-IP-Spring seed 777, read-only probe | `GetTerrainHeight(x, z, true)` not finite at 7 of 21 grid points (x 835..885, z 840..849); same spot as the 2.26e34 words in the dump |
| nh2 | seed 777, both switches on | grid probe 7.5 min + play 11.9 min, PASS, no freeze; 8 hits, all NaN (`0xFFC00000`) from `0xC6A250` |
| sm1 | rebased PR build, IP seed 777 | 7.6 min PASS, no freeze, 8 BridgeHeightNaN hits, all 46 grid answers finite |
| in1 | seed 777, both on | 8 hits, no freeze (reviewer summary; not in the PR text) |

Selftest: `wastar-test.h` runs the real stubs against recorder exits (finite values bit-exact, NaN / +-inf / > 1e6 to the no-bridge path, registers and x87 stack balanced).

Import check (2026-11-13) [verified, capstone, Steam 1.67 exe]: all three queries (`0xC61200`, `0xC6A250`, `0xC6A540`) keep an ebp frame from prologue to `pop ebp` and load x, z from `[ebp+8]` / `[ebp+0Ch]` themselves before `call 0xB745A0`; the log read is under `__try` anyway.

Caveat: his `IslaParadiso.world` is a community version (Phantom99's) that adds a bridge; EA's original world was not checked. On a machine without that bridge the repro does not trigger: record N/A, judge side effects only.

Verdict: ON from 0.36.253 (rare hang fix rule: seen, repro, fix works).
