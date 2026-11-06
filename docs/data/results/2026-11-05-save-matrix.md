# Save matrix 2026-11-05

Tool: `tools/harness/save-matrix.ts` (docs/TESTING.md). Times UTC. Save ms = ts3native `save:` total per save.

## 2026-11-05T14:48 UTC NewDowntownWorld, start save null, build 0b16abc0+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 14:49:24 | control | PASS | Matrix-NewDowntownWorld-off-34, Matrix-NewDowntownWorld-off-35 | 9642, 8356 | info unstable types: d84e7fc6 (ridgeport_0x09ffe3d7.nhd d84e7fc6:00000000:6c1100337657b720 changed) |
| 14:49:47 | identical | PASS | Matrix-NewDowntownWorld-off-36, Matrix-NewDowntownWorld-on-10 | 8132, 8060 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 14:50:21 | per-fix | PASS | Matrix-NewDowntownWorld-pf-off-7, Matrix-NewDowntownWorld-pf-routeasync-5, Matrix-NewDowntownWorld-pf-discourage-5 | 8122, 8294, 7949 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 14:50:54 | overwrite | PASS | Matrix-NewDowntownWorld-ow-8, Matrix-NewDowntownWorld-ow-8, Matrix-NewDowntownWorld-ow-8 | 8529, 8284, 8678 | all checks pass |
| 14:51:15 | back-to-back | PASS | Matrix-NewDowntownWorld-b2b-17, Matrix-NewDowntownWorld-b2b-18 | 8069, 8092 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 14:51:34 | unpaused | PASS | Matrix-NewDowntownWorld-run-8 | 9888 | all checks pass |
| 14:52:05 | build-mode | PASS | Matrix-NewDowntownWorld-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 14:52:18 | verify-counters | PASS | Matrix-NewDowntownWorld-verify-7 | 9718 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 14:52:41 | reload | pending | Matrix-NewDowntownWorld-reload-off-7, Matrix-NewDowntownWorld-reload-on-7 | 9773, 9477 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-11-05T14:55 NewDowntownWorld reload check of Matrix-NewDowntownWorld-reload-on-7 (ON), build 0b16abc0+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 14:55:14 | reload | pending |  |  | info sims in world: 270 before save, 270 after load (NPCs come and go)<br>info ScriptError files (ON): none since the save at 14:52:41 |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-11-05T14:57 NewDowntownWorld reload check of Matrix-NewDowntownWorld-reload-off-7 (OFF), build 0b16abc0+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 14:57:47 | reload | PASS |  |  | info sims in world: 270 before save, 270 after load (NPCs come and go)<br>info ScriptError files (OFF): none since the save at 14:52:41<br>info ScriptError files per save: ON 0, OFF 0 |

1 cases, 1 passed, 0 failed
