using System;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Game-independent part of hovertraintest: "are at least N of these objects listed?" answered from the objects EA's
    // query listed last time, while nothing could have added one. Removal needs no event: a stored object that is gone
    // turns the memo unknown (the caller asks EA again). Additions must call Forget.
    public sealed class AtLeastMemo
    {
        object[] mRefs;
        bool mKnown;

        public bool Known { get { return mKnown; } }
        public int Stored { get { return mKnown ? mRefs.Length : -1; } }

        public void Forget() { mKnown = false; mRefs = null; }

        public void Store(object[] refs) { mRefs = refs ?? new object[0]; mKnown = true; }

        // 1: at least `need` listed, 0: fewer, -1: unknown (never stored, forgotten, or a stored object is gone).
        // Fewer than `need` stored stays "fewer" with no liveness test: a removal only lowers the count. With `need` or more
        // stored, only the first `need` are tested; any one gone means "ask EA" (the rest could still make the count).
        public int Answer(int need, Predicate<object> alive)
        {
            if (!mKnown) return -1;
            if (mRefs.Length < need) return 0;
            for (int i = 0; i < need; i++)
                if (!alive(mRefs[i])) { Forget(); return -1; }
            return 1;
        }
    }

    // hovertraintest (kHoverTrainTest; docs/598 §5). [verified, decompiled] Sim.NPCUseHoverTrain is a solo interaction on
    // every Sim (EP11); its Definition.Test, for every autonomous non-selectable Sim, asks Queries.GetObjects<IHoverTrainStation>()
    // (a native walk of the script host's per-class maps, docs/125 §4.1) and compares the length with 2, in every world,
    // stations or not. Fix: the same answer from AtLeastMemo. EA's gate (`!isAutonomous || a.IsSelectable` -> false) runs
    // first and unchanged, so household / selectable Sims get EA's own false before the memo is read.
    // Additions forget the memo. [verified, decompiled] Every station script object starts in
    // HoverTrainStation.OnStartupRabbitHole: GameObject.Init (both arms, new and postLoad) calls OnStartup, RabbitHole.OnStartup
    // (sealed) calls OnStartupRabbitHole, and Init runs from ScriptProxy.SetLogic for every way an object starts: native
    // creation (lot / world load, buy mode, edit town: ScriptProxy.CreateLogic), save load (ScriptProxy post-load Init) and
    // script spawns (Simulator.CreateObject*, AddObject*, CreateScriptLogic: ScriptCore Simulator.AddObject -> SetLogic).
    // One hook there replaces the old Simulator.CreateObject / AddObject* swaps (Simulator.AddObject(object) is compiled
    // before the mod loads, so that swap is always refused and the memo never armed).
    //   - The station enters the native maps right after Init (AddObjectImpl after SetLogic). So the hook keeps it pending:
    //     while a pending station has no registered proxy (and init did not fail), the memo answers unknown and stores
    //     nothing. [inferred: GetProxy finds an object once it is registered; the check compares the memo with EA's query]
    //   - Another IHoverTrainStation class (a mod), or a subclass that declares its own OnStartupRabbitHole (may skip ours),
    //     keeps the memo off: checked over the loaded assemblies at apply and at every world load.
    //   - Also forget (cheap, belt and braces): World.OnObjectPlacedInLot for a station or an unknown object, OnLotAdded /
    //     OnLotRemoved, world load started / finished, world quit, enter / leave not-in-world, LotManager.EnteringBuildBuyMode /
    //     ExitingBuildBuyMode; nothing is stored while build/buy or edit town is on (EA's query answers there).
    // Removals (sold, deleted, bulldozed, destroyed by script: kSoldObject / kEventObjectDisposed / OnObjectPendingDestruction
    // all end in Simulator.DestroyObject): a stored station whose proxy is gone makes the memo unknown. [inferred: an object
    // with a live proxy is in the per-class map and a destroyed one is not; the check compares the memo with EA's query live]
    // Run: EA's Run lists the stations again (kept: once per trip, and the order of the list feeds the random pick) and
    // dereferences GetClosestObject's answer, which is null when every station is charred (GetClosestObject skips charred
    // objects), and the travel definition; with the switch on both give EA's "no station" false instead of a throw. The
    // `destination == origin` step is one increment with wrap, not a loop: it always exits, and the list has no duplicate,
    // so the second pick differs (kept as EA, including the bias toward the station after the closest one).
    // Counters: [2157] tests answered from the memo, [2158] tests that ran EA's query (switch off: every gated test, the call
    // rate), [2159] memo forgets, [2160] check mismatches (memo vs EA, must stay 0), [2161] Run throws avoided, [2162] errors.
    public static partial class Fixes
    {
        static readonly Type sHtRun = typeof(Sim).GetNestedType("NPCUseHoverTrain", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sHtDef = sHtRun == null ? null : sHtRun.GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly AtLeastMemo sHtMemo = new AtLeastMemo();
        static readonly Predicate<object> sHtAlive = HtAlive;
        static bool sHtReady;   // every addition hook is in: without one the memo could miss a new station
        static bool sHtHooked;  // the OnStartupRabbitHole hook and the events are in
        static string sHtMissing = "";
        static readonly System.Collections.Generic.List<object> sHtPending = new System.Collections.Generic.List<object>();

        static void ApplyHoverTrainTest()
        {
            sHtMissing = (sHtRun == null ? "Sim+NPCUseHoverTrain " : "") + (sHtDef == null ? "Sim+NPCUseHoverTrain+Definition" : "");
            sHtMissing = sHtMissing.Trim();
            if (sHtMissing.Length > 0) { AppliedLog().Add("hovertraintest: not found: " + sHtMissing); return; }
            bool ok = Patch(typeof(HoverTrainStation), "OnStartupRabbitHole", Type.EmptyTypes, typeof(HtStationHook), "StartHook", "StartOrig");
            if (!ok) { sHtMissing = "HoverTrainStation.OnStartupRabbitHole hook (memo never used)"; AppliedLog().Add("hovertraintest: " + sHtMissing); }
            try
            {
                World.OnObjectPlacedInLotEventHandler += HtPlaced;
                World.OnLotAddedEventHandler += HtWorldEvent;
                World.OnLotRemovedEventHandler += HtWorldEvent;
                World.OnWorldLoadStartedEventHandler += HtWorldGone;
                World.OnWorldLoadFinishedEventHandler += HtWorldLoaded;
                World.OnWorldQuitEventHandler += HtWorldGone;
                World.OnEnterNotInWorldEventHandler += HtWorldEvent;
                World.OnLeaveNotInWorldEventHandler += HtWorldEvent;
                HtBuildBuyListen();
            }
            catch (Exception e) { ok = false; sHtMissing = "events threw " + e.GetType().Name; AppliedLog().Add("hovertraintest: " + sHtMissing); }
            Type[] test = new Type[] { typeof(Sim), typeof(Sim), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() };
            Patch(sHtDef, "Test", test, typeof(HtTestHook), "TestHook", "TestOrig");
            Patch(sHtRun, "Run", Type.EmptyTypes, typeof(HtRunHook), "RunHook", "RunOrig");
            sHtHooked = ok;
            HtCheckClasses();
        }

        // The memo arms only when every IHoverTrainStation class starts through the hooked OnStartupRabbitHole:
        // HoverTrainStation itself, or a subclass that does not declare its own (it runs ours). Anything else: memo off.
        static int sHtScanned = -1;  // assembly count at the last scan (assemblies never unload: same count, same answer)
        static string sHtForeign;

        static void HtCheckClasses()
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            if (all.Length != sHtScanned) { sHtForeign = HtForeignClass(all); sHtScanned = all.Length; }
            string foreign = sHtForeign;
            sHtReady = sHtHooked && foreign == null;
            if (!sHtReady) sHtMemo.Forget();
            if (foreign != null && sHtMissing.IndexOf("station class") < 0)
            {
                sHtMissing = EpJoin(sHtMissing, "other station class " + foreign + " (memo never used)");
                AppliedLog().Add("hovertraintest: memo off, other station class " + foreign);
            }
        }

        static string HtForeignClass(Assembly[] all)
        {
            const BindingFlags decl = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (Assembly a in all)
            {
                Type[] ts;
                try { ts = a.GetTypes(); } catch (Exception) { continue; }
                foreach (Type t in ts)
                {
                    if (t == null || t == typeof(HoverTrainStation) || !typeof(IHoverTrainStation).IsAssignableFrom(t) || t.IsInterface) continue;
                    if (t.IsSubclassOf(typeof(HoverTrainStation)) && t.GetMethod("OnStartupRabbitHole", decl) == null) continue;
                    return t.FullName;
                }
            }
            return null;
        }

        // OnStartupRabbitHole ran: a station is about to enter the native maps
        static void HtStarted(object station)
        {
            HtForget();
            if (station != null && !sHtPending.Contains(station)) sHtPending.Add(station);
        }

        // True while a started station is not yet registered (one pass, drops the resolved ones: StartResolved).
        static bool HtPendingOpen()
        {
            for (int i = sHtPending.Count - 1; i >= 0; i--)
            {
                if (StartResolved(sHtPending[i])) sHtPending.RemoveAt(i);
            }
            return sHtPending.Count > 0;
        }

        // A started script object is no longer between its start and its registration: init failed (SetLogic cleared Proxy),
        // destroyed since (ScriptProxy.Dispose cleared the proxy's Target and ObjectId but left the object's Proxy set, so
        // GetProxy never finds it again: r60 Bridgeport kept 3 cars pending for good), or registered (GetProxy finds it).
        // [verified, decompiled ScriptCore ScriptProxy.SetLogic / Dispose] In the gap the proxy's Target is the object.
        internal static bool StartResolved(object o)
        {
            IScriptLogic l = o as IScriptLogic;
            IScriptProxy p = l == null ? null : l.Proxy;
            if (p == null || !ReferenceEquals(p.Target, l)) return true;
            return Simulator.GetProxy(p.ObjectId) != null;
        }

        // LotManager's static events: re-added on every world load (removed first, so never twice)
        static void HtBuildBuyListen()
        {
            LotManager.EnteringBuildBuyMode -= HtForget;
            LotManager.ExitingBuildBuyMode -= HtForget;
            LotManager.EnteringBuildBuyMode += HtForget;
            LotManager.ExitingBuildBuyMode += HtForget;
        }

        static void HtForget()
        {
            if (!sHtMemo.Known) return;
            sHtMemo.Forget();
            PatchCounters()[2159]++;
        }

        static void HtWorldEvent(object sender, EventArgs e) { HtForget(); }

        // The old world's objects go: their pending entries could never resolve
        static void HtWorldGone(object sender, EventArgs e) { HtForget(); sHtPending.Clear(); }

        static void HtWorldLoaded(object sender, EventArgs e)
        {
            HtForget();
            try { HtBuildBuyListen(); HtCheckClasses(); } catch (Exception) { PatchCounters()[2162]++; sHtReady = false; }
        }

        static void HtPlaced(object sender, EventArgs e)
        {
            if (!sHtMemo.Known) return;
            try
            {
                World.OnObjectPlacedInLotEventArgs p = e as World.OnObjectPlacedInLotEventArgs;
                if (p == null || HtMaybeStation(p.ObjectId)) HtForget();
            }
            catch (Exception) { PatchCounters()[2162]++; HtForget(); }
        }

        // A station, or an object we cannot look up (forget: costs one EA query, never a wrong answer)
        static bool HtMaybeStation(ObjectGuid id)
        {
            if (!id.IsValid) return false;
            IScriptProxy p = Simulator.GetProxy(id) ?? Simulator.GetProxyPreInit(id);
            return p == null || p.Target == null || p.Target is IHoverTrainStation;
        }

        // An object EA's query listed is still registered with the simulator.
        static bool HtAlive(object o)
        {
            IGameObject g = o as IGameObject;
            return g != null && Simulator.GetProxy(g.ObjectId) != null;
        }

        // Build/buy and edit town: EA's query answers and nothing is stored (objects come and go natively there).
        static bool HtModeOpen() { return LotManager.sIsBuildBuyModeOn || GameStates.IsEditTownState; }

        // EA's query, as its Test and Run call it
        static IHoverTrainStation[] HtQuery() { return Sims3.Gameplay.Queries.GetObjects<IHoverTrainStation>(); }

        // The memo may answer / store now: armed, no station between start and registration, no mode open.
        static bool HtMayUse()
        {
            if (!sHtReady) return false;
            if (HtPendingOpen() || HtModeOpen()) { HtForget(); return false; }
            return true;
        }

        // ">= 2 stations" for the Test: from the memo when it is known, else EA's query (stored when the memo may be used).
        static bool HtAtLeastTwo(int[] c)
        {
            bool use = HtMayUse();
            if (use)
            {
                int m = sHtMemo.Answer(2, sHtAlive);
                if (m >= 0) { c[2157]++; return m == 1; }
            }
            c[2158]++;
            IHoverTrainStation[] s = HtQuery();
            if (use) sHtMemo.Store(s);
            return s.Length >= 2;
        }

        // ---- HoverTrainStation.OnStartupRabbitHole: every station start (new, loaded, spawned) -----------------------------
        // Instance hook; at run time `this` is the HoverTrainStation. EA's body first, unchanged.
        public class HtStationHook
        {
            public void StartHook()
            {
                StartOrig();
                try { HtStarted(this); } catch (Exception) { PatchCounters()[2162]++; sHtReady = false; sHtMemo.Forget(); }
            }
            public void StartOrig() { } // HoverTrainStation.OnStartupRabbitHole
        }

        // ---- Sim.NPCUseHoverTrain.Definition.Test -------------------------------------------------------------------------
        public class HtTestHook
        {
            public bool TestHook(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                if (a == null) return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);  // EA's own behaviour
                if (!isAutonomous || a.IsSelectable) return false;  // EA's gate, unchanged
                int[] c = PatchCounters();
                if (!ShortcutOn("hovertraintest")) { c[2158]++; return HtQuery().Length >= 2; }  // EA's body (the gate already ran)
                try { return HtAtLeastTwo(c); }
                catch (Exception) { c[2162]++; HtForget(); return HtQuery().Length >= 2; }
            }
            public bool TestOrig(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // Sim+NPCUseHoverTrain+Definition.Test
        }

        // ---- Sim.NPCUseHoverTrain.Run --------------------------------------------------------------------------------------
        // Derives from Interaction<Sim, Sim> for Actor; at run time `this` is the NPCUseHoverTrain.
        public abstract class HtRunHook : Interaction<Sim, Sim>
        {
            public bool RunHook()
            {
                if (!ShortcutOn("hovertraintest")) return RunOrig();
                // EA's body, same calls in the same order (one RandomUtil draw), with the two null dereferences answered "no"
                IHoverTrainStation[] objects = HtQuery();
                if (objects == null || objects.Length < 2) return false;
                IHoverTrainStation origin = GlobalFunctions.GetClosestObject(objects, Actor);
                int num = RandomUtil.GetInt(objects.Length - 1);
                IHoverTrainStation destination = objects[num];
                if (destination == origin) destination = objects[num + 1 == objects.Length ? 0 : num + 1];
                if (origin == null) { PatchCounters()[2161]++; return false; }   // every station charred: EA throws here
                InteractionDefinition travel = origin.GetTravelToDefinition(origin, destination, Actor);
                if (travel == null) { PatchCounters()[2161]++; return false; }
                Actor.InteractionQueue.PushAsContinuation(travel, origin, false);
                return true;
            }
            public bool RunOrig() { return false; } // Sim+NPCUseHoverTrain.Run
        }

        // ---- check -------------------------------------------------------------------------------------------------------
        // Synthetic controls on a private memo: 0, 1, 2 and 3 stations; a placement (forget, then the query lists one more);
        // a removal (a stored station gone: unknown, EA asked again); a mode open (nothing stored).
        static bool HtControls()
        {
            object s1 = new object(), s2 = new object(), s3 = new object();
            System.Collections.Generic.List<object> dead = new System.Collections.Generic.List<object>();
            Predicate<object> alive = delegate(object o) { return !dead.Contains(o); };
            AtLeastMemo m = new AtLeastMemo();
            bool ok = m.Answer(2, alive) == -1;                                  // unknown before the first query
            m.Store(new object[0]); ok &= m.Answer(2, alive) == 0;               // 0 stations
            m.Store(new object[] { s1 }); ok &= m.Answer(2, alive) == 0;          // 1 station
            m.Store(new object[] { s1, s2 }); ok &= m.Answer(2, alive) == 1;      // 2 stations
            m.Store(new object[] { s1, s2, s3 }); ok &= m.Answer(2, alive) == 1;  // 3 stations
            m.Store(new object[] { s1 }); m.Forget(); ok &= m.Answer(2, alive) == -1;   // placement: forgotten
            m.Store(new object[] { s1, s2 }); ok &= m.Answer(2, alive) == 1;      // the recount lists the new one
            dead.Add(s2); ok &= m.Answer(2, alive) == -1 && !m.Known;           // removal of a stored station: ask EA
            m.Store(new object[] { s1 }); ok &= m.Answer(2, alive) == 0;          // the recount: 1 left
            dead.Add(s1); ok &= m.Answer(2, alive) == 0;                         // fewer stays fewer when one goes
            return ok;
        }

        // HoverTrainTestCheck run: the memo's answer, stored before the check (by NPC tests since the last forget), equals EA's
        // query in the same tick (no yield between); a differing answer counts [2160]. 'result': counters + timing.
        public static object HoverTrainTestCheck(string what)
        {
            string counters = B582Counters("hovertraintest", 2157, new[] { "answered from the memo", "EA queries", "forgets", "check mismatches", "Run throws avoided", "errors" }, sHtMissing);
            if (what == "result") return counters + "; " + HtTiming();
            if (what != "run") return "hovertraintest <run|result>";
            bool on = ShortcutOn("hovertraintest");
            bool ctlMemo = HtControls(), controls = ctlMemo;
            string live, missing = "";
            bool fixedNow = false;
            try
            {
                bool knownBefore = sHtMemo.Known;
                int stored = sHtMemo.Stored;
                bool use = HtMayUse();
                int m = use ? sHtMemo.Answer(2, sHtAlive) : -1;
                IHoverTrainStation[] s = HtQuery();
                bool ea = s.Length >= 2;
                if (m >= 0 && (m == 1) != ea) PatchCounters()[2160]++;
                bool same = m < 0 || (m == 1) == ea;
                controls &= same;
                if (m < 0 && use) sHtMemo.Store(s);  // primes the memo for the next check
                fixedNow = on && use && m >= 0 && same;  // a memo answer was compared (run twice: the first run may only prime it)
                live = "live: stations " + s.Length + ", memo known before=" + knownBefore + " (stored " + stored + "), memo answer="
                    + (m < 0 ? "unknown" : (m == 1).ToString()) + ", EA=" + ea + ", same=" + same + ", mode open=" + HtModeOpen() + ", pending " + sHtPending.Count;
            }
            catch (Exception e) { missing = "threw " + e.GetType().Name; live = "live: threw"; }
            return B582Line("hovertraintest", on, "memo answers the >= 2 stations test", fixedNow,
                "0/1/2/3 stations, placement, removal=" + ctlMemo + ", " + live, controls, EpJoin(sHtMissing, missing), counters);
        }

        static string HtTiming()
        {
            try
            {
                int n = HtQuery().Length;
                uint t0 = SrcNowUs();
                for (int i = 0; i < 20; i++) HtQuery();
                uint t1 = SrcNowUs();
                AtLeastMemo m = new AtLeastMemo();
                m.Store(HtQuery());
                int hits = 0;
                for (int i = 0; i < 20; i++) if (m.Answer(2, sHtAlive) >= 0) hits++;
                uint t2 = SrcNowUs();
                return "timing: EA query " + ((t1 - t0) / 20u) + " us, memo answer " + ((t2 - t1) / 20u) + " us (" + hits + "/20 from the memo; stations " + n
                    + ", world objects " + Sims3.Gameplay.Queries.GetObjects<IGameObject>().Length + ")";
            }
            catch (Exception e) { return "timing: threw " + e.GetType().Name; }
        }


        // live path of the check (CheckVerdict.cs CkLive): hook swaps in place
        static readonly int sLive_hovertraintest = LiveAdd("hovertraintest", () => EpJoin(EpJoin(EpLive(typeof(HtRunHook), 1), EpLive(typeof(HtStationHook), 0)), EpLive(typeof(HtTestHook), 1)));
    }
}
