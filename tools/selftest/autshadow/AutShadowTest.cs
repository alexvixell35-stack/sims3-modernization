// Usage: node tools/selftest/autshadow/run.ts  (compiles this with mods/ts3fixes/src/AutShadowCore.cs)
// Offline self-check of AutShadowCore.cs: ULP distance, the Mode R pair classes A0-A4, the Mode W L report and the
// exact-signature L3 lookup, on fixed cases. Exit code 1 on failure.
using System;
using System.Reflection;
using Ts3Fixes;

class Overloads
{
    public virtual float Score(int a) { return a; }
    public float Score(string a) { return 0f; }
    float Score(int a, ref float b) { return b; }
}

class Derived : Overloads { public override float Score(int a) { return 1f; } }

// docs/128 G-M4 / G-M5 fixtures: "game" types and "Mod" types (isMod = name starts with Mod)
namespace Gm
{
    public class Host
    {
        public class Nested
        {
            public Nested(bool b) { }
            public int Go(ref int a, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ulong>> d, object[] arr, float[,] m) { return a; }
        }
        public virtual bool InUse { get { return false; } }
        public virtual void Plain() { }
        public virtual void Unlisted() { }
    }
    public class Mid : Host { }
    public interface IThing { int Count(); void Other(); }
    public class GameImpl : IThing { public int Count() { return 0; } public void Other() { } }
    public class ModObj : Mid, IThing
    {
        public override bool InUse { get { return true; } }
        public new virtual void Plain() { }   // newslot: not an override
        public override void Unlisted() { }   // overrides a virtual no closure calls
        int IThing.Count() { return 1; }
        public void Other() { }
    }
    public class ModFromGame : GameImpl { }  // the interface bodies are the game base's
    public class ModQuiet { public virtual void Plain() { } }
    // docs/122 §14.13: definition-receiver slots (IDefLike / GameDef stand in for InteractionDefinition)
    public interface IDefLike { bool Test(); }
    public class GameDef : IDefLike { public virtual bool Test() { return true; } }
    public class ModOwnDef : IDefLike { public bool Test() { return false; } }      // a mod's own, unrelated definition
    public class ModSubDef : GameDef { public override bool Test() { return false; } }  // derives an allow-listed one
    // G-M6 fixture: tools/autshadow-il-hashes.ts --hash-file must give the same hashes for these (run.ts compares)
    public class IlSample
    {
        public static int sF;
        string mS;
        public static string Run(int a, string b)
        {
            try { sF += a; if (a > 2) return string.Concat(b, "x"); return new System.Text.StringBuilder(b).Append(a).ToString(); }
            catch (InvalidOperationException e) { return e.Message; }
            finally { sF--; }
        }
        public int Switchy(int k) { switch (k) { case 0: return 1; case 1: return 5; case 2: return 9; default: mS = "d"; return Run(k, mS).Length; } }
        public static string Pick(int a) { return null; }
        public static T Pick<T>(int a) where T : class { return null; }  // same SigKey as Pick(int), declared after it
        public System.Collections.Generic.List<int> Gen() { System.Collections.Generic.List<int> l = new System.Collections.Generic.List<int>(); l.Add(1); return l; }
    }
}

class Probe : AutShadowCore.IRainProbe
{
    public bool Seasons, Sim, Outside; public int W, Calls;
    public bool SeasonsOn() { Calls++; return Seasons; }
    public int Weather() { Calls += 10; return W; }
    public bool TargetIsSim() { Calls += 100; return Sim; }
    public bool TargetOutside() { Calls += 1000; return Outside; }
}

