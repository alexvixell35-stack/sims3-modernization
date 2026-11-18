using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/267 F1: the unseen teleport's goal check (unseenfbteleport, on by default; ships with it, no own switch). A cut own
    // Route.Plan places the unseen NPC at the plan's goal (UnseenFallback.cs UfTeleport), but the cut plan never proved that
    // goal reachable: it could be a World Adventures tomb room, a Late Night hidden room, a locked or doorless room, or an upper
    // floor. UfTpGoalWhy refuses those (UfPreCore.TpGoal has the order); a refused goal takes the existing no-teleport path
    // (the cut plan's failed result: EA's own retry or give-up), never a placement into a closed space.
    // Load cleanup: one sim-minute after each world load, an NPC standing in a room with no allowed way out (HrReachesOutside)
    // that our teleport placed exactly there this game session is moved by EA's own placement (AttemptToPutInSafeLocation, or
    // EA's reset when its queue is busy, as hiddenroomnpc does); any other trapped NPC is left and counted (we cannot tell it
    // was us: the placements are kept in memory only, never in the save). Household and selected Sims are never judged.
    // Counters: [kUfTpSlot + reason - 1] goals refused (1 tomb or hidden room, 2 lot closed, 3 room with no way out, 4 raised
    // outdoor; [308] counts them too), [kUfTpSlot + 4] trapped NPCs moved at load (ours), [kUfTpSlot + 5] trapped NPCs left at
    // load (not known to be ours). Harness check: UnseenTpCheck.
    public static partial class Fixes
    {
        const int kUfTpSlot = 1040;

        struct UfTpPlace { public Vector3 At; public string World; }
        // our teleports' placements this game session (SimDescriptionId -> spot); static, so it outlives a world load but not the game
        static readonly Dictionary<ulong, UfTpPlace> sUfTpPlaced = new Dictionary<ulong, UfTpPlace>();

        // tpaptgoal counters [kUfTpAptSlot] home apartment goals allowed, [+1] refused as tomb or hidden room, [+2] refused otherwise
        // (no way out, raised, unknown flood); counted in UfTeleport only (UnseenTpCheck's direct calls do not count)
        const int kUfTpAptSlot = 2753;

        static int UfTpGoalWhy(Sim sim, Vector3 dest)
        {
            bool aptRoom;
            return UfTpGoalWhy(sim, dest, out aptRoom);
        }

        // aptRoom: the goal was judged by tpaptgoal's room-only rule (the Sim's own home apartment / shell lot)
        static int UfTpGoalWhy(Sim sim, Vector3 dest, out bool aptRoom)
        {
            Lot lot = LotManager.GetLotAtPoint(dest);
            bool onLot = lot != null && !lot.IsWorldLot;
            int room = World.GetRoomId(dest);  // the id HrRoomTag and the "trap" check read [inferred: same as Sim.RoomId]
            bool hidden = TombRoomManager.IsPointInATombOrHiddenRoom(dest) || (onLot && lot.IsRoomHidden(room));
            bool outdoor = !hidden && UfOutdoorGround(dest);
            int apt = onLot ? UfTpAptLot(lot, sim) : UfPreCore.kTpAptNone;
            aptRoom = apt == UfPreCore.kTpAptRoom;
            // the usual lot test only for ordinary lots (both versions close apartment / shell lots anyway)
            bool usual = !hidden && !outdoor && onLot && apt == UfPreCore.kTpAptNone && (ShortcutOn("tpdoorrights") ? UfTpLotOpen(lot, sim) : UfLotOpen(lot));
            bool open = !hidden && !outdoor && onLot && UfPreCore.TpAptOpen(apt, usual);
            bool outside = LotManager.RoomIdIsOutside(room);
            int n;
            bool? reach = !open || outside ? null : aptRoom ? UfTpAptReach(lot, room, sim) : HrReachesOutside(lot, room, sim, out n);
            return UfPreCore.TpGoal(hidden, outdoor, open, outside, reach);
        }

        // docs/584 §8 tpaptgoal (default off): an apartment or shell lot is judged by the goal room only when it is the unseen NPC's
        // own home lot (Household.LotHome, as GoHome targets it); any other apartment lot stays closed, as do houseboats, resorts and
        // diving lots. Household, selected and unknown-scope Sims never get the room-only rule (IsPlayerSim: true on any doubt).
        static int UfTpAptLot(Lot lot, Sim sim)
        {
            bool aptOrShell = lot.IsApartmentLot || (lot.BuildableShells != null && lot.BuildableShells.Length > 0);
            if (!aptOrShell) return UfPreCore.kTpAptNone;
            bool other = !HouseboatOk(lot) || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving;
            SimDescription sd = sim == null ? null : sim.SimDescription;
            Household hh = sd == null ? null : sd.Household;
            bool home = hh != null && hh.LotHome == lot && !IsPlayerSim(sim);
            return UfPreCore.TpAptLot(true, other, home, ShortcutOn("tpaptgoal"));
        }

        // the room flood for tpaptgoal: a portal is open when EA lets this Sim through in that direction (IsAllowedThrough: locks,
        // hidden rooms, one-way, horses, strays) and, for a door, TpDoor does not close it outright (a real one-way room or a
        // "nobody" lock, even for a Sim EA would let through, e.g. with IgnoreDoorLocks). Never household access: other units'
        // doors locked to other households close the way, so a resident is placed only in a room it can leave on its own rights.
        // Null (unknown) when a portal threw.
        static bool? UfTpAptReach(Lot lot, int room, Sim sim)
        {
            int n;
            return HrWalkOut(lot, room, delegate (IPortalConnectionObject p, int from, int to)
            {
                if (p.IsAllowedThrough(from, to, sim) != PortalRestriction.None) return false;
                CommonDoor d = p as CommonDoor;
                return d == null || UfPreCore.TpDoor((int)d.LockType, d is DoorSingleGender, d.OneWayRoomDestinationId, true, false, false) != UfPreCore.kTpDoorClosed;
            }, out n);
        }

        // docs/584 §7 tpdoorrights (default off; off = upstream's UfLotOpen): F1's lot test with the teleporting Sim's own door
        // rights. Special lots stay closed as upstream has them (apartments, shells, moving houseboats, resorts, diving lots);
        // then each door by UfPreCore.TpDoor: EA's IsAllowedThrough(sim), and on the Sim's home lot its household's access to a
        // lock a family member owns (GoHome past a sibling's locked room; HrReachesOutside still floods with the Sim's own rights,
        // so it is never placed in a room it cannot leave). Not cached: the verdict is per Sim (one door scan per cut teleport).
        // [2739] lots open here that upstream closes, [2740] of those only by household access, [2741] closed by a door the Sim
        // cannot pass (no lock-free verdict upstream either). Only unseen NPCs reach this (UfTeleport's UfWhy / TownieUnseen).
        static bool UfTpLotOpen(Lot lot, Sim sim)
        {
            if (lot.IsApartmentLot || (lot.BuildableShells != null && lot.BuildableShells.Length > 0) || !HouseboatOk(lot)) return false;  // tpaptgoal judges home ones before this (UfTpGoalWhy)
            if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving) return false;
            int[] c = PatchCounters();
            SimDescription sd = sim.SimDescription;
            Household hh = sd == null ? null : sd.Household;
            bool home = hh != null && hh.LotHome == lot;
            bool upstreamCloses = false;
            int via = UfPreCore.kTpDoorFree;
            foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
            {
                if (d == null) return false;
                int lk = (int)d.LockType;
                bool g = d is DoorSingleGender;
                int ow = d.OneWayRoomDestinationId;
                if (UfPreCore.TpDoorUpstreamCloses(lk, g, ow)) upstreamCloses = true;
                if (lk == UfPreCore.kLockAnybody && !g && ow == UfPreCore.kNoOneWayRoom) continue;  // free: no EA call needed
                bool own;
                try { own = d.IsAllowedThrough(sim); } catch (Exception) { own = false; }  // EA reads mLockOwner without a null check
                int v = UfPreCore.TpDoor(lk, g, ow, own, home, home && !own && UfTpHouseholdOwns(d, hh));
                if (v == UfPreCore.kTpDoorClosed) { c[2741]++; return false; }
                if (v > via) via = v;
            }
            if (upstreamCloses) { c[2739]++; if (via == UfPreCore.kTpDoorHome) c[2740]++; }
            return true;
        }

        // the door's lock belongs to the household: its owner, or one of its owner list, is a member
        static bool UfTpHouseholdOwns(CommonDoor d, Household hh)
        {
            if (hh == null) return false;
            CommonDoor.tLock t = d.LockType;
            if (t == CommonDoor.tLock.SelectedActor || t == CommonDoor.tLock.SelectedHousehold)
                return d.LockOwner != null && d.LockOwner.Household == hh;
            if (t != CommonDoor.tLock.OwnerList || d.OwnerList == null) return false;
            foreach (SimDescription m in hh.AllSimDescriptions)
                if (m != null && d.OwnerList.Contains(m.SimDescriptionId)) return true;
            return false;
        }

        // one ts3native.log line per sim-hour when F1's counters moved (s57 reads why teleports are refused); at most 200 a session
        static AlarmHandle sUfTpLogAlarm = AlarmHandle.kInvalidHandle;
        static string sUfTpLogLast;
        static int sUfTpLogLines;
        static void UfTpLogPass()
        {
            if (sUfTpLogLines >= 200) return;
            int[] c = PatchCounters();
            string line = "unseen tp goal: refused tomb/hidden " + c[kUfTpSlot] + ", lot closed " + c[kUfTpSlot + 1] + ", no way out " + c[kUfTpSlot + 2]
                + ", raised " + c[kUfTpSlot + 3] + " (" + kUfTpSlot + "-" + (kUfTpSlot + 3) + "); load cleanup moved " + c[kUfTpSlot + 4] + " left " + c[kUfTpSlot + 5]
                + "; teleports " + c[307] + "; tpdoorrights " + (ShortcutOn("tpdoorrights") ? "on" : "off") + ": lots opened " + c[2739] + " (household access " + c[2740]
                + "), closed by a door " + c[2741] + " (2739-2741); tpaptgoal " + (ShortcutOn("tpaptgoal") ? "on" : "off") + ": home apartment goals allowed " + c[kUfTpAptSlot]
                + ", refused hidden " + c[kUfTpAptSlot + 1] + ", refused other " + c[kUfTpAptSlot + 2] + " (" + kUfTpAptSlot + "-" + (kUfTpAptSlot + 2) + ")";
            if (line == sUfTpLogLast) return;
            sUfTpLogLast = line;
            sUfTpLogLines++;
            try { DiagLine(line); } catch (Exception) { }
        }

        static string UfTpWhyName(int why)
        {
            return why == UfPreCore.kTpTombOrHidden ? "tomb or hidden room" : why == UfPreCore.kTpLotClosed ? "lot closed or special"
                : why == UfPreCore.kTpNoWayOut ? "room with no way out" : why == UfPreCore.kTpRaised ? "raised outdoor" : "ok";
        }

        static void UfTpNote(Sim sim, Vector3 at)
        {
            if (sim.SimDescription == null) return;
            if (sUfTpPlaced.Count >= 4096) sUfTpPlaced.Clear();  // shortcut: forgets older placements; a later trapped one is only counted
            sUfTpPlaced[sim.SimDescription.SimDescriptionId] = new UfTpPlace { At = at, World = World.GetWorldFileName() };
        }

        static bool UfTpOurs(Sim s)
        {
            UfTpPlace p;
            return sUfTpPlaced.TryGetValue(s.SimDescription.SimDescriptionId, out p) && p.World == World.GetWorldFileName() && (s.Position - p.At).Length() < 1f;
        }

        // Instantiator: every world load
        public static void UfTpCleanupWorldLoad()
        {
            if (AlarmManager.Global == null) return;
            AlarmManager.Global.AddAlarm(1f, TimeUnit.Minutes, delegate { try { UfTpCleanup(); } catch (Exception) { } }, "Ts3Fixes.UnseenTpCleanup", AlarmType.NeverPersisted, null);
            if (sUfTpLogAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sUfTpLogAlarm);
            sUfTpLogLast = null;
            sUfTpLogAlarm = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Hours, delegate { try { UfTpLogPass(); } catch (Exception) { } }, 1f, TimeUnit.Hours, "Ts3Fixes.UnseenTpLog", AlarmType.NeverPersisted, null);
            CallCensusWorldLoad();  // callcensus (CallCensus.cs): its hourly line rides on this world-load call
            DormantProbeWorldLoad();  // dormantprobe (DormantProbe.cs, docs/630): its hourly line, same ride
        }

        // returns "moved m, left l" (the harness reads it)
        static string UfTpCleanup()
        {
            if (!ShortcutOn("unseenfbteleport") || WorldGone() || !GameStates.IsLiveState) return "cleanup: not run (switch off or not live)";
            int[] c = PatchCounters();
            int moved = 0, left = 0;
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (!HrJudgeable(s) || IsPlayerSim(s)) continue;  // household and selected Sims untouched (unknown scope counts as household)
                Lot lot = s.LotCurrent;
                int n;
                if (HrReachesOutside(lot, s.RoomId, s, out n) != false) continue;  // a way out (or unknown): not trapped
                if (!UfTpOurs(s))
                {
                    left++; c[kUfTpSlot + 5]++;
                    ShortcutLog(s.FullName + " -> unseenfb cleanup: in closed room " + s.RoomId + " on " + lot.Name + ", left (not placed there by our teleport this session)");
                    continue;
                }
                bool idle = s.InteractionQueue != null && s.InteractionQueue.Count == 0;
                if (idle) s.AttemptToPutInSafeLocation(false); else s.SetObjectToReset();
                sUfTpPlaced.Remove(s.SimDescription.SimDescriptionId);
                moved++; c[kUfTpSlot + 4]++;
                string what = "unseenfb cleanup: " + (idle ? "moved by the game's safe-spot placement" : "reset (the game's placement)") + " from closed room " + s.RoomId + " on " + lot.Name;
                ShortcutLog(s.FullName + " -> " + what);
                MovedSims()[s.FullName] = what + " " + SimClock.CurrentTime().ToString();
            }
            return "cleanup: moved " + moved + ", left " + left;
        }

        // Harness check (test only, through the agent's reflect_call). UnseenTpCheck(what):
        //   "goal <hex id|active|hidden>": the goal check on that lot, read-only, for a non-resident NPC probe: the first hidden
        //     room's marker spot, one object spot per floor-0 inside room (its room tag from HrRoomTag: H hidden, N closed to the
        //     probe), the lot's entry point. PASS when every H/N spot refuses and at least one open spot passes.
        //   "mark": the NPC the last "trap" put in a hidden room (HiddenRoomCheck) is recorded as placed there by our teleport.
        //   "clean": runs the load cleanup now; "moved m, left l" plus counters.
        public static object UnseenTpCheck(string what)
        {
            string[] w = (what ?? "").Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (!GameStates.IsInWorld()) return "unseen tp check: not in a world";
            string mode = w.Length > 0 ? w[0] : "";
            int[] c = PatchCounters();
            string ctr = " | counters " + kUfTpSlot + "-" + (kUfTpSlot + 5) + ": " + c[kUfTpSlot] + " " + c[kUfTpSlot + 1] + " " + c[kUfTpSlot + 2] + " " + c[kUfTpSlot + 3] + " " + c[kUfTpSlot + 4] + " " + c[kUfTpSlot + 5]
                + ", unseenfbteleport " + (ShortcutOn("unseenfbteleport") ? "on" : "off");
            if (mode == "clean") return UfTpCleanup() + ctr;
            if (mode == "mark")
            {
                foreach (KeyValuePair<ulong, HrTrapped> kv in sHrTrapped)
                {
                    Sim s = kv.Value.Sim;
                    if (s == null || s.HasBeenDestroyed || IsPlayerSim(s)) continue;
                    UfTpNote(s, s.Position);
                    return "mark: " + s.FullName + " @" + Math.Round(s.Position.x, 1) + "," + Math.Round(s.Position.z, 1) + " room " + s.RoomId + ctr;
                }
                return "mark: no trapped NPC (run HiddenRoomCheck trap first)" + ctr;
            }
            if (mode == "apt") return UfTpAptCheck(w.Length > 1 ? w[1] : "any") + ctr;
            if (mode != "goal" || w.Length < 2) return "unseen tp check: goal <hex id|active|hidden> | apt <hex id|any> | mark | clean";
            Lot lot = HrLot(w[1]);
            if (lot == null) return "unseen tp check: no lot " + w[1];
            Sim npc = HrProbe(lot, false);
            if (npc == null) return "goal: no non-resident NPC probe" + ctr;
            System.Text.StringBuilder sb = new System.Text.StringBuilder("goal: lot " + lot.Name + " (" + lot.LotId.ToString("X") + "), probe " + npc.FullName + "\n");
            Dictionary<int, string> none = new Dictionary<int, string>();
            Dictionary<int, bool> rooms = new Dictionary<int, bool>();
            int bad = 0, closed = 0, open = 0;
            List<KeyValuePair<string, Vector3>> spots = new List<KeyValuePair<string, Vector3>>();
            foreach (Sims3.Gameplay.Objects.HiddenRoomMarker m in lot.GetObjects<Sims3.Gameplay.Objects.HiddenRoomMarker>()) { spots.Add(new KeyValuePair<string, Vector3>("hidden marker", m.Position)); break; }
            foreach (Sims3.Gameplay.Abstracts.GameObject o in lot.GetObjects<Sims3.Gameplay.Abstracts.GameObject>())
            {
                if (o == null || o is Sim || o.Level != 0 || LotManager.RoomIdIsOutside(o.RoomId) || rooms.ContainsKey(o.RoomId) || rooms.Count >= 32) continue;
                rooms[o.RoomId] = true;
                spots.Add(new KeyValuePair<string, Vector3>(o.GetType().Name, o.Position));
            }
            spots.Add(new KeyValuePair<string, Vector3>("entry point", lot.EntryPoint()));
            foreach (KeyValuePair<string, Vector3> sp in spots)
            {
                string tag = HrRoomTag(lot, sp.Value, npc, none);
                bool shut = tag.Contains("(H)") || tag.Contains("(N)") || TombRoomManager.IsPointInATombOrHiddenRoom(sp.Value);
                int why = UfTpGoalWhy(npc, sp.Value);
                if (shut) { closed++; if (why == UfPreCore.kTpOk) bad++; }
                else if (why == UfPreCore.kTpOk) open++;
                sb.Append("  ").Append(sp.Key).Append(" ").Append(tag).Append(shut ? " closed" : "").Append(": ").Append(why == 0 ? "teleport ok" : "refused (" + UfTpWhyName(why) + ")")
                  .Append(shut && why == 0 ? " BROKEN" : "").Append('\n');
            }
            sb.Append(bad == 0 && closed > 0 && open > 0 ? "PASS" : closed == 0 ? "N/A (no closed room on this lot)" : bad > 0 ? "FAIL (closed goal accepted)" : "FAIL (no open goal passed)")
              .Append(": closed ").Append(closed).Append(", accepted closed ").Append(bad).Append(", open passed ").Append(open).Append(ctr);
            return sb.ToString();
        }
        // UnseenTpCheck("apt <hex id|any>"), read-only (docs/584 §8 tpaptgoal): on an apartment or shell lot ("any": the first
        // non-active one with an instantiated resident NPC, one with a hidden-room marker first), the goal check for a resident NPC
        // probe (never household or selected) at every hidden-room marker, one object spot per inside room on every floor, and the
        // entry point; then the same inside spots for a non-resident NPC probe (the visitor control). Switch on: PASS when every
        // hidden / closed (H/N) spot is refused, at least one open inside room passes for the resident, and no inside spot passes
        // for the visitor. Switch off: PASS when no inside spot passes (upstream). Also the live-path assert of the hook F1 runs in.
        static string UfTpAptCheck(string q)
        {
            Lot lot = null;
            Sim res = null;
            if (q == "any")
            {
                for (int pass = 0; pass < 2 && lot == null; pass++)
                    foreach (Lot l in LotManager.AllLots)
                    {
                        if (l == null || l.IsWorldLot || l == LotManager.ActiveLot || UfTpAptLot(l, null) == UfPreCore.kTpAptNone) continue;
                        bool marker = false;
                        foreach (Sims3.Gameplay.Objects.HiddenRoomMarker m in l.GetObjects<Sims3.Gameplay.Objects.HiddenRoomMarker>()) { marker = true; break; }
                        if (pass == 0 && !marker) continue;
                        Sim r = UfTpAptResident(l);
                        if (r != null) { lot = l; res = r; break; }
                    }
                if (lot == null) return "apt: N/A (no apartment or shell lot with an instantiated resident NPC)";
            }
            else
            {
                lot = HrLot(q);
                if (lot == null) return "apt: no lot " + q;
                if (UfTpAptLot(lot, null) == UfPreCore.kTpAptNone) return "apt: N/A (" + lot.Name + " is not an apartment or shell lot)";
                res = UfTpAptResident(lot);
                if (res == null) return "apt: N/A (no instantiated resident NPC on " + lot.Name + ")";
            }
            bool on = ShortcutOn("tpaptgoal");
            Sim vis = HrProbe(lot, false);
            System.Text.StringBuilder sb = new System.Text.StringBuilder("apt: lot " + lot.Name + " (" + lot.LotId.ToString("X") + ", apartment " + lot.IsApartmentLot
                + ", shells " + (lot.BuildableShells == null ? 0 : lot.BuildableShells.Length) + "), tpaptgoal " + (on ? "on" : "off") + ", resident " + res.FullName
                + ", visitor " + (vis == null ? "none" : vis.FullName) + "\n");
            List<KeyValuePair<string, Vector3>> spots = new List<KeyValuePair<string, Vector3>>();
            foreach (Sims3.Gameplay.Objects.HiddenRoomMarker m in lot.GetObjects<Sims3.Gameplay.Objects.HiddenRoomMarker>())
                if (spots.Count < 8) spots.Add(new KeyValuePair<string, Vector3>("hidden marker", m.Position));
            Dictionary<int, bool> rooms = new Dictionary<int, bool>();
            foreach (Sims3.Gameplay.Abstracts.GameObject o in lot.GetObjects<Sims3.Gameplay.Abstracts.GameObject>())
            {
                if (o == null || o is Sim || o.Parent != null || LotManager.RoomIdIsOutside(o.RoomId) || rooms.ContainsKey(o.RoomId) || rooms.Count >= 48) continue;
                rooms[o.RoomId] = true;
                spots.Add(new KeyValuePair<string, Vector3>(o.GetType().Name + " L" + o.Level, o.Position));
            }
            spots.Add(new KeyValuePair<string, Vector3>("entry point", lot.EntryPoint()));
            Dictionary<int, string> none = new Dictionary<int, string>();
            int closed = 0, bad = 0, open = 0, inside = 0, insideOk = 0, visOk = 0;
            foreach (KeyValuePair<string, Vector3> sp in spots)
            {
                string tag = HrRoomTag(lot, sp.Value, res, none);
                bool indoor = !LotManager.RoomIdIsOutside(World.GetRoomId(sp.Value));
                bool shut = tag.Contains("(H)") || tag.Contains("(N)") || TombRoomManager.IsPointInATombOrHiddenRoom(sp.Value);
                bool aptRoom;
                int why = UfTpGoalWhy(res, sp.Value, out aptRoom);
                int vwhy = vis == null || !indoor ? -1 : UfTpGoalWhy(vis, sp.Value);
                if (shut) { closed++; if (why == UfPreCore.kTpOk) bad++; }
                else if (why == UfPreCore.kTpOk && indoor) open++;
                if (indoor) { inside++; if (why == UfPreCore.kTpOk) insideOk++; if (vwhy == UfPreCore.kTpOk) visOk++; }
                sb.Append("  ").Append(sp.Key).Append(" ").Append(tag).Append(shut ? " closed" : "").Append(": ").Append(why == 0 ? "teleport ok" : "refused (" + UfTpWhyName(why) + ")")
                  .Append(aptRoom ? " [room rule]" : "").Append(vwhy < 0 ? "" : vwhy == 0 ? "; visitor teleport ok VISITOR-BROKEN" : "; visitor refused (" + UfTpWhyName(vwhy) + ")")
                  .Append(shut && why == 0 ? " BROKEN" : "").Append('\n');
            }
            string live = "";  // live path: F1 runs inside RouteBackoffHook's swaps (sPatched keeps the last hook of a stacked swap, so at least one)
            int hookSwaps = 0;
            foreach (Type h in sPatched.Values) if (h == typeof(RouteBackoffHook)) hookSwaps++;
            if (hookSwaps == 0) live = "RouteBackoffHook swaps 0";
            string verdict;
            if (live.Length > 0) verdict = "FAIL (live path: " + live + ")";
            else if (!on) verdict = insideOk == 0 ? "PASS (off: every inside goal refused, as upstream)" : "FAIL (off but an inside goal passed)";
            else if (bad > 0) verdict = "FAIL (closed goal accepted)";
            else if (visOk > 0) verdict = "FAIL (visitor goal accepted)";
            else if (closed == 0) verdict = "N/A (no hidden or closed room on this lot)";
            else verdict = open > 0 ? "PASS" : "FAIL (no open inside goal passed)";
            sb.Append(verdict).Append(": closed ").Append(closed).Append(", accepted closed ").Append(bad).Append(", open passed ").Append(open)
              .Append(", inside ").Append(inside).Append(", visitor accepted ").Append(visOk);
            return sb.ToString();
        }

        // an instantiated resident of the lot (Household.LotHome) the room rule may judge: human teen or older, not household or selected
        static Sim UfTpAptResident(Lot lot)
        {
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null || !s.IsHuman || !s.SimDescription.TeenOrAbove) continue;
                if (s.LotHome == lot && !IsPlayerSim(s)) return s;
            }
            return null;
        }
    }
}
