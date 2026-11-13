using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Stress repro for docs/257 §9 (ts3fixes boatgetoutguard, diag boatgetoutdiag): EA's Boat.GetOut checks the SittingInBoat
    // posture, clears RoutingParent, sleeps in FadeOut, then GetOutIntoWater reads (a.Posture as SittingInBoat).SeatSlot
    // unchecked. A posture writer on another task inside that fade gives the NullReferenceException at GetOutIntoWater+0x1d.
    // This loop puts each picked Sim in its own boat (teleport to the shore + the game's Board Boat, player-directed), forces
    // Get Out Into Water, and in the fade window (still SittingInBoat of its boat, RoutingParent already null) forces a writer on
    // every `every`-th window: boatreset (boat.SetObjectToReset: the reset cascade runs SittingInBoat.OnReset -> GetOut(bOnReset)
    // -> Standing, EA's suspected path), simreset (Sim.SetObjectToReset), standing (Posture = Standing directly), or mix (rotates).
    // Then Board Boat again from the water, and so on, until `iterations` windows or the first hit (stopOn): stale = ts3fixes
    // counter [947] (an NRE out of a watched GetOut, needs boatgetoutdiag on), guard = [953] (swallowed for an NPC) and, with a
    // household Sim in the loop, [954] (kept by the household rule). NPC picks follow the household rule (known non-active
    // household, not selectable or selected); the household Sim is the active Sim, on purpose, to show EA behaviour stays for it:
    // it is forced once, with the standing writer (deterministic), so arm B expects exactly its one GetOutIntoWater script error.
    public static partial class Handlers
    {
        static void RegisterBoatGetOutStress()
        {
            Dispatcher.Register("boat_getout_stress", BoatGetOutStress);
        }

        class BgsSim { public Sim S; public BoatOwnable Boat; public Vector3 Sea, Shore; public bool Npc, InWindow; public long LastPush; public int Windows, Forced, StaleAt = -1; public string Writer; }

        static int sBgsGen, sBgsWindows, sBgsForced, sBgsTicks, sBgsMade, sBgsBoatFail;
        static int[] sBgsBase = new int[3];
        static string sBgsStop, sBgsLast;
        static List<BgsSim> sBgsSims = new List<BgsSim>();
        static List<object> sBgsRows = new List<object>();
        // standing first: the deterministic writer proves the repro at once; the reset writers follow (which EA path writes it)
        static readonly string[] kBgsWriters = { "standing", "boatreset", "simreset" };
        static Dictionary<string, int> sBgsByWriter = new Dictionary<string, int>();

        static int[] BgsCounters() { return AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[]; }
        static int BgsDelta(int slot, int k) { int[] c = BgsCounters(); return c != null && c.Length > slot ? c[slot] - sBgsBase[k] : 0; }

        // boat_getout_stress {start: true, sims?: 10, household?: true, iterations?: 40, every?: 2, writer?: "mix", stopOn?: "auto" | "stale" | "guard" | "none"}
        //   | {stop: true} | {result: true, require?: true, expect?: "auto" | "stale" | "guard"}; auto = guard with boatgetoutguard on, else stale
        static object BoatGetOutStress(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("start"))
            {
                int gen = ++sBgsGen, iterations = (int)a.Long("iterations", 40), every = Math.Max(1, (int)a.Long("every", 2));
                string writer = a.Str("writer", "mix"), stopOn = BgsAuto(a.Str("stopOn", "auto"));
                if (writer != "mix" && Array.IndexOf(kBgsWriters, writer) < 0) throw new McpException("writer must be boatreset, simreset, standing or mix");
                BgsSetup((int)a.Long("sims", 10), a.Bool("household", true));
                int[] c = BgsCounters();
                for (int k = 0; k < 3; k++) sBgsBase[k] = c != null && c.Length > 954 ? c[k == 0 ? 947 : 952 + k] : 0;
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    while (gen == sBgsGen && sBgsStop == null)
                    {
                        try { BgsTick(writer, every, iterations, stopOn); } catch (Exception e) { sBgsLast = "tick error: " + e.GetType().Name; }
                        Simulator.Sleep(1);
                    }
                }));
                r["started"] = true;
                r["sims"] = BgsNames();
                r["boats_failed"] = sBgsBoatFail;
                return r;
            }
            if (a.Has("stop")) { sBgsGen++; BgsCleanup(); r["stopped"] = true; }
            int stale = BgsDelta(947, 0), hit = BgsDelta(953, 1), hh = BgsDelta(954, 2);
            r["windows"] = sBgsWindows; r["forced"] = sBgsForced; r["boats_made"] = sBgsMade; r["boats_failed"] = sBgsBoatFail;
            r["stale"] = stale; r["guard_hit"] = hit; r["guard_household"] = hh;
            r["stop_reason"] = sBgsStop; r["last"] = sBgsLast; r["sims"] = BgsNames(); r["rows"] = sBgsRows;
            BgsStateLines();  // the progress rows are cut in the job log: per-Sim state goes to ts3native.log
            bool hhForced = false, hhIn = false;
            foreach (BgsSim b in sBgsSims) if (!b.Npc) { hhIn = true; if (b.Forced > 0) hhForced = true; }
            string expect = BgsAuto(a.Str("expect", "auto"));
            string bad = sBgsForced == 0 ? "no writer forced in a GetOut fade window (not exercised)"
                : expect == "stale" ? (stale < 1 ? "no stale NRE out of GetOut (repro not proven)" : null)
                : hit < 1 ? "guard never swallowed an NPC NRE"
                : stale != hit + hh ? "stale NREs " + stale + " != guard hit " + hit + " + household " + hh + " (an NRE the guard did not decide)"
                : hhIn && !hhForced ? "household Sim never reached a GetOut window (EA behaviour not shown)"
                : hhForced && hh < 1 ? "household Sim forced but no household NRE kept (EA behaviour not shown)" : null;
            r["check_ok"] = bad == null;
            if (a.Bool("require", false) && bad != null) throw new McpException("boat_getout_stress: " + bad + " (windows " + sBgsWindows + ", forced " + sBgsForced + ")");
            return r;
        }

        // "auto": guard when ts3fixes says boatgetoutguard is on (arm B), else stale (arm A)
        static string BgsAuto(string v) { return v != "auto" ? v : ModOn("boatgetoutguard") == true ? "guard" : "stale"; }

        static void BgsStateLines()
        {
            foreach (BgsSim b in sBgsSims)
                try
                {
                    Sim s = b.S;
                    DiagLine("boat_getout_stress: state " + s.FullName + (b.Npc ? " npc" : " household") + ": windows " + b.Windows + ", forced " + b.Forced
                        + ", posture " + (s.Posture != null ? s.Posture.GetType().Name : "null") + ", current " + (s.CurrentInteraction != null ? s.CurrentInteraction.GetType().FullName : "none")
                        + ", queue " + s.InteractionQueue.Count + ", boat " + (b.Boat == null ? "none" : b.Boat.HasBeenDestroyed ? "destroyed" : ((int)(s.Position - b.Boat.Position).Length()) + " m"));
                }
                catch (Exception e) { DiagLine("boat_getout_stress: state " + b.S.FullName + ": " + e.GetType().Name); }
        }

        static List<object> BgsNames()
        {
            List<object> n = new List<object>();
            foreach (BgsSim b in sBgsSims) n.Add(b.S.FullName + (b.Npc ? "" : " (household)") + (b.Boat != null ? " " + b.Boat.GetType().Name : " no boat"));
            return n;
        }

        // household rule for the NPC picks: a known, non-active household; not selectable or selected
        static bool BgsNpc(Sim s)
        {
            return s != null && !s.IsSelectable && s != PlumbBob.SelectedActor && s.Household != null && s.Household != Household.ActiveHousehold;
        }

        static void BgsSetup(int n, bool household)
        {
            BgsCleanup();
            sBgsSims = new List<BgsSim>(); sBgsRows = new List<object>();
            sBgsWindows = sBgsForced = sBgsTicks = sBgsMade = sBgsBoatFail = 0; sBgsStop = sBgsLast = null;
            sBgsByWriter = new Dictionary<string, int>();
            Sim me = Util.ActiveSim();
            Vector3 shore, dir;
            if (!BgsShore(me.Position, out shore, out dir)) throw new McpException("boat_getout_stress: no sea within 400 m of " + me.FullName);
            List<Sim> picks = new List<Sim>();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (picks.Count >= n) break;
                if (!BgsNpc(s) || s.HasBeenDestroyed || !s.InWorld || s.LotHome == null || s.Posture is SittingInVehicle || s.Posture is SittingInBoat) continue;
                if (!s.SimDescription.IsHuman || !s.SimDescription.TeenOrAbove || s.SimDescription.IsPregnant) continue;
                picks.Add(s);
            }
            if (picks.Count == 0) throw new McpException("boat_getout_stress: no NPC fits (known non-active household, teen or older, home lot)");
            if (household && me.LotHome != null) picks.Add(me);
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            for (int i = 0; i < picks.Count; i++)
            {
                BgsSim b = new BgsSim();
                b.S = picks[i]; b.Npc = BgsNpc(picks[i]);
                float off = 6f * (i - (picks.Count - 1) / 2f);
                b.Shore = shore + side * off;
                b.Sea = shore + dir * 12f + side * off;
                sBgsSims.Add(b);
                BgsBoat(b);
            }
            // AGENTS.md "Repro Sim setup": cancel, teleport, motives, autonomy off; each Sim on the land spot next to its own boat
            foreach (BgsSim b in sBgsSims) SetupSimsAt(new List<Sim> { b.S }, null, b.Shore, true, true, true, false);
        }

        // the first land point (from `from` outwards) whose next 10 and 20 m are sea
        static bool BgsShore(Vector3 from, out Vector3 shore, out Vector3 dir)
        {
            for (float rad = 0f; rad <= 400f; rad += 5f)
                for (int i = 0; i < 24; i++)
                {
                    double ang = i * Math.PI / 12;
                    dir = new Vector3((float)Math.Cos(ang), 0f, (float)Math.Sin(ang));
                    Vector3 p = from + dir * rad, s1 = p + dir * 10f, s2 = p + dir * 20f;
                    if ((TerrainType)World.GetTerrainType(p.x, p.z, 0) == TerrainType.WorldSea) continue;
                    if ((TerrainType)World.GetTerrainType(s1.x, s1.z, 0) != TerrainType.WorldSea || (TerrainType)World.GetTerrainType(s2.x, s2.z, 0) != TerrainType.WorldSea) continue;
                    shore = p; shore.y = World.GetTerrainHeight(p.x, p.z);
                    return true;
                }
            shore = dir = Vector3.Invalid;
            return false;
        }

        // the Sim's own boat on the water in front of its shore spot, owned by its home lot (Board Boat's test)
        static void BgsBoat(BgsSim b)
        {
            BoatOwnable boat = GlobalFunctions.CreateObjectOutOfWorld("BoatSpeedBoat", ProductVersion.EP10) as BoatOwnable;
            if (boat == null) { sBgsBoatFail++; return; }
            boat.SetOwnerLot(b.S.LotHome);
            boat.LotHome = b.S.LotHome;
            Vector3 pos = b.Sea, fwd = Vector3.UnitZ;
            if (!GlobalFunctions.FindGoodLocationNearby(boat, ref pos, ref fwd)) { boat.Destroy(); sBgsBoatFail++; return; }
            boat.SetPosition(pos); boat.SetForward(fwd); boat.AddToWorld();
            b.Boat = boat;
            sBgsMade++;
            // FindGoodLocationNearby can put the boat 100+ m along the coast (run 2: Sims never reached it): land next to the boat
            Vector3 land;
            if (BgsLandNear(pos, b.Shore, out land)) b.Shore = land;
            DiagLine("boat_getout_stress: boat for " + b.S.FullName + " " + (int)(pos - b.Sea).Length() + " m from the planned spot, shore " + (int)(b.Shore - pos).Length() + " m from it");
        }

        // the first land point on the line from the boat towards `toward` (2 m steps, 150 m at most)
        static bool BgsLandNear(Vector3 boat, Vector3 toward, out Vector3 land)
        {
            Vector3 d = toward - boat; d.y = 0f;
            float len = d.Length();
            land = Vector3.Invalid;
            if (len < 0.1f) return false;
            d = d * (1f / len);
            for (float t = 2f; t <= 150f; t += 2f)
            {
                Vector3 p = boat + d * t;
                if ((TerrainType)World.GetTerrainType(p.x, p.z, 0) == TerrainType.WorldSea) continue;
                p = p + d * 2f;  // a step inland, off the waterline
                p.y = World.GetTerrainHeight(p.x, p.z);
                land = p;
                return true;
            }
            return false;
        }

        static void BgsCleanup()
        {
            foreach (BgsSim b in sBgsSims)
            {
                try
                {
                    if (b.Boat == null || b.Boat.HasBeenDestroyed) continue;
                    SittingInBoat sib = b.S.Posture as SittingInBoat;
                    if (sib != null && sib.Container == b.Boat) continue;  // still aboard: leave it to the game
                    b.Boat.Destroy();
                }
                catch (Exception) { }
            }
        }

        static void BgsTick(string writer, int every, int iterations, string stopOn)
        {
            sBgsTicks++;
            foreach (BgsSim b in sBgsSims)
            {
                Sim s = b.S;
                if (s == null || s.HasBeenDestroyed) { b.Writer = null; continue; }
                if (b.Boat == null || b.Boat.HasBeenDestroyed) { BgsBoat(b); continue; }
                SittingInBoat sib = s.Posture as SittingInBoat;
                bool aboard = sib != null && sib.Container == b.Boat;
                bool window = aboard && s.SimRoutingComponent != null && s.SimRoutingComponent.RoutingParent == null && s.CurrentInteraction is Boat.GetOutOfBoat;
                if (window && !b.InWindow)
                {
                    b.InWindow = true; b.Windows++; sBgsWindows++; b.Writer = null;
                    DiagLine("boat_getout_stress: window " + sBgsWindows + " " + s.FullName + (b.Npc ? " npc" : " household") + " (its #" + b.Windows + ")");
                    if (!b.Npc) { if (b.Forced == 0) BgsForce(b, "standing"); }  // household: once, the deterministic writer (EA's error must stay)
                    else if (b.Windows % every == 0) BgsForce(b, writer == "mix" ? kBgsWriters[BgsNpcForced() % kBgsWriters.Length] : writer);
                    continue;
                }
                if (!window && b.Writer != null && !(s.CurrentInteraction is Boat.GetOutOfBoat))  // the forced GetOut ended: did that writer give the NRE?
                    DiagLine("boat_getout_stress: outcome " + b.Writer + " on " + s.FullName + (b.Npc ? " npc" : " household") + ": stale +" + (BgsDelta(947, 0) - b.StaleAt)
                        + ", guard hit " + BgsDelta(953, 1) + ", household kept " + BgsDelta(954, 2) + ", posture " + (s.Posture != null ? s.Posture.GetType().Name : "null"));
                if (!window && b.Writer != null && !(s.CurrentInteraction is Boat.GetOutOfBoat)) b.Writer = null;
                if (!window) b.InWindow = false;
                // aboard, an idle boat interaction can hold the Sim for good: Get Out Into Water is pushed anyway (user-directed cancels it)
                bool busy = s.CurrentInteraction != null && !(aboard && !(s.CurrentInteraction is Boat.GetOutOfBoat) && s.InteractionQueue.Count <= 1);
                if (window || busy || (!aboard && s.InteractionQueue.Count > 0) || sBgsTicks - b.LastPush < 30) continue;
                b.LastPush = sBgsTicks;
                InteractionPriority prio = new InteractionPriority(InteractionPriorityLevel.UserDirected);
                if (aboard) s.InteractionQueue.Add(Boat.GetOutOfBoat.GetOutIntoWaterSingleton.CreateInstance(b.Boat, s, prio, false, true));
                else if ((s.Position - b.Boat.Position).LengthSqr() > 40f * 40f)
                    SetupSimsAt(new List<Sim> { s }, null, b.Shore, true, false, false, true);  // reset sent it away: back to the shore
                else s.InteractionQueue.Add(Boat.BoardBoat.Singleton.CreateInstance(b.Boat, s, prio, false, true));
            }
            int stale = BgsDelta(947, 0), hit = BgsDelta(953, 1), hh = BgsDelta(954, 2);
            bool hhIn = false, hhForced = false;
            bool pending = false;  // a forced GetOut still running: its outcome line comes first
            foreach (BgsSim b in sBgsSims) { if (b.Writer != null) pending = true; if (!b.Npc) { hhIn = true; if (b.Forced > 0) hhForced = true; } }
            bool allWriters = writer != "mix";  // mix: go on until each writer was forced on an NPC once, to tell which ones give the NRE
            if (!allWriters) { allWriters = true; foreach (string w in kBgsWriters) if (!sBgsByWriter.ContainsKey(w)) allWriters = false; }
            if (pending && sBgsWindows < iterations) { }
            else if (stopOn == "stale" && stale > 0 && allWriters) sBgsStop = "first stale NRE" + (writer == "mix" ? ", every writer forced" : "");
            else if (stopOn == "guard" && hit > 0 && (!hhIn || (hhForced && hh > 0))) sBgsStop = "first guard hit" + (hhIn ? " + household kept" : "");
            else if (sBgsWindows >= iterations) sBgsStop = "iterations done";
            if (sBgsStop != null)
                DiagLine("boat_getout_stress: stop (" + sBgsStop + ") after " + sBgsWindows + " windows, " + sBgsForced + " forced: stale " + stale + ", guard hit " + hit + ", household kept " + hh + "; last forced " + sBgsLast);
        }

        static int BgsNpcForced() { int n = 0; foreach (int v in sBgsByWriter.Values) n += v; return n; }

        static void BgsForce(BgsSim b, string writer)
        {
            sBgsForced++; b.Forced++; b.Writer = writer; b.StaleAt = BgsDelta(947, 0);
            if (b.Npc) { int n; sBgsByWriter.TryGetValue(writer, out n); sBgsByWriter[writer] = n + 1; }
            sBgsLast = writer + " on " + b.S.FullName + (b.Npc ? " npc" : " household") + " in window " + sBgsWindows;
            Dictionary<string, object> o = Util.Obj();
            o["sim"] = b.S.FullName; o["npc"] = b.Npc; o["writer"] = writer; o["window"] = sBgsWindows; o["boat"] = b.Boat.GetType().Name;
            DiagLine("boat_getout_stress: forced " + sBgsLast);
            try
            {
                if (writer == "boatreset") b.Boat.SetObjectToReset();
                else if (writer == "simreset") b.S.SetObjectToReset();
                else b.S.Posture = b.S.Standing;
                o["posture_after"] = b.S.Posture != null ? b.S.Posture.GetType().Name : null;
            }
            catch (Exception e) { o["error"] = e.GetType().Name + ": " + e.Message; }
            DiagLine("boat_getout_stress: after " + writer + ": posture " + (o.ContainsKey("posture_after") ? o["posture_after"] : "?") + (o.ContainsKey("error") ? ", error " + o["error"] : ""));
            sBgsRows.Add(o);
        }
    }
}
