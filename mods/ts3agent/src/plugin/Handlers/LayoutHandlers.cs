using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.Lighting;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.UI;

namespace Ts3Agent
{
    // build_layout: a text map (LayoutMap.cs: how tiles turn into edge walls) built on the home lot with the player's tools.
    // {map: [rows] | "rows\n...", legend?: {"f": {name | product, facing?: N|E|S|W}}, origin?: [lx, lz], rotation?: 0|90|180|270,
    //  level?: 0, house?: false, margin?: 1, lights?: 2, clear?: true, sim_timeout_s?: 60} | {result: true}
    //   s = stove, b = trash can built in; facing default: back to the wall (LayoutPlan.AutoDir).
    //   origin omitted: the first free area nearest the lot centre (FindFootprint). house: a real house round the map, margin
    //   tiles wider on each side: room tool, floor tool, door on the lot +z wall, ceiling lights, auto roof. Smallest real set:
    //   first non-debug catalog entries, no foundation (floors on the ground), no windows.
    // Order: live: clear, camera. Build: level, wall probe, room, floor, map walls (WallExistsBetween per edge), door. Buy: items,
    //   lights (each checked by lot object, tile, facing). Auto roof. Live: X by Go Here, routing plans. Any miss = r["error"];
    //   r["passed"] true only when everything is there. Poll {result: true} with failOn "error".
    // margin_top: 0 makes the map's top row the room's own top wall (rotation 0), roof: false leaves it out (a GIF that must see inside).
    // Roof: World.LotDoAutoRoof is a no-op, its native worker 0xC0FD60 is a bare ret [verified static]. The Auto Roof button's route instead:
    //   SetAutoroofActive in Build, else the roof tool's drag; World.HasAnyRoof is the check (Lot.RoofLevel is 1 on an empty lot as well).
    // Door: Build shows walls as stubs at this camera, so the door tool has no face to hit: walls up for the step, hover like a mouse,
    //   several pixels on both walls the camera sees. Door tool dead: Ctrl+drag a wall gap, the rest still runs, the job still fails.
    // Pixels (2026-11-07): World.GetScreenSpacePos, the game's own projection, for any point and in every mode. Floor picks cannot
    //   do this: they hit only tiles with a floor (0xAFF7D0 with its allow flags 0), so an empty lot gives no pick at all, and they
    //   return false in Build mode on floored lots too. They run once per mode as a second opinion only; r["calib"] shows both.
    //   The wall probe is the first drag: r["wall_probe"] says whether a forwarded wall drag lands, whatever follows.
    public static partial class Handlers
    {
        static void RegisterLayout()
        {
            Dispatcher.Register("build_layout", BuildLayout);
        }

        sealed class Lay
        {
            public Lot lot;
            public LayoutPlan plan;
            public int ox, oz, rot, level, margin, lights, simTimeoutS;
            public int hx0, hz0, hx1, hz1;   // footprint in lot vertices, with the margin
            public bool house, clear, roof;
            public Dictionary<char, BuildBuyProduct> prods = new Dictionary<char, BuildBuyProduct>();
            public Vector3 c00, ex, ez, camPos, camTarget;
            public double plane;
            public uint dwell = 10;
        }

        static Dictionary<string, object> sLayout;

