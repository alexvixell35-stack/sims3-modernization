using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Metadata;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.SimIFace.CAS;
using Sims3.SimIFace.CustomContent;

namespace Ts3Agent
{
    // Store content in use (docs/17 §4 M9): list buy objects and CAS parts by origin, place objects on the home lot and
    // dress Sims in parts, then undo both, so commitMB can be diffed per item in use. Everything is undoable and nothing
    // saves: objects are created free (no funds change) and only objects created here can be removed; parts go into a
    // separate Special outfit (the FirewalkPit barefoot pattern), the Sim's own outfits are never edited.
    // Origin = the game's own content tracker, DownloadContent.GetContentCategory (what the buy/CAS custom-content icon
    // shows): kInstalled = "ea", kFree/PaidDownloadedEA + kFeaturedStoreItem = "store", kUser/LocalUserCreated = "cc".
    // pack = ProductID in the key's group id (bits 27-31, ContentCategoryHandler), as ProductVersion (BaseGame, EP1, ...).
    public static partial class Handlers
    {
        static void RegisterContent()
        {
            Dispatcher.Register("catalog_objects", CatalogObjects);
            Dispatcher.Register("place_objects", PlaceObjects);
            Dispatcher.Register("remove_objects", RemoveObjects);
            Dispatcher.Register("cas_parts", CasParts);
            Dispatcher.Register("wear_parts", WearParts);
            Dispatcher.Register("cas_pool", CasPool);
            Dispatcher.Register("restore_outfit", RestoreOutfit);
            Dispatcher.Register("spawn_sims", SpawnSims);
            Dispatcher.Register("remove_sims", RemoveSims);
            Dispatcher.Register("camera", CameraView);
            Dispatcher.Register("camera_pan", CameraPan);
            Dispatcher.Register("camera_to_tagged", CameraToTagged);
        }

        const string kWearOutfit = "Ts3AgentWear";

        // Undo records live in AppDomain data so a hot plugin reload does not lose them.
        static T DomainState<T>(string name) where T : class, new()
        {
            T v = AppDomain.CurrentDomain.GetData(name) as T;
            if (v == null) { v = new T(); AppDomain.CurrentDomain.SetData(name, v); }
            return v;
        }
        static List<ulong> CreatedObjects() { return DomainState<List<ulong>>("ts3agent.content.created"); }
        // SimDescriptionId -> {outfit category, index} worn before wear_parts
        static Dictionary<ulong, int[]> WornBefore() { return DomainState<Dictionary<ulong, int[]>>("ts3agent.content.worn"); }

        static string ContentOrigin(ResourceKey k)
        {
            switch (DownloadContent.GetContentCategory(k) & ResourceKeyContentCategory.kResourceKeyContentCategoryMask)
            {
                case ResourceKeyContentCategory.kInstalled: return "ea";
                case ResourceKeyContentCategory.kUserCreated:
                case ResourceKeyContentCategory.kLocalUserCreated: return "cc";
                default: return "store";
            }
        }

        static void AddOrigin(Dictionary<string, object> d, ResourceKey k)
        {
            d["key"] = KeyString(k);
            d["origin"] = ContentOrigin(k);
            d["category"] = (DownloadContent.GetContentCategory(k) & ResourceKeyContentCategory.kResourceKeyContentCategoryMask).ToString();
            d["source"] = DownloadContent.GetContentSource(k).ToString();
            d["pack"] = ((ProductVersion)(1u << (int)ContentCategoryHandler.ProductID(k.GroupId))).ToString();
        }

        static string OriginFilter(Args a)
        {
            string f = a.Str("filter", "all").Trim().ToLowerInvariant();
            if (f != "all" && f != "store" && f != "cc" && f != "ea") throw new McpException("filter must be store, cc, ea or all");
            return f;
        }

        // Sims and objects change only in live mode (Create A Sim and the game's own build/buy placement are the game's flows):
        // see docs/171 §14 (native fault 0x575651 after a make_sim 10 s past a build-mode switch)
        static void RequireLive()
        {
            if (SubState() != "LiveModeState") throw new McpException("not available: " + SubState() + " (LiveModeState only)");
        }

        // Buy catalog sorted by key, so filter+max picks the same items every run.
        static List<BuildBuyProduct> SortedCatalog()
        {
            List<BuildBuyProduct> list = new List<BuildBuyProduct>(Catalog());
            list.Sort(delegate(BuildBuyProduct x, BuildBuyProduct y) { return string.CompareOrdinal(KeyString(x.ProductResourceKey), KeyString(y.ProductResourceKey)); });
            return list;
        }

