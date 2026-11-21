using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.TombObjects;
using Sims3.Gameplay.Skills;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // World Adventures and skills (docs/574 §4-7), each off by default. EA bug fixes; no Sim is moved.
    //
    // sandscoins (kSandsCoins, docs/574 §4) [verified, TreasureChest.OpenWithAnimation.TreasureChestOpenedCallback]:
    //   opening a chest treated with Sands of Understanding creates an AncientCoinPile out of the world, shows "bonus
    //   coins", uses up the sands and never gives the pile to anyone: no coins, and a leaked object in the save each
    //   time. Fix: the same steps with the pile added to the opener's inventory, where EA's own AncientCoinPile add event
    //   credits the household's coins and removes it (a refused pile is destroyed). Counters [2660] piles given, [2661] errors.
    // tombmusic (kTombMusic, docs/574 §5) [verified, TombRoomManager statics, cosmetic]: sMusicType and the two music
    //   handles are plain statics never reset; after quitting while in a tomb, the next tomb room of the same music type
    //   only repositions a dead handle, so no tomb music plays until the type changes. Fix: reset at world quit.
    //   Counters [2674] resets, [2675] errors.
    // artsellnoartist (kArtSellNoArtist, docs/574 §6) [code verified, culled-painter trigger inferred]: ImageObject
    //   .SellImageObject pays the seller, then dereferences Artist (SimDescription.Find of the painter) without a null
    //   check and only after that destroys the item: for art whose painter no longer exists the sale throws after the
    //   payment and the painting or photo stays, sellable again. Fix: the artist's sold event is skipped when the artist
    //   is gone; payment and destroy run as EA meant. Counters [2662] sales completed without an artist, [2663] errors.
    // royaltyorphan (kRoyaltyOrphan, docs/574 §7) [verified code, never-fires inferred]: a writer imported on travel
    //   gets a royalty alarm created before SkillManager sets the skill's owner (owner null); Writing.MergeTravelData
    //   then builds a new alarm for the home skill sharing the same royalty list and never removes the imported one, so
    //   a persisted weekly alarm (holding the disposed travel copy) piles up per trip per writer. Fix: after the merge,
    //   the imported skill's alarm is removed. Counters [2664] alarms removed, [2665] errors.
    // Checks: WaSkillsLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kWsAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly Type sScType = typeof(TreasureChest).GetNestedType("OpenWithAnimation", kWsAll);
        static readonly FieldInfo sScFlag = typeof(TreasureChest).GetField("mSandsOfUnderstandingCanAddCoins", kWsAll);
        static readonly FieldInfo sScWeights = typeof(TreasureChest).GetField("kSandsOfUnderstandingCoinPileSizeWeightedChances", kWsAll);
        static readonly MethodInfo sScLocalize = sScType == null ? null : sScType.GetMethod("LocalizeString", kWsAll);
        static readonly Type sTmType = typeof(Sims3.Gameplay.Controllers.TombRoomManager);
        static readonly FieldInfo sTmMusic = sTmType.GetField("sMusicType", kWsAll);
        static readonly FieldInfo sTmHandle = sTmType.GetField("sTombMusicHandle", kWsAll);
        static readonly FieldInfo sTmPlaylist = sTmType.GetField("sTombPlaylistHandle", kWsAll);
        static readonly FieldInfo sRoAlarm = typeof(Writing).GetField("mRoyaltyAlarm", kWsAll);
        static bool sWsHooked;

        internal static string WsMissing()
        {
            string m = "";
            if (sScType == null) m += "TreasureChest.OpenWithAnimation ";
            if (sScFlag == null) m += "TreasureChest.mSandsOfUnderstandingCanAddCoins ";
            if (sScWeights == null) m += "TreasureChest.kSandsOfUnderstandingCoinPileSizeWeightedChances ";
            if (sScLocalize == null) m += "OpenWithAnimation.LocalizeString ";
            if (sTmMusic == null) m += "TombRoomManager.sMusicType ";
            if (sTmHandle == null) m += "TombRoomManager.sTombMusicHandle ";
            if (sTmPlaylist == null) m += "TombRoomManager.sTombPlaylistHandle ";
            if (sRoAlarm == null) m += "Writing.mRoyaltyAlarm ";
            return m.Trim();
        }

        static void ApplyWaSkillsLogic()
        {
            if (sScType != null && sScFlag != null && sScWeights != null && sScLocalize != null)
                Patch(sScType, "TreasureChestOpenedCallback", typeof(SandsCoinsHook), "OpenedHook", "OpenedOrig");
            Patch(typeof(ImageObject), "SellImageObject", new Type[] { typeof(Sim), typeof(int), typeof(bool) }, typeof(ArtSellHook), "SellHook", "SellOrig");
            if (sRoAlarm != null) Patch(typeof(Writing), "MergeTravelData", typeof(RoyaltyOrphanHook), "MergeHook", "MergeOrig");
            if (!sWsHooked)
            {
                sWsHooked = true;
                World.OnWorldQuitEventHandler += delegate { TombMusicQuit(); };
            }
            if (WsMissing().Length > 0) AppliedLog().Add("WA/skills logic: members not found (" + WsMissing() + ")");
        }

        // ---- sandscoins ----
        // Who ends up with the bonus pile: 0 = nobody (EA), 1 = the opener's inventory, 2 = destroyed (refused).
        internal static int ScPileGoes(bool on, bool sandsReady, bool inventoryTakes)
        {
            if (!sandsReady || !on) return 0;
            return inventoryTakes ? 1 : 2;
        }

        public class SandsCoinsHook
        {
            public void OpenedHook(StateMachineClient smc, IEvent evt)
            {
                try
                {
                    if (ShortcutOn("sandscoins"))
                    {
                        InteractionInstance ii = (InteractionInstance)(object)this;
                        Sim actor = ii.InstanceActor;
                        TreasureChest chest = ii.Target as TreasureChest;
                        if (actor != null && chest != null && chest.SandsOfUnderstandingComponent != null && (bool)sScFlag.GetValue(chest))
                        {
                            // EA's steps, with the pile handed to the opener (EA's add event credits the coins)
                            AncientCoinPile pile = Sims3.Gameplay.GlobalFunctions.CreateObjectOutOfWorld("AncientCoinPile") as AncientCoinPile;
                            if (pile != null)
                                pile.CurrentTuningSize = (Sims3.Gameplay.ObjectComponents.PileTuningSizes)Sims3.Gameplay.Core.RandomUtil.GetWeightedIndex((float[])sScWeights.GetValue(null));
                            string text = (string)sScLocalize.Invoke(null, new object[] { actor.IsFemale, "TnsTextBonusCoinsFromSandsOfUnderstanding", new object[] { actor } });
                            actor.ShowTNSIfSelectable(text, Sims3.UI.StyledNotification.NotificationStyle.kGameMessagePositive, actor.ObjectId, chest.ObjectId);
                            sScFlag.SetValue(chest, false);
                            chest.RemoveComponent<Sims3.Gameplay.ObjectComponents.SandsOfUnderstandingComponent>();
                            if (pile != null)
                            {
                                if (ScPileGoes(true, true, actor.Inventory != null && actor.Inventory.TryToAdd(pile)) == 1) PatchCounters()[2660]++;
                                else pile.Destroy();
                            }
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2661]++; }
                OpenedOrig(smc, evt);  // the flag is now cleared, so EA's own bonus block does not run again
            }
            public void OpenedOrig(StateMachineClient smc, IEvent evt) { }
        }

        // ---- tombmusic ----
        internal static bool TmReset(bool on, bool typeSet)
        {
            return on && typeSet;
        }

        static void TombMusicQuit()
        {
            try
            {
                if (!ShortcutOn("tombmusic") || sTmMusic == null || sTmHandle == null || sTmPlaylist == null) return;
                object none = Enum.ToObject(sTmMusic.FieldType, 0);
                if (TmReset(true, !sTmMusic.GetValue(null).Equals(none) || (uint)sTmHandle.GetValue(null) != 0u || (uint)sTmPlaylist.GetValue(null) != 0u))
                {
                    sTmMusic.SetValue(null, none);
                    sTmHandle.SetValue(null, 0u);
                    sTmPlaylist.SetValue(null, 0u);
                    PatchCounters()[2674]++;
                }
            }
            catch (Exception) { PatchCounters()[2675]++; }
        }

        // ---- artsellnoartist ----
        // The sale with no artist: true when it completes (payment and destroy), EA throws before the destroy.
        internal static bool AsCompletes(bool on, bool artistGone)
        {
            return on || !artistGone;
        }

        public class ArtSellHook
        {
            public void SellHook(Sim seller, int value, bool destroy)
            {
                ImageObject me = (ImageObject)(object)this;
                bool gone;
                try { gone = me.Artist == null; }
                catch (Exception) { gone = false; }
                // EA completes a sale with an artist; with none it completes only through our path
                if (!gone || !AsCompletes(ShortcutOn("artsellnoartist"), true)) { SellOrig(seller, value, destroy); return; }
                // EA's steps without the artist's sold event (there is no artist)
                seller.ModifyFunds(value);
                me.SendObjectSoldEvent(seller);
                if (destroy)
                {
                    me.FadeOut();
                    me.Destroy();
                }
                PatchCounters()[2662]++;
            }
            public void SellOrig(Sim seller, int value, bool destroy) { }
        }

        // ---- royaltyorphan ----
        internal static bool RoRemoveImported(bool on, bool importedHasAlarm, bool sameAlarm)
        {
            return on && importedHasAlarm && !sameAlarm;
        }

        public class RoyaltyOrphanHook
        {
            public void MergeHook(Skill skill)
            {
                MergeOrig(skill);
                try
                {
                    Writing imported = skill as Writing;
                    if (imported != null && ShortcutOn("royaltyorphan"))
                    {
                        Writing.RoyaltyAlarm a = sRoAlarm.GetValue(imported) as Writing.RoyaltyAlarm;
                        Writing.RoyaltyAlarm mine = sRoAlarm.GetValue((object)this) as Writing.RoyaltyAlarm;
                        if (RoRemoveImported(true, a != null, a == mine))
                        {
                            a.RemoveRoyaltyAlarm();  // the royalty list stays shared with the home alarm
                            PatchCounters()[2664]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2665]++; }
            }
            public void MergeOrig(Skill skill) { }
        }
    }
}
