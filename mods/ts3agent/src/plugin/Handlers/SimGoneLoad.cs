using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Routing;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // sim_gone_load {arm?} (queues/sim-gone6.json, docs: PLAN "Sim lost mid-visit"): what happens to the active household between
    // the load and 60 s after it. Harness only, off until armed; queue job key "agentArm" arms it from cycle.ts before load_save.
    // Read-only: no patch on purpose. A swap before the load changes the method's IL checksum, which is suspect #1 here (saved task
    // frames are checked against it, ScriptCore TaskStateCollection.Read), and it would also change the noours arm.
    // Without a patch Kill and Reset have no stack: "actionend" gives the action's kill type and failure flags, "interaction" the
    // Cancelled flag, "exit" (Sim.OnExitReasonsAdded) the managed stack of whoever added the exit reason, "stackfail"
    // (LoadSaveManager.ObjectStackLoadFailed, returns false so the game's reset still runs) the state just before the load reset.
    // Our hooks: ts3fixes counter deltas over the window (whole game, not per Sim) plus the Sim's failed-plan count.
    // Cost: per tick one reference compare per household Sim, strings only on a change; one method-table walk at load.
    public static partial class Handlers
    {
        class SlSim
        {
            public Sim S;
            public InteractionInstance Cur;
            public RouteAction Head, Prev;
            public Sim.ExitReasonsAddedCallback Exit;
            public RoutingComponent.RouteActionsFinishedDelegate Done;
        }

        const int kSlMs = 60000, kSlEvents = 400, kSlStacks = 24, kSlFrames = 14, kSlHex = 64;
        const BindingFlags kSlAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        static bool sSlArmed, sSlDone;
        static int sSlT0, sSlDropped, sSlStacksUsed;
        static string sSlCaptureMiss = "no load event yet";
        static readonly List<object> sSlLog = new List<object>();
        static readonly List<SlSim> sSlSims = new List<SlSim>();
        static readonly Dictionary<string, object> sSlFailed = new Dictionary<string, object>();
        static readonly Dictionary<string, object> sSlFrames = new Dictionary<string, object>();
        static Dictionary<string, object> sSlMethods, sSlChecksums;
        static int[] sSlCounters0;
        static List<object> sSlHooks;
        static Dictionary<uint, string> sSlHookSums;
        static MethodInfo sSlDiag;

        static object SimGoneLoad(Args a)
        {
            if (a.Bool("arm", false) && !sSlArmed)
            {
                sSlArmed = true;
                sSlChecksums = SlChecksums();
                sSlHookSums = SlHookSums();
                LoadSaveManager.ObjectStackLoadFailed += OnSlStackFailed;
                LoadSaveManager.ObjectGroupsPreLoad += delegate { SlCapture("preload"); };
                LoadSaveManager.ObjectGroupLoaded += delegate { SlCapture("grouploaded"); };
                LoadSaveManager.ObjectGroupsPostLoad += delegate { SlCapture("postload"); };
                World.OnWorldLoadFinishedEventHandler += OnSlWorldLoaded;
            }
            Dictionary<string, object> r = Util.Obj();
            r["sim_gone_load_armed"] = sSlArmed;
            r["sim_gone_load_done"] = sSlDone;
            r["sim_gone_load_ms"] = sSlT0 != 0 ? System.Environment.TickCount - sSlT0 : -1;
            r["sim_gone_load_failed_types"] = sSlFailed;
            r["sim_gone_load_methods"] = sSlMethods;
            r["sim_gone_load_capture_miss"] = sSlMethods == null ? sSlCaptureMiss : null;
            r["sim_gone_load_checksums"] = sSlChecksums;
            r["sim_gone_load_hooks"] = sSlHooks;
            r["sim_gone_load_hook_sums"] = sSlHookSums != null ? sSlHookSums.Count : -1;
            r["sim_gone_load_saved_frames"] = sSlFrames;
            r["sim_gone_load_dropped"] = sSlDropped;
            r["sim_gone_load_events"] = new List<object>(sSlLog); // last: the log line is cut at the step's "max"
            return r;
        }

        static void SlLog(string sim, string kind, string what, List<object> stack)
        {
            if (sSlLog.Count >= kSlEvents) { sSlDropped++; return; }
            Dictionary<string, object> r = Util.Obj();
            r["ms"] = sSlT0 != 0 ? System.Environment.TickCount - sSlT0 : -1;
            r["sim"] = sim;
            r["kind"] = kind;
            r["what"] = what;
            if (stack != null) r["stack"] = stack;
            sSlLog.Add(r);
        }

        static List<object> SlStack()
        {
            if (sSlStacksUsed >= kSlStacks) return null;
            sSlStacksUsed++;
            List<object> s = SgStack();
            if (s.Count > kSlFrames) s.RemoveRange(kSlFrames, s.Count - kSlFrames);
            return s;
        }

        static string SlType(object o) { return o != null ? o.GetType().Name : "null"; }

        // never throws, returns false: the game's own reset must still run
        static bool OnSlStackFailed(IScriptProxy p)
        {
            try
            {
                SlCapture("stackfail");
                object t = p != null ? p.Target : null;
                string n = t != null ? t.GetType().FullName : "null";
                sSlFailed[n] = sSlFailed.ContainsKey(n) ? (int)sSlFailed[n] + 1 : 1;
                Sim s = t as Sim;
                SimDescription d = s != null ? s.SimDescription : null;
                Household ah = Household.ActiveHousehold;
                if (s != null && (ah == null || d == null || d.Household == ah))
                    SlLog(d != null ? d.FullName : "(no description)", "stackfail", SlType(s.InteractionQueue != null ? s.CurrentInteraction : null) + " " + SgActions(s), SlStack());
            }
            catch (Exception x) { SlLog(null, "stackfail", "record failed: " + x.GetType().Name + " " + x.Message, null); }
            return false;
        }

        static void OnSlWorldLoaded(object sender, EventArgs e)
        {
            if (sSlT0 != 0) return;
            sSlT0 = System.Environment.TickCount;
            sSlCounters0 = (AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[]);
            if (sSlCounters0 != null) sSlCounters0 = (int[])sSlCounters0.Clone();
            Simulator.AddObject(new SlTask());
        }

        class SlTask : Task
        {
            public override void Simulate()
            {
                while (System.Environment.TickCount - sSlT0 < kSlMs)
                {
                    try { SlTick(); }
                    catch (Exception x) { SlLog(null, "tick", "failed: " + x.GetType().Name + " " + x.Message, null); }
                    Simulator.Sleep(0);
                }
                try { SlFinish(); }
                catch (Exception x) { SlLog(null, "finish", "failed: " + x.GetType().Name + " " + x.Message, null); }
                sSlDone = true;
                Simulator.DestroyObject(ObjectId);
            }
        }

        static void SlTick()
        {
            Household h = Household.ActiveHousehold;
            if (h == null) return;
            List<Sim> sims = h.Sims;
            for (int i = 0; i < sims.Count; i++)
            {
                Sim s = sims[i];
                if (s == null || s.HasBeenDestroyed) continue;
                SlSim e = null;
                foreach (SlSim k in sSlSims) if (k.S == s) { e = k; break; }
                if (e == null) e = SlAdd(s);
                SimRoutingComponent rc = s.SimRoutingComponent;
                if (rc == null) continue;
                InteractionInstance c = s.InteractionQueue != null ? s.CurrentInteraction : null;
                RouteAction head = rc.GetCurrentRouteAction(), prev = rc.GetPreviousRouteAction();
                if (c != e.Cur)
                {
                    SlLog(s.FullName, "interaction", SlType(e.Cur) + (e.Cur != null && e.Cur.Cancelled ? " cancelled" : "") + " -> " + SlType(c), null);
                    e.Cur = c;
                }
                if (head == e.Head && prev == e.Prev) continue;
                FollowRouteAction old = e.Head as FollowRouteAction;
                if (head != e.Head && old != null)
                    SlLog(s.FullName, "actionend", old.GetType().Name + " kill=" + old.RouteKillType + " fail=" + old.FailureExplanations, null);
                e.Head = head;
                e.Prev = prev;
                SlLog(s.FullName, "chain", SgActions(s) + " left=" + rc.GetDistanceRemainingOnRoute().ToString("F1"), null);
            }
        }

        static SlSim SlAdd(Sim s)
        {
            SlSim e = new SlSim();
            e.S = s;
            sSlSims.Add(e);
            SlLog(s.FullName, "first", "seen", null);
            sSlFrames[s.FullName] = SlSavedFrames(s);
            e.Exit = delegate(ExitReason why) { try { SlLog(e.S.FullName, "exit", why.ToString() + " on " + SlType(e.S.CurrentInteraction), SlStack()); } catch (Exception) { } };
            s.OnExitReasonsAdded += e.Exit;
            if (s.SimRoutingComponent != null)
            {
                e.Done = delegate(GameObject o, bool ok) { try { SlLog(e.S.FullName, "routeend", ok ? "success" : "failed", null); } catch (Exception) { } };
                s.SimRoutingComponent.OnRouteActionsFinished += e.Done;
            }
            return e;
        }

        static void SlFinish()
        {
            foreach (SlSim e in sSlSims)
            {
                if (e.S.HasBeenDestroyed) continue;
                e.S.OnExitReasonsAdded -= e.Exit;
                if (e.Done != null && e.S.SimRoutingComponent != null) e.S.SimRoutingComponent.OnRouteActionsFinished -= e.Done;
                SlLog(e.S.FullName, "routefails", SgRouteFails(e.S.ObjectId.Value).ToString(), null);
            }
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            if (c == null || sSlCounters0 == null) return;
            sSlHooks = new List<object>();
            for (int i = 0; i < c.Length && i < sSlCounters0.Length && sSlHooks.Count < 200; i++)
                if (c[i] != sSlCounters0[i]) sSlHooks.Add(i + ":+" + (c[i] - sSlCounters0[i]));
        }

        // The Sim's task as the load restored it, before its first tick: "Type.Method CHECKSUM". Empty when the stack was refused.
        static List<object> SlSavedFrames(Sim s)
        {
            List<object> o = new List<object>();
            try
            {
                MethodInfo get = typeof(ScriptCore.ScriptProxy).Assembly.GetType("ScriptCore.TaskControl").GetMethod("GetTaskContext", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object[] a = { s.ObjectId.Value, true, null };
                if (!(bool)get.Invoke(null, a)) return o;
                ScriptCore.TaskContext c = (ScriptCore.TaskContext)a[2];
                if (c.mFrames == null) return o;
                foreach (ScriptCore.TaskFrame fr in c.mFrames)
                {
                    MethodBase m = MethodBase.GetMethodFromHandle(fr.mMethodHandle);
                    uint sum;
                    o.Add(m.DeclaringType.Name + "." + m.Name + " " + (SlChecksum(fr.mMethodHandle, out sum) ? sum.ToString("X8") : "none"));
                }
            }
            catch (Exception x) { o.Add("failed: " + x.GetType().Name + " " + x.Message); }
            return o;
        }

        static bool SlChecksum(RuntimeMethodHandle h, out uint sum)
        {
            sum = 0;
            MethodInfo m = typeof(ScriptCore.ScriptProxy).Assembly.GetType("ScriptCore.TaskControl").GetMethod("GetMethodChecksum", BindingFlags.Static | BindingFlags.Public);
            object[] a = { h, 0u };
            if (!(bool)m.Invoke(null, a)) return false;
            sum = (uint)a[1];
            return true;
        }

        // The checksum the game compares saved frames with, for the suspects: differs between the arms when the swap changes it.
        static Dictionary<string, object> SlChecksums()
        {
            Dictionary<string, object> r = Util.Obj();
            Type[] types = { typeof(SimRoutingComponent), typeof(PortalAnimateThroughAction), typeof(FollowSubPathRouteAction) };
            string[] names = { "DoRoute", "DoPortal", "PerformAction" };
            for (int i = 0; i < types.Length; i++)
                foreach (MethodInfo m in types[i].GetMethods(kSlAll))
                {
                    if (m.Name != names[i]) continue;
                    uint sum;
                    try { r[types[i].Name + "." + m.Name] = SlChecksum(m.MethodHandle, out sum) ? sum.ToString("X8") : "none"; }
                    catch (Exception x) { r[types[i].Name + "." + m.Name] = "failed: " + x.GetType().Name; }
                }
            return r;
        }

        // Which saved methods the load refused (checksum differs, or the method is gone). A dropped frame invalidates its whole task.
        // The task table lives only inside the load; the game fires no event at a known point there, so every chance is taken.
        static void SlCapture(string when)
        {
            if (sSlMethods != null) return;
            SlTrace("capture " + when);
            try
            {
                const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic, inst = BindingFlags.Instance | BindingFlags.NonPublic;
                Assembly core = typeof(ScriptCore.ScriptProxy).Assembly;
                Type lsm = core.GetType("ScriptCore.LoadSaveManager"), tsc = core.GetType("ScriptCore.TaskStateCollection");
                object ts = lsm.GetField("sTaskStates", st).GetValue(null);
                if (ts == null)
                {
                    object rd = lsm.GetField("mObjectGroupReader", st).GetValue(null);
                    if (rd != null) ts = rd.GetType().GetMethod("GetObjectByType", new Type[] { typeof(Type) }).Invoke(rd, new object[] { tsc });
                }
                if (ts == null) { sSlCaptureMiss = when + ": no task table"; return; }
                IList list = tsc.GetField("mMethodList", inst).GetValue(ts) as IList;
                Array saved = tsc.GetField("mSavedTaskContexts", inst).GetValue(ts) as Array;
                if (list == null || saved == null) { sSlCaptureMiss = when + ": task table not read yet"; return; }
                object bad = tsc.GetField("InvalidSavedTaskContext", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                int invalid = 0;
                foreach (object o in saved) if (ReferenceEquals(o, bad)) invalid++;
                List<uint> dropped = new List<uint>();
                foreach (object e in list)
                {
                    Type et = e.GetType();
                    if (((RuntimeMethodHandle)et.GetField("handle").GetValue(e)).Value == IntPtr.Zero) dropped.Add((uint)et.GetField("checksum").GetValue(e));
                }
                Dictionary<string, object> r = Util.Obj();
                r["when"] = when;
                r["methods"] = list.Count;
                r["tasks"] = saved.Length;
                r["invalidTasks"] = invalid;
                r["droppedMethods"] = dropped.Count;
                List<object> hex = new List<object>();
                for (int i = 0; i < dropped.Count && i < kSlHex; i++) hex.Add(dropped[i].ToString("X8"));
                r["droppedChecksums"] = hex;
                List<object> names = new List<object>();
                foreach (uint d in dropped) { string n; if (sSlHookSums.TryGetValue(d, out n)) names.Add(n); }
                r["droppedHooks"] = names;
                sSlMethods = r;
                SlTrace("capture " + when + " done, " + list.Count + " methods");
            }
            catch (Exception x) { sSlCaptureMiss = when + ": " + x.GetType().Name + " " + x.Message; }
        }

        // Crash breadcrumb: ts3native.log survives the game dying, the agent's own log does not. No ts3native: no line.
        static void SlTrace(string line)
        {
            if (sSlDiag == null) return;
            try { sSlDiag.Invoke(null, new object[] { "sg: " + line }); }
            catch (Exception) { sSlDiag = null; }
        }

        // A hook's stub holds the game's original header, so a stub whose checksum was dropped names the patched method.
        // Runs once at the arm (main menu): the load only looks names up. A stub took the whole header, class and vtable slot
        // included, so Type.GetMethods on its hook dies in Mono's `method->slot < nslots` assert when the target is virtual and
        // the hook's vtable is smaller (SwAbuseHook, sim-gone6 and 7). Method tokens have no such check: a stub is a method of
        // this assembly whose class is not.
        static Dictionary<uint, string> SlHookSums()
        {
            Dictionary<uint, string> o = new Dictionary<uint, string>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType("Ts3Fixes.Fixes", false);
                if (t == null) continue;
                sSlDiag = t.GetMethod("DiagLine", BindingFlags.NonPublic | BindingFlags.Static);
                int misses = 0, i = 0;
                while (misses < 64)
                {
                    if ((++i & 255) == 0) SlTrace("token " + i);
                    MethodBase m;
                    try { m = t.Module.ResolveMethod(0x06000000 | i); misses = 0; }
                    catch (Exception) { misses++; continue; }
                    if (m == null || m.DeclaringType == null || m.DeclaringType.Assembly == a || m.IsAbstract) continue;
                    string n = m.DeclaringType.Name + "." + m.Name;
                    SlTrace(n);
                    uint sum;
                    if (SlChecksum(m.MethodHandle, out sum)) o[sum] = n;
                    SlTrace("ok");
                }
                SlTrace("walk done, " + i + " tokens, " + o.Count + " stubs");
            }
            return o;
        }
    }
}
