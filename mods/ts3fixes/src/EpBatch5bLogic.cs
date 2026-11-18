using System;
using System.Collections;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Decorations;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.PerformanceObjects;
using Sims3.Gameplay.Objects.TombObjects;
using Sims3.Gameplay.Rewards;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.TuningValues;
using Sims3.Gameplay.Utilities;
using Sims3.Gameplay.Controllers;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;
using Sims3.SimIFace.Enums;

namespace Ts3Fixes
{
    // EP batch 5b (docs/587 §11.2): tranche 4 (120 scanner-ranked EP types, 4 read-only agents; usage rank exhausted).
    // Every switch off by default; all household-visible. Checks: EpBatch5Checks.cs. Three counter slots each:
    // fixed / seen with the switch off / errors. Slots 3301-3324 (fixes-1500) and 1600-1608 (fixes-1600).
    //   hayscattertime        [3301] HayBale.OnScattered: scatter time never reset
    //   hauntedgnomeinv       [3304] HauntedGnome.OnLoad / SpawnGhost: ghosts and fear from a gnome in an inventory
    //   houdinignomeleak      [3307] MagicGnomeHoudini.SpawnHoudini(pos, fwd): orphan gnome on a failed placement
    //   showstagefxend        [3310] FXObject / Ep6EffectsMachine.ProcessEvent: show end dropped during a one-shot
    //   soakedpetselfreact    [3313] BuffSoakedPet+ShakeWaterOff.Run: the shaking pet reacts to itself
    //   tagbagsurfaceleak     [3316] TagBag+PrepSpace.PlaceArt: surface leaked when no lot is found
    //   equestriantrophyname  [3319] EquestrianTrophy.ImportContent: horse name read from the rider key
    //   rubbleuselist         [3322] RubblePile+Clear.Cleanup: base cleanup skipped, Sim left on the use list
    //   backpacklotleak       [3325] Backpack.Dispose / OnLotChanged: static lot-change handler left on a dead backpack
    //   streetartmasterboost  [3328] StreetArtSkill.GetMuralData: Avant-Garde boost on row 3, not the Master row
    //   bubblingspringsreset  [3331] BubblingSprings.DoReset: hot-spring broadcaster never recreated
    public static partial class Fixes
    {
        static void ApplyEpBatch5b()
        {
            ApplyHayScatterTime(); ApplyHauntedGnomeInv(); ApplyHoudiniGnomeLeak(); ApplyShowStageFxEnd();
            ApplySoakedPetSelfReact(); ApplyTagBagSurfaceLeak(); ApplyEquestrianTrophyName(); ApplyRubbleUseList();
            ApplyBackpackLotLeak(); ApplyStreetArtMasterBoost(); ApplyBubblingSpringsReset();
        }

        // ---- hayscattertime (docs/587 §11.2.1) [verified, decompiled] ----
        // ScatterHay.LoopDelegate adds to the bale's mScatterTime and Run calls OnScattered once it passes TimeToScatter,
        // but nothing resets it. HayStack (3 piles) and HayBaleStacked (never consumed) then finish every later scatter on
        // the first loop tick, even a scatter cancelled at once. Fix: OnScattered (no yield; HayStack calls base) zeroes
        // mScatterTime after EA's body. A partly done, cancelled scatter still keeps its progress, as EA meant.
        static FieldInfo sHsTime;
        internal static float HsAfter(bool on, float t) { return on ? 0f : t; }

        static void ApplyHayScatterTime()
        {
            sHsTime = typeof(HayBale).GetField("mScatterTime", kInstAll);
            if (sHsTime == null) { AppliedLog().Add("hayscattertime: not found: HayBale.mScatterTime"); return; }
            Patch(typeof(HayBale), "OnScattered", new Type[] { typeof(Sim) }, typeof(BEp5HsHook), "ScatterHook", "ScatterOrig");
        }

        public class BEp5HsHook
        {
            public void ScatterHook(Sim actor)
            {
                ScatterOrig(actor);
                try
                {
                    float t = (float)sHsTime.GetValue(this);
                    if (t <= 0f) return;   // nothing to reset, equal in both states
                    bool on = ShortcutOn("hayscattertime");
                    float after = HsAfter(on, t);
                    if (after != t) { sHsTime.SetValue(this, after); PatchCounters()[3301]++; }
                    else if (!on) PatchCounters()[3302]++;
                }
                catch (Exception) { PatchCounters()[3303]++; }
            }
            public void ScatterOrig(Sim actor) { } // HayBale.OnScattered
        }

