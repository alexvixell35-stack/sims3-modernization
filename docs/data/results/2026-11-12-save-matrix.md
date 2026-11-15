# Save matrix 2026-11-12

Tool: `tools/harness/save-matrix.ts` (docs/TESTING.md). Times UTC. Save ms = ts3native `save:` total per save.

## 2026-11-12T02:54 UTC NewDowntownWorld, start save null, build 04964a30+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 03:17:00 | travel | PASS | Matrix-NewDowntownWorld-travel-start-57, Matrix-NewDowntownWorld-travel-away-201, Matrix-NewDowntownWorld-travel-home-192, Matrix-NewDowntownWorld-travel-away-202, Matrix-NewDowntownWorld-travel-home-193, Matrix-NewDowntownWorld-travel-away-203, Matrix-NewDowntownWorld-travel-home-194, Matrix-NewDowntownWorld-travel-away-204, Matrix-NewDowntownWorld-travel-home-195, Matrix-NewDowntownWorld-travel-away-205, Matrix-NewDowntownWorld-travel-home-196, Matrix-NewDowntownWorld-travel-away-206, Matrix-NewDowntownWorld-travel-home-197, Matrix-NewDowntownWorld-travel-away-207, Matrix-NewDowntownWorld-travel-home-198, Matrix-NewDowntownWorld-travel-away-208, Matrix-NewDowntownWorld-travel-home-199 | 10968, 21938, 13488, 10072, 17443, 10596, 15863, 12923, 13694, 10680, 19449, 10213, 13774, 13372, 18433, 12959, 15482 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info vacation cooldown: trips 2+ clear the WentToLocation buff (stress shortcut)<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-11-12T03:19 UTC NewDowntownWorld, start save null, build 04964a30+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 03:19:34 | control | PASS | Matrix-NewDowntownWorld-off-16, Matrix-NewDowntownWorld-off-17 | 11613, 9791 | info unstable types: d84e7fc6 (ridgeport_0x09ffe3d7.nhd d84e7fc6:00000000:6c1100337657b720 changed) |
| 03:19:59 | identical | PASS | Matrix-NewDowntownWorld-off-18, Matrix-NewDowntownWorld-on-6 | 9693, 9812 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 03:20:38 | per-fix | PASS | Matrix-NewDowntownWorld-pf-off-6, Matrix-NewDowntownWorld-pf-routeasync-6, Matrix-NewDowntownWorld-pf-discourage-6 | 9899, 9522, 10176 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 03:21:13 | overwrite | PASS | Matrix-NewDowntownWorld-ow-6, Matrix-NewDowntownWorld-ow-6, Matrix-NewDowntownWorld-ow-6 | 9550, 9437, 9565 | all checks pass |
| 03:21:37 | back-to-back | PASS | Matrix-NewDowntownWorld-b2b-11, Matrix-NewDowntownWorld-b2b-12 | 9420, 9585 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 03:21:57 | unpaused | PASS | Matrix-NewDowntownWorld-run-6 | 11803 | all checks pass |
| 03:22:30 | build-mode | PASS | Matrix-NewDowntownWorld-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 03:22:44 | verify-counters | PASS | Matrix-NewDowntownWorld-verify-6 | 11082 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 03:23:10 | reload | pending | Matrix-NewDowntownWorld-reload-off-9, Matrix-NewDowntownWorld-reload-on-9 | 11111, 10829 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-11-12T05:00 UTC NewDowntownWorld, start save null, build 04964a30+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:09:05 | travel | PASS | Matrix-NewDowntownWorld-travel-start-58, Matrix-NewDowntownWorld-travel-away-209, Matrix-NewDowntownWorld-travel-home-200, Matrix-NewDowntownWorld-travel-away-210, Matrix-NewDowntownWorld-travel-home-201, Matrix-NewDowntownWorld-travel-away-211, Matrix-NewDowntownWorld-travel-home-202 | 11786, 20558, 13184, 10860, 13674, 9893, 18124 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info vacation cooldown: trips 2+ clear the WentToLocation buff (stress shortcut)<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-11-12T12:39 NewDowntownWorld, build 722909ea+dirty (game died)

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 12:39:18 | run | **FAIL** |  |  | case ran: game died |

1 cases, 0 passed, 1 failed (run)

## 2026-11-12T12:42 UTC NewDowntownWorld, start save null, build 722909ea+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 12:49:56 | travel | PASS | Matrix-NewDowntownWorld-travel-start-60, Matrix-NewDowntownWorld-travel-away-212, Matrix-NewDowntownWorld-travel-home-203, Matrix-NewDowntownWorld-travel-away-213, Matrix-NewDowntownWorld-travel-home-204, Matrix-NewDowntownWorld-travel-away-214, Matrix-NewDowntownWorld-travel-home-205 | 10117, 19330, 12702, 9781, 12948, 9845, 13249 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info vacation cooldown: trips 2+ clear the WentToLocation buff (stress shortcut)<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-11-12T13:16 UTC NewDowntownWorld, start save null, build afaa6430+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 13:23:37 | travel | PASS | Matrix-NewDowntownWorld-travel-start-61, Matrix-NewDowntownWorld-travel-away-215, Matrix-NewDowntownWorld-travel-home-206, Matrix-NewDowntownWorld-travel-away-216, Matrix-NewDowntownWorld-travel-home-207, Matrix-NewDowntownWorld-travel-away-217, Matrix-NewDowntownWorld-travel-home-208 | 9130, 16915, 10403, 8949, 11875, 10070, 12728 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info vacation cooldown: trips 2+ clear the WentToLocation buff (stress shortcut)<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-11-12T13:36 UTC NewDowntownWorld, start save null, build afaa6430+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 13:43:13 | travel | PASS | Matrix-NewDowntownWorld-travel-start-62, Matrix-NewDowntownWorld-travel-away-218, Matrix-NewDowntownWorld-travel-home-209, Matrix-NewDowntownWorld-travel-away-219, Matrix-NewDowntownWorld-travel-home-210, Matrix-NewDowntownWorld-travel-away-220, Matrix-NewDowntownWorld-travel-home-211 | 8928, 16947, 10557, 8234, 11499, 9764, 11897 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info vacation cooldown: trips 2+ clear the WentToLocation buff (stress shortcut)<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-11-12T23:29 UTC NewDowntownWorld, start save null, build b7b0ad55+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 23:30:54 | travel | PASS | Matrix-NewDowntownWorld-travel-start-63 | 10043 | info trip route: travel_vacation: queued car on the world lot<br>info stay: staying in France (--stay) |

1 cases, 1 passed, 0 failed
