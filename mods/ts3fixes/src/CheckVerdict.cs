using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ts3Fixes
{
    // Shared harness-check helpers for the default-off EA fixes (docs/582-601). A check runs the fix's pure decision with
    // controls and prints one line ending in "verdict: BROKEN" (switch off), "FIXED" (switch on) or "UNEXPECTED" (a control
    // failed, or a member or live-path problem). Nothing in the world is touched.
    // Live path: a fix registers its hook class and reflection handles with LiveAdd; CkLive then reports a hook with no
    // swap in place (Fixes.sPatched; it keeps the last hook of a stacked swap, so at least one is required) and each handle
    // that is missing or not usable the way the hook uses it (EpMemberProblem). Also two small reflection helpers the fixes
    // share (SpNames, B13Nested). Same file in every PR that uses it.
    public static partial class Fixes
    {
        static string CkVerdict(bool on, bool fixedNow, bool controlsOk, bool membersOk)
        {
            if (!controlsOk || !membersOk) return "UNEXPECTED";
            return !on ? (fixedNow ? "UNEXPECTED" : "BROKEN") : (fixedNow ? "FIXED" : "UNEXPECTED");
        }

        static string CkMembers(string missing)
        {
            return "members found=" + (missing.Length == 0) + (missing.Length == 0 ? "" : " (missing: " + missing + ")");
        }

        // Joins the classic missing-member list with the live-path problems.
        static string EpJoin(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + ", " + b;
        }

        // "name: what first=c[first], ...; members found=..." for a check's result line.
        static string B582Counters(string name, int first, string[] what, string missing)
        {
            int[] c = PatchCounters();
            System.Text.StringBuilder b = new System.Text.StringBuilder(name + ":");
            for (int i = 0; i < what.Length; i++) b.Append((i == 0 ? " " : ", ") + what[i] + " " + (first + i) + "=" + c[first + i]);
            return b.Append("; ").Append(CkMembers(EpJoin(missing, CkLive(name)))).ToString();
        }

        static string B582Line(string name, bool on, string fixedText, bool fixedNow, string ctlText, bool controls, string missing, string counters)
        {
            return name + " check (switch " + (on ? "on" : "off") + "): " + fixedText + "=" + fixedNow + "; control: " + ctlText + "; controls ok=" + controls
                + "; verdict: " + CkVerdict(on, fixedNow, controls, EpJoin(missing, CkLive(name)).Length == 0) + "; " + counters;
        }

        // "a b" names of the members that are null in ("name", member, ...) pairs, for a fix's "not found" line.
        static string SpNames(params object[] pairs)
        {
            string s = "";
            for (int i = 0; i + 1 < pairs.Length; i += 2) if (pairs[i + 1] == null) s += pairs[i] + " ";
            return s.Trim();
        }

        // A nested type by its chain of names (public or not), null when one is missing.
        static Type B13Nested(Type t, params string[] names)
        {
            foreach (string n in names) { if (t == null) return null; t = t.GetNestedType(n, BindingFlags.Public | BindingFlags.NonPublic); }
            return t;
        }

        delegate string LiveProbe();  // the game's mscorlib (.NET 2.0) has no Func<>
        static Dictionary<string, LiveProbe> sLiveChecks;  // switch -> live-path check, filled by LiveAdd (no initializer: field order)

        // Registers a switch's live-path check; called from a static field initializer of the fix's file.
        static int LiveAdd(string sw, LiveProbe check)
        {
            if (sLiveChecks == null) sLiveChecks = new Dictionary<string, LiveProbe>();
            sLiveChecks[sw] = check;
            return 0;
        }

        static string CkLive(string sw)
        {
            LiveProbe f;
            if (sLiveChecks == null || !sLiveChecks.TryGetValue(sw, out f)) return "";
            try { return f() ?? ""; }
            catch (Exception e) { return "live check threw " + e.GetType().Name; }
        }

        // hook: the hook class whose swaps must be in place (null: none); swaps: how many it makes (0: none required).
        // triples: "name", member, spec. spec is a Type (value type the hook casts to), a string ("get", "set", "getset" for
        // properties, "static" to read a static field now), an int (method parameter count) or null (non-null only).
        static string EpLive(Type hook, int swaps, params object[] triples)
        {
            List<string> bad = new List<string>();
            try
            {
                if (hook != null && swaps > 0)
                {
                    int n = 0;
                    foreach (Type h in sPatched.Values) if (h == hook) n++;
                    if (n == 0) bad.Add(hook.Name + " swaps 0/" + swaps);
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
    }
}
