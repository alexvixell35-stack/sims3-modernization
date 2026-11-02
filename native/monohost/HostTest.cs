// Managed side of the monohost selftest (docs/119 §11): our own test code, compiled against the game's mscorlib and
// SimIFace by build.cmd (no EA code here). C# 2 only: Mono 1.2.3's interpreter, no generics or lambdas needed.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sims3.SimIFace;
using Ts3Fixes;

namespace MonoHostTest
{
    public class Node
    {
        public Node Next;
        public Node[] Kids;
        public int Value;
        public string Tag;
    }

    public class Finalizable
    {
        public static int Finalized;
        public int Id;
        ~Finalizable() { Finalized++; }
    }

    // docs/139 MintBench: two receiver types behind one abstract base (monomorphic and alternating callvirt sites)
    public abstract class Shape
    {
        public int W;
        public abstract int Area();
        public virtual int Id() { return 1; }
    }
    public class Sq : Shape { public override int Area() { return W * W; } }
    public class Rect : Shape
    {
        public int H;
        public override int Area() { return W * H; }
        public override int Id() { return 2; }
    }

    public static class HostTest
    {
        // ---- docs/139: interpreter workload for the mint dispatch A/B. The game's own mscorlib does the work
        // (List/Dictionary generics, struct and interface enumerators, boxed Enum.Equals, GetType), the shapes add
        // monomorphic and two-type callvirt sites. The result is a checksum: every dispatch variant must return the same.
        public static int MintBench(int n)
        {
            System.Collections.Generic.Dictionary<int, Shape> map = new System.Collections.Generic.Dictionary<int, Shape>();
            System.Collections.Generic.List<Shape> list = new System.Collections.Generic.List<Shape>();
            for (int i = 0; i < 256; i++)
            {
                Shape s;
                if (i % 3 == 0) { Rect r = new Rect(); r.H = i + 1; s = r; } else s = new Sq();
                s.W = i;
                map[i * 7] = s;
                list.Add(s);
            }
            int acc = 0;
            for (int r = 0; r < n; r++)
            {
                foreach (Shape s in list) acc += s.Area() + s.Id();
                for (int i = 0; i < 256; i++) { Shape s; if (map.TryGetValue(i * 7 + (r & 1), out s)) acc ^= s.W; }
                System.Collections.Generic.IEnumerable<Shape> e = list;
                foreach (Shape s in e) if (s is Sq) acc++;
                if (((object)DayOfWeek.Monday).Equals(r % 7 == 0 ? DayOfWeek.Monday : DayOfWeek.Friday)) acc += 5;
                if (list[r & 255].GetType() == typeof(Rect)) acc += 3;
            }
            return acc;
        }

        // ---- docs/207 P0: per-method loops for the method compiler (MONOHOST_JIT). Each case calls one compile candidate
        // (or a trivial twin pair for the switch cost) n times and returns a checksum that must not depend on the JIT.
        // JitId/JitOut/JitSum are on the compile list; their *Plain twins (same bodies) never are.
        public static int JitId(int x) { return x; }
        public static int JitPlain(int x) { return x; }
        public static int JitOut(int x) { return JitPlain(x) + 1; }
        public static int JitOutPlain(int x) { return JitPlain(x) + 1; }
        public static int JitSum(int[] a) { int s = 0; for (int i = 0; i < a.Length; i++) s += a[i] ^ i; return s; }
        public static int JitSumPlain(int[] a) { int s = 0; for (int i = 0; i < a.Length; i++) s += a[i] ^ i; return s; }
        // docs/207 §8 inlining: a compiled loop calling tiny instance methods (getter + arithmetic, Equals-style compare)
        public class IGet
        {
            public int V, W;
            public int Sum2(int x) { return V + x; }
            public bool Eq(int x) { return W == x; }
            public int Mix(int x, int y) { return (V ^ x) - y; }
        }
        static IGet s_ig = new IGet();
        public static int JitInl(IGet o, int n) { int acc = 0; for (int i = 0; i < n; i++) { acc += o.Sum2(i) + o.Mix(i, 3); if (o.Eq(i & 15)) acc++; } return acc; }
        public static int JitInlPlain(IGet o, int n) { int acc = 0; for (int i = 0; i < n; i++) { acc += o.Sum2(i) + o.Mix(i, 3); if (o.Eq(i & 15)) acc++; } return acc; }
        // docs/207 §12 lazy frames: a compiled loop with direct calls to compiled leaves (static call, non-virtual callvirt)
        public static int LzClamp(int x, int lo, int hi) { if (x < lo) return lo; if (x > hi) return hi; return x; }
        public class LzBox { public int V = 5; public int Get(int k) { if (k > 0) return V + k; return V - k; } }
        static LzBox s_lz = new LzBox();
        public static int JitLz(LzBox o, int n) { int acc = 0; for (int i = 0; i < n; i++) acc += LzClamp(i, 8, 40) + o.Get(i - 32); return acc; }
        public static int JitLzPlain(LzBox o, int n) { int acc = 0; for (int i = 0; i < n; i++) acc += LzClamp(i, 8, 40) + o.Get(i - 32); return acc; }
        // docs/207 §12.7 (v2): a lazy chain (LzMid makes the two lazy calls of JitLz) and a class-guarded callvirt of a virtual method
        public static int LzMid(LzBox o, int k) { int t = LzClamp(k, 8, 40); if (t > 1000) return t; return t + o.Get(k - 32); }
        public class LzShape { public virtual int Area(int k) { if (k > 0) return k; return -k; } }
        public class LzSq : LzShape { public int S = 3; public override int Area(int k) { if (k > 0) return S * k; return S - k; } }
        static LzShape s_sq = new LzSq();
        public static int JitLzChain(LzBox o, int n) { int acc = 0; for (int i = 0; i < n; i++) acc += LzMid(o, i); return acc; }
        public static int JitLzChainPlain(LzBox o, int n) { int acc = 0; for (int i = 0; i < n; i++) acc += LzMid(o, i); return acc; }
        public static int JitLzVirt(LzShape o, int n) { int acc = 0; for (int i = 0; i < n; i++) acc += o.Area(i - 32); return acc; }
        public static int JitLzVirtPlain(LzShape o, int n) { int acc = 0; for (int i = 0; i < n; i++) acc += o.Area(i - 32); return acc; }

