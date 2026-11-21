using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Relationships and households (docs/574 §1-3), each off by default. EA bug fixes; no Sim is moved.
    //
    // wildherdmother (kWildHerdMother, docs/574 §1) [verified, WildHorsePool.FindNewHerdMother / Genealogy
    //   .RemoveDirectRelation]: when the herd mother leaves the pool (tamed or adopted by the player, or dead), EA loops
    //   over her Genealogy.Children and calls child.RemoveDirectRelation(mother), which removes the child from that same
    //   list: the loop throws as soon as she has a foal. RemovePet calls it first, so taming or adopting a mare with foals
    //   aborts before the household add and her foals are left half re-parented; with no other adult mare the setter also
    //   throws on null. Fix: the same steps over a copy of the list, a missing mother tolerated, and no new mother stored
    //   as "none". Counters [2654] mothers replaced, [2655] errors.
    // splitpartner (kSplitPartner, docs/574 §2) [verified, StoryProgression SplitHousehold]: when story progression splits
    //   an NPC household, the Sim who leaves has Partner set to null, but the partner who stays keeps pointing at them
    //   (EA's own ClearPartner clears both sides), which blocks that Sim's new romances. The candidate list also takes
    //   any Sim with an ex in the household, so a child or teen can be picked and moved out alone (the age filter of
    //   CanSplit is missing). Fix: after Execute the stayer's one-sided Partner is cleared; IsValid refuses a leaver below
    //   young adult. Counters [2656] partners cleared / splits refused, [2657] errors.
    // movingflag (kMovingFlag, docs/574 §3) [code verified, trigger inferred]: MovingSituation.sbMovingInProgress is a
    //   persisted static cleared only by the situation's CleanUp; a moving situation that ends without it (deleted waver,
    //   a reset, a save made mid-move) leaves the flag set, and Edit Town, the phone/computer move options and move-in
    //   socials then all fail with "move in progress". Fix: at world load, a set flag with no MovingSituation left is
    //   cleared. Counters [2658] flags cleared, [2659] errors.
    // Checks: HouseholdLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kHlAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sWhMother = typeof(WildHorsePool).GetField("mHerdMotherId", kHlAll);
        static readonly Type sSpType = typeof(Sims3.Gameplay.StoryProgression.StoryAction).Assembly.GetType("Sims3.Gameplay.StoryProgression.SplitHousehold");
        static readonly FieldInfo sSpHousehold = sSpType == null ? null : sSpType.GetField("mHouseholdId", kHlAll);
        static readonly FieldInfo sSpLeaver = sSpType == null ? null : sSpType.GetField("mSimToLeaveId", kHlAll);
        static readonly FieldInfo sMvFlag = typeof(Sims3.Gameplay.Moving.MovingSituation).GetField("sbMovingInProgress", kHlAll);

        internal static string HlMissing()
        {
            string m = "";
            if (sWhMother == null) m += "WildHorsePool.mHerdMotherId ";
            if (sSpHousehold == null) m += "SplitHousehold.mHouseholdId ";
            if (sSpLeaver == null) m += "SplitHousehold.mSimToLeaveId ";
            if (sMvFlag == null) m += "MovingSituation.sbMovingInProgress ";
            return m.Trim();
        }

        static void ApplyHouseholdLogic()
        {
            if (sWhMother != null) Patch(typeof(WildHorsePool), "FindNewHerdMother", typeof(WildHerdMotherHook), "FindHook", "FindOrig");
            if (sSpHousehold != null && sSpLeaver != null)
            {
                Patch(sSpType, "Execute", typeof(SplitPartnerHook), "ExecuteHook", "ExecuteOrig");
                Patch(sSpType, "IsValid", typeof(SplitPartnerHook), "ValidHook", "ValidOrig");
            }
            if (HlMissing().Length > 0) AppliedLog().Add("household logic: members not found (" + HlMissing() + ")");
        }

        // ---- wildherdmother ----
        // EA's loop: remove each child from the list being walked (on = walk a copy). Returns the children moved; threw =
        // the walk failed (List enumerator "collection was modified").
        internal static int WhRelink(bool on, List<int> children, out bool threw)
        {
            threw = false;
            int moved = 0;
            try
            {
                IEnumerable<int> walk = on ? (IEnumerable<int>)children.ToArray() : children;
                foreach (int c in walk) { children.Remove(c); moved++; }
            }
            catch (InvalidOperationException) { threw = true; }
            return moved;
        }

        public class WildHerdMotherHook
        {
            public SimDescription FindHook()
            {
                if (!ShortcutOn("wildherdmother")) return FindOrig();
                try
                {
                    WildHorsePool me = (WildHorsePool)(object)this;
                    ulong motherId = (ulong)sWhMother.GetValue(me);
                    Household pets = Household.PetHousehold;
                    SimDescription next = null;
                    if (pets != null && me.SimDescriptionIds != null)
                        foreach (ulong id in me.SimDescriptionIds)
                        {
                            if (id == motherId) continue;
                            SimDescription d = pets.CurrentMembers.GetSimDescriptionFromId(id);
                            if (d != null && d.IsFemale && d.AdultOrAbove) { next = d; break; }
                        }
                    SimDescription mother = pets == null ? null : me.HerdMother;
                    if (mother != null && mother.Genealogy != null)
                        foreach (Genealogy child in mother.Genealogy.Children.ToArray())
                        {
                            child.RemoveDirectRelation(mother.Genealogy);
                            if (next != null) next.Genealogy.AddChild(child);
                        }
                    if (next != null) me.HerdMother = next;
                    else sWhMother.SetValue(me, 0UL);
                    PatchCounters()[2654]++;
                    return next;
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2655]++; return null; }
            }
            public SimDescription FindOrig() { return null; }
        }

        // ---- splitpartner ----
        // After the split: clear the stayer's Partner when it still points at a leaver whose own Partner EA cleared.
        internal static bool SpClearStayer(bool on, bool stayerPointsAtLeaver, bool leaverCleared)
        {
            return on && stayerPointsAtLeaver && leaverCleared;
        }

        // IsValid: EA's answer, refused for a leaver below young adult.
        internal static bool SpValid(bool on, bool eaValid, bool leaverYaOrAbove)
        {
            return eaValid && (!on || leaverYaOrAbove);
        }

        static SimDescription SpLeaver(object action)
        {
            Household h = Household.Find((ulong)sSpHousehold.GetValue(action));
            return h == null ? null : h.FindMember((ulong)sSpLeaver.GetValue(action));
        }

        public class SplitPartnerHook
        {
            public void ExecuteHook()
            {
                SimDescription leaver = null, partner = null;
                bool on = ShortcutOn("splitpartner");
                try
                {
                    if (on) { leaver = SpLeaver(this); partner = leaver == null ? null : leaver.Partner; }
                }
                catch (Exception) { PatchCounters()[2657]++; }
                ExecuteOrig();
                try
                {
                    if (partner != null && SpClearStayer(true, partner.Partner == leaver, leaver.Partner == null))
                    {
                        partner.Partner = null;
                        PatchCounters()[2656]++;
                    }
                }
                catch (Exception) { PatchCounters()[2657]++; }
            }
            public void ExecuteOrig() { }

            public bool ValidHook()
            {
                bool r = ValidOrig();
                try
                {
                    if (r && ShortcutOn("splitpartner"))
                    {
                        SimDescription leaver = SpLeaver(this);
                        if (leaver != null && !SpValid(true, true, leaver.YoungAdultOrAbove)) { PatchCounters()[2656]++; return false; }
                    }
                }
                catch (Exception) { PatchCounters()[2657]++; }
                return r;
            }
            public bool ValidOrig() { return false; }
        }

        // ---- movingflag ----
        internal static bool MvClear(bool on, bool flagSet, bool situationLeft)
        {
            return on && flagSet && !situationLeft;
        }

        public static void MovingFlagWorldLoad()
        {
            try
            {
                if (!ShortcutOn("movingflag") || sMvFlag == null) return;
                bool left = false;
                foreach (Sims3.Gameplay.Autonomy.Situation s in Sims3.Gameplay.Autonomy.Situation.sAllSituations)
                    if (s is Sims3.Gameplay.Moving.MovingSituation) { left = true; break; }
                if (MvClear(true, Sims3.Gameplay.Moving.MovingSituation.MovingInProgress, left))
                {
                    sMvFlag.SetValue(null, false);
                    PatchCounters()[2658]++;
                }
            }
            catch (Exception) { PatchCounters()[2659]++; }
        }
    }
}