        // ---- hauntedgnomeinv (docs/587 §11.2.2) [verified code; reach: inventory + save/load] ----
        // OnLoad (and OnStartup, DoReset) always create the ghost alarm and the coward-fear broadcaster; only the
        // add-to-inventory event removes them, and it does not fire on load. A haunted gnome loaded in an inventory then
        // spawns a ghost at the inventory owner's spot every idle period and scares cowards near its carrier.
        // Fix: after OnLoad and before SpawnGhost, a gnome in an inventory gets EA's own add-to-inventory cleanup
        // (alarm removed, broadcaster disposed) and SpawnGhost is skipped. RE-found extra: that cleanup leaves the
        // removed handle in mGhostAlarm; it is set to kInvalidHandle.
        static MethodInfo sHgAdd;
        static FieldInfo sHgAlarm;
        internal static bool HgClean(bool on, bool inInventory) { return on && inInventory; }

        static void ApplyHauntedGnomeInv()
        {
            sHgAdd = typeof(HauntedGnome).GetMethod("HauntedAddToInventoryEvent", kInstAll);
            sHgAlarm = typeof(HauntedGnome).GetField("mGhostAlarm", kInstAll);
            if (sHgAdd == null || sHgAlarm == null) { AppliedLog().Add("hauntedgnomeinv: not found: " + EpMiss("HauntedAddToInventoryEvent", sHgAdd, "mGhostAlarm", sHgAlarm)); return; }
            Patch(typeof(HauntedGnome), "OnLoad", Type.EmptyTypes, typeof(BEp5HgHook), "LoadHook", "LoadOrig");
            Patch(typeof(HauntedGnome), "SpawnGhost", Type.EmptyTypes, typeof(BEp5HgHook), "SpawnHook", "SpawnOrig");
        }

        // True when the gnome was cleaned (in an inventory, switch on).
        static bool HgCleanUp(HauntedGnome g)
        {
            if (g == null) return false;
            bool on = ShortcutOn("hauntedgnomeinv");
            bool inInventory = g.InInventory;
            if (!HgClean(on, inInventory))
            {
                if (!on && inInventory) PatchCounters()[3305]++;
                return false;
            }
            sHgAdd.Invoke(g, new object[] { g, null });
            sHgAlarm.SetValue(g, AlarmHandle.kInvalidHandle);
            PatchCounters()[3304]++;
            return true;
        }

        public class BEp5HgHook
        {
            public void LoadHook()
            {
                LoadOrig();
                try { HgCleanUp((HauntedGnome)(object)this); }
                catch (Exception) { PatchCounters()[3306]++; }
            }
            public void LoadOrig() { } // HauntedGnome.OnLoad

            public void SpawnHook()
            {
                bool cleaned = false;
                try { cleaned = HgCleanUp((HauntedGnome)(object)this); }
                catch (Exception) { PatchCounters()[3306]++; }
                if (!cleaned) SpawnOrig();
            }
            public void SpawnOrig() { } // HauntedGnome.SpawnGhost
        }

        // ---- houdinignomeleak (docs/587 §11.2.3) [verified, decompiled] ----
        // SpawnHoudini(pos, fwd) creates the gnome out of world and, when FindGoodLocationNearby fails, falls back to
        // SpawnHoudini() which creates a second one on the home lot; the first is never destroyed. A failed create (null
        // or a FailureObject) threw in FindGoodLocationNearby. Fix: copy (static, no yield): a placed gnome as EA; else
        // the orphan is destroyed and EA's fallback runs, skipped when the active Sim has no home lot (RE-found extra:
        // EA's fallback reads Sim.ActiveActor.LotHome unchecked).
        internal static bool HgOrphanDestroy(bool on, bool created, bool placed) { return on && created && !placed; }

        static void ApplyHoudiniGnomeLeak()
        {
            Patch(typeof(MagicGnomeHoudini), "SpawnHoudini", new Type[] { typeof(Vector3), typeof(Vector3) }, typeof(BEp5HoHook), "SpawnHook", "SpawnOrig");
        }

        public class BEp5HoHook
        {
            public static void SpawnHook(Vector3 objectPosition, Vector3 objectForwardVector)
            {
                // The copy below replaces EA's whole body, so it runs only with the switch on (off: EA's body as is; the
                // orphan only exists after EA's create, so it cannot be counted here with the switch off).
                bool on = ShortcutOn("houdinignomeleak");
                if (!on) { SpawnOrig(objectPosition, objectForwardVector); return; }
                try
                {
                    IGameObject created = GlobalFunctions.CreateObjectOutOfWorld("magicGnomeMagician", ProductVersion.EP6);
                    MagicGnomeHoudini gnome = created as MagicGnomeHoudini;
                    bool placed = gnome != null && GlobalFunctions.FindGoodLocationNearby(gnome, ref objectPosition, ref objectForwardVector);
                    if (placed)
                    {
                        gnome.SetPosition(objectPosition);
                        gnome.SetForward(objectForwardVector);
                        gnome.AddToWorld();
                        return;
                    }
                    if (HgOrphanDestroy(on, created != null, placed)) { created.Destroy(); PatchCounters()[3307]++; }  // changed
                    Sim active = Sim.ActiveActor;
                    Lot lotHome = active == null ? null : active.LotHome;  // changed
                    if (lotHome == null) { PatchCounters()[3308]++; return; }
                    RandomObjectPlacementParams ropParams = new RandomObjectPlacementParams(true, true);
                    GlobalFunctions.CreateObjectOnLot("magicGnomeMagician", ProductVersion.EP6, null, lotHome, ropParams, true);
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3309]++; }
            }
            public static void SpawnOrig(Vector3 objectPosition, Vector3 objectForwardVector) { } // MagicGnomeHoudini.SpawnHoudini
        }

