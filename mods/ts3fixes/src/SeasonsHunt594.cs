using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Environment;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/594 Seasons hunt. Both switches off by default.
    //
    // initstacktrace (tuning kInitStackTrace; docs/594 §1). [verified, IL of Sims3GameplaySystems.dll, EA and simler90 242]
    // InteractionInstance.Init stores `new StackTrace(4, true)` in mInitStackTrace and OnAddedToQueue stores
    // `new StackTrace(1, true)` in mAddedStackTrace. The only reader is LogDebugInfo, which is [Conditional("EA_DEBUG")] /
    // [Conditional("EA_DEBUGRELEASE")]: every call to it is compiled out of the retail DLLs, so both stores are dead.
    // [verified, EA mscorlib] StackTrace.init_frames builds the trace with one `new StackFrame(n, true)` per frame, and each
    // StackFrame walks the stack from the top again: O(depth^2) frame visits plus one StackFrame and MethodBase per frame,
    // for every interaction instance the game creates. Seasons multiplies it: in rain or hail,
    // Autonomy.CalculateScoreForObjectInteraction creates a throwaway instance for every outdoor non-Sim candidate it
    // scores (to ask SeasonsManager.IsShelteredFromPrecipitation, which reads it only for ICanCountAsBeingIndoors targets).
    // Fix: Init and OnAddedToQueue are our copies of EA's bodies without the dead stack walk; every field is set in EA's
    // order with EA's values (private ones through reflection), so the instance is EA's with mInitStackTrace /
    // mAddedStackTrace null. Exact speedup for every Sim, household included (the fields have no reader).
    // Counters: [2128] Init walks skipped, [2129] OnAddedToQueue walks skipped, [2130] walks run (switch off), [2131] errors
    // (an error falls back to EA's body).
    //
    // weatherstonelistener (kWeatherStoneListener; docs/594 §2). [verified, decompiled] WeatherStone keeps the
    // kWeatherStarted / kWeatherStopped listener of a summon in mWeatherChangedEventListener (added in
    // AnimateChangeWeatherStoneState(SummoningWeather) and OnWeatherStarted, removed only by the listener itself when the
    // summoned weather starts and stops). Dispose does not remove it, so a stone destroyed during a summon (SetToDestroy,
    // build/buy delete) leaves the listener on the tracker: it keeps the dead stone alive and its callbacks run
    // AnimateChangeWeatherStoneState on the disposed stone. Fix: Dispose removes the listener first (as every other EA
    // object that holds one does), then runs EA's Dispose. Counters: [2132] listeners removed at Dispose, [2133] listeners
    // left by Dispose (switch off), [2134] errors.
    public static partial class Fixes
    {
        const BindingFlags kIstInst = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo sIstSearch = typeof(InteractionInstance).GetField("mAutonomySearchType", kIstInst);
        static readonly FieldInfo sIstFlags = typeof(InteractionInstance).GetField("mFlags", kIstInst);
        static readonly FieldInfo sIstNextId = typeof(InteractionInstance).GetField("sNextId", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly FieldInfo sIstInitTrace = typeof(InteractionInstance).GetField("mInitStackTrace", kIstInst);
        static readonly FieldInfo sIstAddedTrace = typeof(InteractionInstance).GetField("mAddedStackTrace", kIstInst);
        static readonly FieldInfo sIstSelected = typeof(InteractionInstance).GetField("mSelectedObjects", kIstInst);
        static readonly FieldInfo sWslListener = typeof(WeatherStone).GetField("mWeatherChangedEventListener", kIstInst);

        static void ApplyInitStackTrace()
        {
            string miss = SpNames("InteractionInstance.mAutonomySearchType", sIstSearch, "InteractionInstance.mFlags", sIstFlags,
                "InteractionInstance.sNextId", sIstNextId, "InteractionInstance.mSelectedObjects", sIstSelected);
            if (miss.Length > 0) { AppliedLog().Add("initstacktrace: not found: " + miss); return; }
            Patch(typeof(InteractionInstance), "Init", new Type[] { typeof(InteractionInstanceParameters).MakeByRefType() }, typeof(IstHook), "InitHook", "InitOrig");
            Patch(typeof(InteractionInstance), "OnAddedToQueue", Type.EmptyTypes, typeof(IstHook), "AddedHook", "AddedOrig");
        }

        static void ApplyWeatherStoneListener()
        {
            if (sWslListener == null) { AppliedLog().Add("weatherstonelistener: not found: WeatherStone.mWeatherChangedEventListener"); return; }
            Patch(typeof(WeatherStone), "Dispose", Type.EmptyTypes, typeof(WslHook), "DisposeHook", "DisposeOrig");
        }

        // Pure: skip the dead stack walk? (only with the switch on; the fields have no reader in retail code)
        internal static bool IstSkip(bool on) { return on; }

        // Pure: remove the stone's weather listener before EA's Dispose? (on, and a listener is still held)
        internal static bool WslRemove(bool on, bool held) { return on && held; }

        // Derives from InteractionInstance so the protected mPriority and the public members compile; at run time `this`
        // is the EA instance whose Init / OnAddedToQueue was called.
        public abstract class IstHook : InteractionInstance
        {
            public void InitHook(ref InteractionInstanceParameters parameters)
            {
                if (!IstSkip(ShortcutOn("initstacktrace"))) { PatchCounters()[2130]++; InitOrig(ref parameters); return; }
                object o = this;
                ulong id;
                try
                {
                    // EA's Init, in its order, without `mInitStackTrace = new StackTrace(4, true)`
                    sIstSearch.SetValue(o, parameters.AutonomySearchType);
                    sIstFlags.SetValue(o, parameters.Flags);
                    id = (ulong)sIstNextId.GetValue(null);
                    sIstNextId.SetValue(null, id + 1);
                }
                catch (Exception) { PatchCounters()[2131]++; InitOrig(ref parameters); return; }
                Id = id;
                GroupId = Id;
                InteractionObjectPair = parameters.InteractionObjectPair;
                InstanceActor = parameters.Actor as Sim;
                mPriority = parameters.Priority;
                Prioritized = false;
                mTargetInventoryWhenCreated = null;
                IGameObject target = parameters.Target;
                if (target != null)
                {
                    ItemComponent itemComp = target.ItemComp;
                    if (itemComp != null) mTargetInventoryWhenCreated = itemComp.InventoryParent;
                }
                if (InteractionObjectPair.Tuning != null && InteractionObjectPair.Tuning.PosturePreconditions != null)
                    PosturePreconditions = new PreconditionOptions(InteractionObjectPair.Tuning.PosturePreconditions);
                sIstSelected.SetValue(o, parameters.SelectedObjects);
                PatchCounters()[2128]++;
            }
            public void InitOrig(ref InteractionInstanceParameters parameters) { } // InteractionInstance.Init

            public void AddedHook()
            {
                if (!IstSkip(ShortcutOn("initstacktrace"))) { PatchCounters()[2130]++; AddedOrig(); return; }
                // EA's OnAddedToQueue without `mAddedStackTrace = new StackTrace(1, true)`
                IGameObject target = InteractionObjectPair.Target;
                if (target != null)
                {
                    target.AddToReferenceList(InstanceActor);
                    if (target.InUse) WasInUse = true;
                }
                PatchCounters()[2129]++;
            }
            public void AddedOrig() { } // InteractionInstance.OnAddedToQueue
        }

        // Derives from GameObject so EventTracker / base members compile; at run time `this` is the WeatherStone.
        public abstract class WslHook : GameObject
        {
            public void DisposeHook()
            {
                try
                {
                    object o = this;
                    EventListener l = sWslListener.GetValue(o) as EventListener;
                    if (l != null)
                    {
                        if (WslRemove(ShortcutOn("weatherstonelistener"), true))
                        {
                            EventTracker.RemoveListener(l);
                            sWslListener.SetValue(o, null);
                            PatchCounters()[2132]++;
                        }
                        else PatchCounters()[2133]++;
                    }
                }
                catch (Exception) { PatchCounters()[2134]++; }
                DisposeOrig();
            }
            public void DisposeOrig() { } // WeatherStone.Dispose
        }

        // InitStackTraceCheck run: creates one throwaway GoHere instance on the active Sim (what autonomy's rain branch does
        // per candidate; never queued) and reads its mInitStackTrace: null = our Init ran. Controls: the instance has EA's
        // Init values (Id > 0, GroupId = Id, actor, target, autonomous flag, priority level, inventory, no added trace), Ids
        // stay consecutive, and the pure rule keeps EA's walk with the switch off. 'result' also times one
        // `new StackTrace(4, true)` at this depth (us) against one instance creation, as the gain per instance. Read-only.
        public static object InitStackTraceCheck(string what)
        {
            string counters = B582Counters("initstacktrace", 2128, new[] { "Init walks skipped", "OnAddedToQueue walks skipped", "walks run (switch off)", "errors" }, "");
            if (what == "result") return counters + "; " + IstTiming();
            if (what != "run") return "initstacktrace <run|result>";
            bool on = ShortcutOn("initstacktrace");
            bool fixedNow = false, controls = !IstSkip(false) && IstSkip(true);
            string missing = SpNames("InteractionInstance.mInitStackTrace", sIstInitTrace, "InteractionInstance.mAddedStackTrace", sIstAddedTrace);
            try
            {
                Sim actor = Sim.ActiveActor;
                Terrain t = Terrain.Singleton;
                if (actor == null || t == null) missing += "active Sim / Terrain ";
                else
                {
                    InteractionInstance a = Terrain.GoHere.SameLotSingleton.CreateInstance(t, actor, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, true);
                    InteractionInstance b = Terrain.GoHere.SameLotSingleton.CreateInstance(t, actor, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, true);
                    fixedNow = sIstInitTrace != null && sIstInitTrace.GetValue(a) == null;
                    controls = controls && a.Id > 0 && a.GroupId == a.Id && b.Id == a.Id + 1 && a.InstanceActor == actor && a.Target == t
                        && a.Autonomous && a.GetPriority().Level == InteractionPriorityLevel.Autonomous && a.mTargetInventoryWhenCreated == null
                        && !a.Prioritized && (sIstAddedTrace == null || sIstAddedTrace.GetValue(a) == null)
                        && (on ? sIstInitTrace.GetValue(b) == null : sIstInitTrace.GetValue(b) != null);
                }
            }
            catch (Exception e) { missing += "threw " + e.GetType().Name + " "; }
            return B582Line("initstacktrace", on, "no dead stack walk in Init", fixedNow, "EA's Init values, ids consecutive, off keeps EA's walk=" + controls, controls, missing.Trim(), counters);
        }

        // us for one `new StackTrace(4, true)` here and for one throwaway instance with the switch as it is (20 each).
        static string IstTiming()
        {
            try
            {
                Sim actor = Sim.ActiveActor;
                Terrain t = Terrain.Singleton;
                uint t0 = SrcNowUs();
                for (int i = 0; i < 20; i++) new StackTrace(4, true);
                uint t1 = SrcNowUs();
                if (actor != null && t != null)
                    for (int i = 0; i < 20; i++) Terrain.GoHere.SameLotSingleton.CreateInstance(t, actor, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, true);
                uint t2 = SrcNowUs();
                return "timing: stack walk " + ((t1 - t0) / 20u) + " us, instance " + ((t2 - t1) / 20u) + " us (switch " + (ShortcutOn("initstacktrace") ? "on" : "off") + ")";
            }
            catch (Exception e) { return "timing: threw " + e.GetType().Name; }
        }

        // WeatherStoneListenerCheck run: the Dispose rule on a held listener (remove when on, leave when off). Controls: no
        // listener held = nothing to do; off = EA's Dispose. Live: the world's stone (if any) and its field are readable.
        public static object WeatherStoneListenerCheck(string what)
        {
            string counters = B582Counters("weatherstonelistener", 2132, new[] { "listeners removed at Dispose", "listeners left by Dispose (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "weatherstonelistener <run|result>";
            bool on = ShortcutOn("weatherstonelistener");
            bool fixedNow = WslRemove(on, true);
            bool controls = !WslRemove(true, false) && !WslRemove(false, true) && !WslRemove(false, false);
            string missing = SpNames("WeatherStone.mWeatherChangedEventListener", sWslListener);
            try
            {
                WeatherStone[] stones = Sims3.Gameplay.Queries.GetObjects<WeatherStone>();
                if (stones.Length > 0 && sWslListener != null) sWslListener.GetValue(stones[0]);
            }
            catch (Exception e) { missing += " threw " + e.GetType().Name; }
            return B582Line("weatherstonelistener", on, "summon listener removed at Dispose", fixedNow, "no listener / off as EA=" + controls, controls, missing.Trim(), counters);
        }
    }
}
