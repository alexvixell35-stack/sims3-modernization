using System;
using System.Collections;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // hitch_watch (read only; docs/546 §6): a sim-thread task that stamps every tick with DateTime.UtcNow. A gap of at
    // least min_ms between two of its ticks is one sim iteration that held the thread (a hitch). For each hitch it keeps
    // what the household Sims and the Sims on the active lot were doing on the tick before ("before") and on the tick
    // after ("after"): current interaction, the interaction it waits on for a transition, queue length, routing,
    // posture. A Sim whose state is the same before and after, mid-interaction, is the likely holder; a Sim that started
    // something new is what the hitch led into. Native waits (route job lock, steering) are not visible here: pair with
    // ts3native [HitchAttr] Holders=1 in the same run.
    //   hitch_watch {"start":true, "min_ms":100}   clears, starts
    //   hitch_watch {}                             state: ticks, hitches, longest
    //   hitch_watch {"top":30}                     longest hitches with before/after
    //   hitch_watch {"stop":true}
    public static partial class Handlers
    {
        const string kHwKey = "ts3agent.hitchwatch";

        public class HwSnap { public string Name; public bool Hh; public string Cur, Trans, Posture; public int Queue; public bool Routing; }
        public class HwHitch { public double Ms; public string When; public List<HwSnap> Before, After; }

        static void RegisterHitchWatch()
        {
            Dispatcher.Register("hitch_watch", HitchWatch);
        }

        public class HwTask : Task
        {
            internal static HwTask sCur;
            internal bool Running = true;
            internal long MinTicks = 100 * TimeSpan.TicksPerMillisecond;
            internal long TickCount, Last;
            internal double MaxMs;
            internal readonly List<HwHitch> Hitches = new List<HwHitch>();
            List<HwSnap> mPrev;

            public override void Simulate()
            {
                while (Running)
                {
                    long now = DateTime.UtcNow.Ticks;
                    List<HwSnap> snap = null;
                    try { snap = Snapshot(); } catch (Exception) { }
                    if (Last != 0)
                    {
                        long gap = now - Last;
                        double ms = gap / (double)TimeSpan.TicksPerMillisecond;
                        if (ms > MaxMs) MaxMs = ms;
                        if (gap >= MinTicks)
                        {
                            HwHitch h = new HwHitch();
                            h.Ms = Math.Round(ms, 1);
                            try { h.When = Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString(); } catch (Exception) { h.When = "?"; }
                            h.Before = mPrev; h.After = snap;
                            lock (Hitches)
                            {
                                Hitches.Add(h);
                                if (Hitches.Count > 400)  // keep the 300 longest
                                {
                                    Hitches.Sort(delegate(HwHitch x, HwHitch y) { return y.Ms.CompareTo(x.Ms); });
                                    Hitches.RemoveRange(300, Hitches.Count - 300);
                                }
                            }
                        }
                    }
                    mPrev = snap;
                    Last = DateTime.UtcNow.Ticks;  // the snapshot itself is not part of the next gap
                    TickCount++;
                    Simulator.Sleep(0);
                }
                Simulator.DestroyObject(ObjectId);
            }

            static List<HwSnap> Snapshot()
            {
                List<HwSnap> l = new List<HwSnap>();
                Household hh = Household.ActiveHousehold;
                Sims3.Gameplay.Core.Lot lot = null;
                if (hh != null)
                {
                    foreach (Sim s in hh.AllActors) { if (s != null) l.Add(Snap(s, true)); }
                    Sim act = Sims3.Gameplay.Actors.Sim.ActiveActor;
                    if (act != null) lot = act.LotCurrent;
                }
                if (lot != null)
                {
                    int n = 0;
                    foreach (Sim s in lot.GetAllActors())
                    {
                        if (s == null || (hh != null && s.Household == hh)) continue;
                        l.Add(Snap(s, false));
                        if (++n >= 8) break;
                    }
                }
                return l;
            }

            static HwSnap Snap(Sim s, bool hh)
            {
                HwSnap x = new HwSnap();
                x.Name = s.FirstName + (s.SimDescription != null && !s.SimDescription.IsHuman ? "(" + s.SimDescription.Species + ")" : ""); x.Hh = hh;
                InteractionInstance cur = s.CurrentInteraction;
                x.Cur = cur != null ? cur.GetType().Name : null;
                InteractionInstance tr = s.InteractionQueue != null ? s.InteractionQueue.TransitionInteraction : null;
                x.Trans = tr != null ? tr.GetType().Name : null;
                x.Queue = s.InteractionQueue != null ? s.InteractionQueue.Count : 0;
                x.Routing = s.IsRouting;
                x.Posture = s.Posture != null ? s.Posture.GetType().Name : null;
                return x;
            }
        }

        static object HwSnapList(List<HwSnap> l)
        {
            List<object> o = new List<object>();
            if (l == null) return o;
            foreach (HwSnap x in l)
            {
                string s = (x.Hh ? "HH " : "") + x.Name + ": " + (x.Cur ?? "-");
                if (x.Trans != null && x.Trans != x.Cur) s += " (transition " + x.Trans + ")";
                if (x.Routing) s += " [routing]";
                s += " q" + x.Queue + " " + (x.Posture ?? "");
                o.Add(s);
            }
            return o;
        }

        static object HitchWatch(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            HwTask t = HwTask.sCur;
            if (a.Bool("stop", false) && t != null) { t.Running = false; }
            if (a.Bool("start", false))
            {
                if (t != null) t.Running = false;
                t = new HwTask();
                t.MinTicks = (long)(a.Long("min_ms", 100) * TimeSpan.TicksPerMillisecond);
                HwTask.sCur = t;
                Simulator.AddObject(t);
            }
            if (t == null) { r["running"] = false; return r; }
            r["running"] = t.Running;
            r["ticks"] = t.TickCount;
            r["max_ms"] = Math.Round(t.MaxMs, 1);
            List<HwHitch> hs;
            lock (t.Hitches) hs = new List<HwHitch>(t.Hitches);
            r["hitches"] = hs.Count;
            int n200 = 0, n500 = 0;
            foreach (HwHitch h in hs) { if (h.Ms >= 200) n200++; if (h.Ms >= 500) n500++; }
            r["over_200"] = n200; r["over_500"] = n500;
            if (a.Has("top"))
            {
                hs.Sort(delegate(HwHitch x, HwHitch y) { return y.Ms.CompareTo(x.Ms); });
                List<object> o = new List<object>();
                for (int i = 0; i < hs.Count && i < (int)a.Long("top", 30); i++)
                {
                    Dictionary<string, object> d = Util.Obj();
                    d["ms"] = hs[i].Ms; d["sim_time"] = hs[i].When;
                    d["before"] = HwSnapList(hs[i].Before); d["after"] = HwSnapList(hs[i].After);
                    o.Add(d);
                }
                r["top"] = o;
            }
            return r;
        }
    }
}

