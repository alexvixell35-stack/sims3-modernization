# Queue mono-autshadow-seenexact-1116 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-autshadow-seenexact-1116-2026-11-16.jsonl`.

## 2026-11-16T07:48 UTC, build ecbc9c76, jobs bp-seen-smoke, bp-seen-dry-crowd, bp-seen-act-crowd

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-seen-smoke | 07:48 (15:48) | 33.6 | **FAIL** | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autunseen_offlist 0->234269, autshadow_skips 0->269840, g8_hh_simhours_x100 0->63975, g8_lat0 0->374, g8_lat2 0->298, g8_lat3 0->64, g8_lat4 0->3957, autshadow_guardskips 0->142, g8_len_seen_ticks 0->6981, g8_w_seen_n 0->860, g8_null_picks 0->10176, g8_seen_gohome 0->316, g8_unseen_null_picks 0->9317, autshadow_rain_done 0->87, | 0 | 1718.3/1626.9/1714.2 -> 2235.5/1941.3/1428.4 | - | timing: invalid (job FAIL: FAIL: 1 new script errors (max 0): ScriptError_<pc>__11-16-2026__16-12-38__82C58041.xml; noise medium); build: asi 728891275879 ts3native.asi.master-1116 ts3native 0.36.265-m1116 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mono-offload-1115/ts3fixes. |

Stopped: bp-seen-smoke failed (stopQueueOnFail): FAIL: 1 new script errors (max 0): ScriptError_<pc>__11-16-2026__16-12-38__82C58041.xml.
