// docs/258 option C: exactness corpus for [EqualsFast] (native/ts3native/equalsfast.cpp). Every value type the host can build
// from the game's assemblies (enums of every width, int-only structs, mixed structs with float / bool / string / ref fields,
// primitives, a few generic instances) gets boxed values with many collisions; each pair goes to EqIc.Check, which runs EA's
// InternalEquals icall 0xE7A590 and the [EqualsFast] hook on the same pair (monohost.cpp EqCheckIc). Our own test code,
// compiled against the game's DLLs by build.cmd (no EA code here). C# 2 only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MonoHostTest
{
    public static class EqIc
    {
        // 1 = the hook answered from a plan, 0 = it went to EA's code (same answer), -1 = mismatch
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Check(object a, object b);
    }

    public static class EqCorpus
    {
        static uint s;
        static uint Rnd() { s = s * 1103515245u + 12345u; return s >> 4; }
        static readonly string[] Asms = { "mscorlib", "System", "System.Xml", "ScriptCore", "SimIFace", "Sims3Metadata", "UI",
            "Sims3GameplaySystems", "Sims3GameplayObjects", "Sims3StoreObjects" };
        static readonly float[] F4 = { 0f, -0f, 1f, float.NaN };
        static readonly double[] F8 = { 0.0, -0.0, 1.0, double.NaN };
        public static int Types, Skipped, Bad, PtrSkipped;

        // few distinct values so equal pairs are common: 0, 1, a high bit, a random word
        static long Pick(uint r)
        {
            switch (r & 3) { case 0: return 0; case 1: return 1; case 2: return unchecked((long)0x8000000080000000UL); default: return ((long)r << 32) | (r * 2654435761u); }  // deterministic in r: same salt, same value
        }
        static object Val(Type ft, uint r)
        {
            if (ft.IsEnum) return Enum.ToObject(ft, Pick(r));
            long v = Pick(r);
            switch (Type.GetTypeCode(ft))
            {
                case TypeCode.Int32: return unchecked((int)v);
                case TypeCode.UInt32: return unchecked((uint)v);
                case TypeCode.Int64: return v;
                case TypeCode.UInt64: return unchecked((ulong)v);
                case TypeCode.Int16: return unchecked((short)v);
                case TypeCode.UInt16: return unchecked((ushort)v);
                case TypeCode.SByte: return unchecked((sbyte)v);
                case TypeCode.Byte: return unchecked((byte)v);
                case TypeCode.Char: return unchecked((char)v);
                case TypeCode.Boolean: return (v & 1) != 0;
                case TypeCode.Single: return F4[r & 3];
                case TypeCode.Double: return F8[r & 3];
                case TypeCode.String: return (r & 1) == 0 ? "a" : "b";
            }
            return null;  // refs and nested structs keep their default
        }
        static object Make(Type t, uint r)
        {
            if (t.IsEnum) return Enum.ToObject(t, Pick(r));
            object o = Activator.CreateInstance(t);
            FieldInfo[] fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fs.Length; i++)
            {
                object v = Val(fs[i].FieldType, r >> (2 * (i & 7)) ^ (uint)i);
                if (v != null) try { fs[i].SetValue(o, v); } catch (Exception) { }
            }
            return o;
        }
        static bool Wanted(Type t)
        {
            if (!t.IsValueType || t.IsGenericTypeDefinition || t.ContainsGenericParameters || t.IsPointer || t.IsByRef) return false;
            string n = t.FullName;
            if (n == "System.Void" || n == "System.TypedReference" || n == "System.ArgIterator" || n == "System.RuntimeArgumentHandle") return false;
            // pointer fields: EA's InternalEquals boxes them (mono_value_box on a PTR type) and never returns in the host
            // (SimpleCollator+Context, 2026-11-12); no game Equals compares such a struct, and the plan rejects them anyway
            FieldInfo[] fs = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fs.Length; i++) if (fs[i].FieldType.IsPointer) { PtrSkipped++; return false; }
            return true;
        }
        static int Pairs(Type t, ref object prev)
        {
            object[] v = new object[6];
            uint base0 = Rnd() & 0x3F;  // small salt domain: many equal pairs
            for (int i = 0; i < 6; i++) v[i] = Make(t, i < 2 ? base0 : Rnd() & 0x3F);
            int n = 0;
            for (int i = 0; i < 6; i++)
                for (int j = i; j < 6; j++)
                {
                    if (EqIc.Check(v[i], v[j]) < 0) Bad++;
                    n++;
                }
            if (prev != null) { if (EqIc.Check(v[0], prev) < 0) Bad++; n++; }
            prev = v[0];
            return n;
        }

        public static int AsmCount { get { return Asms.Length; } }
        // pairs checked for one assembly (asm = -1: the generic instances); the native side counts hits and mismatches
        public static int Run(int seed, int asm)
        {
            if (asm < 0) s = (uint)seed;
            int n = 0;
            if (asm < 0)
            {
                Type[] extra = { typeof(KeyValuePair<int, int>), typeof(KeyValuePair<long, int>), typeof(KeyValuePair<int, string>) };
                for (int i = 0; i < extra.Length; i++) n += Pairs(extra[i], ref prev);
                return n;
            }
            Type[] ts;
            try { ts = Assembly.Load(Asms[asm]).GetTypes(); }
            catch (Exception) { Skipped++; return 0; }
            for (int i = 0; i < ts.Length; i++)
            {
                if (!Wanted(ts[i])) continue;
                try { n += Pairs(ts[i], ref prev); Types++; }
                catch (Exception) { Skipped++; }
            }
            return n;
        }
        static object prev;
    }
}
