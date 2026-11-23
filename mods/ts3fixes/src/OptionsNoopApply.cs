using System;
using System.Reflection;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // optionsnoopapply (tuning kOptionsNoopApply, default off; docs/658 §7.3). Player lead: the CAS body tint appeared
    // after opening the Options menu in CAS and leaving it. OK runs OptionsDialog.ApplyGraphicsChanges, which calls
    // DeviceConfig.SetOption for each graphics field whose dialog value differs from the copy read at open, then
    // UIManager.ApplyOptions (native re-apply) when any was set, and UIManager.ChangeResolution when the resolution /
    // refresh / full-screen pick differs. [verified, decompiled] every set is guarded by new != old; no field is set when
    // the copies match. [inferred, not shown] a copy can differ with no player change only through a UI event that
    // writes mNewGraphicsData after the dialog's copy (SetupRefreshMenu sets the refresh menu's CurrentSelection without
    // the unsubscribe EA puts around the resolution menu's). This hook records that: before EA's ApplyGraphicsChanges it
    // compares each differing field with the live DeviceConfig value (OptionsNoopApplyCore) and logs it, switch on or
    // off ("optionsnoopapply: ..."). With the switch on a field equal to the live value (a no-op set) gets the dialog's
    // old copy aligned, so EA skips that SetOption (and ApplyOptions when nothing else changed); the same for a
    // resolution pick equal to the live mode. A real change is applied by EA as before. Counters [4218] no-op sets
    // skipped (or seen with the switch off, in the log), [4219] errors.
    public static partial class Fixes
    {
        static readonly LineCapCore sOnaCap = new LineCapCore(20, 60000, 200);
        const BindingFlags kOnaI = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static FieldInfo sOnaNew, sOnaOld, sOnaList;

        static void ApplyOptionsNoopApply()
        {
            sOnaNew = typeof(OptionsDialog).GetField("mNewGraphicsData", kOnaI);
            sOnaOld = typeof(OptionsDialog).GetField("mOldGraphicsData", kOnaI);
            sOnaList = typeof(OptionsDialog).GetField("mResolutionList", kOnaI);
            if (sOnaNew == null || sOnaOld == null) { AppliedLog().Add("optionsnoopapply: OptionsDialog graphics data not found"); return; }
            Patch(typeof(OptionsDialog), "ApplyGraphicsChanges", Type.EmptyTypes, typeof(OnaHook), "ApplyHook", "NoopApplyOrig");
        }

        // dialog field, DeviceConfig key, offset (the dialog keeps value - 1 for these keys), bool field
        static readonly string[] kOnaField = { "mGraphicEffects", "mEdgeSmoothing", "mTextureDetail", "mLightingDetail", "mSimDetail", "mTreeDetail",
            "mDrawDistance", "mHighDetailLots", "mReflectionQuality", "mEnableObjectHiding", "mEnableAnimationSmoothing", "mEnabledAdvancedRendering" };
        static readonly string[] kOnaKey = { "VisualEffects", "EdgeSmoothing", "TextureQuality", "LightingQuality", "SimQuality", "TreeQuality",
            "DrawDistance", "MaxActiveLots", "GeneralReflections", "ObjectHiding", "AnimationSmoothing", "AdvancedRendering" };
        static readonly uint[] kOnaOffset = { 1, 0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };

        static uint OnaU(object v) { return v is bool ? ((bool)v ? 1u : 0u) : Convert.ToUInt32(v); }

        static void OnaLog(string s) { if (sOnaCap.Take(System.Environment.TickCount)) DiagLine("optionsnoopapply: " + s); }

        static void OnaBefore(object dialog)
        {
            int[] c = PatchCounters();
            bool on = ShortcutOn("optionsnoopapply");
            object nb = sOnaNew.GetValue(dialog), ob = sOnaOld.GetValue(dialog);  // boxed copies of the private struct
            Type gt = nb.GetType();
            bool aligned = false;
            for (int i = 0; i < kOnaField.Length; i++)
            {
                FieldInfo f = gt.GetField(kOnaField[i], kOnaI);
                if (f == null) continue;
                uint n = OnaU(f.GetValue(nb)), o = OnaU(f.GetValue(ob)), raw;
                bool have = DeviceConfig.GetOption(kOnaKey[i], out raw);
                uint live = raw - kOnaOffset[i];
                if (f.FieldType == typeof(bool)) live = raw != 0 ? 1u : 0u;
                int v = OptionsNoopApplyCore.Field(n, o, have, live);
                if (v == OptionsNoopApplyCore.kSame) continue;
                string what = kOnaKey[i] + " dialog " + n + " old " + o + " live " + (have ? live.ToString() : "?");
                if (v == OptionsNoopApplyCore.kSkip)
                {
                    c[4218]++;
                    if (on) { f.SetValue(ob, f.GetValue(nb)); aligned = true; }
                    OnaLog(what + ": no-op set" + (on ? ", skipped" : ", EA sets it (switch off)"));
                }
                else OnaLog(what + (v == OptionsNoopApplyCore.kApply ? ": real change, EA applies it" : ": live value unreadable, EA's compare kept"));
            }
            // resolution / refresh / full screen (OnOkay's ChangeResolution test)
            FieldInfo fr = gt.GetField("mResolution", kOnaI), fz = gt.GetField("mRefresh", kOnaI), ff = gt.GetField("mFullScreen", kOnaI);
            Resolution[] list = sOnaList == null ? null : sOnaList.GetValue(dialog) as Resolution[];
            if (fr != null && fz != null && ff != null && list != null)
            {
                uint nr = OnaU(fr.GetValue(nb)), nz = OnaU(fz.GetValue(nb)), or = OnaU(fr.GetValue(ob)), oz = OnaU(fz.GetValue(ob));
                bool nf = (bool)ff.GetValue(nb), of = (bool)ff.GetValue(ob);
                bool changed = nr != or || nz != oz || nf != of;
                if (changed && nr + nz < list.Length)
                {
                    bool lf;
                    Resolution live = DeviceConfig.GetCurrentResolution(out lf), want = list[nr + nz];
                    int v = OptionsNoopApplyCore.Mode(true, want.mWidth, want.mHeight, want.mRefresh, nf, live.mWidth, live.mHeight, live.mRefresh, lf);
                    string what = "mode dialog " + want.mWidth + "x" + want.mHeight + "@" + want.mRefresh + (nf ? " full" : " window") + " (index " + nr + "+" + nz
                        + ", old " + or + "+" + oz + ") live " + live.mWidth + "x" + live.mHeight + "@" + live.mRefresh + (lf ? " full" : " window");
                    if (v == OptionsNoopApplyCore.kSkip)
                    {
                        c[4218]++;
                        if (on) { fr.SetValue(ob, nr); fz.SetValue(ob, nz); ff.SetValue(ob, nf); aligned = true; }
                        OnaLog(what + ": same mode" + (on ? ", ChangeResolution skipped" : ", EA changes it anyway (switch off)"));
                    }
                    else OnaLog(what + ": real change, EA applies it");
                }
            }
            if (aligned) sOnaOld.SetValue(dialog, ob);
        }

        public class OnaHook
        {
            public void ApplyHook()
            {
                try { OnaBefore(this); }
                catch (Exception ex) { PatchCounters()[4219]++; OnaLog("error " + ex.GetType().Name); }
                NoopApplyOrig();
            }
            public void NoopApplyOrig() { } // receives OptionsDialog.ApplyGraphicsChanges
        }
    }
}
