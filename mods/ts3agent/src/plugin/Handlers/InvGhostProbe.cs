using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace.BuildBuy;

namespace Ts3Agent
{
    // inv_ghost {n?: 3} (docs/191 inventory hunt A2, ts3fixes familyinvghost): STRESS, not a player flow. Repro of EA's
    // "destroyed while in the family inventory" path without paying bills (no money changes): for n EA products, puts two
    // copies of each into the active household's family inventory (one stack), destroys the stack's top copy directly
    // (as Bill.DestroyBill does), then reports how many destroyed objects are still listed and whether the live copy can
    // still be found (FindItemStack) and taken out (TryToRemove). The live copies are then destroyed too. Test saves only.
    public static partial class Handlers
    {
        static void RegisterInvGhost() { Dispatcher.Register("inv_ghost", InvGhost); }

        static bool IgListed(Inventory inv, IGameObject obj)
        {
            foreach (InventoryStack st in inv.InventoryItems.Values)
                if (st != null && st.List != null)
                    foreach (InventoryItem it in st.List) if (it != null && object.ReferenceEquals(it.Object, obj)) return true;
            return false;
        }

        static object InvGhost(Args a)
        {
            RequireLive();
            int n = (int)a.Long("n", 3);
            Household hh = Household.ActiveHousehold;
            if (hh == null || hh.SharedFamilyInventory == null) throw new McpException("no active family inventory");
            Inventory inv = hh.SharedFamilyInventory.Inventory;
            int stacksBefore = inv.InventoryItems.Count;
            int tried = 0, added = 0, deadListed = 0, liveFound = 0, liveRemoved = 0, liveLost = 0;
            List<string> names = new List<string>();
            foreach (BuildBuyProduct p in SortedCatalog())
            {
                if (tried >= n) break;
                if (!Placeable(p) || ContentOrigin(p.ProductResourceKey) != "ea") continue;
                GameObject g1 = null, g2 = null;
                try
                {
                    g1 = GlobalFunctions.CreateObjectOutOfWorld(p.ProductResourceKey) as GameObject;
                    g2 = GlobalFunctions.CreateObjectOutOfWorld(p.ProductResourceKey) as GameObject;
                }
                catch (Exception) { }
                if (g1 == null || g2 == null || !inv.TryToAdd(g1) || !inv.TryToAdd(g2) || !IgListed(inv, g1) || !IgListed(inv, g2))
                {
                    if (g1 != null && !g1.HasBeenDestroyed) g1.Destroy();
                    if (g2 != null && !g2.HasBeenDestroyed) g2.Destroy();
                    continue;
                }
                tried++; added += 2; names.Add(g1.GetType().Name);
                // the top of their stack
                IGameObject top = null;
                foreach (InventoryStack st in inv.InventoryItems.Values)
                    if (st != null && st.List != null && st.List.Count > 0 && (object.ReferenceEquals(st.List[0].Object, g1) || object.ReferenceEquals(st.List[0].Object, g2))) { top = st.List[0].Object; break; }
                GameObject dead = top as GameObject ?? g1, live = object.ReferenceEquals(dead, g1) ? g2 : g1;
                dead.Destroy();
                if (IgListed(inv, dead)) deadListed++;
                bool found = false;
                try
                {
                    System.Reflection.MethodInfo fis = typeof(Inventory).GetMethod("FindItemStack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new Type[] { typeof(IGameObject), typeof(InventoryStack).MakeByRefType() }, null);
                    object[] args = new object[] { live, null };
                    found = fis != null && Convert.ToUInt32(fis.Invoke(inv, args)) != 0;
                }
                catch (Exception) { }
                if (found) liveFound++;
                bool removed = false;
                try { removed = inv.TryToRemove(live); } catch (Exception) { }
                if (removed) liveRemoved++; else if (IgListed(inv, live)) liveLost++;
                live.Destroy();
            }
            Dictionary<string, object> r = Util.Obj();
            r["pairs"] = tried; r["added"] = added; r["types"] = names;
            r["destroyed_still_listed"] = deadListed;
            r["live_copy_found"] = liveFound; r["live_copy_removed"] = liveRemoved; r["live_copy_stuck"] = liveLost;
            r["stacks_before"] = stacksBefore; r["stacks_after"] = inv.InventoryItems.Count;
            return r;
        }
    }
}
