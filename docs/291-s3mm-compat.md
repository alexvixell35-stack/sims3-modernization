# S3MM (Sims3MonoModder) compatibility: static analysis

Written 2026-11-18. Static only: no game run, no build. Ledger: docs/51 §528.

Question (Discord, relayed): is ts3native + ts3fixes compatible with S3MM, and does our JIT overlap with it?

Short answer: **not safe together with master's defaults; mostly fine with v0.2.11 defaults on Steam, with one unsafe case.** Nothing patches the same bytes in a way that breaks at install on Steam 1.67, but there are three semantic conflicts (one is our bug) and one build-specific trap on non-Steam exes. A stand-down is needed before v0.3.0 turns `[MonoJit]` and `[MonoFast] CallPath` on.

## 1. Sources

- **S3MM:** https://github.com/sims3fiend/Sims3MonoModder, GPL-3.0, `main` at `1b20dea` (2026-11-15), two commits past tag `1.3.0-beta` (`d363c80`, 2026-08-30; 19 files differ). Source read in a scratch clone outside the repo; nothing copied. File names below are S3MM's.
- **Ours:** `native/ts3native` at master `76bbd650f`, `ts3native.ini` defaults of master and of tag `v0.2.11`.
- **Exe:** Steam 1.67 `game-copy/bin/TS3W.exe` (PE stamp `0x52DEC247`); retail 1.67 `game-copy/retail/TS3W.exe` (`0x52D872DA`). EA 1.69 `TS3.exe` on disk matches none of the 47 S3MM patterns [verified scan], so the file is not plain code on disk; EA statements below are [inferred] from the retail result and docs/273 (same interpreter code, moved).
- S3MM's 47 prologue patterns were re-run over both exes with a local script [verified]: Steam 46 of 47 first hits equal S3MM's hard-coded fallback (the INI reader pattern hits `0x4EF230` first, fallback `0x4EF2B0`, two hits; S3MM's own matter); retail 46 of 47 resolve uniquely at shifted addresses.

## 2. What S3MM installs

S3MM is an ASI (`Sims3MonoModder.asi`). `DllMain` only starts a thread; that thread resolves addresses by scanning the function prologues, with the Steam 1.67 address as fallback when a pattern does not match (`src/core/addresses.cpp`). All code hooks are Microsoft Detours entry detours. Addresses are Steam 1.67.

| When | Site | What | S3MM file |
|---|---|---|---|
| start thread | `0xE84140`, `0xE84780`, `0xE84860`, `0xE68290`, `0xE66990`, `0xE65BE0`, `0xD78760` | image/assembly open and load, assembly name compare, AppDomain raw load, script host LoadImage: path tracking, replaced-assembly detection | `core/assembly_hooks.cpp` |
| start thread | `0xD7F2B0`, `0xEC0EF0`, `0xD7FC60` | script heap init (Mono ready), StartupComplete icall, per-frame task pump (runs S3MM's queued work on the sim thread) | `core/script_host_hook.cpp` |
| start thread | d3d9 `EndScene` and `Reset` (in the d3d9 module), window procedure subclass | overlay | `ui/imgui_manager.cpp` |
| start thread, always | `0xE59FD0` (invoke), `0xE525C0` (native-body dispatch), `0xE52630` (its virtual variant), `0xEA3140` (`mono_value_box`), `0xE82680` (`mono_lookup_internal_call`); writes Mono's profiler globals `0x1220E2C`, `0x11F45F0`, `0x11F4678` | profiler (call counts, timing, boxing, icalls, allocations). Installed at start, not when the panel opens | `core/mono_profiler.cpp` |
| Mono ready | `0xEA18F0` (`mono_raise_exception`), `0xE53740` (interpreter exception handler), `0xE46B10` (`g_error`), first-chance vectored handler | exception log. The handler acts only when EIP is inside `interp_exec` (`0xE53EB0`+). At `0xE54AEA` (callvirt receiver read, EAX < 0x10000) it zeroes the receiver and resumes at `0xE54ADC`, so EA takes its null-receiver path; it checks two instruction bytes there first and turns the recovery off when they differ | `core/exception_hook.cpp` |
| Mono ready | `0xE6FA90`, `0xE6F840`, `0xEA2AD0`, `0xE6F720`, `0xE70330`, `0xE70D50` | lookup cache over class / vtable / field / method lookups (locked) | `cache/mono_cache.cpp` |
| Mono ready | `0xE5BCD0` (transform's data-item append) | finds the data-item slot of a chained call during a transform | `modding/chain_compiler.cpp` |
| Mono ready, mod load, hot reload | no code patch: **RuntimeMethod overwritten in place** | a hook on an already transformed method copies the hook's 0x48-byte RuntimeMethod over the target's (the pointer at MonoMethod+0x14 stays); a never-transformed target gets its MonoMethod token/class replaced. The original lives on as a cloned MonoMethod with its own RuntimeMethod. Unhook copies the saved bytes back | `modding/method_patcher.cpp`, `hook_manager.cpp` |
| mod load, hot reload | calls only | `.cs` mods compiled with `csc.exe`, loaded from memory (`0xE84780`, `0xE68290`), ~44 icalls registered through `0xE82550`, transform through `0xE51F20` + `0xE52B60`, managed calls through `0xE59FD0`. Reload work is queued to the sim thread (task pump detour) | `modding/mod_loader.cpp`, `core/mono_bridge.cpp` |
| overlay, on demand | calls only | heap browser: `GC_apply_to_each_object` `0xE4A3F0` from the overlay; resource browser: RefPack `0x4EC010`, resource manager vtable calls | `core/mono_bridge.cpp`, `core/resource_system.cpp` |

S3MM does not touch the mint jump table `0xE59880`, does not read or write mint opcode words (only `data_items`), and has no GC hook [verified by grep of its source].

## 3. Ours in the same regions

Install time: everything below is written in `DllMain` except MonoThread P3/P4, which install on the first Simulate tick [verified, `monothread-m5.cpp:929`].

| Module, switch (master / v0.2.11) | Site | Note |
|---|---|---|
| Satb, `[AutShadow] Satb=2` (on / on) | entry detour `0xE525C0` (`satb.cpp:1904`); opcode swaps in the jump table | the binder guard for worker-thread runs |
| `[MonoFast] CallPath=1` (on / off) | jump table entries 0x06, 0x07, 0xEA, 0xEC (`mintcall.cpp:44`) | own call / callvirt / ret handlers; native-body callees still go to EA and `0xE525C0` |
| `[MonoJit] Enabled=1`, `Direct=1`, `Inline=2`, `BoundCall=1` (on / off) | jump table entry 0x1A9; transform detour `0xE643B0` (via ilpatch, also with `[IlPatch] Enabled=0`); opcode words of listed methods | code found by mint ip; lazy call cells keyed by RuntimeMethod pointer |
| `[MintFuse] Sub=1` | `0xE5C8CA` | rewrites first opcode words in every transformed method, third-party assemblies included, with EA's own superinstructions |
| `[MonoThread] Enabled=1 Mode=1` (on / on) | P3 entry detours incl. `0xE4BF90`, `0xE4A3F0`; P4 blocks incl. `0xE53EBD` (inside `interp_exec`'s first 50 bytes); `0xD8A8E0` | a second thread runs EA's interpreter for AutShadow jobs |
| `[MonoJit] FaultRecover=1`, crash log | vectored handlers | pass on anything that is not in our code |
| decompress | `0x4EC065` | inside RefPack, past S3MM's pattern window |
| ts3fixes | none | no method swaps; our icalls are registered natively |

## 4. Overlaps

### 4.1 Same bytes: `0xE525C0` (Satb binder vs S3MM profiler)

The only address both detour in the default configuration. `0xE59FD0` is shared only with `[Verify]` (off).

- **We write first** (expected: S3MM does its work on a thread, we install in `DllMain`; exact order [inferred], see open questions). S3MM's pattern for `0xE525C0` no longer matches, it falls back to the Steam address [verified by simulation: with a jump at `0xE525C0` exactly that pattern fails], and Detours attaches on top of our jump. Chain: S3MM, then ours, then EA. Works on Steam [inferred: Detours relocates a leading relative jump].
- **S3MM writes first.** `DetourCore` sees foreign bytes and skips (`ts3native.cpp:198`). **Our bug:** Satb then stays in act mode with the worker running and no binder guard; the count of installed native hooks only feeds the log (`satb.cpp:1985`, `autshadow-run.cpp:962`) [verified]. Unsafe on its own, with any foreign hook at this address.
- **Non-Steam exe (retail, EA) and we write first.** The fallback is the Steam address, which is other code in those builds: S3MM detours a wrong address [verified for retail by simulation; EA inferred]. Crash risk at start, caused by our write inside S3MM's pattern window (docs/68 §6 rule 2).

### 4.2 Pattern windows we write into later

`0xE4BF90` (P3) and `0xE53EBD` (P4, inside S3MM's 28-byte `interp_exec` pattern) are written at the first Simulate tick, after S3MM's scan, so its scan is not affected [inferred from both install times]. S3MM's calls to `0xE4BF90` and `0xE4A3F0` (heap browser, from the overlay) then run under our GC lock, which is an improvement [inferred].

### 4.3 S3MM's callvirt recovery vs `[MonoFast] CallPath`

S3MM's comment says the fault at `0xE54AEA` recurs every tick for some hooked methods and is recovered silently. With `CallPath=1` the callvirt opcode runs our handler, which reads the receiver the same way (`mintcall.cpp:267-271`: null test, then a read through it) [verified]. A small non-pointer receiver then faults at an EIP in `ts3native.asi`; S3MM's handler ignores it, ours pass it on: **crash where S3MM alone would recover** [inferred from both sources; whether current S3MM still produces the bad receiver is not known]. Compiled code (`[MonoJit]`) has the same shape at guarded callvirt sites.

### 4.4 In-place RuntimeMethod overwrite vs `[MonoJit]`

- Interpreted callers and `CallPath` read the callee's RuntimeMethod live on every call (`mintcall.cpp:257-287`), so they run the hook [verified].
- A `Direct` lazy call site bakes the callee's RuntimeMethod pointer and frame sizes and calls a cell keyed by that pointer; `Inline=2` copies the callee body into the caller. No per-call test compares the callee's code pointer or opcode word (`monojit.cpp:946-1041`, `1128-1144`) [verified]. After S3MM overwrites that callee, compiled callers **keep running the old body: the mod's hook is skipped**. Crash risk [inferred]: a lazy body's exit writes a frame for the overwritten RuntimeMethod with an ip of the old code.
- Reach: hooks S3MM installs at Mono ready mostly hit never-transformed methods (no compiled code yet; the replaced IL does not match our hash list, so we do not compile it). The stale case needs a target that was already compiled into a caller: hot reload, "Scan for New Mods" in a running game, unhook, and already-transformed (for example generic) targets.
- S3MM's clone of the original has the same name, IL size and hash, so we compile it as its own entry: harmless [inferred].
- `MintFuse` and the 0x1A9 words are invisible to S3MM (it never reads opcode words) [verified].

### 4.5 Second interpreter thread (AutShadow worker)

Our worker runs EA's interpreter concurrently with the sim thread and reaches S3MM's detours at `0xE525C0`, `0xEA3140`, `0xEA2AD0`, `0xE70D50`. S3MM's profiler and cache use thread-local state and locks [verified by grep], so the detours themselves look safe. Open: a hooked game method that runs speculatively on the worker executes third-party hook code and S3MM icalls whose native writes Satb's write log does not see. Whether Satb gives up on unknown icalls was not read (I-528-6).

### 4.6 No conflict found

Assembly load detours, script host detours, exception detours, the lookup cache, the overlay, the profiler globals, S3MM's own icalls, GC (S3MM has no GC hook; `[GC]`, gcmark, gcgate and the planned generational GC only meet its heap browser, which goes through locked `0xE4A3F0`), RefPack, ts3fixes.

Degraded, not broken: S3MM's profiler sees fewer calls. Compiled code calls two icalls directly (`0xE7AB60`, `0xE7D520`) and runs listed methods without interpreter dispatch, so per-method counts and times are low for JIT-compiled methods. Use `[MonoJit] Enabled=0` when profiling with S3MM.

## 5. Verdict

OK = no conflict found; degraded = wrong or missing data, no crash; conflict = a feature of one mod silently stops working; crash risk = can crash.

| S3MM feature | `[AutShadow] Satb=2` + `[MonoThread]` | `[MonoFast] CallPath=1` | `[MonoJit]` (Direct, Inline, BoundCall) | GC, MintFuse, EqualsFast, rest |
|---|---|---|---|---|
| Start-up, address scan, Steam 1.67 | OK [verified scan] | OK | OK | OK |
| Start-up, retail / EA exe | **crash risk** (4.1) [verified retail, inferred EA] | OK | OK | OK |
| Profiler (always installed) | OK if we install first; **crash risk** if S3MM is first (unguarded Satb, 4.1) [verified code path] | OK [verified: native bodies still pass `0xE525C0`] | degraded: missing calls and time [verified] | OK |
| Hooks from mods loaded at start | open (4.5) [inferred] | **crash risk** only if S3MM's recovered fault occurs (4.3) [inferred] | mostly OK; conflict for already-transformed targets (4.4) [inferred] | OK |
| Hot reload, late mod scan, unhook | open (4.5) | as above | **conflict** (hook skipped) and **crash risk** (4.4) [verified code, inferred effect] | OK |
| Exception log and crash handler | OK | recovery no longer reached for callvirt (4.3) | OK: our handlers pass on what is not ours [verified] | OK |
| Heap browser, inspector, object explorer | OK, now under our GC lock [inferred] | OK | OK | OK [inferred] |
| Resource browser, assembly diff, xrefs | OK | OK | OK | OK |

## 6. Recommendation

1. **Fix regardless of S3MM:** Satb must refuse act mode when its `0xE525C0` detour is skipped (4.1). Small change, own bugfix branch.
2. **Stand-down, same shape as `apex.cpp`:** `[Compat] S3mmStandDown=1` (default 1). Detect `Sims3MonoModder.asi` by module handle at our `DllMain` (it sorts before `ts3native.asi`, so it is already loaded) plus the late check Apex uses for a renamed file. When found:
   - `[MonoJit]`: off (first step). Later: keep the per-word JIT and stand down only `Direct`, `Inline`, `BoundCall`, or add a per-call compare of the callee's code pointer.
   - `[MonoFast] CallPath`: off, or keep it on with one extra test in the callvirt fast path (receiver below 0x10000 goes to EA's handler, so the fault stays at EA's address and S3MM's recovery still works).
   - Satb / AutShadow worker: off (no binder detour, no second interpreter thread). This also removes our write from S3MM's pattern window, which fixes the retail / EA start-up case.
   - Log one line and count stood-down modules like `apStood`.
3. **Until that ships,** tell S3MM users to set the three switches by hand (section 8).
4. **Tell sims3fiend** (docs/24 contact row): the fallback to Steam addresses on a non-Steam exe when a pattern fails, and that our interpreter patches exist. No upstream bug proven yet, so no PR.

## 7. Live test plan (one job, later)

Steam 1.67, Apex + bridge, S3MM `main` build plus its example mod, master pair.

- Arm A: stand-down build, default ini. Pass: S3MM log shows no failed address and no recovery-disabled line; our log shows the stand-down line; 10 min play, 0 crashes, 0 script errors; the example mod's hook fires.
- Arm B (shows the problem, stress, labelled): stand-down off, JIT + CallPath + Satb on. Hot reload the example mod five times while its target runs. Record: does the hook still fire after each reload (4.4), any fault in `ts3native.asi` at the callvirt handler (4.3), S3MM's recovery count, which of the two detours at `0xE525C0` came first (both logs).
- Read back: both logs' install lines for `0xE525C0`, S3MM's "scanned / fallback / failed" line (expect 1 fallback more when we write first).

## 8. Reply draft for the Discord user

> (Before 0.36.286 / v0.3.0; from then on ts3native switches the three off by itself when S3MM is installed.) Not tested together yet. From reading both sources: on the current release (v0.2.11, Steam) they should load side by side, but one of our background-thread features hooks the same Mono function as S3MM's profiler, so please set `[AutShadow] Enabled=0` in ts3native.ini while S3MM is installed. On the experimental builds also set `[MonoJit] Enabled=0` and `[MonoFast] CallPath=0`: our JIT can keep running the original method after S3MM hot-reloads a hook, and S3MM's profiler misses JIT-compiled calls. On a non-Steam exe do not combine them for now. We are adding automatic detection so these switch themselves off when S3MM is present, and we will test both together before the next release.

## 9. Built (2026-11-18, branch `bugfix/s3mm-standdown-1118`, ts3native `0.36.286-s3mm1`, NOT RUN)

Native selftest only; no game run, no S3MM install on the test machine.

- **Satb act refusal (6.1, our bug):** `SatbInstall` (satb.cpp) now installs the native-writer detours (binder bodies `0xE525C0`, static set / address / get `0xD89BF0` / `0xD89C60` / `0xD89CD0`) before act mode goes live and refuses act when any of the four is not in (`g_satbNatMask`): `REFUSE autshadow satb act: the <site> detour is not installed (foreign bytes: another mod hooks it, docs/291 §4.1); Satb off, the worker never runs concurrently`, `satbMode` -1, the existing `FAIL autshadow satb: Satb=2 -> mode 0 (off)` line follows. Any foreign hook counts, not only S3MM's. The AutShadow worker then runs as with `Satb=0` (the sim waits for it). Selftest `TestS3mm`: fake code pages with the game's prologues, one with a foreign `jmp` at the binder site: 3 of 4 installed, foreign bytes untouched, act refused; all four good: act allowed [verified, selftest].
- **`[Compat] S3mmStandDown=1` (default 1, 6.2; s3mm.cpp):** found = `Sims3MonoModder.asi` loaded at our `DllMain` (GetModuleHandle) or the file in Bin, `scripts\` or `plugins\`. Then `[MonoJit]`, `[MonoFast] CallPath` and the whole `[AutShadow]` (worker and Satb) do not install. Log: one `compat: S3MM detected, standing down [MonoJit], [MonoFast] CallPath, [AutShadow] (module loaded, file found; [Compat] S3mmStandDown=1, docs/291)` line, then `compat: stood down for S3MM: <module> not installed (docs/291 §6)` per switch that was on. `S3mmStandDown=0` logs `not standing down` and installs everything (tests only). Not detected: a renamed ASI (no byte probe; shortcut noted in s3mm.cpp). `[MonoThread]` stays (its P3/P4 have no jobs without AutShadow).
- Household rule: not applicable (the stand-down only disables modules).
- `tools/compat-scan.ts`: a `Sims3MonoModder.asi` in the scanned folders is a named finding (`S3mmStandDown`) instead of "unknown ASI".
- No queue: a live check needs S3MM installed; §7 is the plan. A queue that checks the line uses the regex `^.*compat: S3MM detected, standing down \[MonoJit\], \[MonoFast\] CallPath, \[AutShadow\]` (match: the line above; non-match: `compat: S3MM detected, not standing down (...; [Compat] S3mmStandDown=0, docs/291)`).

## 10. Open questions

- Load order in practice: does S3MM's start thread scan before or after our `DllMain` writes (depends on whether the ASI loader loads plugins under the loader lock)? Both logs answer it (I-528-1).
- Does current S3MM still produce the bad callvirt receiver, and how often (I-528-3)?
- Satb and unknown icalls on the worker (I-528-6).
- EA 1.69: S3MM's patterns could not be run against the on-disk exe.
- `mint_verifier.cpp` and `persist_bridge.cpp` were only skimmed; the persist bridge registers mod statics with the game's save persistence, which is S3MM's own save footprint, not ours.
