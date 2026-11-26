# 2026-11-23: Dalexlol 651-658 stack, non-household switches ON (import/dalexlol-batch-1123)

By Dalexlol. Contributor data from the PR texts and comments (Steam Deck, Sunset Valley new game, forced repros, off arm then on arm); not rerun here (rule: contributor results with harness evidence merge as is and go ON). The `*hh` (household) and `*log` switches stay OFF (household rule; HouseholdToo opt-in), clipwait and its plumbing stay OFF (his petrelax run had it off).

| PR | Switch (ON) | His evidence (off -> on) |
|---|---|---|
| 651 | petrelax | `petrelax-force` (run pr-r1): Go Here past a lying dog, off `FailedBecauseNoPathToDestination ... PetSurfaceFloorJig`, stops 1.0 m before; on: succeeded, path 3.6 m, walked through, arrived 0.0 m in 5.4 s, errors 0 |
| 652 | reachuse | `reachuse-force` (run ru-r2): pile 1.3 m behind a closet door, off: route fail, pile stays after 45 s; on: walked up, pile carried to the can, errors 0 |
| 654 | servingshare, rhshare | `servingshare-force` (sv-r1): off line waits 3.1 / 5.9 / 5.0 sim-min; on no waits, 4487 = 2, errors 0; `rhshare-force` (rh-r1) City Hall tour |
| 655 | showertogether, showerpee | `showershare-force` (sh-r7): off Bo waited 11.9 sim-min, cancelled; on both in the shower, hygiene 99-100; bladder -97 off: Pee Self; on: showers, no accident, errors 0 |
| 656 | viewlenient | `viewlenient-force`: off route-fail balloon, over within 18 s; on stays in place, 4513 = 1, errors 0 |
| 657 | socialreach, unstuck | `socialreach-force`: off no chat (4526 = 1); on placed 2.3 m apart, 4532 = 1, errors 0; kiss control keeps the failure; unstuck acted once in storms, errors 0 |
| 658 | crowdport | `crowdport-force`: after his fix off would = 2; on 4551 = 0, 4562 = 4, no failed route |

Household check: every rule has an `*hh` switch (default OFF) and counts "exempt"; household and selected Sims keep EA's result with it off. Nothing is teleported.
Not imported: #659 (merge of the 655 and 657 stacks; the stack diff needs its own resolve).
