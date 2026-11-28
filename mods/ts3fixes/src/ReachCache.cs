using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.RouteDestinations;

namespace Ts3Fixes
{
    // reachcache / reachcachehh / reachcachelog (docs/688; tunings kReachCache / kReachCacheHh / kReachCacheLog, all off).
    // A route plan whose every destination room lies behind portals the planner itself would answer "locked" for this Sim
    // (a locked door, a privacy-locked bathroom, a pet or ghost lock) fails with DestinationPortalLocked without searching,
    // and without the RouteFail balloon; autonomy then leaves that (Sim, target, interaction) pair out until the lot's portal
    // cache version changes (EA bumps it on every lock change, privacy start / end and build / buy refresh) or 1 sim-hour.
    // Storm logs s560-s594 (12.1 h): 664 household route fails with a locked portal (55 / h), 234 of them the same Sim and
    // target again; EA's own per-Sim room cache (Sim.IsAllowedInRoom, checked in Autonomy.CheckAvailabilityOnLot) only sees
    // the interaction target's room, not the route's destination rooms (CleanUp plate -> sink, pushed follow-ups).
    //  Verdict: a flood over the lot's IPortalConnectionObject cache from the Sim's room (EA's RecalculateAllowedRooms walk:
    //  GetObjectsInRoom, GetAdjoiningRoom, ElevatorInterior's room list). A Door is locked when Lot.OnPortalPathPlan (the
    //  planner's own callback: Sim.OnPortalPathPlan + every PrivacySituation, with this Sim's current interaction) answers
    //  PortalLocked; every other portal counts as open. Destinations: point and slot destinations (each slot's position) on
    //  one lot; any radial, lot or other destination, room 0 (outdoors) or an unplaced point gives "unknown" and the plan
    //  runs. So the cache refuses only what the planner's own lock answers make impossible.
    //  Scope: reachcache = NPCs, reachcachehh = household and selected Sims; a player's order (UserDirected or higher) is
    //  never refused. Plan and Replan only, sim thread only.
    //  Proof: reachcachelog runs the real plan for every refusal verdict (acting or not) and counts EA's answer: a verdict
    //  "refuse" whose plan succeeded is WRONG ([4824], one log line each), and then EA's result is kept.
    // Counters [4820] verdicts / [4821] refuse verdicts / [4822] refusals acted / [4823] verified right / [4824] verified
    // WRONG / [4825] EA locked but verdict not refuse (missed) / [4826] unknown destination / [4827] start not placed /
    // [4828] player's order exempt / [4829] refuse verdict out of scope (switch off for that Sim) / [4830] autonomy pairs
    // left out / [4831] memo records / [4832] memo entries dropped stale / [4833] floods / [4834] flood cache hits /
    // [4835] errors / [4836] household refusals acted / [4837] verified right with DestinationPortalLocked / [4838] dest
    // list overflow; 4839 spare.
    public static partial class Fixes
    {
        const int kRcMaxDests = 16, kRcMaxRoutes = 512;
        sealed class RcDests { public int N; public bool Unknown; public RouteDestination[] D = new RouteDestination[kRcMaxDests]; }
        sealed class RcFlood { public ulong Lot; public int Start, Version, Ia; public int Tick; public Dictionary<int, bool> Reached; }
        sealed class RcPending { public int Verdict; public bool Acted; public Sim Sim; public string Text; }

        static readonly Dictionary<uint, RcDests> sRcDests = new Dictionary<uint, RcDests>();
        static readonly Dictionary<ulong, RcFlood> sRcFloods = new Dictionary<ulong, RcFlood>();
        static readonly Dictionary<uint, RcPending> sRcPending = new Dictionary<uint, RcPending>();
        static readonly RcMemo sRcMemo = new RcMemo();
        static readonly LineCapCore sRcCap = new LineCapCore(20, 60000, 300);
        static bool sRcAny, sRcLog;
        static int sRcSwitchCalls, sRcSwitchTick;
        static string sRcLastWrong = "";
        static int sRcSumTick;

