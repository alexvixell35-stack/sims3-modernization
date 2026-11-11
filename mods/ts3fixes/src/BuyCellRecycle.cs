using System;
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
            Patch(b, "AddGridItem", new Type[] { typeof(ItemGrid), typeof(object), typeof(WindowBase), typeof(object) },
                typeof(BcrHook), "AddHook", "AddOrig");
        }

        static FieldInfo[] sBcrGridState;

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
            public bool AddOrig(ItemGrid grid, object obj, WindowBase win, object ctx) { return false; } // receives AddGridItem's original header
        }
    }
}
