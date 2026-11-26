using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ts3Fixes
{
    // deadrefdescrev writer guard (docs/336 §11, LC-7b): at the first world load with the switch on, the IL of every loaded
    // assembly that is not EA's core (NRaas, other mods, our own) is scanned for writes to the description references phase 4
    // indexes. A kind with such a writer is "untrusted": every delta run steps that kind on every description (exact, just
    // not faster). Game-independent so tools/selftest/deadrefs compiles it with AutShadowCore.cs (its opcode table and
    // MemberName, the same reader AutShadow's G-M6 IL hashes use: MethodBase.GetMethodBody + GetILAsByteArray + Module.Resolve*).
    // A write is: stfld / stsfld of a target field unless the value is a plain `ldnull` (a removal cannot make a holder
    // stale; not when the store is a branch target, where another path may bring a value); ldflda / ldsflda of a target (a
    // ref can be written through); a list target loaded and not consumed at once by a read (null test, pop, a read-only
    // List / ICollection / IEnumerable member after simple argument loads, the List copy constructor). Anything else after the
    // load (stloc, dup, ret, passing it on) counts as a write: over-counting costs speed, never exactness.
    // LC-7c (docs/336 §12): a store of a fresh empty list (`newobj List`1::.ctor(); stfld`) is no write (it holds nothing);
    // argument loads before the consumer may include ldfld and calls that take only what was pushed after the list; a list
    // passed as the source of List`1.AddRange / InsertRange is read. A token that does not resolve in this runtime names
    // no target: that instruction cannot run (Unresolved). Reflection guard: an indexed field's name as a string constant
    // in a method that also calls Type.GetField / GetProperty / GetMember or FieldInfo / PropertyInfo.SetValue makes that
    // kind untrusted; any Type.InvokeMember or System.Reflection.Emit use makes every kind untrusted (Reflect).
    // Not seen: names built at run time, and code emitted by an assembly that is not scanned. The 4 h full walk and
    // deadrefdeltaverify stay for those (docs/336 §11.4).
    public static class DescWriterScan
    {
        public const int KCoworkers = 1, KFormerBoss = 2, KChess = 4, KPregnancy = 8, KAll = 15;
        public static readonly string[] KindNames = { "coworkers", "formerboss", "chess", "pregnancy" };

        // throws when the token cannot be resolved: the caller counts the method unreadable
        public delegate MemberInfo Resolver(int token);

        static readonly string[] kReadNames = { "get_Count", "GetEnumerator", "Contains", "IndexOf", "LastIndexOf", "get_Item", "ToArray",
            "Exists", "Find", "FindAll", "FindIndex", "FindLast", "FindLastIndex", "TrueForAll", "CopyTo", "ConvertAll", "GetRange",
            "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear" }; // removals cannot make a holder hold a reference the index lacks
        static readonly string[] kReadTypes = { "System.Collections.Generic.List`1", "System.Collections.Generic.ICollection`1",
            "System.Collections.Generic.IList`1", "System.Collections.Generic.IEnumerable`1", "System.Collections.IEnumerable", "System.Collections.ICollection" };

        static string GenericName(Type t)
        {
            if (t == null) return null;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) t = t.GetGenericTypeDefinition();
            return t.FullName;
        }

        // a read of the list on top of the stack by a member taking `args` more arguments
        static bool ReadUse(MemberInfo mi, int args, bool ctor)
        {
            MethodBase m = mi as MethodBase;
            if (m == null || Array.IndexOf(kReadTypes, GenericName(m.DeclaringType)) < 0) return false;
            if (ctor) return m is ConstructorInfo && args == 0 && m.GetParameters().Length == 1 && GenericName(m.DeclaringType) == "System.Collections.Generic.List`1";
            return !m.IsStatic && !(m is ConstructorInfo) && Array.IndexOf(kReadNames, m.Name) >= 0 && m.GetParameters().Length == args;
        }

        // the list on top of the stack is the last argument of List`1.AddRange / InsertRange (copies from it)
        static bool SourceUse(MemberInfo mi)
        {
            MethodBase m = mi as MethodBase;
            return m != null && !m.IsStatic && !(m is ConstructorInfo) && GenericName(m.DeclaringType) == "System.Collections.Generic.List`1"
                && ((m.Name == "AddRange" && m.GetParameters().Length == 1) || (m.Name == "InsertRange" && m.GetParameters().Length == 2));
        }

        // newobj List`1::.ctor() or .ctor(int capacity): a list that holds nothing
        static bool EmptyList(MemberInfo mi)
        {
            ConstructorInfo c = mi as ConstructorInfo;
            if (c == null || GenericName(c.DeclaringType) != "System.Collections.Generic.List`1") return false;
            ParameterInfo[] p = c.GetParameters();
            return p.Length == 0 || (p.Length == 1 && p[0].ParameterType == typeof(int));
        }

        static bool SimplePush(int op)
        {
            return (op >= 0x02 && op <= 0x09) || op == 0x0E || op == 0x11 || op == 0x14 || (op >= 0x15 && op <= 0x23) || op == 0x72 || op == 0xFE09 || op == 0xFE0C;
        }

        // One decoded instruction: offset, opcode (0xFExx for two-byte), token (or 0).
        struct Ins { public int At, Op, Tok; }

        static List<Ins> Decode(byte[] il, Dictionary<int, bool> targets)
        {
            List<Ins> l = new List<Ins>();
            int p = 0;
            while (p < il.Length)
            {
                Ins i = new Ins(); i.At = p;
                int op = il[p++];
                if (op == 0xFE) op = 0xFE00 | il[p++];
                i.Op = op;
                int n = AutShadowCore.Operand(op);
                if (n == -1) { i.Tok = BitConverter.ToInt32(il, p); p += 4; }
                else if (n == -2)
                {
                    int cnt = BitConverter.ToInt32(il, p), end = p + 4 + 4 * cnt;
                    for (int k = 0; k < cnt; k++) targets[end + BitConverter.ToInt32(il, p + 4 + 4 * k)] = true;
                    p = end;
                }
                else
                {
                    if ((op >= 0x2B && op <= 0x37) || op == 0xDE) targets[p + 1 + (sbyte)il[p]] = true;
                    else if ((op >= 0x38 && op <= 0x44) || op == 0xDD) targets[p + 4 + BitConverter.ToInt32(il, p)] = true;
                    p += n;
                }
                if (p > il.Length) throw new InvalidOperationException("IL runs past its end");
                l.Add(i);
            }
            return l;
        }

        // Can the callee write through its argument `fromEnd` places from the last one (list: a list; else a ref)? null = yes.
        public delegate bool Callee(MemberInfo callee, int fromEnd, bool list);

        // The list loaded at ins[x] is only read, removed from, or passed to a callee that does no more (callee check).
        static bool ListSafe(List<Ins> ins, int x, Resolver resolve, Callee callee)
        {
            int y = x + 1, pushes = 0; // pushes: values above the list
            for (; y < ins.Count && y <= x + 12; y++)
            {
                int o = ins[y].Op;
                if (SimplePush(o)) { pushes++; continue; }
                if (o == 0x7B && pushes >= 1) continue; // ldfld of an argument
                if (o != 0x28 && o != 0x6F) break;
                MethodInfo mb = resolve(ins[y].Tok) as MethodInfo; // a call that takes only values pushed after the list
                if (mb == null) break;
                int pops = mb.GetParameters().Length + (mb.IsStatic ? 0 : 1);
                if (pops > pushes) break;
                pushes += (mb.ReturnType != typeof(void) ? 1 : 0) - pops;
            }
            if (y >= ins.Count) return false;
            int c = ins[y].Op;
            if (pushes == 0 && (c == 0x2C || c == 0x2D || c == 0x39 || c == 0x3A || c == 0x26)) return true;
            if (pushes == 1 && y == x + 2 && ins[x + 1].Op == 0x14 && (c == 0xFE01 || c == 0xFE03 || c == 0x2E || c == 0x33 || c == 0x3B || c == 0x40)) return true;
            if (c == 0x73) return ReadUse(resolve(ins[y].Tok), pushes, true);
            if (c != 0x28 && c != 0x6F) return false;
            MemberInfo m = resolve(ins[y].Tok);
            return ReadUse(m, pushes, false) || (pushes == 0 && SourceUse(m)) || (callee != null && m != null && callee(m, pushes, true));
        }

        // The ref loaded at ins[x] (ldflda / ldarg of a ref parameter) is only read or cleared: initobj, `ldnull; stind.ref`,
        // ldind.ref, or passed to a callee that does no more.
        static bool RefSafe(List<Ins> ins, int x, Resolver resolve, Callee callee)
        {
            int y = x + 1;
            if (y >= ins.Count) return false;
            int c = ins[y].Op;
            if (c == 0xFE15 || c == 0x50) return true;
            if (c == 0x14 && y + 1 < ins.Count && ins[y + 1].Op == 0x51) return true;
            int pushes = 0;
            while (y < ins.Count && SimplePush(ins[y].Op) && pushes < 4) { y++; pushes++; }
            if (y >= ins.Count || callee == null || (ins[y].Op != 0x28 && ins[y].Op != 0x6F)) return false;
            MemberInfo m = resolve(ins[y].Tok);
            return m != null && callee(m, pushes, false);
        }

        // Kinds this body writes. fields: target "Type::field" name -> kind; lists: the targets that are lists; callee: the
        // check for a target passed on (null: passing it on is a write).
        public static int ScanIl(byte[] il, Resolver resolve, Dictionary<string, int> fields, Dictionary<string, bool> lists, Callee callee)
        {
            return ScanIl(il, resolve, fields, lists, callee, null);
        }

        // LC-7f: where the last ScanIl call found its writes, for the ts3native.log line: up to 2 of
        // "IL_<offset> <opcode> <Type::field> <- / -> <the member it is stored from / passed to>", "; "-joined; null = none
        public static string Hit;

        static string OpName(int op)
        {
            switch (op)
            {
                case 0x7D: return "stfld"; case 0x80: return "stsfld"; case 0x7C: return "ldflda"; case 0x7F: return "ldsflda";
                case 0x7B: return "ldfld"; case 0x7E: return "ldsfld"; default: return "op" + op.ToString("x");
            }
        }

        // the member of a call / callvirt / newobj at ins[y], or its opcode
        static string Through(List<Ins> ins, int y, Resolver resolve)
        {
            if (y < 0 || y >= ins.Count) return "end";
            int o = ins[y].Op;
            if (o != 0x28 && o != 0x6F && o != 0x73) return OpName(o);
            string n = null;
            try
            {
                MemberInfo mi = resolve(ins[y].Tok);
                n = AutShadowCore.MemberName(mi); // null for generic declaring types (List`1): name them plainly
                if (n == null && mi != null) n = (mi.DeclaringType != null ? GenericName(mi.DeclaringType) + "::" : "") + mi.Name;
            }
            catch (Exception) { }
            return (o == 0x73 ? "newobj " : "call ") + (n ?? "token " + ins[y].Tok.ToString("x8"));
        }

        static void NoteHit(List<Ins> ins, int x, string name, Resolver resolve)
        {
            if (Hit != null && Hit.Split(';').Length >= 2) return;
            int op = ins[x].Op;
            string via;
            if (op == 0x7D || op == 0x80) via = " <- " + Through(ins, x - 1, resolve);
            else
            {
                int y = x + 1;
                while (y < ins.Count && y <= x + 12 && ins[y].Op != 0x28 && ins[y].Op != 0x6F && ins[y].Op != 0x73) y++;
                via = " -> " + Through(ins, y < ins.Count && y <= x + 12 ? y : x + 1, resolve);
            }
            string h = "IL_" + ins[x].At.ToString("x4") + " " + OpName(op) + " " + name + via;
            Hit = Hit == null ? h : Hit + "; " + h;
        }

        // strings: ldstr token -> string (null: no reflection guard)
        public static int ScanIl(byte[] il, Resolver resolve, Dictionary<string, int> fields, Dictionary<string, bool> lists, Callee callee, Str strings)
        {
            Hit = null;
            Dictionary<int, bool> targets = new Dictionary<int, bool>();
            List<Ins> ins = Decode(il, targets);
            int mask = 0, named = 0;
            bool byName = false;
            Dictionary<string, int> names = strings != null ? FieldNames(fields) : null;
            for (int x = 0; x < ins.Count; x++)
            {
                int op = ins[x].Op;
                if (strings != null)
                {
                    if (op == 0x72) { string s = strings(ins[x].Tok); int k; if (s != null && names.TryGetValue(s, out k)) named |= k; continue; }
                    uint tt = (uint)ins[x].Tok >> 24;
                    if ((op == 0x28 || op == 0x6F || op == 0x73) && (tt == 0x0A || tt == 0x2B)) // reflection lives in mscorlib: MemberRef / MethodSpec
                    {
                        int g = Reflective(resolve(ins[x].Tok));
                        if (g == 2) { mask = KAll; Reflect = true; Hit = (Hit == null ? "" : Hit + "; ") + "IL_" + ins[x].At.ToString("x4") + " " + Through(ins, x, resolve) + " (every kind)"; }
                        else if (g == 1 && !byName) { byName = true; Hit = (Hit == null ? "" : Hit + "; ") + "IL_" + ins[x].At.ToString("x4") + " " + Through(ins, x, resolve) + " (by name)"; }
                        continue;
                    }
                }
                bool store = op == 0x7D || op == 0x80, addr = op == 0x7C || op == 0x7F, load = op == 0x7B || op == 0x7E;
                if (!store && !addr && !load) continue;
                string name = AutShadowCore.MemberName(resolve(ins[x].Tok));
                int kind;
                if (name == null || !fields.TryGetValue(name, out kind) || (mask & kind) == kind) continue;
                if (store)
                {
                    bool nothing = x > 0 && !targets.ContainsKey(ins[x].At) && (ins[x - 1].Op == 0x14 || (ins[x - 1].Op == 0x73 && EmptyList(resolve(ins[x - 1].Tok))));
                    if (!nothing) { mask |= kind; NoteHit(ins, x, name, resolve); }
                    continue;
                }
                if (addr) { if (!RefSafe(ins, x, resolve, callee)) { mask |= kind; NoteHit(ins, x, name, resolve); } continue; }
                if (lists.ContainsKey(name) && !ListSafe(ins, x, resolve, callee)) { mask |= kind; NoteHit(ins, x, name, resolve); }
            }
            if (byName && named != 0) { mask |= named; Reflect = true; }
            return mask;
        }

        public delegate string Str(int token);

        // 2 = writes anything (Type.InvokeMember, System.Reflection.Emit), 1 = reaches a member by name (Type.GetField /
        // GetProperty / GetMember, FieldInfo / PropertyInfo.SetValue / SetValueDirect), else 0
        static int Reflective(MemberInfo mi)
        {
            MethodBase m = mi as MethodBase;
            if (m == null || m.DeclaringType == null) return 0;
            string ns = m.DeclaringType.Namespace, tn = m.DeclaringType.FullName;
            if (ns == "System.Reflection.Emit") return 2;
            if (tn != "System.Type" && ns != "System.Reflection") return 0;
            if (m.Name == "InvokeMember") return 2;
            return m.Name == "GetField" || m.Name == "GetProperty" || m.Name == "GetMember" || m.Name == "SetValue" || m.Name == "SetValueDirect" ? 1 : 0;
        }

        static Dictionary<string, int> sNamesFor, sNames;
        // target member name ("Type::field" -> "field") -> kinds
        static Dictionary<string, int> FieldNames(Dictionary<string, int> fields)
        {
            if (sNamesFor == fields) return sNames;
            Dictionary<string, int> d = new Dictionary<string, int>();
            foreach (KeyValuePair<string, int> kv in fields)
            {
                string n = kv.Key.Substring(kv.Key.LastIndexOf(':') + 1);
                int k; d.TryGetValue(n, out k); d[n] = k | kv.Value;
            }
            sNamesFor = fields; sNames = d;
            return d;
        }

        static int ArgIndex(int op, byte[] il, int at)
        {
            if (op >= 0x02 && op <= 0x05) return op - 0x02;
            if (op == 0x0E) return il[at + 1];
            if (op == 0xFE09) return BitConverter.ToUInt16(il, at + 2);
            return -1;
        }

        // A callee's body: every use of the argument is safe (ListSafe / RefSafe without a further callee), it is never stored
        // to (starg) or its address taken (ldarga). Only non-virtual (or final) methods with IL, outside EA's core.
        public static bool CalleeKeeps(MemberInfo mi, int fromEnd, bool list, BodyReader body, Dictionary<Module, Dictionary<int, MemberInfo>> cache, Predicate<Assembly> core)
        {
            MethodBase m = mi as MethodBase;
            if (m == null || (m.IsVirtual && !m.IsFinal) || (core != null && core(m.Module.Assembly))) return false;
            MethodInfo gm = m as MethodInfo;
            if (gm != null && gm.IsGenericMethod && !gm.IsGenericMethodDefinition) m = gm.GetGenericMethodDefinition();
            if (NoBody(m)) return false;
            int pos = m.GetParameters().Length - 1 - fromEnd;
            if (pos < 0) return false;
            int arg = pos + (m.IsStatic ? 0 : 1);
            byte[] il = body(m);
            if (il == null) return false;
            List<Ins> ins = Decode(il, new Dictionary<int, bool>());
            Resolver r = For(m, cache);
            for (int x = 0; x < ins.Count; x++)
            {
                int op = ins[x].Op;
                if (op == 0x0F || op == 0xFE0A || op == 0x10 || op == 0xFE0B) { if (ArgIndex(op == 0x0F ? 0x0E : op == 0x10 ? 0x0E : 0xFE09, il, ins[x].At) == arg) return false; continue; }
                if (ArgIndex(op, il, ins[x].At) != arg) continue;
                if (!(list ? ListSafe(ins, x, r, null) : RefSafe(ins, x, r, null))) return false;
            }
            return true;
        }

        // No IL to read (as Fixes.NoBody): abstract, pinvoke, icall / runtime / native.
        public static bool NoBody(MethodBase m)
        {
            return m.IsAbstract || (m.Attributes & MethodAttributes.PinvokeImpl) != 0
                || (m.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime | MethodImplAttributes.Native)) != 0;
        }

        // Resolver over m's module, members cached per module and token. TS3's mscorlib has no ResolveMember with a generic
        // context, so a token that fails inside a generic type or method is taken as a member of a generic type (its parent
        // spec names a type parameter): never a target, whose declaring types are not generic [inferred: compilers do not
        // emit a TypeSpec parent for a non-generic type]. A token whose member or type is missing in this runtime (Missing)
        // resolves to null and sets Unresolved: that instruction cannot run, so it writes nothing (LC-7c). Any other failure
        // without a generic context throws (unreadable).
        public static Resolver For(MethodBase m, Dictionary<Module, Dictionary<int, MemberInfo>> cache)
        {
            Module mod = m.Module;
            Dictionary<int, MemberInfo> names;
            if (!cache.TryGetValue(mod, out names)) cache[mod] = names = new Dictionary<int, MemberInfo>();
            Type dt = m.DeclaringType;
            bool generic = (dt != null && (dt.IsGenericType || dt.ContainsGenericParameters)) || m.IsGenericMethod || m.IsGenericMethodDefinition;
            return delegate(int tok)
            {
                MemberInfo mi;
                if (names.TryGetValue(tok, out mi)) { if (mi == null) Unresolved = true; return mi; }
                try { mi = mod.ResolveMember(tok); }
                catch (Exception e)
                {
                    if (generic) return null;
                    if (!Missing(e, tok)) throw;
                    Unresolved = true;
                    names[tok] = null;
                    return null;
                }
                names[tok] = mi;
                return mi;
            };
        }

        // A member or type reference that does not resolve: a token of a member / type table (a corrupt table or an out-of-range
        // row is not this) that Mono's Module.resolve_token_exception reports as ArgumentException (its non-OutOfRange case; TS3's
        // mscorlib has resolve_token_exception / ResolveTokenError [verified strings]), or .NET's Missing* / TypeLoadException
        // (offline tool). Mono's interpreter transforms a method's whole body at its first call (transform.c, Mono 1.2.3
        // [community]) and cannot resolve the token there either [inferred, I-602-6], so the instruction never runs.
        public static bool Missing(Exception e, int tok)
        {
            uint t = (uint)tok >> 24;
            if (t != 0x01 && t != 0x02 && t != 0x04 && t != 0x06 && t != 0x0A && t != 0x1B && t != 0x2B) return false;
            if (e is ArgumentOutOfRangeException) return false;
            return e.GetType() == typeof(ArgumentException) || e is MissingMemberException || e is TypeLoadException;
        }

        public static bool Unresolved, Reflect; // the last method met an unresolvable token / a reflection or emit hit

        static readonly Dictionary<Module, Dictionary<int, string>> sStrings = new Dictionary<Module, Dictionary<int, string>>();
        // ldstr resolver over m's module (cleared after each ScanTypes: no cache outlives a scan). An unreadable string is no name.
        public static Str StringsFor(MethodBase m)
        {
            Module mod = m.Module;
            Dictionary<int, string> d;
            if (!sStrings.TryGetValue(mod, out d)) sStrings[mod] = d = new Dictionary<int, string>();
            return delegate(int tok)
            {
                string s;
                if (d.TryGetValue(tok, out s)) return s;
                try { s = mod.ResolveString(tok); } catch (Exception) { s = null; }
                d[tok] = s;
                return s;
            };
        }

        // LC-7c pinned writers (docs/336 §12): a method whose IL hash (AutShadowCore.MethodIlHash over its SigKey, as
        // tools/autshadow-il-hashes.ts --hash-file prints it) equals the pin is covered, by a hook that notes its holder (Hook
        // set, DeadRefsDescRev.cs) or without a hook (Hook null: the kGotPregnant listener notes the holder, or the body only
        // removes, read in the source). Asm null: any assembly (NRaas Common copies).
        public sealed class Pin
        {
            public readonly string Asm, Type, Name, Key, Hook;
            public readonly ulong Hash;
            public readonly int Kind;
            public Pin(string asm, string type, string name, string sig, ulong hash, int kind, string hook)
            {
                Asm = asm; Type = type; Name = name; Key = type + "::" + name + sig; Hash = hash; Kind = kind; Hook = hook;
            }
        }

        const string kSd = "Sims3.Gameplay.CAS.SimDescription", kPreg = "Sims3.Gameplay.ActorSystems.Pregnancy";
        public static readonly Pin[] Pins = {
            // M1 SP Career V269: FormerBoss = Boss (NewBossScenario.cs:225)
            new Pin("NRaasStoryProgressionCareer", "NRaas.StoryProgressionSpace.Scenarios.Careers.NewBossScenario", "SetBoss", "(Sims3.Gameplay.Careers.Career," + kSd + ")", 0xeb5d81662c6a16fcUL, KFormerBoss, "SetBoss"),
            // M2 SP Skill V269: mChessRankingNextChallenger (ChessPushScenario.cs:228)
            new Pin("NRaasStoryProgressionSkill", "NRaas.StoryProgressionSpace.Scenarios.Skills.ChessPushScenario", "FindNextOpponent", "(" + kSd + ")", 0xff242d38a28d4b0aUL, KChess, "Chess"),
            // M3 SP V269: pregnancy.mDad (AttachLostFathersScenario.cs:139)
            new Pin("NRaasStoryProgression", "NRaas.StoryProgressionSpace.Scenarios.Pregnancies.AttachLostFathersScenario", "PrivateUpdate", "(NRaas.StoryProgressionSpace.Scenarios.ScenarioFrame)", 0x0a061f3384c96e95UL, KPregnancy, "LostFather"),
            // M4 NRaas Common (SP V269, Woohooer V130 copies): SimDescription.Pregnancy, then kGotPregnant (Pregnancies.cs:56-58)
            new Pin(null, "NRaas.CommonSpace.Helpers.Pregnancies", "Start", "(Sims3.Gameplay.Actors.Sim," + kSd + ",bool)", 0xb250e0d1b00580ccUL, KPregnancy, null),
            // ErrorTrap (test install): Coworkers handed to its generic Remove<S>(List<S>, ...), which only RemoveAt's
            // (Dereference.cs:231-247); FormerBoss through Remove<S>(ref S) = default (DerefOccupation.cs:24-42)
            new Pin("NRaasErrorTrap", "NRaas.ErrorTrapSpace.Dereferences.DerefOccupation", "Perform", "(Sims3.Gameplay.Careers.Occupation,System.Reflection.FieldInfo,System.Collections.Generic.List`1<NRaas.CommonSpace.Helpers.ReferenceWrapper>)", 0x54b286e509c5a4d5UL, KCoworkers | KFormerBoss, null),
            // LC-7d (docs/336 §13): MasterController V135's copy (same token sequence as the SP / Woohooer copy, other IL
            // layout): the assignment, then kGotPregnant
            new Pin(null, "NRaas.CommonSpace.Helpers.Pregnancies", "Start", "(Sims3.Gameplay.Actors.Sim," + kSd + ",bool)", 0xf6d8bc515de063fdUL, KPregnancy, null),
            // CareerManager.mJob / mSchool (a job object swapped in holds its Coworkers / FormerBoss): NRaas Common SafeStore.Dispose
            // restores the stored job / school of mSim (SafeStore.cs:103-110; 16 copies, 3 builds in the 2026-11-22 downloads)
            new Pin(null, "NRaas.CommonSpace.Stores.SafeStore", "Dispose", "()", 0x31a12af3e91a167bUL, KCoworkers | KFormerBoss, "SafeStore"),
            new Pin(null, "NRaas.CommonSpace.Stores.SafeStore", "Dispose", "()", 0x2af226791b5c1c03UL, KCoworkers | KFormerBoss, "SafeStore"),
            new Pin(null, "NRaas.CommonSpace.Stores.SafeStore", "Dispose", "()", 0x586571c133c70b15UL, KCoworkers | KFormerBoss, "SafeStore"),
            new Pin(null, "NRaas.CommonSpace.Helpers.CareerManagerEx", "ImportContent", "(Sims3.Gameplay.Careers.CareerManager,Sims3.SimIFace.CustomContent.ResKeyTable,Sims3.SimIFace.CustomContent.ObjectIdTable,Sims3.SimIFace.IPropertyStreamReader)", 0x3c95f55f82762f53UL, KCoworkers | KFormerBoss, "Dirty"),
            new Pin(null, "NRaas.CommonSpace.Helpers.CareerManagerEx", "ImportContent", "(Sims3.Gameplay.Careers.CareerManager,Sims3.SimIFace.CustomContent.ResKeyTable,Sims3.SimIFace.CustomContent.ObjectIdTable,Sims3.SimIFace.IPropertyStreamReader)", 0xa9d3565e6e884bfdUL, KCoworkers | KFormerBoss, "Dirty"),
            new Pin(null, "NRaas.CommonSpace.Helpers.CareerManagerEx", "ImportContent", "(Sims3.Gameplay.Careers.CareerManager,Sims3.SimIFace.CustomContent.ResKeyTable,Sims3.SimIFace.CustomContent.ObjectIdTable,Sims3.SimIFace.IPropertyStreamReader)", 0xcc6aa2d1b8805a95UL, KCoworkers | KFormerBoss, "Dirty"),
            new Pin(null, "NRaas.CommonSpace.Helpers.CareerManagerEx", "ImportContent", "(Sims3.Gameplay.Careers.CareerManager,Sims3.SimIFace.CustomContent.ResKeyTable,Sims3.SimIFace.CustomContent.ObjectIdTable,Sims3.SimIFace.IPropertyStreamReader)", 0xf5bf22ea969125beUL, KCoworkers | KFormerBoss, "Dirty"),
            new Pin("NRaasStoryProgressionCareer", "NRaas.StoryProgressionSpace.Scenarios.Careers.AssignSchoolScenario", "PrivateUpdate", "(NRaas.StoryProgressionSpace.Scenarios.ScenarioFrame)", 0xe0bdd3019b82cfacUL, KCoworkers | KFormerBoss, "Dirty"),
            new Pin("NRaasMasterController", "NRaas.MasterControllerSpace.Sims.Intermediate.Careers.ChooseCareerOption+CareerItem", "Perform", "(" + kSd + ",bool)", 0xeaa939215e60b838UL, KCoworkers | KFormerBoss, "Dirty"),
            new Pin("NRaasTraveler", "NRaas.TravelerSpace.Helpers.SimDescriptionEx", "MergeTravelInformation", "(" + kSd + "," + kSd + ",bool)", 0xfb709fc976d65070UL, KCoworkers | KFormerBoss, "Dirty"),
            new Pin("NRaasCareer", "NRaas.CareerSpace.Booters.SchoolBooter", "Enroll", "(Sims3.Gameplay.Actors.Sim,string,Sims3.Gameplay.Careers.CareerLocation)", 0xb9acadafe36b18f6UL, KCoworkers | KFormerBoss, "Dirty"),
            // M6 NRaas Careers V87: CareerOmniValue`1.Set writes Coworkers / FormerBoss of its own proxy career (new T() in its
            // constructor, kept only in OmniCareer.mValues); no CareerManager ever holds a proxy (every mJob / mSchool store in
            // NRaas is listed above), so phase 4 never walks it. EA writers called on a proxy note proxy.OwnerDescription, the real
            // owner (Init copies it), whose list the proxy shares. Covered without a hook.
            new Pin("NRaasCareer", "NRaas.Gameplay.Careers.OmniCareer+CareerOmniValue`1", "Set", "(NRaas.Gameplay.Careers.OmniCareer)", 0x082b80cdd982f8baUL, KCoworkers | KFormerBoss, null),
            // M5 Woohooer V130: SimDescription.Pregnancy restored (MaleHaveBabyHomeEx.cs:120-129), Pregnancy.mDad copied (PregnancyProxy.cs:51)
            new Pin("NRaasWoohooer", "NRaas.WoohooerSpace.Interactions.MaleHaveBabyHomeEx", "Cleanup", "()", 0xd6c3b6cccf19b9b5UL, KPregnancy, "BabyCleanup"),
            new Pin("NRaasWoohooer", "NRaas.WoohooerSpace.Proxies.PregnancyProxy", "CopyPregnancy", "(" + kPreg + "," + kPreg + ")", 0x8f541ac5e93fe2c5UL, KPregnancy, "CopyPregnancy"),
            // LC-7f (docs/336 §15): simler90 Gameplay Core Mod UPDATE242 (test install): its SimDescription.MergeTravelInformation calls
            // Other.SimDescriptionMergeTravelInfoCareer, which stores a Clone of the imported Sim's School in mSchool (and its
            // job / retired career into QuitCareers / mRetiredCareer, which phase 4 does not walk). LC-7g (docs/336 §16): no body swap
            // (the EA 1.69 travel return stalled in ImportTravellingHousehold with it); it writes only in a travel-based world
            // (its first test), so there its kinds count as untrusted (DdGather), elsewhere it is covered
            new Pin("simler90Extras", "simler90Extras.Other", "SimDescriptionMergeTravelInfoCareer", "(" + kSd + "," + kSd + ")", 0xc88127ea403a709cUL, KCoworkers | KFormerBoss, "TravelWorld"),
        };

        // The method p names in a (declared, by SigKey), or null
        public static MethodBase PinTarget(Assembly a, Pin p)
        {
            if (p.Asm != null && p.Asm != a.GetName().Name) return null;
            Type t = a.GetType(p.Type, false);
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(kAll)) if (m.Name == p.Name && AutShadowCore.SigKey(m) == p.Key) return m;
            return null;
        }

        // The pins a's methods match by hash (one key may carry several pins, one per known build: any equal hash matches); a
        // pinned method whose hash equals none goes to mismatched (it stays scanned).
        public static List<KeyValuePair<Pin, MethodBase>> MatchPins(Assembly a, Pin[] pins, List<string> mismatched)
        {
            List<KeyValuePair<Pin, MethodBase>> l = new List<KeyValuePair<Pin, MethodBase>>();
            Dictionary<Module, Dictionary<int, string>> hc = new Dictionary<Module, Dictionary<int, string>>();
            Dictionary<string, bool> done = new Dictionary<string, bool>();
            foreach (Pin p in pins)
            {
                if (done.ContainsKey(p.Key)) continue;
                MethodBase m;
                try { m = PinTarget(a, p); } catch (Exception) { m = null; }
                if (m == null) continue;
                done[p.Key] = true;
                ulong h;
                try { h = AutShadowCore.MethodIlHash(p.Key, m, hc); } catch (Exception) { h = 0; }
                Pin hit = null;
                foreach (Pin q in pins) if (q.Key == p.Key && q.Hash == h && (q.Asm == null || q.Asm == a.GetName().Name)) { hit = q; break; }
                if (hit != null) l.Add(new KeyValuePair<Pin, MethodBase>(hit, m));
                else if (mismatched != null) mismatched.Add(a.GetName().Name + " " + p.Key);
            }
            return l;
        }

        // m is not scanned: allowed["Type::Name"] = false (only the SigKeys listed) + allowed[SigKey] = true
        public static void Cover(Dictionary<string, bool> allowed, MethodBase m)
        {
            string k = m.DeclaringType.FullName + "::" + m.Name;
            if (!allowed.ContainsKey(k)) allowed[k] = false;
            allowed[AutShadowCore.SigKey(m)] = true;
        }

        public sealed class Result
        {
            public int Mask, Assemblies, Methods, Unreadable, Unresolved, Reflect;
            public readonly int[] PerKind = new int[4];
            public readonly List<string> Log = new List<string>();
            public void Add(int kinds, string where, int logMax)
            {
                Mask |= kinds;
                for (int k = 0; k < 4; k++) if ((kinds & (1 << k)) != 0) PerKind[k]++;
                if (Log.Count < logMax) Log.Add(where + " writes " + Kinds(kinds));
            }
        }

        public static string Kinds(int mask)
        {
            if (mask == 0) return "none";
            List<string> l = new List<string>();
            for (int k = 0; k < 4; k++) if ((mask & (1 << k)) != 0) l.Add(KindNames[k]);
            return string.Join("+", l.ToArray());
        }

        public delegate byte[] BodyReader(MethodBase m); // null = no body; throws = unreadable

        public static byte[] ReflectionBody(MethodBase m)
        {
            MethodBody b = m.GetMethodBody();
            return b != null ? b.GetILAsByteArray() : null;
        }

        // EA's core assemblies (callees there are not followed); set by the caller
        public static Predicate<Assembly> Core;
        public static string LastError; // the last unreadable method's exception (log)

        // One method: its kinds, or KAll when a method that must have IL cannot be read. A target passed on is followed one
        // call deep (CalleeKeeps); a callee that cannot be read counts as a write of that kind.
        public static int ScanMethod(MethodBase m, BodyReader body, Dictionary<Module, Dictionary<int, MemberInfo>> cache, Dictionary<string, int> fields, Dictionary<string, bool> lists, out bool unreadable)
        {
            unreadable = false;
            Unresolved = Reflect = false;
            Hit = null;
            try
            {
                if (NoBody(m)) return 0;
                byte[] il = body(m);
                if (il == null) { unreadable = true; LastError = "no IL"; return KAll; }
                return ScanIl(il, For(m, cache), fields, lists, delegate(MemberInfo c, int fromEnd, bool list)
                {
                    try { return CalleeKeeps(c, fromEnd, list, body, cache, Core); }
                    catch (Exception) { return false; }
                }, StringsFor(m));
            }
            catch (Exception ex) { unreadable = true; LastError = ex.GetType().Name + ": " + ex.Message; return KAll; }
        }

        const BindingFlags kAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // Every method and constructor of asm, less skipType's types and allowed "Full.Type::Method" keys. An assembly whose
        // types or methods cannot be listed counts as a writer of every kind.
        public static void ScanAssembly(Assembly asm, Predicate<Type> skipType, Dictionary<string, bool> allowed, BodyReader body,
            Dictionary<string, int> fields, Dictionary<string, bool> lists, Result r, int logMax)
        {
            r.Assemblies++;
            string an = asm.GetName().Name;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (Exception) { r.Unreadable++; r.Add(KAll, an + " (types unreadable)", logMax); return; }
            ScanTypes(an, types, skipType, allowed, body, fields, lists, r, logMax);
        }

        public static void ScanTypes(string an, Type[] types, Predicate<Type> skipType, Dictionary<string, bool> allowed, BodyReader body,
            Dictionary<string, int> fields, Dictionary<string, bool> lists, Result r, int logMax)
        {
            Dictionary<Module, Dictionary<int, MemberInfo>> cache = new Dictionary<Module, Dictionary<int, MemberInfo>>();
            foreach (Type t in types)
            {
                if (t == null || (skipType != null && skipType(t))) continue;
                List<MethodBase> ms = new List<MethodBase>();
                try { ms.AddRange(t.GetMethods(kAll)); ms.AddRange(t.GetConstructors(kAll)); }
                catch (Exception) { r.Unreadable++; r.Add(KAll, an + " " + t.FullName + " (methods unreadable)", logMax); continue; }
                foreach (MethodBase m in ms)
                {
                    string key = t.FullName + "::" + m.Name;
                    bool all;
                    if (allowed != null && allowed.TryGetValue(key, out all) && (all || allowed.ContainsKey(AutShadowCore.SigKey(m)))) continue;
                    r.Methods++;
                    bool bad;
                    int k = ScanMethod(m, body, cache, fields, lists, out bad);
                    if (bad) r.Unreadable++;
                    if (Unresolved) r.Unresolved++;
                    if (Reflect) r.Reflect++;
                    if (k != 0) r.Add(k, an + " " + key + (bad ? " (unreadable: " + LastError + ")" : Reflect ? " (reflection)" : "") + (Hit != null ? " at " + Hit : ""), logMax);
                }
            }
            sStrings.Clear();
        }
    }
}
