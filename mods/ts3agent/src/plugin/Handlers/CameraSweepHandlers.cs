using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // camera_sweep (docs/280): a fixed camera-only motion script for camera-stutter profiling, so frame times can be split per
    // motion type. Only the in-game camera moves (CameraController.SetPositionAndTarget, as camera_pan): no Sim, lot or object is
    // touched, the game window is never moved or focused, nothing is saved.
    public static partial class Handlers
    {
        // kinds: 0 still, 1 zoom smooth, 2 zoom steps, 3 rotate (turns in the segment), 4 pan (there and back), 5 still at pan start
        struct SweepSeg { public string Name; public int Ms, Kind; public float Turns; public SweepSeg(string n, int ms, int k, float t) { Name = n; Ms = ms; Kind = k; Turns = t; } }
        static readonly SweepSeg[] kSweep = {
            new SweepSeg("still_start", 5000, 0, 0), new SweepSeg("zoom_smooth", 30000, 1, 0), new SweepSeg("zoom_steps", 30000, 2, 0),
            new SweepSeg("rotate", 30000, 3, 1), new SweepSeg("rotate_fast", 20000, 3, 2), new SweepSeg("still_pan", 5000, 5, 0),
            new SweepSeg("pan", 50000, 4, 0), new SweepSeg("still_end", 10000, 0, 0) };
        static List<object> sSweepSegs = new List<object>();
        static volatile bool sSweepRunning;

        // camera_sweep {lots?, minDist?, maxDist?, pitchDeg?, panHeight?} | {stop:true} | {status:true}
        // Busy area = the `lots` (default 8) lots around the densest lot (DenseLotPath, as camera_pan); orbit centre = their mean.
        // Fixed 180 s schedule by wall clock (kSweep): still 5 s at mid zoom; zoom_smooth 30 s mid -> maxDist (default 180 m) ->
        // minDist (default 5 m) -> mid, log-scale sine; zoom_steps 30 s, 12 jumps of 2.5 s over 6 log-spaced levels max..min..max,
        // then mid; rotate 30 s one 360 deg orbit at mid; rotate_fast 20 s two orbits; still_pan 5 s at the pan start; pan 50 s
        // through the lots there and back at panHeight (default 25 m) like camera_pan; still_end 10 s (backlog after the camera
        // stops). Mid = sqrt(min x max), pitch (default 40 deg) above the horizon. Returns the plan at once (epoch0 + offsets);
        // status: the segments run so far with epochMs (Date.now clock, frames.csv epochMs) and tickMs (GetTickCount, Apex
        // FrameTimeline tick_ms low 32 bits) at start and end, camera sets and the camera-to-target distance at the end (the
        // zoom the game allowed). Shares the camera_pan generation: camera_pan / camera_jumps / stop end a running sweep.
        static object CameraSweep(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("stop") || a.Has("status"))
            {
                if (a.Has("stop")) { ++sPanGen; r["stopped"] = true; }
                lock (sSweepSegs) r["segments"] = new List<object>(sSweepSegs);
                r["running"] = sSweepRunning;
                return r;
            }
            int gen = ++sPanGen;
            List<Vector3> path = DenseLotPath((int)a.Long("lots", 8));
            if (path.Count < 2) throw new McpException("no path: fewer than 2 lots in this world");
            float dMin = (float)a.Double("minDist", 5), dMax = (float)a.Double("maxDist", 180), pitch = (float)(a.Double("pitchDeg", 40) * Math.PI / 180);
            float panH = (float)a.Double("panHeight", 25);
            if (!(dMin >= 1 && dMax > dMin && pitch > 0.05f && pitch < 1.5f && panH >= 2)) throw new McpException("minDist >= 1, maxDist > minDist, pitchDeg 3..85, panHeight >= 2");
            Vector3 c = Vector3.Empty;
            foreach (Vector3 p in path) c = c + p;
            c = c * (1f / path.Count);
            c = new Vector3(c.x, World.GetTerrainHeight(c.x, c.z), c.z);
            float lMin = (float)Math.Log(dMin), lMax = (float)Math.Log(dMax), lMid = (lMin + lMax) / 2;
            float total = 0;
            for (int i = 1; i < path.Count; i++) total += Flat(path[i] - path[i - 1]).Length();
            try { CameraController.DisableObjectFollow(); CameraController.Untether(); } catch (Exception) { }
            lock (sSweepSegs) sSweepSegs.Clear();
            long epoch0 = EpochMs();
            int t0 = System.Environment.TickCount;
            sSweepRunning = true;
            Simulator.AddObject(new OneShotFunctionTask(delegate
            {
                try
                {
                    int sets = 0, cur = -1;
                    Dictionary<string, object> open = null;
                    while (sPanGen == gen)
                    {
                        int el = System.Environment.TickCount - t0, s = 0, left = el;
                        while (s < kSweep.Length && left >= kSweep[s].Ms) { left -= kSweep[s].Ms; s++; }
                        if (s != cur)
                        {
                            if (open != null) CloseSeg(open, epoch0, t0, el, sets);
                            if (s >= kSweep.Length) break;
                            open = Util.Obj();
                            open["name"] = kSweep[s].Name;
                            open["epochMs0"] = epoch0 + el; open["tickMs0"] = (long)(uint)(t0 + el);
                            lock (sSweepSegs) sSweepSegs.Add(open);
                            cur = s; sets = 0;
                        }
                        SweepSeg g = kSweep[s];
                        float u = (float)left / g.Ms, ld = lMid, yaw = 0;
                        if (g.Kind == 1) ld = lMid + (lMax - lMid) * (float)Math.Sin(2 * Math.PI * u);
                        else if (g.Kind == 2)
                        {
                            // 11 levels down and up (L0 = max .. L5 = min .. L0) then mid: 12 steps
                            int k = Math.Min(11, (int)(u * 12));
                            ld = k == 11 ? lMid : lMax + (lMin - lMax) * (k <= 5 ? k : 10 - k) / 5f;
                        }
                        else if (g.Kind == 3) yaw = (float)(2 * Math.PI * g.Turns * u);
                        if (g.Kind == 4 || g.Kind == 5)
                        {
                            float along = g.Kind == 5 ? 0 : (u < 0.5f ? u * 2 : 2 - u * 2) * total;
                            bool back = g.Kind == 4 && u >= 0.5f;
                            Vector3 at = path[0], dir = Flat(path[1] - path[0]);
                            for (int i = 1; i < path.Count; i++)
                            {
                                Vector3 seg = Flat(path[i] - path[i - 1]);
                                float len = seg.Length();
                                if (along <= len || i == path.Count - 1) { at = path[i - 1] + seg * (Math.Min(along, len) / Math.Max(0.01f, len)); dir = seg; break; }
                                along -= len;
                            }
                            if (back) dir = dir * -1f;
                            if (dir.Length() < 0.01f) dir = new Vector3(0, 0, 1);
                            dir = dir * (1f / dir.Length());
                            Vector3 tgt = new Vector3(at.x, World.GetTerrainHeight(at.x, at.z), at.z);
                            CameraController.SetPositionAndTarget(tgt - dir * panH + new Vector3(0, panH, 0), tgt);
                        }
                        else
                        {
                            float d = (float)Math.Exp(ld), h = d * (float)Math.Cos(pitch);
                            CameraController.SetPositionAndTarget(c + new Vector3(-(float)Math.Sin(yaw) * h, d * (float)Math.Sin(pitch), -(float)Math.Cos(yaw) * h), c);
                        }
                        sets++;
                        Simulator.Sleep(0);
                    }
                    if (open != null && !open.ContainsKey("epochMs1")) { CloseSeg(open, epoch0, t0, System.Environment.TickCount - t0, sets); open["cut"] = true; }
                }
                finally { sSweepRunning = false; }
            }));
            List<object> plan = new List<object>();
            int off = 0;
            foreach (SweepSeg g in kSweep) { Dictionary<string, object> p = Util.Obj(); p["name"] = g.Name; p["offMs"] = off; p["ms"] = g.Ms; plan.Add(p); off += g.Ms; }
            r["epochMs0"] = epoch0; r["tickMs0"] = (long)(uint)t0; r["plan"] = plan; r["totalMs"] = off;
            r["centre"] = Pos(c); r["panLengthM"] = Math.Round(total); r["minDist"] = dMin; r["maxDist"] = dMax; r["midDist"] = Math.Round(Math.Exp(lMid), 1);
            return r;
        }

        static void CloseSeg(Dictionary<string, object> seg, long epoch0, int t0, int el, int sets)
        {
            seg["epochMs1"] = epoch0 + el; seg["tickMs1"] = (long)(uint)(t0 + el); seg["sets"] = sets;
            seg["distEnd"] = Math.Round((CameraController.GetPosition() - CameraController.GetTarget()).Length(), 1);
        }

        static long EpochMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    }
}
