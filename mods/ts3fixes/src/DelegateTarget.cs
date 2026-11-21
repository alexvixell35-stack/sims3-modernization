using System;
using System.Reflection;

namespace Ts3Fixes
{
    // The method a compiled call on a target must bind to. Delegate.CreateDelegate(type, target, method) in the game's Mono
    // calls exactly the MethodInfo it is given, with no virtual dispatch: a base virtual MethodInfo runs the base body even
    // when the target's class overrides it, and an abstract one (no body) crashes the interpreter (0xE6463F, UnseenService88
    // RunOrderBehavior, Bridgeport 2026-11-20). So a virtual MethodInfo looked up on a base type is resolved here to the
    // override on the target's own class chain first (the method whose base definition is the same slot). null: no concrete
    // method (abstract and not overridden, or an interface method with no class method found); the caller then skips the
    // call. tools/selftest/delegatesite lists every CreateDelegate site and why its MethodInfo is safe.
    public static partial class Fixes
    {
        static MethodInfo DelegateTarget(object target, MethodInfo m)
        {
            if (m == null || target == null || m.IsStatic) return m;
            if (!m.IsVirtual && !m.IsAbstract) return m;
            Type decl = m.DeclaringType;
            if (!m.IsAbstract && (m.IsFinal || target.GetType() == decl)) return m;
            ParameterInfo[] ps = m.GetParameters();
            Type[] sig = new Type[ps.Length];
            for (int i = 0; i < ps.Length; i++) sig[i] = ps[i].ParameterType;
            MethodInfo slot = decl.IsInterface ? null : m.GetBaseDefinition();
            for (Type t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                MethodInfo o = t.GetMethod(m.Name, kInstAll | BindingFlags.DeclaredOnly, null, sig, null);
                if (o == null || o.IsAbstract || o.ReturnType != m.ReturnType) continue;
                // a `new` method of the same name is not an override; for an interface MethodInfo the class's own method of
                // that name and signature is taken (OverwatchSpread uses GetInterfaceMap where an explicit one matters)
                if (slot != null && o.GetBaseDefinition().MethodHandle.Value != slot.MethodHandle.Value) continue;
                return o;
            }
            return m.IsAbstract ? null : m;
        }
    }
}
