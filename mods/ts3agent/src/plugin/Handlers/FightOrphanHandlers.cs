using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // ip-fight-orphan repro (ts3fixes FightOrphan.cs, fightorphanwait): a snow / water fight Sim left in SnowWaterFightWait that
    // the fight no longer holds in PlayerSlots. EA's ControllerCallback then finds NumPlayers 0 with the Sim still on the use
    // list and returns every minute for good, so nothing ends that Wait. NPCs only: the first `count` pick_sims Sims, each
    // re-checked (DtScope: never the active household, a selectable Sim or a Sim of unknown scope).
    //   fight_orphan {op: "start", count?: 2, lot?, type?: "Waterballoon"}: the setup_sims steps (teleport, motives, autonomy off)
    //     on the beach community lot with the most IBeachObjects (EA's own beach rule, Lot kNumBeachObjectsToConstituteABeach) or
    //     on `lot`, then EA's PlayWith.Run path: CreateAndPlaceFightArea there and a JoinSingleton Wait queued on each Sim.
    //   fight_orphan {op: "orphan"}: once every picked Sim idles in the same fight's Wait with no exit reason, does what the
    //     inferred real cause does (IdleLoop's Move writing over a joiner's slot): clears picked:1's PlayerSlots entry, and cancels
    //     the others (EA's Wait tail: QuitFight frees their slots and use-list entries). done false = not settled yet (poll it).
    //   fight_orphan {op: "status"}: the picked Sims' interactions and slots, the fight's players / use list / alarm; orphaned =
    //     picked:1 in a Wait the fight does not hold, freed = picked:1 no longer in a Wait.
    public static partial class Handlers
    {
        static void RegisterFightOrphan()
        {
            Dispatcher.Register("fight_orphan", FightOrphan);
        }

        static readonly FieldInfo sFoSlots = typeof(SnowWaterFight).GetField("PlayerSlots", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static SnowWaterFight sFoFight;  // the fight "start" made
        static int sFoCount;             // picked Sims in it

        static object FightOrphan(Args a)
        {
            RequireLive();
            string op = a.Str("op", "status");
            if (sFoSlots == null) throw new McpException("fight_orphan: SnowWaterFight.PlayerSlots not found");
            if (op == "start") return FoStart(a);
            List<Sim> sims = FoSims(sFoCount);
            if (op == "orphan") return FoOrphan(sims);
            if (op == "status") return FoStatus(sims);
            throw new McpException("fight_orphan: op start, orphan or status");
        }

        static List<Sim> FoSims(int count)
        {
            if (count < 1) throw new McpException("fight_orphan: run op start first");
            if (Util.Picked.Count < count) throw new McpException("fight_orphan: " + Util.Picked.Count + " picked, needs " + count + " (pick_sims)");
            List<Sim> l = new List<Sim>();
            for (int i = 0; i < count; i++)
            {
                SimDescription sd = SimDescription.Find(Util.Picked[i]);
                if (sd == null || !Util.InWorldNow(sd)) throw new McpException("fight_orphan: picked:" + (i + 1) + " left the world");
                string why = DtScope(sd.CreatedSim);
                if (why != null) throw new McpException("fight_orphan: refused, " + sd.FullName + " is " + why);
                l.Add(sd.CreatedSim);
            }
            return l;
        }

        static object FoStart(Args a)
        {
            int count = a.Has("count") ? (int)a.Double("count") : 2;
            if (count < 2 || count > 4) throw new McpException("fight_orphan: count 2-4");
            List<Sim> sims = FoSims(count);
            Lot lot = a.Has("lot") ? FindLotByName(a.Str("lot")) : null;
            uint best = 0;
            if (lot == null)
                foreach (Lot l in LotManager.AllLots)
                {
                    if (l == null || l.IsWorldLot || !l.IsCommunityLot) continue;
                    uint n = l.CountObjects<IBeachObject>();
                    if (n > best) { best = n; lot = l; }
                }
            if (lot == null) throw new McpException("fight_orphan: no community lot with beach objects (pass lot)");
            Vector3 spot = lot.GetCenterPosition();
            foreach (IBeachObject b in lot.GetObjects<IBeachObject>())
            {
                GameObject g = b as GameObject;
                if (g != null && g.InWorld) { spot = g.Position; break; }
            }
            SnowWaterFight.BallFightType type = (SnowWaterFight.BallFightType)Enum.Parse(typeof(SnowWaterFight.BallFightType), a.Str("type", "Waterballoon"), true);
            List<object> setup = SetupSimsAt(sims, lot, spot, true, true, true, false);
            SnowWaterFight f = SnowWaterFight.CreateAndPlaceFightArea(spot, type);
            if (f == null) throw new McpException("fight_orphan: CreateAndPlaceFightArea found no place near " + spot + " on " + lot.Name);
            List<object> queued = new List<object>();
            foreach (Sim s in sims)
            {
                InteractionInstance w = SnowWaterFight.SnowWaterFightWait.JoinSingleton.CreateInstance(f, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                if (w == null || !s.InteractionQueue.Add(w)) { f.Destroy(); throw new McpException("fight_orphan: the game refused the fight Wait for " + s.FullName); }
                queued.Add(s.FullName);
            }
            sFoFight = f;
            sFoCount = count;
            Dictionary<string, object> r = Util.Obj();
            r["lot"] = lot.Name;
            r["beach_objects"] = (int)best;
            r["fight"] = Util.Id(f.ObjectId.Value);
            r["setup"] = setup;
            r["queued"] = queued;
            return r;
        }

        // the Sim's current Wait on f (null = none)
        static SnowWaterFight.SnowWaterFightWait FoWait(Sim s, SnowWaterFight f)
        {
            SnowWaterFight.SnowWaterFightWait w = s.InteractionQueue == null ? null : s.InteractionQueue.GetCurrentInteraction() as SnowWaterFight.SnowWaterFightWait;
            return w != null && (f == null || w.Target == f) ? w : null;
        }

        static object FoOrphan(List<Sim> sims)
        {
            SnowWaterFight f = sFoFight;
            Dictionary<string, object> r = Util.Obj();
            Sim o = sims[0];
            Sim[] slots = f == null || f.HasBeenDestroyed ? null : sFoSlots.GetValue(f) as Sim[];
            if (slots == null) throw new McpException("fight_orphan: the fight is gone (" + (f == null ? "never started" : "destroyed") + ")");
            int at = Array.IndexOf(slots, o);
            if (FoWait(o, f) != null && at < 0) { r["done"] = true; r["state"] = "already orphaned"; return r; }
            foreach (Sim s in sims)
            {
                // settled: in this fight's Wait, no exit reason (no throw being handed out), alone in its queue, in a slot
                if (FoWait(s, f) == null || s.HasExitReason() || s.InteractionQueue.Count != 1 || Array.IndexOf(slots, s) < 0)
                {
                    r["done"] = false;
                    r["state"] = s.FullName + " not settled: " + FoCur(s) + (s.HasExitReason() ? " (exit reason)" : "") + ", queue " + s.InteractionQueue.Count + (Array.IndexOf(slots, s) < 0 ? ", no slot" : "");
                    return r;
                }
            }
            slots[at] = null;
            for (int i = 1; i < sims.Count; i++) sims[i].InteractionQueue.CancelAllInteractions();
            r["done"] = true;
            r["state"] = o.FullName + " cleared from slot " + at + ", " + (sims.Count - 1) + " other(s) cancelled";
            return r;
        }

        static string FoCur(Sim s)
        {
            InteractionInstance c = s.InteractionQueue == null ? null : s.InteractionQueue.GetCurrentInteraction();
            return c == null ? "idle" : c.GetType().Name;
        }

        static object FoStatus(List<Sim> sims)
        {
            SnowWaterFight f = sFoFight;
            Sim[] slots = f == null || f.HasBeenDestroyed ? null : sFoSlots.GetValue(f) as Sim[];
            Dictionary<string, object> r = Util.Obj();
            List<object> l = new List<object>();
            foreach (Sim s in sims) l.Add(s.FullName + ": " + FoCur(s) + (slots != null && Array.IndexOf(slots, s) >= 0 ? ", in a slot" : ", no slot"));
            r["sims"] = l;
            r["fight_destroyed"] = f == null || f.HasBeenDestroyed;
            r["num_players"] = slots == null ? 0 : f.NumPlayers;
            r["alarm"] = f != null && f.ControllerRepeatingAlarm != AlarmHandle.kInvalidHandle;
            bool inWait = FoWait(sims[0], null) != null;
            r["orphaned"] = inWait && (slots == null || Array.IndexOf(slots, sims[0]) < 0);
            r["freed"] = !inWait;
            bool othersOut = true;
            for (int i = 1; i < sims.Count; i++) if (FoWait(sims[i], null) != null) othersOut = false;
            r["others_out"] = othersOut;
            return r;
        }
    }
}
