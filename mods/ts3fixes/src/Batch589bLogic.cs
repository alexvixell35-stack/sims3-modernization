using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.Store.Objects;
using Sims3.UI.Controller;

namespace Ts3Fixes
{
    // Batch 11b (docs/589 usage sweep, U6-U16): EA bug fixes in everyday objects and the Sim core. All default off; all
    // reach the household (as EA intended, household included). simler90 UPDATE242 changes none of the hooked methods
    // (its TV change is in WatchTV.Run, its LongTermRelationship change in the genealogy test, its SocialComponent
    // changes in the cheating and opportunity code).
    public static partial class Fixes
    {
        // ---- B11 §6 tvsyncalarm (U6) ------------------------------------------------------------------------------------
        // [verified, decompiled] TV.OnChannelChangeResetAlarms (and MovieScreen's) removes the sync-event and video-game
        // alarms but keeps the handles; SetSyncEventAlarm only arms a new one while the handle is invalid. After the
        // first channel change or turn-off the sync-event viewer reactions are gone for good, and the saved stale handle
        // keeps them gone after a reload. Fix: after EA's reset the handles are set invalid (what EA's own video-game
        // code does after its RemoveAlarm). Counters: [3001] handles reset, [3002] stale handles kept (switch off),
        // [3003] errors.
        static readonly FieldInfo sTsTvSync = typeof(TV).GetField("mSyncEventAlarmHandler", kB11All);
        static readonly FieldInfo sTsTvGame = typeof(TV).GetField("mEndOfVideoGameSessionAlarmHandler", kB11All);
        static readonly FieldInfo sTsMsSync = typeof(MovieScreen).GetField("mSyncEventAlarmHandler", kB11All);
        static string sTsMissing = "";

        static void ApplyTvSyncAlarm()
        {
            sTsMissing = SpNames("TV.mSyncEventAlarmHandler", sTsTvSync, "TV.mEndOfVideoGameSessionAlarmHandler", sTsTvGame, "MovieScreen.mSyncEventAlarmHandler", sTsMsSync);
            if (sTsMissing.Length > 0) { AppliedLog().Add("tvsyncalarm: not found: " + sTsMissing); return; }
            Patch(typeof(TV), "OnChannelChangeResetAlarms", Type.EmptyTypes, typeof(BTsTvHook), "ResetHook", "ResetOrig");
            Patch(typeof(MovieScreen), "OnChannelChangeResetAlarms", Type.EmptyTypes, typeof(BTsMsHook), "ResetHook", "ResetOrig");
        }

        // Clears each handle still set; true when one was set (it was stale after EA's RemoveAlarm).
        static bool TsClear(object o, FieldInfo f, bool on)
        {
            if ((AlarmHandle)f.GetValue(o) == AlarmHandle.kInvalidHandle) return false;
            if (on) f.SetValue(o, AlarmHandle.kInvalidHandle);
            return true;
        }

        static void TsAfterReset(object o, FieldInfo a, FieldInfo b)
        {
            try
            {
                bool on = ShortcutOn("tvsyncalarm");
                bool any = TsClear(o, a, on);
                if (b != null && TsClear(o, b, on)) any = true;
                if (any) PatchCounters()[on ? 3001 : 3002]++;
            }
            catch (Exception) { PatchCounters()[3003]++; }
        }

        public class BTsTvHook
        {
            public void ResetHook() { ResetOrig(); TsAfterReset(this, sTsTvSync, sTsTvGame); }
            public void ResetOrig() { }
        }

        public class BTsMsHook
        {
            public void ResetHook() { ResetOrig(); TsAfterReset(this, sTsMsSync, null); }
            public void ResetOrig() { }
        }

