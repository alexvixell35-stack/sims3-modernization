# Queue mono-offload-correctness-1115-mm 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-offload-correctness-1115-mm-2026-11-16.jsonl`.

## 2026-11-16T03:02 UTC, build e82388bb, jobs bp-offu-smoke, bp-offu-dry, bp-offu-apply, bp-offu-household, ip-offu-apply, ip-offu-household

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-offu-smoke | 03:02 (11:02) | 5.8 | **FAIL** | FAIL: profile 4 min, seed 12345 (rng-compare.ts)<br>FAIL: fixes counters: autoff_env_dry=0 (want >=1)<br>ts3fixes activity: autoff_bcast_apply_none 0->58464, autshadow_skips 0->11504, autshadow_guardskips 0->2, autoff_env_apply_none 0->3737, autshadow_gm4_us 0->111060, autoff_env_ready 0->164, autoff_env_pick_overdue 0->3593, autoff_env_asked_expired 0->5, autoff_bcast_items 0->5397, autshadow_gm6 | 1 | 1745.9/1631.9/1808.1 -> 1905.7/1827.7/1658.1 | - | timing: invalid (job FAIL: FAIL: profile 4 min, seed 12345 (rng-compare.ts); FAIL: fixes counters: autoff_env_dry=0 (want >=1); noise medium; heavy tool samplers); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3f |

Stopped: bp-offu-smoke failed (stopQueueOnFail): FAIL: profile 4 min, seed 12345 (rng-compare.ts).