        static void RcSwitches()
        {
            int now = System.Environment.TickCount;
            if ((sRcSwitchCalls++ & 63) != 0 && NoStartCore.Within(now, sRcSwitchTick, 1000)) return;  // re-read every 64 calls or 1 s (a switch flipped mid-session took up to 64 plans to show)
            sRcSwitchTick = now;
            sRcLog = ShortcutOn("reachcachelog");
            sRcAny = sRcLog || ShortcutOn("reachcache") || ShortcutOn("reachcachehh");
            if (!sRcAny && (sRcDests.Count > 0 || sRcMemo.Count > 0)) { sRcDests.Clear(); sRcMemo.Clear(); sRcFloods.Clear(); sRcPending.Clear(); }
        }

        static void RcLine(string s) { if (sRcCap.Take(System.Environment.TickCount)) DiagLine("reachcache: " + s); }

        // ---- destinations (from BlockedUse.cs's AddDestination / ClearDestinationList hooks) ----

        static void RcNoteDest(uint handle, RouteDestination d)
        {
            RcSwitches();
            if (!sRcAny || handle == 0 || d == null) return;
            RcDests rec;
            if (!sRcDests.TryGetValue(handle, out rec))
            {
                if (sRcDests.Count >= kRcMaxRoutes) sRcDests.Clear();  // routes in flight lose their list: unknown, the plan runs
                rec = new RcDests();
                sRcDests[handle] = rec;
            }
            if (rec.N < kRcMaxDests) rec.D[rec.N++] = d;
            else if (!rec.Unknown) { rec.Unknown = true; PatchCounters()[4838]++; }
        }

        static void RcNoteClear(uint handle)
        {
            if (sRcDests.Count == 0) return;
            sRcDests.Remove(handle);
        }

        // the destination rooms of a route: false = unknown (some destination has no room); room 0 or none = unknown too
        static bool RcRooms(RcDests rec, out Lot lot, int[] rooms, out int n)
        {
            lot = null; n = 0;
            if (rec == null || rec.Unknown || rec.N == 0) return false;
            for (int i = 0; i < rec.N; i++)
            {
                RouteDestination d = rec.D[i];
                PointDestination p = d as PointDestination;
                if (p != null) { if (!RcAddPoint(p.mPoint, ref lot, rooms, ref n)) return false; continue; }
                SlotArrayDestination s = d as SlotArrayDestination;
                GameObject o = s != null ? s.mTargetObject as GameObject : null;
                if (o == null || s.mSlotTargets == null || s.mSlotTargets.Length == 0) return false;
                for (int j = 0; j < s.mSlotTargets.Length; j++)
                {
                    SlotArrayDestination.SlotTarget st = s.mSlotTargets[j];
                    if (st == null) return false;
                    if (!RcAddPoint(o.GetPositionOfSlot(st.mSlot), ref lot, rooms, ref n)) return false;
                }
            }
            return n > 0;
        }

        static bool RcAddPoint(Vector3 pt, ref Lot lot, int[] rooms, ref int n)
        {
            if (pt == Vector3.Invalid || pt == Vector3.OutOfWorld || pt == Vector3.Origin) return false;
            Lot l = LotManager.GetLotAtPoint(pt);
            if (l == null || l.IsWorldLot) return false;
            if (lot == null) lot = l;
            else if (l != lot) return false;
            int room = World.GetRoomId(l.LotId, pt, eRoomDefinition.PlacementBlocking);
            if (room <= 0 || room == ReachCacheCore.kNoRoom) return false;  // outdoors or unplaced: no refusal
            if (n >= rooms.Length) return false;
            rooms[n++] = room;
            return true;
        }

        // ---- the flood: EA's room walk with the planner's lock answers ----

