using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // fightstaycap (tuning kFightStayCap, off by default). A snowball / water balloon fight (SnowWaterFight, not the arena)
    // ends only when fewer than two players are left (ControllerCallback -> DoEndOfGame after kWaitForJoinsThreshold
    // minutes). Nothing in SnowWaterFightWait / Throw / Catch looks at the Sim's needs or at how long it has played: Wait's
    // loop ends on an exit reason only, and the controller re-pushes Throw for every idle player each sim minute. Two Sims
    // that keep each other company therefore throw until one is pulled away (a motive-failure exit, a player command).
    // [verified: decompiled SnowWaterFight/SnowWaterFightWait/Throw/Catch and ControllerCallback; the stall itself seen in
    // af4 2026-11-17, Garrett Sleep ~10 real minutes in Throw/Wait/Dodge with a need at -25, ended when the partner left.
    // The real-game reason EA's own tuning does not end it is not visible in the assemblies: inferred.]
    // Fix (a cap, not an EA intent): before EA's ControllerCallback, a player who has been in this fight more than 120
    // sim minutes and is waiting in SnowWaterFightWait with no exit reason pending gets ExitReason.CanceledByScript, so
    // Wait takes EA's own leave path (Exit animation, QuitFight). Household Sims and the selected Sim are never touched
    // (IsPlayerSim, also with HouseholdToo: a release is a cut); arenas are skipped; Sims in Throw / Catch are left alone (a pair is never broken mid-throw).
    // Counters [2719] Sims released, [2720] hook errors, [2721] fight members not found (EA's callback runs).
    public static partial class Fixes
    {
        const float kFightStayMinutes = 120f;
        static readonly FieldInfo sFightSlots = typeof(SnowWaterFight).GetField("PlayerSlots", kInstAll);
        // Sim -> sim-clock minute it was first seen in a fight; one entry per Sim, dropped when it leaves the slots
        static readonly Dictionary<Sim, float> sFightSince = new Dictionary<Sim, float>();

        public class FightStayCapHook
        {
            public void ControllerHook()
            {
                if (ShortcutOn("fightstaycap"))
                {
                    try { FightCap((SnowWaterFight)(object)this); }
                    catch (Exception) { PatchCounters()[2720]++; }
                }
                ControllerOrig();
            }
            public void ControllerOrig() { } // receives SnowWaterFight.ControllerCallback's original body
        }

        static void FightCap(SnowWaterFight f)
        {
            if (f is SnowWaterFightArena) return;
            Sim[] slots = sFightSlots == null ? null : sFightSlots.GetValue(f) as Sim[];
            if (slots == null) { PatchCounters()[2721]++; return; }
            float now = SimClock.ElapsedTime(TimeUnit.Minutes);
            List<Sim> gone = null;
            foreach (KeyValuePair<Sim, float> kv in sFightSince)
                if (kv.Key == null || kv.Key.HasBeenDestroyed || Array.IndexOf(slots, kv.Key) < 0 && FightOf(kv.Key) == null)
                { if (gone == null) gone = new List<Sim>(); gone.Add(kv.Key); }
            if (gone != null) foreach (Sim s in gone) if (s != null) sFightSince.Remove(s);
            foreach (Sim s in slots)
            {
                if (s == null || s.HasBeenDestroyed) continue;
                float since;
                if (!sFightSince.TryGetValue(s, out since)) { sFightSince[s] = now; continue; }
                if (now - since <= kFightStayMinutes || IsPlayerSim(s) || s.HasExitReason()) continue;  // a release is a cut: never lifted by HouseholdToo
                if (!(s.InteractionQueue.GetCurrentInteraction() is SnowWaterFight.SnowWaterFightWait)) continue;
                s.AddExitReason(ExitReason.CanceledByScript);
                sFightSince.Remove(s);
                PatchCounters()[2719]++;
            }
        }

        // the fight a Sim is playing, found from its running interaction (null = not in one)
        static SnowWaterFight FightOf(Sim s)
        {
            try
            {
                InteractionInstance i = s.InteractionQueue == null ? null : s.InteractionQueue.GetCurrentInteraction();
                return i == null ? null : i.Target as SnowWaterFight;
            }
            catch (Exception) { return null; }
        }
    }
}