        static System.Collections.Generic.Dictionary<int, Shape> s_map;
        static System.Collections.Generic.Dictionary<ulong, int> s_mapU;
        static System.Collections.Generic.Dictionary<ObjectGuid, int> s_mapG;
        static System.Collections.Generic.List<Shape> s_list;
        static int[] s_arr;
        static void JitSetup()
        {
            if (s_map != null) return;
            s_map = new System.Collections.Generic.Dictionary<int, Shape>();
            s_mapU = new System.Collections.Generic.Dictionary<ulong, int>();
            s_mapG = new System.Collections.Generic.Dictionary<ObjectGuid, int>();
            s_list = new System.Collections.Generic.List<Shape>();
            s_arr = new int[64];
            for (int i = 0; i < 256; i++)
            {
                Shape s;
                if (i % 3 == 0) { Rect r = new Rect(); r.H = i + 1; s = r; } else s = new Sq();
                s.W = i;
                s_map[i * 7] = s;
                s_mapU[(ulong)i * 0x100000001UL] = i;
                s_mapG[new ObjectGuid((ulong)i * 977UL)] = i;
                s_list.Add(s);
                if (i < 64) s_arr[i] = i * 3;
            }
        }

        // B4 differential corpus (JitCorpus.cs, generated by tools/jit-corpus.ts), driven by monohost with MONOHOST_JITCORPUS=1
        public static int JitCorpusCount() { return JitCorpus.Count; }
        public static int JitCorpusRun(int c) { return JitCorpus.Run(c); }
        public static string JitCorpusTrail(int c) { return JitCorpus.Trail(c); }

        public static int JitBench(int c, int n)
        {
            JitSetup();
            int acc = 0;
            Shape sh;
            int v;
            switch (c)
            {
                case 0: for (int r = 0; r < n; r++) if (s_map.TryGetValue((r & 255) * 7 + (r >> 8 & 1), out sh)) acc += sh.W; break;
                case 1: for (int r = 0; r < n; r++) if (s_mapU.TryGetValue((ulong)(r & 511) * 0x100000001UL, out v)) acc += v; break;
                case 2: for (int r = 0; r < n; r++) if (s_mapG.TryGetValue(new ObjectGuid((ulong)(r & 511) * 977UL), out v)) acc += v; break;
                case 3: for (int r = 0; r < n; r += 256) foreach (Shape s in s_list) acc += s.W; break;
                case 4: for (int r = 0; r < n; r++) acc += s_list[r & 255].W + s_list.Count; break;
                case 5:
                {
                    System.Collections.Generic.List<int> l = new System.Collections.Generic.List<int>();
                    for (int r = 0; r < n; r++) { if ((r & 127) == 0) l = new System.Collections.Generic.List<int>(); l.Add(r); acc += l.Count; }
                    break;
                }
                case 6: for (int r = 0; r < n; r++) { object d = (DayOfWeek)(r % 7); if (d.Equals(DayOfWeek.Monday)) acc += 5; acc += d.GetHashCode(); } break;
                case 7: for (int r = 0; r < n; r++) if (s_list[r & 255].GetType() == typeof(Rect)) acc += 3; break;
                case 8: { int[] a = new int[1]; for (int r = 0; r < n; r++) { if ((r & 63) == 0) a = new int[1]; Array.Resize(ref a, (r & 63) + 1); acc += a.Length; } break; }
                case 9: for (int r = 0; r < n; r++) acc += Array.IndexOf(s_arr, (r & 63) * 3); break;
                case 10: { ObjectGuid g0 = new ObjectGuid(5UL); for (int r = 0; r < n; r++) if (new ObjectGuid((ulong)(r & 7)) != g0) acc++; break; }
                case 11: for (int r = 0; r < n; r++) acc += (int)Sims3.SimIFace.CAS.CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags((Sims3.SimIFace.CAS.CASAgeGenderFlags)(1 << (r & 15))); break;
                case 12: for (int r = 0; r < n; r += 256) foreach (System.Collections.Generic.KeyValuePair<int, Shape> kv in s_map) acc += kv.Key; break;
                case 13: for (int r = 0; r < n; r++) { int k = (r & 255) * 7; if (s_map.ContainsKey(k)) { s_map[k] = s_list[r & 255]; acc++; } } break;
                case 14: for (int r = 0; r < n; r++) acc += JitId(r); break;
                case 15: for (int r = 0; r < n; r++) acc += JitPlain(r); break;
                case 16: for (int r = 0; r < n; r++) acc += JitOut(r); break;
                case 17: for (int r = 0; r < n; r++) acc += JitOutPlain(r); break;
                case 18: for (int r = 0; r < n; r += 64) acc += JitSum(s_arr); break;
                case 19: for (int r = 0; r < n; r += 64) acc += JitSumPlain(s_arr); break;
                case 20: for (int r = 0; r < n; r++) acc += r; break;  // loop alone (subtracted per call)
                case 21: for (int r = 0; r < n; r += 64) acc += JitInl(s_ig, 64); break;
                case 22: for (int r = 0; r < n; r += 64) acc += JitInlPlain(s_ig, 64); break;
                case 23: for (int r = 0; r < n; r += 64) acc += JitLz(s_lz, 64); break;
                case 24: for (int r = 0; r < n; r += 64) acc += JitLzPlain(s_lz, 64); break;
                case 25: for (int r = 0; r < n; r += 64) acc += JitLzChain(s_lz, 64); break;
                case 26: for (int r = 0; r < n; r += 64) acc += JitLzChainPlain(s_lz, 64); break;
                case 27: for (int r = 0; r < n; r += 64) acc += JitLzVirt(s_sq, 64); break;
                case 28: for (int r = 0; r < n; r += 64) acc += JitLzVirtPlain(s_sq, 64); break;
                default: return -1;
            }
            return acc;
        }

        // ---- M1.4: a graph kept alive only through a GC handle ----
        static Node Build(int n, int seed)
        {
            Node root = new Node();
            root.Kids = new Node[n];
            Node prev = null;
            for (int i = 0; i < n; i++)
            {
                Node k = new Node();
                k.Value = seed * 31 + i;
                k.Tag = new string((char)('a' + i % 26), i % 7 + 1);
                k.Kids = new Node[i % 4];
                for (int j = 0; j < k.Kids.Length; j++) { k.Kids[j] = new Node(); k.Kids[j].Value = k.Value ^ j; }
                k.Next = prev;
                prev = k;
                root.Kids[i] = k;
            }
            root.Next = prev;
            return root;
        }

