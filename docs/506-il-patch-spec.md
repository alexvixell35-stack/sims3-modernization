# 506. IL patch spec and patcher: mid-method fixes shipped as a diff, not as EA code

Design 2026-11-01, not built. Contributor round 7 task 5 (Rodney: "design first; we review before the build").

**Question:** many core fixes change a few instructions in the middle of an EA method (e.g. Build 89 in
`SimDescription.Fixup`). A ts3fixes port must wrap the whole method, and when the change is in the middle it cannot
without carrying EA's body. Can these fixes ship like our native byte patches: a small diff that checks what it expects,
applied at load to the player's own copy of the game's script module, and skipped when the method is not the one we
checked?

Tags: **[verified]** = read in our code, the core patch files or the tool output; **[inferred]** = reasoning, with the
check that confirms or refutes it in §7.

## Summary

- **Spec.** One JSON file per fix: the method's full name, its G-M6 IL hash (identity across builds), then per known build
  the exact size and SHA-256 of the IL body and the tokens our inserted code uses. Each edit is an insert or a replace at an
  IL offset, with the opcodes expected there as a check and our own IL in ilasm syntax with member names. No EA bytes are
  stored: the body is identified by its hash, and the re-emitted EA instructions come from the player's copy at load.
- **Patcher.** In ts3native, at the entry of Mono's IL-to-mint transform (`0xE643B0`). It is the same function the compiler
  and the satb cold paths use, so one shared detour serves all three. For a listed method whose body hash matches a known
  build exactly, it decodes the IL and inserts our instructions. It re-encodes every branch in its long form and moves the
  exception clauses. It then hands EA's transform the new body for that one call and restores the header after. Anything
  unexpected leaves EA's body and counts a skip.
- **Conversion.** The 277 core patch files hold 867 patch entries. 79 files (319 entries) add fields or methods and do not
  fit this form [verified: grep of `.method`/`.field` in the patch files]: they need a ts3fixes helper (phase 2, §6) or a
  whole-method wrap. The other entries are inserts or replaces with existing members. A converter turns the Find/Replace
  text into specs. The three examples in §5 are all single inserts on the fall-through path.
- **Build estimate.** About 5-7 h (§8) after review, plus one game run per converted fix.

## 1. Spec format

One file per fix, `mods/ilpatch/specs/<id>.json` (our code and numbers only):

| Field | Meaning |
|---|---|
| `id`, `switch`, `counter` | Fix id. `ts3native.ini` switch `ilp_<id>` (off by default, one per fix). Counter slot (registry) |
| `assembly`, `method` | Module name and full method name with signature, as the call-graph index keys it (`tools/managed-callgraph.ts`) |
| `ilHashG6` | G-M6's token-normalized IL hash of EA 1.67's body (`tools/autshadow-il-hashes.ts`). The method's identity: a build whose body has this hash is EA's method, whatever its token numbers |
| `builds` | One entry per build the converter checked: `{ size, sha256, tokens, patchedSha256 }`. `sha256` is over the exact IL body bytes; `tokens` maps each member name in `il` to its metadata token in that build; `patchedSha256` is the body after the edits (§4, the compiler list) |
| `edits[]` | `{ at, mode, entry, expect, il, maxStack }`, applied in offset order |

Edit fields:
- `at`: IL offset in the original body, an instruction start.
- `mode`: `insert` (before the instruction at `at`) or `replace` (`expect.length` instructions from `at` are dropped).
- `entry`: for an insert, where branches that targeted `at` go after the patch. `fallthrough`: to the original instruction,
  so the insert runs only on the fall-through path. `incoming`: to the inserted code. Text patches show it by where the
  `IL_xxxx` label sits; all three examples keep the label on the original instruction (`fallthrough`).
- `expect`: opcode names of the instructions at `at`, compared after decoding. It is a readable check; the body hash
  already proves the bytes.
- `il`: our instructions, one per string, in ilasm syntax with full member names. Branch targets are original offsets
  (`@0x81`) or local labels inside `il`.
