// Usage: node tools/selftest/doormirror/run.ts  (compiles this with mods/ts3fixes/src/DoorMirrorCore.cs)
// Offline self-check of the P3 door mirror records and per-lot table (docs/105 §10.5) with fake doors: record building
// (owner list copied, room fallback, unreadable doors refused), lazy build and epochs, refused or throwing reads keep
// the lot invalid, and a random model run where every mutation is followed by the invalidation the game hooks would
// send: the table must then always answer exactly what a fresh read of the fake doors gives. Exit code 1 on failure.
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class DoorMirrorTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    sealed class FakeDoor
    {
        public ulong Id; public uint Lock = 3; public ulong Owner, OwnerHousehold; public List<ulong> OwnerList = new List<ulong>();
        public int OneWay = DoorRecord.kNoRoom; public int[] Rooms = { 1, 2 }; public int Kind = DoorRecord.kIsDoor; public bool Bound = true;
    }

    static readonly Dictionary<ulong, List<FakeDoor>> sLots = new Dictionary<ulong, List<FakeDoor>>();
    static int sReads;
    static bool sThrow;

    // Same shape as DoorMirror.ReadLotDoors: false on a missing lot or the first door that cannot be mirrored.
    static bool Read(ulong lotId, List<DoorRecord> into)
    {
        sReads++;
        if (sThrow) throw new InvalidOperationException("fake read failure");
        List<FakeDoor> doors;
        if (!sLots.TryGetValue(lotId, out doors)) return false;
        foreach (FakeDoor d in doors)
        {
            if (!d.Bound) return false;
            DoorRecord r = DoorRecord.Make(d.Id, d.Lock, d.Owner, d.OwnerHousehold, d.OwnerList, d.OneWay, d.Rooms, d.Kind);
            if (r == null) return false;
            into.Add(r);
        }
        return true;
    }

    static bool Same(DoorRecord a, FakeDoor d)
    {
        if (a.ObjectId != d.Id || a.LockType != d.Lock || a.LockOwnerId != d.Owner || a.LockOwnerHouseholdId != d.OwnerHousehold
            || a.OneWayRoom != d.OneWay || a.RoomA != d.Rooms[0] || a.RoomB != d.Rooms[1] || a.Kind != d.Kind || a.OwnerList.Length != d.OwnerList.Count) return false;
        for (int i = 0; i < a.OwnerList.Length; i++) if (a.OwnerList[i] != d.OwnerList[i]) return false;
        return true;
    }

    static void Records()
    {
        List<ulong> owners = new List<ulong>(new ulong[] { 11, 12 });
        DoorRecord r = DoorRecord.Make(0x100, 5, 0, 0, owners, 7, new int[] { 3, 4 }, DoorRecord.kIsDoor | DoorRecord.kBarnDoor);
        Check(r != null && r.LockType == 5 && r.OneWayRoom == 7 && r.RoomA == 3 && r.RoomB == 4 && r.Kind == 3, "OwnerList door: fields copied");
        owners.Add(13);
        Check(r.OwnerList.Length == 2 && r.OwnerList[1] == 12, "owner list is a copy, not the game's list");
        r = DoorRecord.Make(0x101, 3, 0, 0, null, DoorRecord.kNoRoom, new int[] { 9 }, 0);
        Check(r != null && r.OwnerList.Length == 0 && r.RoomA == DoorRecord.kNoRoom && r.RoomB == DoorRecord.kNoRoom, "null list and short room array: empty list, no-room pair");
        Check(DoorRecord.Make(0, 3, 0, 0, null, 0, null, 0) == null, "object id 0 refused");
        Check(DoorRecord.Make(0x102, 7, 0, 0, null, 0, null, 0) == null, "lock value outside tLock refused");
        Check(DoorRecord.Make(0x103, 2, 0, 0, null, 0, null, 0) == null, "SelectedHousehold without owner refused");
        Check(DoorRecord.Make(0x104, 2, 21, 0, null, 0, null, 0) != null, "SelectedHousehold with owner kept (homeless owner: household 0)");
        Check(DoorRecord.Make(0x105, 1, 0, 0, null, 0, null, 0) != null, "SelectedActor without owner kept (the game's rule answers it: restricted)");
        for (uint t = 0; t <= DoorRecord.kLockMax; t++)
            Check(DoorRecord.Make(0x106, t, 21, 31, null, 0, null, 0) != null, "every tLock value with an owner kept: " + t);
    }

    static void TableRules()
    {
        sLots.Clear();
        FakeDoor a = new FakeDoor(); a.Id = 0x200;
        FakeDoor b = new FakeDoor(); b.Id = 0x201; b.Lock = 0;
        sLots[1] = new List<FakeDoor>(new FakeDoor[] { a, b });
        sLots[2] = new List<FakeDoor>();
        DoorMirrorTable t = new DoorMirrorTable();
        DoorMirrorTable.LotReader read = Read;
        uint e1, e2, e3;
        sReads = 0;
        DoorRecord[] r1 = t.Get(1, read, out e1);
        Check(r1 != null && r1.Length == 2 && Same(r1[0], a) && Same(r1[1], b) && sReads == 1, "first ask builds");
        DoorRecord[] r2 = t.Get(1, read, out e2);
        Check(r2 == r1 && e2 == e1 && sReads == 1, "second ask: same array, same epoch, no read");
        a.Lock = 0; // an import writes Nobody without a lock event
        Check(t.Get(1, read, out e2) == r1, "without the postfix the table is stale (why the ImportContent postfix exists)");
        t.Invalidate(1);
        DoorRecord[] r3 = t.Get(1, read, out e3);
        Check(e3 != e1 && r3 != r1 && r3[0].LockType == 0 && sReads == 2, "after Invalidate: new epoch, rebuilt with the imported lock");
        DoorRecord[] empty = t.Get(2, read, out e2);
        Check(empty != null && empty.Length == 0 && e2 != e3, "lot without doors: empty table, its own epoch");

        b.Bound = false; // a door whose managed target is not bound yet
        t.Invalidate(1);
        int refused = t.Refused;
        Check(t.Get(1, read, out e1) == null && t.Refused == refused + 1, "unbound door: lot invalid, never defaults");
        Check(t.Get(1, read, out e2) == null && e2 == e1 && t.Refused == refused + 2, "refused lot is read again at the next ask, epoch unchanged");
        b.Bound = true;
        r1 = t.Get(1, read, out e2);
        Check(r1 != null && r1.Length == 2 && e2 == e1, "bound again: built under the same epoch");
        Check(t.Get(3, read, out e3) == null, "unknown lot: invalid");

        uint before1, before2, after1, after2;
        t.Get(1, read, out before1); t.Get(2, read, out before2);
        t.InvalidateAll();
        int reads = sReads;
        t.Get(1, read, out after1); t.Get(2, read, out after2);
        Check(after1 != before1 && after2 != before2 && after1 != after2 && sReads == reads + 2, "InvalidateAll: every lot new epoch and rebuilt");

        t.Invalidate(2);
        uint again;
        t.Get(1, read, out again);
        Check(again == after1, "Invalidate of one lot leaves the other's epoch");

        sThrow = true;
        t.Invalidate(1);
        Check(t.Get(1, read, out e1) == null, "a throwing read: lot invalid, no exception out");
        sThrow = false;
        Check(t.Get(1, read, out e1) != null, "next ask after the throw builds");
    }

    // Random lots and doors; every mutation is followed by the invalidation its game hook sends (lock change: that
    // lot; load: all). After any op, a valid answer must equal a fresh read and an epoch seen with valid records must
    // still mean the same records.
    static void RandomModel()
    {
        Random rnd = new Random(105);
        for (int run = 0; run < 50; run++)
        {
            sLots.Clear();
            ulong next = 0x1000;
            for (ulong lot = 1; lot <= 6; lot++)
            {
                List<FakeDoor> doors = new List<FakeDoor>();
                int n = rnd.Next(0, 5);
                for (int i = 0; i < n; i++) { FakeDoor d = new FakeDoor(); d.Id = next++; d.Lock = (uint)rnd.Next(0, 7); d.Owner = d.Lock == 2 ? 50UL : 0UL; doors.Add(d); }
                sLots[lot] = doors;
            }
            DoorMirrorTable t = new DoorMirrorTable();
            DoorMirrorTable.LotReader read = Read;
            Dictionary<ulong, KeyValuePair<uint, DoorRecord[]>> seen = new Dictionary<ulong, KeyValuePair<uint, DoorRecord[]>>();
            for (int op = 0; op < 400; op++)
            {
                ulong lot = (ulong)rnd.Next(1, 7);
                List<FakeDoor> doors = sLots[lot];
                int k = rnd.Next(10);
                if (k < 3 && doors.Count > 0)
                {
                    FakeDoor d = doors[rnd.Next(doors.Count)];
                    d.Lock = (uint)rnd.Next(0, 7); d.Owner = d.Lock == 2 ? 60UL : 0UL; d.OwnerHousehold = d.Owner == 0 ? 0UL : 70UL;
                    if (rnd.Next(3) == 0) d.OwnerList.Add((ulong)rnd.Next(100));
                    t.Invalidate(lot);
                }
                else if (k == 3 && doors.Count > 0)
                {
                    doors[rnd.Next(doors.Count)].Bound = rnd.Next(2) == 0;
                    t.Invalidate(lot);
                }
                else if (k == 4)
                {
                    FakeDoor d = new FakeDoor(); d.Id = next++; doors.Add(d); // door placed on the lot
                    t.Invalidate(lot);
                }
                else if (k == 5 && rnd.Next(8) == 0) t.InvalidateAll();
                else
                {
                    uint e;
                    DoorRecord[] r = t.Get(lot, read, out e);
                    bool allBound = true;
                    foreach (FakeDoor d in doors) allBound &= d.Bound;
                    if (!allBound) { Check(r == null, "run " + run + " op " + op + ": unbound door answered"); continue; }
                    Check(r != null && r.Length == doors.Count, "run " + run + " op " + op + ": bound lot not built");
                    if (r == null || r.Length != doors.Count) continue;
                    for (int i = 0; i < r.Length; i++) Check(Same(r[i], doors[i]), "run " + run + " op " + op + ": stale record");
                    KeyValuePair<uint, DoorRecord[]> old;
                    if (seen.TryGetValue(lot, out old) && old.Key == e) Check(old.Value == r, "run " + run + " op " + op + ": same epoch, other records");
                    seen[lot] = new KeyValuePair<uint, DoorRecord[]>(e, r);
                }
            }
        }
    }

    // C# port of ts3native's door channel decoder (SpDoorMsg in native/ts3native/snap.cpp, contract docs/94 §14) plus
    // the GetOptionHook routing test (bits 63 + 58 in the high byte, else the game's answer: false). Keep it in step
    // with snap.cpp; NativeVectors replays snap.cpp's own selftest sequence (native/ts3native/selftest.cpp, "door
    // channel") on it, so a drift in either shows up there.
    sealed class PortDecoder
    {
        public sealed class Door { public ulong Obj, Owner, Household; public uint Kind, Lock, OneWay, RoomA, RoomB; public List<ulong> Ids = new List<ulong>(); }
        public sealed class Lot
        {
            public bool Valid; public uint Epoch, MEpoch, Flags; public List<Door> Doors = new List<Door>(); public List<ulong> RuleB = new List<ulong>();
            public List<ulong> Typed = new List<ulong>(); public List<uint> TypedKind = new List<uint>(); public List<int> Hidden = new List<int>();  // coverage
        }
        public readonly Dictionary<ulong, Lot> Lots = new Dictionary<ulong, Lot>();
        public readonly Dictionary<ulong, uint> Actors = new Dictionary<ulong, uint>();  // [DoorFast] actor flags by object id
        public readonly Queue<ulong> Missed = new Queue<ulong>();  // coverage: lots MISS_NEXT hands out, in order (native: the ring)
        public uint NativeEpoch;
        public int Calls, Errors, Ends, Invalidations;
        public bool Old;   // a ts3native without the [DoorFast] codes 23-28: every one of them is a protocol error
        public bool Dfp1;  // a ts3native with codes 23-28 but without the coverage codes 29-35 (v916 only)
        uint mLotLo, mObjLo, mOwnLo, mHhLo, mIdLo, mEpoch, mBLo, mActLo, mTLo, mTKind = 10;
        ulong mAct, mMiss;
        bool mHaveAct;
        Lot mOpen;
        Door mCur;

        public bool Send(ulong opt)
        {
            uint hi = (uint)(opt >> 32), lo = (uint)opt;
            if ((hi & 0xFF000000) != 0x84000000) return false;
            Calls++;
            return Decode(lo, hi);
        }
        public bool Msg(uint code, uint v) { return Decode(v, 0x84000000 | code << 16); }

        // invalidate-all (channel code 20, the door lock / build-buy ICs): the actors stay; only a world change drops them (v919)
        public void InvalidateAll() { foreach (Lot l in Lots.Values) if (l.Valid) { l.Valid = false; Invalidations++; } }
        public Lot Find(ulong key) { Lot l; Lots.TryGetValue(key, out l); return l; }
        public Door FindDoor(ulong lot, ulong obj)
        {
            Lot l = Find(lot);
            if (l == null || !l.Valid || l.Epoch != NativeEpoch) return null;
            foreach (Door d in l.Doors) if (d.Obj == obj) return d;
            return null;
        }

        bool Err() { Errors++; mOpen = null; mCur = null; return false; }
        void Flush() { if (mCur != null) { mOpen.Doors.Add(mCur); mCur = null; } }

        bool Decode(uint lo, uint hi)
        {
            uint code = (hi >> 16) & 0xFF;
            ulong key = (ulong)lo << 32 | mLotLo;
            if (Old && code > 22) return Err();
            if (Dfp1 && code > 28) return Err();
            switch (code)
            {
                case 1: case 18: case 21: mLotLo = lo; return true;
                case 2:
                {
                    Lot l = Find(key);
                    if (l == null) Lots[key] = l = new Lot();
                    l.Doors.Clear(); l.Valid = false; l.MEpoch = 0; l.RuleB.Clear(); l.Flags = 0;
                    l.Typed.Clear(); l.TypedKind.Clear(); l.Hidden.Clear(); mTKind = 10;
                    mOpen = l; mCur = null;
                    return true;
                }
                case 29: return true;
                case 30: mMiss = Missed.Count > 0 ? Missed.Dequeue() : 0; return mMiss != 0;
                case 31: if (lo >= 64) return Err(); return (mMiss >> (int)lo & 1) != 0;
                case 19: { Lot l = Find(key); if (l != null && l.Valid) { l.Valid = false; Invalidations++; } return true; }
                case 20: InvalidateAll(); return true;
                case 22: { Lot l = Find(key); return l != null && l.Valid && l.Epoch == NativeEpoch; }
                case 26: mActLo = lo; mHaveAct = false; return true;
                case 27: mAct = (ulong)lo << 32 | mActLo; mHaveAct = mAct != 0; if (!mHaveAct) return Err(); return true;
                case 28: if (!mHaveAct) return Err(); mHaveAct = false; Actors[mAct] = lo; return true;
            }
            if (mOpen == null) return Err();
            switch (code)
            {
                case 3: Flush(); mObjLo = lo; return true;
                case 23: Flush(); mBLo = lo; return true;
                case 24: mOpen.RuleB.Add((ulong)lo << 32 | mBLo); return true;
                case 25: Flush(); mOpen.Flags = lo; return true;
                case 32: Flush(); if (lo >= 10) return Err(); mTKind = lo; return true;
                case 33: Flush(); mTLo = lo; return true;
                case 34: if (mTKind >= 10) return Err(); mOpen.Typed.Add((ulong)lo << 32 | mTLo); mOpen.TypedKind.Add(mTKind); return true;
                case 35: Flush(); mOpen.Hidden.Add((int)lo); return true;
                case 4: mCur = new Door(); mCur.Obj = (ulong)lo << 32 | mObjLo; return true;
                case 16: mEpoch = lo; return true;
                case 17:
                {
                    Lot l = mOpen;
                    Flush();
                    mOpen = null;
                    if ((uint)l.Doors.Count != lo) return Err();
                    l.Valid = true; l.Epoch = NativeEpoch; l.MEpoch = mEpoch; Ends++;
                    return true;
                }
            }
            if (mCur == null) return Err();
            switch (code)
            {
                case 5: mCur.Kind = lo; return true;
                case 6: mCur.Lock = lo; return true;
                case 7: mOwnLo = lo; return true;
                case 8: mCur.Owner = (ulong)lo << 32 | mOwnLo; return true;
                case 9: mHhLo = lo; return true;
                case 10: mCur.Household = (ulong)lo << 32 | mHhLo; return true;
                case 11: mCur.OneWay = lo; return true;
                case 12: mCur.RoomA = lo; return true;
                case 13: mCur.RoomB = lo; return true;
                case 14: mIdLo = lo; return true;
                case 15: mCur.Ids.Add((ulong)lo << 32 | mIdLo); return true;
                default: return Err();
            }
        }
    }

    // snap.cpp's selftest sequence for the door channel, message for message, with its expected answers
    static void NativeVectors()
    {
        PortDecoder n = new PortDecoder();
        n.NativeEpoch = 5;
        Check(n.Msg(21, 0x123) && !n.Msg(22, 3), "vec: query of an unknown lot");
        Check(n.Msg(1, 0x123) && n.Msg(2, 3), "vec: open");
        Check(n.Msg(3, 0xA1) && n.Msg(4, 0) && n.Msg(5, 1) && n.Msg(6, 2) && n.Msg(12, 4) && n.Msg(13, 5), "vec: door A1");
        Check(n.Msg(3, 0xA2) && n.Msg(4, 0) && n.Msg(5, 9) && n.Msg(6, 6) && n.Msg(7, 0x55) && n.Msg(8, 0), "vec: door A2");
        Check(n.Msg(9, 0x66) && n.Msg(10, 0) && n.Msg(11, 36863) && n.Msg(14, 0x71) && n.Msg(15, 0), "vec: door A2 fields");
        Check(n.Msg(14, 0x72) && n.Msg(15, 1) && n.Msg(16, 42) && n.Msg(17, 2), "vec: list, epoch, end");
        const ulong lot = 0x300000123UL;
        PortDecoder.Door d = n.FindDoor(lot, 0xA2);
        Check(d != null && d.Kind == 9 && d.Lock == 6 && d.Owner == 0x55 && d.Household == 0x66 && d.OneWay == 36863 && d.Ids.Count == 2, "vec: A2 decoded");
        Check(d != null && d.Ids[0] == 0x71 && d.Ids[1] == 0x100000072UL && n.Find(lot).MEpoch == 42, "vec: A2 owner list and managed epoch");
        Check(n.FindDoor(lot, 0xA1) != null && n.FindDoor(lot, 0xA1).RoomB == 5 && n.FindDoor(lot, 0xA3) == null, "vec: A1, no A3");
        Check(n.Msg(21, 0x123) && n.Msg(22, 3), "vec: query valid");
        n.NativeEpoch++;
        Check(n.FindDoor(lot, 0xA2) == null && !n.Msg(22, 3), "vec: world epoch makes the lot stale");
        n.NativeEpoch--;
        int e0 = n.Errors;
        Check(n.Msg(1, 0x123) && n.Msg(2, 3) && n.Msg(3, 1) && n.Msg(4, 0) && !n.Msg(17, 2), "vec: count mismatch refused");
        Check(n.FindDoor(lot, 1) == null && !n.Msg(5, 1) && n.Errors == e0 + 2, "vec: lot invalid, field outside open is an error");
        Check(n.Msg(1, 0x123) && n.Msg(2, 3) && n.Msg(17, 0) && n.Msg(18, 0x123) && n.Msg(19, 3), "vec: empty lot, then invalidate");
        Check(!n.Msg(22, 3) && n.Invalidations >= 1, "vec: invalidated");
        Check(n.Send(0x8400000000000000UL | (ulong)20 << 48), "vec: through the hook (invalidate all)");
        Check(!n.Send(0x0400000000000000UL | (ulong)20 << 48), "vec: without bit 63 not ours");
        // [DoorFast] extras (docs/548): the second half of snap.cpp's "door channel" test, message for message
        Check(n.Msg(1, 0x123) && n.Msg(2, 3) && n.Msg(3, 0xA1) && n.Msg(4, 0) && n.Msg(5, 1), "vec: open with a door");
        Check(n.Msg(23, 0xB1) && n.Msg(24, 0) && !n.Msg(5, 1), "vec: a rule-b id ends the door");
        Check(n.Msg(1, 0x123) && n.Msg(2, 3) && n.Msg(3, 0xA1) && n.Msg(4, 0) && n.Msg(6, 3), "vec: reopen");
        Check(n.Msg(23, 0xB1) && n.Msg(24, 0) && n.Msg(23, 0xB2) && n.Msg(24, 1) && n.Msg(25, 3) && n.Msg(17, 1), "vec: extras, end");
        PortDecoder.Lot xl = n.Find(lot);
        Check(xl.Valid && xl.Doors.Count == 1 && xl.Doors[0].Lock == 3 && xl.RuleB.Count == 2 && xl.RuleB[0] == 0xB1 && xl.RuleB[1] == 0x1000000B2UL && xl.Flags == 3, "vec: extras decoded");
        Check(!n.Msg(23, 0xB3) && !n.Msg(25, 1), "vec: extras outside BEGIN .. END");
        Check(!n.Msg(28, 7) && n.Msg(26, 0) && !n.Msg(27, 0), "vec: actor without id, id 0");
        Check(n.Msg(26, 0x51) && n.Msg(27, 2) && n.Msg(28, 7) && n.Msg(26, 0x51) && n.Msg(27, 2) && n.Msg(28, 15), "vec: actor twice");
        Check(n.Actors.Count == 1 && n.Actors[0x200000051UL] == 15, "vec: second push replaced the record");
        Check(n.Msg(1, 0x123) && n.Msg(2, 3) && n.Msg(17, 0) && n.Find(lot).RuleB.Count == 0 && n.Find(lot).Flags == 0, "vec: reopen clears the extras");
        Check(n.Msg(20, 0) && n.Actors.Count == 1, "vec: invalidate all keeps the actors (v919)");
        // coverage (v919): the third part of snap.cpp's "door channel" test
        int e1 = n.Errors;
        Check(n.Msg(29, 0) && !n.Msg(30, 0) && !n.Msg(31, 5) && !n.Msg(31, 64), "vec: caps, empty missed-lot ring");
        Check(n.Msg(1, 0x123) && n.Msg(2, 3) && n.Msg(3, 0xA1) && n.Msg(4, 0) && n.Msg(5, 1), "vec: open with a door (coverage)");
        Check(n.Msg(32, 0) && n.Msg(33, 0xD0) && n.Msg(34, 0) && n.Msg(33, 0xD1) && n.Msg(34, 1), "vec: typed kind 0");
        Check(n.Msg(32, 9) && n.Msg(33, 0xD9) && n.Msg(34, 0) && n.Msg(35, 3) && n.Msg(35, 0xFFFF8FFF), "vec: typed kind 9, hidden rooms");
        Check(n.Msg(25, 1) && n.Msg(17, 1), "vec: coverage, flags, end");
        PortDecoder.Lot cl = n.Find(lot);
        Check(cl.Valid && cl.Doors.Count == 1 && cl.Typed.Count == 3 && cl.Typed[0] == 0xD0 && cl.TypedKind[0] == 0 && cl.Typed[1] == 0x1000000D1UL
              && cl.TypedKind[1] == 0, "vec: typed decoded");
        Check(cl.Typed[2] == 0xD9 && cl.TypedKind[2] == 9 && cl.Hidden.Count == 2 && cl.Hidden[0] == 3 && cl.Hidden[1] == -28673 && cl.Flags == 1
              && n.Errors == e1 + 1, "vec: hidden decoded");
        Check(n.Msg(1, 0x123) && n.Msg(2, 3) && !n.Msg(34, 0), "vec: typed id before a kind");
        Check(n.Msg(1, 0x123) && n.Msg(2, 3) && !n.Msg(32, 10), "vec: kind past the last");
        Check(!n.Msg(32, 0) && !n.Msg(35, 1) && n.Errors == e1 + 5, "vec: coverage outside BEGIN .. END");
        Check(n.Msg(1, 0x123) && n.Msg(2, 3) && n.Msg(17, 0) && n.Find(lot).Typed.Count == 0 && n.Find(lot).Hidden.Count == 0, "vec: reopen clears the coverage");
    }

    static bool Same(DoorRecord a, PortDecoder.Door d)
    {
        if (a.ObjectId != d.Obj || a.LockType != d.Lock || a.LockOwnerId != d.Owner || a.LockOwnerHouseholdId != d.Household || (uint)a.OneWayRoom != d.OneWay
            || (uint)a.RoomA != d.RoomA || (uint)a.RoomB != d.RoomB || (uint)a.Kind != d.Kind || a.OwnerList.Length != d.Ids.Count) return false;
        for (int i = 0; i < a.OwnerList.Length; i++) if (a.OwnerList[i] != d.Ids[i]) return false;
        return true;
    }

    static ulong R64(Random rnd) { return (ulong)(uint)rnd.Next() << 33 ^ (ulong)(uint)rnd.Next() << 2 ^ (ulong)rnd.Next(4); }

    // Encoder bits, the round trip of random records through Push into the port, and the adapter (Sync) against the
    // port with native-only invalidations (world epoch, door lock event) and managed ones (Invalidate over the channel).
    static void Channel()
    {
        Check(DoorChannel.Option(17, 0xDEADBEEF) == 0x84110000DEADBEEFUL && (uint)(DoorChannel.Option(22, 1) >> 32) == (0x84000000u | 22u << 16), "option bits");
        Random rnd = new Random(94);
        DoorChannel.Caps = -1;
        for (int run = 0; run < 200; run++)
        {
            PortDecoder n = new PortDecoder();
            n.NativeEpoch = (uint)run;
            ulong lot = R64(rnd);
            DoorRecord[] doors = new DoorRecord[rnd.Next(0, 6)];
            for (int i = 0; i < doors.Length; i++)
            {
                List<ulong> ids = new List<ulong>();
                for (int j = rnd.Next(0, 4); j > 0; j--) ids.Add(R64(rnd));
                uint lk = (uint)rnd.Next(0, 7);
                doors[i] = DoorRecord.Make(R64(rnd) | 1, lk, lk == 2 ? R64(rnd) | 1 : R64(rnd), R64(rnd), ids, rnd.Next(2) == 0 ? DoorRecord.kNoRoom : rnd.Next(),
                                           new int[] { rnd.Next(), -rnd.Next(1, 100) }, rnd.Next(64));
            }
            uint epoch = (uint)rnd.Next();
            LotInfo info = run % 3 == 0 ? null : new LotInfo();  // every third push without [DoorFast] extras
            if (info != null)
            {
                info.RuleB = new ulong[rnd.Next(0, 4)];
                for (int j = 0; j < info.RuleB.Length; j++) info.RuleB[j] = R64(rnd);
                info.Flags = (uint)rnd.Next(2);
                // coverage: typed owners in runs of one kind or mixed, hidden room ids (negative ones too), sometimes unreadable
                int nt = rnd.Next(0, 5);
                info.Typed = new ulong[nt];
                info.TypedKind = new uint[nt];
                for (int j = 0; j < nt; j++) { info.Typed[j] = R64(rnd); info.TypedKind[j] = (uint)rnd.Next(10); }
                info.Hidden = run % 5 == 1 ? null : new int[rnd.Next(0, 3)];
                for (int j = 0; info.Hidden != null && j < info.Hidden.Length; j++) info.Hidden[j] = rnd.Next(-40000, 40000);
            }
            bool xr;
            Check(DoorChannel.Push(lot, doors, epoch, info, n.Send, out xr) && !xr && n.Errors == 0, "run " + run + ": push accepted");
            PortDecoder.Lot l = n.Find(lot);
            Check(l != null && l.Valid && l.MEpoch == epoch && l.Doors.Count == doors.Length && DoorChannel.Query(lot, n.Send) == 1, "run " + run + ": lot valid");
            for (int i = 0; l != null && i < doors.Length && i < l.Doors.Count; i++) Check(Same(doors[i], l.Doors[i]), "run " + run + ": door " + i + " round trip");
            uint wantFlags = info == null ? 0 : info.Flags | (info.Hidden == null ? LotInfo.kHiddenRooms : 0);  // unreadable list: the lot bit
            bool sameB = l != null && l.RuleB.Count == (info != null ? info.RuleB.Length : 0) && l.Flags == wantFlags;
            for (int j = 0; sameB && info != null && j < info.RuleB.Length; j++) sameB = l.RuleB[j] == info.RuleB[j];
            Check(sameB, "run " + run + ": extras round trip");
            bool sameT = l != null && l.Typed.Count == (info != null ? info.Typed.Length : 0)
                         && l.Hidden.Count == (info != null && info.Hidden != null ? info.Hidden.Length : 0);
            for (int j = 0; sameT && info != null && j < info.Typed.Length; j++) sameT = l.Typed[j] == info.Typed[j] && l.TypedKind[j] == info.TypedKind[j];
            for (int j = 0; sameT && info != null && info.Hidden != null && j < info.Hidden.Length; j++) sameT = l.Hidden[j] == info.Hidden[j];
            Check(sameT, "run " + run + ": coverage round trip");
            Check(DoorChannel.Invalidate(lot, n.Send) && DoorChannel.Query(lot, n.Send) == 0, "run " + run + ": invalidate");
        }

        // no ts3native: the game answers false; nothing is read or pushed
        DoorMirrorTable t = new DoorMirrorTable();
        DoorMirrorTable.LotReader read = Read;
        sLots.Clear();
        FakeDoor a = new FakeDoor(); a.Id = 0x200; FakeDoor b = new FakeDoor(); b.Id = 0x201; b.Lock = 5; b.OwnerList.Add(9);
        sLots[7] = new List<FakeDoor> { a, b };
        int reads = sReads, calls = 0;
        Check(DoorChannel.Sync(7, t, read, delegate(ulong o) { calls++; return false; }) == -1 && sReads == reads && calls == 1, "no channel: one call, no read");

        PortDecoder p = new PortDecoder();
        p.NativeEpoch = 1;
        Check(DoorChannel.Sync(7, t, read, p.Send) == 2 && p.FindDoor(7, 0x201) != null && p.FindDoor(7, 0x201).Ids[0] == 9, "first START pushes");
        reads = sReads;
        Check(DoorChannel.Sync(7, t, read, p.Send) == 1 && sReads == reads, "valid lot: query only");
        a.Lock = 0; p.InvalidateAll(); // the door lock event: native only, our table never hears it
        Check(DoorChannel.Sync(7, t, read, p.Send) == 2 && p.FindDoor(7, 0x200).Lock == 0, "native lock-event invalidation: fresh read pushed");
        p.NativeEpoch++; a.Lock = 3;
        Check(DoorChannel.Sync(7, t, read, p.Send) == 2 && p.FindDoor(7, 0x200).Lock == 3, "native world epoch: fresh read pushed");
        b.Bound = false; p.InvalidateAll();
        Check(DoorChannel.Sync(7, t, read, p.Send) == 0 && p.FindDoor(7, 0x200) == null && DoorChannel.Query(7, p.Send) == 0, "refused read: nothing pushed, lot invalid natively");
        b.Bound = true;
        Check(DoorChannel.Sync(7, t, read, p.Send) == 2, "bound again: pushed");
        Check(DoorChannel.Invalidate(7, p.Send) && DoorChannel.Sync(7, t, read, p.Send) == 2, "managed Invalidate over the channel: next START pushes");
        Check(DoorChannel.InvalidateAll(p.Send) && DoorChannel.Query(7, p.Send) == 0, "managed InvalidateAll over the channel");
        Check(p.Errors == 0, "adapter: no protocol errors");

        // [DoorFast] extras: read only when the lot is pushed, sent after its doors; an older ts3native refuses them, and Sync then
        // pushes the doors alone and stops sending extras and actors (NoExtras)
        DoorChannel.NoExtras = false;
        LotInfo li = new LotInfo(); li.RuleB = new ulong[] { 0xB1, 0x1000000B2UL }; li.Flags = LotInfo.kOneHandler;
        int infoCalls = 0;
        DoorChannel.LotInfoReader ri = delegate(ulong lotId) { infoCalls++; return li; };
        PortDecoder x = new PortDecoder();
        x.NativeEpoch = 3;
        Check(DoorChannel.Sync(7, t, read, ri, x.Send) == 2 && infoCalls == 1 && x.FindDoor(7, 0x201) != null, "extras: pushed with the doors");
        Check(x.Find(7).RuleB.Count == 2 && x.Find(7).RuleB[1] == 0x1000000B2UL && x.Find(7).Flags == LotInfo.kOneHandler, "extras: decoded");
        Check(DoorChannel.Sync(7, t, read, ri, x.Send) == 1 && infoCalls == 1, "extras: a valid lot is not read again");
        Check(DoorChannel.Actor(0x51, DoorChannel.kActHuman | DoorChannel.kActNotMummy, x.Send) && x.Actors[0x51] == 3, "actor pushed");
        Check(!DoorChannel.Actor(0, 1, x.Send) && !DoorChannel.NoExtras, "actor id 0: not sent, not a refusal");
        x.InvalidateAll();
        Check(DoorChannel.Sync(7, t, read, delegate(ulong lotId) { return null; }, x.Send) == 2 && x.Find(7).RuleB.Count == 0 && x.Find(7).Flags == 0,
              "unreadable lot info: doors without extras (no prediction natively)");
        x.InvalidateAll();
        Check(DoorChannel.Sync(7, t, read, delegate(ulong lotId) { throw new InvalidOperationException("fake"); }, x.Send) == 2 && x.Find(7).Flags == 0,
              "throwing lot info: doors without extras");
        Check(x.Errors == 0 && !DoorChannel.NoExtras, "extras: no protocol errors");
        PortDecoder old = new PortDecoder();
        old.Old = true;
        old.NativeEpoch = 1;
        DoorChannel.Caps = -1;  // another ts3native: asked again
        Check(DoorChannel.Sync(7, t, read, ri, old.Send) == 2 && DoorChannel.NoExtras && old.Find(7).Valid && old.FindDoor(7, 0x201) != null,
              "older ts3native: extras refused, the doors pushed again without them");
        old.InvalidateAll();
        int ic = infoCalls;
        Check(DoorChannel.Sync(7, t, read, ri, old.Send) == 2 && infoCalls == ic && !DoorChannel.Actor(0x51, 1, old.Send), "NoExtras: no lot info read, no actors");
        DoorChannel.NoExtras = false;
        Check(!DoorChannel.Actor(0x51, 1, old.Send) && DoorChannel.NoExtras, "older ts3native refuses an actor: NoExtras");
        DoorChannel.NoExtras = false;
        LotInfo flagsOnly = new LotInfo();
        flagsOnly.Flags = LotInfo.kHiddenRooms;
        bool refused;
        Check(!DoorChannel.Push(9, new DoorRecord[0], 1, flagsOnly, old.Send, out refused) && refused, "no rule-b ids: the lot flags are the first extras message");
        DoorChannel.NoExtras = false;

        // coverage (v919): a v916 ts3native refuses CAPS once; lots then go as before (rule b, flags, a lot with hidden rooms as the
        // lot bit), no typed ids, no missed lots asked
        LotInfo cov = new LotInfo();
        cov.RuleB = new ulong[] { 0xB1 };
        cov.Flags = LotInfo.kOneHandler;
        cov.Typed = new ulong[] { 0xD0, 0xD1, 0xD6 };
        cov.TypedKind = new uint[] { 0, 0, 6 };
        cov.Hidden = new int[] { 5, -28673 };
        PortDecoder v916 = new PortDecoder();
        v916.Dfp1 = true;
        v916.NativeEpoch = 2;
        v916.Missed.Enqueue(0x300000125UL);
        DoorChannel.Caps = -1;
        Check(DoorChannel.Push(7, new DoorRecord[0], 1, cov, v916.Send, out refused) && !refused && DoorChannel.Caps == 0 && v916.Errors == 1,
              "v916 ts3native: CAPS refused once, the lot pushed");
        PortDecoder.Lot vl = v916.Find(7);
        Check(vl.Valid && vl.RuleB.Count == 1 && vl.Flags == (LotInfo.kOneHandler | LotInfo.kHiddenRooms) && vl.Typed.Count == 0 && vl.Hidden.Count == 0,
              "v916 ts3native: hidden rooms as the lot bit, no typed ids");
        int vc = v916.Calls;
        Check(DoorChannel.NextMissed(v916.Send) == 0 && v916.Calls == vc && v916.Errors == 1, "v916 ts3native: no missed lots asked");
        cov.Hidden = new int[0];
        Check(DoorChannel.Push(7, new DoorRecord[0], 1, cov, v916.Send, out refused) && v916.Find(7).Flags == LotInfo.kOneHandler, "v916: no hidden rooms, no bit");
        // a coverage ts3native: typed ids with their kinds (one kind message per run), hidden ids, the bit only for an unreadable list
        PortDecoder c = new PortDecoder();
        c.NativeEpoch = 2;
        DoorChannel.Caps = -1;
        cov.Hidden = new int[] { 5, -28673 };
        int c0 = c.Calls;
        Check(DoorChannel.Push(7, new DoorRecord[0], 1, cov, c.Send, out refused) && DoorChannel.Caps == 1 && c.Errors == 0, "coverage: pushed");
        // CAPS, lot lo / hi, epoch, B lo / hi, kind 0, 2 x (T lo / hi), kind 6, T lo / hi, 2 hidden, flags, END
        Check(c.Calls - c0 == 1 + 3 + 2 + 1 + 4 + 1 + 2 + 2 + 2, "coverage: one kind message per run of one kind, " + (c.Calls - c0) + " calls");
        PortDecoder.Lot ll = c.Find(7);
        Check(ll.Valid && ll.Flags == LotInfo.kOneHandler && ll.Typed.Count == 3 && ll.Typed[2] == 0xD6 && ll.TypedKind[1] == 0 && ll.TypedKind[2] == 6
              && ll.Hidden.Count == 2 && ll.Hidden[1] == -28673, "coverage: decoded");
        cov.Hidden = null;
        Check(DoorChannel.Push(7, new DoorRecord[0], 1, cov, c.Send, out refused) && c.Find(7).Flags == (LotInfo.kOneHandler | LotInfo.kHiddenRooms)
              && c.Find(7).Hidden.Count == 0, "coverage: unreadable hidden list: the lot bit (unknown)");
        // missed lots: one bit per message, in hand-out order, then none
        c.Missed.Enqueue(0x300000125UL);
        c.Missed.Enqueue(0xFFFFFFFF00000001UL);
        Check(DoorChannel.NextMissed(c.Send) == 0x300000125UL && DoorChannel.NextMissed(c.Send) == 0xFFFFFFFF00000001UL && DoorChannel.NextMissed(c.Send) == 0,
              "coverage: missed lots handed out");
        DoorChannel.NoExtras = true;
        c.Missed.Enqueue(0x125UL);
        Check(DoorChannel.NextMissed(c.Send) == 0 && c.Missed.Count == 1, "NoExtras: no missed lots asked");
        Check(c.Errors == 0, "coverage: no protocol errors");
        DoorChannel.NoExtras = false;
        DoorChannel.Caps = -1;
    }

    static int Main()
    {
        Records();
        TableRules();
        RandomModel();
        NativeVectors();
        Channel();
        Console.WriteLine(sFails == 0 ? "doormirror selftest: all checks passed" : "doormirror selftest: " + sFails + " failures");
        return sFails == 0 ? 0 : 1;
    }
}
