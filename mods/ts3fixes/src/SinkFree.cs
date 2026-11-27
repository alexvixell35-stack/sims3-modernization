using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/686 §40: sinkfree (NPCs) / sinkfreehh (household and selected Sims too). EA's dish cleanup
    // (ServingContainer.ServingContainer_CleanUp.GetBestObjectToWashDishes) takes the closest sink of the Sim's own room, else
    // of the lot, in use or not (CheckInUseAndRoutable only halves an in-use sink's score), so in a small house the Sim with
    // the dishes stands in the bathroom sink's line behind a teeth brusher while the kitchen sink is free (s691b: 17 of 23
    // household bathroom-sink waits). The hook runs EA's pick first; when it is a sink that another Sim uses or heads the line
    // of, the nearest free sink of the lot that passes EA's own tests (not broken, not charred, CanRouteToSink) is returned
    // instead, with EA's level factor; none free = EA's pick. Dishwashers and synthesizers EA picks stay. The only other
    // nearest-sink pick for dishes (BarTray.CleanUp) already skips sinks in use; hand washing has no such pick (autonomy
    // scores each sink). Counters 5000-5009. Off = compare mode (5003).
    public static partial class Fixes
    {
        const int kSnfCalls = 5000, kSnfEaBusy = 5001, kSnfSwitched = 5002, kSnfWould = 5003, kSnfNoneFree = 5004, kSnfExempt = 5005,
            kSnfErrors = 5006, kSnfEaFree = 5007, kSnfNotSink = 5008, kSnfLines = 5009;
        static string sSnfLast = "";

        static void ApplySinkFree()
        {
            Patch(typeof(ServingContainer.ServingContainer_CleanUp), "GetBestObjectToWashDishes", Type.EmptyTypes, typeof(SnfHook), "PickHook", "PickOrig");
        }

        static void SnfError(Exception e) { if (PatchCounters()[kSnfErrors]++ < 5) DiagLine("sinkfree: error " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace); }

        static int SnfOthers(Sink s, Sim a)
        {
            int n = 0;
            foreach (Sim u in s.ActorsUsingMe) if (u != null && u != a) n++;
            return n;
        }

        static bool SnfHeadOther(Sink s, Sim a)
        {
            Sim head = s.Line == null ? null : s.Line.FirstSim;
            return head != null && head != a;
        }

        static bool SnfBusy(Sink s, Sim a) { return SinkFreeCore.Busy(SnfOthers(s, a), SnfHeadOther(s, a)); }

        // The nearest free sink of the Sim's lot with EA's own tests, or null.
        static Sink SnfNearestFree(Sim a, Sink skip)
        {
            Lot lot = a.LotCurrent;
            if (lot == null) return null;
            Sink[] all = lot.GetObjects<Sink>();
            if (all == null || all.Length == 0) return null;
            float[] dist = new float[all.Length]; bool[] ok = new bool[all.Length]; bool[] lvl = new bool[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                Sink s = all[i];
                if (s == null || s == skip || s.HasBeenDestroyed || !s.InWorld || s.Charred) continue;
                if (s.Repairable != null && s.Repairable.Broken) continue;
                if (SnfBusy(s, a) || (s.Line != null && s.Line.FirstSim != null) || !s.CanRouteToSink(a)) continue;
                ok[i] = true; dist[i] = s.GetDistanceToObject(a); lvl[i] = s.Level != a.Level;
            }
            int k = SinkFreeCore.Nearest(dist, ok, lvl, GlobalFunctions.kDifferentLevelScore);
            return k < 0 ? null : all[k];
        }

        static GameObject SnfPick(Sim a, GameObject ea)
        {
            int[] c = PatchCounters();
            c[kSnfCalls]++;
            Sink sink = ea as Sink;
            if (sink == null) { c[kSnfNotSink]++; return ea; }
            if (!SnfBusy(sink, a)) { c[kSnfEaFree]++; return ea; }
            c[kSnfEaBusy]++;
            Sink free = SnfNearestFree(a, sink);
            bool on = ShortcutOn("sinkfree");
            int d = SinkFreeCore.Decide(on, ShlScope(a, ShortcutOn("sinkfreehh")), true, true, free == null ? -1 : 0);
            string who = ShlWho(a);
            if (d == SinkFreeCore.kNone) { c[kSnfNoneFree]++; SnfLine("sinkfree: " + a.FullName + " " + who + ": " + sink.GetType().Name + " busy, no free sink, EA's pick"); return ea; }
            string what = sink.GetType().Name + " (busy: users " + SnfOthers(sink, a) + ", line head " + (SnfHeadOther(sink, a) ? sink.Line.FirstSim.FullName : "none")
                + ", " + sink.GetDistanceToObject(a).ToString("0.0") + " m) -> " + free.GetType().Name + " (" + free.GetDistanceToObject(a).ToString("0.0") + " m)";
            if (d == SinkFreeCore.kExempt) { c[kSnfExempt]++; return ea; }
            if (d == SinkFreeCore.kWould) { c[kSnfWould]++; SnfLine("sinkfree: " + a.FullName + " " + who + ": would wash at " + what); return ea; }
            c[kSnfSwitched]++;
            SnfLine("sinkfree: " + a.FullName + " " + who + ": washes at " + what);
            return free;
        }

        static void SnfLine(string s)
        {
            sSnfLast = s;
            if (!ShortcutOn("sinkfreelog")) return;
            int[] c = PatchCounters();
            if (c[kSnfLines] < 300 && DiagLine(s)) c[kSnfLines]++;
        }

        public abstract class SnfHook : ServingContainer.ServingContainer_CleanUp
        {
            public GameObject PickHook()
            {
                GameObject ea = PickOrig();
                try { return SnfPick(Actor, ea); }
                catch (ResetException) { throw; }
                catch (Exception e) { SnfError(e); return ea; }
            }
            public GameObject PickOrig() { return null; }  // receives GetBestObjectToWashDishes' original body
        }

        static string SnfCounters()
        {
            int[] c = PatchCounters();
            return "sinkfree (npc " + (ShortcutOn("sinkfree") ? "on" : "off") + ", household " + (ShortcutOn("sinkfreehh") ? "on" : "off") + "): dish trips " + c[kSnfCalls]
                + " (EA's sink free " + c[kSnfEaFree] + ", EA's sink busy " + c[kSnfEaBusy] + ", not a sink " + c[kSnfNotSink] + "); switched " + c[kSnfSwitched]
                + ", would " + c[kSnfWould] + ", no free sink " + c[kSnfNoneFree] + ", exempt " + c[kSnfExempt] + ", errors " + c[kSnfErrors] + ", lines " + c[kSnfLines]
                + "; last: " + (sSnfLast.Length > 0 ? sSnfLast : "none");
        }

        // SinkFreeCheck("run"): the rule against its cases (SinkFreeCore); "result": the counters; "reset": counters cleared.
        public static object SinkFreeCheck(string what)
        {
            if (what == "result") return SnfCounters();
            if (what == "reset") { int[] c = PatchCounters(); for (int i = kSnfCalls; i <= kSnfLines; i++) c[i] = 0; sSnfLast = ""; return "sinkfree: counters cleared"; }
            if (what != "run") return "sinkfree <run|result|reset>";
            bool go = SinkFreeCore.Decide(true, true, true, true, 0) == SinkFreeCore.kGo;
            bool would = SinkFreeCore.Decide(false, true, true, true, 0) == SinkFreeCore.kWould;
            bool keepFree = SinkFreeCore.Decide(true, true, true, false, 0) == SinkFreeCore.kNone;
            bool keepNone = SinkFreeCore.Decide(true, true, true, true, -1) == SinkFreeCore.kNone;
            bool keepDw = SinkFreeCore.Decide(true, true, false, true, 0) == SinkFreeCore.kNone;
            bool exempt = SinkFreeCore.Decide(true, false, true, true, 0) == SinkFreeCore.kExempt;
            bool hooked = typeof(ServingContainer.ServingContainer_CleanUp).GetMethod("GetBestObjectToWashDishes") != null;
            return "sinkfree check: busy pick -> free sink " + go + ", off = would " + would + ", free pick kept " + keepFree + ", none free kept " + keepNone
                + ", dishwasher kept " + keepDw + ", out of scope exempt " + exempt + ", members found=" + hooked + "; " + SnfCounters();
        }
    }
}
