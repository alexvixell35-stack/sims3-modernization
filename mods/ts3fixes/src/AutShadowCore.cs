using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ts3Fixes
{
    // Game-independent compare rules of the [AutShadow] pilot (AutShadow.cs, docs/122 §5), kept free of game types so the
    // offline self-test (tools/selftest/autshadow) compiles them alone.
    public static class AutShadowCore
    {
        // Result codes per (meta object, ad): docs/122 §4.2. Filtered = the game's commodity filter drops the ad (not scored
        // by the game either, so never compared or counted).
        public const int kPass = 0, kTestFailed = 1, kSkip = 2, kAbort = 3, kFiltered = -1;
        // Mode R classes (docs/122 §5.1). A5 comes from the native run status, not from a pair.
        public const int A0 = 0, A1 = 1, A2 = 2, A3 = 3, A4 = 4, A5 = 5, kNoClass = -1;

        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits { [FieldOffset(0)] public float F; [FieldOffset(0)] public int I; }

        public static int Bits(float f) { FloatBits b = new FloatBits(); b.F = f; return b.I; }

        // Distance in units in the last place: floats mapped onto one ordered integer line (+0 and -0 both at 0). NaN
        // payloads map like any other bit pattern, so two different NaNs are far apart.
        public static long UlpDistance(float a, float b)
        {
            long x = Ordered(Bits(a)), y = Ordered(Bits(b));
            return x > y ? x - y : y - x;
        }

        // docs/218 §5 content guard: which loaded game modules are checked builds. names / shas: the modules (sha = lower-case
        // hex SHA-256 of the bytes). Null when a sha is missing (the guard cannot run: MVID gate). Sims3GameplaySystems and
        // Sims3GameplayObjects count only as a pair of one set (G-M1, set = its index, else -1 and neither counts); every
        // other module by its own hash in others.
        public static bool[] ContentAccepted(string[] names, string[] shas, string[][] pairSets, string[] others, out int set)
        {
            set = -1;
            if (names.Length == 0) return null;
            foreach (string s in shas) if (s == null) return null;
            int sgs = Array.IndexOf(names, "Sims3GameplaySystems"), sgo = Array.IndexOf(names, "Sims3GameplayObjects");
            if (sgs >= 0 && sgo >= 0)
                for (int k = 0; k < pairSets.Length && set < 0; k++)
                    if (shas[sgs] == pairSets[k][0] && shas[sgo] == pairSets[k][1]) set = k;
            bool[] ok = new bool[names.Length];
            for (int i = 0; i < names.Length; i++)
                ok[i] = i == sgs || i == sgo ? set >= 0 : Array.IndexOf(others, shas[i]) >= 0;
            return ok;
        }

        public static string Hex(byte[] b)
        {
            const string d = "0123456789abcdef";
            char[] c = new char[b.Length * 2];
            for (int i = 0; i < b.Length; i++) { c[2 * i] = d[b[i] >> 4]; c[2 * i + 1] = d[b[i] & 15]; }
            return new string(c);
        }

        // MVID as metadata stores it (16 bytes in hex, docs/data/autonomy-allow.csv) to Guid.ToString("N"): the first three
        // fields are little-endian, so their bytes are reversed.
        public static string GuidNFromRaw(string raw)
        {
            int[] order = { 3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15 };
            char[] n = new char[32];
            for (int i = 0; i < 16; i++) { n[2 * i] = raw[2 * order[i]]; n[2 * i + 1] = raw[2 * order[i] + 1]; }
            return new string(n);
        }

        // docs/124 §13.5: an L3 callee is one exact method (name + parameter types, as t.GetMethods sees it: declared or
        // inherited), never every overload with that name. Null when t has no such method.
        public static MethodInfo FindExact(Type t, string name, Type[] p)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodInfo m in t.GetMethods(all))
            {
                if (m.Name != name) continue;
                ParameterInfo[] q = m.GetParameters();
                if (q.Length != p.Length) continue;
                int i = 0;
                while (i < q.Length && q[i].ParameterType == p[i]) i++;
                if (i == q.Length) return m;
            }
            return null;
        }

        static long Ordered(int i){ return i >= 0 ? i : (long)int.MinValue - i; }

        // ---- H11 (docs/127 §4): the rain/hail shelter branch of Autonomy.CalculateScoreForObjectInteraction ----
        // The game's order: autonomy search, Seasons on, weather rain or hail, then the target (outdoors, not a Sim). Each
        // probe runs only when the ones before it passed (CurrentWeather needs Seasons; IsOutside is an IC).
        public interface IRainProbe { bool SeasonsOn(); int Weather(); bool TargetIsSim(); bool TargetOutside(); }
        public static bool RainShelter(bool autonomySearch, IRainProbe p, int rain, int hail)
        {
            if (!autonomySearch || !p.SeasonsOn()) return false;
            int w = p.Weather();
            return (w == rain || w == hail) && !p.TargetIsSim() && p.TargetOutside();
        }

        // ---- tier-2 P1 (docs/219 §3.1, docs/data/tier2-rows35-handoff.md): get_InUse overrides that can destroy their prop,
        // the leaf-pile fire check (placement IC) and the drag-model pre-warm. The six overrides in counter-slot order
        // (fixes 884-889); 890 fire aborts, 891 drag pre-warm fills.
        public static readonly string[] kT2InUseTypes = {
            "Sims3.Gameplay.Objects.Gardening.Soil", "Sims3.Store.Objects.BBallHoop+BBallHoopBall",
            "Sims3.Store.Objects.BBallArcade+BBallArcadeBall", "Sims3.Store.Objects.ChocolateFountain+ChocolateFountainSkewer",
            "Sims3.Store.Objects.IceCreamMaker+IceCreamCone", "Sims3.Store.Objects.GardenStation+GardenStationProp" };
        public const int kT2Fire = 6, kT2N = 7;
        // Household scope of the request's actor, read on the sim thread at the request: null, destroyed, no description,
        // selectable (household or selected) or in the active household = household. Unknown scope counts as household.
        public static bool T2Household(bool actorNull, bool destroyed, bool descNull, bool selectable, bool activeHousehold)
        {
            return actorNull || destroyed || descNull || selectable || activeHousehold;
        }
        // A P1 hook site: 0 run the game's method (sim thread, reference, or switch off), 1 abort and count, 2 abort
        // (household-scope actor: never answered on the worker, not counted by class). Every worker branch with the switch on
        // aborts, so the unit goes back to the sim thread and its answer stays exact.
        public static int T2Site(bool onWorker, bool switchOn, bool household)
        {
            if (!onWorker || !switchOn) return 0;
            return household ? 2 : 1;
        }
        // tier-2 H7-H10 (docs/126 §3): counter slots 898-902 (Result.T2H): H8 `cache` aborts, H9 `tuning` aborts, H7 drag
        // answers, H10 LOD answers, H10 answers the game would have recomputed (more than 60 ticks since its last check)
        public const int kT2HCache = 0, kT2HTuning = 1, kT2HDrag = 2, kT2HLod = 3, kT2HLodStale = 4, kT2HN = 5;
        // H8: the worker's no-op answer only for the request's own autonomy whose search cache was on at the request
        public static bool T2CacheNoop(bool requestAutonomy, bool cacheActiveAtRequest) { return requestAutonomy && cacheActiveAtRequest; }
        // H10: the game recomputes when now > last check + 60 ticks; the worker answers the stored flag either way
        public static bool T2LodStale(long now, long lastCheck) { return now > lastCheck + 60; }
        // tier-2 P2 (docs/data/tier2-rows35-handoff.md §2): counter slots 938-943 (Result.T2P2; slot 0 is counted on the sim
        // thread): CauseEffect data pre-warm fills, CauseEffect worker aborts (data still null), front-door answers, front-door
        // aborts (lot not memoized at the request, or the game would validate or recompute), BuyModeSuite answers, its aborts (null)
        public const int kT2P2CeWarm = 0, kT2P2CeAbort = 1, kT2P2DoorAns = 2, kT2P2DoorAbort = 3, kT2P2SuiteAns = 4, kT2P2SuiteAbort = 5, kT2P2N = 6;
        // Lot.FindFrontDoor without a write: the exits of the game's GetFrontDoor(kFrontDoorTypeDefault) that change nothing.
        // 0 answer null, 1 the override door, 2 the calculated door, -1 the game would validate or recompute (it writes the
        // lot's flags, SetFrontDoor or street parking), so the worker aborts. *Set = the lot's door guid is valid, *IsDoor = it
        // resolves to a Door, *Ok = the lot's FrontDoor*Validated flag.
        public static int T2FrontDoor(bool useFrontDoor, bool overrideSet, bool overrideIsDoor, bool overrideOk, bool calcSet, bool calcIsDoor, bool calcOk)
        {
            if (!useFrontDoor) return 0;
            if (overrideSet) return overrideIsDoor && overrideOk ? 1 : -1;
            if (calcSet) return calcIsDoor && calcOk ? 2 : -1;
            return calcOk ? 0 : -1;
        }
        // tier-2 P3 CallPet Test site (counter 944 = runs of 2): 0 the game's Test as is (sim thread, reference, switch off, or
        // a != target: that branch writes no field), 1 abort (household-scope actor, uncounted), 2 the game's Test with the
        // shared mSimPickerList saved and put back after it (the worker's list lives only for that call)
        public static int T2CallPetSite(bool onWorker, bool switchOn, bool household, bool self)
        {
            if (!onWorker || !switchOn) return 0;
            if (household) return 1;
            return self ? 2 : 0;
        }

        // H11 shelter answer (docs/127 §4.1): instead of aborting, the worker answers the branch from a bit taken on the sim
        // thread at the cut. The branch asks IsShelteredFromPrecipitation(target, instance), which uses the instance only
        // for ICanCountAsBeingIndoors targets [verified, decompiled SeasonsManager]; for every other target it is the
        // position test, so one bit per meta object stands for it. 0 = no bit (Sim, indoors-capable, not outside, no rain):
        // the worker aborts `rain` as before; 1 sheltered; 2 not sheltered.
        public const byte kShelterNone = 0, kSheltered = 1, kUnsheltered = 2;
        public static byte ShelterBit(bool rainOrHail, bool isSim, bool countsIndoors, bool outside, bool sheltered)
        {
            if (!rainOrHail || isSim || countsIndoors || !outside) return kShelterNone;
            return sheltered ? kSheltered : kUnsheltered;
        }
        // the hooked IsShelteredFromPrecipitation(obj, ii) on the worker: 1 / 0 = answer, -1 = abort `rain`. Only the armed
        // target's skipped call (no instance) is answered; anything else on the worker never had a cut bit.
        public static int ShelterAnswer(bool instanceNull, bool armedTarget, byte bit)
        {
            if (!instanceNull || !armedTarget || bit == kShelterNone) return -1;
            return bit == kSheltered ? 1 : 0;
        }
        // after the score: the pair counts if the branch was not reached (no skip, no answer: meta CalculateScore outside local
        // autonomy) or exactly the one CreateInstance call was skipped and the hook answered it; anything else aborts `rain`
        public static bool ShelterDone(int skips, bool answered) { return skips == 0 ? !answered : skips == 1 && answered; }

        // ---- docs/128 G-M4 / G-M5: method keys in the call-graph index format (tools/managed-callgraph.ts sigType) ----

        static string Alias(string full)
        {
            switch (full)
            {
                case "System.Void": return "void"; case "System.Boolean": return "bool"; case "System.Char": return "char";
                case "System.SByte": return "sbyte"; case "System.Byte": return "byte"; case "System.Int16": return "short";
                case "System.UInt16": return "ushort"; case "System.Int32": return "int"; case "System.UInt32": return "uint";
                case "System.Int64": return "long"; case "System.UInt64": return "ulong"; case "System.Single": return "float";
                case "System.Double": return "double"; case "System.String": return "string"; case "System.Object": return "object";
                case "System.TypedReference": return "typedref"; case "System.IntPtr": return "IntPtr"; case "System.UIntPtr": return "UIntPtr";
                default: return null;
            }
        }

        // C# aliases, '+' for nesting, Name`N<args> for generic instances, & / * / [] / [,] suffixes, generic parameters
        // by name (the index writes !N; keys with ! are not in the generated lists).
        public static string TypeKey(Type t)
        {
            if (t.IsByRef) return TypeKey(t.GetElementType()) + "&";
            if (t.IsPointer) return TypeKey(t.GetElementType()) + "*";
            if (t.IsArray) return TypeKey(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsGenericParameter) return t.Name;
            if (t.IsGenericType && !t.IsGenericTypeDefinition)
            {
                Type[] a = t.GetGenericArguments();
                string[] s = new string[a.Length];
                for (int i = 0; i < a.Length; i++) s[i] = TypeKey(a[i]);
                return t.GetGenericTypeDefinition().FullName + "<" + string.Join(",", s) + ">";
            }
            string full = t.FullName ?? t.Name;
            return Alias(full) ?? full;
        }

        public static string ParamsKey(MethodBase m)
        {
            ParameterInfo[] p = m.GetParameters();
            string[] s = new string[p.Length];
            for (int i = 0; i < p.Length; i++) s[i] = TypeKey(p[i].ParameterType);
            return "(" + string.Join(",", s) + ")";
        }

        public static string SigKey(MethodBase m) { return TypeKey(m.DeclaringType) + "::" + m.Name + ParamsKey(m); }

        // ---- G-M6 (PR #112 / docs/215): closure method content hash; tools/autshadow-il-hashes.ts computes the same from the
        // assembly files. An MVID is not content (tiny-ui-fix rewrites UI / Sims3StoreObjects / SGS / SGO and keeps their
        // MVIDs), so every closure method's body is checked against the hashes of the builds the closures were checked on.
        // FNV-1a 64 over the closure key, the IL with each token operand replaced by the member's name (TokenName; null = the
        // raw token) and the EH clauses. Names, not token numbers: a re-written assembly with the same code hashes the same,
        // any IL, callee, field, type, string or handler change differs.
        // shortcut: locals signature and generic members (raw tokens) are not normalized; a re-numbered generic reference or a
        // changed local type alone is a false mismatch or a miss; normalize them if a mod build trips on it.
        public delegate string TokenName(int token);
        const ulong kFnvPrime = 1099511628211UL;
        public const ulong kFnvBasis = 14695981039346656037UL;
        static ulong Mix(ulong h, uint x) { return (h ^ x) * kFnvPrime; }
        static ulong MixS(ulong h, string s)
        {
            for (int i = 0; i < s.Length; i++) h = Mix(h, s[i]);
            return Mix(h, 0x10000);
        }
        static ulong MixTok(ulong h, string name, int tok) { return name != null ? MixS(Mix(h, 0x10001), name) : Mix(Mix(h, 0x10002), (uint)tok); }

        // Operand bytes of an opcode (ECMA-335 III): -1 = 4-byte token, -2 = switch table.
        static int Operand(int op)
        {
            if (op >= 0xFE00)
            {
                switch (op & 0xFF) { case 0x06: case 0x07: case 0x15: case 0x16: case 0x1C: return -1; case 0x12: case 0x19: return 1; }
                return (op & 0xFF) >= 0x09 && (op & 0xFF) <= 0x0E ? 2 : 0;
            }
            if ((op >= 0x0E && op <= 0x13) || op == 0x1F || op == 0xDE || (op >= 0x2B && op <= 0x37)) return 1;
            if ((op >= 0x38 && op <= 0x44) || op == 0x20 || op == 0x22 || op == 0xDD) return 4;
            if (op == 0x21 || op == 0x23) return 8;
            if (op == 0x45) return -2;
            switch (op)
            {
                case 0x27: case 0x28: case 0x29: case 0x6F: case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: case 0x79:
                case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F: case 0x80: case 0x81: case 0x8C: case 0x8D: case 0x8F:
                case 0xA3: case 0xA4: case 0xA5: case 0xC2: case 0xC6: case 0xD0: return -1;
            }
            return 0;
        }

        // il null = no body. eh: 6 ints per clause (flags, try offset, try length, handler offset, handler length, filter
        // offset); ehCatch: the catch type's name per clause (null = not a catch clause or a generic type).
        public static ulong IlHash(string key, byte[] il, int[] eh, string[] ehCatch, TokenName name)
        {
            ulong h = MixS(kFnvBasis, key);
            if (il == null) return Mix(h, 0x10004);
            int p = 0;
            while (p < il.Length)
            {
                int op = il[p++];
                h = Mix(h, (uint)op);
                if (op == 0xFE) { h = Mix(h, il[p]); op = 0xFE00 | il[p++]; }
                int n = Operand(op);
                if (n == -1) { int tok = BitConverter.ToInt32(il, p); h = MixTok(h, name(tok), tok); p += 4; continue; }
                if (n == -2) n = 4 + 4 * BitConverter.ToInt32(il, p);
                for (int e = p + n; p < e; p++) h = Mix(h, il[p]);
            }
            h = Mix(Mix(h, 0x10003), (uint)(ehCatch == null ? 0 : ehCatch.Length));
            for (int c = 0; ehCatch != null && c < ehCatch.Length; c++)
            {
                for (int k = 0; k < 5; k++) h = Mix(h, (uint)eh[c * 6 + k]);
                if (eh[c * 6] == 0) h = ehCatch[c] != null ? MixS(Mix(h, 0x10001), ehCatch[c]) : Mix(h, 0x10002);
                else if (eh[c * 6] == 1) h = Mix(h, (uint)eh[c * 6 + 5]);
            }
            return h;
        }

        // Names as the generator writes them: "Full.Type::Name(params)" (ParamsKey), "Full.Type::field", "Full.Type", a
        // string's text; null for generic types and members, arrays and tables without a name (TypeSpec, MethodSpec, sigs).
        public static string MemberName(object m)
        {
            Type t = m as Type;
            if (t != null) return t.IsGenericType || t.HasElementType || t.IsGenericParameter ? null : t.FullName;
            MemberInfo mi = m as MemberInfo;
            if (mi == null || mi.DeclaringType == null || mi.DeclaringType.IsGenericType || mi.DeclaringType.IsArray) return null;
            MethodBase mb = m as MethodBase;
            if (mb != null) return mb.IsGenericMethod || mb.IsGenericMethodDefinition ? null : mi.DeclaringType.FullName + "::" + mb.Name + ParamsKey(mb);
            return m is FieldInfo ? mi.DeclaringType.FullName + "::" + mi.Name : null;
        }
        // Fixes.Patch records each target's name before it swaps the MonoMethod header (klass and signature become the hook's,
        // so ResolveMember on a caller's token names the hook; rain-h11 2026-10-31: 15 closure callers of our hooks mismatched).
        public static readonly System.Collections.Generic.Dictionary<IntPtr, string> SwapNames = new System.Collections.Generic.Dictionary<IntPtr, string>();
        public static string ResolveName(Module mod, int tok)
        {
            try
            {
                switch ((uint)tok >> 24)
                {
                    case 0x70: return mod.ResolveString(tok);
                    case 0x01: case 0x02: return MemberName(mod.ResolveType(tok));
                    case 0x04: case 0x06: case 0x0A:
                        MemberInfo mi = mod.ResolveMember(tok);
                        // a header-swapped callee (Fixes.Patch) reads as its hook class: name it as before the swap
                        string sw;
                        if (mi is MethodBase && SwapNames.TryGetValue(((MethodBase)mi).MethodHandle.Value, out sw)) return sw;
                        return MemberName(mi);
                }
            }
            catch (Exception) { }
            return null;
        }
        // The runtime side: body from reflection, names through a per-module cache (shared callees resolve once).
        public static ulong MethodIlHash(string key, MethodBase m, System.Collections.Generic.Dictionary<Module, System.Collections.Generic.Dictionary<int, string>> cache)
        {
            return MethodIlHash(key, m, cache, null);
        }
        // trace: "token name" per token operand (G-M6 triage; tools/autshadow-il-hashes.ts ILH_TRACE prints the same)
        public static ulong MethodIlHash(string key, MethodBase m, System.Collections.Generic.Dictionary<Module, System.Collections.Generic.Dictionary<int, string>> cache, System.Collections.Generic.List<string> trace)
        {
            MethodBody b = m.GetMethodBody();
            if (b == null) return IlHash(key, null, null, null, null);
            Module mod = m.Module;
            System.Collections.Generic.Dictionary<int, string> names;
            if (!cache.TryGetValue(mod, out names)) cache[mod] = names = new System.Collections.Generic.Dictionary<int, string>();
            System.Collections.Generic.IList<ExceptionHandlingClause> cl = b.ExceptionHandlingClauses;
            int[] eh = new int[cl.Count * 6];
            string[] ec = new string[cl.Count];
            for (int i = 0; i < cl.Count; i++)
            {
                ExceptionHandlingClause c = cl[i];
                int f = (int)c.Flags;
                eh[i * 6] = f; eh[i * 6 + 1] = c.TryOffset; eh[i * 6 + 2] = c.TryLength; eh[i * 6 + 3] = c.HandlerOffset; eh[i * 6 + 4] = c.HandlerLength;
                if (f == 1) eh[i * 6 + 5] = c.FilterOffset;
                if (f == 0 && c.CatchType != null) ec[i] = MemberName(c.CatchType);
            }
            return IlHash(key, b.GetILAsByteArray(), eh, ec, delegate(int tok)
            {
                string s;
                if (!names.TryGetValue(tok, out s)) names[tok] = s = ResolveName(mod, tok);
                if (trace != null) trace.Add(tok.ToString("x8") + " " + (s ?? "-"));
                return s;
            });
        }

        // Index keys to the declared methods and constructors of asms that carry them (first assembly with the type wins).
        // Keys whose type or method is missing are left out: the caller compares the count with keys.Length.
        public static System.Collections.Generic.Dictionary<string, MethodBase> Resolve(Assembly[] asms, string[] keys)
        {
            const BindingFlags decl = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            // per type, the member names wanted: key strings are built only for overloads of those names (the key building is
            // the cost under the game's interpreter; monohost GM4-sweep measures the whole sweep)
            System.Collections.Generic.Dictionary<string, bool> want = new System.Collections.Generic.Dictionary<string, bool>();
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, bool>> types = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, bool>>();
            foreach (string k in keys)
            {
                int c = k.IndexOf("::");
                want[k] = true;
                string tn = k.Substring(0, c);
                System.Collections.Generic.Dictionary<string, bool> ns;
                if (!types.TryGetValue(tn, out ns)) types[tn] = ns = new System.Collections.Generic.Dictionary<string, bool>();
                ns[k.Substring(c + 2, k.IndexOf('(', c) - c - 2)] = true;
            }
            System.Collections.Generic.Dictionary<string, MethodBase> res = new System.Collections.Generic.Dictionary<string, MethodBase>();
            foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.Dictionary<string, bool>> e in types)
                foreach (Assembly a in asms)
                {
                    Type t = a.GetType(e.Key);
                    if (t == null) continue;
                    foreach (string n in e.Value.Keys)
                        foreach (MemberInfo mi in t.GetMember(n, MemberTypes.Method | MemberTypes.Constructor, decl))
                        {
                            MethodBase m = mi as MethodBase;
                            // the lists name generic methods with `n: GetObject<T>(ObjectGuid) has GetObject(ObjectGuid)'s SigKey
                            // and overwrote it (rain-h11 2026-10-31: G-M6 hashed and G-M4 L3-checked the generic one)
                            if (m == null || m.IsGenericMethodDefinition) continue;
                            string k = SigKey(m);
                            if (want.ContainsKey(k)) res[k] = m;
                        }
                    break;
                }
            return res;
        }

        // G-M5 census: hit[i] = true when a type of modTypes overrides Slots[i] (a virtual declared on one of its non-mod
        // bases) or supplies the body of interface method Slots[i] in mod code. Returns the number of matching types and
        // names them in log. isMod tells mod types (their own bases are checked when the census reaches them).
        // shortcut: matches by name + parameter keys on each base, not by vtable slot; a newslot in between is still
        // counted (more skips, never fewer).
        public static int OverrideCensus(Type[] modTypes, Predicate<Type> isMod, string[] slots, bool[] hit, System.Collections.Generic.List<string> log)
        {
            int exempt;
            return OverrideCensus(modTypes, isMod, slots, hit, log, null, out exempt);
        }

        // docs/122 §14.13: slots dispatched on the scored definition itself (docs/127 §2 receiver table). The worker scores a
        // pair only when the definition's exact runtime type is allow-listed, so a mod override of these runs there only as
        // a subclass of an allow-listed definition: DerivedOwners covers that, the census does not mark the slot.
        public static readonly string[] kDefReceiverTypes = {
            "Sims3.Gameplay.Interactions.InteractionDefinition", "Sims3.Gameplay.Interactions.IMetaInteractionDefinition",
            "Sims3.Gameplay.Interactions.IOverridesCalculateScore", "Sims3.Gameplay.Interactions.IOverridesAgeTests" };
        public static bool OnScoredDefinition(string slot) { return Array.IndexOf(kDefReceiverTypes, slot.Substring(0, slot.IndexOf("::"))) >= 0; }

        // exemptSlot (may be null): matches on those slots are not marked, only counted in exempted (logged once).
        public static int OverrideCensus(Type[] modTypes, Predicate<Type> isMod, string[] slots, bool[] hit, System.Collections.Generic.List<string> log,
            Predicate<string> exemptSlot, out int exempted)
        {
            int ex = 0;
            Predicate<int> take = delegate(int i) { if (exemptSlot != null && exemptSlot(slots[i])) { ex++; return false; } return true; };
            const BindingFlags decl = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            System.Collections.Generic.Dictionary<string, int> idx = new System.Collections.Generic.Dictionary<string, int>();
            System.Collections.Generic.Dictionary<string, bool> names = new System.Collections.Generic.Dictionary<string, bool>();
            for (int i = 0; i < slots.Length; i++) { idx[slots[i]] = i; int c = slots[i].IndexOf("::"); names[slots[i].Substring(c + 2, slots[i].IndexOf('(', c) - c - 2)] = true; }
            int types = 0;
            foreach (Type t in modTypes)
            {
                if (t == null || t.IsInterface) continue;
                bool any = false;
                foreach (MethodInfo m in t.GetMethods(decl))
                {
                    if (!m.IsVirtual || (m.Attributes & MethodAttributes.NewSlot) != 0 || !names.ContainsKey(m.Name)) continue;
                    string tail = "::" + m.Name + ParamsKey(m);
                    for (Type b = t.BaseType; b != null; b = b.BaseType)
                    {
                        if (isMod(b)) continue;
                        int i;
                        if (idx.TryGetValue(TypeKey(b) + tail, out i) && take(i)) { hit[i] = any = true; if (log != null) log.Add(t.FullName + " overrides " + slots[i]); }
                    }
                }
                foreach (Type itf in t.GetInterfaces())
                {
                    if (isMod(itf)) continue;
                    string ik = TypeKey(itf) + "::";
                    bool listed = false;
                    foreach (string s in slots) if (s.StartsWith(ik)) { listed = true; break; }
                    if (!listed) continue;
                    InterfaceMapping map;
                    try { map = t.GetInterfaceMap(itf); }
                    catch (Exception)
                    {   // no map: every listed method of the interface counts (more skips, never fewer)
                        for (int i = 0; i < slots.Length; i++) if (slots[i].StartsWith(ik) && take(i)) { hit[i] = any = true; if (log != null) log.Add(t.FullName + " implements " + slots[i] + " (no map)"); }
                        continue;
                    }
                    for (int j = 0; j < map.InterfaceMethods.Length; j++)
                    {
                        int i;
                        if (map.TargetMethods[j] == null || !isMod(map.TargetMethods[j].DeclaringType) || !idx.TryGetValue(SigKey(map.InterfaceMethods[j]), out i) || !take(i)) continue;
                        hit[i] = any = true;
                        if (log != null) log.Add(t.FullName + " implements " + slots[i]);
                    }
                }
                if (any) types++;
            }
            exempted = ex;
            return types;
        }

        // G-M5 derive rule (docs/122 §14.13): ownerHit[o] = true when a type of modTypes derives from owners[o] (a mod
        // subclass replacing the definition's singleton, e.g. NRaas GoHere's VisitLotEx.Definition : VisitLot.Definition).
        // Logs the first such type per owner; returns the number of owners newly hit. Null owners are skipped.
        public static int DerivedOwners(Type[] modTypes, Type[] owners, bool[] ownerHit, System.Collections.Generic.List<string> log)
        {
            int n = 0;
            for (int o = 0; o < owners.Length; o++)
            {
                if (owners[o] == null) continue;
                foreach (Type t in modTypes)
                {
                    if (t == null || !t.IsSubclassOf(owners[o])) continue;
                    if (!ownerHit[o]) n++;
                    ownerHit[o] = true;
                    if (log != null) log.Add(owners[o].FullName + " (derived: " + t.FullName + ")");
                    break;
                }
            }
            return n;
        }

        // Class of one pair: reference (sim thread) vs worker, both from the same driver on the same request. kNoClass when
        // neither side scored it (both filtered).
        public static int Classify(int refCode, float refScore, float refMeta, int workCode, float workScore, float workMeta)
        {
            if (refCode == kFiltered && workCode == kFiltered) return kNoClass;
            if (workCode == kSkip || workCode == kAbort || refCode == kSkip || refCode == kAbort) return A4;
            if (refCode != workCode) return A3;
            if (Bits(refScore) == Bits(workScore) && Bits(refMeta) == Bits(workMeta)) return A0;
            long d = Math.Max(UlpDistance(refScore, workScore), UlpDistance(refMeta, workMeta));
            return d <= 1 ? A1 : A2;
        }

        // Mode W report for one request (docs/122 §5.2): only pairs the worker scored (pass or Test failed) that the game
        // also scored. The game's C2 returns 0 for a failed Test, so Test agreement is "score > 0" on both sides.
        // top1 is set when at least one pair counts; top-1 agrees when both sides' first highest score is the same pair.
        public struct LReport { public int Pairs, Equal, TestAgree; public bool Top1Valid, Top1Agree; }

        public static LReport Report(float[] gameScore, bool[] gameHas, float[] workScore, int[] workCode, int n)
        {
            LReport r = new LReport();
            int bestW = -1, bestG = -1;
            for (int i = 0; i < n; i++)
            {
                if (!gameHas[i] || (workCode[i] != kPass && workCode[i] != kTestFailed)) continue;
                r.Pairs++;
                if (Bits(gameScore[i]) == Bits(workScore[i])) r.Equal++;
                if ((gameScore[i] > 0f) == (workScore[i] > 0f)) r.TestAgree++;
                if (bestW < 0 || workScore[i] > workScore[bestW]) bestW = i;
                if (bestG < 0 || gameScore[i] > gameScore[bestG]) bestG = i;
            }
            r.Top1Valid = r.Pairs > 0;
            r.Top1Agree = r.Top1Valid && bestW == bestG;
            return r;
        }
    }
}
