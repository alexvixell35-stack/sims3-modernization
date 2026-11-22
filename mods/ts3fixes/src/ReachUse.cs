using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // reachuse / reachusehh (docs/686 §12, default off). A player's wish: a firefighter, a maid or a Sim doing a chore
    // should do it from a step further away when the spot is closed, not give up. Rules in ReachUseCore.cs.
    //
    // [verified, decompiled] These chores walk to a point or ring, not to a routing slot, and all end in
    // SimRoutingComponent.DoRoute: mopping (Terrain.Mopping.RouteToPuddle: a ring from 1 m in the puddle's room, one
    // level), putting out a fire (Fire.PlanRouteToExtinguish: 0.5-2.5 m in the fire's room, then 0-2.5 m; Sim.
    // ExtinguishSim: 2 m round the burning Sim; the firefighter's situation pushes these same two), picking up and
    // cleaning away things on the floor (CarrySystem.PickUp, trash and ash piles: RouteToObjectRadius in the object's
    // room) and tending plants (Plant.DoPlantRoute: a ring from 0.7 m in the plant's room). DoRoute answers a failed
    // plan with the route-fail behaviour, the exit reason RouteFailed and false, and the chore ends. A fire's plan is
    // tested before DoRoute (ExtinguishBase.RemoveFire): a fire whose plan fails is passed over for the next one, and
    // with none left the Sim (the firefighter too) stands and the fire burns on.
    // [verified, storm logs] Near failures (goal within 4 m, same level) by interaction: MopPuddle 2,296, WashDishes
    // 1,146, Clean 331, PutAwayLeftovers 153, ServingContainer_CleanUp 112, Snack_CleanUp 68, RecycleNewspaper 55.
    //
    // [verified, storm logs] Most of these failures are replans during the walk (MopPuddle near: first plan 434, replan
    // 1,091), where DoRoute answers false after the walk.
    //
    // Fix, in DoRoute (BlockedUse.cs's hook), for a failed plan with no slot destination (the first plan, or a replan
    // that ended the walk with no exit reason on the Sim) of a Sim whose running interaction is of a reach kind and
    // whose route goes to that interaction's target; a Sim standing on the spot is a closed spot like any other:
    //   1. The Sim already stands within the kind's reach of the target, on its level, no wall between: it turns to
    //      the target and the route answers "arrived".
    //   2. Otherwise a normal plan to the nearest point within reach is made (the target's room first, then any room),
    //      and its end must pass the same test. The Sim walks it and the route answers "arrived".
    //   3. No such point: the game's behaviour, and the case is not tried again for kRuCooldownMinutes.
    // Fire.PlanRouteToExtinguish gets the same rule on a failed plan: the plan to the place in reach is handed back
    // (a plan to where the Sim stands when it is in reach already), so the fire is not passed over.
    // Chores done at an object's routing slot (wash up at a sink, clean a toilet, repair, fill a bowl) and things on a
    // surface are blockeduse's case: with reachuse on its rules run for them even when blockeduse is off.
    // A puddle tile covered by an object is mopped when mopunreachable's run is in use (MopUnreachable.cs).
    //
    // Scope: reachuse = Sims the player does not control, reachusehh = household and selected Sims; each stands alone.
    // Humans only. Never a Sim as target except a Sim on fire. Never another level, never through a wall, never on the
    // world lot. Off = compare mode: counted, nothing done. reachuselog writes one line per act. Counters 4446-4461.
    // Check: ReachUseCheck.
    public static partial class Fixes
    {
        const int kRuTries = 4446, kRuWould = 4447, kRuExempt = 4448, kRuFire = 4449, kRuFloor = 4450, kRuSmall = 4451, kRuPlant = 4452,
            kRuInPlace = 4453, kRuSlot = 4454, kRuKeptFar = 4455, kRuKeptLevel = 4456, kRuKeptSight = 4457, kRuStopped = 4458, kRuErrors = 4459,
            kRuCooling = 4460, kRuOther = 4461;
        const float kRuMaxDy = 1.5f;          // half a storey: the same level
        const float kRuGoalSlack = 1.5f;      // the route's goal lies this close to the target
        const float kRuArriveSlack = 0.5f;    // a walk that ends this much outside the reach still counts
        const float kRuCooldownMinutes = 30f;
        const int kRuMaxCool = 256, kRuMaxTypes = 512;

        sealed class RuCase { public Route Near; public Vector3 Goal; public int Kind; public float Reach; public ulong LotId; public string Head; }

        static readonly Dictionary<Type, string> sRuNames = new Dictionary<Type, string>();
        static readonly Dictionary<ulong, long> sRuCool = new Dictionary<ulong, long>();  // Sim ^ goal tile -> ticks of the last kept case
        static readonly LineCapCore sRuCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kRuSites = { "begin", "arrive", "slot", "mop", "fire plan" };
        static readonly int[] sRuSiteErrors = new int[5];
        static string sRuLast = "", sRuLastError = "", sRuMissing = "";

        // The DoRoute hook is blockeduse's (ApplyBlockedUse); this adds the fire's plan.
        static void ApplyReachUse()
        {
            sRuMissing = Patch(typeof(Fire), "PlanRouteToExtinguish", new Type[] { typeof(Sim) }, typeof(RuFireHook), "PlanHook", "PlanOrig") ? "" : "Fire.PlanRouteToExtinguish";
            if (sRuMissing.Length > 0) AppliedLog().Add("reachuse: not found: " + sRuMissing);
        }

        static void RuError(int site, Exception e)
        {
            PatchCounters()[kRuErrors]++;
            sRuLastError = "reachuse: error at " + kRuSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sRuSiteErrors[site]++ < 3) DiagLine(sRuLastError);
        }

        static void RuLine(string text)
        {
            sRuLast = text;
            if (ShortcutOn("reachuselog") && sRuCap.Take(System.Environment.TickCount)) DiagLine("reachuse: " + text);
        }

        // "|Terrain+MopPuddle|Terrain+Mopping|...|": the class and its bases, nested classes with their owner
        static string RuNames(Type t)
        {
            string s;
            if (t == null) return "";
            if (sRuNames.TryGetValue(t, out s)) return s;
            System.Text.StringBuilder b = new System.Text.StringBuilder("|");
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                b.Append(x.DeclaringType != null ? x.DeclaringType.Name + "+" + x.Name : x.Name).Append('|');
            s = b.ToString();
            if (sRuNames.Count >= kRuMaxTypes) sRuNames.Clear();
            sRuNames[t] = s;
            return s;
        }

        static bool RuGardening(Type t)
        {
            for (; t != null && t != typeof(GameObject); t = t.BaseType)
                if (t.Namespace == "Sims3.Gameplay.Objects.Gardening") return true;
            return false;
        }

        static int RuKindOf(InteractionInstance ia)
        {
            if (ia == null) return ReachUseCore.kNoKind;
            IGameObject t = ia.Target;
            return ReachUseCore.Kind(RuNames(ia.GetType()), t != null ? RuNames(t.GetType()) : "", t is Sim, t != null && RuGardening(t.GetType()));
        }

        static int RuKindCounter(int kind)
        {
            return kind == ReachUseCore.kFire ? kRuFire : kind == ReachUseCore.kFloor ? kRuFloor : kind == ReachUseCore.kPlant ? kRuPlant : kRuSmall;
        }

        static string RuKindName(int kind)
        {
            return kind == ReachUseCore.kFire ? "fire" : kind == ReachUseCore.kFloor ? "floor" : kind == ReachUseCore.kSmall ? "small" : kind == ReachUseCore.kPlant ? "plant"
                : kind == ReachUseCore.kChore ? "chore" : "none";
        }

        static ulong RuCoolKey(Sim sim, Vector3 goal)
        {
            return sim.ObjectId.Value ^ ((ulong)(long)(goal.x * 2f) * 73856093UL) ^ ((ulong)(long)(goal.z * 2f) * 19349663UL) ^ ((ulong)(long)goal.y * 83492791UL);
        }

        static void RuCoolSet(Sim sim, Vector3 goal)
        {
            if (sRuCool.Count >= kRuMaxCool) sRuCool.Clear();
            sRuCool[RuCoolKey(sim, goal)] = SimClock.CurrentTicks;
        }

        // The game's own line-of-sight test (GameObject's helper): a light-blocking wall between two points of a lot.
        static bool RuWall(ulong lotId, Vector3 a, Vector3 b) { return World.WallExistsBetweenPoints(lotId, a, b, eRoomDefinition.LightBlocking); }

        // kStandOk when `at` is a place to do the chore from; counts nothing.
        static int RuStand(Vector3 at, RuCase q, float slack)
        {
            float flat = BuFlat(at, q.Goal), dy = at.y - q.Goal.y;
            int st = ReachUseCore.Stand(flat, dy, false, q.Reach + slack, kRuMaxDy);
            return st != ReachUseCore.kStandOk ? st : ReachUseCore.Stand(flat, dy, RuWall(q.LotId, at, q.Goal), q.Reach + slack, kRuMaxDy);
        }

        delegate int RuStandAt(Vector3 at);  // ReachUseCore.kStand* for a place

        // A normal plan to the nearest place min..max metres from `goal` whose end passes `test`: the goal's room on
        // lot `lotId` first, then any room. Null: no such place, `stand` = the first reason found. Plans only; shared
        // with viewlenient (ViewLenient.cs).
        static Route RuNearPlan(Sim sim, Vector3 goal, float min, float max, ulong lotId, bool safeFromNonSimTask, RouteOrientationPreference facing, RuStandAt test, out int stand)
        {
            LotLocation loc = default(LotLocation);
            World.GetLotLocationAtFloor(goal, ref loc);
            stand = ReachUseCore.kStandFar;
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 0 && lotId == 0UL) continue;  // no lot: no room to try first
                Route near = sim.CreateRoute();
                near.DoRouteFail = false;
                near.ExecutionFromNonSimTaskIsSafe = safeFromNonSimTask;
                near.SetOption(Route.RouteOption.DoLineOfSightCheckUserOverride, true);
                RoutePlanResult np = near.PlanToPointRadialRange(goal, min, max, RouteDistancePreference.PreferNearestToRouteDestination, facing,
                    pass == 0 ? lotId : 0UL, pass == 0 ? new int[] { loc.mRoom } : null);
                if (!np.Succeeded() || !BuValid(np.mDestination)) continue;
                int st = test(np.mDestination);
                if (st == ReachUseCore.kStandOk) return near;
                if (stand == ReachUseCore.kStandFar) stand = st;
            }
            return null;
        }

        static void RuKept(Sim sim, RuCase q, int stand, string why)
        {
            int[] c = PatchCounters();
            c[stand == ReachUseCore.kStandLevel ? kRuKeptLevel : stand == ReachUseCore.kStandSight ? kRuKeptSight : kRuKeptFar]++;
            RuCoolSet(sim, q.Goal);
            RuLine(q.Head + ": kept, " + why);
        }

        // Is this Sim's switch on? (reachuse: not player-controlled, reachusehh: household and selected)
        static bool RuOnFor(Sim sim)
        {
            return BlockedUseCore.Decide(true, IsPlayerSim(sim), ShortcutOn("reachuse"), ShortcutOn("reachusehh")) == BlockedUseCore.kGo;
        }

        // A failed plan with no slot destination. Null = the game's behaviour. Reads, counts and plans; moves nothing.
        static RuCase RuBegin(Sim sim, Route r, bool afterWalk)
        {
            if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null || BuIsSlotRoute(r)) return null;
            int[] c = PatchCounters();
            RoutePlanResult pr = r.PlanResult;
            InteractionInstance ia = sim.CurrentInteraction;
            int kind = ia != null && ReachUseCore.PlanKind((int)pr.mType) && BuSimOk(sim) ? RuKindOf(ia) : ReachUseCore.kNoKind;
            float reach = ReachUseCore.Reach(kind);
            if (reach <= 0f) { c[kRuOther]++; return null; }
            IGameObject target = ia.Target;
            bool terrain = target is Terrain;
            Vector3 goal = r.GetApproximateOriginalDestination();
            if (!BuValid(goal) && !terrain && target != null) goal = target.Position;
            bool known = BuValid(goal);
            if (!ReachUseCore.GoalAtTarget(terrain, known, known && !terrain && target != null ? BuFlat(goal, target.Position) : 0f, kRuGoalSlack)) { c[kRuOther]++; return null; }
            c[kRuTries]++;
            int d = BlockedUseCore.Decide(true, IsPlayerSim(sim), ShortcutOn("reachuse"), ShortcutOn("reachusehh"));
            if (d == BlockedUseCore.kWould) { c[kRuWould]++; return null; }
            if (d == BlockedUseCore.kExempt) { c[kRuExempt]++; return null; }
            long last;
            if (sRuCool.TryGetValue(RuCoolKey(sim, goal), out last) && BlockedUseCore.Cooling(SimClock.CurrentTicks, last, SimClock.ConvertToTicks(kRuCooldownMinutes, TimeUnit.Minutes)))
            { c[kRuCooling]++; return null; }

            RuCase q = new RuCase();
            q.Goal = goal; q.Kind = kind; q.Reach = reach;
            q.Head = BuWho(sim) + " " + sim.FullName + ", " + ia.GetType().Name + " -> " + (target != null ? target.GetType().Name : "?") + " at " + BuAt(goal)
                + " (" + RuKindName(kind) + ", " + pr.mType + (afterWalk ? ", after a walk" : "") + ")";
            return RuPlace(sim, q, r.ExecutionFromNonSimTaskIsSafe) ? q : null;
        }

        static bool RuInPosture(Sim sim)
        {
            Sims3.Gameplay.ActorSystems.Posture p = sim.Posture;
            return p != null && p.Container != null && !ReferenceEquals(p.Container, sim);
        }

        // Finds the place to do the chore from. True with q.Near == null: the Sim stands in reach (and sits or lies in
        // nothing). True with q.Near: a plan to the nearest place in reach, the target's room first, then any room.
        // False: kept, counted, cooling down.
        static bool RuPlace(Sim sim, RuCase q, bool safeFromNonSimTask)
        {
            Lot lot = LotManager.GetLotAtPoint(q.Goal);
            if (lot == null || lot.IsWorldLot || sim.LotCurrent != lot) { RuKept(sim, q, ReachUseCore.kStandLevel, "the goal is on the world lot or another lot"); return false; }
            q.LotId = lot.LotId;
            if (!RuInPosture(sim) && RuStand(sim.Position, q, 0f) == ReachUseCore.kStandOk) return true;

            int stand;
            q.Near = RuNearPlan(sim, q.Goal, ReachUseCore.MinDist(q.Kind), q.Reach, q.LotId, safeFromNonSimTask, RouteOrientationPreference.TowardsObject,
                delegate(Vector3 at) { return RuStand(at, q, kRuArriveSlack); }, out stand);
            if (q.Near != null) return true;
            RuKept(sim, q, stand, stand == ReachUseCore.kStandLevel ? "the nearest place is on another level" : stand == ReachUseCore.kStandSight ? "a wall between the nearest place and the target"
                : "no place within " + q.Reach.ToString("0.0") + " m can be walked to");
            return false;
        }

        static void RuFace(Sim sim, Vector3 goal)
        {
            Vector3 at = sim.Position, to = new Vector3(goal.x - at.x, 0f, goal.z - at.z);
            if (to.x * to.x + to.z * to.z > 0.0001f) sim.SetForward(to.Normalize());
        }

        // Fire.PlanRouteToExtinguish answered a failed plan. Null = keep it. Otherwise the plan to walk instead.
        static Route RuFirePlan(Fire fire, Sim sim, Route failed)
        {
            if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null || fire == null || fire.HasBeenDestroyed) return null;
            int[] c = PatchCounters();
            RoutePlanResult pr = failed.PlanResult;
            if (!ReachUseCore.PlanKind((int)pr.mType) || !BuSimOk(sim)) { c[kRuOther]++; return null; }
            c[kRuTries]++;
            int d = BlockedUseCore.Decide(true, IsPlayerSim(sim), ShortcutOn("reachuse"), ShortcutOn("reachusehh"));
            if (d == BlockedUseCore.kWould) { c[kRuWould]++; return null; }
            if (d == BlockedUseCore.kExempt) { c[kRuExempt]++; return null; }
            Vector3 goal = fire.Position;
            long last;
            if (sRuCool.TryGetValue(RuCoolKey(sim, goal), out last) && BlockedUseCore.Cooling(SimClock.CurrentTicks, last, SimClock.ConvertToTicks(kRuCooldownMinutes, TimeUnit.Minutes)))
            { c[kRuCooling]++; return null; }
            RuCase q = new RuCase();
            q.Goal = goal; q.Kind = ReachUseCore.kFire; q.Reach = ReachUseCore.Reach(ReachUseCore.kFire);
            q.Head = BuWho(sim) + " " + sim.FullName + ", extinguish -> " + fire.GetType().Name + " at " + BuAt(goal) + " (fire, " + pr.mType + ")";
            if (!RuPlace(sim, q, failed.ExecutionFromNonSimTaskIsSafe)) return null;
            Route plan = q.Near;
            if (plan == null)
            {
                plan = sim.CreateRoute();  // in reach already: a plan to where it stands, so the caller's DoRoute answers "arrived"
                if (!plan.PlanToPoint(sim.Position).Succeeded()) { RuKept(sim, q, ReachUseCore.kStandFar, "no plan to where the Sim stands"); return null; }
                RuFace(sim, goal);
                c[kRuInPlace]++;
            }
            c[kRuFire]++;
            RuLine(q.Head + ": plan to " + BuAt(plan.PlanResult.mDestination) + ", " + BuFlat(plan.PlanResult.mDestination, goal).ToString("0.0") + " m from the fire"
                + (q.Near != null ? "" : " (stood in reach)"));
            return plan;
        }

        // `this` is the game's Fire. Plans only, no yield.
        public class RuFireHook : Fire
        {
            public Route PlanHook(Sim actor)
            {
                Route r = PlanOrig(actor);
                try
                {
                    if (r == null || r.PlanResult.Succeeded()) return r;
                    Route n = RuFirePlan(this, actor, r);
                    if (n != null) return n;
                }
                catch (ResetException) { throw; }
                catch (Exception e) { RuError(4, e); }
                return r;
            }

            public Route PlanOrig(Sim actor) { return null; }  // receives Fire.PlanRouteToExtinguish's original body
        }

        // After the walk (or at once when the Sim stood in reach). True: r answers "arrived".
        static bool RuArrive(Sim sim, Route r, RuCase q)
        {
            int[] c = PatchCounters();
            if (sim.HasBeenDestroyed) return false;
            Vector3 at = sim.Position;
            int stand = RuStand(at, q, kRuArriveSlack);
            if (stand != ReachUseCore.kStandOk) { c[kRuStopped]++; RuLine(q.Head + ": kept, the walk ended " + BuFlat(at, q.Goal).ToString("0.0") + " m away"); return false; }
            if (q.Near == null) { RuFace(sim, q.Goal); c[kRuInPlace]++; }
            RoutePlanResult ok = new RoutePlanResult();
            ok.mType = RoutePlanResultType.Succeeded;
            ok.mDestination = at;
            r.PlanResult = ok;
            c[RuKindCounter(q.Kind)]++;
            RuLine(q.Head + ": from " + BuFlat(at, q.Goal).ToString("0.0") + " m at " + BuAt(at) + (q.Near != null ? ", walked up" : ", stood in reach"));
            return true;
        }

        // From BuBegin (BlockedUse.cs) for a failed slot plan its own switches leave alone: do reachuse's switches cover it?
        // obj = the object whose slots the route wanted.
        static bool RuSlotCase(Sim sim, GameObject obj)
        {
            try
            {
                if (!ShortcutOn("reachuse") && !ShortcutOn("reachusehh")) return false;
                InteractionInstance ia = sim.CurrentInteraction;
                IGameObject t = ia != null ? ia.Target : null;
                if (t == null || !RuOnFor(sim)) return false;
                bool own = ReferenceEquals(t, obj) || ReferenceEquals(t.Parent, obj) || (t.Parent != null && ReferenceEquals(t.Parent.Parent, obj));
                if (!ReachUseCore.SlotFallback(RuKindOf(ia), own)) return false;
                PatchCounters()[kRuSlot]++;
                RuLine(BuWho(sim) + " " + sim.FullName + ", " + ia.GetType().Name + " -> " + obj.GetType().Name + " 0x" + obj.ObjectId.Value.ToString("X") + ": slot route, blockeduse's rules");
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { RuError(2, e); return false; }
        }

        // From MopUnreachable.cs's run: the puddle tile is covered by `blocker`. True = mop it anyway.
        internal static bool RuMopCovered(Sim sim, GameObject blocker)
        {
            try
            {
                if (!ReachUseCore.MopCovered(sim != null && RuOnFor(sim), blocker != null, blocker is Sim)) return false;
                PatchCounters()[kRuFloor]++;
                RuLine(BuWho(sim) + " " + sim.FullName + ", mop: tile covered by " + blocker.GetType().Name + " mopped");
                return true;
            }
            catch (Exception e) { RuError(3, e); return false; }
        }

        // ---- check ----

        static string RuCounters()
        {
            int[] c = PatchCounters();
            string sites = "";
            for (int i = 0; i < sRuSiteErrors.Length; i++)
                if (sRuSiteErrors[i] > 0) sites += (sites.Length > 0 ? ", " : "") + kRuSites[i] + " " + sRuSiteErrors[i];
            return "reachuse: failed point plans of a reach kind " + c[kRuTries] + ", other failed point plans " + c[kRuOther] + "; on: done from range: fire " + c[kRuFire] + ", floor " + c[kRuFloor]
                + ", small " + c[kRuSmall] + ", plant " + c[kRuPlant] + " (of them without a walk " + c[kRuInPlace] + "), slot chores given to blockeduse " + c[kRuSlot]
                + "; kept: nothing in reach " + c[kRuKeptFar] + ", other level or lot " + c[kRuKeptLevel] + ", wall between " + c[kRuKeptSight] + ", walk stopped " + c[kRuStopped]
                + ", cooling down " + c[kRuCooling] + ", exempt (household without reachusehh) " + c[kRuExempt] + "; off: would " + c[kRuWould]
                + "; errors " + c[kRuErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sRuLastError + ")" : "") + "; " + GlMembers((sBuMissing + " " + sRuMissing).Trim()) + (sRuLast.Length > 0 ? "; last: " + sRuLast : "");
        }

        // ReachUseCheck run: the rules with controls (read-only). Bug case: an NPC's plan to a puddle's ring answers "no
        // path"; a place 2 m from the puddle on its level with no wall between is taken and the mop runs from there.
        // result: the counters and the last line. lot: the puddles, burnt tiles, fires, trash and ash piles of the
        // selected Sim's lot that the selected Sim cannot plan to as the game does (read-only plans, up to 40).
        public static object ReachUseCheck(string what)
        {
            if (what == "result") return RuCounters();
            if (what == "lot") return RuLot();
            if (what != "run") return "reachuse <run|result|lot>";
            bool on = ShortcutOn("reachuse"), hh = ShortcutOn("reachusehh");
            const int Ok = ReachUseCore.kStandOk;
            string mop = "|Terrain+MopPuddle|Terrain+Mopping|Terrain+PuddleInteraction|", fire = "|Fire+Extinguish|ExtinguishBase|", go = "|Terrain+GoHere|";
            bool kinds = ReachUseCore.Kind(mop, "|Terrain|", false, false) == ReachUseCore.kFloor && ReachUseCore.Kind(fire, "|Fire|GameObject|", false, false) == ReachUseCore.kFire
                && ReachUseCore.Kind("|Sim+ExtinguishSim|", "|Sim|", true, false) == ReachUseCore.kFire && ReachUseCore.Kind("|TrashPile+CleanUp|", "|TrashPile|GameObject|", false, false) == ReachUseCore.kSmall
                && ReachUseCore.Kind("|Plant+WaterPlant|", "|Plant|GameObject|", false, true) == ReachUseCore.kPlant;
            bool stand = ReachUseCore.PlanKind(16) && ReachUseCore.PlanKind(10) && ReachUseCore.PlanKind(17) && ReachUseCore.Stand(2f, 0f, false, ReachUseCore.Reach(ReachUseCore.kFloor), kRuMaxDy) == Ok && ReachUseCore.GoalAtTarget(true, true, 0f, kRuGoalSlack)
                && ReachUseCore.GoalAtTarget(false, true, 0.4f, kRuGoalSlack);
            bool slot = ReachUseCore.Kind("|Sink+WashDishes|", "|Sink|GameObject|", false, false) == ReachUseCore.kChore && ReachUseCore.SlotFallback(ReachUseCore.kChore, true)
                && ReachUseCore.Reach(ReachUseCore.kChore) == 0f;
            bool fixedNow = kinds && stand && slot && BlockedUseCore.Decide(true, false, on, hh) == BlockedUseCore.kGo && ReachUseCore.MopCovered(on, true, false);
            bool ctlKind = ReachUseCore.Kind(go, "|Terrain|", false, false) == ReachUseCore.kNoKind && ReachUseCore.Kind("|SocialInteractionA|SocialInteraction|", "|Sim|", true, false) == ReachUseCore.kNoKind
                && ReachUseCore.Kind("|PickUpChild|SocialInteraction|", "|Sim|", true, false) == ReachUseCore.kNoKind && ReachUseCore.Kind("|Toilet+UseToilet|", "|Toilet|GameObject|", false, false) == ReachUseCore.kNoKind
                && ReachUseCore.Reach(ReachUseCore.kNoKind) == 0f && !ReachUseCore.PlanKind(18) && !ReachUseCore.PlanKind(3) && !ReachUseCore.PlanKind(1);
            bool ctlStand = ReachUseCore.Stand(2.6f, 0f, false, 2.5f, kRuMaxDy) == ReachUseCore.kStandFar && ReachUseCore.Stand(1f, 3f, false, 2.5f, kRuMaxDy) == ReachUseCore.kStandLevel
                && ReachUseCore.Stand(1f, -3f, false, 2.5f, kRuMaxDy) == ReachUseCore.kStandLevel && ReachUseCore.Stand(1f, 0f, true, 2.5f, kRuMaxDy) == ReachUseCore.kStandSight;
            bool ctlGoal = !ReachUseCore.GoalAtTarget(false, true, 4f, kRuGoalSlack) && !ReachUseCore.GoalAtTarget(true, false, 0f, kRuGoalSlack);
            bool ctlScope = BlockedUseCore.Decide(true, true, true, false) == BlockedUseCore.kExempt && BlockedUseCore.Decide(true, true, false, true) == BlockedUseCore.kGo
                && BlockedUseCore.Decide(true, false, false, true) == BlockedUseCore.kWould && BlockedUseCore.Decide(true, false, false, false) == BlockedUseCore.kWould;
            bool ctlSlot = !ReachUseCore.SlotFallback(ReachUseCore.kChore, false) && !ReachUseCore.SlotFallback(ReachUseCore.kNoKind, true)
                && !ReachUseCore.MopCovered(true, true, true) && !ReachUseCore.MopCovered(false, true, false) && !ReachUseCore.MopCovered(true, false, false);
            bool controls = ctlKind && ctlStand && ctlGoal && ctlScope && ctlSlot;
            return "reachuse check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): a chore whose spot is closed is done from within reach=" + fixedNow
                + "; control: Go Here, socials, picking up a child, object use, portal locked kept=" + ctlKind + ", too far, other level, wall between kept=" + ctlStand
                + ", a route that is not to the target kept=" + ctlGoal + ", scope per switch=" + ctlScope + ", slot rules only for the target's own slots, a Sim on the puddle kept=" + ctlSlot
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sBuMissing.Length == 0 && sRuMissing.Length == 0) + "; " + RuCounters();
        }

        static string RuLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            Lot lot = sel != null ? sel.LotCurrent : null;
            if (lot == null || lot.IsWorldLot || sel.SimDescription == null) return "reachuse lot: no selected Sim on a lot";
            System.Text.StringBuilder b = new System.Text.StringBuilder("reachuse lot:");
            int seen = 0, closed = 0, inReach = 0;
            List<KeyValuePair<Vector3, int>> spots = new List<KeyValuePair<Vector3, int>>();
            List<string> names = new List<string>();
            foreach (LotLocation[] tiles in new LotLocation[][] { World.GetPuddles(lot.LotId, LotLocation.Invalid), World.GetBurntTiles(lot.LotId, LotLocation.Invalid) })
            {
                if (tiles == null) continue;
                foreach (LotLocation l in tiles)
                {
                    Vector3 p = Vector3.Invalid;
                    World.GetWorldPosition(lot.LotId, l, ref p);
                    if (BuValid(p)) { spots.Add(new KeyValuePair<Vector3, int>(p, ReachUseCore.kFloor)); names.Add("tile"); }
                }
            }
            foreach (GameObject o in lot.GetObjects<GameObject>())
            {
                if (o == null || o is Sim || o.HasBeenDestroyed || !o.InWorld || o.Parent != null) continue;
                string n = RuNames(o.GetType());
                int kind = n.IndexOf("|Fire|") >= 0 ? ReachUseCore.kFire : ReachUseCore.Kind("|x|", n, false, RuGardening(o.GetType()));
                if (ReachUseCore.Reach(kind) > 0f) { spots.Add(new KeyValuePair<Vector3, int>(o.Position, kind)); names.Add(o.GetType().Name); }
            }
            for (int i = 0; i < spots.Count && seen < 40; i++)
            {
                seen++;
                Vector3 p = spots[i].Key;
                int kind = spots[i].Value;
                LotLocation loc = default(LotLocation);
                World.GetLotLocationAtFloor(p, ref loc);
                Route t = sel.CreateRoute();
                t.DoRouteFail = false;
                // the game's ring per kind: mop and piles from 1 m (TrashPile.CarryRouteToObjectRadius), fire 0.5-2.5 m, plants from 0.7 m; all in the target's room
                float min = kind == ReachUseCore.kFire ? 0.5f : kind == ReachUseCore.kPlant ? 0.7f : 1f, max = kind == ReachUseCore.kFire ? 2.5f : -1f;
                if (t.PlanToPointRadialRange(p, min, max, RouteDistancePreference.PreferNearestToRouteDestination, RouteOrientationPreference.TowardsObject, lot.LotId, new int[] { loc.mRoom }).Succeeded()) continue;
                closed++;
                RuCase q = new RuCase();
                q.Goal = p; q.Kind = kind; q.Reach = ReachUseCore.Reach(kind); q.LotId = lot.LotId;
                // as RuPlace: the target's room first, then any room (run fr-m1, 2026-11-22: a pile in a closet read "wall
                // between" from the any-room plan alone, while the rule's own first plan is the one into the closet)
                int stand = ReachUseCore.kStandFar;
                RoutePlanResult np = default(RoutePlanResult);
                for (int pass = 0; pass < 2 && stand != ReachUseCore.kStandOk; pass++)
                {
                    Route w = sel.CreateRoute();
                    w.DoRouteFail = false;
                    w.SetOption(Route.RouteOption.DoLineOfSightCheckUserOverride, true);
                    RoutePlanResult wp = w.PlanToPointRadialRange(p, ReachUseCore.MinDist(kind), ReachUseCore.Reach(kind), RouteDistancePreference.PreferNearestToRouteDestination,
                        RouteOrientationPreference.TowardsObject, pass == 0 ? lot.LotId : 0UL, pass == 0 ? new int[] { loc.mRoom } : null);
                    if (!wp.Succeeded() || !BuValid(wp.mDestination)) continue;
                    int st = RuStand(wp.mDestination, q, kRuArriveSlack);
                    if (st == ReachUseCore.kStandOk || stand == ReachUseCore.kStandFar) { stand = st; np = wp; }
                }
                if (stand == ReachUseCore.kStandOk) inReach++;
                if (b.Length < 700) b.Append(" " + names[i] + " (" + RuKindName(kind) + ") at " + BuAt(p) + " room " + loc.mRoom + ": closed, "
                    + (stand == ReachUseCore.kStandOk ? "a place in reach at " + BuAt(np.mDestination) : stand == ReachUseCore.kStandSight ? "wall between" : stand == ReachUseCore.kStandLevel ? "other level" : "nothing in reach") + ";");
            }
            return b.Append(" targets " + spots.Count + ", looked at " + seen + ", closed to the game's plan " + closed + ", of them with a place in reach " + inReach).ToString();
        }
    }
}
