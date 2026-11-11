using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.SimIFace;
using Sims3.UI;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // mapviewkeep (issue #40; UI only, not save data): the game destroys and rebuilds the map-view filter panel on every
    // switch into and out of map view. HudController.OnCameraMapViewEnabled calls MapViewController.Load (layout
    // HUDMapViewSort, plus one HUDMapviewSortEntry layout per filter row, about 10) and MapViewController.Unload (layout
    // Shutdown + Dispose). Nothing takes the panel's event handlers off its child windows, and the row layouts are never
    // shut down, so each switch left about 14 Sims3.UI.Button, 3 Scrollbar and 20 event handler objects behind (one
    // 28 min run of 1,597 switches: Button 4,888 -> 27,878, largest free block 1574 -> 887 MB). On a loaded machine the
    // rebuild also showed as stutter (UIManager.ProcessEvent in 28% of the sim-thread samples after a switch, 3% without);
    // on a quiet one it is small. A/B in one load, 40 switches per arm: Button +834 off, +79 on; Scrollbar +159 / +14.
    // With the switch on, a switch out of map view hides the panel and a switch in shows the same panel again:
    //   - hide: what MapViewController.Dispose does to game state (map tag filter back to All, sInstance null), the open
    //     filter list closed, the window invisible. sLayout stays set, so any real Unload still destroys the panel.
    //   - show: sInstance back, the row of sLastFilterType selected (another part of the game may have changed it while
    //     the panel was hidden) and the game's own SelectFilter run for it, as Init does through AddSort.
    //   - any Unload from another caller (HUD teardown, ForceShowFullHud) runs the game's method unchanged.
    //   - a kept panel that cannot be shown (native window gone, no row for the filter, switch turned off) is destroyed
    //     by the game's Unload and the game's Load builds a new one.
    // Counters: [2103] panels shown again, [2104] panels built by the game, [2105] kept panels dropped.
    public static partial class Fixes
    {
        static bool sMvkReady;

        static void ApplyMapViewKeep()
        {
            const BindingFlags kS = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            const BindingFlags kI = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type mvc = typeof(HudController).Assembly.GetType("Sims3.UI.Hud.MapViewController");
            if (mvc == null) { AppliedLog().Add("mapviewkeep: MapViewController not found, skipped"); return; }
            FieldInfo mgr = typeof(UIManager).GetField("gUIMgr", kS);
            MapViewKeepHook.sInstance = mvc.GetField("sInstance", kS);
            MapViewKeepHook.sLayout = mvc.GetField("sLayout", kS);
            MapViewKeepHook.sLast = mvc.GetField("sLastFilterType", kS);
            MapViewKeepHook.sGrid = mvc.GetField("mSortGrid", kI);
            MapViewKeepHook.sBack = mvc.GetField("mSortGridBackground", kI);
            MapViewKeepHook.sOpen = mvc.GetField("mOpenSortButton", kI);
            MapViewKeepHook.sSel = mvc.GetField("mLastSelectedNameButton", kI);
            MapViewKeepHook.sHi = mvc.GetField("mLastHighlightedNameButton", kI);
            MapViewKeepHook.sSelect = mvc.GetMethod("SelectFilter", kI);
            MapViewKeepHook.sFilter = typeof(MapTagController).GetProperty("MapTagFilter", kI);
            MapViewKeepHook.sWinH = typeof(WindowBase).GetField("mWinHandle", kI);
            MapViewKeepHook.sMgr = mgr == null ? null : mgr.GetValue(null) as IUIManager;
            bool fields = MapViewKeepHook.sInstance != null && MapViewKeepHook.sLayout != null && MapViewKeepHook.sLast != null && MapViewKeepHook.sGrid != null
                && MapViewKeepHook.sBack != null && MapViewKeepHook.sOpen != null && MapViewKeepHook.sSel != null && MapViewKeepHook.sHi != null
                && MapViewKeepHook.sSelect != null && MapViewKeepHook.sFilter != null && MapViewKeepHook.sWinH != null && MapViewKeepHook.sMgr != null;
            // all or nothing: the Load and Unload hooks act only while the toggle hook marks the caller
            bool toggle = fields && Patch(typeof(HudController), "OnCameraMapViewEnabled", new Type[] { typeof(bool) }, typeof(MapViewKeepHook), "ToggleHook", "ToggleOrig");
            bool unload = toggle && Patch(mvc, "Unload", Type.EmptyTypes, typeof(MapViewKeepHook), "UnloadHook", "UnloadOrig");
            sMvkReady = unload && Patch(mvc, "Load", Type.EmptyTypes, typeof(MapViewKeepHook), "LoadHook", "LoadOrig");
            if (!sMvkReady) AppliedLog().Add("mapviewkeep: off (fields " + fields + ", toggle " + toggle + ", unload " + unload + ")");
        }

        public class MapViewKeepHook
        {
            internal static FieldInfo sInstance, sLayout, sLast, sGrid, sBack, sOpen, sSel, sHi, sWinH;
            internal static MethodInfo sSelect;
            internal static PropertyInfo sFilter;
            internal static IUIManager sMgr;
            static bool sInToggle, sThrew;
            static WindowBase sKept;  // the hidden panel; sLayout still names its layout

            public void ToggleHook(bool enabled)
            {
                sInToggle = true;
                try { ToggleOrig(enabled); }
                finally { sInToggle = false; }
            }
            public void ToggleOrig(bool enabled) { } // receives HudController.OnCameraMapViewEnabled's original header

            public static void UnloadHook()
            {
                try
                {
                    if (sInToggle && sMvkReady && ShortcutOn("mapviewkeep"))
                    {
                        if (sKept != null) return;  // already hidden: the game calls Unload again while map view stays off
                        WindowBase inst = sInstance.GetValue(null) as WindowBase;
                        if (inst != null && sLayout.GetValue(null) != null) { Hide(inst); return; }
                    }
                }
                catch (Exception e) { Threw(e); }
                sKept = null;  // a real unload destroys the kept panel through sLayout
                UnloadOrig();
            }
            public static void UnloadOrig() { } // receives MapViewController.Unload's original header

            public static void LoadHook()
            {
                if (sKept != null)
                {
                    WindowBase k = sKept;
                    try
                    {
                        if (sInToggle && sMvkReady && ShortcutOn("mapviewkeep") && sLayout.GetValue(null) != null && sInstance.GetValue(null) == null
                            && sMgr.GetClassID((uint)sWinH.GetValue(k)) != 0 && Show(k))
                        {
                            sKept = null;  // shown: the next Unload hides it again (left set, every second switch rebuilt the panel)
                            PatchCounters()[2103]++;
                            return;
                        }
                    }
                    catch (Exception e) { Threw(e); }
                    sKept = null;
                    PatchCounters()[2105]++;
                    UnloadOrig();  // the kept panel cannot be shown: the game destroys it and builds a new one below
                }
                if (sLayout != null && sLayout.GetValue(null) == null) PatchCounters()[2104]++;
                LoadOrig();
            }
            public static void LoadOrig() { } // receives MapViewController.Load's original header

            static void Hide(WindowBase inst)
            {
                WindowBase back = sBack.GetValue(inst) as WindowBase;
                Button open = sOpen.GetValue(inst) as Button, hi = sHi.GetValue(inst) as Button;
                if (back != null) back.Visible = false;
                if (open != null) open.Selected = false;
                if (hi != null) { hi.Highlighted = false; sHi.SetValue(inst, null); }
                inst.Visible = false;
                sInstance.SetValue(null, null);
                if (MapTagController.Instance != null) sFilter.SetValue(MapTagController.Instance, MapTagFilterType.All, null);
                sKept = inst;
            }

            // false when the kept panel has no row for the filter the game wants now (nothing was changed then)
            static bool Show(WindowBase k)
            {
                object last = sLast.GetValue(null);
                ItemGrid grid = sGrid.GetValue(k) as ItemGrid;
                if (grid == null) return false;
                List<ItemGridCellItem> items = grid.Items;
                Button sel = null;
                int at = -1;
                for (int i = 0; i < items.Count; i++)
                    if (object.Equals(items[i].mTag, last)) { sel = items[i].mWin as Button; at = i; break; }
                if (sel == null) return false;
                Button old = sSel.GetValue(k) as Button;
                if (old != sel)
                {
                    if (old != null) old.Selected = false;
                    sel.Selected = true;
                    sSel.SetValue(k, sel);
                    grid.SelectedItem = at;
                }
                sInstance.SetValue(null, k);
                sSelect.Invoke(k, new object[] { last, sel.Caption });
                k.Visible = true;
                return true;
            }

            static void Threw(Exception e)
            {
                if (sThrew) return;
                sThrew = true;
                AppliedLog().Add("mapviewkeep: threw " + (e.InnerException ?? e).Message + " (the game's own load and unload ran)");
            }
        }
    }
}
