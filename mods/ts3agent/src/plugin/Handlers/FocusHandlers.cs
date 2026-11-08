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
    //   focus_watch {key?, seconds?, follow?, scope?, lot?}: scope "tagged" (default): camera onto the Sim of AppDomain tag `key` (default
    //     ts3fixes.autunseen.tagged, the last direct pick) and, with follow (default true), stays on it; scope "screen": camera untouched,
    //     every on-screen Sim outside the active household is watched (lot: throws when the camera is on another lot). Samples every tick in a game task, returns at once. No tag: found false.
    //   focus_result {snapS?, maxStepM?, idleSimMin?, starveMotive?}: throws McpException on a wrong outcome (FocusRules.Judge), else
    //     before / after state and the worst values. starveMotive -101 switches the starving rule off (motives floor at -100); min_need still shows it.
    //   focus_summary {min?, arrivalsMin?, distinct?}: throws when fewer than min watches (distinct: true = distinct Sims) were judged or
    //     fewer than arrivalsMin spawn_sims crowd Sims walked onto the camera's lot, so a repro cannot pass by checking nothing
    //     (other Sims reach the lot too and are only reported: arrivals_other).
    //   distinct (switch, default false = the 2026-11-06 behaviour; docs/222 §11, ip-gu2-focus 11-07: 6 watches gave Devin x2, Mick x2 on
    //     BP and 4 x "found false" + Christopher x2 on IP): focus_watch {distinct: true} picks the newest tagged Sim not judged yet from a
    //     ring of the last 16 tags (a collector task reads the AppDomain tag every tick from the first such watch on), skipping gone and
    //     active-household Sims; focus_result {distinct: true} records the Sim as judged, drops it from the ring and clears the tag, and
    //     answers new_sim (true once per Sim: a stepsRequire tally). focus_reset clears all of it (start of a job).
    public static partial class Handlers
    {
        sealed class FocusWatch { public string Name; public Sim Sim; public Dictionary<string, object> Before; public List<FocusRules.Sample> Samples = new List<FocusRules.Sample>(); }
        static Dictionary<ulong, FocusWatch> sFocus;
        static volatile int sFocusGen;
        static bool sFocusDone;
        static int sFocusJudged, sFocusArrivals, sFocusArrivalsOther;
        static readonly List<string> sFocusArrivalNames = new List<string>();
        static readonly List<ulong> sFocusRing = new List<ulong>();          // distinct: recent tags, newest last
        static readonly Dictionary<ulong, bool> sFocusJudgedIds = new Dictionary<ulong, bool>();
        static int sFocusCollectGen;                                         // 0 = no collector; a new world load starts one again
        static ulong sFocusPicked;                                           // distinct: the Sim of the running watch

        static void RegisterFocus()
        {
            Dispatcher.Register("focus_watch", FocusWatchCmd);
            Dispatcher.Register("focus_result", FocusResult);
            Dispatcher.Register("focus_summary", FocusSummary);
            Dispatcher.Register("focus_reset", delegate(Args a) { sFocusRing.Clear(); sFocusJudgedIds.Clear(); sFocusJudged = sFocusArrivals = sFocusArrivalsOther = 0; sFocusArrivalNames.Clear(); sFocusPicked = 0; sFocusCollectGen++; sFocusCollecting = false; return Util.Obj(); });
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
        static int FocusBadQueue(Sim s, out string names)
        {
            int bad = 0;
            names = null;
            if (s.InteractionQueue == null) return 0;
            InteractionInstance head = s.InteractionQueue.GetHeadInteraction();
            foreach (InteractionInstance ii in new List<InteractionInstance>(s.InteractionQueue.InteractionList))
                if (ii != head && ii.Autonomous && !ii.Test()) { bad++; names = (names == null ? "" : names + ", ") + ii.GetInteractionName(); }
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            bad = FocusRules.SnapQueue(bad, c != null && c.Length > 624 ? c[624] : 0);
            if (bad == 0) names = null;
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
            InteractionInstance cur = s.CurrentInteraction;
            x.HasAction = cur != null;
            x.Interaction = cur != null ? cur.GetInteractionName() : null;
            x.InRabbitHole = s.SimInRabbitHolePosture;
            x.Routing = routing;
            x.Held = InTransit(s);
            string bad = null;
            x.BadQueue = test ? FocusBadQueue(s, out bad) : -1;
            x.BadNames = bad;
            return x;
        }

        static bool sFocusCollecting;
        static int sFocusBeat;  // the collector's last tick (wall ms): a world reload ends its task, a stale beat starts a new one
        // distinct: every tick, the tag of the newest direct pick into the ring (a tag set between two watches is not lost)
        static void FocusCollect(string key)
        {
            if (sFocusCollecting && unchecked(System.Environment.TickCount - sFocusBeat) < 2000) return;
            sFocusCollecting = true;
            int gen = ++sFocusCollectGen;
            sFocusBeat = System.Environment.TickCount;
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                ulong last = 0;
                while (sFocusCollectGen == gen)
                {
                    sFocusBeat = System.Environment.TickCount;
                    object d = AppDomain.CurrentDomain.GetData(key);
                    ulong id = d is ulong ? (ulong)d : 0;
                    if (id != last) FocusRules.RingAdd(sFocusRing, id, 16);
                    last = id;
                    Simulator.Sleep(0);
                }
            }));
        }

        // distinct: newest ring Sim that is alive, in the world, outside the active household and not judged yet
        static Sim FocusPickDistinct(string key)
        {
            object d = AppDomain.CurrentDomain.GetData(key);
            if (d is ulong) FocusRules.RingAdd(sFocusRing, (ulong)d, 16);  // the current tag too (first watch)
            ulong pick = FocusRules.PickNewest(sFocusRing, sFocusJudgedIds, delegate(ulong id)
            {
                Sim s = GameObject.GetObject(new ObjectGuid(id)) as Sim;
                return s != null && !s.HasBeenDestroyed && s.InWorld && !s.IsSelectable && s.Household != Household.ActiveHousehold;
            });
            return pick != 0 ? GameObject.GetObject(new ObjectGuid(pick)) as Sim : null;
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
                string key = a.Has("key") ? a.Str("key") : "ts3fixes.autunseen.tagged";
                bool distinct = a.Bool("distinct", false);
                sFocusPicked = 0;
                if (distinct)
                {
                    FocusCollect(key);
                    one = FocusPickDistinct(key);
                    r["ring"] = sFocusRing.Count;
                    if (one == null) { sFocusDone = true; sFocus = null; r["found"] = false; r["reason"] = "no tagged Sim left that was not judged"; return r; }
                    sFocusPicked = one.ObjectId.Value;
                }
                else
                {
                    object d = AppDomain.CurrentDomain.GetData(key);
                    one = d is ulong ? GameObject.GetObject(new ObjectGuid((ulong)d)) as Sim : null;
                    if (one == null || one.HasBeenDestroyed) { sFocusDone = true; sFocus = null; r["found"] = false; return r; }
                }
                r["was_on_screen"] = World.IsObjectOnScreen(one);
                w[one.ObjectId.Value] = new FocusWatch { Name = one.FullName, Sim = one, Before = FocusState(one) };
                FocusCamera(one, 12f, 12f);
                r["sim"] = one.FullName;
            }
            r["found"] = true;
            Lot camLot = screen ? LotManager.GetLotAtPoint(CameraController.GetTarget()) : null;
            if (screen) { r["camera_target"] = Pos(CameraController.GetTarget()); r["camera_lot"] = camLot != null ? camLot.Name : null; }
            // lot: the round's camera lot; another lot fails the step (docs/222 §11.11: round 2 counted Plumbob Pictures Backlot arrivals)
            if (screen && a.Has("lot"))
            {
                Lot want = FindLotByName(a.Str("lot"));
                // houseboat: GetLotAtPoint under the boat is its port (ip-gu2-arrive-off 11-08: Picturesque Port), so judge the distance
                bool on = FocusRules.CameraOnLot(camLot == want, want.IsHouseboatLot(), (CameraController.GetTarget() - LotViewPoint(want)).Length());
                if (on) camLot = want;
                if (!on) { sFocusDone = true; sFocus = null; throw new McpException("focus_watch: camera on " + (camLot != null ? camLot.Name : "no lot") + ", not " + want.Name); }
            }
            Dictionary<ulong, bool> onLot = new Dictionary<ulong, bool>();
            if (camLot != null) foreach (Sim s in camLot.GetSims()) onLot[s.ObjectId.Value] = true;
            // docs/222 §11.11: only spawn_sims crowd Sims count as arrivals; passers-by and rabbit-hole workers made 3 vs 2 by chance
            Dictionary<ulong, bool> crowd = new Dictionary<ulong, bool>();
            foreach (ulong id in SpawnedSims()) crowd[id] = true;
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
                            if (camLot != null && s.LotCurrent == camLot && !onLot.ContainsKey(s.ObjectId.Value))
                            {
                                onLot[s.ObjectId.Value] = true;
                                if (s.SimDescription != null && FocusRules.CrowdArrival(s.SimDescription.SimDescriptionId, crowd)) { sFocusArrivals++; sFocusArrivalNames.Add(s.FullName); }
                                else sFocusArrivalsOther++;
                            }
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
            if (sFocus == null) { Dictionary<string, object> none = Util.Obj(); none["judged"] = 0; none["new_sim"] = false; return none; }  // found false
            if (!sFocusDone) throw new McpException("focus_result: watch still running (step waited too short)");
            FocusRules.Limits l = new FocusRules.Limits();
            l.SnapS = a.Double("snapS", l.SnapS); l.MaxStepM = a.Double("maxStepM", l.MaxStepM); l.IdleSimMin = a.Double("idleSimMin", l.IdleSimMin); l.StarveMotive = a.Double("starveMotive", l.StarveMotive);
            List<object> rows = new List<object>();
            foreach (FocusWatch w in sFocus.Values)
            {
                if (w.Samples[w.Samples.Count - 1].Sec - w.Samples[0].Sec < l.SnapS) continue;  // crossed the screen too fast to judge
                FocusRules.Stats st = new FocusRules.Stats();
                string why = FocusRules.Judge(w.Samples, l, st);
                if (why != null)
                {
                    // docs/222 §11.10: a pop names its mover only if our own placement log is in the reason
                    if (why.StartsWith("pop:"))
                    {
                        Dictionary<string, object> moved = AppDomain.CurrentDomain.GetData("ts3fixes.moved") as Dictionary<string, object>;
                        object mv = null;
                        if (moved != null) moved.TryGetValue(w.Name, out mv);
                        why += "; moved: " + (mv ?? "none");
                        List<object> recent = AppDomain.CurrentDomain.GetData("ts3fixes.recent") as List<object>;
                        if (recent != null && recent.Count > 0)
                            why += "; recent: " + string.Join(" | ", recent.GetRange(Math.Max(0, recent.Count - 10), Math.Min(10, recent.Count)).ConvertAll(o => "" + o).ToArray());
                    }
                    throw new McpException("focus: " + w.Name + ": " + why);
                }
                sFocusJudged++;
                Dictionary<string, object> d = Util.Obj();
                d["sim"] = w.Name; d["samples"] = w.Samples.Count; d["first_seen_s"] = st.FirstSeenS; d["snap_done_s"] = st.SnapDoneS;
                d["max_step_m"] = Math.Round(st.MaxStepM, 2); d["max_idle_sim_min"] = Math.Round(st.MaxIdleSimMin, 1);
                if (st.RhSnaps > 0) { d["rh_snap"] = st.RhSnaps; d["rh_snap_note"] = st.RhNote; }
                d["min_need"] = Math.Round(w.Samples[w.Samples.Count - 1].MinMotive);
                d["before"] = w.Before;
                if (!w.Sim.HasBeenDestroyed) d["after"] = FocusState(w.Sim);
                rows.Add(d);
            }
            sFocus = null;
            Dictionary<string, object> r = Util.Obj();
            bool fresh = false;
            if (a.Bool("distinct", false) && sFocusPicked != 0)
            {
                // judged or crossed too fast: either way this Sim is done; the tag goes so the next watch needs a new pick
                fresh = rows.Count > 0 && !sFocusJudgedIds.ContainsKey(sFocusPicked);
                if (rows.Count > 0) sFocusJudgedIds[sFocusPicked] = true;
                sFocusRing.Remove(sFocusPicked);
                AppDomain.CurrentDomain.SetData(a.Has("key") ? a.Str("key") : "ts3fixes.autunseen.tagged", null);
                sFocusPicked = 0;
            }
            r["judged"] = rows.Count;
            r["new_sim"] = fresh;
            r["distinct_sims"] = sFocusJudgedIds.Count;
            r["rows"] = rows;
            return r;
        }

        static object FocusSummary(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["judged"] = sFocusJudged; r["arrivals"] = sFocusArrivals; r["arrivals_other"] = sFocusArrivalsOther; r["distinct_sims"] = sFocusJudgedIds.Count;
            r["arrival_names"] = sFocusArrivalNames.ToArray();
            int n = a.Bool("distinct", false) ? sFocusJudgedIds.Count : sFocusJudged;
            if (n < (int)a.Double("min", 1)) throw new McpException("focus_summary: only " + n + (a.Bool("distinct", false) ? " distinct" : "") + " Sims judged");
            if (sFocusArrivals < (int)a.Double("arrivalsMin", 0))
                throw new McpException("focus_summary: only " + sFocusArrivals + " crowd Sims arrived on the camera's lot (" + sFocusArrivalsOther + " other Sims did)");
            return r;
        }
    }
}
