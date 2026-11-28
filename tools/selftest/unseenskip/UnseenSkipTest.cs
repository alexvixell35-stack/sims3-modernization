using System;
using Ts3Fixes;

// Offline checks of UnseenSkipCore (rhskipunseen, bookskipunseen, gymtwin, lineswitchpc, slidehang, docs/686 §37).
static class UnseenSkipTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int Go = UnseenSkipCore.kGo;
        // Skip(npcOn, hhUnseenOn, plain, npc, selected, unseen)
        Check(UnseenSkipCore.Skip(true, false, true, true, false, true) == Go, "unseen NPC: skips");
        Check(UnseenSkipCore.Skip(true, false, true, true, false, false) == UnseenSkipCore.kSeen, "NPC in view: EA's wait");
        Check(UnseenSkipCore.Skip(false, true, true, true, false, true) == UnseenSkipCore.kWould, "NPC switch off: would (the hh opt-in does not act for NPCs)");
        Check(UnseenSkipCore.Skip(true, false, true, false, false, true) == UnseenSkipCore.kWould, "unseen household without its opt-in: would, nothing done");
        Check(UnseenSkipCore.Skip(true, false, true, false, false, false) == UnseenSkipCore.kExempt, "household in view without its opt-in: exempt");
        Check(UnseenSkipCore.Skip(false, true, true, false, false, true) == Go, "unseen household with its opt-in: skips");
        Check(UnseenSkipCore.Skip(true, true, true, false, false, false) == UnseenSkipCore.kSeen, "household in view with its opt-in: EA's wait");
        Check(UnseenSkipCore.Skip(true, true, true, false, true, true) == UnseenSkipCore.kExempt, "the selected Sim never counts as unseen");
        Check(UnseenSkipCore.Skip(true, true, false, true, false, true) == UnseenSkipCore.kNone, "horse, rider, carrier, toddler: EA's wait");

        // lineswitch groups
        Check(UnseenSkipCore.InScope(UnseenSkipCore.kGroupGym, true, false) && !UnseenSkipCore.InScope(UnseenSkipCore.kGroupGym, false, true), "gymtwin: NPCs only, lineswitchhh does not widen it");
        Check(UnseenSkipCore.InScope(UnseenSkipCore.kGroupPc, false, true) && !UnseenSkipCore.InScope(UnseenSkipCore.kGroupPc, false, false), "lineswitchpc: household with lineswitchhh only");
        Check(UnseenSkipCore.InScope(UnseenSkipCore.kGroupBath, true, false), "bath: NPCs");
        Check(UnseenSkipCore.SwitchAfter(UnseenSkipCore.kGroupGym, true, 2f) == 0f, "gymtwin: unseen at the start switches at the first check");
        Check(UnseenSkipCore.SwitchAfter(UnseenSkipCore.kGroupGym, false, 2f) == 2f, "gymtwin: seen waits lineswitch's time");
        Check(UnseenSkipCore.SwitchAfter(UnseenSkipCore.kGroupPc, true, 2f) == 2f, "lineswitchpc: no unseen shortcut, lineswitch's time");
        Check(UnseenSkipCore.SwitchAfter(UnseenSkipCore.kGroupBath, false, 99f) == LineSwitchCore.kMaxMinutes, "tuned time clamped");

        // Slide(on, hhOn, waiters, npc, autonomous, selected, heldMin, capMin)
        Check(UnseenSkipCore.Slide(true, false, true, true, true, false, 20f, 20f) == Go, "NPC head past HangOutTimeMax: ends");
        Check(UnseenSkipCore.Slide(true, false, true, true, true, false, 19.9f, 20f) == UnseenSkipCore.kWait, "under the cap: waits");
        Check(UnseenSkipCore.Slide(true, true, false, true, true, false, 90f, 20f) == UnseenSkipCore.kNone, "nobody waiting: the hang-out runs on");
        Check(UnseenSkipCore.Slide(false, true, true, true, true, false, 90f, 20f) == UnseenSkipCore.kWould, "off: would");
        Check(UnseenSkipCore.Slide(true, false, true, false, true, false, 90f, 20f) == UnseenSkipCore.kKeep, "household head without slidehanghh: kept");
        Check(UnseenSkipCore.Slide(true, true, true, false, true, false, 90f, 20f) == Go, "household head with slidehanghh: ends");
        Check(UnseenSkipCore.Slide(true, true, true, false, false, false, 90f, 20f) == UnseenSkipCore.kKeep, "player's order: kept");
        Check(UnseenSkipCore.Slide(true, true, true, false, true, true, 90f, 20f) == UnseenSkipCore.kKeep, "selected head: kept");
        Check(UnseenSkipCore.Slide(true, false, true, true, true, false, 20f, 0f) == Go && UnseenSkipCore.Slide(true, false, true, true, true, false, 19f, float.NaN) == UnseenSkipCore.kWait, "bad tuning: 20 sim-min");

        Check(UnseenSkipCore.View(true) == "unseen" && UnseenSkipCore.View(false) == "seen", "view names");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
