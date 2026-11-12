using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Socializing;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // attractionalarm + uncleguard (tunings kAttractionAlarm, kUncleGuard; default off until measured; docs/191 "relationships").
    //
    // attractionalarm, EA bug [verified decompiled, AttractionNPCBehaviorController]: the controller a romantic attraction
    // creates (Relationship.CalculateAttractionScore) owns three self re-arming, AlwaysPersisted daily alarms (date call,
    // gift, love letter). Each handler first asks IsValidRelationship, which only checks SimDescription.IsValid (id != 0)
    // and IsDead on both sides plus "a selectable Sim is in it". A Sim that was removed (moved out, deleted, culled by story
    // progression) keeps its id, so for it the check passes: the alarms keep firing every day, mailing gifts and love letters
    // and placing phone calls for a Sim that no longer exists, and they keep the controller, the relationship and both
    // descriptions in every save. Nothing disposes the controller when the relationship leaves the tables.
    // The pass (switch on): IsValidRelationship also returns false when either side is a disposed description
    // (!IsValidDescription) or the relationship is no longer the one sAllRelationships holds for the pair; EA's handlers
    // then Dispose the controller (its alarms go). Not applied while changing worlds or on vacation (descriptions of the
    // home world are not live there). Live relationships get EA's answer unchanged.
    //
    // uncleguard, EA bug [verified decompiled, Genealogy.IsFatherSideUncle / IsMotherSideUncle]: both read
    // parent.IMiniSimDescription.IsFemale with no null check; a genealogy node whose Sim was disposed without a MiniSim
    // (Household.Dispose -> SimDescription.Dispose(addMiniSim: false)) returns null there, so the relationship panel
    // (HudModel UIRelationship -> GetMyFamilialDescriptionFor) and MiniSimDescription.AddMiniRelationship throw.
    // The pass (exact crash guard): the same walk, skipping parents with no description (their side is unknown).
    // Counters: [2245] attraction handlers seen, [2246] controllers stopped for a removed Sim, [2247] errors,
    // [2248] uncle checks with a parent with no description (also with the switch off), [2249] uncle checks, [2250] errors.
    public static partial class Fixes
    {
        static FieldInfo sRagParent;

        static void ApplyRelAlarmGuard()
        {
            sRagParent = typeof(AttractionNPCBehaviorController).GetField("mParentRelationship", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (sRagParent != null)
                Patch(typeof(AttractionNPCBehaviorController), "IsValidRelationship", Type.EmptyTypes, typeof(RagHook), "ValidHook", "ValidOrig");
            else AppliedLog().Add("attractionalarm: mParentRelationship not found, not patched");
            Patch(typeof(Genealogy), "IsFatherSideUncle", new Type[] { typeof(Genealogy), typeof(Genealogy) }, typeof(RagUncle), "FatherHook", "FatherOrig");
            Patch(typeof(Genealogy), "IsMotherSideUncle", new Type[] { typeof(Genealogy), typeof(Genealogy) }, typeof(RagUncle), "MotherHook", "MotherOrig");
        }

        public class RagHook
        {
            public bool ValidHook()
            {
                bool ea = ValidOrig();
                int[] c = PatchCounters();
                c[2245]++;
                if (!ea || !ShortcutOn("attractionalarm")) return ea;
                try
                {
                    if (GameStates.IsTravelling || GameStates.IsOnVacation) return ea;
                    Relationship r = sRagParent.GetValue(this) as Relationship;
                    if (r == null) return ea;
                    SimDescription a = r.SimDescriptionA, b = r.SimDescriptionB;
                    bool dead = a == null || b == null || !a.IsValidDescription || !b.IsValidDescription;
                    if (!dead)
                    {
                        Dictionary<SimDescription, Relationship> t;
                        Relationship cur;
                        dead = !Relationship.sAllRelationships.TryGetValue(a, out t) || t == null || !t.TryGetValue(b, out cur) || !object.ReferenceEquals(cur, r);
                    }
                    if (dead) { c[2246]++; return false; }
                }
                catch (Exception) { c[2247]++; }
                return ea;
            }
            public bool ValidOrig() { return false; }
        }

        public static class RagUncle
        {
            static bool Side(Genealogy uncle, Genealogy nephew, bool female)
            {
                int[] c = PatchCounters();
                c[2249]++;
                bool sawNull = false;
                foreach (Genealogy parent in nephew.Parents)
                {
                    if (parent == null) continue;
                    IMiniSimDescription d = parent.IMiniSimDescription;
                    if (d == null) { sawNull = true; continue; }
                    if (d.IsFemale != female) continue;
                    foreach (Genealogy sibling in parent.GetSiblings())
                        if (sibling == uncle || sibling.Spouse == uncle) { if (sawNull) c[2248]++; return true; }
                }
                if (sawNull) c[2248]++;
                return false;
            }
            static bool AnyNull(Genealogy nephew)
            {
                try { foreach (Genealogy p in nephew.Parents) if (p != null && p.IMiniSimDescription == null) return true; }
                catch (Exception) { PatchCounters()[2250]++; }
                return false;
            }
            public static bool FatherHook(Genealogy uncle, Genealogy nephew)
            {
                if (nephew != null && AnyNull(nephew)) { PatchCounters()[2248]++; if (ShortcutOn("uncleguard")) return Side(uncle, nephew, false); }
                return FatherOrig(uncle, nephew);
            }
            public static bool FatherOrig(Genealogy uncle, Genealogy nephew) { return false; }
            public static bool MotherHook(Genealogy uncle, Genealogy nephew)
            {
                if (nephew != null && AnyNull(nephew)) { PatchCounters()[2248]++; if (ShortcutOn("uncleguard")) return Side(uncle, nephew, true); }
                return MotherOrig(uncle, nephew);
            }
            public static bool MotherOrig(Genealogy uncle, Genealogy nephew) { return false; }
        }
    }
}
