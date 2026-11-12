using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;

namespace Ts3Agent
{
    // leak_census section "inventories" (docs/191 inventory hunt, 2026-11-11). Counts the inconsistent states found by the
    // static inventory audit, per owner class (active household Sims / other Sims / active family inventory / other family
    // inventories / objects): items whose ItemComp.InventoryParent is not the inventory listing them (null / another live
    // inventory / a dead one) [A1, A4]; objects listed in more than one inventory [A1]; destroyed or invalid items still
    // listed [A2]; stacks whose top item is dead while live items remain under it [A2]; stacks with a null or empty List
    // [A5]; items in a stack that no longer stack with its top [A7]; objects whose ItemComp says they are in an inventory
    // that does not list them [A4]; family-inventory items per household class, by type [D1]; InventoryItemWin entries in
    // UIManager.mCustomControlInstanceDict by state [B]; live parentless custom controls by type and control id. Reads only.
    public static partial class Handlers
    {
        static string LcInvClass(Inventory inv, Household active)
        {
            object owner = inv.Owner;
            Sim s = owner as Sim;
            if (s != null) return s.Household == active && active != null ? "sim_active" : (s.IsSelectable ? "sim_selectable" : "sim_other");
            SharedFamilyInventory f = owner as SharedFamilyInventory;
            if (f != null)
            {
                Household h = null;
                try { h = LcGet(f, "mHousehold") as Household; } catch (Exception) { }
                if (h == null) foreach (Household hh in Household.sHouseholdList) if (hh != null && hh.SharedFamilyInventory == f) { h = hh; break; }
                return h != null && h == active ? "family_active" : "family_other";
            }
            return "object";
        }

