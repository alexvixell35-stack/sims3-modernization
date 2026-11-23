# 549. Today's issues against the OpenBarrio rewrites (static, 2026-11-14)

Static only: no game run. Sources: OpenBarrio (GPL-3.0, read for logic and names, nothing copied; addresses and names are
theirs), capstone over our repack copy of TS3W.exe 1.67 (every "verified" below was read in those bytes), docs/546-548.
Tags: **[verified]** = read in the bytes; **[inferred]** = reasoning, with the check that would confirm it.

Summary: OpenBarrio covers the **package/resource manager**, the **job scheduler**, the **package file locks** and the
**audio state table**. It has **nothing** on the route planner, FindGoodLocation, object creation or the Simulator
internal calls, which are the paths of issues 1-3.

## 1. Route failures (NoPath / PortalLocked / NoDestination / tantrum, pet doors)

**On the path in OpenBarrio:** only `RoutingSampler::SampleSegment 0x75AFA0` and `SampleArc 0x75B610` (goal sampling of
radial-range destinations), `RouteManagerObject` ctor/dtor/ReleaseHandle `0x857220/0x8583D0/0x8591F0` (lifetime only),
`RouteJob 0x599FC0` (this is the job scheduler's "route a job to a worker", not path routing). Not covered: the planner,
`0x7596E0`, `0x831530`, `0x830340`, `0x82E340`, `0x807090`, `0x946E90`, `0x752620` (docs/548).

**Findings.**
- **Goal checks grow with radius squared [verified arithmetic].** The radial-range generator (function containing
  `0x75C246`) makes `trunc((max-min)/spacing + 0.5)` rings (at least 2), and `SampleArc` makes
  `trunc(arc * radius / spacing + 0.5)` samples per ring (at least 1); every sample runs a full goal check (`0x759B20`
  -> lot lookup, heights). A 10 m radius at 0.5 m spacing is about 20 rings x up to ~125 samples. This is what made
  `ReactToFire` (`PlanToPointRadialRange`) plans expensive (docs/546 §10, already handled by `petfireplan`).
- **Hang hazard [verified bytes; reachability inferred].** Both counts are taken from `fistp` as the **low dword compared
  unsigned** (`0x75B6DE ja`, `0x75C0C7 ja`). A computed count of -1 or less (negative spacing, a reversed arc or a
  negative radius from script) becomes about 4 billion samples, each a full goal check: the sim thread never returns.
  NaN/inf gives 0, so 1 sample (safe). We found no caller that passes such values; the check is a script-side log of
  `RouteDistribution`/radial values that are negative.
- No loop, leak or stale state in the covered functions. The repeated failures are script replans (docs/548 §1).

**Verdict:** native cause not found in OpenBarrio's coverage. Fixes stay script-side (docs/548 §5, `petfireplan`).

## 2. Sims waiting on sync partners

**On the path:** nothing. `Simulator_SleepImpl 0x76CB00` / `GetTaskImpl 0x76CA60` and the Mono task scheduler are not
in their map. `t1::Sleep 0x4E1320` is a plain OS thread sleep. **Verdict:** managed (docs/546 §9 fixes stand).

## 3. Slow alarms (stray pets, sea-life spawner, PetAdoption)

**On the path:** nothing for `FindGoodLocation 0x78AE70`, `Simulator_CreateObjectImpl 0x76D0E0`, or Sim instantiation
(only small CAS outfit getters `0x5A4D30..0x5A6C80`). Resource lookups they make go through §4.
**Verdict:** no native finding; keep the managed measurement (`spawnplace:`, `alarmtime`).

## 4. Camera-pan hitches, low address space, the one-off whole-process hang

**On the path in OpenBarrio:** `Resolve 0x7D80E0`, `FindDatabase 0x4AFFC0`, `DelegateDatabase 0x4AFDA0`, the manager
add/remove `0x4B2D00` and reposition `0x4B2EC0`, `IndexCompressed::Find 0x4AA1B0`, `Mutex::Lock/Unlock 0x4E16F0/0x4E17B0`,
package access `AcquireActivity 0x734710` / `ReleaseActivity 0x7347C0`, `DispatchRetained 0x734550`, deadline refresh
`0x7355A0` -> `0x735320`, the job scheduler `0x599xxx-0x59Axxx`.

Findings, ranked by likely effect in play:

1. **A Resolve miss walks every package twice, with two mutex ops per package [verified].** Manager vtable slot 17 is
   `0x4AFDA0` -> slot 16 `FindDatabase 0x4AFFC0` (both vtables `0xFB2DA0` and `0xFFE250`). `FindDatabase` locks the
   manager mutex (+0x48), then **for every database** unlocks, calls the database's find (vt +0x34), and locks again.
   `Resolve` on a key without type-tag bits that is not found builds the fallback key `0x7D7580` (flips group bit
   `0x08000000`) and walks again. Cost per miss is about 2 x N index finds + 4 x N lock ops (N = mounted packages, CC
   included). This fits the 16.6% of camera-pan stacks. The negative cache of docs/547 candidate 1 is the fix shape,
   but it is exact only if it is cleared on every database add/remove/reposition **and** every database write (a key
   missing now can be written later), so first count misses (MSVC probe).
2. **FindDatabase keeps begin/end across the unlock [verified `0x4AFFD6`/`0x4AFFD9`, esi/ebp held over
   `0x4AFFF4`..`0x4B001C`].** The database remove path `0x4B2D00` also keeps its iterator across its unlock
   (`0x4B2E3A`..`0x4B2E73`) and erases by it [verified]. When another thread adds or removes a package during a lookup,
   the walker reads a freed vector or a released database, and the remover can erase the wrong slot. Result: a crash or
   a resource missing for one lookup. Rate unknown [inferred: needs package add/remove in play on one thread while another
   resolves].
3. **Reposition `0x4B2EC0` drops the database it moves [verified `0x4B2F18`..`0x4B2FFD`].** It erases the pair, then
   inserts from the same (now shifted) slot: the moved database disappears from the list, the next one appears twice,
   and the new priority is never stored. If this runs in play, every resource of that package goes missing (more Resolve
   misses, possibly "missing content"). **No caller found** next to the 487 manager singleton fetches. First step: a
   counter detour on `0x4B2EC0` (log caller); a fix is only worth writing if it fires.
4. **Hang candidate: the package lock order (ABBA) [already ours: `pkglock.cpp`, docs/539 §13.9].** The reader path
   `0x734550` -> `AcquireActivity 0x734710` takes the database lock then the file critical section (+0xB8/+0xC0), and
   `AcquireActivity` opens the file (`0x72D790`) while holding it. The main thread's `0x7355A0` -> `0x735320` takes them
   the other way round. A deadlock here freezes the whole process with no log, which matches the 5-minute hang. Run with
   `[PkgLock] Detect=1` (read-only) in every test until the hang is classified; `Mode=1` is the fix.
5. **Other EA hangs OpenBarrio keeps that a hang dump should be matched against [their notes; reachability inferred]:**
   SACS sort `Partition 0x982980` (empty-key pivot spins; sort entry `0x982DF0`, single caller `0x982E80`),
   `ReadRows 0xF20D10` (docs/547), JPEG upsample `0x4287B0` (width < 2), KNN sort `0xB7BA00` (inverted range), and
   `SampleArc 0x75B610` (§1). A main-thread stall sampler (EIP + stack after N s without a frame) would name the loop; we
   have none (userdiag's watchdog covers saves only).
6. **Not a problem:** the scheduler's "deadline is the debug tag at `0xFD9744`" quirk: those bytes are `0xFFFFFFFF`, the
   infinite timeout [verified]. `ReleaseActivity 0x7347C0` is one short critical section per release; fine.
7. **Dead code, low impact [verified `0x59A7AA`]:** `Depend 0x59A6E0` compares the insert position with the list head after
   the insert, which can never match, so a new dependency never raises the awaited job's priority (`Propagate 0x59A470`).
   Fixing it changes job order (not "same results" by construction); leave it.

## 5. Audio state duplicate-key node leak (0x862CC0)

**On the path:** `AudioState_Add 0x862CC0`, `Insert 0x862910`, `Mark 0x862980`, `Prune 0x862AC0`, `Tick 0x862C20`, pool
grow `0x8625B0` (all OpenBarrio-rewritten).

**Verified:** `Add` takes a pool node, `Insert` returns the existing node on a duplicate key, and `Add` drops that result:
the node is lost (48 bytes; the pool grows in blocks of up to 0x154 nodes and never shrinks). The single caller is
`0x9435D0`, the audio component's handler for **object events 0, 1 and 7** (registered at `0x944B50`); event 5 (before
destroy) marks the node. The table belongs to the audio system singleton (`[0x11D8C40]-0x27BD0`, +0x27BF4), so leaked
nodes last for the process. Any object with an audio state that gets two of the events 0/1/7 before it is destroyed leaks one node
per extra event [inferred: which events fire for which objects is not traced]. Expected size: kilobytes to a few MB per
session, so a small address-space item, not a hitch cause.

**Fix:** `[AudioStateDup]` in `native/ts3native/audiodup.cpp`, wired and off by default; see §7.

## Next steps (ranked)
1. Every test run: `[PkgLock] Detect=1`; add a main-thread stall sampler so the next hang names its loop (§4.4-5).
2. MSVC build (CI), then one game run with `[PkgProbe] Enabled=1` and `[AudioStateDup] Enabled=1` (both log only), a camera
   pan and a travel: read the lines of §7.4. Design the negative cache or the index-based FindDatabase walk only after that.
3. If `audio state dup:` lines appear: a run with `[AudioStateDup] Fix=1`. If `pkgprobe: remove` lines show `ppOverlap` or
   off-main removes: a run with `[PkgProbe] RemoveFix=1`. If `pkgprobe: reposition` ever appears: write the reposition fix
   (§7.2).

## 7. Built (2026-11-14, clang-cl `/Zs` only, NOT RUN; counters v919 at 0x6304, contributor block)

Every switch defaults to 0 in `ts3native.ini`. Re-checked in the bytes for this build [verified, capstone over the repack
copy]: every prologue below, no branch into any patched range, the call conventions (`ret` counts) used by the hooks.

### 7.1 `[AudioStateDup]` (audiodup.cpp): `Enabled=0`, `Fix=0`
- Review of the draft: the prologue `55 8B EC 83 E4 F0 83 EC 18` matches; `Insert 0x862910` takes a third (bool) argument it
  never reads; the duplicate path returns the existing node with "inserted" 0 [verified]. The state is the object stored at
  audio `+0x27BF4` (a pointer, not an embedded table).
- Threads: every code reference to `+0x27BF4` is in the game audio class's event-writer slots (`0x8650B0`..`0x865D86`), its
  `Update 0x865E10` (Tick/Prune), the object event handlers `0x9435D0` / `0x944250` and the ctor / reset [verified scan]. Update
  runs on the sim thread [measured, docs/35]; the EA audio thread's loop reaches no table function through direct calls
  [verified to 6 levels; its virtual calls not followed]. So the bucket walk runs on the thread that writes the table, as
  Insert's own walk does; EA takes no lock here and neither do we [inferred for the event writers].
- Tidied: chain walk bounded (`2^20` nodes, then "not ours to judge", EA runs), SEH around it, counters moved into
  `Ts3nCounters`, log carries the thread.
- Counters: `adMode` (0 off, 1 count, 2 Fix, -1 detour refused), `adCalls`, `adDups` (EA leaks 48 bytes each), `adSkipped`.

### 7.2 `[PkgProbe]` (pkgprobe.cpp): `Enabled=0` (log only), `RemoveFix=0`
- **Resolve `0x7D80E0`** is replaced by our own copy of its steps (only when the whole 190-byte body hashes to the one
  verified here). It calls manager slot 17 through the vtable at call time (Apex's slot chain included), EA's fallback-key
  builder `0x7D7580` and compare `0x54DD80`, so every answer is EA's. Counts: `ppResolve`, `ppTagged` (type-tag keys, no
  lookup), `ppMiss` (first lookup found nothing), `ppRetry` (fallback lookups; always = misses, since the fallback always
  differs [verified `0x7D75B6`]), `ppRetryHit`, `ppNoMgr`.
- **RegisterDatabase `0x4B2D00`** (thiscall: bool add, database, priority; `ret 0xC`; manager slot 13, also called from the
  subclass slot `0x736A70`) and **SetDatabasePriority `0x4B2EC0`** (database, priority; `ret 8`; slot 15) are entry detours that
  count and pass the call on unchanged (full `eax` kept): `ppAdds`, `ppRemoves`, `ppRepos`, `ppOffMain` (not the main thread),
  `ppOverlap` (while another Resolve was in flight: the window of finding 2). `ppMounted` = list size at the last summary.
- **Finding 3 (reposition) stays probe-only.** The defect is confirmed [verified `0x4B2F18`..`0x4B2FFD`: after the erase, the
  insert reads the database and priority from the old slot, which now holds the next pair]. No caller found: no direct call,
  and no `[reg+0x3C]` call within 60 instructions of any of the 489 manager fetches [verified scan]. Apex lists this slot
  (`ResSetDbPriority`, Night Lights) [claim, `apex-hooks.csv`]: if Apex itself calls it, the probe line shows it.
- **Finding 2, remove half: `RemoveFix=1`.** A 6-byte stub at `0x4B2E78` (right after the remove path's relock at
  `0x4B2E73`) finds the database's pair again in the current list (first match, as EA's own search). Same slot = EA's path
  unchanged; moved = erase at the new slot (`ppRfMoved`); gone = no erase, straight to EA's unlock and return
  (`ppRfGone`). The erase helper `0x4B0B90` is a smart-pointer copy, so erasing at the right slot keeps every reference count
  right [verified]. Without a concurrent add / remove the list is the one EA searched, so results are vanilla.
