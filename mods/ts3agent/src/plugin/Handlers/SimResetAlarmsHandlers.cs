using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI.Hud;

namespace Ts3Agent
{
    // sim-reset-alarms repro (ts3fixes SimResetAlarms.cs, simresetalarms; docs/352, docs/357): a Sim reset on the world lot
    // drops the Sim's vampire sunset / sunrise alarms, a reset on a normal lot keeps them. NPCs only: every op re-checks the
    // Sim (DtScope: never the active household, a selectable Sim or a Sim of unknown scope).
    //   sim_reset_alarms {op: "start", sim}: makes the Sim a vampire if it is none (EA's AddOccultType arms the two alarms).
    //   sim_reset_alarms {op: "place", sim, where: "lot" | "street"}: the standard setup_sims (Teleport Me Here, autonomy off)
    //     onto the Sim's home lot (else the far community lot), or onto a world-lot land spot near that lot.
    //   sim_reset_alarms {op: "reset", sim}: Sim.SetObjectToReset (EA's reset; it runs on the Sim's next tick).
    //   sim_reset_alarms {op: "status", sim}: lot kind, vampire_alarms = how many of the two alarm handles the global manager
    //     still knows (EA's GetTimeLeft, no ts3fixes code), and ts3fixes' reset counters since the last "reset" op.
    //   sim_reset_alarms {op: "cleanup"}: removes the vampire occult from every Sim "start" gave it to.
    public static partial class Handlers
    {
        static void RegisterSimResetAlarms()
        {
            Dispatcher.Register("sim_reset_alarms", SimResetAlarms);
        }

        static readonly List<ulong> sSrMade = new List<ulong>();  // Sims "start" made vampires
        static readonly int[] sSrBase = new int[3];               // ts3fixes [1828] [1829] [1830] at the last "reset" op

        static int[] SrCounters()
        {
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            return c != null && c.Length > 1830 ? new int[] { c[1828], c[1829], c[1830] } : new int[3];
        }

        static Lot SrLot(Sim s)
        {
            Lot lot = s.LotHome;
            if (lot == null || lot.IsWorldLot) lot = FindLotByName("far");
            if (lot == null || lot.IsWorldLot) throw new McpException("sim_reset_alarms: no normal lot for " + s.FullName);
            return lot;
        }

        // a world-lot land spot (no sea, no pond) near the lot, 20-200 m from its centre
        static Vector3 SrStreetSpot(Lot lot)
        {
            Vector3 c = lot.GetCenterPosition();
            for (float rad = 20f; rad <= 200f; rad += 10f)
                for (int i = 0; i < 16; i++)
                {
                    double ang = i * Math.PI / 8;
                    Vector3 p = new Vector3(c.x + rad * (float)Math.Cos(ang), 0f, c.z + rad * (float)Math.Sin(ang));
                    Lot at = LotManager.GetLotAtPoint(p);
                    if (at != null && !at.IsWorldLot) continue;
                    TerrainType t = (TerrainType)World.GetTerrainType(p.x, p.z, 0);
                    if (t != TerrainType.WorldTerrain && t != TerrainType.WorldRoad) continue;
                    p.y = World.GetTerrainHeight(p.x, p.z);
                    return p;
                }
            throw new McpException("no world-lot land spot within 200 m of " + lot.Name);
        }

        static int SrVampireAlarms(Sim s)
        {
            OccultVampire v = s.OccultManager == null ? null : s.OccultManager.GetOccultType(OccultTypes.Vampire) as OccultVampire;
            if (v == null) return -1;
            int n = 0;
            foreach (string f in new string[] { "mVampireAlarmNight", "mVampireAlarmDay" })
            {
                FieldInfo fi = typeof(OccultVampire).GetField(f, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null) throw new McpException("sim_reset_alarms: OccultVampire." + f + " not found");
                if (AlarmManager.Global.GetTimeLeft((AlarmHandle)fi.GetValue(v), TimeUnit.Minutes) > 0f) n++;
            }
            return n;
        }

        static object SimResetAlarms(Args a)
        {
            RequireLive();
            string op = a.Str("op", "status");
            Dictionary<string, object> r = Util.Obj();
            if (op == "cleanup")
            {
                List<object> undone = new List<object>();
                foreach (ulong id in sSrMade)
                {
                    SimDescription sd = SimDescription.Find(id);
                    if (sd == null || sd.OccultManager == null) continue;
                    sd.OccultManager.RemoveOccultType(OccultTypes.Vampire);
                    undone.Add(sd.FullName);
                }
                sSrMade.Clear();
                r["vampire_removed"] = undone;
                return r;
            }
            Sim s = Util.FindSim(a, "sim");
            string why = DtScope(s);
            if (why != null) throw new McpException("sim_reset_alarms: refused, " + s.FullName + " is " + why);
            if (op == "start")
            {
                if (!s.OccultManager.HasOccultType(OccultTypes.Vampire))
                {
                    if (!s.OccultManager.AddOccultType(OccultTypes.Vampire, false, false, false)) throw new McpException("sim_reset_alarms: the game refused the vampire occult for " + s.FullName);
                    sSrMade.Add(s.SimDescription.SimDescriptionId);
                }
            }
            else if (op == "place")
            {
                string where = a.Str("where");
                Lot lot = SrLot(s);
                Vector3 c = lot.GetCenterPosition(), spot = Vector3.Invalid;
                if (where == "lot") spot = WalkableClick(s, c);
                else if (where == "street") spot = SrStreetSpot(lot);
                else throw new McpException("sim_reset_alarms: where lot or street");
                r["setup"] = SetupSimsAt(new List<Sim> { s }, where == "lot" ? lot : null, spot, true, true, false, false);
                r["spot"] = (int)spot.x + "," + (int)spot.z;
            }
            else if (op == "reset")
            {
                int[] b = SrCounters();
                for (int i = 0; i < 3; i++) sSrBase[i] = b[i];
                s.SetObjectToReset();
            }
            else if (op != "status") throw new McpException("sim_reset_alarms: op start, place, reset, status or cleanup");
            int[] now = SrCounters();
            Lot cur = s.LotCurrent;
            r["sim"] = s.FullName;
            r["lot"] = cur != null ? cur.Name : null;
            r["world"] = cur != null && cur.IsWorldLot;
            r["on_lot"] = cur != null && !cur.IsWorldLot;
            r["vampire_alarms"] = SrVampireAlarms(s);
            r["resets_world"] = now[0] - sSrBase[0];
            r["resets_other"] = now[1] - sSrBase[1];
            r["kept"] = now[2] - sSrBase[2];
            return r;
        }
    }
}
