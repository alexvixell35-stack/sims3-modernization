// Managed side of the monohost layout dump (MONOHOST_LAYOUT, docs/293): lists every method with a body in the loaded
// assemblies, closed generic base types included (their methods are transformed per instantiation). No EA code here.
using System;
using System.Collections;
using System.Reflection;

namespace MonoHostTest
{
    public static class LayoutDump
    {
        static readonly ArrayList sM = new ArrayList();

        static string TN(Type t)
        {
            string s = (t.DeclaringType != null ? TN(t.DeclaringType) + "+" : t.Namespace != null ? t.Namespace + "." : "") + t.Name;
            if (!t.IsGenericType) return s;
            s += "<";
            foreach (Type a in t.GetGenericArguments()) s += (s.EndsWith("<") ? "" : ",") + a.Name;
            return s + ">";
        }

        public static int Collect()
        {
            Hashtable seen = new Hashtable();
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] ts;
                try { ts = a.GetTypes(); } catch { continue; }
                foreach (Type t in ts)
                    for (Type b = t; b != null && b != typeof(object); b = b.BaseType)
                    {
                        if (b.ContainsGenericParameters || (b != t && !b.IsGenericType) || seen.Contains(b)) break;
                        seen[b] = 1;
                        try
                        {
                            foreach (MethodBase m in b.GetMethods(all)) Add(m);
                            foreach (MethodBase m in b.GetConstructors(all)) Add(m);
                        }
                        catch { }
                    }
            }
            return sM.Count;
        }

        static void Add(MethodBase m)
        {
            const MethodImplAttributes noBody = MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime | MethodImplAttributes.Native;
            if (m.IsAbstract || (m.GetMethodImplementationFlags() & noBody) != 0 || (m.Attributes & MethodAttributes.PinvokeImpl) != 0) return;
            if (m.IsGenericMethodDefinition) return;
            sM.Add(m);
        }

        public static IntPtr Handle(int i) { return ((MethodBase)sM[i]).MethodHandle.Value; }
        public static string Name(int i) { MethodBase m = (MethodBase)sM[i]; return TN(m.DeclaringType) + "::" + m.ToString(); }
    }
}
