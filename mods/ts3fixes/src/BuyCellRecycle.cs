using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.UI;

namespace Ts3Fixes
{
    // buycellrecycle (tuning kBuyCellRecycle, default off until measured; docs/191 "Buy catalog icons").
    //
    // EA bug [verified IL, UI.dll 2026-11-11]: BuyController.PopulateGridTask takes a catalog cell for every product in the
    // list (GetGridItem: pops one from mBuyCatalogGridItemCache / mBuildCatalogPatternGridItemCache, or loads a new layout),
    // then calls AddGridItem(grid, obj, win, ctx). AddGridItem returns without adding the cell to the grid and without
    // putting it back in a cache when the product is hidden or Passport-locked (BuildBuyModel.GetShowBuyProduct), its
    // content type is disabled, or for some preset / featured / collection cases. Those exits come before the cell is
    // changed. The dropped cell is referenced by nothing, never attached and never disposed, so its CustomContentIcon stays
    // in UIManager.mCustomControlInstanceDict for the session: about 150 per Buy visit measured on a real save.
    //
    // AddGridItem returns bool: false on every exit that drops the cell, true after ItemGrid.AddItem [verified IL]. v1 and v2
    // declared the hook void: the swapped signature then made the caller's `pop` underflow Mono's transform (assert
    // transform.c:1170 `td.sp >= td.stack`, int3 at 0x58D9B6) and the game died on the first Buy visit (2026-11-11).
    // The pass: when AddGridItem returns false and the grid did not change (ItemGrid.Count alone is not enough: AddItem can fill
    // the temp slot without changing it; v1 judged by Count and crashed the game on the first Buy visit, 2026-11-11), the untouched cell goes back to the cache it came from
    // (pattern cache for catalog type 4, buy cache otherwise; the same choice RepopulateCatalogGrid makes), so it is reused
    // by the next product and disposed with the cache by BuyController.Dispose. UI only.
    // Counters: [2163] cells dropped by AddGridItem (also counted off), [2164] returned to a cache, [2165] errors.
    //
    // buyclearrecycle (tuning kBuyClearRecycle) [verified IL]: ShowCatalogGrid(false) (back button, catalog switch, room
    // tab, ...) always ends with mCatalogGrid.Clear(), and Clear() = Clear(false) only detaches the cells (no
    // DisposeOnDetach), so they and their icons stay registered for the session. RepopulateCatalogGrid recycles first:
    // each cell's window goes to the pattern cache for catalog type 4, else to the buy cache. The pass does that same step
    // before ShowCatalogGrid(false) runs, except for the inventory catalog (type 3: InventoryItemWin cells, no cache), and
    // only when the grid was filled under the current catalog type (SelectCatalog may switch the type first).
    // buygridsweep (tuning kBuyGridSweep) [verified IL + measured]: SelectCatalog / ShowAllItems repoint mCatalogGrid, and
    // BuyController.Dispose disposes the icons (child id 35, CustomContentIcon; AddGridItem turns their DisposeOnDetach
    // off) of the current grid's cells only. Measured 2026-11-11 (phase-20261111-2234, icon probe): every Buy navigation
    // visit left one ItemGrid with 220 cells that no controller field points to and that is not current at Dispose; its
    // icons stay registered for the session. That grid is the ItemGrid inside BuyController.mExpandableCatalogGrid
    // (ExpandableCatalogGrid.PreInit, control id 174056508), whose sub-layout ExpandableCatalogGrid.Dispose shuts down.
    // The pass (v2): before EA's Dispose, for the named catalog grids and every grid AddGridItem filled this visit
    // except the current one, each cell icon (GetChildByID(35, true) as CustomContentIcon) gets DisposeOnDetach back on,
    // so EA's OnCustomDetach handles it when the layout shuts down. v1 disposed the icons directly, and the shutdown's
    // detach re-registered a new wrapper for each native icon (measured: 220 live entries after Unload either way). The
    // inventory and debug grids are not touched. UI only.
    // Counters: [2166] cells seen at ShowCatalogGrid(false) (also off), [2167] returned to a cache, [2168] errors,
    // [2169] icons in other grids at Dispose (also off), [2170] handed back to EA's detach, [2171] errors.
    public static partial class Fixes
    {
        static FieldInfo sBcrBuyCache, sBcrPatCache, sBcrType;

