# Queue offload-ranks 2026-11-08

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/offload-ranks-2026-11-08.jsonl`.

## 2026-11-08T02:24 UTC, build 57cde9a7, jobs bp-offranks-verify, ip-offranks-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-offranks-verify | 02:24 (10:24) | 12.3 | PASS | PASS: offload-ranks-camera: profile 10 min, first window after load<br>ts3fixes activity: autshadow_skips 0->25961, autshadow_l_testagree 0->22716, autshadow_gm4_methods 0->683, autshadow_a0 0->23690, autshadow_a4 0->7146, autshadow_a5 0->23, autshadow_gm4_us 0->141869, autshadow_gm6_us 0->42862, autshadow_requests 0->814, autshadow_content_modules 0->7, autshadow_l_equal 0->22691, autshadow_gm6_m | 0 | 2185.7/1871.5/1581.9 -> 2327.8/2167.4/1240.8 | - | timing: invalid (offload-ranks-camera: noise medium); build: asi efc5de17539d ts3native.asi.g2pair1108 ts3native 0.36.190 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107/ts3fixes.package.castrim3/ts3fixes.package.master0189/ts3fixes.package.g2pair1107b/ts3fixes.package.g2pair1108/ts3fixes.package.g2pair1108b/ts3fixes.package.castrimdiag1108 (expected game-copy/ts3fixes.package.g |
| ip-offranks-verify | 02:37 (10:37) | 11.9 | **FAIL** | FAIL: offload-ranks-camera: profile 10 min, first window after load<br>FAIL: fixes counters: autoff_lot_a0=0 (want >=1)<br>ts3fixes activity: autshadow_skips 0->12975, autshadow_l_testagree 0->11822, autshadow_gm4_methods 0->683, autshadow_a0 0->11998, autshadow_a4 0->3358, autshadow_a5 0->626, autshadow_gm4_us 0->143225, autshadow_gm6_us 0->40491, autshadow_requests 0->890, autshadow_content_modu | 1 | 2171.2/1872.5/1582.2 -> 2141.8/2018.4/1400.6 | - | timing: invalid (job FAIL: FAIL: offload-ranks-camera: profile 10 min, first window after load; FAIL: fixes counters: autoff_lot_a0=0 (want >=1); offload-ranks-camera: job FAIL: FAIL: offload-ranks-camera: profile 10 min, first window after load; FAIL: fixes counters: autoff_lot_a0=0 (want >=1); noise medium); build: asi efc5de17539d ts3native.asi.g2pair1108 ts3native 0.36.190 (log ok); ts3fixes t |

Stopped: ip-offranks-verify failed (stopQueueOnFail): FAIL: offload-ranks-camera: profile 10 min, first window after load.
