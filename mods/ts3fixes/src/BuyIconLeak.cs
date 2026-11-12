using System;
using System.Collections;
using System.Reflection;
using Sims3.UI;

namespace Ts3Fixes
{
    // buyiconleak (tuning kBuyIconLeak, default off until measured; docs/191 "Buy mode catalogue icons", corrects docs/193 L1).
    //
    // EA bug: BuyController.AddGridItem turns DisposeOnDetach off on every catalogue cell's CustomContentIcon (window id 35)
    // because the controller recycles cells (mBuyCatalogGridItemCache / mBuildCatalogPatternGridItemCache), and
    // BuyController.Dispose (leaving Buy/Build mode) then calls WindowBase.Dispose() on every cached icon and every icon
    // still in mCatalogGrid [verified IL]. WindowBase.Dispose destroys the native window and returns the wrapper to the
    // pool but never removes it from the static UIManager.mCustomControlInstanceDict (only UIManager.OnCustomDetach does,
    // and only for DisposeOnDetach windows), so every icon of every Buy session stays registered, with its events in
    // mEventRegistry, for the whole game session. Measured 2026-11-11 (Wine, phase-20261111-1434): +1,122
    // CustomContentIcon entries per 5 min Buy phase, again in a second phase (2,244), all off the tree. A first version
    // of this pass hooked ShowCatalogGrid(false) and did not change the count (the hide path is not where they leak).
    //
    // The pass: after BuyController.Dispose returns, every entry of mCustomControlInstanceDict whose window is already
    // disposed, or whose wrapper now carries a different handle than its key (a pooled wrapper reused for another
    // window), is removed: the part of OnCustomDetach that Dispose skipped. (DeRegisterAllEvents is called as EA does, but a
    // disposed wrapper already has handle 0, so its mEventRegistry record under the old handle stays: that handle may
    // already belong to a new window, so it is not touched here; docs/191 follow-up.)
    // Live windows are not touched. UI only: no Sims, no save data. Counters: [2217] Buy/Build exits seen, [2218]
    // stale entries removed, [2219] errors.
    public static partial class Fixes
    {
        static FieldInfo sBilDict;
        static MethodInfo sBilDeregister;
        static PropertyInfo sBilHandle, sBilDisposed;

        static void ApplyBuyIconLeak()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sBilDict = typeof(UIManager).GetField("mCustomControlInstanceDict", st);
            sBilDeregister = typeof(UIManager).GetMethod("DeRegisterAllEvents", st, null, new Type[] { typeof(WindowBase) }, null);
            sBilHandle = typeof(WindowBase).GetProperty("WinHandle", inst);
            sBilDisposed = typeof(WindowBase).GetProperty("Disposed", inst);
            if (sBilDict == null || sBilDeregister == null || sBilHandle == null || sBilDisposed == null)
            {
                AppliedLog().Add("buyiconleak: members not found, not patched");
                return;
            }
            Patch(typeof(BuyController), "Dispose", Type.EmptyTypes, typeof(BilHook), "DisposeHook", "DisposeOrig");
        }

        // Removes stale wrappers from UIManager.mCustomControlInstanceDict (disposed, or re-pooled under another handle).
        internal static int SweepStaleCustomControls(int[] c, int errSlot)
        {
            int removed = 0;
            try
            {
                IDictionary dict = sBilDict.GetValue(null) as IDictionary;
                if (dict == null) return 0;
                ArrayList stale = new ArrayList();
                foreach (DictionaryEntry e in dict)
                {
                    WindowBase w = e.Value as WindowBase;
                    if (w == null) continue;
                    bool gone = (bool)sBilDisposed.GetValue(w, null);
                    if (!gone && Convert.ToUInt32(sBilHandle.GetValue(w, null)) != Convert.ToUInt32(e.Key)) gone = true;
                    if (gone) stale.Add(e);
                }
                foreach (DictionaryEntry e in stale)
                {
                    dict.Remove(e.Key);
                    try { sBilDeregister.Invoke(null, new object[] { e.Value }); } catch (Exception) { c[errSlot]++; }
                    removed++;
                }
            }
            catch (Exception) { c[errSlot]++; }
            return removed;
        }

        public class BilHook
        {
            public void DisposeHook()
            {
                BcrSweepOtherGrids(this);  // buygridsweep (BuyCellRecycle.cs): before EA's Dispose, while the grids live
                DisposeOrig();
                BcrClearTracked();
                int[] c = PatchCounters();
                c[2217]++;
                if (ShortcutOn("buyiconleak")) c[2218] += SweepStaleCustomControls(c, 2219);
            }
            public void DisposeOrig() { } // receives BuyController.Dispose()'s original header
        }
    }
}
