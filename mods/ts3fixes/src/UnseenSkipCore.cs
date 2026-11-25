namespace Ts3Fixes
{
    // Game-independent rules of the "skip when nobody sees it" shortcuts (UnseenSkip.cs, docs/686 §37): rhskipunseen /
    // rhskiphhunseen, bookskipunseen / bookskiphhunseen, gymtwin, lineswitchpc, servingsharehhunseen, slidehang /
    // slidehanghh. Offline selftest: tools/selftest/unseenskip.
    //
    // Household rule (CLAUDE.md): household Sims never count as unseen. Each `...hhunseen` switch is an explicit player
    // opt-in, default off, that lifts that rule for its own case only; the selected Sim never counts as unseen.
    public static class UnseenSkipCore
    {
        public const int kNone = 0, kGo = 1, kWould = 2, kSeen = 3, kExempt = 4;

        // Who may skip unseen. npc: not a household or selected Sim. npcOn: the NPC switch; hhUnseenOn: the household
        // opt-in. unseen: the Sim and the object it waits for are off screen and away from every viewer, decided once at
        // the start of the wait. plain: a human child or older on foot, carrying nobody (others keep EA's wait).
        // kGo: skip; kSeen: in scope but seen (EA's wait); kExempt: household without its opt-in, or the selected Sim;
        // kWould: unseen, but the switch for this Sim is off (compare mode).
        public static int Skip(bool npcOn, bool hhUnseenOn, bool plain, bool npc, bool selected, bool unseen)
        {
            if (!plain) return kNone;
            if (npc) return !unseen ? kSeen : npcOn ? kGo : kWould;
            if (selected) return kExempt;
            if (!unseen) return hhUnseenOn ? kSeen : kExempt;
            return hhUnseenOn ? kGo : kWould;
        }

        // ---- lineswitch kinds ----

        // The object groups of LineSwitch.cs: bathroom objects (lineswitch), computers / laptops / bookshelves
        // (lineswitchpc), workout benches / treadmills (gymtwin). kGroupNone: a line lineswitch leaves alone.
        public const int kGroupNone = -1, kGroupBath = 0, kGroupPc = 1, kGroupGym = 2;

        // gymtwin is for NPCs only (no household switch). The other groups: NPCs, household with lineswitchhh.
        public static bool InScope(int group, bool npc, bool hhOn)
        {
            if (group == kGroupGym) return npc;
            return npc || hhOn;
        }

        // Sim-minutes in the line before a free twin is taken. gymtwin: an NPC that was unseen when its wait began takes
        // it at the first check; the others wait the tuned time (LineSwitchCore.After).
        public static float SwitchAfter(int group, bool unseenAtStart, float tuned)
        {
            if (group == kGroupGym && unseenAtStart) return 0f;
            return LineSwitchCore.After(tuned);
        }

        // ---- slidehang ----

        // A jungle gym's ladder line: the head took the turn with HangOutOn (EA's hang-out never reads the
        // HangOutTimeMin/Max tuning that PlayOn ends by) and others wait behind it.
        //   heldMin: sim-minutes since the head took the turn; capMin: the gym's HangOutTimeMax.
        //   npc / hhOn: the head's scope (slidehang: NPC heads; slidehanghh: household heads too).
        //   autonomous / selected: a household head on a player's order, or the selected Sim, keeps its hang-out.
        public const int kWait = 5, kKeep = 6;
        public static int Slide(bool on, bool hhOn, bool waiters, bool npc, bool autonomous, bool selected, float heldMin, float capMin)
        {
            if (!waiters) return kNone;
            if (!(capMin > 0f)) capMin = 20f;
            if (!(heldMin >= capMin)) return kWait;
            if (!on) return kWould;
            if (npc) return kGo;
            if (!hhOn || selected || !autonomous) return kKeep;
            return kGo;
        }

        // ---- linewaitlog view ----

        public static string View(bool unseen) { return unseen ? "unseen" : "seen"; }
    }
}
