using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness check for aptleave (AptLeave.cs, docs/553; test only, called through the agent's reflect_call, never by the
    // game). ApartmentLeaveCheck(what), lot = "active" (default) or a hex lot id:
    //   "list [lot]": read-only. Whether the lot has virtual residential slots (EA's apartment rule), every room a door
    //     touches tagged P (public: PublicRoomMarker or outside), H (hidden: HiddenRoomMarker), U (unmarked: the household's
    //     apartment); the doors between U and P (where EA's room-change farewell fires); hosted situations (parties) on
    //     the lot with guests / left / left early; every NPC on the lot with room tag, farewell-shown flag and party-guest
    //     state; then the last room-change farewell decisions (AptLeave.cs sAlEvents), switch state and counters 2391-2394.
    //   "visit [lot]" (changes the world, test setup like HiddenRoomCheck's trap): picks one adult human NPC that does not
    //     live on the lot (one already there first), puts it directly in the U room of a U|P door, greets it on the lot and
    //     makes sure it has a visit (EA's VisitSituation.Create); the visit's farewell flag is cleared so the test starts
    //     fresh. Then queues EA's Terrain GoHere (target lot = this lot) to a spot in the P room behind that door, the walk
    //     that fires EA's room-change farewell. Read the result with "status" after ~10-20 s.
    //   "party [lot] [make]": the same, and the NPC also becomes a guest of a hosted situation (party) on the lot, the way
    //     HouseParty.TimeToInviteGuests adds one (Guests.Add + AssignRole); its left/left-early entries are cleared. With no
    //     party on the lot: N/A, unless "make": then a HouseParty (no invitations, no invited guests) is created with the
    //     selected Sim as host, starting now ("unparty" ends it). Test saves only: the household gets a party situation.
    //   "status": the test Sim of the last visit/party: room tag, current interaction, farewell flag, party guest /
    //     left early, plus the decisions recorded since the walk was queued, and a verdict:
    //       BROKEN  it reached the P room and EA's farewell ran (flag set or removed from the party) while it was not leaving
    //       FIXED   it reached the P room, the call was skipped (aptleave), flag clear and still a guest
    //       WAIT    not in the P room yet (or no decision recorded yet)
    //   "home": EA's Sim.MakeSimGoHome for the test Sim (a real departure): "status" must then show the farewell flag set
    //     (EA's GoHome call, never touched by the fix) in both arms.
    //   "unparty": exits the HouseParty "party make" created.
    //   "throw [n] [now]" / "nudge" / "partyreport" / "partyend": a real party thrown the phone's way, per-guest tracking
    //     and EA's party score (AptLeavePartyCheck.cs).
    public static partial class Fixes
    {
        static Sim sAlTestSim;
        static int sAlTestU = -1, sAlTestP = -1;
        static long sAlTestAt;
        static HostedSituation sAlMadeParty;

        public static object ApartmentLeaveCheck(string what)
        {
            string[] w = (what ?? "").Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string mode = w.Length > 0 ? w[0] : "list";
            if (!GameStates.IsInWorld()) return "apartment leave check: not in a world";
            try
            {
                if (mode == "status") return AlStatus();
                if (mode == "home") return AlHome();
                if (mode == "unparty") return AlUnparty();
                object real = AlPartyMode(mode, w);  // throw / nudge / partyreport / partyend (AptLeavePartyCheck.cs)
                if (real != null) return real;
                Lot lot = AlLot(w.Length > 1 && w[1] != "make" ? w[1] : "active");
                if (lot == null) return "apartment leave check: no lot " + (w.Length > 1 ? w[1] : "active");
                if (mode == "list") return AlList(lot);
                if (mode == "visit") return AlSetup(lot, false, false);
                if (mode == "party") return AlSetup(lot, true, Array.IndexOf(w, "make") > 0);
                return "apartment leave check: list|visit|party [make] [hex lot id|active] | status|home|unparty | throw [n] [now]|nudge|partyreport|partyend";
            }
            catch (Exception e) { return "apartment leave check: " + e.GetType().Name + ": " + e.Message; }
        }

        static Lot AlLot(string q)
        {
            if (q == "active") return LotManager.ActiveLot;
            ulong id;
            if (!ulong.TryParse(q, System.Globalization.NumberStyles.HexNumber, null, out id)) return null;
            return LotManager.GetLot(id);
        }

        static string AlTag(Lot lot, int room)
        {
            if (LotManager.RoomIdIsOutside(room)) return "P(out)";
            if (lot.IsRoomHidden(room)) return "H";
            return lot.IsRoomPublic(room) ? "P" : "U";
        }

        static string AlRoom(Lot lot, int room) { return "r" + room + AlTag(lot, room); }

        // U|P doors: an unlocked door between an unmarked (not hidden) room and a public inside room. Pairs (door, U, P).
        struct AlDoor { public Door Door; public int U, P; }
        static List<AlDoor> AlDoors(Lot lot)
        {
            List<AlDoor> list = new List<AlDoor>();
            foreach (Door d in lot.GetObjects<Door>())
            {
                int[] rc = d.GetRoomConnections();
                if (rc == null || rc.Length < 2) continue;
                int u = -1, p = -1;
                foreach (int r in rc)
                {
                    if (LotManager.RoomIdIsOutside(r) || lot.IsRoomHidden(r)) continue;
                    if (lot.IsRoomPublic(r)) p = r; else u = r;
                }
                if (u >= 0 && p >= 0) list.Add(new AlDoor { Door = d, U = u, P = p });
            }
            return list;
        }

        static string AlSimLine(Sim s)
        {
            Lot lot = s.LotCurrent;
            VisitSituation v = VisitSituation.FindVisitSituationInvolvingGuest(s);
            HostedSituation g = HostedSituation.GetHostedSituationIAmGuestAtCurrently(s), left = HostedSituation.GetHostedSituationIAmLeaving(s);
            InteractionInstance cur = s.InteractionQueue != null ? s.InteractionQueue.GetCurrentInteraction() : null;
            return s.FullName + " " + (IsNpcSim(s) ? "npc" : "household") + " " + (lot != null ? AlRoom(lot, s.RoomId) : "no lot") + " L" + s.Level
                + " visit=" + (v == null ? "none" : (v.mbSimAnnouncedLeaving ? "farewell-shown" : "no-farewell") + (v.Reason != VisitSituation.GuestLeaveReason.None ? " reason " + v.Reason : ""))
                + " greeted=" + s.IsOnAGreetedLot
                + " party=" + (g != null ? "guest" : left != null ? "left" + (left.SimsWhoLeftEarly.Contains(s) ? "-early" : "") : "no")
                + " doing=" + (cur != null ? AlName(cur) : "nothing");
        }

        static List<HostedSituation> AlParties(Lot lot)
        {
            List<HostedSituation> list = new List<HostedSituation>();
            foreach (Situation x in Situation.sAllSituations) { HostedSituation h = x as HostedSituation; if (h != null && h.Lot == lot) list.Add(h); }
            return list;
        }

        static string AlEventLine(AlEvent e)
        {
            return "  " + e.Sim + " r" + e.From + "->r" + e.To + " on " + e.Lot + ": " + (e.Skipped ? "SKIPPED" : "EA farewell ran") + " (" + (e.On ? "on" : "off") + ", " + e.Why + ")"
                + " farewell-flag " + e.AnnBefore + "->" + e.AnnAfter + " party-guest " + e.GuestBefore + "->" + e.GuestAfter + " @" + SimClock.ConvertFromTicks(e.Ticks, TimeUnit.Minutes).ToString("0") + "min";
        }

        static string AlCounters()
        {
            int[] c = PatchCounters();
            return "aptleave " + (ShortcutOn("aptleave") ? "on" : "off") + ", counters 2391-2394 (skipped/run-on/seen/errors): " + c[2391] + " " + c[2392] + " " + c[2393] + " " + c[2394];
        }

        // ---- list ----

        static object AlList(Lot lot)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("lot ").Append(lot.Name).Append(" (").Append(lot.LotId.ToString("X")).Append(") apartment=").Append(lot.HasVirtualResidentialSlots)
              .Append(" household=").Append(lot.Household != null ? lot.Household.Name : "none").Append('\n');
            SortedDictionary<int, string> rooms = new SortedDictionary<int, string>();
            foreach (Door d in lot.GetObjects<Door>())
            {
                int[] rc = d.GetRoomConnections();
                if (rc == null) continue;
                foreach (int r in rc) if (!LotManager.RoomIdIsOutside(r) && !rooms.ContainsKey(r)) rooms[r] = "L" + d.Level + AlTag(lot, r);
            }
            int nu = 0, np = 0, nh = 0;
            sb.Append("rooms (with a door):");
            foreach (KeyValuePair<int, string> kv in rooms)
            {
                sb.Append(" r").Append(kv.Key).Append('=').Append(kv.Value);
                if (kv.Value.EndsWith("U")) nu++; else if (kv.Value.EndsWith("H")) nh++; else np++;
            }
            sb.Append("\n  ").Append(np).Append(" public, ").Append(nu).Append(" unmarked (household apartment), ").Append(nh).Append(" hidden\n");
            sb.Append("U|P doors (EA's room-change farewell fires walking U->P):");
            foreach (AlDoor d in AlDoors(lot)) sb.Append(" r").Append(d.U).Append("|r").Append(d.P).Append(" L").Append(d.Door.Level);
            sb.Append('\n');
            foreach (HostedSituation h in AlParties(lot))
                sb.Append("party ").Append(h.GetType().Name).Append(" host ").Append(h.Host != null ? h.Host.FullName : "?").Append(" started=").Append(h.Started)
                  .Append(" guests ").Append(h.Guests.Count).Append(" left ").Append(h.SimsWhoLeftParty.Count).Append(" left-early ").Append(h.SimsWhoLeftEarly.Count).Append('\n');
            foreach (Sim s in lot.GetSims()) if (s != null && !s.HasBeenDestroyed && s.SimDescription != null) sb.Append("  ").Append(AlSimLine(s)).Append('\n');
            sb.Append("last room-change farewell decisions:\n");
            foreach (AlEvent e in sAlEvents) sb.Append(AlEventLine(e)).Append('\n');
            sb.Append(AlCounters());
            return sb.ToString();
        }

        // ---- visit / party setup ----

        static Sim AlPickNpc(Lot lot)
        {
            Sim npc = null;
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null || !s.InWorld || s.Parent != null || s.SimInRabbitHolePosture) continue;
                if (!IsNpcSim(s) || !s.IsHuman || !s.SimDescription.YoungAdultOrAbove || s.Service != null || s.SimDescription.AssignedRole != null) continue;
                if (s.LotHome == lot || s.SimDescription.VirtualLotHome == lot) continue;
                if (npc == null || (s.LotCurrent == lot && npc.LotCurrent != lot)) npc = s;
            }
            return npc;
        }

        static Vector3 AlSpotIn(Sim s, Vector3 near, int room)
        {
            World.FindGoodLocationParams fgl = new World.FindGoodLocationParams(near);
            fgl.RequiredRoomID = room;
            fgl.BooleanConstraints = FindGoodLocationBooleans.PreferEmptyTiles | FindGoodLocationBooleans.Routable;
            Vector3 pos, fwd;
            return GlobalFunctions.FindGoodLocation(s, fgl, out pos, out fwd) ? pos : Vector3.Invalid;
        }

        static object AlSetup(Lot lot, bool party, bool make)
        {
            if (!lot.HasVirtualResidentialSlots) return "setup: N/A (" + lot.Name + " has no virtual residential slots: not an apartment lot)";
            if (Household.ActiveHousehold == null || lot != Household.ActiveHousehold.LotHome) return "setup: N/A (EA's farewell only runs on the active household's home lot; " + lot.Name + " is not it)";
            List<AlDoor> doors = AlDoors(lot);
            if (doors.Count == 0) return "setup: N/A (no unlocked door between an unmarked and a public room on " + lot.Name + ")";
            Sim npc = AlPickNpc(lot);
            if (npc == null) return "setup: no adult human NPC visitor candidate in the world";
            Vector3 u = Vector3.Invalid, p = Vector3.Invalid;
            AlDoor door = doors[0];
            foreach (AlDoor d in doors)
            {
                if (d.Door.IsLocked || d.Door.IsAllowedThrough(d.U, d.P, npc) != PortalRestriction.None) continue;
                u = AlSpotIn(npc, d.Door.Position, d.U);
                p = AlSpotIn(npc, d.Door.Position, d.P);
                if (u != Vector3.Invalid && p != Vector3.Invalid) { door = d; break; }
            }
            if (u == Vector3.Invalid || p == Vector3.Invalid) return "setup: N/A (no U|P door with a spot on both sides the NPC may pass)";

            HostedSituation hs = null;
            if (party)
            {
                foreach (HostedSituation h in AlParties(lot)) if (hs == null || h.Started) hs = h;
                if (hs == null && make)
                {
                    Sim host = PlumbBob.SelectedActor;
                    if (host == null || host.LotHome != lot) return "setup: party make needs a selected Sim living on " + lot.Name;
                    hs = new HouseParty(lot, host, new List<SimDescription>(), Sims3.SimIFace.CAS.OutfitCategories.Everyday, SimClock.CurrentTime(), false);
                    sAlMadeParty = hs;
                }
                if (hs == null) return "setup: N/A (no party on " + lot.Name + "; throw one, or run 'party make')";
            }

            // place in the U room (like HiddenRoomCheck trap)
            npc.InteractionQueue.CancelAllInteractions();
            if (npc.Posture != npc.Standing) npc.Posture = npc.Standing;
            npc.SetPosition(u);
            npc.RemoveFromWorld();
            npc.AddToWorld();
            npc.SimRoutingComponent.ForceUpdateDynamicFootprint();
            npc.GreetSimOnLot(lot);
            VisitSituation v = VisitSituation.FindVisitSituationInvolvingGuest(npc) ?? VisitSituation.Create(npc, lot);
            if (v != null) v.mbSimAnnouncedLeaving = false;
            if (hs != null)
            {
                hs.SimsWhoLeftParty.Remove(npc);
                hs.SimsWhoLeftEarly.Remove(npc);
                if (!hs.Guests.Contains(npc)) { hs.Guests.Add(npc); npc.AssignRole(hs); }
            }
            // EA's GoHere to the P room: target lot = this lot
            Terrain.GoHere go = Terrain.GoHere.SameLotSingleton.CreateInstance(Terrain.Singleton, npc, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true) as Terrain.GoHere;
            if (go == null) return "setup: could not create GoHere";
            go.SetDestination(p, Terrain.GoHere.DestinationType.OnTerrain);
            bool queued = npc.InteractionQueue.Add(go);
            sAlTestSim = npc; sAlTestU = door.U; sAlTestP = door.P; sAlTestAt = SimClock.CurrentTicks;
            return "setup: " + npc.FullName + " placed in " + AlRoom(lot, npc.RoomId) + " (wanted r" + door.U + ") on " + lot.Name + ", visit=" + (v != null ? "yes" : "NONE")
                + (hs != null ? ", guest of " + hs.GetType().Name + (hs == sAlMadeParty ? " (made)" : "") : "") + "; GoHere to r" + door.P + " queued=" + queued
                + " @" + Math.Round(p.x, 1) + "," + Math.Round(p.z, 1) + "\n  " + AlSimLine(npc) + "\n" + AlCounters();
        }

        // ---- status / home / unparty ----

        static object AlStatus()
        {
            Sim s = sAlTestSim;
            if (s == null || s.HasBeenDestroyed || s.SimDescription == null) return "status: no test Sim (run visit or party first)";
            StringBuilder sb = new StringBuilder();
            sb.Append("status: ").Append(AlSimLine(s)).Append('\n');
            ulong id = s.SimDescription.SimDescriptionId;
            int mine = 0;
            AlEvent last = new AlEvent();
            foreach (AlEvent e in sAlEvents)
            {
                if (e.Id != id || e.Ticks < sAlTestAt) continue;
                mine++;
                last = e;
                sb.Append(AlEventLine(e)).Append('\n');
            }
            Lot lot = s.LotCurrent;
            bool inP = lot != null && s.RoomId == sAlTestP;
            bool ann = AlAnnounced(s), guest = AlGuest(s);
            HostedSituation left = HostedSituation.GetHostedSituationIAmLeaving(s);
            bool wasGuest = guest || left != null;
            string verdict;
            if (mine == 0) verdict = inP ? "WAIT (in r" + sAlTestP + " but no room-change farewell decision recorded: hook missing? check fix_applied for Sim.RoomChanged / VisitSituation.OnSimLeavingLot)" : "WAIT (not in r" + sAlTestP + " yet)";
            else if (!last.Skipped && (ann || (wasGuest && !guest))) verdict = "BROKEN (EA's farewell ran on a hallway walk: farewell flag " + ann + (wasGuest ? ", party guest " + guest : "") + ")";
            else if (last.Skipped && !ann && (!wasGuest || guest)) verdict = "FIXED (farewell skipped, visit and party guest kept)";
            else verdict = "OTHER (see the decision lines)";
            sb.Append("verdict: ").Append(verdict).Append('\n').Append(AlCounters());
            return sb.ToString();
        }

        static object AlHome()
        {
            Sim s = sAlTestSim;
            if (s == null || s.HasBeenDestroyed) return "home: no test Sim";
            Sim.MakeSimGoHome(s, false);
            return "home: MakeSimGoHome queued for " + s.FullName + "; 'status' should show visit=farewell-shown (EA's GoHome call) in both arms\n  " + AlSimLine(s);
        }

        static object AlUnparty()
        {
            HostedSituation h = sAlMadeParty;
            sAlMadeParty = null;
            if (h == null) return "unparty: no party made by this check";
            h.Exit();
            return "unparty: exited " + h.GetType().Name;
        }
    }
}