        // ---- B11 §7 photofunds (U7) -------------------------------------------------------------------------------------
        // [verified, decompiled] PhoneCell.TakePhoto.Run, funds short of the photo's cost: ModifyFunds(cost - shortfall)
        // is ModifyFunds(+FamilyFunds), so the household's funds double while the shortfall still goes onto the bills.
        // EA meant to take what is there (-FamilyFunds) and bill the rest. Fix: Photography.GetCostToTakePhoto for a
        // phone camera notes the cost; the next Sim.ModifyFunds (EA's, the very next call, no yield between) that adds
        // exactly the Sim's funds while they are short of that cost takes them instead. The note is the photographer's
        // (any Sim when EA passes no Photography skill) and this sim tick's only (no other Sim's ModifyFunds, no later one after a photo that was not taken); the next
        // ModifyFunds clears it. Counters: [3004] photo payments taken, [3005] funds doubled (switch off), [3006] errors.
        static int sPhCost;
        static Sim sPhSim;
        static long sPhTick;

        static void ApplyPhotoFunds()
        {
            Patch(typeof(Photography), "GetCostToTakePhoto", new Type[] { typeof(Photography), typeof(ICamera), typeof(PhotographSize), typeof(PaintingStyle) }, typeof(BPhHook), "CostHook", "CostOrig");
            Patch(typeof(Sim), "ModifyFunds", new Type[] { typeof(int) }, typeof(BPhHook), "FundsHook", "FundsOrig");
        }

        // Pure: is this ModifyFunds EA's short-funds photo payment (adds the funds while they are short of the cost)?
        internal static bool PhBuggy(int cost, int funds, int delta) { return funds > 0 && cost > funds && delta == funds; }

        public class BPhHook
        {
            public static int CostHook(Photography skill, ICamera camera, PhotographSize size, PaintingStyle style)
            {
                int r = CostOrig(skill, camera, size, style);
                if (camera is PhoneCell)
                {
                    sPhCost = r;
                    sPhSim = skill != null && skill.SkillOwner != null ? skill.SkillOwner.CreatedSim : null;  // no skill: EA passes null
                    sPhTick = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                }
                return r;
            }

            public static int CostOrig(Photography skill, ICamera camera, PhotographSize size, PaintingStyle style) { return 0; }

            public void FundsHook(int delta)
            {
                if (sPhCost > 0)
                {
                    int cost = sPhCost;
                    bool mine = (sPhSim == null || (object)sPhSim == (object)this) && sPhTick == Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                    sPhCost = 0;
                    sPhSim = null;
                    if (mine) try
                    {
                        int funds = ((Sim)(object)this).FamilyFunds;
                        if (PhBuggy(cost, funds, delta))
                        {
                            if (ShortcutOn("photofunds")) { delta = -funds; PatchCounters()[3004]++; }
                            else PatchCounters()[3005]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[3006]++; }
                }
                FundsOrig(delta);
            }

            public void FundsOrig(int delta) { }
        }

        // ---- B11 §8 ltrhistorybits (U8) ---------------------------------------------------------------------------------
        // [verified, decompiled; the LTR tuning is unchecked] LongTermRelationship.ForceChangeState to any state but
        // Stranger keeps only HasMet and Kissed of the interaction bits: the friendship history (HaveBeenFriends,
        // HaveBeenBestFriends, HaveLostFriendship) is wiped, so ChangeState fires NewFriend again, Charisma's friend
        // counts climb again and LostAFriend can repeat. About 36 callers (CAS links, story progression, opportunities).
        // Fix: right after EA's ChangeBitsForState in a forced change (before ChangeState reads them) the history bits
        // the relationship had are put back. Stranger still clears everything, as EA. Counters: [3007] forced changes
        // with history kept, [3008] history wiped (switch off), [3009] errors.
        static readonly FieldInfo sLtBits = typeof(LongTermRelationship).GetField("mInteraction", kB11All);
        const LongTermRelationship.InteractionBits kLtHistory = LongTermRelationship.InteractionBits.HaveBeenFriends | LongTermRelationship.InteractionBits.HaveBeenBestFriends | LongTermRelationship.InteractionBits.HaveLostFriendship;
        static LongTermRelationship sLtForced;
        static LongTermRelationship.InteractionBits sLtSaved;
        static string sLtMissing = "";

