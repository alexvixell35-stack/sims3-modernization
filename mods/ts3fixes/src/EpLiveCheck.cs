using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ts3Fixes
{
    // Live-path asserts for the docs/587 checks (EP1-EP6, store). A check used to test only its pure decision with
    // constants, so a hook whose own reflection handle could not be used (hypnotrickrelease read a setter-only property
    // and threw on every run) still printed FIXED. EpLive adds, to the check's "members" verdict:
    //   - the hook class's swaps are really in place (Fixes.Patch counts them per hook in sSwapsByHook; sPatched keeps only the last hook of a stacked swap), with the expected count;
    //   - each reflection handle the hook uses is non-null and usable the way the hook uses it: a field's or property's
    //     type, a property's getter / setter, a method's parameter count and return type, a static value read for real.
    // Call: EpLive(hookType, swaps, "name", member, spec, ...). spec is a Type (value type the hook casts to), a string
    // ("get", "set", "getset" for properties, "static" to read a static field now), an int (method parameter count) or
    // null (non-null only). Returns "" when the live path is usable, else the problems (shown like missing members).
    public static partial class Fixes
    {
        static int EpSwapCount(Type hook)
        {
            int n;  // per hook, not from sPatched: a stacked swap's earlier hook is still in place (Fixes.sSwapsByHook)
            return sSwapsByHook.TryGetValue(hook, out n) ? n : 0;
        }

        static string EpLive(Type hook, int swaps, params object[] triples)
        {
            List<string> bad = new List<string>();
            try
            {
                if (hook != null)
                {
                    int n = EpSwapCount(hook);
                    if (n < swaps) bad.Add(hook.Name + " swaps " + n + "/" + swaps);
                }
                for (int i = 0; i + 1 < triples.Length; i += 3)
                {
                    string name = triples[i] as string;
                    object m = triples[i + 1];
                    object spec = i + 2 < triples.Length ? triples[i + 2] : null;
                    string p = EpMemberProblem(m, spec);
                    if (p != null) bad.Add(name + " " + p);
                }
            }
            catch (Exception e) { bad.Add("live check threw " + e.GetType().Name); }
            return string.Join(", ", bad.ToArray());
        }

        // Null when the member is usable as spec says; else a short reason.
        static string EpMemberProblem(object m, object spec)
        {
            if (m == null) return "missing";
            Type want = spec as Type;
            string mode = spec as string;
            FieldInfo f = m as FieldInfo;
            if (f != null)
            {
                if (want != null && !EpTypeFits(f.FieldType, want)) return "is " + f.FieldType.Name + " not " + want.Name;
                if (mode == "static")
                {
                    if (!f.IsStatic) return "not static";
                    f.GetValue(null);
                }
                return null;
            }
            PropertyInfo pi = m as PropertyInfo;
            if (pi != null)
            {
                if (want != null && !EpTypeFits(pi.PropertyType, want)) return "is " + pi.PropertyType.Name + " not " + want.Name;
                bool needGet = mode == null || mode == "get" || mode == "getset", needSet = mode == "set" || mode == "getset";
                if (needGet && pi.GetGetMethod(true) == null) return "has no getter";
                if (needSet && pi.GetSetMethod(true) == null) return "has no setter";
                return null;
            }
            MethodInfo mi = m as MethodInfo;
            if (mi != null)
            {
                if (mi.ContainsGenericParameters) return "is open generic";
                if (spec is int && mi.GetParameters().Length != (int)spec) return "takes " + mi.GetParameters().Length + " args not " + (int)spec;
                if (want != null && !EpTypeFits(mi.ReturnType, want)) return "returns " + mi.ReturnType.Name + " not " + want.Name;
                return null;
            }
            Type t = m as Type;
            if (t != null)
            {
                if (want != null && !want.IsAssignableFrom(t)) return "is not a " + want.Name;
                return null;
            }
            return null;
        }

        // A value of type `have` can be cast to `want` the way the hook does ((T)GetValue, as T, Convert, enum to int).
        static bool EpTypeFits(Type have, Type want)
        {
            if (want == typeof(object) || want.IsAssignableFrom(have)) return true;
            if (want == typeof(int) && have.IsEnum) return true;   // Convert.ToInt32 on an enum field
            if (want == typeof(System.Collections.IEnumerable) || want == typeof(System.Collections.ICollection) || want == typeof(Array))
                return want.IsAssignableFrom(have);
            return false;
        }

        // Joins the classic missing-member list with the live-path problems.
        static string EpJoin(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + ", " + b;
        }
    }
}
