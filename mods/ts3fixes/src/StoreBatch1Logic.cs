using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.Store.Objects;

namespace Ts3Fixes
{
    // Store batch 1 (docs/587 §13): hand read of Sims3StoreObjects (79 files, 5 read-only agents). Every switch off by
    // default; all household-visible. Checks: StoreBatch1Checks.cs. Three counter slots each: fixed / seen with the
    // switch off / errors. Slots from 1621 (block fixes-1600).
    //   spellbooksummonload  [1621] SpellBook+CastingStore.ImportContent: the Summon spell (enum 0) is skipped on import
    //   chocfountaingeyser   [1624] ChocolateFountain.FountainFilled: a new repeating geyser alarm on every fill
    //   hypnotrickrelease    [1627] Hypnotizer+PerformTrick.Run: the volunteer's route lock is never cleared
    //   voodoofairyfadein    [1630] VoodooDoll+TransformSim.Cleanup: fades in the caster, not the hidden fairy
    //   saunatubclear        [1633] SaunaClassic+BatheInWater.Run: mSimInTub left set after a reset or exception
    //   slotmachinedefs      [1636] SlotMachine+PlaySlots+Definition.InitFromID: autonomous play rewrites the shared menu
    //   treewishingwell      [1639] TreeOfProsperity.HasWishingWellOnLot: counts IWishingWell, which no class implements
    //   playpentalkcast      [1642] PlaypenClassic.CreateLearnToTalkBroadcaster: overwrites a live broadcaster
    //   ovenmenunull         [1645] IndustrialOven+Menu.PickGoodFoodForMe: null food list dereferenced
    //   fvstandskillcurve    [1648] FruitVeggieStand.InitializeData: skill curve built from the frequency arrays
    //   pokerdeadplayer      [1651] PokerTable dealer helpers: a seated Sim who is gone throws and kills the table
    //   roulettehackreset    [1654] RouletteTable.DoReset: the hack stays on after the reset deletes its timer
    //   coopeggquality       [1657] ChickenCoopClassic+HarvestEggs.Run: weighted index cast to Quality one tier low
    public static partial class Fixes
    {
        const BindingFlags kSbNested = BindingFlags.Public | BindingFlags.NonPublic;

        static void ApplyStoreBatch1()
        {
            ApplySpellBookSummonLoad(); ApplyChocFountainGeyser(); ApplyHypnoTrickRelease(); ApplyVoodooFairyFadeIn(); ApplySaunaTubClear();
            ApplySlotMachineDefs(); ApplyTreeWishingWell(); ApplyPlaypenTalkCast(); ApplyOvenMenuNull();
            ApplyFvStandSkillCurve(); ApplyPokerDeadPlayer();
            ApplyRouletteHackReset(); ApplyCoopEggQuality();
        }

        // ---- spellbooksummonload (docs/587 §13.1) [verified, decompiled] ----
        // ImportContent keeps an entry only when `value2 != 0 && value3 != 0f`, where value2 is the SpellType byte and
        // Summon is 0, so a learned or partly learned Summon is dropped on every import (exported Sim, travel, moving):
        // KnowsSpell(Summon) turns false and Learn shows 0% again. Fix: after EA's import, the children are read again
        // and a Summon entry with progress is added. RE-found extra: with only Summon learned SpellProgress stayed null
        // and GetSpellsLearned (LearnSpells.Run) threw; the entry now exists.
        internal static bool SbKeep(bool on, byte spell, float progress) { return progress != 0f && (spell != 0 || on); }

        static void ApplySpellBookSummonLoad()
        {
            Patch(typeof(SpellBook.CastingStore), "ImportContent", new Type[] { typeof(IPropertyStreamReader) }, typeof(BSt1SbHook), "ImportHook", "ImportOrig");
        }

