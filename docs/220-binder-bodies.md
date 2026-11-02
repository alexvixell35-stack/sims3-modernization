# 220. What EA's binder native bodies write

Written 2026-10-30. Static only: no game run, no build. Contributor research round 2, task 2. Updated the same day: the init references are resolved and the store sites are kept per target (§2, §3); 10 rows stay unknown, all through one store.

Question: EA replaced 1,085 managed methods with native bodies, bound through the binder (`0xD9A7B0`, docs/130 D17). Satb's mint barrier does not see their stores, and docs/212 classified them only by the IL of the managed method they replace (a proxy). This doc classifies every body from its own code: pure reader, writes only its out / ref / array arguments, writes other managed memory, writes native state only, or unknown, and marks the bodies the `[AutShadow]` worker can reach.

Output: `docs/data/binder-bodies.csv`, 1,085 rows, one per registration. Tools: `tools/binder-bodies.py` (registrations, init references, store analysis, classes) and `tools/binder-bodies.ts` (managed facts: bound method, parameter kinds, field layout, IL field writes, init reference targets, worker reach).

## 1. Results

| Class | Rows | Worker-reachable (any label) | Direct (`v0` or `tier2`) |
|---|---|---|---|
| reader | 708 | 143 | 52 |
| writes-args | 25 | 12 | 4 |
| writes-other | 296 | 45 | 17 |
| native-only | 46 | 18 | 7 |
| unknown | 10 | 0 | 0 |
| **total** | **1,085** | **218** | **80** |

Confidence: high 588, medium 487, low 10 (the unknown rows). Medium means the row's class may be too low: 471 rows pass a managed pointer to a call the analysis cannot follow (`opaque_handoffs`: mostly indirect calls, 49 truncated GC or allocator callees), and 16 more have a native store whose base pointer was lost (`unresolved_native`).

