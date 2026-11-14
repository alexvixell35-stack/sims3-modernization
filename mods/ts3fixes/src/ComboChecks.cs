using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.RouteDestinations;

namespace Ts3Fixes
{
    // Harness check for the combo rabbit hole audit (docs/551; test only, called through the agent's reflect_call, never by
    // the game, changes nothing). ComboRabbitHoleCheck("combo") lists every combo building and its pieces, "all" every
    // rabbit hole: type, Guid, proxy type, InWorld, Position, LotCurrent, whether RoutingSlot_0..3 read as the world origin
    // on the proxy, and the proxy's routing / enter / exit slot counts. Then the order Queries.GetObjects returns ICityHall
    // and IFieldTrippable objects in (EA code that takes the first one gets a piece when a piece comes first). These are
    // the facts docs/551 marks [inferred]: a piece is out of the world with Position OutOfWorld and LotCurrent null, and a
    // combo model lacks RoutingSlot_0.
    //
    // ComboFixCheck("run") then exercises one EA path per switch on the first matching piece, as the game would call it,
    // and reads the outcome (BROKEN = EA's behaviour, FIXED = the switch's, N/A = no such piece or it is busy):
    // lot (the piece's LotCurrent and whether its alarms land on AlarmManager.Global: the fact that retired comboalarm),
    // protest (CreateProtest on a City Hall piece: the gathering broadcaster's origin, and where a protester would stand:
    // the end of the gathering route planned for the active Sim, EA's or the fix's, with its distance from the building,
    // its lot and whether it is outdoors; the route is planned only, never walked; ProtestOverCallback ends the protest),
    // venue (StartShowSoundBroadcaster on an idle Theatre piece: the TheatreMusic broadcaster's origin; ended again),
    // situation (AutographSessionSituation.Create for the active Sim at a piece: the session's rabbit hole and lot; the
    // situation is ended at once and the active Sim's queue cleared). Run it with the three switches off, then on.
    public static partial class Fixes
    {
        public static object ComboRabbitHoleCheck(string what)
        {
            bool all = what == "all";
            if (!all && what != "combo") return "combo check: <combo|all>";
            StringBuilder sb = new StringBuilder();
            int pieces = 0, piecesLotless = 0, piecesOut = 0;
            foreach (RabbitHole rh in Sims3.Gameplay.Queries.GetObjects<RabbitHole>())
            {
                bool piece = RhPiece(rh), combo = rh is ComboRabbitHole;
                if (!all && !piece && !combo) continue;
                if (piece)
                {
                    pieces++;
                    if (rh.LotCurrent == null) piecesLotless++;
                    if (!rh.InWorld) piecesOut++;
                }
                sb.Append(piece ? "  piece " : "rh ").Append(rh.GetType().Name).Append(" guid=").Append(rh.Guid)
                  .Append(" proxy=").Append(rh.RabbitHoleProxy == null ? "null" : rh.RabbitHoleProxy == rh ? "self" : rh.RabbitHoleProxy.GetType().Name)
                  .Append(" inWorld=").Append(rh.InWorld).Append(" pos=").Append(CheckPos(rh.Position))
                  .Append(" lot=").Append(rh.LotCurrent == null ? "null" : rh.LotCurrent.LotId.ToString("X"));
                if (!piece) sb.Append(ComboSlotFacts(rh));
                sb.Append('\n');
            }
            sb.Append("pieces ").Append(pieces).Append(", out of world ").Append(piecesOut).Append(", with no lot ").Append(piecesLotless).Append('\n');
            sb.Append("ICityHall order: ").Append(TypeOrder(Sims3.Gameplay.Queries.GetObjects<ICityHall>())).Append('\n');
            sb.Append("IFieldTrippable order: ").Append(TypeOrder(Sims3.Gameplay.Queries.GetObjects<IFieldTrippable>())).Append('\n');
            return sb.ToString();
        }

