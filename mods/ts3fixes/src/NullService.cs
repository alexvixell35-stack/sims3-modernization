using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Services;

namespace Ts3Fixes
{
    // nullservice (docs/504): Services.sAllServices is [PersistableStatic]. A saved service whose type no longer loads comes
    // back from the save as a null element, and every EA loop over the list dereferences it:
    // - ResortStaffedObjectComponent.GetService (from GameObject.Init at object load): the NullReferenceException skips the
    //   rest of that object's startup (later components never start);
    // - Services.PostWorldLoadStartup: the rest of its world-load clean-up is skipped;
    // - Services.Simulate catches only ResetException, so reaching the null there would end the services task (maid, mail,
    //   repair, Grim Reaper...) for the session.
    // NRaas Overwatch removes the null (CleanupServicePools), but only in its delayed world-load step, after object load.
    // Fix, switch on: the AllServices getter drops null elements before returning the list (EA's idiom from GameObject.Init),
    // and PostWorldLoadStartup, which reads the field directly, reads the getter first. EA's code runs unchanged.
    // Not covered: Services.IsSimDescriptionInAnyServicePool and PostWorldLoadShutdown read the field directly; by the time
    // they run, the getter has cleaned the list (Services.Simulate reads it). [2014] null elements dropped.
    public static partial class Fixes
    {
        public class NullServiceHook
        {
            public static List<Service> GetHook()
            {
                List<Service> l = GetOrig();
                if (l != null && ShortcutOn("nullservice"))
                {
                    int n = NullServiceCore.DropNulls(l);
                    if (n > 0)
                    {
                        PatchCounters()[2014] += n;
                        if (sNullServiceLines++ < 5) ShortcutLog("nullservice: dropped " + n + " null element(s) from Services.AllServices, " + l.Count + " services left");
                    }
                }
                return l;
            }
            public static List<Service> GetOrig() { return null; } // receives Services.get_AllServices' original header

            public static void StartupHook()
            {
                if (ShortcutOn("nullservice")) GC.KeepAlive(Services.AllServices); // the getter above drops the nulls
                StartupOrig();
            }
            public static void StartupOrig() { } // receives Services.PostWorldLoadStartup's original header
        }
        static int sNullServiceLines;

        // Repro check (tools/harness/repros.json null-service-check). "state": services and nulls in the field. "broken" /
        // "fixed": puts one null into the list, runs an EA reader of the getter (TownieMaker.GetModifiableServices, read-only),
        // takes the null out again in the same call (nothing else runs in between), and throws unless the reader threw a
        // NullReferenceException ("broken", switch off) or returned ("fixed", switch on).
        public static string NullServiceCheck(string what)
        {
            const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo f = typeof(Services).GetField("sAllServices", all);
            List<Service> l = f != null ? f.GetValue(null) as List<Service> : null;
            if (l == null) return "no Services.sAllServices list";
            int nulls = 0;
            foreach (Service s in l) if (s == null) nulls++;
            string state = "switch " + ShortcutOn("nullservice") + ", " + l.Count + " entries, " + nulls + " null, dropped so far " + PatchCounters()[2014];
            if (what == "state") return state;
            bool expectThrow = what == "broken";
            if (!expectThrow && what != "fixed") return "unknown mode " + what + " (state, broken, fixed)";
            MethodInfo reader = typeof(TownieMaker).GetMethod("GetModifiableServices", all);
            if (reader == null) throw new InvalidOperationException("TownieMaker.GetModifiableServices not found");
            l.Add(null);
            string outcome;
            bool threw;
            try
            {
                List<Service> got = reader.Invoke(null, null) as List<Service>;
                threw = false;
                outcome = "the reader returned " + (got != null ? got.Count + " services" : "null");
            }
            catch (TargetInvocationException e)
            {
                threw = e.InnerException is NullReferenceException;
                outcome = "the reader threw " + (e.InnerException != null ? e.InnerException.GetType().Name : "?");
            }
            int left = NullServiceCore.DropNulls(l); // never leave our null for Services.Simulate
            string r = outcome + "; our null still in the list after the reader: " + (left > 0) + "; " + state;
            // 2026-11-01: a core mod that already patches the getter drops the null with the switch off, so this arm cannot fail
            if (expectThrow && !threw && left == 0 && !ShortcutOn("nullservice"))
                r += " (the getter dropped our null with the switch off: another mod already patches Services.get_AllServices; this arm needs EA's getter)";
            if (threw != expectThrow) throw new InvalidOperationException("nullservice " + what + ": wrong outcome: " + r);
            return r;
        }
    }
}