        static Dictionary<string, object> LcInventories(int top)
        {
            Dictionary<string, object> r = Util.Obj();
            Household active = Household.ActiveHousehold;
            List<Inventory> invs = new List<Inventory>();
            Dictionary<Inventory, bool> seenInv = new Dictionary<Inventory, bool>();
            foreach (GameObject g in Sims3.Gameplay.Queries.GetObjects<GameObject>())
            {
                Inventory inv = null;
                try { inv = g.Inventory; } catch (Exception) { }
                if (inv != null && !seenInv.ContainsKey(inv)) { seenInv[inv] = true; invs.Add(inv); }
            }
            foreach (Household h in Household.sHouseholdList)
            {
                Inventory inv = null;
                try { inv = h != null && h.SharedFamilyInventory != null ? h.SharedFamilyInventory.Inventory : null; } catch (Exception) { }
                if (inv != null && !seenInv.ContainsKey(inv)) { seenInv[inv] = true; invs.Add(inv); }
            }

            Dictionary<string, int> items = new Dictionary<string, int>(), stacks = new Dictionary<string, int>(), bad = new Dictionary<string, int>();
            Dictionary<string, int> familyTypes = new Dictionary<string, int>(), objTypes = new Dictionary<string, int>();
            Dictionary<ulong, int> listedIn = new Dictionary<ulong, int>();
            Dictionary<IGameObject, Inventory> listedBy = new Dictionary<IGameObject, Inventory>();
            int maxItems = 0; string maxOwner = "";
            foreach (Inventory inv in invs)
            {
                string cls = LcInvClass(inv, active);
                int n = 0;
                Dictionary<uint, InventoryStack> map = null;
                try { map = inv.InventoryItems; } catch (Exception) { }
                if (map == null) { LcBump(bad, cls + ": no item map"); continue; }
                foreach (InventoryStack st in map.Values)
                {
                    LcBump(stacks, cls);
                    if (st == null || st.List == null) { LcBump(bad, cls + ": stack List null [A5]"); continue; }
                    if (st.List.Count == 0) { LcBump(bad, cls + ": stack empty [A5]"); continue; }
                    IGameObject topObj = null; bool topDead = false, liveBelow = false; int idx = 0;
                    foreach (InventoryItem it in st.List)
                    {
                        IGameObject o = it == null ? null : it.Object;
                        bool dead = o == null || o.HasBeenDestroyed || !Sims3.SimIFace.Objects.IsValid(o.ObjectId);
                        if (idx == 0) { topObj = o; topDead = dead; }
                        else if (!dead) liveBelow = true;
                        idx++; n++;
                        LcBump(items, cls);
                        if (dead) { LcBump(bad, cls + ": dead item listed [A2]"); continue; }
                        try
                        {
                            ulong id = o.ObjectId.Value;
                            int k; listedIn.TryGetValue(id, out k); listedIn[id] = k + 1;
                            listedBy[o] = inv;
                            ItemComponent ic = o.ItemComp;
                            if (ic == null) LcBump(bad, cls + ": no ItemComp");
                            else if (ic.InventoryParent == null) LcBump(bad, cls + ": parent null [A1]");
                            else if (!object.ReferenceEquals(ic.InventoryParent, inv)) LcBump(bad, cls + ": parent is another inventory [A1]");
                            if (idx > 1 && topObj != null && !topDead)
                            {
                                bool stacksOk = true;
                                try { stacksOk = topObj.StacksWith(o); } catch (Exception) { }
                                if (!stacksOk) LcBump(bad, cls + ": item no longer stacks with top [A7]");
                            }
                            if (cls.StartsWith("family")) LcBump(familyTypes, cls + " " + o.GetType().Name);
                            else if (cls == "object") LcBump(objTypes, o.GetType().Name);
                        }
                        catch (Exception) { LcBump(bad, cls + ": read threw"); }
                    }
                    if (topDead && liveBelow) LcBump(bad, cls + ": dead top hides live items [A2]");
                }
                if (n > maxItems) { maxItems = n; try { maxOwner = cls + " " + (inv.Owner != null ? inv.Owner.GetType().Name : "?"); } catch (Exception) { } }
            }
            int multi = 0;
            foreach (int k in listedIn.Values) if (k > 1) multi++;

            // [A4] objects that say they are in an inventory that does not list them
            int halfIn = 0, inInvNotListed = 0;
            foreach (GameObject g in Sims3.Gameplay.Queries.GetObjects<GameObject>())
            {
                try
                {
                    ItemComponent ic = g.ItemComp;
                    if (ic == null) continue;
                    if (ic.InventoryParent != null && !listedBy.ContainsKey(g)) halfIn++;
                    if (g.InInventory && ic.InventoryParent == null && !listedBy.ContainsKey(g)) inInvNotListed++;
                }
                catch (Exception) { }
            }

            // [B] InventoryItemWin wrappers in UIManager's custom-control table
            Dictionary<string, int> iiw = new Dictionary<string, int>(), ccState = new Dictionary<string, int>(), orphanIds = new Dictionary<string, int>();
            try
            {
                FieldInfo df = typeof(Sims3.UI.UIManager).GetField("mCustomControlInstanceDict", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo hp = typeof(Sims3.UI.WindowBase).GetProperty("WinHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                IDictionary dict = df != null ? df.GetValue(null) as IDictionary : null;
                FieldInfo gf = typeof(Sims3.UI.UIManager).GetField("gUIMgr", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Sims3.SimIFace.IUIManager gui = gf != null ? gf.GetValue(null) as Sims3.SimIFace.IUIManager : null;
                if (dict != null)
                    foreach (DictionaryEntry e in dict)
                    {
                        Sims3.UI.WindowBase w = e.Value as Sims3.UI.WindowBase;
                        if (w == null) continue;
                        bool isIiw = w.GetType().Name == "InventoryItemWin";
                        string st;
                        if (w.Disposed) st = "disposed";
                        else if (Convert.ToUInt32(hp.GetValue(w, null)) != Convert.ToUInt32(e.Key)) st = "handle reused";
                        else if (w.Parent == null)
                        {
                            st = "live, no parent";
                            // which layout made it: type and control id (the id is the export id in its layout)
                            try
                            {
                                // alive = the engine still reports a custom control under the handle; visible = shown
                                uint cid = 0;
                                try { if (gui != null) cid = gui.GetCustomControlID(Convert.ToUInt32(e.Key)); } catch (Exception) { }
                                // native parent: 0 = really detached; otherwise the parent's native class id (its
                                // managed Parent is null when WrapperFromHandle has no type for that class)
                                uint np = 0, npc = 0;
                                try { if (gui != null && cid != 0) { np = gui.GetParentWindow(Convert.ToUInt32(e.Key)); if (np != 0) npc = gui.GetClassID(np); } } catch (Exception) { }
                                LcBump(orphanIds, w.GetType().Name + " id " + w.ID + (cid != 0 ? " alive" : " dead") + (cid != 0 && w.Visible ? " visible" : "") + (cid == 0 ? "" : np == 0 ? " native-detached" : " native-parent-class 0x" + npc.ToString("x")));
                            }
                            catch (Exception) { }
                        }
                        else st = "live, on tree";
                        if (isIiw) LcBump(iiw, st);
                        LcBump(ccState, w.GetType().Name + ": " + st);
                    }
            }
            catch (Exception) { LcBump(iiw, "read threw"); }

            r["inventories"] = invs.Count;
            r["items_by_class"] = items;
            r["stacks_by_class"] = stacks;
            r["problems"] = bad;
            r["objects_in_several_inventories"] = multi;
            r["parent_set_but_not_listed"] = halfIn;
            r["in_inventory_flag_not_listed"] = inInvNotListed;
            r["largest_inventory"] = maxItems + " " + maxOwner;
            r["family_items_by_type"] = LcTop(familyTypes, Math.Max(top, 25));
            r["object_inventory_items_by_type"] = LcTop(objTypes, top);
            r["inventory_item_win"] = iiw;
            r["custom_control_states"] = LcTop(ccState, 30);
            r["orphans_by_type_id"] = LcTop(orphanIds, 20);
            return r;
        }
    }
}
