# Queue v015-compare 2026-10-28

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v015-compare-2026-10-28.jsonl`.

## 2026-10-28T17:00 UTC, build f5054207, jobs bp-rvb-rel015-2, ip-rvb-rel015-2, sv-rvb-rel015-2, bp-rvb-rel015-3, ip-rvb-rel015-3, sv-rvb-rel015-3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rvb-rel015-2 | 17:00 (01:00) | 0 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | README comparison, release side (BP): v0.1.5 release build asi.rel015 with the repo (release) ts3native.ini and the built ts3fixes.package as shipped, no switch overrides. 10 min seeded (12345). Read with hitch-ab.ts against bp-rvb-vanilla / bp-rvb-v014. |
| ip-rvb-rel015-2 | 17:00 (01:00) | 0 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | README comparison, release side (IP): v0.1.5 release build asi.rel015 with the repo (release) ts3native.ini and the built ts3fixes.package as shipped, no switch overrides. 10 min seeded (12345). Read with hitch-ab.ts against ip-rvb-vanilla / ip-rvb-v014. |
| sv-rvb-rel015-2 | 17:00 (01:00) | 0 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | README comparison, release side (SV): v0.1.5 release build asi.rel015 with the repo (release) ts3native.ini and the built ts3fixes.package as shipped, no switch overrides. 10 min seeded (12345). Read with hitch-ab.ts against sv-rvb-vanilla / sv-rvb-v014. |
| bp-rvb-rel015-3 | 17:00 (01:00) | 0 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | README comparison, release side (BP): v0.1.5 release build asi.rel015 with the repo (release) ts3native.ini and the built ts3fixes.package as shipped, no switch overrides. 10 min seeded (12345). Read with hitch-ab.ts against bp-rvb-vanilla / bp-rvb-v014. |
| ip-rvb-rel015-3 | 17:00 (01:00) | 0 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | README comparison, release side (IP): v0.1.5 release build asi.rel015 with the repo (release) ts3native.ini and the built ts3fixes.package as shipped, no switch overrides. 10 min seeded (12345). Read with hitch-ab.ts against ip-rvb-vanilla / ip-rvb-v014. |
| sv-rvb-rel015-3 | 17:00 (01:00) | 0 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | README comparison, release side (SV): v0.1.5 release build asi.rel015 with the repo (release) ts3native.ini and the built ts3fixes.package as shipped, no switch overrides. 10 min seeded (12345). Read with hitch-ab.ts against sv-rvb-vanilla / sv-rvb-v014. |