        // One part of the check. With a switch off EA's own code may throw on the out-of-world piece: that is the bug
        // showing, so it reads as BROKEN with the exception instead of failing the whole check.
        delegate string ComboPart();
        static string ComboCheckPart(ComboPart part)
        {
            try { return part(); }
            catch (Exception e) { return "BROKEN (throws " + e.GetType().Name + ": " + e.Message + ")"; }
        }

        public static object ComboFixCheck(string what)
        {
            if (what != "run") return "combo fix check: run";
            StringBuilder sb = new StringBuilder();
            sb.Append("lot: ").Append(ComboCheckPart(ComboCheckLot)).Append('\n');
            sb.Append("protest: ").Append(ComboCheckPart(ComboCheckProtest)).Append('\n');
            sb.Append("venue: ").Append(ComboCheckPart(ComboCheckVenue)).Append('\n');
            sb.Append("situation: ").Append(ComboCheckPart(ComboCheckSituation)).Append('\n');
            int[] c = PatchCounters();
            sb.Append("counters 2337 2338 2340 2342 2343:");
            foreach (int i in new int[] { 2337, 2338, 2340, 2342, 2343 }) sb.Append(' ').Append(c[i]);
            return sb.ToString();
        }

        static T FirstPiece<T>() where T : RabbitHole
        {
            foreach (T rh in Sims3.Gameplay.Queries.GetObjects<T>()) if (RhPiece(rh)) return rh;
            return null;
        }

        static string LotName(Lot lot)
        {
            return lot == null ? "null" : lot.IsWorldLot ? "world lot" : lot.LotId.ToString("X");
        }

        // Not a fix: the fact comboalarm was built on (a null lot) and why it was removed. EA's alarm calls on a piece use
        // its lot's manager, and the world lot's manager is AlarmManager.Global, where AddAlarm also puts them.
        static string ComboCheckLot()
        {
            RabbitHole piece = FirstPiece<RabbitHole>();
            if (piece == null) return "N/A (no combo piece in this world)";
            Lot lot = piece.LotCurrent;
            bool global = lot != null && lot.AlarmManager == AlarmManager.Global;
            AlarmHandle h = piece.AddAlarm(60f, TimeUnit.Minutes, delegate { }, "ts3fixes combo check", AlarmType.NeverPersisted);
            float before = piece.GetTimeLeft(h, TimeUnit.Minutes);
            piece.RemoveAlarm(h);
            bool kept = AlarmManager.Global.GetTimeLeft(h, TimeUnit.Minutes) > 0f;
            AlarmManager.Global.RemoveAlarm(h);
            return piece.GetType().Name + ": lot " + LotName(lot) + (global ? " (manager = Global)" : "") + ", alarm time read " + before.ToString("0")
                + " min, " + (kept ? "kept after RemoveAlarm (EA bug)" : "removed (EA works, no fix needed)");
        }

        static string ComboCheckProtest()
        {
            CityHall hall = FirstPiece<CityHall>();
            Sim sim = Sim.ActiveActor;
            if (hall == null || sim == null) return "N/A (no City Hall piece or no active Sim)";
            if (hall.IsGatheringActive) return "N/A (a gathering is active)";
            hall.CreateProtest();
            string spot;
            bool good;
            try
            {
                FieldInfo f = typeof(CityHall).GetField("mGatheringBroadcaster", kInstAll);
                ReactionBroadcaster b = f == null ? null : f.GetValue(hall) as ReactionBroadcaster;
                object origin = b == null ? null : b.BroadcastingObject;
                bool moved = origin == hall.RabbitHoleProxy;
                good = moved & ComboProtestSpot(hall, sim, out spot);
                return (good ? "FIXED" : "BROKEN") + " (broadcaster origin " + (origin == null ? "none" : origin == hall ? "the piece" : origin.GetType().Name)
                    + ", protest time left " + hall.GetProtestTimeRemaining().ToString("0") + " min; " + spot + ")";
            }
            finally { hall.ProtestOverCallback(); }
        }

