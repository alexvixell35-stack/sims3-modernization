namespace Ts3Fixes
{
    // Game-independent rule of optionsnoopapply (OptionsNoopApply.cs, docs/658 §7.3). Self-check:
    // tools/selftest/optionsnoopapply. OptionsDialog.ApplyGraphicsChanges calls DeviceConfig.SetOption (and so OnOkay
    // calls UIManager.ApplyOptions, a native re-apply of the graphics options) for every field whose dialog value
    // (mNewGraphicsData) differs from the value the dialog read at open (mOldGraphicsData). A field whose dialog value
    // already equals what DeviceConfig holds now is a no-op set. Per field: kApply (a real change, EA's set runs),
    // kSkip (equal to the live config while the dialog's old copy differs: align the old copy so EA skips it),
    // kSame (dialog unchanged), kUnknown (the live value could not be read: leave EA's compare alone).
    public static class OptionsNoopApplyCore
    {
        public const int kSame = 0, kApply = 1, kSkip = 2, kUnknown = 3;

        // dialogNew / dialogOld: the dialog's values; live: DeviceConfig's value in the dialog's units (haveLive false =
        // GetOption failed).
        public static int Field(uint dialogNew, uint dialogOld, bool haveLive, uint live)
        {
            if (dialogNew == dialogOld) return kSame;
            if (!haveLive) return kUnknown;
            return dialogNew == live ? kSkip : kApply;
        }

        // the dialog's resolution pick against the live mode: skip the ChangeResolution when width, height, refresh and
        // full screen all equal what the device runs now
        public static int Mode(bool changed, uint w, uint h, uint hz, bool full, uint lw, uint lh, uint lhz, bool lfull)
        {
            if (!changed) return kSame;
            return w == lw && h == lh && hz == lhz && full == lfull ? kSkip : kApply;
        }
    }
}