        static void ApplyLtrHistoryBits()
        {
            sLtMissing = SpNames("LongTermRelationship.mInteraction", sLtBits);
            if (sLtMissing.Length > 0) { AppliedLog().Add("ltrhistorybits: not found: " + sLtMissing); return; }
            Patch(typeof(LongTermRelationship), "ForceChangeState", new Type[] { typeof(LongTermRelationshipTypes) }, typeof(BLtHook), "ForceHook", "ForceOrig");
            Patch(typeof(LongTermRelationship), "ChangeBitsForState", new Type[] { typeof(LongTermRelationshipTypes) }, typeof(BLtHook), "BitsHook", "BitsOrig");
        }

        // Pure: the bits after the forced change's own bit step: the saved history put back (fix), Stranger excepted.
        internal static LongTermRelationship.InteractionBits LtKeep(LongTermRelationship.InteractionBits now, LongTermRelationship.InteractionBits before, bool stranger)
        {
            return stranger ? now : now | (before & kLtHistory);
        }

        public class BLtHook
        {
            public void ForceHook(LongTermRelationshipTypes newState)
            {
                LongTermRelationship me = (LongTermRelationship)(object)this;
                LongTermRelationship prev = sLtForced;
                LongTermRelationship.InteractionBits prevSaved = sLtSaved;
                try
                {
                    sLtForced = newState == LongTermRelationshipTypes.Stranger ? null : me;
                    sLtSaved = (LongTermRelationship.InteractionBits)sLtBits.GetValue(me);
                }
                catch (Exception) { sLtForced = null; PatchCounters()[3009]++; }
                try { ForceOrig(newState); }
                finally { sLtForced = prev; sLtSaved = prevSaved; }
            }

            public void ForceOrig(LongTermRelationshipTypes newState) { }

            public void BitsHook(LongTermRelationshipTypes newState)
            {
                BitsOrig(newState);
                if (sLtForced == null || (object)sLtForced != (object)this || (sLtSaved & kLtHistory) == 0) return;
                try
                {
                    LongTermRelationship.InteractionBits now = (LongTermRelationship.InteractionBits)sLtBits.GetValue(this);
                    LongTermRelationship.InteractionBits kept = LtKeep(now, sLtSaved, false);
                    if (kept == now) return;
                    if (ShortcutOn("ltrhistorybits")) { sLtBits.SetValue(this, kept); PatchCounters()[3007]++; }
                    else PatchCounters()[3008]++;
                }
                catch (Exception) { PatchCounters()[3009]++; }
            }

            public void BitsOrig(LongTermRelationshipTypes newState) { }
        }

        // ---- B11 §9 goodbyepure (U9) ------------------------------------------------------------------------------------
        // [verified, decompiled + social XML] SocialComponent.GetGoodbyeSocial only names the goodbye for the pie menu,
        // but for a friendly pair it also removes SoCold from both Sims and despawns a Haunted ghost: opening the menu
        // does what the hug does. The "Goodbye Hug" social's own after-callback (GoodbyeAndHug -> OnHug) already does
        // that removal when the hug happens. Fix: our copy returns the same name with no side effects. Counters:
        // [3010] menu builds kept pure, [3011] side effects run (switch off), [3012] errors.
        static void ApplyGoodbyePure()
        {
            Patch(typeof(SocialComponent), "GetGoodbyeSocial", new Type[] { typeof(Sim), typeof(Sim) }, typeof(BGbHook), "NameHook", "NameOrig");
        }

        public class BGbHook
        {
            public string NameHook(Sim actor, Sim target)
            {
                if (!ShortcutOn("goodbyepure"))
                {
                    string n = NameOrig(actor, target);
                    if (n == "Goodbye Hug") PatchCounters()[3011]++;
                    return n;
                }
                try
                {
                    string name = GbName(actor, target);
                    if (name == "Goodbye Hug") PatchCounters()[3010]++;
                    return name;
                }
                catch (Exception) { PatchCounters()[3012]++; return NameOrig(actor, target); }
            }

            public string NameOrig(Sim actor, Sim target) { return null; }
        }