        static void ApplyBuyCellRecycle()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type b = typeof(BuyController);
            sBcrBuyCache = b.GetField("mBuyCatalogGridItemCache", inst);
            sBcrPatCache = b.GetField("mBuildCatalogPatternGridItemCache", inst);
            sBcrType = b.GetField("mCurrCatalogType", inst);
            if (sBcrBuyCache == null || sBcrPatCache == null || sBcrType == null)
            {
                AppliedLog().Add("buycellrecycle: members not found, not patched");
                return;
            }
            string[] gs = { "mLastEntryI", "mLastEntryJ", "mTempEntryI", "mTempEntryJ" };
            sBcrGridState = new FieldInfo[gs.Length];
            for (int i = 0; i < gs.Length; i++)
            {
                sBcrGridState[i] = typeof(ItemGrid).GetField(gs[i], inst);
                if (sBcrGridState[i] == null) { AppliedLog().Add("buycellrecycle: ItemGrid." + gs[i] + " not found, not patched"); return; }
            }
            sBcrCatalogGrid = b.GetField("mCatalogGrid", inst);
            string[] og = { "mGridSortByFunction", "mGridSortByRoom", "mGridCollection", "mGridStoreNotable" };
            List<FieldInfo> ogf = new List<FieldInfo>();
            foreach (string n in og) { FieldInfo f = b.GetField(n, inst); if (f != null) ogf.Add(f); else AppliedLog().Add("buygridsweep: " + n + " not found"); }
            sBcrOtherGrids = ogf.ToArray();
            if (sBcrCatalogGrid != null)
                Patch(b, "ShowCatalogGrid", new Type[] { typeof(bool) }, typeof(BcrHook), "ShowHook", "ShowOrig");
            else AppliedLog().Add("buyclearrecycle/buygridsweep: mCatalogGrid not found, not patched");
            Patch(b, "AddGridItem", new Type[] { typeof(ItemGrid), typeof(object), typeof(WindowBase), typeof(object) },
                typeof(BcrHook), "AddHook", "AddOrig");
        }

        static FieldInfo[] sBcrGridState, sBcrOtherGrids;
        static FieldInfo sBcrCatalogGrid;
        // catalog type each grid was last filled under (AddGridItem returned true); ShowHook recycles only on a match, so a
        // pattern cell never goes to the buy cache or the other way round
        static readonly Dictionary<ItemGrid, int> sBcrGridType = new Dictionary<ItemGrid, int>();
        // Probe (read only, with buycellrecycle patched): every cell AddGridItem adds in this Buy visit; after EA's Dispose,
        // the icons (child 35) it did not dispose, by where their cell was just before Dispose: [2181] cells tracked, [2182]
        // cache, [2183] a controller grid, [2184] nowhere but parented, [2185] nowhere, no parent, [2186] errors.
        static readonly List<WindowBase> sBcrTracked = new List<WindowBase>();
        static readonly Dictionary<WindowBase, bool> sBcrTrackedSet = new Dictionary<WindowBase, bool>();
        static readonly Dictionary<WindowBase, ItemGrid> sBcrTrackedGrid = new Dictionary<WindowBase, ItemGrid>();

        static List<CustomContentIcon> sBcrPrIcons = new List<CustomContentIcon>();
        static List<int> sBcrPrClass = new List<int>();
        static int sBcrPrLogged;

