using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Socializing;
using Sims3.UI.Controller;

namespace Ts3Fixes
{
    // stcsocials (docs/584 §9, default off; proven-exact speedup, so every Sim, household included): Conversation.GetSocialsForSTC
    // runs once per playable commodity (about 10) each time a SocialInteractionCandidateCollection is built, i.e. for every
    // actor/target pair social autonomy scores, and from SocialComponent. In the autonomous branch EA:
    //   - copies the first ActionAvailabilityData.Get list before adding the earlier STCs' lists to it, when the relationship's
    //     STC is on this commodity. Get always returns a new list that nobody else holds, so adding to it directly gives the same
    //     list (same items, same order);
    //   - always copies the result into a third list, calling ActionData.Get (a dictionary lookup, no side effect) for every
    //     candidate. When `flag` (no STC, or the STC is on this commodity) holds, every candidate is kept and the lookup is
    //     never read, so the list itself is returned (callers get a fresh list either way).
    // Every other step runs as EA has it, in EA's order, including the same NullReferenceException when r.STC is null and the
    // commodity test reads it. The non-autonomous branch (pie menu, trait menu items) runs EA's method unchanged.
    // stcsocialscheck (default off, pure check): EA's method runs too, and both results are compared item by item (name,
    // topic, trait-menu flag); a difference is counted and logged once, and EA's result is returned.
    // Counters: [2742] calls served here, [2743] lists or lookups saved (copies skipped + 1 per kept candidate), [2744] checks
    // run, [2745] check mismatches (must stay 0), [2746] fell back to EA's method (GetCurrentSTC not found) or errors.
    public static partial class Fixes
    {
        delegate ShortTermContextTypes StcCurrentFn(Sim actor, Sim target, Relationship r, Conversation c, out bool isActive);
        static StcCurrentFn sStcCurrent;
        static bool sStcTried, sStcOk, sStcLogged;

        static void ApplySocialStc()
        {
            Patch(typeof(Conversation), "GetSocialsForSTC", new Type[] { typeof(CommodityTypes), typeof(Sim), typeof(Sim), typeof(Relationship), typeof(Conversation), typeof(bool) },
                typeof(SocialStcHook), "StcHook", "StcOrig");
        }

        static bool StcReady()
        {
            if (sStcTried) return sStcOk;
            sStcTried = true;
            try
            {
                MethodInfo m = typeof(Conversation).GetMethod("GetCurrentSTC", BindingFlags.Static | BindingFlags.NonPublic, null,
                    new Type[] { typeof(Sim), typeof(Sim), typeof(Relationship), typeof(Conversation), typeof(bool).MakeByRefType() }, null);
                if (m != null && m.ReturnType == typeof(ShortTermContextTypes))
                    sStcCurrent = (StcCurrentFn)Delegate.CreateDelegate(typeof(StcCurrentFn), m);
                sStcOk = sStcCurrent != null;
            }
            catch (Exception) { sStcOk = false; }
            if (!sStcOk) AppliedLog().Add("Conversation.GetSocialsForSTC: stcsocials helper not found, EA's method runs");
            return sStcOk;
        }

        // EA's autonomous branch with the two redundant copies left out (see the header)
        static List<SocialInteractionCandidate> StcAutonomous(CommodityTypes commodity, Sim actor, Sim target, Relationship r, Conversation c, int[] ctr)
        {
            LongTermRelationshipTypes ltr = LongTermRelationshipTypes.Stranger;
            if (r != null) ltr = r.LTR.CurrentLTR;
            bool flag = r != null && (r.STC == null || commodity == r.STC.CurrentCommodity);
            if (!SocialComponent.SocialCommodityIsAvailable(commodity, target, actor, r)) return null;
            bool unused;
            ShortTermContextTypes stc = !flag ? STCData.FirstSTC(commodity) : sStcCurrent(actor, target, r, c, out unused);
            List<SocialInteractionCandidate> list = ActionAvailabilityData.Get(actor, target, stc, ltr, true, null, true);
            if (r != null && r.STC.CurrentCommodity == commodity)
            {
                ctr[2743]++;  // EA's copy of the first list
                ShortTermContextTypes s = STCData.PreviousSTC(stc), was;
                do
                {
                    list.AddRange(ActionAvailabilityData.Get(actor, target, s, ltr, true, null, true));
                    was = s;
                    s = STCData.PreviousSTC(s);
                }
                while (was != s);
            }
            if (flag) { ctr[2743] += 1 + list.Count; return list; }  // EA's copy keeps every item
            List<SocialInteractionCandidate> kept = new List<SocialInteractionCandidate>();
            foreach (SocialInteractionCandidate item in list)
                if (!ActionData.Get(item.Name).DoesSocialOnlyAppearWhenSTCIsCurrent) kept.Add(item);
            return kept;
        }

        static bool StcSame(List<SocialInteractionCandidate> a, List<SocialInteractionCandidate> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Name != b[i].Name || !ReferenceEquals(a[i].Topic, b[i].Topic) || a[i].OnlyAppearsInTraitMenu != b[i].OnlyAppearsInTraitMenu) return false;
            return true;
        }

        public class SocialStcHook
        {
            public static List<SocialInteractionCandidate> StcHook(CommodityTypes commodity, Sim actor, Sim target, Relationship r, Conversation c, bool isAutonomous)
            {
                if (CcOn())  // callcensus (CallCensus.cs): this hook doubles as the census hook for GetSocialsForSTC
                {
                    CcCount(kCcStc); uint t = CcT0();
                    try { return StcBody(commodity, actor, target, r, c, isAutonomous); } finally { CcTime(kCcStc, t); }
                }
                return StcBody(commodity, actor, target, r, c, isAutonomous);
            }
            static List<SocialInteractionCandidate> StcBody(CommodityTypes commodity, Sim actor, Sim target, Relationship r, Conversation c, bool isAutonomous)
            {
                if (!isAutonomous || !ShortcutOn("stcsocials") || !StcReady()) return StcOrig(commodity, actor, target, r, c, isAutonomous);
                int[] ctr = PatchCounters();
                List<SocialInteractionCandidate> ours = StcAutonomous(commodity, actor, target, r, c, ctr);  // EA's exceptions pass through as in EA
                ctr[2742]++;
                if (!ShortcutOn("stcsocialscheck")) return ours;
                ctr[2744]++;
                List<SocialInteractionCandidate> ea = StcOrig(commodity, actor, target, r, c, isAutonomous);
                if (!StcSame(ours, ea))
                {
                    ctr[2745]++;
                    if (!sStcLogged)
                    {
                        sStcLogged = true;
                        try { DiagLine("stcsocialscheck: mismatch for commodity " + commodity + ": ours " + (ours == null ? -1 : ours.Count) + " items, EA " + (ea == null ? -1 : ea.Count)); } catch (Exception) { }
                    }
                }
                return ea;
            }
            public static List<SocialInteractionCandidate> StcOrig(CommodityTypes commodity, Sim actor, Sim target, Relationship r, Conversation c, bool isAutonomous) { return null; } // receives Conversation.GetSocialsForSTC's original header
        }
    }
}