        sealed class RcGraph : IRcRooms
        {
            public Lot L; public Sim S;
            public void Edges(int room, List<RcEdge> into)
            {
                List<IPortalConnectionObject> ps = L.GetObjectsInRoom<IPortalConnectionObject>(room);
                if (ps == null) return;
                for (int i = 0; i < ps.Count; i++)
                {
                    IPortalConnectionObject p = ps[i];
                    if (p == null) continue;
                    if (p is ElevatorInterior)
                    {
                        int[] rc = p.GetRoomConnections();
                        if (rc != null) for (int k = 0; k < rc.Length; k++) if (rc[k] != room) into.Add(new RcEdge(rc[k], false));
                        continue;
                    }
                    int to = p.GetAdjoiningRoom(room);
                    if (to == ReachCacheCore.kNoRoom) continue;
                    Door door = p as Door;
                    bool locked = door != null && L.OnPortalPathPlan(S, door, room, to, false) == PortalLockedCallbackResults.PortalLocked;
                    into.Add(new RcEdge(to, locked));
                }
            }
        }

        static Dictionary<int, bool> RcReached(Sim sim, Lot lot, int start, int iaHash, int[] c)
        {
            int version = lot.GetObjectCacheVersion<IPortalConnectionObject>();
            int now = System.Environment.TickCount;
            RcFlood f;
            if (sRcFloods.TryGetValue(sim.ObjectId.Value, out f) && f.Lot == lot.LotId && f.Start == start && f.Version == version
                && f.Ia == iaHash && NoStartCore.Within(now, f.Tick, 5000))
            {
                c[4834]++;
                return f.Reached;
            }
            c[4833]++;
            List<Door> locked = sim.SimRoutingComponent != null ? sim.SimRoutingComponent.LockedDoorsDuringPlan : null;
            int keep = locked != null ? locked.Count : 0;
            Dictionary<int, bool> reached;
            try { RcGraph g = new RcGraph(); g.L = lot; g.S = sim; reached = ReachCacheCore.Flood(start, g); }
            finally { if (locked != null && locked.Count > keep) locked.RemoveRange(keep, locked.Count - keep); }  // the callback's side list
            if (sRcFloods.Count >= 256) sRcFloods.Clear();
            f = new RcFlood();
            f.Lot = lot.LotId; f.Start = start; f.Version = version; f.Ia = iaHash; f.Tick = now; f.Reached = reached;
            sRcFloods[sim.ObjectId.Value] = f;
            return reached;
        }

        // ---- the plan hook (RouteBackoff.cs PlanHook / ReplanHook) ----

