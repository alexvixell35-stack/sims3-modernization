# 521. IL patch converter: the core mod's text patches to IL patch specs (docs/506 §6, built)

Built 2026-11-02 (contributor round 8 task B1, the offline half; the ts3native patcher of docs/506 §2 is not in this
PR: no C++ toolchain on the contributor's machine). `node tools/ilpatch-convert.ts` reads the private core repo's patch
files (PowerShell files whose entries are Find / Replace blocks of ildasm listing text) and writes one spec per method
in the docs/506 §1 form: the method id, the G-M6 hash, per build the exact body size and SHA-256, the tokens our lines
need, and the SHA-256 of the body after the patch. The three docs/506 §5 examples are converted and committed in
`mods/ilpatch/specs/`. Nothing in a spec is EA's: the body is named by its hashes, and the re-emitted EA instructions
come from the player's copy at load.

Claim marks: **[verified]** = the tool's output on the core repo at this commit, or read in the code; **[inferred]** =
our reading, with its check.

## 1. Usage

```
node tools/ilpatch-convert.ts [--core <Core repo>] [--only <file substring>]... [--write] [--csv <file>]
node tools/ilpatch-convert.ts --self-check
```

- `--core` (or `TS3_CORE_REPO`): the core repo; it reads `patches\*.ps1` and, for the self-check,
  `out\<assembly>.dll`, the core build with every patch applied.
- Builds: `ea-1.67` = `game-copy/assemblies`; `simler90-242` = `game-copy/simler90-242/dll` over EA's when present
  (extract it from the UPDATE242 package with `tools/extract-assemblies.ts`). The core patches were written against the
  UPDATE242 listing, so the method is located there, then by id in the other builds.
- `--write` writes `mods/ilpatch/specs/<id>.json` for the converted entries and the census CSV
  (`docs/data/ilpatch-convert.csv`); without it the tool only reports. `--self-check` runs the relocator and the
  alignment on synthetic bodies, no files.

## 2. What it does, per patch entry

1. **Parse.** `Name`, `File`, `Find`, `Replace`. Lines with an `IL_xxxx:` label are EA's instructions at that offset;
   lines without one are ours; `//` comments are dropped; a line that starts with neither a label nor an opcode is the
   continuation of the operand before it (ildasm wraps long parameter lists).
2. **Locate.** The method whose body has, at every labelled offset, that opcode with that operand: branch targets by
   offset, members by a normalized key (owner, name, arity, parameter types; assembly prefixes, `class` / `valuetype`
   and spacing dropped; ildasm primitive names mapped to the reader's), locals by index, constants by value. One match
   is required; several are reported as `multi-method`.
3. **Align.** Walk Replace: our lines before a labelled line that is unchanged are an insert at that offset with
   `entry: fallthrough`; a labelled line whose opcode changed means the label moved onto our code (`incoming`) and the
   EA instruction at that label is dropped (a `replace`): the Replace text is the whole truth, an EA line that stays is
   listed again unlabelled (until 2026-11-02 the EA instruction was kept too, so B68's `IL_0096: brfalse.s` came out as
   brfalse + EA's brtrue.s on one stack value and the transform crashed; B64, B68, B70 and B73 changed with the fix); Find
   labels missing from Replace are a replace of those instructions. An EA branch that only changed from its short to its
   long form (B80's `brfalse.s` → `brfalse`) is unchanged: the relocator writes every branch long anyway. `maxStack` is
   the peak extra depth of our lines (calls by their signature text).
4. **Tokens.** Each member in our lines is looked up per build in a key index over MethodDef, MemberRef, MethodSpec,
   Field, TypeDef, TypeRef and TypeSpec (the module's own definition first, else the reference). A build with a member
   that has no token is left out of the spec; no build left means `unresolved`.
5. **Apply** (the offline copy of docs/506 §2 steps 3-5, `apply()`): decode the build's body, check `at` and `expect`,
   emit EA's instructions with every branch in its long form and every `switch` re-targeted, our instructions with the
   build's tokens, move every exception clause, and write a fat header with the raised max stack. `patchedSha256` is the
   SHA-256 of the new code.
6. **Self-check.** The patched body's normalized shape (`managed-callgraph.ts` `ilShapeBody`: tokens as names, branch
   targets as instruction indices) against the core build's own body of that method. `equal` when nothing else touches
   the method; when other core patches do, our run of instructions (opcodes and tokens; branch targets not compared) must
   appear in the core body; otherwise the entry `differs`.

## 3. Results on the core repo (859 entries in 278 files) [verified]

| Outcome | Entries | Meaning |
|---|---|---|
| converted | 325 (293 + 32 merged into a spec of the same method) | spec written for both builds; self-check: 239 equal to the core build, 343 with other core patches on the method and our instructions present (per build), 4 differ (below) |
| needs helper | 127 | the Replace adds `.method` / `.field` members (docs/506 phase 2) |
| whole method | 94 | the patch rewrites the header (`.maxstack` / `.locals`): a ts3fixes wrap, not a spec |
| unresolved | 155 | a member of our lines has no token in EA 1.67 or UPDATE242: it is a member another core patch adds (a helper), so these are "needs helper" too |
| no method match | 115 | no body of the base build matches the Find block: a listing of an intermediate core build, or an anchor inside a wrapped operand |
| multi-method | 14 | the Find block is in several bodies (compiler-generated twins) |
| no change | 14 | Replace equals Find |
| error | 15 | the block starts inside a wrapped operand (13), or a Replace label that is not in Find (2) |

By file: 79 files convert whole, 88 in part, 111 not at all. The four `differs` entries are two files whose patches
change the control flow around our lines (`99_o7_magicgnome.ps1` RemoveAgingAlarms, `99_s15_styling.ps1`
GetCustomerReactionType): the core build's bodies there come from later edits of the same lines, so the converted spec
reproduces an older form of the fix and must not ship without a look.

The three docs/506 §5 examples: `b89-fixup-no-inventory` (other core patches in `Fixup` too; our run present),
`b80-remote-chat-no-everyday-outfit` and `b87-scavenger-sleep` (equal to the core build, both builds). Their tokens
differ between EA 1.67 and UPDATE242 (e.g. `get_CreatedSim` is `0x060104f5` in EA and `0x060010b2` in UPDATE242), which
is why the spec carries a token map per build and the patcher must match the exact body hash before it uses one.

## 4. Spec fields the tool fills, and what is left to the native side

`id` is the patch file's name (`b89_fixup_no_inventory` → `b89-fixup-no-inventory`, numbered when a file yields several
methods); `switch` is `ilp_` + the id without its build prefix; `counter` is null until the ts3native patcher reserves
its slots (`tools/registry.ts reserve native`); `source` names the file and entries. `ilHashG6` comes from
`tools/autshadow-il-hashes.ts --hash-file` (EA 1.67). The spec's `il` lines keep the ildasm member text, with branch
targets as original offsets (`@0x81`) and local indices as numbers.

Not built: the ts3native patcher (docs/506 §2, §8) and the monohost offline tests of the three specs. The relocator in
this tool is the TypeScript copy docs/506 §6 step 4 asks for; the C++ copy must produce the same bytes (compare
`patchedSha256`).

## 5. Checks

- `node tools/ilpatch-convert.ts --self-check`: the relocator on a synthetic body (a short branch over 130 inserted
  bytes becomes long and lands right; the exception clause moves; `incoming` entry; a replace), the alignment of a
  docs/506 B89-shaped text patch, and the member keys (ildasm text vs the reader's rendering, plain and generic).
- I-521-1 [inferred]: the converted B80 and B87 bodies are what the core build runs. Check: equal shapes (done, §3);
  the live check is the patcher's own repro per fix (docs/506 §8).

## 6. Labels, counter bumps, merged entries (2026-11-02, second pass) [verified: `--self-check`, census]

Three defects of the first pass, found while converting #166's b77 (the toddler put-down):

1. **Our labels.** The core patches branch to labels of their own (`brtrue.s B77_GONE`, `B77_GONE:` on a line of its own,
   `FIX_HOME_OK:  IL_006c:  callvirt ...` on an EA line, or a label as the block's last line). The parser only knew
   `IL_xxxx`, so a label line was glued onto the operand before it and `parseInt("B77_GONE", 16)` made the branch target
   `0xB77` (or `0xF` for `FIX_...`). Those entries were rejected ("branch target 0xb77 is inside a replaced range or not an
   instruction start"), so no wrong spec was written, but b17, b69, b73, b77 and 67 others were lost to it. Now a label
   names the next instruction: one of ours stays a label in the spec's IL (`B77_GONE: ldarg.0`, `brtrue B77_GONE`; the
   native table carries `edit index << 16 | line index`, so a branch may cross edits of one spec), one on an unchanged EA
   line becomes that line's offset (`@0x6C`), one at the end of the block the offset after it.
2. **Run counters.** A core build's own counter (`ldsfld sB77ParentGone; ldc.i4.1; add; stsfld sB77ParentGone` on a field
   another entry of the same file adds) has no token in any build. The converter drops the bump (`source.droppedCounters`
   in the spec, "counter ... dropped" in the status); the patcher counts applications itself, the per-run count is lost.
   The comparison with the core build takes the bump out of the core's shape first.
3. **Merged entries.** A second entry on the same method was appended to the first spec's edits without the offline
   application, the self-check or its tokens (the b64 spec had no token for `List<Sim>.get_Count()` and the native table
   could not be emitted). The whole spec is checked again at each merge; 6 merged entries turned out unresolved.

4. **A block's own branch to its `at`** (found live, docs/526 §8). The core patches end a fallthrough block with a
   branch to the EA line the block sits before (`brtrue.s IL_007d` in b69, `brfalse.s IL_04bb` in b90, a tail label in
   b17-1): continue with the original instruction. `map()` sent a branch to an insert's `at` to the block's first line
   when it came from that same edit, an endless loop once taken (the smoke 24 hang). Now an insert's `@at` from anywhere
   means the original instruction after the block; only an incoming insert's or a replace's `at` means our first line.
   `--self-check` covers it; the native patcher had the same clause and the same fix.

Census after the pass (`docs/data/ilpatch-convert.csv`, 859 entries): converted 390 (357 + 31 merged + 2 with a counter
dropped; was 325), equal to the core build in both builds 153 (was 120), unresolved 90 (was 155), error 14, the other
groups unchanged. Five converted entries differ from the core build without our instructions found in it (`99_o7_magicgnome`
RemoveAgingAlarms, `99_s15_styling` GetCustomerReactionType, `99_s2_sim_1` extinguish outfit wait, `99_zr2_b2_o3` pet
cancel wait, `99_zzb_b22` free-vacation wishes): the core's later edits of the same lines; they must not ship as they are.
Of #166's 45 builds, 21 fixes (14 methods more than docs/526 §7's seven) now convert whole: see docs/526 §7.
