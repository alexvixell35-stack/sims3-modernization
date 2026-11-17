# Trying the bridge rewind mode: a short guide

For Apex Radiance's maintainer. No coding needed. About 20 minutes. Nothing here changes a save.
The same steps, shorter, are folded into the pull request text (`PR-BODY.md`).

## What this is

A test feature for the RTX Remix bridge. It lets a test tool take a "photo" of the running game after a world has loaded
and later jump the game back to that exact moment, without loading again. It is off unless you turn it on, and players
never need it.

The rule it follows: the jump back is exact, or it does not happen. If the bridge cannot put something back exactly, it
says "refused", changes nothing, and writes the reason into its log.

## Before you start

- Make a copy of your `Game\Bin` folder's bridge files so you can put them back: `d3d9.dll`, `bridge.conf` and the
  whole `.trex` folder.
- You get a zip from us with two files and this guide: `d3d9.dll` and `.trex\NvRemixBridge.exe`. Or build them yourself:
  `native\remix-bridge\build.cmd` on the pull request's branch.

## Part 1: nothing changes when it is off (5 min)

1. Close the game.
2. Copy our `d3d9.dll` into `Game\Bin` and our `NvRemixBridge.exe` into `Game\Bin\.trex` (replace the files there).
3. Start the game, load any world, play for a few minutes, open the Apex Radiance menu, change a setting or two.
4. Expected: everything looks and behaves as before. If anything is different, stop here and tell us what you saw.

## Part 2: turn it on, still nothing changes by itself (5 min)

1. Close the game.
2. Open `Game\Bin\bridge.conf` in Notepad, add this line at the end, save:
   `rewindMode = True`
3. Do the same in `Game\Bin\.trex\bridge.conf`.
4. Start the game, load a world, play for a few minutes.
5. Expected: again no difference. The mode only waits for a test tool to ask.
6. To see that it is on: `Game\Bin\rtx-remix\logs\bridge32.log` has one line with `[Rewind] Rewind mode is on`.

## Part 3: one rewind (10 min, optional, needs our tool)

1. With the game in a loaded world, run the command we send you in a terminal. It pauses the game, takes the snapshot,
   lets the game play for 20 seconds, then rewinds.
2. Expected: after the rewind the game is back at the moment of the snapshot (same time of day, Sims in the same places)
   and plays on normally. The picture should look right: no black or missing textures, no flicker.
3. If the tool prints "refuse", nothing was changed; that is a valid result. The reason is a `[Rewind]` line in
   `Game\Bin\rtx-remix\logs\bridge64.log`. One reason we expect on some setups: "a multisampled render target cannot be
   copied exactly" (anti-aliasing through the game's own multisampling). Send us the lines either way.

## Part 4: the tests that need no game (5 min, optional, needs the build tools)

1. On the pull request's branch run `native\remix-bridge\build.cmd`, then `native\remix-bridge\test-rewind.cmd`.
2. Expected: the last line is `rewind tests passed`. Two small windowless test programs run for a few seconds each.

## What to send back

- What you saw in each part, in your own words. A screenshot if something looked wrong.
- The files `Game\Bin\rtx-remix\logs\bridge32.log` and `bridge64.log`.

## Putting things back

Close the game, copy your saved `d3d9.dll`, `bridge.conf` and `.trex` folder back into `Game\Bin`. Removing the
`rewindMode = True` lines alone also turns the feature off.