        // true = refuse: the caller returns FailFast(DestinationPortalLocked) without planning
        static bool RcBefore(ScriptCore.Route r, int kind)
        {
            RcSwitches();
            if (!sRcAny || OnWorker()) return false;
            int[] c = PatchCounters();
            int now0 = System.Environment.TickCount;
            if (!NoStartCore.Within(now0, sRcSumTick, 600000)) { sRcSumTick = now0; if (c[4820] > 0) DiagLine(RcCounters()); }  // a counters line every 10 min
            try
            {
                IScriptProxy f = r.Follower;
                Sim sim = f != null ? f.Target as Sim : null;
                if (sim == null || sim.HasBeenDestroyed) return false;
                RcDests rec;
                if (!sRcDests.TryGetValue(r.Handle, out rec)) return false;
                Lot lot; int n; int[] rooms = new int[kRcMaxDests * 8];
                if (!RcRooms(rec, out lot, rooms, out n)) { c[4826]++; return false; }
                Lot cur = sim.LotCurrent;
                int start = 0;
                if (cur == lot)
                {
                    start = sim.RoomId;
                    if (start < 0 || start == ReachCacheCore.kNoRoom
                        || World.GetRoomId(lot.LotId, sim.Position, eRoomDefinition.PlacementBlocking) != start) { c[4827]++; return false; }
                }
                InteractionInstance ia = sim.CurrentInteraction;
                int iaHash = ia != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ia) : 0;
                c[4820]++;
                int v = ReachCacheCore.Verdict(RcReached(sim, lot, start, iaHash, c), rooms, n, true);
                if (v != ReachCacheCore.kRefuse) return false;
                c[4821]++;
                bool hh = IsPlayerSim(sim);
                bool order = ia != null && ia.GetPriority().Level >= InteractionPriorityLevel.UserDirected;
                bool scope = hh ? ShortcutOn("reachcachehh") : ShortcutOn("reachcache");
                if (order) c[4828]++;
                else if (!scope) c[4829]++;
                bool act = scope && !order;
                string text = sim.FullName + (hh ? " household" : " npc") + ", " + (ia != null ? ia.GetType().Name : "none")
                    + (ia != null && ia.Target != null ? " target " + ia.Target.GetType().Name : "") + ", " + PrePlanCbCore.Name(kind)
                    + ", rooms " + n + " (first " + rooms[0] + ") from room " + start + (cur == lot ? "" : " (outside)") + ", lot 0x" + lot.LotId.ToString("X");
                if (sRcLog)
                {
                    // verify: the real plan runs; RcAfter compares and keeps EA's result if it succeeded
                    if (sRcPending.Count >= 64) sRcPending.Clear();
                    RcPending p = new RcPending(); p.Verdict = v; p.Acted = act; p.Sim = sim; p.Text = text;
                    sRcPending[r.Handle] = p;
                    return false;
                }
                if (!act) return false;
                RcActed(r, sim, ia, lot, hh, c, text);
                return true;
            }
            catch (Exception e) { c[4835]++; RcLine("error " + e.GetType().Name + ": " + e.Message); return false; }
        }

        static void RcActed(ScriptCore.Route r, Sim sim, InteractionInstance ia, Lot lot, bool hh, int[] c, string text)
        {
            c[4822]++;
            if (hh) c[4836]++;
            r.DoRouteFail = false;  // EA would fail this plan; the balloon is what the cache saves
            if (ia != null && ia.Target != null && ia.InteractionDefinition != null)
            {
                sRcMemo.Record(sim.ObjectId.Value, ia.Target.ObjectId.Value, ia.InteractionDefinition.GetType().GetHashCode(), lot.LotId,
                    lot.GetObjectCacheVersion<IPortalConnectionObject>(), SimClock.CurrentTicks + SimClock.ConvertToTicks(1f, TimeUnit.Hours));
                c[4831]++;
            }
            if (sRcLog) RcLine("refused " + text);
        }

        // after the real plan: verify a pending refusal verdict (EA's result is always the one handed back), count EA's
        // locked results the verdict missed
        static void RcAfter(ScriptCore.Route r, RoutePlanResult res)
        {
            if (!sRcAny) return;
            int[] c = PatchCounters();
            try
            {
                RcPending p = null;
                bool had = sRcPending.Count > 0 && sRcPending.TryGetValue(r.Handle, out p);
                if (!had)
                {
                    if (sRcLog && res.mType == RoutePlanResultType.FailedBecauseDestinationPortalLocked) c[4825]++;
                    return;
                }
                sRcPending.Remove(r.Handle);
                if (res.mType == RoutePlanResultType.Succeeded)
                {
                    c[4824]++;
                    sRcLastWrong = p.Text;
                    DiagLine("reachcache: WRONG (plan succeeded) " + p.Text);
                    return;
                }
                c[4823]++;
                if (res.mType == RoutePlanResultType.FailedBecauseDestinationPortalLocked) c[4837]++;
                if (p.Acted && p.Sim != null)
                {
                    Lot lot = p.Sim.LotCurrent;
                    InteractionInstance ia = p.Sim.CurrentInteraction;
                    // the plan ran (verify mode); acting still drops the balloon and records the pair
                    if (lot != null) RcActed(r, p.Sim, ia, lot, IsPlayerSim(p.Sim), c, p.Text + " (verified " + res.mType + ")");
                }
                else RcLine("would refuse (verified " + res.mType + ") " + p.Text);
            }
            catch (Exception e) { c[4835]++; RcLine("error after " + e.GetType().Name); }
        }

