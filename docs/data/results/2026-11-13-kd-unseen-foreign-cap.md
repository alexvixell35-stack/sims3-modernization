# 2026-11-13: [RouteAsync] KdUnseenForeignCap, contributor results (Dalexlol, PR #378)

Contributor data with this repo's harness, copied from PR #378 and docs/197 §7; not rerun here.

Before (2026-11-11/12, 118 `*-ts3native.log`): 85 foreign START jobs >= 1 s with `capAt -1`, all scope unseen, 308 s in total, longest 21.4 s, in 7 logs (7 of 11 IP logs); in both arms of unrelated A/Bs (12 off / 15 on, 13 off / 14 on), so release behaviour.

| Run | Uncut foreign jobs >= 1 s | `KdUnseenForeignCap` lines |
|---|---|---|
| IP natural 10 min, exact repeat of the run that had 27 (same queue, save, seed), switch on | **0** | 2 |
| three more IP runs with the switch on | **0** each | - |
| BP, SV 10 min natural | switch never acted, no long jobs | 0 |

Left: a cell A* search already running when the cut comes still runs 1-2.3 s past it (docs/164 §7); not this switch.

Scope: unseen followers only (`t_kdJobScope == 2`), foreign kinds only; household, selected, seen and unknown scopes unchanged. Selftest `plandeadline-test.h`.

Verdict: ON from 0.36.253 (ini; no runtime switch, no counter).
