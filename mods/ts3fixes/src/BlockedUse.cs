using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.RouteDestinations;

namespace Ts3Fixes
{
    // blockeduse / blockedusehh (docs/686 §10, default off). A player's wish: a Sim that cannot reach a tub, a bed side, a
    // toilet or a chair because furniture or a wall is in the way should use it anyway; clipping on the last stretch and
    // a short jump onto the slot and back are fine, ordinary walking stays real. Rules in BlockedUseCore.cs.
    //
    // [verified, decompiled] Object use walks to a routing slot: RoutingComponent.RouteToSlot (one slot or a list) and
    // SimRoutingComponent.RouteToUnusedPart (beds, seats: one slot destination per free part) build a route with slot
    // destinations, plan it and hand it to SimRoutingComponent.DoRoute. DoRoute's first test is the plan result: a
    // failed plan runs the route-fail behaviour (a walk to 1-3 m from the blocker, the balloon), adds the exit reason
    // RouteFailed and returns false, and the interaction ends. The callers read only the route's plan result afterwards
    // (the slot's name hash; RouteToUnusedPart also the end point, which it compares with the slot's place).
    // [verified, storm logs] Of 6,622 failed plans of object-use interactions 403 name a piece of furniture as blocker,
    // 16 a wall; 1,719 answer "no destination" (every wanted slot refused before the search) [what refused them: not logged].
    //
    // Fix, in DoRoute, for a failed plan to an object's slots that no Sim blocks:
    //   1. Walk up: unless the Sim already stands in the object's room within kBuNearMax of a wanted slot, the game's own
    //      "near the object" plan (1-3 m, the object's room, the plan its route-fail behaviour makes) is walked as a
    //      normal route. No such plan = the object is out of reach: the game's behaviour stays.
    //   2. Clip: the route is planned again with the game's options "pass through objects" and "no slot obstruction
    //      test" (walls still count). A plan of at most kBuClipMax metres whose end no Sim stands on is walked.
    //   3. Snap: otherwise the Sim is put on the nearest wanted slot within kBuSnapMax on its level, facing as the slot
    //      does, and the route answers as arrived at that slot. A slot behind a wall is such a slot.
    //   4. Out: the Sim is remembered with the spot it came from. When it later plans a route from the slot, is done with
    //      the interaction, sits or lies in nothing, the plan fails and a plan to the spot it came from fails too, it is
    //      put back on that spot and the plan is made again.
    // The game's choice of slot stays whenever one of the wanted slots can be reached: the plan succeeds and none of this
    // runs (a double bed with one free side reachable). Parts in use and slots with weight 0 are never taken.
    //
    // Scope: blockeduse = Sims the player does not control, blockedusehh = household and selected Sims; each stands
    // alone. Humans only, not riding, not in a vehicle. Not for Sims, doors, stairs, rabbit holes and vehicles as the
    // object. Point routes (Go Here) have no slot destination and never come here. A case that kept the game's
    // behaviour is not tried again for kBuCooldownMinutes.
    // Off = compare mode: counted, nothing done. Counters 4414-4429. Check: BlockedUseCheck.
    // reachuse (ReachUse.cs, docs/686 §12) shares the DoRoute hook: failed plans without slot destinations go to its
    // rules, and its switches turn these rules on for chores at their target's own slots.
    // A plan that fails during the walk (a replan; the walk answers false, no exit reason on the Sim)
    // gets the same rules as a failed first plan, minus the game's route-fail behaviour, which the walk has had.
    // socialreach (SocialReach.cs, docs/686 §17) is the fourth step of the hook's chain, unstuck (Unstuck.cs, §18) the
    // last: every failed plan the steps before it leave to the game passes its count.
    public static partial class Fixes
    {
        const int kBuTries = 4414, kBuWould = 4415, kBuExempt = 4416, kBuNear = 4417, kBuClip = 4418, kBuSnapIn = 4419, kBuSnapOut = 4420,
            kBuWalkedOut = 4421, kBuNotKind = 4422, kBuUnreachable = 4423, kBuNoSlot = 4424, kBuNearStopped = 4425, kBuErrors = 4426,
            kBuClipStopped = 4427, kBuOutFound = 4428, kBuCooling = 4429;
        const float kBuNearMax = 3f;        // the game's outer radius of its "near the object" plan
        const float kBuNearMin = 1f;
        const float kBuClipMax = 6f;        // longest walk through objects, metres
        const float kBuSnapMax = 4f;        // farthest slot the Sim is put on, metres (flat)
        const float kBuSnapMaxDy = 1f;      // and in height
        const float kBuSimRadius = 0.5f;    // a Sim this close to a slot holds it
        const float kBuLeftDist = 0.75f;    // a remembered Sim farther than this from its slot has walked away
        const float kBuCooldownMinutes = 30f, kBuExitMaxHours = 24f;
        const int kBuMaxRecs = 256, kBuMaxExits = 64, kBuMaxCool = 256, kBuMaxDests = 8;

        // the slot destinations of a route, by the route's handle (the native list cannot be read back)
        sealed class BuRec { public int N; public SlotArrayDestination[] D = new SlotArrayDestination[kBuMaxDests]; }
        // one handled failure
        sealed class BuCase
        {
            public GameObject Obj; public BuRec Rec; public Route Near; public Vector3 Origin, Goal;
            public RoutePlanResult Saved; public bool OptObjects, OptOverride, OptChecks;
        }
        // a Sim that was let onto a slot
        // Obj: the object whose slot it is (0 = none). Owner: 0 blockeduse, 1 woohoosnap, 2 pairsnap (WooHooSnap.cs)
        sealed class BuExit { public Vector3 Origin, Slot; public WeakReference Ia; public long At; public bool Snap; public ulong Obj; public int Owner; }

