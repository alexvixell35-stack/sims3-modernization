using Sims3.Gameplay.Abstracts;

namespace Ts3Fixes
{
    // Harness checks for ResidentSpawnSlot.cs and Batch582dLogic.cs (docs/582 B5 §8-§10). Pure decisions with controls.
    public static partial class Fixes
    {
        // ResidentSpawnSlotCheck run: the selected Sim's plan failed only at a door locked to the slot's own household ->
        // the resident's slot counts as routable. Controls: no locked door (no path) stays a failure; a Nobody lock, a
        // hidden-room door and a door with its own rules stay failures; a passed check stays passed.
        public static object ResidentSpawnSlotCheck(string what)
        {
            string counters = B582Counters("residentspawnslot", 2882, new[] { "slots accepted for the resident", "would accept with switch off", "failures kept", "errors" }, sRsMissing);
            if (what == "result") return counters;
            if (what != "run") return "residentspawnslot <run|result>";
            bool on = ShortcutOn("residentspawnslot");
            bool passHh = RsPasses(CommonDoor.tLock.SelectedHousehold, 0, false, false, false, true, false, false);
            bool passList = RsPasses(CommonDoor.tLock.OwnerList, 0, false, false, false, false, true, false);
            bool fixedNow = RsDecide(on, false, 1, passHh) && RsDecide(on, false, 2, passHh && passList);
            bool ctlNoPath = !RsDecide(on, false, 0, true);
            bool ctlNobody = !RsPasses(CommonDoor.tLock.Nobody, 0, false, false, true, true, true, false);
            bool ctlHidden = !RsPasses(CommonDoor.tLock.Anybody, 0, true, false, true, true, true, false);
            bool ctlOwn = !RsPasses(CommonDoor.tLock.Anybody, 1, false, false, true, true, true, false);
            bool ctlOther = !RsPasses(CommonDoor.tLock.SelectedHousehold, 0, false, false, false, false, false, false);
            bool ctlOk = RsDecide(on, true, 0, false);
            bool controls = ctlNoPath && ctlNobody && ctlHidden && ctlOwn && ctlOther && ctlOk;
            return B582Line("residentspawnslot", on, "slot behind the resident's own household lock accepted", fixedNow,
                "no path stays failed=" + ctlNoPath + ", Nobody lock=" + ctlNobody + ", hidden room=" + ctlHidden + ", own-rule door=" + ctlOwn
                + ", other household's lock=" + ctlOther + ", passed stays passed=" + ctlOk, controls, sRsMissing, counters);
        }

        // TalkOccupationChanceCheck run: 3 occupations left -> Unemployment chance 0.25. Controls: none left -> 1 (EA right).
        public static object TalkOccupationChanceCheck(string what)
        {
            string counters = B582Counters("talkoccupationchance", 2886, new[] { "menus built by the copy", "Unemployment offered through the fixed chance", "errors" }, sTaoMissing);
            if (what == "result") return counters;
            if (what != "run") return "talkoccupationchance <run|result>";
            bool on = ShortcutOn("talkoccupationchance");
            bool fixedNow = TaoChance(on, 3) == 0.25f && TaoChance(on, 1) == 0.5f && (!on || !TaoRoomForBest(true, Sims3.Gameplay.CelebritySystem.TalkAboutOccupation.kMaximumNumberOfBestChoiceOccupationsInPieMenu));
            bool controls = TaoChance(on, 0) == 1f && TaoRoomForBest(true, 0) && TaoRoomForBest(false, Sims3.Gameplay.CelebritySystem.TalkAboutOccupation.kMaximumNumberOfBestChoiceOccupationsInPieMenu);
            return B582Line("talkoccupationchance", on, "3 left -> 0.25, 1 left -> 0.5", fixedNow, "none left -> 1=" + controls, controls, sTaoMissing, counters);
        }

        // ShellDoorSummonCheck run: a refused teleport leaves the exit unqueued. Control: a queued teleport queues the exit.
        public static object ShellDoorSummonCheck(string what)
        {
            string counters = B582Counters("shelldoorsummon", 2889, new[] { "summons run by the copy", "teleport refused, exit not queued", "errors" }, sSdMissing);
            if (what == "result") return counters;
            if (what != "run") return "shelldoorsummon <run|result>";
            bool on = ShortcutOn("shelldoorsummon");
            bool fixedNow = !SdQueueExit(on, false), controls = SdQueueExit(on, true);
            return B582Line("shelldoorsummon", on, "refused teleport -> exit not queued", fixedNow, "queued teleport queues the exit=" + controls, controls, sSdMissing, counters);
        }
    }
}
