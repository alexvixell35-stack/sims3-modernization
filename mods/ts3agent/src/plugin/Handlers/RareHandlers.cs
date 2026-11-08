using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Rare routing scenarios (docs/117): crowds on one spot, a death during a walk, and route targets that need a search
    // (pond, sea, far lot). Route option bits for sim_route_to are in ReproHandlers.
    public static partial class Handlers
    {
        static void RegisterRare()
        {
            Dispatcher.Register("crowd_go", CrowdGo);
            Dispatcher.Register("crowd_do", CrowdDo);
            Dispatcher.Register("crowd_visit_lot", CrowdVisitLot);
            Dispatcher.Register("houseboat_leave", HouseboatLeave);
            Dispatcher.Register("kill_sim", KillSim);
            Dispatcher.Register("zombies", Zombies);
            Dispatcher.Register("spawn_traffic", SpawnTraffic);
            Dispatcher.Register("unseen_snapback", UnseenSnapback);
            Dispatcher.Register("map_view", MapView);            // docs/228 §11.6: the UI's camera paths (script camera ICs)
            Dispatcher.Register("focus_sim", FocusSim);
            Dispatcher.Register("unseen_water_check", UnseenWaterCheck);
            Dispatcher.Register("unseen_horse_ride", UnseenHorseRide);
            Dispatcher.Register("unseen_water_watch", UnseenWaterWatch);
            Dispatcher.Register("unseen_water_cause", UnseenWaterCause);
            Dispatcher.Register("houseboat_move", HouseboatMove);
            Dispatcher.Register("mem_mark", MemMark);
            Dispatcher.Register("mem_gc", MemGc);                // MemHandlers.cs (docs/191 module 2)
            Dispatcher.Register("catalog_loop", CatalogLoop);
            Dispatcher.Register("buy_sell_loop", BuySellLoop);
            Dispatcher.Register("bb_cc_cycle", BbCcCycle);       // MemStressHandlers.cs (docs/200)
            Dispatcher.Register("et_lot_cycle", EtLotCycle);
            Dispatcher.Register("build_cc_cycle", BuildCcCycle);  // BuildCcHandlers.cs (docs/200 §6)
            Dispatcher.Register("build_cc_catalog", BuildCcCatalog);
            Dispatcher.Register("inv_pool", InvPool);             // BuildCcHandlers.cs: buy_object pool (Sim-inventory CC)
            Dispatcher.Register("scripted_cc_check", ScriptedCcCheck); // ScriptedCcHandlers.cs (docs/200 §8)
            Dispatcher.Register("scripted_cc_cycle", ScriptedCcCycle);
            Dispatcher.Register("preset_cc_check", PresetCcCheck);   // PresetCcHandlers.cs (docs/200 §9)
            Dispatcher.Register("preset_cc_cycle", PresetCcCycle);
        }

        // mem_mark {tag}: docs/191 module 1 / docs/190 phase 1: asks ts3native [MemDiag] for one "diag: mem" sample tagged
        // ev=<tag> ([A-Za-z0-9_-], 31 characters; others become '_'), so a repro brackets an action (Build mode, catalogue)
        // with before / after samples. Sent as the text line "mem: <tag>" over ts3native's [UserDiag] text channel (top
        // byte 0x87, as ts3fixes UserDiag.cs). accepted = false: no ts3native, [UserDiag] off, or an older build.
        static readonly ScriptCore.Route sMemProbe = new ScriptCore.Route();
        static bool MemSend(int op, uint v) { return sMemProbe.GetOption2((Route.RouteOption2)(0x8700000000000000UL | (ulong)(uint)op << 48 | v)); }

        // Since ts3native 0.36.74 the mark is sampled inline (the line exists when this returns) and ts3fixes then answers its
        // suspect counters for it (MemHandlers.cs SusPoll); suspects = false: no ts3fixes, an older one, or [MemDiag] off.
        static object MemMark(Args a)
        {
            bool sus;
            Dictionary<string, object> r = Util.Obj();
            r["accepted"] = MemMarkTag(a.Str("tag"), out sus);
            r["suspects"] = sus;
            return r;
        }

        // One free text line into the ts3native log (same channel and framing as ts3fixes DiagLine); false = channel off.
        static bool DiagLine(string s)
        {
            bool ok = MemSend(0, 0);
            for (int i = 0; ok && i < s.Length; i += 4)
            {
                uint v = 0;
                for (int k = 0; k < 4 && i + k < s.Length; k++) v |= (uint)(s[i + k] & 0xFF) << (8 * k);
                ok = MemSend(1, v);
            }
            return ok && MemSend(2, 0);
        }

        static bool MemMarkTag(string tag, out bool suspects)
        {
            bool ok = DiagLine("mem: " + tag);
            suspects = ok && SusPoll();
            return ok;
        }

        // unseen_water_watch {start: true} | {stop: true} | {result: true, min_engaged?: n, min_mounted?: n, require?: true}
        // (docs/143 §8.10): a game task samples every spawn_sims Sim every 15 ticks while the repro's legs run and keeps,
        // for Sims out of view (more than 100 m from the camera and its target, not on screen): the most at once in a
        // vehicle or boat (Parent set, not a horse), in water deeper than 0.5 m (swimming or routing), and mounted on a horse
        // (Parent is a horse Sim); and every "stranded" sample (in water deeper than 0.5 m, no vehicle, not routing, not in a
        // swim / boat / taxi interaction). {result, require} throws unless no Sim was stranded and the maxima reached
        // min_engaged (vehicle + water) and min_mounted: a run where nobody rode or swam unseen proves nothing and fails
        // as "not exercised".
        static int sWaterGen, sWaterSamples, sWaterMaxEngaged, sWaterMaxVehicle, sWaterMaxWater, sWaterMaxMounted;
        static List<object> sWaterStranded = new List<object>();
        static object UnseenWaterWatch(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("start"))
            {
                int gen = ++sWaterGen;
                sWaterSamples = sWaterMaxEngaged = sWaterMaxVehicle = sWaterMaxWater = sWaterMaxMounted = 0;
                sWaterStranded = new List<object>();
                Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
                {
                    while (gen == sWaterGen)
                    {
                        try { WaterSample(); } catch (Exception) { }
                        Simulator.Sleep(15);
                    }
                }));
                r["started"] = true;
                return r;
            }
            if (a.Has("stop")) { sWaterGen++; r["stopped"] = true; }
            r["samples"] = sWaterSamples;
            r["max_engaged"] = sWaterMaxEngaged;
            r["max_in_vehicle"] = sWaterMaxVehicle;
            r["max_in_water"] = sWaterMaxWater;
            r["max_mounted"] = sWaterMaxMounted;
            r["stranded"] = sWaterStranded;
            int minE = (int)a.Long("min_engaged", 0), minM = (int)a.Long("min_mounted", 0);
            bool ok = sWaterStranded.Count == 0 && sWaterMaxEngaged >= minE && sWaterMaxMounted >= minM && sWaterSamples > 0;
            r["check_ok"] = ok;
            if (a.Bool("require", false) && !ok)
                throw new McpException(sWaterStranded.Count > 0 ? "unseen_water_watch: " + sWaterStranded.Count + " stranded sample(s): " + StrandedSummary(3)
                    : "unseen_water_watch: not exercised (max engaged " + sWaterMaxEngaged + " < " + minE + " or max mounted " + sWaterMaxMounted + " < " + minM + ", samples " + sWaterSamples + ")");
            return r;
        }

        // first n stranded samples as "name depth/above-sea interaction lot", so a failed check names its Sims in the log
        static string StrandedSummary(int n)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < sWaterStranded.Count && i < n; i++)
            {
                Dictionary<string, object> o = sWaterStranded[i] as Dictionary<string, object>;
                if (o == null) continue;
                parts.Add(o["name"] + " depth " + o["depth_m"] + " m, " + o["y_above_sea_m"] + " m above sea, '" + o["interaction"] + "', lot " + (o["lot"] ?? "none"));
            }
            return string.Join("; ", parts.ToArray());
        }

        static void WaterSample()
        {
            float sea = World.GetSeaLevel();
            Vector3 cam = CameraController.GetPosition(), tgt = CameraController.GetTarget();
            int vehicle = 0, water = 0, mounted = 0;
            foreach (Sim s in SpawnedInWorld())
            {
                Vector3 p = s.Position;
                if ((p - cam).Length() < 100f || (p - tgt).Length() < 100f || World.IsObjectOnScreen(s)) continue;  // seen: not this check's case
                object parent = s.Parent;
                Sim horse = parent as Sim;
                bool routing = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting;
                float depth = sea - World.GetTerrainHeight(p.x, p.z);
                string inter = (s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "") + " " + (s.Posture != null ? s.Posture.GetType().Name : "");
                bool swimOrBoat = inter.IndexOf("Swim", StringComparison.OrdinalIgnoreCase) >= 0 || inter.IndexOf("Boat", StringComparison.OrdinalIgnoreCase) >= 0
                    || inter.IndexOf("Taxi", StringComparison.OrdinalIgnoreCase) >= 0 || inter.IndexOf("Ocean", StringComparison.OrdinalIgnoreCase) >= 0
                    || inter.IndexOf("Dive", StringComparison.OrdinalIgnoreCase) >= 0 || inter.IndexOf("Snorkel", StringComparison.OrdinalIgnoreCase) >= 0
                    || inter.IndexOf("Scuba", StringComparison.OrdinalIgnoreCase) >= 0 || inter.IndexOf("Mermaid", StringComparison.OrdinalIgnoreCase) >= 0;
                // a Sim more than 0.3 m above the sea surface stands on something (houseboat deck, dock, pier, bridge), not in the water
                bool aboveWater = p.y > sea + 0.3f;
                if (horse != null && horse.IsHorse) mounted++;
                else if (parent != null) vehicle++;
                else if (depth > 0.5f && !aboveWater)
                {
                    if (routing || swimOrBoat) water++;
                    else
                    {
                        Dictionary<string, object> o = Util.Obj();
                        o["name"] = s.FullName; o["x"] = Math.Round(p.x, 1); o["z"] = Math.Round(p.z, 1);
                        o["depth_m"] = Math.Round(depth, 2); o["interaction"] = inter;
                        o["y_above_sea_m"] = Math.Round(p.y - sea, 2);
                        o["lot"] = s.LotCurrent != null ? s.LotCurrent.Name : null;
                        if (sWaterStranded.Count < 50) sWaterStranded.Add(o);
                    }
                }
            }
            sWaterSamples++;
            sWaterMaxVehicle = Math.Max(sWaterMaxVehicle, vehicle);
            sWaterMaxWater = Math.Max(sWaterMaxWater, water);
            sWaterMaxEngaged = Math.Max(sWaterMaxEngaged, vehicle + water);
            sWaterMaxMounted = Math.Max(sWaterMaxMounted, mounted);
        }

        // unseen_horse_ride {count?: 1..4} | {remove: true}: the horse case of repro unseen-boats (docs/143 §8.10, needs
        // Pets): makes `count` adult horses (GeneticsPet.MakeRandomPet) next to the first spawn_sims Sims and queues each
        // of those Sims a Mount on its horse; a later crowd_go then rides them away (the rider routes, the horse carries).
        // The horses are kept apart from the spawn_sims list (crowd_go must not send them on their own); {remove:true}
        // destroys them. Returns the pairs.
        static readonly List<ulong> sHorses = new List<ulong>();
        static object UnseenHorseRide(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("remove"))
            {
                int n0 = 0;
                foreach (ulong id in sHorses)
                {
                    SimDescription sd = SimDescription.Find(id);
                    if (sd == null) continue;
                    if (sd.Household != null) sd.Household.Remove(sd);
                    sd.Dispose(true, false, true);
                    n0++;
                }
                sHorses.Clear();
                r["removed"] = n0;
                return r;
            }
            int n = a.Has("count") ? (int)a.Long("count") : 2;
            if (n < 1 || n > 4) throw new McpException("count must be 1..4");
            List<Sim> riders = SpawnedInWorld();
            if (riders.Count == 0) throw new McpException("spawn_sims first (the riders)");
            List<object> pairs = new List<object>();
            for (int i = 0; i < n && i < riders.Count; i++)
            {
                Sim rider = riders[i];
                SimDescription hd = Sims3.Gameplay.CAS.GeneticsPet.MakeRandomPet(Sims3.SimIFace.CAS.CASAgeGenderFlags.Adult, Sims3.SimIFace.CAS.CASAgeGenderFlags.Female, Sims3.SimIFace.CAS.CASAgeGenderFlags.Horse);
                if (hd == null) throw new McpException("no horse made (Pets not installed?)");
                if (rider.Household != null) rider.Household.Add(hd);
                Sim horse = hd.Instantiate(rider.Position + new Vector3(2f, 0f, 2f));
                if (horse == null) throw new McpException("horse not placed");
                sHorses.Add(hd.SimDescriptionId);
                InteractionInstance mount = Sim.Mount.Singleton.CreateInstance(horse, rider, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                Dictionary<string, object> o = Util.Obj();
                o["rider"] = rider.FullName;
                o["horse"] = Util.Id(horse.ObjectId);
                o["mount_queued"] = rider.InteractionQueue.Add(mount);
                pairs.Add(o);
            }
            r["pairs"] = pairs;
            return r;
        }

        // unseen_water_check {}: for every spawn_sims Sim (repro unseen-boats, docs/143 §8.10), where it is relative to the
        // sea: terrain under it below sea level (water), parent (the vehicle or horse it sits in), routing, interaction, lot.
        // "stranded" = in water deeper than 0.5 m, in no vehicle, not routing and not in a swim / boat interaction (the
        // dropped-off-a-boat case). Read only.
        static object UnseenWaterCheck(Args a)
        {
            RequireLive();
            float sea = World.GetSeaLevel();
            List<object> rows = new List<object>();
            int stranded = 0, inWater = 0, inVehicle = 0;
            foreach (Sim s in SpawnedInWorld())
            {
                Dictionary<string, object> o = Util.Obj();
                Vector3 p = s.Position;
                float depth = sea - World.GetTerrainHeight(p.x, p.z);
                bool water = depth > 0.5f;
                object parent = s.Parent;
                bool routing = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting;
                string inter = s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : null;
                bool swimOrBoat = inter != null && (inter.IndexOf("Swim", StringComparison.OrdinalIgnoreCase) >= 0 || inter.IndexOf("Boat", StringComparison.OrdinalIgnoreCase) >= 0
                    || inter.IndexOf("Taxi", StringComparison.OrdinalIgnoreCase) >= 0 || inter.IndexOf("Ocean", StringComparison.OrdinalIgnoreCase) >= 0);
                bool bad = water && parent == null && !routing && !swimOrBoat;
                o["name"] = s.FullName;
                o["x"] = Math.Round(p.x, 1); o["z"] = Math.Round(p.z, 1);
                o["water_depth_m"] = Math.Round(depth, 2);
                o["parent"] = parent != null ? parent.GetType().Name : null;
                o["routing"] = routing;
                o["interaction"] = inter;
                o["lot"] = s.LotCurrent != null ? s.LotCurrent.Name : null;
                o["stranded"] = bad;
                if (water) inWater++;
                if (parent != null) inVehicle++;
                if (bad) stranded++;
                rows.Add(o);
            }
            Dictionary<string, object> r = Util.Obj();
            r["sims"] = rows;
            r["in_water"] = inWater;
            r["in_vehicle"] = inVehicle;
            r["stranded"] = stranded;
            r["sea_level"] = Math.Round(sea, 2);
            return r;
        }

        // unseen_snapback {count?: 5..12} | {result: true, require?: true}: in-game check that ts3native [UnseenThrottle]
        // (docs/143 §8.9) puts a Sim back on full-rate locomotion the pass it becomes visible. A game task picks walking Sims
        // that are unseen by the throttle's rule (not household, routing, off screen, outside the camera reach and 100 m
        // from every household Sim), then one at a time: arms ts3native's snap-back watch (viewer channel codes 6/7), moves
        // the in-game camera onto the Sim and keeps it on the Sim every tick (CameraController only; the game window is
        // never moved or focused) until ts3native reports the class flip and for 10 ticks after it, then reads the record.
        // Verdict per Sim: "fail" = it did not run the flip pass, a part skipped it while it was classed visible (code 10),
        // or the position jump over the flip tick passed its normal per-tick step + 4 m/s x (Rate owed time + [UnseenAnim] owed
        // pose time, code 24) + 0.25 m; the normal step is the median over 8-tick windows of the 32 ticks before the camera moves
        // (whole Rate cycles: a throttled Sim moves every Every-th tick, so per-tick steps after the flip were ~0 and a Stray Dog
        // failed with 0.623 m over a 0.25 m bound, 2026-11-02); species per row (pets walk and run faster); "pass" =
        // none of that and it stayed classed visible for the whole window; "not tested" = no flip within 2 x 120 ticks
        // (second try with the camera closer) or classed unseen again inside the window (the camera lost it: 2026-10-21
        // bp-ut2-snapback, where those skips were read as failures). The task keeps picking Sims until `count` passed or
        // 3 x count were tried. {result:true, require:true} throws (the repro step and the queue job fail) unless the run is
        // done, no Sim failed and at least `count` passed.
        static List<object> sSnapRows;
        static string sSnapState = "idle";
        static int sSnapWant, sSnapPassed, sSnapFailed, sSnapNotTested, sSnapRatePassed, sSnapNoDebtSkipped;
        static readonly ScriptCore.Route sSnapProbe = new ScriptCore.Route();
        const ulong kSnapBits = 0x8200000000000000UL;
        static bool SnapMsg(int code, uint value) { return sSnapProbe.GetOption2((Route.RouteOption2)(kSnapBits | (ulong)(uint)code << 48 | value)); }

        static bool SnapUnseen(Sim s, Vector3 camPos, Vector3 camTgt)
        {
            if (s == null || s.HasBeenDestroyed || s.IsSelectable || s.Household == Household.ActiveHousehold) return false;
            if (s.SimRoutingComponent == null || !s.SimRoutingComponent.IsRouting) return false;
            float reach = Math.Max(80f, 2f * Math.Abs(camPos.y - camTgt.y)) + 20f;  // 20 m margin over the native rule
            Vector3 p = s.Position;
            if ((p - camPos).Length() < reach || (p - camTgt).Length() < reach || World.IsObjectOnScreen(s)) return false;
            if (Household.ActiveHousehold != null)
                foreach (Sim h in Household.ActiveHousehold.AllActors)
                    if (h != null && (h.Position - p).Length() < 100f) return false;
            return true;
        }

        static object UnseenSnapback(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("result"))
            {
                r["state"] = sSnapState;
                r["want"] = sSnapWant;
                r["passed"] = sSnapPassed;
                r["failed"] = sSnapFailed;
                r["not_tested"] = sSnapNotTested;
                r["passed_with_rate_debt"] = sSnapRatePassed;  // the Rate snap-back path (owed_ms > 0) exercised
                r["candidates_without_debt_skipped"] = sSnapNoDebtSkipped;
                r["sims"] = sSnapRows ?? new List<object>();
                bool ok = sSnapState.StartsWith("done") && sSnapFailed == 0 && sSnapWant > 0 && sSnapPassed >= sSnapWant && sSnapRatePassed >= 1;
                r["check_ok"] = ok;
                string summary = "wanted " + sSnapWant + ", passed " + sSnapPassed + ", failed " + sSnapFailed + ", not tested " + sSnapNotTested +
                    ", passed with Rate debt " + sSnapRatePassed + " (needs 1), state " + sSnapState;
                r["summary"] = summary;
                if (a.Bool("require", false) && !ok) throw new McpException("unseen_snapback check failed: " + summary);
                return r;
            }
            int n = a.Has("count") ? (int)a.Long("count") : 6;
            if (n < 5 || n > 12) throw new McpException("count must be 5..12");
            if (sSnapState == "running") throw new McpException("already running");
            sSnapRows = new List<object>();
            sSnapState = "running";
            sSnapWant = n;
            sSnapPassed = sSnapFailed = sSnapNotTested = sSnapRatePassed = sSnapNoDebtSkipped = 0;
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                Vector3 homePos = CameraController.GetPosition(), homeTgt = CameraController.GetTarget();
                try
                {
                    List<ulong> done = new List<ulong>();
                    int waited = 0;
                    while ((sSnapPassed < n || sSnapRatePassed < 1) && done.Count < 6 * n && waited < 20000)
                    {
                        Sim pick = null;
                        foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                            if (!done.Contains(s.ObjectId.Value) && SnapUnseen(s, homePos, homeTgt)) { pick = s; break; }
                        if (pick == null) { Simulator.Sleep(10); waited += 10; continue; }
                        done.Add(pick.ObjectId.Value);
                        // prefer a Sim the Rate part is holding (debt > 0, watch code 18): arm, let it pass a few updates,
                        // skip it while there are other candidates; at most 4 x count such skips, then any Sim is tested
                        ulong pid = pick.ObjectId.Value;
                        if (sSnapNoDebtSkipped < 4 * n && SnapMsg(6, (uint)pid) && SnapMsg(7, (uint)(pid >> 32)))
                        {
                            bool held = false;
                            for (int t = 0; t < 12 && !held; t++) { Simulator.Sleep(1); held = SnapMsg(18, 1); }
                            SnapMsg(11, 0);
                            waited += 12;
                            if (!held) { sSnapNoDebtSkipped++; continue; }
                        }
                        Dictionary<string, object> row = SnapOne(pick);
                        object v;
                        string verdict = row.TryGetValue("verdict", out v) ? (string)v : "not tested";
                        if (verdict == "pass") { sSnapPassed++; if (row.ContainsKey("owed_ms") && (int)row["owed_ms"] > 0) sSnapRatePassed++; }
                        else if (verdict == "fail") sSnapFailed++;
                        else sSnapNotTested++;
                        sSnapRows.Add(row);
                        CameraController.SetPositionAndTarget(homePos, homeTgt);
                        Simulator.Sleep(30);  // back out of view before the next pick
                        waited += 30;
                    }
                    sSnapState = sSnapPassed < n ? "done (fewer passes than asked)" : "done";
                }
                catch (Exception e) { sSnapState = "failed: " + e.Message; }
                finally { SnapMsg(11, 0); CameraController.SetPositionAndTarget(homePos, homeTgt); }
            }));
            r["state"] = sSnapState;
            r["count"] = n;
            return r;
        }

        // largest t <= max with "value >= t" for a watch query code (binary search over the one-bit channel)
        static int SnapAtLeast(int code, int max)
        {
            int v = 0;
            for (int bit = 1 << 10; bit > 0; bit >>= 1) if (v + bit <= max && SnapMsg(code, (uint)(v + bit))) v += bit;
            return v;
        }

        static Dictionary<string, object> SnapOne(Sim s)
        {
            Dictionary<string, object> o = Util.Obj();
            o["id"] = Util.Id(s.ObjectId);
            o["name"] = s.FullName;
            ulong id = s.ObjectId.Value;
            if (!SnapMsg(6, (uint)id) || !SnapMsg(7, (uint)(id >> 32)))
            {
                o["error"] = "ts3native snap-back watch not answering ([UnseenThrottle] off?)";
                o["verdict"] = "fail";
                return o;
            }
            // the Sim's normal step while unseen, before the camera moves: 32 ticks, path length per 8-tick window / 8
            // (whole Rate cycles for Every 2, 4, 8); also lets the throttle build owed time
            o["species"] = s.SimDescription != null ? s.SimDescription.Species.ToString() : "?";
            List<float> pre = new List<float>();
            Vector3 last = s.Position;
            float win = 0f;
            for (int t = 1; t <= 32; t++)
            {
                Simulator.Sleep(0);
                if (s.HasBeenDestroyed) { o["error"] = "Sim destroyed"; o["verdict"] = "not tested"; SnapMsg(11, 0); return o; }
                win += (s.Position - last).Length();
                last = s.Position;
                if (t % 8 == 0) { pre.Add(win / 8f); win = 0f; }
            }
            pre.Sort();
            float median = pre[pre.Count / 2];
            Vector3 before = s.Position;
            int tick = 0, flipTick = -1, tries = 1;
            float jump = -1f, camAtFlip = -1f, camMax = 0f;
            List<float> steps = new List<float>();
            Vector3 prev = s.Position, offset = new Vector3(-8f, 10f, -8f);
            CameraController.SetPositionAndTarget(prev + offset, prev);
            while (true)
            {
                Simulator.Sleep(0);
                tick++;
                if (s.HasBeenDestroyed) { o["error"] = "Sim destroyed"; o["verdict"] = "not tested"; SnapMsg(11, 0); return o; }
                Vector3 now = s.Position;
                float step = (now - prev).Length(), cam = (CameraController.GetTarget() - now).Length();
                if (flipTick < 0 && SnapMsg(8, 1)) { flipTick = tick; jump = step; camAtFlip = cam; }
                else if (flipTick >= 0) { steps.Add(step); camMax = Math.Max(camMax, cam); }
                prev = now;
                if (flipTick >= 0 && tick - flipTick >= 10) break;
                if (flipTick < 0 && tick >= 120)
                {
                    if (tries == 2) break;
                    tries = 2;  // second try with the camera closer
                    tick = 0;
                    offset = new Vector3(-3f, 4f, -3f);
                }
                CameraController.SetPositionAndTarget(now + offset, now);  // stays on the Sim through the whole window
            }
            int owedMs = flipTick >= 0 ? SnapAtLeast(9, 1000) : 0;
            int animOwedMs = flipTick >= 0 ? SnapAtLeast(24, 1000) : 0;  // 0 without [UnseenAnim] (or an older asi)
            steps.Sort();
            float postMedian = steps.Count > 0 ? steps[steps.Count / 2] : 0f;
            float bound = median + 4f * (owedMs + animOwedMs) / 1000f + 0.25f;
            bool ran = SnapMsg(8, 2);
            int skipsVis = SnapAtLeast(10, 1000), skipsUnseen = SnapAtLeast(16, 1000), unseenAfter = SnapAtLeast(17, 1000);
            o["flipped"] = flipTick >= 0;
            o["flip_tick"] = flipTick;
            o["tries"] = tries;
            o["ran_same_pass"] = ran;
            o["owed_ms"] = owedMs;
            o["anim_owed_ms"] = animOwedMs;
            o["avoid_called"] = SnapMsg(8, 3);
            o["avoid_ran"] = SnapMsg(8, 4);
            o["footprint_called"] = SnapMsg(8, 5);
            o["footprint_ran"] = SnapMsg(8, 6);
            o["skips_visible"] = skipsVis;
            o["skips_unseen_again"] = skipsUnseen;
            o["unseen_updates_after_flip"] = unseenAfter;
            o["camera_target_dist_at_flip_m"] = Math.Round(camAtFlip, 2);
            o["camera_target_dist_max_m"] = Math.Round(camMax, 2);
            o["before"] = Pos(before);
            o["after"] = Pos(s.Position);
            o["jump_m"] = Math.Round(jump, 3);
            o["step_median_m"] = Math.Round(median, 3);  // before the flip, whole passes (the bound's term)
            o["step_median_post_m"] = Math.Round(postMedian, 3);
            o["jump_bound_m"] = Math.Round(bound, 3);
            o["still_routing"] = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting;
            string verdict;
            if (flipTick < 0) verdict = "not tested";  // no flip: the camera never made it visible (or it stopped walking)
            else if (!ran || skipsVis > 0 || jump > bound) verdict = "fail";
            else if (unseenAfter > 0) verdict = "not tested";  // classed unseen again inside the window: the camera lost it
            else verdict = "pass";
            o["verdict"] = verdict;
            o["pass"] = verdict == "pass";
            SnapMsg(11, 0);
            return o;
        }

        // spawn_traffic {count: 1..20}: driverless traffic cars made the way TrafficManager makes them (CarTaxiLightweight,
        // EP3, a street parking spot of a random commercial lot, placed on a road out of view within 100 m of it, driving
        // around for 2 sim-hours); each one's LiteDriver task then picks lots and drives (the ts3fixes unseencars repro,
        // docs/143 §8.5). Returns how many were placed.
        static object SpawnTraffic(Args a)
        {
            RequireLive();
            int n = (int)a.Long("count");
            if (n < 1 || n > 20) throw new McpException("count must be 1..20");
            List<Lot> lots = new List<Lot>();
            foreach (Lot l in LotManager.AllLots)
                if (l.LotType == LotType.Commercial && l.StreetParking != null) lots.Add(l);
            if (lots.Count == 0) throw new McpException("no commercial lot with street parking");
            int placed = 0, noSpot = 0, noRoad = 0;
            for (int i = 0; i < n; i++)
            {
                Lot lot = RandomUtil.GetRandomObjectFromList(lots);
                Sims3.Gameplay.Objects.Vehicles.CarNpcWithNoDriver car =
                    Sims3.Gameplay.GlobalFunctions.CreateObjectOutOfWorld("CarTaxiLightweight", ProductVersion.EP3) as Sims3.Gameplay.Objects.Vehicles.CarNpcWithNoDriver;
                if (car == null) throw new McpException("CarTaxiLightweight not created (Late Night content missing?)");
                Matrix44 mat = default(Matrix44);
                if (!lot.StreetParking.GetParkingSpotForCar(car, ref mat)) { car.Destroy(); noSpot++; continue; }
                Vector3[] pos;
                Quaternion[] orient;
                if (World.FindPlaceOnRoadOffScreen(car.Proxy, mat.pos.V3, FindPlaceOnRoadOption.Road, 100f, out pos, out orient))
                {
                    car.GeneratingClassName = "ts3agent";
                    car.PlaceAt(pos[0], mat.at.V3, null);
                    car.DriveAroundFor(2f, Sims3.Gameplay.Utilities.TimeUnit.Hours);
                    placed++;
                }
                else { car.Destroy(); noRoad++; }
                lot.StreetParking.FreeParkingSpotForCar(car);
            }
            Dictionary<string, object> r = Util.Obj();
            r["placed"] = placed;
            r["no_spot"] = noSpot;
            r["no_road"] = noRoad;
            return r;
        }

        // Point for {to}: "pond" = representative point of the world pond nearest the Sim; "ocean" = 10 m into the sea
        // found on rings of 10..400 m round the Sim; "far" = centre of the community lot farthest from the Sim; "far2" = centre of
        // the community lot farthest from both the Sim and "far" (FindLotByName).
        // Vector3.Invalid when the world has none.
        static Vector3 RarePoint(Sim s, string to)
        {
            to = to.Trim().ToLowerInvariant();
            if (to == "ocean" || to == "sea") return SeaPoint(s);
            float best = -1f;
            Vector3 pos = Vector3.Invalid;
            if (to == "pond")
            {
                IEnumerable<Sims3.Gameplay.Pools.WorldPond> ponds = Sims3.Gameplay.Pools.PondManager.GetAllWorldPonds();
                if (ponds == null) return pos;
                best = float.MaxValue;
                foreach (Sims3.Gameplay.Pools.WorldPond p in ponds)
                {
                    Vector3 c = p.RepresentativePondPosition();
                    float d = (c - s.Position).LengthSqr();
                    if (d < best) { best = d; pos = c; }
                }
                return pos;
            }
            if (to == "far")
            {
                Lot far = FarLot(s);
                return far != null ? far.GetCenterPosition() : pos;
            }
            if (to == "far2") return FindLotByName("far2").GetCenterPosition();  // docs/202 §3.4.2: a second unseen lot, never the household's
            if (to == "island")
            {
                Lot isl = IslandLot(s, false);
                return isl != null ? isl.GetCenterPosition() : pos;
            }
            if (to == "home")  // the active household's lot (docs/197 §7 goalhhexempt repro)
            {
                Lot home = Household.ActiveHouseholdLot;
                return home != null ? home.GetCenterPosition() : pos;
            }
            throw new McpException("to must be pond, ocean, far, far2, island or home");
        }

        // "island": the community lot whose straight line from the Sim crosses the most sea (samples every 10 m, at least
        // 100 m of sea), so a walk there needs a boat, a water taxi or a swim (the unseen-boats repro, docs/143 §8.10);
        // null when no lot is that far over water (a world without sea or islands). land: no dive lot or resort (crowd_visit_lot "island").
        static Lot IslandLot(Sim s, bool land)
        {
            Lot best = null;
            float bestSea = 99f;
            Vector3 a = s.Position;
            foreach (Lot l in LotManager.AllLots)
            {
                if (l == null || !l.IsCommunityLot || l.IsWorldLot) continue;
                if (land && (l.CommercialLotSubType == CommercialLotSubType.kEP10_Diving || l.CommercialLotSubType == CommercialLotSubType.kEP10_Resort)) continue;
                Vector3 b = l.GetCenterPosition(), d = b - a;
                float len = d.Length(), sea = 0f;
                for (float t = 0f; t < len; t += 10f)
                {
                    Vector3 p = a + d * (t / len);
                    if ((TerrainType)World.GetTerrainType(p.x, p.z, 0) == TerrainType.WorldSea) sea += 10f;
                }
                if (sea > bestSea) { bestSea = sea; best = l; }
            }
            return best;
        }

        // The community lot (not the world lot) farthest from the Sim; null when the world has none.
        static Lot FarLot(Sim s)
        {
            Lot far = null;
            float best = -1f;
            foreach (Lot l in LotManager.AllLots)
            {
                if (l == null || !l.IsCommunityLot || l.IsWorldLot) continue;
                float d = (l.GetCenterPosition() - s.Position).LengthSqr();
                if (d > best) { best = d; far = l; }
            }
            return far;
        }

        static Vector3 SeaPoint(Sim s)
        {
            for (float rad = 10f; rad <= 400f; rad += 10f)
                for (int i = 0; i < 24; i++)
                {
                    double ang = i * Math.PI / 12;
                    Vector3 dir = new Vector3((float)Math.Cos(ang), 0f, (float)Math.Sin(ang));
                    Vector3 p = s.Position + dir * rad, far = s.Position + dir * (rad + 10f);
                    if ((TerrainType)World.GetTerrainType(p.x, p.z, 0) == TerrainType.WorldSea
                        && (TerrainType)World.GetTerrainType(far.x, far.z, 0) == TerrainType.WorldSea) return far;
                }
            return Vector3.Invalid;
        }

        static Vector3 RequireRarePoint(Sim s, string to)
        {
            Vector3 p = RarePoint(s, to);
            if (p == Vector3.Invalid) throw new McpException("no " + to + " point in this world near " + s.FullName);
            return p;
        }

        static void QueueGoHere(Sim s, Vector3 pos)
        {
            InteractionInstance ii = Terrain.GoHere.GetSingleton(s, pos).CreateInstance(Terrain.Singleton, s,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            Terrain.GoHere gh = ii as Terrain.GoHere;
            if (gh != null) gh.SetDestination(pos, false);
            if (!s.InteractionQueue.Add(ii)) throw new McpException("could not queue Go Here for " + s.FullName);
        }

        // map_view {on?: bool}: the map-view button's path (CameraModel.ToggleMapView -> Camera.ToggleMapView ->
        // CameraController.EnableMapViewMode / DisableMapViewMode, the script camera ICs ts3native [UnseenRootMotion] hooks,
        // docs/228 §11.6). on given and already in that state: nothing. Returns the state before and after.
        static object MapView(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            bool before = CameraController.IsMapViewModeEnabled();
            r["before"] = before;
            if (!a.Has("on") || a.Bool("on", !before) != before) Sims3.UI.Responder.Instance.CameraModel.ToggleMapView();
            r["after"] = CameraController.IsMapViewModeEnabled();
            r["pos"] = Pos(CameraController.GetPosition());
            return r;
        }

        // focus_sim {pick?: "far" | "near", index?: 0}: the portrait click's path (HudModel.MoveCameraToSim -> Camera.FocusOnSim
        // -> CameraController.RequestLerpToTarget, plus FollowSim for a routing Sim) onto a spawn_sims Sim; pick far (default)
        // = the index-th farthest from the camera target (an unseen Sim, so Mode 2's camera-IC hold is what keeps it exact),
        // near = nearest; active = the selected Sim (outfit_change changes the active household first). found false: no such
        // Sim in the world.
        static object FocusSim(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            Sim act = PlumbBob.SelectedActor;
            List<Sim> sims = a.Str("pick", "far") != "active" ? SpawnedInWorld() : act != null && act.InWorld ? new List<Sim> { act } : new List<Sim>();
            Vector3 t = CameraController.GetTarget();
            bool near = a.Str("pick", "far") == "near";
            sims.Sort(delegate (Sim x, Sim y) { int c = (x.Position - t).Length().CompareTo((y.Position - t).Length()); return near ? c : -c; });
            int i = (int)a.Long("index", 0);
            r["found"] = i >= 0 && i < sims.Count;
            if (!(bool)r["found"]) return r;
            Sim s = sims[i];
            r["sim"] = s.FullName;
            r["dist"] = (double)(s.Position - t).Length();
            r["routing"] = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting;
            Sims3.UI.Responder.Instance.HudModel.MoveCameraToSim(s.ObjectId);
            return r;
        }

        // Sims spawn_sims made that are in the world now.
        static List<Sim> SpawnedInWorld()
        {
            List<Sim> list = new List<Sim>();
            foreach (ulong id in SpawnedSims())
            {
                SimDescription sd = SimDescription.Find(id);
                if (sd != null && sd.CreatedSim != null && sd.CreatedSim.InWorld) list.Add(sd.CreatedSim);
            }
            return list;
        }

        // crowd_go {target | to | x,z, max?: 50, household?: false, cancel?: false}: every spawn_sims Sim (and the active household with
        // household: true) walks to the same point at once (Go Here, user-directed), so the arrivals steer round each other.
        static object CrowdGo(Args a)
        {
            RequireLive();
            Sim me = Util.ActiveSim();
            Vector3 pos = WalkGoal(a, me);
            if (pos == Vector3.Invalid) throw new McpException("give target, to or x,z");
            List<Sim> crowd = SpawnedInWorld();
            if (a.Bool("household", false) && Household.ActiveHousehold != null)
                foreach (Sim s in Household.ActiveHousehold.AllActors) if (!crowd.Contains(s)) crowd.Add(s);
            int max = (int)a.Long("max", 50);
            List<object> sent = new List<object>();
            foreach (Sim s in crowd)
            {
                if (sent.Count >= max) break;
                try
                {
                    if (a.Bool("cancel", false)) s.InteractionQueue.CancelAllInteractions();  // docs/202 D repros: end the last loop's interaction first
                    QueueGoHere(s, pos);
                    sent.Add(s.FullName);
                }
                catch (Exception e) { sent.Add(s.FullName + ": " + e.Message); }
            }
            if (sent.Count == 0) throw new McpException("no crowd: spawn_sims first (or household: true)");
            Dictionary<string, object> r = Util.Obj();
            r["sent"] = sent;
            r["x"] = Util.Round(pos.x); r["z"] = Util.Round(pos.z);
            return r;
        }

        // crowd_visit_lot {lot, how?: "visit", autonomous?: false, cancel?: false, max?: 50, min?: 0} (docs/202 §3.6.4): every spawn_sims Sim
        // queues the game's own lot trip to `lot` (FindLotByName: "far", "far2", "home" or a name), as sim_visit_lot does for one
        // Sim: VisitCommunityLot.Singleton / VisitLot.Singleton, or with how "goto" GoToCommunityLot / GoToLot. With LynMods
        // University More Shell Entries installed, VisitCommunityLot.Singleton is its VisitCommunityLotLabLecture (the trip kind the
        // shortcuts miss, unseenfbtrip). autonomous false = player-directed (skips the autonomous-only visit gates). A refused Sim's entry
        // says which Test gate refused ("not queued (Def_TestFailed: <tooltip>)"); min: fewer queued than that throws (smoke gate).
        // docs/222 §11.12: VisitLot onto a household's home fails Lot.CommonVisitTest at 0-5 h, when nobody is home, or when greeted;
        // how "goto" (GoToLot: front door or mailbox, outside) has none of those gates.
        static object CrowdVisitLot(Args a)
        {
            RequireLive();
            Lot lot = FindLotByName(a.Str("lot"));
            bool go = string.Equals(a.Str("how", "visit"), "goto", StringComparison.OrdinalIgnoreCase);
            InteractionDefinition def = go ? (lot.IsCommunityLot ? GoToCommunityLot.Singleton : GoToLot.Singleton)
                : lot.IsCommunityLot ? VisitCommunityLot.Singleton : VisitLot.Singleton;
            bool auto = a.Bool("autonomous", false);
            InteractionPriority pri = new InteractionPriority(auto ? InteractionPriorityLevel.Autonomous : InteractionPriorityLevel.UserDirected);
            int max = (int)a.Long("max", 50), queued = 0;
            List<object> sent = new List<object>();
            foreach (Sim s in SpawnedInWorld())
            {
                if (sent.Count >= max) break;
                try
                {
                    if (a.Bool("cancel", false)) s.InteractionQueue.CancelAllInteractions();
                    bool q = s.InteractionQueue.Add(def.CreateInstance(lot, s, pri, auto, true));
                    if (q) queued++;
                    string why = null;
                    sent.Add(q ? s.FullName : s.FullName + ": not queued (" + VisitTestResult(def, lot, s, pri, auto, out why) + (why != null ? ": " + why : "") + ")");
                }
                catch (Exception e) { sent.Add(s.FullName + ": " + e.Message); }
            }
            if (sent.Count == 0) throw new McpException("no crowd: spawn_sims first");
            Dictionary<string, object> r = Util.Obj();
            r["sent"] = sent;
            r["queued"] = queued;
            r["lot"] = lot.Name;
            r["interaction"] = def.GetType().DeclaringType != null ? def.GetType().DeclaringType.FullName : def.GetType().FullName;
            if (queued < (int)a.Long("min", 0)) throw new McpException("crowd_visit_lot: only " + queued + " of " + sent.Count + " queued: " + string.Join(", ", sent.ConvertAll(o => (string)o).ToArray()));
            return r;
        }

        // houseboat_leave {lot, max?: 20} (docs/248 §10): every townie standing on a houseboat lot queues the game's own lot trip to `lot`, so unseenboatstart gets
        // its case at once; {check: true}: a Sim a houseboat lot still lists that stands off that lot is an error (the boat kept a Sim that left it).
        static object HouseboatLeave(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            List<object> sent = new List<object>(), stale = new List<object>();
            bool check = a.Bool("check", false);
            Lot lot = check ? null : FindLotByName(a.Str("lot"));
            InteractionDefinition def = check ? null : lot.IsCommunityLot ? VisitCommunityLot.Singleton : VisitLot.Singleton;
            int max = (int)a.Long("max", 20), boats = 0;
            InteractionPriority pri = new InteractionPriority(InteractionPriorityLevel.UserDirected);
            foreach (Lot l in LotManager.AllLots)
            {
                if (l == null || !l.IsHouseboatLot()) continue;
                boats++;
                foreach (Sim s in new List<Sim>(l.GetSims()))
                {
                    if (check) { if (LotManager.GetLotAtPoint(s.Position) != l) stale.Add(s.FullName + " on " + l.Name); continue; }
                    if (sent.Count >= max || s.Household == Household.ActiveHousehold || s.IsPet) continue;
                    try { sent.Add(s.InteractionQueue.Add(def.CreateInstance(lot, s, pri, false, true)) ? s.FullName : s.FullName + ": not queued"); }
                    catch (Exception e) { sent.Add(s.FullName + ": " + e.Message); }
                }
            }
            if (stale.Count > 0) throw new McpException("houseboat lots still list Sims that left: " + string.Join(", ", stale.ConvertAll(o => (string)o).ToArray()));
            r["houseboats"] = boats;
            r["sent"] = sent;
            if (lot != null) r["lot"] = lot.Name;
            return r;
        }

        // kill_sim {sim? | pick?: "routing" | "any", death?: "Electrocution"}: Sim.Kill (Urnstone.KillSim at MaxDeath
        // priority, which cancels the walk). Without sim it picks a spawn_sims Sim, one that is routing right now when
        // pick is "routing" (the default; falls back to any spawned Sim, reported in "routing"). Never the active Sim
        // unless named.
        static object KillSim(Args a)
        {
            RequireLive();
            Sim victim = null;
            bool routing = false;
            if (a.Has("sim")) { victim = Util.FindSim(a, "sim"); routing = victim.IsRouting; }
            else
            {
                List<Sim> spawned = SpawnedInWorld();
                if (a.Str("pick", "routing") == "routing")
                    foreach (Sim s in spawned) if (s.IsRouting) { victim = s; routing = true; break; }
                if (victim == null && spawned.Count > 0) victim = spawned[0];
            }
            if (victim == null) throw new McpException("no spawned Sim to kill: spawn_sims first or pass sim");
            SimDescription.DeathType dt = (SimDescription.DeathType)Enum.Parse(typeof(SimDescription.DeathType), a.Str("death", "Electrocution"), true);
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = victim.FullName;
            r["routing"] = routing;
            r["death"] = dt.ToString();
            r["killed"] = victim.Kill(dt);
            return r;
        }

        // Destination of a walk for commands that take {target | to | x,z}; Vector3.Invalid when none is given.
        // crowd_do {lot, object, interaction, max?: 50}: every spawn_sims Sim queues the pie-menu interaction matching `interaction`
        // on the first object on `lot` (FindLotByName: "far", "far2", "home" or a name) whose catalog name contains `object`,
        // user-directed, as do_interaction does for one Sim (docs/202 §3.5 D repros: a walk to an object or slot on another lot,
        // planned by a RoutingComponent.RouteTo* helper in the Sim's own interaction). Sims whose menu lacks it are listed with why.
        // `object` and `interaction` take '|' alternatives. The object is the first one (named objects first, outdoor before
        // indoor: D needs an outdoor ground goal) whose menu for the first crowd Sim offers one of the interactions; when no named
        // object does (far2 differs per run: a park, a graveyard), any object on the lot is tried, up to 300 menus.
        // look: true also points the camera at the object, so its lot is in view (D-arrive repro: goal seen, starts unseen).
        // lot "farobj" (docs/202 §3.6.3): community lots ranked as "far2" (farthest from both the active Sim and "far"), outdoor
        // objects only; the first lot with one that offers an interaction wins (far2 alone may be a beach or graveyard without one).
        static object CrowdDo(Args a)
        {
            RequireLive();
            bool scan = string.Equals(a.Str("lot"), "farobj", StringComparison.OrdinalIgnoreCase);
            List<Lot> lots = scan ? FarObjLots() : new List<Lot> { FindLotByName(a.Str("lot")) };
            string objName = a.Str("object", "*");
            string[] names = objName.Split('|'), labels = a.Str("interaction").Split('|');
            List<Sim> crowd = SpawnedInWorld();
            if (crowd.Count == 0) throw new McpException("no crowd: spawn_sims first");
            Lot lot = null;
            GameObject target = null;
            string label = null;
            int tried = 0, namedAll = 0;
            List<string> lotsTried = new List<string>();
            foreach (Lot l in lots)
            {
                if (tried >= 300) break;
                lot = l;
                lotsTried.Add(l.Name);
                List<GameObject> named = new List<GameObject>(), rest = new List<GameObject>();
                foreach (GameObject o in l.GetObjects<GameObject>())
                {
                    if (o == null || o is Sim || (scan && o.RoomId != 0)) continue;
                    string n = Util.ObjectName(o);
                    bool hit = false;
                    foreach (string w in names) if (w.Length > 0 && w != "*" && n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                    (hit ? named : rest).Add(o);
                }
                namedAll += named.Count;
                foreach (List<GameObject> group in new List<GameObject>[] { OutdoorFirst(named), OutdoorFirst(rest) })
                {
                    foreach (GameObject o in group)
                    {
                        if (tried++ >= 300) break;
                        label = CrowdOffer(crowd[0], o, labels, a);
                        if (label != null) { target = o; break; }
                    }
                    if (target != null) break;
                }
                if (target != null) break;
            }
            if (target == null)
                throw new McpException("no " + (scan ? "outdoor " : "") + "object on " + string.Join(", ", lotsTried.ToArray()) + " offers '" + a.Str("interaction") + "' to " + crowd[0].FullName + " (" + namedAll + " named like '" + objName + "', " + tried + " menus tried)");
            if (a.Bool("look", false)) CameraController.SetPositionAndTarget(target.Position + new Vector3(0f, 25f, -25f), target.Position);
            int max = (int)a.Long("max", 50);
            List<object> sent = new List<object>();
            foreach (Sim s in crowd)
            {
                if (sent.Count >= max) break;
                try
                {
                    GameObjectHit hit = HitFor(target, a);
                    MenuEntry e = MatchEntry(BuildMenu(s, target, hit), label);
                    InteractionInstanceParameters p = new InteractionInstanceParameters(e.Iop, s, new InteractionPriority(InteractionPriorityLevel.UserDirected),
                        Sims3.Gameplay.Autonomy.AutonomySearchType.None, InteractionFlags.UserDirected, hit, null, false);
                    InteractionInstance inst = e.Iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
                    sent.Add(inst != null && s.InteractionQueue.Add(inst) ? s.FullName : s.FullName + ": refused");
                }
                catch (ResetException) { throw; }
                catch (Exception ex) { sent.Add(s.FullName + ": " + ex.Message); }
            }
            Dictionary<string, object> r = Util.Obj();
            r["sent"] = sent;
            r["lot"] = lot.Name;
            r["lots_tried"] = lotsTried;
            r["object"] = Util.ObjectName(target);
            r["interaction"] = label;
            r["outdoor"] = target.RoomId == 0;
            return r;
        }

        // crowd_do lot "farobj": community lots except "far", farthest from both the active Sim and "far" first ("far2" = the first)
        static List<Lot> FarObjLots()
        {
            Sim me = Util.ActiveSim();
            Lot far = FarLot(me);
            if (far == null) throw new McpException("no community lot in this world");
            Vector3 f = far.GetCenterPosition();
            List<KeyValuePair<float, Lot>> ranked = new List<KeyValuePair<float, Lot>>();
            foreach (Lot l in LotManager.AllLots)
            {
                if (l == null || !l.IsCommunityLot || l.IsWorldLot || l == far) continue;
                Vector3 c = l.GetCenterPosition();
                ranked.Add(new KeyValuePair<float, Lot>(Math.Min((c - me.Position).LengthSqr(), (c - f).LengthSqr()), l));
            }
            if (ranked.Count == 0) throw new McpException("no second community lot in this world");
            ranked.Sort(delegate(KeyValuePair<float, Lot> x, KeyValuePair<float, Lot> y) { return y.Key.CompareTo(x.Key); });
            return ranked.ConvertAll(delegate(KeyValuePair<float, Lot> kv) { return kv.Value; });
        }

        static List<GameObject> OutdoorFirst(List<GameObject> objs)
        {
            List<GameObject> r = objs.FindAll(delegate(GameObject o) { return o.RoomId == 0; });
            r.AddRange(objs.FindAll(delegate(GameObject o) { return o.RoomId != 0; }));
            return r;
        }

        // the first of `labels` that the Sim's pie menu on o offers (enabled, one match), else null
        static string CrowdOffer(Sim s, GameObject o, string[] labels, Args a)
        {
            List<MenuEntry> menu;
            try { menu = BuildMenu(s, o, HitFor(o, a)); }
            catch (ResetException) { throw; }
            catch (Exception) { return null; }
            foreach (string l in labels)
            {
                try { MatchEntry(menu, l); return l; }
                catch (McpException) { }
            }
            return null;
        }

        static Vector3 WalkGoal(Args a, Sim me)
        {
            if (a.Has("target"))
            {
                GameObject t = FindTarget(a, "target");
                return t is Lot ? LotViewPoint((Lot)t) : t.Position;
            }
            if (a.Has("to")) return RequireRarePoint(me, a.Str("to"));
            if (a.Has("x")) return new Vector3((float)a.Double("x"), (float)a.Double("y", 0), (float)a.Double("z"));
            return Vector3.Invalid;
        }

        // zombies {spawn?: 0, spawned?: false, target | to | x,z?}: Supernatural full-moon zombies on demand (docs/117).
        // spawn: n townies that pass the game's own LunarCycleManager.IsZombifiable are instantiated on the active Sim's lot
        // and get Sim.BeReanimatedSolo at CriticalNPCBehavior, as LunarCycleManager.PossiblySpawnZombie does without its 20%
        // roll (grave-rise animation, Zombie transformation buff). spawned: every spawn_sims Sim gets the Zombie buff
        // (Origin.FromFullMoon, what TriggerLunarEvents gives its two picks). With a goal every zombie then gets a Go Here
        // there. Our zombies are not in the game's sunrise list: they stay zombies until the game ends.
        static object Zombies(Args a)
        {
            RequireLive();
            Sim me = Util.ActiveSim();
            List<Sim> zs = new List<Sim>();
            List<object> names = new List<object>();
            int n = (int)a.Long("spawn", 0);
            if (n > 0)
            {
                MethodInfo ok = typeof(Sims3.Gameplay.Controllers.LunarCycleManager).GetMethod("IsZombifiable", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (ok == null) throw new McpException("LunarCycleManager.IsZombifiable not found");
                foreach (SimDescription sd in Household.AllTownieSimDescriptions())
                {
                    if (zs.Count >= n) break;
                    if (sd == null || (sd.CreatedSim != null && !sd.CreatedSim.InWorld) || !(bool)ok.Invoke(null, new object[] { sd })) continue;
                    Sim z = sd.CreatedSim ?? sd.Instantiate(me.LotCurrent);
                    if (z == null) continue;
                    InteractionInstance ii = Sim.BeReanimatedSolo.Singleton.CreateInstance(z, z,
                        new InteractionPriority(InteractionPriorityLevel.CriticalNPCBehavior), false, false);
                    z.InteractionQueue.AddNext(ii);
                    zs.Add(z); names.Add(z.FullName + " (risen)");
                }
            }
            if (a.Bool("spawned", false))
                foreach (Sim s in SpawnedInWorld())
                {
                    bool added = s.BuffManager != null && s.BuffManager.AddElement(BuffNames.Zombie, Origin.FromFullMoon);
                    if (added) zs.Add(s);
                    names.Add(s.FullName + (added ? " (buff)" : " (buff refused)"));
                }
            if (zs.Count == 0) throw new McpException("no zombie made: spawn n (needs zombifiable townies) or spawned: true after spawn_sims");
            Vector3 goal = WalkGoal(a, me);
            int walking = 0;
            if (goal != Vector3.Invalid)
                foreach (Sim z in zs)
                    try { QueueGoHere(z, goal); walking++; } catch (Exception e) { names.Add(z.FullName + ": " + e.Message); }
            Dictionary<string, object> r = Util.Obj();
            r["zombies"] = names;
            r["walking"] = walking;
            return r;
        }
    }
}
