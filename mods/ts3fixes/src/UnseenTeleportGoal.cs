using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
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

        static int UfTpGoalWhy(Sim sim, Vector3 dest)
        {
            Lot lot = LotManager.GetLotAtPoint(dest);
            bool onLot = lot != null && !lot.IsWorldLot;
            int room = World.GetRoomId(dest);  // the id HrRoomTag and the "trap" check read [inferred: same as Sim.RoomId]
            bool hidden = TombRoomManager.IsPointInATombOrHiddenRoom(dest) || (onLot && lot.IsRoomHidden(room));
            bool outdoor = !hidden && UfOutdoorGround(dest);
            bool open = !hidden && !outdoor && onLot && UfLotOpen(lot);
            bool outside = LotManager.RoomIdIsOutside(room);
            int n;
            bool? reach = open && !outside ? HrReachesOutside(lot, room, sim, out n) : null;
            return UfPreCore.TpGoal(hidden, outdoor, open, outside, reach);
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
            if (mode != "goal" || w.Length < 2) return "unseen tp check: goal <hex id|active|hidden> | mark | clean";
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
    }
}
