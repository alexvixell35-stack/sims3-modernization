using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // bathroomwait (default off; meant to run with privacyroom on): a household Sim who needs the toilet while every toilet at
    // home is busy goes back to it as soon as one frees. EA has no such retrigger: once the Sim has picked something else
    // (privacyroom makes autonomy skip the occupied bathroom), it finishes that and only looks again when the next autonomy
    // pass or a Bladder moodlet comes round, often at -80. Every kBwSleepTicks the loop checks the active household at home:
    // a Sim with Bladder at or below kBwNeed, doing an autonomous thing (or nothing), with no toilet free, is marked waiting.
    // When a toilet is free (not in use, not broken, its room not privacy-locked for that Sim, not claimed by a Sim already
    // sent there), the waiting Sim gets Toilet.UseToilet pushed at UserDirected priority. That cuts the autonomous filler the
    // way a player's click does (InteractionQueue.RemoveLowerPriorityInteractions). The push stays autonomous, so the bathroom
    // door still applies privacy to its route. Never sent: a Sim with a user command running or queued, asleep, eating, talking
    // to the active Sim, washing, already queued for the toilet, or away from home. Without privacyroom, RoomLockedByPrivacy reads
    // EA's "everyone is in the situation" answer and a busy bathroom can look free, so the push can fail at the door.
    // Each wait, push and give-up is logged ("bathroomwait: ...") up to kBwLogMax lines a session.
    public static partial class Fixes
    {
        const int kBwLogMax = 400, kBwSleepTicks = 10;
        const float kBwNeed = -20f, kBwMaxWaitMin = 180f, kBwClaimMin = 30f;
        static int sBwN;
        static InteractionDefinition sBwUse;

        sealed class BwWait { public float T0; public float Need0; }
        sealed class BwClaim { public Sim S; public float T0; }

        public static void BathroomWaitWorldLoad()
        {
            if (!ShortcutOn("bathroomwait")) return;
            Type t = typeof(Toilet).GetNestedType("UseToilet", BindingFlags.NonPublic);
            FieldInfo f = t == null ? null : t.GetField("Singleton", BindingFlags.Public | BindingFlags.Static);
            sBwUse = f == null ? null : f.GetValue(null) as InteractionDefinition;
            if (sBwUse == null) { DiagLine("bathroomwait: Toilet.UseToilet.Singleton not found, switch does nothing"); return; }
            Dictionary<ulong, BwWait> wait = new Dictionary<ulong, BwWait>();
            Dictionary<ulong, BwClaim> claim = new Dictionary<ulong, BwClaim>();
            Simulator.AddObject(new LoopTask(delegate { BathroomWaitLoop(wait, claim); }));
        }

        static void BwLog(Sim s, string what)
        {
            if (++sBwN <= kBwLogMax)
                DiagLine("bathroomwait: " + s.FullName + (s == PlumbBob.SelectedActor ? " (active)" : "") + ": " + what + ", speed " + GameUtils.GetGameTimeSpeedLevel());
        }

        // shortcut: Sims are served in household order, not the most urgent first; sort by Bladder if two often wait for one toilet
        static void BathroomWaitLoop(Dictionary<ulong, BwWait> wait, Dictionary<ulong, BwClaim> claim)
        {
            while (true)
            {
                Simulator.Sleep(kBwSleepTicks);
                Household h = Household.ActiveHousehold;
                if (h == null || h.LotHome == null) continue;
                try
                {
                    Toilet[] toilets = null;
                    float now = SimClock.ElapsedTime(TimeUnit.Minutes);
                    foreach (Sim s in h.AllActors)
                    {
                        if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null || s.Motives == null) continue;
                        ulong id = s.ObjectId.Value;
                        BwWait w;
                        bool waiting = wait.TryGetValue(id, out w);
                        string skip = BwSkip(s, h.LotHome);
                        float need = skip == null ? s.Motives.GetMotiveValue(CommodityKind.Bladder) : 0f;
                        if (skip != null || need > kBwNeed)
                        {
                            if (waiting)
                            {
                                wait.Remove(id);
                                BwLog(s, "stops waiting after " + (now - w.T0).ToString("0.0") + " sim-min: " + (skip ?? "bladder back to " + need.ToString("0")));
                            }
                            continue;
                        }
                        if (toilets == null) toilets = h.LotHome.GetObjects<Toilet>();
                        if (toilets.Length == 0) continue;
                        Toilet free = BwFree(s, h.LotHome, toilets, claim, now);
                        InteractionInstance cur = s.CurrentInteraction;
                        string doing = cur == null ? "nothing" : cur.GetType().Name;
                        if (!waiting)
                        {
                            if (free != null) continue;  // a toilet is free: EA's own autonomy can take it
                            wait[id] = new BwWait { T0 = now, Need0 = need };
                            BwLog(s, "waits, bladder " + need.ToString("0") + ", " + toilets.Length + " toilets busy, doing " + doing);
                            continue;
                        }
                        if (free == null)
                        {
                            if (now - w.T0 > kBwMaxWaitMin) { wait.Remove(id); BwLog(s, "gives up after " + (now - w.T0).ToString("0") + " sim-min"); }
                            continue;
                        }
                        InteractionInstance ii = sBwUse.CreateInstance(free, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), true, true);
                        if (ii != null && s.InteractionQueue.Add(ii))
                        {
                            claim[free.ObjectId.Value] = new BwClaim { S = s, T0 = now };
                            wait.Remove(id);
                            BwLog(s, "sent to the toilet after " + (now - w.T0).ToString("0.0") + " sim-min, bladder " + w.Need0.ToString("0") + " to " + need.ToString("0") + ", cut " + doing);
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { }  // a Sim or toilet gone mid-loop: next sample
            }
        }

        // why a Sim must not be sent now, or null
        static string BwSkip(Sim s, Lot home)
        {
            if (s.LotCurrent != home) return "left home";
            SimDescription d = s.SimDescription;
            if (d == null || !d.IsHuman || !d.ChildOrAbove) return "not a toilet user";
            if (s.IsSleeping) return "asleep";
            foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                if (!i.Autonomous) return "user command";
            if (s.InteractionQueue.GetInteractionSatisfying(CommodityKind.Bladder) != null) return "already going";
            InteractionInstance cur = s.CurrentInteraction;
            if (cur != null && cur.SatisfiesCommodity(CommodityKind.Hunger)) return "eating";
            if (cur != null && cur.SatisfiesCommodity(CommodityKind.Hygiene)) return "washing";
            if (cur != null && PlumbBob.SelectedActor != null && s != PlumbBob.SelectedActor && cur.Target == PlumbBob.SelectedActor) return "with the active Sim";
            return null;
        }

        // the nearest toilet this Sim could use now, or null
        static Toilet BwFree(Sim s, Lot home, Toilet[] toilets, Dictionary<ulong, BwClaim> claim, float now)
        {
            Toilet best = null;
            float bestD = float.MaxValue;
            foreach (Toilet t in toilets)
            {
                if (t == null || t.HasBeenDestroyed || t.InUse) continue;
                if (t.Repairable != null && t.Repairable.Broken) continue;
                if (home.RoomLockedByPrivacy(s, t.RoomId)) continue;
                BwClaim c;
                if (claim.TryGetValue(t.ObjectId.Value, out c) && c.S != s)
                {
                    if (BwClaimLive(c, t, now)) continue;
                    claim.Remove(t.ObjectId.Value);
                }
                float dd = (t.Position - s.Position).LengthSqr();
                if (dd < bestD) { bestD = dd; best = t; }
            }
            return best;
        }

        // a Sim sent to this toilet still has it queued, within kBwClaimMin sim-minutes
        static bool BwClaimLive(BwClaim c, Toilet t, float now)
        {
            if (c.S == null || c.S.HasBeenDestroyed || c.S.InteractionQueue == null || now - c.T0 > kBwClaimMin) return false;
            foreach (InteractionInstance i in c.S.InteractionQueue.InteractionList)
                if (i.Target == t) return true;
            return false;
        }
    }
}
