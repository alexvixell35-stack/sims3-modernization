using System;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // unseenoutfitnospin (ON 2026-11-20, docs/809; needs unseenoutfitdefer on, PR #457): an unseen townie's spin outfit change
    // (Sim.SwitchToOutfitWithSpin(SwitchOutfitHelper, bool), the funnel of every SwitchToOutfitWithSpin overload) takes the
    // WithoutSpin result instead: the managed state changes at once and the native set is a plain one, which
    // unseenoutfitdefer then holds until the Sim can be seen. The spin funnel made about 500 delayed sets of unseen townies a
    // game on Bridgeport and Twinbrook, the biggest part of the outfit work left; each starts a native build at once
    // (SetOutfitInGameObjectDelayed) that unseenoutfitdefer cannot hold (the helper waits on its ready callback).
    //
    // What EA's funnel does [read in the game's core assemblies]:
    //   spin.Start(): returns if disposed or started; ReInit if aborted; aborts another active helper; mActiveSwitchOutfitHelper
    //     = spin; unless mNoChange, SwitchToOutfitDelayed(mCategory, mIndex, Ready) (age test, a Force trait test that never
    //     changes the category, a SimBuilder outfit when the outfit is missing, the supernatural-form outfit, pending info =
    //     category / index / PreviousKey / RevealingScore, the native delayed set); false there sets mNoChange.
    //   spin.Wait(true): LoopIdle, then Sleep(1) until Ready (the native build is done).
    //   if WillChange (= !mNoChange): child or older plays "a_clothesChange" (or OverrideAnimation, or the hoverboard
    //     posture's state) whose event runs spin.ChangeOutfit(); younger ones call ChangeOutfit() at once; returns true.
    //   ChangeOutfit(): LaundryManager.OnClothesChanged(sim, current category, mCategory, mPlaceLaundryPile); mCategory = 0;
    //     DoOutfitDelayedSwap() (visible = pending, AutoEnableCensor, CompleteDelayedModelSwap, RefreshBodyShape, pending =
    //     None, PostOutfitSwap); DoCarriedToddlerSpinIfNeeded; mActiveSwitchOutfitHelper = null; mStarted = false.
    // What this does instead, for a helper that EA would start fresh: the same OnClothesChanged call (same arguments), mCategory
    // = 0 and mReady = true (what Ready() and ChangeOutfit leave), then Sim.SwitchToOutfitWithoutSpin(category, outfit, index)
    // with the outfit Start would have used. Returns true, as EA's funnel does when it changes.
    // Why the caller sees EA's state afterwards:
    //  - The Sim: SwitchToOutfitWithoutSpin(cat, outfit, idx) ends in the state DoOutfitDelayedSwap leaves: visible = category /
    //    index, RevealingScore, pending = None, censor from the new category, RefreshBodyShape, PostOutfitSwap. The one field
    //    that differs is mVisibleOutfitInfo.PreviousKey, which nothing reads. mActiveSwitchOutfitHelper: null before (required)
    //    and null after, as after EA's ChangeOutfit.
    //  - The helper: mStarted false, mAborted false, mReady true, mCategory 0, mNoChange false: EA's fields after ChangeOutfit.
    //    So a later Wait returns at once (mReady), a later Start takes EA's path (SwitchToOutfitDelayed(0) fails the age test,
    //    mNoChange), Dispose does not abort (not started), and WillChange stays true. No wait can hang: there is no delayed
    //    set, no callback to wait for.
    //  - Time: the Sim skips the spin animation and the build wait (some sim seconds), as an unseen Sim with EA's own
    //    WithoutSpin changes does; nothing waits on that animation but the funnel itself.
    // Not taken (EA's funnel runs; counted by reason): the helper started, aborted or disposed before, or any helper active on
    // the Sim [1483] (a caller that pre-starts the helper to hide the build, e.g. career and rabbit-hole arrivals); mNoChange
    // [1484]; not standing, carrying a child, not a human child or older, or an OverrideAnimation (graduation) [1485]; a
    // transform buff, the supernatural form, a ghost, Outerwear without Seasons, the age test failing, or no stored outfit (EA
    // would build one) [1486]; the new or the shown category outside unseenoutfitdefer's kOdSafe set [1487]; a pending outfit,
    // an open delayed swap or a Sim's first set after it is instantiated [1488]; seen, or within unseenoutfitdefer's margin
    // [1489]. Household, selected and unknown-scope Sims are not counted (IsPlayerSim). [1482] taken, [1490] of those whose set
    // then ran at once (unseenoutfitdefer refused it after all: not expected), [1491] the checks threw (EA's funnel ran;
    // nothing changed before it).
    // Cleanup: no loop, no retry; every check runs before the first change, so a refusal leaves EA's path untouched;
    // ResetException passes through.
    public static partial class Fixes
    {
        static readonly FieldInfo sNsLaundry = typeof(Sim.SwitchOutfitHelper).GetField("mPlaceLaundryPile", kInstAll);

        static void ApplyUnseenOutfitNoSpin()
        {
            Patch(typeof(Sim), "SwitchToOutfitWithSpin", new Type[] { typeof(Sim.SwitchOutfitHelper), typeof(bool) }, typeof(NsSimHook), "SpinHook", "SpinOrig");
        }

        // plain class (Sim's constructors are not ours to call); `this` is the Sim at run time
        public class NsSimHook
        {
            public bool SpinHook(Sim.SwitchOutfitHelper spin, bool mirrored)
            {
                if (ShortcutOn("unseenoutfitnospin") && NsTry((Sim)(object)this, spin)) return true;
                return SpinOrig(spin, mirrored);
            }
            public bool SpinOrig(Sim.SwitchOutfitHelper spin, bool mirrored) { return false; } // receives Sim.SwitchToOutfitWithSpin(SwitchOutfitHelper, bool)'s original header
        }

        // true = done here (the caller returns true); false = run EA's funnel
        static bool NsTry(Sim s, Sim.SwitchOutfitHelper h)
        {
            if (h == null || s == null || !sOdHooksOk || !ShortcutOn("unseenoutfitdefer")) return false;
            int[] c = PatchCounters();
            OutfitCategories cat;
            int idx;
            SimOutfit outfit;
            bool laundry;
            int why;
            try { why = NsWhy(s, h, out cat, out idx, out outfit, out laundry); }
            catch (ResetException) { throw; }
            catch (Exception) { c[1491]++; return false; }
            if (why != 0) { if (why > 1) c[why]++; return false; }
            // EA's ChangeOutfit order: laundry first (the old category), then the helper fields, then the outfit
            Sims3.Gameplay.Controllers.LaundryManager.OnClothesChanged(s, s.GetCurrentOutfitCategoryFromOutfitInGameObject(), cat, laundry);
            sSohCategory.SetValue(h, (OutfitCategories)0);
            sSohReady.SetValue(h, true);
            s.SwitchToOutfitWithoutSpin(cat, outfit, idx);
            c[1482]++;
            if (!sOdPending.ContainsKey(s.ObjectId.Value)) c[1490]++;
            return true;
        }

        // 0 = take it; 1 = no, not counted (not a live unseen-townie case); else the counter of the reason
        static int NsWhy(Sim s, Sim.SwitchOutfitHelper h, out OutfitCategories cat, out int idx, out SimOutfit outfit, out bool laundry)
        {
            cat = OutfitCategories.None;
            idx = 0;
            outfit = null;
            laundry = true;
            if (!GameStates.IsLiveState || GameStates.IsTravelling || SaveInProgress() || WorldGone()) return 1;
            if (s.HasBeenDestroyed || s == PlumbBob.SelectedActor || IsPlayerSim(s) || s.Household == null) return 1;
            if (sSohStarted == null || sSohAborted == null || sSohDisposed == null || sSohNoChange == null || sSohReady == null
                || sSohCategory == null || sSohIndex == null || sSimActiveSoh == null || sNsLaundry == null) return 1;
            if ((bool)sSohDisposed.GetValue(h) || (bool)sSohStarted.GetValue(h) || (bool)sSohAborted.GetValue(h) || sSimActiveSoh.GetValue(s) != null) return 1483;
            if ((bool)sSohNoChange.GetValue(h)) return 1484;
            SimDescription sd = s.SimDescription;
            if (!string.IsNullOrEmpty(h.OverrideAnimation) || !sd.IsHuman || !sd.ChildOrAbove || s.Posture == null || s.Posture != s.Standing
                || s.CarryingChildPosture != null) return 1485;
            cat = (OutfitCategories)sSohCategory.GetValue(h);
            idx = (int)sSohIndex.GetValue(h);
            laundry = (bool)sNsLaundry.GetValue(h);
            if (s.BuffManager == null || s.BuffManager.TransformBuffInst != null || sd.IsSupernaturalForm || s.IsGhostOrHasGhostBuff) return 1486;
            if (cat == OutfitCategories.Outerwear && !GameUtils.IsInstalled(ProductVersion.EP8)) return 1486;  // SwitchToOutfitDelayed's own test
            if (!s.AcceptableClothingCategoryForAge(cat)) return 1486;
            outfit = sd.GetOutfit(cat, idx);
            if (outfit == null || outfit.Key == ResourceKey.kInvalidResourceKey) return 1486;  // EA would build one (SimBuilder)
            if (cat == OutfitCategories.None || (cat & ~kOdSafe) != 0) return 1487;
            if (!OdSafeKey(s.ObjectId, CASUtils.GetOutfitInGameObject(s.ObjectId))) return 1487;  // the shown one: Naked either way
            if (!OdSafeKey(s.ObjectId, outfit.Key)) return 1487;
            ulong sid = s.ObjectId.Value;
            if (OdPendingCat(s) != OutfitCategories.None || sOdOpen.ContainsKey(sid) || sOdFresh.ContainsKey(sid)) return 1488;
            if (SafeUfWhy(s) != 0 || OdNear(s)) return 1489;  // seen, or within the margin
            return 0;
        }
    }
}
