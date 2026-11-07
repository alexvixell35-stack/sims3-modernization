using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Agent
{
    // docs/222 §11.2 what a player sees when the camera lands on an unseen Sim, read back and judged (FocusRules).
    //   focus_watch {key?, seconds?, follow?, scope?}: scope "tagged" (default): camera onto the Sim of AppDomain tag `key` (default
    //     ts3fixes.autunseen.tagged, the last direct pick) and, with follow (default true), stays on it; scope "screen": camera untouched,
    //     every on-screen Sim outside the active household is watched. Samples every tick in a game task, returns at once. No tag: found false.
    //   focus_result: throws McpException on a wrong outcome (FocusRules.Judge), else before / after state and the worst values.
    //   focus_summary {min?, arrivalsMin?}: throws when fewer than min watches were judged or fewer than arrivalsMin Sims walked onto
    //     the camera's lot, so a repro cannot pass by checking nothing.
    public static partial class Handlers
    {
        sealed class FocusWatch { public string Name; public Sim Sim; public Dictionary<string, object> Before; public List<FocusRules.Sample> Samples = new List<FocusRules.Sample>(); }
        static Dictionary<ulong, FocusWatch> sFocus;
        static volatile int sFocusGen;
        static bool sFocusDone;
        static int sFocusJudged, sFocusArrivals;

        static void RegisterFocus()
        {
            Dispatcher.Register("focus_watch", FocusWatchCmd);
            Dispatcher.Register("focus_result", FocusResult);
            Dispatcher.Register("focus_summary", FocusSummary);
        }

        static Dictionary<string, object> FocusState(Sim s)
        {
            Dictionary<string, object> d = Util.Obj();
            InteractionInstance cur = s.CurrentInteraction;
            d["interaction"] = cur != null ? cur.GetInteractionName() : null;
            d["queue"] = QueueList(s);
            d["motives"] = MotiveValues(s);
            d["pos"] = Pos(s.Position);
            d["lot"] = s.LotCurrent != null ? s.LotCurrent.Name : null;
            d["routing"] = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting;
            d["in_transit"] = InTransit(s);
            return d;
        }

        static double FocusMinMotive(Sim s)
        {
            if (s.Motives == null) return 100;
            double m = 100;
            if (s.Motives.HasMotive(CommodityKind.Hunger)) m = Math.Min(m, s.Motives.GetValue(CommodityKind.Hunger));
            if (s.Motives.HasMotive(CommodityKind.Energy)) m = Math.Min(m, s.Motives.GetValue(CommodityKind.Energy));
            return m;
        }

        // queued (not running) autonomous picks that fail the game's own Test: ts3fixes FlipCheck must have cancelled them
        static int FocusBadQueue(Sim s)
        {
            int bad = 0;
            if (s.InteractionQueue == null) return 0;
            InteractionInstance head = s.InteractionQueue.GetHeadInteraction();
            foreach (InteractionInstance ii in new List<InteractionInstance>(s.InteractionQueue.InteractionList))
                if (ii != head && ii.Autonomous && !ii.Test()) bad++;
            return bad;
        }

        static FocusRules.Sample FocusSample(Sim s, double sec, bool test)
        {
            Vector3 p = s.Position;
            bool routing = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting;
            FocusRules.Sample x = new FocusRules.Sample();
            x.Sec = sec; x.SimMin = SimClock.ElapsedTime(TimeUnit.Minutes); x.X = p.x; x.Z = p.z; x.MinMotive = FocusMinMotive(s);
            x.OnScreen = World.IsObjectOnScreen(s);
            x.Hidden = s.Parent != null || s.GetHiddenFlags() != HiddenFlags.Nothing;  // in a car, a rabbit hole, a boat: not a visible move
            x.HasAction = s.CurrentInteraction != null;
            x.Routing = routing;
            x.Held = InTransit(s);
            x.BadQueue = test ? FocusBadQueue(s) : -1;
            return x;
        }

        static void FocusCamera(Sim s, float height, float back)
        {
            Vector3 t = s.Position;
            CameraController.SetPositionAndTarget(new Vector3(t.x, t.y + height, t.z - back), t);
        }

        static object FocusWatchCmd(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            int gen = ++sFocusGen;
            sFocusDone = false;
            Dictionary<ulong, FocusWatch> w = new Dictionary<ulong, FocusWatch>();
            sFocus = w;
            int ms = (int)(a.Double("seconds", 30) * 1000);
            bool screen = a.Str("scope", "tagged") == "screen", follow = !screen && a.Bool("follow", true);
            Sim one = null;
            if (!screen)
            {
                object d = AppDomain.CurrentDomain.GetData(a.Has("key") ? a.Str("key") : "ts3fixes.autunseen.tagged");
                one = d is ulong ? GameObject.GetObject(new ObjectGuid((ulong)d)) as Sim : null;
                if (one == null || one.HasBeenDestroyed) { sFocusDone = true; sFocus = null; r["found"] = false; return r; }
                r["was_on_screen"] = World.IsObjectOnScreen(one);
                w[one.ObjectId.Value] = new FocusWatch { Name = one.FullName, Sim = one, Before = FocusState(one) };
                FocusCamera(one, 12f, 12f);
                r["sim"] = one.FullName;
            }
            r["found"] = true;
            Lot camLot = screen ? LotManager.GetLotAtPoint(CameraController.GetTarget()) : null;
            Dictionary<ulong, bool> onLot = new Dictionary<ulong, bool>();
            if (camLot != null) foreach (Sim s in camLot.GetSims()) onLot[s.ObjectId.Value] = true;
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                int t0 = System.Environment.TickCount, lastTest = -1000;
                while (sFocusGen == gen)
                {
                    int el = System.Environment.TickCount - t0;
                    if (el >= ms) break;
                    bool test = el - lastTest >= 500;  // Test is real game work: twice a second is enough for a 3 s rule
                    if (test) lastTest = el;
                    if (screen)
                        foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                        {
                            if (s == null || s.HasBeenDestroyed || s.Household == Household.ActiveHousehold) continue;
                            FocusWatch fw;
                            if (!w.TryGetValue(s.ObjectId.Value, out fw))
                            {
                                if (!World.IsObjectOnScreen(s)) continue;  // seen once = sampled on, so a move while off screen is no pop
                                fw = new FocusWatch { Name = s.FullName, Sim = s, Before = FocusState(s) }; w[s.ObjectId.Value] = fw;
                            }
                            fw.Samples.Add(FocusSample(s, el / 1000.0, test));
                            if (camLot != null && s.LotCurrent == camLot && !onLot.ContainsKey(s.ObjectId.Value)) { onLot[s.ObjectId.Value] = true; sFocusArrivals++; }
                        }
                    else if (!one.HasBeenDestroyed)
                    {
                        if (follow) FocusCamera(one, 12f, 12f);
                        w[one.ObjectId.Value].Samples.Add(FocusSample(one, el / 1000.0, test));
                    }
                    Simulator.Sleep(0);
                }
                if (sFocusGen == gen) sFocusDone = true;
            }));
            return r;
        }

        static object FocusResult(Args a)
        {
            if (sFocus == null) { Dictionary<string, object> none = Util.Obj(); none["judged"] = 0; return none; }  // found false
            if (!sFocusDone) throw new McpException("focus_result: watch still running (step waited too short)");
            FocusRules.Limits l = new FocusRules.Limits();
            l.SnapS = a.Double("snapS", l.SnapS); l.MaxStepM = a.Double("maxStepM", l.MaxStepM); l.IdleSimMin = a.Double("idleSimMin", l.IdleSimMin);
            List<object> rows = new List<object>();
            foreach (FocusWatch w in sFocus.Values)
            {
                if (w.Samples[w.Samples.Count - 1].Sec - w.Samples[0].Sec < l.SnapS) continue;  // crossed the screen too fast to judge
                FocusRules.Stats st = new FocusRules.Stats();
                string why = FocusRules.Judge(w.Samples, l, st);
                if (why != null) throw new McpException("focus: " + w.Name + ": " + why);
                sFocusJudged++;
                Dictionary<string, object> d = Util.Obj();
                d["sim"] = w.Name; d["samples"] = w.Samples.Count; d["first_seen_s"] = st.FirstSeenS; d["snap_done_s"] = st.SnapDoneS;
                d["max_step_m"] = Math.Round(st.MaxStepM, 2); d["max_idle_sim_min"] = Math.Round(st.MaxIdleSimMin, 1);
                d["before"] = w.Before;
                if (!w.Sim.HasBeenDestroyed) d["after"] = FocusState(w.Sim);
                rows.Add(d);
            }
            sFocus = null;
            Dictionary<string, object> r = Util.Obj();
            r["judged"] = rows.Count;
            r["rows"] = rows;
            return r;
        }

        static object FocusSummary(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["judged"] = sFocusJudged; r["arrivals"] = sFocusArrivals;
            if (sFocusJudged < (int)a.Double("min", 1)) throw new McpException("focus_summary: only " + sFocusJudged + " Sims judged");
            if (sFocusArrivals < (int)a.Double("arrivalsMin", 0)) throw new McpException("focus_summary: only " + sFocusArrivals + " Sims arrived on the camera's lot");
            return r;
        }
    }
}
