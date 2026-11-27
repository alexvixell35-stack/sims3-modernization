using System;
using System.Collections.Generic;
using System.Text;

namespace Ts3Fixes
{
    // docs/325 instprobe: the pure part (no game types), tested offline by tools/selftest/instprobe. Splits one timed
    // instantiation into its parts, names the phase, keeps every sample per (phase, part) and prints p50 / p95 / max.
    public static class InstProbeCore
    {
        // Summary order. total = outermost entry to return (wall, includes the age-up wait's sleeps); busy = total - ageup
        // (sim-thread work); spot = the Lot entry's spot search before Instantiate(Vector3); create = the Sim's
        // CreateObjectWithOverrides, split into oncreation (Sim.OnCreation), onstartup (Sim.OnStartup) and native (the rest:
        // object, rig and model setup in the engine); footprint = EnableDynamicFootprint + ForceUpdateDynamicFootprint;
        // initobj = AddInitialObjects; rest = Instantiate(Vector3) minus ageup, create, footprint, initobj (skills, occult,
        // kSimInstantiated listeners, health); f0 = the sim frame the instantiation ended in, f1 = the next one, f1x = f1
        // over the session's median frame; destroy = Sim.Destroy; startuponly = Sim.OnStartup outside any instantiation.
        public static readonly string[] kParts = { "total", "busy", "spot", "ageup", "create", "native", "oncreation", "onstartup",
            "footprint", "initobj", "rest", "f0", "f1", "f1x", "destroy", "startuponly" };
        public const int kMaxSamples = 20000;  // per (phase, part); a session of ~1000 instantiations stays far below
        public const int kLoadWindowMs = 120000;  // instantiations within 2 min after a world load count as "load"

        // Nearest-rank percentile (pct 1..100) of the samples; 0 for none. Sorts a copy.
        public static int Pct(List<int> v, int pct)
        {
            if (v == null || v.Count == 0) return 0;
            int[] a = v.ToArray();
            Array.Sort(a);
            int idx = (int)Math.Ceiling(pct / 100.0 * a.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= a.Length) idx = a.Length - 1;
            return a[idx];
        }

        // Median of the first n entries of a ring (frame times); 0 for none.
        public static int Median(int[] ring, int n)
        {
            if (ring == null || n <= 0) return 0;
            if (n > ring.Length) n = ring.Length;
            int[] a = new int[n];
            Array.Copy(ring, a, n);
            Array.Sort(a);
            return a[(n - 1) / 2];
        }

        // The phase an event belongs to: a harness mark wins; else "load" before the first world load ended (loadEndMs < 0)
        // or within kLoadWindowMs after it, else "steady". Times are Environment.TickCount ms (wrap-safe difference).
        public static string Phase(int nowMs, int loadEndMs, bool loaded, string mark)
        {
            if (!string.IsNullOrEmpty(mark)) return mark;
            if (!loaded) return "load";
            return unchecked(nowMs - loadEndMs) < kLoadWindowMs ? "load" : "steady";
        }

        // The derived parts of one instantiation, all in us, never below 0. Order of the result: kParts[0..10].
        public static int[] Split(uint totalUs, uint innerUs, uint ageupUs, uint createUs, uint onCreUs, uint onStartUs, uint footUs, uint initUs)
        {
            int[] r = new int[11];
            r[0] = Clip(totalUs);
            r[1] = Sub(totalUs, ageupUs);
            r[2] = Sub(totalUs, innerUs);
            r[3] = Clip(ageupUs);
            r[4] = Clip(createUs);
            r[5] = Sub(createUs, (ulong)onCreUs + onStartUs);
            r[6] = Clip(onCreUs);
            r[7] = Clip(onStartUs);
            r[8] = Clip(footUs);
            r[9] = Clip(initUs);
            r[10] = Sub(innerUs, (ulong)ageupUs + createUs + footUs + initUs);
            return r;
        }

        static int Clip(ulong v) { return v > int.MaxValue ? int.MaxValue : (int)v; }
        static int Sub(ulong a, ulong b) { return a > b ? Clip(a - b) : 0; }

        public static string Ms(int us) { return (us / 1000.0).ToString("0.00"); }

        public sealed class Stats
        {
            readonly Dictionary<string, Dictionary<string, List<int>>> mBy = new Dictionary<string, Dictionary<string, List<int>>>();
            public int Dropped;

            public void Add(string phase, string part, int us)
            {
                Dictionary<string, List<int>> p;
                if (!mBy.TryGetValue(phase, out p)) mBy[phase] = p = new Dictionary<string, List<int>>();
                List<int> l;
                if (!p.TryGetValue(part, out l)) p[part] = l = new List<int>();
                if (l.Count >= kMaxSamples) { Dropped++; return; }
                l.Add(us);
            }

            public void Clear() { mBy.Clear(); Dropped = 0; }

            public List<int> Get(string phase, string part)
            {
                Dictionary<string, List<int>> p;
                List<int> l;
                return mBy.TryGetValue(phase, out p) && p.TryGetValue(part, out l) ? l : null;
            }

            // One line per (phase, part) with samples: "instprobe: summary <why> <phase> <part> n N p50 X p95 Y max Z ms" (one log prefix)
            public List<string> Lines(string why)
            {
                List<string> o = new List<string>();
                List<string> phases = new List<string>(mBy.Keys);
                phases.Sort(StringComparer.Ordinal);
                foreach (string ph in phases)
                    foreach (string part in kParts)
                    {
                        List<int> l = Get(ph, part);
                        if (l == null || l.Count == 0) continue;
                        o.Add(Line(why, ph, part, l));
                    }
                return o;
            }

            public static string Line(string why, string phase, string part, List<int> l)
            {
                StringBuilder sb = new StringBuilder("instprobe: summary ");
                sb.Append(why).Append(' ').Append(phase).Append(' ').Append(part).Append(" n ").Append(l.Count)
                    .Append(" p50 ").Append(Ms(Pct(l, 50))).Append(" p95 ").Append(Ms(Pct(l, 95))).Append(" max ").Append(Ms(Pct(l, 100))).Append(" ms");
                return sb.ToString();
            }
        }
    }
}
