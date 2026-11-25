using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Scuba;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // socialreach / socialreachhh (docs/686 §17, default off). A player's wish: Sims should be able to chat at a
    // distance (across a counter, a fence, a crowded room) instead of giving up with the route-fail balloon. Rules in
    // SocialReachCore.cs.
    //
    // [verified, decompiled] A social (SocialInteractionA on the starter, SocialInteractionB on the partner) places
    // the pair in three steps, all in SocialInteraction:
    //   1. SocialRouteA (starter): on another level or farther than 5 m, a walk to 0..3 m from the partner in the
    //      partner's room; else a turn. SimRoutingComponent.DoRoute; false = the social ends ("path plan failed").
    //   2. ReceiveSocialInteraction -> SetupTwoPersonSocial (partner's task): plans the starter's walk to a ring at
    //      exactly the social's distance in the partner's room (RouteSimsToSocial; then the roles swapped when the
    //      starter is outside and the partner at home) and a turn for the partner. When the partner stands too close
    //      to a wall or object or in a doorway: a step aside (Wander), else a two-person jig placed near either Sim
    //      (three tries) and both walk to its slots. No plan = false: the partner adds the exit reason RouteFailed,
    //      copies it to the starter, and the starter's Run leaves the conversation.
    //   3. Both walk the planned routes (DoRoute); the partner's side then asks both to turn to each other
    //      (mbValidateFacing, RoutingComponent.RouteTurnToFace) when they do not stand exactly face to face.
    // Socials whose two Sims touch are marked in the data: ActionData.SocialJig other than SocialJigOnePerson makes a
    // paired social (SocialInteractionA.Run: mbPairedSocial), which the game refuses to play unless the pair stands
    // aligned at the social's distance (VerifyPositionAndAlignment); ForceCreateJig, horses and full-size dogs give
    // the social its own jig. Socials with steps of their own are subclasses (pillow fight, tag, catch, dance).
    // [verified, storm logs] Failed plans of SocialInteractionA/B: 3,331 lines on 1,534 routes; 2,797 with the goal
    // within 3 m on the Sim's level; no path 2,378, portal locked 475, no destination 393, aborted 85. Table in
    // docs/686 §17.
    //
    // Fix, for the plain social classes with a one-person jig, two humans, no baby, nobody riding or in water:
    //   a. In DoRoute (BlockedUse.cs's hook, after blockeduse, reachuse and viewlenient), a failed plan (the first, or
    //      a replan that ended the walk) of a Sim running such a social: within 3 m of the partner on its level, no
    //      wall between, the Sim turns to the partner and the route answers "arrived". Else the starter (never the
    //      partner) walks a normal plan to the nearest place within 2 m of the partner with no wall between, and the
    //      route answers "arrived" when it ends within 3 m.
    //   b. SetupTwoPersonSocial answered false with no exit reason on either Sim: with the starter (or the end of its
    //      walk) within 3 m, same level, no wall, the pair is placed where it stands: no routes, the game's own
    //      facing step turns both. Else the starter gets a normal plan to the nearest place within 2 m. The game's
    //      placement jig of the failed tries is removed.
    // Never paired or jig socials (hug, kiss, slap, fight, handshake, dance, woohoo, carry, anything with a pet, a
    // horse, a baby), never a subclass (pillow fight, tag, catch, pool socials), never through a wall or to another
    // level, never a locked door or an aborted plan.
    //
    // Scope: socialreach = pairs without a Sim the player controls, socialreachhh = pairs with a household or selected
    // Sim; each stands alone. Off = compare mode: counted, nothing done. socialreachlog writes one line per act.
    // Counters 4526-4537. Check: SocialReachCheck. Swap: SocialInteraction.SetupTwoPersonSocial (with a stub).
    public static partial class Fixes
    {
        const int kSrTries = 4526, kSrWould = 4527, kSrExempt = 4528, kSrContact = 4529, kSrApproach = 4530, kSrReceive = 4531, kSrPlace = 4532,
            kSrInPlace = 4533, kSrNear = 4534, kSrKept = 4535, kSrStopped = 4536, kSrErrors = 4537;
        const float kSrCooldownMinutes = 30f;
        const int kSrMaxCool = 256;

        sealed class SrCase { public Route Near; public Sim Partner; public int Site; public string Head; }

        static readonly FieldInfo sSrRoute = typeof(SocialInteraction).GetField("mRoute", kInstAll);
        static readonly Dictionary<ulong, long> sSrCool = new Dictionary<ulong, long>();  // Sim ^ partner -> ticks of the last kept case
        static readonly LineCapCore sSrCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kSrSites = { "begin", "arrive", "placement", "lot" };
        static readonly int[] sSrSiteErrors = new int[4];
        // why a covered social kept the game's answer (since the game started; not saved)
        static readonly string[] kSrWhy = { "too far", "other level", "wall between", "sitting or lying", "starter still walking", "a walk to the game's placement jig", "cooling down" };
        static readonly int[] sSrWhy = new int[7];
        const int kSrWhyFar = 0, kSrWhyLevel = 1, kSrWhyWall = 2, kSrWhyPosture = 3, kSrWhyMoving = 4, kSrWhyJig = 5, kSrWhyCool = 6;
        static string sSrLast = "", sSrLastError = "", sSrMissing = "";

        static void ApplySocialReach()
        {
            sSrMissing = sSrRoute == null ? "SocialInteraction.mRoute " : "";
            if (!Patch(typeof(SocialInteraction), "SetupTwoPersonSocial", new Type[] { typeof(Sim), typeof(Sim), typeof(SocialInteraction) }, typeof(SrSetupHook), "SetupHook", "SetupOrig"))
                sSrMissing += "SocialInteraction.SetupTwoPersonSocial";
            sSrMissing = sSrMissing.Trim();
            if (sSrMissing.Length > 0) AppliedLog().Add("socialreach: not found: " + sSrMissing);
        }

        // socialreach (docs/686 §17): the pair's placement failed: a talk social is done from where the two stand, or
        // the starter walks to the nearest place near the partner. `this` is the partner's SocialInteraction.
        public class SrSetupHook
        {
            public bool SetupHook(Sim simA, Sim simB, SocialInteraction socialA)
            {
                if (SetupOrig(simA, simB, socialA)) return true;
                try { return SrPlace((SocialInteraction)(object)this, simA, simB, socialA); }
                catch (ResetException) { throw; }
                catch (Exception e) { SrError(2, e); return false; }
            }

            public bool SetupOrig(Sim simA, Sim simB, SocialInteraction socialA) { return false; }  // receives SetupTwoPersonSocial's original body
        }

        static void SrError(int site, Exception e)
        {
            PatchCounters()[kSrErrors]++;
            sSrLastError = "socialreach: error at " + kSrSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sSrSiteErrors[site]++ < 3) DiagLine(sSrLastError);
        }

        static void SrLine(string text)
        {
            sSrLast = text;
            if (ShortcutOn("socialreachlog") && sSrCap.Take(System.Environment.TickCount)) DiagLine("socialreach: " + text);
        }

        static void SrKeep(int why) { PatchCounters()[kSrKept]++; sSrWhy[why]++; }

        static bool SrExact(SocialInteraction s) { Type t = s.GetType(); return t == typeof(SocialInteractionA) || t == typeof(SocialInteractionB); }

        static ActionData SrData(SocialInteraction s)
        {
            SocialInteractionA a = s as SocialInteractionA;
            if (a != null) return a.ActionData;
            SocialInteractionB b = s as SocialInteractionB;
            return b != null ? b.ActionData : null;
        }

        static bool SrInWater(Sim s) { return s.Posture is SwimmingInPool || s.Posture is ScubaDiving; }

        static bool SrMounted(Sim s) { return s.IsInRidingPosture || s.IsInBeingRiddenPosture; }

        // SocialReachCore.kTalk / kContact / kOther for a social's two sides (`other` may be null: not linked yet).
        // `starter` is the starter's side when known: its jig is the social's own; the partner's may be a placement jig.
        static int SrCover(SocialInteraction mine, SocialInteraction other, SocialInteraction starter, Sim a, Sim b)
        {
            ActionData data = SrData(mine);
            if (data == null && other != null) data = SrData(other);
            bool exact = SrExact(mine) && (other == null || SrExact(other));
            if (data != null && SocialReachCore.TouchByName(data.Key)) return SocialReachCore.kContact;  // touch with no jig (High Five): seen in sr hand run
            return SocialReachCore.Cover(exact, data != null, data != null ? (int)data.SocialJig : 0, starter != null ? starter.SocialJig != null : mine.SocialJig != null,
                a.IsHuman && b.IsHuman, a.SimDescription.Baby || b.SimDescription.Baby, SrMounted(a) || SrMounted(b), SrInWater(a) || SrInWater(b));
        }

        static string SrName(SocialInteraction s)
        {
            ActionData d = SrData(s);
            return s.GetType().Name + (d != null ? " " + d.Key : "");
        }

        static ulong SrLotId(Sim s) { Lot l = s.LotCurrent; return l != null && !l.IsWorldLot ? l.LotId : 0UL; }

        // ReachUseCore.kStand* of place `at` against the partner at `to`: in reach (flat), same level, no wall of either Sim's lot between.
        static int SrStand(Vector3 at, Vector3 to, ulong lotA, ulong lotB, float max)
        {
            float flat = BuFlat(at, to), dy = at.y - to.y;
            int st = ReachUseCore.Stand(flat, dy, false, max, kRuMaxDy);
            if (st != ReachUseCore.kStandOk) return st;
            bool wall = (lotA != 0UL && RuWall(lotA, at, to)) || (lotB != 0UL && lotB != lotA && RuWall(lotB, at, to));
            return ReachUseCore.Stand(flat, dy, wall, max, kRuMaxDy);
        }

        static int SrWhyOf(int stand) { return stand == ReachUseCore.kStandLevel ? kSrWhyLevel : stand == ReachUseCore.kStandSight ? kSrWhyWall : kSrWhyFar; }

        static ulong SrCoolKey(Sim a, Sim b) { return a.ObjectId.Value ^ (b.ObjectId.Value * 31UL); }

        static bool SrCooling(Sim a, Sim b)
        {
            long last;
            return sSrCool.TryGetValue(SrCoolKey(a, b), out last) && BlockedUseCore.Cooling(SimClock.CurrentTicks, last, SimClock.ConvertToTicks(kSrCooldownMinutes, TimeUnit.Minutes));
        }

        static void SrCoolSet(Sim a, Sim b)
        {
            if (sSrCool.Count >= kSrMaxCool) sSrCool.Clear();
            sSrCool[SrCoolKey(a, b)] = SimClock.CurrentTicks;
        }

        // A normal plan for `walker` to the nearest place kWalkMin..kWalkMax from the partner with no wall between.
        static Route SrNearPlan(Sim walker, Sim partner, bool safeFromNonSimTask, out int stand)
        {
            Vector3 to = partner.Position;
            ulong lotP = SrLotId(partner);
            return RuNearPlan(walker, to, SocialReachCore.kWalkMin, SocialReachCore.kWalkMax, lotP, safeFromNonSimTask, RouteOrientationPreference.TowardsObject,
                delegate(Vector3 at) { return SrStand(at, to, lotP, lotP, SocialReachCore.kWalkMax + kRuArriveSlack); }, out stand);
        }

        // ---- a. a failed plan in DoRoute ----

        // A failed plan blockeduse, reachuse and viewlenient left alone. Null = the game's behaviour. Otherwise the
        // case: Near (a plan to walk first) or the Sim talks from where it stands. Reads, counts and plans; moves nothing.
        static SrCase SrBegin(Sim sim, Route r, bool afterWalk)
        {
            if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null) return null;
            SocialInteraction si = sim.CurrentInteraction as SocialInteraction;
            if (si == null || !SrExact(si)) return null;
            Sim partner = si.Target;
            if (partner == null || partner == sim || partner.HasBeenDestroyed || partner.SimDescription == null) return null;
            int[] c = PatchCounters();
            bool starter = si is SocialInteractionA;
            SocialInteraction other = si.LinkedInteractionInstance as SocialInteraction;
            int cover = SrCover(si, other, starter ? si : other, sim, partner);
            if (cover == SocialReachCore.kOther) return null;
            RoutePlanResult pr = r.PlanResult;
            if (!SocialReachCore.PlanKind((int)pr.mType)) return null;  // a locked door, an aborted plan: the game's answer, not counted
            if (cover == SocialReachCore.kContact) { c[kSrContact]++; return null; }
            c[kSrTries]++;
            int site = starter ? SocialReachCore.kApproach : SocialReachCore.kReceive;
            int d = SocialReachCore.Decide(IsPlayerSim(sim), IsPlayerSim(partner), ShortcutOn("socialreach"), ShortcutOn("socialreachhh"));
            if (d == BlockedUseCore.kWould) { c[kSrWould]++; return null; }
            if (d == BlockedUseCore.kExempt) { c[kSrExempt]++; return null; }

            Vector3 at = sim.Position, goal = r.GetApproximateOriginalDestination();
            bool turn = BuValid(goal) && BuFlat(goal, at) <= SocialReachCore.kTurnSlack;
            bool helper = !starter && si.SocialJig != null && !turn;  // the game's placement jig (a social with its own jig is no talk social)
            bool inPosture = RuInPosture(sim);
            int stand = SrStand(at, partner.Position, SrLotId(sim), SrLotId(partner), SocialReachCore.kTalkMax);
            int step = SocialReachCore.Step(site, stand, inPosture, false, turn, helper);
            SrCase s = new SrCase();
            s.Partner = partner; s.Site = site;
            s.Head = BuWho(sim) + " " + sim.FullName + ", " + SrName(si) + " -> " + partner.FullName + " (" + pr.mType + (afterWalk ? ", after a walk" : "") + (turn ? ", a turn" : "") + ")";
            if (step == SocialReachCore.kHere) return s;
            if (step == SocialReachCore.kWalk)
            {
                if (SrCooling(sim, partner)) { SrKeep(kSrWhyCool); return null; }
                int ns;
                s.Near = SrNearPlan(sim, partner, r.ExecutionFromNonSimTaskIsSafe, out ns);
                if (s.Near != null) return s;
                stand = ns;
                SrCoolSet(sim, partner);  // a search that found no place is not repeated for a while
            }
            int why = helper ? kSrWhyJig : stand == ReachUseCore.kStandLevel ? kSrWhyLevel : inPosture ? kSrWhyPosture : SrWhyOf(stand);
            SrKeep(why);
            SrLine(s.Head + ": kept, " + kSrWhy[why] + ", " + BuFlat(at, partner.Position).ToString("0.0") + " m");
            return null;
        }

        // After the walk (or at once when the Sim stays where it stands). True: r answers "arrived".
        static bool SrArrive(Sim sim, Route r, SrCase s)
        {
            int[] c = PatchCounters();
            Sim partner = s.Partner;
            if (sim.HasBeenDestroyed || partner.HasBeenDestroyed) return false;
            Vector3 at = sim.Position, to = partner.Position;
            bool walked = s.Near != null;
            if (walked)
            {
                int stand = SrStand(at, to, SrLotId(sim), SrLotId(partner), SocialReachCore.kTalkMax);
                if (stand != ReachUseCore.kStandOk)
                {
                    c[kSrStopped]++;
                    SrLine(s.Head + ": the walk ended " + BuFlat(at, to).ToString("0.0") + " m away, " + kSrWhy[SrWhyOf(stand)]);
                    return false;
                }
            }
            RuFace(sim, to);
            RoutePlanResult ok = new RoutePlanResult();
            ok.mType = RoutePlanResultType.Succeeded;
            ok.mDestination = at;
            r.PlanResult = ok;
            c[s.Site == SocialReachCore.kApproach ? kSrApproach : kSrReceive]++;
            c[walked ? kSrNear : kSrInPlace]++;
            SrLine(s.Head + ": " + (walked ? "walked to the nearest place, " : "talks from where it stands, ") + BuFlat(at, to).ToString("0.0") + " m at " + BuAt(at));
            return true;
        }

        // ---- b. the pair's placement failed ----

        // `b` is the partner's social (simB its Sim), socialA the starter's (simA). True: the pair is placed (socialA's
        // route is none, or a plan for the starter to walk). Plans only; runs in the partner's task.
        static bool SrPlace(SocialInteraction b, Sim simA, Sim simB, SocialInteraction socialA)
        {
            if (b == null || socialA == null || simA == null || simB == null || simA == simB || sSrRoute == null) return false;
            if (simA.HasBeenDestroyed || simB.HasBeenDestroyed || simA.SimDescription == null || simB.SimDescription == null) return false;
            if (simA.HasExitReason() || simB.HasExitReason() || socialA.Cancelled || b.Cancelled) return false;  // cancelled or timed out: not a placement failure
            int[] c = PatchCounters();
            int cover = SrCover(b, socialA, socialA, simA, simB);
            if (cover != SocialReachCore.kTalk)
            {
                if (cover == SocialReachCore.kContact) c[kSrContact]++;
                return PrsPlace(b, simA, simB, socialA);  // pairsnap (WooHooSnap.cs, docs/686 §20): a paired social within 3 m is lined up by a snap
            }
            c[kSrTries]++;
            int d = SocialReachCore.Decide(IsPlayerSim(simA), IsPlayerSim(simB), ShortcutOn("socialreach"), ShortcutOn("socialreachhh"));
            if (d == BlockedUseCore.kWould) { c[kSrWould]++; return false; }
            if (d == BlockedUseCore.kExempt) { c[kSrExempt]++; return false; }
            string head = BuWho(simA) + " " + simA.FullName + ", " + SrName(socialA) + " -> " + simB.FullName + " (placement)";

            Vector3 pa = simA.Position, pb = simB.Position;
            bool moving = false;
            Route cur = simA.IsRouting && simA.RoutingComponent != null ? simA.RoutingComponent.GetCurrentRoute() : null;
            if (cur != null) { Vector3 end = cur.GetDestPoint(); if (BuValid(end)) { pa = end; moving = true; } }
            bool inPosture = RuInPosture(simA);
            int stand = SrStand(pa, pb, SrLotId(simA), SrLotId(simB), SocialReachCore.kTalkMax);
            int step = SocialReachCore.Step(SocialReachCore.kPlace, stand, inPosture, moving, false, false);
            Route near = null;
            if (step == SocialReachCore.kWalk)
            {
                if (SrCooling(simA, simB)) { SrKeep(kSrWhyCool); return false; }
                int ns;
                near = SrNearPlan(simA, simB, false, out ns);
                if (near == null) { step = SocialReachCore.kKeep; stand = ns; SrCoolSet(simA, simB); }  // a search that found no place is not repeated for a while
            }
            if (step == SocialReachCore.kKeep)
            {
                int why = stand == ReachUseCore.kStandLevel ? kSrWhyLevel : moving ? kSrWhyMoving : inPosture ? kSrWhyPosture : SrWhyOf(stand);
                SrKeep(why);
                SrLine(head + ": kept, " + kSrWhy[why] + ", " + BuFlat(pa, pb).ToString("0.0") + " m");
                return false;
            }
            SrDropPlacementJig(b, socialA);
            sSrRoute.SetValue(b, null);
            sSrRoute.SetValue(socialA, near);
            c[kSrPlace]++;
            c[near != null ? kSrNear : kSrInPlace]++;
            SrLine(head + ": " + (near != null ? "the starter walks to the nearest place at " + BuAt(near.PlanResult.mDestination) : "placed where they stand" + (moving ? " (the end of the starter's walk)" : ""))
                + ", " + BuFlat(pa, pb).ToString("0.0") + " m apart");
            return true;
        }

        // The game's placement jig of the failed tries stands somewhere near: gone, as the game's Cleanup removes it.
        static void SrDropPlacementJig(SocialInteraction b, SocialInteraction socialA)
        {
            SocialJig jig = b.SocialJig;
            if (jig == null || socialA.SocialJig != null) return;
            b.SocialJig = null;
            jig.Destroy();
        }

        // ---- check ----

        static string SrCounters()
        {
            int[] c = PatchCounters();
            string sites = "", why = "";
            for (int i = 0; i < sSrSiteErrors.Length; i++)
                if (sSrSiteErrors[i] > 0) sites += (sites.Length > 0 ? ", " : "") + kSrSites[i] + " " + sSrSiteErrors[i];
            for (int i = 0; i < sSrWhy.Length; i++)
                if (sSrWhy[i] > 0) why += (why.Length > 0 ? ", " : "") + kSrWhy[i] + " " + sSrWhy[i];
            return "socialreach: failed plans and placements of talk socials " + c[kSrTries] + "; on: by kind: starter's route " + c[kSrApproach] + ", partner's route " + c[kSrReceive]
                + ", placement " + c[kSrPlace] + "; by outcome: from where they stand " + c[kSrInPlace] + ", walked to the nearest place " + c[kSrNear]
                + "; kept as the game has it " + c[kSrKept] + (why.Length > 0 ? " (" + why + ")" : "") + ", walk stopped or ended out of reach " + c[kSrStopped]
                + ", exempt (a household Sim without socialreachhh, or none without socialreach) " + c[kSrExempt] + "; off: would " + c[kSrWould]
                + "; paired or jig socials left alone " + c[kSrContact]
                + "; errors " + c[kSrErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sSrLastError + ")" : "") + "; " + GlMembers(sSrMissing + (sBuMissing.Length > 0 ? " " + sBuMissing : ""))
                + (sSrLast.Length > 0 ? "; last: " + sSrLast : "");
        }

        // SocialReachCheck run: the rules with controls (read-only). Bug case: two NPCs 2 m apart with a counter
        // between: the placement has no plan and the social ends with the route-fail balloon; they could talk from
        // where they stand. result: the counters and the last line. lot: the Sims on the selected Sim's lot, and for
        // each whether a talk social from the selected Sim could be placed as the game does and what the rule finds
        // (read-only plans, up to 12 Sims).
        public static object SocialReachCheck(string what)
        {
            if (what == "result") return SrCounters();
            if (what == "lot") { try { return SrLot(); } catch (ResetException) { throw; } catch (Exception e) { SrError(3, e); return sSrLastError; } }
            if (what != "run") return "socialreach <run|result|lot>";
            bool on = ShortcutOn("socialreach"), hh = ShortcutOn("socialreachhh");
            const int Ok = ReachUseCore.kStandOk, Far = ReachUseCore.kStandFar, Level = ReachUseCore.kStandLevel, Sight = ReachUseCore.kStandSight;
            const int T = SocialReachCore.kTalk, A = SocialReachCore.kApproach, R = SocialReachCore.kReceive, P = SocialReachCore.kPlace;
            const int Keep = SocialReachCore.kKeep, Here = SocialReachCore.kHere, Walk = SocialReachCore.kWalk;
            bool talk = SocialReachCore.Cover(true, true, 0, false, true, false, false, false) == T && SocialReachCore.PlanKind(16) && SocialReachCore.PlanKind(10);
            bool here = SocialReachCore.Step(P, ReachUseCore.Stand(2f, 0f, false, SocialReachCore.kTalkMax, kRuMaxDy), false, false, false, false) == Here
                && SocialReachCore.Step(A, Ok, false, false, false, false) == Here && SocialReachCore.Step(R, Ok, false, false, true, false) == Here;
            bool walk = SocialReachCore.Step(A, Far, false, false, false, false) == Walk && SocialReachCore.Step(A, Sight, false, false, false, false) == Walk
                && SocialReachCore.Step(P, Far, false, false, false, false) == Walk;
            bool fixedNow = talk && here && walk && SocialReachCore.Decide(false, false, on, hh) == BlockedUseCore.kGo;
            bool ctlCover = SocialReachCore.Cover(true, true, 1, false, true, false, false, false) == SocialReachCore.kContact
                && SocialReachCore.Cover(true, true, 0, true, true, false, false, false) == SocialReachCore.kContact
                && SocialReachCore.Cover(false, true, 0, false, true, false, false, false) == SocialReachCore.kOther
                && SocialReachCore.Cover(true, true, 0, false, false, false, false, false) == SocialReachCore.kOther
                && SocialReachCore.Cover(true, true, 0, false, true, true, false, false) == SocialReachCore.kOther
                && SocialReachCore.Cover(true, true, 0, false, true, false, true, false) == SocialReachCore.kOther
                && SocialReachCore.Cover(true, true, 0, false, true, false, false, true) == SocialReachCore.kOther
                && SocialReachCore.Cover(true, false, 0, false, true, false, false, false) == SocialReachCore.kOther;
            bool ctlPlan = !SocialReachCore.PlanKind(18) && !SocialReachCore.PlanKind(3) && !SocialReachCore.PlanKind(1) && !SocialReachCore.PlanKind(14) && !SocialReachCore.PlanKind(11);
            bool ctlStep = SocialReachCore.Step(A, Level, false, false, false, false) == Keep && SocialReachCore.Step(P, Level, false, false, false, false) == Keep
                && SocialReachCore.Step(R, Far, false, false, false, false) == Keep && SocialReachCore.Step(R, Sight, false, false, false, false) == Keep
                && SocialReachCore.Step(R, Ok, false, false, false, true) == Keep && SocialReachCore.Step(A, Ok, true, false, false, false) == Keep
                && SocialReachCore.Step(A, Far, true, false, false, false) == Keep && SocialReachCore.Step(P, Far, false, true, false, false) == Keep
                && SocialReachCore.Step(A, Far, false, false, true, false) == Keep
                && ReachUseCore.Stand(3.2f, 0f, false, SocialReachCore.kTalkMax, kRuMaxDy) == Far && ReachUseCore.Stand(2f, 0f, true, SocialReachCore.kTalkMax, kRuMaxDy) == Sight;
            bool ctlScope = SocialReachCore.Decide(true, false, true, false) == BlockedUseCore.kExempt && SocialReachCore.Decide(false, true, true, false) == BlockedUseCore.kExempt
                && SocialReachCore.Decide(false, true, false, true) == BlockedUseCore.kGo && SocialReachCore.Decide(false, false, false, true) == BlockedUseCore.kWould
                && SocialReachCore.Decide(false, false, false, false) == BlockedUseCore.kWould;
            bool controls = ctlCover && ctlPlan && ctlStep && ctlScope;
            return "socialreach check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): a talk social whose route or placement fails is done from where the two stand (3 m, same level, no wall) or after a walk to the nearest place=" + fixedNow
                + "; control: paired and jig socials, subclasses, pets, babies, riders, swimmers kept=" + ctlCover + ", locked door, aborted plan, slot failures kept=" + ctlPlan
                + ", other level, the partner never walks, placement jig walk, sitting, starter still walking kept=" + ctlStep + ", scope per switch=" + ctlScope
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sSrMissing.Length == 0 && sBuMissing.Length == 0) + "; " + SrCounters();
        }

        static string SrLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            Lot lot = sel != null ? sel.LotCurrent : null;
            if (lot == null || sel.SimDescription == null) return "socialreach lot: no selected Sim";
            System.Text.StringBuilder b = new System.Text.StringBuilder("socialreach lot:");
            int sims = 0, seen = 0, closed = 0, reach = 0;
            ulong lotS = SrLotId(sel);
            foreach (Sim o in lot.GetSims())
            {
                if (o == null || o == sel || o.HasBeenDestroyed || o.SimDescription == null || !o.IsHuman || o.SimDescription.Baby) continue;
                sims++;
                if (seen >= 12) continue;
                seen++;
                // the game's placement plan: a ring at the social's distance round the partner, in the partner's room
                float dist = o.GetSocialRadiusWith(sel);
                Route t = sel.CreateRoute();
                t.DoRouteFail = false;
                bool gameOk = t.PlanToPointRadialRange(o, o.PositionOnFloor, dist, dist + 0.1f, RouteDistancePreference.NoPreference, RouteOrientationPreference.TowardsObject,
                    lot.IsWorldLot ? 0UL : lot.LotId, lot.IsWorldLot ? null : new int[] { o.RoomId }).Succeeded();
                if (gameOk) continue;
                closed++;
                int stand = SrStand(sel.Position, o.Position, lotS, SrLotId(o), SocialReachCore.kTalkMax);
                int step = SocialReachCore.Step(SocialReachCore.kPlace, stand, RuInPosture(sel), false, false, false);
                Route near = null;
                if (step == SocialReachCore.kWalk) { int ns; near = SrNearPlan(sel, o, false, out ns); if (near == null) stand = ns; }
                bool ok = step == SocialReachCore.kHere || near != null;
                if (ok) reach++;
                if (b.Length < 700) b.Append(" " + o.FullName + " " + BuFlat(sel.Position, o.Position).ToString("0.0") + " m: closed, "
                    + (step == SocialReachCore.kHere ? "talk from where they stand" : near != null ? "a place to talk from at " + BuAt(near.PlanResult.mDestination) : kSrWhy[SrWhyOf(stand)]) + ";");
            }
            return b.Append(" Sims " + sims + ", looked at " + seen + ", closed to the game's placement " + closed + ", of them in reach of the rule " + reach).ToString();
        }
    }
}