static class AutShadowTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    static float Next(float f) { return BitConverter.ToSingle(BitConverter.GetBytes(AutShadowCore.Bits(f) + 1), 0); }

    static int Main()
    {
        const int P = AutShadowCore.kPass, F = AutShadowCore.kTestFailed, S = AutShadowCore.kSkip, X = AutShadowCore.kAbort, N = AutShadowCore.kFiltered;
        Check(AutShadowCore.UlpDistance(1f, 1f) == 0, "same float: 0 ULP");
        Check(AutShadowCore.UlpDistance(1f, Next(1f)) == 1, "neighbour: 1 ULP");
        Check(AutShadowCore.UlpDistance(0f, -0f) == 0, "+0 and -0: 0 ULP");
        Check(AutShadowCore.UlpDistance(-float.Epsilon, float.Epsilon) == 2, "across zero: 2 ULP");
        Check(AutShadowCore.UlpDistance(1f, 2f) == 0x800000, "1 to 2: 2^23 ULP");

        Check(AutShadowCore.Classify(P, 1.5f, 0.25f, P, 1.5f, 0.25f) == AutShadowCore.A0, "bit-equal: A0");
        Check(AutShadowCore.Classify(F, 0f, 0.25f, F, 0f, 0.25f) == AutShadowCore.A0, "both failed: A0");
        Check(AutShadowCore.Classify(P, 1.5f, 0f, P, Next(1.5f), 0f) == AutShadowCore.A1, "1 ULP score: A1");
        Check(AutShadowCore.Classify(P, 0f, 0f, P, -0f, 0f) == AutShadowCore.A1, "-0 vs +0: A1 (bits differ)");
        Check(AutShadowCore.Classify(P, 1f, 0.5f, P, 1f, Next(Next(0.5f))) == AutShadowCore.A2, "2 ULP meta: A2");
        Check(AutShadowCore.Classify(P, 1f, 0f, P, 1.1f, 0f) == AutShadowCore.A2, "different score: A2");
        Check(AutShadowCore.Classify(P, 1f, 0f, F, 0f, 0f) == AutShadowCore.A3, "pass vs fail: A3");
        Check(AutShadowCore.Classify(N, 0f, 0f, P, 0f, 0f) == AutShadowCore.A3, "filtered on one side only: A3");
        Check(AutShadowCore.Classify(P, 1f, 0f, X, 0f, 0f) == AutShadowCore.A4, "worker abort: A4");
        Check(AutShadowCore.Classify(P, 1f, 0f, S, 0f, 0f) == AutShadowCore.A4, "worker skip: A4");
        Check(AutShadowCore.Classify(X, 0f, 0f, P, 1f, 0f) == AutShadowCore.A4, "reference abort: A4");
        Check(AutShadowCore.Classify(N, 0f, 0f, N, 0f, 0f) == AutShadowCore.kNoClass, "both filtered: no class");

        float[] g = { 2f, 0f, 5f, 1f, 9f };
        bool[] has = { true, true, true, false, true };
        float[] w = { 2f, 0f, 4f, 7f, 3f };
        int[] c = { P, F, P, P, S };
        AutShadowCore.LReport r = AutShadowCore.Report(g, has, w, c, 5);
        Check(r.Pairs == 3, "L pairs: game-scored and worker pass/fail only");
        Check(r.Equal == 2, "L equal scores");
        Check(r.TestAgree == 3, "L Test agreement (score > 0 both sides)");
        Check(r.Top1Valid && r.Top1Agree, "L top-1: pair 2 on both sides");
        w[0] = 6f;
        r = AutShadowCore.Report(g, has, w, c, 5);
        Check(r.Top1Valid && !r.Top1Agree, "L top-1 differs");
        r = AutShadowCore.Report(g, new bool[5], w, c, 5);
        Check(r.Pairs == 0 && !r.Top1Valid, "L empty");

        const string raw = "3681889397f4aa44b399a2926b2454fc";
        byte[] b = new byte[16];
        for (int i = 0; i < 16; i++) b[i] = Convert.ToByte(raw.Substring(2 * i, 2), 16);
        Check(AutShadowCore.GuidNFromRaw(raw) == new Guid(b).ToString("N"), "MVID raw bytes to Guid N format");

        // docs/124 §13.5: L3 callees resolve by exact signature; a same-name overload is never the one returned.
        Type o = typeof(Overloads);
        MethodInfo one = AutShadowCore.FindExact(o, "Score", new Type[] { typeof(int) });
        Check(one != null && one.GetParameters()[0].ParameterType == typeof(int), "exact overload found");
        MethodInfo two = AutShadowCore.FindExact(o, "Score", new Type[] { typeof(string) });
        Check(two != null && two != one, "each overload resolves to itself");
        Check(AutShadowCore.FindExact(o, "Score", new Type[] { typeof(double) }) == null, "same name, other parameters: not accepted");
        Check(AutShadowCore.FindExact(o, "Score", Type.EmptyTypes) == null, "same name, no parameters: not accepted");
        MethodInfo pr = AutShadowCore.FindExact(o, "Score", new Type[] { typeof(int), typeof(float).MakeByRefType() });
        Check(pr != null && pr != one && !pr.IsPublic, "private ref overload resolved separately");
        Check(AutShadowCore.FindExact(typeof(Derived), "Score", new Type[] { typeof(int) }).DeclaringType == typeof(Derived), "override wins over base");

        // G-M1: the accepted MVIDs are 16-byte raw hex that converts like EA's
        foreach (string m in new string[] { "bb8cb38b0d83b44490103053df82374c", "a95a143fe9314a44805eb847548ffe7f", "99b5bd68c981aa42a5f220f2cf7ae4d2", "ee57f83a5f56384f9ede12b34a086c39" })
        {
            for (int i = 0; i < 16; i++) b[i] = Convert.ToByte(m.Substring(2 * i, 2), 16);
            Check(AutShadowCore.GuidNFromRaw(m) == new Guid(b).ToString("N"), "G-M1 MVID " + m);
        }

        // docs/218 §5 content guard: SGS/SGO only as a pair of one set, other modules by their own hash, a missing hash = no verdict
        {
            string[][] sets = { new string[] { "ea-sgs", "ea-sgo" }, new string[] { "s90-sgs", "s90-sgo" } };
            string[] other = { "ea-sc", "et-sc", "ea-ui" };
            string[] cn = { "ScriptCore", "Sims3GameplaySystems", "Sims3GameplayObjects", "UI" };
            int set;
            bool[] cr = AutShadowCore.ContentAccepted(cn, new string[] { "ea-sc", "s90-sgs", "s90-sgo", "ea-ui" }, sets, other, out set);
            Check(cr != null && set == 1 && cr[0] && cr[1] && cr[2] && cr[3], "content: simler90 pair = set 1, EA ScriptCore and UI accepted");
            cr = AutShadowCore.ContentAccepted(cn, new string[] { "et-sc", "ea-sgs", "ea-sgo", "tui-ui" }, sets, other, out set);
            Check(cr != null && set == 0 && cr[0] && cr[1] && cr[2] && !cr[3], "content: EA pair = set 0, ErrorTrap ScriptCore accepted, a rewritten UI not");
            cr = AutShadowCore.ContentAccepted(cn, new string[] { "ea-sc", "ea-sgs", "s90-sgo", "ea-ui" }, sets, other, out set);
            Check(cr != null && set == -1 && !cr[1] && !cr[2] && cr[0], "content: EA SGS + simler90 SGO (mixed pair) = neither (G-M1)");
            cr = AutShadowCore.ContentAccepted(cn, new string[] { "ea-sc", "tui-sgs", "ea-sgo", "ea-ui" }, sets, other, out set);
            Check(cr != null && set == -1 && !cr[1] && !cr[2], "content: a rewritten SGS = both of the pair unknown");
            cr = AutShadowCore.ContentAccepted(cn, new string[] { "ea-sc", null, "ea-sgo", "ea-ui" }, sets, other, out set);
            Check(cr == null && set == -1, "content: a module without a hash = no verdict (MVID gate)");
            Check(AutShadowCore.ContentAccepted(new string[0], new string[0], sets, other, out set) == null, "content: no modules = no verdict");
            Check(AutShadowCore.Hex(new byte[] { 0x00, 0x0f, 0xa5, 0xff }) == "000fa5ff", "content: hex is lower case, 2 digits a byte");
        }

        // G-M4: index keys (tools/managed-callgraph.ts format) from reflection, and their resolution
        Type nt = typeof(Gm.Host.Nested);
        string goKey = "Gm.Host+Nested::Go(int&,System.Collections.Generic.Dictionary`2<string,System.Collections.Generic.List`1<ulong>>,object[],float[,])";
        Check(AutShadowCore.SigKey(nt.GetMethod("Go")) == goKey, "SigKey: nested, byref, generic instance, arrays: " + AutShadowCore.SigKey(nt.GetMethod("Go")));
        Check(AutShadowCore.SigKey(nt.GetConstructors()[0]) == "Gm.Host+Nested::.ctor(bool)", "SigKey: constructor");
        Check(AutShadowCore.SigKey(typeof(Gm.Host).GetMethod("get_InUse")) == "Gm.Host::get_InUse()", "SigKey: property getter");
        System.Collections.Generic.Dictionary<string, MethodBase> got = AutShadowCore.Resolve(new Assembly[] { typeof(Gm.Host).Assembly },
            new string[] { goKey, "Gm.Host+Nested::.ctor(bool)", "Gm.Host::Plain()", "Gm.Host::Plain(int)", "Gm.Missing::X()" });
        Check(got.Count == 3 && got[goKey] == nt.GetMethod("Go") && got.ContainsKey("Gm.Host::Plain()"), "Resolve: 3 of 5 (other overload and missing type left out)");

        // G-M5: override census
        string[] slots = { "Gm.Host::get_InUse()", "Gm.Host::Plain()", "Gm.IThing::Count()", "Gm.IThing::Other()", "Gm.Mid::Nothing()" };
        bool[] hit = new bool[slots.Length];
        System.Collections.Generic.List<string> log = new System.Collections.Generic.List<string>();
        int n = AutShadowCore.OverrideCensus(new Type[] { typeof(Gm.ModObj), typeof(Gm.ModFromGame), typeof(Gm.ModQuiet), typeof(Gm.IThing) },
            delegate(Type t) { return t != null && t.Name.StartsWith("Mod"); }, slots, hit, log);
        Check(n == 1, "census: one mod type overrides closure slots, got " + n);
        Check(hit[0] && !hit[1] && hit[2] && hit[3] && !hit[4], "census: InUse override, explicit and implicit interface bodies; newslot and game-base bodies not counted");
        Check(log.Count == 3, "census log lines " + log.Count);
        // receiver rule: the old census marks the definition slot for a mod's own definition (mono-cost4 bug); with the
        // definition slots exempt only the object slot is marked, and the subclass skips its base by the derive rule
        string[] ds = { "Gm.IDefLike::Test()", "Gm.GameDef::Test()", "Gm.Host::get_InUse()" };
        Type[] mt = { typeof(Gm.ModOwnDef), typeof(Gm.ModSubDef), typeof(Gm.ModObj) };
        Predicate<Type> modP = delegate(Type t) { return t != null && t.Name.StartsWith("Mod"); };
        bool[] dh = new bool[ds.Length];
        AutShadowCore.OverrideCensus(mt, modP, ds, dh, null);
        Check(dh[0] && dh[1] && dh[2], "old census: own definition marks the interface slot");
        dh = new bool[ds.Length];
        int ex;
        int ntypes = AutShadowCore.OverrideCensus(mt, modP, ds, dh, null, delegate(string sl) { return sl.StartsWith("Gm.IDefLike::") || sl.StartsWith("Gm.GameDef::"); }, out ex);
        Check(!dh[0] && !dh[1] && dh[2] && ntypes == 1 && ex >= 2, "receiver rule: only the object slot marked, exempted " + ex + ", types " + ntypes);
        bool[] oh = new bool[3];
        System.Collections.Generic.List<string> dl = new System.Collections.Generic.List<string>();
        int dn = AutShadowCore.DerivedOwners(mt, new Type[] { typeof(Gm.GameDef), null, typeof(Gm.IThing) }, oh, dl);
        Check(dn == 1 && oh[0] && !oh[1] && !oh[2] && dl.Count == 1 && dl[0].Contains("Gm.ModSubDef"), "derive rule: the subclass skips its base only");
        Check(AutShadowCore.OnScoredDefinition("Sims3.Gameplay.Interactions.InteractionDefinition::Test(x)") && !AutShadowCore.OnScoredDefinition("Sims3.Gameplay.Interfaces.IGameObject::get_InUse()"), "OnScoredDefinition");

        // H11 (docs/127 §4): conditions in the game's order, later probes not run once one fails
        Probe pr2 = new Probe();
        Check(!AutShadowCore.RainShelter(false, pr2, 1, 2) && pr2.Calls == 0, "H11: not an autonomy search: no probe");
        pr2.Calls = 0;
        Check(!AutShadowCore.RainShelter(true, pr2, 1, 2) && pr2.Calls == 1, "H11: Seasons off: weather not read");
        pr2.Seasons = true; pr2.W = 3; pr2.Calls = 0;
        Check(!AutShadowCore.RainShelter(true, pr2, 1, 2) && pr2.Calls == 11, "H11: dry weather: target not probed");
        pr2.W = 2; pr2.Sim = true; pr2.Calls = 0;
        Check(!AutShadowCore.RainShelter(true, pr2, 1, 2) && pr2.Calls == 111, "H11: hail, Sim target: no IsOutside");
        pr2.Sim = false; pr2.Calls = 0;
        Check(!AutShadowCore.RainShelter(true, pr2, 1, 2) && pr2.Calls == 1111, "H11: hail, indoor object");
        pr2.Outside = true; pr2.W = 1;
        Check(AutShadowCore.RainShelter(true, pr2, 1, 2), "H11: rain, outdoor object: abort");

        // H11 shelter answer (docs/127 §4.1): cut bit, worker answer, done rule
        Check(AutShadowCore.ShelterBit(false, false, false, true, false) == AutShadowCore.kShelterNone, "shelter bit: dry weather: none");
        Check(AutShadowCore.ShelterBit(true, true, false, true, false) == AutShadowCore.kShelterNone, "shelter bit: Sim: none");
        Check(AutShadowCore.ShelterBit(true, false, true, true, true) == AutShadowCore.kShelterNone, "shelter bit: ICanCountAsBeingIndoors: none (needs the instance)");
        Check(AutShadowCore.ShelterBit(true, false, false, false, false) == AutShadowCore.kShelterNone, "shelter bit: indoors: none (branch not taken)");
        Check(AutShadowCore.ShelterBit(true, false, false, true, true) == AutShadowCore.kSheltered, "shelter bit: outside under a roof: sheltered");
        Check(AutShadowCore.ShelterBit(true, false, false, true, false) == AutShadowCore.kUnsheltered, "shelter bit: outside, open sky: unsheltered");
        Check(AutShadowCore.ShelterAnswer(true, true, AutShadowCore.kSheltered) == 1, "shelter answer: sheltered");
        Check(AutShadowCore.ShelterAnswer(true, true, AutShadowCore.kUnsheltered) == 0, "shelter answer: unsheltered (multiplier applies)");
        Check(AutShadowCore.ShelterAnswer(false, true, AutShadowCore.kSheltered) == -1, "shelter answer: a real instance: abort");
        Check(AutShadowCore.ShelterAnswer(true, false, AutShadowCore.kSheltered) == -1, "shelter answer: another object: abort");
        Check(AutShadowCore.ShelterAnswer(true, true, AutShadowCore.kShelterNone) == -1, "shelter answer: no cut bit: abort");
        Check(AutShadowCore.ShelterDone(1, true) && !AutShadowCore.ShelterDone(0, true) && !AutShadowCore.ShelterDone(1, false) && !AutShadowCore.ShelterDone(2, true), "shelter done: one skip and one answer");
        Check(AutShadowCore.ShelterDone(0, false), "shelter done: branch not reached (meta CalculateScore outside local autonomy): valid");
        // G-M6 (PR #112): IlHash pinned on the vector tools/autshadow-il-hashes.ts --self-check pins too
        byte[] il = { 0x02, 0x28, 1, 0, 0, 0x0a, 0x72, 1, 0, 0, 0x70, 0x2b, 5, 0x45, 1, 0, 0, 0, 2, 0, 0, 0, 0xfe, 0x15, 2, 0, 0, 0x1b, 0x2a };
        AutShadowCore.TokenName nm = delegate(int t) { return t == 0x0a000001 ? "System.String::Concat(string,string)" : t == 0x70000001 ? "hi" : null; };
        ulong vh = AutShadowCore.IlHash("K::M()", il, new int[] { 0, 0, 5, 5, 3, 0, 2, 0, 8, 8, 2, 0 }, new string[] { "System.Exception", null }, nm);
        Check(vh == 0x3214442e9ad01960UL, "G-M6 vector hash " + vh.ToString("x16"));
        Check(AutShadowCore.IlHash("K::M()", il, new int[0], new string[0], delegate(int t) { return t == 0x70000001 ? "ho" : nm(t); }) != AutShadowCore.IlHash("K::M()", il, new int[0], new string[0], nm), "G-M6: string text in the hash");
        // a header-swapped callee is named as before the swap (Fixes.Patch records it; rain-h11 2026-10-31)
        MethodBase runM = typeof(Gm.IlSample).GetMethod("Run");
        Check(AutShadowCore.ResolveName(runM.Module, runM.MetadataToken) == "Gm.IlSample::Run(int,string)", "G-M6: method token name " + AutShadowCore.ResolveName(runM.Module, runM.MetadataToken));
        AutShadowCore.SwapNames[runM.MethodHandle.Value] = "Orig::Run(int,string)";
        Check(AutShadowCore.ResolveName(runM.Module, runM.MetadataToken) == "Orig::Run(int,string)", "G-M6: swapped callee keeps its recorded name");
        AutShadowCore.SwapNames.Clear();
        System.Collections.Generic.Dictionary<string, MethodBase> pk = AutShadowCore.Resolve(new Assembly[] { typeof(Gm.IlSample).Assembly }, new string[] { "Gm.IlSample::Pick(int)" });
        Check(pk.Count == 1 && !pk["Gm.IlSample::Pick(int)"].IsGenericMethodDefinition, "Resolve: a generic overload with the same SigKey does not replace the listed method");
        // e2e lines for run.ts: the runtime side (reflection) of the fixture methods
        System.Collections.Generic.Dictionary<Module, System.Collections.Generic.Dictionary<int, string>> cache = new System.Collections.Generic.Dictionary<Module, System.Collections.Generic.Dictionary<int, string>>();
        foreach (string mn in new string[] { "Run", "Switchy", "Gen" })
        {
            MethodBase mb = typeof(Gm.IlSample).GetMethod(mn);
            string key = AutShadowCore.SigKey(mb);
            Console.WriteLine("ilhash " + key + " " + AutShadowCore.MethodIlHash(key, mb, cache).ToString("x16"));
        }

        // tier-2 P1 (docs/219 §3.1): switch off or sim thread = the game's method; worker + switch = abort; household scope
        // (incl. unknown) aborts uncounted; six override types, slots 884-889, fire 890
        Check(AutShadowCore.T2Site(false, true, false) == 0 && AutShadowCore.T2Site(true, false, false) == 0
            && AutShadowCore.T2Site(true, false, true) == 0, "T2Site: sim thread or switch off runs the game's method");
        Check(AutShadowCore.T2Site(true, true, false) == 1 && AutShadowCore.T2Site(true, true, true) == 2, "T2Site: worker with the switch aborts (household: uncounted)");
        Check(!AutShadowCore.T2Household(false, false, false, false, false), "T2Household: a non-selectable NPC outside the active household is not household");
        Check(AutShadowCore.T2Household(true, false, false, false, false) && AutShadowCore.T2Household(false, true, false, false, false)
            && AutShadowCore.T2Household(false, false, true, false, false) && AutShadowCore.T2Household(false, false, false, true, false)
            && AutShadowCore.T2Household(false, false, false, false, true), "T2Household: null, destroyed, no description, selectable, active household = household");
        Check(AutShadowCore.kT2InUseTypes.Length == AutShadowCore.kT2Fire && AutShadowCore.kT2N == 7, "T2: six get_InUse types before the fire slot");

        // tier-2 H7-H10 (docs/126 §3): H8 no-op only for the request's autonomy with its cache on; H10 stale past 60 ticks
        Check(AutShadowCore.T2CacheNoop(true, true) && !AutShadowCore.T2CacheNoop(false, true) && !AutShadowCore.T2CacheNoop(true, false),
            "T2CacheNoop: only the request's autonomy with the cache on");
        Check(!AutShadowCore.T2LodStale(160, 100) && AutShadowCore.T2LodStale(161, 100) && AutShadowCore.T2LodStale(0, long.MinValue),
            "T2LodStale: more than 60 ticks (never checked = stale)");
        Check(AutShadowCore.kT2HN == 5 && AutShadowCore.kT2HLodStale == 4, "T2H: five slots 898-902");

        Console.WriteLine(sFails == 0 ? "autshadow selftest: PASS" : "autshadow selftest: " + sFails + " FAIL");
        return sFails == 0 ? 0 : 1;
    }
}
