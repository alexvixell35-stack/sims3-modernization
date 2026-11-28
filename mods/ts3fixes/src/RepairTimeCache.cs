using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;

namespace Ts3Fixes
{
    // repairtimecache (tuning kRepairTimeCache, off by default; docs/567 §2). EA bug fix in object repair
    // (RepairableComponent).
    //
    // [verified, decompiled] RepairableComponent caches the repair time in mRepairTime (persisted with the object).
    // GetRepairTime(actor) fills it from that actor's Handiness level (or 1 minute with the Instant Handiness buff) times
    // the share still to repair, and only when it is <= 0; StopRepair sets it back to -1. Every repair interaction calls
    // GetRepairTime when it starts (RepairableComponent.ConfigureStages, for its progress stage), before it routes and
    // before StartRepair. So whenever an attempt ends without StopRepair, the first Sim's time stays and the next Sim gets
    // it, for its stage and for StartRepair's progress math:
    //  - the repair interaction is cancelled or fails its route / in-use check before StartRepair (common: a queued
    //    repair cancelled, the object in use, another Sim got there first);
    //  - StartRepair electrocutes the Sim (returns false; TV, Stereo, Computer, ... never call StopRepair then);
    //  - the repairing Sim is reset.
    // A skill-0 Sim's long time sticks for a skill-10 Sim or the repairman (and the "remaining share" at that moment);
    // an Instant Handiness Sim's 1 minute sticks for a normal Sim.
    // Fix: GetRepairTime notes which Sim the cached time belongs to. When another Sim asks and the noted Sim is not
    // repairing this object right now (its current interaction does not target it), the stale cache is dropped (-1)
    // and EA's body computes it for the asking Sim. While the noted Sim is mid-repair, the asking Sim gets its own time
    // computed by EA's body and the cache is put back, so the running repair is untouched.
    //
    // Counters: [2553] stale times dropped, [2554] own time given while another Sim repairs, [2555] errors.
    // Check: RepairTimeCacheChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sRtcTime = typeof(RepairableComponent).GetField("mRepairTime", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Dictionary<RepairableComponent, Sim> sRtcWho = new Dictionary<RepairableComponent, Sim>();
        static int sRtcPruneAt = 256;  // next size that triggers a prune

        // Drops the notes of destroyed objects or Sims. A dropped note answers like a destroyed Sim's (not known), so when
        // the prune runs does not change any answer. The next prune waits until the table doubles past what is left (at
        // least 256), so a table of live notes is no longer rescanned on every call.
        static void RtcPrune()
        {
            List<RepairableComponent> gone = null;
            foreach (KeyValuePair<RepairableComponent, Sim> kv in sRtcWho)
                if (kv.Key.Owner == null || kv.Key.Owner.HasBeenDestroyed || kv.Value == null || kv.Value.HasBeenDestroyed) (gone ?? (gone = new List<RepairableComponent>())).Add(kv.Key);
            if (gone != null) foreach (RepairableComponent g in gone) sRtcWho.Remove(g);
            sRtcPruneAt = Math.Max(256, sRtcWho.Count * 2);
        }

        static void ApplyRepairTimeCache()
        {
            if (sRtcTime == null)
            {
                AppliedLog().Add("repairtimecache: RepairableComponent.mRepairTime not found, not patched");
                return;
            }
            Patch(typeof(RepairableComponent), "GetRepairTime", typeof(RtcHook), "TimeHook", "TimeOrig");
        }

        // ---- decision (also used by the check): 0 = EA's code, 1 = drop the stale cache, 2 = own time, cache kept ----
        internal static int RtcAction(bool on, bool cached, bool ownerKnown, bool sameSim, bool ownerRepairingNow)
        {
            if (!on || !cached || !ownerKnown || sameSim) return 0;
            return ownerRepairingNow ? 2 : 1;
        }

        public class RtcHook
        {
            public float TimeHook(IActor actor)
            {
                if (!ShortcutOn("repairtimecache")) return TimeOrig(actor);
                object st = this;
                RepairableComponent comp = (RepairableComponent)st;
                Sim who = null;
                float cur = 0f;
                int act = 0;
                try
                {
                    cur = (float)sRtcTime.GetValue(comp);
                    sRtcWho.TryGetValue(comp, out who);
                    bool known = who != null && !who.HasBeenDestroyed;
                    bool repairing = known && who.CurrentInteraction != null && who.CurrentInteraction.Target == comp.Owner;
                    act = RtcAction(true, cur > 0f, known, who == actor as Sim, repairing);
                    if (act != 0) sRtcTime.SetValue(comp, -1f);
                }
                catch (Exception) { PatchCounters()[2555]++; act = 0; }
                bool computed = cur <= 0f || act != 0;
                float r = TimeOrig(actor);
                try
                {
                    if (act == 2)
                    {
                        PatchCounters()[2554]++;
                        sRtcTime.SetValue(comp, cur);  // the running repair keeps its own time
                    }
                    else
                    {
                        if (act == 1) PatchCounters()[2553]++;
                        if (computed && actor is Sim) sRtcWho[comp] = (Sim)actor;
                    }
                    if (sRtcWho.Count > sRtcPruneAt) RtcPrune();
                }
                catch (Exception) { PatchCounters()[2555]++; }
                return r;
            }
            public float TimeOrig(IActor actor) { return 0f; } // receives RepairableComponent.GetRepairTime's original body
        }
    }
}
