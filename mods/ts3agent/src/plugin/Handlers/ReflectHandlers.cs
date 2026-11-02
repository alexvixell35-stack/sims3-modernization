using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Ts3Agent
{
    // Escape hatch: read/write/call anything in the game by dotted path.
    //   "Sims3.Gameplay.Core.LotManager.ActiveLot.Name"   static type + member chain
    //   "@sim.SimDescription.FirstName"                   named roots (see Roots)
    //   "@household.AllActors[0].FullName"                list / array indexing
    public static partial class Handlers
    {
        const BindingFlags kAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                  | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        static void RegisterReflect()
        {
            Dispatcher.Register("reflect_get", ReflectGet);
            Dispatcher.Register("reflect_set", ReflectSet);
            Dispatcher.Register("reflect_call", ReflectCall);
            Dispatcher.Register("reflect_members", ReflectMembers);
            Dispatcher.Register("method_touch", MethodTouch);  // docs/526 §5: force the IL transform of a method without calling it
            Dispatcher.Register("reflect_find_types", ReflectFindTypes);
        }

        // Named roots usable as the first path segment, e.g. "@sim".
        public delegate object RootResolver();
        static readonly Dictionary<string, RootResolver> sRoots = new Dictionary<string, RootResolver>();

        public static void AddRoot(string name, RootResolver r) { sRoots[name] = r; }

        static object ReflectGet(Args a)
        {
            object v = Resolve(a.Str("path"));
            return Describe(v, (int)a.Long("depth", 1));
        }

        static object ReflectSet(Args a)
        {
            string path = a.Str("path");
            int dot = LastMemberSeparator(path);
            if (dot < 0) throw new McpException("path must end in a member name");
            object owner;
            Type ownerType;
            ResolveOwner(path.Substring(0, dot), out owner, out ownerType);
            string name = path.Substring(dot + 1);
            MemberInfo m = FindMember(ownerType, name);
            FieldInfo f = m as FieldInfo;
            PropertyInfo p = m as PropertyInfo;
            if (f != null)
            {
                f.SetValue(owner, ConvertTo(a.Raw("value"), f.FieldType));
                return Describe(f.GetValue(owner), 0);
            }
            if (p != null && p.CanWrite)
            {
                p.SetValue(owner, ConvertTo(a.Raw("value"), p.PropertyType), null);
                return Describe(p.GetValue(owner, null), 0);
            }
            throw new McpException("'" + name + "' is not a writable field or property of " + ownerType.FullName);
        }

        static object ReflectCall(Args a)
        {
            string path = a.Str("path");
            int dot = LastMemberSeparator(path);
            if (dot < 0) throw new McpException("path must end in a method name");
            object owner;
            Type ownerType;
            ResolveOwner(path.Substring(0, dot), out owner, out ownerType);
            string name = path.Substring(dot + 1);
            List<object> args = a.List("args") ?? new List<object>();
            // ts3fixes swaps Route.Replan (RouteBackoff A10). Mono's InternalInvoke checks the target against the swapped
            // method's class (RouteBackoffHook) and throws "Unable to invoke an invalid target" (same as RefTable.cs), so call
            // it compiled: the virtual call runs the hook like the game's own callers do.
            Sims3.SimIFace.Route route = owner as Sims3.SimIFace.Route;
            if (route != null && name == "Replan" && args.Count == 0)
                return Describe(route.Replan(), (int)a.Long("depth", 1));
            BindingFlags flags = kAny & ~(owner == null ? BindingFlags.Instance : 0);
            Exception last = null;
            foreach (MethodInfo mi in ownerType.GetMethods(flags))
            {
                if (mi.Name != name) continue;
                ParameterInfo[] ps = mi.GetParameters();
                if (ps.Length != args.Count) continue;
                object[] conv = new object[ps.Length];
                try
                {
                    for (int i = 0; i < ps.Length; i++) conv[i] = ConvertTo(args[i], ps[i].ParameterType);
                }
                catch (Exception e) { last = e; continue; }
                object result;
                try { result = mi.Invoke(mi.IsStatic ? null : owner, conv); }
                catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
                catch (TargetException)
                {
                    if (owner == null || !mi.DeclaringType.IsInstanceOfType(owner)) throw;
                    throw new McpException(mi.DeclaringType.FullName + "." + name + " cannot be invoked by reflection (likely "
                                           + "swapped by ts3fixes); add a compiled call in ReflectCall like Route.Replan");
                }
                return Describe(result, (int)a.Long("depth", 1));
            }
            throw new McpException("no overload of " + ownerType.FullName + "." + name + " takes "
                                   + args.Count + " convertible argument(s)" + (last != null ? ": " + last.Message : ""));
        }

        // method_touch path="Sims3.Gameplay.Actors.Sim.DoOneRoundOfRemoteChat": runs Mono's IL transform for every overload of that
        // name in a session that would not call it (docs/526 §5: the [IlPatch] smoke, for b80's chat and b87's opportunity). Mono
        // transforms a method before its first instruction runs, so one call is enough: each overload is invoked once on the sim
        // thread (RunAsync) with an uninitialized receiver (FormatterServices.GetUninitializedObject, or the object the path names;
        // null for a static method) and default arguments (null, zeroed value types), inside try/catch: the body then throws at
        // its first dereference or returns, and the transform has happened. RuntimeMethodHandle.GetFunctionPointer is no option on
        // this Mono: it hits an assert (int 3 at 0x58D9B6, then a call to address 0: bp-ilpatch-smoke 2026-11-02 08:52 crashed).
        // The outcome is an async_log line "touched <path>: <method>: <exception type> | ..." (plus RunAsync's "touch <path> -> ok").
        static object MethodTouch(Args a)
        {
            string path = a.Str("path");
            int dot = LastMemberSeparator(path);
            if (dot < 0) throw new McpException("path must end in a method name");
            object owner;
            Type ownerType;
            ResolveOwner(path.Substring(0, dot), out owner, out ownerType);
            string name = path.Substring(dot + 1);
            List<MethodInfo> methods = new List<MethodInfo>();
            foreach (MethodInfo mi in ownerType.GetMethods(kAny))
                if (mi.Name == name && !mi.IsAbstract && !mi.ContainsGenericParameters) methods.Add(mi);
            if (methods.Count == 0) throw new McpException("no method " + name + " on " + ownerType.FullName);
            RunAsync("touch " + path, delegate
            {
                List<string> outcomes = new List<string>();
                foreach (MethodInfo mi in methods)
                {
                    string what = mi.DeclaringType.Name + "::" + mi.ToString();
                    try
                    {
                        object recv = mi.IsStatic ? null : owner;
                        if (recv == null && !mi.IsStatic)
                            recv = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(mi.DeclaringType);
                        ParameterInfo[] ps = mi.GetParameters();
                        object[] args = new object[ps.Length];
                        for (int i = 0; i < ps.Length; i++)
                        {
                            Type t = ps[i].ParameterType;
                            if (t.IsByRef) t = t.GetElementType();
                            args[i] = t.IsValueType ? Activator.CreateInstance(t) : null;
                        }
                        mi.Invoke(recv, args);
                        outcomes.Add(what + ": returned");
                    }
                    catch (TargetInvocationException e) { outcomes.Add(what + ": " + (e.InnerException ?? e).GetType().Name); }
                    catch (Exception e) { outcomes.Add(what + ": not invoked, " + e.GetType().Name + ": " + e.Message); }
                }
                lock (sAsyncLog)
                {
                    sAsyncLog.Add(Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString() + " touched " + path + ": " + string.Join(" | ", outcomes.ToArray()));
                    if (sAsyncLog.Count > 30) sAsyncLog.RemoveAt(0);
                }
            });
            Dictionary<string, object> res = Util.Obj();
            res["queued"] = methods.Count;
            res["result"] = "async_log line 'touched " + path + ": ...' when the sim thread has run it";
            return res;
        }

        static object ReflectMembers(Args a)
        {
            object owner;
            Type t;
            ResolveOwner(a.Str("path"), out owner, out t);
            string filter = a.Str("filter", "").ToLowerInvariant();
            List<object> list = new List<object>();
            foreach (MemberInfo m in t.GetMembers(kAny))
            {
                if (filter.Length > 0 && m.Name.ToLowerInvariant().IndexOf(filter) < 0) continue;
                string desc;
                if (m is FieldInfo) desc = "field " + TypeName(((FieldInfo)m).FieldType) + " " + m.Name;
                else if (m is PropertyInfo) desc = "prop " + TypeName(((PropertyInfo)m).PropertyType) + " " + m.Name;
                else if (m is MethodInfo)
                {
                    MethodInfo mi = (MethodInfo)m;
                    if (mi.IsSpecialName) continue;
                    List<string> ps = new List<string>();
                    foreach (ParameterInfo p in mi.GetParameters()) ps.Add(TypeName(p.ParameterType) + " " + p.Name);
                    desc = (mi.IsStatic ? "static " : "") + "method " + TypeName(mi.ReturnType) + " " + mi.Name
                           + "(" + string.Join(", ", ps.ToArray()) + ")";
                }
                else continue;
                list.Add(desc);
                if (list.Count >= 400) { list.Add("... (truncated, use filter)"); break; }
            }
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["type"] = t.FullName;
            r["members"] = list;
            return r;
        }

        static object ReflectFindTypes(Args a)
        {
            string q = a.Str("query").ToLowerInvariant();
            List<string> found = new List<string>();
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (Type t in types)
                {
                    if (t != null && t.FullName != null && t.FullName.ToLowerInvariant().IndexOf(q) >= 0)
                    {
                        found.Add(t.FullName);
                        if (found.Count >= 200) return found;
                    }
                }
            }
            return found;
        }

        // ------------------------------------------------------------ path resolution

        static int LastMemberSeparator(string path)
        {
            int depth = 0;
            for (int i = path.Length - 1; i >= 0; i--)
            {
                char c = path[i];
                if (c == ']') depth++;
                else if (c == '[') depth--;
                else if (c == '.' && depth == 0) return i;
            }
            return -1;
        }

        public static object Resolve(string path)
        {
            object owner;
            Type t;
            ResolveOwner(path, out owner, out t);
            if (owner == null && t != null && !IsTypePath(path)) return null;
            return owner ?? (object)(t != null ? t.FullName : null);
        }

        static bool IsTypePath(string path) { return FindType(path) != null; }

        // Resolves a path to either an instance (owner) or a static type context.
        static void ResolveOwner(string path, out object owner, out Type type)
        {
            List<string> segs = SplitPath(path);
            int i;
            owner = null;
            type = null;
            if (segs[0].StartsWith("@"))
            {
                string root = segs[0];
                string idx = null;
                int b = root.IndexOf('[');
                if (b >= 0) { idx = root.Substring(b); root = root.Substring(0, b); }
                if (root.StartsWith("@obj:"))
                {
                    // "@obj:<object id>" addresses any game object directly.
                    ulong oid = ulong.Parse(root.Substring(5), CultureInfo.InvariantCulture);
                    owner = Sims3.Gameplay.Abstracts.GameObject.GetObject(new Sims3.SimIFace.ObjectGuid(oid));
                }
                else
                {
                    RootResolver r;
                    if (!sRoots.TryGetValue(root.Substring(1), out r))
                        throw new McpException("unknown root '" + root + "'; known: @obj:<id>, @" + string.Join(", @", new List<string>(sRoots.Keys).ToArray()));
                    owner = r();
                }
                if (idx != null) owner = ApplyIndexers(owner, idx);
                if (owner == null) throw new McpException(root + " is null right now");
                type = owner.GetType();
                i = 1;
            }
            else
            {
                // Longest prefix that names a type.
                i = segs.Count;
                while (i > 0 && type == null)
                {
                    type = FindType(string.Join(".", segs.GetRange(0, i).ToArray()));
                    if (type == null) i--;
                }
                if (type == null) throw new McpException("no type found at the start of '" + path + "'");
                // Nested types: "Outer.Inner" may also be written "Outer+Inner".
            }
            for (; i < segs.Count; i++)
            {
                string seg = segs[i];
                string idx = null;
                int b = seg.IndexOf('[');
                if (b >= 0) { idx = seg.Substring(b); seg = seg.Substring(0, b); }
                MemberInfo m = FindMember(type, seg);
                object v;
                if (m is FieldInfo) v = ((FieldInfo)m).GetValue(owner);
                else if (m is PropertyInfo) v = ((PropertyInfo)m).GetValue(owner, null);
                else throw new McpException("'" + seg + "' is not a field or property of " + type.FullName);
                if (idx != null) v = ApplyIndexers(v, idx);
                if (v == null)
                {
                    if (i == segs.Count - 1) { owner = null; type = null; return; }
                    throw new McpException(string.Join(".", segs.GetRange(0, i + 1).ToArray()) + " is null");
                }
                owner = v;
                type = v.GetType();
            }
        }

        static object ApplyIndexers(object v, string idx)
        {
            // idx like "[0]" or "[2][1]"
            int p = 0;
            while (p < idx.Length && idx[p] == '[')
            {
                int close = idx.IndexOf(']', p);
                int n = int.Parse(idx.Substring(p + 1, close - p - 1), CultureInfo.InvariantCulture);
                IList list = v as IList;
                if (list != null) v = list[n];
                else
                {
                    IEnumerable e = v as IEnumerable;
                    if (e == null) throw new McpException("cannot index into " + (v == null ? "null" : v.GetType().FullName));
                    int k = 0;
                    object found = null;
                    bool ok = false;
                    foreach (object o in e) { if (k++ == n) { found = o; ok = true; break; } }
                    if (!ok) throw new McpException("index " + n + " out of range");
                    v = found;
                }
                p = close + 1;
            }
            return v;
        }

        static List<string> SplitPath(string path)
        {
            List<string> segs = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '[') depth++;
                else if (path[i] == ']') depth--;
                else if (path[i] == '.' && depth == 0) { segs.Add(path.Substring(start, i - start)); start = i + 1; }
            }
            segs.Add(path.Substring(start));
            return segs;
        }

        static readonly Dictionary<string, Type> sTypeCache = new Dictionary<string, Type>();

        public static Type FindType(string name)
        {
            Type t;
            if (sTypeCache.TryGetValue(name, out t)) return t;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(name, false);
                if (t == null)
                {
                    // Allow "Outer.Inner" for nested types.
                    int dot = name.LastIndexOf('.');
                    if (dot > 0) t = asm.GetType(name.Substring(0, dot) + "+" + name.Substring(dot + 1), false);
                }
                if (t != null) break;
            }
            sTypeCache[name] = t;
            return t;
        }

        static MemberInfo FindMember(Type t, string name)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                FieldInfo f = cur.GetField(name, kAny);
                if (f != null) return f;
                PropertyInfo p;
                try { p = cur.GetProperty(name, kAny); }
                catch (AmbiguousMatchException) { p = null; }
                if (p != null && p.GetIndexParameters().Length == 0) return p;
            }
            throw new McpException("no field or property '" + name + "' on " + t.FullName);
        }

        // ------------------------------------------------------------ conversion & description

        public static object ConvertTo(object v, Type t)
        {
            if (v == null) return null;
            if (t.IsInstanceOfType(v)) return v;
            if (t.IsEnum)
            {
                if (v is string) return Enum.Parse(t, (string)v, true);
                return Enum.ToObject(t, Convert.ToInt64(v, CultureInfo.InvariantCulture));
            }
            string s = v as string;
            if (s != null && s.StartsWith("@") || (s != null && s.IndexOf('.') > 0 && !IsPrimitiveLike(t)))
            {
                object r = Resolve(s);
                if (r != null && t.IsInstanceOfType(r)) return r;
            }
            if (IsPrimitiveLike(t)) return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
            throw new McpException("cannot convert " + v.GetType().Name + " to " + t.FullName);
        }

        static bool IsPrimitiveLike(Type t)
        {
            return t.IsPrimitive || t == typeof(string) || t == typeof(decimal);
        }

        static string TypeName(Type t)
        {
            if (t == null) return "?";
            if (t.IsGenericType)
            {
                List<string> a = new List<string>();
                foreach (Type g in t.GetGenericArguments()) a.Add(TypeName(g));
                return t.Name.Split('`')[0] + "<" + string.Join(",", a.ToArray()) + ">";
            }
            return t.Name;
        }

        public static object Describe(object v, int depth)
        {
            if (v == null || v is string || v is bool || v.GetType().IsPrimitive || v is Enum || v is decimal) return v;
            Type t = v.GetType();
            if (depth <= 0)
            {
                string s;
                try { s = v.ToString(); } catch (Exception) { s = "?"; }
                return "<" + TypeName(t) + "> " + s;
            }
            IEnumerable e = v as IEnumerable;
            if (e != null && !(v is IDictionary))
            {
                List<object> items = new List<object>();
                foreach (object o in e)
                {
                    if (items.Count >= 100) { items.Add("..."); break; }
                    items.Add(Describe(o, depth - 1));
                }
                return items;
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["$type"] = t.FullName;
            try { d["$str"] = v.ToString(); } catch (Exception) { }
            int n = 0;
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.Name.IndexOf('<') >= 0) continue;
                if (++n > 80) { d["$truncated"] = true; break; }
                object fv;
                try { fv = f.GetValue(v); } catch (Exception ex) { fv = "<" + ex.GetType().Name + ">"; }
                d[f.Name] = Describe(fv, depth - 1);
            }
            return d;
        }
    }
}
