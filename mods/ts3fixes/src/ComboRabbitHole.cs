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
    // Combined rabbit holes (docs/551): comboprotest, combovenue, combosituation (tunings kComboProtest / kComboVenue /
    // kComboSituation, all off by default), plus the shared combo helpers that ceremonyspot (EventRouteRetry.cs) uses.
    // Harness checks: ComboRabbitHoleCheck / ComboFixCheck (ComboChecks.cs).
    //
    // What EA does [verified in the decompiled game code unless marked]:
    // - A combo building (ComboRabbitHole subclass, e.g. ComboCityhallPoliceMilitary, ComboStadiumTheatre) makes each piece
    //   with CreateObjectOutOfWorld and ComboInitParams; the piece's RabbitHoleProxy is the combo. The piece is never placed:
    //   its Position is Vector3.OutOfWorld (EA's own Teleporter.PreTeleport tests exactly that to swap in the combo), and its
    //   LotCurrent is the WORLD lot (lot id FFFFFFFFFFFFFFFF), not null [verified in game, ComboRabbitHoleCheck, Appaloosa
    //   Plains 2026-11-14: all 13 pieces]. Interactions on a combo come from its pieces, so Target is the piece.
    // - Alarms of a piece are therefore fine: the world lot's AlarmManager is AlarmManager.Global (AlarmManager.LotId setter,
    //   lot id ulong.MaxValue), so AddAlarm / RemoveAlarm / GetTimeLeft all use the same manager. (An earlier `comboalarm`
    //   assumed a null lot; ComboFixCheck read EA's path as working, so it was removed.)
    // - A ReactionBroadcaster on a piece is registered on the world lot and finds Sims around origin.Position = OutOfWorld:
    //   it reaches nobody.
    //
    // comboprotest (EA bug, verified in code): CityHall.CreateProtest builds its three broadcasters on `this` (the piece),
    // unlike CreateCelebration, which uses RabbitHoleProxy; in a combo the protest never draws anyone. The hook moves them
    // to the building (origin and lot; the Protest interaction still targets the City Hall piece). RouteToGatheringArea for
    // a protest re-anchors on the entrance when RoutingSlot_0 is missing (the ceremonyspot case), for every Sim: it only
    // corrects the goal, the Sim still walks.
    //
    // combovenue (EA bug, verified in code): Theatre.StartShowSoundBroadcaster puts the TheatreMusic broadcaster on the
    // piece, so nobody near a combo theatre (ComboStadiumTheatre, ComboCriminalTheater) gets the buff; it is moved to the
    // building. The player-show calls (StartPlayerConcert & co.) do not throw: they pass the world lot to the search-light
    // and sandwich-board helpers, so only the lights and boards at the building stay unchanged (cosmetic, not fixed, like
    // EA's daily light / board alarms).
    //
    // combosituation (EA bug, verified in code): AutographSessionSituation takes the rabbit hole it is given as its
    // broadcaster origin and lot; for a piece that is OutOfWorld on the world lot, so the autographer (sent to the
    // building's entrance) never enters the broadcaster and the session never starts (no fans, no pay) until the player
    // cancels it. The building is used instead. A field trip to ComboStadiumTheatre gets its theatre link set first: EA
    // sets mTheatre only in the chaperone's OnFieldtripStart, so a student whose loop ticks first throws in
    // OnFieldtripUpdate [likely]. (FieldTripSituation itself works for a piece: its lot is the world lot, not null, and
    // the rest of the situation uses RabbitHoleProxy.LotCurrent.)
    //
    // Non-combo rabbit holes are untouched by every switch: each path tests RhPiece first and runs EA's code unchanged.
    // No Sim is moved by any of these fixes.
    //
    // Counters (registry fixes-2335): [2337] broadcasters moved to the building, [2338] protest routes re-anchored at the
    // entrance, [2340] autograph sessions moved to the building, [2342] ComboStadiumTheatre theatre links set, [2343]
    // errors. [2335] [2336] [2339] [2341] retired (comboalarm, the venue lot lend and the field-trip lot lend were removed).
    public static partial class Fixes
    {
        // ---- tuning ----
        const float kRhEntranceOut = 2f;  // metres out from the first enter slot when a slot reads as the world origin

        // ---- state (reflection, looked up once) ----
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
            Patch(typeof(CityHall), "CreateProtest", typeof(ComboProtestHook), "CreateProtestHook", "CreateProtestOrig");
            Patch(typeof(Theatre), "StartShowSoundBroadcaster", typeof(ComboTheatreHook), "BroadcasterHook", "BroadcasterOrig");
            Patch(typeof(AutographSessionSituation), "Create", typeof(ComboSituationHook), "AutographHook", "AutographOrig");
            Patch(typeof(FieldTripSituation), "Create", typeof(ComboSituationHook), "FieldTripHook", "FieldTripOrig");
        }

        // ---- hooks ----

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

        // combovenue: the TheatreMusic broadcaster moves from the piece to the building.
        public class ComboTheatreHook
        {
            public void BroadcasterHook(ShowVenue.ShowTypes type)
            {
                BroadcasterOrig(type);
                Theatre t = (Theatre)(object)this;
                if (RhPiece(t) && ShortcutOn("combovenue")) RhMoveBroadcaster(t, sRhTheatreBroadcaster);
            }
            public void BroadcasterOrig(ShowVenue.ShowTypes type) { } // receives Theatre.StartShowSoundBroadcaster's original body
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
                if (rabbitHole != null && ShortcutOn("combosituation")) RhLinkComboTheatre(rabbitHole);
                return FieldTripOrig(primaryStudents, age, school, rabbitHole);
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
        // slot), kRhEntranceOut metres out from the first enter slot (away from the building's centre) on terrain height. True when the entrance was used.
        static bool RhSlotOrEntrance(RabbitHole building, Slot slot, bool fix, out Vector3 pos, out Vector3 fwd)
        {
            pos = building.GetPositionOfSlot(slot);
            fwd = building.GetForwardOfSlot(slot);
            if (!fix || Math.Abs(pos.x) + Math.Abs(pos.z) > 1f || building.EnterSlots == null || building.EnterSlots.Count == 0) return false;
            // Outward = from the building's centre through the door. The enter slot's own forward faces the door (a Sim
            // entering looks at it), so it points into the footprint: a goal there has no path (measured 2026-11-14,
            // FailedBecauseNoPathToDestination for the protest crowd).
            Vector3 door = building.GetPositionOfSlot(building.EnterSlots[0]);
            Vector3 outward = door - building.Position;
            outward.y = 0f;
            if (outward.LengthSqr() < 0.01f) outward = -building.GetForwardOfSlot(building.EnterSlots[0]);
            fwd = outward.Normalize();
            pos = door + fwd * kRhEntranceOut;
            pos.y = World.GetTerrainHeight(pos.x, pos.z);
            return true;
        }

        // Moves the broadcaster held in piece.field from the piece (OutOfWorld, on the world lot) to the building:
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
