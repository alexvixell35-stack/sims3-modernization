using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // socialreplanlog (tuning kSocialReplanLog, off by default; logging only; docs/577 §2 (b)/(c)). A receiver of a social
    // (SocialInteraction, SynchronizationRole Receiver) whose replan fails with its own sync partner as the blocker:
    // household Sims stood 30 sim-min in run 1146 (William 14 replans, Apollo 13, blocker = the initiator 0.1-0.2 m away).
    // Logs which ReceiveSocialInteraction branch built the route, the route's ignore list, both positions, the
    // initiator's route destination, and the receive's outcome, to prove the cause before any routing fix.
    //
    // Branch: the receiver's step trail from the shared step hooks (SyncWaitLog.cs SwStepHook, installed here too:
    // SimsNeedToRoute, SetupTwoPersonSocial, SetupGroupSocial, PlaceSocial), plus "jig" when the interaction holds a
    // SocialJig (that branch of ReceiveSocialInteraction calls none of them). The trail restarts at each "receive>".
    // Hook point: RouteFailLog.RflPlan (every plan) calls SrlReplan for failed replans; nothing new runs per plan when
    // the switch is off beyond one switch test.
    // Lines: "socialreplan: <receiver> -> <partner>, replan N <result>, branch <trail>, ignore [...], me at, partner at,
    // partner dest, ..." (first 3 replans of a receive) and "socialreplan done: ... N replans, ok|fail" at its end.
    // Capped at 300 lines per session. Counters: [3972] receiver replans blocked by the partner, [3973] lines dropped
    // by the cap. Check: SocialReplanLogChecks.cs.
    public static partial class Fixes
    {
        const int kSrlMaxLines = 300, kSrlDetailPerReceive = 3;
        class SrlState { public Sim Sim; public string Trail = ""; public int Replans; public string Partner; }
        static readonly List<SrlState> sSrl = new List<SrlState>();
        static int sSrlLines;

        static void ApplySocialReplanLog() { PatchSwSteps(); PatchSyncWaitCore(); }

        static SrlState SrlOf(Sim s, bool make)
        {
            for (int i = 0; i < sSrl.Count; i++) if (object.ReferenceEquals(sSrl[i].Sim, s)) return sSrl[i];
            if (!make) return null;
            if (sSrl.Count >= 64) sSrl.RemoveAt(0);
            SrlState st = new SrlState(); st.Sim = s; sSrl.Add(st);
            return st;
        }

        static void SrlLine(string text)
        {
            if (sSrlLines >= kSrlMaxLines) { PatchCounters()[3973]++; return; }
            sSrlLines++;
            HcLine(text);
        }

        static string SrlV(Vector3 v) { return v.x.ToString("0.00", CultureInfo.InvariantCulture) + "," + v.z.ToString("0.00", CultureInfo.InvariantCulture); }

        // pure: which branch text a trail and jig flag give (also used by the check)
        internal static string SrlBranch(string trail, bool hasJig)
        {
            string b = (trail ?? "").Trim();
            if (hasJig) b = (b.Length == 0 ? "" : b + " ") + "jig";
            return b.Length == 0 ? "none" : b;
        }

        // pure: whether a failed replan is logged (also used by the check)
        internal static bool SrlShouldLog(bool on, bool isReplan, bool failed, bool receiver, bool blockerIsPartner)
        {
            return on && isReplan && failed && receiver && blockerIsPartner;
        }

        // from SwStep (SyncWaitLog.cs): every receiver step
        static void SrlStep(Sim a, object ii, string step)
        {
            if (!ShortcutOn("socialreplanlog")) return;
            try
            {
                if (step == "receive>") { SrlState n = SrlOf(a, true); n.Trail = ""; n.Replans = 0; n.Partner = null; return; }
                SrlState st = SrlOf(a, false);
                if (st == null) return;
                if (step.StartsWith("receive<"))
                {
                    if (st.Replans > 0)
                        SrlLine("socialreplan done: " + SimLabel(a) + " -> " + st.Partner + ", " + st.Replans + " partner-blocked replans, receive "
                            + (step == "receive<ok" ? "ok" : "fail") + ", branch " + SrlBranch(st.Trail, (ii as SocialInteraction) != null && ((SocialInteraction)ii).SocialJig != null) + ", " + HcSimTime());
                    st.Replans = 0;
                    return;
                }
                if (step.EndsWith("<ok") || step.EndsWith("<yes")) st.Trail = (st.Trail + " " + step).Trim();
                if (st.Trail.Length > 120) st.Trail = st.Trail.Substring(st.Trail.Length - 120);
            }
            catch (Exception) { }
        }

        // from RflPlan (RouteFailLog.cs): every plan of a Sim; kind 1 / 3 = Replan / ReplanFromPoint
        static void SrlReplan(ScriptCore.Route r, Sim sim, int kind, RoutePlanResult res)
        {
            if (!ShortcutOn("socialreplanlog")) return;
            try
            {
                bool failed = res.mType != RoutePlanResultType.Succeeded;
                Sim partner = sim.SynchronizationTarget;
                bool receiver = sim.SynchronizationRole == Sim.SyncRole.Receiver && sim.InteractionQueue != null
                    && sim.InteractionQueue.RunningInteraction is SocialInteraction;
                bool blockerIsPartner = partner != null && res.mBlockerObjectId != 0 && res.mBlockerObjectId == partner.ObjectId.Value;
                if (!SrlShouldLog(true, (kind & 1) == 1, failed, receiver, blockerIsPartner)) return;
                PatchCounters()[3972]++;
                SrlState st = SrlOf(sim, true);
                st.Replans++;
                st.Partner = SimLabel(partner);
                if (st.Replans > kSrlDetailPerReceive) return;
                SocialInteraction si = (SocialInteraction)sim.InteractionQueue.RunningInteraction;
                string ignore = "";
                ObjectGuid[] ig = r.GetObjectsToIgnoreForRoute();
                if (ig != null) foreach (ObjectGuid g in ig) ignore += (ignore.Length == 0 ? "" : " ") + (g == partner.ObjectId ? "partner" : g == sim.ObjectId ? "self" : "0x" + g.Value.ToString("X"));
                string pdest = "standing";
                if (partner.IsRouting && partner.RoutingComponent != null && partner.RoutingComponent.GetCurrentRoute() != null)
                {
                    Vector3 d;
                    pdest = partner.RoutingComponent.GetCurrentRoute().GetDestPoint(out d) ? "routing to " + SrlV(d) : "routing";
                }
                Vector3 goal = r.GetApproximateOriginalDestination();
                SrlLine("socialreplan: " + SimLabel(sim) + " -> " + st.Partner + ", replan " + st.Replans + " " + res.mType
                    + ", in " + si.GetType().Name + ", branch " + SrlBranch(st.Trail, si.SocialJig != null)
                    + ", ignore [" + ignore + "], me at " + SrlV(sim.Position) + ", goal " + SrlV(goal)
                    + ", partner at " + SrlV(partner.Position) + " (" + pdest + ", sync " + partner.SynchronizationLevel + "/" + partner.SynchronizationRole + ")"
                    + ", gap " + (sim.Position - partner.Position).Length().ToString("0.00", CultureInfo.InvariantCulture) + " m, " + HcSimTime());
            }
            catch (Exception) { }
        }

        static string SimLabel(Sim s)
        {
            if (s == null) return "none";
            return (IsPlayerSim(s) ? "HH " : "") + s.FirstName;
        }
    }
}
