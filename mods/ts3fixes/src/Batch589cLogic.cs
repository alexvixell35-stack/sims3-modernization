using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.UI;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Batch 11c (docs/589 usage sweep, U17, U19, U21-U23): EA bug fixes in the Sim core and everyday objects. All default
    // off; all reach the household (as EA intended). Slots 1700-1714 (fixes-1700: 1351-1399 is full, 1400-1699 is the EP
    // agent's). simler90 UPDATE242 leaves BuffManager, TV's disc code, Fridge_OpenFridge, Posture and IdleManager as EA.
    public static partial class Fixes
    {
        // ---- B11 §17 buffremovedelayed (U17) ----------------------------------------------------------------------------
        // [verified, decompiled; frequency inferred] BuffManager.RemoveElement(ulong, bool): when a delayed add of the buff
        // is pending, it removes that timer and returns, so a live copy of the same buff stays on the Sim. Fix: when the
        // buff was live before the call and is still live after it (only that early return leaves it), EA's removal runs
        // once more, now past the timer, as base.RemoveElement would have. Counters: [1700] live buffs removed after a
        // pending timer, [1701] left on the Sim (switch off), [1702] errors.
        static void ApplyBuffRemoveDelayed()
        {
            Patch(typeof(BuffManager), "RemoveElement", new Type[] { typeof(ulong), typeof(bool) }, typeof(BBrdHook), "RemoveHook", "RemoveOrig");
        }

        // Pure: did EA's call return early with the buff still live (fix runs the removal again)?
        internal static bool BrLeft(bool liveBefore, bool liveAfter) { return liveBefore && liveAfter; }

        public class BBrdHook
        {
            public void RemoveHook(ulong guid, bool clearRefCounts)
            {
                BuffManager bm = (BuffManager)(object)this;
                bool live = false;
                try { live = bm.HasElement((BuffNames)guid); } catch (Exception) { }
                RemoveOrig(guid, clearRefCounts);
                if (!live) return;
                try
                {
                    if (!BrLeft(live, bm.HasElement((BuffNames)guid))) return;
                    if (ShortcutOn("buffremovedelayed")) { RemoveOrig(guid, false); PatchCounters()[1700]++; }
                    else PatchCounters()[1701]++;
                }
                catch (Exception) { PatchCounters()[1702]++; }
            }

            public void RemoveOrig(ulong guid, bool clearRefCounts) { }
        }

        // ---- B11 §18 tvdisclistener (U19) -------------------------------------------------------------------------------
        // [verified, decompiled] TV.SetDataDiscDisposedListener adds a disposed-listener for the new home-movie disc and
        // never removes the old one (only RemoveHomeMovieDataDisc does): disposing an earlier disc later clears the
        // current movie and turns the TV off. Fix: an old listener still set is removed before EA adds the new one.
        // Counters: [1703] old listeners removed, [1704] old listeners kept (switch off), [1705] errors.
        static readonly FieldInfo sTdListener = typeof(TV).GetField("mDataDiscDisposed", kB11All);
        static string sTdMissing = "";

        static void ApplyTvDiscListener()
        {
            sTdMissing = SpNames("TV.mDataDiscDisposed", sTdListener);
            if (sTdMissing.Length > 0) { AppliedLog().Add("tvdisclistener: not found: " + sTdMissing); return; }
            Patch(typeof(TV), "SetDataDiscDisposedListener", Type.EmptyTypes, typeof(BTdHook), "ListenHook", "ListenOrig");
        }

        public class BTdHook
        {
            public void ListenHook()
            {
                try
                {
                    EventListener old = sTdListener.GetValue(this) as EventListener;
                    if (old != null)
                    {
                        if (ShortcutOn("tvdisclistener")) { EventTracker.RemoveListener(old); sTdListener.SetValue(this, null); PatchCounters()[1703]++; }
                        else PatchCounters()[1704]++;
                    }
                }
                catch (Exception) { PatchCounters()[1705]++; }
                ListenOrig();
            }

            public void ListenOrig() { }
        }

        // ---- B11 §19 fridgetestnull (U21) -------------------------------------------------------------------------------
        // [verified, decompiled] Fridge_OpenFridge.Definition.Test reads target.InventoryComp.InventoryUIModel before any
        // null test; a fridge with no inventory component (community-lot fridge) throws in the pie-menu test. Fix: our
        // copy tests InventoryComp (and the HUD) for null first, then gives EA's answer. Counters: [1706] tests answered
        // without the throw, [1707] tests that threw (switch off), [1708] errors.
        static Type sFtDefType;
        static string sFtMissing = "";

        static void ApplyFridgeTestNull()
        {
            Type ft = typeof(Fridge).Assembly.GetType("Sims3.Gameplay.Objects.Appliances.Fridge_OpenFridge");
            sFtDefType = ft != null ? ft.GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic) : null;
            sFtMissing = SpNames("Fridge_OpenFridge+Definition", sFtDefType);
            if (sFtMissing.Length > 0) { AppliedLog().Add("fridgetestnull: not found: " + sFtMissing); return; }
            Patch(sFtDefType, "Test", new Type[] { typeof(Sim), typeof(Fridge), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() }, typeof(BFtHook), "TestHook", "TestOrig");
        }

        // Pure: EA's answer with the null tests in front: false only for the open secondary inventory, else has-inventory.
        internal static bool FtAnswer(bool compKnown, bool isOpenSecondary, bool hasFridgeInventory)
        {
            if (compKnown && isOpenSecondary) return false;
            return hasFridgeInventory;
        }

        public class BFtHook
        {
            public bool TestHook(Sim a, Fridge target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                if (target == null || target.InventoryComp != null) return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                if (!ShortcutOn("fridgetestnull")) { PatchCounters()[1707]++; return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback); }
                try
                {
                    PatchCounters()[1706]++;
                    return FtAnswer(false, false, target.FridgeInventory != null);
                }
                catch (Exception) { PatchCounters()[1708]++; return false; }
            }

            public bool TestOrig(Sim a, Fridge target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; }
        }

        // ---- B11 §20 cancelposturedef (U22) -----------------------------------------------------------------------------
        // [verified, decompiled] Posture.CancelPosture returns early when the cancel transition is already at the head of
        // the queue, but compares the transition's definition with the head interaction itself (a definition with an
        // instance), so the test is never true and every cancel queues another stand-up. Fix: our copy compares the two
        // definitions; the rest is EA's. Counters: [1709] duplicate stand-ups not queued, [1710] queued again (switch
        // off), [1711] errors.
        static void ApplyCancelPostureDef()
        {
            Patch(typeof(Posture), "CancelPosture", new Type[] { typeof(Sim) }, typeof(BCpHook), "CancelHook", "CancelOrig");
        }

        // Pure: is the cancel already queued at the head? (EA compared a definition with an instance: always false)
        internal static bool CpAlready(object transitionDef, object headDef) { return transitionDef != null && transitionDef == headDef; }

        public class BCpHook
        {
            public void CancelHook(Sim sim)
            {
                Posture p = (Posture)(object)this;
                InteractionInstance head = sim != null ? sim.InteractionQueue.GetHeadInteraction() : null;
                bool on = ShortcutOn("cancelposturedef");
                if (!on || head == null)
                {
                    if (head != null && !on)
                    {
                        try { InteractionInstance t = p.GetCancelTransition(); if (t != null && CpAlready(t.InteractionDefinition, head.InteractionDefinition)) PatchCounters()[1710]++; }
                        catch (Exception) { PatchCounters()[1711]++; }
                    }
                    CancelOrig(sim);
                    return;
                }
                p.OnCancelPosture();
                p.HasBeenCanceled = true;
                InteractionQueue q = sim.InteractionQueue;
                InteractionInstance cancelTransition = p.GetCancelTransition();
                if (cancelTransition != null && CpAlready(cancelTransition.InteractionDefinition, head.InteractionDefinition)) { PatchCounters()[1709]++; return; }
                bool all = true;
                List<ulong> ids = new List<ulong>();
                foreach (InteractionInstance item in q.InteractionList)
                {
                    if (!p.Satisfies(item.PosturePreconditions, item.Target, CommodityKind.None)) { all = false; break; }
                    if (p.RemoveInteractionFromQueue(item) && item.CancellableByPlayer && !item.MustRun) ids.Add(item.Id);
                }
                foreach (ulong id in ids) q.CancelInteraction(id, ExitReason.CancelledByPosture);
                if (all && cancelTransition != null)
                {
                    cancelTransition.Hidden = true;
                    q.AddNext(cancelTransition);
                }
            }

            public void CancelOrig(Sim sim) { }
        }

        // ---- B11 §21 facialidleweight (U23) -----------------------------------------------------------------------------
        // [verified, decompiled] IdleManager.GetFacialMoodIdles splits the buff and trait facial-idle chances (int
        // tunables, 6) by the number of idles with integer division: 7 or more buff (or trait) idles each get weight 0
        // and 4-6 each get 1, so the mood idle wins far more often than tuned. Fix: our copy divides as floats; the list
        // is EA's. (The other IdleManager leads in docs/589 U23, the distress gate and ParseMoodIdleData's flavour, need
        // the idle XML read first and are not built.) Counters: [1712] lists built with float shares, [1713] lists with a
        // truncated share (switch off), [1714] errors.
        static readonly FieldInfo sFiSim = typeof(IdleManager).GetField("mSim", kB11All);
        static string sFiMissing = "";

        static void ApplyFacialIdleWeight()
        {
            sFiMissing = SpNames("IdleManager.mSim", sFiSim);
            if (sFiMissing.Length > 0) { AppliedLog().Add("facialidleweight: not found: " + sFiMissing); return; }
            Patch(typeof(IdleManager), "GetFacialMoodIdles", Type.EmptyTypes, typeof(BFiHook), "IdlesHook", "IdlesOrig");
        }

        // Pure: one idle's share of a chance: EA's integer division or the float share.
        internal static float FiShare(bool on, int chance, int count) { return on ? (float)chance / count : (float)(chance / count); }

        public class BFiHook
        {
            public List<IdleManager.FacialIdleChanceC> IdlesHook()
            {
                bool on = ShortcutOn("facialidleweight");
                Sim sim;
                try { sim = sFiSim.GetValue(this) as Sim; } catch (Exception) { PatchCounters()[1714]++; return IdlesOrig(); }
                if (sim == null) return IdlesOrig();
                List<string> buffs = sim.BuffManager.FacialIdleForBuffs;
                List<string> traits = sim.TraitManager.FacialIdleForTraits;
                bool truncates = (buffs.Count > 0 && IdleManager.FacialBuffIdleChance % buffs.Count != 0) || (traits.Count > 0 && IdleManager.FacialTraitIdleChance % traits.Count != 0);
                if (!on || !truncates) { if (truncates) PatchCounters()[1713]++; return IdlesOrig(); }
                try
                {
                    List<IdleManager.FacialIdleChanceC> list = new List<IdleManager.FacialIdleChanceC>();
                    string mood = sim.MoodManager.FacialIdleForMood;
                    if (!string.IsNullOrEmpty(mood)) list.Add(new IdleManager.FacialIdleChanceC(mood, IdleManager.FacialMoodIdleChance));
                    if (buffs.Count > 0)
                    {
                        float pb = FiShare(true, IdleManager.FacialBuffIdleChance, buffs.Count);
                        foreach (string b in buffs) list.Add(new IdleManager.FacialIdleChanceC(b, pb));
                    }
                    if (traits.Count > 0)
                    {
                        float pt = FiShare(true, IdleManager.FacialTraitIdleChance, traits.Count);
                        foreach (string t in traits) list.Add(new IdleManager.FacialIdleChanceC(t, pt));
                    }
                    PatchCounters()[1712]++;
                    return list;
                }
                catch (Exception) { PatchCounters()[1714]++; return IdlesOrig(); }
            }

            public List<IdleManager.FacialIdleChanceC> IdlesOrig() { return null; }
        }
    }
}
