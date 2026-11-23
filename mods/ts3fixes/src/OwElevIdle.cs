using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Objects.Elevator;

namespace Ts3Fixes
{
    // owelevidle (tuning kOwElevIdle, off by default; docs/655).
    //
    // Symptom [measured, phase-2026111* owreset lines]: about 10 NPCs a night are destroyed and re-made at home by NRaas
    // Overwatch's nightly alarm (05:00 in Bridgeport, a Downtown world; 50-85 ms each). All of them are fade-out resets
    // (ResetSimTask.Perform(sim, true)), and the named Sims live in Bridgeport's elevator towers.
    //
    // Cause [verified, NRaas source + decompiled]: Overwatch's CleanupElevators walks every ElevatorDoors, and for every
    // SimDescription in its interior's ElevatorPortalComponent.mAssignedSims whose Sim exists it runs ResetSimTask.Perform
    // (destroy, sleep, re-instantiate at home), then resets the doors. mAssignedSims keeps an entry from the entry door's
    // OnPortalStart (RegisterSim) until the exit door's OnPortalStart, the ride's route end or a portal reset unregisters it;
    // an entry whose Sim left the ride any other way stays. At 05:00 such Sims are at home (asleep), not riding, and
    // Overwatch resets them anyway. RecoverMissingSims is not the source: its only ResetSimTask call goes through
    // Instantiation.Perform's onReset with fadeOut false, and every logged reset has fade True.
    //
    // Fix: just before CleanupElevators runs, each entry whose Sim shows no sign of a ride (OwElevIdleCore) is dropped with
    // EA's own ElevatorPortalComponent.UnregisterSim, which also undoes the routing flags RegisterSim set (pushing,
    // obstacles, the forced walk style) and frees the slot. Overwatch then resets only real riders, exactly as before.
    // Exempt Sims (IsPlayerSim: household, selected, unknown scope) keep Overwatch's reset. Switch off: the entries are only
    // classified and counted, so a plain run shows how many resets the switch would save. Overwatch absent or shaped
    // differently: one applied-log line, nothing patched.
    // Counters: [4150] idle entries dropped (Overwatch reset avoided), [4151] riding entries left to Overwatch, [4152] exempt
    // idle entries left to Overwatch, [4153] idle entries seen with the switch off, [4154] errors, [4155] passes.
    public static partial class Fixes
    {
        delegate void CompiledVoidFn();   // no-argument compiled call (the game's mscorlib has no non-generic Action)
        static bool sOeiTried;
        static int sOeiLogged;

        internal static void ApplyOwElevIdle(bool lastTry)
        {
            if (sOeiTried) return;
            Assembly ow = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasOverwatch") { ow = a; break; }
            if (ow == null)
            {
                if (lastTry) { sOeiTried = true; AppliedLog().Add("owelevidle: Overwatch not loaded, off"); }
                return;
            }
            sOeiTried = true;
            if (sErSleeping == null) ErMembers();
            Type ce = ow.GetType("NRaas.OverwatchSpace.Alarms.CleanupElevators");
            Type[] args = { typeof(bool) };
            MethodInfo m = ce == null ? null : ce.GetMethod("PrivatePerformAction", kInstAll, null, args, null);
            if (m == null || m.ReturnType != typeof(void) || sErAssigned == null || sErSleeping == null || sErTask == null)
            {
                AppliedLog().Add("owelevidle: CleanupElevators.PrivatePerformAction(bool) or ElevatorPortalComponent.mAssignedSims not found, off");
                return;
            }
            Patch(ce, "PrivatePerformAction", args, typeof(OeiHook), "PerformHook", "PerformOrig"); // logs ok or why not
        }

        static void OeiLog(string s) { if (sOeiLogged++ < 60) DiagLine("owelevidle: " + s); }

