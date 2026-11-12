using System;
using System.Reflection;
using Sims3.UI;

namespace Ts3Fixes
{
    // filtergridleak (tuning kFilterGridLeak, default off until measured; docs/545 "Buy/Build catalog").
    //
    // EA bug [verified IL, UI.dll]: CatalogProductFilter.Init takes its child ItemGrid (export id 189775728) and turns
    // DisposeOnDetach off; CatalogProductFilter has no Dispose override, so when Buy or Build mode closes
    // (BuyController.Unload / BuildController.Unload / BlueprintController.Unload: Layout.Shutdown + Dispose) UIManager.OnCustomDetach skips that grid
    // and its wrapper stays in UIManager.mCustomControlInstanceDict with a live-looking handle (the stale-entry sweeps of
    // buyiconleak / compositorleak do not see it): one ItemGrid per Buy or Build visit for the session.
    //
    // The pass: just before Unload runs, the filter grid of the controller being unloaded gets DisposeOnDetach back on,
    // so EA's own OnCustomDetach removes, unregisters and disposes it during Layout.Shutdown, as for every other custom
    // control in the tree. Nothing is disposed by hand. UI only.
    // Counters: [2281] filter grids found at Unload (also off), [2282] DisposeOnDetach restored, [2283] errors.
    public static partial class Fixes
    {
        const uint kFglGridId = 189775728u;
        static FieldInfo sFglBuyCtl, sFglBuyFilter, sFglBuildCtl, sFglBuildFilter, sFglBpCtl, sFglBpFilter;

        static void ApplyFilterGridLeak()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sFglBuyCtl = typeof(BuyController).GetField("sController", st);
            sFglBuyFilter = typeof(BuyController).GetField("mCatalogProductFilter", inst);
            sFglBuildCtl = typeof(BuildController).GetField("sController", st);
            sFglBuildFilter = typeof(BuildController).GetField("mCatalogProductFilter", inst);
            if (sFglBuyCtl != null && sFglBuyFilter != null)
                Patch(typeof(BuyController), "Unload", Type.EmptyTypes, typeof(FglHook), "BuyUnloadHook", "BuyUnloadOrig");
            else AppliedLog().Add("filtergridleak: BuyController members not found, Buy not patched");
            if (sFglBuildCtl != null && sFglBuildFilter != null)
                Patch(typeof(BuildController), "Unload", Type.EmptyTypes, typeof(FglHook), "BuildUnloadHook", "BuildUnloadOrig");
            else AppliedLog().Add("filtergridleak: BuildController members not found, Build not patched");
            sFglBpCtl = typeof(BlueprintController).GetField("sController", st);
            sFglBpFilter = typeof(BlueprintController).GetField("mCatalogProductFilter", inst);
            if (sFglBpCtl != null && sFglBpFilter != null)
                Patch(typeof(BlueprintController), "Unload", Type.EmptyTypes, typeof(FglHook), "BpUnloadHook", "BpUnloadOrig");
            else AppliedLog().Add("filtergridleak: BlueprintController members not found, Blueprint not patched");
        }

        static void FglRestore(FieldInfo ctlF, FieldInfo filterF)
        {
            int[] c = PatchCounters();
            try
            {
                object ctl = ctlF.GetValue(null);
                WindowBase filter = ctl == null ? null : filterF.GetValue(ctl) as WindowBase;
                ItemGrid g = filter == null ? null : filter.GetChildByID(kFglGridId, true) as ItemGrid;
                if (g == null || g.Disposed || g.DisposeOnDetach) return;
                c[2281]++;
                if (!ShortcutOn("filtergridleak")) return;
                g.DisposeOnDetach = true;
                c[2282]++;
            }
            catch (Exception) { c[2283]++; }
        }

        public class FglHook
        {
            public static void BuyUnloadHook() { FglRestore(sFglBuyCtl, sFglBuyFilter); BuyUnloadOrig(); }
            public static void BuyUnloadOrig() { } // receives BuyController.Unload()'s original header
            public static void BuildUnloadHook() { FglRestore(sFglBuildCtl, sFglBuildFilter); BuildUnloadOrig(); }
            public static void BuildUnloadOrig() { } // receives BuildController.Unload()'s original header
            public static void BpUnloadHook() { FglRestore(sFglBpCtl, sFglBpFilter); BpUnloadOrig(); }
            public static void BpUnloadOrig() { } // receives BlueprintController.Unload()'s original header
        }
    }
}
