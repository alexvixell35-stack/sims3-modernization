# 547: Native hot spots named with the OpenBarrio map

OpenBarrio (github.com/OpenBarrio/OpenBarrio, GPL-3.0, commit 4aff52f, 2026-10-23) rewrites TS3W.exe functions one by one
and publishes their addresses and names in `docs/engine/FUNCTIONS.csv` (5,503 rows, Steam 1.67.2, the same layout
ts3native patches). This page only cross-references addresses: no OpenBarrio code is copied here, and their names are
quoted as theirs. Check their row status before trusting a name ("verified in-game" > "exehost" > "offline").

Method: every `0x…` address in our docs matched against their exact start addresses (61 of our 1,243 addresses match;
nearest-lower matches are not used: their coverage has big gaps, so a containing function cannot be inferred).

## Profile rows we had as unknown [names: OpenBarrio; percentages: ours, docs/71]

| Address | Our profile | OpenBarrio name (status) | What it means for us |
|---|---|---|---|
| `0x7D80E0` | "unknown": 1.1% self and ~6.1% inclusive pooled (all steps), on 16.6% of BP camera-pan stacks | `resource::m03::d05::Resolve` (offline) | Resolves a resource key: when the key has no type-tag bits (`words[3] & 0xF0000000` clear) and the resource manager does not have it (manager vtable slot 17), it builds a fallback key (`0x7D7580`), checks it (`0x54DD80`) and looks that up too. A **missing** key costs two full manager lookups, each walking the registered packages (`FindDatabase 0x4AFFC0` → `IndexCompressed::Find 0x4AA1B0`, docs/28 N20). |
| `0x7347C0` | "unknown", 1.3% pooled | `m03` activity release, `ReleaseActivity` (offline) | Takes a critical section to subtract 2 from an activity count and timestamp it at 1, then unlocks: a lock per resource release. Part of why `Mutex::Lock 0x4E16F0` / `Unlock 0x4E17B0` are 2.0% + 1.7% of main-thread leaves. |
| `0x4AC9C0` | "unknown (part/index key enumeration)", 1.0% | `resource::MatchingKeys` (exehost) | Confirms docs/42's guess. |
| `0x60EAD0` | "Device_ApplySamplerAndRenderStates" | `render::m07::b04::Flush` (offline) | Same function. |
| `0x50EB20` | "UTFWin_DrawDisplayLists", 4.3% | `ui::b09::Traverse` (offline) | Same function. |

## Candidates (hypotheses, need a native counter before any fix) [inferred]

1. **Resolve misses on camera pan.** If most `0x7D80E0` time is the missing-key path, a per-key result cache would halve
   the package walks. It is exact only while no package is mounted or unmounted, so it must clear on every manager
   change (mount `0x7D8030`, the resource change monitor `0x733D20`). First step, read only: count Resolve calls, misses,
   and fallback hits per second during a camera pan (needs an MSVC-built ts3native; a clang-built one must not run in
   the game, docs/15).
2. **Activity release lock.** Measure contention on the `+0xB8` critical section before thinking about it; an atomic
   rewrite changes threading behaviour and is not "same results" by construction.

## EA bugs OpenBarrio keeps on purpose, checked against our exe (2026-11-14)
OpenBarrio copies EA's behaviour 1:1 and marks each bug it keeps in a comment (~1,000 such comments). The ones that could
hang, leak or break a device reset, checked on the installed TS3W.exe (call sites found by scanning `.text` for `E8`
calls; addresses theirs, verdicts ours):

| EA function | Kept bug (their note) | Reachable? |
|---|---|---|
| `0x4ED840` path-part compare | with fold != 0, re-compares the same part forever when the parts are equal | **No** [verified]: every call site (`0x4ED931`, `0x4ED954`, `0x4ED974`, and through `0x4ED940`/`0x4ED960` from `0xAAFC61`, `0x7D0A41`) pass fold 0 (`push 0`; `0x7D0A41` pushes `ebx`, zeroed at `0x7D0896` [inferred: no other write on that path seen]) |
| `0xF20D10` PCM audio ReadRows | spins forever when a block's cursor already equals its size (a `je`-only continue); `bpr` 0 divides by zero | Only on an empty (0-byte) block after a block change, or sub-byte sample formats [inferred]. Candidate guard if a hang ever shows `0xF20D10` on the sim/audio stack |
| `0x610660` render-target capture | `GetRenderTarget` reference never released when the capture texture's size differs from the target (rejected fast path); a held default-pool surface makes D3D9 `Reset` fail | **Not in normal play** [inferred]: its only caller (pick renderer `0x6DFEC0`) sets target 0 to the pick surface from the same resource just before, so the sizes match |
| `0x862CC0` audio state Add | a duplicate key takes a 48-byte pool node and never returns it (`0x862910` does not consume duplicates) | Unknown rate. Slow leak candidate: count duplicates with a native counter (MSVC build) before any fix |
| `0x575CA0` anim FreeNames | names with capacity <= 1 not freed | Not a bug: that is EASTL's shared empty buffer |
| `0x6E89F0` debug spike object | global `0x11D1870` keeps a freed pointer | Debug-only path [inferred] |

## Not covered
OpenBarrio rewrites the native engine only. The Sim-side interaction locks (docs/546 §9: socials, PutDownPet, horse
mounting) are script (managed) code and are not in their map.
