using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/339: EA's TuningControl.TuneAssemblies and PersistStatic.GameplayInitialize throw at the first [Tunable] /
    // [PersistableStatic] script assembly whose GetTypes fails (an add-on installed without its base mod) and stop there:
    // every script mod after it gets no tuning and no static constructor, ours included. ts3native [IlPatch]
    // ilp_tuneisolate makes both loops skip that one assembly. Here, at app startup:
    // - always (log only): one "tuning: broken script mod" line per such assembly, with the references nothing loaded provides;
    // - with ilp_tuneisolate armed (UserDiag op 50): ts3agent's static constructor is run if EA's loop never reached it
    //   (the backstop for a core build the IL patch does not know; ts3agent does the same for us).
    public static partial class Fixes
    {
        static bool sTuneScanDone;

        // by: null from our own startup handler, else the mod that ran our static constructor late
        public static void TuneIsolateStartup(string by)
        {
            if (sTuneScanDone) return;
            sTuneScanDone = true;
            bool armed = DiagSend(50, 0);
            if (by != null) DiagLine("tuning: late init of ts3fixes by " + by + " (tuning loop aborted?): C# default settings until the tuning is read again");
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            Dictionary<string, bool> loaded = new Dictionary<string, bool>();
            foreach (Assembly a in all) loaded[a.GetName().Name] = true;
            foreach (Assembly a in all)
            {
                string why = TuneBroken(a, loaded);
                if (why != null)
                    DiagLine("tuning: broken script mod " + a.GetName().Name + ": " + why + (armed
                        ? "; skipped (ilp_tuneisolate), the script mods after it are tuned"
                        : "; EA's tuning stops here: script mods after it get no tuning (ts3native [IlPatch] ilp_tuneisolate, docs/339)"));
            }
            if (armed && LateInit("Ts3Agent.Instantiator", "ts3agent.started")) DiagLine("tuning: late init of ts3agent (tuning loop aborted?)");
        }

        // null = fine or not one EA's loops read; else what failed and which references are missing
        static string TuneBroken(Assembly a, Dictionary<string, bool> loaded)
        {
            if (Array.IndexOf(kEaAssemblies, a.GetName().Name) >= 0) return null;
            if (a.GetCustomAttributes(typeof(TunableAttribute), true).Length == 0 && a.GetCustomAttributes(typeof(PersistableStaticAttribute), true).Length == 0) return null;
            try { a.GetTypes(); return null; }
            catch (Exception e)
            {
                List<string> missing = new List<string>();
                foreach (AssemblyName r in a.GetReferencedAssemblies()) if (!loaded.ContainsKey(r.Name)) missing.Add(r.Name);
                if (missing.Count > 0) return "GetTypes failed, missing assembly " + string.Join(", ", missing.ToArray());
                ReflectionTypeLoadException t = e as ReflectionTypeLoadException;
                Exception first = t != null && t.LoaderExceptions != null && t.LoaderExceptions.Length > 0 ? t.LoaderExceptions[0] : e;
                string m = first != null ? first.GetType().Name + ": " + first.Message : e.GetType().Name;
                return "GetTypes failed (" + (m.Length > 160 ? m.Substring(0, 160) : m) + ")";
            }
        }

        // Runs typeName's static constructor when its mod is loaded and marker is unset (that constructor sets it); true = ran it
        internal static bool LateInit(string typeName, string marker)
        {
            if (AppDomain.CurrentDomain.GetData(marker) != null) return false;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType(typeName, false);
                if (t == null) continue;
                RuntimeHelpers.RunClassConstructor(t.TypeHandle);
                return true;
            }
            return false;
        }
    }
}
