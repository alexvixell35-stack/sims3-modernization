using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Roles;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.UI;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Batch 12c (docs/592 usage sweep 4, base classes and shared helpers): EA bug fixes as EA intended, household
    // included. All default off. Slots 3172-3195. simler90 UPDATE242 leaves every hooked method as EA.
    // Not built: S1 (inside Autonomy.CalculateScoreForObjectInteraction, the scoring hot path: IL patch queued), S4 (is
    // intmultiple), S7 (removing ended clones breaks EA's own foreach over mActiveCommodityChanges in EndCommodityUpdate(k);
    // impact inferred, needs a design pass), S10 and S12 (inferred), the latent and tidy lists (no live trigger).
    public static partial class Fixes
    {
        // ---- B12 §37 leaveconvnull (docs/592 S2) ---------------------------------------------------------------------
        // [verified, decompiled] SocialComponent.LeaveConversation counts SocialsToday, grants Social Butterfly's
        // ItsAllAboutMe (and plays its cheer) and clears the action memory before its `Conversation == null` return: a
        // Sim leaving a conversation it never joined (about 34 callers end socials this way) counts as a finished social.
        // Fix: no conversation, nothing done. Counters: [3172] empty leaves skipped, [3173] counted (switch off),
        // [3174] errors.
        static void ApplyLeaveConvNull()
        {
            Patch(typeof(SocialComponent), "LeaveConversation", Type.EmptyTypes, typeof(B12LvHook), "LeaveHook", "LeaveOrig");
        }

        internal static bool LvSkip(bool hasConversation) { return !hasConversation; }

        public class B12LvHook
        {
            public void LeaveHook()
            {
                if (LvSkip(((SocialComponent)(object)this).Conversation != null))
                {
                    if (ShortcutOn("leaveconvnull")) { PatchCounters()[3172]++; return; }
                    PatchCounters()[3173]++;
                }
                LeaveOrig();
            }

            public void LeaveOrig() { }
        }

        // ---- B12 §38 vacationalarms (docs/592 S3) --------------------------------------------------------------------
        // [verified, decompiled; impact inferred] SimClockUtils.OnFinishedLoadingSaveGameEventHandler, on arrival in a
        // vacation world: TicksAdvanced += num, then FixLoadAlarms(CurrentTicks + num), but CurrentTicks already includes
        // TicksAdvanced; every alarm due within num hours of arrival bunches at arrival + num. The University branch passes
        // CurrentTicks. Fix: the vacation branch passes CurrentTicks too; the rest is EA's handler. Counters: [3175]
        // arrivals fixed, [3176] arrivals with the double shift (switch off), [3177] errors.
        static readonly FieldInfo sVaSingle = B12F(typeof(SimClockUtils), "sSingleton");
        static readonly MethodInfo sVaMake = B12M(typeof(SimClockUtils), "MakeSimClock");
        static readonly FieldInfo sVaHour = B12F(typeof(SimClockUtils), "kInitialTimeOfDay");
        static string sVaM12 = "";

        static void ApplyVacationAlarms()
        {
            sVaM12 = SpNames("SimClockUtils.sSingleton", sVaSingle, "MakeSimClock", sVaMake, "kInitialTimeOfDay", sVaHour);
            if (sVaM12.Length > 0) { AppliedLog().Add("vacationalarms: not found: " + sVaM12); return; }
            Patch(typeof(SimClockUtils), "OnFinishedLoadingSaveGameEventHandler", new Type[] { typeof(object), typeof(World.OnFinishedLoadingSaveGameEventArgs) }, typeof(B12VaHook), "LoadHook", "LoadOrig");
        }

        // Pure: the tick FixLoadAlarms gets on vacation arrival (current already holds the advance).
        internal static long VaFixTicks(bool fixedRule, long current, long advance) { return fixedRule ? current : current + advance; }

        public class B12VaHook
        {
            public static void LoadHook(object sender, World.OnFinishedLoadingSaveGameEventArgs e)
            {
                if (!GameStates.IsTravelling || !GameUtils.IsOnVacation()) { LoadOrig(sender, e); return; }
                if (!ShortcutOn("vacationalarms")) { PatchCounters()[3176]++; LoadOrig(sender, e); return; }
                try
                {
                    if (e.warnings != 0) Sims3.Gameplay.Gameflow.sShowObjectReplacedWarning = true;
                    Sims3.Gameplay.Gameflow.sGameLoadedFromWorldFile = false;
                    if (sVaSingle.GetValue(null) == null) sVaMake.Invoke(null, null);
                    long num = SimClock.ConvertToTicks(SimClock.HoursUntil((float)sVaHour.GetValue(null)), TimeUnit.Hours);
                    SimClock.TicksAdvanced += num;
                    AlarmManager.FixLoadAlarms(VaFixTicks(true, SimClock.CurrentTicks, num));
                    PatchCounters()[3175]++;
                }
                catch (Exception) { PatchCounters()[3177]++; }
            }

            public static void LoadOrig(object sender, World.OnFinishedLoadingSaveGameEventArgs e) { }
        }

        // ---- B12 §39 cleanupvfx (docs/592 S5) ------------------------------------------------------------------------
        // [verified, decompiled IL] InteractionInstance.Cleanup: `if (!GameStates.IsGameShuttingDown || mVfxs == null)
        // return;` disposes the effects an interaction made by name (SetEffectActor: FishHere, SculptingStation, WishingWell,
        // Snowman, ...) only while the game shuts down; in play they wait for the finalizer. Fix: after EA's Cleanup, a
        // live game disposes them (EA's loop). Our djcleanup copy of the base Cleanup follows the same rule (CvDispose).
        // Counters: [3178] effect lists disposed, [3179] left to the finalizer (switch off), [3180] errors.
        static readonly FieldInfo sCvList = B12F(typeof(InteractionInstance), "mVfxs");
        static string sCvM12 = "";

        static void ApplyCleanupVfx()
        {
            sCvM12 = SpNames("InteractionInstance.mVfxs", sCvList);
            if (sCvM12.Length > 0) { AppliedLog().Add("cleanupvfx: not found: " + sCvM12); return; }
            Patch(typeof(InteractionInstance), "Cleanup", Type.EmptyTypes, typeof(B12CvHook), "CleanupHook", "CleanupOrig");
        }

        // Pure: does Cleanup dispose the effect list? EA: only while shutting down; fixed: only in a live game.
        internal static bool CvDispose(bool fixedRule, bool shuttingDown) { return fixedRule ? !shuttingDown : shuttingDown; }

        static bool CvDisposeList(InteractionInstance ii, bool fixedRule)
        {
            List<VisualEffect> l = sCvList == null ? null : sCvList.GetValue(ii) as List<VisualEffect>;
            if (l == null || !CvDispose(fixedRule, GameStates.IsGameShuttingDown)) return false;
            foreach (VisualEffect v in l) v.Dispose();
            sCvList.SetValue(ii, null);
            return true;
        }

        public class B12CvHook
        {
            public void CleanupHook()
            {
                CleanupOrig();
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    if (sCvList.GetValue(ii) == null || GameStates.IsGameShuttingDown) return;
                    if (!ShortcutOn("cleanupvfx")) { PatchCounters()[3179]++; return; }
                    if (CvDisposeList(ii, true)) PatchCounters()[3178]++;
                }
                catch (Exception) { PatchCounters()[3180]++; }
            }

            public void CleanupOrig() { }
        }

        // ---- B12 §40 lookatcopy (docs/592 S6) ------------------------------------------------------------------------
        // [verified, decompiled] Lot.ObjectsToLookAtInRoom(roomId, includeSims: false) runs RemoveAll(o is Sim) on the live
        // per-room list from mObjectsToLookAtPerRoom: one call (ShoppingRegister Browse) drops every Sim from that room's
        // look-at registry for good. Fix: the Sims are filtered from a copy. Counters: [3181] copies filtered, [3182] live
        // lists cut (switch off), [3183] errors.
        static void ApplyLookAtCopy()
        {
            Patch(typeof(Lot), "ObjectsToLookAtInRoom", new Type[] { typeof(int), typeof(bool) }, typeof(B12LaHook), "LookHook", "LookOrig");
        }

        // Pure: the non-Sim objects of a room's list, as a new list (null when none, EA's answer).
        internal static List<GameObject> LaFilter(List<GameObject> live)
        {
            if (live == null) return null;
            List<GameObject> r = new List<GameObject>(live.Count);
            foreach (GameObject o in live) if (!(o is Sim)) r.Add(o);
            return r.Count == 0 ? null : r;
        }

        public class B12LaHook
        {
            public List<GameObject> LookHook(int roomId, bool includeSims)
            {
                if (includeSims) return LookOrig(roomId, includeSims);
                Lot lot = (Lot)(object)this;
                List<GameObject> live = lot.ObjectsToLookAtInRoom(roomId);
                if (live == null) return null;
                if (!ShortcutOn("lookatcopy")) { PatchCounters()[3182]++; return LookOrig(roomId, includeSims); }
                try { PatchCounters()[3181]++; return LaFilter(live); }
                catch (Exception) { PatchCounters()[3183]++; return LookOrig(roomId, includeSims); }
            }

            public List<GameObject> LookOrig(int roomId, bool includeSims) { return null; }
        }

        // ---- B12 §41 lottotooltip (docs/592 S8) ----------------------------------------------------------------------
        // [verified, decompiled] Grocery.BuyLottoTicket.Definition.Test builds the "not available until" time with
        // DateAndTime(hours, minutes, 0), which is an absolute time from SimClock.StartTicks, not a time of day: the tooltip
        // shows the start hour shifted by the save's start time. simler90 fixed the same misuse in Laundry only. Fix: a
        // refused test outside the hours gets the tooltip with the time of day kBuyLottoStart. Counters: [3184] tooltips
        // fixed, [3185] shifted tooltips (switch off), [3186] errors.
        static readonly Type sLtBuy = typeof(Grocery).GetNestedType("BuyLottoTicket", BindingFlags.NonPublic);
        static readonly Type sLtDef = sLtBuy == null ? null : sLtBuy.GetNestedType("Definition", BindingFlags.NonPublic);
        static readonly FieldInfo sLtStart = B12F(sLtBuy, "kBuyLottoStart");
        static readonly FieldInfo sLtEnd = B12F(sLtBuy, "kBuyLottoEnd");
        static readonly MethodInfo sLtLoc = B12M(typeof(Grocery), "LocalizeString", typeof(string), typeof(object[]));
        static string sLtM12 = "";

        static void ApplyLottoTooltip()
        {
            sLtM12 = SpNames("Grocery+BuyLottoTicket", sLtBuy, "+Definition", sLtDef, "kBuyLottoStart", sLtStart, "kBuyLottoEnd", sLtEnd, "LocalizeString", sLtLoc);
            if (sLtM12.Length > 0) { AppliedLog().Add("lottotooltip: not found: " + sLtM12); return; }
            Patch(sLtDef, "Test", new Type[] { typeof(Sim), typeof(Grocery), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() }, typeof(B12LtHook), "TestHook", "TestOrig");
        }

        // Pure: EA's "outside the selling hours" test, and the time-of-day ticks of the start hour.
        internal static bool LtClosed(float hour, float start, float end)
        {
            if (start > end) return end < hour && hour < start;
            return hour < start || hour > end;
        }

        internal static long LtTicks(float start)
        {
            int h = (int)start, m = (int)((start - h) * 60f);
            return SimClock.ConvertToTicks(h, TimeUnit.Hours) + SimClock.ConvertToTicks(m, TimeUnit.Minutes);
        }

        public class B12LtHook
        {
            public bool TestHook(Sim actor, Grocery target, bool isAutonomous, ref GreyedOutTooltipCallback cb)
            {
                bool r = TestOrig(actor, target, isAutonomous, ref cb);
                if (r || cb == null) return r;
                try
                {
                    float start = (float)sLtStart.GetValue(null), end = (float)sLtEnd.GetValue(null);
                    if (!LtClosed(SimClock.CurrentTime().Hour, start, end)) return r;
                    if (!ShortcutOn("lottotooltip")) { PatchCounters()[3185]++; return r; }
                    string text = (string)sLtLoc.Invoke(null, new object[] { "LottoNotAvailable", new object[] { new DateAndTime(LtTicks(start)) } });
                    cb = InteractionInstance.CreateTooltipCallback(text);
                    PatchCounters()[3184]++;
                }
                catch (Exception) { PatchCounters()[3186]++; }
                return r;
            }

            public bool TestOrig(Sim actor, Grocery target, bool isAutonomous, ref GreyedOutTooltipCallback cb) { return false; }
        }

        // ---- B12 §42 traiticondislike (docs/592 S9) ------------------------------------------------------------------
        // [verified, decompiled] InteractionObjectPair.GetTraitIconKey: the outer `HasMotive(c) && ConstantChange >= 0`
        // makes the inner `ConstantChange >= 0 ? trait icon : dislike icon` always take the trait icon, so a trait motive
        // the interaction lowers never shows its dislike icon in the pie menu. Fix: our copy drops the outer sign test
        // (and returns EA's answer when the tuning has no tradeoff). Counters: [3187] dislike icons shown, [3188] hidden
        // (switch off), [3189] errors.
        static void ApplyTraitIconDislike()
        {
            Patch(typeof(InteractionObjectPair), "GetTraitIconKey", new Type[] { typeof(Sim) }, typeof(B12TiHook), "IconHook", "IconOrig");
        }

        // Pure: 0 = go on, 1 = trait icon, 2 = dislike icon, for a trait commodity the actor has.
        internal static int TiIcon(bool fixedRule, bool hasMotive, float change)
        {
            if (!hasMotive || (!fixedRule && change < 0f)) return 0;
            return change >= 0f ? 1 : 2;
        }

        public class B12TiHook
        {
            public ResourceKey IconHook(Sim actor)
            {
                if (!ShortcutOn("traiticondislike")) return IconOrig(actor);
                try
                {
                    InteractionObjectPair iop = (InteractionObjectPair)(object)this;
                    InteractionTuning tuning = iop.Tuning;
                    if (actor == null || tuning == null || tuning.mTradeoff == null) return IconOrig(actor);
                    IHasTraitIcon hti = iop.InteractionDefinition as IHasTraitIcon;
                    ResourceKey key = hti != null ? hti.GetTraitIcon(actor, iop.Target as GameObject) : ResourceKey.kInvalidResourceKey;
                    List<CommodityKind> list = new List<CommodityKind>();
                    foreach (CommodityChange c in tuning.mTradeoff.mOutputs)
                    {
                        CommodityKind k = c.Commodity;
                        if (!CommodityTest.IsTrait(k)) continue;
                        int icon = TiIcon(true, actor.Motives.HasMotive(k), c.ConstantChange);
                        if (icon == 1) return actor.TraitManager.GetTraitIcon(k);
                        if (icon == 2) { PatchCounters()[3187]++; return actor.TraitManager.GetDislikeTraitIcon(k); }
                        list.Add(k);
                    }
                    if (key != ResourceKey.kInvalidResourceKey) return key;
                    IRestrictedAntiTraitIcons r = iop.InteractionDefinition as IRestrictedAntiTraitIcons;
                    if (r != null) return actor.TraitManager.GetAntiTraitIcon(list, r.GetRestrictedAntiTraitIcons(actor));
                    return key;
                }
                catch (Exception) { PatchCounters()[3189]++; return IconOrig(actor); }
            }

            public ResourceKey IconOrig(Sim actor) { return ResourceKey.kInvalidResourceKey; }
        }

        // ---- B12 §43 roleunprotect (docs/592 S11) --------------------------------------------------------------------
        // [verified path, decompiled] Role.EndRole removes the story-progression protection only for an active role whose
        // Sim is instantiated; RemoveSimFromRole of an inactive role, or of a townie not in the world, leaves
        // FullFromOccupationJob on the Sim's mini description for good (the townie is never culled or moved). Fix: after
        // RemoveSimFromRole, protection still held by the role is removed (EA's Unprotect body, without its SimInRole test).
        // Counters: [3190] protections removed, [3191] left (switch off), [3192] errors.
        static readonly FieldInfo sRuFlag = B12F(typeof(Role), "mIsStoryProgressionProtected");
        static readonly FieldInfo sRuSim = B12F(typeof(Role), "mSim");
        static string sRuM12 = "";

        static void ApplyRoleUnprotect()
        {
            sRuM12 = SpNames("Role.mIsStoryProgressionProtected", sRuFlag, "Role.mSim", sRuSim);
            if (sRuM12.Length > 0) { AppliedLog().Add("roleunprotect: not found: " + sRuM12); return; }
            Patch(typeof(Role), "RemoveSimFromRole", Type.EmptyTypes, typeof(B12RuHook), "RemoveHook", "RemoveOrig");
        }

        internal static bool RuUnprotect(bool stillProtected, bool shuttingDown) { return stillProtected && !shuttingDown; }

        public class B12RuHook
        {
            public void RemoveHook()
            {
                RemoveOrig();
                try
                {
                    Role role = (Role)(object)this;
                    SimDescription sd = sRuSim.GetValue(role) as SimDescription;
                    if (sd == null || !RuUnprotect((bool)sRuFlag.GetValue(role), GameStates.IsGameShuttingDown)) return;
                    if (!ShortcutOn("roleunprotect")) { PatchCounters()[3191]++; return; }
                    MiniSimDescription mini = sd.GetMiniSimForProtection();
                    if (mini != null) mini.RemoveProtection(MiniSimDescription.ProtectionFlag.FullFromOccupationJob);
                    sRuFlag.SetValue(role, false);
                    PatchCounters()[3190]++;
                }
                catch (Exception) { PatchCounters()[3192]++; }
            }

            public void RemoveOrig() { }
        }

        // ---- B12 §44 putdownplaced (docs/592 S13) --------------------------------------------------------------------
        // [verified, decompiled] CarrySystem.PutDownOnSurface fires the surface's slot event with Action.ItemRemoved when it
        // places the held object (PutDown and every other placement fire ItemPlaced), so surface listeners read a
        // placement as a removal. Fix: while a PutDownOnSurface runs, its own ItemRemoved for the held object is fired as
        // ItemPlaced (SurfacePair.TriggerSlotUsed hook, keyed by the held object: the routing before it yields, so other
        // Sims' put-downs interleave). EA's method runs unchanged (it yields; no reflection call across it).
        // Counters: [3193] placements fired as placed, [3194] fired as removed (switch off), [3195] errors.
        static readonly Dictionary<IGameObject, bool> sPpPending = new Dictionary<IGameObject, bool>();

        static void ApplyPutDownPlaced()
        {
            Patch(typeof(CarrySystem), "PutDownOnSurface", new Type[] { typeof(Sim), typeof(ISurface), typeof(SurfaceType), typeof(bool), typeof(bool), typeof(bool) }, typeof(B12PpHook), "PutHook", "PutOrig");
            Patch(typeof(SurfacePair), "TriggerSlotUsed", new Type[] { typeof(SurfaceSlot), typeof(IGameObject), typeof(SurfaceAddOn.Action) }, typeof(B12PpTrigger), "TriggerHook", "TriggerOrig");
        }

        // Pure: the action fired for a slot event (pending = the held object of a running PutDownOnSurface).
        internal static SurfaceAddOn.Action PpAction(bool fixedRule, bool pending, SurfaceAddOn.Action a)
        {
            return fixedRule && pending && a == SurfaceAddOn.Action.ItemRemoved ? SurfaceAddOn.Action.ItemPlaced : a;
        }

        public class B12PpHook
        {
            public static bool PutHook(Sim sim, ISurface surface, SurfaceType type, bool forceRoute, bool requireChair, bool playRouteFail)
            {
                // bookhand (BookHand.cs): the carry machine can go null after CarrySystem.PutDown's own guard ran (the routing to
                // a surface yields); PutDownOnSurface then throws at CarryStateMachine.SetParameter. Same fix, same switch, same counters.
                try { if (sim != null) BhAt(sim, BookHandCore.PutDownWouldThrow(BhStuck(sim), sim.LotCurrent == null), "CarrySystem.PutDownOnSurface"); }
                catch (Exception) { }  // the guard never stops EA's put-down
                IGameObject held = sim == null ? null : sim.GetObjectInRightHand();
                bool track = held != null && surface != null && !(held is ICarryDestroyOnPutDown);
                if (track) sPpPending[held] = true;
                try { return PutOrig(sim, surface, type, forceRoute, requireChair, playRouteFail); }
                finally { if (track) sPpPending.Remove(held); }
            }

            public static bool PutOrig(Sim sim, ISurface surface, SurfaceType type, bool forceRoute, bool requireChair, bool playRouteFail) { return false; }
        }

        public class B12PpTrigger
        {
            public void TriggerHook(SurfaceSlot slot, IGameObject item, SurfaceAddOn.Action action)
            {
                if (item != null && action == SurfaceAddOn.Action.ItemRemoved && sPpPending.Count > 0 && sPpPending.Remove(item))
                {
                    if (ShortcutOn("putdownplaced")) { PatchCounters()[3193]++; action = PpAction(true, true, action); }
                    else PatchCounters()[3194]++;
                }
                TriggerOrig(slot, item, action);
            }

            public void TriggerOrig(SurfaceSlot slot, IGameObject item, SurfaceAddOn.Action action) { }
        }
    }
}
