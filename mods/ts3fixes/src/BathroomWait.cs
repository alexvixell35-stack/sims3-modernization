using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
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
    // sent there), the waiting Sim gets the toilet's own use interaction pushed at UserDirected priority: the first one on the
    // toilet whose definition type is named *UseToilet*, so a mod's replacement (zoeoe_WaterMod's CustomUseToilet, the only one
    // seen in the 11-24 party logs) wins over EA's Toilet.UseToilet, and its Test must pass. That cuts the autonomous filler the
    // way a player's click does (InteractionQueue.RemoveLowerPriorityInteractions). The push stays autonomous, so the bathroom
    // door still applies privacy to its route. Never sent: a Sim with a user command running or queued, asleep, eating, talking
    // to the active Sim, washing, already queued for the toilet, or away from home. Without privacyroom, RoomLockedByPrivacy reads
    // EA's "everyone is in the situation" answer and a busy bathroom can look free, so the push can fail at the door.
    // Each wait, push and give-up is logged ("bathroomwait: ...") up to kBwLogMax lines a session.
    // With facilityplan + facilityplanhh on (docs/686 §36.7) the facility planner decides: a Sim waits from kBwNeed or when
    // its bladder reaches EA's urgent level (-60) within 2 sim-hours; the most urgent Sim is served first; the target is the
    // toilet (or, with showerpeeplan, a shower that also fills the bladder) with the best ETA including the walk and other
    // floors, and the Sim is sent only when that best one is free now (a nearer toilet about to free beats a far free one).
    // Fixes (2026-11-25): a toilet with a line, or one some other Sim has queued, no longer counts as free (the push used
    // to end in that line); one loop per world (a second world load used to start a second loop beside the first);
    // the switch is read every pass (it used to be read only at world load); the first errors are logged.
    public static partial class Fixes
    {
        const int kBwLogMax = 400, kBwSleepTicks = 10;
        const float kBwNeed = -20f, kBwMaxWaitMin = 180f, kBwClaimMin = 30f;
        static int sBwN, sBwGen, sBwErrors;

        sealed class BwWait { public float T0; public float Need0; public bool TestFailLogged; }
        sealed class BwClaim { public Sim S; public float T0; }

        public static void BathroomWaitWorldLoad()
        {
            int gen = ++sBwGen;  // a loop of an older world load sees the new generation and ends
            if (!ShortcutOn("bathroomwait")) return;
            Dictionary<ulong, BwWait> wait = new Dictionary<ulong, BwWait>();
            Dictionary<ulong, BwClaim> claim = new Dictionary<ulong, BwClaim>();
            Simulator.AddObject(new LoopTask(delegate { BathroomWaitLoop(wait, claim, gen); }));
        }

        static void BwLog(Sim s, string what)
        {
            if (++sBwN <= kBwLogMax)
                DiagLine("bathroomwait: " + s.FullName + (s == PlumbBob.SelectedActor ? " (active)" : "") + ": " + what + ", speed " + GameUtils.GetGameTimeSpeedLevel());
        }

        // shortcut: Sims are served in household order, not the most urgent first; sort by Bladder if two often wait for one toilet
        static void BathroomWaitLoop(Dictionary<ulong, BwWait> wait, Dictionary<ulong, BwClaim> claim, int gen)
        {
            while (gen == sBwGen)
            {
                Simulator.Sleep(kBwSleepTicks);
                if (!ShortcutOn("bathroomwait")) { wait.Clear(); continue; }
                Household h = Household.ActiveHousehold;
                if (h == null || h.LotHome == null) continue;
                try
                {
                    Toilet[] toilets = null;
                    float now = SimClock.ElapsedTime(TimeUnit.Minutes);
                    bool plan = ShortcutOn("facilityplan") && ShortcutOn("facilityplanhh");
                    List<Sim> order = new List<Sim>(h.AllActors);
                    if (plan) order.Sort(BwByBladder);  // the most urgent first
                    foreach (Sim s in order)
                    {
                        if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null || s.Motives == null) continue;
                        ulong id = s.ObjectId.Value;
                        BwWait w;
                        bool waiting = wait.TryGetValue(id, out w);
                        string skip = BwSkip(s, h.LotHome);
                        float need = skip == null ? s.Motives.GetMotiveValue(CommodityKind.Bladder) : 0f;
                        if (skip != null || (need > kBwNeed && !(plan && FpBwSoon(s))))
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
                        GameObject free = plan ? FpBwPick(s, h.LotHome) : BwFree(s, h.LotHome, toilets, claim, now);
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
                        string use;
                        InteractionInstance ii = free is Toilet ? BwUse((Toilet)free, s, out use) : FpBwShower(free, s, out use);
                        if (ii == null)
                        {
                            // keeps waiting: the toilet's own test can refuse for a moment (11-24 A/B: 2 of 79 waits ended here);
                            // the next sample tries again, up to kBwMaxWaitMin. Logged once per wait.
                            if (!w.TestFailLogged) { w.TestFailLogged = true; BwLog(s, "keeps waiting: " + free.GetType().Name + " refused (" + use + ")"); }
                        }
                        else if (s.InteractionQueue.Add(ii))
                        {
                            claim[free.ObjectId.Value] = new BwClaim { S = s, T0 = now };
                            wait.Remove(id);
                            BwLog(s, "sent to the toilet after " + (now - w.T0).ToString("0.0") + " sim-min, bladder " + w.Need0.ToString("0") + " to " + need.ToString("0") + ", cut " + doing + ", via " + use);
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { if (sBwErrors++ < 3) DiagLine("bathroomwait: error: " + e.GetType().Name + ": " + e.Message); }  // a Sim or toilet gone mid-loop: next sample
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
                SimQueue line = t.SimLine;
                if (line != null && line.HasMembers()) continue;  // somebody is in its line: the push would only join it
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

        // the toilet's use interaction for this Sim, mod replacements first, or null; use names it (or why none)
        static InteractionInstance BwUse(Toilet t, Sim s, out string use)
        {
            use = "none on the object";
            InteractionObjectPair pick = null;
            foreach (InteractionObjectPair iop in t.Interactions)
            {
                if (iop == null || iop.InteractionDefinition == null) continue;
                string n = iop.InteractionDefinition.GetType().FullName;
                if (n.IndexOf("UseToilet", StringComparison.Ordinal) < 0) continue;
                if (pick == null || !n.StartsWith("Sims3.", StringComparison.Ordinal)) pick = iop;
            }
            if (pick == null) return null;
            use = pick.InteractionDefinition.GetType().FullName;
            InteractionPriority pr = new InteractionPriority(InteractionPriorityLevel.UserDirected);
            InteractionInstanceParameters p = new InteractionInstanceParameters(pick, s, pr, true, true);
            GreyedOutTooltipCallback cb = null;
            if (!InteractionDefinitionUtilities.IsPass(pick.InteractionDefinition.Test(ref p, ref cb))) { use += " test failed"; return null; }
            return pick.InteractionDefinition.CreateInstance(t, s, pr, true, true);
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
