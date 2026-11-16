# 2026-11-16: [RouteAsync] ReadyDrain=1, one Bridgeport run

This is one short run: phase-20261116-2157 on Bridgeport, with the same ts3native build and settings as the evening's baselines, plus `[RouteAsync] ReadyDrain=1` and the other features on. It is compared with two ReadyDrain=0 runs from the same evening (s66 and the apartment-on arm). There is one run per arm, so treat this as a first look, not a verdict.

| | ReadyDrain=0 (s66) | ReadyDrain=0 (apartment-on arm) | ReadyDrain=1 |
|---|---|---|---|
| unseen route-plan average | 232 ms | 237 ms | **210 ms** (about 10% faster) |
| game hitches | 0 | 3 | 1 (325 ms) |
| unseen-scorecard | | | PASS 6/6; recovery 19/19 = 100%, 0 trapped |

The last `route ready drain:` line of the run [verified, ts3native.log]:
- settings: 2000 us per READY poll, 3000 us per frame, gap 300 us;
- polls 3739;
- calls served past the first 28390;
- done in the same poll 71;
- budget ends 156;
- idle stops 3512;
- **frames at the frame budget 0**;
- longest drain 2134 us.

How sure each claim is:
- [verified] The engine's ReadyDrain line and its counters, above.
- [measured, one run each] The plan average and the hitch counts. No regression was seen, but one run per arm cannot rule one out.
- [inferred] The gain comes from serving a job's managed calls in one READY poll instead of one call per poll. The calls, their order and their thread do not change, so the route results are the same. Only the time to answer changes.