        // EA's GetGoodbyeSocial without the SoCold / Haunted removal.
        static string GbName(Sim actor, Sim target)
        {
            Relationship r = Relationship.Get(actor, target, false);
            if (r == null) return "Goodbye";
            CommodityTypes c = r.STC.CurrentCommodity;
            if (c == CommodityTypes.Insulting || c == CommodityTypes.Steamed) return "Goodbye Rude";
            if (r.STC.IsRomantic && r.LTR.HasInteractionBit(LongTermRelationship.InteractionBits.Kissed) && !SocialTest.isToad(actor, target)) return "Goodbye Kiss";
            if (LTRData.Get(r.LTR.CurrentLTR).IsFriendly) return "Goodbye Hug";
            return "Goodbye";
        }

        // ---- B11 §10 bathcleanup (U10) ----------------------------------------------------------------------------------
        // [verified, decompiled] Bathtub.TakeBath.Cleanup always disables the tub's push-away footprints and removes the
        // tub's DuckTimeFun alarm. A second Sim's bath that times out in WaitForTurn or is cancelled while queued runs
        // that Cleanup while another Sim is in the bath: the bather loses the alarm and the footprint that keeps others
        // out. Fix: while another Sim is in this tub (EA's mSimInBath) that interaction's Cleanup leaves those two
        // alone; the rest of EA's Cleanup runs. Counters: [3013] bathers' alarm and footprint kept, [3014] cleared by
        // another Sim's Cleanup (switch off), [3015] errors.
        static readonly FieldInfo sBcInBath = typeof(Bathtub).GetField("mSimInBath", kB11All);
        static readonly Dictionary<Bathtub, bool> sBcKeep = new Dictionary<Bathtub, bool>();
        static string sBcMissing = "";

        static void ApplyBathCleanup()
        {
            sBcMissing = SpNames("Bathtub.mSimInBath", sBcInBath);
            if (sBcMissing.Length > 0) { AppliedLog().Add("bathcleanup: not found: " + sBcMissing); return; }
            ApplyTakeBathCleanup();
            Patch(typeof(Bathtub), "DisableRoutingSlotFootprints", Type.EmptyTypes, typeof(BBcTubHook), "FootHook", "FootOrig");
            Patch(typeof(Bathtub), "CleanupAlarms", Type.EmptyTypes, typeof(BBcTubHook), "AlarmHook", "AlarmOrig");
        }

        // One swap of TakeBath.Cleanup serves bathcleanup and bathsituationfx (Batch589dLogic.cs BsfBeforeCleanup); either
        // Apply installs it once.
        static bool sTbCleanupPatched;
        static void ApplyTakeBathCleanup()
        {
            if (sTbCleanupPatched) return;
            sTbCleanupPatched = Patch(typeof(Bathtub.TakeBath), "Cleanup", Type.EmptyTypes, typeof(BBcHook), "CleanupHook", "CleanupOrig");
        }

        // Pure: does this Cleanup leave the tub's shared state alone? Only while another Sim bathes in it.
        internal static bool BcKeeps(bool on, object inBath, object actor) { return on && inBath != null && inBath != actor; }

        public class BBcHook
        {
            public void CleanupHook()
            {
                Bathtub tub = null;
                BsfBeforeCleanup(this);  // bathsituationfx
                if (sBcInBath != null) try
                {
                    Bathtub.TakeBath b = (Bathtub.TakeBath)(object)this;
                    object inBath = b.Target != null ? sBcInBath.GetValue(b.Target) : null;
                    if (inBath != null && inBath != b.Actor)
                    {
                        if (BcKeeps(ShortcutOn("bathcleanup"), inBath, b.Actor)) { tub = b.Target; sBcKeep[tub] = true; PatchCounters()[3013]++; }
                        else PatchCounters()[3014]++;
                    }
                }
                catch (Exception) { PatchCounters()[3015]++; }
                try { CleanupOrig(); }
                finally { if (tub != null) sBcKeep.Remove(tub); }
            }

            public void CleanupOrig() { }
        }

