using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;

namespace Ts3Fixes
{
    // familyinvghost (tuning kFamilyInvGhost, default off until measured; docs/191 "inventory hunt" A2).
    //
    // EA bug: ItemComponent.Dispose (run when an object is destroyed) takes the object out of the inventory it is in with
    // InventoryParent.RemoveByForce(obj, true), except when that inventory's Owner is a SharedFamilyInventory [verified IL
    // idx 4-16]. So an object destroyed directly while it sits in a family inventory stays listed there as a dead entry:
    // paying bills does this (Bill.DestroyBill / DestroyAllBillsForHousehold call Destroy() on bills collected from the
    // family inventory). The dead entry keeps the stack (no kStackDeleted event, the inventory UI still shows the slot,
    // Count is too high), and a dead stack top hides the live items under it from FindItem / FindItemStack / TryToRemove,
    // because CanStack fails against a destroyed top. RemoveDeadItems clears them at the next load.
    // EA's own household teardown is not affected: Inventory.DestroyItems(true) clears each item's InventoryParent
    // (ItemCompRemoval) before it destroys it, so the item's Dispose sees no parent and nothing re-enters the loop.
    //
    // The pass (switch on): before EA's Dispose, an object still listed in a family inventory is removed from it the way
    // EA removes it from every other inventory (RemoveByForce(obj, true)). EA bug fix: affects the active household's
    // family inventory (dead entries gone at once instead of at the next load).
    // Counters: [2239] destroyed objects found still in a family inventory (also with the switch off), [2240] removed,
    // [2241] errors.
    public static partial class Fixes
    {
        static void ApplyFamilyInvGhost()
        {
            Patch(typeof(ItemComponent), "Dispose", Type.EmptyTypes, typeof(FigHook), "DisposeHook", "DisposeOrig");
        }

        static bool FigListed(Inventory inv, IGameObject obj)
        {
            foreach (InventoryStack st in inv.InventoryItems.Values)
                if (st != null && st.List != null)
                    foreach (InventoryItem it in st.List)
                        if (it != null && object.ReferenceEquals(it.Object, obj)) return true;
            return false;
        }

        public class FigHook
        {
            public void DisposeHook()
            {
                try
                {
                    ItemComponent ic = (ItemComponent)(object)this;
                    Inventory inv = ic.InventoryParent;
                    if (inv != null && inv.Owner is SharedFamilyInventory)
                    {
                        IGameObject obj = ic.Owner;
                        int[] c = PatchCounters();
                        if (obj != null && FigListed(inv, obj))
                        {
                            c[2239]++;
                            if (ShortcutOn("familyinvghost")) { inv.RemoveByForce(obj, true); c[2240]++; }
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2241]++; }
                DisposeOrig();
            }
            public void DisposeOrig() { }
        }
    }
}
