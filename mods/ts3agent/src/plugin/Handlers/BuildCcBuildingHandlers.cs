using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.UI;

namespace Ts3Agent
{
    // docs/200 §7: build_cc_cycle phases 2 (wall amount) and 3 (portal count), after phase 1 (patterns, BuildCcHandlers.cs).
    // STRESS (labelled, not a player flow): the sizes and the loop are the agent's; each step is the player's build tool
    // (tool ids from Sims3.UI.ToolMessageID: 0 wall with SetUserToolMode 2 = room / 0 = wall, as BuildController sets them;
    // 2 floor, 4 object, 12 sledgehammer, 16 modular stairs; Ctrl (modifier 2) on a drag deletes, the player's shortcut [inferred]).
    // Phase 2 targets lot geometry (walls, floor tiles, level polygons) and the routing room graph (docs/38, docs/92);
    // phase 3 targets portal objects (doors, stairs: object model docs/45, routing portals docs/92) [inferred].
    public static partial class Handlers
    {
        sealed class Bld
        {
            public Lot lot;
            public int x0, z0, W, D, levels, rx, rz, rw, rd, doors, stairs;
            public uint dwell;
            public string want;
            public Vector3 c00, ex, ez;
            public float[] y;
            public BuildBuyProduct wall;
            public BuildBuyPreset floor;
            public List<BuildBuyProduct> doorList, stairList;
        }

        sealed class BldPlaced
        {
            public ulong id;
            public int level, aim;
            public Vector3 p;
        }

        // home-lot vertex (i, j) tiles from the building corner, at the floor height of level L
        static Vector3 V(Bld b, float i, float j, int L)
        {
            Vector3 p = Plus(Plus(b.c00, b.ex, i), b.ez, j);
            return new Vector3(p.x, b.y[L], p.z);
        }

        static LotLocation LL(Bld b, int i, int j, int L) { return new LotLocation(b.x0 + i, b.z0 + j, (sbyte)L, 0); }

        // window pixel of a world point off the calibrated plane: the camera ray through p meets that plane at the ground point
        // with the same pixel (exact for a pinhole camera whose eye is CameraController's position [inferred])
        static double[] Px(Vector3 p, double plane)
        {
            Vector3 cam = CameraController.GetPosition();
            double t = (cam.y - plane) / (cam.y - p.y);
            return ToPixels(cam.x + (p.x - cam.x) * t, cam.z + (p.z - cam.z) * t, plane);
        }

        static bool ShowLevel(Bld b, int L)
        {
            b.lot.SetDisplayLevel(L);
            sHomography = null;
            Ticks(15);
            return b.lot.CurrentLotDisplayLevel == L;
        }

        // aim 0: the whole building from the -z side; aim j >= 1: x-line j (j == rz: the far outer wall) from 8 m in front.
        // Returns the plane the floor picks calibrated on (level L, the one below, the ground), NaN when none had enough picks.
        static double Aim(Bld b, int L, int aim)
        {
            Vector3 t = aim == 0 ? V(b, b.W / 2f, b.D / 2f, L) : V(b, b.W / 2f, AimZ(b, aim), L);
            float back = aim == 0 ? b.D / 2f + 12f : 8f, up = aim == 0 ? 20f : 7f;
            Vector3 c = Plus(t, b.ez, -back);
            CameraController.SetPositionAndTarget(new Vector3(c.x, t.y + up, c.z), new Vector3(t.x, t.y + 1f, t.z));
            Ticks(30);
            sHomography = null;
            double[] planes = L > 0 ? new double[] { b.y[L], b.y[L - 1], b.y[0] } : new double[] { b.y[0] };
            foreach (double pl in planes)
            {
                try { CalibrateAt(pl); return pl; }
                catch (McpException) { }
            }
            return double.NaN;
        }

        static float AimZ(Bld b, int j) { return j >= b.rz ? b.D : j * b.rd; }

        static void Drag(Bld b, double plane, Vector3 p0, Vector3 p1, uint mods)
        {
            Drag(Px(p0, plane), Px(p1, plane), b.dwell, mods);
        }

