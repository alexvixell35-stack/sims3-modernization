using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Object use and ownership left behind after a reset, a rejection or a Sim that is gone (docs/575 §2-4), each off by
    // default. EA bug fixes, every Sim; no Sim is moved, no object is created or destroyed.
    //
    // handsetreset (kHandsetReset, docs/575 §2) [verified: script + native]: a landline (wall or table phone) shows its
    //   handset through the VisualState geometry state (SetGeometryState "baseOnly"/"handsetOnBase" ->
    //   IC_World_ObjectSetGeometryVisibilityState 0x78CB70 -> 0x95FEF0 writes the FNV hash into VisualState +0x14). The
    //   handset is hidden and shown only by animation events (HideHandsetInPhoneModel / ShowHandsetInPhoneModel). When
    //   the call is cut short (Sim reset, cancel during the call), Cleanup calls ReplaceHandset(actor, null): no exit
    //   animation, so HandsetPickedUp goes false but the geometry stays "baseOnly", and the VisualState save slot
    //   (0x95F220, vtable 0x1044AB4 +0xC) writes +0x14 into the save, the load slot (0x95F290, +0x8) reads it back, so the
    //   phone shows no handset for good. Fix: after ReplaceHandset with no state machine, the phone's own
    //   SetHandsetState(false); at world load, every idle landline (no handset flag, nobody using it) gets the same call
    //   (0x95FEF0 changes nothing when the state is already right). Counters [2679] handsets put back, [2680] errors.
    // flowersinuse (kFlowersInUse, docs/575 §3) [verified]: the Give Flowers social marks the bouquet in the giver's
    //   inventory in use (GiveFlowersBeforeCallback, on both the Accept and the Reject rule) but only the Accept rule's
    //   after-callback clears it. A rejected or cancelled Give Flowers leaves the bouquet in use for good (InventoryItem
    //   .mInUse is saved): it cannot be dragged, sold or given, and the next Give Flowers skips it and buys a new one.
    //   Fix: when a Give Flowers social cleans up, a bouquet still in use in the giver's inventory is set not in use; at
    //   world load, a bouquet in use in a Sim's inventory is freed when that Sim has no Give Flowers and nothing queued
    //   on the bouquet. Counters [2681] bouquets freed, [2682] errors.
    // uselistprune (kUseListPrune, docs/575 §4) [mechanism verified, trigger inferred]: GameObject.mActorsUsingMe (the
    //   use list behind UseCount, 243 readers) and mReferenceList are saved fields. EA wrote
    //   GameObject.ValidateAllReferenceListsAndUseLists, which drops Sims whose script proxy is gone, but nothing calls
    //   it, and its loop would throw on a null entry. A Sim destroyed while listed (moved out, travelled, deleted) keeps
    //   the object "in use" for good. Fix: EA's own pass at world load, null-safe, also dropping null and destroyed
    //   entries; direct list removal as EA's pass does (no use-list side effects). Counters [2683] entries dropped,
    //   [2684] errors.
    // chestuselist (kChestUseList, docs/575 §5; docs/574 parked lead) [verified]: TreasureChest+OpenWithAnimation.Cleanup
    //   overrides Cleanup without calling base.Cleanup, so when the open is cut between StandardEntry and StandardExit (a
    //   reset, an exception) the base step that takes the Sim off the chest's use list never runs. Nothing else clears it
    //   (Sim reset only runs the interactions' Cleanup; no global use-list scrub exists), and the list is saved, so the
    //   chest stays in use by that Sim. Fix: after that Cleanup, the Sim is taken off the chest's use list when still on
    //   it (after a normal open StandardExit already did, so nothing changes). Counters [2685] Sims taken off, [2686] errors.
    // Checks: ObjectUseLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kOuAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sOuHandset = typeof(Phone).GetField("HandsetPickedUp", kOuAll);
        static readonly MethodInfo sOuSetState = typeof(Phone).GetMethod("SetHandsetState", kOuAll);
        static readonly MethodInfo sOuReplace = typeof(Phone).GetMethod("ReplaceHandset", kOuAll);
        static readonly MethodInfo sOuSocialCleanup = typeof(Sims3.Gameplay.Socializing.SocialInteractionA).GetMethod("Cleanup", kOuAll);

        static void ApplyObjectUseLogic()
        {
            Patch(typeof(Phone), "ReplaceHandset", typeof(HandsetResetHook), "ReplaceHook", "ReplaceOrig");
            Patch(typeof(Sims3.Gameplay.Socializing.SocialInteractionA), "Cleanup", typeof(FlowersInUseHook), "CleanupHook", "CleanupOrig");
            if (sCuType != null) Patch(sCuType, "Cleanup", typeof(ChestUseListHook), "CleanupHook", "CleanupOrig");
            else AppliedLog().Add("TreasureChest+OpenWithAnimation.Cleanup: type not found");
        }

        // ---- chestuselist ----
        static readonly Type sCuType = typeof(Sims3.Gameplay.Objects.TombObjects.TreasureChest).GetNestedType("OpenWithAnimation", kOuAll);
        static readonly MethodInfo sCuCleanup = sCuType == null ? null : sCuType.GetMethod("Cleanup", kOuAll | BindingFlags.DeclaredOnly);

        // Whether the Sim is taken off the chest's use list after OpenWithAnimation.Cleanup: only while still on it.
        internal static bool CuRemove(bool on, bool stillOnUseList)
        {
            return on && stillOnUseList;
        }

        public class ChestUseListHook
        {
            public void CleanupHook()
            {
                CleanupOrig();
                try
                {
                    Sims3.Gameplay.Interactions.InteractionInstance ii = (Sims3.Gameplay.Interactions.InteractionInstance)(object)this;
                    Sim actor = ii.InstanceActor;
                    GameObject chest = ii.Target as GameObject;
                    if (actor != null && chest != null && !chest.HasBeenDestroyed
                        && CuRemove(ShortcutOn("chestuselist"), chest.IsActorUsingMe(actor)))
                    {
                        chest.RemoveFromUseList(actor);
                        PatchCounters()[2685]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2686]++; }
            }
            public void CleanupOrig() { }
        }

        // ---- handsetreset ----
        // Whether the handset geometry is put back on the base: after ReplaceHandset with no state machine (no exit
        // animation will run), or at load for a landline nobody holds or uses. Cell and smart phones have no base.
        internal static bool HrPutBack(bool on, bool landline, bool noStateMachine, bool handsetFlag, int users, bool atLoad)
        {
            if (!on || !landline) return false;
            if (atLoad) return !handsetFlag && users == 0;
            return noStateMachine;
        }

        static bool HrLandline(Phone p)
        {
            return p != null && !(p is PhoneCell) && !(p is PhoneSmart);
        }

        public class HandsetResetHook
        {
            public void ReplaceHook(Sim actor, StateMachineClient smc)
            {
                ReplaceOrig(actor, smc);
                try
                {
                    Phone p = (Phone)(object)this;
                    if (sOuSetState != null && HrPutBack(ShortcutOn("handsetreset"), HrLandline(p), smc == null, false, 0, false)
                        && Sims3.SimIFace.Objects.IsValid(p.ObjectId))
                    {
                        sOuSetState.Invoke(p, new object[] { false });
                        PatchCounters()[2679]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2680]++; }
            }
            public void ReplaceOrig(Sim actor, StateMachineClient smc) { }
        }

        // World load finished (Instantiator): idle landlines saved with the handset hidden.
        public static void HandsetResetWorldLoad()
        {
            try
            {
                if (!ShortcutOn("handsetreset") || sOuSetState == null || sOuHandset == null) return;
                foreach (Phone p in Sims3.Gameplay.Queries.GetObjects<Phone>())
                {
                    try
                    {
                        if (!HrLandline(p) || !Sims3.SimIFace.Objects.IsValid(p.ObjectId)) continue;
                        if (HrPutBack(true, true, false, (bool)sOuHandset.GetValue(p), p.UseCount, true))
                        {
                            sOuSetState.Invoke(p, new object[] { false });
                            PatchCounters()[2679]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[2680]++; }
                }
            }
            catch (Exception) { PatchCounters()[2680]++; }
        }

        // ---- flowersinuse ----
        // Whether a bouquet in the giver's inventory is set not in use. At cleanup: a Give Flowers social whose bouquet
        // is still in use there (after an Accept it has left the giver's inventory). At load: no Give Flowers on the
        // Sim and nothing in its queue aimed at the bouquet.
        internal static bool FiFree(bool on, bool inUseInGiverInventory, bool atLoad, bool simHasGiveFlowers, bool queuedOnBouquet)
        {
            if (!on || !inUseInGiverInventory) return false;
            if (atLoad) return !simHasGiveFlowers && !queuedOnBouquet;
            return true;
        }

        public class FlowersInUseHook
        {
            public void CleanupHook()
            {
                try
                {
                    Sim.GiveFlowers gf = ((object)this) as Sim.GiveFlowers;
                    if (gf != null && gf.FlowerObject != null && ShortcutOn("flowersinuse"))
                    {
                        Sim actor = gf.Actor;
                        bool inUse = actor != null && !actor.HasBeenDestroyed && actor.Inventory != null && actor.Inventory.GetInUse(gf.FlowerObject);
                        if (FiFree(true, inUse, false, true, false) && actor.Inventory.SetNotInUse(gf.FlowerObject))
                            PatchCounters()[2681]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2682]++; }
                CleanupOrig();
            }
            public void CleanupOrig() { }
        }

        public static void FlowersInUseWorldLoad()
        {
            try
            {
                if (!ShortcutOn("flowersinuse")) return;
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    try
                    {
                        if (s == null || s.Inventory == null || s.InteractionQueue == null) continue;
                        List<IFlowers> held = s.Inventory.FindAll<IFlowers>(false);
                        if (held == null || held.Count == 0) continue;
                        bool hasGive = s.InteractionQueue.HasInteractionOfType(typeof(Sim.GiveFlowers));
                        foreach (IFlowers f in held)
                        {
                            if (!s.Inventory.GetInUse(f)) continue;
                            bool queued = false;
                            foreach (Sims3.Gameplay.Interactions.InteractionInstance ii in s.InteractionQueue.InteractionList)
                                if (ii != null && ii.Target == f) { queued = true; break; }
                            if (FiFree(true, true, true, hasGive, queued) && s.Inventory.SetNotInUse(f)) PatchCounters()[2681]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[2682]++; }
                }
            }
            catch (Exception) { PatchCounters()[2682]++; }
        }

        // ---- uselistprune ----
        // Whether a use-list or reference-list entry is dropped: EA's test (no script proxy) plus null and destroyed.
        internal static bool UlDrop(bool on, bool isNull, bool destroyed, bool proxyGone)
        {
            return on && (isNull || destroyed || proxyGone);
        }

        static int UlPrune(List<Sim> list)
        {
            if (list == null || list.Count == 0) return 0;
            int dropped = 0;
            for (int i = list.Count - 1; i >= 0; i--)  // backwards: removal in place, no copy
            {
                Sim s = list[i];
                bool isNull = s == null;
                bool destroyed = !isNull && s.HasBeenDestroyed;
                bool proxyGone = !isNull && !destroyed && Sims3.SimIFace.Simulator.GetProxyPreInit(s.ObjectId) == null;
                if (UlDrop(true, isNull, destroyed, proxyGone)) { list.RemoveAt(i); dropped++; }
            }
            return dropped;
        }

        public static void UseListPruneWorldLoad()
        {
            try
            {
                if (!ShortcutOn("uselistprune")) return;
                foreach (GameObject go in Sims3.Gameplay.Queries.GetObjects<GameObject>())
                {
                    try
                    {
                        if (go == null) continue;
                        int n = UlPrune(go.ActorsUsingMe) + UlPrune(go.ReferenceList);
                        PatchCounters()[2683] += n;
                    }
                    catch (Exception) { PatchCounters()[2684]++; }
                }
            }
            catch (Exception) { PatchCounters()[2684]++; }
        }

        // live paths of the checks (CheckVerdict.cs CkLive): hook swaps in place, reflection handles usable
        static readonly int sLive_handsetreset = LiveAdd("handsetreset", () => EpLive(typeof(HandsetResetHook), 1, "sOuSetState", sOuSetState, 1));
        static readonly int sLive_flowersinuse = LiveAdd("flowersinuse", () => EpLive(typeof(FlowersInUseHook), 1));
        static readonly int sLive_chestuselist = LiveAdd("chestuselist", () => EpLive(typeof(ChestUseListHook), 0));
        static readonly int sLive_resetinusefree = LiveAdd("resetinusefree", () => EpJoin(EpJoin(EpLive(typeof(PutDownInUseHook), 1), EpLive(typeof(ResetInUseHook), 1, "sIuActor", sIuActor, null)), EpLive(typeof(VoodooPutDownInUseHook), 0)));
        static readonly int sLive_putdowninuse = LiveAdd("putdowninuse", () => EpJoin(EpLive(typeof(PutDownInUseHook), 1), EpLive(typeof(VoodooPutDownInUseHook), 0)));
        static readonly int sLive_autonomyinuseblock = LiveAdd("autonomyinuseblock", () => EpLive(typeof(AibHook), 1, "sAibHeadActive", sAibHeadActive, typeof(bool)));
    }
}
