# Save matrix 2026-10-28

Tool: `tools/harness/save-matrix.ts` (docs/TESTING.md). Times UTC. Save ms = ts3native `save:` total per save.

## 2026-10-28T04:21 UTC NewDowntownWorld, start save null, build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:21:53 | control | PASS | Matrix-NewDowntownWorld-off-31, Matrix-NewDowntownWorld-off-32 | 9480, 8018 | info unstable types: d84e7fc6 (ridgeport_0x09ffe3d7.nhd d84e7fc6:00000000:6c1100337657b720 changed) |
| 04:22:15 | identical | PASS | Matrix-NewDowntownWorld-off-33, Matrix-NewDowntownWorld-on-9 | 8174, 8139 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 04:22:50 | per-fix | PASS | Matrix-NewDowntownWorld-pf-off-6, Matrix-NewDowntownWorld-pf-routeasync-4, Matrix-NewDowntownWorld-pf-discourage-4 | 8238, 8276, 8673 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 04:23:24 | overwrite | PASS | Matrix-NewDowntownWorld-ow-7, Matrix-NewDowntownWorld-ow-7, Matrix-NewDowntownWorld-ow-7 | 8813, 8913, 8880 | all checks pass |
| 04:23:47 | back-to-back | PASS | Matrix-NewDowntownWorld-b2b-15, Matrix-NewDowntownWorld-b2b-16 | 8976, 9282 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 04:24:06 | unpaused | PASS | Matrix-NewDowntownWorld-run-7 | 10328 | all checks pass |
| 04:24:38 | build-mode | PASS | Matrix-NewDowntownWorld-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 04:24:50 | verify-counters | PASS | Matrix-NewDowntownWorld-verify-6 | 9997 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 04:25:15 | reload | pending | Matrix-NewDowntownWorld-reload-off-6, Matrix-NewDowntownWorld-reload-on-6 | 10381, 9715 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-10-28T04:27 NewDowntownWorld reload check of Matrix-NewDowntownWorld-reload-on-6 (ON), build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:27:58 | reload | pending |  |  | info sims in world: 270 before save, 270 after load (NPCs come and go)<br>info ScriptError files (ON): none since the save at 04:25:15 |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-10-28T04:30 NewDowntownWorld reload check of Matrix-NewDowntownWorld-reload-off-6 (OFF), build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:30:35 | reload | PASS |  |  | info sims in world: 270 before save, 270 after load (NPCs come and go)<br>info ScriptError files (OFF): none since the save at 04:25:15<br>info ScriptError files per save: ON 0, OFF 0 |

1 cases, 1 passed, 0 failed

## 2026-10-28T04:32 UTC SunsetValley, start save null, build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:32:36 | control | PASS | Matrix-SunsetValley-off-15, Matrix-SunsetValley-off-16 | 8019, 7495 | info unstable types: d84e7fc6 (et Valley_0x0859db3c.nhd d84e7fc6:00000000:6c11001b8b52bb30 changed) |
| 04:32:55 | identical | PASS | Matrix-SunsetValley-off-17, Matrix-SunsetValley-on-5 | 7486, 7638 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 04:33:24 | per-fix | PASS | Matrix-SunsetValley-pf-off-5, Matrix-SunsetValley-pf-routeasync-1, Matrix-SunsetValley-pf-discourage-1 | 7652, 7603, 7523 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 04:33:51 | overwrite | PASS | Matrix-SunsetValley-ow-5, Matrix-SunsetValley-ow-5, Matrix-SunsetValley-ow-5 | 7506, 7636, 7634 | all checks pass |
| 04:34:08 | back-to-back | PASS | Matrix-SunsetValley-b2b-9, Matrix-SunsetValley-b2b-10 | 7398, 7361 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 04:34:24 | unpaused | PASS | Matrix-SunsetValley-run-5 | 8922 | all checks pass |
| 04:34:55 | build-mode | PASS | Matrix-SunsetValley-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 04:35:06 | verify-counters | PASS | Matrix-SunsetValley-verify-5 | 8737 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 04:35:25 | reload | pending | Matrix-SunsetValley-reload-off-2, Matrix-SunsetValley-reload-on-2 | 8910, 8576 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-10-28T04:37 SunsetValley reload check of Matrix-SunsetValley-reload-on-2 (ON), build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:37:55 | reload | pending |  |  | info sims in world: 246 before save, 246 after load (NPCs come and go)<br>info ScriptError files (ON): ScriptError_<pc>__10-28-2026__12-36-56__F9873C14.xml, ScriptError_<pc>__10-28-2026__12-37-54__B4603222.xml |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-10-28T04:40 SunsetValley reload check of Matrix-SunsetValley-reload-off-2 (OFF), build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:40:25 | reload | PASS |  |  | info sims in world: 246 before save, 246 after load (NPCs come and go)<br>info ScriptError files (OFF): ScriptError_<pc>__10-28-2026__12-39-25__688E8A88.xml, ScriptError_<pc>__10-28-2026__12-40-23__B5F64A19.xml<br>info ScriptError files per save: ON 2, OFF 2 |

1 cases, 1 passed, 0 failed

