# Save matrix 2026-11-06

Tool: `tools/harness/save-matrix.ts` (docs/TESTING.md). Times UTC. Save ms = ts3native `save:` total per save.

## 2026-11-06T05:32 UTC NewDowntownWorld, start save null, build 02a039fd+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:33:09 | control | PASS | Matrix-NewDowntownWorld-off-37, Matrix-NewDowntownWorld-off-38 | 11319, 10471 | info unstable types: d84e7fc6 (ridgeport_0x09ffe3d7.nhd d84e7fc6:00000000:6c1100337657b720 changed) |
| 05:33:38 | identical | PASS | Matrix-NewDowntownWorld-off-39, Matrix-NewDowntownWorld-on-11 | 11519, 10206 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 05:34:25 | per-fix | PASS | Matrix-NewDowntownWorld-pf-off-8, Matrix-NewDowntownWorld-pf-routeasync-6, Matrix-NewDowntownWorld-pf-discourage-6 | 12959, 12961, 10694 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 05:35:08 | overwrite | PASS | Matrix-NewDowntownWorld-ow-9, Matrix-NewDowntownWorld-ow-9, Matrix-NewDowntownWorld-ow-9 | 11378, 11078, 11726 | all checks pass |
| 05:35:35 | back-to-back | PASS | Matrix-NewDowntownWorld-b2b-19, Matrix-NewDowntownWorld-b2b-20 | 10543, 11457 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 05:35:55 | unpaused | PASS | Matrix-NewDowntownWorld-run-9 | 12369 | all checks pass |
| 05:36:28 | build-mode | PASS | Matrix-NewDowntownWorld-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 05:36:42 | verify-counters | PASS | Matrix-NewDowntownWorld-verify-8 | 12009 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 05:37:15 | reload | pending | Matrix-NewDowntownWorld-reload-off-8, Matrix-NewDowntownWorld-reload-on-8 | 13944, 13287 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-11-06T05:41 NewDowntownWorld reload check of Matrix-NewDowntownWorld-reload-on-8 (ON), build 02a039fd+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:41:16 | reload | pending |  |  | info sims in world: 270 before save, 270 after load (NPCs come and go)<br>info ScriptError files (ON): none since the save at 05:37:15 |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-11-06T05:44 NewDowntownWorld reload check of Matrix-NewDowntownWorld-reload-off-8 (OFF), build 02a039fd+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:44:19 | reload | PASS |  |  | info sims in world: 270 before save, 270 after load (NPCs come and go)<br>info ScriptError files (OFF): none since the save at 05:37:15<br>info ScriptError files per save: ON 0, OFF 0 |

1 cases, 1 passed, 0 failed

## 2026-11-06T05:46 UTC IslaParadiso, start save null, build 02a039fd+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:46:35 | control | PASS | Matrix-IslaParadiso-off-19, Matrix-IslaParadiso-off-20 | 9944, 9501 | info unstable types: none<br>info known per-save type d84e7fc6: did not differ in this world |
| 05:46:56 | identical | PASS | Matrix-IslaParadiso-off-21, Matrix-IslaParadiso-on-5 | 8789, 8786 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 05:47:31 | per-fix | PASS | Matrix-IslaParadiso-pf-off-6, Matrix-IslaParadiso-pf-routeasync-2, Matrix-IslaParadiso-pf-discourage-2 | 8682, 9337, 10597 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 05:48:05 | overwrite | PASS | Matrix-IslaParadiso-ow-9, Matrix-IslaParadiso-ow-9, Matrix-IslaParadiso-ow-9 | 10697, 9926, 10158 | all checks pass |
| 05:48:27 | back-to-back | PASS | Matrix-IslaParadiso-b2b-13, Matrix-IslaParadiso-b2b-14 | 10396, 9759 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 05:48:46 | unpaused | PASS | Matrix-IslaParadiso-run-8 | 11391 | all checks pass |
| 05:49:17 | build-mode | PASS | Matrix-IslaParadiso-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 05:49:30 | verify-counters | PASS | Matrix-IslaParadiso-verify-5 | 11696 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 05:49:57 | reload | pending | Matrix-IslaParadiso-reload-off-5, Matrix-IslaParadiso-reload-on-5 | 12317, 12245 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-11-06T05:53 IslaParadiso reload check of Matrix-IslaParadiso-reload-on-5 (ON), build 02a039fd+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:53:00 | reload | pending |  |  | info sims in world: 258 before save, 258 after load (NPCs come and go)<br>info ScriptError files (ON): none since the save at 05:49:57 |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-11-06T05:56 IslaParadiso reload check of Matrix-IslaParadiso-reload-off-5 (OFF), build 02a039fd+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:56:16 | reload | PASS |  |  | info sims in world: 258 before save, 258 after load (NPCs come and go)<br>info ScriptError files (OFF): none since the save at 05:49:57<br>info ScriptError files per save: ON 0, OFF 0 |

1 cases, 1 passed, 0 failed