        // ---- autonomy (AutRoomMemo.cs ArmPairHook, before EA scores the pair) ----

        static bool RcRefusePair(Sims3.Gameplay.Autonomy.Autonomy a, InteractionObjectPair iop)
        {
            if (sRcMemo.Count == 0) return false;
            int[] c = PatchCounters();
            try
            {
                if (a == null || iop == null || a.CurrentSearchType != AutonomySearchType.Autonomy) return false;
                Sim sim = a.Actor;
                IGameObject t = iop.Target;
                if (sim == null || t == null || iop.InteractionDefinition == null) return false;
                Lot lot = sim.LotCurrent;
                if (lot == null) return false;
                if (!(IsPlayerSim(sim) ? ShortcutOn("reachcachehh") : ShortcutOn("reachcache"))) return false;
                int b = sRcMemo.Blocked(sim.ObjectId.Value, t.ObjectId.Value, iop.InteractionDefinition.GetType().GetHashCode(), lot.LotId,
                    lot.GetObjectCacheVersion<IPortalConnectionObject>(), SimClock.CurrentTicks);
                if (b < 0) c[4832]++;
                if (b <= 0) return false;
                c[4830]++;
                return true;
            }
            catch (Exception) { c[4835]++; return false; }
        }

        static string RcCounters()
        {
            int[] c = PatchCounters();
            return "reachcache: verdicts " + c[4820] + ", refuse " + c[4821] + ", acted " + c[4822] + " (household " + c[4836] + "), verified right "
                + c[4823] + " (locked " + c[4837] + "), WRONG " + c[4824] + ", missed locked " + c[4825] + ", unknown dest " + c[4826]
                + ", start unplaced " + c[4827] + ", order exempt " + c[4828] + ", out of scope " + c[4829] + ", autonomy pairs left out "
                + c[4830] + ", memo " + c[4831] + " (stale " + c[4832] + "), floods " + c[4833] + " (hits " + c[4834] + "), overflow " + c[4838]
                + ", errors " + c[4835] + (sRcLastWrong.Length > 0 ? "; last wrong: " + sRcLastWrong : "");
        }

