# 115. Event 59: cache the lot's portal handler list (no `GetInvocationList` per dispatch)

Written 2026-10-18. Design only: no code, no game runs. Source finding: docs/105 §9.2 side finding (the dispatcher calls `GetInvocationList` on every dispatch). Rates: docs/108 §7. Tags as in AGENTS.md.

## 1. Answer

- **What it fixes.** `Lot.OnPortalPathPlan` (Sims3GameplaySystems.dll, `Sims3.Gameplay.Core.Lot`, `Lot.cs:4825`) runs once per World event 59 dispatch. Each call builds one `PortalPlanEventArgs` and calls `GetInvocationList` on the lot's private `mOnPortalPathPlanHandler`, which clones every handler node and allocates a `Delegate[]` [verified, IL and mscorlib, docs/105 §9.2]. So every dispatch allocates n + 2 objects (n handlers, the array, the args); this design removes the n + 1 that come from `GetInvocationList`.
- **Design (`invcache`).** A ts3fixes body patch of `Lot.OnPortalPathPlan` keeps a small table of (handler field reference, cached `Delegate[]`) pairs and reuses the array while the lot's field still holds the same reference. It rebuilds the array only when the reference changes. docs/105 §9.2 (I-105-6, ledger I-105-10) proved this exact [verified, static]: only the two event accessors write the field, every set change yields a new reference (or null), and a published delegate never changes its chain. The args object and the handler calls stay as in vanilla.
- **Gain [inferred, not measured].** About 9,100 (BP) and 7,300 (IP) dispatches per minute of play remove about 18k-27k (BP) and 15k-22k (IP) objects per minute, or about 0.5-0.9 MB/min (BP) and 0.4-0.75 MB/min (IP). That is 0.2-0.3 % of the managed allocation rate, so the GC gain is negligible (about 0.015 ms/s of mark time). Sim-thread CPU drops by an estimated 0.3-1.2 ms/s (the interpreted `GetInvocationList` body and n `Clone` icalls, minus one reflection field read). **Small.** The fix is worth building only because it is cheap (about 0.5 day) and exact. It becomes moot if the docs/105 native evaluator lands (0 dispatches per plan), but stays useful on fallback lots (mod handlers, S-tier "ask") and if that plan slips.
- **Existing mods.** None found that does this (a quick web search, 2026-10-18; the S3SS, S3PP and NRaas notes in docs/03, 07, 68, 69 and 77 do not touch the dispatcher) [community, negative search].
- **Effort:** about 0.5 day for Claude with Rodney's help: 0.25 to build (one hook, one tuning, two switches, three counters, an agent read, a repro entry) and 0.25 for the verify load plus one night A/B.

**Built 2026-11-15 (lane L4, NOT RUN):** as §3 (`PortalInvCacheHook` in `ScriptHotSpots.cs`, `InvListCache` in `TickMemo.cs`, tuning `kPortalInvocationCache` off, counters 99-101, agent `invcache_hits` / `_rebuilds` / `_mismatches`), plus: off the sim thread the game's method runs. Repro `event59-invcache`, queue `lane-l4-1115-memos.json`.

## 2. Where the dispatch lives [verified unless marked]

| Piece | Where | Note |
|---|---|---|
| Native ask | `Portal_LockState 0x64F2C0 -> 0x791C00 -> World_FireEvent 0x78F2B0` (event 59) -> host `0xD75FA0` -> runtime invoke | docs/18 table, docs/105 §2 |
| Managed entry | `LotManager.PortalLockedCallback` (Sims3GameplaySystems.dll, `LotManager.cs:746`) | resolves the Sim and portal object, calls the lot's dispatcher |
| Dispatcher (patched) | `Lot.OnPortalPathPlan(Sim, GameObject, int, int, bool) : PortalLockedCallbackResults` | the only per-dispatch `GetInvocationList`; public, instance |
| Handler field | `Lot.mOnPortalPathPlanHandler` (private, type `PortalPathPlanHandler`) | writers: only `add_PortalPathPlan` / `remove_PortalPathPlan` (I-105-10) |
| Vanilla handlers | `Sim.OnPortalPathPlan` (static, every lot at load) + one `PrivacySituation.OnPortalPathPlan` per live privacy situation on the lot | docs/105 §2; mods may add more (NRaas GoHere, docs/104 P2) |

