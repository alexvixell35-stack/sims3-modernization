using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent half of the P3 door mirror (docs/105 §10.5): the per-door record and the per-lot table with its
    // epochs. No game types here, so tools/selftest/doormirror can compile it outside the game. DoorMirror.cs reads the
    // doors and holds the CommonDoor.ImportContent postfix.

    // One door of a lot, as the door handler (CommonDoor.IsAllowedThrough, docs/105 §3 row 4a) reads it: the four lock
    // fields plus the class facts it tests, and the room pair its portals join.
    public sealed class DoorRecord
    {
        public const int kNoRoom = 36863;   // the game's "no room" id (one-way default, GetRoomConnections fallback)
        public const uint kLockMax = 6;     // CommonDoor.tLock: Nobody 0, SelectedActor 1, SelectedHousehold 2, Anybody 3, Pets 4, OwnerList 5, VIPRoom 6
        public const uint kSelectedHousehold = 2;
        // Kind bits: Door (the only class the Sim handler asks, docs/103 §1), IBarnDoor, GateDouble, IElectronicGate,
        // GameObject.CanLargeAnimalUse, DoorSingleGender (its own rule, tier P, docs/105 §3 row 4b), and the door's type
        // overrides CommonDoor.IsAllowedThrough(int, int, Sim) ([DoorFast] rule a needs CommonDoor's own, docs/548).
        // v920 ([DoorFast] rule d): IsAllowedThrough declared on DoorSingleGender, and its mGender Male / Female.
        public const int kIsDoor = 1, kBarnDoor = 2, kGateDouble = 4, kElectronicGate = 8, kLargeAnimalUse = 16, kSingleGender = 32,
            kOverridden = 64, kGenderOwn = 128, kDoorMale = 256, kDoorFemale = 512;

        public ulong ObjectId;              // the portal owner id: the key of the native portal answer (docs/105 §2)
        public uint LockType;               // mLockType
        public ulong LockOwnerId;           // mLockOwner.SimDescriptionId, 0 = no owner
        public ulong LockOwnerHouseholdId;  // mLockOwner.Household.HouseholdId, 0 = none
        public ulong[] OwnerList;           // copy of mOwnerList (SimDescription ids), never the game's list
        public int OneWayRoom;              // mOneWayDestinationRoomId, kNoRoom = both ways
        public int RoomA, RoomB;            // GetRoomConnections(): the two rooms this door's portals join
        public int Kind;

        // The record of one door, or null when the door cannot be mirrored and its lot must stay invalid: no object id,
        // a lock value outside tLock, or SelectedHousehold with no owner (the game's rule would throw on it, so only the
        // game's own code may answer that door).
        public static DoorRecord Make(ulong objectId, uint lockType, ulong ownerId, ulong ownerHouseholdId, IList<ulong> ownerList,
                                      int oneWayRoom, int[] rooms, int kind)
        {
            if (objectId == 0 || lockType > kLockMax || (lockType == kSelectedHousehold && ownerId == 0)) return null;
            DoorRecord r = new DoorRecord();
            r.ObjectId = objectId;
            r.LockType = lockType;
            r.LockOwnerId = ownerId;
            r.LockOwnerHouseholdId = ownerHouseholdId;
            r.OwnerList = new ulong[ownerList == null ? 0 : ownerList.Count];
            for (int i = 0; i < r.OwnerList.Length; i++) r.OwnerList[i] = ownerList[i];
            r.OneWayRoom = oneWayRoom;
            bool two = rooms != null && rooms.Length >= 2;
            r.RoomA = two ? rooms[0] : kNoRoom;
            r.RoomB = two ? rooms[1] : kNoRoom;
            r.Kind = kind;
            return r;
        }
    }

    // Per-lot door records, built lazily on the sim thread at the first ask after an invalidation (docs/105 §10.3 point
    // 1, §10.4.5). Epoch: a number that changes every time a lot is invalidated, so a reader that kept the records of
    // epoch E knows they are still current while Get answers E with a non-null array.
    public sealed class DoorMirrorTable
    {
        // Appends the lot's door records to into; false = some door could not be read (the lot stays invalid).
        public delegate bool LotReader(ulong lotId, List<DoorRecord> into);

        sealed class Entry { public uint Epoch; public DoorRecord[] Doors; } // Doors null = invalid

        readonly Dictionary<ulong, Entry> mLots = new Dictionary<ulong, Entry>();
        readonly List<DoorRecord> mScratch = new List<DoorRecord>();
        uint mClock;
        public int Builds, Refused, Invalidations;

        Entry EntryOf(ulong lotId)
        {
            Entry e;
            if (!mLots.TryGetValue(lotId, out e)) { e = new Entry(); e.Epoch = ++mClock; mLots[lotId] = e; }
            return e;
        }

        // The lot's records (built by read when invalid), or null when read refused or threw; the epoch is the lot's
        // current one either way. A refused lot is read again at the next call.
        public DoorRecord[] Get(ulong lotId, LotReader read, out uint epoch)
        {
            Entry e = EntryOf(lotId);
            epoch = e.Epoch;
            if (e.Doors != null) return e.Doors;
            mScratch.Clear();
            bool ok;
            try { ok = read(lotId, mScratch); } catch (Exception) { ok = false; }
            if (!ok) { Refused++; return null; }
            Builds++;
            return e.Doors = mScratch.ToArray();
        }

        public void Invalidate(ulong lotId)
        {
            Entry e = EntryOf(lotId);
            e.Doors = null;
            e.Epoch = ++mClock;
            Invalidations++;
        }

        public void InvalidateAll()
        {
            foreach (Entry e in mLots.Values) { e.Doors = null; e.Epoch = ++mClock; }
            Invalidations++;
        }
    }

    // Encoder for the ts3native door channel (docs/94 §14, decoder SpDoorMsg in native/ts3native/snap.cpp): one
    // Route.GetOption2 call per message, RouteOption2 bits 63 + 58 set, the code in bits 48-55 and a 32-bit value in
    // bits 0-31 (native sees optHi = 0x84000000 | code << 16, optLo = value). Answer true = accepted. Without
    // ts3native the game answers false for every message, so Query reports "no channel" and nothing is pushed.
    // [DoorFast] (ts3native doorfast.cpp, docs/548): what the native predictor needs besides the door records, read at push time
    // only (never per ask). RuleB: the lot's portal owners of a type with no rule of its own in Sim.OnPortalPathPlan; Flags: lot
    // flags below. A null LotInfo pushes no extras: the lot then gets no prediction natively (flag kOneHandler missing).
    // Coverage (v919, docs/548): Typed / TypedKind = portal owners that are neither a door nor rule b, by kind (kTypes kinds, the
    // order of ts3native's dfType; kinds from kTypePlain on have no rule of their own in Sim.OnPortalPathPlan); Hidden = the lot's
    // hidden room ids (null = unreadable). Sent only to a ts3native that knows the coverage codes (DoorChannel.Coverage); to an
    // older one a lot with hidden rooms goes as kHiddenRooms, as before.
    public sealed class LotInfo
    {
        public const uint kOneHandler = 1, kHiddenRooms = 2;  // only EA's Sim.OnPortalPathPlan listens; hidden rooms unknown (older ts3native: or present)
        public const uint kApartment = 4, kResort = 8;  // v920: Lot.HasVirtualResidentialSlots (rule c), CommercialLotSubType kEP10_Resort (rule d)
        public const uint kTypes = 10, kTypePlain = 6;
        public ulong[] RuleB = new ulong[0];
        public uint Flags;
        public ulong[] Typed = new ulong[0];
        public uint[] TypedKind = new uint[0];
        public int[] Hidden = new int[0];
    }

    public static class DoorChannel
    {
        public const int kLotLo = 1, kLotHi = 2, kObjLo = 3, kObjHi = 4, kKind = 5, kLock = 6, kOwnerLo = 7, kOwnerHi = 8,
            kHhLo = 9, kHhHi = 10, kOneWay = 11, kRoomA = 12, kRoomB = 13, kListLo = 14, kListHi = 15, kEpoch = 16, kEnd = 17,
            kInvLo = 18, kInvHi = 19, kInvAll = 20, kQueryLo = 21, kQueryHi = 22,
            kRuleBLo = 23, kRuleBHi = 24, kLotFlags = 25, kActorLo = 26, kActorHi = 27, kActorFlags = 28,  // [DoorFast] extras
            kCaps = 29, kMissNext = 30, kMissBit = 31, kTypedKind = 32, kTypedLo = 33, kTypedHi = 34, kHidden = 35;  // coverage (v919)
        // actor flags ([DoorFast], pushed at each route START): human (not pet, horse or stray), not a tomb mummy, no Proprietor
        // role, household scope (a Sim the player controls or one that cannot be classified: always asked natively); not a Sim
        // (coverage: the router object is no Sim, EA's handler sees a null Actor; household scope unless the running task's Sim is
        // outside it); v920 (rules c-f): ToddlerOrBelow, IsVisuallyPregnant, avoids ladders (Elder or a Ladder.kTraitsThatAvoidLadders
        // trait), Gender Male / Female (the same bits as DoorRecord.kDoorMale / kDoorFemale)
        public const uint kActHuman = 1, kActNotMummy = 2, kActNoProprietor = 4, kActHousehold = 8, kActNotSim = 16, kActToddler = 32,
            kActPregnant = 64, kActAvoidLadder = 128, kActMale = 256, kActFemale = 512;
        public const ulong kBits = 0x8400000000000000UL;

        // Set when ts3native refused an extras message (a build without the [DoorFast] codes, or no ts3native): from then on
        // lots are pushed as before, without extras, and no actors are sent.
        public static bool NoExtras;

        // Coverage codes known to ts3native: -1 not asked yet, 0 no (an older build refuses kCaps), 1 yes. Asked once (a session
        // keeps its ts3native); tests reset it.
        public static int Caps = -1;
        public static bool Coverage(Sender s)
        {
            if (Caps < 0) Caps = !NoExtras && s(Option(kCaps, 0)) ? 1 : 0;
            return Caps == 1;
        }

        // The next lot ts3native's door asks found no valid table for (coverage), 0 = none; its id comes one bit per message
        public static ulong NextMissed(Sender s)
        {
            if (NoExtras || !Coverage(s) || !s(Option(kMissNext, 0))) return 0;
            ulong id = 0;
            for (int b = 0; b < 64; b++) if (s(Option(kMissBit, (uint)b))) id |= 1UL << b;
            return id;
        }

        public delegate bool Sender(ulong option); // one GetOption2 call with this RouteOption2 value
        public delegate LotInfo LotInfoReader(ulong lotId); // null = unknown: no extras for the lot

        public static ulong Option(int code, uint value) { return kBits | (ulong)(uint)code << 48 | value; }

        static bool Pair(Sender s, int loCode, ulong v) { return s(Option(loCode, (uint)v)) && s(Option(loCode + 1, (uint)(v >> 32))); }

        // 1 = ts3native's table of the lot is valid in its current epoch, 0 = not valid, -1 = no channel (the low half,
        // which ts3native always accepts, was refused: no ts3native, or a build without the door channel)
        public static int Query(ulong lot, Sender s)
        {
            if (!s(Option(kQueryLo, (uint)lot))) return -1;
            return s(Option(kQueryHi, (uint)(lot >> 32))) ? 1 : 0;
        }

        // The lot's records: open (clears the native lot), epoch, per door its fields and owner list, then END with the
        // count, which ts3native checks before it marks the lot valid. False at the first refused message (lot stays invalid).
        public static bool Push(ulong lot, DoorRecord[] doors, uint epoch, Sender s)
        {
            bool extrasRefused;
            return Push(lot, doors, epoch, null, s, out extrasRefused);
        }

        // info (may be null) goes after the doors: its rule-b ids, with coverage its typed owners and hidden room ids, then the lot
        // flags. extrasRefused = the first extras message was refused (an older ts3native: Sync pushes again without them).
        public static bool Push(ulong lot, DoorRecord[] doors, uint epoch, LotInfo info, Sender s, out bool extrasRefused)
        {
            extrasRefused = false;
            bool cov = info != null && Coverage(s);  // asked before the lot opens
            if (!Pair(s, kLotLo, lot) || !s(Option(kEpoch, epoch))) return false; // epoch once per lot, so an empty lot has one too
            foreach (DoorRecord d in doors)
            {
                if (!Pair(s, kObjLo, d.ObjectId) || !s(Option(kKind, (uint)d.Kind)) || !s(Option(kLock, d.LockType))
                    || !Pair(s, kOwnerLo, d.LockOwnerId) || !Pair(s, kHhLo, d.LockOwnerHouseholdId) || !s(Option(kOneWay, (uint)d.OneWayRoom))
                    || !s(Option(kRoomA, (uint)d.RoomA)) || !s(Option(kRoomB, (uint)d.RoomB))) return false;
                foreach (ulong id in d.OwnerList) if (!Pair(s, kListLo, id)) return false;
            }
            if (info != null)
            {
                bool first = true;
                foreach (ulong id in info.RuleB)
                {
                    if (!s(Option(kRuleBLo, (uint)id))) { extrasRefused = first; return false; }
                    first = false;
                    if (!s(Option(kRuleBHi, (uint)(id >> 32)))) return false;
                }
                uint flags = info.Flags;
                if (info.Hidden == null || (!cov && info.Hidden.Length > 0)) flags |= LotInfo.kHiddenRooms;
                if (cov)
                {
                    uint kind = uint.MaxValue;
                    for (int i = 0; i < info.Typed.Length; i++)
                    {
                        if (info.TypedKind[i] != kind)  // the kind is sticky natively: sent once per run of one kind
                        {
                            kind = info.TypedKind[i];
                            if (!s(Option(kTypedKind, kind))) return false;
                        }
                        if (!Pair(s, kTypedLo, info.Typed[i])) return false;
                    }
                    if (info.Hidden != null) foreach (int h in info.Hidden) if (!s(Option(kHidden, (uint)h))) return false;
                }
                if (!s(Option(kLotFlags, flags))) { extrasRefused = first; return false; }
            }
            return s(Option(kEnd, (uint)doors.Length));
        }

        // [DoorFast] one route actor's flags (kAct*); false when refused (then NoExtras: no more actors are sent)
        public static bool Actor(ulong actorId, uint flags, Sender s)
        {
            if (NoExtras || actorId == 0) return false;
            if (Pair(s, kActorLo, actorId) && s(Option(kActorFlags, flags))) return true;
            NoExtras = true;
            return false;
        }

        public static bool Invalidate(ulong lot, Sender s) { return Pair(s, kInvLo, lot); }

        public static bool InvalidateAll(Sender s) { return s(Option(kInvAll, 0)); }

        // The adapter step for one lot at a route START: ask ts3native; when its table of the lot is not valid, drop our
        // cached records too (ts3native also invalidates on what our table never hears: world epochs, the door lock
        // event, build/buy exit) and push a fresh read. A refused read pushes nothing, so the lot stays invalid there.
        // Returns -1 no channel, 1 already valid, 0 nothing pushed, 2 pushed.
        public static int Sync(ulong lot, DoorMirrorTable table, DoorMirrorTable.LotReader read, Sender s) { return Sync(lot, table, read, null, s); }

        // info: the [DoorFast] extras of the lot (null reader, NoExtras, a null answer or a throw: none)
        public static int Sync(ulong lot, DoorMirrorTable table, DoorMirrorTable.LotReader read, LotInfoReader info, Sender s)
        {
            int q = Query(lot, s);
            if (q != 0) return q;
            table.Invalidate(lot);
            uint epoch;
            DoorRecord[] d = table.Get(lot, read, out epoch);
            if (d == null) return 0;
            LotInfo li = null;
            if (info != null && !NoExtras) { try { li = info(lot); } catch (Exception) { li = null; } }
            bool extrasRefused;
            if (Push(lot, d, epoch, li, s, out extrasRefused)) return 2;
            if (!extrasRefused) return 0;
            NoExtras = true; // an older ts3native: the doors alone, as before
            return Push(lot, d, epoch, null, s, out extrasRefused) ? 2 : 0;
        }
    }
}
