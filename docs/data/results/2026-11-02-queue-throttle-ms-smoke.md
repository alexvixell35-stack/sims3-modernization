# Queue throttle-ms-smoke 2026-11-02

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/throttle-ms-smoke-2026-11-02.jsonl`.

## 2026-11-02T10:58 UTC, build caa8c038, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 10:58 (18:58) | 6.7 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenfbtrip_runs 0->198, wake_classify 0->180068, unseen_motive_calls 0->6460, broadcast_skipped 0->137069, unseen_motive_skipped 0->283, unseenfbpre_rj_task 0->33, unseenfbpre_no_spot 0->3, unseenfbpre_holds 0->273, unseen_broadcast_run 0->5755, stuckcheckasync_maxwait_simsec 0->113, wake_wakeall 0->13, unseen_catchup_max_us  | 0 | 2127.3/1842.7/1612.9 -> 2223.4/2034.3/1413.8 | asyncStarted +5081, asyncRefused +50, asyncCollected +5076, asyncDropped +2, asyncUs +38935907, asyncMaxUs +169280, asyncPumpUs +19407246, asyncPumpWaits +3800, asyncRefusedM1Pending +15, asyncBackoffs +1, asyncQueueMax +5, aaVisSims +2, aaPasses +11091, aaPassUs +18315353, aaN_0 +61160, aaN_1 +959304, aaN_2 +4146600, aaUs_0 +518999, aaUs_1 +3148917, aaUs_2 +2302616, aaSkipped +8184, aaOwedRuns +8 | timing: invalid (noise medium); build: asi 4a52ce0cbf53 unseenrm5-asi ts3native 0.36.163 (log ok); ts3fixes tuning be6f80e107fb unseenrm4-wakehooks-fixes/unseenrm5-wakehooks-fixes (expected game-copy/ts3fixes.package.unseenrm5-wakehooks); noise: medium (other apps 10.7% of game CPUs); others on game CPUs 10.7%; clock avg/min 4198/4130 MHz; Measurement smoke: BP 5 min, --ab unseenbroadcast,unseenmo |
