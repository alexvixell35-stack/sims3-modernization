using System;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // convmerge (tuning kConvMerge, off by default; docs/566 §2). EA bug fix in group conversations (Conversation.Merge).
    //
    // [verified, decompiled] Each Conversation owns a SocialGroupDiscourageRegion: every social start adds both Sims'
    // spots and calls UpdateFootprint, which creates a hidden "DynamicSlot" object in the world and registers a
    // discourage route footprint (Route.AddDiscourageFootprint) around the group, so other Sims walk around it. EA removes
    // it with DiscourageRegion.Cleanup (destroys the object) when the conversation ends (Conversation.FinishConversation,
    // SocialComponent.LeaveConversation with one Sim left). When a Sim of one conversation socializes with a Sim of
    // another (SocialComponent.JoinConversationWith, both already talking), Conversation.Merge builds a new conversation
    // for all members and repoints every member at it; the two old conversations are dropped without that cleanup. Their
    // footprint objects stay in the world with their discourage footprints at the old group spots: Sims keep routing
    // around invisible patches where a group once stood, and one hidden object leaks per merged conversation (common at
    // parties and on community lots). The old group situations end on EA's own 45-minute give-up alarm; only the regions
    // leak.
    // Fix: after EA's Merge, the two old conversations' discourage regions are cleaned with EA's DynamicRegionBase.Cleanup
    // (the new conversation builds its own on its next social). Nothing else in the merge changes.
    //
    // Counters: [2544] old conversation regions cleaned, [2545] errors. Check: ConvMergeChecks.cs.
    public static partial class Fixes
    {
        static void ApplyConvMerge()
        {
            Patch(typeof(Conversation), "Merge", typeof(CmHook), "MergeHook", "MergeOrig");
        }

        // ---- decision (also used by the check): clean an old conversation's region after a merge? ----
        internal static bool CmCleanOld(bool on, bool oldIsNew, bool hasFootprint)
        {
            return on && !oldIsNew && hasFootprint;
        }

        public class CmHook
        {
            public static Conversation MergeHook(Conversation c1, Conversation c2)
            {
                Conversation merged = MergeOrig(c1, c2);
                if (ShortcutOn("convmerge"))
                {
                    try
                    {
                        CmClean(c1, merged);
                        CmClean(c2, merged);
                    }
                    catch (Exception) { PatchCounters()[2545]++; }
                }
                return merged;
            }
            public static Conversation MergeOrig(Conversation c1, Conversation c2) { return null; } // receives Conversation.Merge's original body

            static void CmClean(Conversation old, Conversation merged)
            {
                if (old == null || old.DiscourageRegion == null) return;
                if (!CmCleanOld(true, old == merged, old.DiscourageRegion.DynamicFootprint != null)) return;
                PatchCounters()[2544]++;
                old.DiscourageRegion.Cleanup();
            }
        }
    }
}
