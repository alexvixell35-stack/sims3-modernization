# 869: Blank notification sender, `blanktns`

2026-11-22 (UTC), static (EA code read in a local decompile and the managed call graph, not committed). Diagnostic built,
default off, offline selftest passes. In game the `Show` swap (pass 13) and the `Add` swap (pass 14) were refused (§2); the
`ShouldAddNotification` hook is NOT RUN in game yet.

## 1. Question

A beta tester gets an empty red notification. It also happened before ts3fixes was installed, so another mod or EA sends
it. Which code sends it?

[verified, decompiled] `Sims3.SimIFace.StringTable.GetLocalizedString(string key, out string)` returns
`GetUnlocalizedStringFormat(key)` for a key with no text in the loaded string tables, and that method returns `string.Empty`
in retail. A notice whose text key is missing (a mod's STBL not loaded, a wrong key, a pack's table missing) is shown with an
empty box. The text alone cannot name the sender, so the notice must name it.

## 2. Where every notice passes

[verified, decompiled + `tools/managed-callgraph.ts callers`]

- `StyledNotification.Show(Format)`, `Show(Format, string)` and `Show(Format, string, string)` only call the 5-argument
  static `Show(Format, string, string, ProductVersion, ProductVersion)`.
- That overload holds the only `newobj` of `StyledNotification` in EA's script DLLs (the 4-argument constructor is called
  only by the 6-argument one). `NotificationSystem.Show` and the `Sim.ShowTNS*` helpers reach it through the overloads above.
- It returns null while the tutorial runs or with no `NotificationManager`. With `mTimeout > 0` it reads `mText.Length`
  first, so a null text throws a NullReferenceException in EA's code.
- `StyledNotification.Format` is a struct, passed by value.
- With a manager, it builds the notice and passes it straight to `NotificationManager.Add(Notification, TNSCategory)`, so the
  sender is still on the stack there. Neither the constructor nor `Add` / `ShouldAddNotification` reads the text.
- The instance `Show()` runs later from the notification queue (`mTextArea.Caption = mFormat.mText`). It does not have the
  sender on its stack.

The first build swapped the 5-argument static `Show`. In game (pass 13, 2026-11-22, repro `blank-tns`) that swap was refused:
`StyledNotification.Show: already compiled`, live path `BtnHook swaps 0/1`. Some code that runs at startup calls `Show` before
ts3fixes loads, while there is no `NotificationManager`. The second build swapped `NotificationManager.Add`. In game (pass 14)
that swap was refused too: `NotificationManager.Add: already compiled`. [inferred] Compiling `Show` marks the methods it calls
(`Add`, the constructor) as compiled, although `Add` never ran. The hook therefore swaps
`NotificationManager.ShouldAddNotification(Notification, TNSCategory)`, the first call inside `Add`, which is not reached by
compiling `Show` alone. The sender is still on the stack there, and it also sees a mod that calls the public constructor and
`Add` itself. If this swap is refused too, a notification was really added before ts3fixes loaded, and the swap approach cannot
name the sender.

## 3. The diagnostic

`mods/ts3fixes/src/BlankTns.cs` (`BtnHook`), pure part `BlankTnsCore.cs` (`BtnCore`). Tuning `kBlankTns` (default False),
runtime switch `blanktns`. Default off. It goes on in beta builds only. The hook is always installed.

- Switch on, and `Format.mText` null, empty or whitespace: the hook sets the text to
  `[blank notice] sent by <Type.Method> < <Type.Method> < <Type.Method>`. These are the first 3 managed frames outside
  the notification plumbing, innermost first, with full type names, so a mod's namespace shows. The stack comes from
  `new System.Diagnostics.StackTrace(1, false)`, as in `PlayTrace.cs` and `OutfitWait.cs` (verified in game by those
  fixes). `Format` is a struct: the hook reads a boxed copy of `mFormat`, sets its text and stores it back before EA queues
  the notice. A null text with a timeout still throws in EA's static `Show` before `Add` (unchanged; that notice never shows).
- Plumbing (`BtnCore.IsPlumbing`): `Sims3.UI.StyledNotification` and its nested types, `Sims3.UI.NotificationManager`,
  `Sims3.Gameplay.Utilities.NotificationSystem`, `Sims3.Gameplay.Actors.Sim` methods named `ShowTNS*`, our
  `Ts3Fixes.Fixes+BtnHook` and the `Fixes.Btn*` helpers. Frames with no type are skipped too. Other `Sim` methods and every
  other type count as senders.