- `maxStack`: how much the insert can raise the stack depth (the converter computes it; added to the header's max stack).

Why the per-build raw hash and not only the G-M6 hash: the inserted bytes carry tokens. A build with the same normalized
hash but other token numbers (simler90's rebuild, an ilasm round trip) needs its own token map. The converter resolves the
names offline for each build it knows (EA 1.67, simler90 UPDATE242, the user's core). A build it has not seen fails the
exact hash and is skipped, which is the safety rule Rodney asked for ("only if the IL hash matches exactly").

## 2. Patcher (ts3native)

At the transform entry, for each method (main thread; the satb worker gives up at this function before the patcher
runs):

1. Read the method name (as monohost-jit's `JitMethodName` does from the runtime method) and look it up in the armed specs:
   specs whose switch is on, loaded at start-up. No spec → EA's transform, nothing else.
2. Read the body from the method header (I-506-1). Size prefilter, then SHA-256 of the body (BCrypt, as `ImageSha` in
   PR #169). No build matches → skip, `ilp_skip_hash` + the fix's counter stays 0, one log line per method.
3. Decode every instruction (ECMA-335 opcode table: one-byte and `0xFE` two-byte opcodes, `switch` tables). Check each
   edit: `at` is an instruction start, the opcodes there equal `expect`, and every branch in `il` targets an instruction
   start in the same exception region as `at` (a plain branch may not leave a protected block; I-506-3). Any failure →
   skip, `ilp_skip_check`.
4. Re-encode into a new buffer: EA's instructions copied from the player's body, edits inserted. Every branch is written in
   its long form and every `switch` is re-emitted, so no short branch can overflow. Original offsets map to new offsets
   (per `entry` for inserts).
5. Move every exception clause (try and handler start and length, filter offset) through the same map. Header max stack
   += the largest `maxStack`.
6. Point the header's code, size and max stack (and clauses if they moved) at the new copy, call EA's transform, restore
   the header. Reflection and the G-M6 check keep reading EA's bytes (I-506-2). Count `ilp_applied` and the fix's
   counter. Log once per method with the fix id and build.

Cost: a name lookup per transformed method, and a hash only on a name hit. Normally the transform runs once per method
per session, so the patched path runs once per fix. MonoPatcher's on-demand `ForceJIT` (`0xE5637D` `jne` -> `90 90`)
makes EA transform on every call while it is active [verified: mintcall.cpp, docs/68]. The dispatcher therefore caches the
verdict per runtime method (patched body, or none), so a repeat transform costs one pointer lookup and reuses the
patched buffer.

Failure handling: every step that does not hold leaves EA's body unchanged, and a crash inside steps 2-6 is caught by the
same `__try` pattern `ImageSha` uses. The fix is then off, never half applied.

## 3. Hook point

`0xE643B0` is the transform (IL to mint) [verified: monohost-jit.cpp wraps it; satb.cpp lists it as cold site
"transform (IL -> mint)" with its 6-byte prologue]. ts3native's satb cold-path detour checks the same prologue bytes
(`InstallDetour`), so a second detour at that address would fail its byte check [verified: satb.cpp `kSatbCold`,
`InstallDetour(..., bytes, len)`]. The build therefore adds one transform-entry dispatcher with three clients in a fixed
order: the satb give-up (worker thread), the IL patcher, and later the compiler (task 3 a: monohost-jit inside ts3native).
The compiler hook runs after EA's transform, the patcher before it, so they do not conflict.

The patcher could also work at load time (rewrite the image's bytes once). That needs a writable copy of the module and
breaks the content hash of PR #169, so the per-call swap is the better fit.

**Prior art: MonoPatcher `ReplaceIL`.** MonoPatcher 0.3.0 detours Mono's `GenerateCode` at `0xE5CA90` and swaps
`header->ilBegin` / `codeSize` for the methods a mod replaces [verified: docs/68 hook map row `0xE5CA90`, from docs/06].
That is step 6's mechanism, and it shows that a header swap at code generation works in this Mono. It is community
evidence for I-506-1, still to be measured with our patcher. What differs here:
- MonoPatcher takes a whole replacement body from the mod, with no check of what it replaces;
- the spec carries only our lines and applies on an exact hash;
- the hook stays at the transform, not at `0xE5CA90`. MonoPatcher writes that site in its DllMain (a second detour there
  would fail one of the two byte checks), and a body MonoPatcher has already swapped fails our hash and is skipped.
- ts3fixes' own patch host swaps whole `MonoMethod` headers in the heap (docs/04 F8). That is the whole-method wrap path,
  and it stays the first choice whenever a fix can wrap the method.

## 4. The compiler and the other hash lists

- A patched method has a new IL hash. The compiler's known-hash list (task 3) and `compile-candidates.csv` key methods by
  hash. Each spec carries `patchedSha256` per build, and the dispatcher flags the runtime method as patched, so the
  compiler looks it up by the patched hash.
- G-M6 (`AutShadowIlHashes`) hashes the bytes reflection returns. Those stay EA's because the swap lasts one call
  (I-506-2), so a patched method does not count as changed. If I-506-2 fails, each spec adds its patched G-M6 hash to the
  table as an accepted build.

## 5. Converted examples

The three specs as the converter would write them. Hashes and tokens are left out here; the converter fills them per build
from the assemblies. Only our inserted lines appear; the EA instructions around them are named by offset and opcode.

**Build 89: `SimDescription.Fixup` skips the trait-chip pass for a Sim with no inventory.**
EA's own block already branches to offset 0x81 when there is no Sim. The fix adds the same jump when the Sim has no
inventory, inserted after that check on the fall-through path.

```json
{
  "id": "b89-fixup-no-inventory", "switch": "ilp_fixupnoinv", "counter": "<reserved>",
  "assembly": "Sims3GameplaySystems",
  "method": "Sims3.Gameplay.CAS.SimDescription::Fixup()",
  "ilHashG6": "<EA 1.67>", "builds": { "ea-1.67": "<size, sha256, tokens, patchedSha256>" },
  "edits": [{
    "at": "0x2E", "mode": "insert", "entry": "fallthrough", "expect": ["ldarg.0"], "maxStack": 1,
    "il": [
      "ldarg.0",
      "call instance class Sims3.Gameplay.Actors.Sim Sims3.Gameplay.CAS.SimDescription::get_CreatedSim()",
      "callvirt instance class Sims3.Gameplay.Inventory Sims3.Gameplay.Abstracts.GameObject::get_Inventory()",
      "brfalse @0x81"
    ]
  }]
}
```

**Build 80: a phone chat with a Sim who has no everyday outfit no longer throws.**
The portrait balloon is shown only when the target's first everyday outfit exists; otherwise EA's other branch (offset
0x5F) runs. Inserted after the random-chance branch at 0x21.

```json
{
  "id": "b80-remote-chat-no-everyday-outfit", "switch": "ilp_remotechatoutfit", "counter": "<reserved>",
  "assembly": "Sims3GameplaySystems",
  "method": "Sims3.Gameplay.Actors.Sim::DoOneRoundOfRemoteChat(Sims3.UI.CAS.IMiniSimDescription)",
  "ilHashG6": "<EA 1.67>", "builds": { "ea-1.67": "<...>" },
  "edits": [{
    "at": "0x23", "mode": "insert", "entry": "fallthrough", "expect": ["ldloc.0"], "maxStack": 3,
    "il": [
      "ldloc.0", "ldc.i4.2", "ldc.i4.0",
      "callvirt instance class [SimIFace]Sims3.SimIFace.CAS.SimOutfit Sims3.Gameplay.CAS.SimDescriptionCore::GetOutfit(valuetype [SimIFace]Sims3.SimIFace.CAS.OutfitCategories, int32)",
      "brfalse @0x5F"
    ]
  }]
}
```

**Build 87: `ScavengerOpportunity.OnAdded` sleeps between spawns only where a script may yield.**
EA pushes the sleep argument at 0x281 and calls `Simulator.Sleep` at 0x282. The insert before 0x282 makes that pushed
`0` the argument of `CheckYieldingContext(false)`, skips the sleep to 0x287 when the context cannot yield, and pushes the
sleep argument again otherwise. This is EA's own pattern elsewhere in the module.

```json
{
  "id": "b87-scavenger-sleep", "switch": "ilp_scavengersleep", "counter": "<reserved>",
  "assembly": "Sims3GameplaySystems",
  "method": "Sims3.Gameplay.Opportunities.ScavengerOpportunity::OnAdded()",
  "ilHashG6": "<EA 1.67>", "builds": { "ea-1.67": "<...>" },
  "edits": [{
    "at": "0x282", "mode": "insert", "entry": "fallthrough", "expect": ["call"], "maxStack": 0,
    "il": [
      "call bool [SimIFace]Sims3.SimIFace.Simulator::CheckYieldingContext(bool)",
      "brfalse @0x287",
      "ldc.i4.0"
    ]
  }]
}
```

All three use only members the module already references: B89 and B80 call what the same method already calls, and B87
uses a call EA makes elsewhere in the module. The converter confirms each token per build (I-506-4). Method names, offsets and `expect` opcodes were read from
the UPDATE242 base listing the core builds from (the same EA 1.67 bodies [inferred: the core changes neither method
outside these patches]).

## 6. Conversion of the core patch files

`tools/ilpatch-convert.ts` (local, reads the private core repo; it writes specs only):

1. Parse each patch entry's Find and Replace blocks. Lines with an `IL_xxxx` label are EA instructions at that offset;
   lines without one are ours.
2. Align the two sequences: a run of ours between two labelled lines is an insert at the next label's offset. `entry` is
   `fallthrough` when that label stays on the EA instruction and `incoming` when it moved to our first line. A labelled
   line that is missing from Replace is a replace.
3. Resolve every member name in our lines to a token, in each build the converter knows (the same assembly reader as
   `tools/autshadow-il-hashes.ts`). A name with no token in a build → that build is left out of the spec; no build left →
   the entry needs a helper.
4. Apply the spec offline to each build with the same decoder and relocator as the native patcher (one TypeScript copy for
   the tool, the C++ copy for ts3native; a shared test corpus keeps them equal) and compare the result with the core
   build's own body for that method (same instructions, same normalized operands). That is the converter's self-check: a
   spec that does not rebuild the core's method is rejected.
5. Report per file: converted, needs helper (adds members, or uses a member with no token), or multi-method (one spec per
   method).

Phase 2, for the 79 files that add members: a reserved token range that the patcher resolves to ts3fixes helper
methods. It needs a hook on the token-to-method lookup the transform calls [inferred; not checked]. That is only worth
designing once phase 1 has shipped a few fixes.

## 7. Open claims and their checks

| ID | Claim | Check | Confirms | Refutes |
|---|---|---|---|---|
| I-506-1 | The transform reads the IL body, size, max stack and clauses from the method's cached header, so changing those fields for the call changes what it transforms | monohost: disassemble the transform's header reads, then transform one spec'd method offline with the swap | mint for the patched body (our branch in the mint) | mint unchanged |
| I-506-2 | Reflection's IL bytes (G-M6's input) do not come from that header during the call, and nothing else reads it then (the transform runs under its lock) | monohost: hash the method through the G-M6 path after a patched transform | EA's hash | the patched hash |
| I-506-3 | Mono's transform does not check that a plain branch stays in its exception region, so the patcher must | read the transform's branch handling; the patcher rejects such edits anyway | — | — |
| I-506-4 | Every member the three examples call already has a token in EA 1.67's Sims3GameplaySystems | converter step 3 | tokens found | a missing token |

## 8. Build plan (after review)

| Step | Estimate |
|---|---|
| Converter: parse, align, token map, offline apply + self-check against the core build | ~2 h |
| ts3native: transform-entry dispatcher (satb client moved onto it), decoder, relocator, header swap, specs loaded from the ini switches | ~2-3 h |
| monohost offline tests: the three specs, a changed byte (hash skip), an edit with a cross-region branch (check skip) | ~1 h |
| Game runs: one repro per converted fix, each arm's step throws on a wrong outcome | ~10-15 min each, listed separately |

The three examples ship off by default. Each gets the same repro the core fix had (B89: Fixup on a Sim with no inventory;
B80: the remote chat with a target with no everyday outfit; B87: an opportunity added from a non-yielding context).
