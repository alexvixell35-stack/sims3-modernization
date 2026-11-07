using System;
using System.Collections.Generic;

namespace Ts3Agent
{
    // docs/244 X4: fixed-work bench. The sim runs flat out, so a Mono interpreter saving shows up as ms per iteration, which
    // stage ms/s and it/s (+-15-20%) cannot resolve. This runs one fixed managed loop on the sim thread (the Mono VM the game's
    // script runs in) and times it, so profile-world --fixedwork can compare the time per --ab arm.
    // Pure: the loop touches only its own objects (no game state, no System.Random, no tick/RNG calls), and allocates almost
    // nothing (the data is built once; the per-round garbage is one boxed enumerator and the boxed DayOfWeek compare).
    // Same shape as monohost HostTest.MintBench (docs/139): List / Dictionary generics, interface enumerator, one monomorphic
    // and one two-type callvirt site, boxed Enum.Equals, GetType. Off by default: runs only when a harness job sends it.
    public static partial class Handlers
    {
        abstract class FwShape
        {
            public int W;
            public abstract int Area();
            public virtual int Id() { return 1; }
        }
        sealed class FwSq : FwShape { public override int Area() { return W * W; } }
        sealed class FwRect : FwShape
        {
            public int H;
            public override int Area() { return W * H; }
            public override int Id() { return 2; }
        }

        static Dictionary<int, FwShape> sFwMap;
        static List<FwShape> sFwList;
        static bool sFwWarm;

        static int FixedWorkLoop(int n)
        {
            if (sFwMap == null)
            {
                Dictionary<int, FwShape> map = new Dictionary<int, FwShape>();
                List<FwShape> list = new List<FwShape>();
                for (int i = 0; i < 256; i++)
                {
                    FwShape s;
                    if (i % 3 == 0) { FwRect r = new FwRect(); r.H = i + 1; s = r; } else s = new FwSq();
                    s.W = i;
                    map[i * 7] = s;
                    list.Add(s);
                }
                sFwList = list;
                sFwMap = map;
            }
            Dictionary<int, FwShape> m = sFwMap;
            List<FwShape> l = sFwList;
            int acc = 0;
            for (int r = 0; r < n; r++)
            {
                foreach (FwShape s in l) acc += s.Area() + s.Id();
                for (int i = 0; i < 256; i++) { FwShape s; if (m.TryGetValue(i * 7 + (r & 1), out s)) acc ^= s.W; }
                IEnumerable<FwShape> e = l;
                foreach (FwShape s in e) if (s is FwSq) acc++;
                if (((object)DayOfWeek.Monday).Equals(r % 7 == 0 ? DayOfWeek.Monday : DayOfWeek.Friday)) acc += 5;
                if (l[r & 255].GetType() == typeof(FwRect)) acc += 3;
            }
            return acc;
        }

        // fixed_work {ms?}: run FixedWorkLoop(5) blocks for at least ms (default 20) of wall time and report the time per 100
        // rounds (usPer100) and the checksum of one block (sum: identical in every call of every arm, the loop does not depend
        // on the game). The game's Mono has no Stopwatch and no DllImport, so the clock is DateTime.UtcNow: the run starts on a
        // clock edge and ends on one (the clock step adds no error beyond one block), and the call reports the step (resUs): over
        // 1 ms makes the number useless (the harness FAILs). warm = first call since the plugin loaded (builds the data and
        // warms the interpreter: drop it). totalUs = what the call cost the sim thread.
        static object FixedWork(Args a)
        {
            int ms = (int)a.Long("ms", 20);
            if (ms < 5 || ms > 200) throw new McpException("ms must be 5..200");
            bool warm = !sFwWarm;
            sFwWarm = true;
            int sum = FixedWorkLoop(5);
            long first = DateTime.UtcNow.Ticks, t = first;
            while (t == first) t = DateTime.UtcNow.Ticks; // first edge
            long t0 = t;
            while (t == t0) t = DateTime.UtcNow.Ticks;    // second edge: one clock step
            long step = t - t0;
            t0 = t;
            long end = t0 + ms * 10000L;
            int rounds = 0;
            while (t < end) { FixedWorkLoop(5); rounds += 5; t = DateTime.UtcNow.Ticks; }
            long last = t;
            while (t == last) { FixedWorkLoop(5); rounds += 5; t = DateTime.UtcNow.Ticks; }
            Dictionary<string, object> r = Util.Obj();
            r["usPer100"] = (long)((t - t0) / 10.0 * 100.0 / rounds);
            r["sum"] = sum;
            r["rounds"] = rounds;
            r["resUs"] = step / 10;
            r["warm"] = warm;
            r["totalUs"] = (t - first) / 10;
            return r;
        }

        static void RegisterFixedWork()
        {
            Dispatcher.Register("fixed_work", FixedWork);
        }
    }
}
