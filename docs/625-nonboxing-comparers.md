# 625: Non-boxing comparers for enum and AlarmHandle keys; HasAnyElement skipped

2026-11-17, s87, static, NOT RUN (no game launch). Package `ts3fixes.r77-alloc` (Roslyn build). Leads: docs/618 §2.2 #1,
#2, #3, #5 and docs/621 idea A1. Both switches are default off. Code: `mods/ts3fixes/src/Alloc87Comparers.cs`.
Counters 3550-3558 (3559-3579 reserved spare).

## 1. Root cause [verified: ilspycmd on game-copy/assemblies]

- `mscorlib EqualityComparer<T>..cctor`: `_default` (a `private static initonly` field) is `IEquatableOfTEqualityComparer<T>`
  when `T : IEquatable<T>`, else the private `DefaultComparer`. `DefaultComparer.Equals(x, y)` is `x?.Equals(y) ?? y == null`
  (box x for the null test, box y, `constrained.` call of `Object::Equals`). `GetHashCode` is a `constrained.` call of `Object::GetHashCode`.
- `System.Enum.Equals` = `ValueType.DefaultEquals(this, obj)`, `Enum.GetHashCode` = `get_value().GetHashCode()`. An enum
  type overrides neither, so the receiver is boxed too: 3 boxes per compare and 2 per hash (docs/618 #1).
- `Dictionary.Init` stores `EqualityComparer<TKey>.Default` in `hcp`. `List<T>.Contains` reads `Default` on every call.
- `AlarmHandle` (struct, `mLotId` ulong, `mId` uint) has no `IEquatable<AlarmHandle>`. Its `Equals(object)` is a null test,
  `GetType()` on both, then `==`. Its hash is `(int)(mId ^ (mLotId ^ (mLotId >> 32)))`. `AlarmManager.mTimers` is a
  `Dictionary<AlarmHandle, List<Timer>>` built with the default comparer.
- `ObjectGuid` implements `IEquatable<ObjectGuid>`, so its dictionaries already use the non-boxing comparer. Nothing to fix there.
- `MonoField.SetValue` in this mscorlib refuses only `IsLiteral` fields, so reflection can set `_default`. No `[IlPatch]` spec
  and no change at a constructor are needed.

## 2. `enumcmpnobox` (3550-3553)

When the switch is on, `_default` for CommodityKind (int), BuffNames (ulong) and SkillNames (ulong) is set to a sealed comparer per type:
`Equals` is `x == y`, and `GetHashCode` is `(int)v` for an int enum or `(int)(v & 0xFFFFFFFF) ^ (int)(v >> 32)` for a ulong enum.
These are `Int32.GetHashCode` and `UInt64.GetHashCode` of this mscorlib, which is what `Enum.GetHashCode` returns through `get_value`.
The hash is the same, so keys land in the same buckets and iterate in the same order. Equality is the same, so lookups give the
same answers. When the switch is off, EA's instance (read before the first set) goes back.

The sync runs at app startup (`ApplyTuned`, before any save loads), in `SwitchesChanged` (agent toggles, world load) and in the
Check. Dictionaries built while the switch is on keep our comparer after it goes off, which is exact either way. Dictionaries
built before keep EA's comparer.

## 3. `alarmcmpnobox` (3554-3558)

Built the same way for `AlarmHandle`. `Equals` is EA's `operator ==` and `GetHashCode` is EA's override, called through
`constrained.` on a struct that overrides it. Whether mint emits `con.call` (no box) or `con.boxcallvirt` for it is
[inferred] (docs/213 row 3). The Check measures this, so if it boxes, the on arm reads UNEXPECTED, not FIXED. Also, the
live `AlarmManager.mTimers` (`Global` plus each lot's) get their `hcp` swapped in place between EA's comparer and ours, and
nothing else. This is valid because every key hashes the same under both. This replaces docs/618 option A (which drops only
the `GetType` box) with option B (all of the boxes).

## 4. Skipped: `BuffManager.HasAnyElement` params arrays (docs/618 #3)

- The rate in docs/618 is about 9x too high. The callcensus line is cumulative: `calls 66035 in 9.00 sim-h, per sim-hour 7336`,
  so the rate is 7.3k calls per sim-hour, about 2.8 arrays per tick, not about 25.
- The array is built in the caller's IL before any hook runs, and callcensus has no split by caller, so the top call
  sites are unknown.
- There are only two exact routes. One is an `[IlPatch]` spec per caller, which is ts3native: it needs an MSVC build and an
  ini switch, not the ts3fixes build. The other is a ts3fixes copy of each EA caller method, and EA code is never committed.
- Next step if it is wanted: a caller split in callcensus (return-address or a stack-frame sample on `CcBuffs.AnyHook`),
  then one `idleparams`-style spec for the top caller.

## 5. Save check [verified]

- ScriptCore `BCLDefaultFormatter.CreatePersistFormatter` maps `Dictionary<,>` to `DictionaryFormatter<K,V>`
  (`IDictionaryFormatter`). Its `Write` writes the count and then each key and value. Its `Precreate` is `new T()`. So no
  comparer is written into a save, and a loaded dictionary gets whatever `Default` is when it loads.
- `Dictionary.GetObjectData` writes `hcp`, but only for `BinaryFormatter`, and no EA assembly uses `BinaryFormatter`.
- No EA class derives from `Dictionary`. No EA class has an `IEqualityComparer` field, and no EA code reads `.Comparer`
  (grep over game-copy/decompiled).
- `AlarmManager` is `IPersistable`. Its import rebuilds `mTimers` from its timer list.

## 6. Checks (repros `enumcmpnobox-check`, `alarmcmpnobox-check`)

`EnumCmpNoBoxCheck` and `AlarmCmpNoBoxCheck` work as follows for `run`:
1. They build a dictionary and a list with the current `Default`.
2. They count the bytes allocated per hit lookup + miss lookup + `List.Contains`. The count comes from Boehm `GC_words_allocd`,
   read through `Marshal.ReadInt32` at memexp.cpp's addresses. It is read only when the exe's PE stamp and size are Steam 1.67
   or EA 1.69.47, and `GC_gc_no` equals `GC.CollectionCount(0)`. A try that a collection interrupts is retried, up to 4 times.
3. They compare EA's comparer with ours on every key pair: hash, equality, and the key order of two dictionaries filled
   with the same adds, removes and re-adds. The keys include ulong values with high bits set.

The verdict comes from the counted bytes: off with bytes > 0 is BROKEN, and on with 0 bytes is FIXED. When the counter cannot
be read, it falls back to whether our comparer is installed. Controls: the answer differences must be 0 (counters 3553 and 3558).
The alarm Check also needs every live `mTimers` to be on our comparer. Expected: off, about 5 boxes per enum call (2 hash +
3 compare on the hit, plus the Contains); on, 0.

## 7. Not done

- No run. The gain is not measured (use gc-rate / `SqrtHeapLimit=2` `gcsize:` as the before/after metric, docs/618 §3).
- docs/618's other enum key types (FutureWeather, RoleType, DaysOfTheWeek, WalkStyle) are not included. Adding each one takes
  a comparer class and a slot entry.