        // ---- showstagefxend (docs/587 §11.2.4) [verified code; in-game symptom inferred] ----
        // FXObject.ProcessEvent ignores every show event while a one-shot is running (mOneShot, 60 s). A kShowEnd posted
        // then is lost: mIsOn stays true and the persisted EndOneShot alarm restarts the repeating sequence after the
        // show, so stage lights and FX towers stay on. Ep6EffectsMachine has the same `if (mOneShot) return;` and its
        // ReturnSaveState alarm calls TurnOn() even after the machine was turned off or reset. Fix: at kShowEnd during
        // a one-shot, the saved state is restored and the one-shot ended before EA's kShowEnd runs; the EP6 alarm only
        // turns the machine back on while its one-shot is still running. RE-found extras: (1) EP6 failure pick
        // `switch (RandomUtil.GetInt(1))` (0 or 1) has cases 1 and 2, so black smoke never shows and half the failures
        // show nothing; now 0 -> dud fireworks, 1 -> black smoke. (2) FXObject.StopEffects disposes only started
        // effects; StartSequence then overwrites the array, dropping stopped ones (UpdateSequence stops them); now every
        // non-null effect is disposed (Stop only when started, as EA).
        static FieldInfo sE6OneShot, sE6SavedEffect, sE6SavedDelay, sE6Effect, sE6Delay;
        internal static bool FxEndOneShot(bool on, bool isShowEnd, bool oneShot) { return on && isShowEnd && oneShot; }
        internal static int E6FailCase(bool on, int roll) { return on ? roll + 1 : roll; }  // EA's cases are 1 and 2
        // EP6: whether the hook fires the failure effect itself (EA: a roll that hits no case); one-shots stay EA's early return.
        internal static bool E6OwnFailure(bool on, bool isFailure, bool oneShot) { return on && isFailure && !oneShot; }
        // EP6 ReturnSaveState: whether the stale TurnOn (alarm fired after the one-shot already ended) is skipped.
        internal static bool E6SkipTurnOn(bool on, bool oneShot) { return on && !oneShot; }

        static void ApplyShowStageFxEnd()
        {
            Patch(typeof(FXObject), "ProcessEvent", new Type[] { typeof(ShowEventType) }, typeof(BEp5FxHook), "EventHook", "EventOrig");
            Patch(typeof(FXObject), "StopEffects", Type.EmptyTypes, typeof(BEp5FxHook), "StopHook", "StopOrig");
            sE6OneShot = typeof(Ep6EffectsMachine).GetField("mOneShot", kInstAll);
            sE6SavedEffect = typeof(Ep6EffectsMachine).GetField("mSavedEffect", kInstAll);
            sE6SavedDelay = typeof(Ep6EffectsMachine).GetField("mSavedEffectDelay", kInstAll);
            sE6Effect = typeof(EffectsMachineBase).GetField("mEffect", kInstAll);
            sE6Delay = typeof(EffectsMachineBase).GetField("mDelay", kInstAll);
            string miss = EpMiss("mOneShot", sE6OneShot, "mSavedEffect", sE6SavedEffect, "mSavedEffectDelay", sE6SavedDelay, "mEffect", sE6Effect, "mDelay", sE6Delay);
            if (miss.Length > 0) { AppliedLog().Add("showstagefxend: not found: Ep6EffectsMachine " + miss); return; }
            Patch(typeof(Ep6EffectsMachine), "ProcessEvent", new Type[] { typeof(ShowEventType) }, typeof(BEp5E6Hook), "EventHook", "EventOrig");
            Patch(typeof(Ep6EffectsMachine), "ReturnSaveState", Type.EmptyTypes, typeof(BEp5E6Hook), "ReturnHook", "ReturnOrig");
        }

