using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Unseen script throttle (docs/144): per-Sim script updates that only high-LOD Sims run (SimUpdate.Simulate) run
    // less often for Sims the player cannot see. EA's own autonomy LOD already keeps most far Sims out of this path;
    // what is left are high-LOD Sims outside the view (active lot while the household is away, hosted guests, resort
    // workers, daycare children, the 60-tick LOD cache after a Sim walks off screen).
    //   unseenenv     : SimUpdate.CalculateSimEnvironmentScore runs one call in N for an unseen Sim (every 2N sim-minutes
    //                   instead of 2); a forced update (Sim.UpdateEnvironment) always runs.
    //   unseenmotives : Autonomy.Update (motives, buffs, mood, skills, careers, relationships, all integrators of the
    //                   sim-minutes since the last update) waits until N sim-minutes have passed, then integrates the
    //                   whole gap in one call. Only the SimUpdate (high-LOD) call; RunAutonomy's refresh always runs.
    //   unseenpark    : an idle unseen low-LOD Sim stays out of AutonomyManager's queue until kUnseenParkMinutes sim-minutes
    //                   after its last search (docs/243 D1, §9). Autonomy.mLastTime is that clock: on the low-LOD path only
    //                   RunAutonomy's refresh moves it, so nothing new is kept and a search not run is not owed later.
    //   unseenprobe   : classify and count only (no skips), for the A/B and the share measurement.
    // Unseen = not selectable, not in the active household, out of the camera's reach and 80 m from every
    // active-household Sim (FarFromViewers, as the trip shortcuts), and not on screen. A seen Sim always runs.
    // Counters: [146-148] environment calls classified / unseen / skipped, [149-151] motive updates classified / unseen /
    // skipped, [154] unseen motive updates run that left a Sim at <= -95 hunger, energy, bladder or hygiene,
    // [155] gate tables restarted (full), [225] most unseen motive catch-ups in one sim tick (cap UnseenMath.kCatchUpsPerTick),
    // [226] catch-ups held a tick by the cap, [227] longest catch-up call (us). [152-153] belong to the broadcaster rule in BroadcasterPulseHook (Fixes.cs).
    // [778-784] low-LOD refreshes of unseen Sims (docs/245), filled by unseenprobe or unseenpark: [778] run, [779] us in those
    // runs, [780-784] gap since the last refresh in sim-minutes: < 1, 1-5, 5-15, 15-30, >= 30. With unseenprobe alone the
    // buckets under 30 are the searches unseenpark would hold at the default N; with unseenpark on they should empty.
    // [818-823] unseenpark: [818] queue entries held (one per idle poll, not per search), [819] let through inside the window
    // because the Sim is seen (snap-back), [820] let through inside the window as exempt, low-LOD searches queued outside
    // the window [821] unseen, [822] seen, [823] non-household Sims walking onto a seen lot (both arms: the visitor bar),
    // [824] most snap releases in one tick, [825] polls of a seen held Sim deferred to its stagger slot (kUnseenParkStaggerTicks).
    public static partial class Fixes
    {
        static readonly EveryN sEnvGate = new EveryN(), sBroadcastGate = new EveryN(), sCarGate = new EveryN();
        static FieldInfo sSimUpdateSim, sAutonomyLastTime;
        const float kMotiveCapMin = 30f; // well under Motive.kMaxUpdateHoursBeforeAutoSatisfy (60 sim-minutes)
        static readonly TickCap sCatchUps = new TickCap();
        static StopWatch sCatchUpWatch, sRefreshWatch;

        static void ApplyUnseenThrottle()
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            sSimUpdateSim = typeof(SimUpdate).GetField("mSim", f);
            sAutonomyLastTime = typeof(Autonomy).GetField("mLastTime", f);
            if (sSimUpdateSim != null) Patch(typeof(SimUpdate), "CalculateSimEnvironmentScore", typeof(EnvScoreHook), "ScoreHook", "ScoreOrig");
            else AppliedLog().Add("SimUpdate.CalculateSimEnvironmentScore: no mSim field");
            if (sAutonomyLastTime != null) Patch(typeof(Autonomy), "Update", typeof(AutonomyUpdateHook), "UpdateHook", "UpdateOrig");
            else AppliedLog().Add("Autonomy.Update: no mLastTime field");
            Patch(typeof(StreetParking), "GetParkingSpotForCar", typeof(ParkingSpotHook), "SpotHook", "SpotOrig");
            if (sAutonomyLastTime != null) Patch(typeof(AutonomyManager), "Add", typeof(UnseenParkHook), "AddHook", "AddOrig");
            World.OnWorldLoadFinishedEventHandler += delegate { StartParkArrivals(); };
        }

        static int UnseenEvery() { return Math.Max(1, Instantiator.kUnseenThrottleEvery); }

        // The trip shortcuts' rule (TownieUnseen) without its posture and follower tests, which only matter for moving a Sim.
        static bool SimUnseen(Sim s)
        {
            long t = UcBegin(kUcSimUnseen, s != null ? s.ObjectId.Value : 0);  // docs/227 probe
            bool r = SimUnseenBody(s);
            UcEnd(kUcSimUnseen, t);
            return r;
        }
        static bool SimUnseenBody(Sim s)
        {
            if (s == null || s.IsSelectable || s.Household == Household.ActiveHousehold) return false;
            int b = UtbBits(s);  // docs/227 §6 central table
            if (b >= 0) return sUtbCheck ? UtbCompare(UtbUnseen(b), FarFromViewers(s.Position) && !UcOnScreen(s)) : UtbUnseen(b);
            if (!FarFromViewers(s.Position)) return false;
            return !UcOnScreen(s);
        }

        static void NoteGateClears(int[] c) { c[155] = sEnvGate.Clears + sBroadcastGate.Clears + sCarGate.Clears; }

        // SimUpdate has no parameterless constructor, so the hook is a plain class (as BroadcasterPulseHook).
        public class EnvScoreHook
        {
            public void ScoreHook()
            {
                bool on = ShortcutOn("unseenenv");
                if (!on && !ShortcutOn("unseenprobe")) { ScoreOrig(); return; }
                Sim s = sSimUpdateSim.GetValue((SimUpdate)(object)this) as Sim;
                int[] c = PatchCounters();
                c[146]++;
                if (s == null) { ScoreOrig(); return; }
                int key = UnseenMath.Key(s.ObjectId.Value);
                if (s.UpdateEnvironment || !SimUnseen(s)) { sEnvGate.Seen(key); ScoreOrig(); return; }
                c[147]++;
                if (on)
                {
                    long ut = UcBegin(kUcGate, 0);  // docs/227 probe
                    bool run = sEnvGate.Unseen(key, UnseenEvery());
                    UcEnd(kUcGate, ut);
                    NoteGateClears(c);
                    if (!run) { c[148]++; return; } // score, environment buffs and events stay as the last run left them
                }
                ScoreOrig();
            }
            public void ScoreOrig() { } // receives SimUpdate.CalculateSimEnvironmentScore's original header
        }

        // unseencars (docs/143 §8.3): ambient traffic. CarNpcWithNoDriver.LiteDriver (one script task per driverless traffic
        // car) loops: pick a random commercial lot, ask its StreetParking for a spot, and when the spot is over 10 m away
        // plan a car-only route (CarRoutingComponent.CreateRoute, the 15 ms/s of unseen car-only plans docs/143 measured)
        // and drive it; then free the spot and sleep kCarWaitingTimeRange sim-minutes. When no spot is free the game's own
        // loop skips the drive and sleeps. For a car out of view going to a spot out of view this hook gives that answer
        // on (n-1) of n rounds per car (staggered by car): the car stays parked where it is for one more wait, no route
        // is planned, nothing is moved or teleported. (The dropped traffic shortcut of docs/04 placed cars at the spot
        // instead of driving them; this one never moves a car.) Only LiteDriver's own call (the current script task),
        // never TrafficManager's spawn query or the NPC/food/ice-cream/fire trucks. A car or spot in view always drives.
        // Counters: [197] LiteDriver spot asks, [198] spots granted for an unseen car to an unseen spot, [199] of those
        // turned down (car stays parked), [200] asks seen (car or spot in view, or within reach of the household).
        public class ParkingSpotHook
        {
            public bool SpotHook(Car car, ref Matrix44 mat)
            {
                if (!(car is CarNpcWithNoDriver) || !ShortcutOn("unseencars") || !InLiteDriver()) return SpotOrig(car, ref mat);
                int[] c = PatchCounters();
                c[197]++;
                if (!SpotOrig(car, ref mat)) return false;
                int key = UnseenMath.Key(car.ObjectId.Value);
                if (!FarFromViewers(car.Position) || UcOnScreen(car) || !FarFromViewers(mat.pos.V3))
                {
                    sCarGate.Seen(key);
                    c[200]++;
                    return true;
                }
                c[198]++;
                long ut = UcBegin(kUcGate, 0);  // docs/227 probe
                bool run = sCarGate.Unseen(key, Math.Max(1, Instantiator.kUnseenCarEvery));
                UcEnd(kUcGate, ut);
                if (run) return true;
                c[199]++;
                ((StreetParking)(object)this).FreeParkingSpotForCar(car); // as if none was free: the reservation goes back
                return false;
            }
            public bool SpotOrig(Car car, ref Matrix44 mat) { return false; } // receives StreetParking.GetParkingSpotForCar's original header

            static bool InLiteDriver()
            {
                IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                object t = p != null ? p.Target : null;
                return t != null && t.GetType().Name == "LiteDriver";
            }
        }

        // unseenpark (docs/243 D1, §9). AutonomyManager.Add's only callers are the two idle-path sites in Sim.DoInteraction, each
        // followed by the Sim's own Simulator.Sleep. Add and this hook never yield, so neither sits on a saved stack; a held Sim
        // just loops EA's idle path and asks again. Pushes never come here: a queued interaction runs before the idle path.
        // Any doubt lets the search through: seen, gone, not live, classifier error, no clock, a clock ahead of the game.
        public static class UnseenParkHook
        {
            static long sParkBurstTick = -1; // tick of the latest snap release and how many went in it: [824] keeps the max
            static int sParkBurstN;
            public static void AddHook(Autonomy a)
            {
                // IsRunningLocalAutonomy picks the high-LOD queue in Enqueue; DoInteraction has just refreshed it
                if (a.IsRunningLocalAutonomy || !ShortcutOn("unseenpark")) { AddOrig(a); return; }
                Sim s = a.Actor;
                int[] c = PatchCounters();
                int why = SafeUfWhy(s);
                float last = (float)sAutonomyLastTime.GetValue(a);
                bool hold = UnseenMath.ParkHold(last, SimClock.ElapsedTime(TimeUnit.Minutes), Instantiator.kUnseenParkMinutes);
                switch (UnseenMath.ParkVerdict(why == 0, hold, why == 0 && hold && ParkExempt(a, s)))
                {
                    case UnseenMath.kParkHold: c[818]++; return;
                    case UnseenMath.kParkSnap:
                        // seen while held: wait for this Sim's slot so a camera jump does not release them all together
                        long tick = SimClock.CurrentTicks;
                        if (!UnseenMath.ParkStaggerPass(tick, s.SimDescription.SimDescriptionId, Instantiator.kUnseenParkStaggerTicks)) { c[825]++; return; }
                        c[819]++;
                        if (tick == sParkBurstTick) sParkBurstN++; else { sParkBurstTick = tick; sParkBurstN = 1; }
                        if (sParkBurstN > c[824]) c[824] = sParkBurstN;
                        break;
                    case UnseenMath.kParkExempt: c[820]++; break;
                    case UnseenMath.kParkUnseen: c[821]++; break;
                    default: c[822]++; break;
                }
                AddOrig(a);
            }
            public static void AddOrig(Autonomy a) { } // receives AutonomyManager.Add's original header

            // Service Sims and role Sims skip EA's own idle delay, so they stay on it. A situation (party, visit, date, group,
            // wedding) drives its Sims partly by autonomy. Pregnancy is alarm-driven; exempt so no pregnant Sim idles for us.
            static bool ParkExempt(Autonomy a, Sim s)
            {
                SimDescription d = s.SimDescription;
                return s.Service != null || d.HasActiveRole || d.IsPregnant || a.SituationComponent == null || a.SituationComponent.Situations.Count > 0;
            }
        }

        // Fewer searches can mean fewer visitors (docs/243 §5): counted on both arms, with no effect on the game.
        static EventListener sParkArrive;
        static void StartParkArrivals()
        {
            if (sParkArrive != null) EventTracker.RemoveListener(sParkArrive);
            sParkArrive = EventTracker.AddListener(EventTypeId.kEventSimMovedToLot, OnParkArrive);
        }
        static ListenerAction OnParkArrive(Event e)
        {
            try
            {
                Sim s = e.Actor as Sim;
                Lot lot = e.TargetObject as Lot;
                if (s != null && lot != null && !lot.IsWorldLot && s.Household != Household.ActiveHousehold && UfLotSeen(lot)) PatchCounters()[823]++;
            }
            catch (Exception) { }
            return ListenerAction.Keep;
        }

        // Autonomy has no parameterless constructor either.
        public class AutonomyUpdateHook
        {
            public void UpdateHook()
            {
                bool on = ShortcutOn("unseenmotives"), park = ShortcutOn("unseenpark"), probe = ShortcutOn("unseenprobe");
                if (!on && !park && !probe) { UpdateOrig(); return; }
                Autonomy a = (Autonomy)(object)this;
                Sim s = a.Actor;
                if (s == null) { UpdateOrig(); return; }
                // RunAutonomy calls Update only for low-LOD Sims, to refresh motives before it scores: counted, never skipped
                if (!a.GetCachedHighLODValue) { LowLodRefresh(a, s); return; }
                if (!on && !probe) { UpdateOrig(); return; }
                int[] c = PatchCounters();
                c[149]++;
                if (!SimUnseen(s)) { UpdateOrig(); return; }
                c[150]++;
                bool catchUp = false;
                if (on)
                {
                    float last = (float)sAutonomyLastTime.GetValue(a);
                    float gap = SimClock.ElapsedTime(TimeUnit.Minutes) - last;
                    // the game's own Update leaves mLastTime alone below 1 sim-minute; we do the same below N
                    if (last != 0f && !UnseenMath.Due(gap, UnseenEvery(), 1f, kMotiveCapMin))
                    {
                        c[151]++;
                        return;
                    }
                    catchUp = last != 0f && UnseenEvery() > 1;
                    // bounded per tick (docs/144 §8): a catch-up over the cap waits a tick, still counted as skipped
                    if (catchUp && !sCatchUps.Take(Simulator.TicksElapsed(), UnseenMath.kCatchUpsPerTick, gap >= UnseenMath.kHardGapMin))
                    {
                        c[151]++;
                        c[226] = sCatchUps.Held;
                        return;
                    }
                }
                if (catchUp)
                {
                    if (sCatchUpWatch == null) sCatchUpWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
                    sCatchUpWatch.Restart();
                    UpdateOrig();
                    long us = sCatchUpWatch.GetElapsedTime();
                    if (us > c[227]) c[227] = (int)us;
                    c[225] = sCatchUps.Most;
                }
                else UpdateOrig();
                if (!s.HasBeenDestroyed && Critical(s)) c[154]++;
            }
            public void UpdateOrig() { } // receives Autonomy.Update's original header

            void LowLodRefresh(Autonomy a, Sim s)
            {
                if (!SimUnseen(s)) { UpdateOrig(); return; }
                float last = (float)sAutonomyLastTime.GetValue(a);
                float gap = last == 0f ? 0f : SimClock.ElapsedTime(TimeUnit.Minutes) - last;
                int[] c = PatchCounters();
                c[780 + UnseenMath.RefreshBucket(gap)]++;
                if (gap < UnseenMath.kRefreshFloorMin) { UpdateOrig(); return; } // EA's own no-op: not a refresh to time
                if (sRefreshWatch == null) sRefreshWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
                sRefreshWatch.Restart();
                UpdateOrig();
                c[778]++;
                c[779] += (int)sRefreshWatch.GetElapsedTime();
            }

            static bool Critical(Sim s)
            {
                Motives m = s.Motives;
                if (m == null) return false;
                return m.GetMotiveValue(CommodityKind.Hunger) <= -95f || m.GetMotiveValue(CommodityKind.Energy) <= -95f
                    || m.GetMotiveValue(CommodityKind.Bladder) <= -95f || m.GetMotiveValue(CommodityKind.Hygiene) <= -95f;
            }
        }
    }
}
