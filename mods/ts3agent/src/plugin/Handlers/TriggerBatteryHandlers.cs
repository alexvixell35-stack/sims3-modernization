using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.UI.Controller;
using GameSpeed = Sims3.SimIFace.Gameflow.GameSpeed;
using GF = Sims3.Gameplay.Gameflow;

namespace Ts3Agent
{
    // trigger_battery {triggers?: [names], on?: true, keep_on?: false} | {status: true} | {stop: true} (docs/653):
    // runs cheap live triggers for ts3fixes fixes that never fired in a test run, back to back in one task, each a few
    // seconds to a minute, and reports the counter deltas per trigger. Every trigger sets up the bug's own precondition
    // with EA's API and lets EA's hooked method run, so the fix's live hook (not a pure check) counts.
    //   speedrecalc   speedrecalclock   Pause lock held, Gameflow.RecalculateGameSpeed            [3411-3413]
    //   speedlock     speedlockowner    Pause lock held, build mode entered and left             [3414-3416]
    //   speedskip     speedskiplevel    Skip asked while the active Sim has no head interaction  [3405-3407]
    //   heartbroken   heartbrokenbcast  AddHeartBroken twice on an NPC couple                    [3421-3423]
    //   doorlock      doorlockowner     NPC door locked to a resident, resident removed + re-added [1147-1151]
    //   elevator      elevatorridertimeout, elevatorresetriders: NPC rides an elevator up, the
    //                 elevator is reset while the NPC waits inside                                [1155-1157, 1171-1173]
    //   inuse         resetinusefree    item marked in use in an NPC inventory, NPC reset        [2689-2690]
    //   putdown       putdowninuse      NPC put-down from inventory, NPC reset once the item is in use [2691-2692]
    //   gohome        gohomewalk        NPC sent home with WalkStyle Run                          [2477-2478]
    // Household rule: only uncontrolled NPCs (DrNpc), preferring the Sim farthest from the camera; speed and build
    // mode triggers change no Sim. Switches: the trigger's switches are set on in "ts3fixes.switches" (as `patches`
    // does) unless on: false, and put back after the run unless keep_on: true. Returns at once; status has the rows.
    public static partial class Handlers
    {
        static int sTbGen;
        static bool sTbRunning;
        static string sTbNow = "";
        static readonly List<object> sTbRows = new List<object>();
        static Dictionary<string, object> sTbTotals = Util.Obj();
        const BindingFlags kTbAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        delegate string TbRunFn(Dictionary<string, object> row);
        delegate bool TbCond();

        class TbTrigger
        {
            public string Name; public string[] Switches; public int[] Slots; public TbRunFn Run;
            public TbTrigger(string n, string[] sw, int[] slots, TbRunFn run) { Name = n; Switches = sw; Slots = slots; Run = run; }
        }

        static void RegisterTriggerBattery() { Dispatcher.Register("trigger_battery", TriggerBattery); }

        static List<TbTrigger> TbAll()
        {
            List<TbTrigger> l = new List<TbTrigger>();
            l.Add(new TbTrigger("speedrecalc", new[] { "speedrecalclock" }, Rng(3411, 3413), TbSpeedRecalc));
            l.Add(new TbTrigger("speedskip", new[] { "speedskiplevel" }, Rng(3405, 3407), TbSpeedSkip));
            l.Add(new TbTrigger("heartbroken", new[] { "heartbrokenbcast" }, Rng(3421, 3423), TbHeartBroken));
            l.Add(new TbTrigger("doorlock", new[] { "doorlockowner" }, Rng(1147, 1151), TbDoorLock));
            l.Add(new TbTrigger("inuse", new[] { "resetinusefree" }, Rng(2689, 2690), TbInUse));
            l.Add(new TbTrigger("putdown", new[] { "putdowninuse" }, Rng(2691, 2692), TbPutDown));
            l.Add(new TbTrigger("gohome", new[] { "gohomewalk" }, Rng(2477, 2478), TbGoHome));
            l.Add(new TbTrigger("elevator", new[] { "elevatorridertimeout", "elevatorresetriders" }, Cat(Rng(1155, 1157), Rng(1171, 1173)), TbElevator));
            l.Add(new TbTrigger("speedlock", new[] { "speedlockowner" }, Rng(3414, 3416), TbSpeedLock));  // last: it enters build mode
            return l;
        }