        // HasSolidFloor is no floor test on the ground: EA's own callers add this terrain check for level 0 (Sprinkler), and
        // layout-repro-smoke 2026-11-07 found no free tile on an empty lot [inferred from that: true on bare ground]
        static bool GroundFloorAt(Vector3 p) { return World.GetTerrainType(p.x, p.z, 0) == (int)TerrainType.LotFloor; }

        static string sFootprintCensus;

        // first free w x d tile rectangle (1-tile margin) nearest the lot centre: on the lot, no floor, no wall edge, no object
        // tile (object positions only: a large object's other tiles count as free [inferred harmless: the wall tool refuses]),
        // not in avoid (the phase-1 room). null when none fits.
        static int[] FindFootprint(Lot lot, int w, int d, int[] avoid)
        {
            const int N = 64;  // largest TS3 lot side in tiles
            Vector3 c00 = TileCorner(lot, 0, 0), ex = Minus(TileCorner(lot, 1, 0), c00), ez = Minus(TileCorner(lot, 0, 1), c00);
            bool[,] busy = new bool[N, N];
            int onLot = 0, floors = 0, walls = 0, objects = 0;
            for (int x = 0; x < N; x++)
                for (int z = 0; z < N; z++)
                {
                    LotLocation back = new LotLocation(), here = new LotLocation(x, z, 0, 0);
                    bool on = World.GetLotLocation(Plus(Plus(c00, ex, x + 0.5f), ez, z + 0.5f), ref back) == lot.LotId && back.mX == x && back.mZ == z;
                    if (!on) { busy[x, z] = true; continue; }
                    onLot++;
                    bool floor = GroundFloorAt(Plus(Plus(c00, ex, x + 0.5f), ez, z + 0.5f));
                    bool wall = World.WallExistsBetween(lot.LotId, here, new LotLocation(x + 1, z, 0, 0))
                        || World.WallExistsBetween(lot.LotId, here, new LotLocation(x - 1, z, 0, 0))
                        || World.WallExistsBetween(lot.LotId, here, new LotLocation(x, z + 1, 0, 0))
                        || World.WallExistsBetween(lot.LotId, here, new LotLocation(x, z - 1, 0, 0));
                    busy[x, z] = floor || wall;
                    if (floor) floors++;
                    if (wall) walls++;
                }
            foreach (GameObject o in lot.GetObjects<GameObject>())
            {
                LotLocation l = new LotLocation();
                if (World.GetLotLocation(o.Position, ref l) == lot.LotId && l.mX >= 0 && l.mX < N && l.mZ >= 0 && l.mZ < N) { busy[l.mX, l.mZ] = true; objects++; }
            }
            sFootprintCensus = onLot + " tiles on the lot, " + floors + " with a floor, " + walls + " at a wall, " + objects + " objects";
            if (avoid != null)
                for (int x = avoid[0]; x < avoid[0] + avoid[2]; x++)
                    for (int z = avoid[1]; z < avoid[1] + avoid[3]; z++)
                        if (x >= 0 && x < N && z >= 0 && z < N) busy[x, z] = true;
            LotLocation cl = new LotLocation();
            World.GetLotLocation(lot.GetCenterPosition(), ref cl);
            int[] best = null;
            int bestD = int.MaxValue;
            for (int x = 1; x + w + 1 <= N; x++)
                for (int z = 1; z + d + 1 <= N; z++)
                {
                    int dist = Math.Abs(x + w / 2 - cl.mX) + Math.Abs(z + d / 2 - cl.mZ);
                    if (dist >= bestD) continue;
                    bool free = true;
                    for (int i = x - 1; free && i <= x + w; i++)
                        for (int j = z - 1; free && j <= z + d; j++)
                            if (busy[i, j]) free = false;
                    if (free) { best = new int[] { x, z, w, d }; bestD = dist; }
                }
            return best;
        }

