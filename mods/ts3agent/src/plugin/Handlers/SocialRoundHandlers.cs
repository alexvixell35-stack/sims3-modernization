using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
using Sims3.UI.Controller;

namespace Ts3Agent
{
    // Socials that run, for the leak stress (issue #40; user 11-08: "friendly activities or mean need to be fixed so they
    // happen, i want to see what can leak from those"). The stress's do_interaction socials paired random townies from the
    // first 40 in world order, anywhere in town (a long route each), and nothing said whether a queued social ever ran.
    // social_round pairs townies that stand near each other and, at the next call, reports how the last round's socials
    // ended. TOWNIES ONLY: never a Sim of the active household, of a special household (services, pet pool), or on the
    // active household's home lot.
    public static partial class Handlers
    {
        static void RegisterSocialRound()
        {
            Dispatcher.Register("social_round", SocialRound);
        }

        class SocialTrack
        {
            public Sim Actor;
            public string Label;
            public InteractionInstance Inst;
        }

        // the last round's socials, read and cleared by the next call (holds at most 40 instances for one round)
        static readonly List<SocialTrack> sSocialRound = new List<SocialTrack>();

        // social_round {pairs?: 8 (1..40), cats?: "friendly,mean,funny,romantic", query?: "<label part>", enemies?: false,
        // max_dist?: 30, report?: false}: first the outcome of the last round (started = the social's StandardEntry ran, after
        // the approach, or SocialInteractionA.mSocialStarted; cancelled with EA's ExitReason; waiting = still queued; dropped = left the queue without either), then
        // (not with report) a new round: instantiated townies teen and older, paired with the nearest unpaired townie within
        // max_dist metres; each actor queues a random enabled two-segment social of its pie menu on its partner whose category
        // ("Friendly... / Chat": the first segment without its ellipsis) is in cats and whose label contains query, romantic only
        // for two YA+ Sims. Queued as do_interaction does (UserDirected). enemies: the pair is made Enemies (liking -100) first,
        // as modify_relationship, so Mean... / Fight! is on the menu.
        static object SocialRound(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            r["last"] = SocialRoundReport();
            if (a.Bool("report", false)) return r;
            int pairs = (int)a.Long("pairs", 8);
            if (pairs < 1 || pairs > 40) throw new McpException("pairs must be 1..40");
            List<string> cats = new List<string>();
            foreach (string c in a.Str("cats", "friendly,mean,funny,romantic").Split(',')) if (c.Trim().Length > 0) cats.Add(c.Trim().ToLowerInvariant());
            string query = a.Str("query", "").ToLowerInvariant();
            bool enemies = a.Bool("enemies", false);
            float maxDist = (float)a.Double("max_dist", 30);
            Household active = Household.ActiveHousehold;
            Lot activeLot = active != null ? active.LotHome : null;
            List<Sim> free = new List<Sim>();
            foreach (SimDescription sd in SimDescription.GetSimDescriptionsInWorld())
            {
                if (!Util.InWorldNow(sd) || sd.IsPet || sd.Household == null || sd.Household == active || sd.Household.IsSpecialHousehold) continue;
                if ((sd.Age & (CASAgeGenderFlags.Teen | CASAgeGenderFlags.YoungAdult | CASAgeGenderFlags.Adult | CASAgeGenderFlags.Elder)) == 0) continue;
                Sim s = sd.CreatedSim;
                if (activeLot != null && s.LotCurrent == activeLot) continue;
                free.Add(s);
            }
            r["candidates"] = free.Count;
            List<object> queued = new List<object>();
            Dictionary<string, object> skipped = Util.Obj();
            while (queued.Count < pairs && free.Count >= 2)
            {
                Sim actor = RandomUtil.GetRandomObjectFromList(free);
                free.Remove(actor);
                Sim target = null;
                float best = maxDist;
                foreach (Sim s in free)
                {
                    float d = actor.GetDistanceToObject(s);
                    if (d <= best) { best = d; target = s; }
                }
                if (target == null) { Count(skipped, "no townie within " + maxDist + " m"); continue; }
                free.Remove(target);
                const CASAgeGenderFlags kGrown = CASAgeGenderFlags.YoungAdult | CASAgeGenderFlags.Adult | CASAgeGenderFlags.Elder;
                bool grown = (actor.SimDescription.Age & kGrown) != 0 && (target.SimDescription.Age & kGrown) != 0;
                if (enemies)
                {
                    Relationship rel = Relationship.Get(actor.SimDescription, target.SimDescription, true);
                    if (rel != null && rel.LTR != null) { rel.LTR.ForceChangeState(LongTermRelationshipTypes.Enemy); rel.LTR.SetLiking(-100f); }
                }
                GameObjectHit hit = HitFor(target, a);
                List<MenuEntry> fit = new List<MenuEntry>();
                foreach (MenuEntry e in BuildMenu(actor, target, hit))
                {
                    if (!e.Enabled) continue;
                    string[] seg = e.Label.Split(new string[] { " / " }, StringSplitOptions.None);
                    if (seg.Length != 2) continue;  // nested submenus open pickers
                    string cat = seg[0].Trim().TrimEnd('.', ' ').ToLowerInvariant();
                    if (!cats.Contains(cat) || (cat == "romantic" && !grown)) continue;
                    if (query.Length > 0 && e.Label.ToLowerInvariant().IndexOf(query) < 0) continue;
                    fit.Add(e);
                }
                if (fit.Count == 0) { Count(skipped, "no matching social"); continue; }
                if (!actor.InteractionQueue.CanPlayerQueue()) { Count(skipped, "queue full"); continue; }
                MenuEntry pick = RandomUtil.GetRandomObjectFromList(fit);
                InteractionInstanceParameters p = new InteractionInstanceParameters(pick.Iop, actor,
                    new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                    InteractionFlags.UserDirected, hit, null, false);
                InteractionInstance inst = pick.Iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
                if (inst == null || inst is IImmediateInteraction) { Count(skipped, "not a queued interaction"); continue; }
                if (!actor.InteractionQueue.Add(inst)) { Count(skipped, "refused"); continue; }
                SocialTrack t = new SocialTrack();
                t.Actor = actor; t.Label = pick.Label; t.Inst = inst;
                sSocialRound.Add(t);
                Dictionary<string, object> q = Util.Obj();
                q["actor"] = actor.FullName;
                q["target"] = target.FullName;
                q["social"] = pick.Label;
                q["m"] = Util.Round(best);
                q["lot"] = actor.LotCurrent != null ? actor.LotCurrent.Name : null;
                queued.Add(q);
            }
            r["queued"] = queued;
            r["skipped"] = skipped;
            return r;
        }

