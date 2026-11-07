// docs/537 §17: corpus cases added for the IL branch coverage bar (tools/jit-coverage.ts): the listed bodies and branch sides the
// generated corpus (JitCorpus.cs) does not reach. Hand-written, not generated. monohost runs these after the generated cases, the
// same way: twice interpreted, once compiled, hashes equal (HostTest.JitCorpusRun). C# 2.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace MonoHostTest
{
    public class SameHash { public override int GetHashCode() { return 7; } public override bool Equals(object o) { return ReferenceEquals(this, o); } }
    public static class JitCorpusGates
    {
        public const int Count = 21;
        static int M(int h, int v) { return unchecked(h * 31 + v); }
        static int X(int h, Exception e) { string n = e.GetType().Name; int x = 0; for (int i = 0; i < n.Length; i++) x = unchecked(x * 31 + n[i]); return M(h, x); }

        // an enumerator in each state: run out, read past the end, disposed, the collection changed under it
        static int Walk(int h, IEnumerator e)
        {
            try { object o = e.Current; h = M(h, o == null ? 0 : 1); } catch (Exception x) { h = X(h, x); }
            for (int i = 0; i < 64; i++) { bool more; try { more = e.MoveNext(); } catch (Exception x) { h = X(h, x); break; } h = M(h, more ? 1 : 0); if (!more) break; }
            try { object o = e.Current; h = M(h, o == null ? 0 : 1); } catch (Exception x) { h = X(h, x); }
            for (int i = 0; i < 2; i++) try { h = M(h, e.MoveNext() ? 1 : 0); } catch (Exception x) { h = X(h, x); }
            try { e.Reset(); h = M(h, e.MoveNext() ? 1 : 0); } catch (Exception x) { h = X(h, x); }
            return h;
        }
        static int Dead(int h, IEnumerator e)  // the caller disposed it, or changed the collection
        {
            try { h = M(h, e.MoveNext() ? 1 : 0); } catch (Exception x) { h = X(h, x); }
            try { object o = e.Current; h = M(h, o == null ? 0 : 1); } catch (Exception x) { h = X(h, x); }
            try { e.Reset(); } catch (Exception x) { h = X(h, x); }
            return h;
        }
        static int IndexOfs<T>(int h, T[] a, T v)
        {
            try { h = M(h, Array.IndexOf<T>(a, v)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf<T>(a, v, 1)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf<T>(a, v, -1)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf<T>(a, v, 99)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf<T>(a, v, 1, 2)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf<T>(a, v, 1, -1)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf<T>(a, v, 0, 99)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf((Array)a, (object)v)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf((Array)a, (object)v, 1)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf((Array)a, (object)v, -1)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf((Array)a, (object)v, 1, 2)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf((Array)a, (object)v, 1, -1)); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, Array.IndexOf((Array)a, (object)v, 0, 99)); } catch (Exception x) { h = X(h, x); }
            return h;
        }
        static int Dict<K>(int h, K[] keys, K[] absent)
        {
            Dictionary<K, int> d = new Dictionary<K, int>();
            for (int i = 0; i < keys.Length; i++) d[keys[i]] = i;
            int v;
            for (int i = 0; i < absent.Length; i++) { h = M(h, d.TryGetValue(absent[i], out v) ? v : -1); h = M(h, d.ContainsKey(absent[i]) ? 1 : 0); }
            EqualityComparer<K> c = EqualityComparer<K>.Default;
            h = M(h, c.Equals(keys[0], keys[0]) ? 1 : 0); h = M(h, c.Equals(keys[0], keys[1]) ? 1 : 0); h = M(h, c.Equals(default(K), keys[0]) ? 1 : 0);
            h = M(h, c.Equals(keys[0], default(K)) ? 1 : 0); h = M(h, c.Equals(default(K), default(K)) ? 1 : 0);
            try { h = M(h, c.GetHashCode(default(K)) == 0 ? 1 : 0); } catch (Exception x) { h = X(h, x); }
            Dictionary<K, int>.Enumerator e = d.GetEnumerator();
            h = Walk(h, e);
            e.Dispose();
            try { h = M(h, e.MoveNext() ? 1 : 0); } catch (Exception x) { h = X(h, x); }
            try { h = M(h, e.Current.Value); } catch (Exception x) { h = X(h, x); }
            Dictionary<K, int>.Enumerator e2 = d.GetEnumerator();
            h = M(h, e2.MoveNext() ? 1 : 0);
            d.Remove(keys[0]);
            try { h = M(h, e2.MoveNext() ? 1 : 0); } catch (Exception x) { h = X(h, x); }
            foreach (K k in d.Keys) h = M(h, 1);
            foreach (int w in d.Values) h = M(h, w);
            h = Walk(h, ((IEnumerable)d.Keys).GetEnumerator());
            h = Walk(h, ((IEnumerable)d.Values).GetEnumerator());
            return h;
        }
        static int Seq<T>(int h, IEnumerable<T> s, bool empty)
        {
            h = Walk(h, s.GetEnumerator());
            IEnumerator<T> e = s.GetEnumerator();
            e.Dispose();
            h = Dead(h, e);
            foreach (T v in s) h = M(h, 1);
            return h;
        }

        public static int Run(int c)
        {
            int h = 17 + c;
            switch (c)
            {
                case 0:
                {
                    RuntimeTypeHandle t = typeof(int).TypeHandle;
                    h = M(h, t.Value != IntPtr.Zero ? 1 : 0);
                    // the transform folds a direct call of get_Value into a field load (docs/537 §17), so enter the body by reflection
                    object v = typeof(RuntimeTypeHandle).GetProperty("Value").GetValue(t, null);
                    return M(h, (IntPtr)v != IntPtr.Zero ? 1 : 0);
                }
                case 1: return Dict<int>(h, new int[] { 1, 17, 33, 49, 65, 2, 3 }, new int[] { 4, 5, 81, 97, 113, 129, 145, 161 });
                case 2: return Dict<string>(h, new string[] { "a", "b", "c", "d", "e", "f", "g" }, new string[] { "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s" });
                case 3:
                {
                    List<int> l = new List<int>();
                    h = Seq<int>(h, l, true);
                    for (int i = 0; i < 9; i++) l.Add(i);
                    h = Seq<int>(h, l, false);
                    List<int>.Enumerator e = l.GetEnumerator();
                    h = M(h, e.MoveNext() ? 1 : 0); l.Add(1);
                    try { h = M(h, e.MoveNext() ? 1 : 0); } catch (Exception x) { h = X(h, x); }
                    List<int>.Enumerator e2 = l.GetEnumerator();
                    while (e2.MoveNext()) h = M(h, e2.Current);
                    h = M(h, e2.MoveNext() ? 1 : 0);
                    return h;
                }
                case 4:
                {
                    Queue<int> q = new Queue<int>(); h = Seq<int>(h, q, true);
                    for (int i = 0; i < 6; i++) q.Enqueue(i);
                    q.Dequeue(); q.Enqueue(7); h = Seq<int>(h, q, false);
                    IEnumerator<int> e = q.GetEnumerator(); e.MoveNext(); q.Enqueue(8); return Dead(h, e);
                }
                case 5:
                {
                    Stack<int> s = new Stack<int>(); h = Seq<int>(h, s, true);
                    for (int i = 0; i < 6; i++) s.Push(i);
                    h = Seq<int>(h, s, false);
                    IEnumerator<int> e = s.GetEnumerator(); e.MoveNext(); s.Push(8); return Dead(h, e);
                }
                case 6:
                {
                    SortedDictionary<int, int> d = new SortedDictionary<int, int>();
                    h = Seq<KeyValuePair<int, int>>(h, d, true); h = Seq<int>(h, d.Keys, true); h = Seq<int>(h, d.Values, true);
                    for (int i = 0; i < 6; i++) d[i * 3] = i;
                    h = Seq<KeyValuePair<int, int>>(h, d, false); h = Seq<int>(h, d.Keys, false); h = Seq<int>(h, d.Values, false);
                    IEnumerator<int> ek = d.Keys.GetEnumerator(), ev = d.Values.GetEnumerator(); ek.MoveNext(); ev.MoveNext();
                    IEnumerator<KeyValuePair<int, int>> e = d.GetEnumerator(); e.MoveNext(); d[99] = 1; h = Dead(h, ek); h = Dead(h, ev); return Dead(h, e);
                }
                case 7:
                {
                    SortedList<int, int> d = new SortedList<int, int>();
                    h = Seq<KeyValuePair<int, int>>(h, d, true);
                    for (int i = 0; i < 6; i++) d[i * 3] = i;
                    h = Seq<KeyValuePair<int, int>>(h, d, false);
                    IEnumerator<KeyValuePair<int, int>> e = d.GetEnumerator(); e.MoveNext(); d[99] = 1; h = Dead(h, e);
                    h = Walk(h, ((IDictionary)d).GetEnumerator()); h = Walk(h, ((IEnumerable)d.Keys).GetEnumerator());
                    IDictionaryEnumerator de = ((IDictionary)d).GetEnumerator(); de.MoveNext(); d[100] = 1;
                    return Dead(h, de);
                }
                case 8:
                {
                    LinkedList<int> l = new LinkedList<int>();
                    h = Seq<int>(h, l, true);
                    for (int i = 0; i < 6; i++) l.AddLast(i);
                    h = Seq<int>(h, l, false);
                    IEnumerator<int> e = l.GetEnumerator(); e.MoveNext(); l.AddLast(8); return Dead(h, e);
                }
                case 9:
                {
                    Hashtable t = new Hashtable(); h = Walk(h, t.GetEnumerator()); h = Walk(h, ((IEnumerable)t).GetEnumerator());
                    for (int i = 0; i < 6; i++) t[i * 3] = i;
                    h = Walk(h, t.GetEnumerator()); h = Walk(h, t.Keys.GetEnumerator()); h = Walk(h, t.Values.GetEnumerator());
                    IDictionaryEnumerator e = t.GetEnumerator(); e.MoveNext();
                    try { h = M(h, e.Key != null ? 1 : 0); h = M(h, e.Value != null ? 1 : 0); h = M(h, e.Entry.Key != null ? 1 : 0); } catch (Exception x) { h = X(h, x); }
                    t[new SameHash()] = 1; SameHash gone = new SameHash(); t[gone] = 2; t[new SameHash()] = 3; t.Remove(gone);  // a removed slot in a chain stays as a marker
                    h = Walk(h, t.GetEnumerator());
                    t[99] = 1; return Dead(h, e);
                }
                case 10:
                {
                    SortedList t = new SortedList(); h = Walk(h, t.GetEnumerator()); h = Walk(h, t.Keys.GetEnumerator());
                    for (int i = 0; i < 6; i++) t[i * 3] = i;
                    h = Walk(h, t.GetEnumerator()); h = Walk(h, t.Keys.GetEnumerator()); h = Walk(h, t.Values.GetEnumerator());
                    IDictionaryEnumerator e = t.GetEnumerator();
                    try { h = M(h, e.Key != null ? 1 : 0); } catch (Exception x) { h = X(h, x); }
                    try { h = M(h, e.Value != null ? 1 : 0); } catch (Exception x) { h = X(h, x); }
                    e.MoveNext(); t[99] = 1; return Dead(h, e);
                }
                case 11:
                {
                    Stack t = new Stack(); h = Walk(h, t.GetEnumerator());
                    for (int i = 0; i < 6; i++) t.Push(i);
                    h = Walk(h, t.GetEnumerator());
                    IEnumerator e = t.GetEnumerator(); e.MoveNext(); t.Push(1); return Dead(h, e);
                }
                case 12:
                {
                    h = IndexOfs<int>(h, new int[] { 4, 5, 6, 7 }, 6); h = IndexOfs<int>(h, new int[] { 4, 5, 6, 7 }, 9);
                    h = IndexOfs<string>(h, new string[] { "a", null, "b", "c" }, "b"); h = IndexOfs<string>(h, new string[] { "a", null, "b", "c" }, null);
                    h = IndexOfs<int>(h, null, 1);
                    try { h = M(h, Array.IndexOf(new int[2, 2], (object)1)); } catch (Exception x) { h = X(h, x); }
                    try { h = M(h, Array.IndexOf(new int[2, 2], (object)1, 0)); } catch (Exception x) { h = X(h, x); }
                    try { h = M(h, Array.IndexOf(new int[2, 2], (object)1, 0, 1)); } catch (Exception x) { h = X(h, x); }
                    Array nz = Array.CreateInstance(typeof(int), new int[] { 3 }, new int[] { 5 });
                    nz.SetValue(9, 6);
                    try { h = M(h, Array.IndexOf(nz, (object)9)); h = M(h, Array.IndexOf(nz, (object)1)); } catch (Exception x) { h = X(h, x); }
                    try { h = M(h, Array.IndexOf(nz, (object)9, 5)); h = M(h, Array.IndexOf(nz, (object)9, 5, 2)); h = M(h, Array.IndexOf(nz, (object)1, 5, 2)); } catch (Exception x) { h = X(h, x); }
                    try { h = M(h, Array.IndexOf(nz, (object)9, 1)); } catch (Exception x) { h = X(h, x); }
                    try { h = M(h, Array.IndexOf(nz, (object)9, 1, 1)); } catch (Exception x) { h = X(h, x); }
                    return h;
                }
                case 13:
                {
                    int[] a = { -2, 0, 3 };
                    for (int i = 0; i < a.Length; i++)
                        for (int j = 0; j < a.Length; j++)
                        {
                            int p = a[i], q = a[j];
                            h = M(h, Math.Min(p, q)); h = M(h, (int)Math.Min((long)p, (long)q)); h = M(h, (int)Math.Min((short)p, (short)q)); h = M(h, (int)Math.Min((sbyte)p, (sbyte)q));
                            h = M(h, (int)Math.Min((byte)i, (byte)j)); h = M(h, (int)Math.Min((uint)i, (uint)j)); h = M(h, (int)Math.Min((ulong)i, (ulong)j));
                            h = M(h, (int)Math.Min((decimal)p, (decimal)q)); h = M(h, (int)Math.Min((float)p, (float)q)); h = M(h, (int)Math.Min((double)p, (double)q));
                        }
                    h = M(h, float.IsNaN(Math.Min(float.NaN, 1f)) ? 1 : 0); h = M(h, float.IsNaN(Math.Min(1f, float.NaN)) ? 1 : 0);
                    h = M(h, double.IsNaN(Math.Min(double.NaN, 1.0)) ? 1 : 0); h = M(h, double.IsNaN(Math.Min(1.0, double.NaN)) ? 1 : 0);
                    return h;
                }
                case 14:
                {
                    Vector3 a = new Vector3(1f, 2f, 3f), b = new Vector3(a), c3 = new Vector3(new Vector2(4f, 5f)), d = new Vector3(new Vector4(6f, 7f, 8f, 9f));
                    return M(M(M(M(h, (int)a.x), (int)b.y), (int)c3.x), (int)d.z);
                }
                case 15:  // every pair of flag bits, and the flags alone (the lookup is one long chain of bit tests)
                    for (uint s = 0; s < 64; s++)      // the species bits 0xCF00, every subset
                        for (uint a = 0; a < 128; a++)  // the age bits 0x7F
                        {
                            uint f = a | (s & 15) << 8 | (s >> 4) << 14;
                            h = M(h, (int)CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags((CASAgeGenderFlags)(f | (a & 3) << 12 | (a >> 2 & 3) << 20)));
                        }
                    return h;
                case 16:
                {
                    // internal to ScriptCore: reached by reflection
                    Type t = null;
                    foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (t == null) t = a.GetType("ScriptCore.Utility.ChunkedList`1");
                    t = t.MakeGenericType(new Type[] { typeof(int) });
                    object l = Activator.CreateInstance(t);
                    System.Reflection.MethodInfo add = t.GetMethod("Add"), en = t.GetMethod("GetEnumerator");
                    h = Walk(h, (IEnumerator)en.Invoke(l, null));
                    for (int i = 0; i < 2100; i++) add.Invoke(l, new object[] { i });
                    return Walk(h, (IEnumerator)en.Invoke(l, null));
                }
                case 17:
                {
                    Match m = new Regex("[a-c0-9x]+[^d-f]").Match("zzab19xq");
                    return M(M(h, m.Index), m.Length);
                }
                case 18:
                {
                    XmlDocument d = new XmlDocument();
                    d.LoadXml("<r><a/><b/><c>t</c></r>");
                    h = Walk(h, d.DocumentElement.ChildNodes.GetEnumerator());
                    h = Walk(h, d.DocumentElement.LastChild.LastChild.ChildNodes.GetEnumerator());
                    h = Walk(h, d.DocumentElement.FirstChild.ChildNodes.GetEnumerator());
                    IEnumerator e = d.DocumentElement.ChildNodes.GetEnumerator();
                    e.MoveNext(); d.DocumentElement.RemoveChild(d.DocumentElement.FirstChild);
                    return Dead(h, e);
                }
                case 19:
                {
                    // internal to System: the empty collection walks both sides of the end test
                    Type t = null;
                    foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (t == null) t = a.GetType("System.Text.RegularExpressions.IntervalCollection");
                    return Walk(h, ((IEnumerable)Activator.CreateInstance(t)).GetEnumerator());
                }
                case 20:
                {
                    // the topic lists hold nulls: the enumerator only moves through them
                    SimActiveTopics s = (SimActiveTopics)Activator.CreateInstance(typeof(SimActiveTopics), true);
                    foreach (System.Reflection.FieldInfo fi in typeof(SimActiveTopics).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                        if (fi.FieldType == typeof(List<ActiveTopic>)) { List<ActiveTopic> l = new List<ActiveTopic>(); l.Add(null); l.Add(null); fi.SetValue(s, l); }
                    return Walk(h, ((IEnumerable)s).GetEnumerator());
                }
            }
            return h;
        }
    }
}
