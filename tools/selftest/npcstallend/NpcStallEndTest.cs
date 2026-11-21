using System;
using Ts3Fixes;

// Offline self-check of NpcStallEndCore (docs/649).
static class NpcStallEndTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    static int D(bool exempt, double h, string def, bool busy, bool outfit, bool tried)
    {
        return NpcStallEndCore.Decide(exempt, h, 3, exempt ? null : NpcStallEndCore.Kind(def), busy, outfit, tried);
    }

    static void Main()
    {
        const int End = NpcStallEndCore.kEnd, Wait = NpcStallEndCore.kWait;
        // the stalls seen in the runs (definition full names from health.jsonl) end past the mark
        string[] stalls = {
            "Sims3.Gameplay.Objects.Toys.PlayCatchObject+PlayCatchInteraction+Definition",
            "Sims3.Gameplay.Objects.Toys.PlayCatchObject+PlayCatchPlayer2+Definition",
            "Sims3.Gameplay.Abstracts.AthleticGameObject+WorkOut+CardioDefinition",
            "Sims3.Gameplay.Abstracts.AthleticGameObject+WorkOut+StrengthDefinition",
            "Sims3.Gameplay.Objects.Miscellaneous.TrashcanIndoor+EmptyTrash+FakeDefinition",
            "Sims3.Gameplay.Core.VisitCommunityLot+Definition",
            "Sims3.Gameplay.Objects.Electronics.Stereo+DanceTogetherA+Definition",
            "Sims3.Gameplay.Socializing.SocialInteractionB+Definition",
            "Sims3.Gameplay.Objects.HobbiesSkills.ChessTable+PracticeChess+Definition",
            "NRaas.OverwatchSpace.Interactions.GetMailEx+FakeDefinition",
            "Sims3.Gameplay.Actors.Sim+SwitchToOutfit+Definition",
        };
        foreach (string s in stalls)
        {
            Check(NpcStallEndCore.Kind(s) == null, "no allowed kind: " + s);
            Check(D(false, 3.1, s, false, false, false) == End, "stall ends past 3 h: " + s);
            Check(D(false, 2.9, s, false, false, false) == Wait, "stall waits below 3 h: " + s);
        }
        // legitimate long ones the user keeps
        string[][] kept = {
            new string[] { "Sims3.Gameplay.Objects.Beds.BedSleep+Definition", "sleep" },
            new string[] { "Sims3.Gameplay.Careers.WorkInRabbitHole+Definition", "work or school" },
            new string[] { "Sims3.Gameplay.Objects.RabbitHoles.School+GoToSchoolInRabbitHole+Definition", "work or school" },
            new string[] { "Sims3.Gameplay.ActiveCareer.ActiveCareers.PerformanceCareer+PerformerPerformForTips+Definition", "work or school" },
            new string[] { "Sims3.Gameplay.Objects.HobbiesSkills.Piano+PianistPerform+Definition", "performing" },
            new string[] { "Sims3.Gameplay.Objects.Miscellaneous.VelvetRopes+WatchDoor+Definition", "service role" },
            new string[] { "Sims3.Gameplay.Objects.Counters.BarProfessional+TendBar+Definition", "service role" },
            new string[] { "Sims3.Gameplay.Objects.Fishing.Fishing+Definition", "fishing" },
            new string[] { "Sims3.Gameplay.Objects.ReadBook+Definition", "reading" },
            new string[] { "Sims3.Gameplay.Objects.HobbiesSkills.Guitar+Watch+Definition", "watching" },
            new string[] { "Sims3.Gameplay.Abstracts.ShowVenue+AutonomouslyAttendShow+Definition", "watching" },
            new string[] { "Sims3.Gameplay.Situations.ThrowRice+Definition", "watching" },
        };
        foreach (string[] k in kept)
        {
            Check(NpcStallEndCore.Kind(k[0]) == k[1], "kind " + k[1] + ": " + k[0] + " (got " + NpcStallEndCore.Kind(k[0]) + ")");
            Check(D(false, 20, k[0], false, false, false) == NpcStallEndCore.kSkipKind, "allowed kind skipped: " + k[0]);
        }
        string catchDef = stalls[0];
        // household rule: exempt always wins, at any age of the stall
        Check(D(true, 20, catchDef, false, false, false) == NpcStallEndCore.kExempt, "household / selected / unknown scope: exempt");
        Check(D(true, 20, catchDef, true, true, true) == NpcStallEndCore.kExempt, "exempt wins over every other state");
        Check(D(true, 1, catchDef, false, false, false) == Wait, "exempt below the mark: nothing counted");
        // busy (routing, rabbit hole, parented, role, baby or toddler) and outfit waits are left alone
        Check(D(false, 20, catchDef, true, false, false) == NpcStallEndCore.kSkipKind, "mid-route or busy: skipped");
        Check(D(false, 20, catchDef, false, true, false) == NpcStallEndCore.kSkipOutfit, "outfit wait: left to outfitwaitend");
        // never two cancels of one instance
        Check(D(false, 20, catchDef, false, false, true) == NpcStallEndCore.kTried, "second time: cancel did not take, no retry");
        // tuning bounds
        Check(NpcStallEndCore.Limit(0) == 3 && NpcStallEndCore.Limit(-1) == 3 && NpcStallEndCore.Limit(double.NaN) == 3 && NpcStallEndCore.Limit(5000) == 3, "bad tuning: 3 h");
        Check(NpcStallEndCore.Limit(0.2) == 1 && NpcStallEndCore.Limit(6) == 6, "tuning floor 1 h, else as set");
        Check(NpcStallEndCore.Decide(false, 5.9, 6, null, false, false, false) == Wait && NpcStallEndCore.Decide(false, 6.1, 6, null, false, false, false) == End, "custom 6 h mark");
        // progress
        Check(!NpcStallEndCore.Progress(true, 0.5f), "same head, within 1 m: no progress");
        Check(NpcStallEndCore.Progress(true, 1.5f), "moved: progress");
        Check(NpcStallEndCore.Progress(false, 0f), "new head: progress");
        Check(NpcStallEndCore.Kind(null) == null && NpcStallEndCore.Kind("") == null, "no name: no kind");
        // cleared outfit waits (cause 3 in OutfitWaitCore): only a live off-screen NPC past the mark, outside ChangeOutfit
        const int kCleared = 3, kOwn = 1;
        Check(NpcStallEndCore.EndClearedWait(true, 100, 50, true, false, kCleared, kCleared, false), "cleared wait ends");
        Check(!NpcStallEndCore.EndClearedWait(false, 100, 50, true, false, kCleared, kCleared, false), "switch off: kept");
        Check(!NpcStallEndCore.EndClearedWait(true, 40, 50, true, false, kCleared, kCleared, false), "before the mark: kept");
        Check(!NpcStallEndCore.EndClearedWait(true, 100, 50, false, false, kCleared, kCleared, false), "player Sim: kept");
        Check(!NpcStallEndCore.EndClearedWait(true, 100, 50, true, true, kCleared, kCleared, false), "on screen: kept");
        Check(!NpcStallEndCore.EndClearedWait(true, 100, 50, true, false, kOwn, kCleared, false), "own cause: outfitwaitend's, not this one");
        Check(!NpcStallEndCore.EndClearedWait(true, 100, 50, true, false, kCleared, kCleared, true), "inside ChangeOutfit: kept");
        // exhaustive: an exempt Sim is never ended, whatever the other inputs
        int ends = 0;
        for (int m = 0; m < 64; m++)
            if (NpcStallEndCore.Decide(true, (m & 1) != 0 ? 20 : 1, (m & 2) != 0 ? 0 : 3, (m & 4) != 0 ? "sleep" : null, (m & 8) != 0, (m & 16) != 0, (m & 32) != 0) == End) ends++;
        Check(ends == 0, "exempt never ended (" + ends + ")");
        Check(NpcStallEndCore.Name(End) == "ended" && NpcStallEndCore.Name(NpcStallEndCore.kTried) == "cancel did not take", "names");
        Console.WriteLine(sFails == 0 ? "npcstallend selftest: ok" : "npcstallend selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
