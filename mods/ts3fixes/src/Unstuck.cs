using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // unstuck / unstuckhh (docs/686 §18, default off). A player's wish: a Sim or pet that is boxed in (furniture put
    // round it, a spot it was reset or snapped to) should walk out through the objects instead of failing every
    // route. Rules in UnstuckCore.cs.
    //
    // [verified, decompiled] The game's own detector is LotManager.RunStuckCheckHelper: armed for every actor at the
    // nightly lot update, it looks 20-60 sim-minutes later at a Sim that has not moved, is not on the active
    // household's lot and is in no rabbit hole: up to Sim.kNumberOfPlanAttemptsForStuckSim (10) plans to random
    // points of its lot (on the world lot: 4 plans of 2 m, selectable Sims only); all failed = looked at again, the
    // third time the Sim is reset (SetObjectToReset). There is no Sim.IsStuck and no per-Sim route-failure counter;
    // SimRoutingComponent.DoRoute answers each failed plan on its own (route-fail behaviour, exit reason, false).
    // NRaas Overwatch's nightly Stuck Check plans one route per standing Sim and resets as well.
    //
    // Signal: a Sim's failed plans in DoRoute (BlockedUse.cs's hook; every failed plan the other rules leave to the
    // game) are counted per spot: UnstuckCore.kMinFails plans of the kinds "no path", "no destination", "all places
    // obstructed" from one spot, the first at least 2.5 sim-minutes ago, with no successful plan of 3 m or more from
    // that spot between them. The game's stuck check stands for count and grace: a Sim it found no way for is looked
    // at on its next failed plan (FewerPlans.cs StuckCheckRun reports the outcome).
    // Probe, then: a normal plan to a place 3-10 m away succeeds = the Sim can walk, it is not boxed in (kept). Else a
    // plan with the game's option "pass through objects" (walls still count) to the nearest ring (0.75, 1.5, 3 m) whose
    // end is routable and has a normal way on of 3 m; at most 12 m long. No such plan = walled in (kept: the game's
    // reset is the answer). The plan is walked as a real route; then the failed route is planned again from the new
    // spot and walked when it now succeeds.
    // Never a Sim that sits or lies in something, swims, rides or is ridden, is carried, a baby, a Sim with an exit
    // reason (cancelled). A Sim that was looked at is not looked at again for 30 sim-minutes.
    //
    // Scope: unstuck = Sims and pets the player does not control, unstuckhh = household and selected ones; each
    // stands alone. Off = compare mode: the signal is counted, no probe is made. unstucklog writes one line per act.
    // Counters 4538-4549. Check: UnstuckCheck. No swap of its own.
    public static partial class Fixes
    {
        const int kUnFails = 4538, kUnDue = 4539, kUnGame = 4540, kUnWould = 4541, kUnExempt = 4542, kUnStill = 4543, kUnFree = 4544, kUnWalled = 4545,
            kUnClipped = 4546, kUnStopped = 4547, kUnReplanned = 4548, kUnErrors = 4549;
        const int kUnMaxRecs = 256;

        sealed class UnRec { public Vector3 Spot; public long First, Last; public int Fails; public bool Game; }

        sealed class UnCase { public Route Out; public Vector3 From; public float Len, Ring; public string Head; }

        static readonly Dictionary<ulong, UnRec> sUnRecs = new Dictionary<ulong, UnRec>();   // by Sim object id
        static readonly Dictionary<ulong, long> sUnCool = new Dictionary<ulong, long>();     // Sim -> ticks of the last look
        static readonly LineCapCore sUnCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kUnSites = { "begin", "after the walk", "plan seen", "game signal", "lot" };
        static readonly int[] sUnSiteErrors = new int[5];
        static string sUnLast = "", sUnLastError = "";

        static void UnError(int site, Exception e)
        {
            PatchCounters()[kUnErrors]++;
            sUnLastError = "unstuck: error at " + kUnSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sUnSiteErrors[site]++ < 3) DiagLine(sUnLastError);
        }

        static void UnLine(string text)
        {
            sUnLast = text;
            if (ShortcutOn("unstucklog") && sUnCap.Take(System.Environment.TickCount)) DiagLine("unstuck: " + text);
        }

        static bool UnSimOk(Sim sim)
        {
            return UnstuckCore.SimOk(RuInPosture(sim), SrInWater(sim), SrMounted(sim), sim.RoutingComponent == null || sim.RoutingComponent.RoutingParent != null,
                sim.SimDescription.Baby, sim.LotCurrent != null, sim.HasExitReason());
        }

        static UnRec UnRecOf(Sim sim, bool create)
        {
            UnRec rec;
            ulong id = sim.ObjectId.Value;
            if (sUnRecs.TryGetValue(id, out rec) || !create) return rec;
            if (sUnRecs.Count >= kUnMaxRecs) UnPrune();
            rec = new UnRec();
            rec.Spot = sim.Position;
            rec.First = rec.Last = SimClock.CurrentTicks;
            sUnRecs[id] = rec;
            return rec;
        }

        // The table is full: counts with no failed plan for 30 sim-minutes go; all of them when that frees nothing.
        static void UnPrune()
        {
            long now = SimClock.CurrentTicks, old = SimClock.ConvertToTicks(UnstuckCore.kCooldownMinutes, TimeUnit.Minutes);
            List<ulong> gone = new List<ulong>();
            foreach (KeyValuePair<ulong, UnRec> e in sUnRecs)
                if (now - e.Value.Last > old || now < e.Value.Last) gone.Add(e.Key);
            foreach (ulong id in gone) sUnRecs.Remove(id);
            if (sUnRecs.Count >= kUnMaxRecs) sUnRecs.Clear();
        }

        // The game's stuck check ended (FewerPlans.cs StuckCheckRun): `plans` probe plans, `ok` of them succeeded.
        static void UnGameSignal(Sim sim, int plans, int ok)
        {
            try
            {
                if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null || plans <= 0 || ok > 0) return;
                UnRec rec = UnRecOf(sim, true);
                if (!UnstuckCore.SameSpot(BuFlat(sim.Position, rec.Spot), sim.Position.y - rec.Spot.y, UnstuckCore.kSpotTol, kRuMaxDy)) { rec.Spot = sim.Position; rec.Fails = 0; rec.First = SimClock.CurrentTicks; }
                rec.Last = SimClock.CurrentTicks;
                if (!rec.Game) PatchCounters()[kUnGame]++;
                rec.Game = true;
            }
            catch (Exception e) { UnError(3, e); }
        }

        // After every Plan / Replan (BuAfterPlan), only while a Sim is counted: a plan that leads away clears its count.
        static void UnPlanSeen(ScriptCore.Route route, int kind, RoutePlanResult r)
        {
            try
            {
                if (!r.Succeeded()) return;
                IScriptProxy f = route.Follower;
                Sim sim = f != null ? f.Target as Sim : null;
                if (sim == null || !sUnRecs.ContainsKey(sim.ObjectId.Value)) return;
                float len = r.mDistanceTotal > 0f ? r.mDistanceTotal : BuValid(r.mDestination) ? BuFlat(r.mDestination, sim.Position) : 0f;
                if (UnstuckCore.PlanClears(true, kind <= 1, len)) sUnRecs.Remove(sim.ObjectId.Value);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { UnError(2, e); }
        }

        // A plan to the ring min..max round `center`, from `start` (the Sim's own spot when `fromSim`).
        static Route UnRing(Sim sim, bool fromSim, Vector3 start, Vector3 center, float min, float max, bool throughObjects, bool safeFromNonSimTask)
        {
            Route p = sim.CreateRoute();
            p.DoRouteFail = false;
            p.ExecutionFromNonSimTaskIsSafe = safeFromNonSimTask;
            if (throughObjects) p.SetOption(Route.RouteOption.PassThroughObjects, true);
            if (fromSim) p.PlanToPointRadialRange(center, min, max, RouteDistancePreference.PreferNearestToRouteOrigin, RouteOrientationPreference.NoPreference, 0UL, null);
            else p.PlanToPointRadialRangeFromPoint(start, center, min, max, RouteDistancePreference.PreferNearestToRouteOrigin, RouteOrientationPreference.NoPreference, 0UL, null);
            return p;
        }

        // The probe. kFree / kClip (k.Out, k.Len, k.Ring filled) / kWalled. Plans only.
        static int UnProbe(Sim sim, UnCase k, bool safeFromNonSimTask)
        {
            Vector3 at = sim.Position;
            if (UnRing(sim, true, at, at, UnstuckCore.kAwayMin, UnstuckCore.kAwayMax, false, false).PlanResult.Succeeded()) return UnstuckCore.kFree;
            foreach (float ring in UnstuckCore.kRings)
            {
                Route q = UnRing(sim, true, at, at, ring, UnstuckCore.kAwayMax, true, safeFromNonSimTask);
                RoutePlanResult pr = q.PlanResult;
                if (!pr.Succeeded() || !BuValid(pr.mDestination)) break;  // a wider ring holds the narrower ones: none of them has a plan
                Vector3 end = pr.mDestination;
                float len = pr.mDistanceTotal > 0f ? pr.mDistanceTotal : q.GetRouteLength();
                bool routable = q.IsPointRoutable(end);
                bool open = routable && UnRing(sim, false, end, end, UnstuckCore.kAwayMin, UnstuckCore.kAwayMax, false, false).PlanResult.Succeeded();
                if (UnstuckCore.Verdict(false, true, len, routable, open) != UnstuckCore.kClip) continue;
                k.Out = q; k.Len = len; k.Ring = ring;
                return UnstuckCore.kClip;
            }
            return UnstuckCore.kWalled;
        }

        // A failed plan the other rules leave to the game. Null = the game's answer. Otherwise the plan through the
        // objects to walk first. Reads, counts and plans; moves nothing.
        static UnCase UnBegin(Sim sim, Route r)
        {
            if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null) return null;
            RoutePlanResult pr = r.PlanResult;
            if (!UnstuckCore.PlanKind((int)pr.mType)) return null;
            int[] c = PatchCounters();
            c[kUnFails]++;
            long now = SimClock.CurrentTicks;
            UnRec rec = UnRecOf(sim, true);
            Vector3 at = sim.Position;
            if (!UnstuckCore.Continues(UnstuckCore.SameSpot(BuFlat(at, rec.Spot), at.y - rec.Spot.y, UnstuckCore.kSpotTol, kRuMaxDy), now, rec.Last, SimClock.ConvertToTicks(UnstuckCore.kWindowMinutes, TimeUnit.Minutes)))
            { rec.Spot = at; rec.First = now; rec.Fails = 0; rec.Game = false; }
            rec.Fails++;
            rec.Last = now;
            if (!UnstuckCore.Due(rec.Fails, now - rec.First, SimClock.ConvertToTicks(UnstuckCore.kGraceMinutes, TimeUnit.Minutes), rec.Game)) return null;
            long last;
            if (sUnCool.TryGetValue(sim.ObjectId.Value, out last) && BlockedUseCore.Cooling(now, last, SimClock.ConvertToTicks(UnstuckCore.kCooldownMinutes, TimeUnit.Minutes))) return null;

            // the signal: looked at once, then the count starts again
            string head = BuWho(sim) + " " + sim.FullName + " at " + BuAt(at) + " (" + rec.Fails + " failed plans" + (rec.Game ? ", the game's stuck check found no way" : "") + ", last " + pr.mType + ")";
            sUnRecs.Remove(sim.ObjectId.Value);
            if (sUnCool.Count >= kUnMaxRecs) sUnCool.Clear();
            sUnCool[sim.ObjectId.Value] = now;
            c[kUnDue]++;
            if (!UnSimOk(sim)) { c[kUnStill]++; return null; }
            int d = BlockedUseCore.Decide(true, IsPlayerSim(sim), ShortcutOn("unstuck"), ShortcutOn("unstuckhh"));
            if (d == BlockedUseCore.kWould) { c[kUnWould]++; return null; }
            if (d == BlockedUseCore.kExempt) { c[kUnExempt]++; return null; }
            UnCase k = new UnCase();
            k.From = at; k.Head = head;
            int v = UnProbe(sim, k, r.ExecutionFromNonSimTaskIsSafe);
            if (v == UnstuckCore.kFree) { c[kUnFree]++; UnLine(head + ": kept, it can walk " + UnstuckCore.kAwayMin.ToString("0") + " m away"); return null; }
            if (v == UnstuckCore.kWalled) { c[kUnWalled]++; UnLine(head + ": kept, no way out through objects either (walled in)"); return null; }
            return k;
        }

        // After the walk through the objects. True: r has a new plan that succeeded (the caller walks it).
        static bool UnAfter(Sim sim, Route r, UnCase k, bool walked)
        {
            int[] c = PatchCounters();
            if (sim.HasBeenDestroyed) return false;
            if (!walked)
            {
                c[kUnStopped]++;
                UnLine(k.Head + ": the walk out was stopped at " + BuAt(sim.Position));
                return false;
            }
            c[kUnClipped]++;
            bool again = !sim.HasExitReason() && r.Plan().Succeeded();
            if (again) c[kUnReplanned]++;
            UnLine(k.Head + ": walked out through objects, " + k.Len.ToString("0.0") + " m to " + BuAt(sim.Position) + " (ring " + k.Ring.ToString("0.00") + " m)"
                + (again ? ", its route planned again: ok" : ", its route still has no plan"));
            return again;
        }

        // ---- check ----

        static string UnCounters()
        {
            int[] c = PatchCounters();
            string sites = "";
            for (int i = 0; i < sUnSiteErrors.Length; i++)
                if (sUnSiteErrors[i] > 0) sites += (sites.Length > 0 ? ", " : "") + kUnSites[i] + " " + sUnSiteErrors[i];
            return "unstuck: failed plans left to the game " + c[kUnFails] + ", Sims counted now " + sUnRecs.Count + "; stuck signals (" + UnstuckCore.kMinFails + " failed plans from one spot over "
                + UnstuckCore.kGraceMinutes.ToString("0.0") + " sim-minutes, or the game's stuck check) " + c[kUnDue] + ", Sims the game's stuck check found no way for " + c[kUnGame]
                + "; on: walked out through objects " + c[kUnClipped] + " (their route then planned again: ok " + c[kUnReplanned] + "), walk out stopped " + c[kUnStopped]
                + "; kept: can walk away " + c[kUnFree] + ", walled in " + c[kUnWalled] + ", sitting, lying, swimming, riding, carried or cancelled " + c[kUnStill]
                + ", exempt (household without unstuckhh) " + c[kUnExempt] + "; off: would look " + c[kUnWould]
                + "; errors " + c[kUnErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sUnLastError + ")" : "") + "; " + GlMembers(sBuMissing) + (sUnLast.Length > 0 ? "; last: " + sUnLast : "");
        }

        // UnstuckCheck run: the rules with controls (read-only). Bug case: an NPC boxed in by furniture fails three
        // plans from its spot over 2.5 sim-minutes; no normal plan leads 3 m away, a plan through the objects does: it
        // could walk out. result: the counters and the last line. lot: the probe for the selected Sim and up to 12
        // other Sims and pets on its lot (read-only plans): free, boxed in with a way out through objects, or walled in.
        public static object UnstuckCheck(string what)
        {
            if (what == "result") return UnCounters();
            if (what == "lot") { try { return UnLot(); } catch (ResetException) { throw; } catch (Exception e) { UnError(4, e); return sUnLastError; } }
            if (what != "run") return "unstuck <run|result|lot>";
            bool on = ShortcutOn("unstuck"), hh = ShortcutOn("unstuckhh");
            const long G = 100;  // the grace, in any unit
            bool signal = UnstuckCore.PlanKind(16) && UnstuckCore.PlanKind(10) && UnstuckCore.PlanKind(15) && UnstuckCore.SameSpot(0.2f, 0f, UnstuckCore.kSpotTol, kRuMaxDy)
                && UnstuckCore.Continues(true, 50, 20, 1000) && UnstuckCore.Due(UnstuckCore.kMinFails, G, G, false) && UnstuckCore.Due(1, 0, G, true);
            bool act = UnstuckCore.SimOk(false, false, false, false, false, true, false) && UnstuckCore.Verdict(false, true, 4f, true, true) == UnstuckCore.kClip;
            bool fixedNow = signal && act && BlockedUseCore.Decide(true, false, on, hh) == BlockedUseCore.kGo;
            bool ctlSignal = !UnstuckCore.PlanKind(17) && !UnstuckCore.PlanKind(18) && !UnstuckCore.PlanKind(3) && !UnstuckCore.PlanKind(14) && !UnstuckCore.PlanKind(1)
                && !UnstuckCore.Due(UnstuckCore.kMinFails - 1, G * 5, G, false) && !UnstuckCore.Due(UnstuckCore.kMinFails + 5, G - 1, G, false) && !UnstuckCore.Due(0, G, G, true)
                && !UnstuckCore.SameSpot(1f, 0f, UnstuckCore.kSpotTol, kRuMaxDy) && !UnstuckCore.SameSpot(0f, 3f, UnstuckCore.kSpotTol, kRuMaxDy)
                && !UnstuckCore.Continues(false, 50, 20, 1000) && !UnstuckCore.Continues(true, 5000, 20, 1000)
                && UnstuckCore.PlanClears(true, true, 3f) && !UnstuckCore.PlanClears(true, true, 1f) && !UnstuckCore.PlanClears(true, false, 9f) && !UnstuckCore.PlanClears(false, true, 9f);
            bool ctlSim = !UnstuckCore.SimOk(true, false, false, false, false, true, false) && !UnstuckCore.SimOk(false, true, false, false, false, true, false)
                && !UnstuckCore.SimOk(false, false, true, false, false, true, false) && !UnstuckCore.SimOk(false, false, false, true, false, true, false)
                && !UnstuckCore.SimOk(false, false, false, false, true, true, false) && !UnstuckCore.SimOk(false, false, false, false, false, false, false)
                && !UnstuckCore.SimOk(false, false, false, false, false, true, true);
            bool ctlProbe = UnstuckCore.Verdict(true, true, 4f, true, true) == UnstuckCore.kFree && UnstuckCore.Verdict(false, false, 0f, false, false) == UnstuckCore.kWalled
                && UnstuckCore.Verdict(false, true, UnstuckCore.kClipMax + 1f, true, true) == UnstuckCore.kWalled && UnstuckCore.Verdict(false, true, 4f, false, true) == UnstuckCore.kWalled
                && UnstuckCore.Verdict(false, true, 4f, true, false) == UnstuckCore.kWalled;
            bool ctlScope = BlockedUseCore.Decide(true, true, true, false) == BlockedUseCore.kExempt && BlockedUseCore.Decide(true, true, false, true) == BlockedUseCore.kGo
                && BlockedUseCore.Decide(true, false, false, true) == BlockedUseCore.kWould && BlockedUseCore.Decide(true, false, false, false) == BlockedUseCore.kWould;
            bool controls = ctlSignal && ctlSim && ctlProbe && ctlScope;
            return "unstuck check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): a Sim that fails " + UnstuckCore.kMinFails + " plans from one spot over the grace and has no normal way 3 m away walks out through the objects=" + fixedNow
                + "; control: a Sim on the goal, locked door, aborted plan, too few plans, inside the grace, another spot, a plan that led away kept=" + ctlSignal
                + ", sitting, swimming, riding, carried, baby, cancelled kept=" + ctlSim + ", can walk, walled in, too long, end not routable, end boxed in kept=" + ctlProbe + ", scope per switch=" + ctlScope
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sBuMissing.Length == 0) + "; " + UnCounters();
        }

        static string UnLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            Lot lot = sel != null ? sel.LotCurrent : null;
            if (lot == null || sel.SimDescription == null) return "unstuck lot: no selected Sim";
            System.Text.StringBuilder b = new System.Text.StringBuilder("unstuck lot:");
            List<Sim> sims = new List<Sim>();
            sims.Add(sel);
            foreach (Sim o in lot.GetSims())
                if (o != null && o != sel && !o.HasBeenDestroyed && o.SimDescription != null) sims.Add(o);
            int seen = 0, free = 0, boxed = 0, walled = 0, still = 0;
            foreach (Sim s in sims)
            {
                if (seen >= 13) break;
                seen++;
                if (!UnSimOk(s)) { still++; continue; }
                UnCase k = new UnCase();
                int v = UnProbe(s, k, false);
                if (v == UnstuckCore.kFree) { free++; continue; }
                if (v == UnstuckCore.kClip) boxed++; else walled++;
                if (b.Length < 700) b.Append(" " + s.FullName + " at " + BuAt(s.Position) + ": " + (v == UnstuckCore.kClip ? "boxed in, a way out through objects of " + k.Len.ToString("0.0") + " m to "
                    + BuAt(k.Out.PlanResult.mDestination) : "no way out, through objects neither") + ";");
            }
            return b.Append(" Sims and pets on the lot " + sims.Count + ", looked at " + seen + ": free " + free + ", boxed in with a way out " + boxed + ", walled in " + walled
                + ", sitting, lying, swimming, riding or carried " + still).ToString();
        }
    }
}