        // reflect_call Ts3Fixes.Fixes.ReachCacheCheck: run | result | lock | unlock | probe
        //  lock: every Door of the selected Sim's lot that touches a room with a toilet is set to Nobody (kept for unlock)
        //  probe: the selected Sim plans to RoutingSlot_0 of each toilet, shower, bed and fridge on its lot (reachcachelog
        //  verifies every refusal verdict against the real plan)
        static readonly List<KeyValuePair<CommonDoor, CommonDoor.tLock>> sRcLocked = new List<KeyValuePair<CommonDoor, CommonDoor.tLock>>();
        public static object ReachCacheCheck(string what)
        {
            if (what == "result") return RcCounters();
            Sim sim = PlumbBob.SelectedActor;
            Lot lot = sim != null ? sim.LotCurrent : null;
            if (what == "lock" || what == "unlock" || what == "probe")
            {
                if (sim == null || lot == null || lot.IsWorldLot) return "reachcache: no selected Sim on a lot";
                if (what == "unlock")
                {
                    foreach (KeyValuePair<CommonDoor, CommonDoor.tLock> kv in sRcLocked) if (kv.Key != null && !kv.Key.HasBeenDestroyed) kv.Key.SetLockType(kv.Value);
                    int m = sRcLocked.Count; sRcLocked.Clear();
                    return "reachcache: unlocked " + m + " doors";
                }
                if (what == "lock")
                {
                    Dictionary<int, bool> rooms = new Dictionary<int, bool>();
                    foreach (IToiletOrUrinal t in lot.GetObjects<IToiletOrUrinal>()) { GameObject g = t as GameObject; if (g != null && g.RoomId > 0) rooms[g.RoomId] = true; }
                    foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
                    {
                        int[] rc = d.GetRoomConnections();
                        bool touch = false;
                        if (rc != null) foreach (int x in rc) if (rooms.ContainsKey(x)) touch = true;
                        if (!touch || d.LockType == CommonDoor.tLock.Nobody) continue;
                        sRcLocked.Add(new KeyValuePair<CommonDoor, CommonDoor.tLock>(d, d.LockType));
                        d.SetLockType(CommonDoor.tLock.Nobody);
                    }
                    return "reachcache: locked " + sRcLocked.Count + " doors around " + rooms.Count + " toilet rooms";
                }
                int plans = 0, ok = 0, failed = 0;
                List<GameObject> targets = new List<GameObject>();
                foreach (IToiletOrUrinal t in lot.GetObjects<IToiletOrUrinal>()) targets.Add(t as GameObject);
                foreach (IShower t in lot.GetObjects<IShower>()) targets.Add(t as GameObject);
                foreach (IBed t in lot.GetObjects<IBed>()) targets.Add(t as GameObject);
                foreach (IFridge t in lot.GetObjects<IFridge>()) targets.Add(t as GameObject);
                foreach (GameObject g in targets)
                {
                    if (g == null || g.HasBeenDestroyed) continue;
                    Route r = sim.CreateRoute();
                    if (r == null) continue;
                    plans++;
                    if (r.PlanToSlot(g, Slot.RoutingSlot_0).Succeeded()) ok++; else failed++;
                }
                return "reachcache probe: " + plans + " plans, " + ok + " ok, " + failed + " failed; " + RcCounters();
            }
            if (what != "run") return "reachcache <run|result|lock|unlock|probe>";
            // rules on a fake lot: 1 = Sim's room, 2 behind an open door, 3 behind a locked door, 4 behind 3
            RcFake g2 = new RcFake();
            Dictionary<int, bool> reached = ReachCacheCore.Flood(1, g2);
            bool rules = ReachCacheCore.Verdict(reached, new int[] { 3 }, 1, true) == ReachCacheCore.kRefuse
                && ReachCacheCore.Verdict(reached, new int[] { 4 }, 1, true) == ReachCacheCore.kRefuse;
            bool controls = ReachCacheCore.Verdict(reached, new int[] { 2 }, 1, true) == ReachCacheCore.kReach
                && ReachCacheCore.Verdict(reached, new int[] { 3, 2 }, 2, true) == ReachCacheCore.kReach
                && ReachCacheCore.Verdict(reached, new int[] { 3 }, 1, false) == ReachCacheCore.kUnknown
                && ReachCacheCore.Verdict(reached, new int[] { 0 }, 1, true) == ReachCacheCore.kReach;
            bool on = ShortcutOn("reachcache") || ShortcutOn("reachcachehh");
            bool fixedNow = on && rules;
            return "reachcache check (switch " + (ShortcutOn("reachcache") ? "on" : "off") + ", hh " + (ShortcutOn("reachcachehh") ? "on" : "off")
                + ", log " + (ShortcutOn("reachcachelog") ? "on" : "off") + "): members found=" + sBuMissing.Equals("") + "; a room behind a locked door is refused="
                + fixedNow + "; control: open door, one reachable destination, unknown destination, outdoors=" + controls + "; verdict: "
                + GlVerdict(on, fixedNow, controls && rules, sBuMissing.Length == 0) + "; " + RcCounters();
        }

        sealed class RcFake : IRcRooms
        {
            public void Edges(int room, List<RcEdge> into)
            {
                if (room == 1) { into.Add(new RcEdge(2, false)); into.Add(new RcEdge(3, true)); into.Add(new RcEdge(0, false)); }
                else if (room == 0) { into.Add(new RcEdge(1, false)); into.Add(new RcEdge(3, true)); }
                else if (room == 3) into.Add(new RcEdge(4, false));
            }
        }
    }
}
