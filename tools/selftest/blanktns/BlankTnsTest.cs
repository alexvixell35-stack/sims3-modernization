using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/BlankTnsCore.cs (docs/869): the blank-text test and the frame filter.
static class BlankTnsTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        Check(BtnCore.IsBlank(null), "null text is blank");
        Check(BtnCore.IsBlank(""), "empty text is blank");
        Check(BtnCore.IsBlank(" \n\t"), "whitespace text is blank");
        Check(!BtnCore.IsBlank("x"), "text is not blank");
        Check(!BtnCore.IsBlank(" x "), "text with spaces is not blank");

        // a mod's notice through Sim.ShowTNSIfSelectable, innermost first (our hook frame as either type the swap may report)
        string[] types = { "Ts3Fixes.Fixes+BtnHook", "Sims3.UI.NotificationManager", "Sims3.UI.StyledNotification", "Sims3.UI.StyledNotification",
            "Sims3.Gameplay.Actors.Sim", "Sims3.Gameplay.Utilities.NotificationSystem", null, "NRaas.StoryProgressionSpace.Scenarios+Foo",
            "Sims3.Gameplay.Actors.Sim", "Sims3.Gameplay.Interactions.InteractionInstance", "Sims3.Gameplay.Interactions.Later" };
        string[] methods = { "ShouldHook", "ShouldAddNotification", "Show", "Show", "ShowTNSIfSelectable", "Show", "Lost", "Run", "Reset", "RunInteraction", "Never" };
        string[] asms = { "Ts3Fixes", "UI", "UI", "UI", "Sims3GameplaySystems", "Sims3GameplaySystems", "?", "NRaasStoryProgression",
            "Sims3GameplaySystems", "Sims3GameplaySystems", "Sims3GameplaySystems" };
        Check(BtnCore.Text(BtnCore.Pick(types, methods, null, 3)) == "[blank notice] sent by NRaas.StoryProgressionSpace.Scenarios+Foo.Run < Sims3.Gameplay.Actors.Sim.Reset < Sims3.Gameplay.Interactions.InteractionInstance.RunInteraction",
            "first 3 frames outside the plumbing, full type names, Sim methods other than ShowTNS* kept");
        List<string> log = BtnCore.Pick(types, methods, asms, 8);
        Check(log.Count == 4 && log[0] == "NRaasStoryProgression:NRaas.StoryProgressionSpace.Scenarios+Foo.Run", "log frames carry the assembly, up to n");

        Check(BtnCore.IsPlumbing("Sims3.UI.StyledNotification+Format", ".ctor"), "StyledNotification nested types are plumbing");
        Check(!BtnCore.IsPlumbing("Sims3.UI.StyledNotificationFake", "Show"), "a type that only starts with the name is not");
        Check(BtnCore.IsPlumbing("Sims3.UI.NotificationManager", "ShouldAddNotification") && BtnCore.IsPlumbing("Sims3.UI.NotificationManager", "Add")
            && !BtnCore.IsPlumbing("Sims3.UI.NotificationManagerX", "Add"), "NotificationManager (Add and the hooked ShouldAddNotification) is plumbing");
        Check(BtnCore.IsPlumbing("Sims3.Gameplay.Actors.Sim", "ShowTNSAndPlayStingIfSelectable"), "Sim.ShowTNS* helpers are plumbing");
        Check(!BtnCore.IsPlumbing("Sims3.Gameplay.Actors.Sim", "ShowBalloon"), "other Sim methods are senders");
        Check(BtnCore.IsPlumbing("Ts3Fixes.Fixes", "BtnNote") && !BtnCore.IsPlumbing("Ts3Fixes.Fixes", "BlankTnsCheck"), "our Btn* helpers are plumbing, the check is a sender");
        Check(!BtnCore.IsPlumbing("Mod.Sim", "ShowTNSIfSelectable"), "another Sim type is not plumbing");
        Check(BtnCore.IsPlumbing(null, null), "a frame with no type is skipped");
        Check(BtnCore.Text(new List<string>()) == "[blank notice] sent by ?", "no sender frame");

        Console.WriteLine(sFail == 0 ? "blanktns selftest: all passed" : "blanktns selftest: " + sFail + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
