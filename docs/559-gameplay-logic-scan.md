# 559. Gameplay logic scan: bowling, Genie Banish, Space Simulator, band jam, statue line, clean flags, bird TV (2026-11-14)

Static hunt for gameplay logic flaws in `Sims3GameplaySystems` / `Sims3GameplayObjects` (local decompile; no game run).
Online, Store-server and Simport code skipped; cosmetic messages only listed. Status: **static, built, NOT RUN.**
The pregnant-NPC hospital loop, the carried-child put-down retry and the bartender PortalLocked are docs/558 (other agent).

## 1. Tools and rerun

| Tool | Command | Hits |
|---|---|---:|
| New semgrep rules `tools/static/ea-gameplay-logic.semgrep.yml` | `tools/static/gameplay-scan.sh` (both rule sets + greps, ranked TSV in `game-copy/analysis/gameplay-scan/`, ~1 min, < 3 GB) | 51 after tuning (flag-left-set-on-exit 31, duplicate-chain-operand 10, deref-in-null-branch 5, loop-le-count 4, same-sim-both-sides 1; the last was 86 before self-targeted pushes were excluded) |
| Grep: `RandomUtil.GetInt(x.Count)` indexing (semgrep's C# parser misses it inside `do/while`) | in `gameplay-scan.sh` | 1 (§4) |
| Grep: sleep-wait loops with no exit-reason / destroyed / counter test | in `gameplay-scan.sh` | 127 (UI/CAS/loading waits dominate; read the interaction ones) |
| InferSharp `tools/static/infersharp.sh` on Sims3GameplayObjects (existing report) | `game-copy/analysis/infersharp/sims3gameplayobjects/report.txt` | 30 null derefs: 21 are tuning-XML `ParseData` (data always present), 1 real gameplay (`BowlingLane.EveryoneIsDone`, §3d), rest listed in §6 |
| InferSharp on Sims3GameplaySystems | not run: the game was running (needs ~3 GB) | - |

Rules (each names the gameplay symptom): `random-inclusive-index` (RandomUtil.GetInt(max) is inclusive [verified
`RandomUtil.GetInt(int,int)` = min + (max-min+1)*NextDouble]), `loop-le-count`, `index-count`, `null-wrong-operator`,
`deref-in-null-branch`, `contains-inverted`, `same-sim-both-sides` (actor/target swaps), `duplicate-chain-operand`,
`flag-left-set-on-exit` (flag true, then `return false` in Run before clearing), `listener-left-on-exit`,
`unbounded-route-retry`, `age-flag-order-compare`, `level-gt-max`. Read and ruled out: `LogicSkill.GetUndiscoveredPotions`
`i <= Length` (enum starts at 1: correct), GnubbField knights `1..Length` with `[i-1]` (correct), `BowlingLane` upgrade
flag (cleared in Cleanup), MurphyBed/PhotoBooth/Sprinkler flags (cleared on each exit), HorseJump `mClaimedJump`
(cleared in Cleanup), Homework `while (HelpingSim != null)` (helper's Cleanup clears it), same-sim hits (self pushes,
self drinks, makeovers: by design).

## 2. Ranked findings

| # | Where | Flaw | Player effect | Verdict |
|---:|---|---|---|---|
| 1 | `BowlingLane+BowlInBowlingLane.Run` start wait | `while (!ReadyToStart()) Sleep(5)` checks no exit reason; a listed bowler that never comes keeps it waiting | first bowler stuck at the lane, cancel does nothing | **fixed: `bowlinglane`** (§3) |
| 2 | `SpaceSimulator+DoRideB.Run` | waits for `RideOver` with only the rider's (transient) cancel reason as a way out | ride partner stuck in the simulator for good (NPC) / until cancelled | **fixed: `spacesimpartner`** (§5) |
| 3 | `BowlingLane+BowlInBowlingLane.BowlingLoop` | failed `RouteToBowl` returns without ending the turn: same route retried every loop | bowler loops route failures, partner waits | **fixed: `bowlinglane`** (§3b) |
| 4 | `OccultGenie+RemoveSim.Run` | `lots[RandomUtil.GetInt(lots.Count)]` (inclusive) | 1 Banish in Count+1 throws after the liking hit: no banish, genie reset; every cast throws in a world without community lots | **fixed: `geniebanish`** (§4) |
| 5 | `BowlingLane.EndBowlerTurn` | join restart tests `sim` (turn ender) instead of `sim2` | latent at EA's `kMaxBowlers` 2; with more bowlers the others overrun their frame array | **fixed: `bowlinglane`** (§3c) |
| 6 | `JamController.SetSweetMove` | `(j != null && s != Exited) \|\| s != PlayingSweetMove`: null jammer throws; an exited or already-sweet-moving jammer is re-flagged | band jam: a Sweet Move on a leaving jammer hangs it in the jam for good | **fixed: `bandjam`** (§6) |
| 7 | `BoxOfDanger` statue line (static `Queue<Sim>`) | Cleanup `if (Contains(Actor)) Dequeue()` removes the head, not Actor | two magicians doing Human Statue at once + one reset mid-wait: every later statue trick waits for good; the line is saved with the game | **fixed: `statueline`** (§7) |
| 8 | `SpaceSimulator+RideWith.Run` | `RideOver = true` is set before `DoTimedLoop`, so the partner's DoRideB ends at the start of the ride | partner's interaction ends while still in the ride animation [order verified, effect inferred] | **fixed: `spacesimpartner`** + in-game counters (§10) |
| 9 | `FirePit+PutAway.Run` | already-in-inventory exit returns true with `BeingCleaned` still set | fire pit unusable (cannot light/clean) until reset; PicnicBasket+CleanUp has the same exit (two Sims queue Clean Up: the basket can never be eaten from or cleaned again) | **fixed: `cleanflag`** (§8) |
| 10 | `MagicMirror` gossip | partner pick compares with the previous pick, not the Actor | half the mirror gossips are silently skipped | cosmetic, skipped |

## 3. bowlinglane (counters 2448-2452, check `BowlingLaneCheck`, repro `bowling-lane-wait`)

[verified, decompiled] `BowlInBowlingLane.Run`: when it is the Sim's turn it routes to the lane, then spins on
`ReadyToStart()` (every other listed bowler registered as a watcher) with no exit-reason test. `AskToJoinBowlingGame.Run`
adds the joiner's id (`JoinGame`) before `PushAsContinuation` and never removes it when the push returns null; a listed
Sim gone from the world never registers either. Fixes (switch on):
- (a) `ReadyToStart` hook: listed bowlers (not the waiter) with no Sim in the world or nothing targeting this lane in
  their queue are dropped (`StopBowling`, or the id removed when there is no Sim) [2449]; a waiter with a cancel exit
  reason (`ExitReason.Canceled`) is let through [2448]; EA's `DoLoop` then ends the game at once, as a cancel later on.
- (b) `RouteToBowl` hook: 3 failed routes in a row inside the game loop (state machine entered) add
  `ExitReason.RouteFailed` [2450]; Run's own first route already takes EA's exit and is not counted.
- (c) `EndBowlerTurn` hook: when `mRestartGame` is set, EA's `RestartGame` is applied to every other listed bowler whose
  game is running (state machine entered) [2451], then EA's body runs (it restarts the turn ender).
- (d) `EveryoneIsDone` hook: ids with no Sim are dropped before EA's loops dereference `CreatedSim` [2449].
Household Sims: the fix changes only stuck or throwing cases (vanilla waits for good or throws).

## 4. geniebanish (counters 2453-2454, check `GenieBanishCheck`, repro `genie-banish-pick`)

[verified] The Run hook runs EA's body (switch off) or the same steps with `RandomUtil.GetInt(lots.Count - 1)` and EA's
give-up exit for an empty list (switch on): animation, liking hit (`kLTRHitFromRemoving`), target queue cancel, lot pick
skipping `kEP6_BigShow`, `FindGoodLocation`, `TeleportMeHere` "_Genie", `kRemoveSim`, magic points (`kMagicPointCost`).
Loophole kept as EA's (not a crash): points and liking are spent when `FindGoodLocation` fails.

## 5. spacesimpartner (counters 2455-2457, check `SpaceSimPartnerCheck`, repro `spacesim-partner-wait`)

[verified] `RideWith.Run` pushes `DoRideB` (mustRun) on the partner, sets `SimAInRide`, waits for `SimBInRide` and returns
false on a cancel of either Sim or the partner's failed route. `DoRideB` waits `!SimAInRide` (exits on
`SimAFailedToRoute` or a cancel) and then `!RideOver` (exits only on a cancel of itself or of the rider). After the
rider's interaction ends its exit reasons are cleared, so a partner already past the first wait, or starting later,
waits for good. The hook runs after EA's Run (finally): rider = `SimA`, ride not started (`RideOver` false) and no
failed-route signal: the partner's `DoRideB` instances for this simulator are counted [2456] and, switch on, cancelled
with EA's `CancelInteraction` after setting `SimAFailedToRoute` [2455]. A ride that ran (`RideOver` true) and a rider
that failed its own route (EA already signals) are untouched.

