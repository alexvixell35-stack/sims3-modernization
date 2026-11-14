using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // Combined rabbit holes (docs/551): comboalarm, comboprotest, combovenue, combosituation (tunings kComboAlarm /
    // kComboProtest / kComboVenue / kComboSituation, all off by default), plus the shared combo helpers that ceremonyspot
    // (EventRouteRetry.cs) uses. Harness check: ComboRabbitHoleCheck (ComboChecks.cs).
    //
    // What EA does [verified in the decompiled game code unless marked]:
    // - A combo building (ComboRabbitHole subclass, e.g. ComboCityhallPoliceMilitary, ComboStadiumTheatre) makes each piece
    //   with CreateObjectOutOfWorld and ComboInitParams; the piece's RabbitHoleProxy is the combo. The piece is never added
    //   to the world: its Position is Vector3.OutOfWorld (EA's own Teleporter.PreTeleport tests exactly that to swap in the
    //   combo) and its LotCurrent stays null (mLotCurrent is not persisted and only the native lot-change callback sets it)
    //   [inferred; ComboRabbitHoleCheck reads both]. Interactions on a combo come from its pieces, so Target is the piece.
    // - GameObject.AddAlarm puts a lot-less object's alarm on AlarmManager.Global, but GameObject.RemoveAlarm and
    //   GetTimeLeft only look at mLotCurrent's manager, so a piece can add alarms and never remove or read them.
    // - ReactionBroadcaster.Init registers a lot-less origin on the world lot, finds Sims around origin.Position, and
    //   Lot.UpdateReactions drops any broadcaster whose origin has no LotCurrent: a broadcaster on a piece dies unheard.
    //
    // comboalarm (EA bug, verified in code): RemoveAlarm / GetTimeLeft on a piece also look on AlarmManager.Global and on
    // the building's lot manager (handles carry their manager's lot id, so no other alarm can match). Effects without it:
    // Stadium.ResetScore re-adds its repeating GameScoreUpdate alarm every game day and StopScore cannot remove it, so a
    // combo stadium (ComboSchoolStadium, ComboStadiumTheatre, ComboStadiumAnnex) runs one more score alarm per game day for
    // the rest of the session; a player concert cannot cancel the NPC show's end alarms; CityHall.GetProtestTimeRemaining
    // reads 0, so a City Hall protest in ComboCityhallPoliceMilitary lasts the 1 min floor for each protester.
    //
    // comboprotest (EA bug, verified in code): CityHall.CreateProtest builds its three broadcasters on `this` (the piece),
    // unlike CreateCelebration, which uses RabbitHoleProxy; in a combo the protest never draws anyone. The hook moves them
    // to the building (origin and lot; the Protest interaction still targets the City Hall piece). RouteToGatheringArea for
    // a protest re-anchors on the entrance when RoutingSlot_0 is missing (the ceremonyspot case), for every Sim: it only
    // corrects the goal, the Sim still walks.
    //
    // combovenue (EA bug, verified in code): Theatre.StartPlayerConcert / EndPlayerConcert / StartPlayerConvention /
    // EndPlayerConvention and Stadium.StartPlayerConcert / EndPlayerConcert pass LotCurrent (null for a piece) to the search
    // light and sandwich board helpers, which dereference it, or call LotCurrent.AlarmManager: the NullReferenceException
    // ends ShowVenue.PerformConcert / HoldPsychicConvention / HoldGameConvention inside the building (no pay, no event, no
    // concert count). These six run with the building's lot lent to the piece (RhLendLot). One-shot alarms they add
    // (EndShow's facade update, the sandwich-board reset) then land on the building's lot manager, which runs like any
    // lot's (Lot.Simulate) and which comboalarm also searches; the lend can last across a yield inside the call (the
    // search lights' state machines), with the same effect for an alarm added meanwhile. The TheatreMusic broadcaster
    // (Theatre.StartShowSoundBroadcaster) is moved to the building. The daily search-light / sandwich-board alarms stay as
    // EA (cosmetic; EA drops a repeating alarm after its callback throws).
    //
    // combosituation (EA bug, verified in code): AutographSessionSituation and FieldTripSituation take their lot from the
    // rabbit hole they are given (base(rabbitHole.LotCurrent)); for a piece that is null and their first state Exit()s at
    // once: a Hold Autograph Session or an accepted school field trip at a combo is lost. Autograph: the building is used
    // instead (its broadcaster then finds fans). Field trip: the destination stays EA's choice (the trip interaction routes
    // through RabbitHoleProxy already) and the situation is built with the building's lot. A field trip to
    // ComboStadiumTheatre also gets its theatre link set first: EA sets mTheatre only in the chaperone's OnFieldtripStart,
    // so a student whose loop ticks first throws in OnFieldtripUpdate [likely].
    //
    // Non-combo rabbit holes are untouched by every switch: each path tests RhPiece first and runs EA's code unchanged.
    // No Sim is moved by any of these fixes.
    //
    // Counters (registry fixes-2335): [2335] piece alarms removed through the building's managers, [2336] piece alarm
    // times read there, [2337] broadcasters moved to the building, [2338] protest routes re-anchored at the entrance,
    // [2339] venue calls run with the building's lot, [2340] autograph sessions moved to the building, [2341] field trips
    // given the building's lot, [2342] ComboStadiumTheatre theatre links set, [2343] errors ([2344] reserved, unused).
    public static partial class Fixes
    {
        // ---- tuning ----
        const float kRhEntranceOut = 2f;  // metres out from the first enter slot when a slot reads as the world origin

        // ---- state (reflection, looked up once) ----
        static readonly FieldInfo sRhLotField = typeof(GameObject).GetField("mLotCurrent", kInstAll);
        static readonly FieldInfo sRhBroadcastOrigin = typeof(ReactionBroadcaster).GetField("mBroadcastingObject", kInstAll);
        static readonly MethodInfo sRhLotAddReaction = typeof(Lot).GetMethod("AddReaction", kInstAll);
        static readonly MethodInfo sRhLotRemoveReaction = typeof(Lot).GetMethod("RemoveReaction", kInstAll);
        static readonly FieldInfo sRhComboTheatre = typeof(ComboStadiumTheatre).GetField("mTheatre", kInstAll);
        static readonly FieldInfo[] sRhProtestBroadcasters = {
            typeof(CityHall).GetField("mGatheringBroadcaster", kInstAll),
            typeof(CityHall).GetField("mProtestReactionBroadcaster", kInstAll),
            typeof(CityHall).GetField("mProtestPetReactionBroadcaster", kInstAll) };
        static readonly FieldInfo sRhTheatreBroadcaster = typeof(Theatre).GetField("mTheatreBroadcaster", kInstAll);

        static void ApplyComboRabbitHole()
        {
            Patch(typeof(GameObject), "RemoveAlarm", typeof(ComboAlarmHook), "RemoveAlarmHook", "RemoveAlarmOrig");
            Patch(typeof(GameObject), "GetTimeLeft", typeof(ComboAlarmHook), "GetTimeLeftHook", "GetTimeLeftOrig");
            Patch(typeof(CityHall), "CreateProtest", typeof(ComboProtestHook), "CreateProtestHook", "CreateProtestOrig");
            Patch(typeof(Theatre), "StartPlayerConcert", typeof(ComboTheatreHook), "StartConcertHook", "StartConcertOrig");
            Patch(typeof(Theatre), "EndPlayerConcert", typeof(ComboTheatreHook), "EndConcertHook", "EndConcertOrig");
            Patch(typeof(Theatre), "StartPlayerConvention", typeof(ComboTheatreHook), "StartConventionHook", "StartConventionOrig");
            Patch(typeof(Theatre), "EndPlayerConvention", typeof(ComboTheatreHook), "EndConventionHook", "EndConventionOrig");
            Patch(typeof(Theatre), "StartShowSoundBroadcaster", typeof(ComboTheatreHook), "BroadcasterHook", "BroadcasterOrig");
            Patch(typeof(Stadium), "StartPlayerConcert", typeof(ComboStadiumHook), "StartConcertHook", "StartConcertOrig");
            Patch(typeof(Stadium), "EndPlayerConcert", typeof(ComboStadiumHook), "EndConcertHook", "EndConcertOrig");
            Patch(typeof(AutographSessionSituation), "Create", typeof(ComboSituationHook), "AutographHook", "AutographOrig");
            Patch(typeof(FieldTripSituation), "Create", typeof(ComboSituationHook), "FieldTripHook", "FieldTripOrig");
        }

        // ---- hooks ----

        // comboalarm. GameObject.RemoveAlarm / GetTimeLeft run for every object: a non-piece goes straight to EA's body.
        public class ComboAlarmHook
        {
            public void RemoveAlarmHook(AlarmHandle handle)
            {
                RabbitHole rh = (object)this as RabbitHole;
                if (rh != null && RhPiece(rh) && ShortcutOn("comboalarm")) RhRemovePieceAlarm(rh, handle);
                RemoveAlarmOrig(handle);
            }
            public void RemoveAlarmOrig(AlarmHandle handle) { } // receives GameObject.RemoveAlarm's original body

            public float GetTimeLeftHook(AlarmHandle handle, TimeUnit unit)
            {
                float t = GetTimeLeftOrig(handle, unit);
                RabbitHole rh = (object)this as RabbitHole;
                if (t > 0f || rh == null || !RhPiece(rh) || !ShortcutOn("comboalarm")) return t;
                t = RhPieceTimeLeft(rh, handle, unit);
                if (t > 0f) PatchCounters()[2336]++;
                return t;
            }
            public float GetTimeLeftOrig(AlarmHandle handle, TimeUnit unit) { return 0f; } // receives GameObject.GetTimeLeft's original body
        }

        public class ComboProtestHook
        {
            public void CreateProtestHook()
            {
                CreateProtestOrig();
                CityHall hall = (CityHall)(object)this;
                if (!RhPiece(hall) || !ShortcutOn("comboprotest")) return;
                foreach (FieldInfo f in sRhProtestBroadcasters) RhMoveBroadcaster(hall, f);
            }
            public void CreateProtestOrig() { } // receives CityHall.CreateProtest's original body
        }

        // combovenue, Theatre: the four player-show calls run with the building's lot; the music broadcaster moves.
        public class ComboTheatreHook
        {
            public void StartConcertHook()
            {
                bool lent = RhLendLotIf((RabbitHole)(object)this, "combovenue");
                try { StartConcertOrig(); } finally { if (lent) RhTakeLot((RabbitHole)(object)this); }
            }
            public void StartConcertOrig() { } // receives Theatre.StartPlayerConcert's original body

            public void EndConcertHook()
            {
                bool lent = RhLendLotIf((RabbitHole)(object)this, "combovenue");
                try { EndConcertOrig(); } finally { if (lent) RhTakeLot((RabbitHole)(object)this); }
            }
            public void EndConcertOrig() { } // receives Theatre.EndPlayerConcert's original body

            public void StartConventionHook(ShowVenue.ShowTypes showType)
            {
                bool lent = RhLendLotIf((RabbitHole)(object)this, "combovenue");
                try { StartConventionOrig(showType); } finally { if (lent) RhTakeLot((RabbitHole)(object)this); }
            }
            public void StartConventionOrig(ShowVenue.ShowTypes showType) { } // receives Theatre.StartPlayerConvention's original body

            public void EndConventionHook()
            {
                bool lent = RhLendLotIf((RabbitHole)(object)this, "combovenue");
                try { EndConventionOrig(); } finally { if (lent) RhTakeLot((RabbitHole)(object)this); }
            }
            public void EndConventionOrig() { } // receives Theatre.EndPlayerConvention's original body

            public void BroadcasterHook(ShowVenue.ShowTypes type)
            {
                BroadcasterOrig(type);
                Theatre t = (Theatre)(object)this;
                if (RhPiece(t) && ShortcutOn("combovenue")) RhMoveBroadcaster(t, sRhTheatreBroadcaster);
            }
            public void BroadcasterOrig(ShowVenue.ShowTypes type) { } // receives Theatre.StartShowSoundBroadcaster's original body
        }

        public class ComboStadiumHook
        {
            public void StartConcertHook()
            {
                bool lent = RhLendLotIf((RabbitHole)(object)this, "combovenue");
                try { StartConcertOrig(); } finally { if (lent) RhTakeLot((RabbitHole)(object)this); }
            }
            public void StartConcertOrig() { } // receives Stadium.StartPlayerConcert's original body

            public void EndConcertHook()
            {
                bool lent = RhLendLotIf((RabbitHole)(object)this, "combovenue");
                try { EndConcertOrig(); } finally { if (lent) RhTakeLot((RabbitHole)(object)this); }
            }
            public void EndConcertOrig() { } // receives Stadium.EndPlayerConcert's original body
        }

        public static class ComboSituationHook
        {
            public static AutographSessionSituation AutographHook(Sim autographer, RabbitHole rabbitHole)
            {
                if (rabbitHole != null && RhPiece(rabbitHole) && ShortcutOn("combosituation"))
                {
                    rabbitHole = rabbitHole.RabbitHoleProxy;
                    PatchCounters()[2340]++;
                }
                return AutographOrig(autographer, rabbitHole);
            }
            public static AutographSessionSituation AutographOrig(Sim autographer, RabbitHole rabbitHole) { return null; } // receives AutographSessionSituation.Create's original body

            public static FieldTripSituation FieldTripHook(List<SimDescription> primaryStudents, CASAgeGenderFlags age, School school, RabbitHole rabbitHole)
            {
                bool on = rabbitHole != null && ShortcutOn("combosituation");
                if (on) RhLinkComboTheatre(rabbitHole);
                bool lent = on && RhLendLot(rabbitHole);
                if (lent) PatchCounters()[2341]++;
                try { return FieldTripOrig(primaryStudents, age, school, rabbitHole); }
                finally { if (lent) RhTakeLot(rabbitHole); }
            }
            public static FieldTripSituation FieldTripOrig(List<SimDescription> primaryStudents, CASAgeGenderFlags age, School school, RabbitHole rabbitHole) { return null; } // receives FieldTripSituation.Create's original body
        }

        // ---- shared combo helpers (ceremonyspot uses RhSlotOrEntrance too) ----

        // A piece of a combo building: its RabbitHoleProxy is another object (the combo). A stand-alone rabbit hole and the
        // combo itself are their own proxy.
        static bool RhPiece(RabbitHole rh)
        {
            RabbitHole proxy = rh.RabbitHoleProxy;
            return proxy != null && proxy != rh;
        }

        // EA's slot on the building, or, when fix is set and the slot reads as the world origin (the combo model has no such
        // slot), kRhEntranceOut metres out from the first enter slot on terrain height. True when the entrance was used.
        static bool RhSlotOrEntrance(RabbitHole building, Slot slot, bool fix, out Vector3 pos, out Vector3 fwd)
        {
            pos = building.GetPositionOfSlot(slot);
            fwd = building.GetForwardOfSlot(slot);
            if (!fix || Math.Abs(pos.x) + Math.Abs(pos.z) > 1f || building.EnterSlots == null || building.EnterSlots.Count == 0) return false;
            Slot door = building.EnterSlots[0];
            fwd = building.GetForwardOfSlot(door);
            pos = building.GetPositionOfSlot(door) + fwd * kRhEntranceOut;
            pos.y = World.GetTerrainHeight(pos.x, pos.z);
            return true;
        }

        // Lends a piece its building's lot for one EA call (only while it has none). True when lent; the caller gives it
        // back with RhTakeLot in a finally. Alarms the piece adds meanwhile go to the building's lot manager (see header).
        static bool RhLendLot(RabbitHole rh)
        {
            try
            {
                if (sRhLotField == null || !RhPiece(rh) || sRhLotField.GetValue(rh) != null) return false;
                Lot lot = rh.RabbitHoleProxy.LotCurrent;
                if (lot == null) return false;
                sRhLotField.SetValue(rh, lot);
                return true;
            }
            catch (Exception) { PatchCounters()[2343]++; return false; }
        }

        static bool RhLendLotIf(RabbitHole rh, string sw)
        {
            if (!RhPiece(rh) || !ShortcutOn(sw) || !RhLendLot(rh)) return false;
            PatchCounters()[2339]++;
            return true;
        }

        static void RhTakeLot(RabbitHole rh)
        {
            try { sRhLotField.SetValue(rh, null); }
            catch (Exception) { PatchCounters()[2343]++; }
        }

        // The piece's alarms live on AlarmManager.Global (added while it had no lot) or, if added during a lend, on the
        // building's lot manager. A handle names its own manager's lot, so trying both cannot remove another alarm.
        static void RhRemovePieceAlarm(RabbitHole rh, AlarmHandle handle)
        {
            if (handle == AlarmHandle.kInvalidHandle) return;
            try
            {
                float before = RhPieceTimeLeft(rh, handle, TimeUnit.Minutes);
                if (AlarmManager.Global != null) AlarmManager.Global.RemoveAlarm(handle);
                Lot lot = rh.RabbitHoleProxy.LotCurrent;
                if (lot != null && lot.AlarmManager != null) lot.AlarmManager.RemoveAlarm(handle);
                if (before > 0f) PatchCounters()[2335]++;
            }
            catch (Exception) { PatchCounters()[2343]++; }
        }

        static float RhPieceTimeLeft(RabbitHole rh, AlarmHandle handle, TimeUnit unit)
        {
            try
            {
                float t = AlarmManager.Global != null ? AlarmManager.Global.GetTimeLeft(handle, unit) : 0f;
                Lot lot = rh.RabbitHoleProxy.LotCurrent;
                if (t <= 0f && lot != null && lot.AlarmManager != null) t = lot.AlarmManager.GetTimeLeft(handle, unit);
                return t;
            }
            catch (Exception) { PatchCounters()[2343]++; return 0f; }
        }

        // Moves the broadcaster held in piece.field from the piece (out of the world, on the world lot) to the building:
        // the building is the origin and its lot runs it. The broadcaster's interaction still targets the piece.
        static void RhMoveBroadcaster(RabbitHole piece, FieldInfo field)
        {
            try
            {
                ReactionBroadcaster b = field == null ? null : field.GetValue(piece) as ReactionBroadcaster;
                RabbitHole building = piece.RabbitHoleProxy;
                Lot lot = building.LotCurrent;
                if (b == null || lot == null || b.BroadcastingObject != piece || sRhBroadcastOrigin == null || sRhLotAddReaction == null || sRhLotRemoveReaction == null) return;
                Lot world = LotManager.GetWorldLot();
                if (world != null) sRhLotRemoveReaction.Invoke(world, new object[] { b });
                Lot lent = piece.LotCurrent;  // set when this runs inside a lend (combovenue)
                if (lent != null && lent != world) sRhLotRemoveReaction.Invoke(lent, new object[] { b });
                sRhBroadcastOrigin.SetValue(b, building);
                sRhLotAddReaction.Invoke(lot, new object[] { b });
                PatchCounters()[2337]++;
            }
            catch (Exception) { PatchCounters()[2343]++; }
        }

        // A field trip to ComboStadiumTheatre: EA's mTheatre is set only in the chaperone's OnFieldtripStart.
        static void RhLinkComboTheatre(RabbitHole rh)
        {
            try
            {
                ComboStadiumTheatre combo = rh as ComboStadiumTheatre;
                if (combo == null || sRhComboTheatre == null || sRhComboTheatre.GetValue(combo) != null) return;
                Theatre t = combo.GetTheatre();
                if (t == null) return;
                sRhComboTheatre.SetValue(combo, t);
                PatchCounters()[2342]++;
            }
            catch (Exception) { PatchCounters()[2343]++; }
        }
    }
}
