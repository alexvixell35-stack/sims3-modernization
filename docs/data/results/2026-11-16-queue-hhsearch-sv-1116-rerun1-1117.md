# Queue hhsearch-sv-1116-rerun1-1117 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hhsearch-sv-1116-rerun1-1117-2026-11-16.jsonl`.

## 2026-11-16T22:33 UTC, build aef958f7d, jobs sv-doormemox-privacy

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-doormemox-privacy | 22:33 (06:33) | 0 | **FAIL** | FAIL: UNKNOWN: no ts3native.log copy, log rules not checked<br>FAIL: events not fired: log /^ts3native 0\.36\.268-dmxp1117 / 0 lines (want >= 1); log /^OK   door memo x: DoorMemoX=3 / 0 lines (want >= 1); log /^door memo x: ts3fixes doorgen armed/ 0 lines (want >= 1); log /^door memo x misses: .*door handler \(live, not kept\) [1-9]/ 0 lines (want >= 1); log /^door memo x mode 3: armed 1, .* hits  | - | ? -> ? | - | rerun of sv-hhsearch: counts instead of it; timing: invalid (job FAIL: FAIL: UNKNOWN: no ts3native.log copy, log rules not checked; FAIL: events not fired: log /^ts3native 0\.36\.268-dmxp1117; noise unknown); SV-Spring, repro doormemox-privacy (forced toilet + shower on the household lot, 6 townies, ultra speed), DoorMemoX=3 + doorgen on the fix build. PASS = 0 crashes, 0 script errors, no FAIL do |
