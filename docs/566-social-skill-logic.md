# 566: Social and skill-object logic batch (group talk, conversation merge, portrait, picnic)

**In-game status:** `grouptalkstale`, `convmerge`, `portraitpose`, `picniccamera`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15.

2026-11-15, static (decompiled EA scripts; `game-copy/analysis/gameplay-scan/ranked.tsv` and `grep-sleep-wait.txt`
filtered to social / romance / skill-object files, an alarm add/remove census of HobbiesSkills, Gardening, Socializing
and Fishing, then reads of each system's Run / Cleanup / join-leave paths). Built, proven in game (status above). All switches off by
default; EA bug fixes only, using EA's own methods. Scope: docs/555-565 not repeated (band instruments, social sync
waits, hot-tub woohoo FX are covered there).

| § | Switch | Counters | Check | Repro |
|---|---|---|---|---|
| 1 | `grouptalkstale` | 2541-2543 | `Ts3Fixes.Fixes.GroupTalkStaleCheck` | `group-talk-stale` |
| 2 | `convmerge` | 2544-2545 | `Ts3Fixes.Fixes.ConvMergeCheck` | `conv-merge-region` |
| 4 | `portraitpose` | 2546-2547 | `Ts3Fixes.Fixes.PortraitPoseCheck` | `portrait-pose-left` |
| 5 | `picniccamera` | 2548-2549 | `Ts3Fixes.Fixes.PicnicCameraCheck` | `picnic-camera-callback` |

The checks evaluate the fix decisions with controls (off prints BROKEN, on FIXED); neither changes the world.

## 1. Group talk keeps Sims who left (`grouptalkstale`, GroupTalkStale.cs)

[verified, decompiled] `Sim.mGroupTalk` (private `Sim+GroupTalk`: member list, speaker, talk times) is the ambient
chat run by `TryGroupTalk` from activity loops. `RegisterGroupTalk` joins the group of another Sim on the same lot or
starts one; only `UnregisterGroupTalk` takes a Sim out. `ShouldDoGroupTalk` registers on first use, and many
activities call `TryGroupTalk` every tick without ever unregistering (ChessTable, EatHeldFood, Homework, the cooking
pots, PoolTable, HotTub, ...; 50 call sites, ~30 unregister). `RegisterGroupTalk` with a group already set joins or
creates another without leaving the old one, which keeps the Sim as a phantom member. Nothing drops a member that left.

Effects: the remaining Sims of an old group talk with a Sim who is gone: it can be the current speaker or the one
talked to; a listener gets `Relationship.UpdateRelationshipForGroupTalk` with it, members get the social bump and
"Chat" `kSocialInteraction` events (wants, opportunities). The departed Sim, in a later `TryGroupTalk` elsewhere, keeps
its old group (no re-register while `mGroupTalk` is set) and "talks" with the Sims on the old lot
(`CanMemberBeSeenBy` compares only room ids, which repeat between lots, and facing), and never joins the Sims it is
with. Reach: every Sim that used one of the never-unregistering activities and then moved on [verified path,
frequency inferred].

Fix: in `ShouldDoGroupTalk`, before EA's body, members that are destroyed or on another lot are taken out
(`UnregisterGroupTalk` when the group is theirs, else the group's `RemoveMember`); if that leaves the Sim alone it
unregisters, so EA's body registers it with the Sims on its lot. In `RegisterGroupTalk`, a Sim with a group leaves it
first. Groups whose members share the lot run EA's code unchanged. Household Sims: only the phantom relationship /
event gains with absent Sims go away (the bug). Counters 2541 dropped, 2542 left old group, 2543 errors.

## 2. Merged conversations leak their discourage footprints (`convmerge`, ConvMerge.cs)

[verified, decompiled] Each `Conversation` owns a `SocialGroupDiscourageRegion`; every social start
(`SocialInteractionA`) adds both Sims and calls `UpdateFootprint`, which creates a hidden `DynamicSlot` object and
registers a discourage route footprint (`Route.AddDiscourageFootprint`) around the group. EA removes it with
`DiscourageRegion.Cleanup` in `FinishConversation` and in `LeaveConversation` when one Sim is left.
`SocialComponent.JoinConversationWith` with both Sims already in different conversations calls `Conversation.Merge`,
which builds a new conversation and repoints all members; the two old ones are dropped without that cleanup. Their
footprint objects stay in the world at the old group spots (Sims route around invisible patches) and one hidden object
leaks per merge (parties, community lots). The old `SocialGroupInteractionSituation`s end on EA's own 45-minute give-up
alarm, so only the regions leak. Whether the leaked slot objects persist in the save is [inferred, not checked].

Fix: after EA's `Merge`, both old conversations' regions get EA's `DynamicRegionBase.Cleanup` (the old situation only
adds points to its region and never calls `UpdateFootprint`, so nothing re-creates them; `SocialInteraction`'s
Disable/Enable around routes is a no-op once the object is gone). Counters 2544 cleaned, 2545 errors.

