# 534. CAS frame budget: a time budget for SimService_Update in live play

Contributor doc (docs/registry.md range 500-599). Builds docs/42 CAS2 ("turn on SimService time slicing when script leaves it off, or cap the default deadline"), never built before. Native only, off by default: `[CasFrameBudget] Enabled=0`. Static work; the game was not run for this doc.

Tags: **[verified]** = read in the disassembly of the Steam TS3W.exe 1.67 or the script call graph; **[measured]** = from earlier runs (docs named); **[inferred]** = still to confirm in a run.

## 1. What

`SimService_Update 0x5F0E50` (vtable `0xFE514C+0x1C`, main thread, thiscall, `ret 8`) runs the queued Sim build steps (outfit, textures, skin, blend geometry, materials, LODs; docs/42 §4) until a step is not ready, the list ends, or a QPC deadline passes [verified]. The deadline comes from two fields of the SimService:

- `0x5F0EBF cmp byte [edi+0x139],0 / je 0x5F0ECD / mov esi,[edi+0x2C] / jmp 0x5F0ED0 / 0x5F0ECD or esi,-1`, then `Stopwatch_Init(4)` and `Stopwatch_SetDeadline 0x4F34F0(esi, 0)` [verified bytes].
- `Stopwatch_SetDeadline` loads the budget as an **unsigned integer** (`fild`, plus 2^32 when negative), divides it by the mode scale and adds QPC [verified]. So `+0x2C` is an integer in Stopwatch mode-4 units, and `0xFFFFFFFF` (byte `+0x139` = 0) is about 4.3e9 units: no deadline (docs/42 §1).
- Only `IC_CASUtils_SimServiceEnableTimeSlicing 0x5A50F0` writes `+0x139` (docs/42 §3), and no game script calls it: the name appears only in ScriptCore.dll and SimIFace.dll, the wrappers [verified: script call graph]. So in every mode, one frame may run every queued build step back to back.

The fix is an entry detour on `0x5F0E50` (prologue `55 8B EC 83 E4 F0`: push ebp / mov ebp,esp / and esp,-16, no relative operand, checked before the patch). When the switch is on, live play runs and `+0x139` is still 0, the hook sets `+0x139 = 1` and `+0x2C = BudgetMs`, calls EA's function, and then puts both fields back. A field that changed during the call (script) is kept as found and counted. The patch is not at `0x5F0ECD`: `or esi,-1` is 3 bytes, and both `0x5F0ECD` and `0x5F0ED0` are jump targets.

**Live play only.** The hook reads pause bit 1 of the game clock `0x11CDD48` (`+0x40`; `GameClock_IsPaused 0x594540`, docs/85 §3). `LiveModeState.Startup` calls `GameUtils.Unpause` and `LiveModeState.Shutdown` calls `GameUtils.Pause`; `EditTownState.Startup` and `PlayFlowModel.Initialize` call `Pause` [verified: script call graph]. So in CAS (Create a Sim, Plan Outfit), Build/Buy, Edit Town and the world load, the bit is set and EA's unlimited step runs unchanged. Bit 4 (device lost or window not in front, docs/41) is ignored. A player pause probably sets bit 1 too (`Gameflow.SimulateTick` calls `Pause`) [inferred], so a paused live game also keeps EA's step.

**Units.** Mode 4 reads its scale from `0x11CB8FC` (`Stopwatch_SetClockMode 0x4F30E0`, a runtime value) [verified]. The main loop draws loading frames every 100 mode-4 units at about 10 fps (docs/531 §3), so the unit is the millisecond [inferred]. The first budgeted call logs the budget in ms from EA's scale, which confirms the unit in the first run.

## 2. Why: evidence and expected gain

- docs/169: in the BP camera pan, CAS/SimService leads **29% of the frames >= 100 ms**, the largest single service [measured].
- docs/71: `SimService_Update` is on 6.5% (BP) and 2.8% (IP) of main-thread stacks during the bulk outfit change, mostly through `0x5EC6D0` (state 4, skin and blend geometry) [measured]. docs/71 rated the cap "low priority" because those runs had no frame times; docs/169 has them now.
- Expected gain: fewer CAS-led hitch frames during mass builds (pan into a crowded lot, household load in live play, the `bp-main-cas` repro). A frame that now runs 3-10 build steps back to back runs until the budget passes. A single long step is not split (the model build `0x5E2050` alone can take about 160 ms, docs/71 gap 9), so only the multi-step frames gain. Medium confidence.

## 3. Switch

```ini
[CasFrameBudget]
Enabled=0     ; 1 = install the entry detour on SimService_Update 0x5F0E50
BudgetMs=8    ; start value of cfbMs (0..1000); 0 = EA's unlimited step
```

`cfbMs` (`+0x5CC4`) is read on every call, so a test tool can flip it in one load (0 = off) for an A/B.

## 4. Counters (v912, contributor block, docs/registry.md)

| Offset | Name | Meaning |
|---|---|---|
| `+0x5C34` | `v908v911Reserved[36]` | gap: v908-v911 are taken by other branches |
| `+0x5CC4` | `cfbMs` | switch: budget in mode-4 units (ms), 0 = EA's step |
| `+0x5CC8` | `cfbCalls` | `SimService_Update` calls that ran with the budget set |
| `+0x5CCC` | `cfbHits` | of those, calls that took the budget or longer (budget-hit frames) |
| `+0x5CD0` | `cfbForeign` | restores that found `+0x139` or `+0x2C` changed during the call (kept as found; must stay 0) |

`tools/probe/agent.js` reads them when the counters version is 912 or higher.

## 5. Risk

- Sims show their old outfit or placeholder for more frames; script that waits for "build done" (travel, outfit change interactions, `IC_GameUtils_WaitForSimService`) waits more frames. No save or device impact: requests stay in EA's queue, and the state machine already resumes per frame.
- A long single step still makes a hitch (not split).
- Another mod that detours `0x5F0E50` makes the prologue check fail: the fix logs a SKIP and stays off. This repo has no other `0x5F0E50` site in native/ts3native; S3SS and Apex Radiance were not checked for this doc (run `hook-sites.ts --check` before the first run).
- The pause bit is read without the clock lock (`0xFD7C84`): a frame at a mode switch may use the old mode. Harmless both ways.

## 6. A/B plan

- Save: BP (docs/169 `bp-main-cas` repro: spawned Sims and forced outfit changes on the lot in view, camera pan). One load, `cfbMs` flipped between the halves (8 vs 0), at least two halves each way.
- Read: `frame-lead.ts {"service":"CAS/SimService","ms":50,"min":20}` and the CAS/SimService share of the frames >= 100 ms on vs off (docs/169 baseline 29%); frames.csv p99; `cfbCalls`, `cfbHits` (`cfbForeign` must stay 0). Cost side: time until the repro's `spawned` / `outfitChanges` are all built, and the docs/71 sampler share of `SimService_Update` (6.5% BP, 2.8% IP) as a check that the work only spread out.
- Keep it only when the >= 100 ms CAS-led frames drop clearly and the build time grows by less than a few seconds.