        public class BBcTubHook
        {
            public void FootHook() { if (sBcKeep.Count > 0 && sBcKeep.ContainsKey((Bathtub)(object)this)) return; FootOrig(); }
            public void FootOrig() { }
            public void AlarmHook() { if (sBcKeep.Count > 0 && sBcKeep.ContainsKey((Bathtub)(object)this)) return; AlarmOrig(); }
            public void AlarmOrig() { }
        }

        // ---- B11 §11 tvwatchskill (U11) ---------------------------------------------------------------------------------
        // [verified, decompiled] TV.WatchTV.Cleanup removes the Sim from the TV's watcher list and stops the skill the
        // channel teaches (Cooking, Gardening, Fishing) whether or not this watch ever started it: a WatchTV cancelled
        // in the queue stops the Sim's skill gain from whatever it is doing now. Run itself stops the skill when the
        // watch ends normally. Fix: Cleanup stops the skill only when the Sim was still in the watcher list (EA's
        // `if (mSimsWatchingTv.Remove(Actor))`). Counters: [3016] stray stops skipped, [3017] stray stops (switch off),
        // [3018] errors.
        static readonly FieldInfo sTwList = typeof(TV).GetField("mSimsWatchingTv", kB11All);
        static readonly Dictionary<TV.WatchTV, bool> sTwSkip = new Dictionary<TV.WatchTV, bool>();
        static string sTwMissing = "";

        static void ApplyTvWatchSkill()
        {
            sTwMissing = SpNames("TV.mSimsWatchingTv", sTwList);
            if (sTwMissing.Length > 0) { AppliedLog().Add("tvwatchskill: not found: " + sTwMissing); return; }
            Patch(typeof(TV.WatchTV), "Cleanup", Type.EmptyTypes, typeof(BTwHook), "CleanupHook", "CleanupOrig");
            Patch(typeof(TV.WatchTV), "StopSkillGainForSim", new Type[] { typeof(Sim) }, typeof(BTwHook), "StopHook", "StopOrig");
        }

        public class BTwHook
        {
            public void CleanupHook()
            {
                TV.WatchTV w = (TV.WatchTV)(object)this;
                bool marked = false;
                try
                {
                    if (w.Actor != null && w.Target != null)
                    {
                        List<Sim> list = (List<Sim>)sTwList.GetValue(w.Target);
                        if (list != null && !list.Contains(w.Actor))
                        {
                            if (ShortcutOn("tvwatchskill")) { sTwSkip[w] = true; marked = true; }
                            else PatchCounters()[3017]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[3018]++; }
                try { CleanupOrig(); }
                finally { if (marked) sTwSkip.Remove(w); }
            }

            public void CleanupOrig() { }

            public void StopHook(Sim actor)
            {
                if (sTwSkip.Count > 0 && sTwSkip.ContainsKey((TV.WatchTV)(object)this)) { PatchCounters()[3016]++; return; }
                StopOrig(actor);
            }

            public void StopOrig(Sim actor) { }
        }

        // ---- B11 §12 computergametype (U12) -----------------------------------------------------------------------------
        // [verified, decompiled] Computer.PlayComputerGames.Run, autonomous with the game type None: the random game is
        // written into the definition, which for autonomy is the shared Singleton. From then on every autonomous game
        // is that one (the type is no longer None, so it is never picked again), and a HoloComputer's holo game can turn
        // up on a normal computer. Fix: a run that found the shared definition at None puts None back when it ends, so
        // each autonomous run picks its own game (two games started together still share one pick, as before).
        // Counters: [3019] shared picks reset, [3020] picks left in the Singleton (switch off), [3021] errors.
        static Type sCgDefType;
        static FieldInfo sCgType;
        static string sCgtMissing = "";

        static void ApplyComputerGameType()
        {
            sCgDefType = typeof(Computer.PlayComputerGames).GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic);
            sCgType = sCgDefType != null ? sCgDefType.GetField("mGameType", kB11All) : null;
            sCgtMissing = SpNames("PlayComputerGames+Definition", sCgDefType, "Definition.mGameType", sCgType);
            if (sCgtMissing.Length > 0) { AppliedLog().Add("computergametype: not found: " + sCgtMissing); return; }
            Patch(typeof(Computer.PlayComputerGames), "Run", Type.EmptyTypes, typeof(BCgtHook), "RunHook", "RunOrig");
        }

