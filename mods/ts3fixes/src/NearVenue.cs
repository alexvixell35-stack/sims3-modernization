using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // ---- nearvenue (ON 2026-11-20; townies only) ----
        // How EA picks the lot of a community visit [verified, IL]: Autonomy.ScoreInteractionsForMetaAutonomy scores every lot it
        // knows through the lot's MetaAds -> CalculateScoreAndAddToCandidates -> MetaInteractionScoredByVenue`3.CalculateScore
        // = IMetaObject.HowMuchItWantsSimToCome(sim, hour of day) (times a celebrity factor) -> BestScores picks among the best.
        // For a Lot that score (Autonomy.ScoreMetaObject: chance once a day, Sims there or going, Sims wanted at this hour, rain,
        // resort) has no distance term, so a townie crosses the map, or the water, for a venue type that is also next door.
        // nearvenue multiplies Lot.HowMuchItWantsSimToCome for a community lot by a gravity factor (TripStageCore.Weight / Factor):
        // weight 1/(d + water penalty)^2 against the other community lots of the same venue type (Lot.GetMetaAutonomyType) that
        // pass EA's own open test (VisitCommunityLot.SharedVisitVenueTests: hours, closed resorts, ...), normalised so lots EA
        // scores alike keep their summed score. Earliest clean point: the score, before the pick, any interaction instance, lot
        // reference count (NumSimsGoingToOrAtThisLot) or route. Its only game caller is that CalculateScore (VisitCommunityLot,
        // VisitCommunityLotWith, GoToCommunityLot on community lots; VisitLot / VisitLotWithDate target homes and are left alone).
        // Household, selected and no-household Sims, Sims with a role and Sims in a situation keep EA's score; so does a lot not
        // in its group (closed now, the active household's lot, no venue type). On the AutShadow worker a score this would change
        // aborts the worker (the sim thread scores it): the flood-colour asks and caches here are sim-thread only.
        // Counters 1431 community-lot scores asked (switch on, score > 0), 1432 lowered, 1433 raised, 1434 kept, 1435 Sim out of
        // scope, 1436 lot with no venue type, 1437 group lots across water (penalised), 1438 Sim colour unknown (no water
        // penalty), 1439 worker aborts, 1440 groups built; docs/321 H3: 1523 groups with more than kNvGroupMax open lots (the
        // nearest kNvGroupMax kept), 1524 most open lots of one venue type seen in a group (high water).
        static void ApplyNearVenue()
        {
            Patch(typeof(Lot), "HowMuchItWantsSimToCome", new Type[] { typeof(Sim), typeof(float) }, typeof(NvLotHook), "WantHook", "WantOrig");
        }

        // plain class (Lot's constructors are not ours to call); `this` is the Lot at run time
        public class NvLotHook
        {
            public float WantHook(Sim sim, float hours24)
            {
                float s = WantOrig(sim, hours24);
                if (!(s > 0f)) return s;
                // the AutShadow worker reads one bool, never the switch table (the sim thread may be adding a key to it)
                if (OnWorker()) { if (sNvOn) NvWorker(sim, (Lot)(object)this); return s; }
                if (!(sNvOn = ShortcutOn("nearvenue"))) return s;
                return s * NvFactor(sim, (Lot)(object)this);
            }
            public float WantOrig(Sim sim, float hours24) { return 0f; } // receives Lot.HowMuchItWantsSimToCome(Sim, float)'s original header
        }

        sealed class NvGroup
        {
            public ulong Sim; public long Tick; public int Type; public float Sum; public int N;
            public readonly Dictionary<ulong, float> W = new Dictionary<ulong, float>();
        }
        // one Sim's autonomy pass asks a few venue types in one tick: a small ring, reused
        const int kNvRing = 4;
        static readonly NvGroup[] sNvRing = new NvGroup[kNvRing];
        static int sNvNext;
        // the open lots of one group walk (sim thread only), grown at use: a world has as many community lots as it has
        static ulong[] sNvIds = new ulong[64];
        static float[] sNvW = new float[64];
        static bool[] sNvKeep = new bool[64];
        const int kCNvOverCap = 1523, kCNvGroupHw = 1524;

        static float NvFactor(Sim sim, Lot lot)
        {
            try
            {
                if (sim == null || lot == null || lot.IsWorldLot || !lot.IsCommunityLot) return 1f;
                int[] c = PatchCounters();
                c[1431]++;
                if (NvOutOfScope(sim)) { c[1435]++; return 1f; }
                int type = (int)lot.GetMetaAutonomyType;
                if (type == 0) { c[1436]++; return 1f; }
                NvGroup g = NvGroupOf(sim, type);
                float w;
                if (!g.W.TryGetValue(lot.LotId, out w)) { c[1434]++; return 1f; }
                float f = TripStageCore.Factor(w, g.Sum, g.N);
                c[f < 1f ? 1432 : f > 1f ? 1433 : 1434]++;
                if (f != 1f)
                    TsLog(1, "nearvenue sim " + sim.ObjectId.Value + " lot " + lot.LotId + " type " + type + " d " + (int)(lot.EntryPoint() - sim.Position).Length()
                        + " m, group " + g.N + ", factor " + f.ToString("0.00"));
                return f;
            }
            catch (Exception) { return 1f; }  // on any doubt EA's score
        }

        // sim-thread copy of the switch, refreshed by every sim-thread lot score; a stale true costs one aborted worker pass
        static volatile bool sNvOn;
        // on the worker: a score nearvenue would change goes back to the sim thread (reason: a call the worker cannot make); the
        // worker's answer is never scaled, so a worker pass and its sim-thread audit disagree only in the moment the switch flips on
        static void NvWorker(Sim sim, Lot lot)
        {
            try
            {
                if (sim == null || lot == null || lot.IsWorldLot || !lot.IsCommunityLot || NvOutOfScope(sim)) return;
                PatchCounters()[1439]++;
                AutShadowNative.Abort(kReasonModType);
            }
            catch (Exception) { AutShadowNative.Abort(kReasonModType); }  // unsure on the worker: let the sim thread score it
        }

        // the household rule plus Sims whose visits are driven by something else (a role, a situation)
        static bool NvOutOfScope(Sim sim)
        {
            if (IsPlayerSim(sim) || sim.Household == null) return true;
            if (sim.SimDescription.AssignedRole != null) return true;
            Sims3.Gameplay.Autonomy.Autonomy aut = sim.Autonomy;
            return aut != null && aut.SituationComponent != null && aut.SituationComponent.Situations.Count > 0;
        }

        static NvGroup NvGroupOf(Sim sim, int type)
        {
            ulong id = sim.ObjectId.Value;
            long now = SimClock.CurrentTicks;
            foreach (NvGroup x in sNvRing) if (x != null && x.Sim == id && x.Tick == now && x.Type == type) return x;
            NvGroup g = sNvRing[sNvNext];
            if (g == null) sNvRing[sNvNext] = g = new NvGroup();
            sNvNext = (sNvNext + 1) % kNvRing;
            g.Sim = id; g.Tick = now; g.Type = type; g.Sum = 0f; g.N = 0;
            g.W.Clear();
            int[] c = PatchCounters();
            c[1440]++;
            Vector3 at = sim.Position;
            bool colour = UfColourA(at, sim.LotCurrent);  // A = the Sim's colour; a lot proven in another one has no land path
            if (!colour) c[1438]++;
            Lot home = Household.ActiveHouseholdLot;
            int n = 0;
            foreach (object o in LotManager.AllLots)
            {
                Lot l = o as Lot;
                if (l == null || l.IsWorldLot || !l.IsCommunityLot || l == home || (int)l.GetMetaAutonomyType != type) continue;
                GreyedOutTooltipCallback tip = null;
                if (!Sims3.Gameplay.Core.VisitCommunityLot.SharedVisitVenueTests(sim, l, ref tip)) continue;  // open now, by EA's rules
                Vector3 e = l.EntryPoint();
                bool water = colour && UfColourB(e, l) < 0;
                if (water) c[1437]++;
                if (n == sNvIds.Length)
                {
                    Array.Resize(ref sNvIds, n * 2); Array.Resize(ref sNvW, n * 2); Array.Resize(ref sNvKeep, n * 2);
                }
                sNvIds[n] = l.LotId;
                sNvW[n++] = TripStageCore.Weight((e - at).Length(), water);
            }
            // docs/321 H3: the nearest kNvGroupMax, not the first in AllLots order; at or under the cap every open lot, as before
            if (n > TripStageCore.kNvGroupMax) c[kCNvOverCap]++;
            CapHw(kCNvGroupHw, n);
            TripStageCore.KeepNearest(sNvW, n, TripStageCore.kNvGroupMax, sNvKeep);
            for (int i = 0; i < n; i++)
            {
                if (!sNvKeep[i]) continue;
                g.W[sNvIds[i]] = sNvW[i];
                g.Sum += sNvW[i];
                g.N++;
            }
            return g;
        }
    }
}