        public class BSt1SbHook
        {
            public bool ImportHook(IPropertyStreamReader reader)
            {
                bool r = ImportOrig(reader);
                try
                {
                    SpellBook.CastingStore cs = (SpellBook.CastingStore)(object)this;
                    int count;
                    reader.ReadInt32(199192889u, out count, 0);
                    for (uint i = 0u; i < count; i++)
                    {
                        IPropertyStreamReader child = reader.GetChild(i);
                        if (child == null) continue;
                        byte spell; float progress;
                        child.ReadByte(199192896u, out spell, 0);
                        child.ReadFloat(199192897u, out progress, 0f);
                        if (spell != 0 || progress == 0f) continue;   // EA already kept the others
                        if (!ShortcutOn("spellbooksummonload")) { PatchCounters()[1622]++; continue; }
                        if (!SbKeep(true, spell, progress)) continue;
                        if (cs.SpellProgress == null) cs.SpellProgress = new Dictionary<SpellBook.SpellType, float>();
                        if (!cs.SpellProgress.ContainsKey(SpellBook.SpellType.Summon))
                        {
                            cs.SpellProgress.Add(SpellBook.SpellType.Summon, progress);
                            PatchCounters()[1621]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[1623]++; }
                return r;
            }
            public bool ImportOrig(IPropertyStreamReader reader) { return false; } // SpellBook+CastingStore.ImportContent
        }

        // ---- chocfountaingeyser (docs/587 §13.2) [verified, decompiled] ----
        // FountainFilled replaces the spoilage alarm but assigns a new repeating geyser alarm over mGeyserAlarm without
        // removing the old one, and nothing else removes it: after N fills or flavour changes N geyser rolls run every
        // 30 minutes (each gives watching Sims +60 Fun). Fix: before EA's body, a live geyser alarm is removed.
        static FieldInfo sCfGeyser;
        internal static bool CfRemove(bool on, bool live) { return on && live; }

        static void ApplyChocFountainGeyser()
        {
            sCfGeyser = typeof(ChocolateFountain).GetField("mGeyserAlarm", kInstAll);
            if (sCfGeyser == null) { AppliedLog().Add("chocfountaingeyser: not found: ChocolateFountain.mGeyserAlarm"); return; }
            Patch(typeof(ChocolateFountain), "FountainFilled", Type.EmptyTypes, typeof(BSt1CfHook), "FilledHook", "FilledOrig");
        }

        public class BSt1CfHook
        {
            public void FilledHook()
            {
                try
                {
                    ChocolateFountain f = (ChocolateFountain)(object)this;
                    AlarmHandle h = (AlarmHandle)sCfGeyser.GetValue(f);
                    if (h != AlarmHandle.kInvalidHandle)
                    {
                        if (!ShortcutOn("chocfountaingeyser")) PatchCounters()[1625]++;
                        else if (CfRemove(true, true)) { f.RemoveAlarm(h); sCfGeyser.SetValue(f, AlarmHandle.kInvalidHandle); PatchCounters()[1624]++; }
                    }
                }
                catch (Exception) { PatchCounters()[1626]++; }
                FilledOrig();
            }
            public void FilledOrig() { } // ChocolateFountain.FountainFilled
        }

        // ---- hypnotrickrelease (docs/587 §13.3) [verified code; reach: a household volunteer] ----
        // PerformTrick.Run sets RouteLock on the volunteer's BeHypnotizedMainLoop and never clears it (the Hypnotize and
        // command paths and BoxOfMystery's trick do). That loop repeats while RouteLock is set, so a volunteer the show
        // does not cancel (a household Sim) stays frozen until the player cancels. Fix: when Run ends (also by reset or
        // exception), the lock of the loop linked to this trick is cleared. Run yields; the wrapper keeps EA's body.
        // Not fixed: Run always returns false (its flag is never set); only commodity bookkeeping depends on it.
        static Type sHtLoop;
        static FieldInfo sHtLock;
        internal static bool HtRelease(bool on, bool linked, bool locked) { return on && linked && locked; }

        static void ApplyHypnoTrickRelease()
        {
            Type b = typeof(Hypnotizer).GetNestedType("HypnosisInteractionBase", kSbNested);
            sHtLoop = typeof(Hypnotizer).GetNestedType("BeHypnotizedMainLoop", kSbNested);
            if (b != null) sHtLock = b.GetField("mRouteLock", kInstAll);   // review: the RouteLock property has only a setter
            if (sHtLoop == null || sHtLock == null) { AppliedLog().Add("hypnotrickrelease: not found: " + EpMiss("BeHypnotizedMainLoop", sHtLoop, "mRouteLock", sHtLock)); return; }
            Patch(typeof(Hypnotizer.PerformTrick), "Run", Type.EmptyTypes, typeof(BSt1HtHook), "RunHook", "RunOrig");
        }

        public class BSt1HtHook
        {
            public bool RunHook()
            {
                try { return RunOrig(); }
                finally
                {
                    try
                    {
                        Hypnotizer.PerformTrick pt = (Hypnotizer.PerformTrick)(object)this;
                        Sim v = pt.mVolunteerSim;
                        if (v != null && v.InteractionQueue != null)
                        {
                            foreach (InteractionInstance ii in v.InteractionQueue.InteractionList)
                            {
                                if (ii == null || ii.GetType() != sHtLoop || ii.LinkedInteractionInstance != pt) continue;
                                if (!(bool)sHtLock.GetValue(ii)) continue;
                                if (!ShortcutOn("hypnotrickrelease")) { PatchCounters()[1628]++; continue; }
                                if (!HtRelease(true, true, true)) continue;
                                sHtLock.SetValue(ii, false);
                                PatchCounters()[1627]++;
                            }
                        }
                    }
                    catch (Exception) { PatchCounters()[1629]++; }
                }
            }
            public bool RunOrig() { return false; } // Hypnotizer+PerformTrick.Run
        }

        // ---- voodoofairyfadein (docs/587 §13.4) [verified, decompiled] ----
        // For a fairy, TransformSim hides the bound Sim (OnHideActor: SetOpacity 0, mVfxFairyHandle started) and shows it
        // again in the event that disposes the handle. Cleanup, which runs when the interaction is cut between the two,
        // calls Actor.FadeIn() (the caster, already visible) and never stops the handle, so the bound fairy stays
        // invisible and the effect leaks. Fix: after EA's Cleanup, while the handle is still set, it is stopped and
        // disposed and the bound Sim fades in.
        static Type sVfType;
        static FieldInfo sVfHandle;
        internal static bool VfShow(bool on, bool hidden) { return on && hidden; }

        static void ApplyVoodooFairyFadeIn()
        {
            sVfType = typeof(VoodooDoll).GetNestedType("TransformSim", kSbNested);
            if (sVfType != null) sVfHandle = sVfType.GetField("mVfxFairyHandle", kInstAll);
            if (sVfType == null || sVfHandle == null) { AppliedLog().Add("voodoofairyfadein: not found: " + EpMiss("TransformSim", sVfType, "mVfxFairyHandle", sVfHandle)); return; }
            Patch(typeof(VoodooDoll).GetNestedType("TransformSim", BindingFlags.Public | BindingFlags.NonPublic), "Cleanup", Type.EmptyTypes, typeof(BSt1VfHook), "CleanupHook", "CleanupOrig");
        }

        public class BSt1VfHook
        {
            public void CleanupHook()
            {
                VisualEffect fx = null;
                try { fx = sVfHandle.GetValue(this) as VisualEffect; } catch (Exception) { PatchCounters()[1632]++; }
                CleanupOrig();
                if (fx == null) return;
                try
                {
                    if (!ShortcutOn("voodoofairyfadein")) { PatchCounters()[1631]++; return; }
                    if (!VfShow(true, true)) return;
                    fx.Stop(VisualEffect.TransitionType.HardTransition);   // as EA's OnShowActor
                    fx.Dispose();
                    sVfHandle.SetValue(this, null);
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    VoodooDoll doll = ii.Target as VoodooDoll;
                    Sim bound = doll == null ? null : doll.GetBoundSim();
                    if (bound != null) bound.FadeIn();
                    PatchCounters()[1630]++;
                }
                catch (Exception) { PatchCounters()[1632]++; }
            }
            public void CleanupOrig() { } // VoodooDoll+TransformSim.Cleanup
        }

        // ---- saunatubclear (docs/587 §13.5) [verified, decompiled] ----
        // BatheInWater.Run sets Target.mSimInTub = Actor and clears it only at the end of the normal path; the class has
        // no Cleanup. A reset, exception or death while bathing leaves it set, and every BatheInWater test then fails
        // ("InUse") until the sauna itself is reset. Fix: when Run ends by any path, mSimInTub is cleared if it is still
        // this Actor. Run yields; the wrapper keeps EA's body.
        static FieldInfo sStTub;
        internal static bool StClear(bool on, bool stillActor) { return on && stillActor; }

        static void ApplySaunaTubClear()
        {
            sStTub = typeof(SaunaClassic).GetField("mSimInTub", kInstAll);
            if (sStTub == null) { AppliedLog().Add("saunatubclear: not found: SaunaClassic.mSimInTub"); return; }
            Patch(typeof(SaunaClassic).GetNestedType("BatheInWater", BindingFlags.Public | BindingFlags.NonPublic), "Run", Type.EmptyTypes, typeof(BSt1StHook), "RunHook", "RunOrig");
        }

        public class BSt1StHook
        {
            public bool RunHook()
            {
                try { return RunOrig(); }
                finally
                {
                    try
                    {
                        InteractionInstance ii = (InteractionInstance)(object)this;
                        SaunaClassic sauna = ii.Target as SaunaClassic;
                        if (sauna != null && sStTub.GetValue(sauna) == (object)ii.InstanceActor)
                        {
                            if (!ShortcutOn("saunatubclear")) PatchCounters()[1634]++;
                            else if (StClear(true, true)) { sStTub.SetValue(sauna, null); PatchCounters()[1633]++; }
                        }
                    }
                    catch (Exception) { PatchCounters()[1635]++; }
                }
            }
            public bool RunOrig() { return false; } // SaunaClassic+BatheInWater.Run
        }

        // ---- slotmachinedefs (docs/587 §13.6) [verified, decompiled] ----
        // AddInteractions hands out the four static PlaySlotDefs; for an autonomous Sim PlaySlots.Run calls
        // mCurrDef.InitFromID(PlaySlotDefs[GetInt(2)]) on the definition it was queued with, i.e. one of those shared
        // objects, so every autonomous play (NPC visitors too) rewrites a menu entry's name, stakes and kind for the rest
        // of the session: duplicate entries, Lucky Coin gone, "Low" charging high stakes. Fix: before EA's Run, an
        // autonomous play on a shared entry gets its own copy of that entry, so EA's Low/Medium pick rewrites the copy.
        // (Review: the first version skipped InitFromID, which let autonomous Sims play High or spend a Lucky Coin.)
        static FieldInfo sSmDefs;
        static Type sSmDef;   // SlotMachine+PlaySlots+Definition
        static MethodInfo sSmInit;
        internal static bool SmCopy(bool on, bool autonomous, bool shared) { return on && autonomous && shared; }

        static void ApplySlotMachineDefs()
        {
            Type play = typeof(SlotMachine).GetNestedType("PlaySlots", kSbNested);
            sSmDef = play == null ? null : play.GetNestedType("Definition", kSbNested);
            if (play != null) sSmDefs = play.GetField("PlaySlotDefs", kEpStatic);
            if (sSmDef != null) sSmInit = sSmDef.GetMethod("InitFromID", kInstAll, null, new Type[] { sSmDef }, null);
            if (sSmDef == null || sSmDefs == null || sSmInit == null) { AppliedLog().Add("slotmachinedefs: not found: " + EpMiss("PlaySlots+Definition", sSmDef, "PlaySlotDefs", sSmDefs, "InitFromID", sSmInit)); return; }
            Patch(typeof(SlotMachine).GetNestedType("PlaySlots", BindingFlags.Public | BindingFlags.NonPublic), "Run", Type.EmptyTypes, typeof(BSt1SmHook), "RunHook", "RunOrig");
        }

        public class BSt1SmHook
        {
            public bool RunHook()
            {
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    object def = ii.InteractionObjectPair == null ? null : ii.InteractionObjectPair.InteractionDefinition;
                    Array defs = sSmDefs.GetValue(null) as Array;
                    bool shared = def != null && defs != null && Array.IndexOf(defs, def) >= 0;
                    if (ii.Autonomous && shared)
                    {
                        if (!ShortcutOn("slotmachinedefs")) PatchCounters()[1637]++;
                        else if (SmCopy(true, true, true))
                        {
                            object copy = Activator.CreateInstance(sSmDef, true);
                            sSmInit.Invoke(copy, new object[] { def });
                            ii.InteractionObjectPair = new Sims3.Gameplay.Autonomy.InteractionObjectPair((InteractionDefinition)copy, ii.Target);
                            PatchCounters()[1636]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[1638]++; }
                return RunOrig();
            }
            public bool RunOrig() { return false; } // SlotMachine+PlaySlots.Run
        }

        // ---- treewishingwell (docs/587 §13.7) [verified, decompiled] ----
        // HasWishingWellOnLot counts IWishingWell objects, but no decompiled class implements IWishingWell (the store
        // WishingWell derives from GameObject with ISetObject), so it is always false: the tree's 23:00 firefly and
        // pixie shows never spawn and Abundance fruit stays limited. Fix: a WishingWell on the lot also counts.
        internal static bool TwHas(bool on, int interfaceCount, int wellCount) { return interfaceCount != 0 || (on && wellCount != 0); }

        static void ApplyTreeWishingWell()
        {
            Patch(typeof(TreeOfProsperity), "get_HasWishingWellOnLot", Type.EmptyTypes, typeof(BSt1TwHook), "HasHook", "HasOrig");
        }

        public class BSt1TwHook
        {
            public bool HasHook()
            {
                bool ea = HasOrig();
                if (ea) return true;
                try
                {
                    TreeOfProsperity t = (TreeOfProsperity)(object)this;
                    if (t.LotCurrent == null) return false;
                    int wells = (int)t.LotCurrent.CountObjects<WishingWell>();
                    if (wells == 0) return false;
                    if (!ShortcutOn("treewishingwell")) { PatchCounters()[1640]++; return false; }
                    PatchCounters()[1639]++;
                    return TwHas(true, 0, wells);
                }
                catch (Exception) { PatchCounters()[1641]++; return false; }
            }
            public bool HasOrig() { return false; } // TreeOfProsperity.get_HasWishingWellOnLot
        }

        // ---- playpentalkcast (docs/587 §13.8) [verified, decompiled] ----
        // CreateLearnToTalkBroadcaster assigns a new ReactionBroadcaster over mLearnToTalkBroadcaster with no check; with
        // two toddlers learning to talk at once the first is never disposed (Remove only disposes the latest) and keeps
        // making nearby Sims react until the playpen is reset. Fix: a live broadcaster is disposed first.
        static FieldInfo sPpCast;
        internal static bool PpDispose(bool on, bool live) { return on && live; }

        static void ApplyPlaypenTalkCast()
        {
            sPpCast = typeof(PlaypenClassic).GetField("mLearnToTalkBroadcaster", kInstAll);
            if (sPpCast == null) { AppliedLog().Add("playpentalkcast: not found: PlaypenClassic.mLearnToTalkBroadcaster"); return; }
            Patch(typeof(PlaypenClassic), "CreateLearnToTalkBroadcaster", Type.EmptyTypes, typeof(BSt1PpHook), "CreateHook", "CreateOrig");
        }

        public class BSt1PpHook
        {
            public void CreateHook()
            {
                try
                {
                    Sims3.Gameplay.Autonomy.ReactionBroadcaster rb = sPpCast.GetValue(this) as Sims3.Gameplay.Autonomy.ReactionBroadcaster;
                    if (rb != null)
                    {
                        if (!ShortcutOn("playpentalkcast")) PatchCounters()[1643]++;
                        else if (PpDispose(true, true)) { rb.Dispose(); sPpCast.SetValue(this, null); PatchCounters()[1642]++; }
                    }
                }
                catch (Exception) { PatchCounters()[1644]++; }
                CreateOrig();
            }
            public void CreateOrig() { } // PlaypenClassic.CreateLearnToTalkBroadcaster
        }

        // ---- ovenmenunull (docs/587 §13.9) [verified code; reach: menu or funds change while a diner waits] ----
        // GetFoodsAtOrBelowCost returns null when nothing is affordable or the menu is empty, and PickGoodFoodForMe
        // dereferences it at once (the caller OrderFood.Run already handles a null pick). Fix: the same cost rule as EA,
        // and a null list returns null before EA's body.
        static MethodInfo sOmFoods;
        internal static bool OmSkip(bool on, bool listNull) { return on && listNull; }

        static void ApplyOvenMenuNull()
        {
            Type menu = typeof(IndustrialOven).GetNestedType("Menu", kSbNested);
            if (menu != null) sOmFoods = menu.GetMethod("GetFoodsAtOrBelowCost", kInstAll, null, new Type[] { typeof(int) }, null);
            if (menu == null || sOmFoods == null) { AppliedLog().Add("ovenmenunull: not found: " + EpMiss("IndustrialOven+Menu", menu, "GetFoodsAtOrBelowCost", sOmFoods)); return; }
            Patch(typeof(IndustrialOven).GetNestedType("Menu", BindingFlags.Public | BindingFlags.NonPublic), "PickGoodFoodForMe", new Type[] { typeof(Sim) }, typeof(BSt1OmHook), "PickHook", "PickOrig");
        }

        public class BSt1OmHook
        {
            public object PickHook(Sim actor)
            {
                try
                {
                    int cost = actor.FamilyFunds;
                    if (actor.TraitManager.HasElement(Sims3.Gameplay.ActorSystems.TraitNames.DiscountDiner)) cost = int.MaxValue;
                    if (sOmFoods.Invoke(this, new object[] { cost }) == null)
                    {
                        if (!ShortcutOn("ovenmenunull")) PatchCounters()[1646]++;
                        else if (OmSkip(true, true)) { PatchCounters()[1645]++; return null; }
                    }
                }
                catch (Exception) { PatchCounters()[1647]++; }
                return PickOrig(actor);
            }
            public object PickOrig(Sim actor) { return null; } // IndustrialOven+Menu.PickGoodFoodForMe
        }

        // ---- fvstandskillcurve (docs/587 §13.10) [verified, decompiled + shipped tuning] ----
        // Same copy-paste as consignmentskillcurve (§8): InitializeData builds FVShoppingRegister.sSkillVsSaleChanceCurve
        // from kSaleProbabilityFrequency X/Y (shipped 0,3,5,6 / .7,.4,.1,0) instead of the skill arrays (0,3,5 /
        // .1,.5,.9), so the stand's sale chance falls as the seller's skill rises (weight .25). Fix: after EA's
        // InitializeData the curve is rebuilt from EA's own skill arrays (once per new curve object).
        static Curve sFvSet, sFvSeen;
        internal static bool FvRebuild(bool on, bool fromFrequency) { return on && fromFrequency; }

        static void ApplyFvStandSkillCurve()
        {
            Patch(typeof(FruitVeggieStand), "InitializeData", Type.EmptyTypes, typeof(BSt1FvHook), "InitHook", "InitOrig");
        }

        public class BSt1FvHook
        {
            public void InitHook()
            {
                InitOrig();
                try
                {
                    Curve cur = FVShoppingRegister.sSkillVsSaleChanceCurve;
                    if (cur == null || cur == sFvSet) return;
                    if (!ShortcutOn("fvstandskillcurve")) { if (cur != sFvSeen) { sFvSeen = cur; PatchCounters()[1649]++; } return; }   // review: seen only counts; on later still rebuilds
                    float[] x = FruitVeggieStand.kSaleProbabilitySkillXValues, y = FruitVeggieStand.kSaleProbabilitySkillYValues;
                    if (!FvRebuild(true, true) || x == null || y == null || x.Length == 0 || x.Length != y.Length) return;
                    sFvSet = new Curve(x, y);
                    FVShoppingRegister.sSkillVsSaleChanceCurve = sFvSet;
                    PatchCounters()[1648]++;
                }
                catch (Exception) { PatchCounters()[1650]++; }
            }
            public void InitOrig() { } // FruitVeggieStand.InitializeData
        }

        // ---- pokerdeadplayer (docs/587 §13.11) [verified code; frequency inferred] ----
        // A Sim leaving the table only marks mPlayersToRemove; its id stays in mPlayers until the next hand. SetupRound,
        // WaitForPlayersToCompleteAction and ActionsAreComplete call SimDescription.Find(id).CreatedSim.InteractionQueue
        // with no null test (the RunPlayer*Action methods do test), so a guest who leaves the lot or a deleted Sim in
        // between throws in the dealer one-shot; mDealerFunction is never cleared and the table stays dead until reset
        // while seated Sims idle up to 360 minutes. Fix: before each of the three, a seat whose Sim no longer exists
        // (no description or no created Sim) is emptied in mPlayers and mPlayersToAdd.
        static FieldInfo sPkPlayers, sPkToAdd;
        internal static bool PkEmpty(bool on, ulong id, bool exists) { return on && id != 0 && !exists; }

        static void ApplyPokerDeadPlayer()
        {
            sPkPlayers = typeof(PokerTable).GetField("mPlayers", kInstAll);
            sPkToAdd = typeof(PokerTable).GetField("mPlayersToAdd", kInstAll);
            if (sPkPlayers == null || sPkToAdd == null) { AppliedLog().Add("pokerdeadplayer: not found: " + EpMiss("mPlayers", sPkPlayers, "mPlayersToAdd", sPkToAdd)); return; }
            Patch(typeof(PokerTable), "SetupRound", Type.EmptyTypes, typeof(BSt1PkHook), "SetupHook", "SetupOrig");
            Patch(typeof(PokerTable), "WaitForPlayersToCompleteAction", new Type[] { typeof(PokerTable.PlayPoker.Action) }, typeof(BSt1PkHook), "WaitHook", "WaitOrig");
            Patch(typeof(PokerTable), "ActionsAreComplete", Type.EmptyTypes, typeof(BSt1PkHook), "DoneHook", "DoneOrig");
        }

        static void PkPurge(object table)
        {
            try
            {
                ulong[] players = sPkPlayers.GetValue(table) as ulong[];
                ulong[] toAdd = sPkToAdd.GetValue(table) as ulong[];
                if (players == null) return;
                for (int i = 0; i < players.Length; i++)
                {
                    ulong id = players[i];
                    if (id == 0) continue;
                    Sims3.Gameplay.CAS.SimDescription d = Sims3.Gameplay.CAS.SimDescription.Find(id);
                    bool exists = d != null && d.CreatedSim != null && d.CreatedSim.InteractionQueue != null;
                    if (exists) continue;
                    if (!ShortcutOn("pokerdeadplayer")) { PatchCounters()[1652]++; continue; }
                    if (!PkEmpty(true, id, false)) continue;
                    players[i] = 0;
                    if (toAdd != null) for (int j = 0; j < toAdd.Length; j++) if (toAdd[j] == id) toAdd[j] = 0;
                    PatchCounters()[1651]++;
                }
            }
            catch (Exception) { PatchCounters()[1653]++; }
        }

        public class BSt1PkHook
        {
            public void SetupHook() { PkPurge(this); SetupOrig(); }
            public void SetupOrig() { } // PokerTable.SetupRound
            public void WaitHook(PokerTable.PlayPoker.Action action) { PkPurge(this); WaitOrig(action); }
            public void WaitOrig(PokerTable.PlayPoker.Action action) { } // PokerTable.WaitForPlayersToCompleteAction
            public bool DoneHook() { PkPurge(this); return DoneOrig(); }
            public bool DoneOrig() { return false; } // PokerTable.ActionsAreComplete
        }

        // ---- roulettehackreset (docs/587 §13.12) [verified, decompiled + shipped tuning] ----
        // DoHackTable sets mCurrentHack and a DeleteOnReset alarm that calls ResetHack after kHackDuration. DoReset never
        // clears mCurrentHack, while the reset deletes the alarm: the table keeps Good or Bad odds (shipped
        // kOddsChangeForHackedTable 45) for good, and Hack / Sabotage stay greyed out. Fix: after EA's DoReset, a set hack
        // runs EA's own ResetHack (clears the hack and the eye effects). Review: PokerTable and BlackjackTable have the
        // same timer and DoReset (Blackjack does not even remove the eye effects); all three are covered.
        static readonly Type[] sRhTypes = { typeof(RouletteTable), typeof(PokerTable), typeof(BlackjackTable) };
        static readonly FieldInfo[] sRhHack = new FieldInfo[3];
        static readonly MethodInfo[] sRhReset = new MethodInfo[3];
        internal static bool RhClear(bool on, int hack) { return on && hack != 0; }

        static void ApplyRouletteHackReset()
        {
            Type ri = typeof(GameObject).GetNestedType("ResetInformation", BindingFlags.Public | BindingFlags.NonPublic);
            if (ri == null) { AppliedLog().Add("roulettehackreset: not found: GameObject+ResetInformation"); return; }
            Type[] hooks = { typeof(BSt1RhHook0), typeof(BSt1RhHook1), typeof(BSt1RhHook2) };
            for (int i = 0; i < 3; i++)
            {
                sRhHack[i] = sRhTypes[i].GetField("mCurrentHack", kInstAll);
                sRhReset[i] = sRhTypes[i].GetMethod("ResetHack", kInstAll, null, Type.EmptyTypes, null);
                if (sRhHack[i] == null || sRhReset[i] == null) { AppliedLog().Add("roulettehackreset: not found: " + sRhTypes[i].Name + " " + EpMiss("mCurrentHack", sRhHack[i], "ResetHack", sRhReset[i])); continue; }
            }
            if (sRhHack[0] != null && sRhReset[0] != null) Patch(typeof(RouletteTable), "DoReset", new Type[] { ri }, typeof(BSt1RhHook0), "ResetHook", "ResetOrig");
            if (sRhHack[1] != null && sRhReset[1] != null) Patch(typeof(PokerTable), "DoReset", new Type[] { ri }, typeof(BSt1RhHook1), "ResetHook", "ResetOrig");
            if (sRhHack[2] != null && sRhReset[2] != null) Patch(typeof(BlackjackTable), "DoReset", new Type[] { ri }, typeof(BSt1RhHook2), "ResetHook", "ResetOrig");
        }

        static void RhAfterReset(object table, int i)
        {
            try
            {
                int hack = Convert.ToInt32(sRhHack[i].GetValue(table));
                if (hack == 0) return;
                if (!ShortcutOn("roulettehackreset")) { PatchCounters()[1655]++; return; }
                if (!RhClear(true, hack)) return;
                sRhReset[i].Invoke(table, null);
                PatchCounters()[1654]++;
            }
            catch (Exception) { PatchCounters()[1656]++; }
        }

        public class BSt1RhHook0
        {
            public void ResetHook(object resetInformation) { ResetOrig(resetInformation); RhAfterReset(this, 0); }
            public void ResetOrig(object resetInformation) { } // RouletteTable.DoReset
        }
        public class BSt1RhHook1
        {
            public void ResetHook(object resetInformation) { ResetOrig(resetInformation); RhAfterReset(this, 1); }
            public void ResetOrig(object resetInformation) { } // PokerTable.DoReset
        }
        public class BSt1RhHook2
        {
            public void ResetHook(object resetInformation) { ResetOrig(resetInformation); RhAfterReset(this, 2); }
            public void ResetOrig(object resetInformation) { } // BlackjackTable.DoReset
        }

        // ---- coopeggquality (docs/587 §13.13) [verified code + shipped tuning; intent from the tunable's comment] ----
        // HarvestEggs.Run casts GetWeightedIndex(kEggQualityWeights) (0..10) straight to Quality, which starts at
        // Horrifying = 1 (Perfect = 11); the 11 weights are documented Horrifying..Perfect and sibling code (nectar,
        // beekeeping, plants) uses index + 1. Every egg is one tier low and Perfect never happens (shipped peak 8 lands
        // on Very Nice, not Great). Fix: GetWeightedIndex returns index + 1 when called with the coop's own array
        // (reference compare); every other caller gets EA's result. Run yields, so the shared helper is the hook point.
        static float[] sCeWeights;
        internal static int CeIndex(bool on, bool coopArray, int idx, int len) { return on && coopArray && idx >= 0 && idx < len ? idx + 1 : idx; }

        static void ApplyCoopEggQuality()
        {
            FieldInfo f = typeof(ChickenCoopClassic).GetField("kEggQualityWeights", kEpStatic);
            if (f != null) sCeWeights = f.GetValue(null) as float[];
            if (sCeWeights == null) { AppliedLog().Add("coopeggquality: not found: ChickenCoopClassic.kEggQualityWeights"); return; }
            Patch(typeof(RandomUtil), "GetWeightedIndex", new Type[] { typeof(float[]) }, typeof(BSt1CeHook), "IndexHook", "IndexOrig");
        }

        public class BSt1CeHook
        {
            public static int IndexHook(float[] randomList)
            {
                int idx = IndexOrig(randomList);
                if (randomList == null || randomList != sCeWeights) return idx;
                try
                {
                    if (!ShortcutOn("coopeggquality")) { PatchCounters()[1658]++; return idx; }
                    PatchCounters()[1657]++;
                    return CeIndex(true, true, idx, randomList.Length);
                }
                catch (Exception) { PatchCounters()[1659]++; return idx; }
            }
            public static int IndexOrig(float[] randomList) { return 0; } // RandomUtil.GetWeightedIndex
        }
    }
}
