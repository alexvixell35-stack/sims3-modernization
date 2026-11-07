// Managed half of the argument replay (docs/537 §17, native half monohost-jitreplay.cpp, MONOHOST_JITREPLAY=<file from tools/jit-replay.ts>).
// Native code writes the captured bytes; this file resolves types and methods against the game DLLs, allocates, invokes and hashes the
// result. Reflection reaches private methods and closed generics by metadata token. Fixture classes at the end: JitReplayFix (the list
// entry "JitReplayFix.*"), used by native/monohost/jit-replay-fixture.json.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace MonoHostTest
{
    public static class JitReplay
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        static readonly List<Type> s_types = new List<Type>();
        static readonly List<string> s_typeWhy = new List<string>();
        static readonly List<MethodBase> s_meth = new List<MethodBase>();
        static readonly List<string> s_methWhy = new List<string>();
        static readonly List<FieldInfo> s_static = new List<FieldInfo>();
        public static string Why;            // the last refusal, read by the native side
        static MethodBase s_m;
        static object s_self;
        static object[] s_args, s_argsIn;
        static FieldInfo[] s_sf;
        static object[] s_sv;
        static int s_sn;
        public static object Keep;           // what the last Run returned: the native hash follows it

        // ---- the header lines of the replay file, handed over by the native reader: this mscorlib has no file or environment API,
        // and native code has no string constructor here, so names travel as ASCII through this buffer ----
        static readonly byte[] s_buf = new byte[4096];
        public static object BufNow() { return s_buf; }
        static string Text(int n) { char[] c = new char[n]; for (int i = 0; i < n; i++) c[i] = (char)s_buf[i]; return new string(c); }
        public static void AddTypeN(int n) { AddType(Text(n)); }
        public static void AddStaticN(int type, int n) { AddStatic(type, Text(n)); }
        static void AddType(string name)
        {
            Why = null;
            Type t = null;
            try { t = Parse(name); } catch (Exception e) { Why = "type: " + e.GetType().Name; }
            s_types.Add(t);
            s_typeWhy.Add(t == null ? (Why ?? ("no type " + (name.Length > 70 ? name.Substring(name.Length - 70) : name))) : Refuse(t));
        }
        // what cannot be rebuilt from bytes: a native pointer means nothing in another process, a delegate holds one, a Type is a runtime handle
        static string Refuse(Type t)
        {
            if (t.IsArray) return t.GetArrayRank() > 1 ? "multi-dimensional array" : Refuse(t.GetElementType());
            if (typeof(Delegate).IsAssignableFrom(t)) return "delegate";
            if (typeof(Type).IsAssignableFrom(t) || typeof(MemberInfo).IsAssignableFrom(t)) return "reflection object";
            if (t.FullName == "System.Threading.Thread") return "thread";
            if (t == typeof(string) || t.IsPrimitive && t != typeof(IntPtr) && t != typeof(UIntPtr)) return null;
            if (t == typeof(IntPtr) || t == typeof(UIntPtr)) return "native pointer field";
            for (Type c = t; c != null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType)
                foreach (FieldInfo f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Type ft = f.FieldType;
                    if (ft == typeof(IntPtr) || ft == typeof(UIntPtr) || ft.IsPointer) return "native pointer field";
                    if (ft.IsValueType && !ft.IsPrimitive && !ft.IsEnum && ft != t) { string w = Refuse(ft); if (w != null) return w; }
                }
            return null;
        }
        // Ns.Outer`2+Inner[[Arg],[Arg]][] as the capture writes it (a generic nested type lists the outer's arguments)
        static Type Parse(string s)
        {
            if (s.EndsWith("[]")) { Type e = Parse(s.Substring(0, s.Length - 2)); return e == null ? null : e.MakeArrayType(); }
            int b = s.IndexOf("[[");
            if (b < 0) return Find(s);
            Type open = Find(s.Substring(0, b));
            if (open == null) return null;
            List<Type> args = new List<Type>();
            int depth = 0, from = -1;
            for (int i = b + 1; i < s.Length - 1; i++)
            {
                if (s[i] == '[') { if (depth++ == 0) from = i + 1; }
                else if (s[i] == ']' && --depth == 0)
                {
                    Type a = Parse(s.Substring(from, i - from));
                    if (a == null) return null;
                    args.Add(a);
                }
            }
            return open.MakeGenericType(args.ToArray());
        }
        static readonly string[] s_game = { "Sims3GameplaySystems", "Sims3GameplayObjects", "Sims3StoreObjects", "Sims3Metadata", "UI", "SimIFace", "ScriptCore", "System", "System.Xml" };
        static bool s_loaded;
        static Type FindIn(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType(name, false);
                if (t != null) return t;
            }
            int plus = name.LastIndexOf('+');  // a private nested type: the outer's nested lookup, with non-public members
            if (plus > 0)
            {
                Type outer = FindIn(name.Substring(0, plus));
                if (outer != null) return outer.GetNestedType(name.Substring(plus + 1), BindingFlags.Public | BindingFlags.NonPublic);
            }
            return null;
        }
        static Type Find(string name)
        {
            Type t = FindIn(name);
            if (t != null || s_loaded) return t;
            s_loaded = true;  // an argument can live in an assembly nothing has touched yet
            foreach (string n in s_game) { try { Assembly.Load(n); } catch (Exception) { } }
            return FindIn(name);
        }
        // token 0: the fixture's hand-made rows, by name
        public static void AddMeth(int type, int token, int n)
        {
            Type t = s_types[type];
            string name = Text(n);
            name = name.Substring(name.LastIndexOf('.') + 1);
            MethodBase hit = null;
            if (t != null)
            {
                foreach (MethodInfo m in t.GetMethods(All)) if (token != 0 ? m.MetadataToken == token : m.Name == name) hit = m;
                if (hit == null) foreach (ConstructorInfo c in t.GetConstructors(All)) if (token != 0 && c.MetadataToken == token) hit = c;
            }
            s_meth.Add(hit);
            s_methWhy.Add(t == null ? s_typeWhy[type] : hit == null ? "no method with that token" : null);
        }
        static void AddStatic(int type, string field)
        {
            Type t = s_types[type];
            s_static.Add(t == null ? null : t.GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
        }
        public static string MethWhy(int m) { return s_methWhy[m]; }
        public static string WhyNow() { return Why; }
        public static object KeepNow() { return Keep; }
        static readonly List<object> s_live = new List<object>();  // the rebuilt objects' GC root for one input
        public static void Reset() { s_live.Clear(); Keep = null; s_self = null; s_args = s_argsIn = null; s_sv = null; }
        public static string MethName(int m) { return s_meth[m] == null ? null : s_meth[m].DeclaringType.Name + "." + s_meth[m].Name; }

        // ---- allocation: the raw objects; null with Why set = cannot be rebuilt ----
        public static object New(int type, int len)
        {
            Why = s_typeWhy[type];
            if (Why != null) return null;
            Type t = s_types[type];
            object o;
            try
            {
                o = t == typeof(string) ? new string('\0', len) : t.IsArray ? Array.CreateInstance(t.GetElementType(), len) : FormatterServices.GetUninitializedObject(t);
            }
            catch (Exception e) { Why = "allocate: " + e.GetType().Name; return null; }
            s_live.Add(o);
            return o;
        }
        // the box a value-type blob lands in (k = -1: this); null and Why null: a reference, the blob's first word is it
        static object NewBlob(Type t)
        {
            Why = null;
            if (t.IsByRef) t = t.GetElementType();
            if (!t.IsValueType) return null;
            Why = Refuse(t);
            if (Why != null) return null;
            object o = FormatterServices.GetUninitializedObject(t);
            s_live.Add(o);
            return o;
        }
        static MethodBase Closed(MethodBase m, object first)
        {
            MethodInfo mi = m as MethodInfo;
            if (mi == null || !mi.IsGenericMethodDefinition) return m;
            // shortcut: only T[] as the first parameter gives the method's argument; another shape counts as a skip, widen when one shows up
            Type at = first == null ? null : first.GetType();
            if (at == null || !at.IsArray || mi.GetGenericArguments().Length != 1) return null;
            return mi.MakeGenericMethod(at.GetElementType());
        }
        public static object ArgBlob(int m, int k)
        {
            MethodBase b = s_meth[m];
            if (k < 0) return b.DeclaringType.IsValueType ? NewBlob(b.DeclaringType) : null;
            Type p = b.GetParameters()[k].ParameterType;
            // T of an open generic method: the slot may hold a value, and the native side has no box for it
            if (p.IsGenericParameter) { Why = "generic method: argument unknown"; return null; }
            return NewBlob(p);
        }
        public static object StaticBlob(int sid) { Why = null; return s_static[sid] == null ? null : NewBlob(s_static[sid].FieldType); }
        public static int NumArgs(int m) { return s_meth[m].GetParameters().Length; }
        public static bool IsStatic(int m) { return s_meth[m].IsStatic; }

        // ---- the input ----
        public static void Begin(int m, object self, int nargs)
        {
            s_m = s_meth[m]; s_self = self; s_argsIn = new object[nargs]; s_args = new object[nargs];
            s_sf = new FieldInfo[8]; s_sv = new object[8]; s_sn = 0;
        }
        static object Decode(Type p, int lo, int hi, object o)
        {
            if (p.IsByRef || !p.IsValueType) return o;
            if (p.IsEnum) { p = Enum.GetUnderlyingType(p); object raw = Decode(p, lo, hi, o); return raw; }
            if (!p.IsPrimitive) return o;
            long v = unchecked((long)(uint)lo | ((long)hi << 32));
            switch (Type.GetTypeCode(p))
            {
                case TypeCode.Boolean: return (lo & 0xFF) != 0;
                case TypeCode.Char: return (char)lo;
                case TypeCode.SByte: return (sbyte)lo;
                case TypeCode.Byte: return (byte)lo;
                case TypeCode.Int16: return (short)lo;
                case TypeCode.UInt16: return (ushort)lo;
                case TypeCode.Int32: return lo;
                case TypeCode.UInt32: return unchecked((uint)lo);
                case TypeCode.Int64: return v;
                case TypeCode.UInt64: return unchecked((ulong)v);
                case TypeCode.Double: return BitConverter.Int64BitsToDouble(v);
                case TypeCode.Single: return (float)BitConverter.Int64BitsToDouble(v);  // the stack slot holds r4 widened (EA's x87 moves, K_LDSFLD above)
            }
            return null;
        }
        public static void Arg(int k, int lo, int hi, object o)
        {
            Type p = s_m.GetParameters()[k].ParameterType;
            object v = Decode(p, lo, hi, o);
            if (p.IsEnum) v = Enum.ToObject(p, v);
            s_argsIn[k] = v;
        }
        public static void StaticSet(int sid, int lo, int hi, object o)
        {
            FieldInfo f = s_static[sid];
            object v = Decode(f.FieldType, lo, hi, o);
            if (f.FieldType.IsEnum) v = Enum.ToObject(f.FieldType, v);
            s_sf[s_sn] = f; s_sv[s_sn++] = v;
        }
        // false with Why: the method cannot be called with this input
        public static bool Ready()
        {
            Why = null;
            if (s_m.IsGenericMethodDefinition)
            {
                s_m = Closed(s_m, s_argsIn.Length > 0 ? s_argsIn[0] : null);
                if (s_m == null) { Why = "generic method: argument unknown"; return false; }
            }
            if (!s_m.IsStatic && s_self == null) { Why = "null this"; return false; }
            ParameterInfo[] ps = s_m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                bool byref = t.IsByRef;
                if (t.IsByRef) t = t.GetElementType();
                // measured 2026-11-07: every Dictionary TryGetValue input whose value type is a class (out V) faulted the interpreter (write to 0, 50 of 261 in the first 1923
                // inputs), none with a value type did; the out slot of a reference type is not rebuilt right, so these are refused until it is
                if (byref && !t.IsValueType) { Why = "byref to a reference type"; return false; }
                string w = t.IsValueType ? Refuse(t) : null;
                if (w != null) { Why = w + " in a parameter"; return false; }
                if (t.IsValueType && s_argsIn[i] == null) { Why = "value argument not in the capture"; return false; }
            }
            return true;
        }
        static int M(int h, int v) { return unchecked(h * 31 + v); }
        static int Str(int h, string s) { for (int i = 0; s != null && i < s.Length; i++) h = M(h, s[i]); return h; }
        // result: returned object for the native walk (Keep) and the exception as in JitCorpus.X: type and the throwing frame's method.
        // The IL offset stays out: EA prints offset 0 at a superop, the case "stack-trace text only" of tools/jit-corpus.ts.
        public static int Run()
        {
            for (int i = 0; i < s_argsIn.Length; i++) s_args[i] = s_argsIn[i];
            object[] old = new object[s_sn];
            for (int i = 0; i < s_sn; i++) { old[i] = s_sf[i].GetValue(null); s_sf[i].SetValue(null, s_sv[i]); }
            int h = 17;
            object r = null;
            try { r = s_m.Invoke(s_self, s_args); Keep = r; }
            catch (TargetInvocationException e) { Keep = null; h = X(h, e.InnerException ?? e); }
            catch (Exception e) { Keep = null; h = X(h, e); }
            for (int i = 0; i < s_sn; i++) s_sf[i].SetValue(null, old[i]);  // the next input starts from the host's statics
            return h;
        }
        static int X(int h, Exception e)
        {
            h = Str(M(h, 5), e.GetType().FullName);
            string s = e.StackTrace, f = "";
            if (s != null) { int nl = s.IndexOf('\n'); f = (nl < 0 ? s : s.Substring(0, nl)).Trim(); }
            int at = f.IndexOf(" in "), end = at < 0 ? -1 : f.IndexOf(" (", at + 4);
            return Str(h, at >= 0 && end > at ? f.Substring(at + 4, end - at - 4) : "");
        }
        public static object[] ArgsNow() { return s_args; }
        public static int LastInt() { return Keep is int ? (int)Keep : int.MinValue; }
    }

    // ---- fixture (native/monohost/jit-replay-fixture.json): field order is the layout the fixture's hand-made bytes use ----
    public class JitReplayNode { public JitReplayNode next; public JitReplayNode other; public string s; public int[] a; public object nul; public int v; }
    public struct JitReplayPt { public int X; public int Y; }
    public delegate void JitReplayCb();
    public class JitReplayHolder { public JitReplayCb cb; public int v; }
    public static class JitReplayFix
    {
        public static int s_static;
        // a cycle of nodes (next), a string, an int array, a null field, and a write into the heap
        public static int Walk(JitReplayNode n, int steps)
        {
            int h = 0;
            JitReplayNode p = n;
            for (int i = 0; i < steps; i++)
            {
                h = h * 31 + p.v;
                if (p.s != null) h = h * 31 + p.s.Length * 1000 + p.s[0];
                if (p.a != null) h = h * 31 + p.a.Length * 7 + p.a[p.a.Length - 1];
                if (p.nul == null) h += 1;
                p = p.next;
            }
            n.v++;
            return h;
        }
        public static int Self(JitReplayNode n) { return n.other == n ? 1 : 0; }
        public static int Sum(JitReplayPt p, int k) { return p.X * 100 + p.Y + k; }
        public static int Stat(int a) { return s_static + a; }
        public static int Thrower(JitReplayNode n) { return n.next.v; }  // n.next is null: NullReferenceException in both arms
        public static int Cb(JitReplayHolder h) { return h.cb == null ? 7 : 8; }
        public static int Missing(object o) { return o == null ? 1 : 2; }
    }
}