        static object BuildLayout(Args a)
        {
            if (a.Bool("result", false))
            {
                if (sLayout == null) throw new McpException("no build_layout started");
                return sLayout;
            }
            if (sLayout != null && !(bool)sLayout["done"]) throw new McpException("build_layout still running");
            RequireLive();
            Lot lot = HomeLot();
            if (lot == null) throw new McpException("no home lot for the active household");
            Dictionary<char, int> items = new Dictionary<char, int>();
            Lay l = new Lay();
            l.lot = lot;
            items['s'] = -1;
            items['b'] = -1;
            Dictionary<char, string> names = new Dictionary<char, string>(), prefer = new Dictionary<char, string>();
            names['s'] = "stove";
            names['b'] = "trash";
            Dictionary<string, object> legend = a.Raw("legend") as Dictionary<string, object>;
            if (legend != null)
                foreach (KeyValuePair<string, object> kv in legend)
                {
                    Dictionary<string, object> e = kv.Value as Dictionary<string, object>;
                    if (kv.Key.Length != 1 || e == null) throw new McpException("legend: one char -> {name | product, facing?}");
                    char ch = kv.Key[0];
                    int dir = -1;
                    object f;
                    if (e.TryGetValue("facing", out f) && f != null)
                    {
                        dir = "NESW".IndexOf(f.ToString().ToUpperInvariant());
                        if (dir < 0) throw new McpException("legend " + ch + ": facing is N, E, S or W");
                    }
                    items[ch] = dir;
                    object prod;
                    if (e.TryGetValue("product", out prod) && prod != null) { l.prods[ch] = FindProduct(prod.ToString()); names.Remove(ch); }
                    else if (e.TryGetValue("name", out prod) && prod != null) names[ch] = prod.ToString().ToLowerInvariant();
                    else throw new McpException("legend " + ch + ": name or product required");
                    if (e.TryGetValue("prefer", out prod) && prod != null) prefer[ch] = prod.ToString().ToLowerInvariant();
                }
            List<string> rows = new List<string>();
            IList listed = a.Raw("map") as IList;
            if (listed != null) foreach (object o in listed) rows.Add(o.ToString());
            else rows.AddRange(a.Str("map").Split('\n'));
            try { l.plan = LayoutMap.Parse(rows, items); }
            catch (ArgumentException e) { throw new McpException("map: " + e.Message); }
            l.rot = (int)a.Long("rotation", 0);
            l.level = (int)a.Long("level", 0);
            l.house = a.Bool("house", false);
            l.roof = l.house && a.Bool("roof", true);
            l.margin = l.house ? (int)a.Long("margin", 1) : 0;
            l.lights = l.house ? (int)a.Long("lights", 2) : 0;
            l.clear = a.Bool("clear", true);
            l.simTimeoutS = (int)a.Long("sim_timeout_s", 60);
            int[] b;
            try { b = LayoutMap.Bounds(l.plan.W, l.plan.D, l.rot); }
            catch (ArgumentException e) { throw new McpException(e.Message); }
            List<object> origin = a.List("origin");
            if (origin != null && origin.Count == 2)
            {
                l.ox = Convert.ToInt32(origin[0], CultureInfo.InvariantCulture);
                l.oz = Convert.ToInt32(origin[1], CultureInfo.InvariantCulture);
            }
            else
            {
                // room for the item fronts: one tile more than the map (house: the margin does that)
                int pad = Math.Max(l.margin, 1);
                int[] free = FindFootprint(lot, b[2] - b[0] + 2 * pad, b[3] - b[1] + 2 * pad, null);
                if (free == null) throw new McpException("no free area of " + (b[2] - b[0] + 2 * pad) + "x" + (b[3] - b[1] + 2 * pad) + " tiles on " + LayLotName(lot) + " (" + sFootprintCensus + "): give origin or use an emptier lot");
                l.ox = free[0] + pad - b[0];
                l.oz = free[1] + pad - b[1];
            }
            l.hx0 = l.ox + b[0] - l.margin;
            l.hz0 = l.oz + b[1] - (int)a.Long("margin_top", l.margin);
            l.hx1 = l.ox + b[2] + l.margin;
            l.hz1 = l.oz + b[3] + l.margin;
            l.c00 = TileCorner(lot, 0, 0);
            l.ex = Minus(TileCorner(lot, 1, 0), l.c00);
            l.ez = Minus(TileCorner(lot, 0, 1), l.c00);
            Vector3 wp = Vector3.Zero;
            World.GetWorldPosition(lot.LotId, new LotLocation(l.ox, l.oz, (sbyte)l.level, 0), ref wp);
            l.plane = wp.y;
            int cost = 0;
            foreach (LayoutItem it in l.plan.Items)
            {
                if (!l.prods.ContainsKey(it.Ch))
                    l.prods[it.Ch] = LayProduct(names[it.Ch], it.Ch == 's' && (!IsLegend(legend, 's') || names[it.Ch] == "stove") ? typeof(Stove) : it.Ch == 'b' && names[it.Ch] == "trash" ? typeof(Trashcan) : typeof(GameObject), prefer.ContainsKey(it.Ch) ? prefer[it.Ch] : null);
                cost += (int)l.prods[it.Ch].Price;
            }
            Household hh = Household.ActiveHousehold;
            if (hh.FamilyFunds < cost) throw new McpException("not enough money: the items cost §" + cost + ", have §" + hh.FamilyFunds);

            Dictionary<string, object> r = Util.Obj();
            r["done"] = false;
            r["passed"] = false;
            LayPhase(r, "start");
            r["map"] = l.plan.Describe();
            r["lot"] = LayLotName(lot);
            r["origin"] = new int[] { l.ox, l.oz };
            r["rotation"] = l.rot;
            r["funds_before"] = hh.FamilyFunds;
            r["calib"] = Util.Obj();
            sLayout = r;
            RunAsync("build_layout", delegate
            {
                string error = null;
                try { LayRun(l, r); }
                catch (Exception e) { error = DescribeError(e); }
                finally
                {
                    // the mode change waits, and Sleep inside a finally throws "Attempting to yield in a non-yielding context!": a task of its own
                    RunAsync("build_layout exit", delegate
                    {
                        // never leave the job in Build or Buy mode
                        if (!LiveUp())
                        {
                            try { UserToolUtils.UserToolGeneric(4u, new ResourceKey(0uL, 0u, 0u)); GameStates.TransitionToLiveMode(); WaitReady(LiveUp, 3000); }
                            catch (Exception e) { r["exit_error"] = DescribeError(e); }
                        }
                        if (error != null) r["error"] = error;  // only now: failOn stops the repro at this key
                        r["passed"] = error == null;
                        if (hh != null) r["funds_after"] = hh.FamilyFunds;
                        r["done"] = true;
                        // shortcut: the "stove-wall:" tag borrows that line cap, so no native build; give layout its own tag when ts3native next changes
                        string report = Json.Write(r);
                        for (int i = 0; i < report.Length; i += 200) SwLine("stove-wall: layout report " + report.Substring(i, Math.Min(200, report.Length - i)));
                        SwLine("stove-wall: layout " + (error != null ? "FAIL" : "passed"));
                    });
                }
            });
            return r;
        }

        // empty lots have no name
        static string LayLotName(Lot lot) { return "lot " + Util.Id(lot.LotId) + " '" + Util.LotName(lot) + "' " + PlotSize(lot); }

        static void LayPhase(Dictionary<string, object> r, string phase)
        {
            r["phase"] = phase;
            SwLine("stove-wall: layout " + phase);  // GIF label
        }

        static bool IsLegend(Dictionary<string, object> legend, char c) { return legend != null && legend.ContainsKey(c.ToString()); }

        // first catalog product with this name part whose object passes `want`; the object class decides, not the name.
        // prefer: first look for one that also has this word in its name, then settle for any
        static BuildBuyProduct LayProduct(string name, Type want, string prefer)
        {
            for (int pass = prefer != null ? 0 : 1; pass < 2; pass++)
                foreach (BuildBuyProduct p in Catalog())
                {
                    string n = (p.CatalogName ?? "").ToLowerInvariant();
                    if (n.IndexOf(name) < 0 || (pass == 0 && n.IndexOf(prefer) < 0)) continue;
                    GameObject g = GlobalFunctions.CreateObjectOutOfWorld(p.ProductResourceKey) as GameObject;
                    if (g == null) continue;
                    bool ok = want.IsInstanceOfType(g);
                    g.Destroy();
                    if (ok) return p;
                }
            throw new McpException("no catalog product '" + name + "' of class " + want.Name);
        }

        static BuildBuyProduct LayLight()
        {
            foreach (BuildBuyProduct p in Catalog())
            {
                if ((p.CatalogName ?? "").ToLowerInvariant().IndexOf("light") < 0 && (p.CatalogName ?? "").ToLowerInvariant().IndexOf("lamp") < 0) continue;
                GameObject g = GlobalFunctions.CreateObjectOutOfWorld(p.ProductResourceKey) as GameObject;
                if (g == null) continue;
                bool ok = g is LightCeilingLamp;
                g.Destroy();
                if (ok) return p;
            }
            throw new McpException("no ceiling light in the catalog");
        }

        // ---------------------------------------------------------------- geometry

        static Vector3 LayAt(Lay l, double lx, double lz)
        {
            Vector3 p = Plus(Plus(l.c00, l.ex, (float)lx), l.ez, (float)lz);
            return new Vector3(p.x, (float)l.plane, p.z);
        }

        // map vertex -> lot vertex
        static int[] LayV(Lay l, int i, int j)
        {
            int[] v = LayoutMap.Vertex(i, j, l.rot);
            return new int[] { l.ox + v[0], l.oz + v[1] };
        }

        static int[] LayTile(Lay l, int x, int z)
        {
            int[] t = LayoutMap.Tile(x, z, l.rot);
            return new int[] { l.ox + t[0], l.oz + t[1] };
        }

        static LotLocation LayLoc(Lay l, int[] t) { return new LotLocation(t[0], t[1], (sbyte)l.level, 0); }

        // window pixel of a world point. 0x7CF430: (ndc.x + 1) * w / 2, (1 - ndc.y) * h / 2 of the view size [verified static]
        static double[] LayPx(Vector3 p)
        {
            Vector2 sp;
            if (!World.GetScreenSpacePos(p, out sp)) throw new McpException("no screen position for " + p + ": behind the camera?");
            return new double[] { sp.x, sp.y };
        }

        static double[] LayOnScreen(double[] px)
        {
            Rect area = UIManager.GetMainWindow().Area;
            if (px[0] < 4 || px[1] < 4 || px[0] > area.Width - 4 || px[1] > area.Height - 4)
                throw new McpException("point projects to pixel " + Math.Round(px[0]) + "," + Math.Round(px[1]) + " outside the " + (int)area.Width + "x" + (int)area.Height + " window: map too big for this camera");
            return px;
        }

        // ---------------------------------------------------------------- camera

        // high, tilted (not straight down: door clicks need the far wall's face), never the window focus; returns metres off the wish
        static double LayCamera(Vector3 pos, Vector3 target)
        {
            try { CameraController.DisableObjectFollow(); CameraController.Untether(); } catch (Exception) { }
            double off = 0;
            for (int tries = 0; tries < 5; tries++)
            {
                CameraController.SetPositionAndTarget(pos, target);
                Vector3 last = CameraController.GetPosition();
                for (int i = 0; i < 20; i++)
                {
                    Ticks(10);
                    Vector3 now = CameraController.GetPosition();
                    bool still = (now - last).Length() < 0.01f;
                    last = now;
                    if (still) break;
                }
                off = (last - pos).Length();
                if (off < 0.05) break;
            }
            return off;
        }

        // where the camera is and what the two projections say, so a miss can be told from a bad view
        static void LayView(Lay l, string mode, double off, Dictionary<string, object> r)
        {
            Vector3 cam = CameraController.GetPosition(), target = CameraController.GetTarget();
            Rect area = UIManager.GetMainWindow().Area;
            Vector3 centre = LayAt(l, (l.hx0 + l.hx1) / 2.0, (l.hz0 + l.hz1) / 2.0);
            double[] px = LayPx(centre);
            Dictionary<string, object> c = Util.Obj();
            c["camera"] = Pos(cam);
            c["target"] = Pos(target);
            c["camera_off_m"] = Math.Round(off, 3);
            c["window"] = (int)area.Width + "x" + (int)area.Height;
            c["centre_px"] = new double[] { Math.Round(px[0], 1), Math.Round(px[1], 1) };
            ((Dictionary<string, object>)r["calib"])[mode] = c;
            string picks;
            try
            {
                CalibrateAt(l.plane);
                c["picks"] = sCalibInfo;
                double[] fit = ScreenFit.Project(sHomography, centre.x, centre.z);
                c["pick_centre_px"] = new double[] { Math.Round(fit[0], 1), Math.Round(fit[1], 1) };
                picks = "fit " + Math.Round(fit[0], 1) + "," + Math.Round(fit[1], 1);
            }
            catch (McpException e) { c["picks_error"] = picks = e.Message; }
            SwLine("stove-wall: layout view " + mode + " camera " + cam + " target " + target + " off " + Math.Round(off, 2)
                + " window " + c["window"] + " centre " + Math.Round(px[0], 1) + "," + Math.Round(px[1], 1) + " picks: " + picks);
        }

        // ---------------------------------------------------------------- steps

        // the single question first: one plain drag along the map's first run (a wall the plan wants anyway), counted by WallExistsBetween
        static void LayWallProbe(Lay l, ResourceKey wall, Dictionary<string, object> r)
        {
            LayPhase(r, "wall probe");
            Tool(0u, wall, -1);
            UserToolUtils.SetUserToolMode(0u);
            int[] run = l.plan.Runs[0];
            int[] a = LayV(l, run[0], run[1]), b = LayV(l, run[2], run[3]);
            double[] p0 = LayOnScreen(LayPx(LayAt(l, a[0], a[1]))), p1 = LayOnScreen(LayPx(LayAt(l, b[0], b[1])));
            Drag(p0, p1, l.dwell);
            Ticks(30);
            List<object> missing = new List<object>();
            foreach (int[] e in l.plan.Edges) EdgeThere(l, e, missing);
            int landed = l.plan.Edges.Count - missing.Count;
            Dictionary<string, object> w = Util.Obj();
            w["from_px"] = new double[] { Math.Round(p0[0]), Math.Round(p0[1]) };
            w["to_px"] = new double[] { Math.Round(p1[0]), Math.Round(p1[1]) };
            w["edges_landed"] = landed;
            w["edges_in_map"] = l.plan.Edges.Count;
            r["wall_probe"] = w;
            SwLine("stove-wall: layout wall probe " + landed + " of " + l.plan.Edges.Count + " edges, drag " + p0[0] + "," + p0[1] + " to " + p1[0] + "," + p1[1]);
            if (landed == 0) throw new McpException("wall probe: a wall drag from pixel " + Math.Round(p0[0]) + "," + Math.Round(p0[1]) + " to " + Math.Round(p1[0]) + "," + Math.Round(p1[1]) + " built nothing");
        }

        static bool EdgeThere(Lay l, int[] e, List<object> missing)
        {
            bool vertical = e[0] == e[2];
            int[] a = vertical ? LayTile(l, e[0] - 1, e[1]) : LayTile(l, e[0], e[1] - 1), b = LayTile(l, e[0], e[1]);
            if (World.WallExistsBetween(l.lot.LotId, LayLoc(l, a), LayLoc(l, b))) return true;
            if (missing != null) missing.Add(a[0] + "," + a[1] + "|" + b[0] + "," + b[1]);
            return false;
        }

        static void LayDrag(Lay l, Vector3 p0, Vector3 p1, uint mods)
        {
            Drag(LayOnScreen(LayPx(p0)), LayOnScreen(LayPx(p1)), l.dwell, mods);
        }

        static void LayClear(Lay l, Dictionary<string, object> r)
        {
            int refund = 0;
            List<object> sold = new List<object>();
            foreach (GameObject o in l.lot.GetObjects<GameObject>())
            {
                LotLocation t = new LotLocation();
                if (o is Sim || o is Lot || o.IsWallObject || World.GetLotLocation(o.Position, ref t) != l.lot.LotId) continue;
                if (t.mX < l.hx0 - 1 || t.mX > l.hx1 || t.mZ < l.hz0 - 1 || t.mZ > l.hz1 || t.mLevel != l.level) continue;
                string name = Util.ObjectName(o);
                if (!o.CanBeSold() || o.InUse) throw new McpException("area not clear: " + name + " at tile " + t.mX + "," + t.mZ + " cannot be sold now");
                refund += o.Value;
                o.SellBase();
                o.Destroy();  // SellBase only pays and hides it outside Buy mode (as sell_object)
                sold.Add(name);
            }
            r["sold"] = sold;
            r["refund"] = refund;
        }

        static void LayHouseShell(Lay l, ResourceKey wall, Dictionary<string, object> r)
        {
            LayPhase(r, "room");
            Tool(0u, wall, -1);
            UserToolUtils.SetUserToolMode(2u);  // room, as BuildController's room button
            LayDrag(l, LayAt(l, l.hx0, l.hz0), LayAt(l, l.hx1, l.hz1), 0u);
            Ticks(30);
            UserToolUtils.SetUserToolMode(0u);
            List<object> missing = new List<object>();
            for (int x = l.hx0; x < l.hx1; x++)
                foreach (int z in new int[] { l.hz0, l.hz1 })
                    if (!World.WallExistsBetween(l.lot.LotId, new LotLocation(x, z - 1, (sbyte)l.level, 0), new LotLocation(x, z, (sbyte)l.level, 0))) missing.Add(x + "," + z);
            for (int z = l.hz0; z < l.hz1; z++)
                foreach (int x in new int[] { l.hx0, l.hx1 })
                    if (!World.WallExistsBetween(l.lot.LotId, new LotLocation(x - 1, z, (sbyte)l.level, 0), new LotLocation(x, z, (sbyte)l.level, 0))) missing.Add(x + "," + z);
            r["room_edges_missing"] = missing;
            if (missing.Count > 0) throw new McpException("room tool: " + missing.Count + " outer edges missing");
            LayPhase(r, "floor");
            BuildBuyPreset floor = null;
            foreach (object o in UserToolUtils.GetFloorPatternPresetListByType(0xFFFFFFFF))
            {
                BuildBuyPreset p = o as BuildBuyPreset;
                if (p != null && p.Product != null && (p.Product.CatalogName ?? "").ToLowerInvariant().IndexOf("debug") < 0) { floor = p; break; }
            }
            if (floor == null) throw new McpException("no floor pattern preset");
            Tool(2u, floor.Product.ProductResourceKey, (int)floor.ID);
            UserToolUtils.SetUserToolMode(0u);
            LayDrag(l, LayAt(l, l.hx0 + 0.5, l.hz0 + 0.5), LayAt(l, l.hx1 - 0.5, l.hz1 - 0.5), 0u);
            Ticks(30);
            int bare = 0;
            for (int x = l.hx0; x < l.hx1; x++)
                for (int z = l.hz0; z < l.hz1; z++)
                    if (l.level == 0 ? !GroundFloorAt(LayAt(l, x + 0.5, z + 0.5)) : !World.HasSolidFloor(l.lot.LotId, new LotLocation(x, z, (sbyte)l.level, 0))) bare++;
            r["floor"] = floor.Product.CatalogName;
            r["floor_price"] = (int)floor.Product.Price;
            r["floor_tiles_missing"] = bare;
            if (bare > 0) throw new McpException("floor tool: " + bare + " tiles without a floor");
        }

        // a mouse travelling to the pixel, as the wall drags send; the tool's preview follows the moves
        static void LayHover(double[] px)
        {
            for (int s = 6; s >= 0; s--)
            {
                UserToolUtils.ForwardUserToolMouseEvent(UserToolUtils.UserToolMouseEventType.kUserToolMouseMove, (float)px[0] - 6 * s, (float)px[1] - 2 * s, 0u, 0u);
                Ticks(1);
            }
        }

        // far wall's inner face, then the near wall's outer face (the two the camera sees), mid height, then low, middle tile first
        static GameObject LayDoorTry(Lay l, BuildBuyProduct door, List<object> tries)
        {
            Rect area = UIManager.GetMainWindow().Area;
            int mid = l.hx0 + (l.hx1 - l.hx0) / 2;
            foreach (double wallZ in new double[] { l.hz1 - 0.05, l.hz0 - 0.05 })
                foreach (int x in new int[] { mid, mid - 1, mid + 1 })
                    foreach (float up in new float[] { 1.3f, 0.4f })
                    {
                        Vector3 p = LayAt(l, x + 0.5, wallZ);
                        p.y += up;
                        double[] px = LayPx(p);
                        string tag = "tile " + x + " wall z " + wallZ + " height " + up + " pixel " + Math.Round(px[0]) + "," + Math.Round(px[1]);
                        if (px[0] < 4 || px[1] < 4 || px[0] > area.Width - 4 || px[1] > area.Height - 4) { tries.Add(tag + " off screen"); continue; }
                        List<ulong> before = LayIds(l);
                        Tool(4u, door.ProductResourceKey, -1);
                        Ticks(l.dwell);
                        LayHover(px);
                        Click(px, l.dwell);
                        // census after the tool lets go: a preview that hangs on the wall must not count as placed
                        Tool(4u, new ResourceKey(0uL, 0u, 0u), -1);
                        Ticks(l.dwell);
                        GameObject got = LayNew(l, before);
                        tries.Add(tag + (got != null ? " placed" : " missed"));
                        SwLine("stove-wall: layout door try " + tries[tries.Count - 1]);
                        if (got != null) return got;
                    }
            return null;
        }

        static void LayDoor(Lay l, ResourceKey wall, Dictionary<string, object> r)
        {
            LayPhase(r, "door");
            BuildBuyProduct door = null;
            foreach (BuildBuyProduct p in NamedProducts(UserToolUtils.BuildBuyProductType.Object, "", true))
                if ((p.BuildCategoryFlags & (uint)BuildBuyProduct.eBuildCategory.kBuildCategoryDoor) != 0) { door = p; break; }
            if (door == null) throw new McpException("no door in the catalog");
            uint view = LotManager.GetWallsViewMode();
            LotManager.SetWallsViewMode(300u);  // walls up, before the tool: SetWallsViewMode is a UserToolGeneric call
            Ticks(15);
            List<object> tries = new List<object>();
            r["door_tries"] = tries;
            r["door_price"] = (int)door.Price;
            GameObject got = LayDoorTry(l, door, tries);
            if (got != null) r["door"] = Placed(got);
            else
            {
                r["door_missing"] = door.CatalogName;
                LayPhase(r, "wall gap");
                int x = l.hx0 + (l.hx1 - l.hx0) / 2;
                Tool(0u, wall, -1);
                UserToolUtils.SetUserToolMode(0u);
                LayDrag(l, LayAt(l, x, l.hz1), LayAt(l, x + 1, l.hz1), 2u);  // Ctrl: the player's wall delete
                Ticks(30);
                r["wall_gap"] = x + "," + l.hz1 + (World.WallExistsBetween(l.lot.LotId, new LotLocation(x, l.hz1 - 1, (sbyte)l.level, 0), new LotLocation(x, l.hz1, (sbyte)l.level, 0)) ? " still walled" : " open");
                SwLine("stove-wall: layout wall gap " + r["wall_gap"]);
            }
            LotManager.SetWallsViewMode(view);
        }

        static void LayRoof(Lay l, Dictionary<string, object> r)
        {
            LayPhase(r, "roof");
            ulong id = l.lot.LotId;
            try { Responder.Instance.BuildModel.SetAutoroofActive(true, null); }
            catch (Exception e) { r["auto_roof_error"] = e.Message; }
            Ticks(45);
            r["roof_by"] = "auto";
            if (!World.HasAnyRoof(id))
            {
                r["roof_by"] = "tool";
                Tool(6u, l.lot.GetDefaultAutoRoofStyle(), -1);
                Vector3 a = LayAt(l, l.hx0, l.hz0), b = LayAt(l, l.hx1, l.hz1);
                a.y += 2.75f;  // the top of a wall [inferred]
                b.y += 2.75f;
                Drag(LayOnScreen(LayPx(a)), LayOnScreen(LayPx(b)), l.dwell);
                Ticks(30);
            }
            r["roof"] = World.HasAnyRoof(id);
            SwLine("stove-wall: layout roof HasAnyRoof " + r["roof"] + " by " + r["roof_by"]);
            r["roof_over_room"] = World.HasAnyRoofAtPos(LayAt(l, (l.hx0 + l.hx1) / 2.0, (l.hz0 + l.hz1) / 2.0));
            if (!World.HasAnyRoof(id)) r["roof_missing"] = "neither the auto roof nor the roof tool built one";
            World.LotShowRoof(id, false);  // Buy's clicks go to the floor, under it
        }

        static List<ulong> LayIds(Lay l)
        {
            List<ulong> ids = new List<ulong>();
            foreach (GameObject o in l.lot.GetObjects<GameObject>()) ids.Add(o.ObjectId.Value);
            return ids;
        }

        // a new non-Sim lot object: found by lot census, not OnObjectPlacedInLot (bb_cc_cycle saw that event 0 times in every run)
        static GameObject LayNew(Lay l, List<ulong> before)
        {
            foreach (GameObject o in l.lot.GetObjects<GameObject>())
                if (!(o is Sim) && !before.Contains(o.ObjectId.Value)) return o;
            return null;
        }

        // the catalogue click (BuyModel.SelectBuyProduct), hover, press on the tile, drag towards the facing, release: the
        // player's rotate-while-placing. dir null: plain click.
        static GameObject LayPlace(Lay l, BuildBuyProduct p, int[] tile, Vector3? dir)
        {
            List<ulong> before = LayIds(l);
            Responder.Instance.BuyModel.SelectBuyProduct(p, false);
            UIManager.GetSceneWindow().StartForwardingEventsToUserTools();
            Ticks(l.dwell);
            Vector3 at = LayAt(l, tile[0] + 0.5, tile[1] + 0.5);
            double[] px = LayOnScreen(LayPx(at));
            LayHover(px);
            if (dir == null) Click(px, l.dwell);
            else Drag(px, LayOnScreen(LayPx(Plus(at, dir.Value, 1.5f))), l.dwell);
            Ticks(l.dwell);
            UserToolUtils.UserToolGeneric(4u, new ResourceKey(0uL, 0u, 0u));
            Ticks(l.dwell);  // census after the tool lets go, as LayDoorTry
            return LayNew(l, before);
        }

