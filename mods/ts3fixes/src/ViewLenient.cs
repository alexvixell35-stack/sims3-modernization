using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.InteractionsShared;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // viewlenient / viewlenienthh (docs/686 §16, default off). A player's remark: a route error for contemplating the
    // surroundings or viewing something makes no sense; any place with a view would do. Rules in ViewLenientCore.cs.
    //
    // [verified, decompiled] Each of these plans a ring or cone round a point and hands it to SimRoutingComponent.
    // DoRoute, which answers a failed plan with the route-fail behaviour (balloon), the exit reason RouteFailed and
    // false; the interaction ends:
    //   Terrain.Contemplate.Run      0.3-1 m round the clicked point, only in the room of a randomly picked object near
    //                                the Sim; on a failure the next object is tried (a balloon each), then "bored"
    //   ViewObjects.CreateRouteToObject  the viewable's min..max distance, one level; in range with a view: a turn only
    //   MusicalInstrument.DoRouteForWatchOrDance  a dance floor, else 1 m .. 0.9 x the sound radius in the instrument's
    //                                room (front half, then all round)
    //   TV.RouteToCone (WatchTV, standing)  2-6 m in a cone in front of the TV, in its room
    //   Sim.WatchTheStars            a two-Sim jig's slots (a pose for two)
    // [verified, storm logs] Route failures by interaction: Contemplate 300 (294 of them "no destination" at the first
    // plan), WatchTV 69, Watch (performer) 113, WatchTheStars 41, ViewObjects 6. Table in docs/686 §16.
    //
    // Fix, in DoRoute (BlockedUse.cs's hook, after blockeduse and reachuse passed), for a failed plan (the first plan,
    // or a replan that ended the walk with no exit reason on the Sim) of a Sim whose running interaction is one of
    // these and whose route would show the balloon:
    //   contemplate  the Sim stays where it stands and the route answers "arrived" (sitting or swimming: the nearest
    //                place within 6 m of the point it can walk to)
    //   art, performer, tv  the Sim stands within the game's view range of the target, on its level, no wall between
    //                (a TV: on its front side): it turns to it, "arrived". Else a normal plan to the nearest such
    //                place (the target's room first, then any room) is walked, "arrived"
    //   stars, or no such place  the game's own answer (exit reason, false) with the route's balloon switched off
    // Never socials, Go Here or any other interaction; never a route to routing slots (blockeduse's case), a route
    // the game shows no balloon for (its caller has its own answer), an aborted plan, pets, riders.
    //
    // Scope: viewlenient = Sims the player does not control, viewlenienthh = household and selected Sims; each stands
    // alone. Off = compare mode: counted, nothing done. viewlenientlog writes one line per act. Counters 4510-4525.
    // Check: ViewLenientCheck. No swap of its own.
    public static partial class Fixes
    {
        const int kVlTries = 4510, kVlWould = 4511, kVlExempt = 4512, kVlContemplate = 4513, kVlArt = 4514, kVlPerformer = 4515, kVlTv = 4516, kVlStars = 4517,
            kVlInPlace = 4518, kVlNear = 4519, kVlQuiet = 4520, kVlKeptEa = 4521, kVlStopped = 4522, kVlErrors = 4523, kVlCooling = 4524, kVlAfterWalk = 4525;
        const float kVlEyeNudge = 0.3f;        // the point looked at lies this far in front of a painting or screen (off the wall it hangs on)
        const float kVlCooldownMinutes = 30f;  // a target with no place to look from is not searched again by that Sim for this long
        const int kVlMaxCool = 256;

        sealed class VlCase
        {
            public Route Near; public Vector3 Goal, Eye, Fwd; public int Kind; public float Range; public ulong LotId; public bool Quiet; public string Head, Why;
        }

        static readonly Dictionary<ulong, long> sVlCool = new Dictionary<ulong, long>();  // Sim ^ target tile -> ticks of the last search that found no place
        static readonly LineCapCore sVlCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kVlSites = { "begin", "arrive", "quiet end", "lot" };
        static readonly int[] sVlSiteErrors = new int[4];
        static string sVlLast = "", sVlLastError = "";

        static void VlError(int site, Exception e)
        {
            PatchCounters()[kVlErrors]++;
            sVlLastError = "viewlenient: error at " + kVlSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sVlSiteErrors[site]++ < 3) DiagLine(sVlLastError);
        }

        static void VlLine(string text)
        {
            sVlLast = text;
            if (ShortcutOn("viewlenientlog") && sVlCap.Take(System.Environment.TickCount)) DiagLine("viewlenient: " + text);
        }

        static int VlKindCounter(int kind)
        {
            return kind == ViewLenientCore.kContemplate ? kVlContemplate : kind == ViewLenientCore.kArt ? kVlArt : kind == ViewLenientCore.kPerformer ? kVlPerformer
                : kind == ViewLenientCore.kTv ? kVlTv : kVlStars;
        }

        static string VlKindName(int kind)
        {
            return kind == ViewLenientCore.kContemplate ? "contemplate" : kind == ViewLenientCore.kArt ? "art" : kind == ViewLenientCore.kPerformer ? "performer"
                : kind == ViewLenientCore.kTv ? "tv" : kind == ViewLenientCore.kStars ? "stars" : "none";
        }

        static int VlKindOf(InteractionInstance ia)
        {
            IGameObject t = ia != null ? ia.Target : null;
            return t == null ? ViewLenientCore.kNoKind : ViewLenientCore.Kind(RuNames(ia.GetType()), t is Sim, t is Terrain);
        }

        // What a view-kind interaction looks at: the target, or the object a viewable names for its route.
        static GameObject VlLookAt(IGameObject target)
        {
            IViewable vw = target as IViewable;
            GameObject named = vw != null ? vw.GetViewRouteTarget() : null;
            return named != null ? named : target as GameObject;
        }

        // The game's own farthest distance for this target, 0 when it has none to read.
        static float VlGameMax(int kind, IGameObject target)
        {
            if (kind == ViewLenientCore.kArt) { IViewable vw = target as IViewable; return vw != null ? vw.MaxDistanceFromObjectToView : 0f; }
            if (kind == ViewLenientCore.kPerformer) { MusicalInstrument mi = target as MusicalInstrument; return mi != null ? mi.DanceRadius * 0.9f : 0f; }
            return 0f;
        }

        // Fills the place looked at: position, facing, the point a wall test runs to, the lot whose walls count, the range.
        static void VlAim(VlCase v, GameObject look, IGameObject target)
        {
            v.Goal = look.Position;
            v.Fwd = look.ForwardVector;
            bool flatFace = v.Kind == ViewLenientCore.kArt || v.Kind == ViewLenientCore.kTv;
            v.Eye = flatFace && BuValid(v.Fwd) ? new Vector3(v.Goal.x + v.Fwd.x * kVlEyeNudge, v.Goal.y, v.Goal.z + v.Fwd.z * kVlEyeNudge) : v.Goal;
            Lot lot = look.LotCurrent;
            v.LotId = lot != null && !lot.IsWorldLot ? lot.LotId : 0UL;  // no walls on the world lot
            v.Range = ViewLenientCore.Range(v.Kind, VlGameMax(v.Kind, target));
        }

        // kStandOk when `at` is a place to look from; counts nothing.
        static int VlStand(Vector3 at, VlCase v, float slack)
        {
            float flat = BuFlat(at, v.Goal), dy = at.y - v.Goal.y;
            bool front = (at.x - v.Goal.x) * v.Fwd.x + (at.z - v.Goal.z) * v.Fwd.z > 0f;
            int st = ViewLenientCore.Stand(v.Kind, flat, dy, false, front, v.Range + slack, kRuMaxDy);
            if (st != ReachUseCore.kStandOk || v.LotId == 0UL) return st;
            return ViewLenientCore.Stand(v.Kind, flat, dy, RuWall(v.LotId, at, v.Eye), front, v.Range + slack, kRuMaxDy);
        }

        static bool VlStayHere(Sim sim) { return ViewLenientCore.StayHere(RuInPosture(sim), sim.Posture is SwimmingInPool); }

        // A failed plan blockeduse and reachuse left alone. Null = the game's behaviour. Otherwise the case: Quiet (end
        // without the balloon), Near (a plan to walk first) or neither (the Sim looks from where it stands). Reads,
        // counts and plans; moves nothing.
        static VlCase VlBegin(Sim sim, Route r, bool afterWalk)
        {
            if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null) return null;
            InteractionInstance ia = sim.CurrentInteraction;
            int kind = VlKindOf(ia);
            if (kind == ViewLenientCore.kNoKind) return null;
            int[] c = PatchCounters();
            RoutePlanResult pr = r.PlanResult;
            IGameObject target = ia.Target;
            int mode = ViewLenientCore.Mode(kind, ViewLenientCore.PlanKind((int)pr.mType), BuSimOk(sim), r.DoRouteFail, BuIsSlotRoute(r));
            VlCase v = new VlCase();
            v.Kind = kind;
            Vector3 goal = r.GetApproximateOriginalDestination();
            bool known = BuValid(goal);
            if (mode == ViewLenientCore.kView)
            {
                GameObject look = VlLookAt(target);
                if (look == null || look.HasBeenDestroyed) mode = ViewLenientCore.kKeep;
                else
                {
                    VlAim(v, look, target);
                    if (!ViewLenientCore.GoalAtTarget(kind, known, known ? BuFlat(goal, v.Goal) : 0f, v.Range, kRuGoalSlack)) mode = ViewLenientCore.kKeep;  // some other walk of the interaction
                }
            }
            else if (mode != ViewLenientCore.kKeep)
            {
                v.Goal = v.Eye = known ? goal : sim.Position;
                v.Range = ViewLenientCore.Range(kind, 0f);
            }
            if (mode == ViewLenientCore.kKeep) { c[kVlKeptEa]++; return null; }

            c[kVlTries]++;
            if (afterWalk) c[kVlAfterWalk]++;
            int d = BlockedUseCore.Decide(true, IsPlayerSim(sim), ShortcutOn("viewlenient"), ShortcutOn("viewlenienthh"));
            if (d == BlockedUseCore.kWould) { c[kVlWould]++; return null; }
            if (d == BlockedUseCore.kExempt) { c[kVlExempt]++; return null; }

            v.Head = BuWho(sim) + " " + sim.FullName + ", " + ia.GetType().Name + " -> " + target.GetType().Name + " at " + BuAt(v.Goal)
                + " (" + VlKindName(kind) + ", " + pr.mType + (afterWalk ? ", after a walk" : "") + ")";
            if (mode == ViewLenientCore.kQuiet) { v.Quiet = true; v.Why = "a pose for two has no other place"; return v; }
            long last;
            if (sVlCool.TryGetValue(RuCoolKey(sim, v.Goal), out last) && BlockedUseCore.Cooling(SimClock.CurrentTicks, last, SimClock.ConvertToTicks(kVlCooldownMinutes, TimeUnit.Minutes)))
            { c[kVlCooling]++; v.Quiet = true; v.Why = "no place found a short while ago"; return v; }
            VlPlace(sim, v, r.ExecutionFromNonSimTaskIsSafe);
            return v;
        }

        // Finds the place to look from: where the Sim stands (v.Near stays null), else a plan to the nearest place
        // (v.Near), else none (v.Quiet, cooling down).
        static void VlPlace(Sim sim, VlCase v, bool safeFromNonSimTask)
        {
            bool contemplate = v.Kind == ViewLenientCore.kContemplate;
            if (VlStayHere(sim) && (contemplate || VlStand(sim.Position, v, 0f) == ReachUseCore.kStandOk)) return;
            int stand;
            v.Near = RuNearPlan(sim, v.Goal, ViewLenientCore.MinDist(v.Kind), v.Range, v.LotId, safeFromNonSimTask,
                contemplate ? RouteOrientationPreference.NoPreference : RouteOrientationPreference.TowardsObject,
                delegate(Vector3 at) { return VlStand(at, v, kRuArriveSlack); }, out stand);
            if (v.Near != null) return;
            v.Quiet = true;
            v.Why = stand == ReachUseCore.kStandLevel ? "the nearest place is on another level" : stand == ReachUseCore.kStandSight ? "no view from the nearest place"
                : "no place within " + v.Range.ToString("0.0") + " m can be walked to";
            if (sVlCool.Count >= kVlMaxCool) sVlCool.Clear();
            sVlCool[RuCoolKey(sim, v.Goal)] = SimClock.CurrentTicks;
        }

        // After the walk (or at once when the Sim stays where it stands). True: r answers "arrived".
        static bool VlArrive(Sim sim, Route r, VlCase v)
        {
            int[] c = PatchCounters();
            if (sim.HasBeenDestroyed) return false;
            Vector3 at = sim.Position;
            bool walked = v.Near != null, contemplate = v.Kind == ViewLenientCore.kContemplate;
            if (walked && !contemplate && VlStand(at, v, kRuArriveSlack) != ReachUseCore.kStandOk)
            {
                c[kVlStopped]++;
                v.Why = "the walk ended " + BuFlat(at, v.Goal).ToString("0.0") + " m away with no view";
                return false;
            }
            if (!walked && !contemplate) RuFace(sim, v.Goal);  // contemplate: the game turns the Sim to the object it picked
            RoutePlanResult ok = new RoutePlanResult();
            ok.mType = RoutePlanResultType.Succeeded;
            ok.mDestination = at;
            r.PlanResult = ok;
            c[VlKindCounter(v.Kind)]++;
            c[walked ? kVlNear : kVlInPlace]++;
            VlLine(v.Head + ": " + (walked ? "walked to the nearest place, " : "stays in place, ") + BuFlat(at, v.Goal).ToString("0.0") + " m at " + BuAt(at));
            return true;
        }

        // No place to look from: counted; the caller gives the game's answer without the balloon.
        static void VlQuietEnd(Sim sim, VlCase v, bool afterWalk)
        {
            int[] c = PatchCounters();
            c[VlKindCounter(v.Kind)]++;
            c[kVlQuiet]++;
            VlLine(v.Head + ": quiet end, " + (v.Why != null ? v.Why : "the walk was stopped"));
        }

        // ---- check ----

        static string VlCounters()
        {
            int[] c = PatchCounters();
            string sites = "";
            for (int i = 0; i < sVlSiteErrors.Length; i++)
                if (sVlSiteErrors[i] > 0) sites += (sites.Length > 0 ? ", " : "") + kVlSites[i] + " " + sVlSiteErrors[i];
            return "viewlenient: failed plans of a view kind " + c[kVlTries] + " (after a walk " + c[kVlAfterWalk] + "); on: by kind: contemplate " + c[kVlContemplate] + ", art " + c[kVlArt]
                + ", performer " + c[kVlPerformer] + ", tv " + c[kVlTv] + ", stars " + c[kVlStars] + "; by outcome: stayed in place " + c[kVlInPlace] + ", walked to the nearest place " + c[kVlNear]
                + ", quiet ends " + c[kVlQuiet] + " (of them cooling down " + c[kVlCooling] + ", walk stopped " + c[kVlStopped] + "); kept as the game has it " + c[kVlKeptEa]
                + ", exempt (household without viewlenienthh) " + c[kVlExempt] + "; off: would " + c[kVlWould]
                + "; errors " + c[kVlErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sVlLastError + ")" : "") + "; " + GlMembers(sBuMissing) + (sVlLast.Length > 0 ? "; last: " + sVlLast : "");
        }

        // ViewLenientCheck run: the rules with controls (read-only). Bug case: an NPC's plan to a spot for Contemplate
        // answers "no destination" and the Sim plays the route-fail balloon; it could contemplate where it stands.
        // result: the counters and the last line. lot: the TVs, viewables and instruments of the selected Sim's lot
        // the selected Sim cannot plan to as the game does, and what the rule finds for each (read-only plans, up to 40).
        public static object ViewLenientCheck(string what)
        {
            if (what == "result") return VlCounters();
            if (what == "lot") { try { return VlLot(); } catch (ResetException) { throw; } catch (Exception e) { VlError(3, e); return sVlLastError; } }
            if (what != "run") return "viewlenient <run|result|lot>";
            bool on = ShortcutOn("viewlenient"), hh = ShortcutOn("viewlenienthh");
            const int Ok = ReachUseCore.kStandOk, K = ViewLenientCore.kKeep;
            string con = "|Terrain+Contemplate|TerrainInteraction|", art = "|ViewObjects|Interaction`2|", perf = "|Guitar+Watch|MusicalInstrument+WatchBase`1|Interaction`2|", tv = "|TV+WatchTV|Interaction`2|",
                stars = "|Sim+WatchTheStars|Interaction`2|";
            int kc = ViewLenientCore.Kind(con, false, true), ka = ViewLenientCore.Kind(art, false, false), kp = ViewLenientCore.Kind(perf, false, false), kt = ViewLenientCore.Kind(tv, false, false),
                ks = ViewLenientCore.Kind(stars, true, false);
            bool kinds = kc == ViewLenientCore.kContemplate && ka == ViewLenientCore.kArt && kp == ViewLenientCore.kPerformer && kt == ViewLenientCore.kTv && ks == ViewLenientCore.kStars;
            bool modes = ViewLenientCore.Mode(kc, ViewLenientCore.PlanKind(10), true, true, false) == ViewLenientCore.kHere && ViewLenientCore.Mode(ka, ViewLenientCore.PlanKind(16), true, true, false) == ViewLenientCore.kView
                && ViewLenientCore.Mode(kt, ViewLenientCore.PlanKind(18), true, true, false) == ViewLenientCore.kView && ViewLenientCore.Mode(ks, ViewLenientCore.PlanKind(16), true, true, true) == ViewLenientCore.kQuiet;
            bool stand = ViewLenientCore.StayHere(false, false) && ViewLenientCore.Stand(kt, 4f, 0f, false, true, ViewLenientCore.Range(kt, 0f), kRuMaxDy) == Ok
                && ViewLenientCore.Stand(ka, 2f, 0f, false, false, ViewLenientCore.Range(ka, 3f), kRuMaxDy) == Ok;
            bool fixedNow = kinds && modes && stand && BlockedUseCore.Decide(true, false, on, hh) == BlockedUseCore.kGo;
            bool ctlKind = ViewLenientCore.Kind("|Terrain+GoHere|TerrainInteraction|", false, true) == 0 && ViewLenientCore.Kind("|SocialInteractionA|SocialInteraction|", true, false) == 0
                && ViewLenientCore.Kind("|Sim+ListenToGhostStory|", true, false) == 0 && ViewLenientCore.Kind("|TV+RepairTV|", false, false) == 0 && ViewLenientCore.Kind("|Toilet+UseToilet|", false, false) == 0
                && ViewLenientCore.Kind(con, false, false) == 0 && ViewLenientCore.Kind(tv, true, false) == 0;
            bool ctlMode = ViewLenientCore.Mode(kc, ViewLenientCore.PlanKind(3), true, true, false) == K && ViewLenientCore.Mode(ka, true, false, true, false) == K
                && ViewLenientCore.Mode(kp, true, true, false, false) == K && ViewLenientCore.Mode(kt, true, true, true, true) == K && ViewLenientCore.Mode(0, true, true, true, false) == K
                && !ViewLenientCore.PlanKind(1) && !ViewLenientCore.PlanKind(2) && !ViewLenientCore.PlanKind(11);
            bool ctlStand = ViewLenientCore.Stand(kt, 4f, 0f, false, false, 6f, kRuMaxDy) == ReachUseCore.kStandSight && ViewLenientCore.Stand(ka, 2f, 0f, true, true, 4f, kRuMaxDy) == ReachUseCore.kStandSight
                && ViewLenientCore.Stand(kp, 9f, 0f, false, true, 8f, kRuMaxDy) == ReachUseCore.kStandFar && ViewLenientCore.Stand(ka, 2f, 3f, false, true, 4f, kRuMaxDy) == ReachUseCore.kStandLevel
                && !ViewLenientCore.StayHere(true, false) && !ViewLenientCore.StayHere(false, true) && !ViewLenientCore.GoalAtTarget(ka, true, 9f, 4f, kRuGoalSlack);
            bool ctlScope = BlockedUseCore.Decide(true, true, true, false) == BlockedUseCore.kExempt && BlockedUseCore.Decide(true, true, false, true) == BlockedUseCore.kGo
                && BlockedUseCore.Decide(true, false, false, true) == BlockedUseCore.kWould && BlockedUseCore.Decide(true, false, false, false) == BlockedUseCore.kWould;
            bool controls = ctlKind && ctlMode && ctlStand && ctlScope;
            return "viewlenient check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): a Sim that only looks does so from where it stands or the nearest place with a view, no balloon=" + fixedNow
                + "; control: Go Here, socials, ghost story, repair, object use kept=" + ctlKind + ", aborted plan, pet or rider, a route with no balloon, slot route kept=" + ctlMode
                + ", behind the TV, wall between, too far, other level, sitting or swimming, another walk kept=" + ctlStand + ", scope per switch=" + ctlScope
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sBuMissing.Length == 0) + "; " + VlCounters();
        }

        static string VlLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            Lot lot = sel != null ? sel.LotCurrent : null;
            if (lot == null || lot.IsWorldLot || sel.SimDescription == null) return "viewlenient lot: no selected Sim on a lot";
            System.Text.StringBuilder b = new System.Text.StringBuilder("viewlenient lot:");
            int targets = 0, seen = 0, closed = 0, inView = 0;
            foreach (GameObject o in lot.GetObjects<GameObject>())
            {
                if (o == null || o is Sim || o.HasBeenDestroyed || !o.InWorld || o.InInventory) continue;
                int kind = o is TV ? ViewLenientCore.kTv : o is MusicalInstrument ? ViewLenientCore.kPerformer : o is IViewable ? ViewLenientCore.kArt : ViewLenientCore.kNoKind;
                if (kind == ViewLenientCore.kNoKind) continue;
                targets++;
                if (seen >= 40) continue;
                seen++;
                VlCase v = new VlCase();
                v.Kind = kind;
                VlAim(v, VlLookAt(o), o);
                // the game's ring per kind, in the target's room: a TV from 2 m, an instrument from 1 m, a viewable from its own minimum
                IViewable vw = o as IViewable;
                float min = kind == ViewLenientCore.kTv ? 2f : kind == ViewLenientCore.kPerformer ? 1f : vw != null ? vw.MinDistanceFromObjectToView : 0.5f;
                Route t = sel.CreateRoute();
                t.DoRouteFail = false;
                t.SetOption(Route.RouteOption.DoLineOfSightCheckUserOverride, true);
                if (t.PlanToPointRadialRange(v.Goal, min, v.Range, RouteDistancePreference.PreferNearestToRouteDestination, RouteOrientationPreference.TowardsObject, lot.LotId, new int[] { o.RoomId }).Succeeded()) continue;
                closed++;
                bool here = VlStayHere(sel) && VlStand(sel.Position, v, 0f) == ReachUseCore.kStandOk;
                int stand = ReachUseCore.kStandOk;
                Route near = here ? null : RuNearPlan(sel, v.Goal, ViewLenientCore.MinDist(kind), v.Range, v.LotId, false, RouteOrientationPreference.TowardsObject,
                    delegate(Vector3 at) { return VlStand(at, v, kRuArriveSlack); }, out stand);
                if (here || near != null) inView++;
                if (b.Length < 700) b.Append(" " + o.GetType().Name + " (" + VlKindName(kind) + ", " + v.Range.ToString("0.0") + " m) at " + BuAt(v.Goal) + " room " + o.RoomId + ": closed, "
                    + (here ? "seen from where the Sim stands" : near != null ? "a place with a view at " + BuAt(near.PlanResult.mDestination) : stand == ReachUseCore.kStandSight ? "no view"
                    : stand == ReachUseCore.kStandLevel ? "other level" : "nothing in range") + ";");
            }
            return b.Append(" targets " + targets + ", looked at " + seen + ", closed to the game's plan " + closed + ", of them with a place to look from " + inView).ToString();
        }
    }
}