Other `GetInvocationList` calls on the same field (`GetRoomPrivacySituations`, `UpdateDoorPrivacy`, `RoomLockedByPrivacy`) are not per dispatch and stay as they are. They could reuse the same cache later if a sample shows them.

## 3. Hook

Same mechanism as every ts3fixes fix: `Patch(typeof(Lot), "OnPortalPathPlan", new Type[] { typeof(Sim), typeof(GameObject), typeof(int), typeof(int), typeof(bool) }, typeof(PortalInvCacheHook), "DispatchHook", "DispatchOrig")` in `ApplyScriptHotSpots()` (`mods/ts3fixes/src/ScriptHotSpots.cs`). The patch copies the hook's MonoMethod header over the game's method before the interpreter compiles it. The method runs only in play, so it is uncompiled at tuning time. MonoPatcher is not involved. If another mod already compiled or replaced the method, `Patch` logs the failure and our ready flag stays false, so the game's method runs.

Behaviour spec (our own; the vanilla body is restated in docs/105 §2):

1. Not ready, switch `invcache` off, or a reflection failure: return `DispatchOrig(...)` (the game's method, unchanged).
2. Read the field `h` through a `FieldInfo` cached at apply time (a reference-type field, so no boxing [inferred: Mono 1.2.3 `MonoField.GetValue` returns the object]). `h == null`: return `NoListeners`, as vanilla.
3. Look `h` up in a 4-slot table by `ReferenceEquals` (no `Dictionary`: `Delegate.Equals` and `GetHashCode` are value-based, and the default comparer boxes in this Mono, see `TickMemo.cs`). Hit: counter [99]++, use the slot's array. Miss: `list = h.GetInvocationList()`, store (h, list) in the oldest slot, counter [100]++.
4. Verify (switch `invcacheverify`, one hit in `kVerifyEvery` = 8, house `VerifyNow`): take a fresh `GetInvocationList()`. Compare length, then for each index `ReferenceEquals(Target)` and `Method` equality. On a difference, counter [101]++ and dispatch with the fresh list (a checked call gives the game's answer, as `hoursmemoverify`).
5. Dispatch exactly as vanilla. Build the args object with the public constructor `PortalPlanEventArgs(actor, portalObj, this, roomFrom, roomTo, initialLocked)`. Copy the array reference to a local, call each element as `PortalPathPlanHandler` in its own try/catch that swallows the exception, and return Locked, then Discouraged, then Unlocked.

Sketch (our code, not the game's):

<!-- docs-code: ok -->
```csharp
public class PortalInvCacheHook
{
    public PortalLockedCallbackResults DispatchHook(Sim actor, GameObject portalObj, int roomFrom, int roomTo, bool initialLocked)
    {
        if (!sInvReady || !ShortcutOn("invcache")) return DispatchOrig(actor, portalObj, roomFrom, roomTo, initialLocked);
        Lot lot = (Lot)(object)this;
        Delegate h = (Delegate)sHandlerField.GetValue(lot);
        if (h == null) return PortalLockedCallbackResults.NoListeners;
        Delegate[] list = sInv.Get(h, PatchCounters());          // [99] hit / [100] rebuild
        if (VerifyNow("invcacheverify")) list = sInv.Check(h, list, PatchCounters()); // [101] mismatch -> fresh list
        PortalPlanEventArgs e = new PortalPlanEventArgs(actor, portalObj, lot, roomFrom, roomTo, initialLocked);
        for (int i = 0; i < list.Length; i++)
        {
            PortalPathPlanHandler f = list[i] as PortalPathPlanHandler;
            try { f(e); } catch (Exception) { }
        }
        return e.Locked ? PortalLockedCallbackResults.PortalLocked
             : e.Discouraged ? PortalLockedCallbackResults.PortalDiscouraged
             : PortalLockedCallbackResults.PortalUnlocked;
    }
    public PortalLockedCallbackResults DispatchOrig(Sim a, GameObject p, int f, int t, bool l) { return 0; } // receives the original header
}
```

`sInv` (a small class in `TickMemo.cs`, game-independent, with an assert self-check like the other memo classes): `Delegate[4]` keys, `Delegate[][4]` values and a round-robin index. `Clear()` runs on world load finished (the existing `Instantiator` handler) and when the switch goes off, so no handler or `PrivacySituation` target from an old world stays referenced.

Hot-path cost per dispatch: one reflection field read (icall), up to 4 reference compares, and no allocation. If the managed sampler shows the field read costing more than the saving, the fallback is to key the cache by `Lot` reference and drop a slot from `add_`/`remove_PortalPathPlan` patches (two more patches, no reflection on the hot path). Do not build that unless the A/B asks for it.

## 4. Thread and async routing

- **Managed dispatch runs only on the sim (Mono) thread [verified, docs/18 table "Portal-lock callback into C#" and §"0xD75FA0 detour"].** With `[RouteWorker]`/`[RouteAsync]` and design A10 (docs/87 §10), event 59 raised inside a worker plan goes through the host delegate call `0xD75FA0`. That call is proxied to the owner thread (0.7.1), which serves it in the waiting acquire (sync mode) or at READY polls and pumps (async, docs/60 §2). The docs/108 rates include those proxied dispatches, which are all served on the sim thread.
- So the static table needs no lock. Condition [inferred]: this holds only while one thread runs managed code. If `[MonoThread]` (docs/30) ever runs managed routing work on a second thread, the hook must check `Thread.CurrentThread` against the sim thread and run `DispatchOrig` on any other. That check is not part of this design.
- docs/105 plans to replace the dispatch with a native evaluator (0 dispatches per plan) and plans a shadow patch of the same method (I-105-12). Build both in **one** hook: the shadow evaluation runs after the cached-list dispatch.

## 5. Gain estimate [inferred unless marked]

Inputs: docs/108 §7 [verified live]: BP 21,063 plans x 4.30 = about 90.6k event 59 dispatches per 10 min (9,060/min, 151/s); IP 8,257 x 8.78 = about 72.5k per 10 min (7,250/min, 121/s). Handler count n per dispatch: 1 (`Sim.OnPortalPathPlan`) plus one per live privacy situation on the lot, so mostly 1 and sometimes 2 (not measured, I-115-4).

Object sizes (EA Mono, 4-byte header, I-104-1 offsets up to `prev +0x1C` and `kpm_next` after it): a delegate clone is about 36 bytes, 40 after Boehm's 8-byte granule; a `Delegate[n]` is 12 + 4n bytes, so 16 (n = 1) or 24 (n = 2).

| | per dispatch, n = 1 | n = 2 | BP per minute | IP per minute |
|---|---|---|---|---|
| Objects removed | 2 | 3 | 18.1k-27.2k | 14.5k-21.8k |
| Bytes removed | 56 | 104 | 0.51-0.94 MB | 0.41-0.75 MB |

- **GC:** with the 352 MB heap, one collection runs every 30 s with about 151 MB free (docs/04), so the game allocates about 300 MB/min. The saving is 0.15-0.3 % of that: a collection every 30.05-30.1 s instead of 30 s, and about 0.015 ms/s of the 4.8 ms/s GC mark time. Not measurable.
- **Sim-thread CPU:** `GetInvocationList` runs interpreted (mint, docs/29), with one `Clone` icall per handler and an array allocation through the GC fast path. An estimate of 3-10 µs per call against about 1-2 µs for the added field read gives about 0.3-1.2 ms/s (BP) and 0.25-1 ms/s (IP). The managed sampler A/B measures this (§7).
- **Fewer dispatches matter more.** The docs/105 evaluator removes the whole dispatch (args, handlers, the native-to-managed transition), which this fix does not touch.

## 6. Risks

| Risk | Why it is safe, or what guards it |
|---|---|
| Handler added or removed during a dispatch (a `PrivacySituation` ending inside a handler) | vanilla iterates the array it fetched before the loop, and so do we: the loop holds a local reference. The next dispatch sees the new field reference and rebuilds. Same semantics [verified: vanilla takes one list per call]. |
| Re-entrancy (a handler causes a nested event 59) | a nested call may replace a table slot. The outer loop keeps its local array, and the arrays are never written after they are built. Same answer as vanilla. |
| Stale reference / ABA | the table holds the delegate as a managed reference, so the object cannot be freed or its address reused while cached (I-105-6 condition; Mono 1.2.3 does not move objects). |
| `Delegate.Equals` value semantics | the lookup uses only `ReferenceEquals`; no `Dictionary`, no `GetHashCode`. |
| Cached clones vs fresh clones | vanilla also invokes clones from `GetInvocationList`; the clone has the same target and method. The verify mode compares both per index. |
| Leak of old handlers | 4 slots, round robin, cleared on world load and when the switch goes off; at most 4 old handler sets (and their `PrivacySituation` targets) live a little longer. |
| Mod handlers (NRaas GoHere) | added through the accessor, so the reference changes and the cache rebuilds. A mod that writes the private field by reflection also changes the reference, so it is still caught (the key is the field value, not an accessor hook). |
| Another mod replaces `Lot.OnPortalPathPlan` | `Patch` fails if the method was already compiled. If the other mod patches after us, ours is overwritten and the counters stay 0. Both cases are visible in `ts3fixes.applied`. None known [community, negative search]. |
| Thread | sim thread only today (§4). A second managed thread voids the no-lock table (§4 condition). |
| Exception behaviour | same per-handler try/catch, and the args are constructed with the same arguments. |

## 7. Test plan

Memory rules: one change at a time; in-load A/B (`--ab invcache`), not separate runs; 10-minute profile runs; clean-night only for timing; fail-fast waits; stop on obvious errors.

1. **Build:** the hook in `ScriptHotSpots.cs` and the table class in `TickMemo.cs` with an assert self-check: hit, rebuild on a new reference, eviction, `Clear`, and null. Add tuning `Instantiator.kPortalInvocationCache` (default **false**) and switches `invcache` and `invcacheverify` (the verify one is false by default with no tuning, as `hoursmemoverify`). Counters 99-101 (§8): raise `new int[99]` to `new int[102]` in `Fixes.cs` `PatchCounters` and `PatchHandlers.cs`, and read them behind `if (c.Length > 101)`. Agent names: `invcache_hits`, `invcache_rebuilds`, `invcache_mismatches`.
2. **Repro (`tools/harness/repros.json`, id `event59-invcache`):** on Baseline-BP-Spring, make the handler set change while plans run. (a) The `bp-door-lock` steps (a door locked for non-household Sims, household and visitor plans through it) give dispatches with a non-default answer. (b) A household Sim showers or uses the toilet, starting a `PrivacySituation` that adds a handler, while a second Sim is sent into that room; then the situation ends and the handler is removed. If no agent action starts a privacy situation, add one (`use_object` on the home shower) [inferred: a shower or toilet in use starts a privacy situation, docs/105 §2]. Expected: `invcache_rebuilds` rises at each privacy start and end, `invcache_mismatches` = 0, the second Sim waits at the door as with the switch off, and there are no script errors.
3. **Verify load (daytime allowed, no timing):** BP, `invcache` + `invcacheverify` on, 10 min seeded at speed 3, plus the repro. Pass: hits > 0, mismatches 0, rebuilds far fewer than hits (expected: lot loads + 2 x privacy situations + travel; hits about 90k/10 min x 7/8).
4. **In-load A/B (clean night, `clean-night.json`):** `profile-world.ts --ab invcache`, BP and IP, 10 min, with the managed sampler. Read the managed samples of `GetInvocationList` under `Lot.OnPortalPathPlan` (expected about 0 in the on half), sim ms/s, hitches/s and `gc_no` per half. Pass: no regression beyond noise, 0 stuck Sims, 0 mismatches. A gain below noise is the expected outcome; the fix ships if it is stable, as a default-off allocation cleanup. Rerun if the machine load line shows other CPU >= 10 %.
5. **Crash rule:** any crash on this build is ours until the fault address says otherwise (AGENTS.md); two clean runs before calling it fixed.

## 8. Counter slots (reserve in docs/registry.md)

Next free after 98 [verified, registry 2026-10-18]: **99-101**, owner `ScriptHotSpots.cs`, `invcache` (+ `invcacheverify`): [99] dispatches served from the cached list, [100] list rebuilds (new handler reference), [101] verify mismatches. Reserved in docs/registry.md by this commit (status `reserved (docs/115)`); if another branch merges a claim on 99 first, this range moves to the next free one (registry rule 3).

## 9. Ledger

Rows I-115-1 to I-115-7 in docs/51 §56.
