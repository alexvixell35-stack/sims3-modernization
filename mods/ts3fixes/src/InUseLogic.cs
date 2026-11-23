using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Inventory items left "in use" for good (docs/576 §2-3), each off by default. EA bug fixes, every Sim; no Sim is moved,
    // no object is created or destroyed.
    //
    // resetinusefree (kResetInUseFree, docs/576 §2) [verified]: Inventory.SetInUse has 53 callers; InventoryItem.mInUse is
    //   saved and only SetNotInUse / SetStackInUse clear it. Sim.DoReset runs InteractionQueue.OnReset (Cleanup of every
    //   interaction), then resets only the current interaction's target when the Sim is still on its use list; nothing
    //   clears in-use marks in the Sim's own inventory. An interaction whose Cleanup does not clear its mark (PutDown,
    //   VoodooDoll.PutDown, GiveFlowers, Terrarium.EatBug, Scrap.EatScrap, Spirit release, NectarMaker squish, ToyBox, the
    //   placement interactions; docs/576 §2 table) leaves the item greyed out for good when the Sim is reset mid-way.
    //   Fix: after InteractionQueue.OnReset, every item still in use in that Sim's own inventory is set not in use: the
    //   queue is empty, so no interaction of this Sim can hold it. Toddlers and babies are skipped (Crib keeps the stuffed
    //   animal in the child's inventory in use while it sits in the crib). Counters [2689] items freed, [2690] errors.
    // putdowninuse (kPutDownInUse, docs/576 §3) [verified]: PutDown.TryToPlaceOnSurface (and the copy in
    //   VoodooDoll+PutDown) adds the Sim to the surface's use list and marks the item in use, then plays the swipe (yields)
    //   and returns false when ParentToSlot fails, with neither undone; Cleanup only destroys the jig. The item stays in use
    //   in the inventory and the surface stays in use by that Sim (a live Sim, so no use-list pass drops it). Fix: in those
    //   Cleanups, when the item is still in the Sim's inventory and in use (the put-down did not finish), it is set not in
    //   use, and surfaces within 3 m still listing the Sim are released unless another of its queued interactions targets
    //   them. Counters [2691] put-downs repaired, [2692] errors.
    // Checks: InUseLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kIuAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sIuActor = typeof(InteractionQueue).GetField("mActor", kIuAll);
        static readonly Type sIuVoodooPutDown = typeof(Sims3.Store.Objects.VoodooDoll).GetNestedType("PutDown", kIuAll);

        static void ApplyInUseLogic()
        {
            Patch(typeof(InteractionQueue), "OnReset", typeof(ResetInUseHook), "ResetHook", "ResetOrig");
            Patch(typeof(PutDown), "Cleanup", typeof(PutDownInUseHook), "CleanupHook", "CleanupOrig");
            if (sIuVoodooPutDown != null) Patch(sIuVoodooPutDown, "Cleanup", typeof(VoodooPutDownInUseHook), "CleanupHook", "CleanupOrig");
            else AppliedLog().Add("VoodooDoll+PutDown: not found, putdowninuse covers PutDown only");
        }

        // ---- resetinusefree ----
        internal static bool RiFree(bool on, bool toddlerOrBelow, bool itemInUse, int queueLeft)
        {
            return on && !toddlerOrBelow && itemInUse && queueLeft == 0;
        }

        static int RiQueueCount(Sim s)
        {
            int n = 0;
            foreach (InteractionInstance ii in s.InteractionQueue.InteractionList) if (ii != null) n++;
            return n;
        }

        public class ResetInUseHook
        {
            public void ResetHook()
            {
                ResetOrig();
                try
                {
                    if (!ShortcutOn("resetinusefree") || sIuActor == null) return;
                    Sim s = sIuActor.GetValue(this) as Sim;
                    if (s == null || s.HasBeenDestroyed || s.Inventory == null || s.SimDescription == null) return;
                    bool young = s.SimDescription.ToddlerOrBelow;
                    int left = RiQueueCount(s);
                    foreach (IGameObject o in s.Inventory.FindAll<IGameObject>(false))
                        if (o != null && RiFree(true, young, s.Inventory.GetInUse(o), left) && s.Inventory.SetNotInUse(o)) PatchCounters()[2689]++;
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2690]++; }
            }
            public void ResetOrig() { }
        }

        // ---- putdowninuse ----
        internal static bool PdRepair(bool on, bool stillInInventory, bool inUse)
        {
            return on && stillInInventory && inUse;
        }

        static void PdCleanup(InteractionInstance ii)
        {
            try
            {
                if (!ShortcutOn("putdowninuse") || ii == null) return;
                Sim actor = ii.InstanceActor;
                IGameObject item = ii.Target;
                if (actor == null || item == null || actor.HasBeenDestroyed || actor.Inventory == null) return;
                bool there = actor.Inventory.Contains(item);
                if (!PdRepair(true, there, there && actor.Inventory.GetInUse(item))) return;
                actor.Inventory.SetNotInUse(item);
                foreach (Sims3.Gameplay.ObjectComponents.ISurface sf in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.ObjectComponents.ISurface>(actor.Position, 3f))
                {
                    GameObject g = sf as GameObject;
                    if (g == null || !g.IsActorUsingMe(actor)) continue;
                    bool held = false;
                    foreach (InteractionInstance q in actor.InteractionQueue.InteractionList)
                        if (q != null && q != ii && q.Target == (IGameObject)g) { held = true; break; }
                    if (!held) g.RemoveFromUseList(actor);
                }
                PatchCounters()[2691]++;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[2692]++; }
        }

        public class PutDownInUseHook
        {
            public void CleanupHook()
            {
                PdCleanup((InteractionInstance)(object)this);
                CleanupOrig();
            }
            public void CleanupOrig() { }
        }

        public class VoodooPutDownInUseHook
        {
            public void CleanupHook()
            {
                PdCleanup((InteractionInstance)(object)this);
                CleanupOrig();
            }
            public void CleanupOrig() { }
        }
    }
}