        static int[] Rng(int a, int b) { int[] r = new int[b - a + 1]; for (int i = 0; i < r.Length; i++) r[i] = a + i; return r; }
        static int[] Cat(int[] x, int[] y) { int[] r = new int[x.Length + y.Length]; x.CopyTo(r, 0); y.CopyTo(r, x.Length); return r; }

        static int[] TbCounters() { return AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[]; }

        static Dictionary<string, object> TbStatus()
        {
            Dictionary<string, object> r = Util.Obj();
            r["running"] = sTbRunning;
            r["now"] = sTbNow;
            lock (sTbRows) r["rows"] = new List<object>(sTbRows);
            r["fired"] = sTbTotals;
            return r;
        }

        static object TriggerBattery(Args a)
        {
            if (a.Bool("status", false)) return TbStatus();
            if (a.Bool("stop", false)) { sTbGen++; return TbStatus(); }
            RequireLive();
            if (sTbRunning) throw new McpException("trigger_battery already running (stop: true first)");
            if (TbCounters() == null) throw new McpException("ts3fixes is not loaded (no ts3fixes.counters)");
            List<TbTrigger> all = TbAll(), pick = new List<TbTrigger>();
            List<object> want = a.List("triggers");
            if (want == null || want.Count == 0) pick = all;
            else
                foreach (object w in want)
                {
                    TbTrigger t = all.Find(delegate(TbTrigger x) { return x.Name == Convert.ToString(w); });
                    if (t == null) throw new McpException("unknown trigger '" + w + "' (" + string.Join(" ", all.ConvertAll(delegate(TbTrigger x) { return x.Name; }).ToArray()) + ")");
                    pick.Add(t);
                }
            bool on = a.Bool("on", true), keep = a.Bool("keep_on", false);
            int gen = ++sTbGen;
            lock (sTbRows) sTbRows.Clear();
            sTbTotals = Util.Obj();
            sTbRunning = true;
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                Dictionary<string, bool> sw = AppDomain.CurrentDomain.GetData("ts3fixes.switches") as Dictionary<string, bool>;
                if (sw == null) { sw = new Dictionary<string, bool>(); AppDomain.CurrentDomain.SetData("ts3fixes.switches", sw); }
                Dictionary<string, bool?> before = new Dictionary<string, bool?>();
                try
                {
                    foreach (TbTrigger t in pick)
                    {
                        if (sTbGen != gen) break;
                        sTbNow = t.Name;
                        if (on)
                            foreach (string s in t.Switches)
                            {
                                bool was;
                                if (!before.ContainsKey(s)) before[s] = sw.TryGetValue(s, out was) ? (bool?)was : null;
                                sw[s] = true;
                            }
                        if (on) Util.SwitchesChanged();
                        Dictionary<string, object> row = Util.Obj();
                        row["trigger"] = t.Name;
                        row["switches"] = t.Switches;
                        int[] c0 = (int[])TbCounters().Clone();
                        int t0 = System.Environment.TickCount;
                        string res;
                        try { res = t.Run(row); }
                        catch (ResetException) { throw; }
                        catch (Exception e) { res = "error: " + e.GetType().Name + ": " + e.Message; }
                        int[] c1 = TbCounters();
                        Dictionary<string, object> own = Util.Obj(), other = Util.Obj();
                        bool fired = false;
                        foreach (int s in t.Slots) if (s < c1.Length) { int d = c1[s] - (s < c0.Length ? c0[s] : 0); own[s.ToString()] = d; if (d != 0) fired = true; }
                        int nOther = 0;
                        for (int s = 0; s < c1.Length && nOther < 25; s++)
                        {
                            int d = c1[s] - (s < c0.Length ? c0[s] : 0);
                            if (d != 0 && Array.IndexOf(t.Slots, s) < 0) { other[s.ToString()] = d; nOther++; }
                        }
                        row["result"] = res;
                        row["deltas"] = own;
                        if (nOther > 0) row["other_deltas"] = other;
                        row["fired"] = fired;
                        row["s"] = (System.Environment.TickCount - t0) / 1000;
                        foreach (string s in t.Switches) sTbTotals[s] = fired || (sTbTotals.ContainsKey(s) && (bool)sTbTotals[s]);
                        lock (sTbRows) sTbRows.Add(row);
                    }
                }
                catch (Exception e) { lock (sTbRows) { Dictionary<string, object> r = Util.Obj(); r["error"] = e.GetType().Name + ": " + e.Message; sTbRows.Add(r); } }
                finally
                {
                    if (on && !keep)
                    {
                        foreach (KeyValuePair<string, bool?> kv in before) { if (kv.Value.HasValue) sw[kv.Key] = kv.Value.Value; else sw.Remove(kv.Key); }
                        Util.SwitchesChanged();
                    }
                    sTbNow = "";
                    sTbRunning = false;
                }
            }));
            return TbStatus();
        }

        // ---- helpers ----

        static readonly List<ulong> sTbUsed = new List<ulong>();

        // An uncontrolled adult/teen human NPC in the world, not on the active household's lot, not in a role or a service,
        // not used by an earlier trigger of this run; the one farthest from the camera.
        static Sim TbNpc(Predicate<Sim> extra)
        {
            Vector3 cam = CameraController.GetTarget();
            Lot home = Household.ActiveHousehold == null ? null : Household.ActiveHousehold.LotHome;
            Sim best = null; float bestD = -1f;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null) continue;
                SimDescription d = s.SimDescription;
                if (!DrNpc(d) || !d.IsHuman || !d.TeenOrAbove || d.AssignedRole != null || d.CreatedByService != null) continue;
                if (home != null && s.LotCurrent == home) continue;
                if (sTbUsed.Contains(d.SimDescriptionId) || (extra != null && !extra(s))) continue;
                float dist = (s.Position - cam).Length();
                if (dist > bestD) { best = s; bestD = dist; }
            }
            if (best != null) sTbUsed.Add(best.SimDescription.SimDescriptionId);
            return best;
        }

        static bool TbWait(int ms, TbCond until)
        {
            int start = System.Environment.TickCount;
            while (unchecked(System.Environment.TickCount - start) < ms)
            {
                if (until != null && until()) return true;
                Simulator.Sleep(0);
            }
            return until != null && until();
        }

        // An item in the NPC's inventory: one it has, else a placeable EA catalog object created for it (recorded for
        // remove_objects in "ts3agent.content.created").
        static GameObject TbItem(Sim s, out bool created)
        {
            created = false;
            foreach (IGameObject o in s.Inventory.FindAll<IGameObject>(false))
            {
                GameObject g = o as GameObject;
                if (g != null && !s.Inventory.GetInUse(g)) return g;
            }
            List<BuildBuyProduct> cat = SortedCatalog();
            for (int n = 0, tries = 0; n < cat.Count && tries < 40; n++)
            {
                BuildBuyProduct p = cat[n];
                if (!Placeable(p) || ContentOrigin(p.ProductResourceKey) != "ea") continue;
                tries++;
                GameObject g = null;
                try { g = GlobalFunctions.CreateObjectOutOfWorld(p.ProductResourceKey) as GameObject; } catch (Exception) { }
                if (g == null) continue;
                if (s.Inventory.TryToAdd(g)) { CreatedObjects().Add(g.ObjectId.Value); created = true; return g; }
                g.Destroy();
            }
            return null;
        }

        static void TbDropItem(Sim s, GameObject g, bool created)
        {
            if (!created || g == null || g.HasBeenDestroyed) return;
            try { if (s.Inventory != null && s.Inventory.Contains(g)) s.Inventory.TryToRemove(g); g.Destroy(); } catch (Exception) { }
        }

        // ---- speed (no Sim touched) ----

        static readonly MethodInfo sTbSetSpeed = typeof(GF).GetMethod("SetGameSpeed", kTbAll, null, new Type[] { typeof(GameSpeed), typeof(bool) }, null);

        static string TbSpeedRecalc(Dictionary<string, object> row)
        {
            if (GF.GameSpeedLocked) return "skipped: another holder has the speed lock";
            GF.LockGameSpeed(GameSpeed.Pause);
            try { GF.RecalculateGameSpeed(); row["locked_after_recalc"] = GF.GameSpeedLocked; }
            finally { if (GF.GameSpeedLocked) GF.UnlockGameSpeed(); }
            return "ok";
        }

        static string TbSpeedSkip(Dictionary<string, object> row)
        {
            Sim a = Sim.ActiveActor;
            if (a == null || a.InteractionQueue == null) return "skipped: no active Sim";
            if (GF.GameSpeedLocked) return "skipped: speed locked";
            if (sTbSetSpeed == null) return "skipped: Gameflow.SetGameSpeed(GameSpeed, bool) not found";
            GameSpeed prev = GF.CurrentGameSpeed;
            // read only: the active Sim is never touched; the case needs it idle (no head interaction)
            if (!TbWait(20000, delegate { return a.InteractionQueue.GetHeadInteraction() == null; })) return "skipped: active Sim never idle in 20 s";
            sTbSetSpeed.Invoke(null, new object[] { GameSpeed.Skip, false });
            row["speed_after_skip"] = GF.CurrentGameSpeed.ToString();
            sTbSetSpeed.Invoke(null, new object[] { prev == GameSpeed.Skip ? GameSpeed.Normal : prev, false });
            return "ok";
        }

        static string TbSpeedLock(Dictionary<string, object> row)
        {
            if (GF.GameSpeedLocked) return "skipped: another holder has the speed lock";
            Lot lot = HomeLot();
            GF.LockGameSpeed(GameSpeed.Pause);   // the "other holder" (as a thumbnail or CAS would hold it)
            try
            {
                Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate { GameStates.TransitionToBuildMode(lot, null); }));
                bool inBuild = TbWait(30000, delegate { string st = SubState(); return st != null && st.IndexOf("Build", StringComparison.Ordinal) >= 0; });
                row["entered_build"] = inBuild;
                TbWait(1500, null);
                Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate { GameStates.TransitionToLiveMode(); }));
                row["back_live"] = TbWait(30000, delegate { return SubState() == "LiveModeState"; });
                row["lock_kept"] = GF.GameSpeedLocked;
            }
            finally { if (GF.GameSpeedLocked) GF.UnlockGameSpeed(); }
            return "ok";
        }

        // ---- heartbroken (NPC couple) ----

        static string TbHeartBroken(Dictionary<string, object> row)
        {
            Sim partner = null;
            Sim s = TbNpc(delegate(Sim x)
            {
                foreach (Relationship r in Relationship.Get(x))
                {
                    if (r == null || r.LTR == null || !r.LTR.IsPositive) continue;
                    LongTermRelationshipTypes t = r.LTR.CurrentLTR;
                    if (t != LongTermRelationshipTypes.Partner && t != LongTermRelationshipTypes.Spouse && t != LongTermRelationshipTypes.Fiancee && t != LongTermRelationshipTypes.RomanticInterest) continue;
                    SimDescription o = r.GetOtherSimDescription(x.SimDescription);
                    if (o == null || !DrNpc(o) || o.CreatedSim == null || o.CreatedSim.HasBeenDestroyed || o.CreatedSim.BuffManager == null) continue;
                    partner = o.CreatedSim;
                    return true;
                }
                return false;
            });
            if (s == null || partner == null) return "skipped: no NPC couple in the world";
            row["sims"] = s.FullName + " -> " + partner.FullName;
            bool had = partner.BuffManager.HasElement(BuffNames.HeartBroken);
            SocialCallback.AddHeartBroken(s, partner, "Break Up", null, null);
            SocialCallback.AddHeartBroken(s, partner, "Break Up", null, null);  // the second one replaces the first broadcaster
            if (!had) partner.BuffManager.RemoveElement(BuffNames.HeartBroken);
            return "ok";
        }

        // ---- door lock owner (NPC household) ----

        static string TbDoorLock(Dictionary<string, object> row)
        {
            Vector3 cam = CameraController.GetTarget();
            Household best = null; CommonDoor bestDoor = null; float bestD = -1f;
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null || h.IsActive || h.IsSpecialHousehold || h.LotHome == null || h.AllSimDescriptions.Count < 2) continue;
                bool allNpc = true;
                foreach (SimDescription d in h.AllSimDescriptions) if (!DrNpc(d)) { allNpc = false; break; }
                if (!allNpc) continue;
                CommonDoor door = null;
                foreach (CommonDoor d in h.LotHome.GetObjects<CommonDoor>()) if (d != null && d.LockType == CommonDoor.tLock.Anybody) { door = d; break; }
                if (door == null) continue;
                float dist = (h.LotHome.Position - cam).Length();
                if (dist > bestD) { best = h; bestDoor = door; bestD = dist; }
            }
            if (best == null) return "skipped: no all-NPC household of 2+ with an unlocked door";
            SimDescription owner = null;
            foreach (SimDescription d in best.SimDescriptions) if (d.CreatedSim == null && d.TeenOrAbove) { owner = d; break; }  // not instantiated: fewer side effects
            if (owner == null) foreach (SimDescription d in best.SimDescriptions) if (d.TeenOrAbove) { owner = d; break; }
            if (owner == null) return "skipped: no teen or older member";
            row["household"] = best.Name;
            row["owner"] = owner.FullName;
            bestDoor.SetLockTypeAndOwner(CommonDoor.tLock.SelectedActor, owner);
            try
            {
                best.RemoveSimKeepHousehold(owner);   // Household.Remove(sd, false): the hooked method
                best.Add(owner);                       // moved straight back in
            }
            finally
            {
                row["lock_after"] = bestDoor.LockType.ToString();
                if (bestDoor.LockType != CommonDoor.tLock.Anybody) bestDoor.SetLockTypeAndOwner(CommonDoor.tLock.Anybody, null);
            }
            return "ok";
        }

        // ---- in use (NPC inventory) ----

        static string TbInUse(Dictionary<string, object> row)
        {
            Sim s = TbNpc(delegate(Sim x) { return x.Inventory != null; });
            if (s == null) return "skipped: no NPC";
            bool created;
            GameObject g = TbItem(s, out created);
            if (g == null) return "skipped: no inventory item";
            row["sim"] = s.FullName; row["item"] = g.GetType().Name;
            try
            {
                s.Inventory.SetInUse(g);
                s.SetObjectToReset();
                TbWait(3000, delegate { return !s.Inventory.GetInUse(g); });
                row["still_in_use"] = s.Inventory.GetInUse(g);
                if (s.Inventory.GetInUse(g)) s.Inventory.SetNotInUse(g);
            }
            finally { TbDropItem(s, g, created); }
            return "ok";
        }

        static string TbPutDown(Dictionary<string, object> row)
        {
            Sim s = TbNpc(delegate(Sim x) { return x.Inventory != null && !x.IsOutside; });
            if (s == null) s = TbNpc(delegate(Sim x) { return x.Inventory != null; });
            if (s == null) return "skipped: no NPC";
            bool created;
            GameObject g = TbItem(s, out created);
            if (g == null) return "skipped: no inventory item";
            row["sim"] = s.FullName; row["item"] = g.GetType().Name;
            try
            {
                s.InteractionQueue.CancelAllInteractions();
                InteractionInstance ii = PutDown.DisallowCarrySingleton.CreateInstance(g, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                if (ii == null || !s.InteractionQueue.Add(ii)) return "skipped: put-down not queued";
                // the bug window: TryToPlaceOnSurface marked the item in use and the swipe plays; reset the Sim then
                bool inUse = TbWait(45000, delegate { return s.HasBeenDestroyed || !s.Inventory.Contains(g) || s.Inventory.GetInUse(g); });
                row["reached_in_use"] = inUse && !s.HasBeenDestroyed && s.Inventory.Contains(g) && s.Inventory.GetInUse(g);
                if (!(bool)row["reached_in_use"]) return "not reached: the put-down finished or never marked the item";
                s.SetObjectToReset();
                TbWait(3000, null);
                row["still_in_use"] = s.Inventory.Contains(g) && s.Inventory.GetInUse(g);
                if ((bool)row["still_in_use"]) s.Inventory.SetNotInUse(g);
            }
            finally { TbDropItem(s, g, created); }
            return "ok";
        }

        // ---- go home with a walk style (NPC) ----

        static string TbGoHome(Dictionary<string, object> row)
        {
            Sim s = TbNpc(delegate(Sim x) { return x.LotHome != null && x.LotCurrent != x.LotHome; });
            if (s == null) return "skipped: no NPC away from home";
            row["sim"] = s.FullName;
            s.InteractionQueue.CancelAllInteractions();
            GoHome gh = GoHome.Singleton.CreateInstance(s.LotHome, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true) as GoHome;
            if (gh == null) return "skipped: GoHome not created";
            gh.WalkStyle = Sim.WalkStyle.Run;
            if (!s.InteractionQueue.Add(gh)) return "skipped: GoHome not queued";
            int[] c = TbCounters();
            int r0 = c[2477] + c[2478];
            TbWait(60000, delegate { return s.HasBeenDestroyed || TbCounters()[2477] + TbCounters()[2478] != r0 || (s.InteractionQueue.GetHeadInteraction() != gh && !s.InteractionQueue.HasInteraction(gh)); });
            return "ok";
        }

        // ---- elevator (NPC rider) ----

        static readonly FieldInfo sTbElAssigned = typeof(ElevatorInterior.ElevatorPortalComponent).GetField("mAssignedSims", kTbAll);

        static bool TbElWaiting(ElevatorInterior.ElevatorPortalComponent pc, Sim s)
        {
            IDictionary d = sTbElAssigned == null ? null : sTbElAssigned.GetValue(pc) as IDictionary;
            if (d == null || !d.Contains(s.SimDescription)) return false;
            object e = d[s.SimDescription];
            FieldInfo f = e == null ? null : e.GetType().GetField("mbSleeping", kTbAll);
            return f != null && (bool)f.GetValue(e);
        }

        static string TbElevator(Dictionary<string, object> row)
        {
            Vector3 cam = CameraController.GetTarget();
            Lot home = Household.ActiveHousehold == null ? null : Household.ActiveHousehold.LotHome;
            ElevatorInterior low = null, high = null; float bestD = -1f;
            Dictionary<ulong, List<ElevatorInterior>> byLot = new Dictionary<ulong, List<ElevatorInterior>>();
            foreach (ElevatorInterior e in Sims3.Gameplay.Queries.GetObjects<ElevatorInterior>())
            {
                if (e == null || e.LotCurrent == null || e.LotCurrent == home || e.LotCurrent.IsWorldLot) continue;
                List<ElevatorInterior> l;
                if (!byLot.TryGetValue(e.LotCurrent.LotId, out l)) byLot[e.LotCurrent.LotId] = l = new List<ElevatorInterior>();
                l.Add(e);
            }
            foreach (List<ElevatorInterior> l in byLot.Values)
            {
                ElevatorInterior lo = null, hi = null;
                foreach (ElevatorInterior e in l) { if (lo == null || e.Level < lo.Level) lo = e; if (hi == null || e.Level > hi.Level) hi = e; }
                if (lo == null || hi == null || lo.Level == hi.Level) continue;
                float dist = (lo.Position - cam).Length();
                if (dist > bestD) { low = lo; high = hi; bestD = dist; }
            }
            if (low == null) return "skipped: no non-home lot with an elevator on two levels";
            ElevatorInterior.ElevatorPortalComponent pc = low.PortalComponent as ElevatorInterior.ElevatorPortalComponent;
            if (pc == null) return "skipped: elevator has no ElevatorPortalComponent";
            Sim s = TbNpc(null);
            if (s == null) return "skipped: no NPC";
            row["lot"] = Util.LotName(low.LotCurrent); row["sim"] = s.FullName; row["levels"] = low.Level + "->" + high.Level;
            s.InteractionQueue.CancelAllInteractions();
            Vector3 start = low.Position + low.ForwardVector * 1.5f;
            s.SetPosition(start);
            Vector3 goal = high.Position + high.ForwardVector * 1.5f;
            InteractionInstance ii = Terrain.GoHere.GetSingleton(s, goal).CreateInstance(Terrain.Singleton, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            Terrain.GoHere gh = ii as Terrain.GoHere;
            if (gh != null) gh.SetDestination(goal, false);
            SetClickHit(ii, goal);
            if (!s.InteractionQueue.Add(ii)) return "skipped: Go Here not queued";
            // any portal of this elevator column may take the rider; watch them all
            List<ElevatorInterior.ElevatorPortalComponent> pcs = new List<ElevatorInterior.ElevatorPortalComponent>();
            foreach (ElevatorInterior e in byLot[low.LotCurrent.LotId]) { ElevatorInterior.ElevatorPortalComponent p = e.PortalComponent as ElevatorInterior.ElevatorPortalComponent; if (p != null) pcs.Add(p); }
            ElevatorInterior.ElevatorPortalComponent waitingIn = null;
            bool waited = TbWait(60000, delegate
            {
                foreach (ElevatorInterior.ElevatorPortalComponent p in pcs) if (TbElWaiting(p, s)) { waitingIn = p; return true; }
                return s.HasBeenDestroyed || s.Level == high.Level;
            });
            row["waited_inside"] = waitingIn != null;
            row["arrived_level"] = s.Level;
            if (waitingIn == null) return waited ? "no wait caught (rode through or arrived)" : "not reached in 60 s";
            GameObject owner = waitingIn.Owner as GameObject;
            if (owner != null) owner.SetObjectToReset();   // ElevatorPortalComponent.OnReset with the rider waiting
            TbWait(5000, null);
            return "ok";
        }
    }
}
