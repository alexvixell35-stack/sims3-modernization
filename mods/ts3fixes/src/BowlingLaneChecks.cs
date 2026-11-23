using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Entertainment;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // bowlinglane harness check (docs/559 §3). // Harness checks for this family: throwaway objects, never added to the world; nothing in the world changes.
    public static partial class Fixes
    {
        // BowlingLaneCheck
        //   run     (a) a throwaway lane (uninitialized, never placed) whose list holds the waiting bowler and a ghost id
        //           (no Sim): EA's ReadyToStart through the hook. off BROKEN = false (the waiter would wait for good);
        //           on FIXED = true, ghost dropped. (b) the route-fail counter fed 3 failed in-loop routes:
        //           off BROKEN = no exit; on FIXED = exit on the 3rd.
        //   result  counters 2448-2452
        public static object BowlingLaneCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "bowlinglane: released 2448=" + c[2448] + ", ghosts 2449=" + c[2449] + ", route exits 2450=" + c[2450] + ", restarts 2451=" + c[2451] + ", errors 2452=" + c[2452];
            if (what == "result") return counters;
            if (what != "run") return "bowlinglane <run|result>";
            if (sBlIds == null || sBlWatchers == null) return "bowlinglane check: not patched (N/A)";
            try { return BlCheckRun(counters); }
            catch (Exception e)
            {
                Exception x = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                return "bowlinglane check: exception " + x.GetType().Name + ": " + x.Message + " at " + (x.StackTrace ?? "").Replace("\n", " | ") + "; " + counters;
            }
        }

        // (a) runs the patched decision directly (the hook's ghost drop, then EA's ReadyToStart rule: every listed bowler
        // but the current one has a registered watcher; the throwaway lane has none), so it does not depend on calling
        // the swapped method on an object that was never placed (the 1114 in-game run returned nothing there).
        static string BlCheckRun(string counters)
        {
            bool on = ShortcutOn("bowlinglane");
            object lane = FormatterServices.GetUninitializedObject(sBlLane);
            List<ulong> ids = new List<ulong>();
            const ulong waiter = 0x7E57B0A1E0000001UL, ghost = 0x7E57B0A1E0000002UL;  // ids no SimDescription has
            ids.Add(waiter);
            ids.Add(ghost);
            sBlIds.SetValue(lane, ids);
            sBlCur.SetValue(lane, waiter);
            sBlWatchers.SetValue(lane, Activator.CreateInstance(sBlWatchers.FieldType));
            if (on) BlDropGhosts(lane as GameObject, false);
            bool ready = true;
            foreach (ulong id in ids) if (id != waiter) { ready = false; break; }  // no watchers registered
            string a = ready && !ids.Contains(ghost) ? "FIXED" : !ready && ids.Contains(ghost) ? "BROKEN" : "UNEXPECTED";
            const ulong probe = 0x7E57B0A1E0000003UL;
            bool exit = false;
            for (int i = 0; i < kBlRouteFails; i++) exit = BlRouteFailStep(probe, true, false, on);
            sBlFails.Remove(probe);
            string b = exit ? "FIXED" : "BROKEN";
            string verdict = a == b ? a : "UNEXPECTED";
            return "bowlinglane check (switch " + (on ? "on" : "off") + "): start wait with a ghost bowler " + a + " (ready=" + ready + "), route-fail loop " + b + "; verdict: " + verdict + "; " + counters;
        }
    }
}
