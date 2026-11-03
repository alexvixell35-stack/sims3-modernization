# Save matrix 2026-10-26

Tool: `tools/harness/save-matrix.ts` (docs/TESTING.md). Times UTC. Save ms = ts3native `save:` total per save.

## 2026-10-26T03:47 UTC NewDowntownWorld, start save null, build c3f135e4+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 03:50:56 | travel | **FAIL** | Matrix-NewDowntownWorld-travel-start-43, Matrix-NewDowntownWorld-travel-away-30, Matrix-NewDowntownWorld-travel-home-20 | 9677, 18195, 11119 | trip out 2: Error: travel_vacation: nobody can travel: Tiara Angelista: Tiara Angelista is still recovering from the previous vacation.; Lala Angelista: Lala Angelista is still recovering from the previous vacation.; at failure: world NewDowntownWorld dest Undefined in_travel false household Angelista state LiveModeState; queue empty; travel_state {"away":false,"day_of_trip":0,"in_travel":false,"days_left":null,"on_vacation":false,"sims_in_world":264,"minisims":236,"world":"NewDowntownWorld","destination":"Undefined","household":"Angelista","household_in_world":2,"world_file":"Bridgeport.world","household_members":2,"in_world":true,"world_type":"Downtown","travelers":0,"trip_length":0} |

1 cases, 0 passed, 1 failed (travel)

## 2026-10-26T05:40 UTC NewDowntownWorld, start save null, build 3cd4f9b0+dirty

| time | case | result | saves | save ms | failed checks / notes |
|---|---|---|---|---|---|
| 05:48:55 | travel | PASS | Matrix-NewDowntownWorld-travel-start-44, Matrix-NewDowntownWorld-travel-away-31, Matrix-NewDowntownWorld-travel-home-21, Matrix-NewDowntownWorld-travel-away-32, Matrix-NewDowntownWorld-travel-home-22, Matrix-NewDowntownWorld-travel-away-33, Matrix-NewDowntownWorld-travel-home-23 | 10230, 22038, 12232, 9372, 11707, 11489, 13421 | skip fixes skipped (not in this build/ini): cachedreflection (PersistedClass.Init: already compiled; PersistedClassDB.GetPersistedClass: already compiled; Persist.GetPersistedTypeCode: already compiled; CachedReflection: not patched, off), footprint (footprint index disabled (Enabled=0)), compress (save compress off (Enabled=0)), flush (save flush off (Enabled=0))<br>info trip route: travel_vacation: queued car on the world lot<br>info vacation cooldown: trips 2+ clear the WentToLocation buff (stress shortcut)<br>info travel saves target:  untouched (travel saves went to the Matrix save) |

1 cases, 1 passed, 0 failed
