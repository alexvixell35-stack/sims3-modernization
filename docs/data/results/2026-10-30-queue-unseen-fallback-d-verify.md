# Queue unseen-fallback-d-verify 2026-10-30

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-fallback-d-verify-2026-10-30.jsonl`.

## 2026-10-30T07:31 UTC, build 06cbc07b, jobs sv-ufbd2-pre-loop, sv-ufbd2-hold-overwatch, sv-ufbd2-arrive-loop, sv-ufbd2-leave-loop

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-ufbd2-pre-loop | 07:31 (15:31) | 18 | **FAIL** | FAIL: profile loop window end reached: 0 events after 13.6 min, rule not met (off 0, on 0; 12 firings, 2 cycles; rule 30 events per arm; 1 steps capped), cap 16 min, seed 12345 (rng-compare.ts)<br>FAIL: fixes counters: unseenfbpre_shortcuts=0 (want >=10)<br>ts3fixes activity: unseenfb_seen 11->1875, unseenfb_cut 0->3, unseenfb_marked 0->9889, unseenfbpre_rj_townie 0->65, unseenfbteleport_no_spot 0 | 1 | 2129.2/1809.2/1548.9 -> 2136.9/1979.7/1404.9 | asyncStarted +10461, asyncRefused +330, asyncCollected +10449, asyncDropped +11, asyncUs +108987453, asyncMaxUs +363017, asyncPumpUs +73636839, asyncPumpWaits +8288, asyncRefusedM1Pending +47, asyncSkipped +1, asyncBackoffs +9, asyncQueueMax +27 | timing: invalid (job FAIL: FAIL: profile loop window end reached: 0 events after 13.6 min, rule not met (off 0, on 0; 12 firings, 2 cycles; rule 30; noise medium); build: asi 78b5ba5cb03e not in DB ts3native 0.36.124 (log ok); ts3fixes tuning 94c60769ef8f not in DB (expected build/ts3fixes.package); noise: medium (other apps 10.5% of game CPUs); others on game CPUs 10.5%; clock avg/min 4199/4117 M |
