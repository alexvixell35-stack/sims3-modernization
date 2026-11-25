# 2026-11-20: Dalexlol PR #585 (houseboat gangway rescue, houseboat leg memo, boat roam cap), import branch import/dalexlol-585-1120

By Dalexlol. Copied from his PR text (run s128, 2026-11-19, Isla Paradiso, Baseline-IP, one load: off arm then on arm; ts3native
0.36.267-mc1116; his repro output and ts3native.log); not rerun here. His slots 3982-3985 / 4040-4049 / 4062-4069 are our
1378-1381 / 1382-1391 / 1392-1399, his docs/857 is docs/853.

| | off | on |
|---|---|---|
| Gangway: NPC dropped by EA's boat trip | 16.5 m from the gangway | same spot |
| plans from that spot | 3 gangway legs + 3 outer plans, no path, 1115-1126 ms each | 1 gangway leg (1133 ms), 1 outer replan (1140 ms), then outer plans in 3 ms and 1 ms (memo) |
| home within 150 s | no | yes, at 5.4 s (PlaceSimAtEntrance) |
| Roam: NPC driver idle at the helm | 28 roams in < 3 min, never ashore | 2 roams, then EA's ReturnToShore, ashore |

Rest of the session (14 min, all on): 0 exceptions, 0 error counters; one rescue refused as designed (home is not a houseboat).
Caveats (his): the on arm also had `boattaximemo` and `unseenboatgenboat` on, the combo's native no-path memo was on in both arms,
and the two roam arms used different NPCs. Defaults here: all three ON (contributor results with harness evidence).
