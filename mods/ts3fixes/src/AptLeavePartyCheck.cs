using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // ApartmentLeaveCheck real-party modes (docs/553 §5; test only, test saves only: the household throws a real party).
    //   "throw [n] [now] [hex lot id|active]": what the phone's Throw Party does for a house party (Phone.Call.HaveParty, default branch): guests
    //     from EA's own invite list (Phone.Call.GetAllValidCalleesForParty with HouseParty.kLTRMinToInvite), the n (default
    //     6) best-related adult or teen humans of other households who do not live on the lot, then
    //     new HouseParty(lot, selected Sim, guests, Everyday, start) (invitations delivered, as the phone does) and the
    //     kThrewParty PartyEvent; at a community lot (not home) the venue is rented first as the phone does
    //     (RentScheduler.RentLot). Any lot type: home house, apartment, dorm, houseboat, community venue; the selected Sim
    //     must stand on it. start = now + 1 sim-hour (the phone's earliest), "now" = now + 2 sim-minutes. Starts the
    //     per-guest tracker (AptLeave.cs sAlTrack): arrived, apartment->public and public->apartment room changes,
    //     room-change farewells run / skipped.
    //   "nudge": each arrived guest that stands in an unmarked (U) room is sent through the hallway the way a party walk
    //     does it: EA's Go Here to the public side of the nearest U|P door (an interaction on this lot). Optional; natural
    //     play also crosses the hallway.
    //   "partyreport": per guest: arrived, on the lot now and room tag, room changes, farewells run / skipped, party state
    //     (guest / left / left-early / not here), mood; party: state, guests / left / left early, EA's running score
    //     (HouseParty mPartyMood / mNumberOfGuests, read only) and GetPartyOutcome().
    //   "partyend": the report, then every remaining guest leaves the EA way (HostedSituation.MakeGuestGoHome, not early),
    //     EA's final score and outcome over all guests, then the party is exited and the tracker stopped (cleanup).
    public static partial class Fixes
    {
        static HouseParty sAlThrown;

        static object AlPartyMode(string mode, string[] w)
        {
            if (mode == "throw") return AlThrow(w);
            if (mode == "nudge") return AlNudge();
            if (mode == "partyreport") return AlPartyReport(false);
            if (mode == "partyend") return AlPartyReport(true);
            return null;
        }

        static object AlThrow(string[] w)
        {
            if (sAlThrown != null) return "throw: a check party is running; run partyend first";
            Sim host = PlumbBob.SelectedActor;
            if (host == null || host.Household == null) return "throw: no selected Sim";
            int n = 6;
            bool now = false;
            Lot lot = host.LotHome;
            for (int i = 1; i < w.Length; i++)
            {
                int k;
                if (w[i] == "now") now = true;
                else if (w[i] == "active") lot = LotManager.ActiveLot;
                else if (w[i].Length > 4) lot = AlLot(w[i]);  // hex lot id
                else if (int.TryParse(w[i], out k)) n = Math.Max(1, Math.Min(12, k));
            }
            if (lot == null || lot.IsWorldLot) return "throw: no such lot";
            if (host.LotCurrent != lot)
            {
                // the phone's caller is any household member at the venue: take one who is there (selected Sim first)
                Sim other = null;
                foreach (Sim m in host.Household.Sims) if (m != null && !m.HasBeenDestroyed && m.LotCurrent == lot && m.SimDescription.TeenOrAbove) { other = m; break; }
                if (other == null) return "throw: N/A (no teen-or-older member of the selected household on " + lot.Name + ")";
                host = other;
            }
            bool home = lot == host.LotHome;
            if (!home && lot.IsResidentialLot) return "throw: N/A (" + lot.Name + " is someone else's residential lot: the phone throws only at home or at a community venue)";
            List<KeyValuePair<float, SimDescription>> cands = new List<KeyValuePair<float, SimDescription>>();
            foreach (var info in Phone.Call.GetAllValidCalleesForParty(host, HouseParty.kLTRMinToInvite, false))
            {
                SimDescription d = info.SimDescription as SimDescription;
                if (d == null || d.IsDead || !d.IsHuman || !d.TeenOrAbove || d.Household == null || d.Household == host.Household) continue;
                if (d.VirtualLotHome == lot || (d.Household.LotHome == lot)) continue;
                cands.Add(new KeyValuePair<float, SimDescription>(info.RelationShip, d));
            }
            cands.Sort(delegate (KeyValuePair<float, SimDescription> a, KeyValuePair<float, SimDescription> b) { return b.Key.CompareTo(a.Key); });
            List<SimDescription> guests = new List<SimDescription>();
            for (int i = 0; i < cands.Count && guests.Count < n; i++) guests.Add(cands[i].Value);
            if (guests.Count == 0) return "throw: N/A (EA's invite list has no candidate)";

            DateAndTime start = SimClock.CurrentTime() + new DateAndTime(SimClock.ConvertToTicks(now ? 2f : 60f, TimeUnit.Minutes));
            if (!home)
            {
                // the phone's off-lot path: rent the venue from (start - Party.HoursToStartRentBeforePartyStart), at least now + 2 min
                DateAndTime rent = start;
                rent.Ticks -= SimClock.ConvertToTicks(Party.HoursToStartRentBeforePartyStart, TimeUnit.Hours);
                if (rent.CompareTo(SimClock.CurrentTime()) < 0) rent = SimClock.Add(SimClock.CurrentTime(), TimeUnit.Minutes, 2f);
                if (!RentScheduler.Instance.RentLot(lot, host, rent, guests, false)) return "throw: N/A (RentScheduler refused " + lot.Name + ")";
            }
            HouseParty party = new HouseParty(lot, host, guests, Sims3.SimIFace.CAS.OutfitCategories.Everyday, start);
            EventTracker.SendEvent(new PartyEvent(EventTypeId.kThrewParty, host, host.SimDescription, party));
            sAlThrown = party;
            sAlTrackLot = lot;
            sAlTrack = new Dictionary<ulong, AlTrack>();
            foreach (SimDescription d in guests) sAlTrack[d.SimDescriptionId] = new AlTrack { Name = d.FullName };
            StringBuilder sb = new StringBuilder();
            sb.Append("throw: HouseParty on ").Append(lot.Name).Append(" (").Append(lot.LotId.ToString("X")).Append(home ? ", home" : ", rented venue").Append(", apartment=").Append(lot.HasVirtualResidentialSlots).Append(")").Append(" host ").Append(host.FullName).Append(", starts in ").Append(now ? "2 sim-min" : "1 sim-hour")
              .Append(", invited ").Append(guests.Count).Append(" of ").Append(cands.Count).Append(" candidates:");
            foreach (SimDescription d in guests) sb.Append(' ').Append(d.FullName).Append(';');
            sb.Append('\n').Append(AlCounters());
            return sb.ToString();
        }

        static object AlNudge()
        {
            HouseParty party = sAlThrown;
            if (party == null) return "nudge: no check party (run throw)";
            Lot lot = party.Lot;
            List<AlDoor> doors = AlDoors(lot);
            if (doors.Count == 0) return "nudge: N/A (no U|P door on " + lot.Name + ")";
            StringBuilder sb = new StringBuilder("nudge:");
            int sent = 0;
            foreach (Sim g in new List<Sim>(party.Guests))
            {
                if (g == null || g.HasBeenDestroyed || g.LotCurrent != lot || lot.IsRoomPublic(g.RoomId) || lot.IsRoomHidden(g.RoomId)) continue;
                AlDoor best = new AlDoor();
                float bestD = float.MaxValue;
                foreach (AlDoor d in doors)
                {
                    if (d.Door.IsLocked || d.Door.Level != g.Level) continue;
                    float dist = (d.Door.Position - g.Position).LengthSqr();
                    if (dist < bestD) { bestD = dist; best = d; }
                }
                if (best.Door == null) continue;
                Vector3 p = AlSpotIn(g, best.Door.Position, best.P);
                if (p == Vector3.Invalid) continue;
                Terrain.GoHere go = Terrain.GoHere.SameLotSingleton.CreateInstance(Terrain.Singleton, g, new Sims3.Gameplay.Interactions.InteractionPriority(Sims3.Gameplay.Interactions.InteractionPriorityLevel.UserDirected), false, true) as Terrain.GoHere;
                if (go == null) continue;
                go.SetDestination(p, Terrain.GoHere.DestinationType.OnTerrain);
                if (g.InteractionQueue.Add(go)) { sent++; sb.Append(' ').Append(g.FullName).Append(" r").Append(g.RoomId).Append("->r").Append(best.P).Append(';'); }
            }
            return sb.Append(" sent ").Append(sent).Append(" of ").Append(party.Guests.Count).Append(" guests").ToString();
        }

        static object AlPartyScore(HouseParty party, out string outcome)
        {
            outcome = "?";
            try { outcome = party.GetPartyOutcome().ToString(); } catch (Exception e) { outcome = "error " + e.GetType().Name; }
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo mood = typeof(HouseParty).GetField("mPartyMood", f), num = typeof(HouseParty).GetField("mNumberOfGuests", f);
            if (mood == null || num == null) return "score n/a";
            float m = (float)mood.GetValue(party);
            int k = (int)num.GetValue(party);
            return "mood sum " + Math.Round(m) + " over " + k + " leavers" + (k > 0 ? " = avg " + Math.Round(m / k, 1) : "");
        }

        static object AlPartyReport(bool end)
        {
            HouseParty party = sAlThrown;
            if (party == null || sAlTrack == null) return (end ? "partyend" : "partyreport") + ": no check party (run throw)";
            Lot lot = party.Lot;
            StringBuilder sb = new StringBuilder();
            sb.Append(end ? "partyend" : "partyreport").Append(": aptleave ").Append(ShortcutOn("aptleave") ? "on" : "off").Append(", party state ")
              .Append(party.Child != null ? party.Child.GetType().Name : "none").Append(" started=").Append(party.Started).Append(" guests ").Append(party.Guests.Count)
              .Append(" left ").Append(party.SimsWhoLeftParty.Count).Append(" left-early ").Append(party.SimsWhoLeftEarly.Count).Append('\n');
            int arrived = 0, early = 0, farewells = 0, skipped = 0, crossings = 0, stillPartying = 0;
            foreach (KeyValuePair<ulong, AlTrack> kv in sAlTrack)
            {
                AlTrack t = kv.Value;
                SimDescription d = SimDescription.Find(kv.Key);
                Sim s = d != null ? d.CreatedSim : null;
                bool onLot = s != null && !s.HasBeenDestroyed && s.LotCurrent == lot;
                string st = s == null ? "no Sim" : party.Guests.Contains(s) ? "guest" : party.SimsWhoLeftEarly.Contains(s) ? "left-early" : party.SimsWhoLeftParty.Contains(s) ? "left" : "not a guest";
                if (t.Arrived) arrived++;
                if (st == "left-early") early++;
                if (st == "guest" && onLot) stillPartying++;
                farewells += t.FarewellRun; skipped += t.FarewellSkipped; crossings += t.ToPublic;
                sb.Append("  ").Append(t.Name).Append(": arrived=").Append(t.Arrived).Append(t.Arrived ? " first " + t.FirstRoom : "").Append(" now ").Append(onLot ? AlRoom(lot, s.RoomId) : "off lot")
                  .Append(" apt->public ").Append(t.ToPublic).Append(" public->apt ").Append(t.ToApartment)
                  .Append(" farewell run ").Append(t.FarewellRun).Append(" skipped ").Append(t.FarewellSkipped).Append(t.LastRunWhy != null ? " (last run: " + t.LastRunWhy + ")" : "")
                  .Append(" party=").Append(st).Append(onLot && st != "guest" ? " (still here, no longer partying)" : "")
                  .Append(s != null ? " mood " + s.MoodManager.MoodValue : "").Append('\n');
            }
            string outcome;
            sb.Append("running score: ").Append(AlPartyScore(party, out outcome)).Append(", outcome so far ").Append(outcome).Append('\n');
            sb.Append("summary: invited ").Append(sAlTrack.Count).Append(" arrived ").Append(arrived).Append(" hallway crossings ").Append(crossings)
              .Append(" farewells run ").Append(farewells).Append(" skipped ").Append(skipped).Append(" left-early ").Append(early)
              .Append(" (EA penalty ").Append(early * HostedSituation.kPenaltyForEarlyDeparture).Append(" mood) still partying ").Append(stillPartying).Append('\n');
            if (end)
            {
                int sent = 0;
                foreach (Sim g in new List<Sim>(party.Guests)) { if (g != null && !g.HasBeenDestroyed) { party.MakeGuestGoHome(g); sent++; } }
                sb.Append("final (after ").Append(sent).Append(" remaining guests left the EA way): ").Append(AlPartyScore(party, out outcome)).Append(", outcome ").Append(outcome).Append('\n');
                try { party.Exit(); sb.Append("party exited, tracker stopped\n"); } catch (Exception e) { sb.Append("party exit failed: ").Append(e.GetType().Name).Append('\n'); }
                sAlThrown = null;
                sAlTrack = null;
                sAlTrackLot = null;
            }
            sb.Append(AlCounters());
            return sb.ToString();
        }
    }
}
