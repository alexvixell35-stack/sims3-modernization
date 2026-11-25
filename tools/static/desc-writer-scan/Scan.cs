using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Ts3Fixes;

// Offline run of the deadrefdescrev writer scan (docs/336 §11) on extracted mod assemblies: the kinds each one makes
// untrusted, the methods, and the wall time on .NET Framework (the in-game time is the [1766] counter).
// Scan.exe <refDir> <dll>...   (refDir: the game's assemblies, for the references)
static class DescWriterScanOffline
{
    static int Main(string[] args)
    {
        string refDir = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            string n = new AssemblyName(e.Name).Name;
            foreach (string dir in new string[] { refDir, Path.GetDirectoryName(args[1]) })
            {
                string p = Path.Combine(dir, n + ".dll");
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };
        Dictionary<string, int> fields = new Dictionary<string, int>();
        fields["Sims3.Gameplay.Careers.Occupation::Coworkers"] = DescWriterScan.KCoworkers;
        fields["Sims3.Gameplay.Careers.Occupation::FormerBoss"] = DescWriterScan.KFormerBoss;
        fields["Sims3.Gameplay.Skills.LogicSkill::mChessRankingNextChallenger"] = DescWriterScan.KChess;
        fields["Sims3.Gameplay.CAS.SimDescription::Pregnancy"] = DescWriterScan.KPregnancy;
        fields["Sims3.Gameplay.ActorSystems.Pregnancy::mDad"] = DescWriterScan.KPregnancy;
        fields["Sims3.Gameplay.Careers.CareerManager::mJob"] = DescWriterScan.KCoworkers | DescWriterScan.KFormerBoss;
        fields["Sims3.Gameplay.Careers.CareerManager::mSchool"] = DescWriterScan.KCoworkers | DescWriterScan.KFormerBoss;
        Dictionary<string, bool> lists = new Dictionary<string, bool>();
        lists["Sims3.Gameplay.Careers.Occupation::Coworkers"] = true;
        // DWS_JOBS=1 (docs/336 §14 census): only CareerManager.mJob / mSchool stores, EA assemblies included, every writer listed
        bool jobs = Environment.GetEnvironmentVariable("DWS_JOBS") == "1";
        if (jobs) { foreach (string k in new List<string>(fields.Keys)) if (!k.StartsWith("Sims3.Gameplay.Careers.CareerManager::")) fields.Remove(k); lists.Clear(); }
        Dictionary<string, bool> allowed = new Dictionary<string, bool>();
        foreach (string k in new string[] { "Ts3Fixes.Fixes::DescriptionHolders", "Ts3Fixes.Fixes::DdSetPregnancy", "Ts3Fixes.Fixes::TakeARiskCheck", "Ts3Fixes.Fixes::.cctor" }) allowed[k] = true;
        string[] core = { "mscorlib", "System", "System.Xml", "ScriptCore", "SimIFace", "Sims3GameplaySystems", "Sims3GameplayObjects", "Sims3Metadata", "Sims3StoreObjects", "UI" };
        DescWriterScan.Core = delegate(Assembly x) { return Array.IndexOf(core, x.GetName().Name) >= 0; };
        DescWriterScan.Result all = new DescWriterScan.Result();
        Stopwatch total = Stopwatch.StartNew();
        for (int i = 1; i < args.Length; i++)
        {
            Assembly a = Assembly.LoadFrom(args[i]);
            // DWS_HASH="Full.Type::Method;...": print each matching method's SigKey and IL hash (pin authoring, docs/336 §12)
            string want = Environment.GetEnvironmentVariable("DWS_HASH");
            if (!string.IsNullOrEmpty(want))
                foreach (string tm in want.Split(';'))
                {
                    int cut = tm.IndexOf("::");
                    try
                    {
                        Type ht = cut > 0 ? a.GetType(tm.Substring(0, cut), false) : null;
                        if (ht == null) continue;
                        foreach (MethodInfo hm in ht.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (hm.Name == tm.Substring(cut + 2))
                                Console.WriteLine("hash " + a.GetName().Name + " " + AutShadowCore.SigKey(hm) + " " + AutShadowCore.MethodIlHash(AutShadowCore.SigKey(hm), hm, new Dictionary<Module, Dictionary<int, string>>()).ToString("x16"));
                    }
                    catch (Exception e) { Console.WriteLine("hash " + a.GetName().Name + " " + tm + " not loadable offline: " + e.GetType().Name); }
                }
            bool refs = jobs && a.GetName().Name == "Sims3GameplaySystems";
            foreach (AssemblyName rn in a.GetReferencedAssemblies()) if (rn.Name == "Sims3GameplaySystems") refs = true;
            if (!refs) { Console.WriteLine(a.GetName().Name + ": no Sims3GameplaySystems reference, skipped"); continue; }
            DescWriterScan.Result r = new DescWriterScan.Result();
            Stopwatch sw = Stopwatch.StartNew();
            // offline stand-in for the in-game sSwapsByHook test: our hook classes are the nested *Hook types of Fixes
            Predicate<Type> skip = a.GetName().Name == "Ts3Fixes" ? (Predicate<Type>)delegate(Type t) { return t.DeclaringType != null && t.DeclaringType.FullName == "Ts3Fixes.Fixes" && t.Name.EndsWith("Hook"); } : null;
            // .NET Framework cannot load every type of a TS3 script assembly (EA's own mscorlib); the game loads them all, so the
            // offline run scans the types that load and reports the rest (in game an unloadable type list = every kind)
            Type[] types;
            int missing = 0;
            string why = null;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
                foreach (Type t in types) if (t == null) missing++;
                if (e.LoaderExceptions.Length > 0) why = e.LoaderExceptions[0].Message;
            }
            r.Assemblies++;
            // pinned writers (LC-7c): a hash match counts as covered, as when the game installs its hook
            Dictionary<string, bool> al = new Dictionary<string, bool>(allowed);
            List<string> mism = new List<string>();
            foreach (KeyValuePair<DescWriterScan.Pin, MethodBase> pm in DescWriterScan.MatchPins(a, DescWriterScan.Pins, mism))
            {
                DescWriterScan.Cover(al, pm.Value);
                Console.WriteLine(a.GetName().Name + ": pinned " + pm.Key.Key + (pm.Key.Hook != null ? " (hook " + pm.Key.Hook + ")" : " (no hook: event or removals only)"));
            }
            foreach (string m in mism) Console.WriteLine(a.GetName().Name + ": pin hash mismatch " + m);
            DescWriterScan.ScanTypes(a.GetName().Name, types, skip, al, DescWriterScan.ReflectionBody, fields, lists, r, jobs ? 100000 : 30);
            if (missing > 0) Console.WriteLine(a.GetName().Name + ": " + missing + " of " + types.Length + " types not loadable offline (" + why + ")");
            sw.Stop();
            all.Mask |= r.Mask; all.Methods += r.Methods; all.Unreadable += r.Unreadable;
            Console.WriteLine(a.GetName().Name + ": " + r.Methods + " methods, " + r.Unreadable + " unreadable, " + r.Unresolved + " unresolvable-token, " + r.Reflect + " reflection, " + sw.ElapsedMilliseconds + " ms, untrusted " + DescWriterScan.Kinds(r.Mask)
                + " (writers coworkers " + r.PerKind[0] + ", formerboss " + r.PerKind[1] + ", chess " + r.PerKind[2] + ", pregnancy " + r.PerKind[3] + ")");
            foreach (string l in r.Log) Console.WriteLine("  " + l);
        }
        Console.WriteLine("total: " + all.Methods + " methods, " + all.Unreadable + " unreadable, " + total.ElapsedMilliseconds + " ms, untrusted " + DescWriterScan.Kinds(all.Mask));
        return 0;
    }
}
