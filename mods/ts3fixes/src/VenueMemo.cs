using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.Enums;

namespace Ts3Fixes
{
    // docs/122 §14.24 (design D of §14.23, docs/97 AM-1 H4), switch venuememo (tuning kVenueMemo, default off). Two exact
    // cuts in the meta venue score (Lot's per-venue-type HowMuchItWantsSimToCome, run for every pair that passed Test):
    //  1. MetaAutonomyManager.GetTuning(type) scans the type's tuning list and tests each entry's world restriction lists
    //     (List.Contains on boxed enums). The answer depends only on the type, the current world name and type, and the
    //     tuning lists, which only the startup Venues parse writes (appends). Kept per type for one world pair, read on
    //     every call; null answers are not kept (an append could give such a type its first match); cleared at world quit.
    //  2. The venue score asks Array.IndexOf(Lot.kMetaAutonomyVenueTypesToPenalizeScoreInRainOrHail, type) on every call,
    //     before it looks at Seasons or the weather: boxing plus Enum.Equals per element. The hook runs the game's score
    //     steps in the game's order with a typed scan of the same live array instead (same answer for any content, null
    //     array still throws as the game's call). Nothing is kept, so nothing can go stale.
    // Not here: GetDesiredNumSimsForMetaObject (R3). Its desired-population curve is linear between points, not a step,
    // so its answer moves with the clock every tick; no exact key exists (docs/122 §14.24.3).
    // Off the sim thread (the [AutShadow] worker) both hooks run the game's method: no memo or reflection writes there
    // (docs/128 G-M3). venuememoverify checks 1 call in 8 against the game's method and returns the game's answer.
    // Counters: [440] tuning memo hits, [441] misses, [442] tuning verify mismatches, [443] memo clears (world change or
    // quit), [444] venue scores by the hook's steps, [445] venue scores passed to the game's method (private member
    // access failed for that lot), [446] venue verify mismatches, [447] venue verify checks.
    public static partial class Fixes
    {
        static bool sTuningMemoReady, sVenueScoreReady;
        static readonly TuningMemo sTuningMemo = new TuningMemo();
        static FieldInfo sVmRandom;              // Lot.mRandomChanceThatChangesOnceADay (private)
        static MethodInfo sVmNumSims, sVmCelebs; // Lot.NumSimsGoingToOrAtThisLot getter, Lot.GetNumberOfCelebritiesAtThisLot (private)
        delegate int LotIntFn();
        sealed class VmLot { public Lot Lot; public LotIntFn NumSims, Celebs; }
        sealed class VmIdComparer : IEqualityComparer<ulong> // explicit: the default one boxes in this Mono (TickMemo.cs)
        {
            public bool Equals(ulong a, ulong b) { return a == b; }
            public int GetHashCode(ulong k) { return (int)k ^ (int)(k >> 32); }
        }
        static readonly Dictionary<ulong, VmLot> sVmLots = new Dictionary<ulong, VmLot>(new VmIdComparer());
        const int kVmLotsMax = 1024; // shortcut: whole cache dropped past this many lots, never seen in a world; upgrade if the clear counter climbs

        static void ApplyVenueMemo()
        {
            int types = 0;
            foreach (object v in Enum.GetValues(typeof(MetaAutonomyVenueType))) types = Math.Max(types, Convert.ToInt32(v) + 1);
            sTuningMemo.Size(types);
            sTuningMemoReady = Patch(typeof(MetaAutonomyManager), "GetTuning", new Type[] { typeof(MetaAutonomyVenueType) }, typeof(TuningMemoHook), "TuningHook", "TuningOrig");
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
            sVmRandom = typeof(Lot).GetField("mRandomChanceThatChangesOnceADay", inst);
            PropertyInfo p = typeof(Lot).GetProperty("NumSimsGoingToOrAtThisLot", inst);
            sVmNumSims = p != null ? p.GetGetMethod(true) : null;
            sVmCelebs = typeof(Lot).GetMethod("GetNumberOfCelebritiesAtThisLot", inst, null, Type.EmptyTypes, null);
            if (sTuningMemoReady) World.OnWorldQuitEventHandler += delegate { VmClear(); };
        }

        // The venue score hook, patched only with kVenueMemo on: [AutShadow]'s direct-callee guard (AllL3Ok with kLotCallees)
        // reads any swap of this method, ours included, as a mod and skips every Lot meta object (docs/122 §14.24.7).
        // Called by ApplyTuned once the tuning is set (docs/124 §17.8: Apply only sees C# defaults); meta venue scores run
        // only in a world [inferred], so the method is not compiled before the first world load.
        // shortcut: tuning-gated, upgrade to always-patched once AllL3Ok accepts our own swaps (sPatched) as ClosureSweep does
        static void ApplyVenueScore()
        {
            if (!Instantiator.kVenueMemo) return;
            if (sVmRandom == null || sVmNumSims == null || sVmCelebs == null) AppliedLog().Add("VenueMemo: Lot private members not found, venue score hook not patched");
            else sVenueScoreReady = Patch(typeof(Lot), "HowMuchItWantsSimToCome", new Type[] { typeof(MetaAutonomyVenueType), typeof(Sim), typeof(float) }, typeof(VenueScoreHook), "ScoreHook", "ScoreOrig");
            if (sVenueScoreReady && !sTuningMemoReady) World.OnWorldQuitEventHandler += delegate { VmClear(); };
            TunedLog("kVenueMemo", sVenueScoreReady, "venue score hook");
        }

        static void VmClear()
        {
            if (sTuningMemo.Clear()) PatchCounters()[443]++;
            sVmLots.Clear();
        }