        // Where a protester would stand: the end of the gathering route for sim, planned (never walked). The fix's route
        // (EvrCrowdRoute: the entrance anchor, comboprotest on) or, when it gives none, EA's own goal from
        // CityHall.RouteToGatheringArea (the building's RoutingSlot_0). True when the spot is on the building's lot and
        // outdoors (World.IsPositionOutside and no room at that point).
        static bool ComboProtestSpot(CityHall hall, Sim sim, out string spot)
        {
            RabbitHole building = hall.RabbitHoleProxy;
            Route r = EvrCrowdRoute(hall, sim);
            string whose = "fix";
            if (r == null)
            {
                whose = "EA (fix gave none: " + (sEvrCrowdWhy == "" ? "-" : sEvrCrowdWhy) + ", protest active " + hall.IsProtestActive + ")";
                Vector3 c = building.GetPositionOfSlot(Slot.RoutingSlot_0);
                c.y = World.GetTerrainHeight(c.x, c.z);
                RadialRangeDestination d = new RadialRangeDestination();
                d.mCenterPoint = c;
                d.mfMinRadius = EvrTunable("kMinProtestRadius", 0f);
                d.mfMaxRadius = EvrTunable("kMaxProtestRadius", 6f);
                d.mfConeAngle = EvrTunable("kProtestRouteConeAngle", 45f) * (float)Math.PI / 180f;
                d.mConeVector = building.GetForwardOfSlot(Slot.RoutingSlot_0);
                d.mfPreferredSpacing = 0.5f;
                d.mFacingPreference = RouteOrientationPreference.TowardsObject;
                d.ScoreFunctionWeights[6] = 1f;
                r = sim.CreateRoute();
                r.AddDestination(d);
                r.Plan();
            }
            Vector3 anchor, fwd;
            bool moved;
            EvrAnchor(hall, ShortcutOn("comboprotest"), out anchor, out fwd, out moved);
            bool ignore;
            string a = "anchor (" + (moved ? "entrance" : "RoutingSlot_0") + ") " + ComboSpotFacts(building, anchor, out ignore);
            if (!r.PlanResult.Succeeded()) { spot = a + "; " + whose + " route: no plan"; return false; }
            bool good;
            spot = a + "; " + whose + " route ends " + ComboSpotFacts(building, r.GetDestPoint(), out good);
            return good;
        }

        // A point's position, distance from the building, lot and indoor test; ok = on the building's lot and outdoors.
        static string ComboSpotFacts(RabbitHole building, Vector3 p, out bool ok)
        {
            Lot at = LotManager.GetLotAtPoint(p);
            bool onLot = at != null && at == building.LotCurrent;
            bool outside = World.IsPositionOutside(p) && GlobalFunctions.IsOutside(p.x, p.y, p.z, 0f);
            ok = onLot && outside;
            return "at " + CheckPos(p) + ", " + (p - building.Position).Length().ToString("0.0") + " m from the building, lot " + LotName(at)
                + (onLot ? " (the building's)" : " (not the building's)") + ", " + (outside ? "outdoors" : "indoors");
        }

        // StartShowSoundBroadcaster is private and swapped by combovenue: MethodInfo.Invoke refuses a swapped method
        // ("Unable to invoke an invalid target", EaFixes.cs PrefRemoveFn); a delegate runs it like the game's own callers.
        delegate void ShowSoundFn(ShowVenue.ShowTypes type);

