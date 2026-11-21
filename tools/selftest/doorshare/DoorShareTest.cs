using System;
using Ts3Fixes;

// Offline self-check of DoorShareCore (docs/674). The hash and fold values are the ones native/ts3native/doorshare-test.h
// checks for DsNoteHash / DsSimFold: both sides must name an ask the same way.
static class DoorShareTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        const ulong portal = 0x0000004400000033UL, simA = 0x0000B0B00000A0A0UL;
        Check(DoorShareCore.NoteHash(portal, simA, 5, 6) == 0x7845B309u, "note hash of the shared test ask");
        Check(DoorShareCore.NoteHash(portal, simA, 0x10005, unchecked((int)0xFFFF0006)) == 0x7845B309u, "rooms count as 16 bits");
        Check(DoorShareCore.NoteHash(portal, simA, -1, 0x7FFF) == DoorShareCore.NoteHash(portal, simA, 0xFFFF, 0x7FFF), "room -1 is 0xFFFF");
        Check(DoorShareCore.NoteHash(0x9ABCDEF012345678UL, 0x374A00550BADF00DUL, 0xFFFF, 0x7FFF) == 0x14397595u, "note hash, second vector");
        Check(DoorShareCore.NoteHash(portal, simA, 6, 5) != 0x7845B309u, "the other direction is another ask");
        Check(DoorShareCore.NoteHash(portal, simA + 1, 5, 6) != 0x7845B309u, "another Sim is another ask");
        Check(DoorShareCore.NoteHash(0, 0, 0, 0) != 0, "never 0");
        Check(DoorShareCore.SimFold(simA) == 0x1010u, "Sim fold");
        Check(DoorShareCore.SimFold(0x9ABCDEF012345678UL) == 0xCEEu, "Sim fold, second vector");
        Check(DoorShareCore.SimFold(ulong.MaxValue) <= 0x1FFFu && DoorShareCore.SimFold(0xFFFFFFFFUL) <= 0x1FFFu, "Sim fold is 13 bits");
        Check(DoorShareCore.ClassBits(true, false, simA) == (1 | 0x1010 << 3), "class bits: ordinary + fold");
        Check(DoorShareCore.ClassBits(true, true, simA) == (3 | 0x1010 << 3), "class bits: player command");
        Check(DoorShareCore.ClassBits(true, true, ulong.MaxValue) <= 0xFFFF, "class bits fit op 40's 16 bits");
        Check(DoorShareCore.ClassBits(false, true, simA) == 0, "outside the class: nothing");

        Check(DoorShareCore.NoActorBits(false, simA) == (4 | 0x1010 << 3) && DoorShareCore.NoActorBits(true, simA) == (6 | 0x1010 << 3), "class bits: no actor");
        Check(DoorShareCore.WhyBits(DoorShareCore.kWhyIgnoreLocks) == 7 << 3 && (DoorShareCore.WhyBits(7) & 5) == 0, "outside the class: the reason, no class bit");
        Check(DoorShareCore.IdHash(portal) == 0x1BEF90ADu && DoorShareCore.IdHash(0x9ABCDEF012345678UL) == 0xFB6BD181u, "id hash (ts3native DsIdHash)");
        Check(DoorShareCore.IdHash(0) >= 2, "id hash is never 0 or 1 (op 36 lo 1 = arm)");
        Check(DoorShareCore.kSrcLock == 1 && DoorShareCore.kSrcOwnerList == 2 && DoorShareCore.kSrcLotHandler == 3 && DoorShareCore.kSrcHiddenRooms == 4
            && DoorShareCore.kSrcGreet == 5 && DoorShareCore.kSrcDesc == 13 && DoorShareCore.kSrcUnknown == 0, "doorgen sources as ts3native's DS_SRC_*");

        Check(DoorShareCore.WhyNotOrdinary(false, false, false, false, false, false) == 0, "ordinary: a human, a cat, a dog");
        Check(DoorShareCore.WhyNotOrdinary(true, false, false, false, false, false) == DoorShareCore.kWhyHorse, "a horse is not ordinary");
        Check(DoorShareCore.WhyNotOrdinary(false, true, false, false, false, false) == DoorShareCore.kWhyMummy, "a tomb mummy is not ordinary");
        Check(DoorShareCore.WhyNotOrdinary(false, false, true, false, false, false) == DoorShareCore.kWhyProprietor, "a proprietor is not ordinary");
        Check(DoorShareCore.WhyNotOrdinary(false, false, false, true, false, false) == DoorShareCore.kWhyService, "a service Sim is not ordinary");
        Check(DoorShareCore.WhyNotOrdinary(false, false, false, false, true, false) == DoorShareCore.kWhyStray, "a stray is not ordinary");
        Check(DoorShareCore.WhyNotOrdinary(false, false, false, false, false, true) == DoorShareCore.kWhyIgnoreLocks, "IgnoreDoorLocks is not ordinary");
        Check(DoorShareCore.KindNoActor(true, true) == DoorShareCore.kNoActorOnly && DoorShareCore.KindNoActor(false, true) == 0 && DoorShareCore.KindNoActor(true, false) == 0, "no actor: any portal on a lot with the game's handlers alone");

        Check(DoorShareCore.Kind(true, true, false, true, true, true) == DoorShareCore.kDoor, "plain door, lock Anybody");
        Check(DoorShareCore.Kind(true, true, false, false, false, false) == DoorShareCore.kInert, "a portal no rule names");
        Check(DoorShareCore.Kind(true, true, false, true, true, false) == DoorShareCore.kNone, "a lock type that reads the Sim");
        Check(DoorShareCore.Kind(true, true, false, true, false, true) == DoorShareCore.kNone, "a door with its own IsAllowedThrough");
        Check(DoorShareCore.Kind(true, true, true, false, false, false) == DoorShareCore.kNone, "elevator, ladder, pole, stall, jump, diving board");
        Check(DoorShareCore.Kind(true, true, true, true, true, true) == DoorShareCore.kNone, "a door that is also one of those");
        Check(DoorShareCore.Kind(false, true, false, true, true, true) == DoorShareCore.kNone, "another handler on the lot (privacy, a mod)");
        Check(DoorShareCore.Kind(true, false, false, false, false, false) == DoorShareCore.kNone, "another callback on event 59");
        Console.WriteLine(sFails == 0 ? "doorshare selftest: ok" : "doorshare selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
