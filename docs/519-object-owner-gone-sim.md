# 519. An object's owner component keeps a Sim that is gone (`deadrefsweep` phase 11)

Issue #166, core build 43. `ObjectOwnerComponent` holds `mOwner` (the Sim a reward came from, shown as the tooltip line
"from <Sim>", or a theft's victim) and `mThief`. Both are saved by SimDescription id and re-read at load as that
description's `CreatedSim`; in a running world nothing updates them when that Sim object is destroyed (a townie leaving
the world, a reset that builds a new Sim object), so the destroyed Sim object sits on the component until the next load,
and a load-time graph reset (NRaas ErrorTrap) resets the object (the harness saw a topiary reset this way). This doc
describes the holder and the ts3fixes clean-up: an eleventh phase of the `deadrefsweep` switch (off by default; counter
2047, registry `fixes-2047`; ledger §917; docs/205 §2 for the sweep, docs/517 for the phase it follows).

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The holder

- `GameObject.SetRewardGiver(sim)` adds the component with the giving Sim as `mOwner`; `ObjectOwnerComponent.ObjectStolen`
  sets the thief and the victim **[verified]**.
- `ExportContent` writes `mOwner`'s and `mThief`'s `SimDescriptionId`; `ImportContent` re-reads them as
  `SimDescription.Find(id).CreatedSim`, and returns false (the component is not added) when the owner is null or a theft's
  thief is not instantiated **[verified]**.
- Nothing in `Sim.Destroy` or the reset path touches the component **[verified: core build 43's load scan listed
  destroyed Sims in `mOwner`]**.

## 2. The clean-up

Phase 11 of the spread pass (hourly; since 2026-11-03 only after a Sim leaves or daily, docs/205 §2.3a) (`DeadRefs.cs` `OwnerHolders`), for every `GameObject` with the component, mirrors
what EA's import would do at the next load:

- a gone owner or thief Sim (`HasBeenDestroyed`) becomes the live instance of the same description, when there is one
  (a destroyed Sim keeps its description);
- a gone reward giver (no thief) with no live instance drops the component through `RemoveComponent`, which disposes it
  and takes the Return To Owner interaction away;
- a theft whose victim or thief is gone with no live instance is left alone: the stolen state belongs to it (EA's import
  would drop it; we keep the object's history).

The two fields are private, so they are read and written by reflection. Counter [2047] counts changed references
(`deadrefsweep_objectowners`). The pass before a save is unchanged (phases 1-3 only). Nothing runs with `deadrefsweep`
off.

## 3. Test

Repro `object-owner-gone-check` (queue `tools/harness/queues/object-owner-gone.json`, save Baseline-BP). The hard state
is built by the agent check `DeadRefOwnerCheck seed`: two `SocialJigTwoPerson` made out of world, each a reward from one
townie (same pick rule as docs/516), both Sim objects destroyed as a reset does; `putback` instantiates the first townie
again, so the first jig is the relink case and the second the drop case. Off arm: `DeadRefCheck sweep` refuses and
`expect left` sees both gone entries. On arm: the pass is started at once and `state` polled until the first jig's owner
is the live Sim and the second jig has no component; `expect cleaned` requires that; the job requires
`deadrefsweep_objectowners >= 2`.

Result (2026-11-02 02:47, `bp-object-owner-gone-check`, PASS in 15.1 min, al18 rerun): off arm, the seed made two jigs out of world with a reward-giver component each (Lenny Shutter, Diana Jones), destroyed both Sim objects (gone 2 after 2 s), put the first back (live, still gone 2: nothing relinks on its own), and `expect left` found both entries unchanged. On arm, the same seed with Johnny Jones and Katrina Pala; the pass started by the sweep step relinked the first jig to the live Sim object and dropped the second's component at once (gone 0, relinked, dropped at the first poll); `expect cleaned` passed and `deadrefsweep_objectowners` reached 2. Run 1 (00:48) had failed in the seed itself: EA's `SetRewardGiver` throws for every call (docs/525), so the seed now builds the component directly.

## 4. What is left

A theft with a gone Sim and no live instance stays as it is (§2). `Celebrity.mWaitingToReact` and the cooking appliance
handlers (docs/205 §4) are not in this phase.

Side finding: `GameObject.SetRewardGiver(Sim)` cannot be used for the seed. It calls `AddComponent<ObjectOwnerComponent>`
with `{ sim, null }`, and `AddComponentInternal` finds the constructor by `list.ConvertAll(o => o.GetType())`, so the
null thief throws `NullReferenceException` before any component exists [verified: the repro's first run, 2026-11-02
00:33; static read of `AddComponentInternal`]. Whether any EA interaction still reaches `SetRewardGiver` is not checked
here; the seed adds the component with the one-argument constructor and sets the owner by reflection.
