# 312. CAS TargetException detector (`swapinvokediag`)

2026-11-21, built, NOT RUN. Player report (EA app 1.69.47, kit 1120.e6: ts3native 0.36.297-eaooa1, ts3fixes 0.1.1 build
3559b8c4; NRaas suite incl. MasterController and ErrorTrap, BetterCAS, MonoPatcher, ArroUITweaks, S3PP, TS3 Turbo Engine,
Apex Radiance 4.0.11 + bridge).

## 1. What the player saw

10 ScriptErrors in 12 minutes while editing in CAS / Create-a-Style, each `System.Reflection.TargetException: Unable to
invoke an invalid target.` with a one-frame stack `#0: 0x00001 callvirt in System.System.Type:get_HasElementType`
[verified, ScriptError xmls]. Four have NRaas ErrorTrap's task header `Proxy: Sims3.Gameplay.CAS.CASLogic`; six have the
header `OnButtonMouseDown` [verified]. The log around them shows `ObjectDesigner_SetCASPart` and `CASUtils_PartDataGetPreset`
hitches (the designer) [verified, ts3native.log ModTime lines].

## 2. Static narrowing

| # | Claim | Status |
|---|---|---|
| 1 | The message is TargetException's default text (mscorlib `TargetException()`), not one written by managed code; Mono raises it natively when `MonoMethod.InternalInvoke` gets a target that is not an instance of the method's class | [verified] mscorlib decompiled (default ctor text); [inferred] Mono 1.2 icall, ledger §567 |
| 2 | `MonoMethod.Invoke` (IL) runs `Binder.ConvertArgs` (which calls `Type.HasElementType`), then `InternalInvoke`; it rethrows TargetException unwrapped and wraps other exceptions in TargetInvocationException. `PropertyInfo.GetValue/SetValue` and `Delegate.DynamicInvoke` end in it | [verified] mscorlib decompiled |
| 3 | The one frame `Type.get_HasElementType` is the last interpreted frame before the native throw inside `MonoMethod.Invoke`, so the report is a reflection invoke, the caller lost | [inferred], ledger §567 |
| 4 | Our swaps copy the hook's class word into the target method (SwapDelegates.cs), and reflection on a swapped instance method throws exactly this exception | [verified] in game 2026-10-14 (RefTable.cs probe), 2026-11-01 (docs/508) |
| 5 | Header `OnButtonMouseDown` = NRaas `Common.Exception("OnButtonMouseDown", e)` in MasterController's `CASClothingCategoryEx.OnButtonMouseDown`, the MouseDown handler MC adds to CASClothingCategory's type buttons; it calls `CASClothingCategory.SetTypeCategory` (EA). No reflection in that handler itself | [verified] NRaas source, read only |
| 6 | EA's UI dispatch (`UIManager.ProcessEvent` -> `UIEventHandlerGlue.Invoke`) and `ItemGrid` populate callbacks call delegates directly, no reflection | [verified] UI decompiled |
| 7 | Our CAS code's own reflection (CasGrid, CasRowLeak, CasExitSweep, CasPuckLeak, CompositorLeak, CustomCtlSweep) runs inside try/catch: an exception there never reaches a ScriptError | [verified] source |
| 8 | MonoPatcher's managed side has no `Invoke` / `DynamicInvoke` call | [verified] source grep |
| 9 | So the caller is reflection in some code on the SetTypeCategory -> designer path or a CASLogic task that is not ours: EA code has none there, so a mod (BetterCAS, MasterController's CAS parts, ArroUITweaks) or a swap that is not ours (MonoPatcher replacements) | [inferred]; not found statically |

Swapped CAS methods (ours): `CASPuck.Load/Unload/Dispose` (CasExitSweep.cs, CasPuckLeak.cs), `CASHair.PopulateTypesGrid`,
`CASMakeup.PopulatePartsGrid` (CasGrid.cs), `CASClothingRow.CreateGridItems`, `CASClothingCategory.AddGridItem`
(CasRowLeak.cs), `CASCompositorController.Load/Unload` (CompositorLeak.cs). Suspect by path: `CASClothingCategory.AddGridItem`
(SetTypeCategory refills the grid with it; MasterController passes `ths.AddGridItem` to `BeginPopulating`) and
`CASCompositorController.Load/Unload` (the designer) [inferred]. BetterCAS's code was not read (no copy here). No fix is
built: the cause is not proven.

## 3. The detector

`mods/ts3fixes/src/SwapInvokeDiag.cs`, switch `swapinvokediag`, tuning `kSwapInvokeDiag` (default False; True in the diag
package `ts3fixes-on-kSwapInvokeDiag`). With the tuning on at load, `System.Reflection.MonoMethod.Invoke(object,
BindingFlags, Binder, object[], CultureInfo)` is swapped: the hook runs EA's body in a try, logs, and rethrows (`throw;`), so
callers and ScriptErrors are unchanged. A load-time tuning, because the hook sits on every reflection call; off means no
swap at all.

Lines (ts3native.log through `[UserDiag]`, `ts3fixes:` prefix):

```
ts3fixes: swapinvokediag: TargetException: Unable to invoke an invalid target. from MethodInfo.Invoke on Sims3.UI.CAS.CASClothingCategory.AddGridItem (ours, hook CrlCategoryHook), target Sims3.UI.CAS.CASClothingCategory, caller assembly BetterCAS, #1 for this method, 23 frames
ts3fixes: swapinvokediag stack: BetterCAS:Some.Type.Method, UI:Sims3.UI.CAS.CASClothingCategory.SetTypeCategory, ...
```

(example values). Logged: a TargetException or any other exception out of a method we swapped, and a TargetException out
of one we did not swap (`not swapped by us, class X`: another mod's swap or a wrong target). Name = the method's name before
the swap. Stack = every managed frame above the hook, innermost first, up to 60; caller assembly = first frame outside
mscorlib. Limits: 3 lines per method, 40 per session. Fallback when the swap is refused (MonoMethod.Invoke already compiled
at our load: `MonoMethod.Invoke: already compiled` in the applied log): the ScriptProxy.OnScriptError hook (ErrorTrapLoop.cs)
logs `swapinvokediag: TargetException reached task <type> (invoke hook not in)` and the stack at the report.

Counters [1496] TargetException from a method we swapped, [1497] other exception from one, [1498] TargetException from a
method we did not swap, [1499] lines not written. Logging only, no game state written, no Sim touched (no household
exemption needed).

Harness check `Ts3Fixes.Fixes.SwapInvokeDiagCheck` (reflect_call): invokes every swapped CAS instance method by reflection on
an uninitialized object of its own class; expected `OK` = all TargetException, hook in, switch on, [1496] grew by the count.
A `ran` result means the body ran and the class-word theory is wrong for that method.

Agent `cas_click {button, key}` (CasHandlers.cs): MouseDown + Click on a CASClothingCategory button through
`UIManager.ProcessEvent`, so every handler on the button runs (MasterController's too); the second left click on the
selected type opens the designer.

## 4. Queue and kit

- Queue `tools/harness/queues/cas-targetexc-1121.json` (correctness, PREPARED, NOT RUN): BP 10 min, repro
  `cas-targetexc-check`, diag package, agent cas-targetexc-1121; pass = check OK, detector lines present, 0 script errors.
- EA diag kit 1121.e7 (`probe-out/outbox/eli-e7/TS3Kit-ea169-diag-1121.e7.zip`, sha256 4fbf0cb249ae66e1, not sent; ts3fixes builds.json `ts3fixes.package.cas-targetexc-1121` 6add2728 / `-diag` 81c9eabd, agent `ts3agent.package.cas-targetexc-1121` b3575526): e6 asi and inis, ts3fixes from this branch (master incl. PR
  #616) normal (switch off) and diag (tuning on). The install also moves TS3 Turbo Engine (`Turbo.asi` + `Turbo.txt` /
  `Turbo.ini`) into the backup folder (it takes the CreateThread import first, so our thread stack fix SKIPs; e6 log
  `SKIP thread stack CreateThread: IAT 0x00FDF244 holds 0x00D982A0`), with a `kit.log` line; uninstall restores it with a
  sha256 check.

## 5. Next

Read the EA tester's e7 diag log: the `swapinvokediag` lines name the method, the hook and the calling assembly. Then: if ours, a fix
behind its own switch (keep the original method invokable, e.g. a non-swapped twin for reflection), or a stand-down for that
mod; if another mod's swap, the upstream route (AGENTS.md "Bugs in other mods").
