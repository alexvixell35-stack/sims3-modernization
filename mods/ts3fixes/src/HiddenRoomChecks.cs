using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems.Children;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness check for hidden and sealed rooms (docs/552; test only, called through the agent's reflect_call, never by the
    // game). HiddenRoomCheck(what):
    //   "all" (default) / "apt": every non-world lot ("apt": only lots with hidden rooms or virtual residential slots). Per
    //     lot with a problem, one line: its floor-0 inside rooms that are (H) hidden (Lot.IsRoomHidden), (S) sealed (no
    //     portal object at all), (N) closed to a non-resident NPC probe (no allowed portal path outside, HiddenRoom.cs
    //     HrReachesOutside), (R) closed to a resident probe (should not happen except in hidden rooms); hidden rooms on other
    //     floors are counted. Then where EA's placements land on that lot (EaSpots): Lot.EntryPoint, Lot.Position (the
    //     unconditional fallback of Sim.AttemptToPutInSafeLocation, Lot.EntryPoint and Service.GetPositionNearLotCorners),
    //     Service.GetPositionNearLotCorners, the default FGL (PreferEmptyTiles | Routable) from Lot.Position as a reset's
    //     AttemptToFindSafeLocation runs it, Service.CreateSim's level-0 elevator spot, and 4 FGL samples from random floor-0
    //     inside points (landings in a problem room, and of those how many EA's IsPointInLotSafelyRoutable would accept).
    //     Then one line per Sim standing in a hidden or closed room: NPC or household, room, its route-fail count
    //     (RouteFixes.cs sRouteFails, needs watchdog + watchdogidle), current interaction. Read-only: FGL queries and route
    //     plans only, no object is created or moved.
    //   "lot <hex id|active>": the same for one lot, every line printed even without a problem.
    //   "place <hex id|active> <n>": n FGL samples (max 200) and Sim.FindRoutablePointInsideNearFrontDoor's spot (creates
    //     and destroys a ReservedTile: a temporary object).
    //   "trap <hex id|active|hidden>": the repro step (changes the world): places one adult NPC (never household) directly
    //     inside the lot's first hidden room (floor 0 first) and returns its RoomId / lot right after, so hiddenroomnpc has a
    //     trapped NPC to act on. "lot" then ends with one status line per trapped Sim (still in room / moved by fix / left).
    //   "sim <hex id>": one Sim by the id native `routefail job:` lines print as "follower <id>" (route +0x40, the follower's
    //     ObjectGuid value [inferred]; a SimDescriptionId is tried too): name, scope, lot, room/level, hidden/exit, position,
    //     interaction and queue, child carried, route-fail count. Read-only.
    //   "fallback <hex id|active|hidden>": hiddenroomspawn's deterministic check (read-only): for a non-resident adult NPC,
    //     where the reset's last step puts it (EA: Lot.Position; fix: HrResetFallback), Service.GetPositionNearLotCorners
    //     (through the hook: EA's answer with the switch off, the re-picked one on) and Service.CreateSim's elevator spot;
    //     each with its room tag and a verdict BROKEN (hidden or closed room) / FIXED / OK (EA's spot was fine).
    //   "who": every Sim in a hidden or closed room with the time it has been there and its route-fail count, the trapped
    //     Sims' status, and counters 2379-2383. ("hidden" as a lot: the first non-active lot with a floor-0 hidden room.)
    public static partial class Fixes
    {
        public static object HiddenRoomCheck(string what)
        {
            string[] w = (what ?? "").Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string mode = w.Length > 0 ? w[0] : "all";
            if (!GameStates.IsInWorld()) return "hidden room check: not in a world";
            if (mode == "all" || mode == "apt") return HrScan(null, mode == "apt", 4, false);
            if (mode == "who") { HrWatchStart(); return HrWho(); }
            if (mode == "sim") return w.Length > 1 ? HrSimInfo(w[1]) : "hidden room check: sim <hex id> (ObjectId or SimDescriptionId, as in 'follower <id>')";
            if (w.Length < 2) return "hidden room check: all | apt | who | sim <id> | lot|place|trap|fallback <hex id|active|hidden> | reach <lot> <level> <room|all> [sim hex|active|resident|npc] | place <hex id|active> <n> | trap <hex id|active>";
            Lot lot = HrLot(w[1]);
            if (lot == null) return "hidden room check: no lot " + w[1];
            if (mode == "lot") return HrScan(lot, false, 4, false);
            if (mode == "reach") return HrReachInfo(lot, w);
            if (mode == "place")
            {
                int n = 20;
                if (w.Length > 2) int.TryParse(w[2], out n);
                return HrScan(lot, false, Math.Max(1, Math.Min(200, n)), true);
            }
            if (mode == "trap") return HrTrapNpc(lot);
            if (mode == "fallback") return HrFallbackCheck(lot);
            return "hidden room check: unknown mode " + mode;
        }

        static Lot HrLot(string q)
        {
            if (q == "active") return LotManager.ActiveLot;
            if (q == "hidden")
            {
                // the first lot (no household lot) with a hidden room on the ground floor: the repro target in apartment worlds
                foreach (Lot l in LotManager.AllLots)
                {
                    if (l == null || l.IsWorldLot || l == LotManager.ActiveLot) continue;
                    foreach (HiddenRoomMarker m in l.GetObjects<HiddenRoomMarker>()) if (m.Level == 0) return l;
                }
                return null;
            }
            ulong id;
            if (q.StartsWith("0x") || q.StartsWith("0X")) q = q.Substring(2);
            if (!ulong.TryParse(q, System.Globalization.NumberStyles.HexNumber, null, out id)) return null;
            return LotManager.GetLot(id);
        }

        // Rooms of one level for the single-lot scan (no summary counters; those stay floor 0): tag, portals, size, reach.
        static string HrLevelRooms(Lot lot, int level, Sim npc, Sim res)
        {
            StringBuilder b = new StringBuilder();
            int[] inside = World.GetInsideRoomsAtLevel(lot.LotId, level, eRoomDefinition.PlacementBlocking) ?? new int[0];
            foreach (int room in inside)
            {
                bool hid = lot.IsRoomHidden(room);
                int portals = lot.GetObjectsInRoom<IPortalConnectionObject>(room).Count;
                string rn = HrReach(lot, room, npc), rr = HrReach(lot, room, res);
                string tag = (hid ? "H" : "") + (portals == 0 ? "S" : "") + (rn == "closed" ? "N" : "") + (rr == "closed" ? "R" : "");
                b.Append(" r").Append(room).Append('(').Append(tag.Length > 0 ? tag : "ok").Append(" portals=").Append(portals)
                 .Append(" npc=").Append(rn).Append(" res=").Append(rr).Append(')');
            }
            return b.Length > 0 ? b.ToString() : " none";
        }

        // reach <lot> <level> <room|all> [sim hex|active|resident|npc] (read only): for each room of that level (or one
        // room), out/closed per probe (the selected Sim, a resident, a non-resident NPC, or one named Sim), and each portal
        // in the room with the room it leads to and IsAllowedThrough per probe. For the resident spawn-slot lead (docs/582
        // B5 §8): does an NPC-door slot's room reach outside for anyone.
        static object HrReachInfo(Lot lot, string[] w)
        {
            int level, one = -1;
            if (w.Length < 4 || !int.TryParse(w[2], out level)) return "hidden room check: reach <lot> <level> <room|all> [sim hex|active|resident|npc]";
            if (w[3] != "all" && !int.TryParse(w[3], out one)) return "hidden room check: reach: room must be a number or all";
            List<KeyValuePair<string, Sim>> probes = new List<KeyValuePair<string, Sim>>();
            string pick = w.Length > 4 ? w[4] : null;
            if (pick == null || pick == "active") probes.Add(new KeyValuePair<string, Sim>("selected", PlumbBob.SelectedActor));
            if (pick == null || pick == "resident") probes.Add(new KeyValuePair<string, Sim>("resident", HrProbe(lot, true)));
            if (pick == null || pick == "npc") probes.Add(new KeyValuePair<string, Sim>("npc", HrProbe(lot, false)));
            if (pick != null && pick != "active" && pick != "resident" && pick != "npc")
            {
                string hex = pick.StartsWith("0x") || pick.StartsWith("0X") ? pick.Substring(2) : pick;
                ulong id;
                Sim named = null;
                if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out id))
                    foreach (Sim a in LotManager.Actors)
                        if (a != null && a.SimDescription != null && (a.ObjectId.Value == id || a.SimDescription.SimDescriptionId == id)) { named = a; break; }
                if (named == null) return "hidden room check: reach: no Sim " + pick;
                probes.Add(new KeyValuePair<string, Sim>("sim", named));
            }
            StringBuilder sb = new StringBuilder("reach lot ").Append(lot.LotId.ToString("X")).Append(" \"").Append(lot.Name).Append("\" level ").Append(level).Append(" probes");
            foreach (KeyValuePair<string, Sim> p in probes) sb.Append(' ').Append(p.Key).Append('=').Append(p.Value != null ? p.Value.FullName : "none");
            sb.Append('\n');
            int[] inside = World.GetInsideRoomsAtLevel(lot.LotId, level, eRoomDefinition.PlacementBlocking) ?? new int[0];
            List<int> rooms = new List<int>();
            foreach (int r in inside) if (one < 0 || r == one) rooms.Add(r);
            if (one >= 0 && rooms.Count == 0) { sb.Append("  room ").Append(one).Append(" is not an inside room at level ").Append(level).Append(" (checked anyway)\n"); rooms.Add(one); }
            foreach (int room in rooms)
            {
                sb.Append("  r").Append(room).Append(lot.IsRoomHidden(room) ? " hidden" : "").Append(lot.IsRoomPublic(room) ? " public" : "");
                foreach (KeyValuePair<string, Sim> p in probes) sb.Append(' ').Append(p.Key).Append('=').Append(HrReach(lot, room, p.Value));
                sb.Append('\n');
                foreach (IPortalConnectionObject po in lot.GetObjectsInRoom<IPortalConnectionObject>(room))
                {
                    int[] to = po is ElevatorInterior ? po.GetRoomConnections() : new int[] { po.GetAdjoiningRoom(room) };
                    foreach (int r in to)
                    {
                        if (r == room) continue;
                        sb.Append("    ").Append(po.GetType().Name).Append(" -> r").Append(r == 36863 ? "none" : r.ToString());
                        Door d = po as Door;
                        if (d != null) sb.Append(" lock=").Append(d.LockType).Append(d.IsNPCDoor ? " npc_door" : "");
                        if (r != 36863)
                            foreach (KeyValuePair<string, Sim> p in probes)
                            {
                                string v = "?";
                                if (p.Value != null) { try { v = po.IsAllowedThrough(room, r, p.Value).ToString(); } catch (Exception) { v = "threw"; } }
                                sb.Append(' ').Append(p.Key).Append('=').Append(v);
                            }
                        sb.Append('\n');
                    }
                }
            }
            return sb.ToString();
        }

        // ---- probes ----

        // An adult human NPC who does not live on the lot and is no service worker (non-resident probe), and a resident
        // (LotHome or VirtualLotHome is the lot). Read only: IsAllowedThrough asks with them, nothing moves them.
        static Sim HrProbe(Lot lot, bool resident)
        {
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null || !s.IsHuman || !s.SimDescription.TeenOrAbove) continue;
                bool lives = s.LotHome == lot || s.SimDescription.VirtualLotHome == lot;
                if (resident ? lives : !lives && s.Service == null && IsNpcSim(s)) return s;
            }
            return null;
        }

        static string HrReach(Lot lot, int room, Sim probe)
        {
            if (probe == null) return "?";
            int n;
            bool? ok = HrReachesOutside(lot, room, probe, out n);
            return ok == null ? "err" : ok.Value ? "out" : "closed";
        }

        // Room tag of a world point: r<id> plus H (hidden) / N (closed to the NPC probe); "outside" for room 0.
        static string HrRoomTag(Lot lot, Vector3 p, Sim npc, Dictionary<int, string> problems)
        {
            if (p == Vector3.Invalid) return "invalid";
            int room = World.GetRoomId(p);
            if (LotManager.RoomIdIsOutside(room)) return "outside";
            string tag;
            if (problems.TryGetValue(room, out tag)) return "r" + room + "(" + tag + ")";
            return "r" + room + (lot.IsRoomHidden(room) ? "(H)" : HrReach(lot, room, npc) == "closed" ? "(N)" : "");
        }

        // ---- scan ----

        static object HrScan(Lot only, bool aptOnly, int samples, bool place)
        {
            StringBuilder sb = new StringBuilder();
            int lots = 0, problemLots = 0, hidden0 = 0, sealed0 = 0, npcClosed0 = 0, resClosed0 = 0, landings = 0, accepted = 0, simsIn = 0;
            List<Lot> all = new List<Lot>();
            if (only != null) all.Add(only);
            else foreach (object o in LotManager.AllLots) { Lot l = o as Lot; if (l != null && !l.IsWorldLot) all.Add(l); }
            foreach (Lot lot in all)
            {
                HiddenRoomMarker[] markers = lot.GetObjects<HiddenRoomMarker>();
                if (aptOnly && markers.Length == 0 && !lot.HasVirtualResidentialSlots) continue;
                lots++;
                Sim npc = HrProbe(lot, false), res = HrProbe(lot, true);
                Dictionary<int, string> problems = new Dictionary<int, string>();
                StringBuilder rooms = new StringBuilder();
                int[] inside = World.GetInsideRoomsAtLevel(lot.LotId, 0, eRoomDefinition.PlacementBlocking) ?? new int[0];
                foreach (int room in inside)
                {
                    bool hid = lot.IsRoomHidden(room);
                    int portals = lot.GetObjectsInRoom<IPortalConnectionObject>(room).Count;
                    string rn = HrReach(lot, room, npc), rr = HrReach(lot, room, res);
                    string tag = (hid ? "H" : "") + (portals == 0 ? "S" : "") + (rn == "closed" ? "N" : "") + (rr == "closed" ? "R" : "");
                    if (tag.Length == 0 && only == null) continue;
                    if (tag.Length > 0) problems[room] = tag;
                    if (hid) hidden0++;
                    if (portals == 0) sealed0++;
                    if (rn == "closed") npcClosed0++;
                    if (rr == "closed") resClosed0++;
                    uint size = 0;
                    try { size = World.GetInformationAboutRoom(lot.LotId, room).roomData.sizeInTileQuadrants; } catch (Exception) { }
                    rooms.Append(" r").Append(room).Append('(').Append(tag.Length > 0 ? tag : "ok").Append(" portals=").Append(portals)
                         .Append(" quads=").Append(size).Append(" npc=").Append(rn).Append(" res=").Append(rr).Append(')');
                }
                int hiddenUp = 0;
                foreach (HiddenRoomMarker m in markers) if (m.Level != 0) hiddenUp++;
                if (problems.Count == 0 && only == null) continue;
                problemLots++;
                sb.Append("lot ").Append(lot.LotId.ToString("X")).Append(" \"").Append(lot.Name).Append("\" ")
                  .Append(lot.IsResidentialLot ? "res" : "com").Append(lot.HasVirtualResidentialSlots ? " apt" : "")
                  .Append(" hh=").Append(lot.Household != null ? lot.Household.Name : "-")
                  .Append(" probes npc=").Append(npc != null ? npc.FullName : "none").Append(" res=").Append(res != null ? res.FullName : "none")
                  .Append(" | floor0:").Append(rooms.Length > 0 ? rooms.ToString() : " none")
                  .Append(only != null ? " | floor1:" + HrLevelRooms(lot, 1, npc, res) : "")
                  .Append(" | hidden markers other floors ").Append(hiddenUp).Append('\n');
                sb.Append("  spots:").Append(HrEaSpots(lot, npc, problems, samples, place, ref landings, ref accepted)).Append('\n');
            }
            simsIn = HrSimsIn(sb, only);
            if (only != null) { sb.Append(HrTrapStatus(only)); sb.Append(HrEvents(only)); }
            sb.Append("summary: lots ").Append(lots).Append(", with floor-0 problems ").Append(problemLots).Append(", floor-0 rooms hidden ").Append(hidden0)
              .Append(" sealed ").Append(sealed0).Append(" closed-to-npc ").Append(npcClosed0).Append(" closed-to-resident ").Append(resClosed0)
              .Append(", FGL landings in problem rooms ").Append(landings).Append(" (EA routable check accepts ").Append(accepted).Append(")")
              .Append(", Sims in hidden/closed rooms ").Append(simsIn)
              .Append(", hiddenroomnpc ").Append(ShortcutOn("hiddenroomnpc") ? "on" : "off");
            int[] c = PatchCounters();
            sb.Append(", counters 2379-2383:");
            for (int i = 2379; i <= 2383; i++) sb.Append(' ').Append(c[i]);
            return sb.ToString();
        }

        // Where EA's own placement code lands on this lot (the same calls and parameters as the spawners, docs/552 §2).
        static string HrEaSpots(Lot lot, Sim npc, Dictionary<int, string> problems, int samples, bool place, ref int landings, ref int accepted)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(" entry=").Append(HrTry(delegate { return HrRoomTag(lot, lot.EntryPoint(), npc, problems); }));
            sb.Append(" centre=").Append(HrRoomTag(lot, lot.Position, npc, problems));
            sb.Append(" corners=").Append(HrTry(delegate { return HrRoomTag(lot, Service.GetPositionNearLotCorners(lot), npc, problems); }));
            sb.Append(" svcElevator=").Append(HrTry(delegate { return HrElevatorSpot(lot, npc, problems); }));
            if (npc == null) return sb.Append(" (no NPC probe: FGL samples skipped)").ToString();
            sb.Append(" fglCentre=").Append(HrTry(delegate
            {
                Vector3 pos, fwd;
                if (!GlobalFunctions.FindGoodLocation(npc, new World.FindGoodLocationParams(lot.Position), out pos, out fwd)) return "none";
                return HrRoomTag(lot, pos, npc, problems) + (npc.IsPointInLotSafelyRoutable(lot, pos) ? " safe" : " unsafe");
            }));
            int land = 0, acc = 0, tried = 0;
            for (int i = 0; i < samples; i++)
            {
                try
                {
                    Vector3 start = lot.GetRandomPosition(true, false, TerrainType.LotFloor, (sbyte)0);
                    if (start == Vector3.Invalid) continue;
                    tried++;
                    Vector3 pos, fwd;
                    if (!GlobalFunctions.FindGoodLocation(npc, new World.FindGoodLocationParams(start), out pos, out fwd)) continue;
                    int room = World.GetRoomId(pos);
                    if (!problems.ContainsKey(room) && !lot.IsRoomHidden(room)) continue;
                    land++;
                    if (npc.IsPointInLotSafelyRoutable(lot, pos)) acc++;
                }
                catch (Exception) { }
            }
            landings += land;
            accepted += acc;
            sb.Append(" fglRandom ").Append(land).Append('/').Append(tried).Append(" in problem rooms, EA-accepted ").Append(acc);
            if (place) sb.Append(" frontDoorInside=").Append(HrTry(delegate
            {
                IGameObject tile = null;
                try { return npc.FindRoutablePointInsideNearFrontDoor(lot, out tile) && tile != null ? HrRoomTag(lot, tile.Position, npc, problems) : "none"; }
                finally { if (tile != null) tile.Destroy(); }
            }));
            return sb.ToString();
        }

        // Service.CreateSim's elevator branch (virtual residents working a service): EA assigns the level-0 elevator spot
        // before testing that its room is public, so with no public one the last elevator's spot is kept (docs/552 §2).
        static string HrElevatorSpot(Lot lot, Sim npc, Dictionary<int, string> problems)
        {
            string kept = null;
            foreach (ElevatorDoors e in lot.GetObjects<ElevatorDoors>())
            {
                if (e.Level != 0 || e.InUse) continue;
                kept = HrRoomTag(lot, e.GetPositionOfSlot(Slot.RoutingSlot_0), npc, problems);
                if (lot.IsRoomPublic(e.GetRoomIdOfSlot(Slot.RoutingSlot_0))) return kept + " public";
            }
            return kept == null ? "none" : kept + " NOT public (EA keeps it)";
        }

        delegate string HrPart();
        static string HrTry(HrPart part)
        {
            try { return part(); }
            catch (Exception e) { return "throws " + e.GetType().Name; }
        }

        // One line per Sim standing in a hidden room, or in a room it has no allowed way out of.
        static int HrSimsIn(StringBuilder sb, Lot only)
        {
            int n = 0;
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (!HrJudgeable(s)) continue;
                Lot lot = s.LotCurrent;
                if (only != null && lot != only) continue;
                int room = s.RoomId;
                if (LotManager.RoomIdIsOutside(room)) continue;
                bool hid = lot.IsRoomHidden(room);
                string reach = HrReach(lot, room, s);
                if (!hid && reach != "closed") continue;
                n++;
                ulong did = s.SimDescription.SimDescriptionId;
                HrSeen seen;
                if (!sHrSeen.TryGetValue(did, out seen) || seen.Room != room || seen.Lot != lot)
                {
                    HrTrapped tr;
                    bool trapped = sHrTrapped.TryGetValue(did, out tr) && tr.Lot == lot && tr.Room == room;
                    sHrSeen[did] = seen = new HrSeen { Room = room, Lot = lot, AtTicks = trapped ? tr.AtTicks : SimClock.CurrentTicks, AtReal = trapped ? tr.AtReal : DateTime.Now };
                }
                RouteFails f;
                int fails = sRouteFails.TryGetValue(s.ObjectId.Value, out f) ? f.Count : 0;
                sb.Append("sim ").Append(s.FullName).Append(IsPlayerSim(s) ? " household" : " npc").Append(" lot ").Append(lot.LotId.ToString("X"))
                  .Append(" \"").Append(lot.Name).Append("\" room ").Append(room).Append(" level ").Append(s.Level).Append(hid ? " hidden" : "")
                  .Append(" exit=").Append(reach).Append(" routeFails=").Append(fails)
                  .Append(" there ").Append(Math.Round((DateTime.Now - seen.AtReal).TotalSeconds)).Append(" s / ")
                  .Append(Math.Round(SimClock.ConvertFromTicks(SimClock.CurrentTicks - seen.AtTicks, TimeUnit.Minutes))).Append(" sim-min")
                  .Append(" doing ").Append(s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "nothing").Append('\n');
            }
            return n;
        }

        // ---- trap (repro step, changes the world) and who ----

        // Sims "trap" put into a hidden room, for the status lines (who, lot): where they went and whether the fix moved them.
        class HrTrapped { public Sim Sim; public Lot Lot; public int Room; public long AtTicks; public DateTime AtReal; }
        static readonly Dictionary<ulong, HrTrapped> sHrTrapped = new Dictionary<ulong, HrTrapped>();
        // first time "who"/"lot" saw a Sim in its current hidden or closed room (key: description id; value: room, ticks, real time)
        class HrSeen { public int Room; public Lot Lot; public long AtTicks; public DateTime AtReal; }
        static readonly Dictionary<ulong, HrSeen> sHrSeen = new Dictionary<ulong, HrSeen>();

        // Places one adult NPC (never the active household or a selected Sim: IsNpcSim) directly inside the lot's first
        // hidden room (floor 0 first) with the sequence EA's AttemptToPutInSafeLocation uses (SetPosition, SetForward,
        // RemoveFromWorld, AddToWorld, footprint update), which also moves LotCurrent and RoomId at once; a queued Teleport
        // Me Here waited behind the NPC's current interaction (2026-11-14 run: the off arm saw nobody in the room).
        static object HrTrapNpc(Lot lot)
        {
            HiddenRoomMarker target = null;
            foreach (HiddenRoomMarker m in lot.GetObjects<HiddenRoomMarker>())
                if (target == null || (m.Level == 0 && target.Level != 0)) target = m;
            if (target == null) return "trap: N/A (lot " + lot.Name + " has no hidden room; lots with a floor-0 hidden room and a household: " + HrHiddenHomeLots() + ")";
            Sim npc = null;
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null || !s.InWorld || s.Parent != null || s.SimInRabbitHolePosture) continue;
                if (!IsNpcSim(s) || !s.IsHuman || !s.SimDescription.TeenOrAbove || s.Service != null || s.SimDescription.IsTombMummy) continue;
                if (s.LotHome == lot || s.SimDescription.VirtualLotHome == lot) continue;  // a visitor, not a resident of that lot (EA lets residents home in other ways)
                if (npc == null || (s.LotCurrent == lot && npc.LotCurrent != lot)) npc = s;  // one already on the lot first
            }
            if (npc == null) return "trap: no adult non-resident NPC in the world";
            Vector3 spot = target.Position, fwd = npc.ForwardVector;
            World.FindGoodLocationParams fgl = new World.FindGoodLocationParams(spot, FindGoodLocationBooleans.StayInRoom | FindGoodLocationBooleans.PreferEmptyTiles);
            if (!GlobalFunctions.FindGoodLocation(npc, fgl, out spot, out fwd)) { spot = target.Position; fwd = npc.ForwardVector; }
            npc.InteractionQueue.CancelAllInteractions();
            if (npc.Posture != npc.Standing) npc.Posture = npc.Standing;
            npc.SetPosition(spot);
            npc.SetForward(fwd);
            npc.RemoveFromWorld();
            npc.AddToWorld();
            npc.SimRoutingComponent.ForceUpdateDynamicFootprint();
            ulong id = npc.SimDescription.SimDescriptionId;
            HrWatchStart();
            sHrTrapped[id] = new HrTrapped { Sim = npc, Lot = lot, Room = target.RoomId, AtTicks = SimClock.CurrentTicks, AtReal = DateTime.Now };
            Lot now = npc.LotCurrent;
            bool inRoom = now == lot && npc.RoomId == target.RoomId;
            return "trap: " + npc.FullName + " -> hidden room " + target.RoomId + " level " + target.Level + " on " + lot.Name + " (" + lot.LotId.ToString("X") + ") @"
                + Math.Round(spot.x, 1) + "," + Math.Round(spot.z, 1) + "; now room " + npc.RoomId + " lot " + LotName(now) + (inRoom ? " (in the room)" : " (NOT in the room)")
                + " hidden=" + (now != null && now.IsRoomHidden(npc.RoomId)) + " exit=" + (now != null ? HrReach(now, npc.RoomId, npc) : "?");
        }

        // who: every Sim in a hidden or closed room, how long it has been there (since this check first saw it there, or
        // since "trap" put it there), its route-fail count; then the trapped Sims' status.
        static object HrWho()
        {
            StringBuilder sb = new StringBuilder();
            int n = HrSimsIn(sb, null);
            sb.Append(HrTrapStatus(null));
            sb.Append(HrEvents(null));
            sb.Append("who: ").Append(n).Append(" Sims in hidden/closed rooms, hiddenroomnpc ").Append(ShortcutOn("hiddenroomnpc") ? "on" : "off").Append(", counters 2379-2383:");
            int[] c = PatchCounters();
            for (int i = 2379; i <= 2383; i++) sb.Append(' ').Append(c[i]);
            return sb.ToString();
        }

        // One line per Sim "trap" placed (only those on `only` when given): still in the room / moved by the fix (how) /
        // left another way (where it is now). "moved by fix" needs the fix's own record (HiddenRoom.cs sHrMovedBy), not a pass.
        static string HrTrapStatus(Lot only)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<ulong, HrTrapped> kv in sHrTrapped)
            {
                HrTrapped t = kv.Value;
                if (only != null && t.Lot != only) continue;
                Sim s = t.Sim;
                Sims3.Gameplay.CAS.SimDescription td = Sims3.Gameplay.CAS.SimDescription.Find(kv.Key);
                if (td != null && td.CreatedSim != null) s = td.CreatedSim;  // re-created since the trap: follow the description
                double secs = Math.Round((DateTime.Now - t.AtReal).TotalSeconds), mins = Math.Round(SimClock.ConvertFromTicks(SimClock.CurrentTicks - t.AtTicks, TimeUnit.Minutes));
                sb.Append("trapped ").Append(s != null ? s.FullName : "?").Append(" (room ").Append(t.Room).Append(" on ").Append(t.Lot.Name).Append(", ")
                  .Append(secs).Append(" s / ").Append(mins).Append(" sim-min ago): ");
                string moved;
                if (sHrMovedBy.TryGetValue(kv.Key, out moved) && moved.Length > 0 && sHrMovedAt[kv.Key] >= t.AtTicks) sb.Append("moved by fix (").Append(moved).Append(") after ")
                        .Append(Math.Round(SimClock.ConvertFromTicks(sHrMovedAt[kv.Key] - t.AtTicks, TimeUnit.Minutes))).Append(" sim-min");
                else if (s == null || s.HasBeenDestroyed || !s.InWorld)
                    sb.Append("left (").Append(s == null || s.HasBeenDestroyed ? "Sim gone; " + (td != null ? HrWhereDesc(td) : "no description") : HrWhere(s)).Append(")");
                else if (s.LotCurrent == t.Lot && s.RoomId == t.Room) sb.Append("still in room");
                else sb.Append("left (not by hiddenroomnpc").Append(td != null && MovedSims().ContainsKey(td.FullName) ? "; last move: " + MovedSims()[td.FullName] : "").Append("): now room ").Append(s.RoomId).Append(" on ").Append(LotName(s.LotCurrent))
                        .Append(", doing ").Append(s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "nothing");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // ---- watcher (test only, started by "trap" or "who"): when and how Sims enter and leave hidden rooms ----
        // Every sim-minute, for each Sim on a non-world lot: the first minute it stands in a hidden room ("in": its previous
        // room and interaction) and the first minute it is out again ("out": its new room and lot, interaction, and what
        // moved it: the watchdog / hiddenroomnpc entry of the moved-Sims log if it changed meanwhile, else "walked/other").
        // Plain reads plus List.Contains per Sim; the event list keeps the last 60.
        // MovedIn: the Sim's moved-Sims entry when it entered the hidden room. A mover (watchdog, hiddenroomnpc) writes its entry
        // when it decides, while the Sim is still inside; the reset moves it a tick or more later, so the exit compares with MovedIn.
        class HrState { public Lot Lot; public int Room; public bool Hidden; public string Moved, MovedIn; public long InTicks; }
        static readonly Dictionary<ulong, HrState> sHrState = new Dictionary<ulong, HrState>();
        static readonly List<string> sHrEvents = new List<string>();
        static AlarmHandle sHrWatch = AlarmHandle.kInvalidHandle;

        // World load (StartHiddenRoomNpc): the old world's alarm and Sims are gone.
        internal static void HrCheckWorldLoad()
        {
            sHrWatch = AlarmHandle.kInvalidHandle;
            sHrState.Clear(); sHrEvents.Clear(); sHrTrapped.Clear(); sHrSeen.Clear();
        }

        static void HrWatchStart()
        {
            if (sHrWatch != AlarmHandle.kInvalidHandle || AlarmManager.Global == null) return;
            sHrWatch = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Minutes, delegate { try { HrWatchTick(); } catch (Exception) { } }, 1f, TimeUnit.Minutes,
                "Ts3Fixes.HiddenRoomWatch", AlarmType.NeverPersisted, null);
        }

        static void HrWatchTick()
        {
            long now = SimClock.CurrentTicks;
            Dictionary<string, object> moved = MovedSims();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null) continue;
                Lot lot = s.LotCurrent;
                bool hid = lot != null && !lot.IsWorldLot && lot.IsRoomHidden(s.RoomId);
                ulong id = s.SimDescription.SimDescriptionId;
                object mv;
                string mvs = moved.TryGetValue(s.FullName, out mv) ? Convert.ToString(mv) : "";
                HrState st;
                if (!sHrState.TryGetValue(id, out st))
                {
                    if (sHrState.Count > 2048) sHrState.Clear();
                    sHrState[id] = new HrState { Lot = lot, Room = s.RoomId, Hidden = hid, Moved = mvs, MovedIn = mvs, InTicks = hid ? now : 0 };
                    if (hid) HrEvent("in  " + s.FullName + (IsPlayerSim(s) ? " household" : " npc") + " room " + s.RoomId + " on " + LotName(lot) + " (first seen there), doing " + HrDoing(s));
                    continue;
                }
                if (hid && !st.Hidden)
                    HrEvent("in  " + s.FullName + (IsPlayerSim(s) ? " household" : " npc") + " room " + s.RoomId + " on " + LotName(lot) + " from room " + st.Room + " on " + LotName(st.Lot)
                        + ", doing " + HrDoing(s) + (mvs != st.Moved ? ", moved: " + mvs : ""));
                else if (!hid && st.Hidden)
                    HrEvent("out " + s.FullName + " after " + Math.Round(SimClock.ConvertFromTicks(now - st.InTicks, TimeUnit.Minutes)) + " sim-min: room " + st.Room + " on " + LotName(st.Lot)
                        + " -> room " + s.RoomId + " on " + LotName(lot) + ", doing " + HrDoing(s) + ", how: " + (mvs != st.MovedIn ? mvs : "walked/other (no moved-Sims entry since it went in)"));
                if (hid && !st.Hidden) { st.InTicks = now; st.MovedIn = mvs; }
                st.Lot = lot; st.Room = s.RoomId; st.Hidden = hid; st.Moved = mvs;
                sHrState[id] = st;
            }
        }

        static string HrDoing(Sim s) { return s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "nothing"; }

        static void HrEvent(string e)
        {
            if (sHrEvents.Count >= 60) sHrEvents.RemoveAt(0);
            sHrEvents.Add(SimClock.CurrentTime().ToString() + " " + e);
        }

        static string HrEvents(Lot only)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string e in sHrEvents) if (only == null || e.Contains(LotName(only))) sb.Append("event ").Append(e).Append('\n');
            if (sHrWatch == AlarmHandle.kInvalidHandle) sb.Append("event watcher not started (trap or who starts it)\n");
            return sb.ToString();
        }

        // Lots with a floor-0 hidden room that a household lives on (where a trap on the active lot could be run).
        static string HrHiddenHomeLots()
        {
            StringBuilder sb = new StringBuilder();
            foreach (object o in LotManager.AllLots)
            {
                Lot l = o as Lot;
                if (l == null || l.IsWorldLot || l.Household == null) continue;
                foreach (HiddenRoomMarker m in l.GetObjects<HiddenRoomMarker>())
                    if (m.Level == 0) { sb.Append(sb.Length > 0 ? "; " : "").Append(l.Name).Append(" (").Append(l.Household.Name).Append(")"); break; }
            }
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        // ---- sim <hex id> ----

        static object HrSimInfo(string hex)
        {
            ulong id;
            if (hex.StartsWith("0x") || hex.StartsWith("0X")) hex = hex.Substring(2);
            if (!ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out id)) return "sim: bad id " + hex;
            Sim s = null;
            string by = null;
            foreach (Sim a in LotManager.Actors)
            {
                if (a == null || a.SimDescription == null) continue;
                if (a.ObjectId.Value == id) { s = a; by = "ObjectId"; break; }
                if (a.SimDescription.SimDescriptionId == id) { s = a; by = "SimDescriptionId"; }
            }
            if (s == null)
            {
                Sims3.Gameplay.CAS.SimDescription d = Sims3.Gameplay.CAS.SimDescription.Find(id);
                if (d == null) return "sim " + hex + ": no live Sim with that ObjectId or SimDescriptionId";
                if (d.CreatedSim == null) return "sim " + hex + ": SimDescriptionId of " + d.FullName + ", not instantiated; " + HrWhereDesc(d);
                s = d.CreatedSim;
                by = "SimDescriptionId (created Sim not in the actor list)";
            }
            StringBuilder sb = new StringBuilder();
            // one line per part, each on its own so a throw in one never hides the rest
            HrLine(sb, delegate
            {
                return "sim " + hex + " = " + by + " of " + s.FullName + (IsPlayerSim(s) ? " (household)" : " (npc)") + " objectId " + s.ObjectId.Value.ToString("X")
                    + " descId " + s.SimDescription.SimDescriptionId.ToString("X");
            });
            HrLine(sb, delegate { return "where: " + HrWhere(s); });
            HrLine(sb, delegate
            {
                Lot lot = s.LotCurrent;
                return "lot " + (lot == null ? "none" : "\"" + lot.Name + "\" " + LotName(lot) + (lot.HasVirtualResidentialSlots ? " apt" : "") + (lot.IsResidentialLot ? " res" : " com"))
                    + ", home " + (s.LotHome != null ? s.LotHome.Name : "-") + ", virtual home " + (s.SimDescription.VirtualLotHome != null ? s.SimDescription.VirtualLotHome.Name : "-");
            });
            HrLine(sb, delegate
            {
                Lot lot = s.LotCurrent;
                int room = s.RoomId;
                string hid = lot == null || lot.IsWorldLot ? "n/a" : lot.IsRoomHidden(room) ? "yes" : "no";
                string exit;
                try { exit = lot == null || lot.IsWorldLot || !s.InWorld ? "unknown (not on a lot in the world)" : HrReach(lot, room, s); }
                catch (Exception e) { exit = "unknown (throws " + e.GetType().Name + ")"; }
                return "room " + room + " level " + s.Level + " hidden " + hid + " exit=" + exit
                    + " pos " + Math.Round(s.Position.x, 2) + "," + Math.Round(s.Position.y, 2) + "," + Math.Round(s.Position.z, 2);
            });
            HrLine(sb, delegate
            {
                string q = "";
                foreach (Sims3.Gameplay.Interactions.InteractionInstance ii in s.InteractionQueue.InteractionList) q += " " + ii.GetType().Name + (ii.Target != null ? "->" + ii.Target.GetType().Name : "");
                return "doing " + HrDoing(s) + ", queue:" + q;
            });
            HrLine(sb, delegate
            {
                string child = "none";
                CarryingChildPosture cp = s.CarryingChildPosture;
                if (cp != null) child = cp.Child != null ? cp.Child.FullName + (IsPlayerSim(cp.Child) ? " (household)" : " (npc)") + " objectId " + cp.Child.ObjectId.Value.ToString("X") : "posture without child";
                RouteFails f;
                int fails = sRouteFails.TryGetValue(s.ObjectId.Value, out f) ? f.Count : 0;
                return "carrying child " + child + ", routeFails " + fails + " (watchdog count, needs watchdog + watchdogidle)";
            });
            return sb.ToString().TrimEnd('\n');
        }

        static void HrLine(StringBuilder sb, HrPart part)
        {
            try { sb.Append(part()).Append('\n'); }
            catch (Exception e) { sb.Append("(line throws ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append(")\n"); }
        }

        // Where a created Sim is, also when it is not in the world: in the world, in a rabbit hole, held by a parent object,
        // or out of the world (with its interaction, which usually says why: travel, fade-out, a vehicle).
        static string HrWhere(Sim s)
        {
            if (s.HasBeenDestroyed) return "destroyed";
            string parent = s.Parent != null ? s.Parent.GetType().Name : null;
            string posture = s.Posture != null ? s.Posture.GetType().Name : "none";
            if (s.SimInRabbitHolePosture) return "in a rabbit hole (" + (parent ?? posture) + "), interaction " + HrDoing(s);
            if (!s.InWorld) return "out of the world (posture " + posture + (parent != null ? ", parent " + parent : "") + "), interaction " + HrDoing(s) + "; " + HrWhereDesc(s.SimDescription);
            return "in the world, posture " + posture + (parent != null ? ", parent " + parent : "");
        }

        // Where a Sim that is not in the world belongs: home, virtual home, household, and the moved-Sims entry if any.
        static string HrWhereDesc(Sims3.Gameplay.CAS.SimDescription d)
        {
            string r = "home " + (d.LotHome != null ? d.LotHome.Name : "-") + ", virtual home " + (d.VirtualLotHome != null ? d.VirtualLotHome.Name : "-")
                + ", household " + (d.Household != null ? d.Household.Name : "-");
            object mv;
            if (MovedSims().TryGetValue(d.FullName, out mv)) r += ", last move: " + mv;
            return r;
        }

        // ---- fallback (hiddenroomspawn check) ----

        static object HrFallbackCheck(Lot lot)
        {
            Sim npc = null;
            foreach (Sim s in LotManager.Actors)
                if (s != null && !s.HasBeenDestroyed && s.SimDescription != null && s.InWorld && IsNpcSim(s) && s.IsHuman && s.SimDescription.TeenOrAbove
                    && s.Service == null && s.LotHome != lot && s.SimDescription.VirtualLotHome != lot) { npc = s; break; }
            if (npc == null) return "fallback: no adult non-resident NPC";
            bool on = ShortcutOn("hiddenroomspawn");
            Dictionary<int, string> none = new Dictionary<int, string>();
            StringBuilder sb = new StringBuilder();
            sb.Append("fallback on ").Append(lot.Name).Append(" (").Append(LotName(lot)).Append("), probe ").Append(npc.FullName).Append(", hiddenroomspawn ").Append(on ? "on" : "off").Append('\n');
            bool rp;
            Vector3 ea = lot.Position, fix = HrResetFallback(npc, lot, true, out rp);
            bool eaBad = HrSpotBad(ea, npc);
            Vector3 used = on ? fix : ea;
            sb.Append("reset: EA ").Append(HrRoomTag(lot, ea, npc, none)).Append(", fix ").Append(HrRoomTag(lot, fix, npc, none))
              .Append(", used ").Append(HrRoomTag(lot, used, npc, none)).Append(" -> ").Append(!eaBad ? "OK" : HrSpotBad(used, npc) ? "BROKEN" : "FIXED").Append('\n');
            Vector3 c = Service.GetPositionNearLotCorners(lot);  // hooked: the switch decides
            sb.Append("corners: ").Append(HrRoomTag(lot, c, npc, none)).Append(" -> ").Append(HrSpotBad(c, Sim.ActiveActor) ? "BROKEN" : "OK/FIXED").Append('\n');
            sb.Append("svcElevator: ").Append(HrTry(delegate { return HrElevatorSpot(lot, npc, none); })).Append(" (create hook moves such an NPC: counter 2389)\n");
            int[] k = PatchCounters();
            sb.Append("counters 2387-2390:");
            for (int i = 2387; i <= 2390; i++) sb.Append(' ').Append(k[i]);
            return sb.ToString();
        }
    }
}
