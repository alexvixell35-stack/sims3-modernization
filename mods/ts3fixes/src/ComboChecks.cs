using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

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
    // and reads the outcome (BROKEN = EA's behaviour, FIXED = the switch's, N/A = no such piece or it is busy): alarm (a
    // piece adds then removes a 60 min alarm: still on Global = BROKEN), protest (CreateProtest on a City Hall piece: the
    // gathering broadcaster's origin, the protest time left; ProtestOverCallback ends it), venue (StartPlayerConcert /
    // EndPlayerConcert on an idle Theatre or Stadium piece: a NullReferenceException = BROKEN), situation
    // (AutographSessionSituation.Create for the active Sim at a piece: a situation with no lot = BROKEN; the situation is
    // ended at once and the active Sim's queue cleared). Run it with the four switches off, then on.
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

        public static object ComboFixCheck(string what)
        {
            if (what != "run") return "combo fix check: run";
            StringBuilder sb = new StringBuilder();
            sb.Append("alarm: ").Append(ComboCheckAlarm()).Append('\n');
            sb.Append("protest: ").Append(ComboCheckProtest()).Append('\n');
            sb.Append("venue: ").Append(ComboCheckVenue()).Append('\n');
            sb.Append("situation: ").Append(ComboCheckSituation()).Append('\n');
            int[] c = PatchCounters();
            sb.Append("counters 2335-2343:");
            for (int i = 2335; i <= 2343; i++) sb.Append(' ').Append(c[i]);
            return sb.ToString();
        }

        static T FirstPiece<T>() where T : RabbitHole
        {
            foreach (T rh in Sims3.Gameplay.Queries.GetObjects<T>()) if (RhPiece(rh)) return rh;
            return null;
        }

        static string ComboCheckAlarm()
        {
            RabbitHole piece = FirstPiece<RabbitHole>();
            if (piece == null) return "N/A (no combo piece in this world)";
            AlarmHandle h = piece.AddAlarm(60f, TimeUnit.Minutes, delegate { }, "ts3fixes combo check", AlarmType.NeverPersisted);
            float before = piece.GetTimeLeft(h, TimeUnit.Minutes);
            piece.RemoveAlarm(h);
            bool kept = AlarmManager.Global.GetTimeLeft(h, TimeUnit.Minutes) > 0f;
            AlarmManager.Global.RemoveAlarm(h);
            return (kept ? "BROKEN" : "FIXED") + " (" + piece.GetType().Name + ": time left read " + before.ToString("0") + " min, alarm " + (kept ? "kept after RemoveAlarm" : "removed") + ")";
        }

        static string ComboCheckProtest()
        {
            CityHall hall = FirstPiece<CityHall>();
            if (hall == null) return "N/A (no City Hall piece)";
            if (hall.IsGatheringActive) return "N/A (a gathering is active)";
            hall.CreateProtest();
            FieldInfo f = typeof(CityHall).GetField("mGatheringBroadcaster", kInstAll);
            ReactionBroadcaster b = f == null ? null : f.GetValue(hall) as ReactionBroadcaster;
            object origin = b == null ? null : b.BroadcastingObject;
            float left = hall.GetProtestTimeRemaining();
            hall.ProtestOverCallback();
            return (origin == hall.RabbitHoleProxy ? "FIXED" : "BROKEN") + " (broadcaster origin " + (origin == null ? "none" : origin == hall ? "the piece" : origin.GetType().Name)
                + ", protest time left " + left.ToString("0") + " min)";
        }

        static string ComboCheckVenue()
        {
            ShowVenue venue = null;
            Theatre t = FirstPiece<Theatre>();
            if (t != null && !t.IsShowHappening()) venue = t;
            Stadium st = venue == null ? FirstPiece<Stadium>() : null;
            if (st != null && !st.IsGameHappening()) venue = st;
            if (venue == null) return "N/A (no idle Theatre or Stadium piece)";
            string start = ComboInvoke(venue, "StartPlayerConcert"), end = ComboInvoke(venue, "EndPlayerConcert");
            bool ok = start == null && end == null;
            return (ok ? "FIXED" : "BROKEN") + " (" + venue.GetType().Name + ": start " + (start ?? "ok") + ", end " + (end ?? "ok") + ")";
        }

        static string ComboInvoke(object target, string method)
        {
            MethodInfo m = target.GetType().GetMethod(method, kInstAll);
            if (m == null) return "not found";
            try { m.Invoke(target, null); return null; }
            catch (TargetInvocationException e) { return e.InnerException == null ? e.Message : e.InnerException.GetType().Name; }
        }

        static string ComboCheckSituation()
        {
            Sim sim = Sim.ActiveActor;
            RabbitHole piece = FirstPiece<RabbitHole>();
            if (sim == null || piece == null) return "N/A (no active Sim or no combo piece)";
            if (AutographSessionSituation.GetAutographSessionSituation(sim) != null) return "N/A (the active Sim already holds a session)";
            AutographSessionSituation s = AutographSessionSituation.Create(sim, piece);
            Lot lot = s == null ? null : s.Lot;
            string at = s == null || s.RabbitHole == null ? "none" : s.RabbitHole == piece ? "the piece" : s.RabbitHole.GetType().Name;
            try { if (s != null) s.Exit(); } catch (Exception) { }  // EA's broken arm already Exit()ed in its first state
            sim.InteractionQueue.CancelAllInteractions();  // the walk to the session EA queues for the autographer
            return (lot != null ? "FIXED" : "BROKEN") + " (situation lot " + (lot == null ? "null" : "set") + ", at " + at + ")";
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