## 3. Checked, not fixed (leftovers, ranked)

1. `Easel.PaintPortrait` subject left waiting / posing: fixed in §4.
2. `NectarMaker.MakeNectar` with no `Cleanup`: ruled out on re-read. A Sim reset adds the object it was using
   (`StandardEntry` use list) to the reset (`Sim` reset: `AddObjectToReset`), and `NectarMaker.DoReset` runs
   `UpdateStateOnInterrupt` (state machine disposed, `MakingNectar` back to `SquishedFruit` / `FruitAddable`) [verified,
   decompiled]. Not a bug.
3. `SocialInteractionA.Run`: `Target.Conversation != Actor.Conversation` then `Target.Conversation.BeginJoin` throws
   when the target has no conversation and the actor has one; probably unreachable because the actor's socializing
   posture (and its conversation) ends before a new social starts [inferred, not confirmed].
4. `PicnicBasket.Serve` camera callback: fixed in §5.
5. Woohoo cancel paths (bed, shower, hot tub, pet house, sauna): `HandleNearbyWoohoo(More)` without a cancel-safe
   `Less` leaves woohooium metal glowing after a reset (cosmetic). Shower woohoo `HavingWooHoo` waits always end through
   `WooHoo.Cleanup`.
6. `Fishing` box catch: a junk item that fails `Inventory.TryToAdd` stays out of world (leak, rare). `Gossip.GetGossipAbout`
   wraps a null text in quotes (shows `""`); random gossip subjects can be pets (cosmetic).
7. Read, no bug: chess move loop (`PracticeChessDelegate`) and win/lose flow, ping-pong `mSimYCanExit` (Cleanup sets
   it), PlantObject / plant-many reservations, gardening chain re-picks (bounded by `IgnorePlants`), `Water` null state
   machine (`GetStateMachine` never returns null), gem/rock fade-outs (`Inventory` resets opacity), `RomanticBuffSTCReset`
   alarm (cleared on reset), conversation `BeginJoin`/`EndJoin` (Cleanup ends the join).

## 4. Portrait subject left waiting or posing (`portraitpose`, PortraitPose.cs)

[verified, decompiled] `Easel.PaintPortrait.Run` queues `PoseForPortrait` on the subject first, then routes and syncs
(Started, Routed, Committed). Only the success path ends the pose (`Paintee.AddExitReason(Finished)`). Failed sync waits
also flag the subject (`OnWaitForSynchronizationLevelWithSimFailed` adds `SynchronizationFailed`, part of
`ExitReason.Default`), so the race named in the first pass is covered by EA. The other exits are not: a failed route to
the easel or a failed UI check returns with the pose still queued (the subject walks over and waits up to 30 sim-minutes
per sync step), and a painter reset after both reached Committed leaves the subject in `DoLoop(ExitReason.Default)`
with no callback, posing until cancelled or a motive fails.

Fix: `PaintPortrait.Run` inside try/finally; when it did not succeed (false or unwinding), the subject's
`PoseForPortrait` entries with `Painter` = this painter are cancelled with EA's `InteractionQueue.CancelInteraction`.
A successful portrait is unchanged. Counters 2546 cancelled, 2547 errors.

## 5. Served picnic keeps its camera callback (`picniccamera`, PicnicCamera.cs)

[verified, decompiled] `PicnicBasket.Serve.Run` adds `OnMapViewEnabled` to the static
`CameraController.OnCameraMapViewEnabledCallback` (and `OnGameStateChanging` to `Responder.GameStateChanging`) for the
blanket placement. The failure path removes both; the success path removes only `GameStateChanging`. Each served picnic
stays on the static camera event for the session: every later map-view toggle runs `UserToolUtils.OnClose` (closes an
open placement tool) and the interaction is never freed.

Fix: `Serve.Run` inside try/finally; afterwards this instance's `OnMapViewEnabled` is removed from the camera event
(same handler as EA's `-=`; a no-op when EA already removed it). `GameStateChanging` is left to EA, since its handler
also unlocks the active lot. Counters 2548 removed (handler count dropped), 2549 errors; `PicnicCameraCheck result`
shows the live handler count.