namespace Ts3Agent
{
    // household_routine (docs/546 §6): one round of the everyday actions players use most, pushed as user-directed
    // interactions on the household Sims whose queue is (nearly) empty, each Sim rotating through the list. Matches the
    // pie-menu labels like do_interaction does (enabled entries only), so it fails soft when an object or menu entry is
    // missing. Run it every ~30 s during a hitch_watch phase.
    //   household_routine {}            -> per Sim: what was queued, or why not
    //   household_routine {"only":"outfit"}   one action kind for every Sim
    public static partial class Handlers
    {
        // kind, object type-name keys ("self" = the Sim, "sim" = another household Sim), label keys (first match wins)
        static readonly string[][] kRoutine = {
            new string[] { "meal", "Fridge", "", "Quick Meal|Have" },
            new string[] { "outfit", "self", "", "Change Clothes|Change into Everyday|Change into Formal|Change into Athletic|Change" },
            new string[] { "shower", "Shower|Tub", "", "Take Shower|Shower|Take Bath" },
            new string[] { "social", "sim", "", "Chat|Talk About|Joke|Hug" },
            new string[] { "tv", "TV", "", "Watch" },
            new string[] { "computer", "Computer", "", "Play|Surf|Chat" },
            new string[] { "read", "Bookshelf|Book", "", "Read" },
            new string[] { "toilet", "Toilet", "", "Use" },
            new string[] { "nap", "Bed", "", "Nap|Sleep|Relax" },
            new string[] { "music", "Stereo|Radio", "", "Dance|Turn On|Listen" },
            new string[] { "pet", "pet", "", "Play|Pet|Brush|Groom|Ride|Feed|Cuddle|Nuzzle" },
        };
        // pets (dogs, cats, horses, small pets in the household): their own rotation
        static readonly string[][] kPetRoutine = {
            new string[] { "peteat", "Bowl|Trough|PetFood|Feeder", "", "Eat|Drink" },
            new string[] { "petplay", "sim", "", "Play|Greet|Nuzzle|Lick|Sniff|Jump On|Chase" },
            new string[] { "petsleep", "PetBed|Bed|Stall|Basket", "", "Sleep|Nap|Relax|Lie" },
            new string[] { "petwander", "self", "", "Chase Tail|Roll|Dig|Wander|Graze|Hunt|Scratch|Play" },
        };
        static readonly Dictionary<ulong, int> sRoutineNext = new Dictionary<ulong, int>();

