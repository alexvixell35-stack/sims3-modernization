using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ts3Fixes
{
    // Game-independent part of asyncjigsync (AsyncJigSync.cs, docs/240), checked offline by tools/selftest/asyncjigsync.
    public static class AsyncJigSyncCore
    {
        static readonly Dictionary<Type, FieldInfo[]> sFields = new Dictionary<Type, FieldInfo[]>();

        // every instance field of t and its bases (private ones too) whose type is jig or a subclass; cached per t
        public static FieldInfo[] Fields(Type t, Type jig)
        {
            FieldInfo[] f;
            if (sFields.TryGetValue(t, out f)) return f;
            List<FieldInfo> l = new List<FieldInfo>();
            for (Type b = t; b != null && b != typeof(object); b = b.BaseType)
                foreach (FieldInfo fi in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (jig.IsAssignableFrom(fi.FieldType)) l.Add(fi);
            f = l.ToArray();
            sFields[t] = f;
            return f;
        }

        // true when o holds a jig (per jig's fields) for which unplaced answers true
        public static bool Holds(object o, Type jig, Predicate<object> unplaced)
        {
            if (o == null) return false;
            foreach (FieldInfo fi in Fields(o.GetType(), jig))
            {
                object j = fi.GetValue(o);
                if (j != null && unplaced(j)) return true;
            }
            return false;
        }
    }
}
