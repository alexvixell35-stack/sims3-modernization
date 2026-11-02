# 240. asyncjigsync: paired socials vs RouteAsync (release fix)

Status 2026-11-03: built, offline self-check passes; repro `showoff-jig-race` (in-load A/B) and smoke `hug-jig-smoke` in queue `showoff-jig-race.json`. Fixes the v0.2.4 player report in docs/226.

## Root cause [verified in decompiled code + player log]

- `Sim+DoShowOffMove.Run` (Sim.cs ~10086): after both Sims reach `SyncLevel.Started`, the first one there creates `SocialJigTwoPerson` with `CreateObjectOutOfWorld`, stores it in its own and the linked instance's `SimJig`, plans `PlanToPoint(target)` and calls `PlaceJigAlongRoute`, which plans `PlanToPointRadius` and only then sets the jig's position and calls `AddToWorld` (SocialJigTwoPerson.cs 275-317).
- Vanilla plans are synchronous: the partner's task never runs between `CreateObjectOutOfWorld` and `AddToWorld`.
- With `[RouteAsync]` the initiator's own plans send START and sleep a tick at a time in `AsyncWait` (`RouteBackoff.cs`). The idle partner then runs: `SimJig != null`, so it plans `RouteToJigB` to the unplaced jig.
- Player log (ts3native 0.36.105, ts3fixes 0.1.1-test-showoff1, `showoff:` lines): child `Plan async Succeeded`, then adult `Plan async FailedBecausePlannerAborted` (or `FailedBecauseNoPathToDestination blocker Object`) with goal `-20000,-20000,-20000`, then the child's second plan and `jig placed`. The adult's failed DoRoute ends the linked social: the "cannot go there" balloon.
- The goal -20000 is the slot position of a jig that has no world position yet [inferred, docs/51 I-424-1].
- 45 decompiled files use the same create-out-of-world-then-plan pattern (standard socials near walls / doorways through `SocialInteraction` + `CreateJigForTwoPersonSocial`, `PickUpChild`, `ToddlerHug`, occult socials, ...).

## Fix (ts3fixes, switch `asyncjigsync`, tuning `kAsyncJigSync`, default on)

- `AsyncEligible` (own START, Plan and the A10 kinds) and `ForeignEligible` (foreign and owner-task STARTs) answer false while the current task's Sim's `CurrentInteraction`, or its `LinkedInteractionInstance`, holds a `SocialJig` (any field typed `SocialJig` or a subclass, own or inherited, private too; per-type cached `FieldInfo[]`, `AsyncJigSyncCore.cs`) whose `InWorld` is false. The plan then runs synchronously, as vanilla.
- Covers both sides: the initiator keeps its plans sync until the jig is in the world, so the partner cannot run in between; a partner holding the unplaced jig also plans sync.
- Default on: it only restores vanilla order inside an already-shipped feature. Off = v0.2.4 behaviour (the race).
- Cost: one `CurrentInteraction` read and a cached field scan (usually 0-2 fields) per eligible plan.
- Not covered: jigs held only by situations (PerformerSituation, GrimReaperSituation) or in locals never handed to the partner; no partner can race on those through an interaction.

Counters: 758 own plans kept sync, 759 of those with the jig only in the linked interaction (agent `patches`: `asyncjigsync_kept_sync`, `asyncjigsync_linked`).

## Tests

- Offline: `node tools/selftest/asyncjigsync/run.ts` (fields on base / subclass / private, InWorld on / off, null).
- Repro `showoff-jig-race` (Baseline-BP, autonomy off, adult idle): 16 tries per arm, outside / inside alternating, adult 5-10 m away; arm `off` then `on` in one load. `ShowOffBadgesCheck` records a try as failed when the child's DoRoute is not ok or the adult had a failed plan or DoRoute inside `DoShowOffMove`; `verdict ab` passes when the off arm failed at least once and the on arm never.
- Smoke `hug-jig-smoke`: 6 `Friendly Hug` socials (adult on the child, 5-10 m apart), switch on; no crash, no `routefail:` goal -20000.

`node tools/harness/queue.ts tools/harness/queues/showoff-jig-race.json --no-smoke`

## Player workaround on v0.2.4

In `ts3native.ini` (next to `TS3W.exe`), section `[RouteAsync]`: `Enabled=0`. Turns async route planning off entirely (plans as vanilla) until v0.2.5.