        static void Count(Dictionary<string, object> d, string k) { d[k] = (d.ContainsKey(k) ? (int)d[k] : 0) + 1; }

        // SocialInteractionA.Run never calls StandardEntry [verified, decompiled]: a social that ran to the end still has
        // StandardEntryCalled false. Its own flag mSocialStarted is set once BeginSocialInteraction succeeded (the approach
        // worked, both Sims are in the social). Before 2026-11-14 such socials were reported as "dropped".
        static System.Reflection.FieldInfo sSrStarted;
        static bool SrSocialStarted(InteractionInstance i)
        {
            if (!(i is Sims3.Gameplay.Socializing.SocialInteractionA)) return false;
            if (sSrStarted == null) sSrStarted = typeof(Sims3.Gameplay.Socializing.SocialInteractionA).GetField("mSocialStarted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object v = sSrStarted != null ? sSrStarted.GetValue(i) : null;
            return v is bool && (bool)v;
        }

        static bool InQueue(SocialTrack t)
        {
            if (t.Actor == null || t.Actor.HasBeenDestroyed || t.Actor.InteractionQueue == null) return false;
            foreach (InteractionInstance i in t.Actor.InteractionQueue.InteractionList) if (i == t.Inst) return true;
            return false;
        }

        static Dictionary<string, object> SocialRoundReport()
        {
            int started = 0, cancelled = 0, waiting = 0, dropped = 0;
            Dictionary<string, object> reasons = Util.Obj(), ran = Util.Obj();
            foreach (SocialTrack t in sSocialRound)
            {
                if (t.Inst.StandardEntryCalled || SrSocialStarted(t.Inst)) { started++; Count(ran, t.Label); }
                else if (t.Inst.Cancelled) { cancelled++; Count(reasons, t.Inst.CancelledReason.ToString()); }
                else if (InQueue(t)) waiting++;
                else dropped++;
            }
            Dictionary<string, object> d = Util.Obj();
            d["socials"] = sSocialRound.Count;
            d["started"] = started;
            d["cancelled"] = cancelled;
            d["waiting"] = waiting;
            d["dropped"] = dropped;
            d["cancel_reasons"] = reasons;
            d["ran"] = ran;
            sSocialRound.Clear();
            return d;
        }
    }
}