- **Finding 2, FindDatabase half: probe-only.** Apex owns its slot (ResourceLookupCache), and a walk whose list changed under
  it has no vanilla answer to keep (skip or repeat one package); `ppOverlap` measures how often the window opens.
- Apex: these are jmps inside EA bodies; Apex's `slot_chain` checks slot values, which stay EA's (docs/148 §2). `[Verify]`'s
  registerDb probe skips `0x4B2D00` when ours is in. docs/68 and `apex-sites.csv` (class C2) list the sites.

### 7.3 Checks run
- clang-cl `/Zs` (x86, xwin headers, `/W4`) of `ts3native.cpp` and `selftest.cpp`: no diagnostics in the new files (only the
  existing castrim / pools / p10 warnings). The MSVC build and `selftest.exe` (`TestPkgProbe`, `TestAudioStateDup`: fake
  manager, fake list, the RemoveFix stub run with EA's registers, fake 53-bucket table) run in CI only.
- `node tools/hook-sites.ts --check`, `node tools/registry.ts --self-check`, `node tools/selftest-all.ts --ci`.

### 7.4 What to read in `ts3native.log` after a game run
- Install: `OK   detour Resolve (pkgprobe)`, `... RegisterDatabase (pkgprobe)`, `... SetDatabasePriority (pkgprobe)`,
  `... AudioState_Add (audio state dup)`, then `pkgprobe (docs/549 §4): mode 7` (15 with RemoveFix) and `audio state dup on`.
  `SKIP` lines mean the bytes differ (another patch or another exe): nothing installed there.
- `pkgprobe: last 60 s resolve N (tagged, no manager, looked up L: miss M = P%, fallback tried, hit H); D databases; session:
  adds, removes, repositions (off main, during a resolve), remove re-find moved / gone`: one a minute. P% and L during a pan
  size finding 1 (each miss = two walks of D packages); H/M says how much a negative cache would keep.
- `pkgprobe: remove database ... tid ... (main|other) from 0x...; N databases, R resolves in flight` and `pkgprobe:
  reposition database ...`: findings 2 and 3 happen in play only if these lines exist (R > 0 or `other` = the race window).
- `audio state dup: key ... from 0x943625 tid ...; dups N of M adds`: the leak rate (48 bytes per dup).
