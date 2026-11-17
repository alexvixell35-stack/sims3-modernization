# 635: what still costs unseen Sims after the 2026-11-17 fixes (bar waits, Sunday spot hitch, social sync)

Status: static analysis on s84 (phase-20261117-1604, Bridgeport, native on, every proven fix on, `unseenservice` and
`spotasync` on), built as default-off switches `unseenservicewait` and `spotbusydefer` (slots 3780-3819), package
r93-combo. NOT RUN.

## 1. Bar orders that `unseenservice` did not serve (787 sim-min, 70 OrderDrink waits)

- Evidence [verified, log]: every OrderDrink wait in s84 is EA's linked hand-off (`syncwait: ... in OrderDrink, partner in
  MakeDrink (linked running)`). At planning time almost all customers and servers were unseen (`ufplansplit ... why 0`;
  two `why 4`, one `why 6`), at busy Bridgeport bars (Ricky 20+ orders, Mikhai, Flo-Flo, Brandy). So the cause is not scope.
- Cause [verified, IL]: `BarAdvanced+BartenderInteraction::Cleanup` dequeues its order first (IL_0007
  `DequeueInteraction`), then pushes `IdleTend` only when `mInteractions` is empty (IL_001f `get_Count`, IL_0051
  `AddNext`), then runs the base Cleanup (IL_0058). With a line at the bar, the next order reaches the queue head while the
  server is still in (or just out of) the last `Make*`, and never in `IdleTend`. `SvcServe`'s server-idle precondition
  accepted only `IdleTend`, so every queued order after the first fell to EA's hand-off. Rounds and trays are not involved:
  all 70 are single OrderDrink.
- Verdict: a needless wait, but not a lock and not a stutter. The customer sleeps in EA's sync loop for ~11 sim-min per
  drink, and the server runs the full animation path for a Sim nobody sees.
- Fix `unseenservicewait` (off): an in-scope order at the head waits in EA's own loop (`Simulator.Sleep(0)`, EA's
  giveupTime, cap 10 sim-min) while the server is between orders: no interaction, or a BartenderInteraction whose order has
  left the queue. A tending server with no interaction and an empty queue counts as idle. After a served order, EA's
  Cleanup tail (IdleTend when the queue is empty) is re-armed. Tier 0 (player, selected, owned venue, residential, high LOD,
  seen) never waits.
- Per-reason counters (counted whenever `unseenservice` is on): 3780-3784 out of scope (player, not community, owned venue,
  high LOD, seen with no tier 2); 3785-3790 precondition (server busy, server queue, hands, slot, not single, cannot make);
  3791 waits, 3792 served after a wait, 3793 waits handed off, 3794 IdleTend re-armed, 3795 check mismatches.
- Check `UnseenServiceWaitCheck` (repro `unseenservicewait-check`): off BROKEN (hand-off), on FIXED (wait, then serve).

## 2. The recurring Sunday 12:47 PM hitch (305 ms, `ufplan kind 2 285 ms src spot:rabbithole@Community Lot`)

- Evidence [verified, log]: `route async: long job 286 ms, own START, IC kind 2, route 259376C6, watchdog check, uncut`
  ended at 56410208 ms. Armstrong Diesel's `spot:visit` check (387 ms, 1 poll) started at ~56409923 ms, so that job is
  Armstrong's tagged async check. Bianca Rubble's `spot:rabbithole` check started ~10 ms later. Its tagged START was refused
  (`sync 284 ms (START refused)`), and RouteBackoff planned it synchronously. That sync plan waits on world+0x918, which the
  worker holds for the whole job (rtsites.cpp, AsyncStart's M1 lock test), so the sim thread stood still for the rest of
  Armstrong's job. The s86 runs (0857 / 0127 / 0923) show the same tick (~28760) and the same ~280 ms.
- Why `spotasync` did not help: it only tags the START. When the START is refused, the old path (a sync plan) still runs.
  Native `JobYield=0`, so `StepAside` cannot sleep it out either.
- Verdict: a needless lock wait on the sim thread (the only game hitch of the run).
- Fix `spotbusydefer` (off): a spotasync check (unseen NPC, own yielding task; never household, selected or unknown scope)
  whose tagged START is refused plans nothing. The check answers "no verdict now" (null, not cached), the same answer
  SpotVerdict already gives past its per-tick budget, and the caller keeps EA's own trip. Counters: 3800 deferred, 3801
  refused tagged STARTs, 3802 check mismatches.
- Check `SpotBusyDeferCheck` (repro `spotbusydefer-check`).

## 3. Social sync (SocialInteractionA/B, BeScared, ZombifiedSocials)

- SocialInteractionB timeout x2 (60 sim-min): Sugar's B waits on Mars, whose ZombifiedSocials (a zombie chase) targets
  household Sim Apollo; ZombifiedSocials failed after 21.6 min, also aimed at Apollo. BeScared / Scare and most
  SocialInteractionA/B waits are household Sims (`HH`). A failed SocialInteractionA costs 0.0 min.
- Verdict: no stutter or lock (the waits are Sleep polls), and the long cases involve household Sims, which are off limits.
  No fix.
- Re-check 2026-11-17 [verified, decompiled]: ZombifiedSocials.Run is the stock social path
  (`BeginSocialInteraction(new SocialInteractionB.Definition(..."BeingEaten"), pairedSocial: true)` → SocialRouteA →
  CreateSocialB with EA's priority raise), no zombie-only receiver push, priority or household test. Mars → Apollo:
  B was queued (StartSync passed, Mars at Started), parked ~21 min behind the active Sim, then removed;
  `InteractionQueue.CleanupInteraction` cancelled Mars (exit CanceledByScript), so "no link" is the state after EA's
  cleanup (docs/546 §14, the parked-receiver case). Sugar → Mars: Mars was in the approach `DoRoute` 34-38 min (partner
  "routing", step needroute), longer than the B side's `kSocialSyncGiveupTime` 30, then RouteFailed; autonomy picked
  Sugar again. That is EA's approach-walk timeout, as intended. Unrelated, unreachable: the
  `ChanceToAutonomouslyPerformBraaaiiinsSocial` property returns itself (no caller; CreateInstance reads the field).
  EA as intended; nothing built, slots 3820-3829 unused.

## 4. Anything else

- hitch-attr: the Sunday spot plan is the only game hitch in s84 (305 ms; the other 3 are harness hitches).
- planwait: ~20% of plans per minute are sync (225-297 per minute), all short (none in the >= slow-plan ufplansplit lines
  for why 0). They are not attributed by scope yet: a lead for a scope split, not a fix.

## 5. s82 follow-up: the Sunday hitch stayed with spotbusydefer (326 ms, tick 28768)

- Run: phase-20261117-1648 (r93; spotasync, spotbusydefer and spotbusydefer's Check all on and passing).
- Evidence [verified, log]: no START was refused this time. `route async: long job 275 ms, own START, IC kind 2, watchdog
  check, uncut: watchdog (KdUncutCeilMs), scope unseen` ended at 59067075 ms. That is Bart Hammer's `spot:visit@Bridgeport
  Eternal Rest Cemetery` check (341 ms, 1 poll); in s84 the same lot was checked for Armstrong Diesel. The hitch frame
  (326 ms, ending at 59067106 ms) spans that job. Every task that ran in the frame (Bronson Littler, Mars Champion, Bart)
  slept one poll of 341-356 ms.
- What the sim thread blocked on [inferred]: not a sync plan (no `sync ... START refused` line) and not a counted job wait.
  The native summary shows `own-job sim waits 0`, site waits (runplan / rtwait / disc) at most 57 ms, and ReadyDrain off.
  There are no StallWatch lines in this run. The block covers the job's whole lifetime, and the worker holds world+0x918
  for the whole job (rtsites.cpp), so the frame most likely waits on that lock at a site we do not count. Confirming the
  site needs a StallWatch or Holders capture of +0x918 at tick ~28768.
- Root cause [verified, source]: in plandeadline.cpp, KdJobBegin returns 0 (UCW_WATCHDOG) for every job with the watchdog
  tag (`sc & 0x400`). spotasync borrows that tag to get its START accepted, so a spot check of a large lot runs with no
  deadline except KdUncutCeilMs (30 s).
- Fix `spotcut` (off): a spotasync check (unseen NPC only) also sends the [UnseenFallback] MARK (UfSend 1) before its
  START. ts3native caps a marked job at ufUs (220 ms less the learned overrun) whatever its tag (ts3native.cpp, `t_ufJob`).
  A cut check (UfSend 2) answers no verdict (null, not cached), as spotbusydefer does, and the caller keeps EA's own trip.
  Counters: 3803 marked, 3804 cut, 3805 check mismatches. Check `SpotCutCheck`.
- Limit: 220 ms keeps the frame under the 300 ms hitch line but not under 200 ms. A shorter cap for this case needs a
  native per-tag deadline (spot checks tagged apart from A1); that is not built yet.
