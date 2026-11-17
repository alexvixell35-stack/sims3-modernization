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

## 2026-11-06T21:11 UTC NewDowntownWorld, start save null, build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:11:36 | control | PASS | Matrix-NewDowntownWorld-off-1, Matrix-NewDowntownWorld-off-2 | 9749, 8341 | info unstable types: d84e7fc6 (ridgeport_0x09ffe3d7.nhd d84e7fc6:00000000:6c1100337657b720 changed) |
| 21:12:00 | identical | PASS | Matrix-NewDowntownWorld-off-3, Matrix-NewDowntownWorld-on-1 | 8419, 8375 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 21:12:35 | per-fix | PASS | Matrix-NewDowntownWorld-pf-off-1, Matrix-NewDowntownWorld-pf-routeasync-1, Matrix-NewDowntownWorld-pf-discourage-1 | 8404, 8463, 8486 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 21:13:07 | overwrite | PASS | Matrix-NewDowntownWorld-ow-1, Matrix-NewDowntownWorld-ow-1, Matrix-NewDowntownWorld-ow-1 | 8354, 8465, 8377 | all checks pass |
| 21:13:29 | back-to-back | PASS | Matrix-NewDowntownWorld-b2b-1, Matrix-NewDowntownWorld-b2b-2 | 8353, 8303 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 21:13:48 | unpaused | PASS | Matrix-NewDowntownWorld-run-1 | 9871 | all checks pass |
| 21:14:20 | build-mode | PASS | Matrix-NewDowntownWorld-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 21:14:33 | verify-counters | PASS | Matrix-NewDowntownWorld-verify-1 | 9849 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 21:14:57 | reload | pending | Matrix-NewDowntownWorld-reload-off-1, Matrix-NewDowntownWorld-reload-on-1 | 9758, 9806 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-11-06T21:17 NewDowntownWorld reload check of Matrix-NewDowntownWorld-reload-on-1 (ON), build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:17:34 | reload | pending |  |  | info sims in world: 270 before save, 270 after load (NPCs come and go)<br>info ScriptError files (ON): none since the save at 21:14:57 |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-11-06T21:20 NewDowntownWorld reload check of Matrix-NewDowntownWorld-reload-off-1 (OFF), build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:20:09 | reload | PASS |  |  | info sims in world: 270 before save, 270 after load (NPCs come and go)<br>info ScriptError files (OFF): none since the save at 21:14:57<br>info ScriptError files per save: ON 0, OFF 0 |

1 cases, 1 passed, 0 failed

## 2026-11-06T21:21 UTC IslaParadiso, start save null, build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:22:05 | control | PASS | Matrix-IslaParadiso-off-1, Matrix-IslaParadiso-off-2 | 8810, 7812 | info unstable types: none<br>info known per-save type d84e7fc6: did not differ in this world |
| 21:22:24 | identical | PASS | Matrix-IslaParadiso-off-3, Matrix-IslaParadiso-on-1 | 8175, 7744 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 21:22:54 | per-fix | PASS | Matrix-IslaParadiso-pf-off-1, Matrix-IslaParadiso-pf-routeasync-1, Matrix-IslaParadiso-pf-discourage-1 | 7701, 7952, 7983 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 21:23:22 | overwrite | PASS | Matrix-IslaParadiso-ow-1, Matrix-IslaParadiso-ow-1, Matrix-IslaParadiso-ow-1 | 7803, 7986, 7798 | all checks pass |
| 21:23:41 | back-to-back | PASS | Matrix-IslaParadiso-b2b-1, Matrix-IslaParadiso-b2b-2 | 7884, 7820 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 21:23:57 | unpaused | PASS | Matrix-IslaParadiso-run-1 | 9199 | all checks pass |
| 21:24:28 | build-mode | PASS | Matrix-IslaParadiso-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 21:24:39 | verify-counters | PASS | Matrix-IslaParadiso-verify-1 | 9198 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 21:25:00 | reload | pending | Matrix-IslaParadiso-reload-off-1, Matrix-IslaParadiso-reload-on-1 | 9056, 9081 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-11-06T21:27 IslaParadiso reload check of Matrix-IslaParadiso-reload-on-1 (ON), build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:27:31 | reload | pending |  |  | info sims in world: 259 before save, 259 after load (NPCs come and go)<br>info ScriptError files (ON): ScriptError_<pc>__11-07-2026__05-27-30__96349617.xml |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-11-06T21:30 IslaParadiso reload check of Matrix-IslaParadiso-reload-off-1 (OFF), build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:30:11 | reload | **FAIL** |  |  | new errors with fixes on: 1 not seen with OFF: -- Dereferencing -- / Potential Object Found: Sims#.Gameplay.Core.SocialJigOnePerson (ID=#, W=False, I=False) / Reference Continue: Sims#.Gameplay.Rol |

1 cases, 0 passed, 1 failed (reload)