        // before EA's Dispose: where each tracked cell is, and its icon (kept by reference, so the after-check does not
        // look windows up again once Dispose has destroyed them). Class 0 cache, 1 a controller grid, 2 nowhere, parented,
        // 3 nowhere, no parent.
        internal static void BcrProbeBeforeDispose(object ctl)
        {
            sBcrPrIcons.Clear(); sBcrPrClass.Clear();
            int[] c = PatchCounters();
            try
            {
                if (sBcrTracked.Count == 0) return;
                List<WindowBase> buy = sBcrBuyCache.GetValue(ctl) as List<WindowBase>, pat = sBcrPatCache.GetValue(ctl) as List<WindowBase>;
                Dictionary<WindowBase, bool> inGrid = new Dictionary<WindowBase, bool>();
                List<FieldInfo> gf = new List<FieldInfo>(sBcrOtherGrids ?? new FieldInfo[0]);
                if (sBcrCatalogGrid != null) gf.Add(sBcrCatalogGrid);
                foreach (string n in new string[] { "mGridInventory", "mGridDebug" }) { FieldInfo f = ctl.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (f != null) gf.Add(f); }
                foreach (FieldInfo f in gf)
                {
                    ItemGrid g = null;
                    try { g = f.GetValue(ctl) as ItemGrid; } catch (Exception) { }
                    if (g == null) continue;
                    try { foreach (ItemGridCellItem it in g.Items) if (it.mWin != null) inGrid[it.mWin] = true; } catch (Exception) { }
                }
                foreach (WindowBase cell in sBcrTracked)
                {
                    try
                    {
                        if (cell.Disposed) continue;
                        CustomContentIcon icon = cell.GetChildByID(35u, true) as CustomContentIcon;
                        if (icon == null || icon.Disposed) continue;
                        int k;
                        if ((buy != null && buy.Contains(cell)) || (pat != null && pat.Contains(cell))) k = 0;
                        else if (inGrid.ContainsKey(cell)) k = 1;
                        else if (cell.Parent != null)
                        {
                            k = 2;
                            if (sBcrPrLogged < 3)
                            {
                                sBcrPrLogged++;
                                ItemGrid ag; sBcrTrackedGrid.TryGetValue(cell, out ag);
                                string fname = "none";
                                foreach (FieldInfo f in ctl.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                    if (typeof(WindowBase).IsAssignableFrom(f.FieldType)) { try { if (ag != null && object.ReferenceEquals(f.GetValue(ctl), ag)) { fname = f.Name; break; } } catch (Exception) { } }
                                int cnt = -1, items = -1; bool disp = true;
                                try { if (ag != null) { disp = ag.Disposed; cnt = ag.Count; items = ag.Items.Count; } } catch (Exception) { }
                                AppliedLog().Add("buy icon probe: added to grid field " + fname + ", grid disposed " + disp + ", Count " + cnt + ", Items " + items
                                    + ", current " + (ag != null && object.ReferenceEquals(sBcrCatalogGrid.GetValue(ctl), ag)) + ", grid is " + (ag != null ? ag.GetType().Name + " id " + ag.ID : "?"));
                                WindowBase pw = cell.Parent, gp = pw != null ? pw.Parent : null;
                                AppliedLog().Add("buy icon probe: cell in no cache/grid, parent " + (pw != null ? pw.GetType().Name + " id " + pw.ID : "?")
                                    + ", grandparent " + (gp != null ? gp.GetType().Name + " id " + gp.ID : "none") + ", cell visible " + cell.Visible);
                            }
                        }
                        else k = 3;
                        sBcrPrIcons.Add(icon); sBcrPrClass.Add(k);
                    }
                    catch (Exception) { c[2186]++; }
                }
            }
            catch (Exception) { c[2186]++; }
        }

        // after EA's Dispose: icons it left undisposed, by class: [2182] cache, [2183] grid, [2184] nowhere parented,
        // [2185] nowhere unparented
        // live CustomContentIcon entries in the dict (handle == key): [slot] with no parent, [slot+1] parented
        internal static void BcrIconCensus(int slot)
        {
            int[] c = PatchCounters();
            try
            {
                IDictionary dict = sBilDict != null ? sBilDict.GetValue(null) as IDictionary : null;
                if (dict == null) return;
                int np = 0, par = 0;
                foreach (DictionaryEntry e in dict)
                {
                    CustomContentIcon w = e.Value as CustomContentIcon;
                    if (w == null || w.Disposed || Convert.ToUInt32(sBilHandle.GetValue(w, null)) != Convert.ToUInt32(e.Key)) continue;
                    if (w.Parent == null) np++; else par++;
                }
                c[slot] = np; c[slot + 1] = par;  // a snapshot, not a running total
            }
            catch (Exception) { c[2186]++; }
        }

        internal static void BcrProbeAfterDispose(object ctl)
        {
            int[] c = PatchCounters();
            try
            {
                for (int i = 0; i < sBcrPrIcons.Count; i++)
                    if (!sBcrPrIcons[i].Disposed) c[2182 + sBcrPrClass[i]]++;
            }
            catch (Exception) { c[2186]++; }
            finally { sBcrTracked.Clear(); sBcrTrackedSet.Clear(); sBcrTrackedGrid.Clear(); sBcrPrIcons.Clear(); sBcrPrClass.Clear(); }
        }

        // buygridsweep: called by BilHook.DisposeHook (BuyIconLeak.cs) before EA's BuyController.Dispose
        internal static void BcrSweepOtherGrids(object ctl)
        {
            sBcrGridType.Clear();  // this controller's grids die with it
            if (sBcrCatalogGrid == null || sBcrOtherGrids == null) return;
            int[] c = PatchCounters();
            try
            {
                object cur = sBcrCatalogGrid.GetValue(ctl);
                bool on = ShortcutOn("buygridsweep");
                // the named catalog grids, plus every grid PopulateGridTask filled this visit (AddGridItem's grid argument):
                // mCatalogGrid is repointed by SelectCatalog / ShowAllItems, and a grid it no longer points to keeps its
                // cells (measured: one grid with 220 cells per Buy navigation visit, in no controller field)
                List<ItemGrid> grids = new List<ItemGrid>();
                foreach (FieldInfo f in sBcrOtherGrids) { ItemGrid g0 = f.GetValue(ctl) as ItemGrid; if (g0 != null && !grids.Contains(g0)) grids.Add(g0); }
                foreach (ItemGrid g0 in sBcrTrackedGrid.Values) if (g0 != null && !grids.Contains(g0)) grids.Add(g0);
                foreach (ItemGrid g in grids)
                {
                    if (g == null || g.Disposed || object.ReferenceEquals(g, cur)) continue;
                    foreach (ItemGridCellItem it in g.Items)
                    {
                        try
                        {
                            if (it.mWin == null) continue;
                            CustomContentIcon icon = it.mWin.GetChildByID(35u, true) as CustomContentIcon;
                            if (icon == null || icon.Disposed || icon.DisposeOnDetach) continue;
                            c[2169]++;
                            if (!on) continue;
                            // v2: hand the icon back to EA's detach (OnCustomDetach removes, unregisters and disposes it when
                            // the grid's layout shuts down). v1 disposed it here, and the shutdown's detach then re-created
                            // a wrapper for the native icon through WrapperFromHandle (measured phase-20261111-2318).
                            icon.DisposeOnDetach = true;
                            c[2170]++;
                        }
                        catch (Exception) { c[2171]++; }
                    }
                }
            }
            catch (Exception) { c[2171]++; }
        }

        // ItemGrid.Count is mLastEntryJ * EntriesCountI + mLastEntryI + 1, but AddItem can also fill the temp slot
        // (mTempEntryI/J, Count unchanged), so "added" is judged by all four fields, and then by Items.
        static long BcrGridState(ItemGrid g)
        {
            long h = 17;
            foreach (FieldInfo f in sBcrGridState) h = h * 1000003 + Convert.ToInt64(f.GetValue(g));
            return h;
        }

        static bool BcrInGrid(ItemGrid g, WindowBase w)
        {
            foreach (ItemGridCellItem it in g.Items) if (object.ReferenceEquals(it.mWin, w)) return true;
            return false;
        }

        public class BcrHook
        {
            public bool AddHook(ItemGrid grid, object obj, WindowBase win, object ctx)
            {
                long s0 = 0; bool ok = false;
                try { if (grid != null && win != null && sBcrGridState != null) { s0 = BcrGridState(grid); ok = true; } } catch (Exception) { }
                bool added = AddOrig(grid, obj, win, ctx);
                if (added && grid != null) { try { sBcrGridType[grid] = Convert.ToInt32(sBcrType.GetValue(this)); } catch (Exception) { } }
                if (added && win != null && !sBcrTrackedSet.ContainsKey(win)) { sBcrTrackedSet[win] = true; sBcrTracked.Add(win); sBcrTrackedGrid[win] = grid; PatchCounters()[2181]++; }
                if (!ok || added) return added;
                int[] c = PatchCounters();
                try
                {
                    if (BcrGridState(grid) != s0 || win.Disposed || win.Parent != null || BcrInGrid(grid, win)) return added;
                    c[2163]++;
                    if (!ShortcutOn("buycellrecycle")) return added;
                    int type = Convert.ToInt32(sBcrType.GetValue(this));
                    List<WindowBase> cache = (type == 4 ? sBcrPatCache : sBcrBuyCache).GetValue(this) as List<WindowBase>;
                    if (cache == null || cache.Contains(win)) return added;
                    cache.Add(win);
                    c[2164]++;
                }
                catch (Exception) { c[2165]++; }
                return added;
            }
            public void ShowHook(bool show)
            {
                if (!show)
                {
                    int[] c = PatchCounters();
                    try
                    {
                        ItemGrid g = sBcrCatalogGrid.GetValue(this) as ItemGrid;
                        int type = Convert.ToInt32(sBcrType.GetValue(this));
                        int filled;
                        if (g != null && type != 3 && sBcrGridType.TryGetValue(g, out filled) && filled == type)
                        {
                            List<ItemGridCellItem> items = g.Items;
                            c[2166] += items.Count;
                            if (ShortcutOn("buyclearrecycle") && items.Count > 0)
                            {
                                List<WindowBase> cache = (type == 4 ? sBcrPatCache : sBcrBuyCache).GetValue(this) as List<WindowBase>;
                                if (cache != null)
                                    foreach (ItemGridCellItem it in items)
                                        if (it.mWin != null && !it.mWin.Disposed && !cache.Contains(it.mWin)) { cache.Add(it.mWin); c[2167]++; }
                            }
                        }
                    }
                    catch (Exception) { c[2168]++; }
                }
                ShowOrig(show);
            }
            public void ShowOrig(bool show) { } // receives ShowCatalogGrid(bool)'s original header

            public bool AddOrig(ItemGrid grid, object obj, WindowBase win, object ctx) { return false; } // receives AddGridItem's original header
        }
    }
}
