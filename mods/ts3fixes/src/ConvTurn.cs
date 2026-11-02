using System;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // convturn (issue #166 build 19, docs/509): a conversation's turn passes only when the Sim on turn selects a social
    // (Conversation.UpdateOnSelectingInteraction), and SocialComponent.ScoreSocialsOnSims scores only on the Sim's own turn.
    // Autonomy.RunAutonomy calls it only for a Sim that runs local (high-LOD) autonomy. When the turn sits with a townie that
    // does not (off screen, on a world lot), nobody ever scores a social: the player's Sim is never on turn and the townie
    // never scores, so both stand there until a need or the player ends it.
    // Fix, switch on: before EA's ScoreSocialsOnSims, when the turn is not the asker's and its holder cannot or does not take
    // it (ConvTurnCore.Stuck), the asker gets the turn, as EA's own turn change does it (a plain store of mWhoseTurn); EA's
    // method then scores as on the asker's own turn. Only local Sims get here, so two off-screen townies are unchanged.
    // [2031] stuck turns seen by a scoring call (also with the switch off), [2032] turns taken.
    public static partial class Fixes
    {
        static readonly FieldInfo sCtSim = typeof(SocialComponent).GetField("mSim", kInstAll);
        static readonly FieldInfo sCtTurn = typeof(Conversation).GetField("mWhoseTurn", kInstAll);
        static readonly FieldInfo sCtTalked = typeof(Conversation).GetField("mWhenTalkedLast", kInstAll);

        public class ConvTurnHook
        {
            public void ScoreHook()
            {
                try { ConvTurnBefore((SocialComponent)(object)this); }
                catch (Exception) { } // the check must never stop EA's scoring
                ScoreOrig();
            }
            public void ScoreOrig() { } // receives SocialComponent.ScoreSocialsOnSims()'s original header
        }

        // true when the asker took the turn
        static bool ConvTurnBefore(SocialComponent sc)
        {
            Conversation c = sc.Conversation;
            if (c == null) return false;
            Sim h = c.WhoseTurn;
            Autonomy au = h != null ? h.Autonomy : null;
            InteractionQueue q = h != null ? h.InteractionQueue : null;
            InteractionInstance head = q != null ? q.GetHeadInteraction() : null;
            float since = SimClock.ElapsedTime(TimeUnit.Minutes) - c.WhenTalkedLast;
            if (!ConvTurnCore.Stuck(h == null, false, h != null && h.HasBeenDestroyed, au == null, au != null && au.IsRunningLocalAutonomy,
                    q == null, head == null, head is SocialInteraction, since))
                return false;
            Sim asker = sCtSim.GetValue(sc) as Sim; // the one reflection read, on the stuck path only (mSim is private)
            if (asker == null || h == asker) return false;
            PatchCounters()[2031]++;
            if (!ShortcutOn("convturn")) return false;
            sCtTurn.SetValue(c, asker);
            PatchCounters()[2032]++;
            return true;
        }

        static Sim sCtAsker;
        // Agent checks for docs/509 (repro conversation-turn-check), on a real conversation of the active household:
        //   "start": picks two Sims of the active household on one lot (above toddler, neither in a conversation); throws
        //            when there is no such pair. It leaves what they are doing alone (the conversation does not need them free).
        //   "chat":  puts the two in one conversation with EA's SocialComponent.JoinConversationWith (what EA's
        //            StartSocializingWith calls); throws when they are not in one conversation afterwards. Each arm runs it:
        //            EA can end the off arm's conversation before the on arm (run 5: one Sim went to bed). An expect step
        //            that finds the conversation ended joins the same two again in its own call (run 6).
        //   "pick":  a Sim of the active household in a conversation with another member; throws when there is none.
        //   "expect stuck" (switch off) / "expect taken" (switch on): in one call, gives the turn to the other member and
        //            backdates the last social by 20 sim minutes (a silent holder), then runs the hook's check as EA's
        //            ScoreSocialsOnSims would. Throws unless: off, seen +1, nothing taken, the turn stays with the holder;
        //            on, seen +1, taken +1, the turn is the asker's.
        //   "expect control" (switch on): the same with the last social just now and the holder local: throws unless
        //            nothing is seen or taken. Not in the repro: in a joined conversation the holder is always busy with a
        //            non-social, which the hook counts as stuck (run 8); the self-test covers the not-stuck cases.
        //   "state": members, whose turn, minutes since the last social, counters.
        //   "stop":  forgets the Sim.
        public static string ConvTurnCheck(string what)
        {
            if (what == "start")
            {
                sCtA = sCtB = null;
                if (Household.ActiveHousehold == null) throw new InvalidOperationException("convturn start: no active household");
                foreach (Sim a in Household.ActiveHousehold.AllActors)
                {
                    foreach (Sim b in Household.ActiveHousehold.AllActors)
                    {
                        if (a == b || a.HasBeenDestroyed || b.HasBeenDestroyed || a.LotCurrent == null || a.LotCurrent != b.LotCurrent) continue;
                        if (a.Conversation != null || b.Conversation != null || a.SimDescription.ToddlerOrBelow || b.SimDescription.ToddlerOrBelow) continue;
                        sCtA = a; sCtB = b; break;
                    }
                    if (sCtA != null) break;
                }
                if (sCtA == null) throw new InvalidOperationException("convturn start: no two Sims of the active household (above toddler, not in a conversation) on one lot; " + CtDoing());
                return sCtA.FullName + " and " + sCtB.FullName + " (" + CtDoing() + ")";
            }
            if (what == "chat")
            {
                if (sCtA == null) throw new InvalidOperationException("convturn chat: start first");
                // EA's own entry: StartSocializingWith calls it when a social starts. A queued Chat lost the race to
                // autonomy in runs 2-4 (2026-11-01: queued behind other interactions, refused, or the Sims went to bed).
                if (sCtA.Conversation != null && sCtA.Conversation == sCtB.Conversation)
                    return sCtA.FullName + " and " + sCtB.FullName + ": already in one conversation (" + sCtA.Conversation.NumMembers + " members)";
                bool joined = sCtA.SocialComponent != null && sCtA.SocialComponent.JoinConversationWith(sCtB);
                Conversation jc = sCtA.Conversation;
                if (!joined || jc == null || sCtB.Conversation != jc)
                    throw new InvalidOperationException("convturn chat: EA did not put " + sCtA.FullName + " and " + sCtB.FullName + " in one conversation (joined " + joined + "); " + CtDoing());
                return sCtA.FullName + " and " + sCtB.FullName + ": in one conversation (" + jc.NumMembers + " members)";
            }
            if (what == "pick")
            {
                sCtAsker = null;
                string again = "";
                for (int pass = 0; pass < 2 && sCtAsker == null; pass++)
                {
                    // run 7: the conversation ended in the second after chat; join the same two again in this call
                    if (pass == 1 && sCtA != null) again = ConvTurnCheck("chat") + " again; ";
                    if (Household.ActiveHousehold != null)
                        foreach (Sim s in Household.ActiveHousehold.AllActors)
                        {
                            Conversation c = s.Conversation;
                            if (c != null && OtherMember(c, s) != null) { sCtAsker = s; break; }
                        }
                }
                if (sCtAsker == null) throw new InvalidOperationException("convturn pick: no Sim of the active household is in a conversation with another Sim; " + CtDoing());
                return again + sCtAsker.FullName + "; switch " + ShortcutOn("convturn") + "; " + ConvTurnCheck("state");
            }
            if (what == "stop") { sCtAsker = null; return "stopped"; }
            if (sCtAsker == null) throw new InvalidOperationException("convturn: pick first");
            Conversation conv = sCtAsker.Conversation;
            if (what == "state")
            {
                if (conv == null) return "no conversation; seen " + PatchCounters()[2031] + ", taken " + PatchCounters()[2032];
                string m = "";
                foreach (Sim s in conv.Members) m += (m.Length > 0 ? ", " : "") + s.FullName + (s.Autonomy != null && s.Autonomy.IsRunningLocalAutonomy ? " (local)" : "");
                Sim t = conv.WhoseTurn;
                return "members [" + m + "], turn " + (t != null ? t.FullName : "nobody") + ", "
                    + (SimClock.ElapsedTime(TimeUnit.Minutes) - (float)sCtTalked.GetValue(conv)).ToString("0.0") + " min since the last social, seen "
                    + PatchCounters()[2031] + ", taken " + PatchCounters()[2032];
            }
            if (what == "expect stuck" || what == "expect taken" || what == "expect control")
            {
                // EA ends a conversation of two Sims who do no social within a second or two (runs 5 and 6, 2026-11-01:
                // one went to bed or walked away). Then the same two join again in this call, so no tick passes before the check.
                string again = "";
                if (conv == null && sCtA != null)
                {
                    again = ConvTurnCheck("chat") + " again; ";
                    ConvTurnCheck("pick");
                    conv = sCtAsker.Conversation;
                }
                if (conv == null) throw new InvalidOperationException("convturn " + what + ": " + sCtAsker.FullName + " is no longer in a conversation");
                Sim holder = OtherMember(conv, sCtAsker);
                if (holder == null) throw new InvalidOperationException("convturn " + what + ": no other member");
                bool control = what == "expect control";
                if (control && (holder.Autonomy == null || !holder.Autonomy.IsRunningLocalAutonomy))
                    throw new InvalidOperationException("convturn expect control: the holder " + holder.FullName + " runs no local autonomy, so the control cannot be built");
                sCtTurn.SetValue(conv, holder);
                sCtTalked.SetValue(conv, SimClock.ElapsedTime(TimeUnit.Minutes) - (control ? 0f : 20f));
                int seen0 = PatchCounters()[2031], taken0 = PatchCounters()[2032];
                bool took = ConvTurnBefore(sCtAsker.SocialComponent);
                int seen = PatchCounters()[2031] - seen0, taken = PatchCounters()[2032] - taken0;
                Sim t = conv.WhoseTurn;
                bool on = ShortcutOn("convturn");
                bool ok = what == "expect stuck" ? !on && seen == 1 && taken == 0 && !took && t == holder
                    : what == "expect taken" ? on && seen == 1 && taken == 1 && took && t == sCtAsker
                    : on && seen == 0 && taken == 0 && !took && t == holder;
                string st = "seen +" + seen + ", taken +" + taken + ", turn " + (t != null ? t.FullName : "nobody") + " (holder " + holder.FullName + ")";
                if (!ok) throw new InvalidOperationException("convturn " + what + ": wrong outcome (switch " + on + "); " + st);
                return again + "as expected; " + st;
            }
            return "unknown check " + what + " (start, chat, pick, expect stuck, expect taken, expect control, state, stop)";
        }

        static Sim sCtA, sCtB;

        // what each Sim of the active household is doing, for the failure messages
        static string CtDoing()
        {
            string r = "";
            if (Household.ActiveHousehold != null)
                foreach (Sim s in Household.ActiveHousehold.AllActors)
                {
                    InteractionInstance cur = s.CurrentInteraction;
                    r += (r.Length > 0 ? "; " : "") + s.FullName + ": " + (cur != null ? cur.GetType().Name : "nothing") + ", queue "
                        + (s.InteractionQueue != null ? s.InteractionQueue.Count : -1) + (s.Conversation != null ? ", in a conversation" : "")
                        + (s.LotCurrent != null ? ", lot " + s.LotCurrent.Name : "");
                }
            return r;
        }

        static Sim OtherMember(Conversation c, Sim self)
        {
            foreach (Sim s in c.Members) if (s != null && s != self && !s.HasBeenDestroyed) return s;
            return null;
        }
    }
}
