using System;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;

namespace Ts3Fixes
{
    // Game-independent part of newcartest: "does any listed object match?" for a match that can turn from true to false
    // as time passes but never back (a car's new-car smell). It keeps only the objects that could still match when EA's
    // query was stored (the new cars), with the clock reading and the tuning at that time. While nothing was added, the
    // answer is "any kept object, still listed, matches live". Removal needs no event (a gone object is skipped); a clock
    // that went back or a changed tuning value means unknown. Additions must call Forget.
    public sealed class FadingSubsetMemo
    {
        object[] mKeep;
        bool mKnown;
        long mAt;
        float mTuning;

        public bool Known { get { return mKnown; } }
        public int Kept { get { return mKnown ? mKeep.Length : -1; } }

        public void Forget() { mKnown = false; mKeep = null; }

        // all: what EA's query listed; mayMatch: the part of the match that only fades with time (checked now)
        public void Store(object[] all, Predicate<object> mayMatch, long now, float tuning)
        {
            System.Collections.Generic.List<object> keep = new System.Collections.Generic.List<object>();
            if (all != null)
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && mayMatch(all[i])) keep.Add(all[i]);
            mKeep = keep.ToArray();
            mAt = now;
            mTuning = tuning;
            mKnown = true;
        }

        // 1: a kept, live object matches; 0: none does; -1: unknown (never stored, forgotten, clock back, tuning changed)
        public int Answer(Predicate<object> match, Predicate<object> alive, long now, float tuning)
        {
            if (!mKnown) return -1;
            if (now < mAt || tuning != mTuning) { Forget(); return -1; }
            for (int i = 0; i < mKeep.Length; i++)
                if (alive(mKeep[i]) && match(mKeep[i])) return 1;  // gone first: EA never calls the match on a gone car
            return 0;
        }
    }

    // newcartest (kNewCarTest; docs/598 §6). [verified, decompiled] SocialTest.TestHasNewCar and TestHasExpensiveNewCar are
    // the ProcTests of four autonomous Friendly socials (Enthuse About New / Fast / Technologically Advanced Car: TestHasNewCar;
    // Expensive Car: TestHasExpensiveNewCar; OriginalSocialDataEP4: Available auto=True, ages T-E, actor not Grumpy, the last
    // three need Daredevil / ComputerWhiz / Snob). ActionData.Test runs the ProcTest for every actor / target pair that passes
    // those gates, each time SocialInteractionA.Definition.Test or GetSocialsForSTC builds the socials of a conversation. Each
    // run lists every car in the world (Queries.GetObjects<CarOwnable>()) and returns true when one has the actor's home lot
    // as owner and IsNewCar (TimeOfPurchase within BuffNewCarSmell.DaysOfNewCarSmell days), plus Value > the threshold for
    // the expensive test. Fix: the same answer from FadingSubsetMemo, which keeps only the cars that were new when EA's query
    // last ran. [verified, decompiled] IsNewCar only turns false as the clock runs: TimeOfPurchase is written only in
    // CarOwnable.OnCreation (= now, the newest value possible), and the owner lot and Value are checked live on each kept car.
    // A car that was not new at store time cannot be new later (same TimeOfPurchase, clock forward, same tuning: checked).
    // Additions forget the memo. [verified, decompiled] Every car script object starts in CarOwnable.OnStartup (GameObject.Init,
    // both arms; OnCreation runs just before it on a new object), and the six EA classes that override OnStartup (PriusC,
    // CarTwizy, CarUFO, SirenEnabledVehicle, FireEngine, HoverCarExpensive) call base.OnStartup unconditionally. As in
    // hovertraintest the started car stays pending (no store) until GetProxy finds it. Another CarOwnable subclass that
    // declares its own OnStartup (a mod) keeps the memo off. CarOwnable.ImportContent forgets too (an imported car could carry
    // another TimeOfPurchase). Belt and braces: build/buy enter / exit, world load / quit; nothing stored in build/buy or edit
    // town. Removals: a kept car whose proxy is gone is skipped. [inferred: the query's set is the registered CarOwnable script
    // objects, inventory cars included, so moving a car into or out of an inventory changes nothing; the check compares the
    // memo with EA's own test for every Sim live]
    // Household rule: household, selected and unknown-scope actors (Fixes.HouseholdExempt) always run EA's test.
    // Counters: [2747] tests answered from the memo, [2748] tests that ran EA's (switch off: every call, the call rate),
    // [2749] memo forgets, [2750] check mismatches (memo vs EA, must stay 0), [2751] errors, [2752] ImportContent forgets.
    public static partial class Fixes
    {
        static readonly FadingSubsetMemo sNcMemo = new FadingSubsetMemo();
        static readonly Predicate<object> sNcIsNew = NcIsNew;
        static readonly Predicate<object> sNcMatch = NcMatch;
        static readonly Predicate<object> sNcOwnedAlive = NcOwnedAlive;
        static readonly Predicate<object> sNcAlive = HtAlive;   // HoverTrain598.cs: still registered with the simulator
        static readonly System.Collections.Generic.List<object> sNcPending = new System.Collections.Generic.List<object>();
        static bool sNcReady, sNcHooked;
        static string sNcMissing = "";
        static int sNcScanned = -1;
        static string sNcForeign;
        static Lot sNcHome;        // the actor's home lot for NcMatch (single sim thread, set right before use)
        static bool sNcExpensive;  // NcMatch adds the Value test (TestHasExpensiveNewCar)

        static void ApplyNewCarTest()
        {
            Type[] test = new Type[] { typeof(Sim), typeof(Sim), typeof(ActiveTopic), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() };
            bool ok = Patch(typeof(CarOwnable), "OnStartup", Type.EmptyTypes, typeof(NcCarHook), "StartHook", "StartOrig");
            if (!ok) sNcMissing = EpJoin(sNcMissing, "CarOwnable.OnStartup hook (memo never used)");
            bool imp = Patch(typeof(CarOwnable), "ImportContent", new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamReader) },
                typeof(NcCarHook), "ImportHook", "ImportOrig");
            if (!imp) { ok = false; sNcMissing = EpJoin(sNcMissing, "CarOwnable.ImportContent hook (memo never used)"); }
            try
            {
                World.OnWorldLoadStartedEventHandler += NcWorldGone;
                World.OnWorldLoadFinishedEventHandler += NcWorldLoaded;
                World.OnWorldQuitEventHandler += NcWorldGone;
                World.OnEnterNotInWorldEventHandler += NcWorldEvent;
                World.OnLeaveNotInWorldEventHandler += NcWorldEvent;
                NcBuildBuyListen();
            }
            catch (Exception e) { ok = false; sNcMissing = EpJoin(sNcMissing, "events threw " + e.GetType().Name); }
            Patch(typeof(SocialTest), "TestHasNewCar", test, typeof(NcTestHook), "NewHook", "NewOrig");
            Patch(typeof(SocialTest), "TestHasExpensiveNewCar", test, typeof(NcTestHook), "ExpHook", "ExpOrig");
            sNcHooked = ok;
            if (sNcMissing.Length > 0) AppliedLog().Add("newcartest: " + sNcMissing);
            NcCheckClasses();
        }

        // The memo arms only when every CarOwnable class starts through the hooked CarOwnable.OnStartup: EA's own classes
        // (verified above) and any subclass that does not declare its own OnStartup.
        static readonly string[] sNcEaOverrides = {
            "Sims3.Store.Objects.PriusC", "Sims3.Gameplay.Objects.Vehicles.CarTwizy", "Sims3.Gameplay.Objects.Vehicles.CarUFO",
            "Sims3.Gameplay.Objects.Vehicles.SirenEnabledVehicle", "Sims3.Gameplay.Objects.Vehicles.FireEngine",
            "Sims3.Gameplay.Objects.Vehicles.HoverCarExpensive" };

        static void NcCheckClasses()
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            if (all.Length != sNcScanned) { sNcForeign = NcForeignClass(all); sNcScanned = all.Length; }
            sNcReady = sNcHooked && sNcForeign == null;
            if (!sNcReady) sNcMemo.Forget();
            if (sNcForeign != null && sNcMissing.IndexOf("car class") < 0)
            {
                sNcMissing = EpJoin(sNcMissing, "other car class " + sNcForeign + " (memo never used)");
                AppliedLog().Add("newcartest: memo off, other car class " + sNcForeign);
            }
        }

        static string NcForeignClass(Assembly[] all)
        {
            const BindingFlags decl = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (Assembly a in all)
            {
                Type[] ts;
                try { ts = a.GetTypes(); } catch (Exception) { continue; }
                foreach (Type t in ts)
                {
                    if (t == null || t == typeof(CarOwnable) || !t.IsSubclassOf(typeof(CarOwnable))) continue;
                    if (t.GetMethod("OnStartup", decl) == null) continue;
                    if (Array.IndexOf(sNcEaOverrides, t.FullName) >= 0 && a == t.Assembly && NcEaAssembly(a)) continue;
                    return t.FullName;
                }
            }
            return null;
        }

        static bool NcEaAssembly(Assembly a)
        {
            string n = a.GetName().Name;
            return n == "Sims3GameplaySystems" || n == "Sims3GameplayObjects" || n == "Sims3StoreObjects";
        }

        static void NcStarted(object car)
        {
            NcForget();
            if (car != null && !sNcPending.Contains(car)) sNcPending.Add(car);
        }

        // True while a started car is not yet registered (one pass, drops the resolved ones: registered, init failed, or
        // destroyed since: StartResolved, HoverTrain598.cs)
        static bool NcPendingOpen()
        {
            for (int i = sNcPending.Count - 1; i >= 0; i--)
            {
                if (StartResolved(sNcPending[i])) sNcPending.RemoveAt(i);
            }
            return sNcPending.Count > 0;
        }

        static void NcBuildBuyListen()
        {
            LotManager.EnteringBuildBuyMode -= NcForget;
            LotManager.ExitingBuildBuyMode -= NcForget;
            LotManager.EnteringBuildBuyMode += NcForget;
            LotManager.ExitingBuildBuyMode += NcForget;
        }

        static void NcForget()
        {
            if (!sNcMemo.Known) return;
            sNcMemo.Forget();
            PatchCounters()[2749]++;
        }

        static void NcWorldEvent(object sender, EventArgs e) { NcForget(); }

        static void NcWorldGone(object sender, EventArgs e) { NcForget(); sNcPending.Clear(); }

        static void NcWorldLoaded(object sender, EventArgs e)
        {
            NcForget();
            try { NcBuildBuyListen(); NcCheckClasses(); } catch (Exception) { PatchCounters()[2751]++; sNcReady = false; }
        }

        static float NcDays() { return BuffNewCarSmell.DaysOfNewCarSmell; }

        static bool NcIsNew(object o)
        {
            CarOwnable c = o as CarOwnable;
            return c != null && c.IsNewCar;
        }

        // The memo's "still listed" test, owner lot first: a plain field read (GameObject.GetOwnerLot, no side effect), so the
        // native proxy lookup runs only for cars of the actor's home lot. With most cars new (Bridgeport: 50 of 50 kept) this
        // keeps an answer at a few field reads; EA's own calls (IsNewCar, Value) still never run on a gone car.
        static bool NcOwnedAlive(object o)
        {
            CarOwnable c = o as CarOwnable;
            return c != null && c.GetOwnerLot() == sNcHome && sNcAlive(o);
        }

        // EA's per-car test, same order (owner lot, then IsNewCar, then Value for the expensive test)
        static bool NcMatch(object o)
        {
            CarOwnable c = o as CarOwnable;
            if (c == null || c.GetOwnerLot() != sNcHome || !c.IsNewCar) return false;
            return !sNcExpensive || (float)c.Value > CarOwnable.kExpensiveCarTestThreshold;
        }

        static bool NcMayUse()
        {
            if (!sNcReady) return false;
            if (NcPendingOpen() || HtModeOpen()) { NcForget(); return false; }
            return true;
        }

        // The memo's answer, or -1 (unknown). Sets sNcHome / sNcExpensive for NcMatch.
        static int NcMemoAnswer(Sim actor, bool expensive)
        {
            sNcHome = actor.LotHome;
            sNcExpensive = expensive;
            return sNcMemo.Answer(sNcMatch, sNcOwnedAlive, SimClock.CurrentTicks, NcDays());
        }

        // EA's loop over a listed array (same as its body)
        static bool NcEaLoop(CarOwnable[] all, Sim actor, bool expensive)
        {
            sNcHome = actor.LotHome;
            sNcExpensive = expensive;
            foreach (CarOwnable c in all)
                if (NcMatch(c)) return true;
            return false;
        }

        static bool NcAnswer(Sim actor, bool expensive, int[] c)
        {
            bool use = NcMayUse();
            if (use)
            {
                int m = NcMemoAnswer(actor, expensive);
                if (m >= 0) { c[2747]++; return m == 1; }
            }
            c[2748]++;
            CarOwnable[] all = Sims3.Gameplay.Queries.GetObjects<CarOwnable>();
            if (use) sNcMemo.Store(all, sNcIsNew, SimClock.CurrentTicks, NcDays());
            return NcEaLoop(all, actor, expensive);
        }

        // ---- CarOwnable.OnStartup / ImportContent ----------------------------------------------------------------------------
        // Instance hooks; at run time `this` is the car. EA's body first, unchanged.
        public class NcCarHook
        {
            public void StartHook()
            {
                StartOrig();
                try { NcStarted(this); } catch (Exception) { PatchCounters()[2751]++; sNcReady = false; sNcMemo.Forget(); }
            }
            public void StartOrig() { } // CarOwnable.OnStartup

            public bool ImportHook(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader)
            {
                bool r = ImportOrig(resKeyTable, objIdTable, reader);
                TcnAfterImport((CarOwnable)(object)this, GameStates.IsTravelling);   // travelcarnew (TravelCarNew.cs): a car rebuilt on travel is not new
                if (sNcMemo.Known) PatchCounters()[2752]++;
                NcForget();
                return r;
            }
            public bool ImportOrig(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader) { return false; } // CarOwnable.ImportContent
        }

        // ---- SocialTest.TestHasNewCar / TestHasExpensiveNewCar -------------------------------------------------------------
        public class NcTestHook
        {
            public static bool NewHook(Sim actor, Sim target, ActiveTopic topic, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                if (!NcUse(actor)) { PatchCounters()[2748]++; return NewOrig(actor, target, topic, isAutonomous, ref greyedOutTooltipCallback); }
                try { return NcAnswer(actor, false, PatchCounters()); }
                catch (Exception) { PatchCounters()[2751]++; NcForget(); return NewOrig(actor, target, topic, isAutonomous, ref greyedOutTooltipCallback); }
            }
            public static bool NewOrig(Sim actor, Sim target, ActiveTopic topic, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // SocialTest.TestHasNewCar

            public static bool ExpHook(Sim actor, Sim target, ActiveTopic topic, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                if (!NcUse(actor)) { PatchCounters()[2748]++; return ExpOrig(actor, target, topic, isAutonomous, ref greyedOutTooltipCallback); }
                try { return NcAnswer(actor, true, PatchCounters()); }
                catch (Exception) { PatchCounters()[2751]++; NcForget(); return ExpOrig(actor, target, topic, isAutonomous, ref greyedOutTooltipCallback); }
            }
            public static bool ExpOrig(Sim actor, Sim target, ActiveTopic topic, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // SocialTest.TestHasExpensiveNewCar

            // EA's own test for: switch off, a null actor (EA's behaviour), household / selected / unknown-scope actors
            static bool NcUse(Sim actor)
            {
                return ShortcutOn("newcartest") && actor != null && !HouseholdExempt(actor);
            }
        }

        // ---- check ---------------------------------------------------------------------------------------------------------
        // Synthetic controls on a private memo: no cars, an old car, a new car of the lot, the car aging out, a new car gone,
        // a car added (forget, recount), the clock going back, the tuning changed.
        static bool NcControls()
        {
            object home = new object(), other = new object();
            object oldCar = new object(), newCar = new object(), otherCar = new object(), added = new object();
            System.Collections.Generic.Dictionary<object, long> bought = new System.Collections.Generic.Dictionary<object, long>();
            System.Collections.Generic.Dictionary<object, object> owner = new System.Collections.Generic.Dictionary<object, object>();
            bought[oldCar] = 0; owner[oldCar] = home;
            bought[newCar] = 90; owner[newCar] = home;
            bought[otherCar] = 95; owner[otherCar] = other;
            bought[added] = 99; owner[added] = home;
            long now = 100, days = 20;  // "new" = bought within 20 ticks
            System.Collections.Generic.List<object> dead = new System.Collections.Generic.List<object>();
            Predicate<object> isNew = delegate(object o) { return now - bought[o] <= days; };
            Predicate<object> match = delegate(object o) { return owner[o] == home && now - bought[o] <= days; };
            Predicate<object> alive = delegate(object o) { return !dead.Contains(o); };
            FadingSubsetMemo m = new FadingSubsetMemo();
            bool ok = m.Answer(match, alive, now, 1f) == -1;                                     // unknown before the first query
            m.Store(new object[0], isNew, now, 1f); ok &= m.Answer(match, alive, now, 1f) == 0;   // no cars
            m.Store(new object[] { oldCar }, isNew, now, 1f); ok &= m.Kept == 0 && m.Answer(match, alive, now, 1f) == 0;  // an old car: not kept
            m.Store(new object[] { oldCar, newCar, otherCar }, isNew, now, 1f);
            ok &= m.Kept == 2 && m.Answer(match, alive, now, 1f) == 1;                           // a new car of the lot
            now = 111; ok &= m.Answer(match, alive, now, 1f) == 0;                               // it aged out (other lot's still new)
            now = 100; dead.Add(newCar); ok &= m.Answer(match, alive, now, 1f) == 0;              // gone: not counted
            m.Forget(); ok &= m.Answer(match, alive, now, 1f) == -1;                             // a car added: forgotten
            m.Store(new object[] { oldCar, otherCar, added }, isNew, now, 1f); ok &= m.Answer(match, alive, now, 1f) == 1;  // recount
            ok &= m.Answer(match, alive, now - 1, 1f) == -1 && !m.Known;                         // clock went back
            m.Store(new object[] { added }, isNew, now, 1f); ok &= m.Answer(match, alive, now, 2f) == -1;  // tuning changed
            return ok;
        }

        // NewCarTestCheck run: for every Sim in the world, the memo's answer (both tests) equals EA's own test in the same tick
        // (no yield between); a differing answer counts [2750]. 'result': counters + timing.
        public static object NewCarTestCheck(string what)
        {
            string counters = B582Counters("newcartest", 2747, new[] { "answered from the memo", "EA tests", "forgets", "check mismatches", "errors", "import forgets" }, sNcMissing);
            if (what == "result") return counters + "; " + NcTiming();
            if (what != "run") return "newcartest <run|result>";
            bool on = ShortcutOn("newcartest");
            bool ctl = NcControls(), controls = ctl;
            string live, missing = "";
            bool fixedNow = false;
            try
            {
                bool knownBefore = sNcMemo.Known;
                int kept = sNcMemo.Kept, sims = 0, fromMemo = 0, diff = 0, eaTrue = 0;
                bool use = NcMayUse();
                if (use && !sNcMemo.Known) sNcMemo.Store(Sims3.Gameplay.Queries.GetObjects<CarOwnable>(), sNcIsNew, SimClock.CurrentTicks, NcDays());
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s == null || s.HasBeenDestroyed || s.SimDescription == null) continue;
                    sims++;
                    for (int k = 0; k < 2; k++)
                    {
                        GreyedOutTooltipCallback cb = null;
                        bool ea = k == 0 ? NcTestHook.NewOrig(s, null, null, true, ref cb) : NcTestHook.ExpOrig(s, null, null, true, ref cb);
                        if (ea) eaTrue++;
                        int m = use ? NcMemoAnswer(s, k == 1) : -1;
                        if (m < 0) continue;
                        fromMemo++;
                        if ((m == 1) != ea) { diff++; PatchCounters()[2750]++; }
                    }
                }
                controls &= diff == 0;
                fixedNow = on && use && fromMemo > 0 && diff == 0;
                live = "live: Sims " + sims + ", answers from the memo " + fromMemo + ", EA true " + eaTrue + ", differ " + diff
                    + ", memo known before=" + knownBefore + " (new cars kept " + kept + "), mode open=" + HtModeOpen() + ", pending " + sNcPending.Count + NcPendingWhy();
            }
            catch (Exception e) { missing = "threw " + e.GetType().Name; live = "live: threw"; }
            return B582Line("newcartest", on, "memo answers TestHasNewCar / TestHasExpensiveNewCar as EA", fixedNow,
                "no / old / new / aged / gone / added car, clock back, tuning=" + ctl + ", " + live, controls, EpJoin(sNcMissing, missing), counters);
        }

        // Why each pending car is still pending: target still this car, object id, GetProxy / GetProxyPreInit found, in world
        static string NcPendingWhy()
        {
            if (sNcPending.Count == 0) return "";
            System.Text.StringBuilder b = new System.Text.StringBuilder(" [");
            for (int i = 0; i < sNcPending.Count && i < 5; i++)
            {
                IScriptLogic l = sNcPending[i] as IScriptLogic;
                IScriptProxy p = l == null ? null : l.Proxy;
                CarOwnable c = sNcPending[i] as CarOwnable;
                if (i > 0) b.Append("; ");
                if (p == null) { b.Append("no proxy"); continue; }
                b.Append(sNcPending[i].GetType().Name).Append(" target=").Append(ReferenceEquals(p.Target, l)).Append(" id=").Append(p.ObjectId.Value.ToString("X"))
                    .Append(" proxy=").Append(Simulator.GetProxy(p.ObjectId) != null).Append(" pre=").Append(Simulator.GetProxyPreInit(p.ObjectId) != null)
                    .Append(" inWorld=").Append(c != null && c.InWorld).Append(" inv=").Append(c != null && c.InInventory);
            }
            return b.Append("]").ToString();
        }

        static string NcTiming()
        {
            try
            {
                Sim s = null;
                foreach (Sim x in Sims3.Gameplay.Queries.GetObjects<Sim>()) if (x != null && !x.HasBeenDestroyed && x.SimDescription != null) { s = x; break; }
                if (s == null) return "timing: no Sim";
                CarOwnable[] cars = Sims3.Gameplay.Queries.GetObjects<CarOwnable>();
                GreyedOutTooltipCallback cb = null;
                uint t0 = SrcNowUs();
                for (int i = 0; i < 20; i++) NcTestHook.NewOrig(s, null, null, true, ref cb);
                uint t1 = SrcNowUs();
                FadingSubsetMemo m = new FadingSubsetMemo();
                m.Store(cars, sNcIsNew, SimClock.CurrentTicks, NcDays());
                uint t2 = SrcNowUs();
                sNcHome = s.LotHome; sNcExpensive = false;
                int hits = 0;
                for (int i = 0; i < 20; i++) if (m.Answer(sNcMatch, sNcOwnedAlive, SimClock.CurrentTicks, NcDays()) >= 0) hits++;
                uint t3 = SrcNowUs();
                return "timing: EA test " + ((t1 - t0) / 20u) + " us, memo answer " + ((t3 - t2) / 20u) + " us (" + hits + "/20 from the memo; cars "
                    + cars.Length + ", new cars kept " + m.Kept + ")";
            }
            catch (Exception e) { return "timing: threw " + e.GetType().Name; }
        }
    }
}