        // One pass over the elevators Overwatch is about to clean, in its own order (every ElevatorDoors' interior).
        // census: classify only (no counters, no changes); returns "riding r exempt e idle i".
        static string OeiPass(bool census)
        {
            int[] c = census ? new int[4160] : PatchCounters();
            if (!census) c[4155]++;
            sOeiLogged = 0;
            bool on = !census && ShortcutOn("owelevidle");
            foreach (ElevatorDoors doors in Sims3.Gameplay.Queries.GetObjects<ElevatorDoors>())
            {
                try
                {
                    ElevatorInterior ei = doors.InteriorObj;
                    ElevatorInterior.ElevatorPortalComponent pc = ei == null ? null : ei.ElevatorPortal;
                    IDictionary d = pc == null ? null : sErAssigned.GetValue(pc) as IDictionary;
                    if (d == null || d.Count == 0) continue;
                    List<Sim> drop = null;
                    foreach (DictionaryEntry e in d)
                    {
                        SimDescription sd = e.Key as SimDescription;
                        Sim s = sd == null ? null : sd.CreatedSim;
                        bool sleeping = (bool)sErSleeping.GetValue(e.Value), live = ErLiveTask(e.Value);
                        bool routing = s != null && s.IsRouting, usingIt = s != null && ei.IsActorUsingMe(s);
                        bool inside = s != null && ElevatorInterior.IsSimInsideElevator(s);
                        int r = OwElevIdleCore.Decide(s != null, sleeping, live, routing, usingIt, inside, s != null && IsPlayerSim(s));
                        if (r == OwElevIdleCore.kNotCase) continue;
                        string why = "sleeping " + sleeping + " task " + live + " routing " + routing + " using " + usingIt + " inside " + inside;
                        if (r == OwElevIdleCore.kRiding) { c[4151]++; OeiLog(s.FullName + ", elevator " + ei.ObjectId + ": riding (" + why + "), left to Overwatch"); continue; }
                        if (r == OwElevIdleCore.kExempt) { c[4152]++; OeiLog(s.FullName + ", elevator " + ei.ObjectId + ": idle, exempt, left to Overwatch"); continue; }
                        if (!on) { c[4153]++; OeiLog(s.FullName + ", elevator " + ei.ObjectId + ": idle (" + why + ")" + (census ? ", census" : ", switch off, Overwatch resets it")); continue; }
                        if (drop == null) drop = new List<Sim>();
                        drop.Add(s);
                    }
                    if (drop == null) continue;
                    foreach (Sim s in drop)  // after the walk: UnregisterSim changes the dictionary
                    {
                        pc.UnregisterSim(s);
                        c[4150]++;
                        OeiLog(s.FullName + ", elevator " + ei.ObjectId + ": idle entry dropped, no Overwatch reset");
                    }
                }
                catch (Exception ex) { c[4154]++; OeiLog("error " + ex.GetType().Name); }
            }
            return "riding " + c[4151] + " exempt " + c[4152] + " idle " + c[4153] + " dropped " + c[4150] + " errors " + c[4154];
        }

        // Repro owelevidle-check (tools/harness/repros.json): "census" classifies every elevator entry (read-only); "run" runs
        // Overwatch's own CleanupElevators now (our pass first, then its resets; owreset lines name them with owresettime on);
        // "result" shows the counters.
        public static object OwElevIdleCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "dropped " + c[4150] + " riding " + c[4151] + " exempt " + c[4152] + " seen off " + c[4153] + " errors " + c[4154] + " passes " + c[4155];
            if (what == "result") return "owelevidle " + (ShortcutOn("owelevidle") ? "on" : "off") + ", hook " + (sOeiTried ? "tried" : "not tried") + ": " + counters;
            if (what == "census") return "owelevidle census: " + OeiPass(true);
            if (what != "run") return "owelevidle <census|run|result>";
            Type ce = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasOverwatch") { ce = a.GetType("NRaas.OverwatchSpace.Alarms.CleanupElevators"); break; }
            MethodInfo m = ce == null ? null : ce.GetMethod("PerformAlarm", kInstAll, null, Type.EmptyTypes, null);
            if (m == null) return "owelevidle run: Overwatch CleanupElevators.PerformAlarm not found";
            ((CompiledVoidFn)Fixes.Bind(typeof(CompiledVoidFn), Activator.CreateInstance(ce), m))();   // compiled call: PerformAlarm is swapped (OverwatchSpread), MethodInfo.Invoke would throw TargetException
            return "owelevidle run done, " + OwElevIdleCheck("result");
        }

        public class OeiHook
        {
            public void PerformHook(bool prompt)
            {
                try { OeiPass(false); } catch (Exception) { PatchCounters()[4154]++; }
                PerformOrig(prompt);
            }
            public void PerformOrig(bool prompt) { } // receives CleanupElevators.PrivatePerformAction's original body
        }
    }
}
