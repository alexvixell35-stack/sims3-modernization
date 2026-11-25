using System;
using System.Collections;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // hitch_watch (read only; docs/546 §6, docs/247 §14): a sim-thread task that stamps every tick. A gap of at least
    // min_ms between two of its ticks is one sim iteration that held the thread (a hitch). For each hitch it keeps what
    // the household Sims and the Sims on the active lot were doing on the tick before ("before") and on the tick after
    // ("after"): current interaction, the interaction it waits on for a transition, queue length, routing, posture. A Sim
    // whose state is the same before and after, mid-interaction, is the likely holder; a Sim that started something new is
    // what the hitch led into. Native waits (route job lock, steering) are not visible here: pair with ts3native
    // [HitchAttr] Holders=1 in the same run.
    // Per hitch also (docs/247 §14): wall_ms = System.Environment.TickCount at the end of the gap, the clock of ts3native's
    // "at N ms" stamps; tick = SimClock.CurrentTicks, the "tick N" of ufplan lines; gc = Mono collections inside the gap;
    // agent = agent commands and RunAsync work inside the gap with their ms (harness time, not game time: social_round,
    // mem_gc, leak_census ...). over_200_game / over_500_game leave out hitches that are half or more agent work. Hitches >= log_ms also go to
    // ts3native.log as one "hitchwatch:" line (at most kHwLogMax a session: they share ts3native's 400-line cap).
    //   hitch_watch {"start":true, "min_ms":100, "log_ms":300}   clears, starts (log_ms 0 = no log lines)
    //   hitch_watch {}                                           state: ticks, hitches, longest
    //   hitch_watch {"top":30}                                   longest hitches with before/after
    //   hitch_watch {"stop":true}
    public static partial class Handlers
    {
        const int kHwLogMax = 80;

        public class HwSnap { public string Name; public bool Hh; public string Cur, Trans, Posture; public int Queue; public bool Routing; }
        public class HwHitch { public double Ms; public string When, Agent; public long SimTicks; public int WallMs, Gc, AgentMs; public List<HwSnap> Before, After; }

        static void RegisterHitchWatch()
        {
            Dispatcher.Register("hitch_watch", HitchWatch);
            Dispatcher.Register("tick_stats", TickStats);
        }

        // tick_stats (docs/599, read only): the sim-iteration interval as hitch_watch sees it (one stamp per tick of a task
        // that sleeps 0, so the gap is one full scheduler pass), but every tick, not only gaps >= min_ms: count, average, p50,
        // p95, p99, max, ticks >= 250 ms / >= 1 s (over_250, over_1000) and ticks per wall second over a window. Histogram of 0.1 ms bins to 2 s (above: one overflow bin,
        // counted at its real value in avg and max). Frame (render-thread) time is not visible from script: frame-times.js.
        //   tick_stats {"start":true}   clears and starts      tick_stats {}   stats since start or the last reset
        //   tick_stats {"reset":true}   stats, then clears (one window per call)   tick_stats {"stop":true}
        public class TsTask : Task
        {
            internal static TsTask sCur;
            internal bool Running = true;
            const int kBins = 20000;  // 0.1 ms each
            readonly int[] mBins = new int[kBins + 1];
            long mN, mLast, mWin0;
            double mSum, mMax;
            long mSim0;

            public override void Simulate()
            {
                while (Running)
                {
                    long now = DateTime.UtcNow.Ticks;
                    lock (mBins)
                    {
                        if (mLast != 0)
                        {
                            double ms = (now - mLast) / (double)TimeSpan.TicksPerMillisecond;
                            int b = (int)(ms * 10);
                            mBins[b < 0 ? 0 : b > kBins ? kBins : b]++;
                            mN++; mSum += ms; if (ms > mMax) mMax = ms;
                        }
                        else { mWin0 = now; mSim0 = Sims3.Gameplay.Utilities.SimClock.CurrentTicks; }
                        mLast = now;
                    }
                    Simulator.Sleep(0);
                }
                Simulator.DestroyObject(ObjectId);
            }

            double Pct(double q)
            {
                long want = (long)Math.Ceiling(q * mN), acc = 0;
                for (int i = 0; i <= kBins; i++) { acc += mBins[i]; if (acc >= want) return i >= kBins ? mMax : (i + 0.5) / 10.0; }
                return mMax;
            }

            long Over(int bin) { long n = 0; for (int i = bin; i <= kBins; i++) n += mBins[i]; return n; }

            internal Dictionary<string, object> Stats(bool reset)
            {
                Dictionary<string, object> r = Util.Obj();
                lock (mBins)
                {
                    double wallS = mLast > mWin0 ? (mLast - mWin0) / (double)TimeSpan.TicksPerSecond : 0;
                    r["ticks"] = mN;
                    r["wall_s"] = Math.Round(wallS, 1);
                    r["ticks_per_s"] = wallS > 0 ? Math.Round(mN / wallS, 2) : 0;
                    r["avg_ms"] = mN > 0 ? Math.Round(mSum / mN, 2) : 0;
                    r["p50_ms"] = mN > 0 ? Pct(0.50) : 0;
                    r["p95_ms"] = mN > 0 ? Pct(0.95) : 0;
                    r["p99_ms"] = mN > 0 ? Pct(0.99) : 0;
                    r["max_ms"] = Math.Round(mMax, 1);
                    r["over_250"] = Over(2500);   // ticks >= 250 ms / >= 1 s (kit benchmark hitch counts, docs/315)
                    r["over_1000"] = Over(10000);
                    try { r["sim_minutes"] = Math.Round(Sims3.Gameplay.Utilities.SimClock.ConvertFromTicks(Sims3.Gameplay.Utilities.SimClock.CurrentTicks - mSim0, Sims3.Gameplay.Utilities.TimeUnit.Minutes), 1); } catch (Exception) { }
                    if (reset)
                    {
                        Array.Clear(mBins, 0, mBins.Length);
                        mN = 0; mSum = 0; mMax = 0; mWin0 = mLast;
                        try { mSim0 = Sims3.Gameplay.Utilities.SimClock.CurrentTicks; } catch (Exception) { }
                    }
                }
                return r;
            }
        }

        static object TickStats(Args a)
        {
            TsTask t = TsTask.sCur;
            if (a.Bool("stop", false) && t != null) { t.Running = false; TsTask.sCur = null; Dictionary<string, object> s = t.Stats(false); s["running"] = false; return s; }
            if (a.Bool("start", false))
            {
                if (t != null) t.Running = false;
                t = new TsTask();
                TsTask.sCur = t;
                Simulator.AddObject(t);
            }
            if (t == null) { Dictionary<string, object> n = Util.Obj(); n["running"] = false; return n; }
            Dictionary<string, object> r = t.Stats(a.Bool("reset", false));
            r["running"] = t.Running;
            return r;
        }

        // Agent time on the sim thread. Mailbox.Poll runs each command inside one sim tick, so a command's span is exact;
        // RunAsync work can sleep across ticks, so its span covers every gap it was alive in (an upper bound).
        internal static class HwAgent
        {
            struct Span { public string Name; public int T0, T1; }
            static readonly List<Span> sDone = new List<Span>();             // last 64 finished spans
            static readonly Dictionary<int, Span> sLive = new Dictionary<int, Span>();  // running RunAsync work by id
            static int sNextId;
            static bool sWrapped;

            internal static int Begin(string name)
            {
                Span s = new Span(); s.Name = name; s.T0 = System.Environment.TickCount;
                lock (sLive) { int id = ++sNextId; sLive[id] = s; return id; }
            }

            internal static void End(int id)
            {
                lock (sLive)
                {
                    Span s;
                    if (!sLive.TryGetValue(id, out s)) return;
                    sLive.Remove(id);
                    s.T1 = System.Environment.TickCount;
                    sDone.Add(s);
                    if (sDone.Count > 64) sDone.RemoveAt(0);
                }
            }

            // spans that overlap [t0, t1] (TickCount ms, wrap-safe) as "name ms ..." (null = none); most = the longest overlap
            internal static string In(int t0, int t1, out int most)
            {
                List<string> o = new List<string>();
                most = 0;
                lock (sLive)
                {
                    foreach (Span s in sDone) Add(o, s.Name, s.T0, s.T1, t0, t1, ref most);
                    foreach (Span s in sLive.Values)  // a task killed mid-work (world quit) never ends: 10 min at most
                        if (unchecked(t1 - s.T0) < 600000) Add(o, s.Name, s.T0, t1, t0, t1, ref most);
                }
                return o.Count > 0 ? string.Join(", ", o.ToArray()) : null;
            }

            static void Add(List<string> o, string name, int s0, int s1, int t0, int t1, ref int most)
            {
                int a = unchecked(s0 - t0) > 0 ? s0 : t0, b = unchecked(s1 - t1) < 0 ? s1 : t1;
                int ms = unchecked(b - a);
                if (ms < 0) return;
                o.Add(name + " " + ms);
                if (ms > most) most = ms;
            }

            internal static void Clear() { lock (sLive) { sLive.Clear(); sDone.Clear(); } }

            // Wraps every registered command once, so each mailbox command is timed (plugin only: the loader's
            // Dispatcher stays as it is, and a hot-reloaded plugin registers fresh handlers and wraps them again).
            internal static void WrapAll()
            {
                if (sWrapped) return;
                Dictionary<string, Handler> map = typeof(Dispatcher).GetField("sHandlers",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null) as Dictionary<string, Handler>;
                if (map == null) return;
                foreach (string name in new List<string>(map.Keys)) map[name] = Wrap(name, map[name]);
                sWrapped = true;
            }

            static Handler Wrap(string name, Handler h)
            {
                return delegate(Args a)
                {
                    int id = Begin(name);
                    try { return h(a); }
                    finally { End(id); }
                };
            }
        }

        public class HwTask : Task
        {
            internal static HwTask sCur;
            internal bool Running = true;
            internal long MinTicks = 100 * TimeSpan.TicksPerMillisecond;
            internal double LogMs = 300;
            internal long TickCount, Last;
            internal double MaxMs;
            internal readonly List<HwHitch> Hitches = new List<HwHitch>();
            List<HwSnap> mPrev;
            int mLastWall, mLastGc, mLogged;

            public override void Simulate()
            {
                while (Running)
                {
                    long now = DateTime.UtcNow.Ticks;
                    int wall = System.Environment.TickCount, gc = GC.CollectionCount(0);
                    List<HwSnap> snap = null;
                    try { snap = Snapshot(); } catch (Exception) { }
                    if (Last != 0)
                    {
                        long gap = now - Last;
                        double ms = gap / (double)TimeSpan.TicksPerMillisecond;
                        if (ms > MaxMs) MaxMs = ms;
                        if (gap >= MinTicks) Keep(ms, wall, gc, snap);
                    }
                    mPrev = snap;
                    Last = DateTime.UtcNow.Ticks;  // the snapshot itself is not part of the next gap
                    mLastWall = System.Environment.TickCount;
                    mLastGc = GC.CollectionCount(0);
                    TickCount++;
                    Simulator.Sleep(0);
                }
                Simulator.DestroyObject(ObjectId);
            }

            void Keep(double ms, int wall, int gc, List<HwSnap> snap)
            {
                HwHitch h = new HwHitch();
                h.Ms = Math.Round(ms, 1);
                try { h.When = Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString(); } catch (Exception) { h.When = "?"; }
                h.SimTicks = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                h.WallMs = wall;
                h.Gc = gc - mLastGc;
                h.Agent = HwAgent.In(mLastWall, wall, out h.AgentMs);
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
                if (LogMs > 0 && ms >= LogMs && mLogged < kHwLogMax)
                {
                    mLogged++;
                    DiagLine("hitchwatch: " + Math.Round(ms) + " ms, tick " + h.SimTicks + ", " + h.When + ", gc +" + h.Gc
                        + ", agent " + (h.Agent ?? "-") + ", at " + (uint)wall + " ms" + (mLogged == kHwLogMax ? " (last line)" : ""));
                }
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
                HwAgent.Clear();
                HwAgent.WrapAll();
                t = new HwTask();
                t.MinTicks = (long)(a.Long("min_ms", 100) * TimeSpan.TicksPerMillisecond);
                t.LogMs = a.Long("log_ms", 300);
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
            int n200 = 0, n500 = 0, g200 = 0, g500 = 0;
            foreach (HwHitch h in hs)
            {
                bool game = h.AgentMs * 2 < h.Ms;  // agent work under half the gap: counted as game time
                if (h.Ms >= 200) { n200++; if (game) g200++; }
                if (h.Ms >= 500) { n500++; if (game) g500++; }
            }
            r["over_200"] = n200; r["over_500"] = n500;
            r["over_200_game"] = g200; r["over_500_game"] = g500;  // without hitches that are half or more agent work
            if (a.Has("top"))
            {
                hs.Sort(delegate(HwHitch x, HwHitch y) { return y.Ms.CompareTo(x.Ms); });
                List<object> o = new List<object>();
                for (int i = 0; i < hs.Count && i < (int)a.Long("top", 30); i++)
                {
                    HwHitch h = hs[i];
                    Dictionary<string, object> d = Util.Obj();
                    d["ms"] = h.Ms; d["sim_time"] = h.When; d["tick"] = h.SimTicks; d["wall_ms"] = (uint)h.WallMs; d["gc"] = h.Gc;
                    if (h.Agent != null) d["agent"] = h.Agent;
                    d["before"] = HwSnapList(h.Before); d["after"] = HwSnapList(h.After);
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