        // footprint census on levels 0 .. levels-1 (wall edges, solid floor tiles) plus lot-wide rooms on those levels and
        // portal objects: what the lot and routing APIs report, not what the agent asked for
        static Dictionary<string, object> BldCounts(Bld b)
        {
            int walls = 0, floors = 0, rooms = 0;
            for (int L = 0; L < b.levels; L++)
            {
                for (int i = 0; i < b.W; i++)
                    for (int j = 0; j <= b.D; j++)
                        if (World.WallExistsBetween(b.lot.LotId, LL(b, i, j - 1, L), LL(b, i, j, L))) walls++;
                for (int i = 0; i <= b.W; i++)
                    for (int j = 0; j < b.D; j++)
                        if (World.WallExistsBetween(b.lot.LotId, LL(b, i - 1, j, L), LL(b, i, j, L))) walls++;
                for (int i = 0; i < b.W; i++)
                    for (int j = 0; j < b.D; j++)
                        if (World.HasSolidFloor(b.lot.LotId, LL(b, i, j, L))) floors++;
                int[] rs = World.GetInsideRoomsAtLevel(b.lot.LotId, L, eRoomDefinition.Default);
                if (rs != null) rooms += rs.Length;
            }
            Dictionary<string, object> r = Util.Obj();
            r["walls"] = walls;
            r["floor_tiles"] = floors;
            r["rooms"] = rooms;
            r["doors"] = b.lot.GetObjects<Door>().Length;
            r["stairs"] = b.lot.GetObjects<Sims3.Gameplay.Core.Stairs>().Length;
            r["ladders"] = b.lot.GetObjects<Sims3.Gameplay.Objects.Ladder>().Length;
            r["portal_objects"] = b.lot.GetObjects<IHasPortalComponent>().Length;
            return r;
        }

        // remove == false: per level from the ground up, the room tool round the footprint, wall-tool runs on the room grid
        // lines, the floor tool over the footprint (upper levels get their floor; the ground floor is painted). remove == true:
        // from the top down, Ctrl-drags of the same floor rectangle, grid lines and the four outer sides.
        static void BuildingPass(Bld b, bool remove, Dictionary<string, object> pc, Dictionary<string, object> r, string label)
        {
            int drags = 0, levelMiss = 0, aimMiss = 0;
            uint mods = remove ? 2u : 0u;
            for (int n = 0; n < b.levels; n++)
            {
                int L = remove ? b.levels - 1 - n : n;
                r["phase"] = label + ": level " + L;
                if (!ShowLevel(b, L)) { levelMiss++; continue; }
                double pl = Aim(b, L, 0);
                if (double.IsNaN(pl)) { aimMiss++; continue; }
                if (remove)
                {
                    Tool(2u, b.floor.Product.ProductResourceKey, (int)b.floor.ID);
                    UserToolUtils.SetUserToolMode(0u);
                    Drag(b, pl, V(b, 0.5f, 0.5f, L), V(b, b.W - 0.5f, b.D - 0.5f, L), mods);
                    drags++;
                }
                Tool(0u, b.wall.ProductResourceKey, -1);
                if (!remove)
                {
                    UserToolUtils.SetUserToolMode(2u);
                    Drag(b, pl, V(b, 0, 0, L), V(b, b.W, b.D, L), 0u);
                    drags++;
                }
                UserToolUtils.SetUserToolMode(0u);
                for (int i = 1; i < b.rx; i++) { Drag(b, pl, V(b, i * b.rw, 0, L), V(b, i * b.rw, b.D, L), mods); drags++; }
                for (int j = 1; j < b.rz; j++) { Drag(b, pl, V(b, 0, j * b.rd, L), V(b, b.W, j * b.rd, L), mods); drags++; }
                if (remove)
                {
                    Drag(b, pl, V(b, 0, 0, L), V(b, b.W, 0, L), mods);
                    Drag(b, pl, V(b, b.W, 0, L), V(b, b.W, b.D, L), mods);
                    Drag(b, pl, V(b, b.W, b.D, L), V(b, 0, b.D, L), mods);
                    Drag(b, pl, V(b, 0, b.D, L), V(b, 0, 0, L), mods);
                    drags += 4;
                }
                else
                {
                    Tool(2u, b.floor.Product.ProductResourceKey, (int)b.floor.ID);
                    UserToolUtils.SetUserToolMode(0u);
                    Drag(b, pl, V(b, 0.5f, 0.5f, L), V(b, b.W - 0.5f, b.D - 0.5f, L), 0u);
                    drags++;
                }
            }
            Tool(4u, new ResourceKey(0uL, 0u, 0u), -1);  // hand tool
            pc[remove ? "remove_drags" : "build_drags"] = drags;
            pc[remove ? "remove_level_misses" : "build_level_misses"] = levelMiss;
            pc[remove ? "remove_aim_misses" : "build_aim_misses"] = aimMiss;
        }