        public class BCgtHook
        {
            public bool RunHook()
            {
                object def = null;
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    if (ii.Autonomous && (object)ii.InteractionDefinition == (object)Computer.PlayComputerGames.Singleton && (ComputerGames)sCgType.GetValue(ii.InteractionDefinition) == ComputerGames.None)
                        def = ii.InteractionDefinition;
                }
                catch (Exception) { PatchCounters()[3021]++; }
                try { return RunOrig(); }
                finally
                {
                    if (def != null)
                    {
                        try
                        {
                            if ((ComputerGames)sCgType.GetValue(def) != ComputerGames.None)
                            {
                                if (ShortcutOn("computergametype")) { sCgType.SetValue(def, ComputerGames.None); PatchCounters()[3019]++; }
                                else PatchCounters()[3020]++;
                            }
                        }
                        catch (Exception) { PatchCounters()[3021]++; }
                    }
                }
            }

            public bool RunOrig() { return false; }
        }

        // ---- B11 §13 moodtankreset (U13) --------------------------------------------------------------------------------
        // [verified, decompiled] MoodManager.UpdateMood tanks the MoodAngry motive once when the mood turns angry and
        // remembers it in mPreviousMotiveToTank; nothing clears that when the mood recovers (the loop below sets the
        // motive back to 100), so the next angry mood never tanks it. The field is saved, so a reload keeps it. Fix:
        // when the mood is not negative the remembered motive is cleared (the next angry mood tanks again). Counters:
        // [3022] tank memories cleared, [3023] kept with a recovered mood (switch off), [3024] errors.
        static readonly FieldInfo sMtPrev = typeof(MoodManager).GetField("mPreviousMotiveToTank", kB11All);
        static readonly Dictionary<ulong, bool> sMtMay = new Dictionary<ulong, bool>();  // by SimDescriptionId (no Sim kept alive)
        static string sMtMissing = "";

        static void ApplyMoodTankReset()
        {
            sMtMissing = SpNames("MoodManager.mPreviousMotiveToTank", sMtPrev);
            if (sMtMissing.Length > 0) { AppliedLog().Add("moodtankreset: not found: " + sMtMissing); return; }
            Patch(typeof(MoodManager), "UpdateMood", new Type[] { typeof(float), typeof(bool) }, typeof(BMtHook), "MoodHook", "MoodOrig");
        }

        public class BMtHook
        {
            public void MoodHook(float dt, bool delayUpdate)
            {
                MoodOrig(dt, delayUpdate);
                MoodManager mm = (MoodManager)(object)this;
                ulong id;
                try { id = mm.Actor.SimDescription.SimDescriptionId; } catch (Exception) { return; }
                if (mm.IsInNegativeMood) { sMtMay[id] = true; return; }
                bool may;
                if (sMtMay.TryGetValue(id, out may) && !may) return;
                sMtMay[id] = false;
                try
                {
                    if ((Sims3.Gameplay.Autonomy.CommodityKind)sMtPrev.GetValue(mm) != Sims3.Gameplay.Autonomy.CommodityKind.MoodAngry) return;
                    if (ShortcutOn("moodtankreset")) { sMtPrev.SetValue(mm, Sims3.Gameplay.Autonomy.CommodityKind.None); PatchCounters()[3022]++; }
                    else { PatchCounters()[3023]++; sMtMay.Remove(id); }
                }
                catch (Exception) { PatchCounters()[3024]++; }
            }

            public void MoodOrig(float dt, bool delayUpdate) { }
        }

