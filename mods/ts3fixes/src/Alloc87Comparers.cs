using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // Non-boxing equality comparers (docs/625, s87; leads docs/618 §2.2 #1 #2 #5, docs/621 idea A1). Both default off.
    //
    // Root cause [verified, ilspycmd on game-copy/assemblies/mscorlib.dll]: EqualityComparer<T>..cctor picks
    // IEquatableOfTEqualityComparer only when T implements IEquatable<T>, else the private DefaultComparer, whose
    // Equals(x, y) is `box x; brfalse; box x; box y; callvirt Object::Equals` and GetHashCode(x) is `constrained. !T
    // callvirt Object::GetHashCode`. An enum overrides neither method itself (System.Enum does: Equals ->
    // ValueType.DefaultEquals, GetHashCode -> get_value().GetHashCode()), so a key compare boxes 3 times and a hash 2 times.
    // AlarmHandle (Sims3GameplaySystems) has no IEquatable<AlarmHandle>, so AlarmManager.mTimers boxes the same way, and its
    // Equals(object) adds GetType(). Dictionary.Init stores EqualityComparer<TKey>.Default in `hcp`; List<T>.Contains reads
    // Default per call. ObjectGuid implements IEquatable<ObjectGuid>: no box, nothing to fix.
    //
    // Fix: `_default` is `private static initonly`; MonoField.SetValue in this mscorlib refuses only literals, so the field
    // is set at runtime to a sealed comparer per type whose Equals and GetHashCode give the defaults' exact answers:
    //   int enum (CommodityKind): Int32.GetHashCode = the value;
    //   ulong enums (BuffNames, SkillNames): UInt64.GetHashCode = (int)(v & 0xFFFFFFFF) ^ (int)(v >> 32);
    //   AlarmHandle: EA's own operator == and its GetHashCode override (no box: the struct overrides it).
    // Same hash, so the same buckets and iteration order; same equality, so the same lookups. Dictionaries built while it
    // is set keep it (exact either way); dictionaries built before keep EA's comparer, except the live AlarmManager mTimers,
    // whose `hcp` is swapped in place (valid: every key hashes the same). Switch off puts EA's instances back.
    //
    // Save check [verified]: ScriptCore BCLDefaultFormatter maps Dictionary<,> to DictionaryFormatter (IDictionaryFormatter:
    // Write = count + key/value fields, Precreate = `new T()`), so no comparer reaches a save and a loaded dictionary gets
    // whatever Default is then. Dictionary.GetObjectData writes "hcp" only for BinaryFormatter, which no EA assembly uses; no
    // EA class derives from Dictionary or holds an IEqualityComparer field. AlarmManager is IPersistable and rebuilds mTimers
    // from its timer list at import.
    //
    // Counters: [3550] enum comparers installed, [3551] restored, [3552] errors, [3553] answer mismatches in the Check;
    //           [3554] AlarmHandle comparer installed, [3555] restored, [3556] mTimers swapped in place, [3557] errors,
    //           [3558] answer mismatches in the Check.
    public static partial class Fixes
    {
        // ---- the comparers ------------------------------------------------------------------------------------------------
        sealed class A87Commodity : EqualityComparer<CommodityKind>
        {
            public override bool Equals(CommodityKind x, CommodityKind y) { return x == y; }
            public override int GetHashCode(CommodityKind obj) { return (int)obj; }
        }
        sealed class A87Buff : EqualityComparer<BuffNames>
        {
            public override bool Equals(BuffNames x, BuffNames y) { return x == y; }
            public override int GetHashCode(BuffNames obj) { ulong v = (ulong)obj; return (int)(v & 0xFFFFFFFFu) ^ (int)(v >> 32); }
        }
        sealed class A87Skill : EqualityComparer<SkillNames>
        {
            public override bool Equals(SkillNames x, SkillNames y) { return x == y; }
            public override int GetHashCode(SkillNames obj) { ulong v = (ulong)obj; return (int)(v & 0xFFFFFFFFu) ^ (int)(v >> 32); }
        }
        sealed class A87Alarm : EqualityComparer<AlarmHandle>
        {
            public override bool Equals(AlarmHandle x, AlarmHandle y) { return x == y; }
            public override int GetHashCode(AlarmHandle obj) { return obj.GetHashCode(); }
        }

        // One swappable Default: the field, EA's instance (read before the first set) and ours.
        sealed class A87Slot
        {
            public readonly string Name;
            public readonly FieldInfo Field;
            public readonly object Ours;
            public object Ea;
            public A87Slot(string name, Type t, object ours)
            {
                Name = name; Ours = ours;
                Type ec = typeof(EqualityComparer<>).MakeGenericType(t);
                Field = ec.GetField("_default", kStaticAll);
                if (Field != null) Ea = ec.GetProperty("Default", kStaticAll).GetValue(null, null);  // runs the cctor first
            }
            public bool Ok { get { return Field != null && Ea != null && Ea.GetType().Name == "DefaultComparer"; } }
            public bool Installed { get { return Field != null && ReferenceEquals(Field.GetValue(null), Ours); } }
            public void Set(bool on) { Field.SetValue(null, on ? Ours : Ea); }
        }

        static A87Slot[] sA87Enum;
        static A87Slot sA87Alarm;
        static string sA87Missing = "", sA87AlarmMissing = "";
        static FieldInfo sA87Timers, sA87Hcp;
        static bool sA87EnumOn, sA87AlarmOn;

        static void A87Init()
        {
            if (sA87Enum != null) return;
            sA87Enum = new A87Slot[] {
                new A87Slot("CommodityKind", typeof(CommodityKind), new A87Commodity()),
                new A87Slot("BuffNames", typeof(BuffNames), new A87Buff()),
                new A87Slot("SkillNames", typeof(SkillNames), new A87Skill()) };
            sA87Alarm = new A87Slot("AlarmHandle", typeof(AlarmHandle), new A87Alarm());
            string m = "";
            foreach (A87Slot s in sA87Enum) if (!s.Ok) m = EpJoin(m, "EqualityComparer<" + s.Name + ">._default/DefaultComparer");
            sA87Missing = m;
            sA87Timers = typeof(AlarmManager).GetField("mTimers", kInstAll);
            sA87Hcp = sA87Timers == null ? null : sA87Timers.FieldType.GetField("hcp", kInstAll);
            m = sA87Alarm.Ok ? "" : "EqualityComparer<AlarmHandle>._default/DefaultComparer";
            if (sA87Timers == null) m = EpJoin(m, "AlarmManager.mTimers");
            if (sA87Hcp == null) m = EpJoin(m, "Dictionary.hcp");
            sA87AlarmMissing = m;
        }

        static void ApplyAlloc87()
        {
            try
            {
                A87Init();
                AppliedLog().Add("enumcmpnobox: " + (sA87Missing.Length == 0 ? "ok (EqualityComparer<T>._default, 3 enum types)" : "not found: " + sA87Missing));
                AppliedLog().Add("alarmcmpnobox: " + (sA87AlarmMissing.Length == 0 ? "ok (EqualityComparer<AlarmHandle>._default, mTimers.hcp)" : "not found: " + sA87AlarmMissing));
            }
            catch (Exception e) { sA87Missing = sA87AlarmMissing = "init threw " + e.GetType().Name; PatchCounters()[3552]++; }
        }

        // Called from SwitchesChanged (agent toggles, world load) and ApplyTuned (app startup, before any save loads), and by
        // the Checks: puts in or takes out the comparers so the switches' state holds. Cheap when nothing changed.
        internal static void Alloc87Sync()
        {
            if (sA87Enum == null) return;
            int[] c = PatchCounters();
            bool on = sA87Missing.Length == 0 && ShortcutOn("enumcmpnobox");
            if (on != sA87EnumOn)
            {
                try
                {
                    foreach (A87Slot s in sA87Enum) s.Set(on);
                    sA87EnumOn = on;
                    c[on ? 3550 : 3551]++;
                }
                catch (Exception) { c[3552]++; }
            }
            on = sA87AlarmMissing.Length == 0 && ShortcutOn("alarmcmpnobox");
            if (on != sA87AlarmOn)
            {
                try
                {
                    sA87Alarm.Set(on);
                    sA87AlarmOn = on;
                    c[on ? 3554 : 3555]++;
                    c[3556] += A87Rehome(on);
                }
                catch (Exception) { c[3557]++; }
            }
        }

        // Swaps `hcp` of the live AlarmManagers' mTimers between EA's comparer and ours (only those two: anything else is
        // left alone). Exact: both hash every key the same, so the buckets stay valid.
        static int A87Rehome(bool on)
        {
            object from = on ? sA87Alarm.Ea : sA87Alarm.Ours, to = on ? sA87Alarm.Ours : sA87Alarm.Ea;
            int n = 0;
            foreach (AlarmManager am in A87Managers())
            {
                object d = sA87Timers.GetValue(am);
                if (d != null && ReferenceEquals(sA87Hcp.GetValue(d), from)) { sA87Hcp.SetValue(d, to); n++; }
            }
            return n;
        }

        // The global AlarmManager and every lot's own one, each once (a lot without its own returns Global).
        static ICollection<AlarmManager> A87Managers()
        {
            Dictionary<AlarmManager, bool> seen = new Dictionary<AlarmManager, bool>();
            if (AlarmManager.Global != null) seen[AlarmManager.Global] = true;
            ICollection lots = LotManager.AllLots;
            if (lots != null)
                foreach (object o in lots)
                {
                    Lot lot = o as Lot;
                    AlarmManager am = lot == null ? null : lot.AlarmManager;
                    if (am != null) seen[am] = true;
                }
            return seen.Keys;
        }

        // ---- the allocation counter: Boehm's GC_words_allocd (words since the last collection) -----------------------------
        // Addresses as ts3native memexp.cpp reads them (Steam 1.67; EA app 1.69.47 through ts3addr-ea169.h); used only when
        // the exe's PE stamp and size are one of those builds and GC_gc_no equals GC.CollectionCount(0).
        static int sA87Words = -2, sA87GcNo;

        static bool A87CounterOk()
        {
            if (sA87Words == -2)
            {
                sA87Words = -1;
                try
                {
                    int nt = 0x400000 + Marshal.ReadInt32(new IntPtr(0x40003C));
                    int stamp = Marshal.ReadInt32(new IntPtr(nt + 8)), size = Marshal.ReadInt32(new IntPtr(nt + 0x50));
                    if (stamp == 0x52DEC247 && size == 0x00E5A000) { sA87Words = 0x012225C4; sA87GcNo = 0x011F2F90; }
                    else if (stamp == 0x6707155C && size == 0x00EB5000) { sA87Words = 0x0127C944; sA87GcNo = 0x0124D020; }
                    if (sA87Words > 0 && Marshal.ReadInt32(new IntPtr(sA87GcNo)) != GC.CollectionCount(0)) sA87Words = -1;
                }
                catch (Exception) { sA87Words = -1; }
            }
            return sA87Words > 0;
        }

        static int A87GcNo() { return Marshal.ReadInt32(new IntPtr(sA87GcNo)); }
        static int A87WordsNow() { return Marshal.ReadInt32(new IntPtr(sA87Words)); }

        // ---- the Check bodies ---------------------------------------------------------------------------------------------
        const int kA87Calls = 4000;

        // Bytes allocated per lookup + Contains pair on a dictionary and list built now (so with today's Default); -1 when the
        // counter is unreadable or a collection hit every one of 4 tries.
        static int A87BytesPerCall<T>(T[] keys, T miss)
        {
            if (!A87CounterOk() || keys.Length == 0) return -1;
            Dictionary<T, int> d = new Dictionary<T, int>();
            List<T> l = new List<T>();
            for (int i = 0; i < keys.Length; i++) { d[keys[i]] = i; if (i < 8) l.Add(keys[i]); }
            T probe = keys[keys.Length / 2];
            int v;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int g0 = A87GcNo(), w0 = A87WordsNow();
                for (int i = 0; i < kA87Calls; i++) { d.TryGetValue(probe, out v); d.TryGetValue(miss, out v); l.Contains(probe); }
                int w1 = A87WordsNow();
                if (A87GcNo() == g0 && w1 >= w0) return (int)((long)(w1 - w0) * 4 / kA87Calls);
            }
            return -1;
        }

        // Same answers: Equals on every pair, GetHashCode on every key, and the same key order from two dictionaries filled the
        // same way (one with each comparer, with removes and re-adds). Returns the number of differences.
        static int A87Compare<T>(T[] keys, IEqualityComparer<T> ea, IEqualityComparer<T> ours)
        {
            int bad = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                if (ea.GetHashCode(keys[i]) != ours.GetHashCode(keys[i])) bad++;
                for (int j = 0; j < keys.Length; j++) if (ea.Equals(keys[i], keys[j]) != ours.Equals(keys[i], keys[j])) bad++;
            }
            Dictionary<T, int> a = new Dictionary<T, int>(ea), b = new Dictionary<T, int>(ours);
            for (int i = 0; i < keys.Length; i++) { a[keys[i]] = i; b[keys[i]] = i; }
            for (int i = 0; i < keys.Length; i += 3) { a.Remove(keys[i]); b.Remove(keys[i]); }
            for (int i = keys.Length - 1; i >= 0; i -= 5) { a[keys[i]] = -i; b[keys[i]] = -i; }
            if (a.Count != b.Count) bad++;
            List<T> ka = new List<T>(a.Keys), kb = new List<T>(b.Keys);
            for (int i = 0; i < ka.Count && i < kb.Count; i++) if (!ea.Equals(ka[i], kb[i]) || a[ka[i]] != b[kb[i]]) bad++;
            return bad;
        }

        // The enum keys of a type plus values with high bits set (the ulong hash folds them), without repeats.
        static T[] A87Keys<T>(ulong[] extra)
        {
            List<T> l = new List<T>();
            foreach (object o in Enum.GetValues(typeof(T))) { T t = (T)o; if (!l.Contains(t)) l.Add(t); if (l.Count >= 48) break; }
            foreach (ulong x in extra)
            {
                T t = (T)Enum.ToObject(typeof(T), x);
                if (!l.Contains(t)) l.Add(t);
            }
            return l.ToArray();
        }

        static readonly ulong[] kA87Wide = { 0UL, 1UL, 0xFFFFFFFFUL, 0x100000000UL, 0x100000001UL, 0x8000000000000000UL, 0xFFFFFFFFFFFFFFFFUL, 0x123456789ABCDEF0UL };
        static readonly ulong[] kA87Narrow = { 0UL, 1UL, 0x7FFFFFFFUL };

        static string A87Bytes(int b) { return b < 0 ? "n/a" : b + " B (~" + ((b + 15) / 16) + " boxes)"; }

        // EnumCmpNoBoxCheck run: per type, bytes allocated per (hit lookup + miss lookup + List.Contains) with the comparer
        // Default gives now, and the answers of EA's comparer vs ours. Off: EA's DefaultComparer boxes (bytes > 0) = BROKEN;
        // on: 0 bytes = FIXED. Controls: zero answer differences, and the per-call count readable (else the installed
        // comparer type decides).
        public static object EnumCmpNoBoxCheck(string what)
        {
            A87Init();
            Alloc87Sync();
            string counters = B582Counters("enumcmpnobox", 3550, new[] { "installed", "restored", "errors", "answer mismatches" }, sA87Missing);
            if (what == "result") return counters;
            if (what != "run") return "enumcmpnobox <run|result>";
            bool on = ShortcutOn("enumcmpnobox");
            if (sA87Missing.Length > 0) return B582Line("enumcmpnobox", on, "no boxes", false, "n/a", false, sA87Missing, counters);
            int diff = 0, worst = 0;
            bool counted = true, installed = true;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                CommodityKind[] ck = A87Keys<CommodityKind>(kA87Narrow);
                BuffNames[] bn = A87Keys<BuffNames>(kA87Wide);
                SkillNames[] sn = A87Keys<SkillNames>(kA87Wide);
                diff += A87Compare(ck, (IEqualityComparer<CommodityKind>)sA87Enum[0].Ea, (IEqualityComparer<CommodityKind>)sA87Enum[0].Ours);
                diff += A87Compare(bn, (IEqualityComparer<BuffNames>)sA87Enum[1].Ea, (IEqualityComparer<BuffNames>)sA87Enum[1].Ours);
                diff += A87Compare(sn, (IEqualityComparer<SkillNames>)sA87Enum[2].Ea, (IEqualityComparer<SkillNames>)sA87Enum[2].Ours);
                int[] b = { A87BytesPerCall(ck, (CommodityKind)0x7FFFFFFE), A87BytesPerCall(bn, (BuffNames)0xFFFFFFFFFFFFFFFEUL), A87BytesPerCall(sn, (SkillNames)0xFFFFFFFFFFFFFFFEUL) };
                for (int i = 0; i < 3; i++)
                {
                    if (b[i] < 0) counted = false; else if (b[i] > worst) worst = b[i];
                    installed &= sA87Enum[i].Installed;
                    sb.Append(i == 0 ? "" : ", ").Append(sA87Enum[i].Name).Append(' ').Append(A87Bytes(b[i]));
                }
            }
            catch (Exception e) { PatchCounters()[3552]++; return "enumcmpnobox check threw " + e.GetType().Name + ": " + e.Message + "; " + counters; }
            PatchCounters()[3553] += diff;
            bool fixedNow = counted ? worst == 0 : installed;
            return B582Line("enumcmpnobox", on, "per call (hit + miss lookup + List.Contains, " + (counted ? "counted by GC_words_allocd" : "not counted: comparer type") + "): " + sb
                + "; installed=" + installed + "; no boxes", fixedNow, "EA vs ours answer differences=" + diff + " (hash, equality, key order)", diff == 0, "", counters);
        }

        // AlarmCmpNoBoxCheck run: the same for AlarmHandle keys (lot ids with high bits, ulong.MaxValue), plus how many live
        // AlarmManager mTimers carry our comparer now.
        public static object AlarmCmpNoBoxCheck(string what)
        {
            A87Init();
            Alloc87Sync();
            string counters = B582Counters("alarmcmpnobox", 3554, new[] { "installed", "restored", "mTimers swapped", "errors", "answer mismatches" }, sA87AlarmMissing);
            if (what == "result") return counters;
            if (what != "run") return "alarmcmpnobox <run|result>";
            bool on = ShortcutOn("alarmcmpnobox");
            if (sA87AlarmMissing.Length > 0) return B582Line("alarmcmpnobox", on, "no boxes", false, "n/a", false, sA87AlarmMissing, counters);
            int diff, bytes, live = 0, liveOurs = 0;
            try
            {
                List<AlarmHandle> l = new List<AlarmHandle>();
                foreach (ulong lot in kA87Wide) for (uint id = 0; id < 6; id++) l.Add(new AlarmHandle(lot, id * 0x10001u));
                l.Add(new AlarmHandle(0x0000000500000005UL, 0u));  // hash of mLotId folds to 0, as for kInvalidHandle
                AlarmHandle[] keys = l.ToArray();
                diff = A87Compare(keys, (IEqualityComparer<AlarmHandle>)sA87Alarm.Ea, (IEqualityComparer<AlarmHandle>)sA87Alarm.Ours);
                bytes = A87BytesPerCall(keys, new AlarmHandle(0xFFFFFFFFFFFFFFFEUL, 7u));
                foreach (AlarmManager am in A87Managers())
                {
                    object d = sA87Timers.GetValue(am);
                    if (d == null) continue;
                    live++;
                    if (ReferenceEquals(sA87Hcp.GetValue(d), sA87Alarm.Ours)) liveOurs++;
                }
            }
            catch (Exception e) { PatchCounters()[3557]++; return "alarmcmpnobox check threw " + e.GetType().Name + ": " + e.Message + "; " + counters; }
            PatchCounters()[3558] += diff;
            bool installed = sA87Alarm.Installed;
            bool fixedNow = (bytes >= 0 ? bytes == 0 : installed) && liveOurs == live;
            return B582Line("alarmcmpnobox", on, "per call (hit + miss lookup + List.Contains, " + (bytes >= 0 ? "counted by GC_words_allocd" : "not counted: comparer type") + "): "
                + A87Bytes(bytes) + "; installed=" + installed + "; live mTimers on ours " + liveOurs + "/" + live + "; no boxes", fixedNow,
                "EA vs ours answer differences=" + diff + " (hash, equality, key order)", diff == 0, "", counters);
        }
    }
}
