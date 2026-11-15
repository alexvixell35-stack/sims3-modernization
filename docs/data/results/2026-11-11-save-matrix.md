# Save matrix 2026-11-11

Tool: `tools/harness/save-matrix.ts` (docs/TESTING.md). Times UTC. Save ms = ts3native `save:` total per save.

## 2026-11-11T04:32 UTC NewDowntownWorld, start save null, build 3698341d+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 04:50:22 | travel | PASS | Matrix-NewDowntownWorld-travel-start-55, Matrix-NewDowntownWorld-travel-away-190, Matrix-NewDowntownWorld-travel-home-181, Matrix-NewDowntownWorld-travel-away-191, Matrix-NewDowntownWorld-travel-home-182, Matrix-NewDowntownWorld-travel-away-192, Matrix-NewDowntownWorld-travel-home-183, Matrix-NewDowntownWorld-travel-away-193, Matrix-NewDowntownWorld-travel-home-184, Matrix-NewDowntownWorld-travel-away-194, Matrix-NewDowntownWorld-travel-home-185, Matrix-NewDowntownWorld-travel-away-195, Matrix-NewDowntownWorld-travel-home-186, Matrix-NewDowntownWorld-travel-away-196, Matrix-NewDowntownWorld-travel-home-187, Matrix-NewDowntownWorld-travel-away-197, Matrix-NewDowntownWorld-travel-home-188 | 9705, 17559, 11272, 8556, 11473, 9044, 12010, 8725, 13914, 8744, 11545, 9388, 11739, 9093, 11762, 9139, 12401 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info vacation cooldown: trips 2+ clear the WentToLocation buff (stress shortcut)<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed

## 2026-11-11T14:34 UTC NewDowntownWorld, start save null, build 3698341d+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 14:35:28 | control | PASS | Matrix-NewDowntownWorld-off-13, Matrix-NewDowntownWorld-off-14 | 11544, 9668 | info unstable types: d84e7fc6 (ridgeport_0x09ffe3d7.nhd d84e7fc6:00000000:6c1100337657b720 changed) |
| 14:35:52 | identical | PASS | Matrix-NewDowntownWorld-off-15, Matrix-NewDowntownWorld-on-5 | 9537, 8795 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 14:36:27 | per-fix | PASS | Matrix-NewDowntownWorld-pf-off-5, Matrix-NewDowntownWorld-pf-routeasync-5, Matrix-NewDowntownWorld-pf-discourage-5 | 9176, 8827, 8796 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |
| 14:37:04 | overwrite | PASS | Matrix-NewDowntownWorld-ow-5, Matrix-NewDowntownWorld-ow-5, Matrix-NewDowntownWorld-ow-5 | 10767, 9871, 9353 | all checks pass |
| 14:37:27 | back-to-back | PASS | Matrix-NewDowntownWorld-b2b-9, Matrix-NewDowntownWorld-b2b-10 | 9213, 8821 | info flush waits: +0 (0 means the worker had finished before the second save) |
| 14:37:46 | unpaused | PASS | Matrix-NewDowntownWorld-run-5 | 10102 | all checks pass |
| 14:38:18 | build-mode | PASS | Matrix-NewDowntownWorld-build-1 | - | skip save in build mode: game refuses: the game cannot save right now |
| 14:38:32 | verify-counters | PASS | Matrix-NewDowntownWorld-verify-5 | 11281 | skip compress verify ran: save compress not loaded<br>skip footprint verify ran: verify off: set [FootprintIndex] Verify=1 in ts3native.ini (read at game start only) |
| 14:39:01 | reload | pending | Matrix-NewDowntownWorld-reload-off-8, Matrix-NewDowntownWorld-reload-on-8 | 12983, 10789 | info routegate: N/A: no runtime switch, installed on both sides; its gates wait only while an async job runs (none with routeasync 0) |

8 cases, 8 passed, 0 failed, pending: reload

## 2026-11-11T16:18 UTC NewDowntownWorld, start save null, build 3698341d+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 16:26:58 | travel | PASS | Matrix-NewDowntownWorld-travel-start-56, Matrix-NewDowntownWorld-travel-away-198, Matrix-NewDowntownWorld-travel-home-189, Matrix-NewDowntownWorld-travel-away-199, Matrix-NewDowntownWorld-travel-home-190, Matrix-NewDowntownWorld-travel-away-200, Matrix-NewDowntownWorld-travel-home-191 | 11308, 20140, 12634, 11313, 14194, 9522, 19423 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info vacation cooldown: trips 2+ clear the WentToLocation buff (stress shortcut)<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed
