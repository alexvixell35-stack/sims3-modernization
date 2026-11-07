using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.UI;

namespace Ts3Agent
{
    // Build mode through the game's own tools: the native wall/floor/object tools are driven with the same
    // calls the build UI makes (UserToolGeneric + forwarded mouse events at window pixels). World points are
    // turned into pixels with a ground-plane homography fitted from PickFloorAtWindowPoint samples.
    public static partial class Handlers
    {
        static void RegisterBuild()
        {
            Dispatcher.Register("build_mode", BuildMode);
            Dispatcher.Register("pick", Pick);
            Dispatcher.Register("calibrate", Calibrate);
            Dispatcher.Register("tile_info", TileInfo);
            Dispatcher.Register("tool_select", ToolSelect);
            Dispatcher.Register("tool_drag", ToolDrag);
            Dispatcher.Register("build_level", BuildLevel);
            Dispatcher.Register("build_presets", BuildPresets);
            Dispatcher.Register("auto_roof", AutoRoof);
        }

        static Lot HomeLot()
        {
            Household hh = Household.ActiveHousehold;
            if (hh != null && hh.LotHome != null) return hh.LotHome;
            return Util.ActiveSim().LotCurrent;
        }

        static object BuildMode(Args a)
        {
            bool on = a.Bool("on", true);
            Lot lot = HomeLot();
            RunAsync(on ? "enter build mode" : "enter live mode", delegate
            {
                if (on) GameStates.TransitionToBuildMode(lot, null);
                else GameStates.TransitionToLiveMode();
            });
            sHomography = null;
            Dictionary<string, object> r = Util.Obj();
            r["requested"] = on ? "build" : "live";
            return r;
        }

        static object Pick(Args a)
        {
            Vector3 hit = Vector3.Zero;
            Dictionary<string, object> r = Util.Obj();
            r["hit"] = World.PickFloorAtWindowPoint((int)a.Long("x"), (int)a.Long("y"), ref hit);
            r["world"] = Pos(hit);
            return r;
        }

        // ------------------------------------------------------------ world <-> window pixels

        // Homography for one world plane (ScreenFit), fitted from floor picks on that plane.
        static double[] sHomography;
        static double sCalibPlane = double.NaN; // height the fit is for; NaN = all picks
        static Dictionary<string, object> sCalibInfo;
        static Vector3 sCalibCamPos, sCalibCamTarget;

        static bool CameraMoved()
        {
            Vector3 p = CameraController.GetPosition(), t = CameraController.GetTarget();
            return (p - sCalibCamPos).Length() > 0.01f || (t - sCalibCamTarget).Length() > 0.01f;
        }

        // calibrate {y?}: y keeps only the picks within 1.25 m of that height (less than half a floor).
        static object Calibrate(Args a)
        {
            return CalibrateAt(a.Double("y", double.NaN));
        }

        static Dictionary<string, object> CalibrateAt(double plane)
        {
            sHomography = null;
            sCalibCamPos = CameraController.GetPosition();
            sCalibCamTarget = CameraController.GetTarget();
            Rect area = UIManager.GetMainWindow().Area;
            int w = (int)area.Width, h = (int)area.Height;
            List<double[]> picks = new List<double[]>(); // wx, wy, wz, px, py
            double ymin = double.MaxValue, ymax = double.MinValue;
            for (int i = 1; i < 20; i++)
                for (int j = 1; j < 16; j++)
                {
                    int px = w * i / 20, py = h * j / 16;
                    Vector3 hit = Vector3.Zero;
                    if (!World.PickFloorAtWindowPoint(px, py, ref hit)) continue;
                    picks.Add(new double[] { hit.x, hit.y, hit.z, px, py });
                    ymin = Math.Min(ymin, hit.y);
                    ymax = Math.Max(ymax, hit.y);
                }
            if (CameraMoved()) throw new McpException("camera moved while calibrating; wait for it to settle");
            List<double[]> pts = ScreenFit.OnPlane(picks, plane, 1.25);
            if (pts.Count < 6)
                throw new McpException("only " + pts.Count + " of " + picks.Count + " floor picks hit"
                    + (double.IsNaN(plane) ? "" : " the plane at y=" + Math.Round(plane, 2))
                    + (picks.Count > 0 ? " (pick heights " + Math.Round(ymin, 2) + ".." + Math.Round(ymax, 2) + ")" : "")
                    + "; 285 points tried on the " + w + "x" + h + " window, " + (285 - picks.Count) + " found no floor tile (bare ground is no floor): point the camera at a floored lot");
            double[] H = ScreenFit.FitHomography(pts);
            Dictionary<string, object> r = Util.Obj();
            r["samples"] = pts.Count;
            r["picks"] = picks.Count;
            if (!double.IsNaN(plane)) r["plane_y"] = Math.Round(plane, 2);
            r["max_error_px"] = Math.Round(ScreenFit.MaxError(H, pts), 2);
            r["window"] = w + "x" + h;
            sHomography = H;
            sCalibPlane = plane;
            sCalibInfo = r;
            return r;
        }

        static double[] ToPixels(double x, double z, double plane)
        {
            bool samePlane = double.IsNaN(plane) ? double.IsNaN(sCalibPlane) : Math.Abs(plane - sCalibPlane) < 0.01;
            if (sHomography == null || !samePlane || CameraMoved()) CalibrateAt(plane);
            return ScreenFit.Project(sHomography, x, z);
        }

        // ------------------------------------------------------------ tiles

        static object TileInfo(Args a)
        {
            Lot lot = HomeLot();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("lx"))
            {
                LotLocation loc = new LotLocation((int)a.Long("lx"), (int)a.Long("lz"), (sbyte)a.Long("level", 0), 0);
                Vector3 wp = Vector3.Zero;
                World.GetWorldPosition(lot.LotId, loc, ref wp);
                r["world"] = Pos(wp);
                r["room"] = World.GetRoomId(lot.LotId, loc);
                r["solid_floor"] = World.HasSolidFloor(lot.LotId, loc);
                if (a.Has("lx2"))
                {
                    LotLocation b = new LotLocation((int)a.Long("lx2"), (int)a.Long("lz2"), loc.mLevel, 0);
                    r["wall_between"] = World.WallExistsBetween(lot.LotId, loc, b);
                }
            }
            else
            {
                Vector3 p = new Vector3((float)a.Double("x"), (float)a.Double("y", 0), (float)a.Double("z"));
                if (!a.Has("y")) p.y = World.GetTerrainHeight(p.x, p.z);
                LotLocation loc = new LotLocation();
                r["lot_id"] = Util.Id(World.GetLotLocation(p, ref loc));
                r["lx"] = loc.mX;
                r["lz"] = loc.mZ;
                r["level"] = loc.mLevel;
                r["room"] = World.GetRoomId(p);
            }
            return r;
        }

        // ------------------------------------------------------------ tools

        static ResourceKey ParseKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return new ResourceKey(0uL, 0u, 0u);
            string[] k = s.Split(':');
            return new ResourceKey(ulong.Parse(k[2], NumberStyles.HexNumber), uint.Parse(k[0], NumberStyles.HexNumber),
                                   uint.Parse(k[1], NumberStyles.HexNumber));
        }

        // tool: 0 wall, 1 wallpaper, 2 floor, 3 foundation, 4 object, 8 fence, 12 sledgehammer, 16 stairs, 17 railings,
        // 20 column, 25 wall drag (room), 30 half wall (Sims3.UI.ToolMessageID in UI.dll). product "first": the first
        // catalog entry for that tool, as the build catalog fills it: the first wall/floor pattern preset for 1/2, else the
        // first UserToolUtils.GetProductList entry of the same number (the BuildBuyProductType values equal the tool ids for
        // 0, 2, 3, 8, 16, 17, 20); no entry leaves the empty key.
        static object ToolSelect(Args a)
        {
            uint tool = (uint)a.Long("tool");
            string prod = a.Has("product") ? a.Str("product") : null;
            bool hasPreset = a.Has("preset");
            uint preset = hasPreset ? (uint)a.Long("preset") : 0u;
            ResourceKey key = ParseKey(prod == "first" ? null : prod);
            if (prod == "first" && (tool == 1 || tool == 2))
            {
                List<object> ps = tool == 1 ? UserToolUtils.GetWallPatternPresetListByType(0xFFFFFFFF)
                                            : UserToolUtils.GetFloorPatternPresetListByType(0xFFFFFFFF);
                BuildBuyPreset first = null;
                if (ps != null)
                    foreach (object o in ps)
                    {
                        first = o as BuildBuyPreset;
                        if (first != null && first.Product != null) break;
                        first = null;
                    }
                if (first == null) throw new McpException("no pattern preset for tool " + tool);
                key = first.Product.ProductResourceKey;
                preset = (uint)first.ID;
                hasPreset = true;
            }
            else if (prod == "first")
            {
                List<object> list = UserToolUtils.GetProductList((UserToolUtils.BuildBuyProductType)tool);
                BuildBuyProduct first = list != null && list.Count > 0 ? list[0] as BuildBuyProduct : null;
                if (first != null) key = first.ProductResourceKey;
            }
            if (hasPreset) UserToolUtils.UserToolGeneric(tool, key, preset);
            else UserToolUtils.UserToolGeneric(tool, key);
            UIManager.GetSceneWindow().StartForwardingEventsToUserTools();
            Dictionary<string, object> r = Util.Obj();
            r["tool"] = tool;
            r["product"] = KeyString(key);
            if (hasPreset) r["preset"] = preset;
            return r;
        }

        // build_level {up?: true} or {level: n}: the build UI's floor up/down buttons (ZoomPuck -> Lot.ChangeDisplayLevel) on
        // the home lot. Walls, floors and stairs are drawn on the displayed level. aim: [lx, lz] also points the camera at
        // that home-lot tile (from 15 m up and 12 m back), so a later tool_drag with "tiles" can calibrate on screen.
        // Returns the level after the change.
        static object BuildLevel(Args a)
        {
            Lot lot = HomeLot();
            bool changed = a.Has("level") ? lot.SetDisplayLevel((int)a.Long("level"))
                                          : lot.ChangeDisplayLevel(a.Bool("up", true) ? 1 : -1);
            sHomography = null;  // floor picks move with the displayed level
            List<object> aim = a.List("aim");
            if (aim != null && aim.Count == 2)
            {
                Vector3 t = Vector3.Zero;
                World.GetWorldPosition(lot.LotId, new LotLocation(Convert.ToInt32(aim[0], CultureInfo.InvariantCulture),
                    Convert.ToInt32(aim[1], CultureInfo.InvariantCulture), (sbyte)lot.CurrentLotDisplayLevel, 0), ref t);
                CameraController.SetPositionAndTarget(new Vector3(t.x, t.y + 15f, t.z - 12f), t);
            }
            Dictionary<string, object> r = Util.Obj();
            r["changed"] = changed;
            r["level"] = lot.CurrentLotDisplayLevel;
            r["top"] = lot.RoofLevel;
            return r;
        }

        // Press at the first point, move through the rest, release at the last (world x,z pairs, or pixels with
        // "pixels": true, or home-lot tile coordinates [lx,lz] on the displayed level with "tiles": true, so a repro needs
        // no world coordinates). One drag draws a wall run, paints a floor rectangle, or clicks (single point).
        // Fails before any mouse event when points at least 0.5 m apart project closer than min_px (default 8), and
        // for tiles when a floor pick at a projected pixel lands more than 1 m from its tile. Returns pixels, calib
        // (the fit used) and, for tiles, check_m (largest pick-back distance).
        static object ToolDrag(Args a)
        {
            List<object> raw = a.List("points");
            if (raw == null || raw.Count == 0) throw new McpException("points: [[x,z], ...] required");
            bool pixels = a.Bool("pixels", false);
            bool tiles = a.Bool("tiles", false);
            Lot tileLot = tiles ? HomeLot() : null;
            int frames = (int)a.Long("frames", 3);
            List<double[]> world = new List<double[]>();
            double plane = double.NaN; // tiles: the displayed level's floor height, so the fit ignores other planes
            foreach (object o in raw)
            {
                IList p = (IList)o;
                double x = Convert.ToDouble(p[0], CultureInfo.InvariantCulture), z = Convert.ToDouble(p[1], CultureInfo.InvariantCulture);
                if (tiles)
                {
                    Vector3 wp = Vector3.Zero;
                    World.GetWorldPosition(tileLot.LotId, new LotLocation((int)x, (int)z, (sbyte)tileLot.CurrentLotDisplayLevel, 0), ref wp);
                    x = wp.x;
                    z = wp.z;
                    if (double.IsNaN(plane)) plane = wp.y;
                }
                world.Add(new double[] { x, z });
            }
            List<double[]> px = new List<double[]>();
            foreach (double[] p in world) px.Add(pixels ? p : ToPixels(p[0], p[1], plane));
            double checkM = 0;
            if (!pixels)
            {
                string bad = ScreenFit.Degenerate(world, px, a.Double("min_px", 8));
                if (bad != null) throw new McpException("degenerate drag: " + bad);
                if (tiles)
                    // Pick back through each pixel: the floor there must be the tile asked for (tiles are 1 m).
                    for (int i = 0; i < px.Count; i++)
                    {
                        Vector3 hit = Vector3.Zero;
                        double d = World.PickFloorAtWindowPoint((int)px[i][0], (int)px[i][1], ref hit)
                            ? Math.Sqrt((hit.x - world[i][0]) * (hit.x - world[i][0]) + (hit.z - world[i][1]) * (hit.z - world[i][1]))
                            : double.PositiveInfinity;
                        checkM = Math.Max(checkM, d);
                        if (d > 1.0)
                            throw new McpException("tile point " + i + " projects to a pixel whose floor pick is "
                                + (double.IsInfinity(d) ? "empty" : Math.Round(d, 2) + " m away") + "; calibration "
                                + Json.Write(sCalibInfo));
                    }
            }
            uint button = (uint)a.Long("button", 1000);
            uint mods = (uint)a.Long("modifiers", 0);
            RunAsync("tool drag " + px.Count + " pts", delegate
            {
                UIManager.GetSceneWindow().StartForwardingEventsToUserTools();
                Move(px[0], mods, frames);
                UserToolUtils.ForwardUserToolMouseEvent(UserToolUtils.UserToolMouseEventType.kUserToolMouseDown,
                                                        (float)px[0][0], (float)px[0][1], button, mods);
                Simulator.Sleep((uint)frames);
                for (int i = 1; i < px.Count; i++)
                {
                    // Interpolate so the tool sees a continuous drag.
                    for (int s = 1; s <= 8; s++)
                    {
                        double t = s / 8.0;
                        Move(new double[] { px[i - 1][0] + (px[i][0] - px[i - 1][0]) * t, px[i - 1][1] + (px[i][1] - px[i - 1][1]) * t },
                             mods, 1);
                    }
                }
                double[] last = px[px.Count - 1];
                Simulator.Sleep((uint)frames);
                UserToolUtils.ForwardUserToolMouseEvent(UserToolUtils.UserToolMouseEventType.kUserToolMouseUp,
                                                        (float)last[0], (float)last[1], button, mods);
                Simulator.Sleep((uint)frames);
            });
            List<object> outPx = new List<object>();
            foreach (double[] p in px) outPx.Add(new double[] { Math.Round(p[0], 1), Math.Round(p[1], 1) });
            Dictionary<string, object> r = Util.Obj();
            r["pixels"] = outPx;
            if (!pixels) r["calib"] = sCalibInfo;
            if (tiles) r["check_m"] = Math.Round(checkM, 2);
            return r;
        }

        static void Move(double[] p, uint mods, int frames)
        {
            UserToolUtils.ForwardUserToolMouseEvent(UserToolUtils.UserToolMouseEventType.kUserToolMouseMove,
                                                    (float)p[0], (float)p[1], 0u, mods);
            Simulator.Sleep((uint)Math.Max(1, frames));
        }

        static object BuildPresets(Args a)
        {
            string kind = a.Str("kind", "floor");
            uint cat = (uint)a.Long("category", 0xFFFFFFFF);
            List<object> src = kind == "wall" ? UserToolUtils.GetWallPatternPresetListByType(cat)
                                              : UserToolUtils.GetFloorPatternPresetListByType(cat);
            string q = a.Has("query") ? a.Str("query").ToLowerInvariant() : null;
            List<object> list = new List<object>();
            if (src != null)
                foreach (object o in src)
                {
                    BuildBuyPreset p = o as BuildBuyPreset;
                    if (p == null || p.Product == null) continue;
                    string name = p.Product.CatalogName ?? "";
                    if (q != null && name.ToLowerInvariant().IndexOf(q) < 0) continue;
                    Dictionary<string, object> d = Util.Obj();
                    d["name"] = name;
                    d["product"] = KeyString(p.Product.ProductResourceKey);
                    d["preset"] = p.ID;
                    d["price"] = (int)p.Product.Price;
                    list.Add(d);
                    if (list.Count >= (int)a.Long("limit", 40)) break;
                }
            return list;
        }

        static object AutoRoof(Args a)
        {
            Lot lot = HomeLot();
            World.LotDoAutoRoof(lot.LotId);
            Dictionary<string, object> r = Util.Obj();
            r["roofed"] = lot.Name;
            return r;
        }
    }
}
