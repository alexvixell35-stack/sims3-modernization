using System;
using Ts3Fixes;

// Offline self-check of OptionsNoopApplyCore (docs/658 §7.3).
static class OptionsNoopApplyTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(OptionsNoopApplyCore.Field(2, 2, true, 1) == OptionsNoopApplyCore.kSame, "dialog unchanged: EA sets nothing");
        Check(OptionsNoopApplyCore.Field(2, 1, true, 2) == OptionsNoopApplyCore.kSkip, "dialog equals the live config: no-op set");
        Check(OptionsNoopApplyCore.Field(3, 1, true, 2) == OptionsNoopApplyCore.kApply, "a real change: EA applies it");
        Check(OptionsNoopApplyCore.Field(3, 1, false, 3) == OptionsNoopApplyCore.kUnknown, "live unreadable: EA's compare kept");
        Check(OptionsNoopApplyCore.Mode(false, 1, 1, 60, true, 2, 2, 50, false) == OptionsNoopApplyCore.kSame, "mode unchanged");
        Check(OptionsNoopApplyCore.Mode(true, 1920, 1080, 60, true, 1920, 1080, 60, true) == OptionsNoopApplyCore.kSkip, "same mode by another index: skip ChangeResolution");
        Check(OptionsNoopApplyCore.Mode(true, 1920, 1080, 60, true, 1920, 1080, 144, true) == OptionsNoopApplyCore.kApply, "another refresh: real");
        Check(OptionsNoopApplyCore.Mode(true, 1920, 1080, 60, false, 1920, 1080, 60, true) == OptionsNoopApplyCore.kApply, "window / full screen: real");
        Console.WriteLine(sFails == 0 ? "optionsnoopapply selftest: ok" : "optionsnoopapply selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
