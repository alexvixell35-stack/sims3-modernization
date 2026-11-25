using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // The facility planner (docs/686 §36, all default off). Sims of one house wait for each other at the toilet, shower,
    // bath and sink: EA's autonomy sees "in use" (x0.2) but not for how long, prefers an empty line only once at the pick
    // (SimQueue.TryReplacementInteraction, docs/686 §25.1) and then waits up to 15 sim-min in place.
    // The planner keeps, per facility object, who uses it since when and the motive that use fills (so busy-until), the
    // line and the Sims that have it queued; per Sim, the time until its need gets urgent from EA's own motive values
    // (Motive.Decay and the Bladder / Hygiene bands of the Motives XML) and a walk-time estimate. It acts in three places:
    //   pick   GameObject.GetInUseAutonomyMultiplier: an object that frees clearly later than another of the kind scores
    //          x0.1, and when every one is late and the Sim is not urgent x0.02, so autonomy takes another real action
    //          first and picks the facility again once it is predicted free (no queue, no filler: autonomy itself);
    //          a romantic partner's running shower counts as free (showertogether joins it, ShowerShare.cs).
    //   wait   SimQueue.WaitForExitReasonOnSim (LineSwitch.cs's hook): each sim-minute in a facility line the Sim moves to
    //          an object that frees clearly sooner including its walk (other room, other floor, busy or not), or leaves
    //          the line when it is not urgent and the wait is long; EA's way when it is urgent and nothing is better.
    //          Hysteresis (3 sim-min and 25 %) and a 20 sim-min per-Sim cooldown: no ping-pong. A player's order keeps
    //          its object unless it waits 10 sim-min and an identical free one exists (lineswitch's rule).
    //   use    InteractionInstance.Begin/EndCommodityUpdates: a facility use's start and end (busy-until, learned mean
    //          length per kind, facilitychain's hand-off), plus facilityquick: a household user ends a shower, bath or
    //          toilet use through EA's own ExitReason.Finished (the normal tail: rinse, step out, flush, wash hands) once
    //          its motive is at 70 (85 % of the bar) and 5 sim-min in, only while a waiter's need is urgent within 30
    //          sim-min (a line waiter's check, or StandWaitLocked's privacy-locked waiter).
    // facilitychain: after a toilet use, a Sim whose Hygiene is due within 2 sim-hours takes a free shower or bath in the
    // same room next (EA's PushAsContinuation; EA's own WashHands push comes first), and after a shower or bath a due
    // Bladder the same room's free toilet; the queued use counts in the object's busy-until for waiters.
    // Scope: facilityplan NPCs, facilityplanhh household and selected Sims (a visible change: own switch, docs/271);
    // facilityquick / facilitychain the same with their hh switches (scope of the user / the chaining Sim). Off = compare
    // mode at the waits (would lines and counters); the pick hook works only with facilityplan or facilityplanlog on.
    // Counters 4740-4779. Check: FacilityPlanCheck. Rules: FacilityPlanCore.cs.
    public static partial class Fixes
    {
        const int kFpSwitched = 4740, kFpWouldSwitch = 4741, kFpDeferred = 4742, kFpWouldDefer = 4743, kFpKeptEa = 4744, kFpExempt = 4745,
            kFpPickLow = 4746, kFpPickDefer = 4747, kFpPickWould = 4748, kFpUses = 4749, kFpErrors = 4750, kFpReturned = 4751,
            kFpQuick = 4752, kFpQuickWould = 4753, kFpQuickExempt = 4754, kFpChain = 4755, kFpChainWould = 4756, kFpChainExempt = 4757,
            kFpChecks = 4758, kFpPrivacyQuick = 4759, kFpJoinOffers = 4763, kFpBladderDistress = 4764, kFpWaitMinutesX10 = 4765, kFpWaits = 4766,
            kFpCrossShower = 4767;

        sealed class FpUse { public Sim User; public InteractionInstance Ia; public GameObject Obj; public int Kind; public long Start; public float V0; public bool Quick, QuickWould; }
        sealed class FpDefer { public int Kind; public long Start; public float Eta; }
        sealed class FpPickCache { public long Tick; public float Best, BestWait; public bool Urgent, EaWay, Counted; public readonly Dictionary<ulong, float> Eta = new Dictionary<ulong, float>(); public readonly List<ulong> Join = new List<ulong>(); }

        static readonly Dictionary<ulong, FpUse> sFpUses = new Dictionary<ulong, FpUse>();         // by object
        static readonly Dictionary<ulong, long> sFpCool = new Dictionary<ulong, long>();           // by Sim: last move
        static readonly Dictionary<ulong, FpDefer> sFpDefers = new Dictionary<ulong, FpDefer>();   // by Sim
        static readonly Dictionary<ulong, FpPickCache> sFpPick = new Dictionary<ulong, FpPickCache>();  // by Sim * 4 + kind
        static readonly float[] sFpMean = new float[FacilityPlanCore.kKinds];
        static readonly LineCapCore sFpCap = new LineCapCore(40, 60000, 600);
        sealed class FpStat { public string Name; public readonly int[] Uses = new int[FacilityPlanCore.kKinds]; public float Wait; public int Accidents, Switched, Deferred, Chained, Quick; }
        static readonly Dictionary<ulong, FpStat> sFpStats = new Dictionary<ulong, FpStat>();  // per Sim since the last "reset" (repro arms)
        static InteractionDefinition sFpToiletDef;  // UseToilet's definition (a private class), seen at the first toilet use
        static string sFpLast = "", sFpLastError = "", sFpMissing = "";

        static void ApplyFacilityPlan()
        {
            sFpMissing = "";
            if (!Patch(typeof(GameObject), "GetInUseAutonomyMultiplier", new Type[] { typeof(Sim) }, typeof(FpPickHook), "PickHook", "PickOrig")) sFpMissing += " GameObject.GetInUseAutonomyMultiplier";
            if (!Patch(typeof(InteractionInstance), "BeginCommodityUpdates", Type.EmptyTypes, typeof(FpUseHook), "BeginHook", "BeginOrig")) sFpMissing += " InteractionInstance.BeginCommodityUpdates";
            if (!Patch(typeof(InteractionInstance), "EndCommodityUpdates", new Type[] { typeof(bool) }, typeof(FpUseHook), "EndHook", "EndOrig")) sFpMissing += " InteractionInstance.EndCommodityUpdates";
            sFpMissing = sFpMissing.Trim();
            if (sFpMissing.Length > 0) AppliedLog().Add("facilityplan: not found: " + sFpMissing);
        }

        static void FpLine(string text)
        {
            sFpLast = text;
            if (ShortcutOn("facilityplanlog") && sFpCap.Take(System.Environment.TickCount)) DiagLine(text);
        }

        static void FpError(Exception e)
        {
            int n = PatchCounters()[kFpErrors]++;
            sFpLastError = "facilityplan: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sFpLastError);
        }

        // ---- kinds, needs, busy-until -------------------------------------------------------------------------------

        static int FpObjKind(object o)
        {
            if (o is Toilet) return FacilityPlanCore.kToilet;
            if (o is IShowerable) return FacilityPlanCore.kShower;
            if (o is Bathtub) return FacilityPlanCore.kBath;
            if (o is Sink) return FacilityPlanCore.kSink;
            if (o is Fridge) return FacilityPlanCore.kFridge;
            return -1;
        }

        static int FpIaKind(InteractionInstance ia)
        {
            if (ia == null) return -1;
            if (ia is Shower.TakeShower) return FacilityPlanCore.kShower;
            if (ia is Bathtub.TakeBath) return FacilityPlanCore.kBath;
            if (ia is Sink.WashHands || ia is Sink.BrushTeeth) return FacilityPlanCore.kSink;
            if (ia.Target is Toilet && ia.GetType().Name == "UseToilet") return FacilityPlanCore.kToilet;
            return -1;
        }

        static GameObject[] FpObjects(Lot lot, int kind)
        {
            switch (kind)
            {
                case FacilityPlanCore.kToilet: return LswObjects(lot, typeof(Toilet));
                case FacilityPlanCore.kShower: return LswObjects(lot, typeof(IShowerable));
                case FacilityPlanCore.kBath: return LswObjects(lot, typeof(Bathtub));
                case FacilityPlanCore.kFridge: return lot.GetObjects<Fridge>();
                default: return LswObjects(lot, typeof(Sink));
            }
        }

        static CommodityKind FpMotive(int kind) { return kind == FacilityPlanCore.kToilet ? CommodityKind.Bladder : kind == FacilityPlanCore.kFridge ? CommodityKind.Hunger : CommodityKind.Hygiene; }

        static float FpMean(int kind) { float m = sFpMean[kind]; return m > 0f ? m : FacilityPlanCore.Prior(kind); }

        // The Sim's need for a kind: sim-minutes until its urgent level and until an accident (kInfinite: none).
        static float FpNeed(Sim s, int kind, out float accident, out float value)
        {
            accident = FacilityPlanCore.kInfinite; value = 100f;
            if (kind == FacilityPlanCore.kSink || s.Motives == null) return FacilityPlanCore.kInfinite;
            Motive m = s.Motives.GetMotive(FpMotive(kind));
            if (m == null) return FacilityPlanCore.kInfinite;
            value = m.Value;
            if (kind == FacilityPlanCore.kToilet)
            {
                accident = FacilityPlanCore.MinutesToAccident(value, m.Decay);
                return FacilityPlanCore.MinutesTo(value, FacilityPlanCore.kBladderUrgent, m.Decay);
            }
            return FacilityPlanCore.MinutesTo(value, kind == FacilityPlanCore.kFridge ? FacilityPlanCore.kHungerUrgent : FacilityPlanCore.kHygieneUrgent, m.Decay);
        }

        static FpUse FpUseOf(GameObject o)
        {
            FpUse u;
            if (o == null || !sFpUses.TryGetValue(o.ObjectId.Value, out u)) return null;
            if (u.User == null || u.User.HasBeenDestroyed || !object.ReferenceEquals(u.User.CurrentInteraction, u.Ia)) { sFpUses.Remove(o.ObjectId.Value); return null; }
            return u;
        }

        // Remaining sim-minutes of a recorded use.
        static float FpRemaining(FpUse u)
        {
            float so = OjsMinutes(u.Start);
            Shower.TakeShower ts = u.Ia as Shower.TakeShower;
            if (ts != null) return Math.Max(0f, ts.GetShowerTime());  // EA's own: (max - hygiene) / gain, in sim-minutes
            if (u.Kind == FacilityPlanCore.kSink || u.User.Motives == null) return FacilityPlanCore.Remaining(0f, 0f, 0f, FpMean(u.Kind), so);
            CommodityKind c = FpMotive(u.Kind);
            float v = u.User.Motives.GetValue(c), max = u.User.Motives.GetMax(c);
            float gain = so >= 1f ? (v - u.V0) / so : 0f;
            return FacilityPlanCore.Remaining(max, v, gain, FpMean(u.Kind), so);
        }

        // Sim-minutes until the object is free for `forSim`: its user's rest, the line ahead of forSim, Sims that have it
        // queued (a chained hand-off). 0 for a partner's running shower when showertogether would let forSim join.
        static float FpBusy(GameObject o, Sim forSim, int kind, out bool join, out Sim user)
        {
            join = false; user = null;
            float b = 0f;
            FpUse u = FpUseOf(o);
            if (u != null) { user = u.User; b += FpRemaining(u); }
            else if (o.ActorsUsingMe.Count > 0) { user = o.ActorsUsingMe[0]; b += FpMean(kind) * 0.5f; }  // a use begun before we looked
            if (kind == FacilityPlanCore.kShower && user != null && ShortcutOn("showertogether") && sShsMissing.Length == 0)
            {
                Sim other;
                if (ShsDecide(forSim, o, kShsShower, out other) == ShowerShareCore.kGo) { join = true; return 0f; }
            }
            SimQueue line = o.SimLine;
            List<Sim> inLine = line == null ? null : line.SimsInQueue;
            if (inLine != null)
                foreach (Sim m in inLine)
                {
                    if (m == null || m == user) continue;
                    if (m == forSim) break;  // only those ahead
                    b += FpMean(kind);
                }
            Lot lot = o.LotCurrent;
            // a room another Sim holds with privacy (a toilet, shower or bath use; a sink use starts none): its door stays
            // locked for forSim until that use ends, so a free sink or toilet in there is not free for it
            if (lot != null && !lot.IsWorldLot && sFpUses.Count > 0 && lot.RoomLockedByPrivacy(forSim, o.RoomId))
                foreach (FpUse pu in sFpUses.Values)
                    if (pu.Obj != o && pu.Obj.RoomId == o.RoomId && pu.Obj.LotCurrent == lot && pu.Kind != FacilityPlanCore.kSink && pu.User != forSim)
                        b = Math.Max(b, FpRemaining(pu));
            if (lot != null && !lot.IsWorldLot)
                foreach (Sim s in lot.GetSims())
                {
                    if (s == null || s == forSim || s == user || s.InteractionQueue == null || (inLine != null && inLine.Contains(s))) continue;
                    int i = 0;
                    foreach (InteractionInstance q in s.InteractionQueue.InteractionList)
                        if (i++ > 0 && q != null && object.ReferenceEquals(q.Target, o)) { b += FpMean(kind); break; }  // queued, not running
                }
            return b;
        }

        static FpStat FpSt(Sim s)
        {
            FpStat st;
            if (s == null) return new FpStat();
            if (!sFpStats.TryGetValue(s.ObjectId.Value, out st))
            {
                if (sFpStats.Count > 256) sFpStats.Clear();
                st = new FpStat(); st.Name = s.FullName; sFpStats[s.ObjectId.Value] = st;
            }
            return st;
        }

        // ShowerShare.cs's MotiveDistress hook: a bladder failure the game queues.
        static void FpAccident(Sim s) { FpSt(s).Accidents++; }

        static float FpTravel(Sim s, GameObject o)
        {
            return FacilityPlanCore.Travel((o.Position - s.Position).Length(), o.Level - s.Level);
        }

        static bool FpCoolOk(Sim s)
        {
            long t;
            return !sFpCool.TryGetValue(s.ObjectId.Value, out t) || OjsMinutes(t) >= FacilityPlanCore.kCooldownMinutes;
        }

        static string FpObjName(GameObject o) { return o == null ? "?" : o.GetType().Name + " (level " + o.Level + ", room " + o.RoomId + ")"; }

        // ---- pick: the autonomy score factor ------------------------------------------------------------------------

        static float FpPick(GameObject o, Sim actor, float orig)
        {
            int kind = FpObjKind(o);
            if (kind < 0 || actor == null || !LswHuman(actor)) return orig;
            bool on = ShortcutOn("facilityplan"), log = ShortcutOn("facilityplanlog"), share = kind == FacilityPlanCore.kShower && ShortcutOn("showertogether");
            if (!on && !log && !share) return orig;
            if (log && sFsGen == 0) FacilitySlotCheck("start");  // docs/686 §36.10: the slot probe runs with the planner's log (storms)
            Lot lot = o.LotCurrent;
            if (lot == null || lot.IsWorldLot) return orig;
            bool scope = ShlScope(actor, ShortcutOn("facilityplanhh"));
            ulong key = actor.ObjectId.Value * 4UL + (ulong)kind;
            FpPickCache c;
            long now = SimClock.CurrentTicks;
            if (!sFpPick.TryGetValue(key, out c) || c.Tick != now)
            {
                if (sFpPick.Count > 256) sFpPick.Clear();
                if (c == null) { c = new FpPickCache(); sFpPick[key] = c; }
                c.Tick = now; c.Eta.Clear(); c.Join.Clear(); c.Counted = false;
                c.Best = FacilityPlanCore.kInfinite; c.BestWait = FacilityPlanCore.kInfinite;
                GameObject[] objs = FpObjects(lot, kind);
                if (objs != null)
                    foreach (GameObject x in objs)
                    {
                        if (x == null || x.HasBeenDestroyed || !x.InWorld) continue;
                        bool join; Sim user;
                        float busy = FpBusy(x, actor, kind, out join, out user);
                        float walk = FpTravel(actor, x);
                        float eta = FacilityPlanCore.Eta(walk, busy, 0f);
                        c.Eta[x.ObjectId.Value] = eta;
                        if (eta - walk < c.BestWait) c.BestWait = eta - walk;
                        if (join) c.Join.Add(x.ObjectId.Value);
                        if (eta < c.Best) c.Best = eta;
                    }
                float acc, v;
                float mtu = FpNeed(actor, kind, out acc, out v);
                c.Urgent = FacilityPlanCore.Urgent(mtu, c.Best);
                c.EaWay = acc < c.Best;  // an accident comes first whatever we pick: EA's own choice
            }
            if (share && c.Join.Contains(o.ObjectId.Value))
            {
                // a romantic partner showers there and showertogether lets this Sim join: not "in use" for it
                if (!c.Counted) { c.Counted = true; PatchCounters()[kFpJoinOffers]++; FpLine("facilityplan: pick: " + BuWho(actor) + " " + actor.FullName + " may join its partner's shower " + FpObjName(o)); }
                return Math.Max(orig, 1f);
            }
            if (!on && !log) return orig;
            float mine;
            if (c.EaWay || !c.Eta.TryGetValue(o.ObjectId.Value, out mine)) return orig;
            float f = FacilityPlanCore.PickFactor(mine, c.Best, c.BestWait, c.Urgent);
            if (f >= 1f) return orig;
            bool act = on && scope;
            if (!c.Counted)
            {
                c.Counted = true;
                int[] k = PatchCounters();
                if (!act) k[kFpPickWould]++;
                else if (f <= FacilityPlanCore.kPickDefer) k[kFpPickDefer]++;
                else k[kFpPickLow]++;
                FpLine("facilityplan: pick" + (act ? "" : " (would)") + ": " + BuWho(actor) + " " + actor.FullName + ", " + FacilityPlanCore.kKindNames[kind] + " " + FpObjName(o)
                    + " eta " + mine.ToString("0.0") + " vs best " + c.Best.ToString("0.0") + " sim-min, least wait after the walk " + c.BestWait.ToString("0.0") + (f <= FacilityPlanCore.kPickDefer ? ": all busy, another action first" : ": a sooner one exists") + " (x" + f.ToString("0.00") + ")");
            }
            return act ? orig * f : orig;
        }

        public abstract class FpPickHook : GameObject
        {
            public float PickHook(Sim actor)
            {
                float m = PickOrig(actor);
                try { return FpPick(this, actor, m); }
                catch (ResetException) { throw; }
                catch (Exception e) { FpError(e); return m; }
            }
            public float PickOrig(Sim actor) { return 1f; }  // receives GameObject.GetInUseAutonomyMultiplier's original body
        }

        // ---- wait: one check per sim-minute in a facility line --------------------------------------------------------

        static bool FpWait(object queue, Sim sim, LswWait w)
        {
            if (w.Done || w.Target.HasBeenDestroyed || sim.HasExitReason()) return false;
            int kind = FpIaKind(w.Ia);
            if (kind < 0) kind = FpObjKind(w.Target);
            if (kind < 0) return false;
            int[] c = PatchCounters();
            c[kFpChecks]++;
            if (!w.FpSeen) { w.FpSeen = true; c[kFpWaits]++; }
            bool on = ShortcutOn("facilityplan"), human = LswHuman(sim), auto = w.Ia.Autonomous;
            bool scope = ShlScope(sim, ShortcutOn("facilityplanhh"));
            bool joinUnused; Sim user;
            float cur = FpBusy(w.Target, sim, kind, out joinUnused, out user);
            float acc, value;
            float mtu = FpNeed(sim, kind, out acc, out value);
            if (sFpUses.Count > 0) FpQuick(sim, w.Target, mtu, value, kind, "line");
            if (!human) return false;
            // candidates: the other objects of the kind; for a toilet line also showers when showerpeeplan lets the
            // shower fill the bladder and the Sim's Hygiene is not near full
            GameObject best = null; float bestEta = FacilityPlanCore.kInfinite; bool bestFree = false, bestCross = false;
            Lot lot = w.Target.LotCurrent;
            if (lot != null && !lot.IsWorldLot)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    int k = kind;
                    if (pass == 1)
                    {
                        if (kind != FacilityPlanCore.kToilet || !ShortcutOn("showerpeeplan") || !ShlScope(sim, ShortcutOn("showerpeeplanhh")) || !ShpSimOk(sim)
                            || sim.Motives.GetValue(CommodityKind.Hygiene) >= FacilityPlanCore.kQuickHygiene) break;
                        k = FacilityPlanCore.kShower;
                    }
                    GameObject[] objs = FpObjects(lot, k);
                    if (objs == null) continue;
                    foreach (GameObject o in objs)
                    {
                        if (o == null || object.ReferenceEquals(o, w.Target) || o.HasBeenDestroyed || !o.InWorld || (w.FpBad != null && w.FpBad.Contains(o))) continue;
                        if (o.Repairable != null && o.Repairable.Broken) continue;
                        bool join; Sim u2;
                        float busy = FpBusy(o, sim, k, out join, out u2);
                        float eta = FacilityPlanCore.Eta(FpTravel(sim, o), busy, 0f);
                        if (eta < bestEta) { bestEta = eta; best = o; bestFree = busy <= 0f && !join; bestCross = pass == 1; }
                    }
                }
            }
            int why;
            int d = FacilityPlanCore.Decide(on, human, auto, scope, FpCoolOk(sim), cur, bestEta, bestFree, mtu, acc, out why);
            string head = BuWho(sim) + " " + sim.FullName + ", " + w.Ia.GetType().Name + " " + FpObjName(w.Target) + " eta " + cur.ToString("0.0") + " sim-min"
                + (user != null ? " (user " + user.FullName + ")" : "") + ", " + FpMotive(kind) + " " + value.ToString("0") + " (urgent in " + FpMin(mtu) + ", accident in " + FpMin(acc) + "), waited " + OjsMinutes(w.Start).ToString("0.0");
            string to = best == null ? "" : " -> " + (bestCross ? "TakeShower " : "") + FpObjName(best) + " eta " + bestEta.ToString("0.0");
            switch (d)
            {
                case FacilityPlanCore.kWouldSwitch:
                    if (!w.FpWould) { w.FpWould = true; c[kFpWouldSwitch]++; FpLine("facilityplan: would switch: " + head + to); }
                    return false;
                case FacilityPlanCore.kWouldDefer:
                    if (!w.FpWould) { w.FpWould = true; c[kFpWouldDefer]++; FpLine("facilityplan: would do something else first: " + head); }
                    return false;
                case FacilityPlanCore.kExempt:
                    if (!w.FpWould) { w.FpWould = true; c[kFpExempt]++; }
                    return false;
                case FacilityPlanCore.kKeepEa:
                    if (!w.FpKept) { w.FpKept = true; c[kFpKeptEa]++; FpLine("facilityplan: kept, " + FacilityPlanCore.kWhyNames[why] + ": " + head); }
                    return false;
                case FacilityPlanCore.kSwitch:
                    {
                        InteractionDefinition def = bestCross ? Shower.TakeShower.Singleton : w.Ia.InteractionDefinition;
                        InteractionInstance ni = def.CreateInstance(best, sim, w.Ia.GetPriority(), w.Ia.Autonomous, w.Ia.CancellableByPlayer);
                        if (ni == null || !ni.Test()) { FpBadAdd(w, best); return false; }
                        Route r = sim.CreateRoute();
                        r.DoRouteFail = false;
                        r.PlanToPointRadialRange(best, best.PositionOnFloor, 0f, 1.5f, RouteDistancePreference.PreferNearestToRouteDestination,
                            RouteOrientationPreference.TowardsObject, lot.LotId, new int[] { best.RoomId });
                        if (!r.PlanResult.Succeeded()) { FpBadAdd(w, best); return false; }  // left out from the next check on (before: the same failing plan every sim-minute)
                        sim.InteractionQueue.PushAsContinuation(ni, false);
                        ni.WasInUse = true;
                        sim.AddExitReason(ExitReason.FailedToStart);
                        FpLeft(sim, w);
                        c[kFpSwitched]++;
                        FpSt(sim).Switched++;
                        if (bestCross) c[kFpCrossShower]++;
                        FpLine("facilityplan: switched: " + head + to);
                        return true;
                    }
                case FacilityPlanCore.kDefer:
                    {
                        sim.AddExitReason(ExitReason.FailedToStart);
                        FpLeft(sim, w);
                        FpDefer df = new FpDefer();
                        df.Kind = kind; df.Start = SimClock.CurrentTicks; df.Eta = cur;
                        if (sFpDefers.Count > 64) sFpDefers.Clear();
                        sFpDefers[sim.ObjectId.Value] = df;
                        c[kFpDeferred]++;
                        FpSt(sim).Deferred++;
                        FpLine("facilityplan: something else first: " + head + "; back when it is predicted free");
                        return true;
                    }
            }
            return false;
        }

        static void FpBadAdd(LswWait w, GameObject o)
        {
            if (w.FpBad == null) w.FpBad = new List<GameObject>();
            if (w.FpBad.Count < 8) w.FpBad.Add(o);
        }

        // The Sim leaves its line through EA's own tail: no route-fail balloon on this tick (LswSkipRouteFail), cooldown.
        static void FpLeft(Sim sim, LswWait w)
        {
            w.Done = true;
            sLswSim = sim; sLswTick = SimClock.CurrentTicks;
            if (sFpCool.Count > 128) sFpCool.Clear();
            sFpCool[sim.ObjectId.Value] = SimClock.CurrentTicks;
            float m = OjsMinutes(w.Start);
            PatchCounters()[kFpWaitMinutesX10] += (int)(m * 10f);
            FpSt(sim).Wait += m;
        }

        static string FpMin(float m) { return m >= FacilityPlanCore.kInfinite ? "never" : m.ToString("0") + " sim-min"; }

        // LswEnd: the wait is over (turn, timeout, cancel): its length counts.
        static void FpWaitEnd(LswWait w)
        {
            if (!w.FpSeen || w.Done) return;
            float m = OjsMinutes(w.Start);
            PatchCounters()[kFpWaitMinutesX10] += (int)(m * 10f);
            Sim s = w.Ia == null ? null : w.Ia.InstanceActor;
            if (s != null) FpSt(s).Wait += m;
        }

        // ---- facilityquick ------------------------------------------------------------------------------------------

        // A waiter (line or privacy lock) whose need of `kind` is urgent within kQuickUrgentMinutes asks the object's user
        // to end through EA's own finish once its motive is good enough.
        static void FpQuick(Sim waiter, GameObject obj, float waiterMtu, float waiterValue, int waiterKind, string how)
        {
            FpUse u = FpUseOf(obj);
            if (u == null || u.Quick || u.User == waiter || u.Kind == FacilityPlanCore.kSink || u.User.Motives == null) return;
            if (!FacilityPlanCore.WaiterUrgent(waiterMtu)) return;
            CommodityKind ck = FpMotive(u.Kind);
            float v = u.User.Motives.GetValue(ck), so = OjsMinutes(u.Start);
            if (!FacilityPlanCore.QuickEnd(u.Kind, v, so, true)) return;
            int[] c = PatchCounters();
            float saved = FpRemaining(u);
            string text = BuWho(u.User) + " " + u.User.FullName + ", " + u.Ia.GetType().Name + " " + FpObjName(obj) + " after " + so.ToString("0.0") + " sim-min, " + ck + " " + v.ToString("0")
                + " (EA ends at full), saves about " + saved.ToString("0.0") + " sim-min; waiter (" + how + ") " + BuWho(waiter) + " " + waiter.FullName + " " + FpMotive(waiterKind) + " " + waiterValue.ToString("0")
                + ", urgent in " + FpMin(waiterMtu);
            if (!ShortcutOn("facilityquick")) { if (!u.QuickWould) { u.QuickWould = true; c[kFpQuickWould]++; FpLine("facilityquick: would end early: " + text); } return; }
            if (!ShlScope(u.User, ShortcutOn("facilityquickhh"))) { if (!u.QuickWould) { u.QuickWould = true; c[kFpQuickExempt]++; } return; }
            u.Quick = true;
            u.User.AddExitReason(ExitReason.Finished);  // EA's own end of the loop (DuringShower's "motive full"): the normal tail runs
            c[kFpQuick]++;
            FpSt(u.User).Quick++;
            if (how != "line") c[kFpPrivacyQuick]++;
            FpLine("facilityquick: ended early: " + text);
        }

        // StandWaitLocked.cs: `waiter` is held at a door by owner's privacy situation.
        static void FpPrivacyWaiter(Sim waiter, Sim owner)
        {
            try
            {
                if (sFpUses.Count == 0 || waiter == null || owner == null) return;
                InteractionInstance ia = waiter.InteractionQueue == null ? null : waiter.InteractionQueue.GetCurrentInteraction();
                int kind = FpIaKind(ia);
                if (kind < 0 || kind == FacilityPlanCore.kSink) kind = FacilityPlanCore.kToilet;  // a facility visit not yet begun: the bladder decides
                InteractionInstance oc = owner.CurrentInteraction;
                GameObject obj = oc == null ? null : oc.Target as GameObject;
                if (obj == null) return;
                float acc, v;
                float mtu = FpNeed(waiter, kind, out acc, out v);
                FpQuick(waiter, obj, mtu, v, kind, "privacy lock");
            }
            catch (ResetException) { throw; }
            catch (Exception e) { FpError(e); }
        }

        // ---- use: start, end, chain -----------------------------------------------------------------------------------

        static void FpBegin(InteractionInstance ia)
        {
            int kind = FpIaKind(ia);
            if (kind < 0) return;
            GameObject o = ia.Target as GameObject;
            Sim s = ia.InstanceActor;
            if (o == null || s == null || s.Motives == null) return;
            if (kind == FacilityPlanCore.kToilet && sFpToiletDef == null) sFpToiletDef = ia.InteractionDefinition;
            FpUse u = new FpUse();
            u.User = s; u.Ia = ia; u.Obj = o; u.Kind = kind; u.Start = SimClock.CurrentTicks;
            u.V0 = kind == FacilityPlanCore.kSink ? 0f : s.Motives.GetValue(FpMotive(kind));
            if (sFpUses.Count > 128) sFpUses.Clear();
            sFpUses[o.ObjectId.Value] = u;
            FpDefer df;
            if (sFpDefers.TryGetValue(s.ObjectId.Value, out df) && df.Kind == kind)
            {
                sFpDefers.Remove(s.ObjectId.Value);
                PatchCounters()[kFpReturned]++;
                FpLine("facilityplan: came back: " + BuWho(s) + " " + s.FullName + ", " + ia.GetType().Name + " " + FpObjName(o) + " " + OjsMinutes(df.Start).ToString("0.0")
                    + " sim-min after it left the line (predicted free in " + df.Eta.ToString("0.0") + ")");
            }
        }

        static void FpEnd(InteractionInstance ia, bool succeeded)
        {
            GameObject o = ia.Target as GameObject;
            FpUse u;
            if (o == null || !sFpUses.TryGetValue(o.ObjectId.Value, out u) || !object.ReferenceEquals(u.Ia, ia)) return;
            sFpUses.Remove(o.ObjectId.Value);
            float so = OjsMinutes(u.Start);
            if (succeeded && !u.Quick) sFpMean[u.Kind] = FacilityPlanCore.Learn(sFpMean[u.Kind], so);
            PatchCounters()[kFpUses]++;
            Sim s = u.User;
            if (succeeded) FpSt(s).Uses[u.Kind]++;
            if (ShortcutOn("facilityplanlog") && s.Motives != null)
                FpLine("facilityplan: use: " + BuWho(s) + " " + s.FullName + ", " + ia.GetType().Name + " " + FpObjName(o) + " " + so.ToString("0.0") + " sim-min"
                    + (u.Kind == FacilityPlanCore.kSink ? "" : ", " + FpMotive(u.Kind) + " " + u.V0.ToString("0") + " -> " + s.Motives.GetValue(FpMotive(u.Kind)).ToString("0"))
                    + (u.Quick ? " (ended early)" : "") + (succeeded ? "" : " (not finished)") + "; mean " + FacilityPlanCore.kKindNames[u.Kind] + " " + FpMean(u.Kind).ToString("0.0"));
            if (succeeded && u.Kind != FacilityPlanCore.kSink) FpChain(u);
        }

        // facilitychain: the other need of the same visit, in the same room, on a free object.
        static void FpChain(FpUse u)
        {
            Sim s = u.User;
            if (!LswHuman(s) || s.InteractionQueue == null || s.InteractionQueue.Count > 1 || s.Motives == null) return;  // other actions queued: theirs first
            bool toToilet = u.Kind != FacilityPlanCore.kToilet;
            int need = toToilet ? FacilityPlanCore.kToilet : FacilityPlanCore.kShower;
            CommodityKind ck = FpMotive(need);
            float acc, v;
            float mtu = FpNeed(s, need, out acc, out v);
            if (!FacilityPlanCore.ChainDue(v, mtu, s.Motives.GetMax(ck))) return;
            if (toToilet && sShp.ContainsKey(s.ObjectId.Value)) return;  // showerpee fills the bladder as the Sim steps out
            Lot lot = u.Obj.LotCurrent;
            if (lot == null || lot.IsWorldLot) return;
            GameObject next = null;
            int[] kinds = toToilet ? new int[] { FacilityPlanCore.kToilet } : new int[] { FacilityPlanCore.kShower, FacilityPlanCore.kBath };
            foreach (int k in kinds)
            {
                GameObject[] objs = FpObjects(lot, k);
                if (objs == null) continue;
                foreach (GameObject o in objs)
                {
                    if (o == null || o.HasBeenDestroyed || !o.InWorld || o.RoomId != u.Obj.RoomId || o.Level != u.Obj.Level || o.InUse || o.ActorsUsingMe.Count > 0) continue;
                    if (o.Repairable != null && o.Repairable.Broken) continue;
                    SimQueue line = o.SimLine;
                    if (line != null && line.HasMembers()) continue;
                    if (LswClaimed(o, s)) continue;
                    next = o; break;
                }
                if (next != null) break;
            }
            if (next == null) return;
            InteractionDefinition def = toToilet ? sFpToiletDef : FpObjKind(next) == FacilityPlanCore.kShower ? Shower.TakeShower.Singleton : Bathtub.TakeBath.Singleton;
            if (def == null) return;
            int[] c = PatchCounters();
            string text = BuWho(s) + " " + s.FullName + ", after " + u.Ia.GetType().Name + " " + FpObjName(u.Obj) + ": " + ck + " " + v.ToString("0") + " (urgent in " + FpMin(mtu) + ") -> "
                + FpObjName(next) + " in the same visit";
            if (!ShortcutOn("facilitychain")) { c[kFpChainWould]++; FpLine("facilitychain: would chain: " + text); return; }
            if (!ShlScope(s, ShortcutOn("facilitychainhh"))) { c[kFpChainExempt]++; return; }
            InteractionInstance ni = def.CreateInstance(next, s, u.Ia.GetPriority(), true, true);
            if (ni == null || !ni.Test()) return;
            if (!s.InteractionQueue.PushAsContinuation(ni, false)) return;
            c[kFpChain]++;
            FpSt(s).Chained++;
            FpLine("facilitychain: chained: " + text);
        }

        public abstract class FpUseHook : InteractionInstance
        {
            public void BeginHook()
            {
                BeginOrig();
                if (FpIaKind(this) < 0) return;
                try { FpBegin(this); }
                catch (ResetException) { throw; }
                catch (Exception e) { FpError(e); }
            }
            public void BeginOrig() { }  // receives InteractionInstance.BeginCommodityUpdates's original body
            public void EndHook(bool succeeded)
            {
                EndOrig(succeeded);
                if (sFpUses.Count == 0) return;
                try { FpEnd(this, succeeded); }
                catch (ResetException) { throw; }
                catch (Exception e) { FpError(e); }
            }
            public void EndOrig(bool succeeded) { }  // receives InteractionInstance.EndCommodityUpdates's original body
        }

        // ---- check --------------------------------------------------------------------------------------------------

        static string FpCounters()
        {
            int[] c = PatchCounters();
            return "facilityplan: waits " + c[kFpWaits] + " (checks " + c[kFpChecks] + ", line wait " + (c[kFpWaitMinutesX10] / 10f).ToString("0.0") + " sim-min); switched " + c[kFpSwitched]
                + " (to a shower for the bladder " + c[kFpCrossShower] + "), something else first " + c[kFpDeferred] + ", came back " + c[kFpReturned] + "; off: would switch " + c[kFpWouldSwitch]
                + ", would defer " + c[kFpWouldDefer] + "; kept EA (urgent) " + c[kFpKeptEa] + "; exempt " + c[kFpExempt] + "; picks: lowered " + c[kFpPickLow] + ", all late " + c[kFpPickDefer]
                + ", would " + c[kFpPickWould] + ", partner shower offers " + c[kFpJoinOffers] + "; uses " + c[kFpUses] + " (mean toilet " + FpMean(0).ToString("0.0") + ", shower " + FpMean(1).ToString("0.0")
                + ", bath " + FpMean(2).ToString("0.0") + ", sink " + FpMean(3).ToString("0.0") + ", fridge " + FpMean(4).ToString("0.0") + "); facilityquick: ended " + c[kFpQuick] + " (privacy waiters " + c[kFpPrivacyQuick] + "), would " + c[kFpQuickWould]
                + ", exempt " + c[kFpQuickExempt] + "; facilitychain: chained " + c[kFpChain] + ", would " + c[kFpChainWould] + ", exempt " + c[kFpChainExempt] + "; bladder failures " + c[kFpBladderDistress]
                + "; errors " + c[kFpErrors] + (sFpLastError.Length > 0 ? " (last: " + sFpLastError + ")" : "") + "; " + GlMembers(sFpMissing) + (sFpLast.Length > 0 ? "; last: " + sFpLast : "");
        }

        // Per active-household Sim: Bladder, Hygiene now; since the last reset: line wait, uses per kind, accidents, moves.
        static string FpHousehold()
        {
            Sims3.Gameplay.CAS.Household h = Sims3.Gameplay.CAS.Household.ActiveHousehold;
            if (h == null) return "facilityplan household: none";
            System.Text.StringBuilder b = new System.Text.StringBuilder("facilityplan household:");
            float wait = 0f; int trips = 0, acc = 0;
            foreach (Sim s in h.AllActors)
            {
                if (s == null || s.HasBeenDestroyed || s.Motives == null || !s.SimDescription.IsHuman) continue;
                FpStat st; sFpStats.TryGetValue(s.ObjectId.Value, out st);
                if (st == null) st = new FpStat();
                int t = st.Uses[0] + st.Uses[1] + st.Uses[2];
                wait += st.Wait; trips += t; acc += st.Accidents;
                b.Append(" | ").Append(s.FullName).Append(": bladder ").Append(s.Motives.GetValue(CommodityKind.Bladder).ToString("0"))
                    .Append(", hygiene ").Append(s.Motives.GetValue(CommodityKind.Hygiene).ToString("0")).Append(", wait ").Append(st.Wait.ToString("0.0"))
                    .Append(", toilet ").Append(st.Uses[0]).Append(", shower ").Append(st.Uses[1]).Append(", bath ").Append(st.Uses[2]).Append(", sink ").Append(st.Uses[3])
                    .Append(", accidents ").Append(st.Accidents).Append(", switched ").Append(st.Switched).Append(", deferred ").Append(st.Deferred)
                    .Append(", chained ").Append(st.Chained).Append(", ended early ").Append(st.Quick);
            }
            b.Append(" | total: line wait ").Append(wait.ToString("0.0")).Append(" sim-min, trips ").Append(trips).Append(", accidents ").Append(acc);
            return b.ToString();
        }

        // FacilityPlanCheck run: the rules with controls. result: the counters and the last line.
        public static object FacilityPlanCheck(string what)
        {
            if (what == "result") return FpCounters();
            if (what == "reset") { sFpStats.Clear(); return "facilityplan: per-Sim stats cleared"; }
            if (what == "household") return FpHousehold();
            if (what == "fridgeff") return FridgeFfCheck(what);  // docs/686 §36.11 (FridgeFf.cs)
            if (what != null && what.StartsWith("slots")) return FacilitySlotCheck(what.Length > 6 ? what.Substring(6) : "");  // docs/686 §36.10 (FacilitySlot.cs)
            if (what != "run") return "facilityplan <run|result>";
            bool on = ShortcutOn("facilityplan");
            int why;
            const float inf = FacilityPlanCore.kInfinite;
            bool fixedNow = on
                && FacilityPlanCore.Decide(true, true, true, true, true, 12f, 4f, false, 300f, inf, out why) == FacilityPlanCore.kSwitch
                && FacilityPlanCore.Decide(true, true, true, true, true, 12f, inf, false, 300f, inf, out why) == FacilityPlanCore.kDefer
                && FacilityPlanCore.PickFactor(12f, 2f, 0f, false) == FacilityPlanCore.kPickLow
                && FacilityPlanCore.QuickEnd(FacilityPlanCore.kShower, 75f, 6f, true);
            bool controls = FacilityPlanCore.Decide(true, false, true, true, true, 12f, 4f, false, 300f, inf, out why) == FacilityPlanCore.kWait
                && FacilityPlanCore.Decide(true, true, false, true, true, 12f, 4f, false, 300f, inf, out why) == FacilityPlanCore.kWait && why == FacilityPlanCore.kWhyPlayer
                && FacilityPlanCore.Decide(true, true, true, true, true, 12f, 11f, false, 300f, inf, out why) != FacilityPlanCore.kSwitch
                && FacilityPlanCore.Decide(true, true, true, true, true, 12f, inf, false, 300f, 5f, out why) == FacilityPlanCore.kKeepEa
                && FacilityPlanCore.Decide(true, true, true, true, false, 12f, 4f, false, 300f, inf, out why) == FacilityPlanCore.kWait && why == FacilityPlanCore.kWhyCooldown
                && FacilityPlanCore.Decide(false, true, true, true, true, 12f, 4f, false, 300f, inf, out why) == FacilityPlanCore.kWouldSwitch
                && !FacilityPlanCore.QuickEnd(FacilityPlanCore.kShower, 75f, 6f, false) && !FacilityPlanCore.QuickEnd(FacilityPlanCore.kShower, 60f, 6f, true);
            return "facilityplan check (switch " + (on ? "on" : "off") + ", hh " + (ShortcutOn("facilityplanhh") ? "on" : "off") + ", facilityquick " + (ShortcutOn("facilityquick") ? "on" : "off")
                + ", facilitychain " + (ShortcutOn("facilitychain") ? "on" : "off") + "): a waiting Sim moves to a sooner object or does something else first=" + fixedNow
                + "; control: pets, player's order, small gain, urgent with nothing better, cooldown, off=would, quick needs an urgent waiter and a good-enough motive=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sFpMissing.Length == 0) + "; " + FpCounters();
        }
    }
}
