# Harness: end the load's dialog window when the world is loaded (2026-11-03, alexvixell35-stack)

`cycle.ts` arms `dialog_expect` (60 s) before `load_save` and re-arms it every 10 s while the world loads, so the in-game
autopilot leaves dialogs to the harness's `auto_dialog` accept. The window was never ended, so it ran ~60 s past the load.
A modal that a mod shows right after the load waited that long with nobody clicking it.

## Seen

- Old-save queue job, 2026-11-03 21:30: `ts3fixes: ap click: Welcome to Deep Conversations for The Sims 3 -> (left to the
  harness, dialog_expect)` in ts3native.log right after the load; the welcome dialog (Victor Andrade's Deep Conversations,
  a `SimpleMessageDialog` one sim-minute after the world loads) stayed on screen for about a minute or more [observed].
  The modal holds the game for that time, inside the profile's warm-up slice.

## Fix

`await agent('dialog_expect', { seconds: 0 })` right after the `world loaded` wait (`seconds 0 clears`, AutopilotHandlers.cs).
The settle loop that follows then sees the autopilot clear it within a second.

## Checks

- `node tools/harness/game-alive.ts --self-check`: ok (its `dialog_expect` source asserts still hold).
- Syntax: the type-stripped `cycle.ts` passes `node --check`.
- In game, same save and mods (2026-11-03): before the fix the welcome appeared 11 s after the load and was clicked 47 s
  later (`left to the harness, dialog_expect`, then `#2 accept (only button)` 60 s after the last re-arm). With the fix, on
  the next load: world loaded 21:42:20, welcome clicked by the autopilot at 21:42:32 as it appeared, no `left to the harness`
  line [measured, ts3agent `dialog_expect {status:true}` history].
