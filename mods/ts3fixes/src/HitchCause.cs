using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Hitch-cause logging (docs/247 §9), observe only. Nothing here changes what the game does. Lines go to ts3native.log through
    // the [UserDiag] channel; tools/harness/hitch-cause-report.ts reads them.
    // Patches go in only with tuning kHitchCauseHooks (test package: build-fixes.ts --on kHitchCauseHooks), so a release package
    // has none of these swaps. Runtime switches, all off: alarmtime, boatreplanlog, owresettime.
    //
    // alarmtime: which alarm or lot update made a long Lot.Simulate pass. AlarmManager.Simulate reaches a yield (an inline alarm
    // may sleep, docs/141), so it is not swapped: its leaf AlarmManager.Timer.get_YieldRequired, which it reads once just before
    // each callback, stamps the alarm, and the next stamp closes the one before. Not PriorityQueue.RemoveAt: bp-hitch-cause timed 0, it is
    // an EA binder body (docs/130 D17, docs/220), and the void hook did not match its object return (a header swap hands callers the hook's signature).
    // AlarmWillYield-marked alarms run in an AlarmOneShot task: only their dispatch is timed here. The rest of the pass is stamped by the
    // leaves Lot.UpdateDoorPrivacy and Lot.UpdateDetailPriority (neither yields, swap-stack-audit). After them come the display
    // level and UpdateReactions, which yields: no stamp there, so rest<= is the time to the next stamp of any lot, an upper bound
    // that can hold other tasks' runs.
    // Lines: "alarmtime:" per alarm >= 20 ms, "lotpass:" per pass with alarms + door + detail >= 20 ms or world lot rest<= >= 100 ms.
    // An alarm that sleeps is closed by the next stamp of another task, ended=yield: its ms run to that stamp and the work
    // after the sleep is not in it.
    // shortcut: one open alarm at a time; if two lots ever needed overlapping timing, key this state by task.
    //
    // boatreplanlog: every Route.ReplanFromPoint with a boat as follower, stamped from the leaves RouteSrcBegin and RflPlan so no
    // hook that can sit on a saved stack changes. Line "boatreplan:": ms, caller, previous route action, Sim and boat seen or
    // unseen (UfWhy), why [UnseenFallback] did not take it, why the docs/176 job would refuse it (ts3native ArWould), route size.
    //
    // owresettime: which part of a Overwatch reset ran how long. ResetSimTask.Perform and FixInvisibleTask.InstantiateAtHome are
    // swapped with stubs (they can sleep; test package only). Line "owreset:".
    //
    // Counters: [826] alarms timed, [827] alarmtime lines, [828] lotpass lines, [829] boatreplan lines, [830] of those the
    // docs/176 job could take if it were on, [831] of those with the Sim and the boat unseen, [832] owreset lines, [833] lines
    // dropped by the cap, [834] / [835] boat replan ms seen / unseen.
    public static partial class Fixes
    {
        const uint kHcMinUs = 20000, kHcRestMinUs = 100000;
        const int kHcLinesPerMin = 120;
        static Type sHcTimer;
        static FieldInfo sHcCallBack, sHcDue;
        static bool sHcOwTried;
        static int sHcMinute = -1, sHcInMinute;

        // from ApplyTuned, so a release package patches nothing
        static void ApplyHitchCause()
        {
            if (!Instantiator.kHitchCauseHooks) return;
            sHcTimer = typeof(AlarmManager).GetNestedType("Timer", BindingFlags.NonPublic);
            sHcCallBack = sHcTimer != null ? sHcTimer.GetField("CallBack") : null;
            sHcDue = sHcTimer != null ? sHcTimer.GetField("AlarmDateAndTime") : null;
            // each on its own: one refused target must not take the others with it, Patch logs which one
            bool ok = sHcCallBack != null && sHcDue != null && Patch(sHcTimer, "get_YieldRequired", Type.EmptyTypes, typeof(HcTimerHook), "YieldHook", "YieldOrig");
            ok &= Patch(typeof(Lot), "UpdateDoorPrivacy", Type.EmptyTypes, typeof(HcLotHook), "DoorHook", "DoorOrig");
            ok &= Patch(typeof(Lot), "UpdateDetailPriority", Type.EmptyTypes, typeof(HcLotHook), "DetailHook", "DetailOrig");
            // here, before the first world load compiles it
            // docs/247 §12.4: the reset tail's selection step; at startup, before the first world load compiles it. Stamps only inside a
            // reset task's record (HcRstNow), so any other selection runs straight through. DoSelectActor does not sleep [inferred, decompile]
            ok &= Patch(typeof(PlumbBob), "DoSelectActor", new Type[] { typeof(Sim), typeof(bool) }, typeof(HcTailHook), "SelHook", "SelOrig");
            ok &= Patch(typeof(Sims3.Gameplay.GlobalFunctions), "CreateObjectWithOverrides", new Type[] { typeof(string), typeof(ProductVersion), typeof(Vector3), typeof(int), typeof(Vector3), typeof(System.Collections.Hashtable), typeof(Simulator.ObjectInitParameters) }, typeof(HcInstHook), "CowHook", "CowOrig");
            TunedLog("kHitchCauseHooks", ok, "alarm and lot pass stamps");
            HcApplyOverwatch();
        }

        // again at the first world load: the mod may load after app startup
        internal static void HcApplyOverwatch()
        {
            if (sHcOwTried || !Instantiator.kHitchCauseHooks) return;
            Assembly ow = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasOverwatch") { ow = a; break; }
            if (ow == null) return;
            sHcOwTried = true;
            Type simReset = ow.GetType("NRaas.CommonSpace.Tasks.ResetSimTask"), inst = ow.GetType("NRaas.CommonSpace.Tasks.FixInvisibleTask"),
                cp = ow.GetType("NRaas.CommonSpace.Tasks.ResetSimTask+CreationProtection"), place = ow.GetType("NRaas.CommonSpace.Helpers.Instantiation");
            bool ok = simReset != null && inst != null && cp != null && place != null
                && Patch(simReset, "Perform", new Type[] { typeof(Sim), typeof(bool) }, typeof(HcResetHook), "PerformHook", "PerformOrig")
                && Patch(inst, "InstantiateAtHome", null, typeof(HcInstHook), "InstHook", "InstOrig")
                && Patch(inst, "Perform", new Type[] { typeof(SimDescription), typeof(bool) }, typeof(HcInstHook), "RetryHook", "RetryOrig")
                && Patch(place, "AttemptToPutInSafeLocation", new Type[] { typeof(Sim), typeof(Vector3), typeof(bool) }, typeof(HcInstHook), "SafeHook", "SafeOrig")
                && Patch(cp, "Dispose", new Type[] { typeof(bool), typeof(bool) }, typeof(HcProtectHook), "DisposeHook", "DisposeOrig");
            // docs/247 §12.4 the 456 ms tail after InstantiateAtHome: NRaas' own steps (static, run only in a reset); each on its own
            ok &= Patch(simReset, "ResetSkillModifiers", new Type[] { typeof(SimDescription) }, typeof(HcTailHook), "SkillHook", "SkillOrig");
            ok &= Patch(simReset, "ResetRole", new Type[] { typeof(Sim) }, typeof(HcTailHook), "RoleHook", "RoleOrig");
            ok &= Patch(simReset, "UpdateInterface", new Type[] { typeof(Sim) }, typeof(HcTailHook), "UiHook", "UiOrig");
            TunedLog("kHitchCauseHooks", ok, "Overwatch reset timing");
        }

        static void HcLine(string s)
        {
            int minute = (int)(SrcNowUs() / 60000000u); // SrcNowUs wraps after ~71 min: one short bucket
            if (minute != sHcMinute) { sHcMinute = minute; sHcInMinute = 0; }
            if (sHcInMinute >= kHcLinesPerMin) { PatchCounters()[833]++; return; }
            sHcInMinute++;
            DiagLine(s);
        }

        static string HcSimTime()
        {
            return "day " + (SimClock.CurrentTicks / SimClock.ConvertToTicks(1f, TimeUnit.Days)) + " hour " + SimClock.HoursPassedOfDay.ToString("0.00");
        }

        static string HcName(object o)
        {
            if (o == null) return "static";
            Sim s = o as Sim;
            if (s != null) return "Sim " + s.FullName;
            Lot l = o as Lot;
            if (l != null) return (l.IsWorldLot ? "WorldLot " : "Lot ") + l.LotId.ToString("X");
            return o.GetType().FullName;
        }

        static string HcMethod(Delegate d)
        {
            if (d == null) return "?";
            Delegate[] all = d.GetInvocationList();
            MethodInfo m = all[0].Method;
            return (m.DeclaringType != null ? m.DeclaringType.FullName + "." : "") + m.Name + (all.Length > 1 ? " +" + (all.Length - 1) : "");
        }

        // ---- alarmtime ----
        static Delegate sHcCb, sHcTopCb;
        static bool sHcOpen, sHcDetailDone;
        static ObjectGuid sHcTask;
        static uint sHcT0, sHcTopUs, sHcPassUs, sHcDoorUs, sHcDetailUs, sHcRestT0;
        static int sHcPassN, sHcPhase; // 0 none, 1 alarms, 2 lot updates
        static Lot sHcLot;

        // closes the open alarm at now
        static void HcClose(uint now, bool yielded)
        {
            if (!sHcOpen) return;
            sHcOpen = false;
            uint us = unchecked(now - sHcT0);
            int[] c = PatchCounters();
            c[826]++;
            sHcPassN++;
            sHcPassUs += us;
            if (us >= sHcTopUs) { sHcTopUs = us; sHcTopCb = sHcCb; }
            if (us < kHcMinUs) return;
            c[827]++;
            IScriptProxy p = Simulator.GetProxy(sHcTask);
            HcLine("alarmtime: " + (us / 1000) + " ms, " + HcMethod(sHcCb) + ", target " + HcName(sHcCb != null ? sHcCb.Target : null)
                + ", task " + HcName(p != null ? p.Target : null) + ", " + HcSimTime() + ", ended=" + (yielded ? "yield" : "run"));
        }

        // now = the next stamp after the pass
        static void HcPassFlush(uint now)
        {
            if (sHcPhase == 0) return;
            uint total = sHcPassUs + sHcDoorUs + sHcDetailUs, rest = sHcDetailDone ? unchecked(now - sHcRestT0) : 0;
            if (total >= kHcMinUs || (rest >= kHcRestMinUs && sHcLot != null && sHcLot.IsWorldLot))
            {
                PatchCounters()[828]++;
                HcLine("lotpass: " + (total / 1000) + " ms, lot " + HcName(sHcLot) + ", alarms " + sHcPassN + " " + (sHcPassUs / 1000) + " ms, top " + (sHcTopUs / 1000)
                    + " ms " + HcMethod(sHcTopCb) + ", door " + (sHcDoorUs / 1000) + " ms, detail " + (sHcDetailUs / 1000) + " ms, rest<= " + (sHcDetailDone ? (rest / 1000).ToString() : "?")
                    + " ms, " + HcSimTime());
            }
            sHcDetailDone = false;
            sHcPhase = 0; sHcPassN = 0; sHcPassUs = sHcTopUs = sHcDoorUs = sHcDetailUs = 0; sHcTopCb = null; sHcLot = null;
        }

        // t: a timer Simulate is about to call back. The reset paths read the same getter on timers not due yet: skipped.
        // shortcut: a timer due in this same pass and removed by a reset during a callback opens a false stamp, rare, split by owner only if it shows.
        static void HcAlarmDue(object t)
        {
            try
            {
                if (((DateAndTime)sHcDue.GetValue(t)).CompareTo(SimClock.CurrentTime()) >= 0) return;
                ObjectGuid task = Simulator.CurrentTask;
                uint now = SrcNowUs();
                HcClose(now, sHcOpen && sHcTask != task);
                if (sHcPhase == 2) HcPassFlush(now);
                sHcPhase = 1;
                sHcCb = (Delegate)sHcCallBack.GetValue(t); // now: InternalRemoveAlarm clears it right after, before the callback
                sHcTask = task;
                sHcOpen = true;
                sHcT0 = SrcNowUs();
            }
            catch (Exception) { sHcOpen = false; }
        }

        static uint HcDoorStart(Lot lot)
        {
            uint now = SrcNowUs();
            try
            {
                HcClose(now, sHcOpen && sHcTask != Simulator.CurrentTask);
                if (sHcPhase == 2) HcPassFlush(now);
                sHcPhase = 2;
                sHcLot = lot;
            }
            catch (Exception) { }
            return SrcNowUs();
        }

        // same signature as the target: a header swap hands callers the hook's
        public class HcTimerHook
        {
            public bool YieldHook()
            {
                if (ShortcutOn("alarmtime")) HcAlarmDue(this);
                return YieldOrig();
            }
            public bool YieldOrig() { return false; } // receives Timer.get_YieldRequired's original header
        }

        public class HcLotHook
        {
            public void DoorHook()
            {
                if (!ShortcutOn("alarmtime")) { DoorOrig(); return; }
                uint t = HcDoorStart((Lot)(object)this);
                DoorOrig();
                sHcDoorUs = unchecked(SrcNowUs() - t);
            }
            public void DoorOrig() { }

            public void DetailHook()
            {
                if (!ShortcutOn("alarmtime")) { DetailOrig(); return; }
                uint t = SrcNowUs();
                DetailOrig();
                sHcDetailUs = unchecked(SrcNowUs() - t);
                sHcRestT0 = SrcNowUs();
                sHcDetailDone = true;
            }
            public void DetailOrig() { }
        }

        // ---- boatreplanlog ----
        static readonly Sims3.SimIFace.Route.RouteOption2 kWouldBits = unchecked((Sims3.SimIFace.Route.RouteOption2)0x8800000000000000UL);
        struct HcBoat
        {
            public ScriptCore.Route Route;
            public string Caller, Prev, Sim, Boat, Lot, Why, Uf;
            public int SimWhy;
            public ulong SimId;
            public bool Unseen, Take;
            public Vector3 From;
        }
        static HcBoat sHcBoat;

        static bool HcWould(ScriptCore.Route r, uint q) { return r.GetOption2((Sims3.SimIFace.Route.RouteOption2)((ulong)kWouldBits | q)); }

        // RouteSrcBegin, before the plan
        static void HcBoatBegin(ScriptCore.Route r, Vector3 fromPt)
        {
            sHcBoat.Route = null;
            if (!ShortcutOn("boatreplanlog")) return;
            try
            {
                IScriptProxy f = r.Follower;
                GameObject boat = f != null ? f.Target as GameObject : null;
                if (!(boat is IBoat)) return;
                string caller = "?";
                for (int i = 1; i < 24; i++)
                {
                    MethodBase m = new StackFrame(i, false).GetMethod();
                    if (m == null) break;
                    string ft = m.DeclaringType != null ? m.DeclaringType.FullName : null;
                    if (ft == "Ts3Fixes.Fixes" || RouteSrcCore.IsPassThrough(ft, m.Name)) continue;
                    caller = ft + "." + m.Name;
                    break;
                }
                IScriptProxy task = Simulator.GetProxy(Simulator.CurrentTask);
                Sim sim = task != null ? task.Target as Sim : null;
                RouteAction prev = sim != null && sim.RoutingComponent != null ? sim.RoutingComponent.GetPreviousRouteAction() : null;
                if (prev == null && boat.RoutingComponent != null) prev = boat.RoutingComponent.GetPreviousRouteAction();
                bool boatUnseen = UfPosUnseen(boat.Position, kUfSimRadiusM);
                Vector3 dest = r.GetApproximateOriginalDestination();
                HcBoat b = default(HcBoat);
                b.SimWhy = sim != null ? UfWhy(sim) : -1;
                b.Unseen = UbCore.ReplanUnseen(b.SimWhy, boatUnseen);
                b.SimId = sim != null ? sim.ObjectId.Value : 0;
                b.Route = r;
                b.Caller = caller;
                b.Prev = prev != null ? prev.GetType().Name : "none";
                b.Sim = sim != null ? sim.FullName : HcName(task != null ? task.Target : null);
                b.Boat = boat.GetType().Name + " seen=" + (boatUnseen ? 0 : 1);
                b.From = fromPt;
                Lot lot = sim != null ? sim.LotCurrent : null, goal = LotManager.GetLotAtPoint(dest);
                b.Lot = (lot != null ? lot.LotId.ToString("X") : "0") + " goalLot " + (goal != null ? goal.LotId.ToString("X") : "0");
                // ts3native: what a foreign START meets now (RouteBackoff.cs AsyncKindStart, then GetOptionHook, AsyncStart)
                string why = "";
                bool could = true;
                if (!RouteBackoffHook.ForeignEligible()) { why += ",noTaskYield"; could = false; }
                if (!(ShortcutOn("asyncreplan") && ShortcutOn("asyncreplanforeign"))) { why += ",switch"; could = false; }
                if (!HcWould(r, 1)) { why += ",asyncOff"; could = false; }
                if (!HcWould(r, 5)) { why += ",afNoYield"; could = false; }
                if (HcWould(r, 6)) { why += ",afActive"; could = false; }
                if (!HcWould(r, 7)) { why += ",m1"; could = false; }
                b.Take = could;
                if (!HcWould(r, 2)) why += ",ForeignKinds=0";
                if (!HcWould(r, 3)) why += ",afMode=0";
                if (!HcWould(r, 4)) why += ",Kinds=0";
                b.Why = why.Length > 0 ? why.Substring(1) : "none";
                // UnseenFallback: UfPlanBody takes a Sim follower only, and D (unseenfbpre) kind 0 only
                b.Uf = "follower=boat,kind=3, townieUnseen=" + (sim != null && TownieUnseen(sim, null) ? 1 : 0) + " goalUnseen=" + (UfGoalUnseen(dest) ? 1 : 0);
                sHcBoat = b;
            }
            catch (Exception) { sHcBoat.Route = null; }
        }

        // RflPlan, after the plan; t0 = SrcNowUs() before it
        static void HcBoatEnd(ScriptCore.Route r, uint t0, RoutePlanResult res, bool async)
        {
            if (sHcBoat.Route == null) return;
            HcBoat b = sHcBoat;
            sHcBoat.Route = null;
            if (!ReferenceEquals(b.Route, r)) return;
            try
            {
                uint ms = unchecked(SrcNowUs() - t0) / 1000;
                int[] c = PatchCounters();
                c[829]++;
                if (b.Take) c[830]++;
                if (b.Unseen) c[831]++;
                c[b.Unseen ? 835 : 834] += (int)ms;
                if (ShortcutOn("unseenboat")) UbBookReplan(b.SimId, ms);
                Vector3 dest = r.GetApproximateOriginalDestination();
                HcLine("boatreplan: " + ms + " ms, " + (async ? "async" : "sync") + " " + res.mType + ", caller " + b.Caller + ", prev " + b.Prev + ", sim " + b.Sim + " uf=" + b.SimWhy
                    + ", " + b.Boat + ", class " + (b.Unseen ? "unseen" : "seen") + ", job " + (b.Take ? "could" : "no") + " why " + b.Why + ", ufskip " + b.Uf + ", dist " + (dest - b.From).Length().ToString("0.0")
                    + ", route " + r.GetRouteLength().ToString("0.0") + " paths " + r.GetNumPaths() + ", lot " + b.Lot + ", " + V(b.From) + " to " + V(dest)
                    + ", cleared " + UbClearedText(b.SimId));  // docs/247 §12.9: the UbTrip exit behind a "no refusal" replan
            }
            catch (Exception) { }
        }

        // ---- owresettime ----
        // One record per task, the steps of a reset sleep and other tasks run in between.
        class HcRst
        {
            public bool OnPerform, Performed;
            public uint PerfEnd, InstEnd, CowUs, SafeUs, RetryUs, DisposeUs;
            public int CowN, CowNull, SafeN, SafeNo, RetryN;
            public uint SelUs, SkillUs, RoleUs, UiUs;  // docs/247 §12.4 tail steps (sleeps inside them count)
            public int SelN;
        }
        static readonly Dictionary<ulong, HcRst> sHcRst = new Dictionary<ulong, HcRst>();

        static HcRst HcRstNow(bool make)
        {
            ulong task = Simulator.CurrentTask.Value;
            HcRst r;
            if (!sHcRst.TryGetValue(task, out r) && make) { r = new HcRst(); sHcRst[task] = r; }
            return r;
        }

        // Test package only: replaces OwResetHook on StuckCheck.ResetTask.OnPerform, so the release hook's IL stays the same
        // (a frame of a changed hook on a saved stack is refused once, docs/242).
        // shortcut: no stuck notice sorting under this hook, take that path into PerformHook if a test needs both on.
        public class HcOnPerformHook
        {
            public void PerformHook()
            {
                if (!ShortcutOn("owresettime")) { PerformOrig(); return; }
                uint t0 = SrcNowUs();
                string who = "null", suffix = "null";
                try
                {
                    Sim sim = (Sim)sOwnSim.GetValue(this);
                    suffix = (string)sOwnSuffix.GetValue(this) ?? "null";
                    if (sim != null && sim.SimDescription != null) who = sim.FullName + ", " + sim.SimDescription.Species + " " + sim.SimDescription.Age;
                }
                catch (Exception) { }
                HcRst r = HcRstNow(true);
                r.OnPerform = true;
                try { PerformOrig(); }
                finally { HcOnPerformDone(r, t0, who, suffix); }
            }
            public void PerformOrig() { } // receives StuckCheck.ResetTask.OnPerform's original header
        }

        // after = time after ResetSimTask.Perform: Common.Notify, its Localize and StyledNotification. No Perform and safe > 0:
        // AttemptToPutInSafeLocation took the path with no reset.
        static void HcOnPerformDone(HcRst r, uint t0, string who, string suffix)
        {
            try
            {
                uint now = SrcNowUs();
                sHcRst.Remove(Simulator.CurrentTask.Value);
                PatchCounters()[832]++;
                HcLine("owreset: OnPerform " + who + ", " + (unchecked(now - t0) / 1000) + " ms with sleeps, after " + (r.Performed ? (unchecked(now - r.PerfEnd) / 1000).ToString() : "-")
                    + " ms, suffix " + suffix + ", reset " + (r.Performed ? "yes" : "no") + ", safe " + r.SafeN + " " + (r.SafeUs / 1000) + " ms no " + r.SafeNo + ", " + HcSimTime());
            }
            catch (Exception) { }
        }

        public class HcResetHook
        {
            public static Sim PerformHook(Sim sim, bool fadeOut)
            {
                if (!ShortcutOn("owresettime")) return PerformOrig(sim, fadeOut);
                uint t0 = SrcNowUs();
                string name = sim != null ? sim.FullName : "null";
                bool home = sim != null && sim.LotHome != null;
                bool sel = sim != null && Sim.ActiveActor == sim;  // NRaas' "active": the tail then runs DoSelectActor
                HcRstNow(true);
                Sim r = null;
                try { r = PerformOrig(sim, fadeOut); }
                finally { HcResetDone(name, home, fadeOut, r != null, t0, sel); }
                return r;
            }
            public static Sim PerformOrig(Sim sim, bool fadeOut) { return null; }
        }

        static void HcResetDone(string name, bool home, bool fade, bool made, uint t0, bool sel)
        {
            try
            {
                uint now = SrcNowUs();
                HcRst r = HcRstNow(true);
                r.Performed = true;
                r.PerfEnd = now;
                if (!r.OnPerform) sHcRst.Remove(Simulator.CurrentTask.Value);
                PatchCounters()[832]++;
                string tail = "-";
                if (r.InstEnd != 0)
                {
                    uint t = unchecked(now - r.InstEnd), parts = r.SelUs + r.SkillUs + r.RoleUs + r.UiUs;
                    tail = (t / 1000) + " ms (select " + r.SelN + " " + (r.SelUs / 1000) + ", skills " + (r.SkillUs / 1000) + ", role " + (r.RoleUs / 1000) + ", ui " + (r.UiUs / 1000)
                        + ", rest " + ((t > parts ? t - parts : 0) / 1000) + ")";
                }
                HcLine("owreset: Perform " + name + ", " + (unchecked(now - t0) / 1000) + " ms with sleeps, selected " + sel + ", tail " + tail + ", home " + home + " fade " + fade
                    + " result " + made + ", create " + r.CowN + " " + (r.CowUs / 1000) + " ms null " + r.CowNull + ", retry " + r.RetryN + " " + (r.RetryUs / 1000) + " ms, safe " + r.SafeN + " " + (r.SafeUs / 1000) + " ms, restore " + (r.DisposeUs / 1000) + " ms, " + HcSimTime());
            }
            catch (Exception) { }
        }

        public class HcInstHook
        {
            public static Sim InstHook(SimDescription d, object onReset)
            {
                if (!ShortcutOn("owresettime")) return InstOrig(d, onReset);
                uint t0 = SrcNowUs();
                long tick = SimClock.CurrentTicks;
                HcRst st = HcRstNow(false);
                int cow0 = st != null ? st.CowN : 0;
                Sim r = InstOrig(d, onReset);
                try
                {
                    uint now = SrcNowUs();
                    if (st != null) st.InstEnd = now;
                    PatchCounters()[832]++;
                    HcLine("owreset: InstantiateAtHome " + (d != null ? d.FullName + ", " + d.Species + " " + d.Age : "null") + ", " + (unchecked(now - t0) / 1000) + " ms, yielded " + (SimClock.CurrentTicks != tick) + ", result " + (r != null)
                        + ", tries " + (st != null ? (st.CowN - cow0).ToString() : "?"));
                }
                catch (Exception) { }
                return r;
            }
            public static Sim InstOrig(SimDescription d, object onReset) { return null; }

            // InstantiateAtHome's second try: reached only after a null first result. Approach is an int enum.
            public static int RetryHook(SimDescription d, bool force)
            {
                HcRst st = ShortcutOn("owresettime") ? HcRstNow(false) : null;
                if (st == null) return RetryOrig(d, force);
                uint t0 = SrcNowUs();
                int r = RetryOrig(d, force);
                st.RetryN++;
                st.RetryUs += unchecked(SrcNowUs() - t0);
                return r;
            }
            public static int RetryOrig(SimDescription d, bool force) { return 0; }

            public static bool SafeHook(Sim ths, Vector3 destination, bool allowHibernate)
            {
                HcRst st = ShortcutOn("owresettime") ? HcRstNow(false) : null;
                if (st == null) return SafeOrig(ths, destination, allowHibernate);
                uint t0 = SrcNowUs();
                bool r = SafeOrig(ths, destination, allowHibernate);
                st.SafeN++;
                if (!r) st.SafeNo++;
                st.SafeUs += unchecked(SrcNowUs() - t0);
                return r;
            }
            public static bool SafeOrig(Sim ths, Vector3 destination, bool allowHibernate) { return false; }

            public static IGameObject CowHook(string instanceName, ProductVersion version, Vector3 initPos, int level, Vector3 initFwd, System.Collections.Hashtable overrides, Simulator.ObjectInitParameters initData)
            {
                HcRst st = ShortcutOn("owresettime") ? HcRstNow(false) : null;
                if (st == null) return CowOrig(instanceName, version, initPos, level, initFwd, overrides, initData);
                uint t0 = SrcNowUs();
                IGameObject r = CowOrig(instanceName, version, initPos, level, initFwd, overrides, initData);
                st.CowN++;
                if (r == null) st.CowNull++;
                st.CowUs += unchecked(SrcNowUs() - t0);
                return r;
            }
            public static IGameObject CowOrig(string instanceName, ProductVersion version, Vector3 initPos, int level, Vector3 initFwd, System.Collections.Hashtable overrides, Simulator.ObjectInitParameters initData) { return null; }
        }

        // docs/247 §12.4: the reset tail's steps, stamped into the reset task's record; outside a reset they run straight through
        public class HcTailHook
        {
            public static bool SelHook(Sim actor, bool force)
            {
                HcRst st = ShortcutOn("owresettime") ? HcRstNow(false) : null;
                if (st == null) return SelOrig(actor, force);
                uint t0 = SrcNowUs();
                bool r = SelOrig(actor, force);
                st.SelN++;
                st.SelUs += unchecked(SrcNowUs() - t0);
                return r;
            }
            public static bool SelOrig(Sim actor, bool force) { return false; }

            public static void SkillHook(SimDescription sim)
            {
                HcRst st = ShortcutOn("owresettime") ? HcRstNow(false) : null;
                uint t0 = SrcNowUs();
                SkillOrig(sim);
                if (st != null) st.SkillUs += unchecked(SrcNowUs() - t0);
            }
            public static void SkillOrig(SimDescription sim) { }

            public static void RoleHook(Sim sim)
            {
                HcRst st = ShortcutOn("owresettime") ? HcRstNow(false) : null;
                uint t0 = SrcNowUs();
                RoleOrig(sim);
                if (st != null) st.RoleUs += unchecked(SrcNowUs() - t0);
            }
            public static void RoleOrig(Sim sim) { }

            public static void UiHook(Sim sim)
            {
                HcRst st = ShortcutOn("owresettime") ? HcRstNow(false) : null;
                uint t0 = SrcNowUs();
                UiOrig(sim);
                if (st != null) st.UiUs += unchecked(SrcNowUs() - t0);
            }
            public static void UiOrig(Sim sim) { }
        }

        // ResetSimTask.CreationProtection, the restore at the end of the reset; runs with the protection as this
        public class HcProtectHook
        {
            public void DisposeHook(bool postLoad, bool isReset)
            {
                HcRst st = ShortcutOn("owresettime") ? HcRstNow(false) : null;
                if (st == null) { DisposeOrig(postLoad, isReset); return; }
                uint t0 = SrcNowUs();
                DisposeOrig(postLoad, isReset);
                st.DisposeUs += unchecked(SrcNowUs() - t0);
            }
            public void DisposeOrig(bool postLoad, bool isReset) { }
        }
    }
}
