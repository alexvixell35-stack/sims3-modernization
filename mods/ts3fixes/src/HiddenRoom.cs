using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Hidden rooms (docs/552). A room the world builder marked with a HiddenRoomMarker (Lot.IsRoomHidden: Late Night
    // apartment units of NPC neighbours, World Adventures tomb rooms) is closed to every Sim: CommonDoor.IsAllowedThrough
    // answers DoorRestriction for any door that touches one, before the service, IgnoreDoorLocks and lock-type rules, and
    // Sim.OnPortalPathPlan turns that into a locked portal for the route planner [verified, decompiled]. A Sim that ends up
    // inside one cannot walk out through a door ("cannot figure out how to use the doors"). EA's own spawners avoid hidden
    // rooms where they name a spot (NPC-door slots, Service.CreateSim), but several fallbacks place a Sim at Lot.Position
    // (the lot centre on the ground floor) with no room or route check, Service.CreateSim keeps a level-0 elevator spot it
    // did not accept, and EA's stuck check (LotManager.RunStuckCheckHelper) skips the active household's lot and any Sim
    // that moved, so a trapped Sim pacing a hidden room on the player's lot is never reset (docs/552 §2-§3).
    //
    // hiddenroomnpc (off by default): every 10 sim-minutes, an NPC (PlayerSims.cs IsNpcSim) standing in a hidden room on a
    // non-world lot, with no portal EA lets it through that leads outside (HrReachesOutside, EA's own allowed-room flood),
    // seen so on two passes in a row (or on the first pass once a route plan of it has failed: RouteFixes.cs sRouteFails, kept
    // while watchdog + watchdogidle are on), is moved out by EA's own placement: Sim.AttemptToPutInSafeLocation(false) when its
    // queue is empty, else EA's reset (SetObjectToReset, which runs the same placement); a second try the same sim-day sends
    // it home (SetToResetAndSendHome). At most two tries per Sim per sim-day. Household and selected Sims are only counted
    // and logged, never moved. Mummies, Sims in a rabbit hole or held by a parent object are left alone.
    // Counters (each counts a move action taken, never a pass): [2379] trapped NPCs moved out (first try: placement or reset issued), [2380] second tries (sent home), [2381] household Sims seen trapped
    // (log only), [2382] NPCs still trapped after two tries (left), [2383] errors.
    // Prevention of the same bug at EA's unchecked placements: hiddenroomspawn (HiddenRoomSpawn.cs).
    // Harness check: HiddenRoomCheck (HiddenRoomChecks.cs).
    public static partial class Fixes
    {
        const float kHrPassMinutes = 10f;
        const int kHrMaxRooms = 256;  // flood bound (a lot has far fewer rooms)
        static AlarmHandle sHrAlarm = AlarmHandle.kInvalidHandle;

        struct HrTrap { public long First, Day; public int Tries; public bool SeenLastPass; }
        static readonly Dictionary<ulong, HrTrap> sHrTraps = new Dictionary<ulong, HrTrap>();
        // last move per Sim (description id): how, and when (ticks); read by HiddenRoomCheck's trap status lines
        static readonly Dictionary<ulong, string> sHrMovedBy = new Dictionary<ulong, string>();
        static readonly Dictionary<ulong, long> sHrMovedAt = new Dictionary<ulong, long>();

        // No hook: the sweep is an alarm (StartHiddenRoomNpc). The line tells the census the build has it.
        static void ApplyHiddenRoomNpc() { AppliedLog().Add("HiddenRoomNpc.HrPass: ok (sweep alarm, started at world load)"); }

        // Called on every world load (Instantiator). NeverPersisted: not written into the save.
        public static void StartHiddenRoomNpc()
        {
            if (AlarmManager.Global == null) return;
            if (sHrAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sHrAlarm);
            sHrTraps.Clear();
            HrCheckWorldLoad();
            sHrAlarm = AlarmManager.Global.AddAlarmRepeating(kHrPassMinutes, TimeUnit.Minutes, delegate { HrPass(); }, kHrPassMinutes, TimeUnit.Minutes,
                "Ts3Fixes.HiddenRoomNpc", AlarmType.NeverPersisted, null);
        }

        // ---- shared with HiddenRoomChecks.cs ----

        // Can `sim` get from `room` on `lot` to the outside (room 0) through portals EA lets it pass? EA's own flood
        // (Sim.RecalculateAllowedRooms: doors, stairs, ladders, elevators; an ElevatorInterior links all its rooms), asked
        // with IsAllowedThrough(from, to, sim) == None, without the privacy-room callbacks and without touching the Sim's
        // allowed-room cache. Null when a portal threw (unknown: callers do not act on it). Read-only.
        internal static bool? HrReachesOutside(Lot lot, int room, Sim sim, out int rooms)
        {
            return HrWalkOut(lot, room, delegate (IPortalConnectionObject p, int from, int to) { return p.IsAllowedThrough(from, to, sim) == PortalRestriction.None; }, out rooms);
        }

        // Is a portal open from one room to the next? (HrWalkOut's rule: one Sim's IsAllowedThrough, or residentspawnwalk's
        // resident rules, ResidentSpawnSlot.cs)
        internal delegate bool HrPortalOpen(IPortalConnectionObject p, int from, int to);

        // The room walk: breadth-first over the lot's portals from `room` through every open one; true when it reaches an
        // outside room, false when it cannot, null on a throw.
        internal static bool? HrWalkOut(Lot lot, int room, HrPortalOpen open, out int rooms)
        {
            rooms = 0;
            if (LotManager.RoomIdIsOutside(room)) return true;
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            Queue<int> queue = new Queue<int>();
            seen[room] = true;
            queue.Enqueue(room);
            try
            {
                while (queue.Count > 0 && seen.Count < kHrMaxRooms)
                {
                    int from = queue.Dequeue();
                    rooms++;
                    foreach (IPortalConnectionObject p in lot.GetObjectsInRoom<IPortalConnectionObject>(from))
                    {
                        int[] to = p is ElevatorInterior ? p.GetRoomConnections() : new int[] { p.GetAdjoiningRoom(from) };
                        foreach (int r in to)
                        {
                            if (r == 36863 || r == from || seen.ContainsKey(r)) continue;  // 36863: no room on that side
                            if (!open(p, from, r)) continue;
                            if (LotManager.RoomIdIsOutside(r)) return true;
                            seen[r] = true;
                            queue.Enqueue(r);
                        }
                    }
                }
            }
            catch (Exception) { return null; }
            return false;
        }

        // A Sim the hidden-room rules may judge: live, in the world, on a non-world lot, not carried or in a vehicle, not in
        // a rabbit hole, not a tomb mummy (EA locks every portal for those on purpose).
        internal static bool HrJudgeable(Sim s)
        {
            if (s == null || s.HasBeenDestroyed || s.SimDescription == null || !s.InWorld) return false;
            if (s.Parent != null || s.SimInRabbitHolePosture || s.SimDescription.IsTombMummy) return false;
            Lot lot = s.LotCurrent;
            return lot != null && !lot.IsWorldLot;
        }

        // ---- hiddenroomnpc ----

        static void HrPass()
        {
            try
            {
                if (!ShortcutOn("hiddenroomnpc")) { sHrTraps.Clear(); return; }
                if (WorldGone() || !GameStates.IsLiveState || GameStates.IsTravelling) return;
                long now = SimClock.CurrentTicks;
                int[] c = PatchCounters();
                Dictionary<ulong, bool> present = new Dictionary<ulong, bool>();
                foreach (Sim s in new List<Sim>(LotManager.Actors))
                {
                    if (!HrJudgeable(s)) continue;
                    Lot lot = s.LotCurrent;
                    if (!lot.IsRoomHidden(s.RoomId)) continue;
                    int rooms;
                    if (HrReachesOutside(lot, s.RoomId, s, out rooms) != false) continue;  // a way out (or unknown): not trapped
                    ulong id = s.SimDescription.SimDescriptionId;
                    present[id] = true;
                    HrTrap t;
                    bool known = sHrTraps.TryGetValue(id, out t);
                    if (!known || now - t.Day >= kDayTicks || t.Day > now) t = new HrTrap { First = now, Day = now };
                    bool second = t.SeenLastPass;  // trapped on the previous pass too
                    t.SeenLastPass = true;
                    if (IsPlayerSim(s))
                    {
                        // household rule: report only (once per sim-day)
                        if (!known) { c[2381]++; ShortcutLog(s.FullName + " -> hiddenroomnpc: household Sim in hidden room " + s.RoomId + " on " + lot.Name + ", left (household)"); }
                        sHrTraps[id] = t;
                        continue;
                    }
                    RouteFails rf;
                    bool failed = sRouteFails.TryGetValue(s.ObjectId.Value, out rf) && rf.Count > 0;  // it tried to leave and could not
                    bool due = second || failed;
                    if (due && t.Tries < 2) HrMoveOut(s, lot, ref t, c);
                    else if (due && t.Tries == 2) { t.Tries = 3; c[2382]++; ShortcutLog(s.FullName + " -> hiddenroomnpc: still in hidden room " + s.RoomId + " on " + lot.Name + " after two tries, left"); }
                    sHrTraps[id] = t;
                }
                // a Sim not trapped on this pass starts over (keeps its day's tries)
                List<ulong> gone = new List<ulong>();
                foreach (KeyValuePair<ulong, HrTrap> kv in sHrTraps) if (!present.ContainsKey(kv.Key)) gone.Add(kv.Key);
                foreach (ulong id in gone)
                {
                    HrTrap t = sHrTraps[id];
                    if (now - t.Day >= kDayTicks || t.Day > now) sHrTraps.Remove(id);
                    else { t.SeenLastPass = false; sHrTraps[id] = t; }
                }
            }
            catch (Exception) { PatchCounters()[2383]++; }
        }

        // EA's own placement: AttemptToFindSafeLocation rejects the hidden room (its spot fails IsPointInLotSafelyRoutable),
        // then tries a routable spot nearby and the lot's front door, else home.
        static void HrMoveOut(Sim s, Lot lot, ref HrTrap t, int[] c)
        {
            t.Tries++;
            string how;
            if (t.Tries == 1)
            {
                c[2379]++;
                if (s.InteractionQueue != null && s.InteractionQueue.Count == 0) { s.AttemptToPutInSafeLocation(false); how = "moved by the game's safe-spot placement"; }
                else { s.SetObjectToReset(); how = "reset (the game's placement)"; }
            }
            else
            {
                c[2380]++;
                Lot home = s.LotHome ?? s.SimDescription.VirtualLotHome;
                if (home != null && home != lot) { s.SetToResetAndSendHome(); how = "reset and sent home"; }
                else { s.SetObjectToReset(); how = "reset again"; }
            }
            string what = "hiddenroomnpc: " + how + " from hidden room " + s.RoomId + " on " + lot.Name;
            ShortcutLog(s.FullName + " -> " + what);
            ulong id = s.SimDescription.SimDescriptionId;
            if (sHrMovedBy.Count > 256) { sHrMovedBy.Clear(); sHrMovedAt.Clear(); }
            sHrMovedBy[id] = how + (t.Tries == 1 ? "" : " (2nd)");
            sHrMovedAt[id] = SimClock.CurrentTicks;
            MovedSims()[s.FullName] = what + " @" + Math.Round(s.Position.x, 1) + "," + Math.Round(s.Position.z, 1) + " " + SimClock.CurrentTime().ToString();
        }
    }
}
