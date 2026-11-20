# 308. `[Diag] TaskSeam`: exact per-task-run attribution of script mods (2026-11-20)

Status: built + host tests (native selftest), queues dry-run only, no game run. Branch `feature/mod-seam-budget-1120`,
0.36.295-tsk1. docs/302 §3 #3 (prerequisite of #4 `[ModBudget]`, which is handed to another contributor and not in this
branch). Tags: [verified] = disassembly or our source, cited; [inferred] = to confirm (docs/51 §556); [measured] = a run.

## 1. What it does

`[Diag] TaskSeam=1` (ts3native.ini, default 0; read at game start) times every script task run and every per-object script
call on the sim thread and names the mod behind it. Read-only: nothing runs differently, earlier or later, so the household
rule does not apply (no Sim, save or setting is changed).

- **Seam:** the four call sites of `ScriptHost_RunTask` `0xD7F000` (ThreadedObjects resume `0xD7FA74` and first start
  `0xD7FB53`, TaskObjects `0xD7FE01`, `0xD800FC`) and the two per-object `mono_runtime_invoke` `0xEA1090` calls of
  OneShotObjects (`0xD748C2`) and ScriptTick (`0xD80D14`). Call-site retargets (rel32), byte-checked, all or none, the same
  way `[RouteAsync] TaskServe` patches the RunTask sites (docs/637). With TaskServe on, the four sites already call its hook;
  TaskSeam then chains to that hook instead of `0xD7F000` (installed after it). Any other target = another hook owns the
  seam: stand down with a log line, nothing written.
