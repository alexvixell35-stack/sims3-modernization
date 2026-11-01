using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Routing-freeze repros (docs/169 "routing"): set up the state the game's own route callers react to, then let those
    // callers plan, in a loop. The plans are the game's: its stuck-check alarm body (and the reset it ends in), its Go Here
    // interaction, its build/buy exit replans. These commands only place Sims, pick goals and arm the game's own alarms.
    public static partial class Handlers
    {
        static void RegisterRouteFreeze()
        {
            Dispatcher.Register("strand_crowd", StrandCrowd);
            Dispatcher.Register("stuck_check_fire", StuckCheckFire);
            Dispatcher.Register("crowd_unreachable", CrowdUnreachable);
            Dispatcher.Register("fenced_place", FencedPlace);
            Dispatcher.Register("crowd_sea_lot", CrowdSeaLot);
        }

        // unroutable spots found per lot (each sample costs one plan in the agent's task: found once, reused every loop)
        static readonly Dictionary<ulong, List<Vector3>> sUnroutable = new Dictionary<ulong, List<Vector3>>();

        // Up to `want` spots of `lot` that fail the game's Sim.IsPointInLotSafelyRoutable (enclosed room, fenced corner),
        // sampled as sim_strand does but without pool tiles (a pool is reachable by swimming).
        static List<Vector3> UnroutableSpots(Sim probe, Lot lot, int tries, int want)
        {
            List<Vector3> spots;
            if (sUnroutable.TryGetValue(lot.LotId, out spots) && spots.Count > 0) return spots;
            spots = new List<Vector3>();
            for (int i = 0; i < tries && spots.Count < want; i++)
            {
                Vector3 p = lot.GetRandomPosition(true, true, TerrainType.LotTerrain | TerrainType.LotFloor);
                if (p == Vector3.Invalid || probe.IsPointInLotSafelyRoutable(lot, p)) continue;
                spots.Add(p);
            }
            if (spots.Count > 0) sUnroutable[lot.LotId] = spots;
            return spots;
        }

        // Lot for {x, z} (the lot at that point) or {lot}: a name (FindLotByName: "home", "far", exact or contains) or "sea" (SeaLot).
        static Lot RouteLot(Args a, string def)
        {
            if (a.Has("x"))
            {
                Lot at = LotManager.GetLotAtPoint(new Vector3((float)a.Double("x"), 0f, (float)a.Double("z")));
                if (at == null) throw new McpException("no lot at x,z");
                return at;
            }
            string name = a.Str("lot", def);
            if (string.Equals(name, "sea", StringComparison.OrdinalIgnoreCase))
            {
                Lot sea = SeaLot();
                if (sea == null) throw new McpException("no lot with sea inside its area in this world (community or residential)");
                return sea;
            }
            Lot lot = FindLotByName(name);
            if (lot == null) throw new McpException("no lot " + name);
            return lot;
        }

        // strand_crowd {lot?: "far" | x,z, tries?: 60, max?: 50}: every spawn_sims Sim stands on an unroutable spot of `lot`
        // (interactions cancelled), the state the game's stuck check and reset handle. Not the active household's lot:
        // the stuck check skips that one. Spots are found once per lot and reused (several Sims may share a spot).
        static object StrandCrowd(Args a)
        {
            RequireLive();
            Lot lot = RouteLot(a, "far");
            if (lot.IsWorldLot || lot == Household.ActiveHouseholdLot) throw new McpException("needs a non-world lot other than the active household's (the stuck check skips that lot)");
            List<Sim> crowd = SpawnedInWorld();
            if (crowd.Count == 0) throw new McpException("no crowd: spawn_sims first");
            List<Vector3> spots = UnroutableSpots(crowd[0], lot, (int)a.Long("tries", 60), 4);
            if (spots.Count == 0) throw new McpException("no unroutable spot on " + lot.Name + " (try another lot or more tries)");
            int max = (int)a.Long("max", 50), n = 0;
            foreach (Sim s in crowd)
            {
                if (n >= max) break;
                s.InteractionQueue.CancelAllInteractions();
                s.SetPosition(spots[n % spots.Count]);
                n++;
            }
            Dictionary<string, object> r = Util.Obj();
            r["lot"] = lot.Name;
            r["stranded"] = n;
            r["spots"] = spots.Count;
            return r;
        }

        // stuck_check_fire {attempt?: 3, delay_min?: 1, lot?: name|"sea"|"any" | x,z}: arms the game's own stuck check
        // (LotManager.RunStuckCheckHelper, internal) for every spawn_sims Sim on `lot` (default: any lot), as its 3 AM
        // Init does but with the attempt number given and `delay_min` sim-minutes instead of 20-60: the game's alarm on
        // AlarmManager.Global runs the game's RunStuckCheck (10 plans to random lot points incl. sea; attempt 3 and all
        // failed -> the game's SetObjectToReset -> safe-location plans). The Sim's position is captured now: a Sim that
        // moves before the alarm is skipped by the game, as at 3 AM.
        static object StuckCheckFire(Args a)
        {
            RequireLive();
            Type helper = typeof(LotManager).GetNestedType("RunStuckCheckHelper", BindingFlags.NonPublic | BindingFlags.Public);
            if (helper == null) throw new McpException("LotManager.RunStuckCheckHelper not found (game version?)");
            ConstructorInfo ctor = helper.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(ulong), typeof(Vector3), typeof(uint) }, null);
            MethodInfo exec = helper.GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Instance);
            if (ctor == null || exec == null) throw new McpException("RunStuckCheckHelper ctor(ulong, Vector3, uint) or Execute() not found");
            uint attempt = (uint)a.Long("attempt", 3);
            float delay = (float)a.Double("delay_min", 1);
            string lotName = a.Str("lot", "any");
            Lot only = !a.Has("x") && string.Equals(lotName, "any", StringComparison.OrdinalIgnoreCase) ? null : RouteLot(a, "any");
            int armed = 0, skippedHome = 0;
            foreach (Sim s in SpawnedInWorld())
            {
                if (only != null && s.LotCurrent != only) continue;
                if (s.LotCurrent == Household.ActiveHouseholdLot) { skippedHome++; continue; }
                object h = ctor.Invoke(new object[] { s.SimDescription.SimDescriptionId, s.Position, attempt });
                AlarmTimerCallback cb = (AlarmTimerCallback)Delegate.CreateDelegate(typeof(AlarmTimerCallback), h, exec);
                AlarmManager.Global.AddAlarm(delay, TimeUnit.Minutes, cb, "RunStuckCheck(ts3agent #" + attempt + ":" + s.FullName, AlarmType.NeverPersisted, s);
                armed++;
            }
            if (armed == 0) throw new McpException("no spawn_sims Sim off the active household's lot" + (only != null ? " on " + only.Name : "") + " (strand_crowd or crowd_sea_lot first)");
            Dictionary<string, object> r = Util.Obj();
            r["armed"] = armed;
            r["skipped_home_lot"] = skippedHome;
            r["attempt"] = attempt;
            r["delay_min"] = delay;
            return r;
        }

        // fenced_place {lot?: "far", tries?: 60, max?: 5, height?: 12, back?: 12}: docs/202 §3.9.7 (D) repro. Places up to `max`
        // spawn_sims Sims on a spot of `lot` that fails IsPointInLotSafelyRoutable (fenced / enclosed, as crowd_unreachable finds it)
        // and lists them as placed by ts3fixes unseenroute (AppDomain "ts3fixes.unseenroute.placed"), as a wrong relaxed placement
        // would leave them; tags the spot for camera_to_tagged {key: "ts3fixes.unseenroute.fenced"}. When such a Sim is seen, D
        // must move it out. Asserts D's preconditions (error otherwise): the spot fails the game's check for EVERY placed Sim's
        // own route (D checks with the Sim's profile, not the active Sim's) and the lot entry point is routable for each (D's
        // target). The camera moves onto the spot BEFORE the Sims do, so no unseen window lets unseenroute relax them away
        // (2026-11-01 unseenroute6 run: 2 of the 5 were held/relaxed in the 5 s before camera_to_tagged).
        static object FencedPlace(Args a)
        {
            RequireLive();
            Lot lot = RouteLot(a, "far");
            if (lot.IsWorldLot) throw new McpException("needs a non-world lot");
            List<Vector3> spots = UnroutableSpots(Util.ActiveSim(), lot, (int)a.Long("tries", 60), 4);
            if (spots.Count == 0) throw new McpException("no unroutable spot on " + lot.Name + " (try another lot or more tries)");
            int max = (int)a.Long("max", 5);
            List<Sim> sims = new List<Sim>();
            foreach (Sim s in SpawnedInWorld()) { if (sims.Count >= max) break; sims.Add(s); }
            if (sims.Count == 0) throw new McpException("no Sims: spawn_sims first");
            Vector3 entry = lot.EntryPoint();
            entry.y = World.GetTerrainHeight(entry.x, entry.z);
            List<string> rejected = new List<string>();
            Vector3 p = Vector3.Invalid;
            foreach (Vector3 q in spots)
            {
                string bad = null;
                foreach (Sim s in sims)
                {
                    if (s.IsPointInLotSafelyRoutable(lot, q)) { bad = s.FullName + " reaches the lot edge from it"; break; }
                    if (!s.CreateRoute().IsPointRoutable(entry)) { bad = s.FullName + ": lot entry point not routable"; break; }
                }
                if (bad == null) { p = q; break; }
                rejected.Add(Util.Round(q.x) + "," + Util.Round(q.z) + ": " + bad);
            }
            if (p == Vector3.Invalid) throw new McpException("no spot on " + lot.Name + " unreachable for every placed Sim: " + string.Join("; ", rejected.ToArray()));
            CameraController.SetPositionAndTarget(new Vector3(p.x, p.y + (float)a.Double("height", 12), p.z - (float)a.Double("back", 12)), p);
            var placed = AppDomain.CurrentDomain.GetData("ts3fixes.unseenroute.placed") as Dictionary<ulong, bool>;
            if (placed == null) { placed = new Dictionary<ulong, bool>(); AppDomain.CurrentDomain.SetData("ts3fixes.unseenroute.placed", placed); }
            List<object> names = new List<object>();
            foreach (Sim s in sims)
            {
                s.InteractionQueue.CancelAllInteractions();
                s.SetPosition(p);
                placed[s.ObjectId.Value] = true;
                names.Add(s.FullName);
            }
            AppDomain.CurrentDomain.SetData("ts3fixes.unseenroute.fenced", new float[] { p.x, p.y, p.z });
            Dictionary<string, object> r = Util.Obj();
            r["lot"] = lot.Name;
            r["spot"] = new List<object> { p.x, p.y, p.z };
            r["sent"] = names;
            r["verified"] = "unreachable for all " + names.Count + ", entry routable";
            r["rejected"] = new List<object>(rejected.ToArray());
            return r;
        }

        // crowd_unreachable {lot?: "home" | x,z, tries?: 60, max?: 50, household?: false}: every spawn_sims Sim (and the household
        // with household: true) gets the game's Go Here to one spot of `lot` that fails Sim.IsPointInLotSafelyRoutable
        // (enclosed: no path from outside), so each Go Here plan floods the start's reachable area and fails (docs/142).
        // The spot is found once per lot. Without one: error (pick a lot with a closed room or fenced corner).
        static object CrowdUnreachable(Args a)
        {
            RequireLive();
            Sim me = Util.ActiveSim();
            Lot lot = RouteLot(a, "home");
            if (lot.IsWorldLot) throw new McpException("needs a non-world lot");
            List<Vector3> spots = UnroutableSpots(me, lot, (int)a.Long("tries", 60), 4);
            if (spots.Count == 0) throw new McpException("no unroutable spot on " + lot.Name + " (try another lot or more tries)");
            Vector3 goal = spots[0];
            List<Sim> crowd = SpawnedInWorld();
            if (a.Bool("household", false) && Household.ActiveHousehold != null)
                foreach (Sim s in Household.ActiveHousehold.AllActors) if (!crowd.Contains(s)) crowd.Add(s);
            int max = (int)a.Long("max", 50);
            List<object> sent = new List<object>();
            foreach (Sim s in crowd)
            {
                if (sent.Count >= max) break;
                try { QueueGoHere(s, goal); sent.Add(s.FullName); }
                catch (Exception e) { sent.Add(s.FullName + ": " + e.Message); }
            }
            if (sent.Count == 0) throw new McpException("no crowd: spawn_sims first (or household: true)");
            Dictionary<string, object> r = Util.Obj();
            r["lot"] = lot.Name;
            r["sent"] = sent.Count;
            r["x"] = Util.Round(goal.x); r["z"] = Util.Round(goal.z);
            return r;
        }

        // The community lot whose area holds the most sea: GetRandomPosition with WorldSea only, 16 draws per lot, no plans.
        // The stuck check draws its points with WorldSea in the mask, so a Sim standing still here gets sea goals (no path,
        // the whole reachable area flooded first: the SV crowd 7 s shape, docs/163 §3 and docs/169).
        // Sunset Valley has no community lot with sea in its area (sv-route-seacheck-freeze FAIL 2026-10-23): fall back to a
        // residential lot that is not the active household's home (the stuck check skips that one), 32 draws per lot.
        static Lot SeaLot()
        {
            Lot best = SeaLotWhere(true, 16);
            return best ?? SeaLotWhere(false, 32);
        }

        static Lot SeaLotWhere(bool community, int draws)
        {
            Household active = Household.ActiveHousehold;
            Lot home = active != null ? active.LotHome : null;
            Lot best = null;
            int bestHits = 0;
            foreach (Lot l in LotManager.AllLots)
            {
                if (l == null || l.IsWorldLot || l.IsCommunityLot != community || l == home) continue;
                int hits = 0;
                for (int i = 0; i < draws; i++)
                    if (l.GetRandomPosition(false, true, TerrainType.WorldSea) != Vector3.Invalid) hits++;
                if (hits > bestHits) { bestHits = hits; best = l; }
            }
            return best;
        }

        // crowd_sea_lot {max?: 50}: every spawn_sims Sim gets the game's Go Here to the centre of SeaLot (the stuck-check
        // setup of the SV 7 s repro). Error when the world has no lot with sea inside.
        static object CrowdSeaLot(Args a)
        {
            RequireLive();
            Lot lot = SeaLot();
            if (lot == null) throw new McpException("no lot with sea inside its area in this world (community or residential)");
            Vector3 goal = lot.GetCenterPosition();
            int max = (int)a.Long("max", 50), sent = 0;
            foreach (Sim s in SpawnedInWorld())
            {
                if (sent >= max) break;
                try { QueueGoHere(s, goal); sent++; } catch (Exception) { }
            }
            if (sent == 0) throw new McpException("no crowd: spawn_sims first");
            Dictionary<string, object> r = Util.Obj();
            r["lot"] = lot.Name;
            r["sent"] = sent;
            return r;
        }
    }
}
