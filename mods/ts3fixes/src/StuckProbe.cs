using System;
using System.Reflection;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness probe (profile-world.ts, read only): one line of a Sim's state when the stuck rule first flags it. Visits that
    // stand still for a sim-day after one of our shortcut placements (visit / rhoutside / rabbithole direct, 2026-11-14..17,
    // e.g. af6 Tom Wordy "Visit Plasma 501") make no route plan at all, so the Sim's task waits before EA's route; this
    // shows where: posture and its container, routing parent, queue (types, cancelled, running), transition interaction,
    // sync level and target, situation, exit reasons, and the visit's followers.
    public static partial class Fixes
    {
        static readonly FieldInfo sSpFollowers = typeof(VisitCommunityLot).GetField("mFollowers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public static object StuckProbe(string name)
        {
            Sim s = null;
            foreach (Sim x in Sims3.Gameplay.Queries.GetObjects<Sim>())
                if (x.SimDescription != null && x.FullName == name) { s = x; break; }
            if (s == null) return "no Sim " + name;
            StringBuilder b = new StringBuilder();
            try
            {
                b.Append("inWorld ").Append(s.InWorld).Append(", hh ").Append(s.Household == null ? "none" : s.Household.Name);
                b.Append(", posture ").Append(s.Posture == null ? "none" : s.Posture.GetType().Name);
                if (s.Posture != null && s.Posture.Container != null) b.Append(" in ").Append(s.Posture.Container.GetType().Name);
                object rp = s.RoutingComponent == null ? null : s.RoutingComponent.RoutingParent;
                b.Append(", routingParent ").Append(rp == null ? "none" : rp.GetType().Name);
                b.Append(", routing ").Append(s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting);
                b.Append(", exit ").Append(s.ExitReason);
                b.Append(", sync ").Append(s.SynchronizationLevel).Append(" with ").Append(s.SynchronizationTarget == null ? "none" : s.SynchronizationTarget.FullName);
                Situation sit = s.GetSituationOfType<Situation>();
                b.Append(", situation ").Append(sit == null ? "none" : sit.GetType().Name);
                b.Append(", service ").Append(s.Service == null ? "none" : s.Service.GetType().Name);
                InteractionInstance cur = s.CurrentInteraction;
                b.Append(", current ").Append(cur == null ? "none" : cur.GetType().Name);
                if (cur != null) b.Append(" running ").Append(s.InteractionQueue.IsRunning(cur, true)).Append(" cancelled ").Append(cur.Cancelled);
                InteractionInstance tr = s.InteractionQueue.TransitionInteraction;
                b.Append(", transition ").Append(tr == null ? "none" : tr.GetType().Name);
                b.Append(", queue [");
                int n = 0;
                foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                {
                    if (n++ > 0) b.Append("; ");
                    if (n > 6) { b.Append("..."); break; }
                    b.Append(i == null ? "null" : i.GetType().Name + (i.Cancelled ? " cancelled" : "") + (i.Autonomous ? "" : " user"));
                    VisitCommunityLot v = i as VisitCommunityLot;
                    if (v != null && sSpFollowers != null)
                    {
                        System.Collections.ICollection f = sSpFollowers.GetValue(v) as System.Collections.ICollection;
                        b.Append(" followers ").Append(f == null ? "null" : f.Count.ToString()).Append(" to ").Append(v.Target == null ? "null" : v.Target.Name);
                    }
                }
                b.Append("]");
            }
            catch (Exception e) { b.Append(" | probe error ").Append(e.GetType().Name).Append(": ").Append(e.Message); }
            return b.ToString();
        }
    }
}
