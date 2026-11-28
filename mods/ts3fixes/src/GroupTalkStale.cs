using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;

namespace Ts3Fixes
{
    // grouptalkstale (tuning kGroupTalkStale, off by default; docs/566 §1). EA bug fix in Sim group talk (the ambient
    // "chat while doing something" of chess, fishing, eating, cooking, homework, pool, hot tub, swings, ...).
    //
    // [verified, decompiled] A Sim's group talk lives in Sim.mGroupTalk (private Sim+GroupTalk: a member list, the
    // current speaker, talk times). RegisterGroupTalk joins the group of another Sim on the same lot (or starts one);
    // only UnregisterGroupTalk takes a Sim out again (RemoveMember + mGroupTalk = null). Many activities call TryGroupTalk
    // every loop tick (which registers on first use through ShouldDoGroupTalk) and never unregister (ChessTable,
    // EatHeldFood, Homework, the cooking pots, PoolTable, HotTub, ...), so a Sim keeps the group after it walks away,
    // goes home or leaves the lot. And RegisterGroupTalk, when the Sim already has a group, joins or creates a new one
    // without leaving the old: the old group keeps it as a phantom member. EA never drops a member that left. Effects:
    //  - a Sim that left (or whose group went stale) still counts in its old group: when the remaining Sim group-talks,
    //    the absent Sim can be the current speaker or the Sim talked to; a listener gets
    //    Relationship.UpdateRelationshipForGroupTalk with a Sim on another lot, members get the social motive bump and
    //    "Chat" kSocialInteraction events (wants, opportunities, relationship events) from a Sim who is not there;
    //  - the departed Sim, doing a later TryGroupTalk elsewhere (no re-register: mGroupTalk is still set), "talks" with
    //    the Sims of its old group on the old lot (CanMemberBeSeenBy only compares room ids and facing, and room ids
    //    repeat between lots), with the same relationship and event effects;
    //  - it never joins the Sims it is actually with, since only a Sim without a group registers.
    // Fix (EA's own methods): in ShouldDoGroupTalk, before EA's body, members of this Sim's group that are destroyed or on
    // another lot are taken out (UnregisterGroupTalk when the group is still theirs, else the group's RemoveMember); if
    // that left this Sim alone it unregisters too, so EA's body registers it fresh with the Sims on its lot. In
    // RegisterGroupTalk, a Sim that already has a group leaves it (UnregisterGroupTalk) before EA joins or starts one.
    // Groups whose members are all on the lot run EA's code unchanged.
    //
    // Counters: [2541] departed members dropped, [2542] Sims moved out of an old group on re-register, [2543] errors.
    // Check: GroupTalkStaleChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sGtField = typeof(Sim).GetField("mGroupTalk", kInstAll);
        static readonly Type sGtType = typeof(Sim).GetNestedType("GroupTalk", BindingFlags.NonPublic);
        static readonly PropertyInfo sGtMembers = sGtType == null ? null : sGtType.GetProperty("Members", kInstAll);
        static readonly MethodInfo sGtRemove = sGtType == null ? null : sGtType.GetMethod("RemoveMember", kInstAll, null, new Type[] { typeof(Sim) }, null);

        static void ApplyGroupTalkStale()
        {
            if (sGtField == null || sGtMembers == null || sGtRemove == null)
            {
                AppliedLog().Add("grouptalkstale: Sim group-talk members not found, not patched");
                return;
            }
            Patch(typeof(Sim), "ShouldDoGroupTalk", typeof(GtHook), "ShouldHook", "ShouldOrig");
            Patch(typeof(Sim), "RegisterGroupTalk", typeof(GtHook), "RegisterHook", "RegisterOrig");
        }

        // ---- decision (also used by the check): is this other member gone from the speaker's group talk? ----
        internal static bool GtMemberGone(bool on, bool isSelf, bool destroyed, bool sameLot)
        {
            return on && !isSelf && (destroyed || !sameLot);
        }

        // Takes the departed members out of me's group; true when it dropped any.
        static bool GtPrune(Sim me, object group)
        {
            List<Sim> members = (List<Sim>)sGtMembers.GetValue(group, null);
            if (members == null) return false;
            bool dropped = false;
            foreach (Sim m in new List<Sim>(members))
            {
                bool destroyed = m == null || m.HasBeenDestroyed;
                if (!GtMemberGone(true, m == me, destroyed, !destroyed && m.LotCurrent == me.LotCurrent)) continue;
                PatchCounters()[2541]++;
                dropped = true;
                if (!destroyed && sGtField.GetValue(m) == group) m.UnregisterGroupTalk();
                else sGtRemove.Invoke(group, new object[] { m });
            }
            return dropped;
        }

        public class GtHook
        {
            public bool ShouldHook()
            {
                if (ShortcutOn("grouptalkstale"))
                {
                    try
                    {
                        object st = this;
                        Sim me = (Sim)st;
                        object group = sGtField.GetValue(me);
                        if (group != null && GtPrune(me, group))
                        {
                            List<Sim> left = (List<Sim>)sGtMembers.GetValue(group, null);
                            if (left == null || left.Count <= 1) me.UnregisterGroupTalk();  // EA's body re-registers with the Sims here
                        }
                    }
                    catch (Exception) { PatchCounters()[2543]++; }
                }
                return ShouldOrig();
            }
            public bool ShouldOrig() { return false; } // receives Sim.ShouldDoGroupTalk's original body

            public void RegisterHook()
            {
                if (ShortcutOn("grouptalkstale"))
                {
                    try
                    {
                        object st = this;
                        Sim me = (Sim)st;
                        if (sGtField.GetValue(me) != null)
                        {
                            PatchCounters()[2542]++;
                            me.UnregisterGroupTalk();
                        }
                    }
                    catch (Exception) { PatchCounters()[2543]++; }
                }
                RegisterOrig();
            }
            public void RegisterOrig() { } // receives Sim.RegisterGroupTalk's original body
        }
    }
}
