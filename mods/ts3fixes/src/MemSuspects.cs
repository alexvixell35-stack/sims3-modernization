using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // docs/191 module 2 (ts3native [MemDiag] 0.36.74): managed leak-suspect counts for each "diag: mem" sample, read on the
    // sim thread and sent as one "memsus: k=v ..." line over the [UserDiag] text channel (UserDiag.cs DiagLine); ts3native
    // logs it as "diag: memsus #N <tag> ..." with the number of the diag: mem sample it answers. Counts only, no names.
    //   cc      UIManager.mCustomControlInstanceDict.Count (docs/193 L1: wrapped custom controls, removed only on dispose)
    //   ccIcon  ... of which CustomContentIcon (the catalogue badge icons)
    //   ppc     Route.PostPlanCallback invocation list (docs/192 #4: NRaas Overwatch StuckCheck adds one per load)
    //   etRef   ScriptCore.ExceptionTrap.OnLoadReference list (docs/192 #5: NRaas ErrorTrap's own ScriptCore; '-' without it)
    //   gsc     Responder.Instance GameStateChanging list (docs/192 #1: IPlaceableObject_Place adds, never removes)
    //   mapv    CameraController.OnCameraMapViewEnabledCallback list (docs/192 #1)
    //   wq wpl  World world-quit / object-placed-in-lot lists (docs/192 #2 store objects)
    //   ogs     LoadSaveManager ObjectGroupSaving list (docs/192 #2 WalkerClassic)
    //   smh     GameEntryMovingModel.SplitMergeHouseholdsComplete list (docs/192 #3 UniqueObjectSpawner)
    //   gcCnt   GC.CollectionCount(0) (managed view of ts3native's gcN; mem-report cross-checks the two at marks)
    //   rdMs    time this read took (ms)
    // Validation: with [MemDiag] SelfTest=1 ts3native asks (op 5) once per session for MemSusSelfTest: each counter must move
    // by exactly +1 when a known entry is added through the same field and back when the original is put back (a delegate
    // list gets its own first entry appended, then the original delegate object is restored; the dictionary gets one entry
    // under an unused key, then it is removed). Synchronous on the sim thread, no yield in between, so nothing else sees the
    // extra entry. A counter that passed has no suffix, '?' = not validated this session (field empty or self-test not run),
    // '!' = failed. Reads never write; the self-test writes and restores.
    public static partial class Fixes
    {
        static readonly string[] kSusKeys = { "cc", "ccIcon", "ppc", "etRef", "gsc", "mapv", "wq", "wpl", "ogs", "smh" };
        static readonly Dictionary<string, char> sSusMark = new Dictionary<string, char>();  // key -> 'v' passed, '!' failed
        static int sSusGen;
        static bool sSusSelfTestDone;
        const BindingFlags kSusStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags kSusInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Called from Instantiator's world-load handler after DiagWorldLoad: with [MemDiag] on, one task per load polls
        // ts3native every 10 ticks (one GetOption2 call) and answers the wanted sample.
        public static void MemSusWorldLoad()
        {
            if (!DiagSend(3, 0)) return;  // no ts3native, [UserDiag] or [MemDiag] off: nothing to answer, no task
            sAliveOn = true;
            sTypesDue = true;
            int gen = ++sSusGen;
            Simulator.AddObject(new LoopTask(delegate
            {
                while (gen == sSusGen)
                {
                    try { MemSusPoll(); } catch (Exception) { }
                    Simulator.Sleep(10);
                }
            }));
        }

        // Answers a wanted sample now (also called by ts3agent's mem_mark / mem_gc right after the mark, so a mark's counts are
        // read before the agent's next step). Returns true when a line was sent.
        public static bool MemSusPoll()
        {
            if (!sSusSelfTestDone && DiagSend(5, 0)) { sSusSelfTestDone = true; MemSusSelfTest(); }
            if (sSusRetry.Count > 0) MemSusRetry();
            // ts3native 0.36.76 queues wanted samples (oldest first): answer all of them now, so a mark's own line is not
            // left to the next poll behind an older one (8 = native queue size; older ts3native answers one and stops)
            bool sent = false;
            for (int i = 0; i < 8 && DiagSend(4, 0); i++) sent |= DiagLine("memsus: " + MemSusLine());
            if (sent) { try { DiagLine(AliveLine()); } catch (Exception) { } }
            if (sent && sTypesDue) { sTypesDue = false; try { DiagLine(TypesLine()); } catch (Exception) { } }
            return sent;
        }

        static string MemSusLine()
        {
            StopWatch w = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            w.Start();
            StringBuilder b = new StringBuilder();
            foreach (string k in kSusKeys)
            {
                int n = SusCount(k);
                char m;
                b.Append(b.Length > 0 ? " " : "").Append(k).Append('=').Append(n < 0 ? "-" : n.ToString());
                if (n >= 0) b.Append(!sSusMark.TryGetValue(k, out m) ? "?" : m == '!' ? "!" : "");
            }
            b.Append(" gcCnt=").Append(GC.CollectionCount(0));
            w.Stop();
            b.Append(" rdMs=").Append((w.GetElapsedTime() / 1000.0).ToString("0.00"));
            w.Dispose();
            return b.ToString();
        }

        // -1 = not readable here (mod absent, singleton not created, field renamed)
        static int SusCount(string key)
        {
            try
            {
                if (key == "cc" || key == "ccIcon")
                {
                    Dictionary<uint, WindowBase> d = SusDict();
                    if (d == null) return -1;
                    if (key == "cc") return d.Count;
                    int icons = 0;
                    foreach (WindowBase v in d.Values) if (v != null && v.GetType().Name == "CustomContentIcon") icons++;
                    return icons;
                }
                object target;
                FieldInfo f = SusField(key, out target);
                if (f == null) return -1;
                Delegate x = f.GetValue(target) as Delegate;
                return x == null ? 0 : x.GetInvocationList().Length;
            }
            catch (Exception) { return -1; }
        }

        static Dictionary<uint, WindowBase> SusDict()
        {
            FieldInfo f = typeof(UIManager).GetField("mCustomControlInstanceDict", kSusStatic);
            return f == null ? null : f.GetValue(null) as Dictionary<uint, WindowBase>;
        }

        static FieldInfo FieldUp(Type t, string name, BindingFlags b)  // a private backing field may sit in a base class
        {
            for (; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, b | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        // The delegate field behind a counter, and its owner object (null for a static field).
        static FieldInfo SusField(string key, out object target)
        {
            target = null;
            switch (key)
            {
                case "ppc": return typeof(Sims3.SimIFace.Route).GetField("PostPlanCallback", kSusStatic);
                case "etRef":
                    {
                        Type t = Type.GetType("ScriptCore.ExceptionTrap, ScriptCore");  // only NRaas ErrorTrap's ScriptCore has it
                        return t == null ? null : t.GetField("OnLoadReference", kSusStatic);
                    }
                case "gsc":
                    target = Sims3.UI.Responder.Instance;
                    return target == null ? null : FieldUp(target.GetType(), "GameStateChanging", kSusInst);
                case "mapv": return typeof(CameraController).GetField("OnCameraMapViewEnabledCallback", kSusStatic);
                case "wq": return typeof(World).GetField("sOnWorldQuitEventHandler", kSusStatic);
                case "wpl": return typeof(World).GetField("sOnObjectPlacedInLotEventHandler", kSusStatic);
                case "ogs":
                    {
                        FieldInfo g = typeof(LoadSaveManager).GetField("gLoadSaveManager", kSusStatic);
                        target = g == null ? null : g.GetValue(null);
                        return target == null ? null : FieldUp(target.GetType(), "ObjectGroupSaving", kSusInst);
                    }
                case "smh": return typeof(Sims3.Gameplay.GameEntryMovingModel).GetField("SplitMergeHouseholdsComplete", kSusStatic);
            }
            return null;
        }

        // One known change per counter through the same field the reading uses: +1, then back to the start value.
        static void MemSusSelfTest()
        {
            List<string> pass = new List<string>(), fail = new List<string>(), skip = new List<string>();
            foreach (string k in kSusKeys)
            {
                string why;
                bool? ok = SusTestOne(k, out why);
                if (ok == null) { skip.Add(k + " (" + why + ")"); sSusRetry.Add(k); continue; }
                (ok.Value ? pass : fail).Add(k);
            }
            if (skip.Count > 0) DiagLine("memsus selftest not yet testable (stay '?', retried at each sample): " + string.Join(", ", skip.ToArray()));
            DiagLine(fail.Count > 0 ? "memsus selftest FAIL: " + string.Join(" ", fail.ToArray())
                                    : "memsus selftest PASS (" + string.Join(" ", pass.ToArray()) + ")");
        }

        // Counters that were empty or unreadable at the self-test (no ErrorTrap, no catalogue opened yet) are tested once they
        // have entries; a late result is its own "memsus selftest <key> ..." line.
        static readonly List<string> sSusRetry = new List<string>();
        static void MemSusRetry()
        {
            foreach (string k in sSusRetry.ToArray())
            {
                string why;
                if (SusTestOne(k, out why) != null) sSusRetry.Remove(k);
            }
        }

        static bool? SusTestOne(string k, out string why)
        {
            int up, back, start = SusCount(k);
            bool? ok = k == "cc" || k == "ccIcon" ? SusTestDict(k, start, out up, out back, out why) : SusTestDelegate(k, start, out up, out back, out why);
            if (ok == null) return null;
            sSusMark[k] = ok.Value ? 'v' : '!';
            DiagLine("memsus selftest " + k + " " + start + " -> " + up + " -> " + back + " (want +1, back) " + (ok.Value ? "ok" : "FAIL"));
            return ok;
        }

        // null = cannot be tested now (why says so)
        static bool? SusTestDelegate(string k, int start, out int up, out int back, out string why)
        {
            up = back = -1;
            object target;
            FieldInfo f = SusField(k, out target);
            if (f == null || start < 0) { why = "not readable"; return null; }
            Delegate orig = f.GetValue(target) as Delegate;
            if (orig == null) { why = "empty list"; return null; }
            why = null;
            try
            {
                f.SetValue(target, Delegate.Combine(orig, orig.GetInvocationList()[0]));
                up = SusCount(k);
            }
            finally { f.SetValue(target, orig); }  // the original object back, whatever happened
            back = SusCount(k);
            return up == start + 1 && back == start;
        }

        static bool? SusTestDict(string k, int start, out int up, out int back, out string why)
        {
            up = back = -1;
            Dictionary<uint, WindowBase> d = SusDict();
            if (d == null || start < 0) { why = "not readable"; return null; }
            WindowBase value = null;
            if (k == "ccIcon")
            {
                foreach (WindowBase v in d.Values) if (v != null && v.GetType().Name == "CustomContentIcon") { value = v; break; }
                if (value == null) { why = "no CustomContentIcon entry yet"; return null; }
            }
            uint key = 0xFFFFFFF0;
            while (d.ContainsKey(key)) key--;
            why = null;
            try
            {
                d.Add(key, value);  // ccIcon: a second key for an existing icon; cc: a null entry
                up = SusCount(k);
            }
            finally { d.Remove(key); }
            back = SusCount(k);
            return up == start + 1 && back == start;
        }

        // ---- docs/191 "Player memory lines" (ts3native 0.36.79 [MemDiag] Light, release default on) ----
        // Destroyed-but-alive: ScriptObject.Destroy (every script destroy that reaches the base: World.RemoveObjectFromObjectManager
        // + Simulator.DestroyObject) keeps 1 in 32 destroyed objects in a ring of 512 reused WeakReference slots (no new
        // finalizable object per sample; a weak ref = a Mono GC handle + a Boehm disappearing link, docs/57). At each memsus
        // answer, a slot whose target survived at least 2 collections since its destroy counts as alive, by type:
        //   memsus alive: seen=<destroys> trk=<slots in use> freed=<collected> alive=<n> hook=1|0 ms=<read> top=Type:n,...
        // Types: EA core assemblies by class name, anything else as <assembly>/<class> (ties a type to its mod). Sim thread only
        // (Destroy runs on the sim; no GC handle on a worker, docs/57).
        // shortcut: ring overwrite drops the oldest tracked object after 512 x 32 destroys (~15 min to hours of play; a world
        // shutdown fills it at once); growth of alive across samples is the leak signal, not the absolute count.
        const int kAliveSlots = 512, kAliveEvery = 32;
        static bool sAliveOn, sAliveHook, sTypesDue;
        static int sAliveSeen, sAliveNext, sAliveFreed;
        static readonly WeakReference[] sAliveRef = new WeakReference[kAliveSlots];
        static readonly int[] sAliveGc = new int[kAliveSlots];  // GC.CollectionCount(0) at the destroy; -1 = slot empty
        static readonly Dictionary<Type, string> sAliveNames = new Dictionary<Type, string>();
        static readonly string[] kCoreAsm = { "Sims3GameplaySystems", "Sims3GameplayObjects", "SimIFace", "ScriptCore", "UI", "mscorlib" };

        static void ApplyMemAlive()
        {
            for (int i = 0; i < kAliveSlots; i++) sAliveGc[i] = -1;
            // v0.2.6: not patched. The swap drops the virtual flag, so a callvirt naming ScriptObject.Destroy skips every override (docs/67 §33).
        }

        static void AliveTrack(ScriptObject o)
        {
            if (!sAliveOn || o == null || ++sAliveSeen % kAliveEvery != 0) return;
            int i = sAliveNext++ & (kAliveSlots - 1);
            if (sAliveRef[i] == null) sAliveRef[i] = new WeakReference(o);
            else sAliveRef[i].Target = o;
            sAliveGc[i] = GC.CollectionCount(0);
        }

        public class AliveDestroyHook
        {
            public void DestroyHook()
            {
                try { AliveTrack((object)this as ScriptObject); } catch (Exception) { }  // `this` is really the ScriptObject
                DestroyOrig();
            }
            public void DestroyOrig() { }  // receives ScriptObject.Destroy's original header
        }

        static string AliveName(Type t)
        {
            string n;
            if (sAliveNames.TryGetValue(t, out n)) return n;
            string a = t.Assembly.GetName().Name;
            n = Array.IndexOf(kCoreAsm, a) >= 0 ? t.Name : a + "/" + t.Name;
            if (sAliveNames.Count < 2000) sAliveNames[t] = n;
            return n;
        }

        static string TopList(Dictionary<string, int> c, int max, int cap)
        {
            List<KeyValuePair<string, int>> l = new List<KeyValuePair<string, int>>(c);
            l.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y) { return y.Value.CompareTo(x.Value); });
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < l.Count && i < max; i++)
            {
                string e = (b.Length > 0 ? "," : "") + l[i].Key.Replace(' ', '_').Replace(',', '_').Replace(':', '_') + ":" + l[i].Value;
                if (b.Length + e.Length > cap) break;
                b.Append(e);
            }
            return b.Length > 0 ? b.ToString() : "-";
        }

        static string AliveLine()
        {
            StopWatch w = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            w.Start();
            int gc = GC.CollectionCount(0), trk = 0, alive = 0;
            Dictionary<string, int> byType = new Dictionary<string, int>();
            for (int i = 0; i < kAliveSlots; i++)
            {
                if (sAliveGc[i] < 0 || sAliveRef[i] == null) continue;
                object o = sAliveRef[i].Target;
                if (o == null) { sAliveGc[i] = -1; sAliveFreed++; continue; }
                trk++;
                if (gc - sAliveGc[i] < 2) continue;  // not yet through 2 collections (ScriptProxy.Dispose runs after Destroy)
                alive++;
                string n = AliveName(o.GetType());
                int k;
                byType.TryGetValue(n, out k);
                byType[n] = k + 1;
            }
            string top = TopList(byType, 8, 260);
            w.Stop();
            string line = "memsus alive: seen=" + sAliveSeen + " trk=" + trk + " freed=" + sAliveFreed + " alive=" + alive + " hook=" + (sAliveHook ? 1 : 0)
                + " ms=" + (w.GetElapsedTime() / 1000.0).ToString("0.00") + " top=" + top;
            w.Dispose();
            return line;
        }

        // Object counts by type once per load (first memsus answer after the load): n=<objects> kinds=<types> ms=<read> top=Type:n,...
        static string TypesLine()
        {
            StopWatch w = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            w.Start();
            GameObject[] all = Sims3.Gameplay.Queries.GetObjects<GameObject>();
            Dictionary<string, int> byType = new Dictionary<string, int>();
            foreach (GameObject o in all)
            {
                if (o == null) continue;
                string n = AliveName(o.GetType());
                int k;
                byType.TryGetValue(n, out k);
                byType[n] = k + 1;
            }
            string top = TopList(byType, 12, 330);
            w.Stop();
            string line = "memsus types: n=" + all.Length + " kinds=" + byType.Count + " ms=" + (w.GetElapsedTime() / 1000.0).ToString("0.00") + " top=" + top;
            w.Dispose();
            return line;
        }
    }
}