        // ---- B11 §14 reactiongrumpy (U14) -------------------------------------------------------------------------------
        // [verified, decompiled] ReactionInteraction.SetReactionType: a Grumpy Sim turns a positive reaction into
        // TraitGrumpy, but with any other reaction the type is never set and stays 0 (Angry). Fix: for a Grumpy Sim and
        // a non-positive reaction the given reaction is set, as for every other Sim. Counters: [3025] reactions set,
        // [3026] left at Angry (switch off), [3027] errors.
        static readonly FieldInfo sRgType = typeof(ReactionInteraction).GetField("mReactionType", kB11All);
        static string sRgMissing = "";

        static void ApplyReactionGrumpy()
        {
            sRgMissing = SpNames("ReactionInteraction.mReactionType", sRgType);
            if (sRgMissing.Length > 0) { AppliedLog().Add("reactiongrumpy: not found: " + sRgMissing); return; }
            Patch(typeof(ReactionInteraction), "SetReactionType", new Type[] { typeof(ReactionTypes) }, typeof(BRgHook), "SetHook", "SetOrig");
        }

        // Pure: the type a Grumpy Sim gets: TraitGrumpy for a positive reaction, else the reaction (EA: unset).
        internal static ReactionTypes RgType(bool positive, ReactionTypes r) { return positive ? ReactionTypes.TraitGrumpy : r; }

        public class BRgHook
        {
            public void SetHook(ReactionTypes reactionType)
            {
                SetOrig(reactionType);
                try
                {
                    ReactionInteraction ri = (ReactionInteraction)(object)this;
                    if (ri.Actor == null || !ri.Actor.TraitManager.HasElement(TraitNames.Grumpy)) return;
                    IdleAnimationInfo info;
                    if (IdleManager.sReactionAnimations.TryGetValue(reactionType, out info) && info.Positive) return;
                    if ((ReactionTypes)sRgType.GetValue(ri) == reactionType) return;
                    if (ShortcutOn("reactiongrumpy")) { sRgType.SetValue(ri, RgType(false, reactionType)); PatchCounters()[3025]++; }
                    else PatchCounters()[3026]++;
                }
                catch (Exception) { PatchCounters()[3027]++; }
            }

            public void SetOrig(ReactionTypes reactionType) { }
        }

        // ---- B11 §15 trainsimgone (U15) ---------------------------------------------------------------------------------
        // [verified, decompiled] AthleticGameObject.TrainSim.Run: when the trainee left between Test and Run, Trainee
        // and the machine's other actor are both null and the wait loop's Trainee.LotCurrent throws at once (ts3fixes
        // trainboost covers the skill-gain leak on reset, not this). Fix: with no trainee Run ends (false) before EA's
        // body. Counters: [3028] trainee-less runs ended, [3029] runs that threw (switch off), [3030] errors.
        static void ApplyTrainSimGone()
        {
            Patch(typeof(AthleticGameObject.TrainSim), "Run", Type.EmptyTypes, typeof(BTgHook), "RunHook", "RunOrig");
        }

        public class BTgHook
        {
            public bool RunHook()
            {
                try
                {
                    AthleticGameObject.TrainSim t = (AthleticGameObject.TrainSim)(object)this;
                    if (t.Trainee == null && t.Target != null && t.Target.OtherActor(t.Actor) == null)
                    {
                        if (ShortcutOn("trainsimgone")) { PatchCounters()[3028]++; return false; }
                        PatchCounters()[3029]++;
                    }
                }
                catch (Exception) { PatchCounters()[3030]++; }
                return RunOrig();
            }

            public bool RunOrig() { return false; }
        }

        // ---- B11 §16 stereoupgrade (U16) --------------------------------------------------------------------------------
        // [verified, decompiled] Stereo.UpgradeChangedCallback (wired speakers added or removed) disposes the music
        // broadcaster and builds a new one whatever the stereo's state: a stereo that is off or broken starts giving
        // music buffs and waking sleepers. DanceTogetherA.Cleanup, for a pair dancing at a wall speaker, calls the
        // speaker's DismissDancers: ending any pair (even one cancelled in the queue) sends every dancer at that speaker
        // away; the pair's own RemoveDancer calls follow. Fix: the broadcaster is rebuilt only when there was one (the
        // stereo was playing), and a pair's Cleanup removes only its own dancers. Counters: [3031] rebuilds skipped on a
        // silent stereo, [3032] dismissals of the other dancers skipped, [3033] off-switch hits of either.
        static MethodInfo sSuReset, sSuSet;
        static readonly Dictionary<WallMountedSpeaker, bool> sSuKeep = new Dictionary<WallMountedSpeaker, bool>();
        static string sSuMissing = "";
        delegate void SuVoidFn();