        static void LayItems(Lay l, Dictionary<string, object> r)
        {
            LayPhase(r, "items");
            List<object> objs = new List<object>(), miss = new List<object>();
            r["objects"] = objs;
            r["objects_missing"] = miss;
            foreach (LayoutItem it in l.plan.Items)
            {
                int[] tile = LayTile(l, it.X, it.Z), d = LayoutMap.DirOnLot(it.Dir, l.rot);
                Vector3 fwd = Plus(Plus(Vector3.Zero, l.ex, d[0]), l.ez, d[1]);
                BuildBuyProduct p = l.prods[it.Ch];
                string tag = it.Ch + " at tile " + tile[0] + "," + tile[1];
                GameObject go = LayPlace(l, p, tile, fwd);
                if (go == null) { miss.Add(tag + ": the tool placed nothing (" + p.CatalogName + ")"); continue; }
                LotLocation t = new LotLocation();
                World.GetLotLocation(go.Position, ref t);
                double turn = Math.Abs(Facing(go.ForwardVector) - Facing(fwd));
                if (t.mX != tile[0] || t.mZ != tile[1]) miss.Add(tag + ": landed on " + t.mX + "," + t.mZ);
                if (Math.Min(turn, 360 - turn) > 10) miss.Add(tag + ": faces " + Facing(go.ForwardVector) + ", wanted " + Facing(fwd));
                Dictionary<string, object> o = Placed(go);
                o["char"] = it.Ch.ToString();
                o["tile"] = tile;
                objs.Add(o);
            }
            if (l.lights > 0)
            {
                LayPhase(r, "lights");
                BuildBuyProduct light = LayLight();
                for (int i = 0; i < l.lights; i++)
                {
                    int[] tile = { l.hx0 + (l.hx1 - l.hx0) * (i + 1) / (l.lights + 1), (l.hz0 + l.hz1) / 2 };
                    GameObject go = LayPlace(l, light, tile, null);
                    if (go == null) { miss.Add("ceiling light at tile " + tile[0] + "," + tile[1] + ": the tool placed nothing (" + light.CatalogName + ")"); continue; }
                    objs.Add(Placed(go));
                }
            }
            if (miss.Count > 0) throw new McpException("objects: " + string.Join("; ", miss.ConvertAll<string>(delegate(object x) { return x.ToString(); }).ToArray()));
        }