        static int Sum(Node root, int n, int seed)
        {
            if (root == null || root.Kids == null || root.Kids.Length != n) return -1;
            int bad = 0, chain = 0;
            for (int i = 0; i < n; i++)
            {
                Node k = root.Kids[i];
                if (k == null || k.Value != seed * 31 + i || k.Tag == null || k.Tag.Length != i % 7 + 1 || k.Tag[0] != (char)('a' + i % 26)) { bad++; continue; }
                if (k.Kids.Length != i % 4) bad++;
                for (int j = 0; j < k.Kids.Length; j++) if (k.Kids[j] == null || k.Kids[j].Value != (k.Value ^ j)) bad++;
            }
            for (Node c = root.Next; c != null; c = c.Next) chain++;
            if (chain != n) bad++;
            return bad;
        }

        public static IntPtr BuildGraph(int n, int seed) { return GCHandle.ToIntPtr(GCHandle.Alloc(Build(n, seed))); }
        public static IntPtr BuildWeak(int n, int seed) { return GCHandle.ToIntPtr(GCHandle.Alloc(Build(n, seed), GCHandleType.Weak)); }
        public static int CheckGraph(IntPtr h, int n, int seed) { return Sum((Node)GCHandle.FromIntPtr(h).Target, n, seed); }
        public static bool IsAlive(IntPtr h) { return GCHandle.FromIntPtr(h).Target != null; }
        public static void Free(IntPtr h) { GCHandle.FromIntPtr(h).Free(); }

        // garbage only: nothing outlives the call
        public static int Churn(int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                Node x = new Node();
                x.Kids = new Node[8];
                x.Tag = new string('x', 16);
                s += x.Kids.Length + x.Tag.Length;
            }
            return s;
        }

        public static void MakeFinalizable(int n)
        {
            for (int i = 0; i < n; i++) { Finalizable f = new Finalizable(); f.Id = i; }
        }
        // docs/128 G-M4 / G-M5 under the game's Mono: resolve the generated closure list (mods/ts3fixes/src/AutShadowLists.cs)
        // in the loaded script assemblies, L3 each method, then the override census over non-game assemblies. monohost times it.
        public static int SweepFound, SweepForeign, SweepAsms, CensusTypes;
        static readonly string[] kGame = { "mscorlib", "System", "System.Xml", "ScriptCore", "SimIFace", "Sims3GameplaySystems",
            "Sims3GameplayObjects", "Sims3Metadata", "Sims3StoreObjects", "UI" };
        public static int GuardSweep()
        {
            System.Collections.Generic.List<System.Reflection.Assembly> game = new System.Collections.Generic.List<System.Reflection.Assembly>(), other = new System.Collections.Generic.List<System.Reflection.Assembly>();
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                (Array.IndexOf(kGame, a.GetName().Name) >= 0 ? game : other).Add(a);
            SweepAsms = game.Count;
            System.Collections.Generic.Dictionary<string, System.Reflection.MethodBase> got = Ts3Fixes.AutShadowCore.Resolve(game.ToArray(), Ts3Fixes.AutShadowLists.Methods);
            int foreign = 0;
            foreach (System.Reflection.MethodBase m in got.Values)
                if (Marshal.ReadIntPtr(new IntPtr(m.MethodHandle.Value.ToInt32() + 8)) != Marshal.ReadIntPtr(m.DeclaringType.TypeHandle.Value)) foreign++;
            SweepFound = got.Count; SweepForeign = foreign;
            System.Collections.Generic.List<Type> types = new System.Collections.Generic.List<Type>();
            foreach (System.Reflection.Assembly a in other) types.AddRange(a.GetTypes());
            bool[] hit = new bool[Ts3Fixes.AutShadowLists.Slots.Length];
            CensusTypes = Ts3Fixes.AutShadowCore.OverrideCensus(types.ToArray(), delegate(Type t) { return t != null && other.Contains(t.Assembly); },
                Ts3Fixes.AutShadowLists.Slots, hit, null);
            return got.Count;
        }
        public static int GetSweep(int what) { return what == 0 ? SweepFound : what == 1 ? SweepForeign : what == 2 ? SweepAsms : what == 3 ? CensusTypes : Ts3Fixes.AutShadowLists.Methods.Length; }
        public static int FinalizedCount() { return Finalizable.Finalized; }
        public static int Collections() { return GC.CollectionCount(0); }

        // ---- M1.5: a task that yields through Simulator.Sleep ----
        public static int Steps;
        public static ulong[] SeenTask = new ulong[32];
        public static int GetSteps() { return Steps; }
        public static ulong GetSeenTask(int i) { return SeenTask[i]; }

        // Sleeps i + 1 ticks after step i; checks that a local graph and the loop state survive each yield (and the host's
        // collections between ticks). Returns Steps, or a negative error code.
        public static int SleepLoop(int n)
        {
            Node local = Build(50, 7);
            int acc = 0;
            for (int i = 0; i < n; i++)
            {
                Steps++;
                SeenTask[i] = Simulator.CurrentTask.Value;
                acc += i;
                Simulator.Sleep((uint)(i + 1));
                if (Sum(local, 50, 7) != 0) return -100 - i;
                Churn(200);
            }
            if (acc != n * (n - 1) / 2) return -2;
            Steps += 1000;
            return Steps;
        }

        // a yield from a .cctor frame is refused by the interpreter (0xE53550): the task gets NotSupportedException
        public static int SleepInCctor() { return CctorSleeper.X; }
        public static int SleepInCctor2() { return CctorSleeper2.X; }  // M5: the same exception task after the arena fix is in

        // ---- M5: jobs for the second managed thread (pure managed work, no internal calls of the game) ----
        public static int LastWorker;  // a static is a root: the host reads the worker's result from here, not from the boxed return
        public static int GetLastWorker() { return LastWorker; }
        public static string LastError = "";  // the worker entry drops managed exceptions; the jobs record theirs here
        public static string GetLastError() { return LastError; }

        // A graph referenced only from this frame, i.e. the worker's stack, while both threads allocate and collect.
        public static int WorkerGraph(int rounds)
        {
            LastError = "";
            try { return WorkerGraphBody(rounds); }
            catch (Exception e) { LastError = e.GetType().FullName + ": " + e.Message + " @ " + e.StackTrace; LastWorker = -1000000; return LastWorker; }
        }
        static int WorkerGraphBody(int rounds)
        {
            Node mine = Build(400, 13);
            int bad = 0;
            for (int r = 0; r < rounds; r++)
            {
                Churn(300);
                if (Sum(mine, 400, 13) != 0) bad++;
                if ((r & 63) == 63) GC.Collect();  // the worker collects too: the main thread's stack then comes from P2
            }
            LastWorker = bad == 0 ? rounds : -bad;
            return LastWorker;
        }