        public abstract class BEp5FxHook : FXObject
        {
            public void EventHook(ShowEventType eventType)
            {
                try
                {
                    bool on = ShortcutOn("showstagefxend");
                    bool isEnd = eventType == ShowEventType.kShowEnd, oneShot = mOneShot;
                    if (FxEndOneShot(on, isEnd, oneShot))
                    {
                        ReturnSaveState();
                        mOneShot = false;
                        PatchCounters()[3310]++;
                    }
                    else if (!on && FxEndOneShot(true, isEnd, oneShot)) PatchCounters()[3311]++;
                }
                catch (Exception) { PatchCounters()[3312]++; }
                EventOrig(eventType);
            }
            public void EventOrig(ShowEventType eventType) { } // FXObject.ProcessEvent

            public void StopHook()
            {
                // A copy of EA's whole StopEffects: runs only with the switch on (off: EA's body as is).
                if (!ShortcutOn("showstagefxend")) { StopOrig(); return; }
                if (mSequenceAlarm != AlarmHandle.kInvalidHandle)
                {
                    RemoveAlarm(mSequenceAlarm);
                    mSequenceAlarm = AlarmHandle.kInvalidHandle;
                }
                if (mFxJoint == null) return;  // changed
                for (int i = 0; i < mFxJoint.Length; i++)
                {
                    VisualEffect fx = mFxJoint[i];
                    if (fx == null) continue;
                    try
                    {
                        if (fx.HasEffectStarted()) fx.Stop();
                        else PatchCounters()[3310]++;  // changed: a stopped effect EA would have dropped
                        fx.Dispose();
                    }
                    catch (Exception) { PatchCounters()[3312]++; }
                    mFxJoint[i] = null;
                }
            }
            public void StopOrig() { } // FXObject.StopEffects
        }

        public abstract class BEp5E6Hook : Ep6EffectsMachine
        {
            public void EventHook(ShowEventType eventType)
            {
                bool on = ShortcutOn("showstagefxend");
                try
                {
                    bool oneShot = (bool)sE6OneShot.GetValue(this);
                    bool isEnd = eventType == ShowEventType.kShowEnd;
                    if (FxEndOneShot(on, isEnd, oneShot))
                    {
                        sE6Effect.SetValue(this, sE6SavedEffect.GetValue(this));
                        sE6Delay.SetValue(this, sE6SavedDelay.GetValue(this));
                        sE6OneShot.SetValue(this, false);
                        PatchCounters()[3310]++;
                    }
                    else if (!on && FxEndOneShot(true, isEnd, oneShot)) PatchCounters()[3311]++;
                    else if (E6OwnFailure(on, eventType == ShowEventType.kShowEffectFailure, oneShot))
                    {
                        FireStageEvent(E6FailCase(on, RandomUtil.GetInt(1)) == 1 ? Effect.Fail_DudFireworks : Effect.Fail_BlackSmokePuffs);
                        PatchCounters()[3310]++;
                        return;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3312]++; }
                EventOrig(eventType);
            }
            public void EventOrig(ShowEventType eventType) { } // Ep6EffectsMachine.ProcessEvent

            public void ReturnHook()
            {
                try
                {
                    bool on = ShortcutOn("showstagefxend");
                    bool oneShot = (bool)sE6OneShot.GetValue(this);
                    if (E6SkipTurnOn(on, oneShot)) { PatchCounters()[3310]++; return; }
                    if (!on && E6SkipTurnOn(true, oneShot)) PatchCounters()[3311]++;
                }
                catch (Exception) { PatchCounters()[3312]++; }
                ReturnOrig();
            }
            public void ReturnOrig() { } // Ep6EffectsMachine.ReturnSaveState
        }

        // ---- soakedpetselfreact (docs/587 §11.2.5) [verified code; in-game effect inferred] ----
        // ShakeWaterOff.Run queues ReactToShakeWaterOff (High priority) on every Sim from Queries.GetObjects within
        // kReactionRadius, which includes the shaking pet itself (other EA sites remove the actor: BaristaBar, Telescope,
        // WaterTrough). The pet then turns to face itself and may re-soak itself (kChanceToAddSoakedBuff ships 0.25).
        // Fix: copy of Run (yields through AnimateSim; docs/242 copy-safety) that skips the actor.
        static Type sSoType;
        static FieldInfo sSoPuddle, sSoRadius;
        internal static bool SpQueue(bool on, bool isActor) { return !(on && isActor); }

        static void ApplySoakedPetSelfReact()
        {
            sSoType = typeof(BuffSoakedPet).GetNestedType("ShakeWaterOff", BindingFlags.Public | BindingFlags.NonPublic);
            if (sSoType != null) { sSoPuddle = sSoType.GetField("AddPuddle", kInstAll); sSoRadius = sSoType.GetField("kReactionRadius", kEpStatic); }
            if (sSoType == null || sSoPuddle == null || sSoRadius == null) { AppliedLog().Add("soakedpetselfreact: not found: BuffSoakedPet+ShakeWaterOff " + EpMiss("AddPuddle", sSoPuddle, "kReactionRadius", sSoRadius)); return; }
            Patch(sSoType, "Run", Type.EmptyTypes, typeof(BEp5SpHook), "RunHook", "RunOrig");
        }

