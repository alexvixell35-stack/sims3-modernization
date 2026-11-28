using System;
using System.Collections;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/275 Z1 + M1: two exact in-place early-outs for the "writer" Mono candidates, each default off with its own tuning,
    // switch and verify switch. Same answer and same writes as the game's method for every lot and every Sim (household and
    // selected included), so no scope gate. Off the sim thread each hook runs the game's method (docs/128 G-M3). Rules in
    // TickMemo.cs (TableKeeper, AbsentMemo), offline self-check tools/selftest/hotspots.
    //
    // zdemptyout (Z1, tuning kZombieDanceEmptyOut): ZombieDanceTimeOut.HasStereoOnTheLot and UpdateTimeOutOnTheLots look the lot
    // up in the private per-lot table and write only on a hit. With the table empty the lookup fails: the first returns false,
    // the second returns, neither writes [verified: decompile]. The hooks answer exactly that without the lookup. The table
    // reference is read once per ZombieDanceTimeOut instance (the field is written only by the private constructor [verified:
    // IL scan, docs/51 §491]); a null table goes to the game's method (which throws as before).
    // Counters: [1064] HasStereo calls, [1065] of them with an empty table, [1066] UpdateTimeOut calls, [1067] of them with an
    // empty table, [1068] verify mismatches (zdemptyoutverify: the game's method runs too and must answer false / leave the
    // table empty).
    //
    // decaymemo (M1, tuning kDecayMultMemo): Motive.GetCommodityDecayMultiplier(sim) multiplies the factors of three reward buffs
    // and returns 1 when none is on the Sim. A buff absent at a full check stays absent until something is added to that
    // Sim's buff table; the only adds are the two private BuffManager.AddBuff overloads (every Dictionary.Add / set_Item on a
    // BuffManager's mValues in the game assemblies, simler90's core included; no subclass of BuffManager) [verified: IL scan,
    // docs/51 §491]. Both bump one add generation before and after the game's body (OnAddition callbacks run before the Add).
    // The memo keeps one buff manager: same manager, same generation, all three absent at the check = 1.
    // Counters: [1060] hits, [1061] misses, [1062] verify mismatches (decaymemoverify: the game's method runs on every hit
    // and must return 1), [1063] buff adds seen.
    public static partial class Fixes
    {
        static bool sZdReady, sDecayReady;
        static FieldInfo sZdTableField;
        static readonly TableKeeper sZdTable = new TableKeeper();
        static readonly AbsentMemo sDecayMemo = new AbsentMemo();
        static int sBuffAddGen;
        // switch reads cost a string-keyed lookup, about what the hooks save: read them every 256 calls (a test switch
        // flip takes effect within 256 calls; both paths are exact)
        static int sZdPoll, sDecayPoll;
        static bool sZdOn, sZdVerify, sDecayOn, sDecayVerify;

        static void ApplyWriterMemos()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
            // zdemptyout
            sZdTableField = typeof(ZombieDanceTimeOut).GetField("mZombieDanceTimeOutOnLots", inst);
            if (sZdTableField == null || !typeof(ICollection).IsAssignableFrom(sZdTableField.FieldType))
                AppliedLog().Add("zdemptyout: ZombieDanceTimeOut table field not found, not patched");
            else
                sZdReady = Patch(typeof(ZombieDanceTimeOut), "HasStereoOnTheLot", new Type[] { typeof(ulong) }, typeof(ZdHook), "StereoHook", "StereoOrig")
                    & Patch(typeof(ZombieDanceTimeOut), "UpdateTimeOutOnTheLots", new Type[] { typeof(ulong), typeof(float) }, typeof(ZdHook), "TimeOutHook", "TimeOutOrig");
            // decaymemo: both add paths first; without them the memo is never patched
            bool adds = Patch(typeof(BuffManager), "AddBuff", new Type[] { typeof(BuffInstance) }, typeof(BuffAddHook), "AddHook", "AddOrig")
                & Patch(typeof(BuffManager), "AddBuff", new Type[] { typeof(BuffInstance), typeof(BuffNames), typeof(int), typeof(float), typeof(bool), typeof(MoodAxis), typeof(Origin), typeof(bool), typeof(bool) },
                    typeof(BuffAddHook), "AddFullHook", "AddFullOrig");
            if (!adds) AppliedLog().Add("decaymemo: a BuffManager.AddBuff path could not be hooked, memo not patched");
            else sDecayReady = Patch(typeof(Motive), "GetCommodityDecayMultiplier", new Type[] { typeof(Sim) }, typeof(DecayMultHook), "MultHook", "MultOrig");
            if (sZdReady || sDecayReady) World.OnWorldQuitEventHandler += delegate { sZdTable.Clear(); sDecayMemo.Clear(); };
        }

        public class ZdHook
        {
            static ICollection Table(object self)
            {
                if (!sZdTable.Same(self)) sZdTable.Keep(self, sZdTableField.GetValue(self) as ICollection);
                return sZdTable.Table;
            }

            static void Poll()
            {
                if ((sZdPoll++ & 255) == 0) { sZdOn = ShortcutOn("zdemptyout"); sZdVerify = ShortcutOn("zdemptyoutverify"); }
            }

            public bool StereoHook(ulong lotId)
            {
                if (!sZdReady || OnWorker()) return StereoOrig(lotId);
                Poll();
                if (!sZdOn) return StereoOrig(lotId);
                int[] c = PatchCounters();
                c[1064]++;
                ICollection t = Table(this);
                if (!TableKeeper.Empty(t)) return StereoOrig(lotId);
                c[1065]++;
                if (sZdVerify && (StereoOrig(lotId) || t.Count != 0)) c[1068]++;
                return false;
            }
            public bool StereoOrig(ulong lotId) { return false; } // receives ZombieDanceTimeOut.HasStereoOnTheLot's original header

            public void TimeOutHook(ulong lotId, float timeElapsed)
            {
                if (!sZdReady || OnWorker()) { TimeOutOrig(lotId, timeElapsed); return; }
                Poll();
                if (!sZdOn) { TimeOutOrig(lotId, timeElapsed); return; }
                int[] c = PatchCounters();
                c[1066]++;
                ICollection t = Table(this);
                if (!TableKeeper.Empty(t)) { TimeOutOrig(lotId, timeElapsed); return; }
                c[1067]++;
                if (sZdVerify) { TimeOutOrig(lotId, timeElapsed); if (t.Count != 0) c[1068]++; }
            }
            public void TimeOutOrig(ulong lotId, float timeElapsed) { } // receives ZombieDanceTimeOut.UpdateTimeOutOnTheLots's original header
        }

        public class BuffAddHook
        {
            public bool AddHook(BuffInstance bi)
            {
                sBuffAddGen++;
                try { return AddOrig(bi); }
                finally { sBuffAddGen++; PatchCounters()[1063]++; }  // a decay check made inside the body (OnAddition) must not stay
            }
            public bool AddOrig(BuffInstance bi) { return false; } // receives BuffManager.AddBuff(BuffInstance)'s original header

            public bool AddFullHook(BuffInstance newBuff, BuffNames guid, int moodValue, float timeoutLength, bool timeoutPaused, MoodAxis axisEffected, Origin origin, bool considerDelayTimer, bool travelReaddition)
            {
                sBuffAddGen++;
                try { return AddFullOrig(newBuff, guid, moodValue, timeoutLength, timeoutPaused, axisEffected, origin, considerDelayTimer, travelReaddition); }
                finally { sBuffAddGen++; PatchCounters()[1063]++; }
            }
            public bool AddFullOrig(BuffInstance newBuff, BuffNames guid, int moodValue, float timeoutLength, bool timeoutPaused, MoodAxis axisEffected, Origin origin, bool considerDelayTimer, bool travelReaddition) { return false; } // receives BuffManager.AddBuff(9 args)'s original header
        }

        public class DecayMultHook
        {
            public float MultHook(Sim sim)
            {
                if (!sDecayReady || OnWorker()) return MultOrig(sim);
                if ((sDecayPoll++ & 255) == 0) { sDecayOn = ShortcutOn("decaymemo"); sDecayVerify = ShortcutOn("decaymemoverify"); }
                if (!sDecayOn) return MultOrig(sim);
                int[] c = PatchCounters();
                BuffManager bm = sim != null ? sim.BuffManager : null;
                int gen = sBuffAddGen;
                if (sDecayMemo.Hit(bm, gen))
                {
                    c[1060]++;
                    if (sDecayVerify && MultOrig(sim) != 1f) c[1062]++;
                    return 1f;
                }
                c[1061]++;
                float res = MultOrig(sim);  // throws as the game's does on a null Sim or manager (nothing kept)
                // GetElement(BuffNames) = mValues.TryGetValue (GenericManager, not overridden): absent = !HasElement
                sDecayMemo.Store(bm, gen, bm != null && !bm.HasElement(BuffNames.CommodityDecayModifier)
                    && !bm.HasElement(BuffNames.CommodityDecayModifier1) && !bm.HasElement(BuffNames.CommodityDecayModifier2));
                return res;
            }
            public float MultOrig(Sim sim) { return 1f; } // receives Motive.GetCommodityDecayMultiplier's original header
        }
    }
}
