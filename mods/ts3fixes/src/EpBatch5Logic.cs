using System;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Decorations;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.ShelvesStorage;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EP batch 5 (docs/587 §11): one-shot effects that EA starts and drops (scan pass 4, docs/583 §8.1 P4-2), one switch
    // per family. [verified, decompiled; native lifetime inferred] Each site does VisualEffect.Create, ParentTo or
    // SetPosAndOrient, Start() on a local and returns: no SetAutoDestroy, no Stop, no Dispose. EA's own
    // SubmitOneShotEffect is Start + SetAutoDestroy(true), so these engine effects are never destroyed (one handle per use;
    // a looping asset keeps playing on its parent). Fix (store-and-stop's lighter form): the site runs EA's body with a
    // marker; the last site effect (by asset name) VisualEffect.Create returns during that body (the dropped one in every
    // site) gets SetAutoDestroy(true) after EA's Start, exactly the tail of SubmitOneShotEffect. VisualEffect.Create gets
    // a wrapper that only stores its result while a marker is open and the name is a site's. Wrappers, not copies: EA's
    // bodies run unchanged (StartEvent and ResetTheFuture reach a yield deep down; the swap audit keeps them, stub remap).
    // Not counted: FireworksBase.StartFX (stored in mFireworkFX and disposed by EA); SnackBowl (docs/587 8.5).
    // Every switch off by default. Checks: EpBatch5Checks.cs. Slots 3283-3300 (block fixes-1500).
    //   chemistryvfx      [3283-3285] BuffHorrified, BuffTurboSleep, BuffBrightenedDay, BuffChemistryLabPotion OnRemoval;
    //                                 BladderFlowPotion+MotiveHelper.RestoreBladderCallback; ChemistryLab+MakePotionBase.StartEvent
    //   dartboardvfx      [3286-3288] Dartboard+TakeTurnThrowingDarts.PlayDartEffect (4 throw results)
    //   wardrobewandvfx   [3289-3291] Wardrobe+UpgradeWardrobe.ShowSuccessVfx / ShowFailVfx
    //   petdroolvfx       [3292-3294] ServingContainer+EatOffFloor.DroolEvent / SpitUpEvent
    //   timeportalvfx     [3295-3297] TimePortal+ResetTimeContinuum.ResetTheFuture
    //   haystackvfx       [3298-3300] HayStack.PlayTransitionEffect
    // Dropped: Consignment ReputationTrackedStat (P4-1): the shipped tuning has 9 levels for 9 names, so the fallback is
    // already RockBottom; only the code defaults (8 levels) show the flaw (docs/587 §11.1).
    // All household-visible (effects on household Sims and objects). Counters per family: effects auto-destroyed,
    // effects dropped with the switch off, errors.
    public static partial class Fixes
    {
        static readonly string[] kOsFam = { "chemistryvfx", "dartboardvfx", "wardrobewandvfx", "petdroolvfx", "timeportalvfx", "haystackvfx" };
        static readonly int[] kOsSlot = { 3283, 3286, 3289, 3292, 3295, 3298 };
        static int sOsOpen;
        static VisualEffect sOsLast;
        static string sOsMissing = "";

        // The effects the sites create (all one-shot assets; HayStack's names are a non-tunable static). The Create
        // wrapper stores only these: StartEvent and ResetTheFuture have a deep path to a yield (swap audit), and another
        // task's effect created during such a yield must never be taken for the site's.
        static readonly string[] kOsNames = { "ep4PotionWearOff", "ep4PotionWearOffChild", "ep4LabTablePotionDiscovery",
            "ep3DartboardBad", "ep3DartboardOK", "ep3DartboardGood", "ep3DartboardBullseye", "ep7WandSpellSun_main", "ep7WandFail_main",
            "ep5DogDroolJaw", "ep5DogLittleDroolJaw", "ep5DogDroolSpitUp", "ep11PortalReset_main",
            "ep5HayStackMostlyEmpty2None", "ep5HayStackHalfFull2MostlyEmpty", "ep5HayStackFull2HalfFull" };

        // Pure: is this a site effect?
        internal static bool OsIsSiteEffect(string name)
        {
            if (name == null) return false;
            for (int i = 0; i < kOsNames.Length; i++) if (kOsNames[i] == name) return true;
            return false;
        }

        // Pure: is the dropped effect set to auto-destroy?
        internal static bool OsAutoDestroy(bool on, bool created) { return on && created; }

        static void OsMiss(System.Text.StringBuilder miss, bool ok, string what)
        {
            if (!ok) miss.Append(miss.Length == 0 ? "" : ", ").Append(what);
        }

        static void ApplyOneShotVfx()
        {
            const BindingFlags n = BindingFlags.Public | BindingFlags.NonPublic;
            Type[] bmbi = new Type[] { typeof(BuffManager), typeof(BuffInstance) };
            Type[] ev = new Type[] { typeof(StateMachineClient), typeof(IEvent) };
            System.Text.StringBuilder miss = new System.Text.StringBuilder();
            OsMiss(miss, Patch(typeof(BuffHorrified), "OnRemoval", bmbi, typeof(BEp5Os0), "H", "O"), "BuffHorrified.OnRemoval");
            OsMiss(miss, Patch(typeof(BuffTurboSleep), "OnRemoval", bmbi, typeof(BEp5Os1), "H", "O"), "BuffTurboSleep.OnRemoval");
            OsMiss(miss, Patch(typeof(BuffBrightenedDay), "OnRemoval", bmbi, typeof(BEp5Os2), "H", "O"), "BuffBrightenedDay.OnRemoval");
            OsMiss(miss, Patch(typeof(BuffChemistryLabPotion), "OnRemoval", bmbi, typeof(BEp5Os3), "H", "O"), "BuffChemistryLabPotion.OnRemoval");
            OsMiss(miss, Patch(typeof(BladderFlowPotion.MotiveHelper), "RestoreBladderCallback", Type.EmptyTypes, typeof(BEp5Os4), "H", "O"), "BladderFlowPotion+MotiveHelper.RestoreBladderCallback");
            OsMiss(miss, Patch(typeof(ChemistryLab.MakePotionBase), "StartEvent", ev, typeof(BEp5Os5), "H", "O"), "ChemistryLab+MakePotionBase.StartEvent");
            OsMiss(miss, Patch(typeof(Dartboard.TakeTurnThrowingDarts), "PlayDartEffect", ev, typeof(BEp5Os6), "H", "O"), "Dartboard+TakeTurnThrowingDarts.PlayDartEffect");
            OsMiss(miss, Patch(typeof(Wardrobe).GetNestedType("UpgradeWardrobe", n), "ShowSuccessVfx", ev, typeof(BEp5Os7), "H", "O"), "Wardrobe+UpgradeWardrobe.ShowSuccessVfx");
            OsMiss(miss, Patch(typeof(Wardrobe).GetNestedType("UpgradeWardrobe", n), "ShowFailVfx", ev, typeof(BEp5Os8), "H", "O"), "Wardrobe+UpgradeWardrobe.ShowFailVfx");
            OsMiss(miss, Patch(typeof(ServingContainer.EatOffFloor), "DroolEvent", ev, typeof(BEp5Os9), "H", "O"), "ServingContainer+EatOffFloor.DroolEvent");
            OsMiss(miss, Patch(typeof(ServingContainer.EatOffFloor), "SpitUpEvent", ev, typeof(BEp5Os10), "H", "O"), "ServingContainer+EatOffFloor.SpitUpEvent");
            OsMiss(miss, Patch(typeof(TimePortal).GetNestedType("ResetTimeContinuum", n), "ResetTheFuture", ev, typeof(BEp5Os11), "H", "O"), "TimePortal+ResetTimeContinuum.ResetTheFuture");
            OsMiss(miss, Patch(typeof(HayStack), "PlayTransitionEffect", Type.EmptyTypes, typeof(BEp5Os12), "H", "O"), "HayStack.PlayTransitionEffect");
            // the marker wrapper; only stores the result while a site above is running
            OsMiss(miss, Patch(typeof(VisualEffect), "Create", new Type[] { typeof(string) }, typeof(BEp5OsCreate), "CreateHook", "CreateOrig"), "VisualEffect.Create");
            sOsMissing = miss.ToString();
            if (sOsMissing.Length > 0) AppliedLog().Add("oneshot vfx: not found: " + sOsMissing);
        }

        // Opens a marker; returns the previous "last effect" so nested markers restore it.
        static VisualEffect OsBegin() { VisualEffect prev = sOsLast; sOsLast = null; sOsOpen++; return prev; }

        static void OsEnd(int fam, VisualEffect prev)
        {
            VisualEffect v = sOsLast;
            sOsOpen--; sOsLast = prev;
            if (v == null) return;   // nothing created: equal in both switch states
            int slot = kOsSlot[fam];
            try
            {
                bool on = ShortcutOn(kOsFam[fam]);
                if (OsAutoDestroy(on, v != null)) { v.SetAutoDestroy(true); PatchCounters()[slot]++; }
                else if (v != null && !on) PatchCounters()[slot + 1]++;
            }
            catch (Exception) { PatchCounters()[slot + 2]++; }
        }

        public class BEp5OsCreate
        {
            public static VisualEffect CreateHook(string effectName)
            {
                VisualEffect v = CreateOrig(effectName);
                if (sOsOpen > 0 && v != null && OsIsSiteEffect(effectName)) sOsLast = v;
                return v;
            }
            public static VisualEffect CreateOrig(string effectName) { return null; } // VisualEffect.Create
        }

        public class BEp5Os0 { public void H(BuffManager bm, BuffInstance bi) { VisualEffect p = OsBegin(); try { O(bm, bi); } finally { OsEnd(0, p); } } public void O(BuffManager bm, BuffInstance bi) { } } // BuffHorrified.OnRemoval
        public class BEp5Os1 { public void H(BuffManager bm, BuffInstance bi) { VisualEffect p = OsBegin(); try { O(bm, bi); } finally { OsEnd(0, p); } } public void O(BuffManager bm, BuffInstance bi) { } } // BuffTurboSleep.OnRemoval
        public class BEp5Os2 { public void H(BuffManager bm, BuffInstance bi) { VisualEffect p = OsBegin(); try { O(bm, bi); } finally { OsEnd(0, p); } } public void O(BuffManager bm, BuffInstance bi) { } } // BuffBrightenedDay.OnRemoval
        public class BEp5Os3 { public void H(BuffManager bm, BuffInstance bi) { VisualEffect p = OsBegin(); try { O(bm, bi); } finally { OsEnd(0, p); } } public void O(BuffManager bm, BuffInstance bi) { } } // BuffChemistryLabPotion.OnRemoval
        public class BEp5Os4 { public void H() { VisualEffect p = OsBegin(); try { O(); } finally { OsEnd(0, p); } } public void O() { } } // BladderFlowPotion+MotiveHelper.RestoreBladderCallback
        public class BEp5Os5 { public void H(StateMachineClient s, IEvent e) { VisualEffect p = OsBegin(); try { O(s, e); } finally { OsEnd(0, p); } } public void O(StateMachineClient s, IEvent e) { } } // ChemistryLab+MakePotionBase.StartEvent
        public class BEp5Os6 { public void H(StateMachineClient s, IEvent e) { VisualEffect p = OsBegin(); try { O(s, e); } finally { OsEnd(1, p); } } public void O(StateMachineClient s, IEvent e) { } } // Dartboard+TakeTurnThrowingDarts.PlayDartEffect
        public class BEp5Os7 { public void H(StateMachineClient s, IEvent e) { VisualEffect p = OsBegin(); try { O(s, e); } finally { OsEnd(2, p); } } public void O(StateMachineClient s, IEvent e) { } } // Wardrobe+UpgradeWardrobe.ShowSuccessVfx
        public class BEp5Os8 { public void H(StateMachineClient s, IEvent e) { VisualEffect p = OsBegin(); try { O(s, e); } finally { OsEnd(2, p); } } public void O(StateMachineClient s, IEvent e) { } } // Wardrobe+UpgradeWardrobe.ShowFailVfx
        public class BEp5Os9 { public void H(StateMachineClient s, IEvent e) { VisualEffect p = OsBegin(); try { O(s, e); } finally { OsEnd(3, p); } } public void O(StateMachineClient s, IEvent e) { } } // ServingContainer+EatOffFloor.DroolEvent
        public class BEp5Os10 { public void H(StateMachineClient s, IEvent e) { VisualEffect p = OsBegin(); try { O(s, e); } finally { OsEnd(3, p); } } public void O(StateMachineClient s, IEvent e) { } } // ServingContainer+EatOffFloor.SpitUpEvent
        public class BEp5Os11 { public void H(StateMachineClient s, IEvent e) { VisualEffect p = OsBegin(); try { O(s, e); } finally { OsEnd(4, p); } } public void O(StateMachineClient s, IEvent e) { } } // TimePortal+ResetTimeContinuum.ResetTheFuture
        public class BEp5Os12 { public void H() { VisualEffect p = OsBegin(); try { O(); } finally { OsEnd(5, p); } } public void O() { } } // HayStack.PlayTransitionEffect
    }
}
