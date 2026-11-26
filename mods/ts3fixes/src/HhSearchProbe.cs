using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using AutonomyT = Sims3.Gameplay.Autonomy.Autonomy;

namespace Ts3Fixes
{
    // hhsearchprobe (docs/276 §2.3 A, docs/277 §5; measurement only, default off; test runs turn it on): why SV runs far more
    // household autonomy searches per second than BP. Read-only for every Sim: household Sims are only counted.
    // - AutQueueHook (AutonomyManager.Add, AutLive.cs): why the Sim was enqueued, from the fields Sim.DoInteraction's idle branch
    //   just read (IsRunningLocalAutonomy as CanRunAutonomyImmediately left it, mLastInteractionWasAutonomous / Succeeded,
    //   TimeSinceInteractionQueueBecameEmpty), plus the previous pick + EA's success flag. ShouldRunLocalAutonomy is NOT called
    //   (its getter writes IsRunningLocalAutonomy).
    // - AutPushHook (Autonomy.ConsiderPerformingAutonomousAction, AutLive.cs): outcome, queue head before the push, scope, lot.
    // - L0Close (AutLocalProbe.cs, with autt2l0 on): the pair count of the same household search.
    // Household class = AutLocalProbeCore.Class (household or unknown scope), the same split as the autt2l0 L0 probe. Per-Sim
    // lines (top 6 by count) in AppDomain data "ts3fixes.hhsearch" (agent key hhsearch_sims), rebuilt every 64 household searches.
    public static partial class Fixes
    {
        sealed class HhPending { public int Reason; public string Prev; }
        static readonly Dictionary<ulong, HhSearchRec> sHhRecs = new Dictionary<ulong, HhSearchRec>();
        static readonly Dictionary<ulong, ulong> sHhDesc = new Dictionary<ulong, ulong>();
        static readonly Dictionary<ulong, HhPending> sHhPending = new Dictionary<ulong, HhPending>();
        static int sHhNotes;
        static AutonomyT sHhPairsAut;  // the last household L0 search and its pair count (AutLocalProbe.cs L0Close)
        static int sHhPairs;
        static FieldInfo fHhLastOk, fHhLastAuto;
        static bool sHhFieldsTried;
        const int kHhMaxSims = 64;
        static readonly CommodityKind[] kHhMotives = { CommodityKind.Hunger, CommodityKind.Energy, CommodityKind.Bladder, CommodityKind.Hygiene, CommodityKind.Social, CommodityKind.Fun };

        // AutonomyManager.Add: the enqueue reason, kept until that Sim's next search end
        static void HhSearchEnqueued(AutonomyT a)
        {
            try
            {
                Sim s = a.Actor;
                if (s == null || AutLocalProbeCore.Class(SafeUfWhy(s)) != AutLocalProbeCore.kHh) return;
                if (!sHhFieldsTried)
                {
                    sHhFieldsTried = true;
                    const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
                    fHhLastOk = typeof(Sim).GetField("mLastInteractionSucceeded", inst);
                    fHhLastAuto = typeof(Sim).GetField("mLastInteractionWasAutonomous", inst);
                }
                if (fHhLastOk == null || fHhLastAuto == null) { PatchCounters()[HhSearchRec.kExc]++; return; }
                bool ok = (bool)fHhLastOk.GetValue(s), auto = (bool)fHhLastAuto.GetValue(s);
                int reason = HhSearchRec.Reason(a.IsRunningLocalAutonomy, s.Conversation != null && s.IsActiveSim && !auto, auto, ok, s.IsSelectable,
                    a.TimeSinceInteractionQueueBecameEmpty);
                HhSearchRec rec;
                string prev = (auto && sHhRecs.TryGetValue(s.ObjectId.Value, out rec) && rec.LastDef != null ? rec.LastDef : "user/other") + (ok ? "|ok" : "|fail");
                if (sHhPending.Count > 256) sHhPending.Clear();  // Sims destroyed while queued
                HhPending p = new HhPending(); p.Reason = reason; p.Prev = prev;
                sHhPending[s.ObjectId.Value] = p;
            }
            catch (Exception) { PatchCounters()[HhSearchRec.kExc]++; }
        }

