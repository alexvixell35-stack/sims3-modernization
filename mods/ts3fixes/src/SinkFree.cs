using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/686 §40: sinkfree (NPCs) / sinkfreehh (household and selected Sims too). Rules in SinkFreeCore.cs.
    // 1. Kitchen sinks first: ServingContainer.ServingContainer_CleanUp.GetBestObjectToWashDishes (EA: the closest sink of the
    //    Sim's room, else of the lot, in use or not; CheckInUseAndRoutable only halves an in-use sink's score) runs as EA's,
    //    then SinkFreeCore.Pick moves the dishes to the nearest free kitchen sink (with EA's own tests: not broken, not
    //    charred, CanRouteToSink, and EA's level factor). A bathroom sink (pedestal, or in a room with a toilet, shower or
    //    bath) gets dishes only on a lot without a kitchen sink. s691b: 17 of 23 household bathroom-sink waits were dishes.
    // 2. Drop it in the sink: Sink.WashDishes.Run of the Sim with the dishes, at a kitchen sink where another Sim runs
    //    WashDishes and no other kitchen sink is free: a real walk (RouteToObjectRadialRange) to the sink, EA's put-down on
    //    the floor (CarrySystem.PutDownOnFloor), the washer's TimedStage longer by EA's wash length / kMaxDishStackSize per
    //    dish, and at the end of that wash (its own Run returning true, i.e. EA's stage done) the dishes get EA's washed events
    //    and are destroyed as EA destroys a washed stack. At most SinkFreeCore.kHandCap dishes per wash, else EA's line. A
    //    wash that ends early (cancel, reset, need) leaves the dishes on the floor, dirty, for EA's cleanup; a dish another Sim
    //    has picked up by then is left to that Sim. Nothing is created, so nothing is lost or duplicated.
    // The only other nearest-sink pick for dishes (BarTray.CleanUp) already skips sinks in use; hand washing has no such pick.
    // Counters 5000-5017. Off = compare mode (would counters).
    public static partial class Fixes
    {
        const int kSnfCalls = 5000, kSnfEaBusy = 5001, kSnfSwitched = 5002, kSnfWould = 5003, kSnfNoneFree = 5004, kSnfExempt = 5005,
            kSnfErrors = 5006, kSnfEaFree = 5007, kSnfNotSink = 5008, kSnfLines = 5009, kSnfToKitchen = 5010, kSnfHanded = 5011,
            kSnfHandWould = 5012, kSnfCapped = 5013, kSnfWashed = 5014, kSnfLeft = 5015, kSnfTaken = 5016, kSnfHandBack = 5017;
        static string sSnfLast = "";
        static FieldInfo sSnfTotal;  // SimpleStage.mTotalTime (protected): the stage's length, for TimedStage.ResetCompletionTime
        static FieldInfo sSnfStack;  // ServingContainer_CleanUp.kMaxDishStackSize (private static, 8)

        sealed class SnfPend { public Sim Washer; public List<GameObject> Tops = new List<GameObject>(); public List<int> Counts = new List<int>(); public int Dishes; }
        static readonly Dictionary<InteractionInstance, SnfPend> sSnfPend = new Dictionary<InteractionInstance, SnfPend>();

        static void ApplySinkFree()
        {
            Patch(typeof(ServingContainer.ServingContainer_CleanUp), "GetBestObjectToWashDishes", Type.EmptyTypes, typeof(SnfHook), "PickHook", "PickOrig");
            Patch(typeof(Sink.WashDishes), "Run", Type.EmptyTypes, typeof(SnfWashHook), "RunHook", "RunOrig");
            sSnfTotal = typeof(SimpleStage).GetField("mTotalTime", BindingFlags.Instance | BindingFlags.NonPublic);
            sSnfStack = typeof(ServingContainer.ServingContainer_CleanUp).GetField("kMaxDishStackSize", BindingFlags.Static | BindingFlags.NonPublic);
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

        static bool SnfUsable(Sink s, Sim a)
        {
            if (s == null || s.HasBeenDestroyed || !s.InWorld || s.Charred) return false;
            if (s.Repairable != null && s.Repairable.Broken) return false;
            return s.CanRouteToSink(a);
        }

        static bool SnfKitchen(Sink s)
        {
            bool bath = false;
            Lot lot = s.LotCurrent;
            int room = s.RoomId;
            if (lot != null && room != 0)
                bath = lot.GetObjectsInRoom<Toilet>(room).Count > 0 || lot.GetObjectsInRoom<Shower>(room).Count > 0 || lot.GetObjectsInRoom<Bathtub>(room).Count > 0;
            return SinkFreeCore.Kitchen(s is SinkPedestal, bath);
        }

        static GameObject SnfPick(Sim a, GameObject ea)
        {
            int[] c = PatchCounters();
            c[kSnfCalls]++;
            Sink sink = ea as Sink;
            if (sink == null) { c[kSnfNotSink]++; return ea; }
            bool eaBusy = SnfBusy(sink, a);
            if (eaBusy) c[kSnfEaBusy]++; else c[kSnfEaFree]++;
            Lot lot = a.LotCurrent;
            Sink[] all = lot == null ? null : lot.GetObjects<Sink>();
            if (all == null || all.Length == 0) return ea;
            int n = all.Length, eaIdx = -1;
            bool[] kit = new bool[n], busy = new bool[n], lvl = new bool[n]; float[] dist = new float[n];
            for (int i = 0; i < n; i++)
            {
                Sink s = all[i];
                if (s == sink) eaIdx = i;
                bool use = s == sink || SnfUsable(s, a);
                dist[i] = use ? s.GetDistanceToObject(a) : float.MaxValue;
                busy[i] = !use || SnfBusy(s, a) || (s.Line != null && s.Line.FirstSim != null && s.Line.FirstSim != a);
                kit[i] = use && SnfKitchen(s);
                lvl[i] = use && s.Level != a.Level;
            }
            if (eaIdx < 0) return ea;
            int k = SinkFreeCore.Pick(eaIdx, kit, busy, dist, lvl, GlobalFunctions.kDifferentLevelScore);
            int d = SinkFreeCore.Decide(ShortcutOn("sinkfree"), ShlScope(a, ShortcutOn("sinkfreehh")), true, k != eaIdx && k >= 0);
            string who = ShlWho(a);
            if (d == SinkFreeCore.kNone)
            {
                if (eaBusy) { c[kSnfNoneFree]++; SnfLine("sinkfree: " + a.FullName + " " + who + ": " + sink.GetType().Name + " busy, no better sink, EA's pick"); }
                return ea;
            }
            Sink to = all[k];
            string what = sink.GetType().Name + (kit[eaIdx] ? " kitchen" : " bathroom") + " (users " + SnfOthers(sink, a) + ", line head "
                + (SnfHeadOther(sink, a) ? sink.Line.FirstSim.FullName : "none") + ", " + dist[eaIdx].ToString("0.0") + " m) -> " + to.GetType().Name
                + (kit[k] ? " kitchen" : " bathroom") + (busy[k] ? " busy" : " free") + " (" + dist[k].ToString("0.0") + " m)";
            if (d == SinkFreeCore.kExempt) { c[kSnfExempt]++; return ea; }
            if (d == SinkFreeCore.kWould) { c[kSnfWould]++; SnfLine("sinkfree: " + a.FullName + " " + who + ": would wash at " + what); return ea; }
            c[kSnfSwitched]++;
            if (kit[k] && !kit[eaIdx]) c[kSnfToKitchen]++;
            SnfLine("sinkfree: " + a.FullName + " " + who + ": washes at " + what);
            return to;
        }

        // The Sim washing dishes at s now (not a), and its WashDishes.
        static Sink.WashDishes SnfWasher(Sink s, Sim a, out Sim washer)
        {
            washer = null;
            foreach (Sim u in s.ActorsUsingMe)
            {
                if (u == null || u == a) continue;
                Sink.WashDishes w = u.CurrentInteraction as Sink.WashDishes;
                if (w != null && w.Target == s) { washer = u; return w; }
            }
            return null;
        }

        static bool SnfFreeKitchen(Sim a, Sink not)
        {
            Lot lot = a.LotCurrent;
            Sink[] all = lot == null ? null : lot.GetObjects<Sink>();
            if (all == null) return false;
            foreach (Sink s in all)
                if (s != not && SnfUsable(s, a) && !SnfBusy(s, a) && (s.Line == null || s.Line.FirstSim == null) && SnfKitchen(s)) return true;
            return false;
        }

        static int SnfMaxStack()
        {
            try { if (sSnfStack != null) return (int)sSnfStack.GetValue(null); } catch (ResetException) { throw; } catch (Exception) { }
            return 8;
        }

        // Rule 2 for the Sim a at the start of its WashDishes at s: true = handed in (its Run ends here).
        static bool SnfTryHand(Sink.WashDishes self)
        {
            Sim a = self.Actor; Sink s = self.Target;
            if (a == null || s == null) return false;
            ServingContainer held = a.GetObjectInRightHand() as ServingContainer;
            if (held == null || held.StackList == null) return false;
            Sim washer;
            Sink.WashDishes wi = SnfWasher(s, a, out washer);
            if (wi == null) return false;
            SnfPrune();
            SnfPend p;
            int handed = sSnfPend.TryGetValue(wi, out p) ? p.Dishes : 0;
            int dishes = held.StackList.Count;
            int[] c = PatchCounters();
            int d = SinkFreeCore.Handoff(ShortcutOn("sinkfree"), ShlScope(a, ShortcutOn("sinkfreehh")), SnfKitchen(s), true, SnfFreeKitchen(a, s), dishes, handed);
            string who = ShlWho(a);
            if (d == SinkFreeCore.kNone) return false;
            if (d == SinkFreeCore.kCapped) { c[kSnfCapped]++; SnfLine("sinkfree: " + a.FullName + " " + who + ": " + dishes + " dishes, " + handed + " handed in already: EA's line"); return false; }
            if (d == SinkFreeCore.kExempt) { c[kSnfExempt]++; return false; }
            if (d == SinkFreeCore.kWould) { c[kSnfHandWould]++; SnfLine("sinkfree: " + a.FullName + " " + who + ": would hand " + dishes + " dishes to " + washer.FullName + " at " + s.GetType().Name); return false; }
            if (!a.RouteToObjectRadialRange(s, 0.6f, 1.6f)) { a.RemoveExitReason(ExitReason.RouteFailed); return false; }  // no walk: EA's line
            if (a.HasExitReason(ExitReason.Default)) return false;
            if (washer.CurrentInteraction != wi) { c[kSnfHandBack]++; SnfLine("sinkfree: " + a.FullName + " " + who + ": " + washer.FullName + " stopped before the hand-in: washes itself"); return false; }
            int count = held.StackList.Count;
            CarrySystem.PutDownOnFloor(a);
            if (a.GetObjectInRightHand() == held || held.Parent != null) return false;  // still held: EA's wash
            if (p == null) { p = new SnfPend(); p.Washer = washer; sSnfPend[wi] = p; }
            p.Tops.Add(held); p.Counts.Add(count); p.Dishes += count;
            List<Sims3.UI.Hud.IStage> st = wi.IStages; TimedStage ts = st != null && st.Count > 0 ? st[0] as TimedStage : null;
            float extra = SinkFreeCore.ExtraMinutes(s.TuningSink.WashDishesLengthInMinutes, SnfMaxStack(), count);
            if (ts != null && sSnfTotal != null) ts.ResetCompletionTime((float)sSnfTotal.GetValue(ts) + extra);
            c[kSnfHanded] += count;
            SnfLine("sinkfree: " + a.FullName + " " + who + ": hands " + count + " dishes to " + washer.FullName + " at " + s.GetType().Name + ", wash +" + extra.ToString("0.0") + " sim-min");
            return true;
        }

        // End of a wash with handed-in dishes: washed (EA's events, destroyed) when its stage finished, else left down.
        static void SnfWashEnd(Sink.WashDishes wi, bool done)
        {
            SnfPend p;
            if (!sSnfPend.TryGetValue(wi, out p)) return;
            sSnfPend.Remove(wi);
            SnfSettle(p, done, wi.Actor);
        }

        static void SnfSettle(SnfPend p, bool done, Sim washer)
        {
            int[] c = PatchCounters();
            for (int i = 0; i < p.Tops.Count; i++)
            {
                GameObject top = p.Tops[i];
                ServingContainer sc = top as ServingContainer;
                bool down = top != null && !top.HasBeenDestroyed && top.InWorld && top.Parent == null && !top.InUse
                    && sc != null && sc.StackList != null && sc.StackList.Count == p.Counts[i];
                int e = SinkFreeCore.EndDish(done, down);
                if (e == SinkFreeCore.kEndTaken) { c[kSnfTaken] += p.Counts[i]; continue; }
                if (e == SinkFreeCore.kEndLeft) { c[kSnfLeft] += p.Counts[i]; continue; }
                foreach (IStackingCarryable item in sc.StackList) EventTracker.SendEvent(EventTypeId.kWashedOneDish, washer, item as IGameObject);
                EventTracker.SendEvent(EventTypeId.kCleanedObject, washer);
                top.Destroy();
                c[kSnfWashed] += p.Counts[i];
            }
        }

        // Washes that ended without our end hook (reset): their dishes stay down, dirty.
        static void SnfPrune()
        {
            if (sSnfPend.Count == 0) return;
            List<InteractionInstance> gone = null;
            foreach (KeyValuePair<InteractionInstance, SnfPend> kv in sSnfPend)
                if (kv.Value.Washer == null || kv.Value.Washer.HasBeenDestroyed || kv.Value.Washer.CurrentInteraction != kv.Key) (gone = gone ?? new List<InteractionInstance>()).Add(kv.Key);
            if (gone == null) return;
            foreach (InteractionInstance k in gone) { SnfPend p = sSnfPend[k]; sSnfPend.Remove(k); SnfSettle(p, false, p.Washer); }
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

        public abstract class SnfWashHook : Sink.WashDishes
        {
            public bool RunHook()
            {
                bool handed = false;
                try { handed = (ShortcutOn("sinkfree") || ShortcutOn("sinkfreelog")) && SnfTryHand(this); }
                catch (ResetException) { throw; }
                catch (Exception e) { SnfError(e); }
                if (handed) return true;
                bool ok = RunOrig();
                try { if (sSnfPend.Count > 0) SnfWashEnd(this, ok); }
                catch (ResetException) { throw; }
                catch (Exception e) { SnfError(e); }
                return ok;
            }
            public bool RunOrig() { return false; }  // receives Sink.WashDishes.Run's original body
        }

        static string SnfCounters()
        {
            int[] c = PatchCounters();
            return "sinkfree (npc " + (ShortcutOn("sinkfree") ? "on" : "off") + ", household " + (ShortcutOn("sinkfreehh") ? "on" : "off") + "): dish trips " + c[kSnfCalls]
                + " (EA's sink free " + c[kSnfEaFree] + ", EA's sink busy " + c[kSnfEaBusy] + ", not a sink " + c[kSnfNotSink] + "); switched " + c[kSnfSwitched]
                + " (to a kitchen sink " + c[kSnfToKitchen] + "), would " + c[kSnfWould] + ", no better sink " + c[kSnfNoneFree] + ", exempt " + c[kSnfExempt]
                + "; handed in " + c[kSnfHanded] + " dishes (would " + c[kSnfHandWould] + " trips, capped " + c[kSnfCapped] + ", washer stopped first " + c[kSnfHandBack]
                + "), washed " + c[kSnfWashed] + ", left down " + c[kSnfLeft] + ", taken by another " + c[kSnfTaken] + ", pending washes " + sSnfPend.Count
                + "; errors " + c[kSnfErrors] + ", lines " + c[kSnfLines] + "; last: " + (sSnfLast.Length > 0 ? sSnfLast : "none");
        }

        // SinkFreeCheck("run"): the rules against their cases (SinkFreeCore); "result": the counters; "reset": counters cleared.
        public static object SinkFreeCheck(string what)
        {
            if (what == "result") return SnfCounters();
            if (what == "reset") { int[] c = PatchCounters(); for (int i = kSnfCalls; i <= kSnfHandBack; i++) c[i] = 0; sSnfLast = ""; return "sinkfree: counters cleared"; }
            if (what != "run") return "sinkfree <run|result|reset>";
            bool[] kit = { false, true }, busy = { true, false }; float[] dist = { 1f, 6f };
            bool toKitchen = SinkFreeCore.Pick(0, kit, busy, dist, null, 0.25f) == 1;
            bool neverBath = SinkFreeCore.Pick(1, kit, new bool[] { false, true }, dist, null, 0.25f) == 1;
            bool hand = SinkFreeCore.Handoff(true, true, true, true, false, 2, 0) == SinkFreeCore.kGo;
            bool cap = SinkFreeCore.Handoff(true, true, true, true, false, 2, 2) == SinkFreeCore.kCapped;
            bool early = SinkFreeCore.EndDish(false, true) == SinkFreeCore.kEndLeft;
            bool hooked = typeof(ServingContainer.ServingContainer_CleanUp).GetMethod("GetBestObjectToWashDishes") != null && sSnfTotal != null;
            return "sinkfree check: bathroom pick -> free kitchen " + toKitchen + ", busy kitchen never -> bathroom " + neverBath + ", hand-in " + hand
                + ", cap " + cap + ", early end leaves dishes " + early + ", members found=" + hooked + "; " + SnfCounters();
        }
    }
}