        // catalog_objects {filter?, max?, pack?}: counts = matched products per "pack|category|source" (repacked Store
        // content reports as installed EA content, so pick it by pack or source there); pack = only that ProductVersion name.
        static object CatalogObjects(Args a)
        {
            string f = OriginFilter(a);
            int max = (int)a.Long("max", 50);
            string pack = a.Str("pack", null);
            List<object> items = new List<object>();
            Dictionary<string, object> counts = Util.Obj();
            int matched = 0;
            foreach (BuildBuyProduct p in SortedCatalog())
            {
                ResourceKey k = p.ProductResourceKey;
                if (f != "all" && ContentOrigin(k) != f) continue;
                Dictionary<string, object> d = Util.Obj();
                AddOrigin(d, k);
                if (pack != null && Convert.ToString(d["pack"]) != pack) continue;
                matched++;
                string ck = d["pack"] + "|" + d["category"] + "|" + d["source"];
                counts[ck] = (counts.ContainsKey(ck) ? (int)counts[ck] : 0) + 1;
                if (items.Count >= max) continue;
                d["name"] = p.CatalogName;
                d["instance"] = p.ObjectInstanceName;
                d["price"] = (int)p.Price;
                d["placeable"] = Placeable(p);
                items.Add(d);
            }
            Dictionary<string, object> r = Util.Obj();
            r["matched"] = matched;
            r["catalog"] = Catalog().Count;
            r["counts"] = counts;
            r["items"] = items;
            return r;
        }

        // What place_objects picks by filter: floor objects that are not animals, horses, boats or underwater objects.
        static bool Placeable(BuildBuyProduct p)
        {
            if (p.IsWallObject) return false;
            if ((p.BuyCategoryFlags & (uint)BuildBuyProduct.eBuyCategory.kBuyCategoryPets) != 0) return false;
            if ((p.BuySubCategoryFlags & (ulong)(BuildBuyProduct.eBuySubCategory.kBuySubCategoryHorses
                | BuildBuyProduct.eBuySubCategory.kBuySubCategoryDogs | BuildBuyProduct.eBuySubCategory.kBuySubCategoryCats)) != 0) return false;
            return (p.BuySubCategory2Flags & (ulong)(BuildBuyProduct.eBuySubCategory2.kBuySubCategory2Boats
                | BuildBuyProduct.eBuySubCategory2.kBuySubCategory2UnderwaterObjects)) == 0;
        }

        // place_objects {keys:[...] | filter, name?, count?, skip?, lot?} (name: catalogue name substring, e.g. "CCGen"): create objects free of charge at free spots (the game's
        // FindGoodLocationNearby from each lot's centre: never on top of, nor moving, existing objects). lot: "home" (default:
        // the active household's lot; no spot -> the family inventory), "residential" (occupied residential lots), "community",
        // "town" (both), or a lot name / id; objects go round-robin over the lots and a spot off its lot is refused. skip: start
        // at the nth matching product, so calls in a row place distinct products. Returns the ids for remove_objects.
        static object PlaceObjects(Args a)
        {
            RequireLive();
            Household hh = Household.ActiveHousehold;
            List<Lot> lots = TargetLots(a.Str("lot", "home"), hh);
            bool home = lots.Count == 1 && hh != null && lots[0] == hh.LotHome;
            List<ResourceKey> keys = new List<ResourceKey>();
            if (a.Has("keys"))
            {
                List<object> ks = a.List("keys");
                if (ks.Count == 0) throw new McpException("keys is empty");
                int n = (int)a.Long("count", ks.Count);
                for (int i = 0; i < n; i++) keys.Add(ParseKey(Convert.ToString(ks[i % ks.Count])));
            }
            else
            {
                string f = OriginFilter(a), name = a.Str("name", null);
                int n = (int)a.Long("count"), skip = (int)a.Long("skip", 0), seen = 0;
                foreach (BuildBuyProduct p in SortedCatalog())
                {
                    if (keys.Count >= n) break;
                    if (Placeable(p) && (f == "all" || ContentOrigin(p.ProductResourceKey) == f)
                        && (name == null || (p.CatalogName ?? "").Contains(name)) && seen++ >= skip) keys.Add(p.ProductResourceKey);
                }
                if (keys.Count < n) throw new McpException("only " + keys.Count + " placeable '" + f + "' objects in the catalog after skip " + skip);
            }
            if (keys.Count > 100) throw new McpException("count is capped at 100");
            List<ulong> created = CreatedObjects();
            List<object> placed = new List<object>();
            Dictionary<string, object> perLot = Util.Obj();
            int ok = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                ResourceKey k = keys[i];
                Lot lot = lots[i % lots.Count];
                Dictionary<string, object> d = Util.Obj();
                AddOrigin(d, k);
                placed.Add(d);
                GameObject go = GlobalFunctions.CreateObjectOutOfWorld(k) as GameObject;
                if (go == null) { d["error"] = "the game could not create it"; continue; }
                created.Add(go.ObjectId.Value);
                d["id"] = Util.Id(go.ObjectId);
                d["name"] = Util.ObjectName(go);
                Vector3 pos = lot.GetCenterPosition(), fwd = Vector3.UnitZ;
                if (GlobalFunctions.FindGoodLocationNearby(go, ref pos, ref fwd) && LotManager.GetLotAtPoint(pos) == lot)
                {
                    go.SetPosition(pos);
                    go.SetForward(fwd);
                    go.AddToWorld();
                    d["where"] = home ? "lot" : lot.Name;
                    d["position"] = Pos(pos);
                    perLot[lot.Name] = (perLot.ContainsKey(lot.Name) ? (int)perLot[lot.Name] : 0) + 1;
                    ok++;
                }
                else if (home && hh.SharedFamilyInventory != null && hh.SharedFamilyInventory.Inventory.TryToAdd(go))
                    d["where"] = "family inventory";
                else
                {
                    go.Destroy();
                    created.Remove(go.ObjectId.Value);
                    d.Remove("id");
                    d["error"] = home ? "no free spot on the lot and the family inventory refused it" : "no free spot on " + lot.Name;
                }
            }
            Dictionary<string, object> r = Util.Obj();
            r["lot"] = home ? lots[0].Name : a.Str("lot");
            r["lots"] = lots.Count;
            r["placed"] = ok;
            r["per_lot"] = perLot;
            r["objects"] = placed;
            r["tracked"] = created.Count;
            return r;
        }