## 2026-10-28T04:42 UTC IslaParadiso, start save null, build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:42:22 | control | PASS | Matrix-IslaParadiso-off-16, Matrix-IslaParadiso-off-17 | 9154, 7773 | info unstable types: none<br>info known per-save type d84e7fc6: did not differ in this world |
| 04:42:41 | identical | PASS | Matrix-IslaParadiso-off-18, Matrix-IslaParadiso-on-4 | 7701, 7617 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 04:43:10 | per-fix | PASS | Matrix-IslaParadiso-pf-off-5, Matrix-IslaParadiso-pf-routeasync-1, Matrix-IslaParadiso-pf-discourage-1 | 7733, 7691, 8334 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 04:43:39 | overwrite | PASS | Matrix-IslaParadiso-ow-6, Matrix-IslaParadiso-ow-6, Matrix-IslaParadiso-ow-6 | 8654, 8022, 7760 | all checks pass |
| 04:43:57 | back-to-back | PASS | Matrix-IslaParadiso-b2b-11, Matrix-IslaParadiso-b2b-12 | 7794, 7779 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 04:44:12 | unpaused | PASS | Matrix-IslaParadiso-run-7 | 8924 | all checks pass |
| 04:44:44 | build-mode | PASS | Matrix-IslaParadiso-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 04:44:55 | verify-counters | PASS | Matrix-IslaParadiso-verify-4 | 9610 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 04:45:15 | reload | pending | Matrix-IslaParadiso-reload-off-4, Matrix-IslaParadiso-reload-on-4 | 9133, 9202 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-10-28T04:47 IslaParadiso reload check of Matrix-IslaParadiso-reload-on-4 (ON), build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:47:47 | reload | pending |  |  | info sims in world: 259 before save, 259 after load (NPCs come and go)<br>info ScriptError files (ON): none since the save at 04:45:15 |

0 cases, 0 passed, 0 failed, pending: reload

## 2026-10-28T04:50 IslaParadiso reload check of Matrix-IslaParadiso-reload-off-4 (OFF), build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:50:16 | reload | PASS |  |  | info sims in world: 259 before save, 259 after load (NPCs come and go)<br>info ScriptError files (OFF): none since the save at 04:45:15<br>info ScriptError files per save: ON 0, OFF 0 |

1 cases, 1 passed, 0 failed

## 2026-10-28T04:51 UTC NewDowntownWorld, start save null, build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:54:55 | travel | PASS | Matrix-NewDowntownWorld-travel-start-45, Matrix-NewDowntownWorld-travel-away-34, Matrix-NewDowntownWorld-travel-home-24 | 10416, 19457, 11767 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-10-28T04:56 UTC NewDowntownWorld, start save null, build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:59:06 | travel | PASS | Matrix-NewDowntownWorld-travel-start-46, Matrix-NewDowntownWorld-travel-away-35, Matrix-NewDowntownWorld-travel-home-25 | 10436, 12187, 10658 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-10-28T05:00 UTC NewDowntownWorld, start save null, build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:03:28 | travel | PASS | Matrix-NewDowntownWorld-travel-start-47, Matrix-NewDowntownWorld-travel-away-36, Matrix-NewDowntownWorld-travel-home-26 | 9468, 8840, 10660 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-10-28T05:05 UTC NewDowntownWorld, start save null, build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:08:54 | travel | PASS | Matrix-NewDowntownWorld-travel-start-48, Matrix-NewDowntownWorld-travel-away-37, Matrix-NewDowntownWorld-travel-home-27 | 9503, 9046, 11340 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_university: queued moving van<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-10-28T05:10 UTC NewDowntownWorld, start save null, build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:13:39 | travel | PASS | Matrix-NewDowntownWorld-travel-start-49, Matrix-NewDowntownWorld-travel-away-38, Matrix-NewDowntownWorld-travel-home-28 | 10023, 12006, 11213 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_future: queued portal Travel<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-10-28T05:15 UTC NewDowntownWorld, start save null, build 24c51af3+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:17:55 | travel | PASS | Matrix-NewDowntownWorld-travel-start-50, Matrix-NewDowntownWorld-travel-away-39, Matrix-NewDowntownWorld-travel-home-29 | 9456, 10865, 11311 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_future: direct (no interaction)<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-10-28T08:43 UTC NewDowntownWorld, start save null, build 9bac2624+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 08:44:47 | travel | PASS | Matrix-NewDowntownWorld-travel-start-51 | 9066 | info trip route: travel_university: queued moving van<br>info stay: staying in University (--stay) |

1 cases, 1 passed, 0 failed

## 2026-10-28T08:52 UTC NewDowntownWorld, start save null, build 9bac2624+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 08:53:44 | travel | PASS | Matrix-NewDowntownWorld-travel-start-52 | 9236 | info trip route: travel_vacation: queued car on the world lot<br>info stay: staying in France (--stay) |

1 cases, 1 passed, 0 failed

## 2026-10-28T10:58 UTC NewDowntownWorld, start save null, build 9bac2624+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 10:59:29 | travel | PASS | Matrix-NewDowntownWorld-travel-start-53 | 9381 | info trip route: travel_vacation: queued car on the world lot<br>info stay: staying in France (--stay) |

1 cases, 1 passed, 0 failed

## 2026-10-28T11:02 UTC NewDowntownWorld, start save null, build 5018eeda+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 11:03:50 | travel | PASS | Matrix-NewDowntownWorld-travel-start-54 | 10268 | info trip route: travel_vacation: queued car on the world lot<br>info stay: staying in France (--stay) |

1 cases, 1 passed, 0 failed
