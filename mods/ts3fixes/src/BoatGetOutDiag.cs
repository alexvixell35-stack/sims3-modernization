using System;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Island;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // boatgetoutdiag (docs/257 §7, diagnostic, tuning kBoatGetOutDiag, off): EA's Boat.GetOut (8 arguments, every overload
    // ends there) checks that the Sim's posture is a SittingInBoat of this boat, then sleeps in FadeOut, then GetOutIntoWater
    // reads a.Posture again unchecked. The NullReferenceException at GetOutIntoWater+0x1d means another task replaced the
    // posture during the fade. With the switch on, the watch hook records each GetOut in flight (BgoTable) and the existing
    // set_Posture hook (PosturePop.cs) calls BgoNote, which logs the first posture set per watch that is not the own task's
    // normal end: writer task, nested or foreign, its object type, the stack with our frames named. A NullReferenceException
    // out of GetOut is logged and rethrown unchanged (throw;), so EA's error and its ScriptError stay. Logging only: no Sim
    // (household or not) changes, so no household exemption; household Sims are flagged in the line.
    // Counters [945] watched GetOuts, [946] replacements in the window, [947] stale throws (NRE out of a watched GetOut).
    // At most kBgoLines lines per session in ts3native.log.
    public static partial class Fixes
    {
        const int cBgo = 945, kBgoLines = 20, kBgoFrames = 12;
        static readonly BgoTable sBgo = new BgoTable();
        static int sBgoLines, sBgoSeq;

        public class BoatGetOutDiagHook
        {
            public void GetOutHook(Sim a, Vector3 getOutOntoLandVector, bool bOnReset, bool tryWaterFirst, Houseboat houseboat, Vector3 specificWaterPosition, bool fadeIn, bool ifItMakesSenseSwitchToMermaidTail)
            {
                int at = ShortcutOn("boatgetoutdiag") ? BgoPush(a, (Boat)(object)this, tryWaterFirst, houseboat != null) : -1;
                if (at < 0) { GetOutOrig(a, getOutOntoLandVector, bOnReset, tryWaterFirst, houseboat, specificWaterPosition, fadeIn, ifItMakesSenseSwitchToMermaidTail); return; }
                try { GetOutOrig(a, getOutOntoLandVector, bOnReset, tryWaterFirst, houseboat, specificWaterPosition, fadeIn, ifItMakesSenseSwitchToMermaidTail); }
                catch (NullReferenceException) { BgoStale(at); throw; }
                finally { sBgo.Pop(at); }
            }
            public void GetOutOrig(Sim a, Vector3 getOutOntoLandVector, bool bOnReset, bool tryWaterFirst, Houseboat houseboat, Vector3 specificWaterPosition, bool fadeIn, bool ifItMakesSenseSwitchToMermaidTail) { } // receives Boat.GetOut's original header
        }

        // only a GetOut that will reach the fade: EA returns at once unless the Sim sits in this boat
        static int BgoPush(Sim a, Boat boat, bool water, bool houseboat)
        {
            try
            {
                SittingInBoat sib = a != null ? a.Posture as SittingInBoat : null;
                if (sib == null || sib.Container != boat) return -1;
                int at = sBgo.Push(a, sib, Simulator.CurrentTask.Value);
                if (at < 0) { if (sBgo.Overflow == 1) BgoLine("boatgetout: more than " + BgoTable.kMax + " GetOuts in flight; the rest are not watched"); return -1; }
                sBgo.Id[at] = 0; sBgo.Water[at] = water; sBgo.Houseboat[at] = houseboat; sBgo.At[at] = Simulator.TicksElapsed();
                PatchCounters()[cBgo]++;
                return at;
            }
            catch (Exception) { return -1; }
        }

        // From PosturePopHook.SetHook, first line, while a GetOut is watched. Never throws into the setter.
        static void BgoNote(Sim s, Posture cur, Posture value)
        {
            try
            {
                ulong task = Simulator.CurrentTask.Value;
                int at;
                int k = sBgo.Classify(s, cur, task, out at);
                if (k == BgoTable.kIgnore || k == BgoTable.kOwnEnd || sBgo.Replaced[at]) return;
                sBgo.Replaced[at] = true;
                PatchCounters()[cBgo + 1]++;
                if (sBgoLines >= kBgoLines) return;
                int id = ++sBgoSeq;
                sBgo.Id[at] = id;
                IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                string writer = p != null && p.Target != null ? p.Target.GetType().Name : "?";
                Boat boat = ((SittingInBoat)cur).Container as Boat;
                string ours, stack;
                BgoStack(out ours, out stack);
                object moved;
                MovedSims().TryGetValue(s.FullName, out moved);
                BgoLine("boatgetout replaced #" + id + ": " + s.FullName + " 0x" + s.ObjectId.Value.ToString("x") + " " + BgoScope(s)
                    + " SittingInBoat->" + (value != null ? value.GetType().Name : "null") + " during GetOut of " + (boat != null ? boat.GetType().Name + " 0x" + boat.ObjectId.Value.ToString("x") : "?")
                    + " (+" + (Simulator.TicksElapsed() - sBgo.At[at]) + " ticks, " + SimClock.CurrentTime() + ", water " + sBgo.Water[at] + ", houseboat " + sBgo.Houseboat[at] + ") by "
                    + (k == BgoTable.kNested ? "nested" : "foreign") + " task " + writer + "; ours: " + ours + "; last move: " + (moved != null ? moved.ToString() : "none"));
                BgoLine("boatgetout stack #" + id + ": " + stack);
            }
            catch (Exception) { }
        }

        static void BgoStale(int at)
        {
            try
            {
                PatchCounters()[cBgo + 2]++;
                if (sBgoLines >= kBgoLines) return;
                Sim s = sBgo.Sim[at] as Sim;
                BgoLine("boatgetout stale: " + (s != null ? s.FullName + " " + BgoScope(s) : "?") + " NRE in GetOut after "
                    + (sBgo.Replaced[at] ? (sBgo.Id[at] > 0 ? "replaced #" + sBgo.Id[at] : "a replacement (line cap)") : "no replacement seen (posture changed without set_Posture?)"));
            }
            catch (Exception) { }
        }

        static string BgoScope(Sim s)
        {
            return s.IsSelectable || (s.Household != null && s.Household == Household.ActiveHousehold) ? "household" : "npc";
        }

        static void BgoLine(string line) { sBgoLines++; DiagLine(line); }

        // the writer's frames above the set_Posture hook, kBgoFrames at most; ours = the Ts3Fixes frames among them
        static void BgoStack(out string ours, out string stack)
        {
            StringBuilder o = new StringBuilder(), b = new StringBuilder();
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
            for (int i = 0, n = 0; i < st.FrameCount && n < kBgoFrames; i++)
            {
                System.Reflection.MethodBase m = st.GetFrame(i).GetMethod();
                if (m == null) continue;
                Type t = m.DeclaringType;
                if (t == typeof(PosturePopHook) || t == typeof(BoatGetOutDiagHook) || m.Name == "BgoNote") continue;  // our diag hooks, on every such stack
                string name = (t != null ? t.Name : "?") + "." + m.Name;
                if (b.Length > 0) b.Append(" < ");
                b.Append(name);
                n++;
                if (t != null && t.Namespace == "Ts3Fixes") { if (o.Length > 0) o.Append(", "); o.Append(name); }
            }
            ours = o.Length > 0 ? o.ToString() : "none";
            stack = b.ToString();
        }
    }
}