        // The lot's private counters as delegates bound to that lot (made once per lot and world); null when that fails.
        static VmLot VmFns(Lot lot)
        {
            VmLot f;
            ulong id = lot.LotId;
            if (sVmLots.TryGetValue(id, out f) && ReferenceEquals(f.Lot, lot)) return f;
            try
            {
                f = new VmLot();
                f.Lot = lot;
                f.NumSims = (LotIntFn)Delegate.CreateDelegate(typeof(LotIntFn), lot, sVmNumSims);
                f.Celebs = (LotIntFn)Delegate.CreateDelegate(typeof(LotIntFn), lot, sVmCelebs);
            }
            catch (Exception) { return null; }
            if (sVmLots.Count >= kVmLotsMax) sVmLots.Clear();
            sVmLots[id] = f;
            return f;
        }

        // Same membership answer as Array.IndexOf(a, t) >= 0 on an enum array, without boxing.
        static bool VmInList(MetaAutonomyVenueType[] a, MetaAutonomyVenueType t)
        {
            if (a == null) return Array.IndexOf(a, t) >= 0; // throws ArgumentNullException, as the game's call
            for (int i = 0; i < a.Length; i++) if (a[i] == t) return true;
            return false;
        }

        // The game's per-venue-type score steps, in the game's order, with VmInList for the rain/hail list.
        static float VmScore(Lot lot, VmLot f, MetaAutonomyVenueType type, Sim sim, float hours24)
        {
            if (sim.LotCurrent == lot.LotCurrent) return 0f;
            float going = f.NumSims();
            MetaAutonomyTuning tuning = MetaAutonomyManager.GetTuning(type);
            if (tuning == null) return 0f;
            if (tuning.CelebrityMax != -1 && sim.SimDescription.IsCelebrity && (tuning.CelebrityMax == 0 || f.Celebs() >= tuning.CelebrityMax)) return 0f;
            float desired = Sims3.Gameplay.Autonomy.Autonomy.GetDesiredNumSimsForMetaObject(lot, tuning, hours24, true);
            bool gigSoon = false;
            Gig gig = lot.GetScheduledGigForToday();
            if (gig != null)
            {
                float until = SimClock.HoursUntil(gig.StartTime);
                gigSoon = until < 2f && until > 0f;
                if (gigSoon) desired = gig.DesiredAudienceSize;
            }
            float score;
            if ((lot.HasActiveLocationOpportunity || gigSoon) && going < desired) score = 100f;
            else
            {
                float intensity = tuning.GetIntensity(lot, sim);
                score = Sims3.Gameplay.Autonomy.Autonomy.ScoreMetaObject((float)sVmRandom.GetValue(lot), going, desired, intensity);
            }
            if (VmInList(Lot.kMetaAutonomyVenueTypesToPenalizeScoreInRainOrHail, type) && SeasonsManager.Enabled
                && (SeasonsManager.CurrentWeather == Weather.Rain || SeasonsManager.CurrentWeather == Weather.Hail))
                score *= Lot.kkMetaAutonomyPenaltyScoreMultiplier;
            if (type == MetaAutonomyVenueType.Resort && lot.IsCheckedInResort(sim)) score *= Lot.kMetaAutonomyMultiplierAtCheckedInResort;
            return score;
        }

        public static class TuningMemoHook
        {
            public static MetaAutonomyTuning TuningHook(MetaAutonomyVenueType type)
            {
                if (!sTuningMemoReady || OnWorker() || !ShortcutOn("venuememo")) return TuningOrig(type);
                int[] c = PatchCounters();
                if (sTuningMemo.World((int)GameUtils.GetCurrentWorld(), (int)GameUtils.GetCurrentWorldType())) c[443]++;
                object kept;
                if (sTuningMemo.TryGet((int)type, out kept))
                {
                    c[440]++;
                    if (VerifyNow("venuememoverify"))
                    {
                        MetaAutonomyTuning g = TuningOrig(type);
                        if (!ReferenceEquals(g, kept)) c[442]++;
                        return g;
                    }
                    return (MetaAutonomyTuning)kept;
                }
                c[441]++;
                MetaAutonomyTuning res = TuningOrig(type);
                sTuningMemo.Store((int)type, res);
                return res;
            }
            public static MetaAutonomyTuning TuningOrig(MetaAutonomyVenueType type) { return null; } // receives MetaAutonomyManager.GetTuning's original header
        }

        public class VenueScoreHook
        {
            public float ScoreHook(MetaAutonomyVenueType type, Sim sim, float hours24)
            {
                if (!sVenueScoreReady || OnWorker() || !ShortcutOn("venuememo")) return ScoreOrig(type, sim, hours24);
                Lot lot = (Lot)(object)this;
                int[] c = PatchCounters();
                VmLot f = VmFns(lot);
                if (f == null) { c[445]++; return ScoreOrig(type, sim, hours24); }
                if (VerifyNow("venuememoverify"))
                {
                    c[447]++;
                    float mine = VmScore(lot, f, type, sim, hours24);
                    float g = ScoreOrig(type, sim, hours24);
                    if (!TuningMemo.SameFloat(mine, g)) c[446]++;
                    return g;
                }
                c[444]++;
                return VmScore(lot, f, type, sim, hours24);
            }
            public float ScoreOrig(MetaAutonomyVenueType type, Sim sim, float hours24) { return 0f; } // receives Lot.HowMuchItWantsSimToCome(MetaAutonomyVenueType, Sim, float)'s original header
        }
    }
}
