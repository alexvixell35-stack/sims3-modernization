using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // synctime (test switch, hooks only with kHitchCauseHooks; docs/546 §9): how long Sims stand locked waiting for a sync
    // partner. Sim.WaitForSynchronizationLevelWithSim [verified, decompiled] loops with Simulator.Sleep(0) until both Sims
    // reach the level, giving up after giveupTime sim minutes (StartSync: kNumMinToWaitOnSyncStart 30, after up to
    // kNumMinToWaitOnPreSync 15 in StartSync's own loop; WaitForSyncComplete: kNumMinToWaitOnSync 15); with
    // performSocializeWithTest it runs SocialInteractionA.Definition.CanSocializeWithSyncCheck every tick of the wait.
    // Logs each wait that took >= 1 sim minute or failed: actor (HH = household), partner, level, result, sim minutes,
    // social checks run during the wait and their total time, the actor's interaction; failed waits add the partner's sync
    // state, social route flags, its last ReceiveSocialInteraction step and its own wait.
    // The wait and receive hooks themselves live in SocialReceiveFail.cs (shared with that fix); this file holds the
    // test-only hooks (social check timing, receiver steps) and the timing/formatting helpers they call.
    public static partial class Fixes
    {
        class SwStat { public int Checks; public uint CheckUs; public int Call; public Sim.SyncLevel Level; public Sim Target; public long T0; public string In; }
        // Sims inside a wait, compared by reference (a Sim's hash and equality can change once it is destroyed); entries
        // left by a task that died mid-wait (DestroyObject on a sleeping task skips finally) are dropped after 2000 calls
        static List<Sim> sSwSims = new List<Sim>();
        static List<SwStat> sSwStats = new List<SwStat>();
        static int sSwLines, sSwCalls, sSwErrors;

        static int SwFind(Sim s) { for (int i = 0; i < sSwSims.Count; i++) if (object.ReferenceEquals(sSwSims[i], s)) return i; return -1; }

        static bool ApplySyncWaitLog()
        {
            bool ok = PatchSyncWaitCore();  // wait + receive hooks, shared with socialreceivefail (SocialReceiveFail.cs)
            ok &= Patch(typeof(SocialInteractionA.Definition), "CanSocializeWithSyncCheck", new Type[] { typeof(string), typeof(Sim), typeof(Sim), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType(), typeof(bool), typeof(bool) }, typeof(SwCheckHook), "CheckHook", "CheckOrig");
            // which step of ReceiveSocialInteraction a receiver is in (shown on failed waits as "partner step")
            Type si = typeof(SocialInteraction);
            Type[] three = { typeof(Sim), typeof(Sim), si };
            ok &= Patch(si, "SimsNeedToRoute", new Type[] { typeof(Sim), typeof(Sim) }, typeof(SwStepHook), "NeedHook", "NeedOrig");
            ok &= Patch(si, "SetupTwoPersonSocial", three, typeof(SwStepHook), "TwoHook", "TwoOrig");
            ok &= Patch(si, "SetupGroupSocial", three, typeof(SwStepHook), "GroupHook", "GroupOrig");
            ok &= Patch(si, "PlaceSocial", three, typeof(SwStepHook), "PlaceHook", "PlaceOrig");
            return ok;
        }

        // ---- receiver steps (test) ----

        // last ReceiveSocialInteraction step per Sim, by reference; oldest dropped past 200 Sims
        static List<Sim> sStSims = new List<Sim>();
        static List<string> sStStep = new List<string>();
        static List<long> sStT0 = new List<long>();
        static void SwStep(object ii, string step)
        {
            try
            {
                if (!ShortcutOn("synctime")) return;
                Sim a = ((InteractionInstance)ii).InstanceActor;
                if (a == null) return;
                int at = -1;
                for (int i = 0; i < sStSims.Count; i++) if (object.ReferenceEquals(sStSims[i], a)) { at = i; break; }
                if (at < 0) { if (sStSims.Count >= 200) { sStSims.RemoveAt(0); sStStep.RemoveAt(0); sStT0.RemoveAt(0); } sStSims.Add(a); sStStep.Add(step); sStT0.Add(SimClock.CurrentTicks); }
                else { sStStep[at] = step; sStT0[at] = SimClock.CurrentTicks; }
            }
            catch (Exception) { sSwErrors++; }
        }
        static string SwStepOf(Sim p)
        {
            for (int i = 0; i < sStSims.Count; i++)
                if (object.ReferenceEquals(sStSims[i], p))
                    return ", partner step " + sStStep[i] + " " + SimClock.ConvertFromTicks(SimClock.CurrentTicks - sStT0[i], TimeUnit.Minutes).ToString("0.0") + " min ago";
            return ", partner step none";
        }

        public class SwStepHook
        {
            public bool NeedHook(Sim a, Sim b) { SwStep(this, "needroute>"); bool r = NeedOrig(a, b); SwStep(this, r ? "needroute<yes" : "needroute<no"); return r; }
            public bool NeedOrig(Sim a, Sim b) { return false; }
            public bool TwoHook(Sim a, Sim b, SocialInteraction s) { SwStep(this, "twoperson>"); bool r = false; try { r = TwoOrig(a, b, s); return r; } finally { SwStep(this, r ? "twoperson<ok" : "twoperson<fail"); } }
            public bool TwoOrig(Sim a, Sim b, SocialInteraction s) { return false; }
            public bool GroupHook(Sim a, Sim b, SocialInteraction s) { SwStep(this, "group>"); bool r = false; try { r = GroupOrig(a, b, s); return r; } finally { SwStep(this, r ? "group<ok" : "group<fail"); } }
            public bool GroupOrig(Sim a, Sim b, SocialInteraction s) { return false; }
            public bool PlaceHook(Sim a, Sim b, SocialInteraction s) { SwStep(this, "place>"); bool r = false; try { r = PlaceOrig(a, b, s); return r; } finally { SwStep(this, r ? "place<ok" : "place<fail"); } }
            public bool PlaceOrig(Sim a, Sim b, SocialInteraction s) { return false; }
        }

        public class SwCheckHook
        {
            public static InteractionTestResult CheckHook(string social, Sim actor, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback, bool bAllowOnToddlers, bool interactionHasAlreadyStarted)
            {
                int at = actor != null ? SwFind(actor) : -1;
                if (at < 0) return CheckOrig(social, actor, target, isAutonomous, ref greyedOutTooltipCallback, bAllowOnToddlers, interactionHasAlreadyStarted);
                SwStat st = sSwStats[at];
                uint t0 = SrcNowUs();
                InteractionTestResult r = CheckOrig(social, actor, target, isAutonomous, ref greyedOutTooltipCallback, bAllowOnToddlers, interactionHasAlreadyStarted);
                st.Checks++; st.CheckUs += unchecked(SrcNowUs() - t0);
                return r;
            }
            public static InteractionTestResult CheckOrig(string social, Sim actor, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback, bool bAllowOnToddlers, bool interactionHasAlreadyStarted) { return InteractionTestResult.GenericUnknown; }
        }

        // ---- wait timing, called from SyncWaitHook (SocialReceiveFail.cs) ----

        // Starts timing a wait; null when synctime is off or this Sim is already inside a timed wait (nested).
        static SwStat SwBegin(Sim me, Sim target, Sim.SyncLevel level)
        {
            if (!ShortcutOn("synctime")) return null;
            try
            {
                int calls = ++sSwCalls;
                if (calls % 500 == 0) HcLine("syncwait heartbeat: " + calls + " waits, " + sSwLines + " logged, " + sSwSims.Count + " in progress, " + sSwErrors + " errors");
                for (int i = sSwStats.Count - 1; i >= 0; i--) if (calls - sSwStats[i].Call > 2000) { sSwSims.RemoveAt(i); sSwStats.RemoveAt(i); }
                if (SwFind(me) >= 0) return null;
                SwStat st = new SwStat();
                st.Call = calls; st.Level = level; st.Target = target; st.T0 = SimClock.CurrentTicks;
                st.In = me.CurrentInteraction != null ? me.CurrentInteraction.GetType().Name : "none";
                sSwSims.Add(me); sSwStats.Add(st);
                return st;
            }
            catch (Exception) { sSwErrors++; return null; }
        }

        // Ends a timed wait: drops it from the in-progress list and logs it if it took >= 1 sim minute or failed.
        static void SwFinish(Sim me, Sim target, Sim.SyncLevel level, float giveupTime, bool r, SwStat st)
        {
            try
            {
                int at = SwFind(me);
                if (at >= 0) { sSwSims.RemoveAt(at); sSwStats.RemoveAt(at); }
                float min = SimClock.ConvertFromTicks(SimClock.CurrentTicks - st.T0, TimeUnit.Minutes);
                if (!r && min >= giveupTime) SwCountTimeout(me, target, level, min);
                if ((min < 1f && r) || sSwLines >= 3000) return;
                sSwLines++;
                string res = r ? "ok" : (min >= giveupTime ? "timeout" : (me.HasExitReason(ExitReason.SynchronizationFailed) ? "failed" : "exit"));
                HcLine("syncwait: " + SwWho(me) + " -> " + SwWho(target) + ", " + level + ", " + res + ", " + min.ToString("0.0") + "/" + giveupTime.ToString("0") + " min, checks " + st.Checks + " " + (st.CheckUs / 1000) + " ms, in "
                    + SwIn(me) + ", partner in " + SwPartner(me, target) + (r ? "" : SwSyncState(me, target)) + ", " + HcSimTime());
            }
            catch (Exception) { sSwErrors++; }
        }

        // Counters that survive the native log caps (fix_counters in every snapshot): [2314] sync waits that ran to their
        // giveup time, [2315] their total sim minutes, [2316] of them the socialreceivefail case (RfClassify Parked: the
        // fix would have ended it); of the rest with an initiator waiting on a Started partner whose last receive failed,
        // why the fix did not apply: [2317] not linked to the partner's running SocialInteractionB, [2318] that B's
        // receive did not fail (the failure was an earlier social), [2319] partner moved on (level, target or Finished).
        // Classified at the timeout, so a wait the fix ends early is counted in 2312 instead.
        static void SwCountTimeout(Sim me, Sim target, Sim.SyncLevel level, float min)
        {
            int[] c = PatchCounters();
            c[2314]++; c[2315] += (int)min;
            if (target == null) return;
            RfCase k = RfClassify(me, target, level);
            if (k == RfCase.Parked) { c[2316]++; return; }
            if (me.SynchronizationRole != Sim.SyncRole.Initiator || target.SynchronizationLevel != Sim.SyncLevel.Started || SwStepOf(target).IndexOf("receive<fail") < 0) return;
            if (k == RfCase.NotLinked) c[2317]++;
            else if (k == RfCase.NotFailed || k == RfCase.NoList) c[2318]++;
            else if (k == RfCase.PartnerMoved) c[2319]++;
        }

        static void SwLogEndedEarly(Sim me, Sim target, Sim.SyncLevel level)
        {
            if (ShortcutOn("synctime")) HcLine("socialreceivefail: " + SwWho(me) + " -> " + SwWho(target) + ", " + level + ", ended at once (partner's receive failed), in " + SwIn(me) + ", " + HcSimTime());
        }

        static string SwIn(Sim s) { return s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "none"; }

        // Failed waits only: why IsAtSynchronizationLevelWith stayed false (partner level/role/target/sleeping) and the
        // social route handshake flags of both sides (SocialInteraction mbInitialRoute, mbApproachRoute, mbBRoutePlanned,
        // mbIsTurnToFace; private, read by reflection once per logged line).
        static string SwSyncState(Sim me, Sim p)
        {
            if (p == null) return "";
            string s = ", partner sync " + p.SynchronizationLevel + "/" + p.SynchronizationRole + (p.SynchronizationTarget == me ? " on me" : (p.SynchronizationTarget == null ? " on none" : " on " + SwWho(p.SynchronizationTarget)))
                + (p.SynchronizationSleeping ? " sleeping" : "") + ", own " + me.SynchronizationLevel + "/" + me.SynchronizationRole;
            s += SwFlags(" own", me.CurrentInteraction) + SwFlags(" partner", p.CurrentInteraction);
            s += SwStepOf(p);
            int at = SwFind(p);
            if (at < 0) s += ", partner not in a sync wait";
            else
            {
                SwStat ps = sSwStats[at];
                s += ", partner waiting in " + ps.In + " for " + (ps.Target == me ? "me" : SwWho(ps.Target)) + " " + ps.Level + " for "
                    + SimClock.ConvertFromTicks(SimClock.CurrentTicks - ps.T0, TimeUnit.Minutes).ToString("0.0") + " min";
            }
            return s;
        }
        static readonly string[] sSwFlagNames = { "mbInitialRoute", "mbApproachRoute", "mbBRoutePlanned", "mbIsTurnToFace" };
        static System.Reflection.FieldInfo[] sSwFlagFields;
        static string SwFlags(string who, object ii)
        {
            if (!(ii is SocialInteraction)) return "";
            if (sSwFlagFields == null)
            {
                sSwFlagFields = new System.Reflection.FieldInfo[sSwFlagNames.Length];
                for (int i = 0; i < sSwFlagNames.Length; i++)
                    sSwFlagFields[i] = typeof(SocialInteraction).GetField(sSwFlagNames[i], System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            }
            System.Text.StringBuilder b = new System.Text.StringBuilder(who + " flags ");
            foreach (System.Reflection.FieldInfo f in sSwFlagFields)
            {
                object v = f != null ? f.GetValue(ii) : null;
                b.Append(v is bool ? ((bool)v ? "1" : "0") : "?");
            }
            return b.ToString();
        }

        static string SwPartner(Sim me, Sim p)
        {
            try
            {
                if (p == null || p.HasBeenDestroyed) return "gone";
                InteractionInstance mine = me.CurrentInteraction, linked = mine != null ? mine.LinkedInteractionInstance : null;
                string where = linked == null ? "no link" : (p.CurrentInteraction == linked ? "linked running" : (p.InteractionQueue != null && p.InteractionQueue.HasInteraction(linked) ? "linked queued" : (p.InteractionQueue != null && p.InteractionQueue.TransitionInteraction == linked ? "linked in transition" : "linked gone")));
                return (p.CurrentInteraction != null ? p.CurrentInteraction.GetType().Name : "none") + " (" + where + (p.SimRoutingComponent != null && p.SimRoutingComponent.IsRouting ? ", routing" : "") + ")";
            }
            catch (Exception) { return "?"; }
        }

        static string SwWho(Sim s)
        {
            if (s == null) return "null";
            return (s.IsSelectable ? "HH " : "") + s.FirstName + (s.SimDescription != null && !s.SimDescription.IsHuman ? "(" + s.SimDescription.Species + ")" : "");
        }
    }
}