        // place_objects lot targets: "home", "residential" (occupied, not the world lot), "community", "town", or a lot name / id
        static List<Lot> TargetLots(string which, Household hh)
        {
            List<Lot> l = new List<Lot>();
            if (which == "home")
            {
                if (hh == null || hh.LotHome == null) throw new McpException("the active household has no home lot");
                l.Add(hh.LotHome);
                return l;
            }
            bool res = which == "residential" || which == "town", com = which == "community" || which == "town";
            foreach (Lot lot in LotManager.AllLots)
            {
                if (lot == null || lot.IsWorldLot) continue;
                if (res || com)
                {
                    if ((res && lot.IsResidentialLot && lot.Household != null) || (com && lot.IsCommunityLot)) l.Add(lot);
                }
                else if (lot.Name == which || lot.LotId.ToString() == which) l.Add(lot);
            }
            if (l.Count == 0) throw new McpException("no lot matches '" + which + "'");
            l.Sort(delegate(Lot x, Lot y) { return x.LotId.CompareTo(y.LotId); }); // stable spread across runs
            return l;
        }

        // remove_objects {ids?:[...]}: destroy objects that place_objects created or buy_object sim_inventory added (all of them when ids is left out);
        // any other id is refused. An object a Sim is using is left alone and reported; retry it later.
        static object RemoveObjects(Args a)
        {
            RequireLive();
            List<ulong> created = CreatedObjects();
            List<ulong> ids = new List<ulong>();
            if (a.Has("ids")) foreach (object o in a.List("ids")) ids.Add(Convert.ToUInt64(o, System.Globalization.CultureInfo.InvariantCulture));
            else ids.AddRange(created);
            List<object> removed = new List<object>(), kept = new List<object>();
            foreach (ulong id in ids)
            {
                if (!created.Contains(id)) { kept.Add(Util.Id(id) + ": not created by place_objects"); continue; }
                GameObject go = GameObject.GetObject(new ObjectGuid(id));
                if (go == null) { created.Remove(id); removed.Add(Util.Id(id) + " (already gone)"); continue; }
                if (go.InUse) { kept.Add(Util.Id(id) + ": in use"); continue; }
                Inventory inv = Inventory.ParentInventory(go);
                if (inv != null) inv.TryToRemove(go);
                go.Destroy();
                created.Remove(id);
                removed.Add(Util.Id(id));
            }
            Dictionary<string, object> r = Util.Obj();
            r["removed"] = removed;
            r["kept"] = kept;
            r["tracked"] = created.Count;
            return r;
        }

        static List<ulong> SpawnedSims() { return DomainState<List<ulong>>("ts3agent.content.sims"); }

        // Sim objects in the world vs SimDescriptions in households (M11: MB per instantiated Sim).
        static void AddSimCounts(Dictionary<string, object> r)
        {
            int descs = 0;
            foreach (Household h in Household.sHouseholdList) descs += h.AllSimDescriptions.Count;
            r["simObjects"] = Sims3.Gameplay.Queries.GetObjects<Sim>().Length;
            r["simDescriptions"] = descs;
            r["tracked"] = SpawnedSims().Count;
        }

        // spawn_sims {count}: the debug `spawn <n>` command (Commands.OnSpawn) but tracked: each new YA/adult/elder joins a
        // random non-active household with a home lot and is instantiated near its lot corner. Returns ids for remove_sims.
        static object SpawnSims(Args a)
        {
            RequireLive();
            int n = (int)a.Long("count");
            if (n < 1 || n > 50) throw new McpException("count must be 1..50");
            List<Household> homes = new List<Household>();
            foreach (Household h in Household.sHouseholdList)
                if (!h.IsActive && !h.IsSpecialHousehold && h.LotHome != null) homes.Add(h);
            if (homes.Count == 0) throw new McpException("no non-active household with a home lot");
            List<ulong> spawned = SpawnedSims();
            List<object> made = new List<object>();
            for (int i = 0; i < n; i++)
            {
                Household h = RandomUtil.GetRandomObjectFromList(homes);
                SimUtils.SimCreationSpec spec = new SimUtils.SimCreationSpec();
                spec.Age = CASAgeGenderFlags.YoungAdult | CASAgeGenderFlags.Adult | CASAgeGenderFlags.Elder;
                spec.Normalize();
                SimDescription sd = spec.Instantiate();
                h.Add(sd);
                Vector3 pos = h.LotHome.Corners[RandomUtil.GetInt(3)];
                pos.x += RandomUtil.GetFloat(-10f, 10f);
                pos.z += RandomUtil.GetFloat(-10f, 10f);
                spawned.Add(sd.SimDescriptionId);
                bool ok = sd.Instantiate(pos) != null;
                Dictionary<string, object> d = Util.Obj();
                d["id"] = Util.Id(sd.SimDescriptionId);
                d["name"] = sd.FullName;
                d["household"] = h.Name;
                if (!ok) d["error"] = "not instantiated";
                made.Add(d);
            }
            Dictionary<string, object> r = Util.Obj();
            r["sims"] = made;
            AddSimCounts(r);
            return r;
        }

