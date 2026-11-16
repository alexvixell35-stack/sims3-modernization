using System;
using System.Reflection;

namespace Ts3Fixes
{
    // Reflection and check helpers shared by the docs/582-593 batch files (Batch582* .. Batch593*, ButlerPayAlarm.cs).
    // One copy here, the same in every batch PR, so the batches merge in any order.
    public static partial class Fixes
    {
        const BindingFlags kB11All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        delegate bool B12BoolFn();

        static FieldInfo B12F(Type t, string n) { return t == null ? null : t.GetField(n, kB11All); }
        static MethodInfo B12M(Type t, string n, params Type[] a) { return t == null ? null : t.GetMethod(n, kB11All, null, a, null); }

        // B12F that also walks the base types (a private field declared on a base class).
        static FieldInfo B12FUp(Type t, string n)
        {
            for (; t != null; t = t.BaseType) { FieldInfo f = t.GetField(n, kB11All | BindingFlags.DeclaredOnly); if (f != null) return f; }
            return null;
        }

        // A type by full name from any loaded assembly (store content lives in its own assemblies).
        static Type B13StoreType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = a.GetType(name); if (t != null) return t; }
            return null;
        }

        // A check line from the hook's own decision, with the live-path assert folded into "members found".
        static object B12Check(string what, string name, int first, string[] cw, string missing, string fixedText, bool fixedPure, string ctlText, bool controls)
        {
            string counters = B582Counters(name, first, cw, missing);
            if (what == "result") return counters;
            if (what != "run") return name + " <run|result>";
            bool on = ShortcutOn(name);
            return B582Line(name, on, fixedText, on && fixedPure, ctlText, controls, missing, counters);
        }
    }
}
