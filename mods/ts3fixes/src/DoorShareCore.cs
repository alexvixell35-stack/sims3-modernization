namespace Ts3Fixes
{
    // Game-independent part of doorshare (DoorShare.cs, docs/674): the note hash and class bits shared with ts3native
    // (native/ts3native/doorshare-core.h: DsNoteHash, DsSimFold, DS_C_*, DS_KIND_*) and the two rules. Self-check:
    // tools/selftest/doorshare (the same test vectors as the native test).
    public static class DoorShareCore
    {
        public const int kNone = 0, kDoor = 1, kInert = 2;             // DS_KIND_*
        public const int kOrdinary = 1, kCommand = 2, kSimShift = 3;   // DS_C_*

        static uint Mix(uint h, uint v) { unchecked { return (h ^ v) * 0x01000193u; } }

        // names one door ask: portal object id, routing Sim id, the two rooms as the 16 bits the planner passes. Never 0.
        public static uint NoteHash(ulong portal, ulong sim, int from, int to)
        {
            unchecked
            {
                uint h = 0x811C9DC5u;
                h = Mix(h, (uint)portal); h = Mix(h, (uint)(portal >> 32));
                h = Mix(h, (uint)sim); h = Mix(h, (uint)(sim >> 32));
                h = Mix(h, (uint)from & 0xFFFFu); h = Mix(h, (uint)to & 0xFFFFu);
                return h != 0 ? h : 1u;
            }
        }

        public static uint SimFold(ulong sim)
        {
            unchecked
            {
                uint x = (uint)sim ^ (uint)(sim >> 32);
                return (x ^ (x >> 13) ^ (x >> 26)) & 0x1FFFu;
            }
        }

        // op 40's 16 bits; a Sim outside the class sends 0 (ts3native then never uses a shared answer for the route)
        public static int ClassBits(bool ordinary, bool command, ulong sim)
        {
            if (!ordinary) return 0;
            return kOrdinary | (command ? kCommand : 0) | (int)(SimFold(sim) << kSimShift);
        }

        // The ordinary class: every Sim fact Sim.OnPortalPathPlan and CommonDoor.IsAllowedThrough read is at the value that
        // leaves the answer to the door and the lot. human: the horse and pet rules do not apply; no tomb mummy (always
        // locked); no proprietor role (unlocks); no service (some ignore locks); no stray; no IgnoreDoorLocks flag.
        public static bool Ordinary(bool human, bool tombMummy, bool proprietor, bool hasService, bool stray, bool ignoreLocks)
        {
            return human && !tombMummy && !proprietor && !hasService && !stray && !ignoreLocks;
        }

        // What an "unlocked" answer for this portal object depends on once the Sim is ordinary, the lot runs the game's
        // handler alone and event 59 has the game's callback alone:
        //  - special (firefighter pole, box stall, horse jump, elevator doors, diving board, ladder): their own rules read
        //    age, posture, traits or the running interaction: not shareable
        //  - a Door whose IsAllowedThrough is CommonDoor's, lock type Anybody: hidden rooms and the one-way room only
        //  - a Door with any other lock type or its own rule (single-gender door, turnstile): the Sim matters
        //  - anything else: no rule of the handler names it; the answer is the planner's initialLocked
        public static int Kind(bool lotPure, bool worldPure, bool special, bool isDoor, bool commonRule, bool lockAnybody)
        {
            if (!lotPure || !worldPure || special) return kNone;
            if (!isDoor) return kInert;
            return commonRule && lockAnybody ? kDoor : kNone;
        }
    }
}