        static readonly Dictionary<uint, BuRec> sBuRecs = new Dictionary<uint, BuRec>();
        static readonly Dictionary<ulong, BuExit> sBuExits = new Dictionary<ulong, BuExit>();  // by Sim object id
        static readonly Dictionary<ulong, long> sBuCool = new Dictionary<ulong, long>();       // Sim ^ object -> ticks of the last kept case
        static readonly LineCapCore sBuCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kBuSites = { "begin", "approach", "out", "destination", "clip end" };
        static readonly int[] sBuSiteErrors = new int[5];
        static string sBuLast = "", sBuLastError = "", sBuMissing = "";
        static bool sBuBusy;

        static void ApplyBlockedUse()
        {
            sBuMissing = "";
            if (!Patch(typeof(ScriptCore.Route), "AddDestination", new Type[] { typeof(RouteDestination) }, typeof(RouteBackoffHook), "BuDestHook", "BuDestOrig")) sBuMissing += "Route.AddDestination ";
            if (!Patch(typeof(ScriptCore.Route), "ClearDestinationList", Type.EmptyTypes, typeof(RouteBackoffHook), "BuClearHook", "BuClearOrig")) sBuMissing += "Route.ClearDestinationList ";
            if (!Patch(typeof(SimRoutingComponent), "DoRoute", new Type[] { typeof(Route) }, typeof(BuRouteHook), "RouteHook", "RouteOrig")) sBuMissing += "SimRoutingComponent.DoRoute ";
            sBuMissing = sBuMissing.Trim();
            if (sBuMissing.Length > 0) AppliedLog().Add("blockeduse: not found: " + sBuMissing);
        }

        static void BuError(int site, Exception e)
        {
            PatchCounters()[kBuErrors]++;
            sBuLastError = "blockeduse: error at " + kBuSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sBuSiteErrors[site]++ < 3) DiagLine(sBuLastError);
        }

        static void BuLine(string text)
        {
            sBuLast = text;
            if (sBuCap.Take(System.Environment.TickCount)) DiagLine("blockeduse: " + text);
        }

        static string BuWho(Sim s) { return s == PlumbBob.SelectedActor ? "selected" : IsPlayerSim(s) ? "household" : "npc"; }

        static string BuAt(Vector3 v) { return v.x.ToString("0.0") + "," + v.y.ToString("0.0") + "," + v.z.ToString("0.0"); }