## 2026-11-06T21:32 UTC SunsetValley, start save null, build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:33:14 | control | PASS | Matrix-SunsetValley-off-1, Matrix-SunsetValley-off-2 | 9128, 8443 | info unstable types: d84e7fc6 (et Valley_0x0859db3c.nhd d84e7fc6:00000000:6c11001b8b52bb30 changed) |
| 21:33:35 | identical | PASS | Matrix-SunsetValley-off-3, Matrix-SunsetValley-on-1 | 8431, 8213 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 21:34:06 | per-fix | PASS | Matrix-SunsetValley-pf-off-1, Matrix-SunsetValley-pf-routeasync-1, Matrix-SunsetValley-pf-discourage-1 | 8262, 8116, 8255 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 21:34:35 | overwrite | PASS | Matrix-SunsetValley-ow-1, Matrix-SunsetValley-ow-1, Matrix-SunsetValley-ow-1 | 8171, 8279, 8328 | all checks pass |
| 21:34:54 | back-to-back | PASS | Matrix-SunsetValley-b2b-1, Matrix-SunsetValley-b2b-2 | 8286, 8423 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 21:35:11 | unpaused | PASS | Matrix-SunsetValley-run-1 | 9895 | all checks pass |
| 21:35:43 | build-mode | PASS | Matrix-SunsetValley-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 21:35:54 | verify-counters | PASS | Matrix-SunsetValley-verify-1 | 9681 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 21:36:16 | reload | pending | Matrix-SunsetValley-reload-off-1, Matrix-SunsetValley-reload-on-1 | 9503, 9658 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-11-06T21:38 SunsetValley reload check of Matrix-SunsetValley-reload-on-1 (ON), build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:38:57 | reload | pending |  |  | info sims in world: 246 before save, 246 after load (NPCs come and go)<br>info ScriptError files (ON): ScriptError_<pc>__11-07-2026__05-37-57__6C09B228.xml |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-11-06T21:41 SunsetValley reload check of Matrix-SunsetValley-reload-off-1 (OFF), build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:41:37 | reload | PASS |  |  | info sims in world: 246 before save, 246 after load (NPCs come and go)<br>info ScriptError files (OFF): ScriptError_<pc>__11-07-2026__05-40-37__FC358947.xml<br>info ScriptError files per save: ON 1, OFF 1 |

1 cases, 1 passed, 0 failed

## 2026-11-06T21:43 UTC NewDowntownWorld, start save null, build 41cfcd23+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:46:12 | travel | PASS | Matrix-NewDowntownWorld-travel-start-1, Matrix-NewDowntownWorld-travel-away-1, Matrix-NewDowntownWorld-travel-home-1 | 9721, 18248, 10979 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-11-06T21:50 UTC IslaParadiso, start save null, build 16363a78+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:50:48 | control | PASS | Matrix-IslaParadiso-off-4, Matrix-IslaParadiso-off-5 | 9442, 8624 | info unstable types: none<br>info known per-save type d84e7fc6: did not differ in this world |
| 21:51:09 | identical | PASS | Matrix-IslaParadiso-off-6, Matrix-IslaParadiso-on-2 | 8743, 8662 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 21:51:40 | per-fix | PASS | Matrix-IslaParadiso-pf-off-2, Matrix-IslaParadiso-pf-routeasync-2, Matrix-IslaParadiso-pf-discourage-2 | 8817, 8597, 8608 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 21:52:10 | overwrite | PASS | Matrix-IslaParadiso-ow-2, Matrix-IslaParadiso-ow-2, Matrix-IslaParadiso-ow-2 | 8870, 8339, 8622 | all checks pass |
| 21:52:29 | back-to-back | PASS | Matrix-IslaParadiso-b2b-3, Matrix-IslaParadiso-b2b-4 | 8677, 8507 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 21:52:46 | unpaused | PASS | Matrix-IslaParadiso-run-2 | 10096 | all checks pass |
| 21:53:17 | build-mode | PASS | Matrix-IslaParadiso-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 21:53:29 | verify-counters | PASS | Matrix-IslaParadiso-verify-2 | 9836 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 21:53:51 | reload | pending | Matrix-IslaParadiso-reload-off-2, Matrix-IslaParadiso-reload-on-2 | 9655, 9659 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-11-06T21:56 IslaParadiso reload check of Matrix-IslaParadiso-reload-on-2 (ON), build 16363a78+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:56:31 | reload | pending |  |  | info sims in world: 258 before save, 258 after load (NPCs come and go)<br>info ScriptError files (ON): none since the save at 21:53:51 |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-11-06T21:59 IslaParadiso reload check of Matrix-IslaParadiso-reload-off-2 (OFF), build 16363a78+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 21:59:10 | reload | PASS |  |  | info sims in world: 258 before save, 258 after load (NPCs come and go)<br>info ScriptError files (OFF): ScriptError_<pc>__11-07-2026__05-59-07__92DB2941.xml<br>info ScriptError files per save: ON 0, OFF 1<br>info known EA errors: known EA error nraas-errortrap-correction-log x1 (ignored): ON 0, OFF 1 |

1 cases, 1 passed, 0 failed