        // L0Close, household class: remember the pair count for the push note of the same search
        static void HhSearchPairs(AutonomyT a, int pairs) { sHhPairsAut = a; sHhPairs = pairs; }

        // queue head before the push decision: who holds the Sim when a pick is not queued
        static int HhBusyKind(AutonomyT a)
        {
            try
            {
                Sim s = a.Actor;
                InteractionInstance head = s != null && s.InteractionQueue != null ? s.InteractionQueue.GetHeadInteraction() : null;
                return head == null ? HhSearchRec.kBusyNone : head.Autonomous ? HhSearchRec.kBusyAutoHead : HhSearchRec.kBusyUserHead;
            }
            catch (Exception) { PatchCounters()[HhSearchRec.kExc]++; return HhSearchRec.kBusyNone; }
        }

        static string HhLowestMotive(Sim s)
        {
            if (s.Motives == null) return null;
            string name = null; float low = float.MaxValue;
            foreach (CommodityKind k in kHhMotives)
            {
                if (!s.Motives.HasMotive(k)) continue;
                float v = s.Motives.GetValue(k);
                if (v < low) { low = v; name = k.ToString(); }
            }
            // value bucketed to 25 so a few keys cover the run
            return name == null ? null : name + "<=" + (int)(Math.Ceiling(low / 25f) * 25f);
        }

        static void HhSearchNote(AutonomyT a, InteractionInstance best, bool pushed, int busy)
        {
            int[] c = PatchCounters();
            try
            {
                Sim s = a.Actor;
                int why = SafeUfWhy(s);
                if (s == null || AutLocalProbeCore.Class(why) != AutLocalProbeCore.kHh) return;
                ulong id = s.ObjectId.Value;
                HhSearchRec rec;
                if (!sHhRecs.TryGetValue(id, out rec))
                {
                    if (sHhRecs.Count >= kHhMaxSims) return;
                    SimDescription d = s.SimDescription;
                    rec = new HhSearchRec(d != null ? d.FirstName : id.ToString("x"), d != null && d.IsPet, s.IsSelectable);
                    sHhRecs[id] = rec;
                    sHhDesc[id] = d != null ? d.SimDescriptionId : 0UL;
                    c[HhSearchRec.kSims]++;
                    if (rec.Pet) c[HhSearchRec.kPets]++;
                }
                HhPending p;
                int reason = HhSearchRec.kRNone; string prev = null;
                if (sHhPending.TryGetValue(id, out p)) { reason = p.Reason; prev = p.Prev; sHhPending.Remove(id); }
                if (ReferenceEquals(sHhPairsAut, a)) { c[HhSearchRec.kPairN]++; c[HhSearchRec.kPairs] += sHhPairs; sHhPairsAut = null; }
                string def = best != null && best.InteractionDefinition != null ? DefKey(best.InteractionDefinition.GetType()) : null;
                Lot lot = s.LotCurrent;
                Household hh = Household.ActiveHousehold;
                bool activeLot = lot != null && hh != null && hh.LotHome == lot;
                rec.Note(c, HhSearchRec.Scope(why), a.IsRunningLocalAutonomy, def, pushed, busy, reason, prev, def == null ? HhLowestMotive(s) : null,
                    lot != null ? lot.LotId : 0UL, s.Level, activeLot, System.Environment.TickCount, SimClock.ElapsedTime(TimeUnit.Minutes));
                if (++sHhNotes % 64 == 0) HhPublish();
            }
            catch (Exception) { c[HhSearchRec.kExc]++; }
        }

        static void HhPublish()
        {
            List<KeyValuePair<ulong, HhSearchRec>> all = new List<KeyValuePair<ulong, HhSearchRec>>(sHhRecs);
            all.Sort(delegate (KeyValuePair<ulong, HhSearchRec> x, KeyValuePair<ulong, HhSearchRec> y) { return y.Value.N.CompareTo(x.Value.N); });
            List<object> lines = new List<object>(6);
            for (int i = 0; i < all.Count && i < 6; i++) { ulong d; sHhDesc.TryGetValue(all[i].Key, out d); lines.Add(all[i].Value.Line(d)); }
            AppDomain.CurrentDomain.SetData("ts3fixes.hhsearch", lines);
        }
    }
}