## 6. bandjam (counters 2458-2460, check `BandJamCheck`, repro `band-jam-sweetmove`)

Band/jam session flow read end to end (JamController, JamInfo, BandInstrument PlayBandInstrument / JoinJam / SweetMove /
PerformGig) [verified, decompiled]. Join: `StartPlaying` joins a jam found in the room (`InstrumentToJoin`) or makes a new
controller, `AddJammer` waits (exit-checked) until the head's loop event turns it Playing. Leave: `StopPlaying` nulls the
instrument's controller and calls `RemoveJammer(Actor, playExitAnim: true)`, which sets PendingExit and Sleep-waits, with
no exit test, until the head jammer's loop event (PlayLoopCallBack, fired only for `mJammers[0]`'s state machine) turns it
Exited. Leader leaving: the next listed jammer becomes the head, so the loop goes on. Found:
- (a) `SetSweetMove` operator flaw (§2 #6): the own-state line also runs for PendingExit, WarmingUp, Routing, ReadyToPlay;
  on PendingExit the state cycles SweetMoveRequested -> PlayingSweetMove -> Playing and never reaches Exited: the leaving
  Sim hangs inside StopPlaying for good (uncancellable). The SweetMove interaction's Test needs Playing, but the pie menu
  is built earlier than the click. Fix: the own request is applied only to a Playing (or already requested) jammer; the
  other jammers' 50% roll runs as EA's; a requester not in the jam (EA throws) does nothing.
- (b) `RemoveJammer(BandInstrument)` (instrument `DoReset`) never stops the SoundArray when the list empties, and the Sim's
  later `RemoveJammer(Sim)` returns early (not listed any more): the jam music loops on with nobody playing [inferred: a
  SoundArray plays until `Stop`]. Fix: either overload stops the sound of an empty controller (EA's `StopSoundArray`).
- Read, no change: `PlayBandInstrument.Cleanup` calls `base.Cleanup()` only when the instrument's controller is null; a
  9th joiner (`NumJammers >= 8`, `FailedToStart`) keeps the joined controller and skips the base cleanup [verified; very
  rare, JoinJam's Test caps at 8]. `StartGigSession`'s non-head branch dereferences `GetJammerInfo(s)` (gig only, the
  participants are added first). Exit waits in `RemoveJammer` stay uncancellable by EA design; with (a) fixed every path
  reaches Exited while the head's state machine runs.

## 7. statueline (counters 2461-2462, check `StatueLineCheck`, repro `statue-line-dequeue`)

[verified] `BoxOfDanger.mStatueLine` is a static `[Persistable]` `Queue<Sim>` shared by every box; LowLODPose/HighLODPose
enqueue the performer and Sleep-wait for the head inside an animation event; PoseCapture and Cleanup do
`if (Contains(Actor)) Dequeue()`, right for the head, wrong for a performer cancelled or reset while waiting (the head is
removed and the stale Actor entry stays, saved with the game). Fix: Cleanup removes Actor itself; each pose first drops
entries of Sims gone or not doing a trick (a stale line from an old save clears on the next trick).

## 8. cleanflag (counters 2463-2464, check `CleanFlagCheck`, repro `clean-flag-left`)

[verified] FirePit+PutAway: BeingCleaned set before the route; `TargetIsAlreadyCleanedUp` (already in an inventory) returns
true with it set; only `DoReset` clears it; CanLight/CanEnjoy/CanPutOut/CanClean all need it clear. PicnicBasket+CleanUp:
same exit when the basket is no longer Served (another Sim finished cleaning first); the next DoServe/Served basket fails
`CanEnjoy` (no plates, no Clean Up). Fix: a run that started with the flag clear and ends with it set clears it.

## 9. birdtvnull (counters 2465-2466, check `BirdTvNullCheck`, repro `bird-tv-null-channel`)

[verified] `TV.TurnOnOff(true)` sets `TurnedOn` and returns before picking a channel when the TV is broken;
`TV.CurrentChannel` is null for an empty / unknown channel key; `MinorPetBirdLarge.ShouldReactToTv` (bird idle update)
reads `.IsRomanticChannel` / `.IsHorrorChannel` on it. Fix: EA's test with channel-less TVs skipped. (Infer# TV.cs:3212 is
the same null at load for a channel from a removed pack: `PlayGivenChannel` at TV startup; left, rare.)

## 10. spacesimpartner, second flaw: RideOver set at the ride's start (counters 2467-2468, repro `spacesim-ride-over-timing`)

[verified order] RideWith.Run: wait for the partner, fee, StandardEntry, state machine with x = rider and y = partner,
`BeginCommodityUpdates`, **`RideOver = true`**, `DoTimedLoop(kTimeRideLasts)`, puke/cheer, liking. DoRideB (partner):
`while (!RideOver)` then puke check or cheer, `EndCommodityUpdates`, `StandardExit`, return. So the partner's interaction
ends (and its end reaction plays) at the start of the ride while the rider's state machine still animates it as `y`.
What the partner then does in game (stays posed, or its next interaction pulls it out mid-ride) is [inferred]; the
always-on counters measure it: at the end of each ride that ran, 2467 = the partner's DoRideB was already gone, 2468 =
still in it. Fix (same switch): the `RideOver` getter reads false while the rider's RideWith.Run is running, so the
partner's wait, reaction and exit come at the end of the ride. If Mono inlines the trivial getter into DoRideB.Run the
fix has no effect; 2468 staying 0 with the switch on shows that.

## 11. Unfixed, ranked (next pass)

1. InferSharp on Sims3GameplaySystems: run once in a gap between game tests; the IL translation ran out of memory under the
   script's default `DOTNET_GCHeapHardLimit` (1.5 GB). Retry with `DOTNET_GCHeapHardLimit=0xB0000000 JOBS=2 nice -n 15
   tools/static/infersharp.sh sims3gameplaysystems Sims3GameplaySystems` (~2.75 GB) while no game runs (a test started
   before the retry), then triage its gameplay hits.
2. `PlayBandInstrument.Cleanup` skips `base.Cleanup()` for a 9th joiner (§6, very rare).
3. `TV` startup `PlayGivenChannel` with a channel from a removed pack (§9, rare).
4. InferSharp `TombTrap.EnterState` (interprocedural, not confirmed) and `PicnicTable.CreateSitHereForEatingTransition`
   null (slot pairs come from the table's own seats: not reachable in normal play) [inferred]: no player effect found.
5. MagicMirror gossip partner pick (cosmetic).
