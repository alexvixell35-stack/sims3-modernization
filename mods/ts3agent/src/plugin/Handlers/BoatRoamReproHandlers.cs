using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Repro for ts3fixes boatroamcap (docs/853 §4, BoatRoamCap.cs): EA's Boat.BoatAround loop of an NPC driver on a world lot,
    // with no exit of its own. Picks NPCs as setup_sims (known non-active household, not selectable or selected, teen or older
    // human, home lot), gives each its own speed boat on the sea off the nearest shore (as boat_getout_stress: owned by its home
    // lot, BoatOwnable, so EA's ReturnToShore can run), sets it on the shore (AGENTS.md "Repro Sim setup", autonomy off), then
    // EA's Board Boat (player-directed, as driver). While aboard and idle, BoatAround is pushed at autonomous priority, as
    // autonomy picks it in play (EA's GoBoating needs a map-view click, so it cannot be pushed from script). It watches `seconds`
    // (300): per Sim the BoatAround runs seen, ReturnToShore runs seen, whether it got out of the boat, and the 1392-1399 deltas.
    // OFF: BoatAround runs on and on, never ashore. ON: after kBoatRoamCapRoams (3) roams (or at night) a ReturnToShore follows
    // and the Sim gets out; 'boatroamcap: <name> back to shore after N roams / H sim-h' in ts3native.log.
    public static partial class Handlers
    {
        static void RegisterBoatRoamRepro()
        {
            Dispatcher.Register("boat_roam_repro", BoatRoamRepro);
        }

        class BrrSim { public BgsSim B; public int Roams, Returns, Boards; public bool WasAboard, Ashore; public object LastIa; public string Error; }

        static int sBrrGen;
        static bool sBrrDone;
        static string sBrrError;
        static List<BrrSim> sBrrSims = new List<BrrSim>();
        static readonly string[] kBrrCtr = { "capped", "returns_pushed", "returns_failed", "exempt", "errors", "shore_reached", "service_logged", "night_or_motive" };
        static readonly int[] sBrrBase = new int[8];

        static int BrrCtr(int k) { int[] c = BgsCounters(); return c != null && c.Length > 1399 ? c[1392 + k] : -1; }

        // boat_roam_repro {start: true, sims?: 1, seconds?: 300} | {result: true} | {stop: true}
        static object BoatRoamRepro(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("start"))
            {
                int gen = ++sBrrGen, seconds = Math.Max(10, (int)a.Long("seconds", 300));
                BrrSetup(Math.Max(1, (int)a.Long("sims", 1)));
                sBrrDone = false; sBrrError = null;
                for (int k = 0; k < 8; k++) sBrrBase[k] = BrrCtr(k);
                DiagLine("boat_roam_repro: start, " + sBrrSims.Count + " sims, " + seconds + " s, boatroamcap " + ModOn("boatroamcap"));
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    int t0 = System.Environment.TickCount;
                    try
                    {
                        while (gen == sBrrGen && System.Environment.TickCount - t0 < seconds * 1000)
                        {
                            BrrTick();
                            Simulator.Sleep(1);
                        }
                    }
                    catch (Exception e) { sBrrError = e.GetType().Name + ": " + e.Message; DiagLine("boat_roam_repro: error " + sBrrError); }
                    finally
                    {
                        if (gen == sBrrGen)
                        {
                            sBrrDone = true;
                            foreach (BrrSim s in sBrrSims)
                                DiagLine("boat_roam_repro: " + s.B.S.FullName + " " + s.Roams + " BoatAround runs, " + s.Returns + " ReturnToShore, ashore " + s.Ashore);
                        }
                    }
                }));
                r["started"] = true;
                List<object> names = new List<object>();
                foreach (BrrSim s in sBrrSims) names.Add(s.B.S.FullName + (s.B.Boat != null ? " " + s.B.Boat.GetType().Name : " no boat"));
                r["sims"] = names;
                return r;
            }
            if (a.Has("stop")) { sBrrGen++; r["stopped"] = true; return r; }
            Dictionary<string, object> ctr = Util.Obj();
            for (int k = 0; k < 8; k++) { int v = BrrCtr(k); ctr[kBrrCtr[k] + "_" + (1392 + k)] = v < 0 ? -1 : v - sBrrBase[k]; }
            r["done"] = sBrrDone; r["error"] = sBrrError; r["counters"] = ctr; r["boatroamcap"] = ModOn("boatroamcap");
            List<object> rows = new List<object>();
            foreach (BrrSim s in sBrrSims)
            {
                Dictionary<string, object> o = Util.Obj();
                Sim sim = s.B.S;
                o["sim"] = sim.FullName; o["boat"] = s.B.Boat != null ? s.B.Boat.GetType().Name : null;
                o["roams"] = s.Roams; o["returns"] = s.Returns; o["boards"] = s.Boards; o["ashore"] = s.Ashore; o["error"] = s.Error;
                o["posture"] = sim.Posture != null ? sim.Posture.GetType().Name : null;
                o["current"] = sim.CurrentInteraction != null ? sim.CurrentInteraction.GetType().Name : null;
                rows.Add(o);
            }
            r["sims"] = rows;
            return r;
        }

        static void BrrSetup(int n)
        {
            sBrrSims = new List<BrrSim>();
            Sim me = Util.ActiveSim();
            Vector3 shore, dir;
            if (!BgsShore(me.Position, out shore, out dir)) throw new McpException("boat_roam_repro: no sea within 400 m of " + me.FullName);
            List<Sim> picks = new List<Sim>();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (picks.Count >= n) break;
                if (!BgsNpc(s) || s.HasBeenDestroyed || !s.InWorld || s.LotHome == null || s.Posture is SittingInVehicle || s.Posture is SittingInBoat) continue;
                if (!s.SimDescription.IsHuman || !s.SimDescription.TeenOrAbove || s.SimDescription.IsPregnant) continue;
                picks.Add(s);
            }
            if (picks.Count == 0) throw new McpException("boat_roam_repro: no NPC fits (known non-active household, teen or older, home lot)");
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            for (int i = 0; i < picks.Count; i++)
            {
                BgsSim b = new BgsSim();
                b.S = picks[i]; b.Npc = true;
                float off = 8f * (i - (picks.Count - 1) / 2f);
                b.Shore = shore + side * off;
                b.Sea = shore + dir * 14f + side * off;
                BgsBoat(b);
                BrrSim w = new BrrSim(); w.B = b;
                if (b.Boat == null) w.Error = "no boat placed";
                sBrrSims.Add(w);
            }
            foreach (BrrSim w in sBrrSims) SetupSimsAt(new List<Sim> { w.B.S }, null, w.B.Shore, true, true, true, false);
        }

        static void BrrTick()
        {
            foreach (BrrSim w in sBrrSims)
            {
                Sim s = w.B.S;
                BoatOwnable boat = w.B.Boat;
                if (s == null || s.HasBeenDestroyed || boat == null || boat.HasBeenDestroyed || w.Ashore) continue;
                SittingInBoat sib = s.Posture as SittingInBoat;
                bool aboard = sib != null && sib.Container == boat;
                InteractionInstance cur = s.CurrentInteraction;
                if (cur != null && cur != w.LastIa)
                {
                    w.LastIa = cur;
                    if (cur is Boat.BoatAround) w.Roams++;
                    else if (cur is Boat.ReturnToShore) { w.Returns++; DiagLine("boat_roam_repro: " + s.FullName + " ReturnToShore after " + w.Roams + " BoatAround runs"); }
                }
                if (aboard) w.WasAboard = true;
                else if (w.WasAboard && cur == null && s.InteractionQueue.Count == 0)
                {
                    w.Ashore = true;  // out of the boat after being aboard: the loop ended
                    DiagLine("boat_roam_repro: " + s.FullName + " ashore after " + w.Roams + " BoatAround runs, " + w.Returns + " ReturnToShore");
                    continue;
                }
                if (cur != null || s.InteractionQueue.Count > 0) continue;
                if (aboard)  // idle at the helm: what autonomy picks in play
                    s.InteractionQueue.Add(Boat.BoatAround.Singleton.CreateInstance(boat, s, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, true));
                else if (!w.WasAboard && w.Boards < 5)
                {
                    w.Boards++;
                    s.InteractionQueue.Add(Boat.BoardBoat.Singleton.CreateInstance(boat, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true));
                }
            }
        }
    }
}
