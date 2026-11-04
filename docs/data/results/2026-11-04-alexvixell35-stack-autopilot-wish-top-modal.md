# Autopilot answered a hidden lifetime wish dialog in a loop (2026-11-03, old save)

Machine: Steam, contributor alexvixell35-stack. Save "1111 kit5", queue fs-roall-local (local), job old-roall-fs-ab-rerun, seed 12345.

## What happened

- 23:58:12 the clock guard failed the job: `sim clock stuck at day 13 22:43 for ~60 s (speed Pause, sub_state LiveModeState)`.
- The modal on top was EA's age-up dialog (`Happy Birthday!`, "Faith is finally a young adult ... you may choose a trait for Faith.", one button `#2`).
- In 21 earlier runs the autopilot clicked that dialog: `ap click: Faith is finally a young adult ... -> #2 accept (only button)`.
- This time the upkeep log shows the autopilot alive, but answering something else:
  - `23:57:13 ... dialogs 90 (lifetime wish -> first wish + okay; ...)`
  - `23:57:43 ... dialogs 120 (lifetime wish -> first wish + okay; ...)`
- That is 210 answers in 60 s, every 250 ms step, none of them effective. The age-up dialog was never answered.

## Cause

`AutoDialog` (HarnessHandlers.cs) calls `AnswerLifetimeWish` before it looks at the top modal. `AnswerLifetimeWish` reads EA's static `LifetimeWishSelectionDialog.sDialog` / `WishPickerDialog.sDialog`. When one is set it selects a wish, presses Okay and returns "answered", whichever modal is on top.

`sDialog` was set while the age-up dialog was the top modal. Either a wish dialog sat under it, or one was built and never reached the screen. Its Okay ended nothing, so each step answered it again and the top dialog stayed.

The trip lines did not show this. The 210 answers were only in `<tag>-upkeep.log`.

## Fix

- `AnswerLifetimeWish(modal, r)` answers only when that dialog's `ModalDialogWindow` is the top modal (`UIManager.GetModalWindow()`, same wrapper per window handle). Otherwise the top modal gets the usual rules / only button / safe default path.
- `profile-world.ts`: at a clock-guard trip, one more line: `at the trip: autopilot N answers since the last upkeep (last: ...), blocked ...`. A dialog answered in a loop now shows next to the stuck clock.

## Checks

- `node tools/harness/build-agent.ts`: compiles against the game's assemblies (465,346-byte package).
- `node tools/pr-preflight.ts`: no problems.
- Game check: see "Game runs" below (wish dialogs on top must still read `lifetime wish -> first wish + okay`).

## Game runs

Same save, queue roall-ab-oldsave (local), 2026-11-04. Faith ages up in both jobs. Answers from `<tag>-upkeep.log`:

| job | agent | answers in the 30 s window, in order |
|---|---|---|
| old-roall-ab (00:11) | before the fix | `lifetime wish -> first wish + okay`, then `Faith is finally a young adult ... -> #2 accept (only button)` |
| old-roall-ab-onfirst (00:26) | with the fix (package rebuilt at the job start from the fixed source) | `Faith is finally ... -> #2 accept (only button)`, `Workaholic -> #12 cancel (safe default; ...)`, `lifetime wish -> first wish + okay` |

- Before the fix the wish dialog was answered while the age-up dialog was on top: here once and it worked, in the failed job 210 times without effect.
- With the fix the top dialog goes first each time: the age-up message, then EA's trait picker, then the wish dialog once it is on top.
- The wish dialog on top is still recognised (`first wish + okay`, no "no safe button" line), so the window compare works in game.
