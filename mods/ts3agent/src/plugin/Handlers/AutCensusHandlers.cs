using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // docs/546 row 2 (read only): which interactions autonomy tests, how often they score 0, and the time they take.
    // Hook: InteractionObjectPair.TestAndCalculateScore(Autonomy, AutonomySearchType, InteractionFlags), the call every
    // autonomous candidate goes through: the definition's Test, then the score; 0 when the test fails [verified,
    // decompiled]. Per row (definition type @ target type): calls, calls that returned <= 0 ("zero": a failed test or a
    // worthless score), wall time inside the call, and why_sampled: for every 32nd zero call of a row the definition's
    // Test is run once more and its InteractionTestResult tallied ("Pass" = test passed, score was 0).
    // Installed only on request ({"install":true}, sent by the harness at the main menu): the swap has to happen before
    // autonomy compiles the method (refused after), and ts3fixes' AutShadow treats a foreign swap of this method as a
    // reason to keep townie scoring on the sim thread (AutShadow.cs AllL3Ok), so a census run measures every call on
    // the sim thread and its frame times are not comparable to a normal run. Without the request nothing is swapped.
    // Clock: DateTime.UtcNow (the game's Mono has no Stopwatch); single calls are below its resolution, so per-row
    // time is a sampled sum, good for ranking rows over minutes, not for one call.
    //   aut_census {"install":true}   swap the method (main menu only)
    //   aut_census {}                 state and totals
    //   aut_census {"start":true}     count from now (also clears)
    //   aut_census {"stop":true}
    //   aut_census {"top":30, "sort":"ms"|"calls"|"zero"|"max"}   rows (max: longest single call, with who and when)
    public static partial class Handlers
    {
        const string kAcKey = "ts3agent.autcensus";

        public class AcRow { public long Calls, Zero, Ticks, HhCalls, HhTicks, MaxTicks; public string MaxWho; public Dictionary<string, int> Why = new Dictionary<string, int>(); }

        static void RegisterAutCensus()
        {
            Dispatcher.Register("aut_census", AutCensus);
            Dispatcher.Register("object_types", ObjectTypes);
        }

        // object_types {"match":"FirePit"}: every GameObject whose type name contains match (default: all, top 40 types),
        // counted by type and by in world / out of world (InWorld false, e.g. hidden helper objects). Read only.
        static object ObjectTypes(Args a)
        {
            string match = a.Str("match", "");
            Dictionary<string, int[]> by = new Dictionary<string, int[]>();
            int total = 0;
            foreach (Sims3.Gameplay.Abstracts.GameObject o in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Abstracts.GameObject>())
            {
                if (o == null) continue;
                string n = o.GetType().FullName;
                if (match.Length > 0 && n.IndexOf(match) < 0) continue;
                int[] c;
                if (!by.TryGetValue(n, out c)) { c = new int[2]; by[n] = c; }
                bool inWorld = false;
                try { inWorld = o.InWorld; } catch (Exception) { }
                c[inWorld ? 0 : 1]++; total++;
            }
            List<KeyValuePair<string, int[]>> l = new List<KeyValuePair<string, int[]>>(by);
            l.Sort(delegate(KeyValuePair<string, int[]> x, KeyValuePair<string, int[]> y) { return (y.Value[0] + y.Value[1]).CompareTo(x.Value[0] + x.Value[1]); });
            Dictionary<string, object> r = Util.Obj();
            r["total"] = total;
            List<object> rows = new List<object>();
            for (int i = 0; i < l.Count && i < (match.Length > 0 ? 200 : 40); i++)
            {
                Dictionary<string, object> d = Util.Obj();
                d["type"] = l[i].Key; d["in_world"] = l[i].Value[0]; d["out_of_world"] = l[i].Value[1];
                rows.Add(d);
            }
            r["types"] = rows;
            return r;
        }

        static Hashtable AcState()
        {
            Hashtable h = AppDomain.CurrentDomain.GetData(kAcKey) as Hashtable;
            if (h == null) { h = new Hashtable(); AppDomain.CurrentDomain.SetData(kAcKey, h); }
            return h;
        }

        static void AcInstall()
        {
            Hashtable st = AcState();
            if (st.ContainsKey("installed")) return;  // once per game (a hot reload keeps the first hook)
            st.Remove("refused");
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            try
            {
                MethodInfo t = typeof(InteractionObjectPair).GetMethod("TestAndCalculateScore", all, null,
                    new Type[] { typeof(Autonomy), typeof(AutonomySearchType), typeof(InteractionFlags) }, null);
                MethodInfo r = typeof(AcHook).GetMethod("ScoreHook", all), s = typeof(AcHook).GetMethod("ScoreOrig", all);
                if (t == null || r == null || s == null) { st["refused"] = "method not found"; return; }
                if (Marshal.ReadInt32(new IntPtr(t.MethodHandle.Value.ToInt32() + 0x14)) != 0) { st["refused"] = "already compiled (install at startup only)"; return; }
                if ((t.Attributes & MethodAttributes.Virtual) != 0) { st["refused"] = "virtual (not handled)"; return; }
                SwapMethod(s, t);   // the stub now runs the original body
                IntPtr m = s.MethodHandle.Value;  // a stub is only called directly (ts3fixes StubDirect)
                Marshal.WriteInt16(m, (short)(Marshal.ReadInt16(m) & ~(int)MethodAttributes.Virtual));
                IntPtr slot = new IntPtr(m.ToInt32() + 0x1C);
                Marshal.WriteInt32(slot, Marshal.ReadInt32(slot) | ~0xFFF);
                SwapMethod(t, r);   // TestAndCalculateScore now runs the hook
                st["installed"] = true;
                st["rows"] = new Dictionary<string, AcRow>();
                AcHook.sRows = (Dictionary<string, AcRow>)st["rows"];
            }
            catch (Exception e) { st["refused"] = "error: " + e.Message; }
        }

        public class AcHook
        {
            internal static Dictionary<string, AcRow> sRows;
            internal static bool sOn;
            internal static long sCalls, sZero, sTicks, sErrors, sHhCalls, sHhTicks;

            public float ScoreHook(Autonomy autonomy, AutonomySearchType searchType, InteractionFlags flags)
            {
                if (!sOn) return ScoreOrig(autonomy, searchType, flags);
                long t0 = DateTime.UtcNow.Ticks;
                float score = ScoreOrig(autonomy, searchType, flags);
                long dt = DateTime.UtcNow.Ticks - t0;
                try
                {
                    InteractionObjectPair iop = (InteractionObjectPair)(object)this;
                    string def = iop.InteractionDefinition != null ? iop.InteractionDefinition.GetType().FullName : "null";
                    string tgt = iop.Target != null ? iop.Target.GetType().Name : "null";
                    Sims3.Gameplay.Actors.Sim actor = autonomy.Actor;
                    string key = def + " @ " + tgt + (actor != null && actor.SimDescription != null && !actor.SimDescription.IsHuman ? " [" + actor.SimDescription.Species + "]" : "");
                    bool hh = actor == null || actor.IsSelectable || actor.Household == null || actor.Household == Sims3.Gameplay.CAS.Household.ActiveHousehold;  // unknown scope counts as household
                    AcRow row;
                    lock (sRows)
                    {
                        if (!sRows.TryGetValue(key, out row)) { row = new AcRow(); sRows[key] = row; }
                        row.Calls++; row.Ticks += dt; if (score <= 0f) row.Zero++;
                        if (dt > row.MaxTicks) { row.MaxTicks = dt; row.MaxWho = (hh ? "hh " : "townie ") + (actor != null ? actor.FullName : "?") + " @ sim " + Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString(); }
                        if (hh) { row.HhCalls++; row.HhTicks += dt; sHhCalls++; sHhTicks += dt; }
                        sCalls++; sTicks += dt; if (score <= 0f) sZero++;
                    }
                    // every 32nd zero of a row: re-run the definition's Test once to learn which check said no
                    // (InteractionTestResult name; "Pass" = the test passed and the score was 0). Census runs only.
                    if (score <= 0f && (row.Zero & 31) == 1)
                    {
                        if (autonomy.OverrideInteractionParameters.HasValue) flags |= autonomy.OverrideInteractionParameters.Value.Flags;
                        InteractionInstanceParameters p = new InteractionInstanceParameters(iop, autonomy.Actor, new InteractionPriority(InteractionPriorityLevel.Autonomous), searchType, flags);
                        GreyedOutTooltipCallback cb = null;
                        string why = iop.InteractionDefinition.Test(ref p, ref cb).ToString();
                        lock (sRows) { int n; row.Why.TryGetValue(why, out n); row.Why[why] = n + 1; }
                    }
                }
                catch (Exception) { sErrors++; }
                return score;
            }
            public float ScoreOrig(Autonomy autonomy, AutonomySearchType searchType, InteractionFlags flags) { return 0f; } // receives the original
        }

        static object AutCensus(Args a)
        {
            Hashtable st = AcState();
            if (a.Bool("install", false)) AcInstall();
            Dictionary<string, object> r = Util.Obj();
            r["installed"] = st.ContainsKey("installed");
            if (st.ContainsKey("refused")) r["refused"] = st["refused"];
            if (!st.ContainsKey("installed")) return r;
            Dictionary<string, AcRow> rows = (Dictionary<string, AcRow>)st["rows"];
            if (a.Bool("start", false))
            {
                lock (rows) { rows.Clear(); AcHook.sCalls = AcHook.sZero = AcHook.sTicks = AcHook.sErrors = AcHook.sHhCalls = AcHook.sHhTicks = 0; }
                AcHook.sOn = true; st["started"] = System.Environment.TickCount;
            }
            if (a.Bool("stop", false)) AcHook.sOn = false;
            const double f = TimeSpan.TicksPerMillisecond;
            r["on"] = AcHook.sOn;
            r["seconds"] = st.ContainsKey("started") ? (System.Environment.TickCount - (int)st["started"]) / 1000 : 0;
            r["calls"] = AcHook.sCalls; r["zero"] = AcHook.sZero; r["ms"] = Math.Round(AcHook.sTicks / f, 1); r["errors"] = AcHook.sErrors;
            r["hh_calls"] = AcHook.sHhCalls; r["hh_ms"] = Math.Round(AcHook.sHhTicks / f, 1);
            r["rows"] = rows.Count;
            if (a.Has("top"))
            {
                int top = (int)a.Long("top", 30);
                string sort = a.Str("sort", "ms");
                List<KeyValuePair<string, AcRow>> l;
                lock (rows) l = new List<KeyValuePair<string, AcRow>>(rows);
                l.Sort(delegate(KeyValuePair<string, AcRow> x, KeyValuePair<string, AcRow> y)
                {
                    long vx = sort == "calls" ? x.Value.Calls : sort == "zero" ? x.Value.Zero : sort == "max" ? x.Value.MaxTicks : x.Value.Ticks;
                    long vy = sort == "calls" ? y.Value.Calls : sort == "zero" ? y.Value.Zero : sort == "max" ? y.Value.MaxTicks : y.Value.Ticks;
                    return vy.CompareTo(vx);
                });
                List<object> o = new List<object>();
                for (int i = 0; i < l.Count && i < top; i++)
                {
                    AcRow v = l[i].Value;
                    Dictionary<string, object> d = Util.Obj();
                    d["key"] = l[i].Key; d["calls"] = v.Calls; d["zero"] = v.Zero;
                    Dictionary<string, object> w = Util.Obj();
                    lock (rows) foreach (KeyValuePair<string, int> kv in v.Why) w[kv.Key] = kv.Value;
                    d["why_sampled"] = w;
                    d["max_ms"] = Math.Round(v.MaxTicks / f, 1); d["max_who"] = v.MaxWho;
                    d["hh_calls"] = v.HhCalls; d["hh_ms"] = Math.Round(v.HhTicks / f, 1);
                    d["ms"] = Math.Round(v.Ticks / f, 1); d["us_per_call"] = v.Calls > 0 ? Math.Round(v.Ticks / f * 1000.0 / v.Calls, 1) : 0;
                    o.Add(d);
                }
                r["top"] = o;
            }
            return r;
        }
    }
}
