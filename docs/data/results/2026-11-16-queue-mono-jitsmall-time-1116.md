# Queue mono-jitsmall-time-1116 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-jitsmall-time-1116-2026-11-16.jsonl`.

## 2026-11-16T05:55 UTC, build 178339ab, jobs sv-jitsmall-time

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-jitsmall-time | 05:55 (13:55) | 22.7 | PASS | PASS: profile 15 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: idlememo_capped 0->292, owchain_alarms 0->1, owspread_types_run 0->5, etspread_passes 0->1; errortrapspread_log [simmin 21002 inventory pass: 11317 objects, 550 inventories checked, 10833 name lookups skipped, 8 destroyed meanwhile, 945 ticks, longest 17.1 ms, most items in a tick 37, largest inventory 37 items]; ts3fixes stuc | 0 | 1803.8/1717.5/1816.1 -> 1924/1684.3/1705.3 | asyncStarted +770, asyncRefused +10, asyncCollected +770, asyncUs +232208010, asyncMaxUs +6665706, asyncPumpUs +1122576, asyncPumpWaits +136, asyncBackoffs +2, asyncQueueMax +5 | timing: invalid (job VOID: runInvalid: noisy machine (others 32.2% of game CPUs); noise high; heavy tool samplers); build: asi 728891275879 ts3native.asi.master-1116 ts3native 0.36.265-m1116 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mono-offload-1115/ts3fixes.package.master-mono-1 |
