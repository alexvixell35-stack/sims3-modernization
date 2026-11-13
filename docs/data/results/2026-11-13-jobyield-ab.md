# 2026-11-13: [RouteAsync] JobYield in-game A/B, contributor results (Dalexlol, PR #397, docs/183 §6)

Contributor in-load A/B with this repo's harness (`--ab jobyield --fixes-on jobyield`, 2-min slices, 10 min, both start sides; main 2f50e37f plus #378, release ini, Apex aside). Copied from PR #397 and docs/183 §6; not rerun here.

| Game | Hitches per 10 min, off -> on |
|---|---|
| IP seed 12345, both orders (pair mean) | >= 300 ms 227.6 -> 121.4 (**0.53 [0.39, 0.73]**); >= 500 ms 0.53 [0.35, 0.79]; >= 1 s 134.5 -> 55.4 (**0.41 [0.26, 0.64]**); >= 2 s 2.1 -> 5.4 (4 events); p99 670 -> 130 ms |
| IP seed 2024, on first | >= 300 ms 358.8 -> 161.0; >= 1 s 61.8 -> 14.6 |
| IP seed 2024, off first (quiet) | >= 300 ms 7.5 -> 3.2 |
| BP, on first | >= 100 ms 212.7 -> 166.9; no >= 300 ms in either arm |
| SV, off first | >= 100 ms 76.0 -> 42.2; >= 300 ms 0 -> 1 event (374 ms) |

- Step-asides 326-499 per game; `jobyield_capped` 0 in all 6 games.
- Stuck resets per stuck check: 6.0% on, 5.3% off (IP); 0 on BP / SV.
- Integration run mx1 (#395 DoorFast build + JobYield on): IP seeds 12345 / 2024 PASS; BP crashed in EA `Realm_AddShape 0x6824D0` (seen once before without JobYield; he does not count it against JobYield) [community, not checked here].

Rough spots, not blocking: >= 2 s class 2.1 -> 5.4 (4 events, too few); SV >= 300 ms 0 -> 1 event.

Household Sims included (no exemption): Rodney 2026-11-13 accepts "same plan result, task sleeps a few ticks instead of the whole game freezing" as exact for household Sims.

Verdict: ON from 0.36.253 (`JobYield=1`, ts3fixes `kJobYield=True`).