        // remove_sims {}: remove and dispose every Sim spawn_sims made (the debug `exterminate` path, limited to ours).
        // A Sim already gone is dropped from the list. No Sims of the save are touched. A Sim sitting in a vehicle, boat or on
        // a horse (Parent set) or walking with a linked partner is not disposed in place: disposing a rider mid-ride left
        // its partner's steering agent with a link to a destroyed agent and the game hit a pure virtual call in avoidance
        // (2026-10-21 ip-ut2-boats crash at remove_sims, docs/143 §8.11). Such Sims are reset first (SetObjectToReset gets
        // them out of the vehicle with the game's own reset path) and disposed by a task 30 ticks later ("deferred").
        static object RemoveSims(Args a)
        {
            List<ulong> spawned = SpawnedSims();
            List<object> removed = new List<object>(), deferred = new List<object>();
            List<SimDescription> later = new List<SimDescription>();
            foreach (ulong id in spawned.ToArray())
            {
                SimDescription sd = SimDescription.Find(id);
                spawned.Remove(id);
                if (sd == null) { removed.Add(Util.Id(id) + " (already gone)"); continue; }
                Sim s = sd.CreatedSim;
                if (s != null && !s.HasBeenDestroyed && (s.Parent != null || s.IsRouting))
                {
                    s.SetObjectToReset();
                    later.Add(sd);
                    deferred.Add(Util.Id(id));
                    continue;
                }
                if (sd.Household != null) sd.Household.Remove(sd);
                sd.Dispose(true, false, true);
                removed.Add(Util.Id(id));
            }
            if (later.Count > 0)
                Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
                {
                    Simulator.Sleep(30);
                    foreach (SimDescription sd in later)
                    {
                        try
                        {
                            if (sd.Household != null) sd.Household.Remove(sd);
                            sd.Dispose(true, false, true);
                        }
                        catch (Exception) { }  // gone meanwhile
                    }
                }));
            Dictionary<string, object> r = Util.Obj();
            r["removed"] = removed;
            r["deferred"] = deferred;
            AddSimCounts(r);
            return r;
        }

        // camera {lot?, pos?:{x,y,z}, target?:{x,y,z}}: set the camera (CameraController.SetPositionAndTarget, what
        // -campos/-camtarget do at load); no args = read only. Returns the current pose.
        // M10: max zoom out (high pos.y) and pan the town, diffing commitMB and the largest free block between poses.
        // lot: onto that lot's centre (FindLotByName: "home", "far" or a name), so a world-neutral repro needs no coordinates.
        static object CameraView(Args a)
        {
            RequireLive();
            if (a.Has("lot"))
            {
                Vector3 c = FindLotByName(a.Str("lot")).GetCenterPosition();
                CameraController.SetPositionAndTarget(new Vector3(c.x, c.y + 30f, c.z - 30f), c);
            }
            else if (a.Has("pos") || a.Has("target"))
                CameraController.SetPositionAndTarget(
                    a.Has("pos") ? Vec(a, "pos") : CameraController.GetPosition(),
                    a.Has("target") ? Vec(a, "target") : CameraController.GetTarget());
            Dictionary<string, object> r = Util.Obj();
            r["pos"] = Pos(CameraController.GetPosition());
            r["target"] = Pos(CameraController.GetTarget());
            return r;
        }

        // camera_to_tagged {height?, back?}: docs/222 snap-back repro. Moves the camera onto the Sim whose autonomous pick
        // ts3fixes' unseen-direct path pushed last (AppDomain data "ts3fixes.autunseen.tagged" = its ObjectId), so the Sim
        // turns seen while that pick is still queued. No tag yet or the Sim is gone: camera unchanged, found false.
        // key: another AppDomain tag, e.g. "ts3fixes.unseenroute.tagged" (docs/202 §3.9: the Sim of the last relaxed hold) or a
        // point tag (float[3] x, y, z), e.g. "ts3fixes.unseenroute.goal" (that hold's goal: the goal turns seen, the Sim does not).
        // sim: this Sim instead of a tag (docs/197 §7: a seen NPC on demand). delayMs: the step returns at once and the camera jumps
        // later, so the next step can start a slow route job and the jump lands inside it.
        static object CameraToTagged(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            object d = AppDomain.CurrentDomain.GetData(a.Has("key") ? a.Str("key") : "ts3fixes.autunseen.tagged");
            ulong id = d is ulong ? (ulong)d : 0UL;
            Sim s = a.Has("sim") ? Util.FindSim(a, "sim") : id != 0UL ? GameObject.GetObject(new ObjectGuid(id)) as Sim : null;
            float[] pt = d as float[];
            r["found"] = (s != null && !s.HasBeenDestroyed) || (pt != null && pt.Length == 3);
            if (!(bool)r["found"]) return r;
            float h = (float)a.Double("height", 12), back = (float)a.Double("back", 12);
            Vector3 t = s != null ? s.Position : new Vector3(pt[0], pt[1], pt[2]);
            int delayMs = (int)a.Double("delayMs", 0);
            if (delayMs > 0)
                Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
                {
                    int t0 = System.Environment.TickCount;
                    while (System.Environment.TickCount - t0 < delayMs) Simulator.Sleep(0);
                    CameraController.SetPositionAndTarget(new Vector3(t.x, t.y + h, t.z - back), t);
                }));
            else CameraController.SetPositionAndTarget(new Vector3(t.x, t.y + h, t.z - back), t);
            if (s != null) r["sim"] = s.FullName;
            r["target"] = Pos(t);
            return r;
        }

        // camera_pan {from?:{x,y,z}, to?:{x,y,z}, lots?, height?, seconds?, dwellS?, passes?} | {stop:true}: lot streaming repro
        // (docs/28 §5.5). Pans the in-game camera at street zoom along a path, in a game task that moves it every tick by
        // wall-clock time (not sim time), so lot scoring sees a steady camera speed. Path: from -> to (y ignored), else the
        // `lots` (default 8) lots nearest the lot with the most other lots within 150 m, in x order (the densest
        // neighbourhood). height (default 25 m) above the terrain, looking ahead; seconds (default 40) of movement per pass,
        // plus dwellS (default 0) still at every path point (lots activate only while the camera is slow, docs/28 §5.2);
        // passes (default 4) alternate there and back. Returns at once; a new camera_pan or {stop:true} ends a running one.
        // Only the game camera moves: the game window is never moved or focused.
        static volatile int sPanGen;
        static object CameraPan(Args a)
        {
            RequireLive();
            int gen = ++sPanGen;
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("stop")) { r["stopped"] = true; return r; }
            List<Vector3> path = a.Has("from") && a.Has("to") ? new List<Vector3> { Vec(a, "from"), Vec(a, "to") } : DenseLotPath(a.Has("lots") ? (int)a.Double("lots") : 8);
            if (path.Count < 2) throw new McpException("no path: fewer than 2 lots in this world");
            float height = a.Has("height") ? (float)a.Double("height") : 25f;
            int moveMs = (int)((a.Has("seconds") ? a.Double("seconds") : 40) * 1000), dwellMs = (int)((a.Has("dwellS") ? a.Double("dwellS") : 0) * 1000);
            int passes = a.Has("passes") ? (int)a.Double("passes") : 4;
            float total = 0;
            for (int i = 1; i < path.Count; i++) total += Flat(path[i] - path[i - 1]).Length();
            if (moveMs < 1000 || passes < 1 || total < 1) throw new McpException("seconds >= 1, passes >= 1 and a path longer than 1 m needed");
            int passMs = moveMs + dwellMs * path.Count;
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                int t0 = System.Environment.TickCount;
                while (sPanGen == gen)
                {
                    int el = System.Environment.TickCount - t0;
                    if (el >= passMs * passes) break;
                    bool back = (el / passMs) % 2 == 1;
                    int left = el % passMs;
                    // walk the path: dwell at each point, then move along the next segment for its share of moveMs
                    Vector3 at = path[back ? path.Count - 1 : 0], dir = Vector3.Empty;
                    for (int i = 0; i < path.Count; i++)
                    {
                        Vector3 p = path[back ? path.Count - 1 - i : i];
                        at = p;
                        if (left < dwellMs || i == path.Count - 1) break;
                        left -= dwellMs;
                        Vector3 q = path[back ? path.Count - 2 - i : i + 1], seg = Flat(q - p);
                        int segMs = (int)(moveMs * seg.Length() / total);
                        dir = seg;
                        if (left < segMs) { at = p + seg * ((float)left / Math.Max(1, segMs)); break; }
                        left -= segMs;
                    }
                    if (dir.Length() < 0.01f) dir = Flat(path[path.Count - 1] - path[0]) * (back ? -1f : 1f);
                    dir = dir * (1f / Math.Max(0.01f, dir.Length()));
                    Vector3 target = new Vector3(at.x, World.GetTerrainHeight(at.x, at.z), at.z);
                    CameraController.SetPositionAndTarget(target - dir * height + new Vector3(0, height, 0), target);
                    Simulator.Sleep(0);
                }
            }));
            List<object> pts = new List<object>();
            foreach (Vector3 p in path) pts.Add(Pos(p));
            r["path"] = pts;
            r["lengthM"] = Math.Round(total);
            r["passMs"] = passMs;
            r["passes"] = passes;
            r["height"] = height;
            return r;
        }

        static Vector3 Flat(Vector3 v) { return new Vector3(v.x, 0, v.z); }

        static List<Vector3> DenseLotPath(int k)
        {
            List<Vector3> c = new List<Vector3>();
            foreach (Lot l in LotManager.AllLots) if (l != null && !l.IsWorldLot) c.Add(l.GetCenterPosition());
            if (c.Count < 2) return c;
            int best = 0, bestN = -1;
            for (int i = 0; i < c.Count; i++)
            {
                int n = 0;
                for (int j = 0; j < c.Count; j++) if (Flat(c[j] - c[i]).Length() < 150f) n++;
                if (n > bestN) { bestN = n; best = i; }
            }
            Vector3 centre = c[best];
            c.Sort(delegate(Vector3 x, Vector3 y) { return Flat(x - centre).Length().CompareTo(Flat(y - centre).Length()); });
            if (c.Count > k) c.RemoveRange(k, c.Count - k);
            c.Sort(delegate(Vector3 x, Vector3 y) { return x.x.CompareTo(y.x); });
            return c;
        }

        static Vector3 Vec(Args a, string key)
        {
            Dictionary<string, object> d = a.Raw(key) as Dictionary<string, object>;
            if (d == null || !d.ContainsKey("x") || !d.ContainsKey("y") || !d.ContainsKey("z")) throw new McpException(key + " needs {x, y, z}");
            System.Globalization.CultureInfo ic = System.Globalization.CultureInfo.InvariantCulture;
            return new Vector3((float)Convert.ToDouble(d["x"], ic), (float)Convert.ToDouble(d["y"], ic), (float)Convert.ToDouble(d["z"], ic));
        }

        // A part fits when it covers the Sim's age and gender and has the same species (none = human), as OutfitUtils does.
        static bool Fits(CASPart p, CASAgeGenderFlags ags)
        {
            CASAgeGenderFlags pa = p.AgeGenderSpecies;
            if ((pa & CASAgeGenderFlags.SpeciesMask) == 0) pa |= CASAgeGenderFlags.Human;
            if ((ags & CASAgeGenderFlags.SpeciesMask) == 0) ags |= CASAgeGenderFlags.Human;
            CASAgeGenderFlags ag = CASAgeGenderFlags.AgeMask | CASAgeGenderFlags.GenderMask;
            return (pa & ags & ag) == (ags & ag) && (pa & CASAgeGenderFlags.SpeciesMask) == (ags & CASAgeGenderFlags.SpeciesMask);
        }

        // cas_parts {filter?, max?, sim?, body_type?}: every CAS part (PartSearch, the list OutfitUtils draws from), sorted
        // by key; sim = only parts that fit that Sim; body_type = a BodyTypes name (Hair, UpperBody, Shoes, ...).
        static object CasParts(Args a)
        {
            string f = OriginFilter(a);
            int max = (int)a.Long("max", 50);
            bool bySim = a.Has("sim");
            CASAgeGenderFlags ags = bySim ? Util.FindSimDescription(a, "sim").AgeGenderSpecies : CASAgeGenderFlags.None;
            BodyTypes bt = BodyTypes.None;
            if (a.Has("body_type"))
            {
                try { bt = (BodyTypes)Enum.Parse(typeof(BodyTypes), a.Str("body_type"), true); }
                catch (ArgumentException) { throw new McpException("unknown body_type '" + a.Str("body_type") + "'"); }
            }
            List<CASPart> parts = new List<CASPart>();
            Dictionary<ResourceKey, bool> seen = new Dictionary<ResourceKey, bool>();
            foreach (CASPart p in new PartSearch())
            {
                if (seen.ContainsKey(p.Key)) continue;
                seen[p.Key] = true;
                if (bt != BodyTypes.None && p.BodyType != bt) continue;
                if (bySim && !Fits(p, ags)) continue;
                if (f != "all" && ContentOrigin(p.Key) != f) continue;
                parts.Add(p);
            }
            parts.Sort(delegate(CASPart x, CASPart y) { return string.CompareOrdinal(KeyString(x.Key), KeyString(y.Key)); });
            List<object> items = new List<object>();
            for (int i = 0; i < parts.Count && i < max; i++)
            {
                Dictionary<string, object> d = Util.Obj();
                AddOrigin(d, parts[i].Key);
                d["body_type"] = parts[i].BodyType.ToString();
                d["age_gender"] = parts[i].AgeGenderSpecies.ToString();
                d["name"] = CASUtils.PartDataGetName(parts[i].Key);
                items.Add(d);
            }
            Dictionary<string, object> r = Util.Obj();
            r["matched"] = parts.Count;
            r["items"] = items;
            return r;
        }

        // wear_parts {sim?, keys:[...]}: current outfit + the parts (each replaces what the Sim wears on that body type;
        // full body and top/bottom replace each other; a later key of the same body type wins) as a new Special outfit,
        // switched to at once. Parts that do not fit the Sim are skipped and reported. restore_outfit undoes it.
        // Without keys: filter (cc/store/ea/all, or pool = cas_pool's keys) + body_types [...] + nth pick, per body type, the nth fitting part of that
        // origin (sorted by key). docs/191 cc-soak runs with content no-cc, so "cc" is then only the synthetic set [inferred:
        // Mods packages report kLocalUserCreated, 2026-10-15 store-items-memory]. sim "household": every member in the world.
        // sim "town": the active household, then other instantiated human Sims by id, Sims skip_sims .. skip_sims + max_sims
        // (default 0, 20); Sim i picks nth + i, so Sims wear distinct parts while the lists last. remember: see below.
        static readonly Dictionary<string, KeyValuePair<int, List<ulong>>> sSimSets = new Dictionary<string, KeyValuePair<int, List<ulong>>>();
        static object WearParts(Args a)
        {
            RequireLive();
            string who = a.Has("sim") ? a.Str("sim") : null;
            if (who != "household" && who != "town") return WearOn(Util.FindSim(a, "sim"), a, 0, null);
            if (Household.ActiveHousehold == null) throw new McpException("no active household");
            List<Sim> sims = new List<Sim>(Household.ActiveHousehold.Sims);
            if (who == "town")
            {
                List<Sim> others = new List<Sim>();
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                    if (s != null && s.SimDescription != null && s.SimDescription.IsHuman && !sims.Contains(s)) others.Add(s);
                others.Sort(delegate(Sim x, Sim y) { return x.SimDescription.SimDescriptionId.CompareTo(y.SimDescription.SimDescriptionId); });
                sims.AddRange(others);
            }
            int skip = who == "town" ? (int)a.Long("skip_sims", 0) : 0, end = who == "town" ? Math.Min(sims.Count, skip + (int)a.Long("max_sims", 20)) : sims.Count;
            // remember (sim "town"): name this pick; a later call with the same name dresses the same Sims at the same
            // index (steady-state tests, docs/200 §11), so townies that came or went in between do not change the set
            string mem = who == "town" ? a.Str("remember", null) : null;
            KeyValuePair<int, List<ulong>> set;
            if (mem != null && sSimSets.TryGetValue(mem, out set))
            {
                skip = set.Key;
                end = skip + set.Value.Count;
                sims = new List<Sim>(new Sim[skip]);
                foreach (ulong id in set.Value) { SimDescription sd = SimDescription.Find(id); sims.Add(sd == null ? null : sd.CreatedSim); }
            }
            else if (mem != null)
            {
                List<ulong> ids = new List<ulong>();
                for (int i = skip; i < end; i++) ids.Add(sims[i].SimDescription.SimDescriptionId);
                sSimSets[mem] = new KeyValuePair<int, List<ulong>>(skip, ids);
            }
            Dictionary<CASAgeGenderFlags, Dictionary<BodyTypes, List<string>>> cache = new Dictionary<CASAgeGenderFlags, Dictionary<BodyTypes, List<string>>>();
            List<object> each = new List<object>();
            int dressed = 0, parts = 0;
            List<string> errs = new List<string>();
            for (int i = skip; i < end; i++)
            {
                Sim m = sims[i];
                if (m == null) { Dictionary<string, object> d = Util.Obj(); d["sim"] = i; d["error"] = "remembered Sim no longer instantiated"; each.Add(d); errs.Add("Sim #" + i + ": not instantiated"); continue; }
                try
                {
                    Dictionary<string, object> w = (Dictionary<string, object>)WearOn(m, a, who == "town" ? i : 0, cache);
                    parts += ((List<object>)w["worn"]).Count;
                    each.Add(w); dressed++;
                }
                catch (McpException e) { Dictionary<string, object> d = Util.Obj(); d["sim"] = m.FullName; d["error"] = e.Message; each.Add(d); errs.Add(m.FullName + ": " + e.Message); }
            }
            if (dressed == 0) throw new McpException("no household Sim could wear a part: " + string.Join("; ", errs.ToArray()));
            Dictionary<string, object> r = Util.Obj();
            r["dressed"] = dressed;
            r["parts"] = parts;
            r["sims"] = each;
            return r;
        }

        // cache (per wear_parts call): the sorted fitting parts per age/gender/species, so a town of Sims scans the parts once each
        static List<string> PickParts(Args a, CASAgeGenderFlags ags, int nthAdd, Dictionary<CASAgeGenderFlags, Dictionary<BodyTypes, List<string>>> cache)
        {
            int nth = (int)a.Long("nth", 0) + nthAdd;
            Dictionary<BodyTypes, List<string>> hits;
            if (cache == null || !cache.TryGetValue(ags, out hits)) { hits = FittingParts(a, ags); if (cache != null) cache[ags] = hits; }
            List<string> keys = new List<string>();
            foreach (List<string> l in hits.Values)
                if (l.Count > 0) keys.Add(l[nth % l.Count]);
            return keys;
        }

        // cas_pool {add?:[keys], clear?}: CAS part instances that wear_parts filter "pool" may pick (the cc-synth set's keys
        // from its manifest.json, so the player's own CC is never picked); sent in chunks, kept until clear or a reload
        static readonly Dictionary<ulong, bool> sCasPool = new Dictionary<ulong, bool>();
        static object CasPool(Args a)
        {
            if (a.Bool("clear", false)) sCasPool.Clear();
            if (a.Has("add")) foreach (object o in a.List("add")) sCasPool[ParseKey(Convert.ToString(o)).InstanceId] = true;
            Dictionary<string, object> r = Util.Obj();
            r["pool"] = sCasPool.Count;
            return r;
        }

        static Dictionary<BodyTypes, List<string>> FittingParts(Args a, CASAgeGenderFlags ags)
        {
            bool pool = a.Str("filter", "all").Trim().ToLowerInvariant() == "pool";
            if (pool && sCasPool.Count == 0) throw new McpException("filter pool: the pool is empty; cas_pool add first");
            string f = pool ? "all" : OriginFilter(a);
            Dictionary<BodyTypes, List<string>> hits = new Dictionary<BodyTypes, List<string>>();
            foreach (object o in a.List("body_types"))
            {
                try { hits[(BodyTypes)Enum.Parse(typeof(BodyTypes), Convert.ToString(o), true)] = new List<string>(); }
                catch (ArgumentException) { throw new McpException("unknown body_type '" + o + "'"); }
            }
            foreach (CASPart p in new PartSearch())
            {
                List<string> l;
                if (!hits.TryGetValue(p.BodyType, out l) || !Fits(p, ags) || (f != "all" && ContentOrigin(p.Key) != f) || (pool && !sCasPool.ContainsKey(p.Key.InstanceId))) continue;
                string k = KeyString(p.Key);
                if (!l.Contains(k)) l.Add(k);
            }
            foreach (List<string> l in hits.Values) l.Sort(string.CompareOrdinal);
            return hits;
        }

        static object WearOn(Sim s, Args a, int nthAdd, Dictionary<CASAgeGenderFlags, Dictionary<BodyTypes, List<string>>> cache)
        {
            SimDescription sd = s.SimDescription;
            if (WornBefore().ContainsKey(sd.SimDescriptionId)) throw new McpException(sd.FullName + " already wears agent parts; restore_outfit first");
            if (sd.IsSupernaturalForm) throw new McpException(sd.FullName + " is in a supernatural form; its outfit cannot be swapped");
            List<object> worn = new List<object>(), skipped = new List<object>();
            SimBuilder b = new SimBuilder();
            try
            {
                b.UseCompression = true;
                OutfitUtils.SetOutfit(b, s.CurrentOutfit, sd, false);
                List<string> keyList = new List<string>();
                if (a.Has("keys")) foreach (object o in a.List("keys")) keyList.Add(Convert.ToString(o));
                else keyList = PickParts(a, sd.AgeGenderSpecies, nthAdd, cache);
                foreach (string ks in keyList)
                {
                    CASPart p = new CASPart(ParseKey(ks));
                    if (p.Key == ResourceKey.kInvalidResourceKey || p.BodyType == BodyTypes.None) { skipped.Add(ks + ": no such CAS part"); continue; }
                    if (!Fits(p, sd.AgeGenderSpecies)) { skipped.Add(ks + ": " + p.AgeGenderSpecies + " does not fit " + sd.AgeGenderSpecies); continue; }
                    if (p.BodyType == BodyTypes.FullBody) b.RemoveParts(BodyTypes.FullBody, BodyTypes.UpperBody, BodyTypes.LowerBody);
                    else if (p.BodyType == BodyTypes.UpperBody || p.BodyType == BodyTypes.LowerBody) b.RemoveParts(p.BodyType, BodyTypes.FullBody);
                    else b.RemoveParts(p.BodyType);
                    if (!b.AddPart(p)) { skipped.Add(ks + ": SimBuilder refused it"); continue; }
                    Dictionary<string, object> d = Util.Obj();
                    AddOrigin(d, p.Key);
                    d["body_type"] = p.BodyType.ToString();
                    worn.Add(d);
                }
                if (worn.Count == 0) throw new McpException("no part could be worn: " + string.Join("; ", skipped.ConvertAll(Convert.ToString).ToArray()));
                ResourceKey key = b.CacheOutfit(kWearOutfit + sd.SimDescriptionId);
                int[] before = { (int)s.CurrentOutfitCategory, s.CurrentOutfitIndex };
                int idx = sd.AddSpecialOutfit(new SimOutfit(key), kWearOutfit);
                if (idx < 0) throw new McpException("the game refused the outfit");
                WornBefore()[sd.SimDescriptionId] = before;
                s.SwitchToOutfitWithoutSpin(OutfitCategories.Special, sd.GetOutfit(OutfitCategories.Special, idx), idx);
            }
            finally { b.Dispose(); }
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = sd.FullName;
            r["worn"] = worn;
            r["skipped"] = skipped;
            return r;
        }

        // restore_outfit {sim?}: back to the outfit worn before wear_parts (if the Sim still wears ours) and the agent
        // outfit removed from the Sim. sim: "all" restores every Sim wear_parts dressed.
        static object RestoreOutfit(Args a)
        {
            Dictionary<ulong, int[]> rec = WornBefore();
            List<ulong> ids = new List<ulong>();
            if (a.Has("sim") && a.Str("sim") == "all") ids.AddRange(rec.Keys);
            else ids.Add(Util.FindSimDescription(a, "sim").SimDescriptionId);
            List<object> done = new List<object>();
            foreach (ulong id in ids)
            {
                SimDescription sd = SimDescription.Find(id);
                if (sd == null) { rec.Remove(id); continue; }
                int[] before;
                // nothing of ours to take off (wear_parts never dressed this Sim, or it was already restored): done, no-op
                if (!rec.TryGetValue(id, out before)) { done.Add(sd.FullName + " (wears no agent parts: nothing to restore)"); continue; }
                Sim s = sd.CreatedSim;
                int ours = sd.HasSpecialOutfit(kWearOutfit) ? sd.GetSpecialOutfitIndexFromKey(ResourceUtils.HashString32(kWearOutfit)) : -1;
                bool switched = false;
                if (s != null && ours >= 0 && s.CurrentOutfitCategory == OutfitCategories.Special && s.CurrentOutfitIndex == ours)
                {
                    switched = s.SwitchToOutfitWithoutSpin((OutfitCategories)before[0], before[1]);
                    if (!switched) s.SwitchToOutfitWithoutSpin(Sim.ClothesChangeReason.Force, OutfitCategories.Everyday);
                }
                sd.RemoveSpecialOutfit(kWearOutfit);
                rec.Remove(id);
                done.Add(sd.FullName + (switched ? "" : " (outfit removed; previous outfit not re-applied)"));
            }
            Dictionary<string, object> r = Util.Obj();
            r["restored"] = done;
            return r;
        }
    }
}
