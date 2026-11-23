using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/202 §3.4.2 "finish the walk" (switch unseenfbfinish, off; Rodney 2026-10-27). Replaces C1 / C2's fake answers ("no
    // obstacles" / "nothing to avoid": unseen Sims walked into things and replanned, 200-300 ms hitches) for walkers that can be
    // finished. Needs ts3native [UnseenFallback] Sites=1 + FinishWalk=1 and unseenfbsites (the push loop this runs in).
    //   1. Flag (UfSitesPush, at most kUfFinishEvalMax new Sims per push, held Sims every push): an unseen (§2 UfWhy 0) routing Sim
    //      passing D's Sim rules (UfFinishSim: teen+ human, not riding / carried / carrying, no group, interaction not social, not
    //      one whose point is the walk; a townie's plain Go Here is allowed), standing, whose current route action is the game's own FollowSubPathRouteAction on the LAST
    //      subpath, an ordinary walk (PathType.SimPath: no portal, door, stairs, vehicle, boat or water left), no slave Sims and
    //      nothing queued after it; its path end out of view with its lot and on no lot with an active-household Sim. Code 15.
    //   2. ts3native holds a flagged Sim when C1 / C2 find +0x918 busy (its agent updates skipped: it stands, no steering, no
    //      lock); any other unseen Sim gets the game's own wait (no fake).
    //   3. Claim (codes 16 + 17) and time it: remaining planned length (Route.GetDistanceRemaining) / D's walk speed, capped at
    //      2 sim-hours (§3.7); the Sim is in the in-transit table (UfHolds) meanwhile. Every push re-checks the Sim, the end and
    //      the route action; a failed check releases the hold (code 19) and the Sim walks on from where it stood (nothing changed:
    //      its path and route action are untouched).
    //   4. Finish (same push, after the checks and the game's own point test Route.IsPointRoutable(end)): the game's arrival kill
    //      FollowRouteAction.Kill(Success, kStop_Hard) (what SteeringAdaptor.HandleEventAtDestination asks), then release
    //      (code 18). The next agent pass stops the route; EventStopped wakes the Sim's task.
    //   5. Place (FollowSubPathRouteAction.PerformAction hook, in the Sim's own task, after PostRoute and before DoRoute goes on):
    //      camera checks again, PlaceSim at the path end facing the walk direction. DoRoute's own UpdateDynamicFootprint(true)
    //      then re-inserts the Sim's footprint at the new spot and returns true: the interaction continues as after a walk. Seen
    //      at placement: ContinueAndPopPathAndReplan (DoRoute replans from where the Sim stands and walks the rest).
    // Counters [363-370] (Fixes.cs).
    public static partial class Fixes
    {
        const int kUfFinishEvalMax = 16;
        sealed class UfFinishRec { public Sim Sim; public ulong Id; public FollowRouteAction Action; public Vector3 End, Fwd; public long Until, FinishAt; }
        static readonly Dictionary<ulong, UfFinishRec> sUfFinish = new Dictionary<ulong, UfFinishRec>();                   // claimed holds
        static readonly Dictionary<FollowRouteAction, UfFinishRec> sUfFinishing = new Dictionary<FollowRouteAction, UfFinishRec>();  // killed, not placed yet
        static readonly List<Sim> sUfFinishFlagged = new List<Sim>();  // flagged in the current push
        static readonly Dictionary<ulong, bool> sUfFinishPrev = new Dictionary<ulong, bool>();  // ids flagged in the push before
        // route actions given up once: never flagged again (a Sim that walks on after a give-up is not held again for the same walk)
        static readonly Dictionary<FollowRouteAction, bool> sUfFinishNo = new Dictionary<FollowRouteAction, bool>();
        static void UfFinishNoMore(FollowRouteAction a)
        {
            if (a == null) return;
            if (sUfFinishNo.Count >= 256) sUfFinishNo.Clear();  // shortcut: old actions dropped wholesale (a dropped one may be held once more)
            sUfFinishNo[a] = true;
        }
        static bool sUfFinishPatched;

        static void ApplyUfFinish()
        {
            sUfFinishPatched = Patch(typeof(FollowSubPathRouteAction), "PerformAction", Type.EmptyTypes, typeof(UfFinishActionHook), "PerformHook", "PerformOrig");
        }

        // a new world load (StartUfSitesPush): ts3native's holds of the old load are gone with its list
        static void UfFinishReset()
        {
            foreach (UfFinishRec f in sUfFinish.Values) UfHolds().Remove(f.Id);
            foreach (UfFinishRec f in sUfFinishing.Values) UfHolds().Remove(f.Id);
            sUfFinish.Clear();
            sUfFinishing.Clear();
            sUfFinishFlagged.Clear();
            sUfFinishPrev.Clear();
            sUfFinishNo.Clear();
        }

        static bool UfFinishable(Sim s)
        {
            if (!sUfFinishPatched) return false;
            int[] c = PatchCounters();
            FollowRouteAction a;
            Vector3 end;
            try
            {
                if (!UfFinishRoute(s, out a, out end)) { c[369]++; return false; }
                if (!UfFinishEndOk(end)) { c[368]++; return false; }
            }
            catch (Exception) { c[369]++; return false; }  // never ends the push loop
            return true;
        }

        static bool UfFinishRoute(Sim s, out FollowRouteAction a, out Vector3 end)
        {
            a = null;
            end = Vector3.Invalid;
            if (!UfFinishSim(s)) return false;
            RoutingComponent rc = s.RoutingComponent;
            a = rc != null ? rc.GetCurrentRouteAction() as FollowRouteAction : null;
            Route r = a != null && !sUfFinishNo.ContainsKey(a) ? a.RouteToBeFollowed : null;
            if (r == null) return false;
            uint paths = r.GetNumPaths();
            if (!UfFinishCore.RouteShape(a.GetType() == typeof(FollowSubPathRouteAction), a.RouteKillType != FollowRouteAction.KillType.None,
                    a.mSlaveObjectIds.Count + (a.mbSlavesRoute ? 1 : 0), paths, paths == 1 ? (uint)r.GetPathData(0).PathType : 0u,
                    rc.GetRouteActionAfter(a) != null)) return false;
            r.GetPathEndPoint(0, ref end);
            return end != Vector3.Invalid && !float.IsNaN(end.x);
        }

        // D's Sim rules (UfPreSim) with the finish deny list (a townie's plain Go Here allowed), standing (walking keeps Standing),
        // not in a tomb or hidden room
        // unseenfbgohere (kUnseenFbGoHere, default off, docs/328 §7.5): NRaas GoHere's VisitLotEx / VisitCommunityLotEx may take the
        // unseen fallback (pre and finish) like EA's VisitLot / VisitCommunityLot; every other NRaas type stays denied. [1587] / [1588]
        // count pre / finish eligibility of a GoHere trip; the first of each writes one log line (the switch-on queue reads it).
        static bool UfTripOk(Sims3.Gameplay.Interactions.InteractionInstance cur, bool finish)
        {
            string n = cur.GetType().FullName;
            bool gh = ShortcutOn("unseenfbgohere");
            bool ok = finish ? !UfFinishCore.Denied(n, gh) : !UfPreCore.Denied(n, gh);
            if (ok && gh && Array.IndexOf(UfPreCore.kGoHereTrips, n) >= 0 && PatchCounters()[finish ? 1588 : 1587]++ == 0)
                DiagLine("unseenfbgohere: " + n + " eligible (" + (finish ? "finish" : "pre") + ")");
            return ok;
        }

        static bool UfFinishSim(Sim s)
        {
            Sims3.Gameplay.CAS.SimDescription d = s.SimDescription;
            if (d == null || d.IsPet || !d.TeenOrAbove || !s.IsHuman || UfHeld(s) || s.Posture != s.Standing) return false;
            if (s.GetSituationOfType<Sims3.Gameplay.Situations.GroupingSituation>() != null) return false;
            if (Sims3.Gameplay.Controllers.TombRoomManager.IsObjectInATombOrHiddenRoom(s)) return false;  // docs/267 F2: hidden rooms too
            Sims3.Gameplay.Interactions.InteractionInstance cur = s.CurrentInteraction;
            return cur != null && !(cur is Sims3.Gameplay.Socializing.SocialInteraction) && UfTripOk(cur, true);
        }

        // the end is out of view with its lot (UfGoalUnseen) and on no lot where an active-household Sim is
        static bool UfFinishEndOk(Vector3 end)
        {
            if (!UfGoalUnseen(end)) return false;
            Lot lot = LotManager.GetLotAtPoint(end);
            if (lot == null || lot.IsWorldLot || Household.ActiveHousehold == null) return true;
            foreach (Sim h in Household.ActiveHousehold.AllActors) if (h != null && h.LotCurrent == lot) return false;
            return true;
        }

        static bool UfFinishHeld(ulong id) { return UfSitesSend(16, (uint)id) && UfSitesSend(17, (uint)(id >> 32)); }
        static void UfFinishRelease(ulong id, bool finished) { UfSitesSend(16, (uint)id); UfSitesSend(finished ? 18 : 19, (uint)(id >> 32)); }

        // after each push (UfSitesPush): claim new holds, then check, finish or give up the claimed ones
        static void UfFinishStep()
        {
            int[] c = PatchCounters();
            long now = SimClock.CurrentTicks;
            if (sUfFinishFlagged.Count > 0 && UfSitesSend(20, 0))
            {
                foreach (Sim s in sUfFinishFlagged)
                {
                    ulong id = s.ObjectId.Value;
                    if (sUfFinish.ContainsKey(id) || !UfFinishHeld(id)) continue;
                    FollowRouteAction a;
                    Vector3 end;
                    if (!UfFinishRoute(s, out a, out end)) { UfFinishRelease(id, false); UfFinishNoMore(a); c[366]++; continue; }
                    Vector3 d = end - s.Position;
                    float rem = a.RouteToBeFollowed.GetDistanceRemaining();
                    if (!(rem > 0f) || rem > 10000f) rem = d.Length();
                    float min = UfFinishCore.HoldMinutes(rem);
                    UfFinishRec f = new UfFinishRec();
                    f.Sim = s;
                    f.Id = id;
                    f.Action = a;
                    f.End = end;
                    d.y = 0f;
                    f.Fwd = d.LengthSqr() > 0.01f ? d * (1f / d.Length()) : s.ForwardVector;
                    f.Until = now + SimClock.ConvertToTicks(min, TimeUnit.Minutes);
                    sUfFinish[id] = f;
                    UfHolds()[id] = f.Until;
                    c[363]++;
                    if ((int)min > c[370]) c[370] = (int)min;
                }
            }
            if (sUfFinish.Count > 0)
            {
                List<UfFinishRec> all = new List<UfFinishRec>(sUfFinish.Values);
                foreach (UfFinishRec f in all)
                {
                    try { UfFinishCheck(f, now, c); }
                    catch (Exception)  // a failed check never ends the push loop: drop the record, the Sim walks on
                    {
                        sUfFinish.Remove(f.Id);
                        sUfFinishing.Remove(f.Action);
                        UfHolds().Remove(f.Id);
                        UfFinishRelease(f.Id, false);
                        UfFinishNoMore(f.Action);
                        c[366]++;
                    }
                }
            }
            // killed routes whose task never came back (reset, destroyed): drop after a sim-hour
            if (sUfFinishing.Count > 0)
            {
                List<FollowRouteAction> stale = null;
                foreach (KeyValuePair<FollowRouteAction, UfFinishRec> kv in sUfFinishing)
                    if (now - kv.Value.FinishAt > SimClock.ConvertToTicks(1f, TimeUnit.Hours) || now < kv.Value.FinishAt) (stale ?? (stale = new List<FollowRouteAction>())).Add(kv.Key);
                if (stale != null) foreach (FollowRouteAction a in stale) { UfHolds().Remove(sUfFinishing[a].Id); sUfFinishing.Remove(a); }
            }
        }

        static void UfFinishCheck(UfFinishRec f, long now, int[] c)
        {
            Sim s = f.Sim;
            bool same = s != null && !s.HasBeenDestroyed && s.InWorld && s.RoutingComponent != null
                && s.RoutingComponent.GetCurrentRouteAction() == f.Action && f.Action.RouteKillType == FollowRouteAction.KillType.None;
            bool ok = same && UfWhy(s) == 0 && UfFinishEndOk(f.End);  // camera checks every push and right before the finish
            int st = UfFinishCore.Step(UfFinishHeld(f.Id), same, ok, now, f.Until);
            if (st == UfFinishCore.kKeep) return;
            sUfFinish.Remove(f.Id);
            UfHolds().Remove(f.Id);
            if (st == UfFinishCore.kGiveUp) { UfFinishRelease(f.Id, false); UfFinishNoMore(f.Action); c[366]++; return; }
            // the landing spot: the game's own point test for this route (an end inside a footprint answers false)
            if (!f.Action.RouteToBeFollowed.IsPointRoutable(f.End)) { UfFinishRelease(f.Id, false); UfFinishNoMore(f.Action); c[365]++; return; }
            f.FinishAt = now;
            sUfFinishing[f.Action] = f;
            UfHolds()[f.Id] = now + SimClock.ConvertToTicks(10f, TimeUnit.Minutes);  // in transit until the Sim's task places it
            f.Action.Kill(FollowRouteAction.KillType.Success, StopMode.kStop_Hard);
            if (f.Action.RouteKillType != FollowRouteAction.KillType.Success)
            {
                sUfFinishing.Remove(f.Action);
                UfHolds().Remove(f.Id);
                UfFinishRelease(f.Id, false);
                UfFinishNoMore(f.Action);
                c[366]++;
                return;
            }
            UfFinishRelease(f.Id, true);  // the next agent pass stops the route; EventStopped wakes the Sim's task (UfFinishPlace)
        }

        // in the Sim's own task, after FollowSubPathRouteAction.PerformAction (PostRoute done, DoRoute not yet on)
        static RouteAction.ActionResult UfFinishPlace(FollowRouteAction a, RouteAction.ActionResult r)
        {
            UfFinishRec f;
            if (a == null || !sUfFinishing.TryGetValue(a, out f)) return r;
            sUfFinishing.Remove(a);
            UfHolds().Remove(f.Id);
            if (a.RouteKillType != FollowRouteAction.KillType.Success || r != RouteAction.ActionResult.ContinueAndPopPath) return r;
            int[] c = PatchCounters();
            Sim s = f.Sim;
            if (s == null || s.HasBeenDestroyed || !s.InWorld || UfWhy(s) != 0 || !UfFinishEndOk(f.End))
            {
                c[365]++;
                return RouteAction.ActionResult.ContinueAndPopPathAndReplan;  // DoRoute replans from where the Sim stands
            }
            float dist = (f.End - s.Position).Length();
            PlaceSim(s, f.End, f.Fwd, "unseenfbfinish route end");
            ShortcutLog(s.FullName + " => route end (unseenfbfinish, " + (int)dist + " m skipped)");
            c[364]++;
            return r;
        }

        public class UfFinishActionHook
        {
            public RouteAction.ActionResult PerformHook()
            {
                RouteAction.ActionResult r = PerformOrig();
                return sUfFinishing.Count == 0 ? r : UfFinishPlace((object)this as FollowRouteAction, r);  // `this` is really a FollowSubPathRouteAction
            }
            public RouteAction.ActionResult PerformOrig() { return RouteAction.ActionResult.Terminate; } // receives FollowSubPathRouteAction.PerformAction's original header
        }
    }
}
