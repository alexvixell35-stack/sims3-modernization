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
    //   "unparty": exits the HouseParty "party make" or "hhguest" created.
    //   "hhguest": household case. An NPC who lives in another apartment building hosts a HouseParty there (made by the
    //     check, no invitations); the selected Sim becomes its guest, is placed in an unmarked room there and walks into the
    //     public room behind a U|P door. "status" then: off BROKEN (removed as left early), on FIXED (still a guest).
    //   "hhexit": that Sim leaves the lot for real (EA's VisitLot to its home lot): on, the deferred EA removal runs at the
    //     lot change ("status": party=left-early, counter 2395 +1); off, it was already removed in the hallway.
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
                if (mode == "hhguest") return AlHouseholdGuest();
                if (mode == "hhexit") return AlHouseholdExit();
                object real = AlPartyMode(mode, w);  // throw / nudge / partyreport / partyend (AptLeavePartyCheck.cs)
                if (real != null) return real;
                Lot lot = AlLot(w.Length > 1 && w[1] != "make" ? w[1] : "active");
                if (lot == null) return "apartment leave check: no lot " + (w.Length > 1 ? w[1] : "active");
                if (mode == "list") return AlList(lot);
                if (mode == "visit") return AlSetup(lot, false, false);
                if (mode == "party") return AlSetup(lot, true, Array.IndexOf(w, "make") > 0);
                return "apartment leave check: list|visit|party [make] [hex lot id|active] | status|home|unparty|hhguest|hhexit | throw [n] [now]|nudge|partyreport|partyend";
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
            return "aptleave " + (ShortcutOn("aptleave") ? "on" : "off") + ", counters 2391-2397 (skipped/run-on/seen/errors/deferred-applied/deferred-dropped/household-skipped): "
                + c[2391] + " " + c[2392] + " " + c[2393] + " " + c[2394] + " " + c[2395] + " " + c[2396] + " " + c[2397];
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

        // A U|P door `s` may pass with a free spot on both sides; false when none.
        static bool AlPickDoor(Lot lot, Sim s, out AlDoor door, out Vector3 u, out Vector3 p)
        {
            door = new AlDoor(); u = Vector3.Invalid; p = Vector3.Invalid;
            foreach (AlDoor d in AlDoors(lot))
            {
                if (d.Door.IsLocked || d.Door.IsAllowedThrough(d.U, d.P, s) != PortalRestriction.None) continue;
                u = AlSpotIn(s, d.Door.Position, d.U);
                p = AlSpotIn(s, d.Door.Position, d.P);
                if (u != Vector3.Invalid && p != Vector3.Invalid) { door = d; return true; }
            }
            return false;
        }

        // Test placement (like HiddenRoomCheck trap): queue cleared, standing, put at `at`.
        static void AlPlace(Sim s, Vector3 at)
        {
            s.InteractionQueue.CancelAllInteractions();
            if (s.Posture != s.Standing) s.Posture = s.Standing;
            s.SetPosition(at);
            s.RemoveFromWorld();
            s.AddToWorld();
            s.SimRoutingComponent.ForceUpdateDynamicFootprint();
        }

        // `s` becomes a fresh guest of `hs` the way HouseParty.TimeToInviteGuests adds one.
        static void AlMakeGuest(HostedSituation hs, Sim s)
        {
            hs.SimsWhoLeftParty.Remove(s);
            hs.SimsWhoLeftEarly.Remove(s);
            if (!hs.Guests.Contains(s)) { hs.Guests.Add(s); s.AssignRole(hs); }
        }

        // EA's Go Here to `p` (target lot = the lot of p), the walk that fires EA's room-change call. Records the test Sim.
        static bool AlWalk(Sim s, Vector3 p, AlDoor door)
        {
            Terrain.GoHere go = Terrain.GoHere.SameLotSingleton.CreateInstance(Terrain.Singleton, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true) as Terrain.GoHere;
            if (go == null) return false;
            go.SetDestination(p, Terrain.GoHere.DestinationType.OnTerrain);
            sAlTestSim = s; sAlTestU = door.U; sAlTestP = door.P; sAlTestAt = SimClock.CurrentTicks;
            return s.InteractionQueue.Add(go);
        }

        static object AlSetup(Lot lot, bool party, bool make)
        {
            if (!lot.HasVirtualResidentialSlots) return "setup: N/A (" + lot.Name + " has no virtual residential slots: not an apartment lot)";
            if (Household.ActiveHousehold == null || lot != Household.ActiveHousehold.LotHome) return "setup: N/A (EA's farewell only runs on the active household's home lot; " + lot.Name + " is not it)";
            Sim npc = AlPickNpc(lot);
            if (npc == null) return "setup: no adult human NPC visitor candidate in the world";
            AlDoor door; Vector3 u, p;
            if (!AlPickDoor(lot, npc, out door, out u, out p)) return "setup: N/A (no U|P door with a spot on both sides the NPC may pass on " + lot.Name + ")";

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

            AlPlace(npc, u);
            npc.GreetSimOnLot(lot);
            VisitSituation v = VisitSituation.FindVisitSituationInvolvingGuest(npc) ?? VisitSituation.Create(npc, lot);
            if (v != null) v.mbSimAnnouncedLeaving = false;
            if (hs != null) AlMakeGuest(hs, npc);
            bool queued = AlWalk(npc, p, door);
            return "setup: " + npc.FullName + " placed in " + AlRoom(lot, npc.RoomId) + " (wanted r" + door.U + ") on " + lot.Name + ", visit=" + (v != null ? "yes" : "NONE")
                + (hs != null ? ", guest of " + hs.GetType().Name + (hs == sAlMadeParty ? " (made)" : "") : "") + "; GoHere to r" + door.P + " queued=" + queued
                + " @" + Math.Round(p.x, 1) + "," + Math.Round(p.z, 1) + "\n  " + AlSimLine(npc) + "\n" + AlCounters();
        }

        // ---- household guest at an NPC's apartment party ----

        // hhguest: an NPC living in another apartment building hosts a HouseParty there (no invitations, made by the check,
        // like the agent's throw_party); the selected Sim becomes its guest, is placed in an unmarked room of that building
        // and walks into the public room behind a U|P door. off: EA removes it as left early; on: kept (2397), deferred.
        static object AlHouseholdGuest()
        {
            Sim me = PlumbBob.SelectedActor;
            if (me == null) return "hhguest: no selected Sim";
            if (sAlMadeParty != null) return "hhguest: a check party is running; run unparty first";
            Sim host = null;
            Lot lot = null;
            AlDoor door = new AlDoor(); Vector3 u = Vector3.Invalid, p = Vector3.Invalid;
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || !IsNpcSim(s) || !s.IsHuman || s.SimDescription == null || !s.SimDescription.YoungAdultOrAbove) continue;
                Lot h = s.LotHome;
                if (h == null || h == me.LotHome || !h.HasVirtualResidentialSlots) continue;
                if (AlPickDoor(h, me, out door, out u, out p)) { host = s; lot = h; break; }
            }
            if (host == null) return "hhguest: N/A (no instantiated NPC who lives in another apartment building with a U|P door the selected Sim may pass)";
            HouseParty hs = new HouseParty(lot, host, new List<SimDescription>(), Sims3.SimIFace.CAS.OutfitCategories.Everyday, SimClock.CurrentTime(), false);
            sAlMadeParty = hs;
            AlPlace(me, u);
            me.GreetSimOnLot(lot);
            AlMakeGuest(hs, me);
            bool queued = AlWalk(me, p, door);
            return "hhguest: " + me.FullName + " (household) guest of " + host.FullName + "'s HouseParty on " + lot.Name + " (" + lot.LotId.ToString("X") + "), placed in "
                + AlRoom(lot, me.RoomId) + ", GoHere to r" + door.P + " queued=" + queued + "\n  " + AlSimLine(me) + "\n" + AlCounters();
        }

        // hhexit: the test Sim leaves that lot for real (EA's VisitLot to its home lot); with aptleave on, the deferred
        // removal must run when its lot changes (2395 +1, party=left-early in status).
        static object AlHouseholdExit()
        {
            Sim s = sAlTestSim;
            if (s == null || s.HasBeenDestroyed) return "hhexit: no test Sim";
            Lot home = s.LotHome;
            if (home == null || home == s.LotCurrent) return "hhexit: N/A (no other home lot)";
            InteractionInstance go = VisitLot.Singleton.CreateInstance(home, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            bool queued = go != null && s.InteractionQueue.Add(go);
            return "hhexit: VisitLot " + home.Name + " queued=" + queued + " for " + s.FullName + "\n  " + AlSimLine(s) + "\n" + AlCounters();
        }

        // ---- status / home / unparty ----

        static object AlStatus()
        {
            Sim s = sAlTestSim;
            if (s == null || s.HasBeenDestroyed || s.SimDescription == null) return "status: no test Sim (run visit or party first)";
            StringBuilder sb = new StringBuilder();
            sb.Append("status: ").Append(AlSimLine(s)).Append('\n');
            ulong id = s.SimDescription.SimDescriptionId;
            // The verdict judges the first decision after the walk was queued (the hallway step under test), by what it
            // recorded at that moment; later decisions (a real departure, another walk) are only summarised. Reading live
            // state instead gave OTHER/BROKEN in the 2026-11-14 run where the Sim later went home (EA's own removal).
            int mine = 0;
            AlEvent first = new AlEvent();
            string later = "";
            foreach (AlEvent e in sAlEvents)
            {
                if (e.Id != id || e.Ticks < sAlTestAt) continue;
                if (mine == 0) first = e;
                else later += (later.Length > 0 ? "; " : "") + (e.Skipped ? "skipped" : "ran") + " (" + e.Why + ")";
                mine++;
                sb.Append(AlEventLine(e)).Append('\n');
            }
            Lot lot = s.LotCurrent;
            bool inP = lot != null && s.RoomId == sAlTestP;
            bool lost = (first.GuestBefore && !first.GuestAfter) || (!first.AnnBefore && first.AnnAfter);
            string verdict;
            if (mine == 0) verdict = inP ? "WAIT (in r" + sAlTestP + " but no room-change farewell decision recorded: hook missing? check fix_applied for Sim.RoomChanged / VisitSituation.OnSimLeavingLot)" : "WAIT (not in r" + sAlTestP + " yet)";
            else if (!first.Skipped && lost) verdict = "BROKEN (EA's call ran on the hallway step: farewell flag " + first.AnnBefore + "->" + first.AnnAfter + ", party guest " + first.GuestBefore + "->" + first.GuestAfter + ")";
            else if (first.Skipped && !lost) verdict = "FIXED (hallway step skipped: " + first.Why + "; farewell flag and party guest kept)";
            else if (!first.Skipped) verdict = "OTHER (EA's call ran but changed nothing: " + first.Why + ")";
            else verdict = "OTHER (skipped but state changed: see the decision lines)";
            if (later.Length > 0) verdict += "; later: " + later + (later.IndexOf("ran (") >= 0 ? " (a later 'ran' with a leaving reason = a real departure: EA's removal then is expected)" : "");
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
