
## 13. `bp-ab-wildflowertask` 2026-10-19 00:46 PHT: queue PASS, but no result

The queue marked the job PASS (11.7 min), but the A/B measured nothing. The repro's `wildflower_drain` step failed at once: `Wildflower drain members not found (no Seasons/wildflower content?)`. So the drain list was never refilled and no slow fire happened in either half. In the same load, ts3fixes found `sLotsToGetWildflowers` and hooked `OnGrowWildflowersOnLot` ("ok"). The decompiled `Wildflower` has all four members the agent looks up (`sLotsToGetWildflowers`, `sGrowWildflowerInSpringAlarm`, `mCollected`, `OnGrowWildflowersOnLot`), so the guess in the message is wrong and the cause is open (2026-11-06: the swap makes the method public, docs/247 §12.5). The agent error now names each missing member. Next: one short `wildflower-drain` repro run to see which lookup fails, then rerun the A/B. The job also needs a guard: a failed repro step must fail the job, not end as PASS.

## 14. Default on 2026-11-06 (docs/247 §11.2, §11.10, pair `hitchfix1`, not run)

`kWildflowerTask` is True in `Instantiator.cs` and the tuning XML: 923 ms on BP, EA's alarm, rule "rare hitch fixes default on". The `shortcut:` of §7.3 is closed: every save path sets its flag, then sleeps before the snapshot, and the guard now reads `GameStates.IsTravelling` too (`WildflowerFire.MustStop`). Live checks: `hitch-fix-verify.json` `bp-wildflower-verify` (run, docs/247 §12.2), `wildflower-save.json` (not run, replaces `bp-wildflower-save-abort`, docs/247 §12.7).
