using System;
using System.Reflection;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // Every instance delegate ts3fixes makes from a MethodInfo goes through Bind (gate: tools/swap-stack-audit.ts rawDelegates).
        // Delegate.CreateDelegate(Type, object, MethodInfo) binds the exact method, no virtual dispatch, in the game's Mono. On an
        // abstract or interface method the interpreter then transforms a method with no IL header and the game dies at
        // 0x00E6463F (2026-11-18: UnseenService88 SvcServe on the abstract BarInteraction.RunOrderBehavior, docs/51 §531).
        // Bind takes the override on the target's own class chain instead; any other method binds exactly as before (a call
        // to a base body on purpose, e.g. the BaseDispose sites, keeps working).
        internal static Delegate Bind(Type delegateType, object target, MethodInfo m)
        {
            if (m != null && target != null && !m.IsStatic && (m.IsAbstract || m.DeclaringType.IsInterface)) m = BodyOn(target.GetType(), m);
            return Delegate.CreateDelegate(delegateType, target, m);
        }

        // The implementation of abstract / interface method m that an instance of t runs. Throws when there is none (better a
        // managed exception the caller's try/catch sees than an access violation in EA's transform).
        internal static MethodInfo BodyOn(Type t, MethodInfo m)
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            ParameterInfo[] ps = m.GetParameters();
            Type[] sig = new Type[ps.Length];
            for (int i = 0; i < ps.Length; i++) sig[i] = ps[i].ParameterType;
            bool iface = m.DeclaringType.IsInterface;
            MethodInfo root = iface ? m : m.GetBaseDefinition();
            for (Type c = t; c != null; c = c.BaseType)
            {
                MethodInfo o = c.GetMethod(m.Name, inst, null, sig, null);
                if (o == null && iface) o = c.GetMethod(m.DeclaringType.FullName.Replace('+', '.') + "." + m.Name, inst, null, sig, null);  // explicit implementation
                if (o == null || o.IsAbstract) continue;
                if (iface || o.GetBaseDefinition() == root) return o;  // same vtable slot, not a `new` method that hides it
            }
            throw new InvalidOperationException("no body for " + m.DeclaringType.Name + "." + m.Name + " on " + t.Name);
        }
    }
}