- **Owner per run**, resolved once per task node into a side table (8192 entries, re-resolved when the node's state or task
  object changes): task state `[0]` MonoMethod, `[1]` task object [verified docs/166 §1]; `ScriptProxy.mTarget` -> the target's
  class's assembly; `OneShotFunctionTask` / NRaas `FunctionTask` `mFunction` -> delegate `method_info` -> MonoReflectionMethod
  `+4` MonoMethod -> assembly (docs/166 §2, verified live by the Frida profiler); else the task method's own assembly (a mod's
  Task subclass). Field offsets through `mono_class_get_field_from_name` `0xE6F720` (MonoClassField: type `+0`, offset
  `+8`, static bit 0x10 of the type's attrs [verified, native/worldimage/wimage.h]), cached per class, sim thread only.
  Owner rows are ModTime's (`MdtOwnerOf`, new `MdtOwnerOfImage`), so kinds (game / ours / mod, core mods as game) and
  packages match `modtime-summary.txt`.
- **Scope (reported, docs/302 §4.2):** a ScriptProxy target of class `Sims3.Gameplay.Actors.Sim` whose task key (the
  proxy's ObjectGuid) is not in a fresh household table (ts3fixes `PushHeld`, unseen.cpp `g_utHeld`) = npc-candidate;
  everything else (Lots, global managers, delegates, no table) = household. The active lot is not known natively, so the
  npc-candidate share is an **upper bound** of what a deferral could ever touch.
- **Cost:** two `QueryPerformanceCounter` reads, one thread-id read and one hash probe per run; every 64th run two more QPC
  reads measure the seam's own time (`tskSelfNs`, and "Seam self cost" in the summary). Expected ~100-300 ns per run
  [inferred, Frida dummy ~300 ns incl. its trampoline, docs/166 §3]; the smoke measures it.

**Output:** `taskseam-summary.txt` next to ts3native.log every 60 s (writer thread, lowest priority): task / invoke ms/s,
mod-owned ms/s and the npc-candidate share against the docs/302 §4.7 gate (>= 20% of mod task time), self cost, then the top
40 owners: ms/s, runs/s, run length p50 / p99 (log2 bucket edges) / max, runs per iteration (mean and max), npc%, invoke
ms/s, package (filled when `[Diag] ModTime=1` ran its package scan). Counters v222 (`tsk*`, docs/registry.md), log line
`OK   task seam ([Diag] TaskSeam=1, docs/308): ...` and `task seam: summary written (...)`.

## 2. Addresses per edition

| Steam 1.67 | Retail 1.67 | EA 1.69 | What |
|---|---|---|---|
| `0xD7F000` | `0xD7F490` | - | RunTask: `sub esp,1Ch; push ebx/ebp/esi/edi; mov ebp,ecx`, `[esp+0x30]` = &node, args `(entry*, bool, bool)`, thiscall, ret 0Ch (callers push 3 dwords, no cleanup) [verified: Steam disassembly, retail body identical to Steam modulo absolute addresses (150 instructions diffed)] |
| `0xD7FA74` / `0xD7FB53` / `0xD7FE01` / `0xD800FC` | `0xD7FF04` / `0xD7FFE3` / `0xD80291` / `0xD8058C` | - | `E8` to RunTask [verified bytes both]; return sites only move to the next node, nothing reads a result |
| `0xEA1090` | `0xEA1370` | - | `jmp [0x1195B60]` / `jmp [0x1196B60]` (mono_runtime_invoke thunk) [verified] |
| `0xD748C2` / `0xD80D14` | `0xD74BA2` / `0xD811A4` | - | `E8` to the invoke thunk, cdecl `(method, obj, params, &exc)`, caller cleans 0x10 [verified] |
| `0xE6F720` | `0xE6FA10` | - | `mono_class_get_field_from_name`, cdecl; prologue bytes equal [verified] |

EA 1.69: skipped with a log line (`SKIP task seam ([Diag] TaskSeam): no verified addresses for this exe (EA app 1.69.47;
docs/308 §2)`). The EA exe is encrypted on disk (docs/68 §7) and no decrypted image is in `game-copy/`; its table already
had `0xD7F000` and the RunTask sites unresolved (TaskServe stands down there too). The rows are 0 in `ts3addr-ea169.h`.
Fill them from a `-DumpOut` image (docs/68 §7) before an EA arm.

Stack and alignment: the hooks are plain `__fastcall` (RunTask: ecx = host, edx unused, 3 stack args, callee pops 0Ch)
and `__cdecl` (invoke) C functions; the stage functions realign their own frames (`and esp, -8`), our calls add normal
frames only.

## 3. Limits

- A run's time includes everything it calls (EA code a mod calls counts for the mod, like ModTime's "with game" column).
- Inline alarm callbacks and EventTracker listeners are not task runs (docs/302 §2.2 S2, S3): they count for the task that
  runs them (a lot's task, EA). ModTime's sampler still sees them.
- Scope is an upper bound (active lot unknown; delegate targets never resolve to a Sim). This is the number the docs/302
  §4.7 gate needs: if even the upper bound is under 20%, `[ModBudget]` stays parked.
- [inferred] Under the household rule almost nothing a mod owns resolves to a Sim target: a ScriptProxy whose target is a
  Sim is EA-owned, and a mod's delegate targets are closures (unknown scope). The attribution job will show it.

## 4. Queues (dry-run only, not run)

- `tools/harness/queues/taskseam-smoke-1120.json` (correctness, RUN FIRST): BP 10 min, switch + version read back
  (`@tskOn==1`, `@tskSites==3`, log lines), `tskRuns`/`tskInvRuns` > 0, a summary written, 0 script errors, household
  untouched (read-only: seed compare + no behaviour switch changed), no SKIP/FAIL task seam lines. Read the self cost.
- `tools/harness/queues/taskseam-manymods-1120.json` (**RUN FIRST** for the `[ModBudget]` build decision, docs/302 §6.3 job
  3): the many-mods set via queue key `modSet` (on master) on save ManyMods-IP-aged (run many-mods-age-1120 first);
  TaskSeam + ModTime on, 20 min live; read the npc-candidate share against the 20% gate. Script errors of the third-party set are reported, not gated (TaskSeam is read-only; the 0-error gate is the smoke).

## 5. Host tests

`taskseam-test.h` (native selftest): histogram buckets and quantiles; a fake RunTask / invoke: every run calls through once
with its arguments, counted under its owner row, node reuse re-resolves, unreadable nodes still run, other threads / nested
runs / switch off pass straight through uncounted, the invoke result and exc pass through; call-site target decode.
