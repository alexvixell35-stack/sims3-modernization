using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Situations;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Agent
{
    // Repro steps (tools/harness/repro.ts): put a Sim somewhere and give it a trip, so a bug that depends on a place can
    // be reproduced on demand instead of waiting for it.
    public static partial class Handlers
    {
        static void RegisterRepro()
        {
            Dispatcher.Register("place_sim", PlaceSim);
            Dispatcher.Register("setup_sims", SetupSims);
            Dispatcher.Register("sim_go_home", SimGoHome);
            Dispatcher.Register("sim_strand", SimStrand);
            Dispatcher.Register("sim_where", SimWhere);
            Dispatcher.Register("strand_many", StrandMany); // HitchReproHandlers.cs
            Dispatcher.Register("posture_chain_check", PostureChainCheck); // HitchReproHandlers.cs, issue #89
            Dispatcher.Register("sim_visit_lot", SimVisitLot);
            Dispatcher.Register("compat_visit", CompatVisit);
            Dispatcher.Register("compat_visit_status", CompatVisitStatus);
            Dispatcher.Register("sim_gone_watch", SimGoneWatch);
            Dispatcher.Register("sim_gone_load", SimGoneLoad); // SimGoneLoad.cs
            Dispatcher.Register("sim_route_to", SimRouteTo);
            Dispatcher.Register("route_replan", RouteReplan);
            Dispatcher.Register("sim_portal_trip", SimPortalTrip);
            Dispatcher.Register("thumbnails", Thumbnails);
            Dispatcher.Register("outfit_change", OutfitChange);
            Dispatcher.Register("pet_guard", PetGuard);
            Dispatcher.Register("wildflower_drain", WildflowerDrain);
            Dispatcher.Register("gc_streak_force", GcStreakForce);
            // Reflect roots for the docs/109 §6 repros (reflect_call "@door.SetLockType", go_here {target: "@upstairs"}).
            AddRoot("route", LastLiveRoute);
            AddRoot("door", NearestHomeDoor);
            AddRoot("upstairs", NearestUpstairsObject);
            AddRoot("gangway", RepoGangway);
        }

        // The last route sim_route_to planned: reflect_call "@route.Replan" replans a route object that already holds paths.
        static Route sLastRoute;

        // "@route": sLastRoute while its native handle and follower Sim still exist (a disposed route has handle 0).
        static object LastLiveRoute()
        {
            if (sLastRoute == null) throw new McpException("no live route: run sim_route_to first");
            if (sLastRoute.RouteHandle == 0 || sLastRoute.Follower == null)
                throw new McpException("no live route: the last sim_route_to route was disposed or its Sim is gone");
            return sLastRoute;
        }
        static Sims3.Gameplay.Objects.Island.Gangway sGangway;

        // "@door": the door (CommonDoor) on the home lot nearest the active Sim.
        static object NearestHomeDoor()
        {
            Sim me = Util.ActiveSim();
            GameObject best = null;
            float bestD = float.MaxValue;
            foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>(HomeLot()))
            {
                if (!(o is CommonDoor) || !o.InWorld) continue;
                float d = (o.Position - me.Position).LengthSqr();
                if (d < bestD) { bestD = d; best = o; }
            }
            if (best == null) throw new McpException("no door on the home lot");
            return best;
        }

        // "@upstairs": the in-world object (not a Sim) on an upper floor (Level >= 1) nearest the active Sim, on any lot.
        static object NearestUpstairsObject()
        {
            Sim me = Util.ActiveSim();
            GameObject best = null;
            float bestD = float.MaxValue;
            foreach (Lot l in LotManager.AllLots)
            {
                if (l == null) continue;
                foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>(l))
                {
                    if (o == null || !o.InWorld || o is Sim || o is Lot || o.Level < 1) continue;
                    float d = (o.Position - me.Position).LengthSqr();
                    if (d < bestD) { bestD = d; best = o; }
                }
            }
            if (best == null) throw new McpException("no object on an upper floor");
            return best;
        }

        // "@gangway": a port gangway without a houseboat, kept so the create call (ChangeHouseboatSize "Small") and the
        // remove call ("None") use the same one. Only worlds with ports (Isla Paradiso).
        static object RepoGangway()
        {
            if (sGangway != null && sGangway.InWorld) return sGangway;
            foreach (Lot l in LotManager.AllLots)
            {
                if (l == null || !l.IsPortLot()) continue;
                Sims3.Gameplay.Objects.Island.Gangway g = l.GetGangway();
                if (g != null && g.GetHouseboat() == null) return sGangway = g;
            }
            throw new McpException("no port gangway without a houseboat");
        }

        static Sims3.Gameplay.Utilities.AlarmHandle sWfDrainAlarm = Sims3.Gameplay.Utilities.AlarmHandle.kInvalidHandle;

        // wildflower_drain {lots?: 20, every_min?: 30, clear?: false, stop?: false}: docs/110 §7.5 repro (repros.json
        // wildflower-drain), so no test waits for spring. Replaces Wildflower's spring drain list with `lots` community lot
        // ids (cycling through them, shuffled with our own seed so the game's RNG stream is untouched) and starts our own
        // repeating alarm, every `every_min` sim minutes, that calls the game's drain handler OnGrowWildflowersOnLot (as the
        // game's hourly alarm does; with ts3fixes loaded that is the wildflower hook). The game's own alarm, if live, drains
        // the same list. clear: before each fire destroy uncollected wildflowers lying in the world, else the game's
        // 20-flower cap stops the drain after 20 placed. Our alarm ends itself when the list is empty; stop removes it.
        // save_pending "name": docs/247 §12, hits the kWildflowerTask save guard on purpose: a task waits for a flower out of
        // world on two ticks running (a try failed, the fire sleeps between tries), then saves as a new save of that name.
        // Returns the list size before and after, whether the game's alarm is live, can_spawn, the uncollected count and
        // out_of_world (uncollected flowers neither in the world nor in an inventory).
        static object WildflowerDrain(Args a)
        {
            RequireLive();
            const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic;
            Type wt = typeof(Sims3.Gameplay.Objects.Wildflower);
            FieldInfo lotsF = wt.GetField("sLotsToGetWildflowers", st), alarmF = wt.GetField("sGrowWildflowerInSpringAlarm", st);
            FieldInfo collected = wt.GetField("mCollected", BindingFlags.Instance | BindingFlags.NonPublic);
            // Public too: Patch swaps the hook's method header in, flags included, and GrowHook is public (docs/247 §12)
            MethodInfo grow = wt.GetMethod("OnGrowWildflowersOnLot", st | BindingFlags.Public);
            if (lotsF == null || alarmF == null || collected == null || grow == null) throw new McpException("Wildflower drain members not found:" + (lotsF == null ? " sLotsToGetWildflowers" : "") + (alarmF == null ? " sGrowWildflowerInSpringAlarm" : "") + (collected == null ? " mCollected" : "") + (grow == null ? " OnGrowWildflowersOnLot" : ""));
            Sims3.Gameplay.Utilities.AlarmManager.Global.RemoveAlarm(sWfDrainAlarm);
            sWfDrainAlarm = Sims3.Gameplay.Utilities.AlarmHandle.kInvalidHandle;
            Dictionary<string, object> r = Util.Obj();
            List<ulong> before = lotsF.GetValue(null) as List<ulong>;
            r["list_before"] = before == null ? -1 : before.Count;
            if (!a.Bool("stop", false))
            {
                int n = (int)a.Long("lots", 20);
                List<Lot> comm = LotManager.GetAllCommunityLots();
                if (comm == null || comm.Count == 0) throw new McpException("no community lots in this world");
                Random rnd = new Random(110);
                List<ulong> ids = new List<ulong>();
                for (int i = 0; i < n; i++)
                {
                    if (i % comm.Count == 0)
                        for (int k = comm.Count - 1; k > 0; k--) { int j = rnd.Next(k + 1); Lot t = comm[k]; comm[k] = comm[j]; comm[j] = t; }
                    ids.Add(comm[i % comm.Count].LotId);
                }
                lotsF.SetValue(null, ids);
                bool clear = a.Bool("clear", false);
                float every = (float)a.Double("every_min", 30);
                sWfDrainAlarm = Sims3.Gameplay.Utilities.AlarmManager.Global.AddAlarmRepeating(every, Sims3.Gameplay.Utilities.TimeUnit.Minutes, delegate
                {
                    List<ulong> l = lotsF.GetValue(null) as List<ulong>;
                    if (l == null || l.Count == 0)
                    {
                        Sims3.Gameplay.Utilities.AlarmManager.Global.RemoveAlarm(sWfDrainAlarm);
                        sWfDrainAlarm = Sims3.Gameplay.Utilities.AlarmHandle.kInvalidHandle;
                        return;
                    }
                    if (clear)
                        foreach (Sims3.Gameplay.Objects.Wildflower w in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Objects.Wildflower>())
                            if (w.InWorld && !(bool)collected.GetValue(w)) w.Destroy();
                    grow.Invoke(null, null);
                }, every, Sims3.Gameplay.Utilities.TimeUnit.Minutes, "ts3agent wildflower drain", Sims3.Gameplay.Utilities.AlarmType.NeverPersisted, null);
            }
            string saveName = a.Bool("stop", false) ? null : a.Str("save_pending", null);
            sWfSaveName = saveName;
            if (saveName != null) RunAsync("wildflower save_pending " + saveName, delegate
            {
                Sims3.UI.IOptionsModel om = Sims3.Gameplay.UI.Responder.Instance.OptionsModel;
                for (int seen = 0; sWfSaveName == saveName; Simulator.Sleep(0))
                {
                    seen = WfOutOfWorld(collected) > 0 ? seen + 1 : 0;
                    if (seen < 2 || !om.CanSaveGame()) continue;
                    sWfSaveName = null;
                    om.SaveName = saveName;
                    sAgentSavePending = true; // save_game's marks: save_watch labels this save "agent"
                    sAgentSaveAt = System.Environment.TickCount;
                    om.SaveGame(false, true, false);
                }
            });
            List<ulong> after = lotsF.GetValue(null) as List<ulong>;
            r["list_after"] = after == null ? -1 : after.Count;
            r["game_alarm_live"] = !((Sims3.Gameplay.Utilities.AlarmHandle)alarmF.GetValue(null)).Equals(Sims3.Gameplay.Utilities.AlarmHandle.kInvalidHandle);
            r["our_alarm_live"] = !sWfDrainAlarm.Equals(Sims3.Gameplay.Utilities.AlarmHandle.kInvalidHandle);
            r["can_spawn"] = Sims3.Gameplay.Objects.Wildflower.CanSpawnWildflowers();
            int un = 0;
            foreach (Sims3.Gameplay.Objects.Wildflower w in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Objects.Wildflower>())
                if (!(bool)collected.GetValue(w)) un++;
            r["uncollected"] = un;
            r["out_of_world"] = WfOutOfWorld(collected);
            return r;
        }

        static string sWfSaveName; // wildflower_drain save_pending: the save still to make; stop clears it

        static int WfOutOfWorld(FieldInfo collected)
        {
            int n = 0;
            foreach (Sims3.Gameplay.Objects.Wildflower w in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Objects.Wildflower>())
                if (!w.InWorld && !w.InInventory && !(bool)collected.GetValue(w)) n++;
            return n;
        }

        // pet_guard {action: "start" | "state" | "cancel", target?}: docs/84 §7.2 fix D-X repro (repros.json pet-guard-cancel).
        // start: a pet of the active household guards an object on the home lot (PetGuardSitutation.GuardObject, the Guard
        // interaction); with no pet in the household a random adult dog is made, joins it next to the object and is tracked
        // for remove_sims. target = an object id, else a random floor object on the home lot (no Sim, not carried, not a door, window, wall,
        // ceiling, vehicle or hidden object). Once the pet
        // arrives the guard adds a route discourage footprint on a hidden DynamicSlot (Route.AddDiscourageFootprint).
        // cancel: cancels the pet's interactions; the guard's Cleanup then removes the footprint and destroys the slot in the
        // same script call (Remove, then Destroy: the case D dropped). state: guarding / area present, for an "until" step;
        // state with restart:true starts a new guard (new random target) when the pet has none queued.
        // ensure: base saves (spring-saves): with no pet in the household, a random adult dog joins it on the home lot and is
        // NOT tracked, so it stays in the save; returns pets (the household's pet count) for an "until" check.
        static object PetGuard(Args a)
        {
            RequireLive();
            string action = a.Str("action", "start");
            Household h = Household.ActiveHousehold;
            if (h == null || h.LotHome == null) throw new McpException("no active household with a home lot");
            Sim pet = null;
            foreach (Sim s in h.Pets) if (s != null && s.InteractionQueue != null) { pet = s; break; }
            Dictionary<string, object> r = Util.Obj();
            if (action == "ensure")
            {
                if (pet == null)
                {
                    SimDescription sd = GeneticsPet.MakeRandomPet(CASAgeGenderFlags.Adult, CASAgeGenderFlags.Female, CASAgeGenderFlags.Dog);
                    if (sd == null) throw new McpException("could not make a dog (Pets not installed?)");
                    h.Add(sd);
                    Sim near = Sim.ActiveActor; // next to the active Sim (inside the house, on the lot); the lot centre may be inside a wall
                    Vector3 pos = near != null && near.LotCurrent == h.LotHome ? near.Position : h.LotHome.GetCenterPosition();
                    pet = sd.Instantiate(pos);
                    if (pet == null) throw new McpException("dog made but not instantiated");
                    r["spawned"] = true;
                }
                int n = 0;
                foreach (Sim s in h.Pets) if (s != null) n++;
                r["pets"] = n;
                r["pet"] = pet.FullName;
                return r;
            }
            // state + restart: the guard is gone without its area (route close failed, lazy-pet nap, autonomy took over):
            // start a new guard on a fresh random target, so an "until area" poll does not wait out a dead guard
            bool restart = action == "state" && a.Bool("restart", false) && !(pet != null && pet.InteractionQueue.HasInteractionOfType(PetGuardSitutation.GuardObject.Singleton)); // queued counts: no restart loop while the old head exits
            if (restart) action = "start";
            if (action == "start")
            {
                r["restarted"] = restart;
                GameObject target = a.Has("target") ? Util.FindObject(a, "target") : null;
                if (target == null)
                {
                    // floor objects only: doors, windows, wall and ceiling objects, hidden helpers and objects without a catalog
                    // product never let the guard route close (untriaged runs picked doors, area stayed false; GuardObject.Test
                    // also refuses parked vehicles)
                    List<GameObject> cand = h.LotHome.GetObjects<GameObject>(delegate(GameObject o)
                    {
                        return !(o is Sim) && o.Parent == null && o.InWorld && o.Product != null && !o.IsWallObject
                            && !(o is CommonDoor) && !(o is Windows) && !(o is Sims3.Gameplay.Objects.Vehicles.Vehicle)
                            && !(o is Sims3.Gameplay.Objects.Lighting.LightCeilingLamp) && !(o is Sims3.Gameplay.Objects.Electronics.GoboCeilingLight)
                            && o.GetHiddenFlags() == HiddenFlags.Nothing;
                    });
                    r["candidates"] = cand.Count;
                    if (cand.Count == 0) throw new McpException("no object on the home lot to guard");
                    target = RandomUtil.GetRandomObjectFromList(cand);
                }
                if (pet == null)
                {
                    SimDescription sd = GeneticsPet.MakeRandomPet(CASAgeGenderFlags.Adult, CASAgeGenderFlags.Female, CASAgeGenderFlags.Dog);
                    if (sd == null) throw new McpException("could not make a dog (Pets not installed?)");
                    h.Add(sd);
                    SpawnedSims().Add(sd.SimDescriptionId);
                    Vector3 pos = target.Position;
                    pos.x += 2f;
                    pet = sd.Instantiate(pos);
                    if (pet == null) throw new McpException("dog made but not instantiated");
                    r["spawned"] = true;
                }
                pet.InteractionQueue.CancelAllInteractions();
                InteractionInstance ii = PetGuardSitutation.GuardObject.Singleton.CreateInstance(target, pet,
                    new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                r["queued"] = pet.InteractionQueue.Add(ii);
                r["target"] = Util.ObjectName(target);
                r["targetId"] = Util.Id(target.ObjectId);
            }
            else if (pet == null) throw new McpException("the active household has no pet (run pet_guard start first)");
            else if (action == "cancel") pet.InteractionQueue.CancelAllInteractions();
            else if (action != "state") throw new McpException("action must be ensure, start, state or cancel");
            PetGuardSitutation.GuardObject g = pet.InteractionQueue.GetHeadInteraction() as PetGuardSitutation.GuardObject;
            // the interaction's own field: set after RouteCloseToObject reaches the object, cleared in GuardObject.Cleanup (decompiled C#)
            FieldInfo area = typeof(PetGuardSitutation.GuardObject).GetField("mDiscourageArea", BindingFlags.NonPublic | BindingFlags.Instance);
            GameObject slot = g != null && area != null ? area.GetValue(g) as GameObject : null;
            r["pet"] = pet.FullName;
            r["guarding"] = g != null;
            r["area"] = slot != null;
            if (slot != null) r["areaId"] = Util.Id(slot.ObjectId);
            return r;
        }

        // thumbnails {lots?, household?, maxS?, status?}: the ThumbnailManager ICs that create D3D textures from the calling
        // thread (docs/43 §4, docs/51): lot thumbnails for the active home lot and the next lived-in lots (lots, default 3), the
        // active household's thumbnail (household, default true), then UpdateDirtyLotThumbnails. Follow with save_game for the
        // save path. The ICs run in a game task, one per tick, and no new one starts after maxS (default 15) seconds: the reply
        // is the plan at once, so a slow or stuck IC no longer holds the mailbox (it did in both verify runs, 2026-10-15).
        // status:true returns the last run's partial result (done, timed_out, finished, ms).
        static Dictionary<string, object> sThumbs;
        static volatile bool sThumbsBusy;

        static object Thumbnails(Args a)
        {
            if (a.Bool("status", false))
            {
                Dictionary<string, object> s = Util.Obj();
                lock (sThumbsLock) if (sThumbs != null) foreach (KeyValuePair<string, object> kv in sThumbs) s[kv.Key] = kv.Value;
                s["busy"] = sThumbsBusy;
                return s;
            }
            RequireLive();
            if (sThumbsBusy) throw new McpException("previous thumbnails run still going; thumbnails {status:true} shows it");
            int max = a.Has("lots") ? (int)a.Double("lots") : 3;
            int limitMs = (int)(a.Double("maxS", 15) * 1000);
            List<Lot> lots = new List<Lot>();
            Household h = Household.ActiveHousehold;
            if (h != null && h.LotHome != null) lots.Add(h.LotHome);
            foreach (Lot l in LotManager.AllLots)
            {
                if (lots.Count >= max) break;
                if (l != null && !l.IsWorldLot && l.Household != null && !lots.Contains(l)) lots.Add(l);
            }
            List<object> planned = new List<object>();
            foreach (Lot l in lots) planned.Add(l.Name);
            bool hh = h != null && a.Bool("household", true);
            ulong hhId = hh ? h.HouseholdId : 0;
            List<object> done = new List<object>();
            Dictionary<string, object> st = Util.Obj();
            st["lots"] = done;
            st["household"] = false;
            st["dirty_update"] = false;
            st["timed_out"] = false;
            st["finished"] = false;
            lock (sThumbsLock) sThumbs = st;
            sThumbsBusy = true;
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                int t0 = System.Environment.TickCount;
                try
                {
                    // one IC per tick; Over() stops before the next one once the time limit is gone
                    foreach (Lot l in lots)
                    {
                        if (Over(st, t0, limitMs)) break;
                        ThumbnailManager.GenerateLotThumbnails(l.LotId, l.LotId, ThumbnailSizeMask.All);
                        lock (sThumbsLock) done.Add(l.Name);
                        Simulator.Sleep(0);
                    }
                    if (hh && !Over(st, t0, limitMs))
                    {
                        ThumbnailManager.GenerateHouseholdThumbnail(hhId, hhId, ThumbnailSizeMask.All);
                        lock (sThumbsLock) st["household"] = true;
                        Simulator.Sleep(0);
                    }
                    if (!Over(st, t0, limitMs))
                    {
                        ThumbnailManager.UpdateDirtyLotThumbnails();
                        lock (sThumbsLock) st["dirty_update"] = true;
                    }
                }
                catch (Exception e) { lock (sThumbsLock) st["error"] = e.GetType().Name + ": " + e.Message; }
                finally
                {
                    lock (sThumbsLock) { st["ms"] = System.Environment.TickCount - t0; st["finished"] = true; }
                    sThumbsBusy = false;
                }
            }));
            Dictionary<string, object> r = Util.Obj();
            r["queued"] = true;
            r["lots"] = planned;
            r["household"] = hh;
            r["maxS"] = limitMs / 1000.0;
            return r;
        }

        static readonly object sThumbsLock = new object();

        static bool Over(Dictionary<string, object> st, int t0, int limitMs)
        {
            if (System.Environment.TickCount - t0 < limitMs) return false;
            lock (sThumbsLock) st["timed_out"] = true;
            return true;
        }

        // outfit_change {count?, category?}: several Sims change outfit at once (outfit builds through the SimService, docs/42,
        // docs/51): up to count (default 6) humans, child or older, active household first, switch to category (default
        // athletic; everyday to change back) without the spin. townies: true (issue #40 leak stress) = never the active
        // household, starting at a random Sim of the world list so repeated calls spread over the town.
        static object OutfitChange(Args a)
        {
            RequireLive();
            int max = a.Has("count") ? (int)a.Double("count") : 6;
            string name = a.Str("category", "athletic").ToLowerInvariant();
            Sims3.SimIFace.CAS.OutfitCategories cat;
            switch (name)
            {
                case "everyday": cat = Sims3.SimIFace.CAS.OutfitCategories.Everyday; break;
                case "formal": cat = Sims3.SimIFace.CAS.OutfitCategories.Formalwear; break;
                case "sleepwear": cat = Sims3.SimIFace.CAS.OutfitCategories.Sleepwear; break;
                case "swimwear": cat = Sims3.SimIFace.CAS.OutfitCategories.Swimwear; break;
                case "athletic": cat = Sims3.SimIFace.CAS.OutfitCategories.Athletic; break;
                default: throw new McpException("category: everyday, formal, sleepwear, swimwear or athletic");
            }
            List<Sim> sims = new List<Sim>();
            Household h = Household.ActiveHousehold;
            bool townies = a.Bool("townies", false);
            if (h != null && !townies) foreach (Sim s in h.Sims) if (sims.Count < max) sims.Add(s);
            Sim[] all = Sims3.Gameplay.Queries.GetObjects<Sim>();
            int at = townies && all.Length > 0 ? new Random().Next(all.Length) : 0;
            for (int i = 0; i < all.Length && sims.Count < max; i++)
            {
                Sim s = all[(at + i) % all.Length];
                if (sims.Contains(s) || (townies && h != null && s.Household == h)) continue;
                sims.Add(s);
            }
            List<object> changed = new List<object>(), skipped = new List<object>();
            foreach (Sim s in sims)
            {
                SimDescription sd = s == null ? null : s.SimDescription;
                if (sd == null) continue;
                if (!sd.IsHuman || !sd.ChildOrAbove || sd.IsSupernaturalForm) { skipped.Add(sd.FullName + ": not a human child or older in normal form"); continue; }
                s.SwitchToOutfitWithoutSpin(Sim.ClothesChangeReason.Force, cat);
                changed.Add(sd.FullName + " (" + s.CurrentOutfitCategory + ")");
            }
            Dictionary<string, object> r = Util.Obj();
            r["category"] = cat.ToString();
            r["changed"] = changed;
            r["skipped"] = skipped;
            return r;
        }

        // setup_sims {sims: [name | id | "picked:N"], lot?, near?: object id or type name on `lot` (e.g. "DanceFloor"), x?, z?, y?,
        //   teleport?: true, motives?: true, moodlets?: true, autonomy?: true (false: off, tracked as the autonomy command)}: the standard Sim setup for repros (AGENTS.md "Repro Sim setup"; counts as
        //   realistic play, only the waiting is skipped): cancel each Sim's queue, teleport it to the spot unless it already stands
        //   within 2 m on that lot (teleport false when the walk there is what the repro tests), max its shown motives and clear its
        //   moodlets, so the next step can force the interaction at once instead of waiting for arrival or autonomy.
        static object SetupSims(Args a)
        {
            RequireLive();
            Lot lot = a.Has("lot") ? FindLotByName(a.Str("lot")) : null;
            Vector3 spot;
            if (a.Has("near"))
            {
                string q = a.Str("near");
                GameObject near = null;
                ulong id;
                if (ulong.TryParse(q, out id)) near = Util.FindObject(a, "near");
                else if (lot != null)
                    foreach (GameObject o in lot.GetObjects<GameObject>())
                        if (o.GetType().Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { near = o; break; }
                if (near == null) throw new McpException("setup_sims: no object " + q + (lot != null ? " on " + lot.Name : " (pass lot to search by type name)"));
                spot = near.Position;
            }
            else if (a.Has("x")) spot = new Vector3((float)a.Double("x"), 0f, (float)a.Double("z"));
            else throw new McpException("setup_sims: pass near or x/z");
            if (a.Has("x") && a.Has("near")) throw new McpException("setup_sims: near or x/z, not both");
            if (!a.Has("near")) spot.y = a.Has("y") ? (float)a.Double("y") : World.GetTerrainHeight(spot.x, spot.z);
            List<Sim> sims = new List<Sim>();
            foreach (object o in a.List("sims")) sims.Add(Util.FindSim(new Args(new Dictionary<string, object> { { "sim", Convert.ToString(o) } }), "sim"));
            Dictionary<string, object> r = Util.Obj();
            r["sims"] = SetupSimsAt(sims, lot, spot, a.Bool("teleport", true), a.Bool("motives", true), a.Bool("moodlets", true), a.Bool("autonomy", true));
            r["lot"] = lot != null ? lot.Name : null;
            return r;
        }

        // setup_sims for callers that already hold the Sims, lot and spot (dance_together_floor's several floors); one line per Sim back
        internal static List<object> SetupSimsAt(List<Sim> sims, Lot lot, Vector3 spot, bool teleport, bool motives, bool moodlets, bool autonomy)
        {
            List<object> done = new List<object>();
            for (int i = 0; i < sims.Count; i++)
            {
                Sim s = sims[i];
                s.InteractionQueue.CancelAllInteractions();
                Vector3 p = new Vector3(spot.x + 0.5f * (i - (sims.Count - 1) / 2f), spot.y, spot.z);  // half a metre apart in a row
                float dx = s.Position.x - p.x, dz = s.Position.z - p.z;
                bool moved = teleport && !((lot == null || s.LotCurrent == lot) && dx * dx + dz * dz < 4f);
                if (moved)
                {
                    // EA's own teleport (testingcheats Teleport Me Here, as NRaas GoHere): a bare SetPosition leaves LotCurrent on the
                    // old lot (2026-11-08: both NPCs still "on Trawler Lodge" after a SetPosition onto Waylon's Haunt)
                    TerrainInteraction tp = new Terrain.TeleportMeHere.Definition(false).CreateInstance(Terrain.Singleton, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true) as TerrainInteraction;
                    typeof(TerrainInteraction).GetField("Destination", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(tp, p);  // not public in the reference DLLs
                    if (!s.InteractionQueue.Add(tp)) throw new McpException("setup_sims: the game refused Teleport Me Here for " + s.FullName);
                }
                Motives m = s.Motives;
                if (motives && m != null) foreach (CommodityKind k in kShownMotives) if (m.HasMotive(k)) m.SetMax(k);
                if (moodlets) s.BuffManager.RemoveAllElements();
                if (!autonomy) AutonomyCmd(new Args(new Dictionary<string, object> { { "sim", s.SimDescription.SimDescriptionId.ToString() }, { "enabled", false } }));  // tracked; autonomy {enabled: true} restores
                done.Add(s.FullName + (moved ? ": Teleport Me Here queued" : teleport ? ": already there" : ": not moved (teleport false)"));
            }
            return done;
        }

        // place_sim {sim, x, z, y?}: stand the Sim at a world position (resets what it was doing, like the game's teleport)
        static object PlaceSim(Args a)
        {
            RequireLive();
            Sim s = Util.FindSim(a, "sim");
            Vector3 p = s.Position;
            p.x = (float)a.Double("x");
            p.z = (float)a.Double("z");
            p.y = a.Has("y") ? (float)a.Double("y") : World.GetTerrainHeight(p.x, p.z);
            s.InteractionQueue.CancelAllInteractions();
            s.SetPosition(p);
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = s.FullName;
            r["lot"] = s.LotCurrent != null ? s.LotCurrent.Name : null;
            return r;
        }

        // sim_strand {sim, lot?, tries?}: stand the Sim on a spot of `lot` (default: the lot it is on) that fails the game's
        // Sim.IsPointInLotSafelyRoutable (an enclosed room, a fenced corner, a pool tile), the case NRaas Overwatch and the
        // ts3fixes watchdog reset as "unroutable". Samples up to `tries` (default 40) random lot positions, one route plan
        // each, in this one call (a test hitch is fine). Error if every sample is routable.
        static object SimStrand(Args a)
        {
            Sim s = Util.FindSim(a, "sim");
            Lot lot = a.Has("lot") ? FindLotByName(a.Str("lot")) : s.LotCurrent;
            if (lot == null || lot.IsWorldLot) throw new McpException("needs a non-world lot (pass lot)");
            int tries = a.Has("tries") ? (int)a.Double("tries") : 40;
            for (int i = 0; i < tries; i++)
            {
                Vector3 p = lot.GetRandomPosition(true, true, TerrainType.LotTerrain | TerrainType.LotFloor | TerrainType.LotPool);
                if (p == Vector3.Invalid || s.IsPointInLotSafelyRoutable(lot, p)) continue;
                s.InteractionQueue.CancelAllInteractions();
                s.SetPosition(p);
                // a teleport keeps the old posture (sitting): CheckIdle only judges a standing Sim (bp-wdasync-stuck-repro-1111)
                if (s.Posture != s.Standing) s.Posture = s.Standing;
                sStrandSpot[s.SimDescription.SimDescriptionId] = p;
                Dictionary<string, object> r = Util.Obj();
                r["sim"] = s.FullName;
                r["lot"] = lot.Name;
                r["x"] = Math.Round(p.x, 2); r["y"] = Math.Round(p.y, 2); r["z"] = Math.Round(p.z, 2);
                r["terrain"] = World.GetTerrainType(p).ToString();
                r["samples"] = i + 1;
                return r;
            }
            throw new McpException("no unroutable spot in " + tries + " samples on " + lot.Name + " (try another lot or more tries)");
        }

        static readonly Dictionary<ulong, Vector3> sStrandSpot = new Dictionary<ulong, Vector3>();

        // sim_where {sim, maxDist?, notRouting?, requireStanding?}: position, lot, current interaction, routing, posture. With maxDist (m) it is a check against
        // the spot sim_strand last put the Sim on: error when he is farther away (or, with notRouting, routing), so a repro step fails setup
        // when something (Go Home) moved him before the watchdog could judge the spot. requireStanding: error when his posture is not standing.
        static object SimWhere(Args a)
        {
            Sim s = Util.FindSim(a, "sim");
            Dictionary<string, object> r = Util.Obj();
            bool routing = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting;
            InteractionInstance cur = s.CurrentInteraction;
            r["sim"] = s.FullName;
            r["x"] = Math.Round(s.Position.x, 2); r["z"] = Math.Round(s.Position.z, 2);
            r["lot"] = s.LotCurrent != null ? s.LotCurrent.Name : null;
            r["interaction"] = cur != null ? cur.GetInteractionName() : null;
            r["routing"] = routing;
            r["standing"] = s.Posture == s.Standing;
            Vector3 spot;
            bool have = sStrandSpot.TryGetValue(s.SimDescription.SimDescriptionId, out spot);
            if (have) r["from_strand"] = Math.Round((s.Position - spot).Length(), 2);
            if (a.Bool("requireStanding", false) && s.Posture != s.Standing)
                throw new McpException("sim_where: " + s.FullName + " is not standing (posture " + (s.Posture != null ? s.Posture.GetType().Name : "none") + ")");
            if (a.Has("maxDist"))
            {
                if (!have) throw new McpException("sim_where: no sim_strand spot recorded for " + s.FullName);
                double d = (s.Position - spot).Length();
                if (d > a.Double("maxDist")) throw new McpException("sim_where: " + s.FullName + " is " + Math.Round(d, 1) + " m from the strand spot (max " + a.Double("maxDist") + "), interaction " + (cur != null ? cur.GetInteractionName() : "none"));
                if (a.Bool("notRouting", true) && routing) throw new McpException("sim_where: " + s.FullName + " is routing");
            }
            return r;
        }

        // sim_go_home {sim}: the game's own Sim.MakeSimGoHome (GoHome, or the virtual-home trip for homeless townies)
        static object SimGoHome(Args a)
        {
            Sim s = Util.FindSim(a, "sim");
            Dictionary<string, object> r = Util.Obj();
            r["queued"] = Sim.MakeSimGoHome(s, false);
            r["home"] = s.LotHome != null ? s.LotHome.Name : null;
            return r;
        }

        // Lot by name (case-insensitive, exact first, then "contains").
        // Where the player sees a lot. A houseboat lot's centre is an off-world slot (IP Atlantis 230,2064.5, docs/222 §11.12):
        // the camera keeps its old target there and Go Here walks off the map; the player sees the boat at its port.
        static Vector3 LotViewPoint(Lot l)
        {
            if (l.IsHouseboatLot())
                foreach (Sims3.Gameplay.Objects.Island.Houseboat b in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Objects.Island.Houseboat>())
                    if (b.GetHouseboatLot() == l) return b.Position;
            return l.GetCenterPosition();
        }

        static Lot FindLotByName(string name)
        {
            if (string.Equals(name, "home", StringComparison.OrdinalIgnoreCase)) return HomeLot();
            if (string.Equals(name, "far", StringComparison.OrdinalIgnoreCase))  // farthest community lot from the active Sim (docs/117)
            {
                Lot far = FarLot(Util.ActiveSim());
                if (far == null) throw new McpException("no community lot in this world");
                return far;
            }
            if (string.Equals(name, "island", StringComparison.OrdinalIgnoreCase))  // docs/248: the land community lot behind the most sea
            {
                Lot isl = IslandLot(Util.ActiveSim(), true);
                if (isl == null) throw new McpException("no community lot over the sea in this world");
                return isl;
            }
            if (string.Equals(name, "far2", StringComparison.OrdinalIgnoreCase))  // docs/202 §3.5 D repros: the community lot farthest from both the active Sim and "far"
            {
                Sim me = Util.ActiveSim();
                Lot far = FarLot(me), best = null;
                float bestD = -1f;
                foreach (Lot l in LotManager.AllLots)
                {
                    if (l == null || !l.IsCommunityLot || l.IsWorldLot || l == far || far == null) continue;
                    float d = Math.Min((l.GetCenterPosition() - me.Position).LengthSqr(), (l.GetCenterPosition() - far.GetCenterPosition()).LengthSqr());
                    if (d > bestD) { bestD = d; best = l; }
                }
                if (best == null) throw new McpException("no second community lot in this world");
                return best;
            }
            if (string.Equals(name, "farhome", StringComparison.OrdinalIgnoreCase))  // farthest other household's home from the active Sim
            {
                Sim me = Util.ActiveSim();
                Lot best = null;
                float bestD = -1f;
                foreach (Lot l in LotManager.AllLots)
                {
                    if (l == null || l.IsWorldLot || l.IsCommunityLot || l.Household == null || l.Household == Household.ActiveHousehold) continue;
                    float d = (l.GetCenterPosition() - me.Position).LengthSqr();
                    if (d > bestD) { bestD = d; best = l; }
                }
                if (best == null) throw new McpException("no other household's home in this world");
                return best;
            }
            Lot partial = null;
            foreach (Lot l in LotManager.AllLots)
            {
                if (l == null || l.Name == null) continue;
                if (string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) return l;
                if (partial == null && l.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) partial = l;
            }
            if (partial == null) throw new McpException("no lot named '" + name + "'");
            return partial;
        }

        // The InteractionTestResult a lot trip's Test gives (and its greyed-out tooltip text) when InteractionQueue.Add refused it.
        static string VisitTestResult(InteractionDefinition def, Lot lot, Sim s, InteractionPriority pri, bool auto, out string why)
        {
            why = null;
            InteractionInstanceParameters ip = new InteractionInstanceParameters(new InteractionObjectPair(def, lot), s, pri, auto, true);
            GreyedOutTooltipCallback cb = null;
            try { string t = def.Test(ref ip, ref cb).ToString(); if (cb != null) why = cb(); return t; }
            catch (ResetException) { throw; }
            catch (Exception e) { return "threw " + e.GetType().Name; }
        }

        // sim_visit_lot {sim, lot, how?: "visit"} (lot "home", "far", "farhome" or a name): the game's own visit interaction (VisitCommunityLot for community lots, VisitLot for
        // homes), autonomous priority like a townie's own choice; triggers the lot-visit shortcuts on demand. how: "goto" = the plain
        // lot trips GoToCommunityLot / GoToLot instead (shortcut switches gotocommunity / gotolot; visit / visitlot for "visit"). autonomous?: false = player-directed; on queued false, test + why say which Test gate refused.
        static object SimVisitLot(Args a)
        {
            Sim s = Util.FindSim(a, "sim");
            string ln = a.Str("lot");
            Lot lot = ln.StartsWith("lot:") ? FindLot(ln.Substring(4)) : FindLotByName(ln);  // "lot:<id>" = story fuzzer venue targets (docs/171 §7)
            bool go = string.Equals(a.Str("how", "visit"), "goto", StringComparison.OrdinalIgnoreCase);
            InteractionDefinition def = go ? (lot.IsCommunityLot ? GoToCommunityLot.Singleton : GoToLot.Singleton)
                : lot.IsCommunityLot ? VisitCommunityLot.Singleton : VisitLot.Singleton;
            // autonomous: false = player-directed (UserDirected priority, isAutonomous false): skips Lot.CommonVisitTest's autonomous-only
            // gates (visit frequency, LastVisitedSomeone < 1 day) and cancels the Sim's own queued autonomy, so a townie already visiting runs it now
            bool auto = a.Bool("autonomous", true);
            InteractionPriority pri = new InteractionPriority(auto ? InteractionPriorityLevel.Autonomous : InteractionPriorityLevel.UserDirected);
            InteractionInstance i = def.CreateInstance(lot, s, pri, auto, true);
            Dictionary<string, object> r = Util.Obj();
            // vehicle: true reserves the own_vehicle object ("@vehicle"), as CarOwnable.VisitCommunityLot does before its visit
            if (a.Bool("vehicle", false))
            {
                Sims3.Gameplay.Objects.Vehicles.Vehicle v = sVehicle as Sims3.Gameplay.Objects.Vehicles.Vehicle;
                if (v == null) throw new McpException("no vehicle: own_vehicle first");
                s.SetReservedVehicle(v);
                r["vehicle"] = Util.ObjectName(v);
            }
            bool queued = s.InteractionQueue.Add(i);
            r["queued"] = queued;
            if (!queued) // why the definition's Test refused (InteractionQueue.InsertInteraction only returns false)
            {
                string why;
                r["test"] = VisitTestResult(def, lot, s, pri, auto, out why);
                if (why != null) r["why"] = why;
            }
            r["lot"] = lot.Name;
            r["interaction"] = def.GetType().DeclaringType != null ? def.GetType().DeclaringType.Name : def.GetType().Name;
            return r;
        }

        // sim_route_to {sim, x, z} or {sim, target, radius?: 1}: plan a route for any Sim to a point or to an object (any
        // FindTarget form, e.g. "@upstairs" for a plan across levels) or {sim, to: pond|ocean|far}, options?, options2? (go_here only drives the active Sim); a route-failure
        // trigger for the backoff / route-fail repros (the plan goes through the patched planner). Plans only: walking must
        // run in the Sim's own task, not the agent's. The route is kept as "@route" for reflect_call "@route.Replan".
        static object SimRouteTo(Args a)
        {
            Sim s = Util.FindSim(a, "sim");
            Route route = s.CreateRoute();
            // options / options2: extra Route.RouteOption / RouteOption2 bits by name on top of the Sim's own
            // (SetRouteOptions), e.g. ["RouteAsGhost", "PassThroughWalls"], ["EnablePlanningAsBoat"] (docs/117)
            List<string> set = new List<string>();
            foreach (object o in a.List("options") ?? new List<object>())
            {
                route.SetOption((Route.RouteOption)Enum.Parse(typeof(Route.RouteOption), (string)o, true), true);
                set.Add((string)o);
            }
            foreach (object o in a.List("options2") ?? new List<object>())
            {
                route.SetOption2((Route.RouteOption2)Enum.Parse(typeof(Route.RouteOption2), (string)o, true), true);
                set.Add((string)o);
            }
            if (a.Has("target"))
            {
                GameObject t = FindTarget(a, "target");
                route.PlanToPointRadius(t, t.Position, (float)a.Double("radius", 1.0));
            }
            else if (a.Has("to")) route.PlanToPoint(WalkableClick(s, RequireRarePoint(s, a.Str("to"))));  // "pond", "ocean", "far"; a lot centre can be a pond or a building (WalkableClick, ControlHandlers)
            else
            {
                Vector3 p = new Vector3((float)a.Double("x"), 0f, (float)a.Double("z"));
                p.y = World.GetTerrainHeight(p.x, p.z);
                route.PlanToPoint(p);
            }
            sLastRoute = route;
            Dictionary<string, object> r = Util.Obj();
            r["plan"] = route.PlanResult.mType.ToString();
            if (set.Count > 0) r["options"] = set;
            return r;
        }

        // route_replan {count?: 3}: replan "@route" (the last sim_route_to route, not followed) from its follower's position with
        // Route.ReplanFromPoint, count times, in a one-shot task: a yielding task that is not the route's Sim, the context of the
        // foreign ReplanFromPosition plans (docs/176: boat/car followers driven from their Sim's task, other tasks). Returns at
        // once; {result: true} returns and clears the rows so far: plan type, paths, ms (TickCount) and sig, a hash of the path
        // start points and the destination (the same sig on both arms = the same route).
        static readonly List<object> sReplanRows = new List<object>();
        static object RouteReplan(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            if (a.Bool("result", false))
            {
                lock (sReplanRows) { r["rows"] = new List<object>(sReplanRows); sReplanRows.Clear(); }
                return r;
            }
            Route route = (Route)LastLiveRoute();
            int count = (int)a.Long("count", 3);
            RunAsync("route_replan", delegate
            {
                for (int i = 0; i < count; i++)
                {
                    if (route.RouteHandle == 0 || route.Follower == null) throw new McpException("route disposed");
                    GameObject f = route.Follower.Target as GameObject;
                    Vector3 from = f != null ? f.PositionOnFloor : Vector3.Invalid;
                    int t0 = System.Environment.TickCount;
                    RoutePlanResult res = route.ReplanFromPoint(from);
                    int ms = System.Environment.TickCount - t0;
                    uint n = route.GetNumPaths();
                    int sig = PointSig(17, route.GetDestPoint());
                    for (uint p = 0; p < n; p++)
                    {
                        Vector3 s = default(Vector3);
                        route.GetPathStartPoint(p, ref s);
                        sig = PointSig(sig, s);
                    }
                    Dictionary<string, object> row = Util.Obj();
                    row["plan"] = res.mType.ToString();
                    row["paths"] = (long)n;
                    row["ms"] = (long)ms;
                    row["sig"] = sig.ToString("X8");
                    lock (sReplanRows) { if (sReplanRows.Count < 400) sReplanRows.Add(row); }
                }
            });
            r["queued"] = count;
            return r;
        }
        // centimetre grid: a hash that does not depend on the runtime's struct hashing
        static int PointSig(int h, Vector3 v)
        {
            return ((h * 31 + (int)Math.Round(v.x * 100f)) * 31 + (int)Math.Round(v.y * 100f)) * 31 + (int)Math.Round(v.z * 100f);
        }

        // sim_portal_trip {sim?, via?}: Go Here (user-directed, default the active Sim) into the sea (via "ocean", default:
        // 10 m past the first sea point found on rings of 10..400 m round the Sim) or onto the nearest houseboat (via
        // "houseboat"). Both routes cross a portal whose PortalAnimateThroughAction.DoPortal skips the route: the trigger
        // for the route adaptor leak (repro ip-portal-leak, docs/09 section 5). Needs a world with sea / houseboats (IP).
        static object SimPortalTrip(Args a)
        {
            Sim s = a.Has("sim") ? Util.FindSim(a, "sim") : Util.ActiveSim();
            string via = a.Has("via") ? a.Str("via").Trim().ToLowerInvariant() : "ocean";
            Vector3 pos = Vector3.Invalid;
            string what = null;
            if (via == "houseboat")
            {
                float best = float.MaxValue;
                foreach (Lot l in LotManager.AllLots)
                {
                    if (l == null || !l.IsHouseboatLot()) continue;
                    Sims3.Gameplay.Objects.Island.Houseboat hb = l.GetHouseboat();
                    if (hb == null) continue;
                    float d = (hb.Position - s.Position).LengthSqr();
                    if (d < best) { best = d; pos = hb.Position; what = l.Name; }
                }
            }
            else
            {
                pos = SeaPoint(s);
                if (pos != Vector3.Invalid) what = "sea";
            }
            if (what == null) throw new McpException(via == "houseboat" ? "no houseboat in this world" : "no sea within 400 m of " + s.FullName);
            InteractionInstance ii = Terrain.GoHere.GetSingleton(s, pos).CreateInstance(Terrain.Singleton, s,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            Terrain.GoHere gh = ii as Terrain.GoHere;
            if (gh != null) gh.SetDestination(pos, false);
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = s.FullName;
            r["to"] = what;
            r["x"] = Math.Round(pos.x, 1); r["z"] = Math.Round(pos.z, 1);
            r["queued"] = s.InteractionQueue.Add(ii);
            return r;
        }

        // compat_visit {def, kind?: "community" | "home", followers?: false, expectType?, maxLots?: 8, noSubway?: false, check?: false}
        // (docs/TESTING.md, compat evidence): the active Sim visits the nearest lot of that kind through the lot's own pie menu (the
        // player flow), entry chosen by the type name of its definition (labels depend on the mod's string package). followers: one Sim
        // from the entry's own picker goes along. noSubway skips subway stops. check: only name the lot, nothing queued (poll it: a home
        // needs someone home). Throws "no enabled menu entry" when the menu has no such entry (mod missing), "greyed" with the game's
        // reasons when it has one the player cannot click, and also when the instance is not expectType.
        static Lot sCvLot;
        static List<Sim> sCvSims;
        static string sCvType;
        static bool sCvRan;
        static InteractionInstance sCvInst;
        static List<Vector3> sCvPos;
        static int sCvMovedAt, sCvStartedAt;
        static object CompatVisit(Args a)
        {
            RequireLive();
            Sim me = Util.ActiveSim();
            string want = a.Str("def");
            bool home = a.Str("kind", "community") == "home";
            List<Lot> lots = new List<Lot>();
            foreach (Lot l in LotManager.AllLots)
            {
                if (l == me.LotCurrent || l == me.LotHome || l.IsWorldLot) continue;
                if (a.Bool("noSubway", false) && l.GetObjects<Sims3.Gameplay.Objects.RabbitHoles.Subway>().Length > 0) continue;
                if (home ? (l.IsResidentialLot && l.Household != null) : l.IsCommunityLot) lots.Add(l);
            }
            lots.Sort(delegate(Lot x, Lot y) { return (x.Position - me.Position).LengthSqr().CompareTo((y.Position - me.Position).LengthSqr()); });
            int cap = Math.Min(lots.Count, (int)a.Double("maxLots", 8));
            List<string> seen = new List<string>(), greyed = new List<string>();
            for (int i = 0; i < cap; i++)
                foreach (MenuEntry e in BuildMenu(me, lots[i], HitFor(lots[i], a)))
                {
                    string t = e.Iop.InteractionDefinition.GetType().FullName;
                    if (!seen.Contains(t)) seen.Add(t);
                    if (t.IndexOf(want) < 0) continue;
                    if (!e.Enabled) { if (!greyed.Contains(e.Reason)) greyed.Add(e.Reason); continue; }
                    if (!a.Bool("check", false)) return CompatVisitStart(a, me, lots[i], e);
                    Dictionary<string, object> c = Util.Obj();
                    c["lot"] = lots[i].Name;
                    c["dist"] = Math.Round(Math.Sqrt((lots[i].Position - me.Position).LengthSqr()));
                    return c;
                }
            if (greyed.Count > 0) throw new McpException("menu entry of definition '" + want + "' greyed on all " + cap + " lots: " + string.Join("; ", greyed.ToArray()));
            throw new McpException("no enabled menu entry of definition '" + want + "' on " + cap + " lots; menu definitions: " + string.Join(", ", seen.ToArray()));
        }

        static object CompatVisitStart(Args a, Sim me, Lot lot, MenuEntry e)
        {
            List<Sim> sims = new List<Sim>();
            sims.Add(me);
            List<object> picked = null;
            if (a.Bool("followers", false))
            {
                foreach (KeyValuePair<string, object> row in PickerRows(e))
                {
                    SimDescription d = row.Value as SimDescription;
                    Sim f = d != null ? d.CreatedSim : row.Value as Sim;
                    if (f == null || f == me || f.LotCurrent == lot) continue;
                    picked = new List<object>();
                    picked.Add(row.Value);
                    sims.Add(f);
                    break;
                }
                if (picked == null) throw new McpException("'" + e.Label + "': its picker offers no Sim to take along");
            }
            InteractionInstanceParameters p = new InteractionInstanceParameters(e.Iop, me,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                InteractionFlags.UserDirected, HitFor(lot, a), picked, false);
            InteractionInstance inst = e.Iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
            if (inst == null) throw new McpException("the game could not create '" + e.Label + "'");
            string type = inst.GetType().FullName, expect = a.Str("expectType", "");
            if (type.IndexOf(expect) < 0) throw new McpException("'" + e.Label + "' made " + type + ", not " + expect);
            if (!me.InteractionQueue.Add(inst)) throw new McpException("the game refused to queue '" + e.Label + "'");
            sCvLot = lot; sCvSims = sims; sCvType = type; sCvRan = false; sCvInst = inst;
            sCvPos = new List<Vector3>();
            foreach (Sim s in sims) sCvPos.Add(s.Position);
            sCvMovedAt = sCvStartedAt = System.Environment.TickCount;
            Dictionary<string, object> r = Util.Obj();
            r["lot"] = lot.Name;
            r["dist"] = Math.Round(Math.Sqrt((lot.Position - me.Position).LengthSqr()));
            r["instance"] = type;
            List<object> names = new List<object>();
            foreach (Sim s in sims) names.Add(s.FullName);
            r["sims"] = names;
            return r;
        }

        // The Sim object of the visit was destroyed or replaced: report instead of throwing, so the run keeps the evidence
        // (which Sim, when, is the world's current object for that Sim a new one, what the others do, watchdog counters).
        static object CompatVisitGone(int i)
        {
            Sim s = sCvSims[i];
            Dictionary<string, object> r = Util.Obj();
            r["failed"] = "compat_visit Sim " + i + " object gone";
            r["simIndex"] = i;
            r["elapsedMs"] = System.Environment.TickCount - sCvStartedAt;
            r["destroyed"] = s == null || s.HasBeenDestroyed;
            SimDescription d = s != null ? s.SimDescription : null;
            Sim cur = d != null ? d.CreatedSim : null;
            r["name"] = d != null ? d.FullName : null;
            r["createdSimIsNewObject"] = cur != null && !object.ReferenceEquals(cur, s);
            r["createdSimEqualsCurrent"] = cur != null && cur == d.CreatedSim;
            r["currentAtLot"] = cur != null && cur.LotCurrent != null ? cur.LotCurrent.Name : null;
            r["atHome"] = cur != null && cur.LotCurrent != null && cur.LotCurrent == cur.LotHome;
            List<object> others = new List<object>();
            for (int j = 0; j < sCvSims.Count; j++)
            {
                if (j == i) continue;
                Sim o = sCvSims[j];
                InteractionInstance ci = o != null && !o.HasBeenDestroyed ? o.CurrentInteraction : null;
                others.Add(j + ": " + (ci != null ? ci.GetType().FullName : "none"));
            }
            r["otherSimsInteraction"] = others;
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            if (c != null && c.Length > 292)
            {
                r["watchdog_shortcuts"] = c[31]; r["watchdog_second"] = c[36];
                r["owstuck_runs"] = c[258]; r["owstuck_sims"] = c[256]; r["owstucknotice_named"] = c[292];
            }
            r["leftGroupReason"] = "not available: no game API names it; compare the counters and the script log";
            return r;
        }

        // compat_visit_status: arrived = every Sim of the last compat_visit stands on its lot; ran = that instance type was seen at the
        // head of the active Sim's queue at a poll (the mod's own Run started, not just queued). Poll faster than the walk.
        // failed (fail-fast on real progress, not arrived): the active Sim is off the lot and the visit left its queue (a freeze cancels
        // it), or no Sim moved 1 m for 60 s real time; for until "failOn".
        static object CompatVisitStatus(Args a)
        {
            if (sCvLot == null) throw new McpException("no compat_visit yet");
            SgPoll();
            if (sCvSims[0] == null || sCvSims[0].HasBeenDestroyed) throw new McpException("the compat_visit Sim is gone");
            InteractionInstance head = sCvSims[0].CurrentInteraction;
            if (head != null && head.GetType().FullName == sCvType) sCvRan = true;
            bool arrived = true;
            List<object> where = new List<object>();
            for (int i = 0; i < sCvSims.Count; i++)
            {
                Sim s = sCvSims[i];
                if (s == null || s.HasBeenDestroyed) return CompatVisitGone(i);
                if (s.LotCurrent != sCvLot) arrived = false;
                if ((s.Position - sCvPos[i]).LengthSqr() >= 1f) { sCvPos[i] = s.Position; sCvMovedAt = System.Environment.TickCount; }
                where.Add(s.FullName + " at " + (s.LotCurrent != null ? s.LotCurrent.Name : "street") + (s.IsRouting ? " routing" : ""));
            }
            Dictionary<string, object> r = Util.Obj();
            r["arrived"] = arrived;
            r["ran"] = sCvRan;
            r["lot"] = sCvLot.Name;
            r["where"] = where;
            if (!arrived && sCvSims[0].LotCurrent != sCvLot && !sCvSims[0].InteractionQueue.HasInteraction(sCvInst))
                r["failed"] = "the visit left the queue before the active Sim arrived";
            else if (!arrived && System.Environment.TickCount - sCvMovedAt > 60000)
                r["failed"] = "no Sim moved for 60 s";
            return r;
        }

        // sim_gone_watch {on?}: who destroys or re-creates a Sim (queues/sim-gone.json, Lala lost mid-visit). Harness only, off until
        // on:true: two EventTracker listeners, no patch. Both events are sent synchronously (Sim.Destroy, SimDescription.Instantiate),
        // so the stack is the real caller. Active-household Sims get a full record (first kSgMax), other Sims only a count, so the
        // cost stays at a few stacks per run. Without "on" it only reads. Turn on inside the world the run uses.
        // Who schedules ResetSimTask (queues/sim-gone2.json): a task hides the caller, and NRaas ErrorTrap.OnScriptError is the only
        // caller in reach (the other one waits 30 sim-minutes). In live mode ErrorTrap writes no ScriptError file, so sim_gone_errors
        // is the one place the exception shows: a second subscriber on ScriptCore.ExceptionTrap.OnScriptError (ErrorTrap's ScriptCore
        // only, hence reflection), no patch. It runs after ErrorTrap on the failing task, before the reset. With sim_gone_trap
        // "hooked" and no error for a lost Sim, the reset did not come through OnScriptError.
        // sim_gone_tasks (sim-gone3): the other caller, ErrorTrap DereferenceFollowRouteAction.DelayedResetTask, is 30 sim-minutes, so an
        // ultra-speed run does reach it. No hook needed: every pending NRaas alarm task sits in Common.AlarmTask.sTasks (one per
        // assembly, hence the walk), read at each sim_gone_watch and compat_visit_status poll. The task class names the scheduler.
        static Sims3.Gameplay.EventSystem.EventListener sSgGone, sSgMade;
        static readonly List<object> sSgLog = new List<object>();
        static int sSgOther, sSgDropped;
        const int kSgMax = 12, kSgFrames = 30, kSgErrorChars = 2500;
        static readonly List<object> sSgErrors = new List<object>();
        static int sSgErrorsOther;
        static FieldInfo sSgTrapField;
        static Delegate sSgTrapHook;
        static string sSgTrap = "off";
        static readonly List<object> sSgTaskSeen = new List<object>(), sSgTasks = new List<object>(), sSgTrail = new List<object>();
        static Sim sSgTrailSim;
        static string sSgTrailLast;
        const int kSgTrail = 60;
        static List<object> sSgHolders, sSgDeref;
        static object SimGoneWatch(Args a)
        {
            bool on = a.Bool("on", sSgGone != null);
            if (on && sSgGone == null)
            {
                lock (sSgLog) { sSgLog.Clear(); sSgErrors.Clear(); sSgOther = sSgDropped = sSgErrorsOther = 0; }
                sSgTaskSeen.Clear(); sSgTasks.Clear(); sSgTrail.Clear(); sSgTrailSim = null; sSgTrailLast = null; sSgHolders = null; sSgDeref = null;
                sSgGone = Sims3.Gameplay.EventSystem.EventTracker.AddListener(Sims3.Gameplay.EventSystem.EventTypeId.kSimDestroyed, OnSimGone);
                sSgMade = Sims3.Gameplay.EventSystem.EventTracker.AddListener(Sims3.Gameplay.EventSystem.EventTypeId.kSimInstantiated, OnSimGone);
                SgTrapOn();
            }
            else if (!on && sSgGone != null)
            {
                Sims3.Gameplay.EventSystem.EventTracker.RemoveListener(sSgGone);
                Sims3.Gameplay.EventSystem.EventTracker.RemoveListener(sSgMade);
                sSgGone = sSgMade = null;
                SgTrapOff();
            }
            if (on) SgPoll();
            Dictionary<string, object> r = Util.Obj();
            r["sim_gone_on"] = on;
            r["sim_gone_clock"] = Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString();
            r["sim_gone_tasks"] = new List<object>(sSgTasks);
            r["sim_gone_trail"] = new List<object>(sSgTrail);
            r["sim_gone_holders"] = sSgHolders;
            r["sim_gone_deref"] = sSgDeref;
            lock (sSgLog)
            {
                r["sim_gone_other_sims"] = sSgOther;
                r["sim_gone_dropped"] = sSgDropped;
                r["sim_gone_trap"] = sSgTrap;
                r["sim_gone_errors_other"] = sSgErrorsOther;
                r["sim_gone_errors"] = new List<object>(sSgErrors); // before the events: the log line is cut at the step's "max"
                r["sim_gone_events"] = new List<object>(sSgLog);
            }
            return r;
        }

        // never throws: runs inside status polls
        static void SgPoll()
        {
            try
            {
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    SgDeref(asm);
                    Type at = asm.GetType("NRaas.Common+AlarmTask", false);
                    FieldInfo lf = at != null ? at.GetField("sTasks", BindingFlags.Static | BindingFlags.NonPublic) : null;
                    System.Collections.IEnumerable tasks = lf != null ? lf.GetValue(null) as System.Collections.IEnumerable : null;
                    if (tasks == null) continue;
                    foreach (object t in tasks)
                    {
                        Type tt = t.GetType();
                        if (!tt.Name.EndsWith("ResetTask") && tt.Name != "ResetSimTask") continue;
                        if (sSgTaskSeen.Contains(t) || sSgTasks.Count >= kSgMax) continue;
                        sSgTaskSeen.Add(t);
                        FieldInfo sf = tt.GetField("mSim", inst), xf = tt.GetField("mInstance", inst), hf = at.GetField("mHandle", inst);
                        Sim s = sf != null ? sf.GetValue(t) as Sim : null;
                        object saved = xf != null ? xf.GetValue(t) : null;
                        InteractionInstance ci = s != null && s.InteractionQueue != null ? s.CurrentInteraction : null;
                        Dictionary<string, object> r = Util.Obj();
                        r["assembly"] = asm.GetName().Name;
                        r["task"] = tt.FullName;
                        r["sim"] = s != null ? s.FullName : null;
                        r["msSinceVisit"] = sCvStartedAt != 0 ? System.Environment.TickCount - sCvStartedAt : -1;
                        r["clock"] = Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString();
                        r["minutesLeft"] = Sims3.Gameplay.Utilities.AlarmManager.Global.GetTimeLeft((Sims3.Gameplay.Utilities.AlarmHandle)hf.GetValue(t), Sims3.Gameplay.Utilities.TimeUnit.Minutes);
                        r["instance"] = saved != null ? saved.GetType().FullName : "null";
                        r["current"] = ci != null ? ci.GetType().FullName : "null";
                        r["sameNow"] = ReferenceEquals(saved, ci);
                        r["routeNow"] = s != null ? SgActions(s) : null;
                        sSgTasks.Add(r);
                    }
                    if (sSgHolders == null) SgHolders(asm);
                }
                // the follower at every poll: DelayedResetTask is only made while its route has distance left at ErrorTrap's scan
                if (sCvSims != null && sCvSims.Count > 1 && sCvSims[sCvSims.Count - 1] != null) sSgTrailSim = sCvSims[sCvSims.Count - 1];
                if (sSgTrailSim != null && sSgTrail.Count < kSgTrail)
                {
                    bool gone = sSgTrailSim.HasBeenDestroyed;
                    InteractionInstance c = gone || sSgTrailSim.InteractionQueue == null ? null : sSgTrailSim.CurrentInteraction;
                    Sims3.Gameplay.ObjectComponents.RoutingComponent rc = gone ? null : sSgTrailSim.SimRoutingComponent;
                    string now = gone ? "destroyed" : (c != null ? c.GetType().Name + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c).ToString("X") : "null")
                        + (sSgTrailSim.IsRouting ? " routing" : "") + " left=" + (rc != null ? rc.GetDistanceRemainingOnRoute().ToString("F1") : "?") + " " + SgActions(sSgTrailSim);
                    if (now != sSgTrailLast)
                    {
                        sSgTrailLast = now;
                        sSgTrail.Add((sCvStartedAt != 0 ? System.Environment.TickCount - sCvStartedAt : -1) + "ms " + Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString() + " " + now);
                    }
                }
            }
            catch (Exception x) { if (sSgTasks.Count < kSgMax) sSgTasks.Add("poll failed: " + x.GetType().Name + " " + x.Message); }
        }

        // ErrorTrap's candidates (its PreProcess), first poll they exist: the scan runs ~12 s later and DelayedResetTask keeps the interaction seen here
        static void SgDeref(Assembly asm)
        {
            if (sSgDeref != null) return;
            Type dt = asm.GetType("NRaas.ErrorTrapSpace.Dereferences.Controllers.DereferenceFollowRouteAction", false);
            FieldInfo f = dt != null ? dt.GetField("sSims", BindingFlags.Static | BindingFlags.NonPublic) : null;
            System.Collections.IDictionary d = f != null ? f.GetValue(null) as System.Collections.IDictionary : null;
            if (d == null || d.Count == 0) return;
            sSgDeref = new List<object>();
            sSgDeref.Add((sCvStartedAt != 0 ? System.Environment.TickCount - sCvStartedAt : -1) + "ms " + Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString() + " candidates " + d.Count);
            foreach (System.Collections.DictionaryEntry e in d)
            {
                Sim s = e.Key as Sim;
                if (s != null && sSgDeref.Count < 16) sSgDeref.Add(s.FullName + ": " + (e.Value != null ? e.Value.GetType().Name : "null") + ", " + SgActions(s));
            }
        }

        // ErrorTrap's own reference list from the load, until its scan clears it: who holds each Sim's FollowRouteAction outside the
        // Sim's mRouteActions, the actions its scan acts on. One snapshot, first time the list has entries.
        static void SgHolders(Assembly asm)
        {
            Type ol = asm.GetType("NRaas.ErrorTrapSpace.ObjectLookup", false);
            PropertyInfo lp = ol != null ? ol.GetProperty("List", BindingFlags.Static | BindingFlags.Public) : null;
            System.Collections.IDictionary byType = lp != null ? lp.GetValue(null, null) as System.Collections.IDictionary : null;
            if (byType == null || byType.Count == 0) return;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sSgHolders = new List<object>();
            FieldInfo idf = typeof(Sims3.Gameplay.Routing.FollowRouteAction).GetField("mRoutingObjectId", inst);
            foreach (System.Collections.DictionaryEntry e in byType)
            {
                if (!typeof(Sims3.Gameplay.Routing.FollowRouteAction).IsAssignableFrom((Type)e.Key)) continue;
                foreach (object item in (System.Collections.IEnumerable)e.Value)
                {
                    if (sSgHolders.Count >= 40) return;
                    Type it = item.GetType();
                    object child = it.GetField("mChild", inst).GetValue(item), parent = it.GetField("mParent", inst).GetValue(item);
                    FieldInfo pf = it.GetField("mField", inst).GetValue(item) as FieldInfo;
                    IScriptProxy p = Simulator.GetProxy((ObjectGuid)idf.GetValue(child));
                    Sim s = p != null ? p.Target as Sim : null;
                    if (s == null || s.Household != Household.ActiveHousehold) continue; // the step log line is cut, keep it short
                    Sims3.Gameplay.ObjectComponents.RoutingComponent rc = s.SimRoutingComponent;
                    sSgHolders.Add(s.FullName + " " + child.GetType().Name + " in " + (parent != null ? parent.GetType().Name : "null") + "." + (pf != null ? pf.Name : "?")
                        + " inChain=" + (rc != null && rc.ContainsRouteAction((Sims3.Gameplay.Routing.RouteAction)child)));
                }
            }
        }

        // running chain and the popped action (the one ErrorTrap's "not in mRouteActions" test can trip on)
        static string SgActions(Sim s)
        {
            Sims3.Gameplay.ObjectComponents.RoutingComponent rc = s.SimRoutingComponent;
            if (rc == null) return "no routing component";
            string o = "";
            Sims3.Gameplay.Routing.RouteAction ra = rc.GetCurrentRouteAction();
            for (int i = 0; ra != null && i < 16; i++, ra = rc.GetRouteActionAfter(ra)) o += ra.GetType().Name + " ";
            ra = rc.GetPreviousRouteAction();
            return o + "prev=" + (ra != null ? ra.GetType().Name : "null");
        }

        // never throws: EA removes a listener that does
        static Sims3.Gameplay.EventSystem.ListenerAction OnSimGone(Sims3.Gameplay.EventSystem.Event e)
        {
            try
            {
                Sim s = e.TargetObject as Sim;
                SimDescription d = s != null ? s.SimDescription : null;
                Household h = d != null ? d.Household : null;
                if (s == null || (h != null && h != Household.ActiveHousehold)) { lock (sSgLog) sSgOther++; return Sims3.Gameplay.EventSystem.ListenerAction.Keep; }
                lock (sSgLog) { if (sSgLog.Count >= kSgMax) { sSgDropped++; return Sims3.Gameplay.EventSystem.ListenerAction.Keep; } }
                Dictionary<string, object> r = Util.Obj();
                r["event"] = e.Id == Sims3.Gameplay.EventSystem.EventTypeId.kSimDestroyed ? "destroyed" : "instantiated";
                r["sim"] = d != null ? d.FullName : "(no description)";
                r["id"] = s.ObjectId.Value.ToString("X");
                r["msSinceVisit"] = sCvStartedAt != 0 ? System.Environment.TickCount - sCvStartedAt : -1;
                r["clock"] = Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString();
                r["lot"] = s.LotCurrent != null ? s.LotCurrent.Name : null;
                r["task"] = SgTask();
                InteractionInstance ci = s.InteractionQueue != null ? s.CurrentInteraction : null;
                r["interaction"] = ci != null ? ci.GetType().FullName : null;
                r["route"] = SgRoute(s);
                r["stack"] = SgStack();
                lock (sSgLog) sSgLog.Add(r);
            }
            catch (Exception x) { lock (sSgLog) { sSgDropped++; if (sSgLog.Count < kSgMax) sSgLog.Add("record failed: " + x.GetType().Name + " " + x.Message); } }
            return Sims3.Gameplay.EventSystem.ListenerAction.Keep;
        }

        static void SgTrapOn()
        {
            try
            {
                Type t = Type.GetType("ScriptCore.ExceptionTrap, ScriptCore");
                sSgTrapField = t != null ? t.GetField("OnScriptError", BindingFlags.Public | BindingFlags.Static) : null;
                if (sSgTrapField == null) { sSgTrap = "no ExceptionTrap: EA's ScriptCore, ErrorTrap not installed"; return; }
                sSgTrapHook = Delegate.CreateDelegate(sSgTrapField.FieldType, typeof(Handlers).GetMethod("OnSgScriptError", BindingFlags.Public | BindingFlags.Static));
                sSgTrapField.SetValue(null, Delegate.Combine((Delegate)sSgTrapField.GetValue(null), sSgTrapHook));
                sSgTrap = "hooked";
            }
            catch (Exception x) { sSgTrapHook = null; sSgTrap = "hook failed: " + x.GetType().Name + " " + x.Message; }
        }

        static void SgTrapOff()
        {
            if (sSgTrapHook == null) return;
            try { sSgTrapField.SetValue(null, Delegate.Remove((Delegate)sSgTrapField.GetValue(null), sSgTrapHook)); }
            catch (Exception x) { sSgTrap = "unhook failed: " + x.GetType().Name; return; }
            sSgTrapHook = null;
            sSgTrap = "off";
        }

        // never throws: it runs inside the game's error path. Active-household Sims get all kSgMax slots, anything else the first half.
        public static void OnSgScriptError(ScriptCore.ScriptProxy proxy, Exception e)
        {
            try
            {
                object target = proxy != null ? proxy.Target : null;
                Sim s = target as Sim;
                if (s == null && target is SimUpdate)
                {
                    FieldInfo f = typeof(SimUpdate).GetField("mSim", BindingFlags.NonPublic | BindingFlags.Instance);
                    s = f != null ? f.GetValue(target) as Sim : null;
                }
                SimDescription d = s != null ? s.SimDescription : null;
                bool mine = d != null && d.Household == Household.ActiveHousehold;
                lock (sSgLog) { if (sSgErrors.Count >= (mine ? kSgMax : kSgMax / 2)) { sSgErrorsOther++; return; } }
                Dictionary<string, object> r = Util.Obj();
                r["exception"] = e != null ? e.GetType().FullName : null;
                r["target"] = target != null ? target.GetType().FullName : null;
                r["sim"] = d != null ? d.FullName : null;
                r["id"] = s != null ? s.ObjectId.Value.ToString("X") : null;
                r["msSinceVisit"] = sCvStartedAt != 0 ? System.Environment.TickCount - sCvStartedAt : -1;
                r["task"] = SgTask();
                if (s != null)
                {
                    InteractionInstance ci = s.InteractionQueue != null ? s.CurrentInteraction : null;
                    r["interaction"] = ci != null ? ci.GetType().FullName : null;
                    r["queued"] = s.InteractionQueue != null ? s.InteractionQueue.Count : -1;
                    r["posture"] = s.Posture != null ? s.Posture.GetType().FullName : null;
                    r["lot"] = s.LotCurrent != null ? s.LotCurrent.Name : null;
                    r["route"] = SgRoute(s);
                }
                string text = e != null ? e.ToString() : "";
                r["text"] = text.Length > kSgErrorChars ? text.Substring(0, kSgErrorChars) : text;
                lock (sSgLog) sSgErrors.Add(r);
            }
            catch (Exception x) { lock (sSgLog) { sSgErrorsOther++; if (sSgErrors.Count < kSgMax) sSgErrors.Add("record failed: " + x.GetType().Name + " " + x.Message); } }
        }

        // Our route plan for this Sim at this moment: routeFails = failed plans since its last good one (ts3fixes NotePlanResult,
        // -1 without ts3fixes), async* = ts3fixes [56] waits begun, [57] polls, [58] cap hits, whole game.
        static Dictionary<string, object> SgRoute(Sim s)
        {
            Dictionary<string, object> r = Util.Obj();
            try
            {
                r["routing"] = s.IsRouting;
                r["routeFails"] = SgRouteFails(s.ObjectId.Value);
                int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
                if (c != null && c.Length > 58) { r["asyncWaits"] = c[56]; r["asyncPolls"] = c[57]; r["asyncCapHits"] = c[58]; }
            }
            catch (Exception x) { r["unreadable"] = x.GetType().Name; }
            return r;
        }

        static int SgRouteFails(ulong id)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType("Ts3Fixes.Fixes", false);
                FieldInfo f = t != null ? t.GetField("sRouteFails", BindingFlags.NonPublic | BindingFlags.Static) : null;
                System.Collections.IDictionary fails = f != null ? f.GetValue(null) as System.Collections.IDictionary : null;
                if (fails == null) continue;
                object v = fails.Contains(id) ? fails[id] : null; // a missing key throws on this mscorlib
                return v != null ? (int)v.GetType().GetField("Count").GetValue(v) : 0;
            }
            return -1;
        }

        static string SgTask()
        {
            try
            {
                IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                return p != null && p.Target != null ? p.Target.GetType().FullName : null;
            }
            catch (Exception x) { return "unreadable: " + x.GetType().Name; }
        }

        // same walk as ts3fixes PlayTrace.Callers: names only, no file info
        static List<object> SgStack()
        {
            List<object> o = new List<object>();
            try
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
                for (int i = 0; i < st.FrameCount && o.Count < kSgFrames; i++)
                {
                    MethodBase m = st.GetFrame(i).GetMethod();
                    if (m != null) o.Add((m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name);
                }
            }
            catch (Exception x) { o.Add("stack unreadable: " + x.GetType().Name); }
            return o;
        }
    }
}
