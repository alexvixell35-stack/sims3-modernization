# 533. ObjectDesigner thumbnail drain cap: `[ThumbDrainCap]`

Written 2026-11-06 (contributor). Static + disassembly + the native selftest; not run in game. Off by default:
`[ThumbDrainCap] Enabled=0`.

## What

`ObjectDesignerService_Update 0xB3A960` (docs/531 §6.2) runs every frame under lock `0x106B874` and calls
`ProcessThumbnailRequests 0xB373A0`. That function empties the whole thumbnail request map (`+0x214`, head `+0x218`,
count `+0x220`) in one call. It has no count cap and no deadline. Each pass erases the map's head first (`0xB33670`), then
builds that request's TextureBuilder texture.

`native/ts3native/odthumb.cpp` (next version, counters v913 `td*`) stops the loop after `MaxPerFrame` requests or after
`BudgetUs` microseconds. The requests that are left stay in EA's map, in order, and the next frame's update takes them.
No request is dropped. At least one request runs per frame, so the queue always empties.

## Why

Opening a Buy or Build catalog page queues one request per swatch, so one frame pays for the whole page. docs/531's fix
list (candidate C3) puts that at one 5-30 ms frame per page [inferred, not measured]. The cap spreads that work over
several frames. Thumbnails already arrive late today (the compositor step is budgeted), so the UI copes with it.

## Who waits on the queue

Checked in the disassembly of the Steam TS3W.exe 1.67:

- `0xB373A0` has one caller, the per-frame update at `0xB3A9BF`. There is no flush, save or shutdown path that calls it,
  and no vtable or other reference to it.
- Every request insert (`0xB38050`, from the script icall at `0x787A08`) sets a "work pending" flag at `+0x5C`. The
  update clears the flag at its end (`0xB3ABC8`). `0xB32950` reads it. It has two users: the script icall `0x787740`
  and a wait loop `0xC9FB50` that yields until the flag clears. `BlueprintHelper` (realize floors, partitions,
  preset pattern data) calls that wait loop from 19 sites. A cap alone would clear the flag while requests are left,
  and those waits would end early. So the fix also patches the clear: the flag stays 1 while the map is not empty. The
  waits then last a few more frames and still see every request handled. Without the cap the map is always empty at
  that point (the lock is held), so the flag clears as before.
- The reset at `0xB3A340` releases every entry still in the map and clears it. It already handles a non-empty map
  (requests that arrive after the last update), so leftover requests at shutdown or world unload are freed as before.

## Sites

All three are patched, or none. Each is checked against its original bytes first.

| Site | Original | Patch |
|---|---|---|
| `0xB3A9BF` | `call 0xB373A0` | `call TdBeginStub`: counts the drain, starts the budget, jumps to `0xB373A0` (a detour on its entry still runs) |
| `0xB37730` | back-edge `cmp dword [edi+220h],0 / jne 0xB373D2` (13 bytes) | `jmp TdBackStub`: empty map goes to EA's exit `0xB3773D`, else the cap decides between the next request `0xB373D2` and the exit |
| `0xB3ABC8` | `xor ecx,ecx / lea edx,[esi+5Ch] / xchg [edx],ecx` | `jmp TdClearStub`: the flag becomes "requests left" instead of 0 |

The back-edge is also the target of the two release paths at `0xB374A8` and `0xB374D1`. `esp` and `edi` there are the
loop's own, so both targets work unchanged. The exit is the shared epilogue (register pops only).

## Not capped

- Presets `0xB34A50`: the BlueprintHelper waits above read preset pattern data, so it is left whole until a counter
  shows long lists.
- Hair swatches `0xB34750`, and large patterns `0xB37DA0` (already one request per frame).

## Switch

```ini
[ThumbDrainCap]
Enabled=0
MaxPerFrame=8   ; 0 = no count cap
BudgetUs=2000   ; 0 = no time budget
```

Log: `OK   thumb drain cap ...`, or `SKIP thumb drain cap: bytes at ... differ` (counter `tdOn = -1`).

## Counters (v913, `ts3native_counters` +0x5CD4, docs/registry.md)

| Field | Meaning |
|---|---|
| `tdOn` | 1 = the three sites are patched, -1 = their bytes differ (not installed) |
| `tdDrains` | `0xB373A0` calls with a non-empty queue |
| `tdReqs` | requests those calls handled |
| `tdCut` | calls stopped by the cap or the budget with requests left |
| `tdMaxQueue` | longest queue seen at a call's start |

`+0x5C34` to `+0x5CD3` are zeros in this branch: v908-v912 belong to other branches.

## Risk

- Thumbnails of a long page appear a few frames later. This is the intent.
- Script code that waits for the pending flag waits more frames (about queue / 8). It still terminates, because each
  frame handles at least one request.
- Another mod that patches the same three sites: the fix logs SKIP and stays off.
- The cap and the counters run on the update's thread only (one caller, lock held).

## A/B plan

1. Counter build first: `Enabled=1` with `MaxPerFrame=0 BudgetUs=0`. The fix is installed but never cuts. Page through
   Buy mode catalog pages on BP and read `tdMaxQueue` and `tdReqs / tdDrains`. This shows whether pages are long
   enough to matter.
2. A/B in one BP load (`--ab thumbcap`): Buy mode catalog paging on, then off (`MaxPerFrame=8 BudgetUs=2000`). Count
   frames >= 30 ms in frames.csv during the paging, plus `frame-lead.ts {"service":"ObjectDesigner","ms":30}`.
3. Check that every swatch on every page gets its thumbnail, and that a blueprint placement (BlueprintHelper wait) still
   finishes. Expect `tdCut > 0` on the on side.
