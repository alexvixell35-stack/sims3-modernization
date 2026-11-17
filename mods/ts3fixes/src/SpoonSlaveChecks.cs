using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.TuningValues;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/543 §1: a spooning slave in a shared bed. Off by default; acts on household Sims too (EA bug fix, no skip or
    // throttle).
    //
    // spoonslavechecks (kSpoonSlaveChecks; docs/543 §1). [verified, decompiled] BedSleep.LoopHandler runs each sleeper's own
    // per-tick checks (light-sleeper wake after TraitTuning.LightSleeperMinutesAbleToSleep, early TiredFromMoving removal,
    // the one-time NoMonsters cut) after `if (mSpooningSlave) return;`. Two romantic partners in one bed are paired by the
    // first to tick (master) for the whole night: ClearSpooningContext after each spoon is followed by a new pairing on the
    // master's next tick. So the slave partner skips those checks all night, while the master runs them even mid-spoon
    // (they come before its `if (mSpooning)` block), and BedDreamPod / FairyHouse run them for every sleeper. Fix: on a tick
    // where EA's body returns at the slave test (slave, and not the evil-gnome branch, which EA takes first when not
    // spooning), the same three checks run right after EA's body, which is the place EA runs them for everyone else (nothing
    // between that test and the checks reads or writes state the checks use). The hook is spoonreset's SrHook (one swap of
    // LoopHandler, SpoonReset.cs). Counters: [2163] slave ticks given the checks, [2164] light-sleeper wakes delivered,
    public static partial class Fixes
    {
        static readonly FieldInfo sSsSlave = typeof(BedSleep).GetField("mSpooningSlave", kInstAll);
        static readonly FieldInfo sSsSpooning = typeof(BedSleep).GetField("mSpooning", kInstAll);
        static readonly FieldInfo sSsGnome = typeof(BedSleep).GetField("mEvilGnomeBeingScary", kInstAll);
        static readonly FieldInfo sSsStart = typeof(BedSleep).GetField("mInteractionStartTime", kInstAll);

        static string SsMissing()
        {
            string m = "";
            if (sSsSlave == null || sSsSlave.FieldType != typeof(bool)) m += " BedSleep.mSpooningSlave";
            if (sSsSpooning == null || sSsSpooning.FieldType != typeof(bool)) m += " BedSleep.mSpooning";
            if (sSsGnome == null) m += " BedSleep.mEvilGnomeBeingScary";
            if (sSsStart == null || sSsStart.FieldType != typeof(DateAndTime)) m += " BedSleep.mInteractionStartTime";
            return m.Trim();
        }

        // Pure: does EA's LoopHandler return at the slave test this tick? (EA: `if (!mSpooning && gnome alive) {gnome}
        // else { if (mSpooningSlave) return; ... }`)
        internal static bool SsSlaveReturn(bool slave, bool spooning, bool gnomeAlive)
        {
            return slave && (spooning || !gnomeAlive);
        }

        // Pure: EA's light-sleeper test (strictly more minutes than the limit).
        internal static bool SsLightWake(bool lightSleeper, float minutes, float limit)
        {
            return lightSleeper && minutes > limit;
        }

        // Called by SrHook before EA's body: true when EA's body will return at the slave test and the switch is on.
        static bool SsBefore(object st, bool on)
        {
            try
            {
                if (sSsSlave == null || sSsSpooning == null || sSsGnome == null || sSsStart == null) return false;
                if (!(bool)sSsSlave.GetValue(st)) return false;
                IMagicGnomeEvil g = sSsGnome.GetValue(st) as IMagicGnomeEvil;
                if (!SsSlaveReturn(true, (bool)sSsSpooning.GetValue(st), g != null && !g.HasBeenDestroyed)) return false;
                if (on) return true;
                PatchCounters()[2165]++;
            }
            catch (Exception) { PatchCounters()[2166]++; }
            return false;
        }

        // After EA's body returned at the slave test: EA's three per-sleeper checks, in EA's order (BedSleep.LoopHandler).
        static void SsAfter(object st)
        {
            try
            {
                BedSleep me = (BedSleep)st;
                Sim actor = me.Actor;
                if (actor == null || actor.HasBeenDestroyed || actor.BuffManager == null) return;
                PatchCounters()[2163]++;
                float num = SimClock.ElapsedTime(TimeUnit.Minutes, (DateAndTime)sSsStart.GetValue(st));
                if (SsLightWake(actor.HasTrait(TraitNames.LightSleeper), num, TraitTuning.LightSleeperMinutesAbleToSleep))
                {
                    PatchCounters()[2164]++;
                    actor.AddExitReason(ExitReason.Finished);
                    return;
                }
                if (num > BuffTiredFromMoving.TimeToRemove * 60f) actor.BuffManager.RemoveElement(BuffNames.TiredFromMoving);
                BuffNoMonsters.BuffInstanceNoMonsters nm = actor.BuffManager.GetElement(BuffNames.NoMonsters) as BuffNoMonsters.BuffInstanceNoMonsters;
                if (nm != null && !nm.HasBeenReduced && num / 60f >= BuffNoMonsters.MinHoursSleptForQuickRemoval)
                {
                    nm.mTimeoutCount -= (float)BuffNoMonsters.HoursSubtractedForQuickRemoval * 60f;
                    nm.HasBeenReduced = true;
                }
            }
            catch (Exception) { PatchCounters()[2166]++; }
        }

        // SpoonSlaveChecksCheck run: the slave tick gets the checks; controls: the master / unpaired tick (EA runs them
        // itself) and the gnome tick (EA's gnome branch) are left to EA; the light-sleeper limit is strict like EA's.
        public static object SpoonSlaveChecksCheck(string what)
        {
            string miss = SsMissing();
            string counters = B582Counters("spoonslavechecks", 2163, new[] { "slave ticks checked", "light-sleeper wakes", "slave ticks skipped (switch off)", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "spoonslavechecks <run|result>";
            bool on = ShortcutOn("spoonslavechecks");
            bool fixedNow = on && SsSlaveReturn(true, false, false) && SsSlaveReturn(true, true, true);
            bool controls = !SsSlaveReturn(false, false, false) && !SsSlaveReturn(false, true, false) && !SsSlaveReturn(true, false, true)
                && SsLightWake(true, 121f, 120f) && !SsLightWake(true, 120f, 120f) && !SsLightWake(false, 500f, 120f);
            return B582Line("spoonslavechecks", on, "spooning slave gets EA's light-sleeper / TiredFromMoving / NoMonsters checks", fixedNow,
                "master/unpaired/gnome tick left to EA, strict limit", controls, miss, counters);
        }

        // live paths of the checks (CheckVerdict.cs CkLive): hook swaps in place, reflection handles usable
        static readonly int sLive_spoonslavechecks = LiveAdd("spoonslavechecks", () => EpLive(typeof(SrHook), 1, "sSrMasterI", sSrMasterI, null, "sSrSlaveI", sSrSlaveI, null, "sSrIsSlave", sSrIsSlave, typeof(bool), "sSrIsMaster", sSrIsMaster, typeof(bool), "sSrSpooning", sSrSpooning, typeof(bool), "sSrSmc", sSrSmc, null, "sSrClear", sSrClear, 0));
    }
}