        static bool IsCc(Bld b, BuildBuyProduct p) { return (p.CatalogName ?? "").ToLowerInvariant().IndexOf(b.want) >= 0; }

        // doors on the camera-side face of every x-line (interior lines and the far outer wall) per level, tiles 1 and rw-2 of
        // each room (away from the wall junctions), CC and EA alternating; the pixel must pick this wall (else pick_misses).
        // Then stairs on levels 0 .. levels-2 in the first rooms of row 0. Placement seen by World.OnObjectPlacedInLot.
        static void PlacePortals(Bld b, List<ulong> seen, List<BldPlaced> mine, Dictionary<string, object> pc, Dictionary<string, object> r, string label)
        {
            int tried = 0, placed = 0, cc = 0, misses = 0, pickMiss = 0, aimMiss = 0;
            List<int> tiles = new List<int>();
            tiles.Add(Math.Min(1, b.rw - 1));
            if (b.rw - 2 > 1) tiles.Add(b.rw - 2);
            for (int L = 0; L < b.levels && tried < b.doors; L++)
            {
                if (!ShowLevel(b, L)) { aimMiss++; continue; }
                for (int j = 1; j <= b.rz && tried < b.doors; j++)
                {
                    r["phase"] = label + ": doors level " + L + " line " + j + " (placed " + placed + ")";
                    double pl = Aim(b, L, j);
                    if (double.IsNaN(pl)) { aimMiss++; continue; }
                    for (int c = 0; c < b.rx && tried < b.doors; c++)
                        foreach (int t in tiles)
                        {
                            if (tried >= b.doors) break;
                            Vector3 f = V(b, c * b.rw + t + 0.5f, AimZ(b, j) - 0.15f, L);
                            Vector3 p = new Vector3(f.x, f.y + 1f, f.z);
                            double[] px = Px(p, pl);
                            Vector3 hit = Vector3.Zero, o = Vector3.Zero, d = Vector3.Zero;
                            if (!World.PickWallAtWindowPoint((int)px[0], (int)px[1], ref hit, ref o, ref d)
                                || Math.Abs(hit.x - p.x) + Math.Abs(hit.z - p.z) > 0.9f || Math.Abs(hit.y - p.y) > 1.5f) { pickMiss++; continue; }
                            BuildBuyProduct dp = b.doorList[tried % b.doorList.Count];
                            tried++;
                            Tool(4u, dp.ProductResourceKey, -1);
                            int before = seen.Count;
                            Click(px, 2);
                            Ticks(b.dwell);
                            if (seen.Count == before) { misses++; continue; }
                            placed++;
                            if (IsCc(b, dp)) cc++;
                            for (int k = before; k < seen.Count; k++) { BldPlaced m = new BldPlaced(); m.id = seen[k]; m.level = L; m.aim = j; m.p = p; mine.Add(m); }
                        }
                }
            }
            int sTried = 0, sPlaced = 0, sCc = 0;
            for (int L = 0; L + 1 < b.levels && b.stairList.Count > 0; L++)
            {
                r["phase"] = label + ": stairs level " + L;
                if (!ShowLevel(b, L)) { aimMiss++; continue; }
                double pl = Aim(b, L, 0);
                if (double.IsNaN(pl)) { aimMiss++; continue; }
                for (int s = 0; s < b.stairs; s++)
                {
                    BuildBuyProduct sp = b.stairList[sTried % b.stairList.Count];
                    sTried++;
                    Vector3 p = V(b, (s % b.rx) * b.rw + 1.5f, b.rd / 2f, L);
                    Tool(16u, sp.ProductResourceKey, -1);
                    int before = seen.Count;
                    Click(Px(p, pl), 2);
                    Ticks(b.dwell);
                    if (seen.Count == before) { misses++; continue; }
                    sPlaced++;
                    if (IsCc(b, sp)) sCc++;
                    for (int k = before; k < seen.Count; k++) { BldPlaced m = new BldPlaced(); m.id = seen[k]; m.level = L; m.aim = 0; m.p = p; mine.Add(m); }
                }
            }
            Tool(4u, new ResourceKey(0uL, 0u, 0u), -1);
            pc["doors_tried"] = tried;
            pc["doors_placed"] = placed;
            pc["cc_doors_placed"] = cc;
            pc["stairs_tried"] = sTried;
            pc["stairs_placed"] = sPlaced;
            pc["cc_stairs_placed"] = sCc;
            pc["click_misses"] = misses;
            pc["pick_misses"] = pickMiss;
            pc["aim_misses"] = aimMiss;
        }