Reach labels are the ones of docs/218 (PR #112): `v0` = the docs/122 pilot closure, `tier2` = a docs/126 ready or new-rows candidate, `-cha` = through overrides up to 5 hops by class hierarchy, `tier2-blocked` = only through blocked candidates, `no` = not reachable (867 rows).

What the classes contain [verified, from the CSV]:

- **writes-other (296).** 263 rows write only fields of `this` (or objects reached from them, such as `XmlAttributeTokenInfo.get_Value`'s value cache): `Mono.Xml2.XmlTextReader` 80, `System.Text.StringBuilder` 57, `System.Xml.XmlTextReader` 33, `Sims3.Gameplay.Utilities.Curve` 18, `System.Xml.XmlParserContext` 17, `ScriptCore.PersistReader` 17, `Sims3.Collections.PriorityQueue` 7, `System.IO.StringReader` 6. 14 rows write objects reached from arguments, 19 both or a static field. 12 rows write static fields: 7 `.cctor`s plus `SimClock.set_TicksAdvanced`, `set_StartTime`, `SetStartTime`, `AddOneHour`, `AddOneDay`. None of the 12 static writers is reachable from the worker.
- **writes-args (25).** Sizes are in the column: `this:4` (struct `this`, 9 rows, e.g. `DateAndTime..ctor(long)`), `arg1:4` 8, `arg0:4` 5 (e.g. `SimClock.ElapsedTimeInMinutes(ref float)`), `arg2:4` 4, `arg3:4` 2, and single rows with byte or unknown sizes. `String.ParseFormatSpecifier` writes its five byref arguments.
- **native-only (46).** 40 rows write only the IC nesting counter `[0x11F2D84]` (§4.2). 5 are `String.memcpy`/`memcpy1`/`memcpy2`/`memcpy4`, which write through their `byte*` destination (`arg0 target`); the destination is managed memory whenever the caller passes a string's characters. `MonoMethod.IsDefined` writes through `this.mhandle`, the native `MonoMethod*`.
- **reader (708).** No store outside the interpreter frame, the return slot, the thread context, runtime bookkeeping and objects the body allocated. 264 readers have an opaque handoff, so their confidence is medium.
- **unknown (10).** All through one store, `0xD9CCA4` in the `MonoCustomAttrs.GetCustomAttributes(ICustomAttributeProvider, Type, bool)` body (§3); none is reachable from the worker.

Direct worker-reachable writers (80 direct rows, 28 that store):

| Group | Rows | What they write |
|---|---|---|
| `StringBuilder` constructors, `Append`, `Insert`, `Remove`, `InternalEnsureCapacity` | 13 | `this._str` (fields and characters in place), `_length`, `_cached_str`, `_maxCapacity` |
| `String.Format(IFormatProvider, string, object[])`, `FormatHelper` | 2 | objects reached from the `object[]` argument (field unknown, medium) |
| `Array.Clear` | 1 | the array, through the `ClearInternal` icall |
| `String.InternalSetChar` | 1 | the string's characters in place |
| `DateAndTime..ctor(long)`, `SimClock.ElapsedTimeInMinutes(ref float)`, `String.ParseDecimal(string, ref int)`, `String.ParseFormatSpecifier` | 4 | struct `this` or the byref arguments |
| `String.memcpy*`, `String.Format(string, object, object)`, `Array.Sort(Array, int, int, IComparer)`, `Enum.GetName` | 7 | native only: the copy destination or the IC counter (`Array.Sort` sorts in managed code it calls) |

The 10 direct rows that were unknown in the first version (`SimClock` day helpers, `DateAndTime.get_DayOfWeek`, `Substring`, `Concat`, `Replace`, `ParseFormatSpecifier`) are now 9 readers (they write only the string they allocate) and `ParseFormatSpecifier` (writes-args).

Most of these write objects the caller just made (a `StringBuilder` for a tooltip or a key, a result string). The CSV cannot tell a fresh receiver from a shared one; that is a property of the call site (§4.3).

## 2. Method

| Step | How | Tag |
|---|---|---|
| Registrations | the table builder `0xE3E570` calls `0xD9A860(hash, adapter, body, init)` 1,085 times; `index` = call order (the hashes ascend) | [verified] |
| Bound method | hash -> method by `managed-callgraph.ts` `bindKeys` (the binder's own key string); 1,085 of 1,085 bound | [verified] |
| Entry and roots | the dispatcher calls `adapter(MonoInvocation frame, ThreadContext)` through `RuntimeMethod+0x24`; frame `+0x0C` = `this`, `+0x14` = arguments in 8-byte slots, `+0x08` = return slot, `+0x20` = pending exception (docs/130 call path) | [verified] |
| Init references | 924 registrations have an init (161 have none). The init fills binder globals through helpers that take an IL offset into the bound method's own IL: `0xD9A890` the callee's RuntimeMethod (header -> IL code -> token at that offset -> `mono_get_method`, class init, vtable, `Interp_GetRuntimeMethod`, binder), `0xD89F00` its icall pointer (`mono_lookup_internal_call`, cached at `+0x20`), `0xD89EB0` a field, `0xD89D90` a string (`ldstr`), `0xD89DE0` a class, `0xD89E50` a generic token. `binder-bodies.py --hashes` decodes each init and writes `global:kind:offset`; `binder-bodies.ts` reads the token at that offset. 4,575 of 4,605 references land on an opcode of the right kind at exactly that offset (method 3,324, string 506, class 289, icall 246, field 179, token 61); most of the 30 misses are in `BCLDefaultFormatter.CreatePersistFormatter`, whose IL does not decode | [verified] |
| IL hash guard | before it fills anything, every init calls `0xD89D40(constant, method)`: `0xD83680` hashes the bound method's IL (method header, opcode size table `0x108C2B0`) and compares. On a mismatch it clears the RuntimeMethod's native entry (`+0x24`, `+0x28`, `+0x2C`) and the init returns, so a method whose IL a mod changed stays interpreted | [verified]; which IL bytes enter the hash is [inferred] |
| Icall effects | 245 globals hold an icall pointer (92 distinct icalls). A call through one gets a summary from Mono 1.2.3 icall semantics and the EA ScriptCore signatures: constant result (`get_OffsetToStringData` = 8), fresh result (21, e.g. `InternalAllocateStr`, `String..ctor(char[])`, `Enum.get_value`), stores through named arguments (22, e.g. the `InternalStrcpy` destination, `InternalCopyTo`'s `char[]`, `Array.ClearInternal`, the `StreamHost_Read*` out arguments, `MonoField.SetValueInternal`'s object), managed code (3: custom attribute constructors, `MonoMethod.InternalInvoke`; listed in `calls_managed`), no managed store (45). The table is in the tool; icalls outside it would be opaque handoffs (none occur) | [inferred] per icall |
| Stores | abstract store analysis over symbolic inputs (stack arguments, entry registers): each function is analysed once, its summary (stores, final registers, stack pop) is substituted at every call site; pointer values are tracked 4 loads deep; each import or icall call instruction is its own store site | [verified] method; per-row results as tagged |
| `_setjmp3` arms | every icall wrapper sets a jmp_buf with `_setjmp3` (`0xF7EE92`) and tests the result; the non-zero arm runs only after a longjmp from a raised exception. A byte scan finds 238 such calls in the glue: 222 arms restore the frame's jmp_buf field (`+0x10`) and jump to the exception-slot check, 6 restore and return, 10 reach the check a few instructions later. The analysis drops these arms; following them merged stale integer locals into copy destinations | [verified] scan |
| Pointer arithmetic | `x + x` is a count; a constant or integer operand is an index; a product or shift (`imul`, `mul`, `shl`, `shr`, `sar`) is an integer, so `base + 0x10 + index * mono_array_element_size` stays on the array (`PriorityQueue`, `Hashtable.Rehash`); `add [local], reg` keeps the pointer (`string + OffsetToStringData` lives in a local in `Concat` and `ToUpper`); `memcpy`, `memmove`, `memset` and the `str*`/`wcs*` copies return their destination (C standard) | [verified] rule |
| Frame pointers in loops | a value with 3 or more frame offsets on one chain becomes "frame, offset unknown" at the join. `0xDC94B0` keeps a vector with inline stack storage and uses it as a stack (push and pop in a loop); without this the abstract end pointer walked onto the vector's own header slots and every later store through it was unknown (27 rows) | [verified] read at `0xDC94C6`-`0xDC99B1` |
| Store sites | a summary keeps one site per store target; when two stores share a target, the site that hides the least is kept (glue or import call, then runtime or engine, then the EA allocator), because the classifier relabels by site. Keeping the first site had hidden glue stores behind Mono or allocator sites (the image map store above, the vector of `0xDC94B0`, the custom attribute store of §3, `XmlAttributeTokenInfo`'s value cache) | [verified] rule |
| Impossible candidates | a store candidate derived from a scalar field, a scalar argument or the inside of a string (sync record, length, characters) is dropped when the same store instruction also has a possible target (a fresh object, a native or managed pointer); without one it would stay unknown | [verified] rule; the dropped candidates are merge artifacts [inferred] |
| Stops | Mono allocation entries return a fresh object; managed re-entry (`ScriptHost_NativeToManaged 0xD8A8E0`, interpreter and `runtime_invoke` entries, class init `0xEA3370`) is recorded, not followed; the exception builders `0xE77000`-`0xE775FF` and `mono_raise_exception 0xEA18F0` end the path (docs/122 I-122-26); the EA static-field helpers `0xD89CD0` / `0xD89BF0` / `0xD89C60` are modelled | [verified] by reading each |
| Remoting | `0xEA2D10` has the shape of `mono_upgrade_remote_class`: `mono_object_isinst` (`0xEA3820`) reaches it when the object is a transparent proxy, and it rewrites the proxy's remote class at `+8`. No script assembly uses `RealProxy` or a transparent proxy (docs/78 index), so it is modelled without stores. Followed, it put an "object at unknown offset" store on 36 rows (`Enum.GetValue`, `String.Equals(object)`, `GetCustomAttributes`, `Array.Sort`, the persist formatters) | [inferred] |
| Not counted | stores to the interpreter frame, the return slot and `ThreadContext` (per script thread, docs/57); runtime bookkeeping (stores in Mono and the GC at `>= 0xE40000`, the EA heap `0x4E0000`-`0x4EFFFF` whatever the block, EA runtime support such as the binder `0xD9A7B0` and the image loader `ScriptHost_LoadImage 0xD78760` (its image map at `host+0xAD0`, reached from Mono assembly loading through the image hook `0xD6FCB0`), and records reached through the binder's init globals `0x11EE000`-`0x11F3000`); unresolved import stores are judged by the instruction that called the import; stores to objects the body allocated | [verified] ranges |
| Field names | Mono 1.2 `mono_class_layout_fields` rules with this build's 4-byte object header (docs/130 D18): parent fields first, reference fields first for auto-layout classes outside mscorlib, other fields sorted by alignment, structs in declaration order, explicit layout from the FieldLayout table | [verified] check below |

**Layout check [verified].** Of the 843 native stores through `this` that the analysis resolves, 835 land on a field start with the field's size under the model above. The 8 misses: 7 are the high half of a `long` field in a struct `this` (`DateAndTime`, `PriorityQueue+QueueItem`; the CSV shows them as `this:4`), and 1 is `String.InternalSetLength` at `+4` (strings are not laid out by the model; their stores show as characters in place). The `--self-check` of `binder-bodies.ts` pins three known layouts (`PriorityQueue`, `Curve.m_x` at `0x14`, `Vector3`) and one init reference.

## 3. Unknown rows: 44 then, 10 now

The first version left 44 rows unknown because the destination of their copies came from an icall pointer the init caches (for `Concat(string, string)`: `call [0x11EF494]` = `InternalAllocateStr`, from the call at IL `0x39`). With the init references resolved:

| First-version group | Rows | Now | Why |
|---|---|---|---|
| copy into a string the method allocates (`Substring`, `Concat`, `Replace`, `Insert`, `Remove`, `PadLeft`, ...) | 12 | reader | the destination is the `InternalAllocateStr` result plus `get_OffsetToStringData` |
| path runs into such a body (`String.Concat` 14, `String.Copy` 4, `String.Remove` 1, incl. the `SimClock` day helpers) | 19 | 18 reader, 1 writes-other | same, one call deeper; `XmlAttributeTokenInfo.get_Value` also writes its value cache |
| `ToUpper` / `ToLower` (with and without culture) | 4 | reader | the character loop writes the result string through `add [local], OffsetToStringData` |
| `ParseFormatSpecifier`, `PersistReader.ReadObjectTable`, `XmlTextReader.SkipTextDeclaration` | 3 | writes-args, writes-other, writes-other | five byref arguments; `this.mObjectOffsetTables`; `this.line`, `column`, `peekChars`, `curNodePeekIndex` |
| `GetCustomAttributes` (`MonoCustomAttrs`, `MonoType`, `MonoMethod`) | 6 | unknown | the store below |

**The 10 unknown rows [verified store, inferred target].** All 10 reach `0xD9CCA4` in the body of `MonoCustomAttrs.GetCustomAttributes(ICustomAttributeProvider, Type, bool)` (`0xD9BCB0`): the 6 above plus `Attribute.GetCustomAttribute` (2), `MonoCustomAttrs.GetCustomAttribute` and `MonoType.InvokeMember`. The store writes element 0 of an array (`[array + 0x10]`, after a length check) that the body gets back from a managed call through `NativeToManaged`. The method's init references resolve that call to `Array.CreateInstance(Type, int)` (IL `0x35`), followed by a `castclass` to the array type (TypeSpec `0x1B000052`, class global `[0x11EEBF0]`, IL `0x3A`), which is the one the body passes to the castclass helper `0xD89B40` right before the store. `Array.CreateInstance` returns a new array, so the store is on a fresh object [inferred]; the analysis does not follow managed results, so the rows stay unknown and carry this in `unknown_hint`. None of the 10 is reachable from the worker.

Count check against the first version [verified, CSV diff]: unknown -> reader 34, writes-other 3, writes-args 1, unknown 6. Other moves:

- 35 writes-other rows drop to reader (21) or native-only (14). For 32 of them the only managed store was the remoting path (`0xEA2D10`, §2); `String.Split` (2 rows) and `PriorityQueue.Verify` lost a merged-path store.
- 7 readers become writes-other: 6 through an icall store (`MonoType..ctor(object)`, where `type_from_obj` fills the type; `Array.Clear`; `FastBaseStreamReader.Read` for bytes and chars; `MonoField.SetValue`; `String.CopyTo`) and `Binder.FindMostDerivedMatch`, whose store the first version had hidden behind an allocator site.
- 4 writes-other rows become unknown (`Attribute.GetCustomAttribute` 2, `MonoCustomAttrs.GetCustomAttribute`, `MonoType.InvokeMember`): their old managed store was the remoting path, and the `0xD9CCA4` store was hidden behind a Mono site.

## 4. Findings for the worker

1. **Re-entry [verified].** 862 of 1,085 bodies can call back into managed code through `ScriptHost_NativeToManaged 0xD8A8E0` (162 also through `mono_runtime_invoke`). That code runs in the interpreter, so Satb's mint barrier and the managed closures cover its stores; the native part of the body does not. docs/122 found the same for its 78-body closure (59 enter the interpreter).
2. **IC nesting counter `[0x11F2D84]` [verified].** 131 bodies reach a write of it, 29 of them reachable from the worker (`v0` 4, `tier2` 2, `v0-cha` 9, `tier2-cha` 14). A byte scan of the exe finds 121 write instructions (18 `add`, 103 `sub`) in the glue range `0xD9B18D`-`0xE37F5B`, all non-atomic. docs/122 §12 lists only `ParseFormatSpecifier 0xDA9790` as a writer: that holds inside its 78-body closure, not for the binder as a whole. A worker that runs any of these bodies races the sim thread's counter (docs/122 I-56-1). Fix shapes [design]: add these bodies to the F2 abort list, or give the worker its own counter (the binder table can be patched at install, docs/212 §fix shape).
3. **Fresh or shared [inferred].** The direct worker-reachable writers mostly write a receiver the caller made in the same scoring call (`StringBuilder`, result strings). A receiver that outlives the call is a real race: `Curve.Fx` writes `this.m_x` on a tuning curve shared by every Sim (reach `v0-cha`, 4 `Curve` rows reachable), and `PriorityQueue` bodies write the alarm queue (docs/212). The call sites decide; the next step is a fresh-receiver check per call site in the managed closures (`managed-callgraph.ts` already has `newobj` edges).
4. **Satb coverage.** The CSV is the list docs/212 §fix shape needs: 708 readers need nothing; the 321 writes-args and writes-other rows need sim-side logging or a worker stop; the 46 native-only rows write EA globals (mostly the IC counter) the worker must not touch; the 10 unknown rows need a stop (none is reachable today).
5. **Mods and the IL hash guard [verified].** Because each init compares an IL hash before binding (§2), a mod that ships its own copy of an EA assembly with a changed method body gets the managed method interpreted, not EA's native body. docs/218's mod guard can rely on that for changed methods; unchanged methods in a mod copy still bind natively.

## 5. Reproduce and limits

```
python tools/binder-bodies.py --hashes regs.txt
node tools/binder-bodies.ts --asm game-copy/assemblies --index game-copy/analysis/callgraph/index.json --hashes regs.txt --out facts.json
python tools/binder-bodies.py --facts facts.json
python tools/binder-bodies.py --self-check
node tools/binder-bodies.ts --self-check --asm game-copy/assemblies
```

The Python tool needs capstone 5 and a copy of the Steam `TS3W.exe` (`game-copy/bin/TS3W.exe`, or `--exe`, or `TS3W_EXE`); the full run takes about a minute. Its self-check runs a classifier fixture always (9 rows, including a dropped scalar candidate, the same store kept unknown without a real target, and an allocator store, plus the store-site and frame-widening rules) and, when the exe is there, checks 5 known bodies (`ElapsedTimeInMinutes`, `Curve.Fx`, `PriorityQueue.Add`, `SimClock.get_CurrentTicks`, `String.CopyTo` through its icall), one init reference and the `_setjmp3` arm test.

Limits:

- Static, one build (1.67 Steam). No body was run.
- The icall effects are a table from Mono 1.2.3 semantics and the EA signatures, not an analysis of the icall code [inferred].
- Calls through unresolved pointers are opaque; the row lists them as handoffs and drops to medium confidence. A medium reader may write through such a call.
- The analysis budget is 200,000 instructions per glue function and a quarter of that elsewhere; 49 rows hit it in a GC or allocator callee (`trunc:` handoffs). Those callees are runtime code.
- Merged paths can put an argument atom on a pointer it never was. Scalar-derived candidates are dropped only when the same store has a possible target; a store with only such candidates would show as unknown (none do now), never as a lower class.
- A store through a widened frame pointer is not counted and does not overwrite the frame slots it may hit; a frame slot that held a managed pointer before such a store keeps it.
- Managed results that come back through `NativeToManaged` are unknown; a body that stores into one stays unknown (the 10 rows of §3).
- The remoting stub and the `_setjmp3` arm pruning remove paths the game does not take in practice [inferred for remoting, verified scan for the arms]; a script that made a transparent proxy would invalidate the first.
- `unknown_hint` comes from a hand-read site (`0xD9CCA4`) or the first path to the store.
- The worker reach labels inherit docs/218's limits (static closures, overrides by class hierarchy unless `--typed` is given).
