using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // upgradefailalarm (tuning kUpgradeFailAlarm, off by default; docs/567 §1). EA bug fix in Handiness upgrades
    // (UpgradableComponent).
    //
    // [verified, decompiled] StartUpgrade decides the outcome up front. For a failure (Failure, BadFailure, Shocked) it
    // arms an alarm on the object, "Failure Upgrade Alarm", that adds ExitReason.CanceledByScript to the upgrading Sim
    // when the failure time is up, and throws the handle away. When the attempt ends earlier (the player cancels it, a
    // need or another push interrupts it), GetUpgradeResult (via StopUpgrade, or directly in JuiceKeg / WaterTrough)
    // books the progress but nothing removes the alarm. It fires later and cancels whatever that Sim is doing then:
    // eating, sleeping, a new upgrade attempt (a retry that would succeed is cut off and only part-credited), any of the
    // ~70 upgrade interactions.
    // Fix: StartUpgrade notes the alarm handles it added to the object (EA's AlarmManager index, before/after); when the
    // attempt is booked (GetUpgradeResult) or a new attempt starts on the same object, a noted alarm that is still
    // pending is removed with EA's AlarmManager.RemoveAlarm. An attempt that ran its full failure time (the alarm already
    // fired) is unchanged.
    //
    // Counters: [2550] pending failure alarms removed when the attempt was booked, [2551] removed at a restart,
    // [2552] errors. Check: UpgradeFailAlarmChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sUfaIndex = typeof(AlarmManager).GetField("mGameObjectIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Dictionary<UpgradableComponent, List<KeyValuePair<AlarmManager, AlarmHandle>>> sUfaPending = new Dictionary<UpgradableComponent, List<KeyValuePair<AlarmManager, AlarmHandle>>>();

        static void ApplyUpgradeFailAlarm()
        {
            if (sUfaIndex == null)
            {
                AppliedLog().Add("upgradefailalarm: AlarmManager index not found, not patched");
                return;
            }
            Patch(typeof(UpgradableComponent), "StartUpgrade", typeof(UfaHook), "StartHook", "StartOrig");
            Patch(typeof(UpgradableComponent), "GetUpgradeResult", typeof(UfaHook), "ResultHook", "ResultOrig");
        }

        // ---- decision (also used by the check): remove a noted failure alarm? ----
        internal static bool UfaRemove(bool on, bool attemptEnded, bool alarmPending)
        {
            return on && attemptEnded && alarmPending;
        }

        static List<AlarmManager> UfaManagers(GameObject owner)
        {
            List<AlarmManager> l = new List<AlarmManager>();
            if (AlarmManager.Global != null) l.Add(AlarmManager.Global);
            if (owner != null && owner.LotCurrent != null && owner.LotCurrent.AlarmManager != null && owner.LotCurrent.AlarmManager != AlarmManager.Global) l.Add(owner.LotCurrent.AlarmManager);
            return l;
        }

        static List<KeyValuePair<AlarmManager, AlarmHandle>> UfaHandles(GameObject owner)
        {
            List<KeyValuePair<AlarmManager, AlarmHandle>> r = new List<KeyValuePair<AlarmManager, AlarmHandle>>();
            if (owner == null) return r;
            foreach (AlarmManager m in UfaManagers(owner))
            {
                IDictionary idx = sUfaIndex.GetValue(m) as IDictionary;
                if (idx == null || !idx.Contains(owner)) continue;
                IList hs = idx[owner] as IList;
                if (hs == null) continue;
                foreach (object h in hs) r.Add(new KeyValuePair<AlarmManager, AlarmHandle>(m, (AlarmHandle)h));
            }
            return r;
        }

        // Removes the alarms noted for comp that are still pending; returns how many.
        static int UfaDrop(UpgradableComponent comp)
        {
            List<KeyValuePair<AlarmManager, AlarmHandle>> noted;
            if (!sUfaPending.TryGetValue(comp, out noted)) return 0;
            sUfaPending.Remove(comp);
            int n = 0;
            foreach (KeyValuePair<AlarmManager, AlarmHandle> a in noted)
            {
                if (!UfaRemove(true, true, a.Key.GetTimeLeft(a.Value, TimeUnit.Minutes) > 0f)) continue;
                a.Key.RemoveAlarm(a.Value);
                n++;
            }
            return n;
        }

        public class UfaHook
        {
            public UpgradeResult StartHook(Upgrade goalUpgrade, InteractionInstance inst, Handiness.UpgradeTuning tuning)
            {
                if (!ShortcutOn("upgradefailalarm")) return StartOrig(goalUpgrade, inst, tuning);
                object st = this;
                UpgradableComponent comp = (UpgradableComponent)st;
                List<KeyValuePair<AlarmManager, AlarmHandle>> before = null;
                try
                {
                    PatchCounters()[2551] += UfaDrop(comp);
                    before = UfaHandles(comp.Owner);
                }
                catch (Exception) { PatchCounters()[2552]++; }
                UpgradeResult r = StartOrig(goalUpgrade, inst, tuning);
                try
                {
                    if (before != null && r != UpgradeResult.Success)
                    {
                        List<KeyValuePair<AlarmManager, AlarmHandle>> added = new List<KeyValuePair<AlarmManager, AlarmHandle>>();
                        foreach (KeyValuePair<AlarmManager, AlarmHandle> a in UfaHandles(comp.Owner))
                            if (!before.Contains(a)) added.Add(a);
                        if (added.Count > 0) sUfaPending[comp] = added;
                    }
                }
                catch (Exception) { PatchCounters()[2552]++; }
                return r;
            }
            public UpgradeResult StartOrig(Upgrade goalUpgrade, InteractionInstance inst, Handiness.UpgradeTuning tuning) { return UpgradeResult.Cancelled; } // receives UpgradableComponent.StartUpgrade's original body

            public UpgradeResult ResultHook(Handiness.UpgradeTuning tuning, Sim s)
            {
                if (ShortcutOn("upgradefailalarm"))
                {
                    try
                    {
                        object st = this;
                        PatchCounters()[2550] += UfaDrop((UpgradableComponent)st);
                    }
                    catch (Exception) { PatchCounters()[2552]++; }
                }
                return ResultOrig(tuning, s);
            }
            public UpgradeResult ResultOrig(Handiness.UpgradeTuning tuning, Sim s) { return UpgradeResult.Cancelled; } // receives UpgradableComponent.GetUpgradeResult's original body
        }
    }
}
