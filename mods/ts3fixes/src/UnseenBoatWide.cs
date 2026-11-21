using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/248 §12 unseenboatwide (default off, needs unseenboat): relaxes some unseenboat refusals so fewer unseen NPC trips across the water
    // take a boat. Only refusals change; every view test, the household / selected / unknown-scope rules and the water-purpose rules stay:
    //   household lot: an NPC visiting the active household's lot (not its own home) is placed at that lot's street side, never a houseboat,
    //     only when no household Sim is near (FarFromViewers);
    //   indoors or raised goal (not water), or a private lot with no spot verdict: placed at the goal lot's street side (front door's outdoor
    //     slot, else the mailbox slot), dry, outdoor ground, routable, unseen; EA plans the last metres on land;
    //   group leader or a trip with followers: the whole party moves (ring around the leader); every member human, child or older, not held,
    //     not household / selected / unknown scope, unseen; a child only with a parent in the party; animals stay with EA / petlonghome;
    //   unknown colours: probes around both ends and their lots' entry points; a crossing only when the answers agree;
    //   owned boat: left where it is when it is only the way across (not chosen by the interaction, not reserved, not occupied).
    // Kept exact (docs/248 §12): routes that end on the water (EndAsBoat / DestinationMustBeInDeepWater are only set by water-purpose callers
    // [verified, decompiled]), start swimming, callers that do not follow the route, houseboat goals and starts. Counters [4181-4195].
    public static partial class Fixes
    {
        static int sUbwMinute = -1, sUbwLogged;
        static void UbwLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sUbwMinute) { sUbwMinute = minute; sUbwLogged = 0; }
            if (sUbwLogged++ < 10) DiagLine("unseenboatwide: " + s);
        }

        public partial class RouteBackoffHook
        {
            // UfPreSim(sim, false) with the party rules: null = the Sim and its party may go; party = the members to move with it (may be empty)
            static string UbwPreSim(Sim sim, ref int rules, out List<Sim> party)
            {
                party = null;
                SimDescription d = sim.SimDescription;
                if (d == null || d.IsPet || !sim.IsHuman || UfHeld(sim) || !d.ChildOrAbove) return "Sim " + UfPreSimWhy(sim, false);
                Sims3.Gameplay.Interactions.InteractionInstance cur = sim.CurrentInteraction;
                if (cur == null || cur is Sims3.Gameplay.Socializing.SocialInteraction || UfPreCore.Denied(cur.GetType().FullName)) return "Sim " + UfPreSimWhy(sim, false);
                List<Sim> members = new List<Sim>();
                Sims3.Gameplay.Situations.GroupingSituation g = sim.GetSituationOfType<Sims3.Gameplay.Situations.GroupingSituation>();
                if (g != null)
                {
                    if (!UbWideCore.GroupOk(g.Leader == sim)) return "Sim group member";
                    foreach (Sim m in g.Participants) if (m != sim && !members.Contains(m)) members.Add(m);
                    if (members.Count > 0) rules |= UbWideCore.kGroup;
                }
                List<Sim> f;
                if (!UbwFollowers(cur, out f)) return "followers unknown";
                if (f != null && f.Count > 0)
                {
                    rules |= UbWideCore.kFollowers;
                    foreach (Sim m in f) if (m != sim && !members.Contains(m)) members.Add(m);
                }
                bool child = !d.TeenOrAbove;
                if (!UbWideCore.ChildOk(d.ChildOrAbove, child, UbwParentIn(sim, null, members))) return "Sim child alone";
                if (child) rules |= UbWideCore.kChild;
                List<UbWideCore.Member> list = new List<UbWideCore.Member>(members.Count);
                foreach (Sim m in members)
                {
                    UbWideCore.Member e = new UbWideCore.Member();
                    SimDescription md = m != null && !m.HasBeenDestroyed ? m.SimDescription : null;
                    if (md == null) { e.Held = true; list.Add(e); continue; }
                    e.Pet = md.IsPet; e.Human = m.IsHuman; e.ChildOrAbove = md.ChildOrAbove; e.Child = !md.TeenOrAbove;
                    e.Held = UfHeld(m);
                    e.Exempt = IsPlayerSim(m) || HouseholdExempt(m);
                    e.NoHousehold = m.Household == null;
                    e.Seen = !e.Exempt && (UfWhy(m) != 0 || !TownieUnseen(m, null));
                    e.ParentInParty = e.Child && UbwParentIn(m, sim, members);
                    if (e.Child) rules |= UbWideCore.kChild;
                    list.Add(e);
                }
                string why = UbWideCore.PartyWhy(list);
                if (why != null) { PatchCounters()[UbWideCore.kSlotPartyRefused]++; return why; }
                party = members;
                return null;
            }

            // a teen-or-older parent or step-parent of s among leader + members
            static bool UbwParentIn(Sim s, Sim leader, List<Sim> members)
            {
                Sims3.Gameplay.Socializing.Genealogy gs = s.SimDescription != null ? s.SimDescription.Genealogy : null;
                if (gs == null) return false;
                for (int i = -1; i < members.Count; i++)
                {
                    Sim p = i < 0 ? leader : members[i];
                    if (p == null || p == s || p.HasBeenDestroyed || p.SimDescription == null || !p.SimDescription.TeenOrAbove || p.SimDescription.Genealogy == null) continue;
                    if (gs.IsParentOrStepParent(p.SimDescription.Genealogy)) return true;
                }
                return false;
            }

            // the trip's followers as UbAlone reads them; false = the list cannot be read (VisitLot field gone): exact
            static bool UbwFollowers(Sims3.Gameplay.Interactions.InteractionInstance cur, out List<Sim> f)
            {
                f = null;
                VisitCommunityLot visit = cur as VisitCommunityLot;
                if (visit != null) { f = sUbwVclFollowers != null ? sUbwVclFollowers.GetValue(visit) as List<Sim> : null; return sUbwVclFollowers != null || visit.NumFollowers == 0; }
                GoHome home = cur as GoHome;
                if (home != null) { f = home.SimFollowers; return true; }
                GoToLot lot = cur as GoToLot;
                if (lot != null) { f = lot.Followers; return true; }
                VisitLot visit2 = cur as VisitLot;
                if (visit2 != null) { f = sUbVisitLotFollowers != null ? sUbVisitLotFollowers.GetValue(visit2) as List<Sim> : null; return sUbVisitLotFollowers != null; }
                Sims3.Gameplay.Abstracts.RabbitHole.IRabbitHoleFollowers hole = cur as Sims3.Gameplay.Abstracts.RabbitHole.IRabbitHoleFollowers;
                if (hole != null) f = hole.SimFollowers;
                return true;
            }
            static readonly System.Reflection.FieldInfo sUbwVclFollowers = typeof(VisitCommunityLot).GetField("mFollowers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            // owned boat that may stay parked (UbWideCore.OwnedMayStay)
            static bool UbwOwnedMayStay(Sim sim)
            {
                bool temporary = false;
                Boat b = sim.GetOwnedAndUsableBoat(sim.LotCurrent, false, null, ref temporary);
                Sim.ISelectVehicleInteraction sel = sim.CurrentInteraction as Sim.ISelectVehicleInteraction;
                return UbWideCore.OwnedMayStay(temporary, sel != null && sel.SelectedVehicle != null, b != null && b == sim.GetReservedBoat(), b != null && b.HasAnyOccupants());
            }

            static bool UbwWater(Vector3 p)
            {
                return (World.GetTerrainType(p) & (TerrainType.WorldSea | TerrainType.WorldPond | TerrainType.LotPool | TerrainType.LotPond)) != 0;
            }
            // a placement spot: dry, outdoors (room 0) on the ground, routable for the Sim, out of view
            bool UbwSpotOk(Vector3 e)
            {
                return e != Vector3.Invalid && !UbwWater(e) && UfOutdoorGround(e) && IsPointRoutable(e) && UfGoalUnseen(e);
            }
            // the goal lot's street side: the front door's outdoor routing slot (Lot.EntryPoint), else the mailbox's routing slot [verified, decompiled]
            bool UbwStreetSide(Lot goal, ref Vector3 place)
            {
                if (goal == null || goal.IsWorldLot) return false;
                Vector3 e = goal.EntryPoint();
                if (UbwSpotOk(e)) { place = e; return true; }
                Mailbox mb = goal.FindMailbox();
                if (mb == null) return false;
                e = mb.GetPositionOfSlot(mb.GetAnyRoutingSlot());
                if (!UbwSpotOk(e)) return false;
                place = e;
                return true;
            }
            // UfRelaxedLotWhy without the household-lot line (the caller checked HhLotOk): apartment, shell, houseboat, resort, dive lot, lock
            static string UbwLotWhy(Lot lot)
            {
                if (lot.IsApartmentLot) return "goal:apartment";
                if (lot.BuildableShells.Length > 0) return "goal:shell";
                if (lot.IsHouseboatLot()) return "goal:houseboat";
                if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving) return "goal:resort/diving";
                return UfLotLocked(lot) ? "goal:locked door" : null;
            }

            // rj_unknown fallback: colour probes at the end points and their lots' entry points (ring of 4 at 8 m each). The start's colour is the
            // first known probe, and no other start probe may be another colour; the goal's answers must agree (UbWideCore.Combine)
            static int UbwColours(Sim sim, Vector3 start, Vector3 dest, Lot goal)
            {
                List<Vector3> sp = UbwProbes(start, sim.LotCurrent), gp = UbwProbes(dest, goal);
                int anchor = -1;
                for (int i = 0; i < sp.Count && anchor < 0; i++) if (UfColourAsk(11, sp[i])) anchor = i;
                if (anchor < 0) return 0;
                for (int i = 0; i < sp.Count; i++) if (i != anchor && UfColourAsk(13, sp[i])) return 0;
                List<int> ans = new List<int>(gp.Count);
                foreach (Vector3 q in gp) ans.Add(UfColourAsk(12, q) ? 1 : UfColourAsk(13, q) ? -1 : 0);
                return UbWideCore.Combine(ans);
            }
            static List<Vector3> UbwProbes(Vector3 p, Lot lot)
            {
                List<Vector3> o = new List<Vector3>(10);
                UbwRing(o, p);
                if (lot != null && !lot.IsWorldLot) UbwRing(o, lot.EntryPoint());
                return o;
            }
            static void UbwRing(List<Vector3> o, Vector3 c)
            {
                if (c == Vector3.Invalid) return;
                float[] r = UbWideCore.Ring(UbWideCore.kProbeRingM);
                for (int i = 0; i < r.Length; i += 2)
                {
                    Vector3 q = new Vector3(c.x + r[i], c.y, c.z + r[i + 1]);
                    q.y = World.GetTerrainHeight(q.x, q.z);
                    o.Add(q);
                }
            }

            // after the wait: every member still movable (UbwPreSim's member rules); false = the trip goes exact
            static bool UbwPartyStill(List<Sim> party)
            {
                if (party == null) return true;
                foreach (Sim m in party)
                    if (m == null || m.HasBeenDestroyed || m.SimDescription == null || UfHeld(m) || IsPlayerSim(m) || HouseholdExempt(m) || m.Household == null || UfWhy(m) != 0 || !TownieUnseen(m, null)) return false;
                return true;
            }
            // the party around the leader's spot (routable outdoor dry points only); a member with no point stays and follows exactly
            void UbwPlaceParty(List<Sim> party, Vector3 place, Vector3 fwd, int[] c)
            {
                if (party == null) return;
                int i = 0;
                foreach (Sim m in party)
                {
                    float dx, dz;
                    UbWideCore.MemberOffset(++i, out dx, out dz);
                    Vector3 q = new Vector3(place.x + dx, place.y, place.z + dz);
                    q.y = World.GetTerrainHeight(q.x, q.z);
                    if (!UbwWater(q) && UfOutdoorGround(q) && IsPointRoutable(q))
                    {
                        PlaceSim(m, q, fwd, "unseenboatwide party");
                        if (sUrPlaced.Count >= 256) sUrPlaced.Clear();
                        sUrPlaced[m.ObjectId.Value] = true;
                        c[UbWideCore.kSlotMembersPlaced]++;
                    }
                    else c[UbWideCore.kSlotMemberLeft]++;
                }
            }
        }
    }
}