        public abstract class BEp5SpHook : Interaction<Sim, Sim>
        {
            public bool RunHook()
            {
                // A copy of EA's whole Run: runs only with the switch on (off: EA's body as is, counted per shake).
                bool on = ShortcutOn("soakedpetselfreact");
                if (!on) { PatchCounters()[3314]++; return RunOrig(); }
                Lot lotCurrent = Actor.LotCurrent;
                Sim[] objects = Sims3.Gameplay.Queries.GetObjects<Sim>(Actor.Position, (float)sSoRadius.GetValue(null));
                foreach (Sim sim in objects)
                {
                    if (!SpQueue(on, sim == Actor)) { PatchCounters()[3313]++; continue; }  // changed
                    if ((lotCurrent == sim.LotCurrent || Actor.RoomId == 0) && Actor.RoomId == sim.RoomId && Actor.Level == sim.Level)
                    {
                        InteractionInstance instance = BuffSoakedPet.ReactToShakeWaterOff.Singleton.CreateInstance(Actor, sim, new InteractionPriority(InteractionPriorityLevel.High), true, true);
                        sim.InteractionQueue.AddAfterCheckingForDuplicates(instance);
                    }
                }
                if ((bool)sSoPuddle.GetValue(this))
                {
                    PuddleManager.AddPuddle(Actor.Position);
                }
                EnterStateMachine("ShakeOffWater", "Enter", "x");
                AnimateSim("Shake Off");
                AnimateSim("Exit");
                return true;
            }
            public bool RunOrig() { return false; } // BuffSoakedPet+ShakeWaterOff.Run
        }

        // ---- tagbagsurfaceleak (docs/587 §11.2.6) [verified, decompiled] ----
        // The three Prep interactions create a GraffitiSurface out of world and call PlaceArt; when FindTargetLot returns
        // null PlaceArt returns false without destroying it (the hand-tool failure branch does). Fix: after PlaceArt
        // (it yields; a wrapper keeps EA's body), a false result with the surface still out of world destroys it.
        internal static bool TbDestroy(bool on, bool placed, bool destroyed, bool inWorld) { return on && !placed && !destroyed && !inWorld; }

        static void ApplyTagBagSurfaceLeak()
        {
            if (!Patch(typeof(TagBag).GetNestedType("PrepSpace", BindingFlags.Public | BindingFlags.NonPublic), "PlaceArt", new Type[] { typeof(GraffitiSurface) }, typeof(BEp5TbHook), "PlaceHook", "PlaceOrig"))
                AppliedLog().Add("tagbagsurfaceleak: not patched: TagBag+PrepSpace.PlaceArt");
        }

        public class BEp5TbHook
        {
            public bool PlaceHook(GraffitiSurface surface)
            {
                bool placed = false;
                try { placed = PlaceOrig(surface); return placed; }
                finally
                {
                    // review: also when a reset unwinds PlaceArt (the surface is still out of world then)
                    try
                    {
                        if (!placed && surface != null)
                        {
                            bool on = ShortcutOn("tagbagsurfaceleak");
                            bool destroyed = surface.HasBeenDestroyed, inWorld = !destroyed && surface.InWorld;
                            if (TbDestroy(on, placed, destroyed, inWorld)) { surface.Destroy(); PatchCounters()[3316]++; }
                            else if (!on && TbDestroy(true, placed, destroyed, inWorld)) PatchCounters()[3317]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[3318]++; }
                }
            }
            public bool PlaceOrig(GraffitiSurface surface) { return false; } // TagBag+PrepSpace.PlaceArt
        }

        // ---- equestriantrophyname (docs/587 §11.2.7) [verified, decompiled] ----
        // ExportContent writes the horse name under kHorseName (183657686) but ImportContent reads mHorseName from
        // kRiderName (183657670), so after an export/import (travel, moving, library) the trophy's View notice shows
        // the rider's name as the horse's. Fix: after EA's import, mHorseName is read from its own key.
        const uint kEtHorseKey = 183657686u;
        static FieldInfo sEtHorse;
        // The horse name the trophy keeps: on, the value under its own key when present; off, EA's (read from the rider key).
        internal static string EtName(bool on, string ea, bool hasHorseKey, string horse) { return on && hasHorseKey ? horse : ea; }