        // sledgehammer click on each placed object from the camera it was placed with; missed ones destroyed (fallback), and
        // so is any door / stairs / ladder on the lot that is not in base (placed without the event): nothing stays behind
        static void RemovePortals(Bld b, List<BldPlaced> mine, Dictionary<ulong, bool> baseIds, Dictionary<string, object> pc, Dictionary<string, object> r, string label)
        {
            int sold = 0, fallback = 0, extra = 0;
            for (int L = 0; L < b.levels; L++)
                for (int aim = 0; aim <= b.rz; aim++)
                {
                    List<BldPlaced> group = mine.FindAll(delegate(BldPlaced m) { return m.level == L && m.aim == aim; });
                    if (group.Count == 0) continue;
                    r["phase"] = label + ": remove level " + L + " aim " + aim;
                    double pl = ShowLevel(b, L) ? Aim(b, L, aim) : double.NaN;
                    Tool(12u, new ResourceKey(0uL, 0u, 0u), -1);
                    foreach (BldPlaced m in group)
                    {
                        GameObject o = GameObject.GetObject(new ObjectGuid(m.id));
                        if (o == null || !o.InWorld) continue;
                        if (!double.IsNaN(pl)) { Click(Px(m.p, pl), 2); Ticks(b.dwell); }
                        o = GameObject.GetObject(new ObjectGuid(m.id));
                        if (o == null || !o.InWorld) { sold++; continue; }
                        o.Destroy();
                        fallback++;
                    }
                }
            List<GameObject> left = new List<GameObject>();
            foreach (Door o in b.lot.GetObjects<Door>()) left.Add(o);
            foreach (Sims3.Gameplay.Core.Stairs o in b.lot.GetObjects<Sims3.Gameplay.Core.Stairs>()) left.Add(o);
            foreach (Sims3.Gameplay.Objects.Ladder o in b.lot.GetObjects<Sims3.Gameplay.Objects.Ladder>()) left.Add(o);
            foreach (GameObject o in left)
                if (!baseIds.ContainsKey(o.ObjectId.Value) && o.InWorld) { o.Destroy(); extra++; }
            Tool(4u, new ResourceKey(0uL, 0u, 0u), -1);
            pc["sold"] = sold;
            pc["fallback_deletes"] = fallback;
            pc["extra_deletes"] = extra;
        }

        static Dictionary<ulong, bool> PortalIds(Lot lot)
        {
            Dictionary<ulong, bool> ids = new Dictionary<ulong, bool>();
            foreach (Door o in lot.GetObjects<Door>()) ids[o.ObjectId.Value] = true;
            foreach (Sims3.Gameplay.Core.Stairs o in lot.GetObjects<Sims3.Gameplay.Core.Stairs>()) ids[o.ObjectId.Value] = true;
            foreach (Sims3.Gameplay.Objects.Ladder o in lot.GetObjects<Sims3.Gameplay.Objects.Ladder>()) ids[o.ObjectId.Value] = true;
            return ids;
        }