        static void RegisterRoutine() { Dispatcher.Register("household_routine", HouseholdRoutine); }

        static object HouseholdRoutine(Args a)
        {
            Sims3.Gameplay.CAS.Household hh = Sims3.Gameplay.CAS.Household.ActiveHousehold;
            if (hh == null || hh.LotHome == null) throw new McpException("no active household with a home lot");
            string only = a.Str("only", "");
            List<object> outl = new List<object>();
            List<Sims3.Gameplay.Abstracts.GameObject> objs = new List<Sims3.Gameplay.Abstracts.GameObject>(hh.LotHome.GetObjects<Sims3.Gameplay.Abstracts.GameObject>());
            foreach (Sims3.Gameplay.Actors.Sim s in hh.AllActors)
            {
                if (s == null || s.SimDescription == null || s.SimDescription.Baby) continue;
                string[][] list = s.SimDescription.IsHuman ? kRoutine : kPetRoutine;
                string res = "busy (queue " + s.InteractionQueue.Count + ")";
                if (s.InteractionQueue.Count <= 1 && s.InteractionQueue.CanPlayerQueue())
                {
                    int start; sRoutineNext.TryGetValue(s.SimDescription.SimDescriptionId, out start);
                    res = "nothing matched";
                    for (int k = 0; k < list.Length; k++)
                    {
                        string[] act = list[(start + k) % list.Length];
                        if (only.Length > 0 && act[0] != only) continue;
                        string got = null;
                        try { got = RoutineTry(s, act, objs, hh); } catch (Exception e) { got = null; res = act[0] + ": " + e.Message; }
                        if (got != null) { res = act[0] + ": " + got; sRoutineNext[s.SimDescription.SimDescriptionId] = (start + k + 1) % list.Length; break; }
                    }
                }
                outl.Add(s.FirstName + (s.SimDescription.IsHuman ? "" : " (" + s.SimDescription.Species + ")") + " -> " + res);
            }
            Dictionary<string, object> r = Util.Obj();
            r["sims"] = outl;
            return r;
        }

        static string RoutineTry(Sims3.Gameplay.Actors.Sim s, string[] act, List<Sims3.Gameplay.Abstracts.GameObject> objs, Sims3.Gameplay.CAS.Household hh)
        {
            List<Sims3.Gameplay.Abstracts.GameObject> targets = new List<Sims3.Gameplay.Abstracts.GameObject>();
            if (act[1] == "self") targets.Add(s);
            else if (act[1] == "sim") { foreach (Sims3.Gameplay.Actors.Sim o in hh.AllActors) if (o != null && o != s && o.LotCurrent == s.LotCurrent && o.SimDescription.IsHuman) targets.Add(o); }
            else if (act[1] == "pet") { foreach (Sims3.Gameplay.Actors.Sim o in hh.AllActors) if (o != null && o != s && o.LotCurrent == s.LotCurrent && !o.SimDescription.IsHuman) targets.Add(o); }
            else
            {
                string[] keys = act[1].Split('|');
                foreach (Sims3.Gameplay.Abstracts.GameObject o in objs)
                {
                    if (o == null || !o.InWorld) continue;
                    string tn = o.GetType().Name;
                    foreach (string k in keys) if (tn.IndexOf(k) >= 0) { targets.Add(o); break; }
                    if (targets.Count >= 4) break;
                }
            }
            string[] labels = act[3].Split('|');
            foreach (Sims3.Gameplay.Abstracts.GameObject t in targets)
            {
                List<MenuEntry> menu = BuildMenu(s, t, HitFor(t, new Args(new Dictionary<string, object>())));
                foreach (string want in labels)
                    foreach (MenuEntry e in menu)
                    {
                        if (!e.Enabled || e.Iop == null || e.Label.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        InteractionInstanceParameters p = new InteractionInstanceParameters(e.Iop, s,
                            new InteractionPriority(InteractionPriorityLevel.UserDirected), Sims3.Gameplay.Autonomy.AutonomySearchType.None,
                            InteractionFlags.UserDirected, HitFor(t, new Args(new Dictionary<string, object>())), null, false);
                        InteractionInstance inst = e.Iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
                        if (inst == null || inst is IImmediateInteraction) continue;
                        if (s.InteractionQueue.Add(inst)) return e.Label + " @ " + t.GetType().Name;
                    }
            }
            return null;
        }
    }
}
