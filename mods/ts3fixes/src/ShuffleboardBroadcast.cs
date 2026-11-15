using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Objects.HobbiesSkills;

namespace Ts3Fixes
{
    // shuffleboardbcast (tuning kShuffleboardBcast, off by default; docs/546 §17). EA bug fix (leak), exact for every Sim.
    //
    // Shuffleboard+Play.TriggerReactions makes two new ReactionBroadcasters (people and pets) and stores them in
    // mBroadcaster / mPetBroadcaster without disposing the pair already there [verified, decompiled]. Run calls it after
    // every throw and a second time when the throw ends the round (EndRound), so each round end replaces a live pair;
    // Play.Cleanup disposes only the last pair. A broadcaster has TimeDuration -1 (ReactionBroadcasterParams default) and
    // leaves its lot's reaction list only through Dispose or when the board leaves the world, so every replaced pair keeps
    // pulsing Sims once a second for the rest of the session: the board's lot does one more PulseSims per leaked
    // broadcaster per second, and a Sim who later plays there gets one stale BroadcastCallback per leaked pair (a
    // reaction toward a long-gone thrower, or Quit + CanceledByScript when a game just ended).
    // Measured (leak census, Bridgeport): Shuffleboard broadcasters 0 -> 94 (phase-20261114-1737) and 0 -> 148
    // (phase-20261114-1835, 79 on one lot) in ~45 min; lots total 71 -> 254.
    //
    // The fix: a pair that TriggerReactions is about to replace is kept aside and disposed right after that same Play
    // instance's Cleanup, the point where EA ends the current pair. While the interaction runs both pairs pulse as in EA's
    // code, so every reaction a player or watcher gets during the throw is unchanged; only the pulses after Cleanup (the
    // leak) are gone. Applies to all Sims: nothing a Sim does during the interaction changes.
    // Counters: [2409] broadcasters replaced by TriggerReactions (also with the switch off), [2410] replaced broadcasters
    // disposed, [2411] errors (registry fixes-2409). Harness check: ShuffleboardCheck (ObjectLeakChecks.cs), repro
    // shuffleboard-broadcast-leak.
    public static partial class Fixes
    {
        static FieldInfo sSbBcast, sSbPetBcast;

        // replaced pairs per Play instance (sim thread only). A Play whose task dies before Cleanup leaves its entry
        // behind: past kSbStashMax entries the oldest is disposed (a Play instance lives for one throw).
        sealed class SbStash { public object Play; public readonly List<ReactionBroadcaster> Old = new List<ReactionBroadcaster>(); }
        static readonly List<SbStash> sSbStash = new List<SbStash>();
        const int kSbStashMax = 32;

        static void ApplyShuffleboardBcast()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type play = typeof(Shuffleboard).GetNestedType("Play", BindingFlags.Public | BindingFlags.NonPublic);
            sSbBcast = play == null ? null : play.GetField("mBroadcaster", inst);
            sSbPetBcast = play == null ? null : play.GetField("mPetBroadcaster", inst);
            if (sSbBcast == null || sSbPetBcast == null) { AppliedLog().Add("shuffleboardbcast: Shuffleboard+Play members not found, not patched"); return; }
            Patch(play, "TriggerReactions", typeof(SbHook), "TriggerHook", "TriggerOrig");
            Patch(play, "Cleanup", typeof(SbHook), "CleanupHook", "CleanupOrig");
        }

        public class SbHook
        {
            public void TriggerHook()
            {
                try { SbKeepReplaced(this); } catch (Exception) { PatchCounters()[2411]++; }
                TriggerOrig();
            }
            public void TriggerOrig() { } // receives Shuffleboard+Play.TriggerReactions's original body

            public void CleanupHook()
            {
                try { CleanupOrig(); }
                finally
                {
                    try { SbDisposeKept(this); } catch (Exception) { PatchCounters()[2411]++; }
                }
            }
            public void CleanupOrig() { } // receives Shuffleboard+Play.Cleanup's original body
        }

        // Before TriggerReactions overwrites the pair: count it, and with the switch on keep it for this Play's Cleanup.
        static void SbKeepReplaced(object play)
        {
            ReactionBroadcaster b = sSbBcast.GetValue(play) as ReactionBroadcaster, p = sSbPetBcast.GetValue(play) as ReactionBroadcaster;
            if (b == null && p == null) return;
            int[] c = PatchCounters();
            if (b != null) c[2409]++;
            if (p != null) c[2409]++;
            if (!ShortcutOn("shuffleboardbcast")) return;
            SbStash s = SbFind(play, false);
            if (s == null)
            {
                if (sSbStash.Count >= kSbStashMax) { SbDispose(sSbStash[0]); sSbStash.RemoveAt(0); }
                s = new SbStash();
                s.Play = play;
                sSbStash.Add(s);
            }
            if (b != null) s.Old.Add(b);
            if (p != null) s.Old.Add(p);
        }

        static void SbDisposeKept(object play)
        {
            if (sSbStash.Count == 0) return;
            SbStash s = SbFind(play, true);
            if (s != null) SbDispose(s);
        }

        static SbStash SbFind(object play, bool remove)
        {
            for (int i = 0; i < sSbStash.Count; i++)
            {
                if (!ReferenceEquals(sSbStash[i].Play, play)) continue;
                SbStash s = sSbStash[i];
                if (remove) sSbStash.RemoveAt(i);
                return s;
            }
            return null;
        }

        static void SbDispose(SbStash s)
        {
            foreach (ReactionBroadcaster b in s.Old)
            {
                try { b.Dispose(); PatchCounters()[2410]++; } catch (Exception) { PatchCounters()[2411]++; }
            }
            s.Old.Clear();
        }
    }
}
