## Summary

Adds a rewind mode to the RTX Remix bridge for test harnesses (patch 0015). A test host snapshots the game process after
a world load and later puts its memory back, so one load serves many test runs that start from the same state. The game
then holds the D3D9 state of the snapshot again, and with this patch the bridge server returns to that state too. For
Apex Radiance that means many crash-triage or live checks from one world load, and renderer or shader comparisons drawn
from the same state.

**Flag.** `rewindMode = True` in both copies of `bridge.conf`. Default: not set, off; the shipped `bridge.conf` only
gains comment lines. With it off the client makes no cooperation block, the server takes no snapshot, and each added
hook is one test of a flag that is never set: no allocation, lock, system call or extra command.

**Design.**
- **Park at Present.** At the end of a `Present` the client checks a cooperation block (`Local\rewind-coop-<pid>`). On
  request it parks and holds the send locks of both channels, so no other thread of the game can send. It passes each
  request of the host on as one synchronous `Bridge_Rewind` command, which is new and last in the command list, so the
  existing numbers stay. A request during which another thread sent is refused.
- **Tombstones.** While a snapshot is held, a destroy of an object alive at it is deferred.
- **Copy on write.** Render targets and depth surfaces are copied at the snapshot. The old contents of a vertex buffer,
  index buffer or surface are kept before the first write after it; a level of a default pool texture, which cannot
  be locked, is read through a render target. Both are limited by `rewindMaxKeptMB` (1024) and logged as they grow;
  past the limit the snapshot is given up and freed.
- **Depth surfaces made with Discard.** The game makes its depth surfaces that way, and `StretchRect` does not copy
  one. D3D9 leaves them undefined after a `Present`, so none is kept; the server checks in the command stream that
  the game clears each one before it draws with it, and gives the snapshot up otherwise.
- **Restore by id.** A rewind puts the contents back, releases the objects made since, and restores the handle tables
  (a destroyed object comes back under its id), render targets, device state and data queue positions. The server then
  reads no command until the client's process is back at the snapshot, which it reads from a word in shared memory
  that the host's restore clears.

**Also in this branch: patch 0016.** A game start crashed in `GetBackBuffer` while the device was made. Patch 0010
left render target 0 in the device's count of implicit references, so a new device had one reference less than its
creator held, and a hook's `AddRef` and `Release` destroyed the implicit swapchain. Patch 0016 fixes the count. This
is a bug in patch 0010 itself, not in rewind mode: it can crash plain Apex Radiance at device creation, with the mode
off. It does not depend on rewind mode and can be taken alone; I am happy to split it into its own PR if you prefer.

**Limits.** A rewind is exact or refused before anything changes (`E_FAIL`, reason in `bridge64.log`). The server gives
the snapshot up for multisampled render targets and depth surfaces, a write into a volume texture, a device reset and a
few rare setters. Cursor commands are counted, not undone. A rewind that fails part-way answers `E_ABORT`, and the host
ends the game. The rewind of the game process itself is the host tool's work and not part of this change. Details:
`docs/features/rtx-remix-bridge.md`, Rewind mode.

## Validation

- Tested commit: cb2e62d
- Build (public / developer): not affected, the change is in the bridge only. `native\remix-bridge\build.cmd` from a
  fresh fetch: all 15 patches apply, x86 and x64 build.
- Offline checks: `native\remix-bridge\test-rewind.cmd` passes. `test_rewind_server` runs the server side on a D3D9
  device, 75 checks on the system's d3d9.dll and on DXVK: tables, deferred destroys, contents of buffers, textures,
  render targets, back buffer and depth surface, device state, queries, the memory limit, every refusal, `E_ABORT`.
  `test_rewind_device` runs a 32-bit app through the built pair with the mode on (park, snapshot, plan, drop, a second
  thread kept from sending) and off (nothing published), and checks that a new device survives an `AddRef` and a
  `Release` (patch 0016). `tools/public_text_check.py` passes.
- In game (backend, scene) and what remains unverified: this commit, built into an Apex Radiance 4.0.1 install copy
  (bridge with its DXVK x64 backend, `rewindMode = True`), in a loaded Bridgeport world, driven by the
  sims-3-modernization harness. Two clean runs of the same 5 jobs, each 5/5 PASS: two plan-only jobs, a strict job
  that is refused as expected (exit 2: the host runs without the cooperation block, so it sees the bridge channels
  change), a bridge plan job (2 plans, ~25,500 objects kept, ~470 MB saved) and a bridge apply job with 2 rewinds.
  All 4 rewinds returned code 0 with the game process verified equal to its snapshot; the memory restore took about
  0.5 s (498.7 and 509.2 ms in the second run). After each rewind the game answered and played on, with no crash,
  also at exit. Faults fixed on the way to this commit: the copy of a depth surface made with Discard failed; after a
  rewind the server read no command for 60 s, because the host resets kernel events with the memory (the server now
  holds on a word in the channel view, and the handshake events are named `Rewind*_<guid>`); one start of two crashed
  at device creation (patch 0016). Not verified: other backends than DXVK under the bridge, multisampled scenes (refused
  by design), long play between snapshot and rewind. A default pool texture written after the snapshot (UpdateTexture, UpdateSurface) is copied with StretchRect into a render target, read with GetRenderTargetData and put back with UpdateSurface on a rewind (cb2e62d). The rewind request itself
  has no offline test, since it needs a host that puts the game process back. In their first runs on the test machine
  the 32-bit bridge test apps, upstream's `test_sent_state_device` included and also with the pair built before this
  change, ended with an access violation at the first `Present`; every later run passed and the cause is not found.

<details>
<summary>Trying it without code, in plain steps</summary>

Nothing here changes a save. Keep a copy of `d3d9.dll`, `bridge.conf` and the `.trex` folder from `Game\Bin` first.

1. **Off, nothing changes.** Put the built `d3d9.dll` into `Game\Bin` and `NvRemixBridge.exe` into `Game\Bin\.trex`.
   Play a few minutes and use the Apex Radiance menu. Expected: everything looks and behaves as before.
2. **On, still nothing by itself.** Add the line `rewindMode = True` at the end of `Game\Bin\bridge.conf` and of
   `Game\Bin\.trex\bridge.conf`. Play a few minutes. Expected: no difference. `rtx-remix\logs\bridge32.log` has one line
   `[Rewind] Rewind mode is on`; `bridge64.log` has no `[Rewind]` line.
3. **One rewind, with the host tool.** In a loaded world the tool takes the snapshot, lets the game play 20 seconds and
   rewinds. Expected: the game is back at the moment of the snapshot (same time of day, Sims in the same places), the
   picture is right and the game plays on. If the tool prints "refuse", nothing was changed; the reason is in
   `bridge64.log`. Multisampled render targets are refused by design, and the log then says so.
4. **Without the game.** `native\remix-bridge\build.cmd`, then `native\remix-bridge\test-rewind.cmd`. Expected last line:
   `rewind tests passed`.

Removing the two `rewindMode = True` lines turns the mode off again.

</details>

## Checklist

- [x] Title follows `area: imperative summary` (see docs/PUBLIC-TEXT-POLICY.md)
- [x] New menu texts translated; `perl tools/menu_audit.pl .` reviewed (no menu text in this change)
- [x] Feature, validation and history pages updated when behaviour changes

🤖 Assisted with AI together with @rodneypantonial