        static bool EnterBuild(Lot lot, Dictionary<string, object> r, string label)
        {
            GameStates.TransitionToBuildMode(lot, null);
            if (!WaitReady(BuildUp, 3000)) { r["error"] = label + ": build mode did not open (state " + SubState() + ")"; return false; }
            Ticks(30);
            return true;
        }

        static bool ExitToLive(Bld b, Dictionary<string, object> r, string label)
        {
            b.lot.SetDisplayLevel(0);
            GameStates.TransitionToLiveMode();
            if (!WaitReady(LiveUp, 3000)) { r["error"] = label + ": live mode did not come back (state " + SubState() + ")"; return false; }
            Ticks(30);
            return true;
        }

        // phases 2 and 3, one building per cycle: build (exit, <wp>-full), [portals: place (exit, <pp>-full), remove (exit,
        // <pp>-empty)], remove the building (exit, <wp>-empty). Marks are taken in live mode, after the build-exit rebuild of
        // lot routing. Returns false on an error (r["error"] set).
        static bool BuildingCycles(Bld b, int cycles, bool portals, string wp, string pp, List<ulong> seen, Dictionary<string, object> r)
        {
            List<object> perW = new List<object>(), perP = new List<object>();
            Dictionary<string, object> rw = Util.Obj(), rp = Util.Obj();
            rw["footprint"] = new object[] { b.x0, b.z0, b.W, b.D };
            rw["levels"] = b.levels;
            rw["rooms_grid"] = new object[] { b.rx, b.rz };
            rw["cycles_done"] = 0;
            rw["per_cycle"] = perW;
            r["walls_phase"] = rw;
            if (portals)
            {
                rp["cc_doors"] = b.doorList.FindAll(delegate(BuildBuyProduct p) { return IsCc(b, p); }).Count;
                rp["door_products"] = b.doorList.Count;
                rp["stairs_products"] = b.stairList.Count;
                rp["cycles_done"] = 0;
                rp["per_cycle"] = perP;
                r["portals_phase"] = rp;
            }
            Dictionary<string, object> start = BldCounts(b);
            rw["start"] = start;
            for (int c = 1; c <= cycles; c++)
            {
                string lw = "walls cycle " + c, lp = "portals cycle " + c;
                Dictionary<string, object> pw = Util.Obj();
                perW.Add(pw);
                r["phase"] = lw + ": enter";
                if (!EnterBuild(b.lot, r, lw)) return false;
                BuildingPass(b, false, pw, r, lw + ": build");
                if (!ExitToLive(b, r, lw)) return false;
                pw["full"] = BldCounts(b);
                MarkGc(wp + "-full");
                if (portals)
                {
                    Dictionary<string, object> pc = Util.Obj();
                    perP.Add(pc);
                    Dictionary<ulong, bool> baseIds = PortalIds(b.lot);
                    List<BldPlaced> mine = new List<BldPlaced>();
                    r["phase"] = lp + ": enter";
                    if (!EnterBuild(b.lot, r, lp)) return false;
                    PlacePortals(b, seen, mine, pc, r, lp);
                    if (!ExitToLive(b, r, lp)) return false;
                    pc["full"] = BldCounts(b);
                    MarkGc(pp + "-full");
                    if (!EnterBuild(b.lot, r, lp)) return false;
                    RemovePortals(b, mine, baseIds, pc, r, lp);
                    if (!ExitToLive(b, r, lp)) return false;
                    pc["empty"] = BldCounts(b);
                    MarkGc(pp + "-empty");
                    rp["cycles_done"] = c;
                }
                r["phase"] = lw + ": remove";
                if (!EnterBuild(b.lot, r, lw)) return false;
                BuildingPass(b, true, pw, r, lw + ": remove");
                if (!ExitToLive(b, r, lw)) return false;
                pw["empty"] = BldCounts(b);
                MarkGc(wp + "-empty");
                rw["cycles_done"] = c;
            }
            return true;
        }