        static float BuFlat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return (float)Math.Sqrt(dx * dx + dz * dz); }

        static bool BuValid(Vector3 v) { return v != Vector3.Invalid && v != Vector3.OutOfWorld && v != Vector3.Origin; }

        // ---- the route's slot destinations ----

        static void BuNoteDest(uint handle, RouteDestination d)
        {
            SlotArrayDestination s = d as SlotArrayDestination;
            if (s == null || handle == 0 || s.mSlotTargets == null) return;
            BuRec rec;
            if (!sBuRecs.TryGetValue(handle, out rec))
            {
                if (sBuRecs.Count >= kBuMaxRecs) sBuRecs.Clear();  // routes in flight lose their list: their failure keeps the game's behaviour
                rec = new BuRec();
                sBuRecs[handle] = rec;
            }
            if (rec.N < kBuMaxDests) rec.D[rec.N++] = s;
        }

        static void BuNoteClear(uint handle)
        {
            BuRec rec;
            if (sBuRecs.Count == 0 || !sBuRecs.TryGetValue(handle, out rec)) return;
            for (int i = 0; i < rec.N; i++) rec.D[i] = null;
            rec.N = 0;
        }

        public partial class RouteBackoffHook
        {
            public void BuDestHook(RouteDestination destination)
            {
                try { BuNoteDest(Handle, destination); }
                catch (Exception e) { BuError(3, e); }
                BuDestOrig(destination);
            }

            public void BuDestOrig(RouteDestination destination) { }  // receives AddDestination's original body

            public void BuClearHook()
            {
                try { BuNoteClear(Handle); }
                catch (Exception e) { BuError(3, e); }
                BuClearOrig();
            }

            public void BuClearOrig() { }  // receives ClearDestinationList's original body
        }

        // The wanted slots of a failed route: place, facing, flat distance and height from the Sim. A slot of a
        // destination with weight 0 (a part in use), a slot with weight 0, an unknown place or a Sim on it is not usable.
        sealed class BuSlots
        {
            public int N; public Slot[] Name = new Slot[16]; public GameObject[] Obj = new GameObject[16]; public uint[] Tag = new uint[16];
            public Vector3[] Pos = new Vector3[16]; public float[] Dist = new float[16], Dy = new float[16]; public bool[] Usable = new bool[16];
        }

        static bool BuSimAt(Vector3 pos, Sim self, Lot lot)
        {
            if (lot == null) return false;
            foreach (Sim o in lot.GetAllActors())
                if (o != null && o != self && !o.HasBeenDestroyed && BuFlat(o.Position, pos) < kBuSimRadius && Math.Abs(o.Position.y - pos.y) < kBuSnapMaxDy) return true;
            return false;
        }

        static BuSlots BuReadSlots(Sim sim, BuRec rec, bool checkSims)
        {
            BuSlots s = new BuSlots();
            Vector3 at = sim.Position;
            for (int i = 0; i < rec.N && s.N < 16; i++)
            {
                SlotArrayDestination d = rec.D[i];
                GameObject o = d != null ? d.mTargetObject as GameObject : null;
                if (o == null || o.HasBeenDestroyed || d.mSlotTargets == null) continue;
                foreach (SlotArrayDestination.SlotTarget t in d.mSlotTargets)
                {
                    if (t == null || t.mSlot == Slot.None || s.N >= 16) continue;
                    int k = s.N++;
                    s.Name[k] = t.mSlot; s.Obj[k] = o; s.Tag[k] = d.Tag;
                    Vector3 p = o.GetPositionOfSlot(t.mSlot, sim.SimDescription.AgeGenderSpecies, false);
                    s.Pos[k] = p;
                    bool ok = d.Weight > 0f && t.mWeight > 0f && BuValid(p) && BuFlat(p, o.Position) < 12f;
                    s.Dist[k] = ok ? BuFlat(p, at) : float.MaxValue;
                    s.Dy[k] = ok ? p.y - at.y : 0f;
                    s.Usable[k] = ok && !(checkSims && BuSimAt(p, sim, o.LotCurrent));
                }
            }
            return s;
        }

        // Does the route hold slot destinations (the last plan was a plan to routing slots)?
        static bool BuIsSlotRoute(Route r)
        {
            BuRec rec;
            return sBuRecs.TryGetValue(r.Handle, out rec) && rec.N > 0;
        }

        // ---- a failed plan in DoRoute ----

        static bool BuSimOk(Sim sim)
        {
            if (!sim.IsHuman || sim.IsInRidingPosture || sim.Posture is SittingInVehicle) return false;
            return sim.RoutingComponent != null && sim.RoutingComponent.RoutingParent == null && sim.LotCurrent != null;
        }

        static bool BuObjOk(GameObject o)
        {
            if (o == null || o is Sim || o.HasBeenDestroyed || !o.InWorld || o.InInventory) return false;
            if (o.PortalComponent != null || o is RabbitHole || o is Vehicle) return false;
            Lot lot = o.LotCurrent;
            return lot != null && !lot.IsWorldLot;
        }

        static ulong BuCoolKey(Sim sim, GameObject o) { return sim.ObjectId.Value ^ (o.ObjectId.Value * 31UL); }

        static void BuCoolSet(Sim sim, GameObject o)
        {
            if (sBuCool.Count >= kBuMaxCool) sBuCool.Clear();
            sBuCool[BuCoolKey(sim, o)] = SimClock.CurrentTicks;
        }

        // Null = the game's behaviour. Reads, counts and plans the walk up; moves nothing.
        static BuCase BuBegin(Sim sim, Route r)
        {
            BuRec rec;
            if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null || !sBuRecs.TryGetValue(r.Handle, out rec) || rec.N == 0) return null;
            GameObject obj = r.DestObj as GameObject;
            if (obj == null || !ReferenceEquals(obj, rec.D[rec.N - 1].mTargetObject)) return null;  // the list is of another plan of this route
            int[] c = PatchCounters();
            c[kBuTries]++;
            RoutePlanResult pr = r.PlanResult;
            bool blockerSim = pr.mBlockerType == RouteBlockerType.Sim || (pr.mBlockerObjectId != 0 && GameObject.GetObject(new ObjectGuid(pr.mBlockerObjectId)) is Sim);
            bool kind = BlockedUseCore.Kind((int)pr.mType, blockerSim) && BuSimOk(sim) && BuObjOk(obj);
            int d = BlockedUseCore.Decide(kind, IsPlayerSim(sim), ShortcutOn("blockeduse"), ShortcutOn("blockedusehh"));
            if (kind && d != BlockedUseCore.kGo && RuSlotCase(sim, obj)) d = BlockedUseCore.kGo;  // reachuse (ReachUse.cs, docs/686 §12): a chore at its target's own slots
            if (d == BlockedUseCore.kNone) { c[kBuNotKind]++; return null; }
            if (d == BlockedUseCore.kWould) { c[kBuWould]++; return null; }
            if (d == BlockedUseCore.kExempt) { c[kBuExempt]++; return null; }
            long last;
            if (sBuCool.TryGetValue(BuCoolKey(sim, obj), out last) && BlockedUseCore.Cooling(SimClock.CurrentTicks, last, SimClock.ConvertToTicks(kBuCooldownMinutes, TimeUnit.Minutes)))
            { c[kBuCooling]++; return null; }

            BuCase k = new BuCase();
            k.Obj = obj; k.Rec = rec; k.Saved = pr;
            BuSlots s = BuReadSlots(sim, rec, false);
            float nearest = float.MaxValue;
            for (int i = 0; i < s.N; i++) if (s.Usable[i] && s.Dist[i] < nearest) nearest = s.Dist[i];
            if (nearest == float.MaxValue) { c[kBuNoSlot]++; BuCoolSet(sim, obj); return null; }
            if (BlockedUseCore.NeedNear(sim.LotCurrent == obj.LotCurrent, sim.Level == obj.Level, sim.RoomId == obj.RoomId, nearest, kBuNearMax))
            {
                Route near = sim.CreateRoute();
                near.DoRouteFail = false;
                near.ExecutionFromNonSimTaskIsSafe = r.ExecutionFromNonSimTaskIsSafe;
                near.SetOption(Route.RouteOption.DoLineOfSightCheckUserOverride, true);
                near.PlanToPointRadialRange(obj, obj.PositionOnFloor, kBuNearMin, kBuNearMax, RouteDistancePreference.PreferNearestToRouteDestination,
                    RouteOrientationPreference.TowardsObject, obj.LotCurrent.LotId, new int[] { obj.RoomId });
                if (!near.PlanResult.Succeeded()) { c[kBuUnreachable]++; BuCoolSet(sim, obj); return null; }
                k.Near = near;
                c[kBuNear]++;
            }
            return k;
        }

        static void BuRestoreOptions(Route r, BuCase k)
        {
            r.SetOption(Route.RouteOption.PassThroughObjects, k.OptObjects);
            r.SetOption(Route.RouteOption.UserOverrideSlotObstruction, k.OptOverride);
            r.SetOption(Route.RouteOption.DoSlotObstructionChecksUserOverride, k.OptChecks);
        }

        static void BuRemember(Sim sim, BuCase k, bool snap)
        {
            ulong id = sim.ObjectId.Value;
            BuExit x;
            if (!sBuExits.TryGetValue(id, out x))
            {
                if (sBuExits.Count >= kBuMaxExits) sBuExits.Clear();
                x = new BuExit();
                x.Origin = k.Origin;  // a Sim let on twice keeps the first spot: that one it walked to
                sBuExits[id] = x;
            }
            x.Slot = k.Goal; x.Snap = snap; x.At = SimClock.CurrentTicks; x.Owner = 0;
            x.Obj = k.Obj != null ? k.Obj.ObjectId.Value : 0UL;
            InteractionInstance ia = sim.CurrentInteraction;
            x.Ia = ia != null ? new WeakReference(ia) : null;
        }

        // After the walk up. kClip: r holds a plan through objects, options still relaxed (the caller walks it and puts
        // them back). kSnap: the Sim stands on the slot and r answers "arrived". kKeep: r is as it was.
        static int BuApproach(Sim sim, Route r, BuCase k)
        {
            int[] c = PatchCounters();
            GameObject obj = k.Obj;
            if (sim.HasBeenDestroyed || !BuObjOk(obj)) return BlockedUseCore.kKeep;
            bool here = sim.LotCurrent == obj.LotCurrent && sim.Level == obj.Level;
            k.Origin = sim.Position;
            k.OptObjects = r.GetOption(Route.RouteOption.PassThroughObjects);
            k.OptOverride = r.GetOption(Route.RouteOption.UserOverrideSlotObstruction);
            k.OptChecks = r.GetOption(Route.RouteOption.DoSlotObstructionChecksUserOverride);
            bool clipOk = false, simAtGoal = false;
            float len = 0f;
            RoutePlanResult pr = k.Saved;
            if (here)
            {
                r.SetOption(Route.RouteOption.PassThroughObjects, true);
                r.SetOption(Route.RouteOption.UserOverrideSlotObstruction, true);
                r.SetOption(Route.RouteOption.DoSlotObstructionChecksUserOverride, false);
                pr = r.Plan();
                clipOk = pr.Succeeded();
                if (clipOk)
                {
                    len = pr.mDistanceTotal > 0f ? pr.mDistanceTotal : r.GetRouteLength();
                    simAtGoal = BuSimAt(pr.mDestination, sim, obj.LotCurrent);
                }
            }
            BuSlots s = BuReadSlots(sim, k.Rec, true);
            int pick = BlockedUseCore.PickSlot(s.Dist, s.Dy, s.Usable, s.N, kBuSnapMax, kBuSnapMaxDy);
            int step = BlockedUseCore.Step(here, clipOk, len, kBuClipMax, simAtGoal, pick);
            string head = BuWho(sim) + " " + sim.FullName + " -> " + obj.GetType().Name + " 0x" + obj.ObjectId.Value.ToString("X") + " (" + k.Saved.mType + ", blocker " + k.Saved.mBlockerType + ")";
            if (step == BlockedUseCore.kClip)
            {
                k.Goal = pr.mDestination;
                c[kBuClip]++;
                BuLine(head + ": clip " + len.ToString("0.0") + " m to slot 0x" + pr.mDestSlotNameHash.ToString("X") + " at " + BuAt(k.Goal) + (k.Near != null ? ", walked up first" : ""));
                return step;
            }
            BuRestoreOptions(r, k);
            // the player's door lock wins: never a snap into a room whose doors the player locked against this Sim (HhShared.cs, docs/686 §41)
            if (step == BlockedUseCore.kSnap && s.Obj[pick].RoomId != sim.RoomId && HhDoorLockedAgainst(sim, s.Obj[pick].LotCurrent, s.Obj[pick].RoomId)) step = BlockedUseCore.kKeep;
            if (step == BlockedUseCore.kSnap)
            {
                GameObject target = s.Obj[pick];
                Vector3 pos = s.Pos[pick], fwd = target.GetForwardOfSlot(s.Name[pick]);
                bool fade = HhSnapSeen(sim, pos);  // every snap that can be seen is faded (HhShared.cs, docs/686 §41)
                HhSnap(sim, pos, fwd, BuValid(fwd), fade);
                RoutePlanResult ok = new RoutePlanResult();
                ok.mType = RoutePlanResultType.Succeeded;
                ok.mDestSlotNameHash = (uint)s.Name[pick];
                ok.mDestination = pos;
                ok.mDestinationTag = s.Tag[pick];
                r.PlanResult = ok;
                k.Goal = pos;
                BuRemember(sim, k, true);
                c[kBuSnapIn]++;
                BuLine(head + ": snap in " + s.Dist[pick].ToString("0.0") + " m to slot 0x" + ((uint)s.Name[pick]).ToString("X") + " at " + BuAt(pos) + " from " + BuAt(k.Origin)
                    + (clipOk ? " (plan through objects " + len.ToString("0.0") + " m" + (simAtGoal ? ", a Sim at its end" : "") + ")" : " (no plan through objects)") + (k.Near != null ? ", walked up first" : ""));
                return step;
            }
            r.PlanResult = k.Saved;
            c[kBuNoSlot]++;
            BuCoolSet(sim, obj);
            BuLine(head + ": kept, " + (here ? "no slot within " + kBuSnapMax + " m (" + s.N + " wanted)" : "the Sim is not on the object's level"));
            return step;
        }

        // `this` is the game's SimRoutingComponent. The only DoRoute hook (issue #92, docs/130 §11): petrelax's step runs
        // here first, and the failed-plan rules answer with a step code, so the game's own DoRoute body always runs one
        // hook frame deep (the game's route-fail behaviour used to run 9 of our frames deep, two hook layers and a chain of
        // 7 rule calls). A Sim saved mid-route has a frame in RouteHook, and a build that changes its IL resets those Sims
        // once at the first load (docs/242 §2): changed 2026-11-22 (replans to RouteEnded) and 2026-11-24 (this fold;
        // no release had a DoRoute hook, so release saves are not affected). New rules belong in RouteBlocked and what
        // it calls, as step codes.
        public class BuRouteHook : SimRoutingComponent
        {
            // Step codes of the failed-plan rules. Final: answer true / false, the game's DoRoute body, the same with its
            // route-fail balloon off. Inside RouteBlocked only: next rule, unstuck's look, the game's answer (kStEa: the
            // body before a walk, false after one, which has had it).
            const int kStNo = 0, kStYes = 1, kStOrig = 2, kStQuiet = 3, kStNext = 4, kStStuck = 5, kStEa = 6;

            public bool RouteHook(Route r)
            {
                if (r == null) return RouteOrig(r);
                if (r.PlanResult.mType != RoutePlanResultType.Succeeded) PrxPlanFailed(OwnerSim, r);  // petrelax (PetRelax.cs) may plan again
                int s;
                if (r.PlanResult.mType != RoutePlanResultType.Succeeded) s = RouteBlocked(r, false);
                else if (AlwRoute(r)) return true;  // alibiwalk (AlibiWalk.cs, docs/691): an unseen NPC's long walk cut to its tail first, off by default
                else s = RouteEnded(r);
                if (s == kStOrig) return RouteOrig(r);
                if (s != kStQuiet) return s == kStYes;
                bool show = r.DoRouteFail;  // viewlenient: the game's answer for a failed plan, its balloon switched off
                r.DoRouteFail = false;
                try { return RouteOrig(r); }
                finally { r.DoRouteFail = show; }
            }

            public bool RouteOrig(Route r) { return false; }  // receives DoRoute's original body

            bool AlwRoute(Route r) { AlwBefore(OwnerSim, r); return RouteOrig(r); }

            // The walk answered false. A plan that failed on the way (a replan) with no exit reason on the Sim gets the
            // rules of a failed first plan; a walk that was cancelled or interrupted keeps its answer.
            int RouteEnded(Route r)
            {
                Sim sim = OwnerSim;
                if (r.PlanResult.mType == RoutePlanResultType.Succeeded || sim == null || sim.HasBeenDestroyed || sim.HasExitReason()) return kStNo;
                return RouteBlocked(r, true);
            }

            // The rules in order, each called from here (one frame under RouteHook): a rule's own walk runs 3 frames deep.
            int RouteBlocked(Route r, bool afterWalk)
            {
                Sim sim = OwnerSim;
                int s = RouteUse(sim, r, afterWalk);
                if (s == kStNext) s = RouteReach(sim, r, afterWalk);
                if (s == kStNext) s = RouteView(sim, r, afterWalk);
                if (s == kStNext) s = RouteSocial(sim, r, afterWalk);
                if (s == kStStuck) s = RouteStuck(r);  // a failed plan the rules above leave to the game: unstuck's look first (Unstuck.cs)
                if (s == kStEa) s = afterWalk ? kStNo : kStOrig;
                return s;
            }

            // No catch round a walk: a reset must unwind the Sim's stack.
            int RouteUse(Sim sim, Route r, bool afterWalk)
            {
                // woohoosnap / pairsnap (WooHooSnap.cs, docs/686 §20): a WooHoo partner's or a paired social's failed plan
                WhsCase w = null;
                try { w = WhsBegin(sim, r, afterWalk); }
                catch (ResetException) { throw; }
                catch (Exception e) { WhsError(false, 0, e); }
                if (w != null) return RouteSnap(sim, r, w);
                BuCase k = null;
                try { k = BuBegin(sim, r); }
                catch (ResetException) { throw; }
                catch (Exception e) { BuError(0, e); }
                if (k == null) return kStNext;
                if (k.Near != null && !RouteOrig(k.Near))
                {
                    PatchCounters()[kBuNearStopped]++;  // cancelled or stopped on the way: the game's answer for the failed plan
                    return kStStuck;
                }
                int step = BlockedUseCore.kKeep;
                try { step = BuApproach(sim, r, k); }
                catch (ResetException) { throw; }
                catch (Exception e)
                {
                    BuError(1, e);
                    try { BuRestoreOptions(r, k); r.PlanResult = k.Saved; } catch (Exception) { }
                }
                if (step == BlockedUseCore.kSnap) return kStYes;
                if (step != BlockedUseCore.kClip) return kStStuck;
                bool walked;
                try { walked = RouteOrig(r); }
                finally { BuRestoreOptions(r, k); }
                try
                {
                    if (walked) BuRemember(sim, k, false);
                    else PatchCounters()[kBuClipStopped]++;
                }
                catch (ResetException) { throw; }
                catch (Exception e) { BuError(4, e); }
                return walked ? kStYes : kStNo;
            }

            // woohoosnap / pairsnap (WooHooSnap.cs, docs/686 §20): the Sim walks up to the object's room when a normal plan
            // gets it there, then it is put on its spot. A walk up that was cancelled keeps the game's answer.
            int RouteSnap(Sim sim, Route r, WhsCase w)
            {
                if (w.Near != null && !RouteOrig(w.Near) && (sim.HasBeenDestroyed || sim.HasExitReason()))
                {
                    try { WhsKeep(WooHooSnapCore.kWhyStopped, w.Head); } catch (Exception) { }
                    return kStEa;
                }
                bool arrived = false;
                try { arrived = w.Pair ? PrsArrive(sim, r, w) : WhsArrive(sim, r, w); }
                catch (ResetException) { throw; }
                catch (Exception e) { WhsError(w.Pair, 1, e); }
                return arrived ? kStYes : kStStuck;
            }

            // reachuse (ReachUse.cs, docs/686 §12): a failed plan that blockeduse left alone. A chore whose route has no
            // slot destination is done from within reach of its target; anything else gets the game's answer.
            int RouteReach(Sim sim, Route r, bool afterWalk)
            {
                RuCase q = null;
                try { q = RuBegin(sim, r, afterWalk); }
                catch (ResetException) { throw; }
                catch (Exception e) { RuError(0, e); }
                if (q == null) return kStNext;
                if (q.Near != null && !RouteOrig(q.Near))
                {
                    PatchCounters()[kRuStopped]++;  // cancelled or stopped on the way: the game's answer for the failed plan
                    return kStStuck;
                }
                bool arrived = false;
                try { arrived = RuArrive(sim, r, q); }
                catch (ResetException) { throw; }
                catch (Exception e) { RuError(1, e); }
                return arrived ? kStYes : kStStuck;
            }

            // viewlenient (ViewLenient.cs, docs/686 §16): a failed plan that blockeduse and reachuse left alone. A Sim that
            // only looks at something (contemplate, view, watch) looks from where it stands or from the nearest place
            // with a view; with no such place the interaction ends without the route-fail balloon.
            int RouteView(Sim sim, Route r, bool afterWalk)
            {
                VlCase v = null;
                try { v = VlBegin(sim, r, afterWalk); }
                catch (ResetException) { throw; }
                catch (Exception e) { VlError(0, e); }
                if (v == null) return kStNext;
                bool arrived = false;
                if (!v.Quiet && (v.Near == null || RouteOrig(v.Near)))
                {
                    try { arrived = VlArrive(sim, r, v); }
                    catch (ResetException) { throw; }
                    catch (Exception e) { VlError(1, e); }
                }
                else if (!v.Quiet) PatchCounters()[kVlStopped]++;  // cancelled or stopped on the way
                if (arrived) return kStYes;
                try { VlQuietEnd(sim, v, afterWalk); }
                catch (ResetException) { throw; }
                catch (Exception e) { VlError(2, e); }
                // the game's own answer for a failed plan (exit reason RouteFailed, false), its balloon switched off (RouteHook)
                return afterWalk ? kStNo : kStQuiet;
            }

            // socialreach (SocialReach.cs, docs/686 §17): a failed plan of a Sim in a talk social that the rules above
            // left alone. Within reach of the partner the Sim talks from where it stands; the starter otherwise walks
            // to the nearest place near the partner.
            int RouteSocial(Sim sim, Route r, bool afterWalk)
            {
                SrCase s = null;
                try { s = SrBegin(sim, r, afterWalk); }
                catch (ResetException) { throw; }
                catch (Exception e) { SrError(0, e); }
                if (s == null) return kStStuck;
                if (s.Near != null && !RouteOrig(s.Near))
                {
                    PatchCounters()[kSrStopped]++;  // cancelled or stopped on the way: the game's answer for the failed plan
                    return kStEa;
                }
                bool arrived = false;
                try { arrived = SrArrive(sim, r, s); }
                catch (ResetException) { throw; }
                catch (Exception e) { SrError(1, e); }
                return arrived ? kStYes : kStEa;
            }

            // unstuck (Unstuck.cs, docs/686 §18): every failed plan the rules above leave to the game is counted per
            // Sim and spot. A Sim found boxed in walks out through the objects (a real walk, walls count), its route is
            // planned again and walked when it now has a plan; anything else gets the game's answer.
            int RouteStuck(Route r)
            {
                Sim sim = OwnerSim;
                UnCase k = null;
                try { k = UnBegin(sim, r); }
                catch (ResetException) { throw; }
                catch (Exception e) { UnError(0, e); }
                if (k == null) return kStEa;
                bool walked = RouteOrig(k.Out);
                bool again = false;
                try { again = UnAfter(sim, r, k, walked); }
                catch (ResetException) { throw; }
                catch (Exception e) { UnError(1, e); }
                return again ? kStOrig : kStEa;
            }
        }

        // ---- out again: called after every Plan / Replan (RouteBackoff.cs); one count test when nobody is remembered ----

        internal static RoutePlanResult BuAfterPlan(ScriptCore.Route route, int kind, RoutePlanResult r)
        {
            if (sUnRecs.Count != 0) UnPlanSeen(route, kind, r);  // unstuck (Unstuck.cs, docs/686 §18): a plan that leads away clears the Sim's count
            if (sBuExits.Count == 0 || sBuBusy) return r;
            try
            {
                IScriptProxy f = route.Follower;
                Sim sim = f != null ? f.Target as Sim : null;
                BuExit x;
                if (sim == null || !sBuExits.TryGetValue(sim.ObjectId.Value, out x)) return r;
                int[] c = PatchCounters();
                ulong id = sim.ObjectId.Value;
                Posture p = sim.Posture;
                bool inPosture = p != null && p.Container != null && !ReferenceEquals(p.Container, sim);
                object ia = x.Ia != null ? x.Ia.Target : null;
                int look = BlockedUseCore.ExitLook(ia != null && ReferenceEquals(ia, sim.CurrentInteraction), inPosture, BuFlat(sim.Position, x.Slot), kBuLeftDist,
                    SimClock.CurrentTicks - x.At, SimClock.ConvertToTicks(kBuExitMaxHours, TimeUnit.Hours));
                if (look == BlockedUseCore.kExitWait) return r;
                if (look == BlockedUseCore.kExitGone || r.Succeeded()) { sBuExits.Remove(id); c[kBuWalkedOut]++; return r; }
                if (kind > 1) return r;  // a plan from a point the caller named says nothing about where the Sim stands
                sBuBusy = true;
                try
                {
                    Route probe = sim.CreateRoute();
                    probe.DoRouteFail = false;
                    bool probeOk = BuValid(x.Origin) && probe.PlanToPoint(x.Origin).Succeeded();
                    sBuExits.Remove(id);
                    if (!BlockedUseCore.ExitSnap(false, true, probeOk)) { c[kBuWalkedOut]++; return r; }  // it can walk: the plan failed for its own reasons
                    Vector3 to = x.Origin, fwd = sim.ForwardVector;
                    bool found = false;
                    if (!BuValid(to) || !probe.IsPointRoutable(to))
                    {
                        Vector3 fp, ff;
                        if (Sims3.Gameplay.GlobalFunctions.FindGoodLocation(sim, new World.FindGoodLocationParams(BuValid(to) ? to : sim.Position), out fp, out ff)) { to = fp; fwd = ff; found = true; }
                    }
                    if (!BuValid(to)) return r;
                    Vector3 from = sim.Position;
                    HhSnap(sim, to, fwd, found && BuValid(fwd), x.Owner == 0 && HhSnapSeen(sim, to));  // faded when seen; a woohoosnap / pairsnap exit keeps its own timing
                    c[kBuSnapOut]++;
                    if (found) c[kBuOutFound]++;
                    if (x.Owner != 0) WhsOut(sim, x.Owner, from, to);  // woohoosnap / pairsnap (WooHooSnap.cs, docs/686 §20)
                    BuLine(BuWho(sim) + " " + sim.FullName + ": snap out from " + BuAt(from) + " to " + BuAt(to) + (found ? " (a spot found near the one it came from)" : " (the spot it came from)")
                        + ", came by " + (x.Snap ? "snap" : "clip"));
                    r = kind == 0 ? route.Plan() : route.Replan();
                }
                finally { sBuBusy = false; }
            }
            catch (ResetException) { throw; }
            catch (Exception e) { BuError(2, e); }
            return r;
        }

        // ---- check ----

        static string BuCounters()
        {
            int[] c = PatchCounters();
            string sites = "";
            for (int i = 0; i < sBuSiteErrors.Length; i++)
                if (sBuSiteErrors[i] > 0) sites += (sites.Length > 0 ? ", " : "") + kBuSites[i] + " " + sBuSiteErrors[i];
            return "blockeduse: failed slot plans " + c[kBuTries] + ", not this kind (a Sim blocks, portal locked, pet, object kind) " + c[kBuNotKind] + "; on: walked up " + c[kBuNear]
                + ", clip " + c[kBuClip] + ", snap in " + c[kBuSnapIn] + ", snap out " + c[kBuSnapOut] + " (to a found spot " + c[kBuOutFound] + "), walked out " + c[kBuWalkedOut]
                + "; kept: object out of reach " + c[kBuUnreachable] + ", no slot in range " + c[kBuNoSlot] + ", walk up stopped " + c[kBuNearStopped] + ", clip walk stopped " + c[kBuClipStopped]
                + ", cooling down " + c[kBuCooling] + ", exempt (household without blockedusehh) " + c[kBuExempt] + "; off: would " + c[kBuWould]
                + "; errors " + c[kBuErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sBuLastError + ")" : "") + "; remembered Sims " + sBuExits.Count + ", routes with slots " + sBuRecs.Count
                + "; " + GlMembers(sBuMissing) + (sBuLast.Length > 0 ? "; last: " + sBuLast : "");
        }

        // BlockedUseCheck run: the rules with controls (read-only). Bug case: an NPC's plan to a tub's slots answers "no
        // path" with a dresser as blocker; near the tub a plan through objects of 1.5 m is walked; with no such plan the
        // Sim is put on the slot 0.8 m away. result: the counters and the last line. lot: tubs, showers, toilets, sinks,
        // beds and seats of the selected Sim's lot whose routing slots the selected Sim cannot plan to now (read-only plans).
        public static object BlockedUseCheck(string what)
        {
            if (what == "result") return BuCounters();
            if (what == "lot") return BuLot();
            if (what != "run") return "blockeduse <run|result|lot>";
            bool on = ShortcutOn("blockeduse"), hh = ShortcutOn("blockedusehh");
            const int G = BlockedUseCore.kGo;
            bool kind = BlockedUseCore.Kind(BlockedUseCore.kNoPath, false) && BlockedUseCore.Kind(BlockedUseCore.kNoDestination, false);
            float[] dist = { 2.5f, 0.8f, 0.3f }, dy = { 0f, 0f, 0f };
            bool[] usable = { true, true, false };
            int pick = BlockedUseCore.PickSlot(dist, dy, usable, 3, kBuSnapMax, kBuSnapMaxDy);
            bool clip = BlockedUseCore.Step(true, true, 1.5f, kBuClipMax, false, pick) == BlockedUseCore.kClip;
            bool snap = pick == 1 && BlockedUseCore.Step(true, false, 0f, kBuClipMax, false, pick) == BlockedUseCore.kSnap
                && BlockedUseCore.Step(true, true, 14f, kBuClipMax, false, pick) == BlockedUseCore.kSnap;
            bool outAgain = BlockedUseCore.ExitLook(false, false, 0.1f, kBuLeftDist, 10, 1000) == BlockedUseCore.kExitCheck && BlockedUseCore.ExitSnap(false, true, false);
            bool fixedNow = kind && clip && snap && outAgain && BlockedUseCore.Decide(true, false, on, hh) == G;
            bool ctlKind = !BlockedUseCore.Kind(BlockedUseCore.kNoPath, true) && !BlockedUseCore.Kind(18, false) && !BlockedUseCore.Kind(3, false) && !BlockedUseCore.Kind(BlockedUseCore.kSucceeded, false);
            bool ctlScope = BlockedUseCore.Decide(true, true, true, false) == BlockedUseCore.kExempt && BlockedUseCore.Decide(true, true, false, true) == G
                && BlockedUseCore.Decide(true, false, false, true) == BlockedUseCore.kWould && BlockedUseCore.Decide(true, true, false, false) == BlockedUseCore.kWould
                && BlockedUseCore.Decide(false, false, true, true) == BlockedUseCore.kNone;
            bool ctlNear = BlockedUseCore.NeedNear(true, true, false, 1f, kBuNearMax) && BlockedUseCore.NeedNear(true, false, true, 1f, kBuNearMax)
                && BlockedUseCore.NeedNear(true, true, true, 5f, kBuNearMax) && !BlockedUseCore.NeedNear(true, true, true, 2f, kBuNearMax);
            bool ctlKeep = BlockedUseCore.Step(false, true, 1f, kBuClipMax, false, 1) == BlockedUseCore.kKeep && BlockedUseCore.Step(true, false, 0f, kBuClipMax, false, -1) == BlockedUseCore.kKeep
                && BlockedUseCore.Step(true, true, 1f, kBuClipMax, true, -1) == BlockedUseCore.kKeep
                && BlockedUseCore.PickSlot(new float[] { 5f, 1f }, new float[] { 0f, 3f }, new bool[] { true, true }, 2, kBuSnapMax, kBuSnapMaxDy) == -1;
            bool ctlOut = BlockedUseCore.ExitLook(true, false, 0.1f, kBuLeftDist, 10, 1000) == BlockedUseCore.kExitWait && BlockedUseCore.ExitLook(false, true, 0.1f, kBuLeftDist, 10, 1000) == BlockedUseCore.kExitWait
                && BlockedUseCore.ExitLook(false, false, 2f, kBuLeftDist, 10, 1000) == BlockedUseCore.kExitGone && BlockedUseCore.ExitLook(false, false, 0.1f, kBuLeftDist, 2000, 1000) == BlockedUseCore.kExitGone
                && !BlockedUseCore.ExitSnap(true, true, false) && !BlockedUseCore.ExitSnap(false, false, false) && !BlockedUseCore.ExitSnap(false, true, true);
            bool ctlCool = BlockedUseCore.Cooling(100, 90, 20) && !BlockedUseCore.Cooling(100, 70, 20) && !BlockedUseCore.Cooling(100, 110, 20);
            bool controls = ctlKind && ctlScope && ctlNear && ctlKeep && ctlOut && ctlCool;
            return "blockeduse check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): a slot closed by furniture or a wall is used (clip, else snap, and out again)=" + fixedNow
                + "; control: a Sim as blocker, portal locked, aborted kept=" + ctlKind + ", scope per switch=" + ctlScope + ", walk up unless near in the room=" + ctlNear
                + ", other level, no slot, a Sim on the slot kept=" + ctlKeep + ", out only when done, standing, at the slot and unable to walk=" + ctlOut + ", cooldown=" + ctlCool
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sBuMissing.Length == 0) + "; " + BuCounters();
        }

        static string BuLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null || sel.SimDescription == null) return "blockeduse lot: no selected Sim on a lot";
            System.Text.StringBuilder b = new System.Text.StringBuilder("blockeduse lot:");
            int objects = 0, closed = 0, slots = 0;
            foreach (GameObject o in sel.LotCurrent.GetObjects<GameObject>())
            {
                if (objects >= 60) break;
                string n = o.GetType().Name;
                if (!BuObjOk(o) || !(n.StartsWith("Bathtub") || n.StartsWith("Shower") || n.StartsWith("Toilet") || n.StartsWith("Sink") || n.StartsWith("Bed") || n.StartsWith("Chair") || n.StartsWith("Sofa"))) continue;
                Slot[] rs = o.GetRoutingSlots();
                if (rs == null || rs.Length == 0) continue;
                objects++;
                int bad = 0;
                string first = "";
                foreach (Slot s in rs)
                {
                    slots++;
                    Route t = sel.CreateRoute();
                    t.DoRouteFail = false;
                    RoutePlanResult pr = t.PlanToSlot(o, s);
                    if (pr.Succeeded()) continue;
                    bad++;
                    if (first.Length == 0) first = pr.mType + " blocker " + pr.mBlockerType;
                }
                if (bad == 0) continue;
                closed++;
                if (b.Length < 500) b.Append(" " + n + " 0x" + o.ObjectId.Value.ToString("X") + " at " + BuAt(o.Position) + " room " + o.RoomId + ": " + bad + " of " + rs.Length + " slots closed (" + first + ");");
            }
            return b.Append(" objects " + objects + ", slots " + slots + ", with a closed slot " + closed).ToString();
        }
    }
}
