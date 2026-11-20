# 2026-11-20: Dalexlol PR #575 (autonomy quality: 5 NPC switches), import branch import/dalexlol-575-1120

By Dalexlol. Contributor data copied from his PR text (jobs of 2026-11-18, local build with household Sims not exempt there,
more switches on); not rerun here. His slots 3992-4016 are our 1345-1369 (`docs/registry.md`: his numbers clashed with #564).
Rodney said OK to import #575 on 2026-11-19. Household rule in code: every refusal / cancel / yield skips household, selected
and unknown-scope Sims (`IsPlayerSim`, not the HouseholdToo-liftable `HouseholdExempt`).

| Switch | His evidence | Default here |
|---|---|---|
| fightorphanwait | repro `ip-fight-orphan`, Isla Paradiso, etl14g af18-ip-31398-orphan-m2: PASS, orphaned Wait ended (slot 1345 = 1); the first try FAILED (setup made no orphan) | ON |
| poolnolane | exact EA bug fix; offers refused per job: Bridgeport 2064-2434, Isla Paradiso 96-808, Appaloosa Plains 125-160, Lucky Palms 26; 0 errors; no forced trigger | ON |
| autonomyfailcooldown | 9 soak jobs of 18-25 min (BP x2, IP, SV, TB, AP x3, LP), all PASS, 0 stuck; refused 4-14192, recorded 76-369, cooldowns 3-88 (counts include household cooldowns the PR drops); no off/on | ON |
| venuepicktime | logging only; picks timed 1398-3732 per job, 0 picks >= 200 ms | ON |
| venuepickfast | yields 1591-4194 per job, 168-283 not yielded; no off/on, no gain shown | OFF (Rodney call) |

Not measured by him: off/on failed plans per hour; no timing table. 0 errors in every error slot (1347, 1350, 1356, 1359, 1365).

## PR #588 (follow-up, branch import/dalexlol-588-1120)

By Dalexlol, natural play 2026-11-19 on his local build (counters 4359 / 4360, now 1374 / 1375; his version also covered household
Sims, this one does not). Verdicts from his run-verdict.ts: search type None not recorded (1374): 16 games, sum 2,041, largest
etl14k-1119 ak19-sv-31413 (Sunset Valley, 25 min) 301, PASS. NPC pianist breaks (1375): 3 games, sum 158, largest 124
(etl14k-1119 ak19-bp-31411, Bridgeport, 25 min; also ar19-bp-reload 14), PASS. No off/on comparison; the 124 breaks in one game
mean the route still fails after each break (retries now 30 sim-minutes apart). Cause (his, decompiled, [verified] by him): `PianistPerform`
is pushed by `Piano.PushRoleStartingInteraction` from `Pianist.SimulateRole` on every role pass with search type None.