        static string ComboCheckVenue()
        {
            Theatre t = FirstPiece<Theatre>();
            if (t == null) return "N/A (no Theatre piece: ComboStadiumTheatre / ComboCriminalTheater only)";
            if (t.IsShowHappening() || sRhTheatreBroadcaster == null || sRhTheatreBroadcaster.GetValue(t) != null) return "N/A (the theatre piece has a show on)";
            MethodInfo start = typeof(Theatre).GetMethod("StartShowSoundBroadcaster", kInstAll);
            MethodInfo end = typeof(Theatre).GetMethod("EndShowSoundBroadcaster", kInstAll);
            if (start == null || end == null) return "N/A (Theatre show-sound methods not found)";
            ((ShowSoundFn)Delegate.CreateDelegate(typeof(ShowSoundFn), t, start))(ShowVenue.ShowTypes.kPlayerConcert);
            ReactionBroadcaster b = sRhTheatreBroadcaster.GetValue(t) as ReactionBroadcaster;
            object origin = b == null ? null : b.BroadcastingObject;
            end.Invoke(t, null);  // not swapped
            return (origin == t.RabbitHoleProxy ? "FIXED" : "BROKEN") + " (TheatreMusic broadcaster origin " + (origin == null ? "none" : origin == t ? "the piece" : origin.GetType().Name) + ")";
        }

        // EA's broken arm: the session is centred on the piece (OutOfWorld, world lot), so the autographer never enters its
        // broadcaster. Fixed: centred on the building, on the building's lot.
        static string ComboCheckSituation()
        {
            Sim sim = Sim.ActiveActor;
            RabbitHole piece = FirstPiece<RabbitHole>();
            if (sim == null || piece == null) return "N/A (no active Sim or no combo piece)";
            if (AutographSessionSituation.GetAutographSessionSituation(sim) != null) return "N/A (the active Sim already holds a session)";
            AutographSessionSituation s = AutographSessionSituation.Create(sim, piece);
            RabbitHole at = s == null ? null : s.RabbitHole;
            Lot lot = s == null ? null : s.Lot;
            try { if (s != null) s.Exit(); } catch (Exception) { }
            sim.InteractionQueue.CancelAllInteractions();  // the walk to the session EA queues for the autographer
            bool ok = at != null && at.Position != Vector3.OutOfWorld && lot != null && lot == piece.RabbitHoleProxy.LotCurrent;
            string where = at == null ? "none" : at == piece ? "the piece" : at.GetType().Name;
            return (ok ? "FIXED" : "BROKEN") + " (session at " + where + (at == null ? "" : " " + CheckPos(at.Position)) + ", lot " + LotName(lot) + ")";
        }

        // RoutingSlot_0..3 on the building (o = reads as the world origin, + = a real point), slot counts.
        static string ComboSlotFacts(RabbitHole rh)
        {
            StringBuilder sb = new StringBuilder(" routingSlot0-3=");
            Slot[] slots = { Slot.RoutingSlot_0, Slot.RoutingSlot_1, Slot.RoutingSlot_2, Slot.RoutingSlot_3 };
            foreach (Slot s in slots)
            {
                Vector3 p = rh.GetPositionOfSlot(s);
                sb.Append(Math.Abs(p.x) + Math.Abs(p.z) > 1f ? '+' : 'o');
            }
            Slot[] routing = rh.GetRoutingSlots();
            sb.Append(" routingSlots=").Append(routing == null ? 0 : routing.Length)
              .Append(" enter=").Append(rh.EnterSlots == null ? 0 : rh.EnterSlots.Count)
              .Append(" exit=").Append(rh.ExitSlots == null ? 0 : rh.ExitSlots.Count);
            return sb.ToString();
        }

        static string CheckPos(Vector3 p)
        {
            return p == Vector3.OutOfWorld ? "OutOfWorld" : string.Format("{0:0.0},{1:0.0},{2:0.0}", p.x, p.y, p.z);
        }

        static string TypeOrder<T>(T[] objs) where T : class
        {
            List<string> names = new List<string>();
            if (objs != null)
                foreach (T o in objs)
                {
                    RabbitHole rh = o as RabbitHole;
                    names.Add(o.GetType().Name + (rh != null && RhPiece(rh) ? "(piece)" : ""));
                }
            return names.Count == 0 ? "none" : string.Join(", ", names.ToArray());
        }
    }
}
