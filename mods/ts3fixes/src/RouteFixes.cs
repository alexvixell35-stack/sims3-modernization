using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Routing fixes from docs/10, 11 and 12. Counters: [27] virtual-home runs, [28] virtual-home fades, [29] obstacle
    // replans switched off, [30] route-fail walks skipped, [31] stuck Sims reset or faded by the watchdog, [32] station
    // detours skipped. Watchdog home kinds: [33] none or virtual (faded), [34] shell apartment, [35] house; [36] second
    // resets in one sim-day. Watchdog idle cases (switch watchdogidle): [46] unroutable spot, [47] hidden room, [48] stationary,
    // [49] route plans failing, [50] routability verdicts asked, [51] route-fail Sims tracked.
    public static partial class Fixes
    {
        static void ApplyRouteFixes()
        {
            Patch(typeof(Sim.GoToVirtualHome.GoToVirtualHomeInternal), "Run", typeof(VirtualHomeHook), "RunHook", "RunOrig");
            Patch(typeof(SimRoutingComponent), "SetRouteOptions", typeof(RouteOptionsHook), "SetRouteOptionsHook", "SetRouteOptionsOrig");
            Patch(typeof(SimRoutingComponent), "GetRouteFailPath", typeof(RouteOptionsHook), "GetRouteFailPathHook", "GetRouteFailPathOrig");
            Patch(typeof(SimRoutingComponent), "CheckAndUpdateRouteForStations", typeof(StationsHook), "CheckStationsHook", "CheckStationsOrig");
        }

        // After every successful cross-lot plan, SimRoutingComponent.CheckAndUpdateRouteForStations rolls a chance for an
        // NPC to take the subway or hover train instead, scanning every station and planning up to 3 more routes
        // (docs/10). Nobody sees an unseen townie ride the subway, so it keeps the route it already has. The first static
        // hook: a static replacement and a static stub (the stub may only be called from the hook). Counter [32].
        public static class StationsHook
        {
            public static void CheckStationsHook(Route r)
            {
                if (ShortcutOn("stations"))
                {
                    Sim sim = r.Follower != null ? r.Follower.Target as Sim : null;
                    if (sim != null && TownieUnseen(sim, null))
                    {
                        PatchCounters()[32]++;
                        return;
                    }
                }
                CheckStationsOrig(r);
            }
            public static void CheckStationsOrig(Route r) { } // receives CheckAndUpdateRouteForStations' original header
        }

        // Homeless and virtual-apartment townies "go home" by walking to a random or apartment lot and fading out there
        // (Sim.GoToVirtualHome.GoToVirtualHomeInternal.Run; 4.4% of script time in Bridgeport, docs/10, docs/12). When the
        // townie and that home are unseen, end the trip the game's own way right away: fade out and remove the Sim.
        // Tourists and aliens keep their own handling.
        public class VirtualHomeHook : Sim.GoToVirtualHome.GoToVirtualHomeInternal
        {
            public bool RunHook()
            {
                int[] c = PatchCounters();
                c[27]++;
                SimDescription d = Actor.SimDescription;
                Lot home = d.VirtualLotHome;
                if (ShortcutOn("virtualhome") && d.Household != null && !d.Household.IsTouristHousehold && !d.Household.IsAlienHousehold
                    && TownieUnseen(Actor, null) && (home == null || FarFromViewers(home.Position)))
                {
                    c[28]++;
                    ShortcutLog(Actor.FullName + " -> virtual home (faded)");
                    Actor.FadeOut(true, true);
                    Simulator.Sleep(uint.MaxValue);
                    return true;
                }
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives GoToVirtualHomeInternal.Run's original header
        }

        // A failed plan for a Sim costs a second full search to find the blocker (ReplanToFindObstacleWhenPathPlanFails,
        // set on every Sim route) and then more plans to walk the Sim to it for the route-fail animation
        // (GetRouteFailPath; docs/11). Nobody sees that for an unseen townie: switch the option off for its routes and
        // skip the walk (callers already handle "no fail path").
        public class RouteOptionsHook : SimRoutingComponent
        {
            public void SetRouteOptionsHook(Route r, uint ageGenderFlags)
            {
                SetRouteOptionsOrig(r, ageGenderFlags);
                if (ShortcutOn("failreplan") && TownieUnseen(OwnerSim, null))
                {
                    r.SetOption(Route.RouteOption.ReplanToFindObstacleWhenPathPlanFails, false);
                    PatchCounters()[29]++;
                }
            }
            public void SetRouteOptionsOrig(Route r, uint ageGenderFlags) { } // receives SetRouteOptions' original header

            public Route GetRouteFailPathHook(Route r)
            {
                if (ShortcutOn("failreplan") && TownieUnseen(OwnerSim, null))
                {
                    PatchCounters()[30]++;
                    return null;
                }
                return GetRouteFailPathOrig(r);
            }
            public Route GetRouteFailPathOrig(Route r) { return null; } // receives GetRouteFailPath's original header
        }

        // Watchdog for stuck townies (docs/12): an unseen townie that has not moved for 60 sim-minutes while going home
        // (GoHome, GoToVirtualHome or its internal step), or for 120 sim-minutes while routing anywhere (two Sims blocking
        // each other in a doorway wait it out forever otherwise), gets the game's own Sim.SetToResetAndSendHome(). A townie
        // without a household home lot (homeless or virtual resident; not tourists or aliens) is faded out and removed
        // instead, the end state of the game's own GoToVirtualHome, since a send-home reset can drop it on the corner of the
        // lot it is stuck on (docs/12 §5.1). Once per Sim per sim-day, plus one more that day only if it is stuck again
        // more than 10 m from the first spot, so a Sim stuck again on the way home is not reset in a loop. Checks every
        // 10 sim-minutes while a world is loaded.
        const float kStuckGoHomeMinutes = 60f, kStuckRoutingMinutes = 120f, kWatchMinutes = 10f, kSecondResetMeters = 10f;
        static readonly long kDayTicks = SimClock.ConvertToTicks(1f, TimeUnit.Days);

        // Route plans that failed per Sim (object id) while it stayed within 1 m: the watchdog's "route plans failing" case
        // (Overwatch's Route.PostPlanCallback part). Fed by RouteBackoffHook.PlanHook for every plan; our own routability
        // checks (SpotVerdict, the Overwatch hook) are not counted. A successful plan clears the Sim's count.
        struct RouteFails { public Vector3 Pos; public long First; public int Count; }
        static readonly Dictionary<ulong, RouteFails> sRouteFails = new Dictionary<ulong, RouteFails>();
        // per task (the shape of PR #306's flag): this check's plan can sleep on its route job, and another Sim's result must still reach
        // NotePlanResult. shortcut: one slot, a second check sleeping beside it ends the first's cover early; a list if that ever shows up
        static bool sOwnCheckOn;
        static ulong sOwnCheckTask;
        static bool sInOwnCheck
        {
            get { return sOwnCheckOn && sOwnCheckTask == Simulator.CurrentTask.Value; }
            set { sOwnCheckOn = value; sOwnCheckTask = value ? Simulator.CurrentTask.Value : 0; }
        }

        // D5 (docs/151 §6): last successful plan per Sim from its spot (goal >= 1 m away, so a turn in place does not count),
        // recorded with routesrc (V7) or idlememo on.
        static readonly PlanOkMemo sPlanOk = new PlanOkMemo();

        static void NotePlanResult(IScriptProxy follower, bool ok, ScriptCore.Route route)
        {
            if (follower == null || sInOwnCheck) return;
            if (ok)
            {
                if (sRouteFails.Count > 0) sRouteFails.Remove(follower.ObjectId.Value);
                if (ShortcutOn("idlememo") || ShortcutOn("routesrc"))
                {
                    Sim s = follower.Target as Sim;
                    Vector3 goal = s != null ? route.GetApproximateOriginalDestination() : Vector3.Invalid;
                    if (s != null && goal != Vector3.Invalid && goal != Vector3.Origin && (goal - s.Position).Length() >= 1f)
                        sPlanOk.Note(follower.ObjectId.Value, s.Position.x, s.Position.y, s.Position.z, SimClock.CurrentTicks);
                }
                return;
            }
            if (!ShortcutOn("watchdog") || !ShortcutOn("watchdogidle")) return;
            Sim sim = follower.Target as Sim;
            if (sim == null) return;
            ulong id = follower.ObjectId.Value;
            long now = SimClock.CurrentTicks;
            RouteFails f;
            if (!sRouteFails.TryGetValue(id, out f) || (sim.Position - f.Pos).Length() > 1f || f.First > now)
            {
                if (sRouteFails.Count >= 1024) sRouteFails.Clear(); // bounded; a stuck Sim is counted again from its next failures
                f = new RouteFails { Pos = sim.Position, First = now };
                PatchCounters()[51]++;
            }
            f.Count++;
            sRouteFails[id] = f;
        }

        public static void StartWatchdog()
        {
            Simulator.AddObject(new StuckWatchdog());
        }

        class StuckWatchdog : Task
        {
            struct Seen { public Vector3 Pos; public long Since; }
            readonly Dictionary<ulong, Seen> mSeen = new Dictionary<ulong, Seen>();
            struct Reset { public long At; public Vector3 Pos; public int Count; }
            readonly Dictionary<ulong, Reset> mLastReset = new Dictionary<ulong, Reset>();

            public override void Simulate()
            {
                uint wait = (uint)SimClock.ConvertToTicks(kWatchMinutes, TimeUnit.Minutes);
                while (true)
                {
                    try { if (ShortcutOn("watchdog")) Check(); } catch (Exception) { mSeen.Clear(); }
                    try { if (ShortcutOn("watchdog") && ShortcutOn("watchdogidle")) CheckIdle(); } catch (Exception) { mStill.Clear(); }
                    Simulator.Sleep(wait);
                }
            }

            // Once per Sim per sim-day, plus one more that day only if it is stuck again more than 10 m from the first spot
            // (shared by every watchdog case, so no Sim is reset in a loop).
            bool ResetAllowed(Sim s, long now, out bool second)
            {
                Reset last;
                bool today = mLastReset.TryGetValue(s.SimDescription.SimDescriptionId, out last) && now - last.At < kDayTicks && last.At <= now;
                second = today && last.Count == 1 && (s.Position - last.Pos).Length() > kSecondResetMeters;
                return !today || second;
            }

            void NoteReset(Sim s, long now, bool second)
            {
                Reset last;
                mLastReset.TryGetValue(s.SimDescription.SimDescriptionId, out last);
                mLastReset[s.SimDescription.SimDescriptionId] = new Reset { At = second ? last.At : now, Pos = s.Position, Count = second ? 2 : 1 };
                if (second) PatchCounters()[36]++;
            }

            // ---- Idle cases (switch "watchdogidle"): what NRaas Overwatch's nightly Stuck Check and its route-fail
            // callback catch (docs/07 §2.2), checked every 10 sim-minutes instead of once a night, and spread over ticks.
            //  [46] Unroutable: a non-selectable Sim standing still in the same (or no) interaction on a non-world lot whose
            //       spot fails the game's Sim.IsPointInLotSafelyRoutable (Overwatch's "Unroutable"). An unseen standing
            //       townie qualifies after 60 sim-minutes; any other non-selectable Sim (on screen, not standing) after one
            //       sim-day in the same spot, as Overwatch's nightly "same spot as last night" (and only standing Sims get
            //       the route check: a seated Sim's spot is inside its chair). Verdicts come from the shared, profile-keyed
            //       cache (Fixes.SpotVerdict); a cached "not routable" is re-checked before acting. At most one full check
            //       per sim tick (budget shared with SafeSpot), a yield after each, at most kIdleChecksPerPass per pass.
            //  [47] Hidden room: same stillness rule, in a room the lot marks hidden (Lot.IsRoomHidden): no route check.
            //  [48] Stationary: any non-selectable Sim in the same spot and interaction for a whole sim-day (Overwatch's
            //       "Stationary"; Sims in a rabbit hole, in a bed, vehicle or other parent are skipped).
            //  [49] Route plans failing: any Sim but the active household (docs/249; [2066] counts those left alone), whose route plans
            //       failed at least 10 times over more than 30 sim-minutes while it stayed within 1 m (RouteBackoff's plan
            //       hook counts them via NotePlanResult; a successful plan clears the count; our own routability checks do
            //       not count). [51] Sims tracked this way.
            // [50] counts routability verdicts asked by this pass. Actions are the game's own: an idle Sim with nothing
            // queued is moved by Sim.AttemptToPutInSafeLocation(false) (the reset's own placement: its spot if safe, else a
            // routable spot nearby on its lot, else the lot's front door, else home), so nothing it was doing is cut;
            // otherwise the game's reset (SetObjectToReset, which runs the same placement). A hidden-room Sim with a home
            // gets SetToResetAndSendHome (its own spot may count as safe inside the hidden room). Shared daily reset limit.
            const float kIdleMinutes = 60f, kRouteFailMinutes = 30f;
            const int kRouteFailPlans = 10, kIdleChecksPerPass = 16;
            struct Still { public Vector3 Pos; public InteractionInstance Cur; public long Since, OkAt; }
            readonly Dictionary<ulong, Still> mStill = new Dictionary<ulong, Still>();

            void CheckIdle()
            {
                long now = SimClock.CurrentTicks;
                long idle = SimClock.ConvertToTicks(kIdleMinutes, TimeUnit.Minutes), failSpan = SimClock.ConvertToTicks(kRouteFailMinutes, TimeUnit.Minutes);
                int[] c = PatchCounters();
                List<Sim> queue = new List<Sim>();
                Dictionary<ulong, Still> keep = new Dictionary<ulong, Still>();
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s == null || s.SimDescription == null || !s.InWorld) continue;
                    if (UfInTransit(s)) continue;  // docs/202 §3.7: a D walk-time hold (unseenfbpre) stands still by design
                    ulong id = s.ObjectId.Value;
                    RouteFails f;
                    bool household = s.IsSelectable || s.Household == Household.ActiveHousehold;
                    if (sRouteFails.TryGetValue(id, out f) && f.Count >= kRouteFailPlans && now - f.First > failSpan && f.First <= now
                        && (s.Position - f.Pos).Length() <= 1f)
                    {
                        sRouteFails.Remove(id);
                        if (household) { c[2066]++; DiagLine("watchdog: household Sim " + s.FullName + " route plans failing " + f.Count + ", no reset"); continue; }
                        if (Act(s, now, 49, "reset (route plans failing)")) continue;
                    }
                    if (household || s.Parent != null || s.SimInRabbitHolePosture) continue;
                    InteractionInstance cur = s.CurrentInteraction;
                    Still st;
                    if (!mStill.TryGetValue(id, out st) || (s.Position - st.Pos).Length() > 1f || st.Cur != cur || st.Since > now)
                        st = new Still { Pos = s.Position, Cur = cur, Since = now };
                    keep[id] = st;
                    long still = now - st.Since;
                    if (still >= kDayTicks) { if (Act(s, now, 48, "reset (stationary a sim-day)")) keep.Remove(id); continue; }
                    bool unseen = TownieUnseen(s, null);
                    Lot lot = s.LotCurrent;
                    if (still < (unseen ? idle : kDayTicks) || s.IsRouting || lot == null || lot.IsWorldLot) continue;
                    if (lot.IsRoomHidden(s.RoomId)) { if (Act(s, now, 47, "reset (hidden room)")) keep.Remove(id); continue; }
                    if (s.Posture == s.Standing && (st.OkAt == 0 || now - st.OkAt > kDayTicks)) queue.Add(s);
                }
                mStill.Clear();
                foreach (KeyValuePair<ulong, Still> kv in keep) mStill[kv.Key] = kv.Value;
                if (mLastReset.Count > 1024) mLastReset.Clear();

                // the route checks: one full plan per tick at most, re-validated after every yield
                int asked = 0;
                // D5 (idlememo, docs/151 §6): a Sim that planned successfully from its spot since it went still can walk off
                // it, so no check; and at most 4 checks a pass. routesrc counts the memo hits without acting (V7).
                bool memo = ShortcutOn("idlememo"), src = ShortcutOn("routesrc");
                int cap = memo ? RouteSrcCore.kIdleChecksCapped : kIdleChecksPerPass;
                for (int qi = 0; qi < queue.Count; qi++)
                {
                    Sim s = queue[qi];
                    if (asked >= cap) { if (memo) c[237] += queue.Count - qi; break; }
                    for (int tries = 0; tries < 4; tries++)
                    {
                        Still st;
                        if (s.HasBeenDestroyed || !s.InWorld || !mStill.TryGetValue(s.ObjectId.Value, out st) || (s.Position - st.Pos).Length() > 1f
                            || st.Cur != s.CurrentInteraction || s.IsRouting || s.Posture != s.Standing || s.LotCurrent == null || s.LotCurrent.IsWorldLot) break;
                        if ((memo || src) && sPlanOk.Covers(s.ObjectId.Value, s.Position.x, s.Position.y, s.Position.z, st.Since, now))
                        {
                            c[233]++;
                            if (memo) { c[236]++; st.OkAt = now; mStill[s.ObjectId.Value] = st; break; }
                        }
                        Lot lot = s.LotCurrent;
                        int before = c[39];
                        SpotVerdicts vs = Verdicts(lot);
                        RouteProfile prof = ProfileOf(s);
                        // watchdoglockskip (docs/247 §12.4, the 477 ms CheckIdle hitch: rtLock 312 + rtPlanWait 157), in docs/250 §7.4's
                        // order: (1) the verdict cache answers first (SpotVerdict's c[37] hit, no plan); (2) no job holds +0x918: the
                        // sync check as before; (3) a job holds it: the async check A1 (watchdogasync) if on and its START is
                        // accepted; (4) else skip the Sim this pass (it keeps its Still record, the next pass checks it). The queue
                        // has no household or selected Sim (filtered above). Older ts3native and the game answer q 8 false: no skip.
                        bool cachedOk;
                        bool cached = prof.Ok && vs.Cells.TryGetValue(KeyOf(prof, s.Position), out cachedOk) && cachedOk;
                        if (!cached && ShortcutOn("watchdoglockskip") && HcWould(sYieldProbe, 8) && !WdA1Started(s)) { c[871]++; break; }  // wdLockSkip; q 8: native selftest jobyield-test.h
                        bool? ok = SpotVerdict(s, lot, vs, prof, s.Position, true, true, false);
                        if (ok.HasValue)
                        {
                            asked++;
                            c[50]++;
                            if (ok.Value) { st.OkAt = now; mStill[s.ObjectId.Value] = st; }
                            else if (Act(s, now, 46, "reset (idle on unroutable spot)")) mStill.Remove(s.ObjectId.Value);
                        }
                        if ((c[39] != before || !ok.HasValue) && MayYield()) Simulator.Sleep(0); // a full plan ran (or the tick's budget was used): next one in a later tick
                        if (ok.HasValue) break;
                    }
                }
            }

            // docs/250 §7.4 step 3, the async idle check A1 (switch watchdogasync): not built yet, so always "off" and
            // watchdoglockskip fires whenever a job holds the lock. A1 replaces this body; true = its START was accepted.
            static bool WdA1Started(Sim s) { return false; }

            // The game's own end state for a stuck idle Sim, within the shared daily limit. True = acted.
            bool Act(Sim s, long now, int counter, string why)
            {
                bool second;
                if (!ResetAllowed(s, now, out second)) return false;
                NoteReset(s, now, second);
                PatchCounters()[counter]++;
                string what = "watchdog " + why + (second ? " (2nd)" : "") + " in " + (s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "nothing")
                    + (s.LotCurrent != null ? " on " + s.LotCurrent.Name : "");
                ShortcutLog(s.FullName + " -> " + what);
                MovedSims()[s.FullName] = what + " @" + Math.Round(s.Position.x, 1) + "," + Math.Round(s.Position.z, 1) + " " + SimClock.CurrentTime().ToString();
                Lot home = s.LotHome ?? s.SimDescription.VirtualLotHome;
                if (counter == 47 && home != null) s.SetToResetAndSendHome();
                else if (counter == 46 && s.InteractionQueue != null && s.InteractionQueue.Count == 0) s.AttemptToPutInSafeLocation(false);
                else s.SetObjectToReset();
                return true;
            }

            void Check()
            {
                long now = SimClock.CurrentTicks;
                long goHome = SimClock.ConvertToTicks(kStuckGoHomeMinutes, TimeUnit.Minutes), routing = SimClock.ConvertToTicks(kStuckRoutingMinutes, TimeUnit.Minutes);
                Dictionary<ulong, Seen> keep = new Dictionary<ulong, Seen>();
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    InteractionInstance cur = s.CurrentInteraction;
                    if (cur == null) continue;
                    bool home = cur is GoHome || cur is Sim.GoToVirtualHome || cur is Sim.GoToVirtualHome.GoToVirtualHomeInternal;
                    bool moving = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting;
                    if (!home && !moving) continue;
                    if (!TownieUnseen(s, null) || UfInTransit(s)) continue;  // docs/202 §3.7: D holds are in transit
                    ulong id = s.ObjectId.Value;
                    Seen seen;
                    if (!mSeen.TryGetValue(id, out seen) || (s.Position - seen.Pos).Length() > 1f)
                        seen = new Seen { Pos = s.Position, Since = now };
                    bool second;
                    if (ResetAllowed(s, now, out second) && now - seen.Since >= (home ? goHome : routing))
                    {
                        int[] c = PatchCounters();
                        c[31]++;
                        NoteReset(s, now, second);
                        Lot lot = s.LotHome;
                        string kind = lot == null ? "none/virtual" : lot.BuildableShells.Length > 0 ? "shell apartment" : "house";
                        c[lot == null ? 33 : lot.BuildableShells.Length > 0 ? 34 : 35]++;
                        Household hh = s.Household;
                        bool fade = lot == null && hh != null && !hh.IsTouristHousehold && !hh.IsAlienHousehold;
                        string what = "watchdog " + (fade ? "faded" : "reset") + (second ? " (2nd)" : "") + " [" + kind + (lot != null ? " " + lot.Name : "") + "] stuck on " + cur.GetType().Name;
                        ShortcutLog(s.FullName + " -> " + what);
                        MovedSims()[s.FullName] = what + " @" + Math.Round(s.Position.x, 1) + "," + Math.Round(s.Position.z, 1) + " " + SimClock.CurrentTime().ToString();
                        if (fade) s.FadeOut(false, true); // fade, then the alarm destroys the Sim, as GoToVirtualHome ends unseen trips
                        else s.SetToResetAndSendHome();
                        continue;
                    }
                    keep[id] = seen;
                }
                mSeen.Clear();
                foreach (KeyValuePair<ulong, Seen> kv in keep) mSeen[kv.Key] = kv.Value;
                if (mLastReset.Count > 1024) mLastReset.Clear();
            }
        }
    }
}