        static void ApplyEquestrianTrophyName()
        {
            sEtHorse = typeof(EquestrianTrophy).GetField("mHorseName", kInstAll);
            if (sEtHorse == null) { AppliedLog().Add("equestriantrophyname: not found: EquestrianTrophy.mHorseName"); return; }
            Patch(typeof(EquestrianTrophy), "ImportContent", new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamReader) }, typeof(BEp5EtHook), "ImportHook", "ImportOrig");
        }

        public class BEp5EtHook
        {
            public bool ImportHook(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader)
            {
                bool r = ImportOrig(resKeyTable, objIdTable, reader);
                try
                {
                    if (reader == null) return r;
                    bool on = ShortcutOn("equestriantrophyname");
                    string ea = (string)sEtHorse.GetValue(this), horse;
                    bool has = reader.ReadString(kEtHorseKey, out horse, "");
                    string name = EtName(on, ea, has, horse);
                    if (name != ea) { sEtHorse.SetValue(this, name); PatchCounters()[3319]++; }
                    else if (!on && EtName(true, ea, has, horse) != ea) PatchCounters()[3320]++;
                }
                catch (Exception) { PatchCounters()[3321]++; }
                return r;
            }
            public bool ImportOrig(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader) { return false; } // EquestrianTrophy.ImportContent
        }

        // ---- rubbleuselist (docs/587 §11.2.8) [verified code; reach: a reset or exception mid-dig] ----
        // RubblePile+Clear overrides Cleanup without calling base.Cleanup, the same defect chestuselist fixes for
        // TreasureChest+OpenWithAnimation (docs/575 §5): when Clear is cut between StandardEntry and StandardExit the
        // Sim stays on the pile's use list. Fix: after Clear.Cleanup, the Sim is taken off when still on it (CuRemove).
        static void ApplyRubbleUseList()
        {
            Patch(typeof(RubblePile).GetNestedType("Clear", BindingFlags.Public | BindingFlags.NonPublic), "Cleanup", Type.EmptyTypes, typeof(BEp5RuHook), "CleanupHook", "CleanupOrig");
        }

        public class BEp5RuHook
        {
            public void CleanupHook()
            {
                CleanupOrig();
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    Sim actor = ii.InstanceActor;
                    GameObject pile = ii.Target as GameObject;
                    if (actor == null || pile == null || pile.HasBeenDestroyed) return;
                    bool on = ShortcutOn("rubbleuselist");
                    bool still = pile.IsActorUsingMe(actor);
                    if (CuRemove(on, still)) { pile.RemoveFromUseList(actor); PatchCounters()[3322]++; }
                    else if (!on && still) PatchCounters()[3323]++;
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3324]++; }
            }
            public void CleanupOrig() { } // RubblePile+Clear.Cleanup
        }

        // ---- backpacklotleak (docs/587 §11.2.9) [verified code; reach inferred] ----
        // The UsingBackpack setter adds OnLotChanged to the static Sim.OnLotChangedEvent; Dispose removes the car
        // handler and the inventory handlers but not that one, so a backpack destroyed while in use stays subscribed and
        // OnLotChanged then reads ItemComp.InventoryParent.Owner of a dead object on every Sim's lot change; the throw
        // aborts the other handlers and the rest of the lot change. Fix: Dispose first drops the handler when in use;
        // OnLotChanged with no inventory parent drops itself and returns.
        static MethodInfo sBpOnLot;
        internal static bool BpDropOnDispose(bool on, bool usingIt) { return on && usingIt; }
        internal static bool BpDropOnLot(bool on, bool hasParent) { return on && !hasParent; }

        static void ApplyBackpackLotLeak()
        {
            sBpOnLot = typeof(Backpack).GetMethod("OnLotChanged", kInstAll, null, new Type[] { typeof(Sim), typeof(Lot), typeof(Lot) }, null);
            if (sBpOnLot == null) { AppliedLog().Add("backpacklotleak: not found: Backpack.OnLotChanged"); return; }
            Patch(typeof(Backpack), "Dispose", Type.EmptyTypes, typeof(BEp5BpHook), "DisposeHook", "DisposeOrig");
            Patch(typeof(Backpack), "OnLotChanged", new Type[] { typeof(Sim), typeof(Lot), typeof(Lot) }, typeof(BEp5BpHook), "LotHook", "LotOrig");
        }

        static void BpUnsubscribe(Backpack b)
        {
            Sim.OnLotChangedEvent -= (Sim.OnLotChanged)Delegate.CreateDelegate(typeof(Sim.OnLotChanged), b, sBpOnLot);
        }

        public class BEp5BpHook
        {
            public void DisposeHook()
            {
                try
                {
                    Backpack b = (Backpack)(object)this;
                    bool on = ShortcutOn("backpacklotleak");
                    bool usingIt = b.UsingBackpack;
                    if (BpDropOnDispose(on, usingIt)) { BpUnsubscribe(b); PatchCounters()[3325]++; }
                    else if (!on && usingIt) PatchCounters()[3326]++;
                }
                catch (Exception) { PatchCounters()[3327]++; }
                DisposeOrig();
            }
            public void DisposeOrig() { } // Backpack.Dispose

            public void LotHook(Sim sim, Lot oldLot, Lot newLot)
            {
                bool on = ShortcutOn("backpacklotleak");
                try
                {
                    Backpack b = (Backpack)(object)this;
                    bool hasParent = b.ItemComp != null && b.ItemComp.InventoryParent != null;
                    if (BpDropOnLot(on, hasParent)) { BpUnsubscribe(b); PatchCounters()[3325]++; return; }
                    if (!on && !hasParent) PatchCounters()[3326]++;
                }
                catch (Exception)
                {
                    PatchCounters()[3327]++;
                    if (on) return;   // review: with the switch off EA's handler always runs (it used to be skipped here)
                }
                LotOrig(sim, oldLot, newLot);
            }
            public void LotOrig(Sim sim, Lot oldLot, Lot newLot) { } // Backpack.OnLotChanged
        }

        // ---- streetartmasterboost (docs/587 §11.2.10) [verified; intent from the tuning name] ----
        // GetMuralData filters StreetArtData to the rows with variations at the size, then adds the Avant-Garde
        // masterpiece boost (TraitTuning, ships 5) at position i == 3. Shipped data: large wall and ground murals have no
        // Low row, so Master sits at index 2 and gets no boost (Master weights are 0 below level 9, so the boost is the
        // only way there). Fix: for an Avant-Garde owner whose Master row is not at 3, a copy (no yield) boosts the row
        // whose quality is "Master". Every other call runs EA's body.
        static Type sSaData;
        static FieldInfo sSaList, sSaType, sSaSmall, sSaMed, sSaLarge, sSaWeights, sSaQuality;
        static MethodInfo sSaDuration, sSaVarString;

        // Pure: the index that gets the boost (-1: none). Off: EA's position 3.
        internal static int SaBoostIndex(bool on, string[] qualities)
        {
            if (!on) return qualities.Length > 3 ? 3 : -1;
            for (int i = 0; i < qualities.Length; i++) if (qualities[i] == "Master") return i;
            return -1;
        }

        static void ApplyStreetArtMasterBoost()
        {
            const BindingFlags n = BindingFlags.Public | BindingFlags.NonPublic;
            sSaData = typeof(StreetArtSkill).GetNestedType("StreetArtData", n);
            if (sSaData != null)
            {
                sSaList = sSaData.GetField("sStreetArtData", kEpStatic);
                sSaType = sSaData.GetField("mType", kInstAll);
                sSaSmall = sSaData.GetField("mSmallVariations", kInstAll);
                sSaMed = sSaData.GetField("mMediumVariations", kInstAll);
                sSaLarge = sSaData.GetField("mLargeVariations", kInstAll);
                sSaWeights = sSaData.GetField("mWeights", kInstAll);
                sSaQuality = sSaData.GetField("mQuality", kInstAll);
                sSaDuration = sSaData.GetMethod("GetDuration", kInstAll, null, new Type[] { typeof(int) }, null);
                sSaVarString = typeof(StreetArtSkill).GetMethod("GetStreetArtVariationString", kInstAll, null, new Type[] { sSaData, typeof(int), typeof(int) }, null);
            }
            string miss = EpMiss("StreetArtData", sSaData, "sStreetArtData", sSaList, "mType", sSaType, "mSmallVariations", sSaSmall, "mMediumVariations", sSaMed,
                "mLargeVariations", sSaLarge, "mWeights", sSaWeights, "mQuality", sSaQuality, "GetDuration", sSaDuration, "GetStreetArtVariationString", sSaVarString);
            if (miss.Length > 0) { AppliedLog().Add("streetartmasterboost: not found: " + miss); return; }
            Patch(typeof(StreetArtSkill), "GetMuralData", new Type[] { typeof(StreetArtSkill.Type), typeof(int), typeof(string).MakeByRefType(), typeof(string).MakeByRefType(),
                typeof(float).MakeByRefType(), typeof(bool).MakeByRefType() }, typeof(BEp5SaHook), "MuralHook", "MuralOrig");
        }

        static int SaVariations(object row, int size)
        {
            switch (size)
            {
                case 0: return (int)sSaSmall.GetValue(row);
                case 1: return (int)sSaMed.GetValue(row);
                case 2: return (int)sSaLarge.GetValue(row);
            }
            return 0;
        }

        public class BEp5SaHook
        {
            public void MuralHook(StreetArtSkill.Type streetArtType, int size, out string quality, out string variation, out float duration, out bool isMasterpeice)
            {
                StreetArtSkill skill = (StreetArtSkill)(object)this;
                try
                {
                    Sim owner = skill.SkillOwner == null ? null : skill.SkillOwner.CreatedSim;
                    if (owner != null && owner.HasTrait(TraitNames.AvantGarde))
                    {
                        System.Collections.Generic.List<object> rows = new System.Collections.Generic.List<object>();
                        foreach (object row in (IList)sSaList.GetValue(null))
                            if ((StreetArtSkill.Type)sSaType.GetValue(row) == streetArtType && SaVariations(row, size) > 0) rows.Add(row);
                        string[] q = new string[rows.Count];
                        for (int i = 0; i < q.Length; i++) q[i] = (string)sSaQuality.GetValue(rows[i]);
                        bool on = ShortcutOn("streetartmasterboost");
                        int ea = SaBoostIndex(false, q), boostIdx = SaBoostIndex(on, q);
                        if (rows.Count > 0 && boostIdx != ea)
                        {
                            float[] weights = new float[rows.Count];
                            for (int i = 0; i < weights.Length; i++)
                            {
                                weights[i] = ((int[])sSaWeights.GetValue(rows[i]))[skill.SkillLevel];
                                if (i == boostIdx) weights[i] += TraitTuning.BoostCreatingStreetArtMasterpieceWithAvantGardeTrait;  // changed
                            }
                            int picked = RandomUtil.GetWeightedIndex(weights);
                            quality = q[picked];
                            variation = (string)sSaVarString.Invoke(skill, new object[] { rows[picked], picked, size });
                            duration = (float)sSaDuration.Invoke(rows[picked], new object[] { size }) * 60f;
                            isMasterpeice = quality == "Master";
                            PatchCounters()[3328]++;
                            return;
                        }
                        if (!on && rows.Count > 0 && SaBoostIndex(true, q) != ea) PatchCounters()[3329]++;
                    }
                }
                catch (Exception) { PatchCounters()[3330]++; }
                MuralOrig(streetArtType, size, out quality, out variation, out duration, out isMasterpeice);
            }
            public void MuralOrig(StreetArtSkill.Type streetArtType, int size, out string quality, out string variation, out float duration, out bool isMasterpeice)
            { quality = null; variation = null; duration = 0f; isMasterpeice = false; } // StreetArtSkill.GetMuralData
        }

        // ---- bubblingspringsreset (docs/587 §11.2.11) [verified code; reach: an object reset] ----
        // DoReset disposes the Nature's Hot Tub broadcaster (ResetBroadcaster) and never recreates it; only OnStartup
        // does. After a reset that spring never gives the buff until the lot reloads. Fix: after DoReset, EA's own
        // SetBroadcaster (creates only when null). EA's DoReset also skips base.DoReset; left as EA (the reset system
        // runs the object reset steps it needs, and adding a base call changes more than the bug).
        static MethodInfo sBsSet;
        static FieldInfo sBsCast;
        internal static bool BsRecreate(bool on, bool missing) { return on && missing; }

        static void ApplyBubblingSpringsReset()
        {
            sBsSet = typeof(BubblingSprings).GetMethod("SetBroadcaster", kInstAll, null, Type.EmptyTypes, null);
            sBsCast = typeof(BubblingSprings).GetField("mBubblingSpringsBroadcaster", kInstAll);
            if (sBsSet == null || sBsCast == null) { AppliedLog().Add("bubblingspringsreset: not found: " + EpMiss("SetBroadcaster", sBsSet, "mBubblingSpringsBroadcaster", sBsCast)); return; }
            Type ri = typeof(GameObject).GetNestedType("ResetInformation", BindingFlags.Public | BindingFlags.NonPublic);
            if (ri == null) { AppliedLog().Add("bubblingspringsreset: not found: GameObject+ResetInformation"); return; }
            Patch(typeof(BubblingSprings), "DoReset", new Type[] { ri }, typeof(BEp5BsHook), "ResetHook", "ResetOrig");
        }

        public class BEp5BsHook
        {
            public void ResetHook(object resetInformation)
            {
                ResetOrig(resetInformation);
                try
                {
                    bool on = ShortcutOn("bubblingspringsreset");
                    bool missing = sBsCast.GetValue(this) == null;
                    if (BsRecreate(on, missing)) { sBsSet.Invoke(this, null); PatchCounters()[3331]++; }
                    else if (!on && missing) PatchCounters()[3332]++;
                }
                catch (Exception) { PatchCounters()[3333]++; }
            }
            public void ResetOrig(object resetInformation) { } // BubblingSprings.DoReset
        }
    }
}