        // Go Here, a player's click (as go_here), and wait; the clock runs in live mode
        static void LaySim(Lay l, Dictionary<string, object> r)
        {
            if (l.plan.SimX < 0) return;
            LayPhase(r, "sim");
            Sim s = Util.ActiveSim();
            int[] tile = LayTile(l, l.plan.SimX, l.plan.SimZ);
            Vector3 at = LayAt(l, tile[0] + 0.5, tile[1] + 0.5);
            s.InteractionQueue.CancelAllInteractions();
            InteractionInstance ii = Terrain.GoHere.GetSingleton(s, at).CreateInstance(Terrain.Singleton, s,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            Terrain.GoHere gh = ii as Terrain.GoHere;
            if (gh != null) gh.SetDestination(at, false);
            SetClickHit(ii, at);
            if (!s.InteractionQueue.Add(ii)) throw new McpException("could not queue Go Here to X");
            int t0 = System.Environment.TickCount;
            float d = (s.Position - at).Length();
            while (d > 0.9f && System.Environment.TickCount - t0 < l.simTimeoutS * 1000)
            {
                Ticks(15);
                d = (s.Position - at).Length();
            }
            r["sim_at"] = Pos(s.Position);
            r["sim_tile"] = tile;
            r["sim_distance_m"] = Math.Round(d, 2);
            if (d > 0.9f) throw new McpException("the Sim did not reach X (tile " + tile[0] + "," + tile[1] + "): " + Math.Round(d, 1) + " m away after " + l.simTimeoutS + " s, doing " + (s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "nothing"));
        }

        // plans only, nothing walks: what routing now says about each object's slot, from the Sim's tile
        static void LaySlots(Lay l, Dictionary<string, object> r)
        {
            List<object> objs = r["objects"] as List<object>;
            if (objs == null) return;
            Sim s = Util.ActiveSim();
            foreach (object x in objs)
            {
                Dictionary<string, object> o = (Dictionary<string, object>)x;
                GameObject go = GameObject.GetObject(new ObjectGuid(Convert.ToUInt64(o["id"].ToString(), CultureInfo.InvariantCulture)));
                if (go == null) continue;
                try
                {
                    Vector3 slot = go.GetPositionOfSlot(Slot.RoutingSlot_0);
                    o["slot"] = Pos(slot);
                    o["wall_sim_to_slot"] = World.WallExistsBetweenPoints(l.lot.LotId, s.Position, slot, eRoomDefinition.PlacementBlocking);
                    Route route = s.CreateRoute();
                    route.PlanToSlot(go, Slot.RoutingSlot_0);
                    o["plan"] = route.PlanResult.mType.ToString();
                    o["path_m"] = Math.Round(route.PlanResult.mDistanceTotal, 1);
                }
                catch (Exception e) { o["slot_error"] = e.Message; }  // not every object has a routing slot
            }
        }

        static void LayRun(Lay l, Dictionary<string, object> r)
        {
            if (l.clear) { LayPhase(r, "clear"); LayClear(l, r); }
            LayPhase(r, "camera");
            double size = Math.Max(l.hx1 - l.hx0, l.hz1 - l.hz0);
            float up = (float)Math.Max(12, size * 1.3 + 6);
            Vector3 centre = LayAt(l, (l.hx0 + l.hx1) / 2.0, (l.hz0 + l.hz1) / 2.0);
            double off = LayCamera(new Vector3(centre.x - l.ez.x * up * 0.65f, centre.y + up, centre.z - l.ez.z * up * 0.65f), centre);
            // whatever camera the game settled on is the one Build and Buy go back to
            l.camPos = CameraController.GetPosition();
            l.camTarget = CameraController.GetTarget();
            LayView(l, "live", off, r);

            LayPhase(r, "build mode");
            GameStates.TransitionToBuildMode(l.lot, null);
            if (!WaitReady(BuildUp, 3000)) throw new McpException("build mode did not open (state " + SubState() + ")");
            Ticks(30);
            l.lot.SetDisplayLevel(l.level);
            Ticks(15);
            LayView(l, "build", LayCamera(l.camPos, l.camTarget), r);

            List<object> w = UserToolUtils.GetProductList(UserToolUtils.BuildBuyProductType.Wall);
            BuildBuyProduct wall = w != null && w.Count > 0 ? w[0] as BuildBuyProduct : null;
            if (wall == null) throw new McpException("no wall product");
            LayWallProbe(l, wall.ProductResourceKey, r);
            if (l.house) LayHouseShell(l, wall.ProductResourceKey, r);

            LayPhase(r, "walls");
            Tool(0u, wall.ProductResourceKey, -1);
            UserToolUtils.SetUserToolMode(0u);
            foreach (int[] run in l.plan.Runs)
            {
                int[] a = LayV(l, run[0], run[1]), b = LayV(l, run[2], run[3]);
                LayDrag(l, LayAt(l, a[0], a[1]), LayAt(l, b[0], b[1]), 0u);
            }
            Ticks(30);
            List<object> missing = new List<object>();
            foreach (int[] e in l.plan.Edges) EdgeThere(l, e, missing);
            r["edges_expected"] = l.plan.Edges.Count;
            r["edges_missing"] = missing;
            if (missing.Count > 0)
                throw new McpException("wall tool: " + missing.Count + " of " + l.plan.Edges.Count + " edges missing: " + string.Join(" ", missing.ConvertAll<string>(delegate(object x) { return x.ToString(); }).ToArray()));

            // what the walls and floor should have cost shows here: the game charges live, unless no_charge (EA's Edit Town route)
            r["wall_price"] = (int)wall.Price;
            r["no_charge"] = !LotManager.FamilyFundsCanBeCharged(null, null);
            r["funds_after_walls"] = Household.ActiveHousehold.FamilyFunds;

            if (l.house) LayDoor(l, wall.ProductResourceKey, r);
            if (l.roof) LayRoof(l, r);
            Tool(4u, new ResourceKey(0uL, 0u, 0u), -1);

            if (l.plan.Items.Count > 0 || l.lights > 0)
            {
                LayPhase(r, "buy mode");
                GameStates.TransitionToBuyMode(l.lot, null);
                if (!WaitReady(BuyUp, 3000)) throw new McpException("buy mode did not open (state " + SubState() + ")");
                Ticks(30);
                LayView(l, "buy", LayCamera(l.camPos, l.camTarget), r);
                LayItems(l, r);
            }
            if (l.roof) World.LotShowRoof(l.lot.LotId, true);
            LayPhase(r, "live mode");
            GameStates.TransitionToLiveMode();
            if (!WaitReady(LiveUp, 3000)) throw new McpException("live mode did not come back (state " + SubState() + ")");
            Ticks(30);
            foreach (LightGameObject g in l.lot.GetObjects<LightGameObject>()) g.SwitchLight(true, true);  // a player's light switch: a new room starts dark at night
            LaySim(l, r);
            LaySlots(l, r);
            if (r.ContainsKey("door_missing")) throw new McpException("door tool placed nothing in " + ((List<object>)r["door_tries"]).Count + " tries (" + r["door_missing"] + "), wall gap " + r["wall_gap"] + "; the rest ran");
            if (r.ContainsKey("roof_missing")) throw new McpException("roof: " + r["roof_missing"] + "; the rest ran");
            LayPhase(r, "done");
        }
    }
}
