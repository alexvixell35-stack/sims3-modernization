using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // aptleave (docs/553, off by default): Late Night apartment visitors say goodbye and drop out of the party while they
    // are still visiting.
    //
    // EA [verified, decompiled Sim.RoomChanged]: on a lot with virtual residential slots (an apartment building: a residential
    // lot with PublicRoomMarkers), a Sim that changes room on the same lot from a room that is not public (an unmarked room,
    // which is the household's apartment, or a hidden room) into a public one (marked hallway / lobby, or outside) gets
    // VisitSituation.OnSimLeavingLot, unless it works on that lot. OnSimLeavingLot is EA's "this guest is leaving" step,
    // which GoHome, GoOutside and the rabbit-hole commutes call when the Sim really leaves. It removes the Sim from the
    // hosted situation (party) it is a guest at, counted as left early (HostedSituation.RemoveGuest(bLeftEarly: true):
    // liking change with the host, the -50 early-departure mood penalty in HouseParty's score, the guest no longer counts
    // as at the party), and for a greeted visitor on the active household's lot it shows the farewell ("NPCLeaving" or
    // the party's LeavingMessage) once and sets VisitSituation.mbSimAnnouncedLeaving.
    // The room-change call does not check that the Sim is leaving. A guest who only walks through the hallway (to
    // another room of the apartment, to a lobby object, while a social or party activity routes it) says goodbye and is
    // out of the party, yet stays: its visit goes on (EA's own visit code tolerates a guest in a public room for 30
    // sim-minutes before sending it home), it is never re-added to the party, and when it really leaves later no farewell
    // shows (the flag is already set) [verified]. EA's intent, inferred from the job exemption and from every other caller:
    // say goodbye when the visitor leaves the apartment for good.
    //
    // Fix: the room-change call is skipped while the Sim is not leaving the lot.
    //   NPCs (PlayerSims.cs IsNpcSim): skipped when it has a current interaction whose target lot is this lot and that is
    //     not GoHome / GoOutside, it does not live on this lot (residents of other units keep EA's call), its visit has no
    //     leave reason, and no GoHome / GoOutside is queued. With no current interaction, or one that targets another lot,
    //     EA's call runs. A Sim that later leaves gets EA's farewell from GoHome / GoOutside (not room-change calls, never
    //     touched).
    //   Household Sims (and unknown scope): for them EA's call only does the party part (RemoveGuest; the farewell part
    //     returns for selectable Sims). Skipped unless an interaction in the queue (running or queued) is GoHome / GoOutside
    //     or targets another lot, or the Sim lives on this lot (then EA's call runs at once, as before).
    //   Every skip of a party guest is deferred, not dropped: when that Sim's lot really changes away from the party lot
    //     (Sim.RoomChanged with a new lot) and it is still a guest of that party, EA's own RemoveGuest(sim, left early)
    //     runs then (on a script task, since RemoveGuest can yield). Back in an unmarked room of the same lot = it never
    //     left: the deferral is dropped. So a household Sim gets EA's exact effect at the moment it really leaves, and none
    //     while it stays.
    // Hooks: Sim.RoomChanged (marks the room-change context, then runs EA's body unchanged; applies deferrals afterwards)
    // and VisitSituation.OnSimLeavingLot (decides only inside that context).
    // Counters: [2391] room-change calls skipped (Sim not leaving), [2392] room-change calls run with the switch on (leaving),
    // [2393] room-change calls seen (any switch state), [2394] errors, [2395] deferred party removals applied at the real
    // lot exit, [2396] deferrals dropped (back in the apartment), [2397] household room-change calls skipped (of 2391).
    // Harness check: ApartmentLeaveCheck (AptLeaveChecks.cs).
    public static partial class Fixes
    {
        static void ApplyAptLeave()
        {
            Patch(typeof(Sim), "RoomChanged", new Type[] { typeof(int), typeof(int), typeof(Lot) }, typeof(AlRoomHook), "RoomHook", "RoomOrig");
            Patch(typeof(VisitSituation), "OnSimLeavingLot", typeof(AlLeaveHook), "LeaveHook", "LeaveOrig");
        }

        // Sims whose Sim.RoomChanged is running, with the room pair (sim thread only). Keyed per Sim, not one static: EA's
        // RoomChanged can yield (AutoLightsOn reaches StateMachineClient.RequestState, docs/data/swap-stack-audit.csv), so
        // other Sims' room changes interleave with it. An entry left by a task that died mid-yield is overwritten or removed
        // by that Sim's next room change, and AlIsRoomCall rejects it unless the Sim stands in the room it names.
        struct AlRoomCtx { public int From, To; }
        static readonly Dictionary<ulong, AlRoomCtx> sAlRooms = new Dictionary<ulong, AlRoomCtx>();

        // Last room-change farewell decisions, newest last (read by ApartmentLeaveCheck).
        internal struct AlEvent { public string Sim; public ulong Id; public string Lot; public int From, To; public bool On, Skipped, AnnBefore, AnnAfter, GuestBefore, GuestAfter; public string Why; public long Ticks; }
        internal static readonly List<AlEvent> sAlEvents = new List<AlEvent>();

        // Is `s` leaving the apartment (EA's call should run)? why: the reason, for the log and the check.
        internal static bool AlLeaving(Sim s, out string why)
        {
            Lot lot = s.LotCurrent;
            if (lot == null) { why = "no lot"; return true; }
            if (s.LotHome == lot || s.SimDescription.VirtualLotHome == lot) { why = "lives on this lot"; return true; }
            VisitSituation v = VisitSituation.FindVisitSituationInvolvingGuest(s);
            if (v != null && v.Reason != VisitSituation.GuestLeaveReason.None) { why = "visit leave reason " + v.Reason; return true; }
            InteractionInstance cur = s.InteractionQueue != null ? s.InteractionQueue.GetCurrentInteraction() : null;
            if (cur == null) { why = "no interaction"; return true; }
            if (cur is GoHome || cur is GoOutside) { why = cur.GetType().Name; return true; }
            foreach (InteractionInstance q in s.InteractionQueue.InteractionList)
                if (q is GoHome || q is GoOutside) { why = q.GetType().Name + " queued"; return true; }
            Lot t = cur.GetTargetLot();
            if (t != lot) { why = AlName(cur) + " to " + (t != null ? t.Name : "no lot"); return true; }
            why = AlName(cur) + " on this lot";
            return false;
        }

        // Household Sims: decisive test on the whole queue. Leaving = lives on this lot, or any running/queued interaction is
        // GoHome / GoOutside or targets another lot (or no lot). Not leaving otherwise (also with an empty queue: the
        // deferral gives EA's effect if it later leaves).
        internal static bool AlLeavingPlayer(Sim s, out string why)
        {
            Lot lot = s.LotCurrent;
            if (lot == null) { why = "no lot"; return true; }
            if (s.LotHome == lot || s.SimDescription.VirtualLotHome == lot) { why = "lives on this lot"; return true; }
            if (s.InteractionQueue != null)
                foreach (InteractionInstance q in s.InteractionQueue.InteractionList)
                {
                    if (q is GoHome || q is GoOutside) { why = "household, " + q.GetType().Name + " queued"; return true; }
                    Lot t = q.GetTargetLot();
                    if (t != lot) { why = "household, " + AlName(q) + " to " + (t != null ? t.Name : "no lot"); return true; }
                }
            why = "household, nothing off this lot queued";
            return false;
        }

        // ---- deferred party removal ----

        struct AlPending { public HostedSituation Party; public Lot Lot; }
        static readonly Dictionary<ulong, AlPending> sAlPending = new Dictionary<ulong, AlPending>();  // by SimDescriptionId

        static void AlDefer(Sim s)
        {
            HostedSituation h = HostedSituation.GetHostedSituationIAmGuestAtCurrently(s);
            if (h == null) return;
            if (sAlPending.Count >= 256) sAlPending.Clear();  // bound (a party has far fewer guests)
            sAlPending[s.SimDescription.SimDescriptionId] = new AlPending { Party = h, Lot = s.LotCurrent };
        }

        // After EA's RoomChanged of a Sim with a deferral: left the party lot -> EA's RemoveGuest(left early), if still a
        // guest of a running party; back in a non-public room of the party lot -> dropped.
        static void AlPendingRoom(Sim s, Lot previousLot)
        {
            AlPending p;
            ulong id = s.SimDescription.SimDescriptionId;
            if (!sAlPending.TryGetValue(id, out p)) return;
            Lot lot = s.LotCurrent;
            if (lot == p.Lot)
            {
                if (!lot.IsRoomPublic(s.RoomId)) { sAlPending.Remove(id); PatchCounters()[2396]++; }
                return;
            }
            if (previousLot != p.Lot) return;
            sAlPending.Remove(id);
            HostedSituation h = p.Party;
            if (h == null || !Situation.sAllSituations.Contains(h) || !h.Guests.Contains(s)) return;
            PatchCounters()[2395]++;
            Simulator.AddObject(new LoopTask(delegate
            {
                try { if (Situation.sAllSituations.Contains(h) && h.Guests.Contains(s)) h.RemoveGuest(s, true); }
                catch (Exception) { PatchCounters()[2394]++; }
            }));
        }

        // Is this OnSimLeavingLot call EA's apartment room-change call? Inside the Sim's RoomChanged, same lot, from a room
        // that is not public into the public room it stands in now (EA's own condition, re-checked).
        static bool AlIsRoomCall(Sim s, out AlRoomCtx ctx)
        {
            ctx = new AlRoomCtx();
            if (s == null || !sAlRooms.TryGetValue(s.ObjectId.Value, out ctx)) return false;
            Lot lot = s.LotCurrent;
            return lot != null && lot.HasVirtualResidentialSlots && s.RoomId == ctx.To && !lot.IsRoomPublic(ctx.From) && lot.IsRoomPublic(ctx.To);
        }

        internal static string AlName(InteractionInstance i)
        {
            Type t = i.GetType();
            return t.DeclaringType != null ? t.DeclaringType.Name + "+" + t.Name : t.Name;
        }

        // farewell already shown for this visit / guest at a hosted situation (party) on its lot
        internal static bool AlAnnounced(Sim s)
        {
            VisitSituation v = VisitSituation.FindVisitSituationInvolvingGuest(s);
            return v != null && v.mbSimAnnouncedLeaving;
        }
        internal static bool AlGuest(Sim s) { return HostedSituation.GetHostedSituationIAmGuestAtCurrently(s) != null; }

        static void AlRecord(Sim s, AlRoomCtx ctx, bool on, bool skipped, bool annBefore, bool guestBefore, string why)
        {
            AlEvent e = new AlEvent { Sim = s.FullName, Id = s.SimDescription.SimDescriptionId, Lot = s.LotCurrent != null ? s.LotCurrent.Name : "?", From = ctx.From, To = ctx.To,
                On = on, Skipped = skipped, AnnBefore = annBefore, GuestBefore = guestBefore, Why = why, Ticks = SimClock.CurrentTicks };
            try { e.AnnAfter = AlAnnounced(s); e.GuestAfter = AlGuest(s); } catch (Exception) { }
            if (sAlEvents.Count >= 32) sAlEvents.RemoveAt(0);
            sAlEvents.Add(e);
        }

        // ---- test-only tracker (ApartmentLeaveCheck "throw"): null unless a check party is being watched ----

        internal class AlTrack { public string Name; public bool Arrived; public string FirstRoom, LastRunWhy; public int ToPublic, ToApartment, FarewellRun, FarewellSkipped; }
        internal static Dictionary<ulong, AlTrack> sAlTrack;  // by SimDescriptionId
        internal static Lot sAlTrackLot;

        static AlTrack AlTracked(Sim s)
        {
            AlTrack t;
            return s != null && s.SimDescription != null && sAlTrack != null && sAlTrack.TryGetValue(s.SimDescription.SimDescriptionId, out t) ? t : null;
        }

        static void AlTrackRoom(Sim s, int from, int to, Lot previousLot)
        {
            try
            {
                AlTrack t = AlTracked(s);
                Lot lot = s.LotCurrent;
                if (t == null || lot == null || lot != sAlTrackLot) return;
                if (!t.Arrived)
                {
                    // where it first stood on the party lot: hidden (H) or no allowed way outside (closed, docs/552)
                    int n;
                    bool? outside = HrReachesOutside(lot, to, s, out n);
                    t.FirstRoom = "r" + to + (lot.IsRoomHidden(to) ? "H" : "") + (lot.IsRoomPublic(to) ? "P" : "") + (outside == false ? " closed" : "");
                }
                t.Arrived = true;
                if (previousLot != lot || from == to) return;
                bool was = lot.IsRoomPublic(from), now = lot.IsRoomPublic(to);
                if (!was && now) t.ToPublic++;
                else if (was && !now) t.ToApartment++;
            }
            catch (Exception) { }
        }

        static void AlTrackFarewell(Sim s, bool skipped, string why)
        {
            AlTrack t = AlTracked(s);
            if (t == null) return;
            if (skipped) t.FarewellSkipped++; else { t.FarewellRun++; t.LastRunWhy = why; }
        }

        public class AlRoomHook
        {
            // EA's body runs unchanged; the hook only marks the context for OnSimLeavingLot.
            public void RoomHook(int roomId, int previousRoomId, Lot previousLot)
            {
                Sim sim = (Sim)(object)this;
                ulong id = sim.ObjectId.Value;
                sAlRooms[id] = new AlRoomCtx { From = previousRoomId, To = roomId };
                try
                {
                    RoomOrig(roomId, previousRoomId, previousLot);
                    if (sAlPending.Count > 0 && sim.SimDescription != null) { try { AlPendingRoom(sim, previousLot); } catch (Exception) { PatchCounters()[2394]++; } }
                    if (sAlTrack != null) AlTrackRoom(sim, previousRoomId, roomId, previousLot);
                }
                finally { sAlRooms.Remove(id); }
            }
            public void RoomOrig(int roomId, int previousRoomId, Lot previousLot) { } // receives Sim.RoomChanged's original header
        }

        public static class AlLeaveHook
        {
            public static void LeaveHook(Sim leavingSim)
            {
                AlRoomCtx ctx;
                if (!AlIsRoomCall(leavingSim, out ctx)) { LeaveOrig(leavingSim); return; }  // GoHome, GoOutside, commutes: EA's code
                int[] c = PatchCounters();
                c[2393]++;
                bool on = ShortcutOn("aptleave"), annBefore = false, guestBefore = false, leaving = true;
                string why = "switch off";
                bool player = true;
                try
                {
                    annBefore = AlAnnounced(leavingSim);
                    guestBefore = AlGuest(leavingSim);
                    if (on)
                    {
                        player = IsPlayerSim(leavingSim);
                        leaving = player ? AlLeavingPlayer(leavingSim, out why) : AlLeaving(leavingSim, out why);
                        if (!leaving) AlDefer(leavingSim);
                    }
                }
                catch (Exception) { c[2394]++; leaving = true; why = "error"; }
                AlTrackFarewell(leavingSim, !leaving, why);
                if (!leaving)
                {
                    c[2391]++;
                    if (player) c[2397]++;
                    AlRecord(leavingSim, ctx, on, true, annBefore, guestBefore, why);
                    return;
                }
                if (on) c[2392]++;
                LeaveOrig(leavingSim);
                try { AlRecord(leavingSim, ctx, on, false, annBefore, guestBefore, why); } catch (Exception) { c[2394]++; }
            }
            public static void LeaveOrig(Sim leavingSim) { } // receives VisitSituation.OnSimLeavingLot's original header
        }
    }
}
