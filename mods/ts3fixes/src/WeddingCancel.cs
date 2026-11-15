using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // weddingcancel (tuning kWeddingCancel, off by default; docs/563 §1). EA bug fixes, exact for every Sim.
    //
    // WeddingCeremony [verified, decompiled]: every wedding outside a party (the "Get Married" social, the arch, resort
    // weddings) runs this root situation. ExchangeRings arms a 1-hour alarm, ImpatienceAtNoRingsExchanged, which (rings
    // not exchanged: the forced Exchange Rings failed or was cancelled) sets WeddingCanceled(parent, jilter: null).
    // (a) WeddingCanceled.Init returns at once when the jilter is null, before arming its only way out (the
    //     sWaitTimeAfterWeddingCanceled alarm that calls EndOfCeremony -> Exit). Nothing else ends the ceremony unless a
    //     wedding party ends, a ring Sim dies or is deleted. So the couple keep the ceremony role for good, and the stale
    //     ceremony is the first WeddingCeremony GetSituationOfType returns: on the next arch wedding, WeddingArch
    //     RouteToWeddingArchSlot sees its child is not RouteToWeddingArch and fails, and the new ceremony exits. The couple
    //     can no longer marry under an arch. Fix: after EA's Init with no jilter and no alarm, the same end alarm is armed
    //     (EA's tunable, EA's callback, stored in mAlarmHandle so CleanUp removes it).
    // (b) With a jilter, Init means to give every audience member who likes the jilted Sim an Insulting short-term context
    //     toward the jilter, but it calls Relationship.Get(jilter, guest, true), drops the result and sets the STC on the
    //     jilter/jilted relationship again. Fix: after EA's Init, the STC is set on the jilter/guest relationship for the
    //     same guests (valid audience, positive LTR with the jilted Sim), with EA's commodity and value.
    // Counters: [2505] end alarms armed, [2506] guest insults set, [2507] errors. Check: WeddingCancelChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sWcJilter = typeof(WeddingCeremony.WeddingCanceled).GetField("mJilter", kAnyAll);
        static readonly FieldInfo sWcAlarm = typeof(WeddingCeremony.WeddingCanceled).GetField("mAlarmHandle", kAnyAll);
        static readonly FieldInfo sWcWait = typeof(WeddingCeremony).GetField("sWaitTimeAfterWeddingCanceled", kAnyAll);
        static readonly MethodInfo sWcEnd = typeof(WeddingCeremony.WeddingCanceled).GetMethod("EndOfCeremony", kAnyAll, null, Type.EmptyTypes, null);

        static void ApplyWeddingCancel()
        {
            if (sWcJilter == null || sWcAlarm == null || sWcWait == null || sWcEnd == null)
            {
                AppliedLog().Add("weddingcancel: WeddingCanceled members not found, not patched");
                return;
            }
            Patch(typeof(WeddingCeremony.WeddingCanceled), "Init", typeof(WcHook), "InitHook", "InitOrig");
        }

        // ---- decisions (also used by the check) ----
        // (a) arm EA's end alarm: the impatience cancel (no jilter) left the state with no alarm
        internal static bool WcEndAlarm(bool on, bool jilterSet, bool alarmSet)
        {
            return on && !jilterSet && !alarmSet;
        }
        // (b) insult the jilter in this guest's eyes: a jilter, a valid guest (not one of the couple) who likes the jilted Sim
        internal static bool WcInsultGuest(bool on, bool jilterSet, bool validGuest, bool likesJilted)
        {
            return on && jilterSet && validGuest && likesJilted;
        }

        public class WcHook
        {
            public void InitHook(WeddingCeremony parent)
            {
                InitOrig(parent);
                if (!ShortcutOn("weddingcancel")) return;  // fast path: switch off
                try
                {
                    object st = this;
                    Sim jilter = sWcJilter.GetValue(st) as Sim;
                    if (jilter == null)
                    {
                        AlarmHandle h = (AlarmHandle)sWcAlarm.GetValue(st);
                        if (parent == null || parent.RingGiver == null) return;
                        if (!WcEndAlarm(true, false, h != AlarmHandle.kInvalidHandle)) return;
                        AlarmTimerCallback cb = (AlarmTimerCallback)Delegate.CreateDelegate(typeof(AlarmTimerCallback), st, sWcEnd);
                        float wait = (float)sWcWait.GetValue(null);
                        sWcAlarm.SetValue(st, AlarmManager.Global.AddAlarm(wait, TimeUnit.Hours, cb, "Wait For Wedding Ceremoney to Finish", AlarmType.AlwaysPersisted, parent.RingGiver));
                        PatchCounters()[2505]++;
                        return;
                    }
                    if (parent == null || parent.Audience == null || parent.RingGiver == null || parent.RingReceiver == null) return;
                    Sim jilted = jilter != parent.RingGiver ? parent.RingGiver : parent.RingReceiver;
                    foreach (Sim guest in parent.Audience.ToArray())
                    {
                        bool valid = guest != null && guest != jilter && guest != jilted && parent.IsAudienceMemberValid(guest);
                        Relationship toJilted = valid ? Relationship.Get(jilted, guest, false) : null;
                        if (!WcInsultGuest(true, true, valid, toJilted != null && toJilted.LTR != null && toJilted.LTR.IsPositive)) continue;
                        Relationship toJilter = Relationship.Get(jilter, guest, true);
                        if (toJilter == null || toJilter.STC == null) continue;
                        toJilter.STC.Set(jilter, guest, CommodityTypes.Insulting, 150f);
                        PatchCounters()[2506]++;
                    }
                }
                catch (Exception) { PatchCounters()[2507]++; }
            }
            public void InitOrig(WeddingCeremony parent) { } // receives WeddingCeremony.WeddingCanceled.Init's original body
        }
    }
}