        // Math.Min(float) asks Single.IsNaN through the interpreter's native-to-managed entry 0xD8A8E0 (docs/122 §12)
        public static int MinLoop(int n)
        {
            int bad = 0;
            for (int i = 0; i < n; i++)
            {
                float a = i, b = n - i;
                if (Math.Min(a, b) != (a < b ? a : b)) bad++;
            }
            return bad;
        }
        // F1 (docs/120 §9.4): host internal call that runs MinLoop(n) through ScriptHost_NativeToManaged 0xD8A8E0
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int N2mMinLoop(int n);
        public static int MainN2m(int n) { return N2mMinLoop(n); }
        public static int WorkerN2m(int n)
        {
            LastError = "";
            try { LastWorker = N2mMinLoop(n); }
            catch (Exception e) { LastError = e.GetType().FullName + ": " + e.Message + " @ " + e.StackTrace; LastWorker = -1000000; }
            return LastWorker;
        }
        public static int WorkerMin(int n)
        {
            LastError = "";
            try { LastWorker = MinLoop(n); }
            catch (Exception e) { LastError = e.GetType().FullName + ": " + e.Message + " @ " + e.StackTrace; LastWorker = -1000000; }
            return LastWorker;
        }
    }

    // ---- G-A2 (docs/122 §14.4): [AutShadow] part 2 on the M5 worker. The host registers ts3native's guard thunks for
    // these names; their bodies are host fakes with the game's IC layouts (Objects_GetPosition, World_GetLotLocationImpl,
    // Query_GetObjectsOnLot, World_GetSunriseTime, World_GetWorldNameKey, EAText_GetMoneyString, Random_NextDoubleImpl).
    public static class AsIc
    {
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern Vector3 Objects_GetPosition(ulong id);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern ulong World_GetLotLocationImpl(Vector3 worldPos, ref LotLocation location, int roomDefinition);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern Array Query_GetObjectsOnLot(Type type, ulong lotId, int roomId);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern float World_GetSunriseTime();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern string World_GetWorldNameKey();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern string EAText_GetMoneyString(long amount);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern double Random_NextDoubleImpl();
        // host helpers (not guarded): the lot's object array and the world name key the fake bodies answer with
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void HostSetLotObjects(object[] objs);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void HostSetNameKey(string key);
    }

    // Synthetic shadow search: per meta object one score from IC answers (row, position-keyed lot, per-lot list, world
    // constants, a reference answer, a stub string), every 7th object also draws (the worker aborts 'rng'). The
    // sim-thread reference and the worker run the same code on the same cut; Finish compares them bit for bit (A0-A5).
    public static class AsDriver
    {
        public const int kObjects = 50;
        static ulong[] sReq;               // the request: meta object ids (rooted static, as ts3fixes sReq)
        static float[] sRef = new float[kObjects], sWork = new float[kObjects];
        static int[] sRefCode = new int[kObjects], sWorkCode = new int[kObjects];
        static object[] sLot;              // the lot's objects (also held here: the arrays the worker gets hold these)
        public static int A0, A1, A2, A3, A4, A5, Runs, Finished, Abandoned, Rng, OtherAborts, MainBad, LastStatus = -1;
        public static string Error = "";
        public static int Get(int what)
        {
            switch (what)
            {
                case 0: return A0; case 1: return A1; case 2: return A2; case 3: return A3; case 4: return A4; case 5: return A5;
                case 6: return Runs; case 7: return Finished; case 8: return Rng; case 9: return OtherAborts; case 10: return MainBad;
                case 11: return LastStatus; case 12: return Abandoned; default: return Error.Length;
            }
        }
        public static string GetError() { return Error; }
        public static void ResetCounts() { A0 = A1 = A2 = A3 = A4 = A5 = Runs = Finished = Abandoned = Rng = OtherAborts = MainBad = 0; Error = ""; }

        public static int Setup()
        {
            sLot = new object[12];
            for (int i = 0; i < sLot.Length; i++) { Node n = new Node(); n.Value = i; sLot[i] = n; }
            AsIc.HostSetLotObjects(sLot);
            AsIc.HostSetNameKey(new string('k', 3) + "WorldNameKey");  // only the host's masked copy and later the snapshot hold it
            // warm-up on this ("sim") thread: every method and IC signature the worker runs (docs/30 §5 step 6)
            Finish(); Reference(); Work();
            float[] s = new float[kObjects]; int[] c = new int[kObjects];
            ulong[] ids = new ulong[kObjects];
            for (int i = 0; i < kObjects; i++) ids[i] = 0x500000000UL + (ulong)(i + 1);
            Score(ids, s, c);
            AutShadowNative.SetRefs(string.Empty, new NullReferenceException());
            AutShadowNative.SetMethods(typeof(AsDriver).GetMethod("Finish").MethodHandle.Value,
                typeof(AsDriver).GetMethod("Reference").MethodHandle.Value, typeof(AsDriver).GetMethod("Work").MethodHandle.Value);
            return AutShadowNative.Version();
        }

        public static int Request(int id)
        {
            if (!AutShadowNative.Begin()) return 0;
            ulong[] ids = new ulong[kObjects];
            for (int i = 0; i < kObjects; i++) { ids[i] = 0x500000000UL + (ulong)(i + 1); AutShadowNative.AddRow(ids[i], 0); }
            sReq = ids;
            AutShadowNative.Post(id);
            return 1;
        }

        // docs/206 §7 (Satb proof): Score folds SatbWorld.Score in; the main thread mutates SatbWorld while the worker runs
        static bool sSatb;
        public static int SatbSetup() { SatbWorld.Setup(); sSatb = true; SatbWorld.Score(1); SatbWorld.Mutate(1); SatbWorld.Score(2); return SatbWorld.N; }
        public static int MainMutate(int seed) { SatbWorld.Mutate(seed); return SatbWorld.Mutations; }
        public static int SatbScoreNow(int salt) { return SatbWorld.Score(salt); }
        // more first-written words than the log holds (65,534): the sim must stop the worker and wait before going on
        static int[] sBig;
        public static int MainBig(int seed) { if (sBig == null) sBig = new int[70000]; for (int i = 0; i < sBig.Length; i++) sBig[i] = seed + i; return sBig.Length; }

        static bool sInRef;
        public static void Reference() { if (sReq == null) return; sInRef = true; try { Score(sReq, sRef, sRefCode); } finally { sInRef = false; } }
        public static void Work()
        {
            if (sReq == null) return;
            try { Score(sReq, sWork, sWorkCode); }
            catch (Exception e) { Error = e.GetType().FullName + ": " + e.Message; throw; }
        }

        // code: 0 pass, 3 + reason abort, 2 not reached (stop)
        static void Score(ulong[] ids, float[] score, int[] code)
        {
            for (int i = 0; i < ids.Length; i++) { score[i] = 0; code[i] = 2; }
            for (int i = 0; i < ids.Length; i++)
            {
                if (AutShadowNative.Stopped()) return;
                AutShadowNative.RngUnit(i);  // docs/125: each object draws from its own stream
                AutShadowNative.ResetAbort();
                float sc = 0;
                try
                {
                    Vector3 p = AsIc.Objects_GetPosition(ids[i]);
                    LotLocation loc = new LotLocation();
                    ulong lot = AsIc.World_GetLotLocationImpl(p, ref loc, 0);
                    Array a = AsIc.Query_GetObjectsOnLot(typeof(Node), lot, 0);
                    int sum = 0;
                    if (a != null) for (int j = 0; j < a.Length; j++) { Node n = a.GetValue(j) as Node; sum += n == null ? 1000 : n.Value; }
                    string key = AsIc.World_GetWorldNameKey();
                    string money = AsIc.EAText_GetMoneyString(i);
                    sc = p.x * 0.5f + p.z * 0.25f + AsIc.World_GetSunriseTime() + loc.mX + loc.mZ * 2 + loc.mRoom + (float)(lot & 0xFFFF)
                        + (a == null ? -1 : a.Length) + sum + (key == null ? -7 : key.Length * 3 + key[0]) + (money == null ? -3 : money.Length);
                    if (sSatb) sc += (float)(SatbWorld.Score(i) & 0x7FFF);
                    Node garbage = new Node(); garbage.Kids = new Node[3]; garbage.Tag = "g" + i;  // allocation on both threads
                    // docs/125 addendum §4.1: a reference-only draw (a hooked call) in a bracket leaves the unit stream alone
                    if (sInRef && i % 7 == 0) { AutShadowNative.RngPause(1); AsIc.Random_NextDoubleImpl(); AutShadowNative.RngPause(0); }
                    if (i % 7 == 0) sc += (float)AsIc.Random_NextDoubleImpl();
                }
                catch (Exception) { code[i] = 3 + 12; score[i] = 0; AutShadowNative.ResetAbort(); continue; }
                int ab = AutShadowNative.Aborted();
                code[i] = ab >= 0 ? 3 + ab : 0;
                score[i] = sc;
            }
        }

        public static void Finish()
        {
            if (sReq == null) return;
            LastStatus = AutShadowNative.LastRun();
            Finished++;
            if (LastStatus == 0)
            {
                Runs++;
                for (int i = 0; i < sReq.Length; i++)
                {
                    int w = sWorkCode[i], r = sRefCode[i];
                    if (w >= 3) { A4++; if (w == 3 + 3) Rng++; else OtherAborts++; continue; }
                    if (w == 2) { Abandoned++; continue; }
                    if (w != r) { A3++; continue; }
                    int bw = BitConverter.ToInt32(BitConverter.GetBytes(sWork[i]), 0), br = BitConverter.ToInt32(BitConverter.GetBytes(sRef[i]), 0);
                    if (bw == br) A0++;
                    else if (bw - br == 1 || br - bw == 1) A1++;
                    else { A2++; if (Error.Length == 0) Error = "A2 at " + i + ": ref " + sRef[i] + " work " + sWork[i]; }
                }
            }
            else A5++;
            sReq = null;
        }

        // the main ("sim") thread's own passes during the window: the same ICs pass through to the host bodies
        public static int MainPass()
        {
            float[] s = new float[kObjects]; int[] c = new int[kObjects];
            ulong[] ids = new ulong[kObjects];
            for (int i = 0; i < kObjects; i++) ids[i] = 0x500000000UL + (ulong)(i + 1);
            Score(ids, s, c);
            int bad = 0;
            for (int i = 0; i < kObjects; i++) if (c[i] != 0 || (i % 7 != 0 && s[i] != sRef[i])) bad++;  // i % 7: live RNG draws
            MainBad += bad;
            return bad;
        }
    }

    // docs/206 §7 (Satb offline proof): a managed world the worker's score reads with every heap load kind, and a mutator
    // the main ("sim") thread runs between the cut and the worker's end with every store kind and the runtime writers
    // (Array.Copy through List<T>, Array.Clear, Buffer.BlockCopy, Dictionary). With Satb the worker must score the cut.
    public struct SVec { public float x, y, z; public int tag; }
    public class SObj
    {
        public sbyte I1; public byte U1; public short I2; public ushort U2; public int I4; public long I8; public float R4; public double R8;
        public object O; public SVec V; public SObj Next; public string S; public bool B; public char C;
    }
    public class SRemote : MarshalByRefObject { public int X; public SVec V; }
    // docs/206 §10: written by Marshal.PtrToStructure(ptr, object) on the main thread (0.36.136 logs its fields)
    [StructLayout(LayoutKind.Sequential)] public class SPod { public int A; public double B; public short C; }
    public static class SatbWorld
    {
        public const int N = 64;
        public static SObj[] sObjs;
        public static int sI4; public static long sI8; public static double sR8; public static float sR4; public static object sObj;
        public static SVec sVec; public static string sStr; public static short sI2; public static byte sU1;
        public static int[] sInts; public static long[] sLongs; public static byte[] sBytes; public static short[] sShorts;
        public static float[] sFloats; public static double[] sDoubles; public static object[] sRefs; public static SVec[] sVecs;
        public static sbyte[] sSbytes; public static ushort[] sUshorts; public static char[] sChars;
        public static System.Collections.Generic.List<SObj> sList;
        public static System.Collections.Generic.Dictionary<int, int> sDict;
        public static SRemote sRemote;
        public static int Mutations;
        // docs/206 §10: reflection writes (MonoField.SetValueInternal: instance and static, int / long / struct / enum) and
        // PtrToStructure into an existing object, both on the main thread while the worker scores
        public enum SKind : byte { A = 1, B = 2, C = 200 }
        public static int sReflI4; public static SVec sReflV; public static SKind sReflE;
        public static SPod sPod;
        static IntPtr sPodMem;
        static System.Reflection.FieldInfo fI8, fV, fE, fsI4, fsV, fsE, fNext;

        public static void Setup()
        {
            sObjs = new SObj[N];
            for (int i = 0; i < N; i++) { SObj o = new SObj(); Fill(o, i); sObjs[i] = o; }
            for (int i = 0; i < N; i++) sObjs[i].Next = sObjs[(i + 1) % N];
            sInts = new int[N]; sLongs = new long[N]; sBytes = new byte[N * 4]; sShorts = new short[N]; sFloats = new float[N];
            sDoubles = new double[N]; sRefs = new object[N]; sVecs = new SVec[N]; sSbytes = new sbyte[N]; sUshorts = new ushort[N]; sChars = new char[N];
            for (int i = 0; i < N; i++)
            {
                sInts[i] = i * 3; sLongs[i] = (long)i << 33; sShorts[i] = (short)(i - 30); sFloats[i] = i * 0.5f; sDoubles[i] = i * 0.25;
                sRefs[i] = sObjs[i]; sVecs[i].x = i; sVecs[i].z = -i; sVecs[i].tag = i; sSbytes[i] = (sbyte)(-i); sUshorts[i] = (ushort)(i * 1000); sChars[i] = (char)('a' + i % 26);
            }
            for (int i = 0; i < sBytes.Length; i++) sBytes[i] = (byte)i;
            sList = new System.Collections.Generic.List<SObj>();
            for (int i = 0; i < 20; i++) sList.Add(sObjs[i]);
            sDict = new System.Collections.Generic.Dictionary<int, int>();
            for (int i = 0; i < 40; i++) sDict[i] = i * 7;
            sRemote = new SRemote(); sRemote.X = 11; sRemote.V.y = 2.5f;
            sI4 = 1; sI8 = 2; sR8 = 3.5; sR4 = 4.5f; sObj = sObjs[1]; sVec.y = 7; sStr = "abc"; sI2 = -5; sU1 = 200;
            sReflI4 = 3; sReflV.z = 9; sReflE = SKind.B;
            sPod = new SPod(); sPod.A = 1; sPod.B = 2.5; sPod.C = 3;
            sPodMem = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SPod)));
            Marshal.StructureToPtr(sPod, sPodMem, false);
            const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            fI8 = typeof(SObj).GetField("I8", bf); fV = typeof(SObj).GetField("V", bf); fE = typeof(SatbWorld).GetField("sReflE", bf);
            fsI4 = typeof(SatbWorld).GetField("sReflI4", bf); fsV = typeof(SatbWorld).GetField("sReflV", bf); fsE = fE;
            fNext = typeof(SObj).GetField("Next", bf);
        }
        static void Fill(SObj o, int i)
        {
            o.I1 = (sbyte)(-i); o.U1 = (byte)(i + 100); o.I2 = (short)(-i * 100); o.U2 = (ushort)(i * 300); o.I4 = i * 11; o.I8 = (long)i * 0x100000001L;
            o.R4 = i * 1.25f; o.R8 = i * 2.5; o.O = i % 3 == 0 ? (object)("s" + i) : null; o.V.x = i; o.V.y = i * 2; o.V.z = i * 3; o.V.tag = i; o.S = "t" + i;
            o.B = (i & 1) != 0; o.C = (char)('A' + i % 26);
        }

        static int H(int h, int v) { return (h ^ v) * 16777619; }
        static int H(int h, long v) { return H(H(h, (int)v), (int)(v >> 32)); }
        static int H(int h, double v) { return H(h, BitConverter.DoubleToInt64Bits(v)); }
        // big locals: each frame takes ~0.5 KB of the arena, so a 50-deep path needs several chunks
        public struct SBig { public double a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, b0, b1, b2, b3, b4, b5, b6, b7, b8, b9, c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, d0, d1; }
        static int Deep(int d, int x)
        {
            SBig p = new SBig(), q = new SBig();
            p.a0 = x; p.d1 = d; q = p; q.b5 = p.a0 + p.d1;
            if (d <= 0) return x;
            return Deep(d - 1, (int)q.b5 + x) + 1;
        }
        static int ReadRefI4(ref int x) { return x; }
        static long ReadRefI8(ref long x) { return x; }
        static double ReadRefR8(ref double x) { return x; }
        static float ReadRefR4(ref float x) { return x; }
        static short ReadRefI2(ref short x) { return x; }
        static ushort ReadRefU2(ref ushort x) { return x; }
        static sbyte ReadRefI1(ref sbyte x) { return x; }
        static byte ReadRefU1(ref byte x) { return x; }
        static object ReadRefO(ref object x) { return x; }
        static SVec ReadRefV(ref SVec v) { SVec c = v; return c; }
        static int VecH(int h, SVec v) { return H(H(H(H(h, (double)v.x), (double)v.y), (double)v.z), v.tag); }

        // every heap load kind: ldfld.*, ldsfld*, ldelem.*, ldelem.any.vt, ldelema + ldind.*, ldobj, ldrmfld; collections
        public static int Score(int salt)
        {
            int h = salt;
            for (int k = 0; k < 8; k++)
            {
                SObj o = sObjs[(salt * 7 + k * 5) % N];
                h = H(h, o.I1); h = H(h, o.U1); h = H(h, o.I2); h = H(h, o.U2); h = H(h, o.I4); h = H(h, o.I8); h = H(h, (double)o.R4); h = H(h, o.R8);
                h = H(h, o.O == null ? 0 : o.O.GetHashCode()); h = VecH(h, o.V); h = H(h, o.V.tag); h = H(h, o.Next.I4); h = H(h, o.S == null ? 1 : o.S.Length);
                h = H(h, o.B ? 3 : 4); h = H(h, o.C);
                h = H(h, ReadRefI4(ref o.I4)); h = H(h, ReadRefI8(ref o.I8)); h = H(h, ReadRefR8(ref o.R8)); h = H(h, (double)ReadRefR4(ref o.R4));
                h = H(h, ReadRefI2(ref o.I2)); h = H(h, ReadRefU2(ref o.U2)); h = H(h, ReadRefI1(ref o.I1)); h = H(h, ReadRefU1(ref o.U1));
                h = H(h, ReadRefO(ref o.O) == null ? 5 : 6); h = VecH(h, ReadRefV(ref o.V));
            }
            int i = salt % N;
            h = H(h, sI4); h = H(h, sI8); h = H(h, sR8); h = H(h, (double)sR4); h = H(h, sObj == null ? 0 : ((SObj)sObj).I4); h = VecH(h, sVec);
            h = H(h, sStr.Length); h = H(h, sI2); h = H(h, sU1);
            h = H(h, ReadRefI4(ref sI4)); h = VecH(h, ReadRefV(ref sVec));
            h = H(h, sInts[i]); h = H(h, sLongs[i]); h = H(h, sBytes[i * 3]); h = H(h, sShorts[i]); h = H(h, (double)sFloats[i]); h = H(h, sDoubles[i]);
            h = H(h, sRefs[i] == null ? 0 : ((SObj)sRefs[i]).I2); h = VecH(h, sVecs[i]); h = H(h, sSbytes[i]); h = H(h, sUshorts[i]); h = H(h, sChars[i]);
            h = H(h, ReadRefI4(ref sInts[(i + 1) % N])); h = VecH(h, ReadRefV(ref sVecs[(i + 2) % N])); h = H(h, ReadRefI8(ref sLongs[(i + 3) % N]));
            h = H(h, sList.Count);
            for (int j = 0; j < sList.Count; j++) h = H(h, sList[j].I4);
            int dv;
            h = H(h, sDict.Count); if (sDict.TryGetValue(salt % 50, out dv)) h = H(h, dv);
            h = H(h, sRemote.X); h = H(h, (double)sRemote.V.y);
            h = H(h, sReflI4); h = VecH(h, sReflV); h = H(h, (int)sReflE); h = H(h, sPod.A); h = H(h, sPod.B); h = H(h, sPod.C);
            // docs/206 §12: reflection reads on the worker (MonoField.GetValue: instance long / struct / reference, static int / enum)
            SObj ro = sObjs[(salt + 10) % N];
            h = H(h, (long)fI8.GetValue(ro)); h = VecH(h, (SVec)fV.GetValue(sObjs[(salt + 11) % N])); h = H(h, ((SObj)fNext.GetValue(ro)).I4);
            h = H(h, (int)fsI4.GetValue(null)); h = H(h, (int)(SKind)fsE.GetValue(null));
            // docs/206 §13: deep call paths on the worker grow the frame arena again and again (same pool chunks)
            for (int r = 0; r < 2; r++) h = H(h, Deep(50, salt + r));  // the interpreter's depth limit is 63 (docs/130 §1)
            // worker-side native copies of shared arrays (Array.Copy through List<T>(IEnumerable) / CopyTo, Clone, BlockCopy)
            System.Collections.Generic.List<SObj> copy = new System.Collections.Generic.List<SObj>(sList);
            h = H(h, copy.Count); if (copy.Count > 0) h = H(h, copy[salt % copy.Count].I2);
            int[] ints = (int[])sInts.Clone(); h = H(h, ints[(salt + 5) % N]);
            double[] dbl = new double[4]; Array.Copy(sDoubles, salt % (N - 4), dbl, 0, 4); h = H(h, dbl[1]);
            byte[] bytes = new byte[16]; Buffer.BlockCopy(sLongs, 8 * (salt % (N - 2)), bytes, 0, 16); h = H(h, bytes[3] + bytes[11] * 256);
            return h;
        }

        // every store kind on shared objects, statics and arrays, plus the runtime writers behind List/Dictionary/Array
        public static void Mutate(int seed)
        {
            Mutations++;
            int i = seed % N;
            SObj o = sObjs[i];
            o.I1 = (sbyte)seed; o.U1 = (byte)seed; o.I2 = (short)seed; o.U2 = (ushort)seed; o.I4 = seed; o.I8 = (long)seed << 20; o.R4 = seed; o.R8 = seed * 0.5;
            o.O = (seed & 1) != 0 ? null : (object)o; o.V.x = seed; o.V = new SVec(); o.V.tag = seed; o.S = "m" + seed; o.B = !o.B; o.C = (char)seed;
            SObj fresh = new SObj(); Fill(fresh, seed % 97); o.Next = fresh; fresh.Next = sObjs[(i + 1) % N];
            WriteRefI4(ref sObjs[(i + 3) % N].I4, seed); WriteRefV(ref sObjs[(i + 4) % N].V, seed); WriteRefI8(ref sObjs[(i + 5) % N].I8, seed);
            WriteRefR8(ref sObjs[(i + 6) % N].R8, seed); WriteRefI2(ref sObjs[(i + 7) % N].I2, seed); WriteRefU1(ref sObjs[(i + 8) % N].U1, seed);
            sI4 = seed; sI8 = seed * 3L; sR8 = seed * 0.75; sR4 = seed; sObj = o; sVec.y = seed; sVec = new SVec(); sStr = "x" + seed; sI2 = (short)seed; sU1 = (byte)seed;
            WriteRefI4(ref sI4, seed + 1); WriteRefV(ref sVec, seed);
            sInts[i] = seed; sLongs[i] = seed; sBytes[(i * 3) % sBytes.Length] = (byte)seed; sShorts[i] = (short)seed; sFloats[i] = seed; sDoubles[i] = seed;
            sRefs[i] = fresh; sVecs[i] = new SVec(); sVecs[(i + 1) % N].tag = seed; sSbytes[i] = (sbyte)seed; sUshorts[i] = (ushort)seed; sChars[i] = (char)seed;
            WriteRefI4(ref sInts[(i + 1) % N], seed); WriteRefV(ref sVecs[(i + 2) % N], seed); WriteRefI8(ref sLongs[(i + 3) % N], seed);
            if ((seed & 3) == 0 && sList.Count > 4) sList.RemoveAt(seed % sList.Count); else sList.Insert(seed % (sList.Count + 1), o);
            if (sList.Count > 40) sList.RemoveRange(0, 10);
            sDict[seed % 50] = seed; if ((seed & 7) == 0) sDict.Remove((seed / 8) % 50);
            if ((seed & 15) == 0) Array.Clear(sInts, seed % (N - 8), 4);
            if ((seed & 15) == 5) Buffer.BlockCopy(sLongs, 8, sBytes, 0, 16);
            if ((seed & 15) == 9) Array.Copy(sDoubles, 1, sDoubles, 0, N - 1);
            sRemote.X = seed; sRemote.V.y = seed;
            // reflection (instance long and struct, static int / struct / enum) and PtrToStructure into the shared SPod
            SVec rv = new SVec(); rv.y = seed; rv.tag = seed + 1;
            fI8.SetValue(sObjs[(i + 10) % N], (long)seed * 7); fV.SetValue(sObjs[(i + 11) % N], rv);
            fsI4.SetValue(null, seed); fsV.SetValue(null, rv); fsE.SetValue(null, (SKind)(seed & 0xFF));
            Marshal.WriteInt32(sPodMem, 0, seed); Marshal.WriteInt64(sPodMem, 8, BitConverter.DoubleToInt64Bits(seed * 0.5)); Marshal.WriteInt16(sPodMem, 16, (short)seed);
            Marshal.PtrToStructure(sPodMem, sPod);
            o.I4 += sObjs[(i + 9) % N].I4;  // a read on the main thread after its own writes
        }
        static void WriteRefI4(ref int x, int v) { x = v; }
        static void WriteRefI8(ref long x, int v) { x = v; }
        static void WriteRefR8(ref double x, int v) { x = v; }
        static void WriteRefI2(ref short x, int v) { x = (short)v; }
        static void WriteRefU1(ref byte x, int v) { x = (byte)v; }
        static void WriteRefV(ref SVec x, int v) { SVec n = new SVec(); n.x = v; n.tag = -v; x = n; }
    }

    public class CctorSleeper
    {
        public static int X;
        static CctorSleeper() { Simulator.Sleep(1); X = 5; }
    }

    // ---- docs/139 §8: call/return fast path (MONOHOST_MINTCALL). Same results in every mode; CallBench times one call kind.
    public class CBase
    {
        public int V;
        public virtual int Get() { return V; }
        public int Plain() { return V; }
        public virtual long Wide(long x) { return x * 3 + V; }
    }
    public class CDer : CBase
    {
        public override int Get() { return V + 1; }
        public override long Wide(long x) { return x - V; }
    }
    public interface ICnt { int Next(int x); }
    public struct CVal : ICnt
    {
        public int A;
        public int Next(int x) { A += x; return A; }
        public override string ToString() { return "v" + A; }
    }
    public class CObj : ICnt
    {
        public int B;
        public CObj(int b) { B = Twice(b); }
        static int Twice(int b) { return b + b; }
        public int Next(int x) { B ^= x; return B; }
    }
    public struct CBig { public long P, Q; public double R; }
    public static class CallTest
    {
        public static int Depth;
        static int Id(int x) { return x + 1; }
        static void Nop() { }
        static long L(long a, long b) { return a * 31 + b; }
        static double D(double a) { return a * 0.5 + 1.0; }
        static CBig Big(int i) { CBig b; b.P = i; b.Q = -i; b.R = i * 0.25; return b; }
        static int Rec(int n) { Depth++; return n <= 0 ? 0 : 1 + Rec(n - 1); }
        static int Thrower(int n, int at)
        {
            try { if (n == at) throw new InvalidOperationException("at " + n); return Thrower(n + 1, at) + 1; }
            finally { Depth += n; }
        }
        static int Catcher(int at)
        {
            try { return Thrower(0, at); }
            catch (InvalidOperationException e) { return e.Message.Length * 1000 + (e.StackTrace == null ? 0 : e.StackTrace.Length); }
        }
        static int NullCall(CBase o) { try { return o.Get(); } catch (NullReferenceException) { return -7; } }
        static int Overflow()
        {
            Depth = 0;
            try { return Rec(10000); } catch (Exception e) { return -1000 - e.GetType().Name.Length; }
        }

        // every call shape the fast path takes or bails on; returns a checksum (compare across MONOHOST_MINTCALL modes)
        public static int Run(int n)
        {
            int acc = 0;
            CBase[] objs = { new CBase(), new CDer(), new CBase(), new CDer() };
            for (int i = 0; i < objs.Length; i++) objs[i].V = i * 7;
            for (int r = 0; r < n; r++)
            {
                acc += Id(r);
                Nop();
                acc ^= (int)L(r, acc);
                acc += (int)D(r);
                CBig b = Big(r);
                acc += (int)(b.P - b.Q) + (int)b.R;
                CBase o = objs[r & 3];
                acc += o.Get() + o.Plain() + (int)o.Wide(r);  // the r & 3 site alternates types: cache misses
                acc += objs[1].Get();                         // monomorphic site
                CVal v = new CVal();
                acc += v.Next(r) + ((ICnt)v).Next(1) + v.ToString().Length;  // value-type receiver, boxed interface call
                ICnt c = new CObj(r);
                acc += c.Next(5);
                acc += Rec(r % 40);
                acc ^= Catcher(r % 9);
                acc += NullCall((r & 7) == 0 ? null : o);
            }
            acc += Overflow() + Depth;
            return acc;
        }

        public static int CallBench(int n, int kind)
        {
            CBase o = new CDer();
            o.V = 3;
            int acc = 0;
            if (kind == 0) for (int i = 0; i < n; i++) acc += i;
            else if (kind == 1) for (int i = 0; i < n; i++) acc += Id(i);
            else if (kind == 2) for (int i = 0; i < n; i++) { Nop(); acc += i; }
            else if (kind == 3) for (int i = 0; i < n; i++) acc += o.Get();
            else if (kind == 4) for (int i = 0; i < n; i++) acc += o.Plain();
            return acc;
        }

        // M1.5-style task: yields 5 frames deep, the frames between must come back intact after each resume
        static int Deep(int d, int i)
        {
            if (d == 0) { Simulator.Sleep((uint)(i + 1)); return i; }
            int x = d * 10;
            int r = Deep(d - 1, i);
            return r + x;
        }
        public static int SleepDeep(int n)
        {
            int acc = 0;
            for (int i = 0; i < n; i++) acc += Deep(5, i) + Id(i);
            return acc;  // sum(i + 150 + i + 1)
        }
    }

    public class CctorSleeper2
    {
        public static int X;
        static CctorSleeper2() { Simulator.Sleep(1); X = 6; }
    }
}

namespace Ts3Fixes
{
    // ts3native's [AutShadow] internal calls (registered at the first Simulate tick; docs/122 §14.2): the same contract as
    // mods/ts3fixes/src/AutShadow.cs, declared here so the host test runs the native side unchanged.
    public static class AutShadowNative
    {
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Version();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Config(int key);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern bool IsWorker();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Abort(int reason);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Aborted();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void ResetAbort();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern bool Stopped();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern bool Begin();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void AddRow(ulong objectId, int kind);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Post(int requestId);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int LastRun();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void SetMethods(IntPtr finish, IntPtr reference, IntPtr work);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void SetRefs(string empty, Exception fmtException);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Log(string jsonLine);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void RngUnit(int unit);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void RngPause(int on);
    }
}