- Log: the first 20 blank notices of a session each write two kinds of line to ts3native.log through the [UserDiag]
  channel (`DiagLine`, docs/188). The `blanktns:` line holds the same text, the notice style, `null text` / `empty text` /
  `whitespace text` and the case number. One or more `blanktns stack:` lines hold up to 8 frames outside the plumbing as
  `Assembly:Type.Method`. The assembly names the mod's DLL.
- A notice with text is untouched. The only cost is the blank test.
- Switch off: EA's path unchanged, plus counter 2363. No stack is read.
- The hook never throws. An error is counted (2365) and EA's `Add` runs with the notice as it was. A failed log write still
  leaves the notice its sender text.
- Household notices are included on purpose. This is a diagnostic that changes only the text of a notice that would
  otherwise be empty. It changes no game state, no Sim and no save data (AGENTS.md household rule: nothing to exempt).
- Not done on purpose: no hook on `StringTable` (a hot path), so the missing string key itself is not reported.

Swap-stack audit: `BtnHook.ShouldHook` has the stub `ShouldOrig`. `ShouldAddNotification` reads a device option and the
notification lists and has no path to a yield [verified, decompiled], so no saved frame can sit in it.

## 4. Counters (contributor range, reserved by hand)

`node tools/registry.ts reserve` finds no free 4-slot block past 4089. Slots 2362-2365 are free in the source and the
registry here and in the open contributor branches (after firenre 2354-2357 and firecount 2358-2361; checked 2026-11-22).

| Slot | Meaning |
|---|---|
| 2362 | blank notices renamed with their sender (switch on) |
| 2363 | blank notices seen with the switch off |
| 2364 | blank notices not logged (session cap of 20 reached, or the log channel refused the line, e.g. no ts3native) |
| 2365 | errors (EA's path runs with the text unchanged) |

## 5. Check and repro

- `Ts3Fixes.Fixes.BlankTnsCheck("run")` shows one blank `kSystemMessage` notice and one with text, then reads back the text
  EA stored in each (`StyledNotification.mFormat`). Off: `verdict: BROKEN` (the blank one stays blank). On:
  `verdict: FIXED` (it reads `[blank notice] sent by Ts3Fixes.Fixes.BlankTnsCheck < ...`). Control: the notice with text
  keeps it. Two notices appear in game on each run.
- `BlankTnsCheck("result")`: counters 2362-2365, the last 3 senders and the live path (CheckLiveMap).
- Repro `blank-tns` (tools/harness/repros.json): off run / result, on run / result. Any world.
- Offline: `node tools/selftest/blanktns/run.ts` (blank test, frame filter, text).

## 6. Not done / next

- NOT RUN in game with the `ShouldAddNotification` hook: whether it is still uncompiled when ts3fixes loads, whether the
  frame of the swapped hook reports `NotificationManager` or `BtnHook` as its type (both are filtered), and how deep stacks
  look in a real mod's notice.
- Run the repro, then turn `kBlankTns` on in the beta kit and ask the tester for a screenshot plus `ts3native.log`.

## Import 2026-11-23 (maintainer)

PR #647 by Dalexlol, counters 2362-2365 kept. `blanktns` stays **OFF** at import: it is a diagnostic whose only visible effect is extra text (`[blank notice] sent by ...`) in a blank notification, so turning it on for players is Rodney's decision (on in betas, per the tuning comment). The hook works: repro `blank-tns` (pass 16, Isla Paradiso) is off BROKEN (notice stays blank) / on FIXED (names its sender), the control notice is kept, errors 0. Only the repro's own frame was observed; the stack depth of a real mod's call is unseen. Household: notifications only, no Sim effect. Repro `blank-tns` added to repros.json.

## Experimental build 2026-11-23

Rodney 2026-11-23: `blanktns` goes **ON in the experimental/tester build only** (`docs/data/verified-switches.json` entry
`blanktns`, `kBlankTns` True via tools/release/experimental.ts). The release keeps `kBlankTns` False. Still NOT RUN in game
(section 6): the first tester report with a blank notice is the in-game check.
