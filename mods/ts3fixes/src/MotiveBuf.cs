using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // motivebuf (docs/584 §5, default off; proven-exact speedup, so every Sim, household included): Motives.UpdateMotives
    // runs from Autonomy.Update for every Sim once per sim-minute or more (the active Sim every update) and snapshots its
    // motives with `new List<Motive>(mMotives.Values)` before updating each one: a List and its array (Mono 2.x: also a
    // ValueCollection) per call, ~123 Sims x 60+ calls per sim-hour in Bridgeport (run 2043 census) [verified, decompiled].
    // The snapshot stays (the loop may Simulator.Sleep every 10 motives, and a Motive.Update may add or remove motives),
    // but it goes into a list taken from a small pool and handed back cleared, so nothing is allocated. Same motives, same
    // order (the dictionary's own enumeration, which is what ValueCollection copies), same timer (EA's static sUpdateTimer),
    // same sleep and destroyed-Sim return, same MotiveChanged call. A Sim sleeping mid-loop holds its list; the next
    // caller takes another one, so nested or interleaved updates never share a buffer.
    // motivebufcheck (default off, pure check): each buffered snapshot is compared with EA's copy (count and each element
    // by reference); a difference is counted and logged once, and the update runs over EA's copy.
    // Counters: [2729] updates served from a pooled list (each saves >= 2 allocations), [2730] snapshots checked, [2731]
    // check mismatches (must stay 0), [2732] fell back to EA's method (fields not found) or errors.
    public static partial class Fixes
    {
        static FieldInfo sMbMotives, sMbChanged;
        static StopWatch sMbTimer;
        static bool sMbTried, sMbOk, sMbLogged;
        static readonly Stack<List<Motive>> sMbPool = new Stack<List<Motive>>();

        static void ApplyMotiveBuf()
        {
            Patch(typeof(Motives), "UpdateMotives", new Type[] { typeof(float), typeof(Sim) }, typeof(MotivesUpdateHook), "UpdateHook", "UpdateOrig");
        }

        static bool MbReady()
        {
            if (sMbTried) return sMbOk;
            sMbTried = true;
            try
            {
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
                sMbMotives = typeof(Motives).GetField("mMotives", inst);
                sMbChanged = typeof(Motives).GetField("MotiveChanged", inst);
                FieldInfo t = typeof(Motives).GetField("sUpdateTimer", BindingFlags.Static | BindingFlags.NonPublic);
                sMbTimer = t != null ? t.GetValue(null) as StopWatch : null;
                sMbOk = sMbMotives != null && sMbMotives.FieldType == typeof(Dictionary<int, Motive>) && sMbChanged != null && sMbTimer != null;
            }
            catch (Exception) { sMbOk = false; }
            if (!sMbOk) AppliedLog().Add("Motives.UpdateMotives: motivebuf fields not found, EA's method runs");
            return sMbOk;
        }

        // buffbufcheck (default off, check for ts3native [IlPatch] ilp_buffbuf, spec mods/ilpatch/specs/buffbuf-delayed-copy.json):
        // the IL patch jumps over UpdateBuffs' copy-and-walk of mDelayedBuffs when that ListSorted enumerates nothing. Skipping
        // is exact because a walk over an empty copy does nothing; this check reads, right before UpdateBuffs (Autonomy.Update
        // calls it after UpdateMotives), whether the list's own Count field agrees with what it enumerates (the patch tests
        // enumeration, EA's other code trusts Count), and how many calls take the patched skip. [2733] calls checked, [2734]
        // empty (the patched build skips the copy: >= 1 allocation saved each), [2735] Count / enumeration mismatches (must be 0).
        static FieldInfo sBbDelayed, sBbCount;
        static bool sBbTried;
        static void BbCheck(Sim sim)
        {
            int[] c = PatchCounters();
            try
            {
                if (sim == null || sim.HasBeenDestroyed || sim.BuffManager == null) return;
                if (!sBbTried)
                {
                    sBbTried = true;
                    sBbDelayed = typeof(Sims3.Gameplay.ActorSystems.BuffManager).GetField("mDelayedBuffs", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (sBbDelayed != null) sBbCount = sBbDelayed.FieldType.GetField("Count", BindingFlags.Instance | BindingFlags.Public);
                }
                if (sBbDelayed == null || sBbCount == null) return;
                object list = sBbDelayed.GetValue(sim.BuffManager);
                System.Collections.IEnumerable e = list as System.Collections.IEnumerable;
                if (e == null) return;
                int n = 0;
                foreach (object o in e) n++;
                c[2733]++;
                if (n == 0) c[2734]++;
                if (n != (int)sBbCount.GetValue(list)) c[2735]++;
            }
            catch (Exception) { c[2732]++; }
        }

        static List<Motive> MbTake() { return sMbPool.Count > 0 ? sMbPool.Pop() : new List<Motive>(32); }
        static void MbGive(List<Motive> l) { l.Clear(); if (sMbPool.Count < 16) sMbPool.Push(l); }

        // true = the buffered snapshot equals EA's copy
        static bool MbCheck(Dictionary<int, Motive> d, List<Motive> buf, int[] c)
        {
            c[2730]++;
            List<Motive> ea = new List<Motive>(d.Values);
            bool same = ea.Count == buf.Count;
            for (int i = 0; same && i < ea.Count; i++) same = ReferenceEquals(ea[i], buf[i]);
            if (same) return true;
            c[2731]++;
            if (!sMbLogged) { sMbLogged = true; DiagLine("motivebuf: snapshot differs from the game's copy (" + buf.Count + " vs " + ea.Count + "), the game's copy used"); }
            buf.Clear();
            buf.AddRange(ea);
            return false;
        }

        public class MotivesUpdateHook : Motives
        {
            public void UpdateHook(float simMinutesPassed, Sim sim)
            {
                try { UpdateBody(simMinutesPassed, sim); }
                finally { if (SwOn(kSwBuffBufCheck)) BbCheck(sim); }  // Autonomy.Update calls UpdateBuffs right after this
            }
            void UpdateBody(float simMinutesPassed, Sim sim)
            {
                if (!SwOn(kSwMotiveBuf) || !MbReady()) { UpdateOrig(simMinutesPassed, sim); return; }
                int[] c = PatchCounters();
                Dictionary<int, Motive> d;
                MotiveChangedCallback changed;
                try { d = (Dictionary<int, Motive>)sMbMotives.GetValue(this); }
                catch (Exception) { c[2732]++; UpdateOrig(simMinutesPassed, sim); return; }
                if (d == null) { c[2732]++; UpdateOrig(simMinutesPassed, sim); return; }
                sMbTimer.Restart();
                int n = 0;
                float hours = simMinutesPassed / 60f, ofDay = SimClock.HoursPassedOfDay;
                List<Motive> buf = MbTake();
                try
                {
                    foreach (KeyValuePair<int, Motive> kv in d) buf.Add(kv.Value);
                    if (SwOn(kSwMotiveBufCheck)) MbCheck(d, buf, c);
                    c[2729]++;
                    for (int i = 0; i < buf.Count; i++)
                    {
                        buf[i].Update(hours, ofDay, sim);
                        n++;
                        if (n % 10 == 0 && sMbTimer.GetElapsedTimeFloat() > 1f)
                        {
                            Simulator.Sleep(0u);
                            if (sim.HasBeenDestroyed) return;
                            sMbTimer.Restart();
                        }
                    }
                }
                finally { MbGive(buf); }
                changed = sMbChanged.GetValue(this) as MotiveChangedCallback;
                if (changed != null) changed();
            }
            public void UpdateOrig(float simMinutesPassed, Sim sim) { } // receives Motives.UpdateMotives' original header
        }
    }
}
