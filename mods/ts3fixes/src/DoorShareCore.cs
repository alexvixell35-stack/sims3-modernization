namespace Ts3Fixes
{
    // Game-independent part of doorshare (DoorShare.cs, docs/674): the note hash and class bits shared with ts3native
    // (native/ts3native/doorshare-core.h: DsNoteHash, DsSimFold, DS_C_*, DS_KIND_*) and the two rules. Self-check:
    // tools/selftest/doorshare (the same test vectors as the native test).
    public static class DoorShareCore
    {
        public const int kNone = 0, kDoor = 1, kInert = 2, kNoActorOnly = 3;        // DS_KIND_*
        public const int kOrdinary = 1, kCommand = 2, kNoActor = 4, kSimShift = 3;  // DS_C_*
        // why a follower is in no class (op 40 carries it in the fold's place, for ts3native's count): DS_WHY_*
        public const int kWhyNoDesc = 1, kWhyHorse = 2, kWhyStray = 3, kWhyMummy = 4, kWhyProprietor = 5, kWhyService = 6, kWhyIgnoreLocks = 7;
        // which input a doorgen bump reports (op 36 hi): DS_SRC_*. ts3native's table (DsSrcScope) says what each one clears
        public const int kSrcUnknown = 0, kSrcLock = 1, kSrcOwnerList = 2, kSrcLotHandler = 3, kSrcHiddenRooms = 4, kSrcGreet = 5, kSrcHousehold = 6,
            kSrcFire = 7, kSrcFireBuff = 8, kSrcIgnoreLocks = 9, kSrcOccult = 10, kSrcRole = 11, kSrcResort = 12, kSrcDesc = 13;
        // UserDiag op 36 hi = kSrcOpBase + source (ts3native DS_SRC_OP_BASE); hi 1 / 2 are DoorGen.cs' lot hold / free
        public const int kSrcOpBase = 16;

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

        // names a door (object id) or a lot (lot id) in a doorgen bump; ts3native indexes its stamps with it. Never 0 or 1
        // (op 36's lo 1 is "arm").
        public static uint IdHash(ulong id)
        {
            unchecked
            {
                uint h = Mix(Mix(0x811C9DC5u, (uint)id), (uint)(id >> 32));
                h ^= h >> 15;
                return h < 2 ? h + 2 : h;
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

        // op 40's 16 bits. In a class: the class bit, the command bit, the follower id's fold. In none: only the reason.
        public static int ClassBits(bool ordinary, bool command, ulong sim)
        {
            if (!ordinary) return 0;
            return kOrdinary | (command ? kCommand : 0) | (int)(SimFold(sim) << kSimShift);
        }
        public static int NoActorBits(bool command, ulong follower)
        {
            return kNoActor | (command ? kCommand : 0) | (int)(SimFold(follower) << kSimShift);
        }
        public static int WhyBits(int why) { return why << kSimShift; }

        // The ordinary class: every Sim fact Sim.OnPortalPathPlan and CommonDoor.IsAllowedThrough read is at the value that
        // leaves the answer of a plain Anybody door or an unnamed portal to the door and the lot. 0 = ordinary, else kWhy*.
        // Not a horse (its own door-kind rule); not a stray (greeting); no tomb mummy (always locked); no proprietor role
        // (unlocks); no service (some ignore locks); no IgnoreDoorLocks flag. Cats, dogs and wild animals are ordinary: for
        // them the rule differs from a human's only in "a pet with a fire on the lot skips the lock type", and lock type
        // Anybody allows either way; the Pets lock type is not shared.
        public static int WhyNotOrdinary(bool horse, bool tombMummy, bool proprietor, bool hasService, bool stray, bool ignoreLocks)
        {
            if (horse) return kWhyHorse;
            if (stray) return kWhyStray;
            if (tombMummy) return kWhyMummy;
            if (proprietor) return kWhyProprietor;
            if (hasService) return kWhyService;
            if (ignoreLocks) return kWhyIgnoreLocks;
            return 0;
        }

        // What an "unlocked" answer for this portal object depends on once the Sim is ordinary, the lot runs the game's
        // handler alone and event 59 has the game's callback alone:
        //  - special (firefighter pole, box stall, horse jump, elevator doors, diving board, ladder): their own rules read
        //    age, posture, traits or the running interaction: not shareable
        //  - a Door whose IsAllowedThrough is CommonDoor's, lock type Anybody: hidden rooms and the one-way room only
        //  - a Door with any other lock type or its own rule (single-gender door, turnstile): the Sim matters
        //  - anything else: no rule of the handler names it; the answer is the planner's initialLocked
        //  - no actor (the routing object is not a Sim: a boat, a car): the handler does nothing at all, so every portal
        //    answers the planner's initialLocked; such a row serves only followers that are no Sim (kNoActorOnly)
        public static int KindNoActor(bool lotPure, bool worldPure) { return lotPure && worldPure ? kNoActorOnly : kNone; }

        public static int Kind(bool lotPure, bool worldPure, bool special, bool isDoor, bool commonRule, bool lockAnybody)
        {
            if (!lotPure || !worldPure || special) return kNone;
            if (!isDoor) return kInert;
            return commonRule && lockAnybody ? kDoor : kNone;
        }
    }
}
