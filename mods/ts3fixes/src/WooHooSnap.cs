using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // woohoosnap / woohoosnaphh and pairsnap / pairsnaphh (docs/686 §20, default off). A player's wish: "if they get to
    // kissing and just can't reach the bed, one of them, or the shower or wherever they are woohooing, you can teleport
    // them". Rules in WooHooSnapCore.cs.
    //
    // [verified, decompiled] Every WooHoo object sends each partner on a route of its own and ends the WooHoo when one
    // fails (docs/686 §20.1 has the table):
    //   bed, tent, igloo   Bed.PushWooHooOrTryForBaby queues EnterRelaxing for both (RouteToUnusedPart, then
    //                      PartData.IsRoutingSlotObstructed) and Beds.WooHoo (the only IIsWooHoo) for the starter
    //   shower             the partner's TakeShower (RouteToSlotAndCheckInUse), linked to the starter's Shower.WooHoo
    //                      (a walk to 1.5-4 m, then RouteToSlot RoutingSlot_0)
    //   others             an interaction class named WooHoo / Woohoo / TryForBaby on each Sim (wardrobe, all-in-one
    //                      bathroom, leaf pile, hay stack, sarcophagus, time machine, bot station, box of mystery,
    //                      actor trailer, photo booth, elevator, underwater cave, caravan): a slot route on the object,
    //                      some after a walk to a ring round it
    // All of them end in SimRoutingComponent.DoRoute. Hot tub, sauna, treehouse, fairy house and hot air balloon
    // WooHoo start with both Sims already in the object; rabbit hole WooHoo with both inside.
    //
    // woohoosnap, in DoRoute (BlockedUse.cs's hook, first step): a failed plan (WooHooSnapCore.PlanKind) of a human
    // whose interaction is part of a WooHoo, with Sim, partner and object on one lot: the Sim walks up to the object's
    // room when a normal plan gets it there, then it is put on the route's nearest wanted slot (any level), or for a
    // route without slots (a ring round the object) on a free place the game's placement search finds at the route's goal, and the route answers
    // "arrived". The Sim is remembered for blockeduse's way out, and the game's slot-obstruction test answers "free"
    // for a remembered Sim at that object (ShowerShare.cs's hook). The game's tests (age, relationship, privacy, in
    // use) are untouched: only the walk is replaced.
    //
    // pairsnap, for paired socials (kiss, hug: SocialReachCore's "contact"), two humans within 3 m on one level:
    //   a. SetupTwoPersonSocial answered false (SocialReach.cs's hook): the partner is put at the social's distance
    //      from the starter (or the end of the starter's walk), on the first routable place of a ring of eight with no
    //      wall between, the two facing each other; no routes.
    //   b. In DoRoute: a failed plan or walk of either Sim: onto its jig slot when the route leads to a jig, else onto
    //      such a ring place; while the other still walks the Sim only turns.
    //   c. SocialJigTwoPerson.PlanRouteToJigA / RouteToJigB answered a failed plan: planned again through objects (walls
    //      count); a plan of at most kBuClipMax metres is kept and walked.
    //
    // Scope: woohoosnap / pairsnap = pairs without a Sim the player controls, ...hh = pairs with one; each stands alone.
    // Off = compare mode: counted, nothing done. woohoosnaplog / pairsnaplog write one line per act.
    // Counters 4566-4581. Checks: WooHooSnapCheck, PairSnapCheck. Swaps: the two jig plan methods (no yield).
    public static partial class Fixes
    {
        const int kWhsTries = 4566, kWhsWould = 4567, kWhsExempt = 4568, kWhsBed = 4569, kWhsPlumbing = 4570, kWhsOther = 4571, kWhsKept = 4572, kWhsNear = 4573,
            kWhsSnapOut = 4574, kWhsErrors = 4575, kPrsTries = 4576, kPrsWould = 4577, kPrsExempt = 4578, kPrsSnap = 4579, kPrsKept = 4580, kPrsErrors = 4581;

        sealed class WhsCase
        {
            public bool Pair; public int Step; public GameObject Obj; public BuRec Rec; public Route Near; public Sim Partner;
            public Vector3 Goal; public float Dist; public string Head;
        }

        static readonly Dictionary<Type, bool> sWhsTypes = new Dictionary<Type, bool>();
        static readonly Dictionary<Type, FieldInfo[]> sWhsSimFields = new Dictionary<Type, FieldInfo[]>();
        static readonly FieldInfo sPrsDist = typeof(SocialInteraction).GetField("mSocialDist", kInstAll);
        static readonly LineCapCore sWhsCap = new LineCapCore(20, 60000, 300), sPrsCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kWhsSites = { "begin", "arrive", "placement", "jig plan", "lot", "out" };
        static readonly int[] sWhsSiteErrors = new int[6], sPrsSiteErrors = new int[6];
        static readonly string[] kWhsWhy = { "other lot or the world lot", "not a route failure (locked door, aborted)", "no spot", "pet, rider or carried", "a social's own route", "walk up cancelled" };
        static readonly int[] sWhsWhy = new int[6];
        static readonly string[] kPrsWhy = { "too far", "other level", "sitting or lying", "still walking", "not a route failure", "no spot without a wall", "cancelled" };
        static readonly int[] sPrsWhy = new int[7];
        static readonly string[] kPrsHow = { "snap", "turn", "jig slot", "walk through objects" };
        static readonly int[] sPrsHow = new int[4];
        static string sWhsLast = "", sWhsLastError = "", sPrsLast = "", sPrsLastError = "", sPrsMissing = "";

        static void ApplyWooHooSnap()
        {
            string missing = sPrsDist == null ? "SocialInteraction.mSocialDist " : "";
            if (!Patch(typeof(SocialJigTwoPerson), "PlanRouteToJigA", new Type[] { typeof(Route), typeof(Sim), typeof(ObjectGuid), typeof(Vector3) }, typeof(PrsJigHook), "JigAHook", "JigAOrig"))
                missing += "SocialJigTwoPerson.PlanRouteToJigA ";
            if (!Patch(typeof(SocialJigTwoPerson), "RouteToJigB", new Type[] { typeof(Sim), typeof(ObjectGuid), typeof(Vector3) }, typeof(PrsJigHook), "JigBHook", "JigBOrig"))
                missing += "SocialJigTwoPerson.RouteToJigB";
            sPrsMissing = missing.Trim();
            if (sPrsMissing.Length > 0) AppliedLog().Add("pairsnap: not found: " + sPrsMissing);
        }

        static void WhsError(bool pair, int site, Exception e)
        {
            PatchCounters()[pair ? kPrsErrors : kWhsErrors]++;
            string text = (pair ? "pairsnap" : "woohoosnap") + ": error at " + kWhsSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (pair) sPrsLastError = text; else sWhsLastError = text;
            if ((pair ? sPrsSiteErrors : sWhsSiteErrors)[site]++ < 3) DiagLine(text);
        }

        static void WhsLine(string text)
        {
            sWhsLast = text;
            if (ShortcutOn("woohoosnaplog") && sWhsCap.Take(System.Environment.TickCount)) DiagLine("woohoosnap: " + text);
        }

        static void PrsLine(string text)
        {
            sPrsLast = text;
            if (ShortcutOn("pairsnaplog") && sPrsCap.Take(System.Environment.TickCount)) DiagLine("pairsnap: " + text);
        }

        static void WhsKeep(int why, string head)
        {
            PatchCounters()[kWhsKept]++;
            sWhsWhy[why]++;
            WhsLine(head + ": kept, " + kWhsWhy[why]);
        }

        static void PrsKeep(int why, string head)
        {
            PatchCounters()[kPrsKept]++;
            sPrsWhy[why]++;
            PrsLine(head + ": kept, " + kPrsWhy[why]);
        }

        static RoutePlanResult WhsArrived(Vector3 at)
        {
            RoutePlanResult ok = new RoutePlanResult();
            ok.mType = RoutePlanResultType.Succeeded;
            ok.mDestination = at;
            return ok;
        }

        // ---- which interaction is part of a WooHoo, and with whom ----

        static bool WhsIsWooHoo(InteractionInstance ia)
        {
            Type t = ia.GetType();
            bool yes;
            if (sWhsTypes.TryGetValue(t, out yes)) return yes;
            string name = t.FullName ?? t.Name;
            yes = ia is IIsWooHoo || (WooHooSnapCore.WooHooName(name) && name.IndexOf("WooHooty", StringComparison.Ordinal) < 0);
            if (sWhsTypes.Count >= 512) sWhsTypes.Clear();
            sWhsTypes[t] = yes;
            return yes;
        }

        // The other Sim of a WooHoo interaction: its target, its linked interaction's Sim, or a Sim it holds in a field
        // (WooHooer / WooHooee and the like). Null = not known yet.
        static Sim WhsPartnerOf(InteractionInstance ia, Sim sim)
        {
            Sim p = ia.Target as Sim;
            if (p != null && p != sim) return p;
            InteractionInstance linked = ia.LinkedInteractionInstance;
            p = linked != null ? linked.InstanceActor : null;
            if (p != null && p != sim) return p;
            Type t = ia.GetType();
            FieldInfo[] fields;
            if (!sWhsSimFields.TryGetValue(t, out fields))
            {
                List<FieldInfo> list = new List<FieldInfo>();
                for (Type k = t; k != null && k != typeof(InteractionInstance) && k != typeof(object); k = k.BaseType)
                    foreach (FieldInfo f in k.GetFields(kInstAll | BindingFlags.DeclaredOnly))
                        if (f.FieldType == typeof(Sim)) list.Add(f);
                fields = list.ToArray();
                if (sWhsSimFields.Count >= 256) sWhsSimFields.Clear();
                sWhsSimFields[t] = fields;
            }
            foreach (FieldInfo f in fields)
            {
                p = f.GetValue(ia) as Sim;
                if (p != null && p != sim) return p;
            }
            return null;
        }

        static Sim WhsQueuedWith(Sim owner, Sim other)
        {
            if (owner == null || owner.HasBeenDestroyed || owner.InteractionQueue == null) return null;
            foreach (InteractionInstance q in owner.InteractionQueue.InteractionList)
            {
                if (!(q is IIsWooHoo)) continue;
                Sim t = q.Target as Sim;
                if (other == null) { if (t != null && t != owner) return t; }
                else if (t == other) return owner;
            }
            return null;
        }

        // Is the Sim's running interaction a step of a WooHoo? The bed's steps are plain Relax interactions: they count
        // while a bed WooHoo of the pair waits in a queue (the starter's own, or one that targets this Sim).
        static bool WhsContext(Sim sim, InteractionInstance ia, out Sim partner)
        {
            partner = null;
            if (WhsIsWooHoo(ia)) { partner = WhsPartnerOf(ia, sim); return true; }
            InteractionInstance linked = ia.LinkedInteractionInstance;
            if (linked != null && WhsIsWooHoo(linked))
            {
                partner = linked.InstanceActor;
                if (partner == sim) partner = linked.Target as Sim;
                return true;
            }
            Bed bed = ia.Target as Bed;
            if (bed == null || ia.GetType().Name != "EnterRelaxing") return false;
            partner = WhsQueuedWith(sim, null);
            if (partner != null) return true;
            Lot lot = bed.LotCurrent;
            if (lot == null) return false;
            foreach (Sim o in lot.GetSims())
            {
                if (o == sim) continue;
                partner = WhsQueuedWith(o, sim);
                if (partner != null) return true;
            }
            return false;
        }

        static int WhsGroup(GameObject o)
        {
            if (o == null) return WooHooSnapCore.kOtherObject;
            Type t = o.GetType();
            return WooHooSnapCore.Group(o is Bed, t.Namespace, t.Name);
        }

        // ---- a failed plan in DoRoute: first step of the hook's chain ----

        // Null = not ours (the chain goes on). Reads, counts and plans the walk up; moves nothing.
        static WhsCase WhsBegin(Sim sim, Route r, bool afterWalk)
        {
            if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null) return null;
            InteractionInstance ia = sim.CurrentInteraction;
            if (ia == null) return null;
            BuRec rec = null;
            GameObject obj = r.DestObj as GameObject;
            bool slots = obj != null && sBuRecs.TryGetValue(r.Handle, out rec) && rec.N > 0 && ReferenceEquals(obj, rec.D[rec.N - 1].mTargetObject);
            if (!slots) rec = null;
            SocialInteraction si = ia as SocialInteraction;
            bool jig = obj is SocialJig;
            if (si != null && (!slots || jig || !WhsIsWooHoo(si))) return PrsBegin(sim, r, si, jig && slots ? rec : null, jig ? obj : null, afterWalk);
            Sim partner;
            if (!WhsContext(sim, ia, out partner)) return null;
            if (partner != null && (partner.HasBeenDestroyed || partner.SimDescription == null)) partner = null;

            int[] c = PatchCounters();
            c[kWhsTries]++;
            RoutePlanResult pr = r.PlanResult;
            GameObject target = slots ? obj : (obj ?? ia.Target as GameObject);
            if (target != null && (target.HasBeenDestroyed || target == sim)) target = null;
            Lot lot = target != null ? target.LotCurrent : sim.LotCurrent;
            bool sameLot = lot != null && !lot.IsWorldLot && sim.LotCurrent == lot && (partner == null || partner.LotCurrent == lot);
            int pick = -1;
            float nearest = float.MaxValue;
            if (slots)
            {
                BuSlots s = BuReadSlots(sim, rec, false);
                pick = BlockedUseCore.PickSlot(s.Dist, s.Dy, s.Usable, s.N, float.MaxValue, float.MaxValue);
                if (pick >= 0) nearest = s.Dist[pick];
            }
            Vector3 goal = r.GetApproximateOriginalDestination();
            bool point = BuValid(goal) || (target != null && !(target is Sim && target == partner && RuInPosture(partner)));
            int why;
            int step = WooHooSnapCore.Step(BuSimOk(sim), WooHooSnapCore.PlanKind((int)pr.mType), sameLot, pick, point, si != null, out why);
            int d = WooHooSnapCore.Decide(IsPlayerSim(sim), partner != null && IsPlayerSim(partner), ShortcutOn("woohoosnap"), ShortcutOn("woohoosnaphh"));
            string head = BuWho(sim) + " " + sim.FullName + ", " + ia.GetType().Name + (partner != null ? " with " + partner.FullName : "") + " -> "
                + (target != null ? target.GetType().Name + " 0x" + target.ObjectId.Value.ToString("X") : "a place") + " (" + pr.mType + ", blocker " + pr.mBlockerType + (afterWalk ? ", after a walk" : "") + ")";
            if (step == WooHooSnapCore.kKeep) { if (d == BlockedUseCore.kGo) WhsKeep(why, head); return null; }
            if (d == BlockedUseCore.kWould) { c[kWhsWould]++; return null; }
            if (d == BlockedUseCore.kExempt) { c[kWhsExempt]++; return null; }

            WhsCase w = new WhsCase();
            w.Step = step; w.Obj = target; w.Rec = rec; w.Partner = partner; w.Goal = goal; w.Head = head;
            if (target != null && !(target is Sim) && BlockedUseCore.NeedNear(true, sim.Level == target.Level, sim.RoomId == target.RoomId, slots ? nearest : BuFlat(sim.Position, target.Position), kBuNearMax))
            {
                Route near = sim.CreateRoute();
                near.DoRouteFail = false;
                near.ExecutionFromNonSimTaskIsSafe = r.ExecutionFromNonSimTaskIsSafe;
                near.SetOption(Route.RouteOption.DoLineOfSightCheckUserOverride, true);
                near.PlanToPointRadialRange(target, target.PositionOnFloor, kBuNearMin, kBuNearMax, RouteDistancePreference.PreferNearestToRouteDestination,
                    RouteOrientationPreference.TowardsObject, lot.LotId, new int[] { target.RoomId });
                if (near.PlanResult.Succeeded()) w.Near = near;  // no such plan: the Sim is put on its spot from where it stands
            }
            return w;
        }

        // The Sim is on `pos` now: remembered for blockeduse's way out (and its "free slot" answer at `obj`).
        static void WhsRemember(Sim sim, GameObject obj, Vector3 from, Vector3 pos, int owner)
        {
            BuCase k = new BuCase();
            k.Obj = obj; k.Origin = from; k.Goal = pos;
            BuRemember(sim, k, true);
            BuExit x;
            if (sBuExits.TryGetValue(sim.ObjectId.Value, out x)) x.Owner = owner;
        }

        // After the walk up (or at once). True: the Sim stands on its spot and r answers "arrived".
        static bool WhsArrive(Sim sim, Route r, WhsCase w)
        {
            int[] c = PatchCounters();
            GameObject obj = w.Obj;
            if (sim.HasBeenDestroyed || (obj != null && obj.HasBeenDestroyed)) return false;
            Lot lot = obj != null ? obj.LotCurrent : sim.LotCurrent;
            if (lot == null || lot.IsWorldLot || sim.LotCurrent != lot) { WhsKeep(WooHooSnapCore.kWhyLot, w.Head); return false; }
            Vector3 from = sim.Position, pos, fwd = Vector3.Invalid;
            RoutePlanResult ok;
            string how;
            if (w.Step == WooHooSnapCore.kSlot)
            {
                BuSlots s = BuReadSlots(sim, w.Rec, false);
                int pick = BlockedUseCore.PickSlot(s.Dist, s.Dy, s.Usable, s.N, float.MaxValue, float.MaxValue);
                if (pick < 0) { WhsKeep(WooHooSnapCore.kWhySpot, w.Head); return false; }
                pos = s.Pos[pick];
                fwd = s.Obj[pick].GetForwardOfSlot(s.Name[pick]);
                ok = WhsArrived(pos);
                ok.mDestSlotNameHash = (uint)s.Name[pick];
                ok.mDestinationTag = s.Tag[pick];
                how = "slot 0x" + ((uint)s.Name[pick]).ToString("X");
            }
            else
            {
                // a route to a ring or a point: a free place the game's placement search finds at the route's goal, else next to the object
                Vector3 seed = BuValid(w.Goal) ? w.Goal : obj != null ? obj.Position : Vector3.Invalid, ff;
                if (!BuValid(seed) || !Sims3.Gameplay.GlobalFunctions.FindGoodLocation(sim, new World.FindGoodLocationParams(seed), out pos, out ff) || !BuValid(pos))
                { WhsKeep(WooHooSnapCore.kWhySpot, w.Head); return false; }
                how = "a free place at the route's goal";
                ok = WhsArrived(pos);
            }
            sim.SetPosition(pos);
            if (BuValid(fwd)) sim.SetForward(fwd);
            else if (obj != null) RuFace(sim, obj.Position);
            r.PlanResult = ok;
            WhsRemember(sim, obj is Sim ? null : obj, from, pos, 1);
            // The shower's interactions route to the slot twice (run ws-m1: a second plan from the slot itself failed
            // as the first did): the Sim is on its spot already, the answer is the same, nothing is counted or written.
            if (w.Near == null && BuFlat(from, pos) < 0.02f) return true;
            int g = WhsGroup(obj);
            c[g == WooHooSnapCore.kBed ? kWhsBed : g == WooHooSnapCore.kPlumbing ? kWhsPlumbing : kWhsOther]++;
            if (w.Near != null) c[kWhsNear]++;
            WhsLine(w.Head + ": snap " + BuFlat(from, pos).ToString("0.0") + " m to " + how + " at " + BuAt(pos) + " from " + BuAt(from) + (w.Near != null ? ", walked up first" : ""));
            return true;
        }

        // blockeduse's way out put a Sim of ours back (BuAfterPlan).
        static void WhsOut(Sim sim, int owner, Vector3 from, Vector3 to)
        {
            PatchCounters()[kWhsSnapOut]++;
            string text = BuWho(sim) + " " + sim.FullName + ": snap out from " + BuAt(from) + " to " + BuAt(to);
            if (owner == 2) PrsLine(text); else WhsLine(text);
        }

        // GameObject.IsRoutingSlotObstructed for a Sim that was let onto a closed slot of this object (clip or snap, by
        // blockeduse, woohoosnap or pairsnap): the slot is closed, which is why the Sim was put there. Without this the
        // bed's Relax ends after the snap and a shower's exit waits for the slot for ever.
        static bool BuLetOn(GameObject obj, Sim s)
        {
            BuExit x;
            return sBuExits.TryGetValue(s.ObjectId.Value, out x) && x.Obj != 0UL && x.Obj == obj.ObjectId.Value;
        }

        // ---- pairsnap ----

        static int PrsCoverOf(SocialInteraction mine, SocialInteraction other, SocialInteraction starter, Sim a, Sim b)
        {
            ActionData data = SrData(mine);
            if (data == null && other != null) data = SrData(other);
            bool exact = (SrExact(mine) || (mine is IWooHooSocialInteraction)) && (other == null || SrExact(other) || other is IWooHooSocialInteraction);
            return SocialReachCore.Cover(exact, data != null, data != null ? (int)data.SocialJig : 0, starter != null ? starter.SocialJig != null : mine.SocialJig != null, true, false, false, false);
        }

        static bool PrsPairOk(int cover, Sim a, Sim b)
        {
            return WooHooSnapCore.PairCover(cover, a.IsHuman && b.IsHuman, a.SimDescription.Baby || b.SimDescription.Baby, SrMounted(a) || SrMounted(b), SrInWater(a) || SrInWater(b))
                && BuSimOk(a) && BuSimOk(b);
        }

        static Vector3 PrsWalkEnd(Sim s, out bool moving)
        {
            moving = false;
            Route cur = s.IsRouting && s.RoutingComponent != null ? s.RoutingComponent.GetCurrentRoute() : null;
            if (cur != null)
            {
                Vector3 end = cur.GetDestPoint();
                if (BuValid(end)) { moving = BuFlat(end, s.Position) > SocialReachCore.kTurnSlack; return end; }
            }
            return s.Position;
        }

        // The first place of the ring round `stay` (at `sp`), `dist` away, with no wall between that the mover's routes
        // could end on; with none routable the first without a wall (the mover then stands in furniture).
        static bool PrsSpot(Sim mover, Vector3 mp, Vector3 sp, ulong lotId, float dist, out Vector3 pos, out Vector3 fwd, out bool routable)
        {
            pos = Vector3.Invalid; fwd = Vector3.Invalid; routable = false;
            Route probe = mover.CreateRoute();
            bool have = false;
            for (int i = 0; i < WooHooSnapCore.kRing; i++)
            {
                float x, z, fx, fz;
                WooHooSnapCore.Ring(sp.x, sp.z, mp.x, mp.z, dist, i, out x, out z, out fx, out fz);
                Vector3 p = new Vector3(x, sp.y, z);
                if (lotId != 0UL && RuWall(lotId, p, sp)) continue;
                bool ok = probe.IsPointRoutable(p);
                if (!ok && have) continue;
                pos = p; fwd = new Vector3(fx, 0f, fz); have = true;
                if (ok) { routable = true; return true; }
            }
            return have;
        }

        static float PrsDistOf(SocialInteraction si)
        {
            object v = sPrsDist != null ? sPrsDist.GetValue(si) : null;
            return v is float ? (float)v : 0f;
        }

        // b. A social's failed plan or walk in DoRoute. `rec` / `jig`: the route leads to a jig's slot.
        static WhsCase PrsBegin(Sim sim, Route r, SocialInteraction si, BuRec rec, GameObject jig, bool afterWalk)
        {
            Sim partner = si.Target;
            if (partner == null || partner == sim || partner.HasBeenDestroyed || partner.SimDescription == null) return null;
            bool starter = si is SocialInteractionA;
            SocialInteraction other = si.LinkedInteractionInstance as SocialInteraction;
            if (!PrsPairOk(PrsCoverOf(si, other, starter ? si : other, sim, partner), sim, partner)) return null;
            RoutePlanResult pr = r.PlanResult;
            if (!SocialReachCore.PlanKind((int)pr.mType) && (int)pr.mType != BlockedUseCore.kNoUnobstructedSlots) return null;
            float dist = PrsDistOf(si);
            if (rec == null && dist <= 0.05f) return null;
            int[] c = PatchCounters();
            c[kPrsTries]++;
            int d = WooHooSnapCore.Decide(IsPlayerSim(sim), IsPlayerSim(partner), ShortcutOn("pairsnap"), ShortcutOn("pairsnaphh"));
            if (d == BlockedUseCore.kWould) { c[kPrsWould]++; return null; }
            if (d == BlockedUseCore.kExempt) { c[kPrsExempt]++; return null; }
            bool otherMoving;
            Vector3 to = PrsWalkEnd(partner, out otherMoving), at = sim.Position;
            string head = BuWho(sim) + " " + sim.FullName + ", " + SrName(si) + " -> " + partner.FullName + " (" + pr.mType + (afterWalk ? ", after a walk" : "") + (rec != null ? ", jig route" : "") + ")";
            int why;
            int step = WooHooSnapCore.PairStep(BuFlat(at, to), at.y - to.y, sim.Level == partner.Level, RuInPosture(sim), false, otherMoving && rec == null, out why);
            if (step == WooHooSnapCore.kPairKeep) { PrsKeep(why, head + ", " + BuFlat(at, to).ToString("0.0") + " m"); return null; }
            WhsCase w = new WhsCase();
            w.Pair = true; w.Step = step; w.Obj = jig; w.Rec = rec; w.Partner = partner; w.Goal = to; w.Dist = dist; w.Head = head;
            return w;
        }

        static bool PrsArrive(Sim sim, Route r, WhsCase w)
        {
            int[] c = PatchCounters();
            Sim partner = w.Partner;
            if (sim.HasBeenDestroyed || partner.HasBeenDestroyed) return false;
            Vector3 from = sim.Position;
            if (w.Step == WooHooSnapCore.kPairTurn)
            {
                RuFace(sim, w.Goal);
                r.PlanResult = WhsArrived(from);
                c[kPrsSnap]++; sPrsHow[1]++;
                PrsLine(w.Head + ": turned to where the other walks");
                return true;
            }
            if (w.Rec != null)
            {
                BuSlots s = BuReadSlots(sim, w.Rec, false);
                int pick = BlockedUseCore.PickSlot(s.Dist, s.Dy, s.Usable, s.N, kBuSnapMax, kBuSnapMaxDy);
                if (pick < 0) { PrsKeep(WooHooSnapCore.kPairWhyFar, w.Head + ", no jig slot within " + kBuSnapMax + " m"); return false; }
                Vector3 p = s.Pos[pick], f = s.Obj[pick].GetForwardOfSlot(s.Name[pick]);
                sim.SetPosition(p);
                if (BuValid(f)) sim.SetForward(f);
                RoutePlanResult ok = WhsArrived(p);
                ok.mDestSlotNameHash = (uint)s.Name[pick];
                ok.mDestinationTag = s.Tag[pick];
                r.PlanResult = ok;
                WhsRemember(sim, null, from, p, 2);
                c[kPrsSnap]++; sPrsHow[2]++;
                PrsLine(w.Head + ": snap " + s.Dist[pick].ToString("0.0") + " m onto the jig slot at " + BuAt(p));
                return true;
            }
            Vector3 sp = partner.Position, pos, fwd;
            bool routable;
            if (!PrsSpot(sim, from, sp, SrLotId(partner), w.Dist, out pos, out fwd, out routable)) { PrsKeep(WooHooSnapCore.kPairWhySpot, w.Head); return false; }
            sim.SetPosition(pos);
            sim.SetForward(fwd);
            partner.SetForward(new Vector3(-fwd.x, 0f, -fwd.z));
            r.PlanResult = WhsArrived(pos);
            if (!routable) WhsRemember(sim, null, from, pos, 2);
            c[kPrsSnap]++; sPrsHow[0]++;
            PrsLine(w.Head + ": snap " + BuFlat(from, pos).ToString("0.0") + " m to " + BuAt(pos) + ", " + w.Dist.ToString("0.00") + " m from the other" + (routable ? "" : " (not a routable place)"));
            return true;
        }

        // a. SetupTwoPersonSocial answered false and socialreach left it alone. `b` is the partner's social (simB its
        // Sim: this task's), socialA the starter's (simA). True: the pair is placed, no routes.
        static bool PrsPlace(SocialInteraction b, Sim simA, Sim simB, SocialInteraction socialA)
        {
            try
            {
                if (!PrsPairOk(PrsCoverOf(b, socialA, socialA, simA, simB), simA, simB)) return false;
                float dist = PrsDistOf(b);
                if (dist <= 0.05f || socialA.SocialJig != null) return false;  // a social with its own jig is placed by the jig (rule c)
                int[] c = PatchCounters();
                c[kPrsTries]++;
                int d = WooHooSnapCore.Decide(IsPlayerSim(simA), IsPlayerSim(simB), ShortcutOn("pairsnap"), ShortcutOn("pairsnaphh"));
                if (d == BlockedUseCore.kWould) { c[kPrsWould]++; return false; }
                if (d == BlockedUseCore.kExempt) { c[kPrsExempt]++; return false; }
                bool moving;
                Vector3 pa = PrsWalkEnd(simA, out moving), pb = simB.Position;
                string head = BuWho(simA) + " " + simA.FullName + ", " + SrName(socialA) + " -> " + simB.FullName + " (placement)";
                int why;
                int step = WooHooSnapCore.PairStep(BuFlat(pa, pb), pa.y - pb.y, simA.Level == simB.Level, RuInPosture(simB) || RuInPosture(simA), simB.IsRouting, false, out why);
                Vector3 pos = Vector3.Invalid, fwd = Vector3.Invalid;
                bool routable = false;
                if (step != WooHooSnapCore.kPairKeep && !PrsSpot(simB, pb, pa, SrLotId(simA), dist, out pos, out fwd, out routable)) { step = WooHooSnapCore.kPairKeep; why = WooHooSnapCore.kPairWhySpot; }
                if (step == WooHooSnapCore.kPairKeep) { PrsKeep(why, head + ", " + BuFlat(pa, pb).ToString("0.0") + " m"); return false; }
                SrDropPlacementJig(b, socialA);
                simB.SetPosition(pos);
                simB.SetForward(fwd);
                if (!moving) simA.SetForward(new Vector3(-fwd.x, 0f, -fwd.z));  // a starter still on its walk is turned by the game's facing step
                sSrRoute.SetValue(b, null);
                sSrRoute.SetValue(socialA, null);
                if (!routable) WhsRemember(simB, null, pb, pos, 2);
                c[kPrsSnap]++; sPrsHow[0]++;
                PrsLine(head + ": the partner snaps " + BuFlat(pb, pos).ToString("0.0") + " m to " + BuAt(pos) + ", " + dist.ToString("0.00") + " m from the starter" + (moving ? " (the end of its walk)" : "")
                    + (routable ? "" : " (not a routable place)"));
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { WhsError(true, 2, e); return false; }
        }

        // c. A jig route's plan failed: planned again through objects. `r` keeps the failed plan unless a short plan is found.
        static void PrsJigPlan(SocialJig jig, Sim sim, Sim other, Route r, Vector3 fromPoint)
        {
            try
            {
                if (r == null || sim == null || other == null || other == sim || jig == null || !jig.InWorld) return;
                RoutePlanResult pr = r.PlanResult;
                if (pr.Succeeded() || (!SocialReachCore.PlanKind((int)pr.mType) && (int)pr.mType != BlockedUseCore.kNoUnobstructedSlots)) return;
                if (sim.HasBeenDestroyed || other.HasBeenDestroyed || sim.SimDescription == null || other.SimDescription == null) return;
                if (!PrsPairOk(SocialReachCore.kContact, sim, other)) return;
                int[] c = PatchCounters();
                c[kPrsTries]++;
                int d = WooHooSnapCore.Decide(IsPlayerSim(sim), IsPlayerSim(other), ShortcutOn("pairsnap"), ShortcutOn("pairsnaphh"));
                if (d == BlockedUseCore.kWould) { c[kPrsWould]++; return; }
                if (d == BlockedUseCore.kExempt) { c[kPrsExempt]++; return; }
                bool from = BuValid(fromPoint);
                Vector3 at = from ? fromPoint : sim.Position, to = other.Position;
                string head = BuWho(sim) + " " + sim.FullName + " -> " + jig.GetType().Name + " with " + other.FullName + " (" + pr.mType + ", jig plan)";
                int why;
                if (WooHooSnapCore.PairStep(BuFlat(at, to), at.y - to.y, sim.Level == other.Level, RuInPosture(sim), false, false, out why) == WooHooSnapCore.kPairKeep)
                { PrsKeep(why, head + ", " + BuFlat(at, to).ToString("0.0") + " m"); return; }
                bool o1 = r.GetOption(Route.RouteOption.PassThroughObjects), o2 = r.GetOption(Route.RouteOption.UserOverrideSlotObstruction), o3 = r.GetOption(Route.RouteOption.DoSlotObstructionChecksUserOverride);
                r.SetOption(Route.RouteOption.PassThroughObjects, true);
                r.SetOption(Route.RouteOption.UserOverrideSlotObstruction, true);
                r.SetOption(Route.RouteOption.DoSlotObstructionChecksUserOverride, false);
                RoutePlanResult clip = from ? r.ReplanFromPoint(fromPoint) : r.Replan();
                float len = clip.Succeeded() ? (clip.mDistanceTotal > 0f ? clip.mDistanceTotal : r.GetRouteLength()) : 0f;
                if (WooHooSnapCore.ClipWalk(clip.Succeeded(), len, kBuClipMax))
                {
                    c[kPrsSnap]++; sPrsHow[3]++;
                    PrsLine(head + ": planned through objects, " + len.ToString("0.0") + " m");
                    return;
                }
                r.SetOption(Route.RouteOption.PassThroughObjects, o1);
                r.SetOption(Route.RouteOption.UserOverrideSlotObstruction, o2);
                r.SetOption(Route.RouteOption.DoSlotObstructionChecksUserOverride, o3);
                if (clip.Succeeded()) { if (from) r.ReplanFromPoint(fromPoint); else r.Replan(); }  // too long: the failed plan again
                PrsKeep(WooHooSnapCore.kPairWhySpot, head + ", no short plan through objects");
            }
            catch (ResetException) { throw; }
            catch (Exception e) { WhsError(true, 3, e); }
        }

        // `this` is the game's two-person jig.
        public abstract class PrsJigHook : SocialJigTwoPerson
        {
            public Route JigAHook(Route r, Sim actorA, ObjectGuid simObjIdToIgnore, Vector3 routeFromPoint)
            {
                Route res = JigAOrig(r, actorA, simObjIdToIgnore, routeFromPoint);
                if (res != null && res.PlanResult.mType != RoutePlanResultType.Succeeded) PrsJigPlan(this, actorA, SimB, res, routeFromPoint);
                return res;
            }
            public Route JigAOrig(Route r, Sim actorA, ObjectGuid simObjIdToIgnore, Vector3 routeFromPoint) { return null; }  // receives PlanRouteToJigA's original body

            public Route JigBHook(Sim actorB, ObjectGuid simObjIdToIgnore, Vector3 routeFromPoint)
            {
                Route res = JigBOrig(actorB, simObjIdToIgnore, routeFromPoint);
                if (res != null && res.PlanResult.mType != RoutePlanResultType.Succeeded) PrsJigPlan(this, actorB, SimA, res, routeFromPoint);
                return res;
            }
            public Route JigBOrig(Sim actorB, ObjectGuid simObjIdToIgnore, Vector3 routeFromPoint) { return null; }  // receives RouteToJigB's original body
        }

        // ---- checks ----

        static string WhsListErrors(int[] sites)
        {
            string s = "";
            for (int i = 0; i < sites.Length; i++)
                if (sites[i] > 0) s += (s.Length > 0 ? ", " : "") + kWhsSites[i] + " " + sites[i];
            return s;
        }

        static string WhsListWhy(string[] names, int[] counts)
        {
            string s = "";
            for (int i = 0; i < counts.Length; i++)
                if (counts[i] > 0) s += (s.Length > 0 ? ", " : "") + names[i] + " " + counts[i];
            return s.Length > 0 ? " (" + s + ")" : "";
        }

        static string WhsCounters()
        {
            int[] c = PatchCounters();
            string sites = WhsListErrors(sWhsSiteErrors);
            return "woohoosnap: failed plans of WooHoo partners " + c[kWhsTries] + "; on: snaps at a bed " + c[kWhsBed] + ", shower, tub or sauna " + c[kWhsPlumbing] + ", other object " + c[kWhsOther]
                + ", of them after a walk up " + c[kWhsNear] + "; kept as the game has it " + c[kWhsKept] + WhsListWhy(kWhsWhy, sWhsWhy)
                + ", exempt (a household Sim without woohoosnaphh, or none without woohoosnap) " + c[kWhsExempt] + "; off: would " + c[kWhsWould]
                + "; snap outs (woohoosnap and pairsnap) " + c[kWhsSnapOut] + "; errors " + c[kWhsErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sWhsLastError + ")" : "")
                + "; " + GlMembers((sBuMissing + " " + sShsMissing).Trim()) + (sWhsLast.Length > 0 ? "; last: " + sWhsLast : "");
        }

        static string PrsCounters()
        {
            int[] c = PatchCounters();
            string sites = WhsListErrors(sPrsSiteErrors);
            return "pairsnap: failed placements, plans and walks of paired socials within reach of the rule " + c[kPrsTries] + "; on: placed " + c[kPrsSnap] + WhsListWhy(kPrsHow, sPrsHow)
                + "; kept as the game has it " + c[kPrsKept] + WhsListWhy(kPrsWhy, sPrsWhy) + ", exempt (a household Sim without pairsnaphh, or none without pairsnap) " + c[kPrsExempt]
                + "; off: would " + c[kPrsWould] + "; errors " + c[kPrsErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sPrsLastError + ")" : "")
                + "; " + GlMembers((sPrsMissing + " " + sSrMissing + " " + sBuMissing).Trim()) + (sPrsLast.Length > 0 ? "; last: " + sPrsLast : "");
        }

        // WooHooSnapCheck run: the rules with controls (read-only). Bug case: two NPCs agreed on a WooHoo, the bed's free
        // side is closed by a dresser, the second Sim's Relax route answers "no path" and the WooHoo never starts.
        // result: the counters and the last line. lot: beds, showers and objects with a WooHoo interaction class on the
        // selected Sim's lot whose routing slots the selected Sim cannot plan to (read-only plans).
        public static object WooHooSnapCheck(string what)
        {
            if (what == "result") return WhsCounters();
            if (what == "lot") { try { return WhsLot(); } catch (ResetException) { throw; } catch (Exception e) { WhsError(false, 4, e); return sWhsLastError; } }
            if (what != "run") return "woohoosnap <run|result|lot>";
            bool on = ShortcutOn("woohoosnap"), hh = ShortcutOn("woohoosnaphh");
            int why;
            bool names = WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Plumbing.Shower+WooHoo") && WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Plumbing.AllInOneBathroom+WoohooInAllInOneBathroomB")
                && WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Beds.TryForBaby");
            bool snap = WooHooSnapCore.Step(true, WooHooSnapCore.PlanKind(16), true, 0, false, false, out why) == WooHooSnapCore.kSlot
                && WooHooSnapCore.Step(true, WooHooSnapCore.PlanKind(14), true, 1, true, false, out why) == WooHooSnapCore.kSlot
                && WooHooSnapCore.Step(true, WooHooSnapCore.PlanKind(17), true, -1, true, false, out why) == WooHooSnapCore.kPoint;
            bool fixedNow = names && snap && WooHooSnapCore.Decide(false, false, on, hh) == BlockedUseCore.kGo;
            bool ctlName = !WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Beds.BedSleep") && !WooHooSnapCore.WooHooName(null);
            bool ctlPlan = !WooHooSnapCore.PlanKind(18) && !WooHooSnapCore.PlanKind(3) && !WooHooSnapCore.PlanKind(11) && !WooHooSnapCore.PlanKind(1);
            bool ctlStep = WooHooSnapCore.Step(true, true, false, 0, true, false, out why) == WooHooSnapCore.kKeep && why == WooHooSnapCore.kWhyLot
                && WooHooSnapCore.Step(true, false, true, 0, true, false, out why) == WooHooSnapCore.kKeep && why == WooHooSnapCore.kWhyPlan
                && WooHooSnapCore.Step(false, true, true, 0, true, false, out why) == WooHooSnapCore.kKeep && why == WooHooSnapCore.kWhySim
                && WooHooSnapCore.Step(true, true, true, -1, true, true, out why) == WooHooSnapCore.kKeep && why == WooHooSnapCore.kWhySocial
                && WooHooSnapCore.Step(true, true, true, -1, false, false, out why) == WooHooSnapCore.kKeep && why == WooHooSnapCore.kWhySpot;
            bool ctlScope = WooHooSnapCore.Decide(true, false, true, false) == BlockedUseCore.kExempt && WooHooSnapCore.Decide(false, true, true, false) == BlockedUseCore.kExempt
                && WooHooSnapCore.Decide(false, true, false, true) == BlockedUseCore.kGo && WooHooSnapCore.Decide(false, false, false, true) == BlockedUseCore.kWould
                && WooHooSnapCore.Decide(false, false, false, false) == BlockedUseCore.kWould;
            bool ctlGroup = WooHooSnapCore.Group(true, "Sims3.Gameplay.Objects.Beds", "Tent") == WooHooSnapCore.kBed
                && WooHooSnapCore.Group(false, "Sims3.Gameplay.Objects.Plumbing", "ShowerTub") == WooHooSnapCore.kPlumbing
                && WooHooSnapCore.Group(false, "Sims3.Gameplay.Objects.ShelvesStorage", "Wardrobe") == WooHooSnapCore.kOtherObject;
            bool controls = ctlName && ctlPlan && ctlStep && ctlScope && ctlGroup;
            return "woohoosnap check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): a WooHoo partner whose route to its spot fails is put on the slot or place (any level of the lot)=" + fixedNow
                + "; control: other interactions kept=" + ctlName + ", locked door, aborted plan, missing object kept=" + ctlPlan + ", other lot, pets and riders, a social's own route, no spot kept=" + ctlStep
                + ", scope per switch=" + ctlScope + ", object groups=" + ctlGroup + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sBuMissing.Length == 0 && sShsMissing.Length == 0) + "; " + WhsCounters();
        }

        static bool WhsCapable(GameObject o)
        {
            if (o is Bed) return true;
            Type t = o.GetType();
            if (t.Namespace != null && t.Namespace.EndsWith(".Plumbing", StringComparison.Ordinal) && (t.Name.StartsWith("Shower", StringComparison.Ordinal) || t.Name.StartsWith("AllInOne", StringComparison.Ordinal))) return true;
            for (Type k = t; k != null && k != typeof(GameObject); k = k.BaseType)
                foreach (Type n in k.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    if (WooHooSnapCore.WooHooName(n.Name) && typeof(InteractionInstance).IsAssignableFrom(n)) return true;
            return false;
        }

        static string WhsLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null || sel.SimDescription == null) return "woohoosnap lot: no selected Sim on a lot";
            System.Text.StringBuilder b = new System.Text.StringBuilder("woohoosnap lot:");
            int objects = 0, closed = 0, slots = 0;
            foreach (GameObject o in sel.LotCurrent.GetObjects<GameObject>())
            {
                if (objects >= 60) break;
                if (o == null || o is Sim || o.HasBeenDestroyed || !o.InWorld || !WhsCapable(o)) continue;
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
                if (b.Length < 600) b.Append(" " + o.GetType().Name + " 0x" + o.ObjectId.Value.ToString("X") + " at " + BuAt(o.Position) + " level " + o.Level + ": " + bad + " of " + rs.Length + " slots closed (" + first + ");");
            }
            return b.Append(" WooHoo objects " + objects + ", slots " + slots + ", with a closed slot " + closed).ToString();
        }

        // PairSnapCheck run: the rules with controls (read-only). Bug case: two NPCs 1.5 m apart with a counter between
        // start a kiss; the placement has no plan and the social ends with the route-fail balloon. result: the counters
        // and the last line. lot: the Sims within 3 m of the selected Sim on its level, whether the game's placement
        // ring has a plan and which place the rule would use (read-only, up to 12 Sims).
        public static object PairSnapCheck(string what)
        {
            if (what == "result") return PrsCounters();
            if (what == "lot") { try { return PrsLot(); } catch (ResetException) { throw; } catch (Exception e) { WhsError(true, 4, e); return sPrsLastError; } }
            if (what != "run") return "pairsnap <run|result|lot>";
            bool on = ShortcutOn("pairsnap"), hh = ShortcutOn("pairsnaphh");
            int why;
            float x, z, fx, fz;
            WooHooSnapCore.Ring(10f, 10f, 11.5f, 10f, 0.7f, 0, out x, out z, out fx, out fz);
            bool ring = Math.Abs(x - 10.7f) < 0.001f && Math.Abs(z - 10f) < 0.001f && WooHooSnapCore.Aligned(x, z, fx, fz, 10f, 10f, -fx, -fz, 0f, 0.7f);
            bool cover = WooHooSnapCore.PairCover(SocialReachCore.Cover(true, true, 1, false, true, false, false, false), true, false, false, false);
            bool snap = WooHooSnapCore.PairStep(1.5f, 0f, true, false, false, false, out why) == WooHooSnapCore.kPairSnap
                && WooHooSnapCore.PairStep(2.9f, 0.1f, true, false, false, true, out why) == WooHooSnapCore.kPairTurn && WooHooSnapCore.ClipWalk(true, 2f, kBuClipMax);
            bool fixedNow = ring && cover && snap && WooHooSnapCore.Decide(false, false, on, hh) == BlockedUseCore.kGo;
            bool ctlCover = !WooHooSnapCore.PairCover(SocialReachCore.kTalk, true, false, false, false) && !WooHooSnapCore.PairCover(SocialReachCore.kOther, true, false, false, false)
                && !WooHooSnapCore.PairCover(SocialReachCore.kContact, false, false, false, false) && !WooHooSnapCore.PairCover(SocialReachCore.kContact, true, true, false, false)
                && !WooHooSnapCore.PairCover(SocialReachCore.kContact, true, false, true, false) && !WooHooSnapCore.PairCover(SocialReachCore.kContact, true, false, false, true);
            bool ctlStep = WooHooSnapCore.PairStep(3.2f, 0f, true, false, false, false, out why) == WooHooSnapCore.kPairKeep && why == WooHooSnapCore.kPairWhyFar
                && WooHooSnapCore.PairStep(1f, 0f, false, false, false, false, out why) == WooHooSnapCore.kPairKeep && why == WooHooSnapCore.kPairWhyLevel
                && WooHooSnapCore.PairStep(1f, 0.5f, true, false, false, false, out why) == WooHooSnapCore.kPairKeep
                && WooHooSnapCore.PairStep(1f, 0f, true, true, false, false, out why) == WooHooSnapCore.kPairKeep && why == WooHooSnapCore.kPairWhyPosture
                && WooHooSnapCore.PairStep(1f, 0f, true, false, true, false, out why) == WooHooSnapCore.kPairKeep && why == WooHooSnapCore.kPairWhyMoving
                && !WooHooSnapCore.ClipWalk(false, 0f, kBuClipMax) && !WooHooSnapCore.ClipWalk(true, 9f, kBuClipMax);
            bool ctlScope = WooHooSnapCore.Decide(true, false, true, false) == BlockedUseCore.kExempt && WooHooSnapCore.Decide(false, true, false, true) == BlockedUseCore.kGo
                && WooHooSnapCore.Decide(false, false, false, true) == BlockedUseCore.kWould && WooHooSnapCore.Decide(false, false, false, false) == BlockedUseCore.kWould;
            bool controls = ctlCover && ctlStep && ctlScope;
            return "pairsnap check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): a paired social whose placement or route fails within 3 m on one level is lined up by a snap (aligned as the game's own test wants)=" + fixedNow
                + "; control: talk socials, pets, babies, riders, swimmers kept=" + ctlCover + ", too far, other level, sitting, still walking, long or no plan through objects kept=" + ctlStep
                + ", scope per switch=" + ctlScope + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sPrsMissing.Length == 0 && sSrMissing.Length == 0 && sBuMissing.Length == 0) + "; " + PrsCounters();
        }

        static string PrsLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            Lot lot = sel != null ? sel.LotCurrent : null;
            if (lot == null || sel.SimDescription == null) return "pairsnap lot: no selected Sim";
            System.Text.StringBuilder b = new System.Text.StringBuilder("pairsnap lot:");
            int sims = 0, seen = 0, closed = 0, reach = 0;
            const float dist = 0.7f;
            foreach (Sim o in lot.GetSims())
            {
                if (o == null || o == sel || o.HasBeenDestroyed || o.SimDescription == null || !o.IsHuman || o.SimDescription.Baby) continue;
                sims++;
                int why;
                if (seen >= 12 || WooHooSnapCore.PairStep(BuFlat(sel.Position, o.Position), sel.Position.y - o.Position.y, sel.Level == o.Level, false, false, false, out why) == WooHooSnapCore.kPairKeep) continue;
                seen++;
                Route t = sel.CreateRoute();
                t.DoRouteFail = false;
                bool gameOk = t.PlanToPointRadialRange(o, o.PositionOnFloor, dist, dist + 0.1f, RouteDistancePreference.NoPreference, RouteOrientationPreference.TowardsObject,
                    lot.IsWorldLot ? 0UL : lot.LotId, lot.IsWorldLot ? null : new int[] { o.RoomId }).Succeeded();
                if (gameOk) continue;
                closed++;
                Vector3 pos, fwd;
                bool routable;
                bool ok = PrsSpot(o, o.Position, sel.Position, SrLotId(sel), dist, out pos, out fwd, out routable);
                if (ok) reach++;
                if (b.Length < 700) b.Append(" " + o.FullName + " " + BuFlat(sel.Position, o.Position).ToString("0.0") + " m: closed to the game's ring, "
                    + (ok ? "snap to " + BuAt(pos) + (routable ? "" : " (not routable)") : "a wall on every side") + ";");
            }
            return b.Append(" Sims " + sims + ", within 3 m on the level " + seen + ", closed to the game's placement at " + dist + " m " + closed + ", of them placed by the rule " + reach).ToString();
        }
    }
}
