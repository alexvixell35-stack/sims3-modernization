# 528. Online dating profiles for Sims who never made one (`datingprofile`)

Issue #175. A player saw the online dating app "keep creating" profiles, also for their own Sims; an EA forum thread
reports the same (no profile made, yet the Sim acts as signed up, and Remove Profile does not stick). This doc
describes the cause, the ts3fixes fix (switch `datingprofile`, on by default; counters 2051-2052, registry
`fixes-2051`; ledger §932) and the live check. The same fix runs as IL patches in a player's core mod (build 98).

Claim marks: **[verified]** = read in the game's IL, or seen live; **[inferred]** = our reading, not yet checked in game.

## 1. The bug

- `OnlineDatingManager.GetRecordForSim(id)` adds an empty `OnlineDatingRecord` (no `Profile`) for any Sim it is asked
  about **[verified]**.
- `OnlineDatingManager.DidSimsMeetOnline(a, b)` calls it for both Sims and then asks each record whether the other Sim
  is an accepted date **[verified]**. Its callers: `CelebrityManager.CanSocialize` (every social with a celebrity who is
  not a friend, autonomy included) and `InviteToLotSituation.MeetUp.Init` **[verified]**.
- `OnlineDatingManager.SimHasProfile(id)` is `mActiveRecords.ContainsKey(id)` **[verified]**. So after one celebrity
  social a Sim "has a profile": the computer (and phone mods that copy EA's tests) offers Browse, Check Messages and
  Remove; Edit builds a blank profile (`GetProfileForSim` fills the empty record); Remove deletes the record, and the
  next lookup makes it again.
- The records are saved with the manager. `PostWorldInit` only drops the records of Sims that no longer exist
  **[verified]**, so the empty records add up in an old save.
- Not the cause: autonomy. The four computer interactions (Create, Remove, Browse, Check Messages) refuse autonomous
  use **[verified]**.

## 2. The fix

Two parts, both behind `datingprofile` (on by default):

1. `SimHasProfile` (`mods/ts3fixes/src/DatingProfile.cs`, hook `DatingProfileHook.HasHook`, `EaFixes.cs` patch list):
   after EA's answer, a true answer for a record whose `Profile` is null becomes false. Counter [2051]
   `datingprofile_no_profile`. This also covers the empty records already in a save, so nothing has to scan them at
   load; they stay in the save, unused.
2. `DidSimsMeetOnline` on the sim thread reads the records without making one (`AutShadow.cs`, `AutDatingHook.MetPeek`).
   EA's own body answers false for a Sim with no accepted dates, which is what a missing record answers here; the
   worker side of the same hook (AutShadow H4, I-122-15) already answered this way. Counter [2052]
   `datingprofile_no_record`: calls where EA's body would have made a record.

Part 2 lives in AutShadow's hook because ts3fixes puts one patch on a method, and AutShadow swaps `DidSimsMeetOnline`
(H4). ts3fixes installs AutShadow's managed hooks at every start (`ApplyAutShadow` in `Fixes.cs`), whatever the native
`[AutShadow]` section says. So part 2 is in place with the release defaults too [verified: job
`bp-dating-profile-noaut`, §3]. The method has no hook only when AutShadow's member lookup fails (an "AutShadow: off"
line in `ts3fixes.applied`). Then the empty records are still made, and part 1 keeps them from counting as a profile.

Not changed: EA's other `GetRecordForSim` callers (the profile UI paths, `SimMetOnlineDate`). They run only for a Sim
with a profile or after an online date.

## 3. Test

Repro `dating-profile-check`, queue `tools/harness/queues/dating-profile.json`, save Baseline-BP. **It needs a core
without the b98 IL patch** (simler90's UPDATE242 core without our patches). The player's core mod carries the same fix
as an IL patch, so with it the off arm sees no bug. The agent check `Ts3Fixes.Fixes.DatingProfileCheck` picks two Sims with no record outside the active household,
calls `DidSimsMeetOnline` on them once, then `SimHasProfile` on each.

- Off arm (`expect broken`): both records made (records added 2), both read as a profile.
- On arm (`expect fixed`): the off arm's two empty records read as no profile; two more Sims get no record when H4 is
  in place (records added 0), or records that read as no profile when it is not. Then every empty record the check
  made is removed.
- Job `bp-dating-profile-check` runs with AutShadow on (Mode 0) and needs both counters at 1 or more.
- Job `bp-dating-profile-noaut` runs the release defaults (native AutShadow off) and needs `datingprofile_no_profile`.
- Both jobs have H4's managed hook (§2).

**Result (2026-11-03, build 94b7eac, `docs/data/results/2026-11-03-queue-dating-profile.md`):**

- **05:37 run, simler90's UPDATE242 core without our patches in `Mods/Packages`:** both jobs PASS. The player's core
  and Tiny UI Fix were held aside for the run, then moved back and checked.
  - Off arm, both jobs: records added 2, `SimHasProfile` True/True.
  - On arm, both jobs: records added 0, `SimHasProfile` False/False. The off arm's 2 empty records read as no profile.
    The check removed the empty records it made.
  - The release-defaults job also made no record: H4's managed hook was in place with native AutShadow off.
- **05:29 run, the player's core (b98):** FAIL on the off arm. Records added 0 and no profile with the switch off: the
  core's IL patch had already fixed it.

## 4. What is left

- A computer's menu read on a Sim with an empty record (the check calls `SimHasProfile` directly).
- Old empty records stay in the save. A clean-up would be one pass at load over `mActiveRecords`; it is not needed for
  the fix.