        static void ApplyStereoUpgrade()
        {
            sSuReset = typeof(Stereo).GetMethod("ResetBroadcaster", kB11All);
            sSuSet = typeof(Stereo).GetMethod("SetBroadcaster", kB11All);
            sSuMissing = SpNames("Stereo.ResetBroadcaster", sSuReset, "Stereo.SetBroadcaster", sSuSet);
            if (sSuMissing.Length > 0) { AppliedLog().Add("stereoupgrade: not found: " + sSuMissing); return; }
            Patch(typeof(Stereo), "UpgradeChangedCallback", new Type[] { typeof(Upgrade), typeof(Upgrade) }, typeof(BSuUpHook), "UpgradeHook", "UpgradeOrig");
            Patch(typeof(Stereo.DanceTogetherA), "Cleanup", Type.EmptyTypes, typeof(BSuDanceHook), "CleanupHook", "CleanupOrig");
            Patch(typeof(WallMountedSpeaker), "DismissDancers", Type.EmptyTypes, typeof(BSuSpeakerHook), "DismissHook", "DismissOrig");
        }

        // Pure: does the upgrade change rebuild the broadcaster? EA always; the fix only when one was running.
        internal static bool SuRebuild(bool on, bool hadBroadcaster) { return !on || hadBroadcaster; }

        public class BSuUpHook
        {
            public void UpgradeHook(Upgrade oldUpgrade, Upgrade newUpgrade)
            {
                if (newUpgrade != Upgrade.WireHouseWithSpeakers && oldUpgrade != Upgrade.WireHouseWithSpeakers) return;
                Stereo s = (Stereo)(object)this;
                bool had = s.SoundBroadcaster != null;
                bool on = ShortcutOn("stereoupgrade");
                if (had || !on) { if (!had) PatchCounters()[3033]++; UpgradeOrig(oldUpgrade, newUpgrade); return; }
                try
                {
                    ((SuVoidFn)Delegate.CreateDelegate(typeof(SuVoidFn), s, sSuReset))();
                    if (SuRebuild(on, had)) ((SuVoidFn)Delegate.CreateDelegate(typeof(SuVoidFn), s, sSuSet))();
                    PatchCounters()[3031]++;
                }
                catch (Exception) { UpgradeOrig(oldUpgrade, newUpgrade); }
            }

            public void UpgradeOrig(Upgrade oldUpgrade, Upgrade newUpgrade) { }
        }

        public class BSuDanceHook
        {
            public void CleanupHook()
            {
                WallMountedSpeaker sp = null;
                try
                {
                    Stereo.DanceTogetherA d = (Stereo.DanceTogetherA)(object)this;
                    Stereo.DanceTogetherA.Definition def = d.InteractionDefinition as Stereo.DanceTogetherA.Definition;
                    if (def != null) sp = def.DanceObject as WallMountedSpeaker;
                    if (sp != null)
                    {
                        if (ShortcutOn("stereoupgrade")) sSuKeep[sp] = true;
                        else { PatchCounters()[3033]++; sp = null; }
                    }
                }
                catch (Exception) { sp = null; }
                try { CleanupOrig(); }
                finally { if (sp != null) sSuKeep.Remove(sp); }
            }

            public void CleanupOrig() { }
        }

        public class BSuSpeakerHook
        {
            public void DismissHook()
            {
                if (sSuKeep.Count > 0 && sSuKeep.ContainsKey((WallMountedSpeaker)(object)this)) { PatchCounters()[3032]++; return; }
                DismissOrig();
            }

            public void DismissOrig() { }
        }
    }
}