        // the building for phases 2 / 3 from the action args (see build_cc_cycle); throws with the reason when it cannot start
        static Bld NewBuilding(Lot lot, Args a, string want, uint dwell, BuildBuyProduct wallStyle, List<BuildBuyPreset> eaFloor, int[] avoid)
        {
            Bld b = new Bld();
            b.lot = lot;
            b.want = want;
            b.dwell = dwell;
            b.levels = Math.Max(1, (int)a.Long("levels", 3));
            int room = Math.Max(2, (int)a.Long("room_size", 4));
            int[] fp = null;
            if (a.Has("building"))
            {
                List<object> bl = a.List("building");
                fp = new int[] { Convert.ToInt32(bl[0]), Convert.ToInt32(bl[1]), Convert.ToInt32(bl[2]), Convert.ToInt32(bl[3]) };
            }
            else
                foreach (int[] wd in new int[][] { new int[] { 16, 12 }, new int[] { 12, 12 }, new int[] { 12, 8 }, new int[] { 8, 8 }, new int[] { 8, 6 }, new int[] { 6, 6 }, new int[] { 4, 4 } })
                    if ((fp = FindFootprint(lot, wd[0], wd[1], avoid)) != null) break;
            if (fp == null) throw new McpException("no free 4 x 4 (or larger) footprint on the home lot for the building: pass building: [lx0, lz0, w, d]");
            b.x0 = fp[0]; b.z0 = fp[1]; b.W = fp[2]; b.D = fp[3];
            b.rx = Math.Max(1, b.W / room); b.rz = Math.Max(1, b.D / room);
            b.rw = b.W / b.rx; b.rd = b.D / b.rz;
            b.c00 = TileCorner(lot, b.x0, b.z0);
            b.ex = Minus(TileCorner(lot, b.x0 + 1, b.z0), b.c00);
            b.ez = Minus(TileCorner(lot, b.x0, b.z0 + 1), b.c00);
            b.y = new float[b.levels];
            for (int L = 0; L < b.levels; L++)
            {
                Vector3 p = Vector3.Zero;
                World.GetWorldPosition(lot.LotId, new LotLocation(b.x0, b.z0, (sbyte)L, 0), ref p);
                // GetWorldPosition of a level with no floor yet may give the ground height [not checked]: then 3 m per level [inferred]
                b.y[L] = L == 0 ? p.y : (p.y - b.y[L - 1] > 1f ? p.y : b.y[L - 1] + 3f);
            }
            b.wall = wallStyle;
            b.floor = eaFloor.Count > 0 ? eaFloor[0] : null;
            if (b.wall == null) throw new McpException("no wall product for the wall tool");
            if (b.floor == null) throw new McpException("no EA floor pattern for the building floors");
            b.doors = Math.Max(0, (int)a.Long("doors", 72));
            b.stairs = Math.Max(0, (int)a.Long("stairs", 2));
            List<BuildBuyProduct> ccD = new List<BuildBuyProduct>(), eaD = new List<BuildBuyProduct>();
            foreach (BuildBuyProduct p in NamedProducts(UserToolUtils.BuildBuyProductType.Object, "", true))
                if ((p.BuildCategoryFlags & (uint)BuildBuyProduct.eBuildCategory.kBuildCategoryDoor) != 0) (IsCc(b, p) ? ccD : eaD).Add(p);
            List<BuildBuyProduct> ccS = new List<BuildBuyProduct>(), eaS = new List<BuildBuyProduct>();
            foreach (BuildBuyProduct p in NamedProducts(UserToolUtils.BuildBuyProductType.ModularStairs, "", false)) (IsCc(b, p) ? ccS : eaS).Add(p);
            b.doorList = Interleave(ccD, eaD);
            b.stairList = Interleave(ccS, eaS);
            if (b.doors > 0 && b.doorList.Count == 0) throw new McpException("no door product (CC or EA) in the catalogue");
            return b;
        }

        static List<BuildBuyProduct> Interleave(List<BuildBuyProduct> x, List<BuildBuyProduct> y)
        {
            List<BuildBuyProduct> r = new List<BuildBuyProduct>();
            for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
            {
                if (i < x.Count) r.Add(x[i]);
                if (i < y.Count) r.Add(y[i]);
            }
            return r;
        }
    }
}
