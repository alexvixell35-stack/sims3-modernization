using System.Collections.Generic;

namespace Ts3Fixes
{
    // docs/248 §12 unseenboatwide (default off, needs unseenboat): pure rules of the wider unseen boat skip, offline-tested
    // (tools/selftest/unseenboatwide). Only refusals of UbGate / UbTrip are relaxed; with the switch off nothing here is asked.
    public static class UbWideCore
    {
        // counter slots [4181-4195], one per relaxed rule plus its guards (Fixes.cs, docs/registry.md)
        public const int kSlotHhLot = 4181, kSlotIndoor = 4182, kSlotVerdict = 4183, kSlotGroup = 4184, kSlotFollowers = 4185,
            kSlotChild = 4186, kSlotUnknownCross = 4187, kSlotUnknownSame = 4188, kSlotOwned = 4189, kSlotMembersPlaced = 4190,
            kSlotPartyRefused = 4191, kSlotNoStreet = 4192, kSlotErrors = 4193, kSlotShortcuts = 4194, kSlotMemberLeft = 4195;
        public const int kMaxParty = 8;  // members besides the leader; a bigger party keeps the exact path

        // rule bits of one trip (which relaxations it needed); the trip counts once per bit when it passes the gate
        public const int kHhLot = 1, kIndoor = 2, kVerdict = 4, kGroup = 8, kFollowers = 16, kChild = 32, kUnknown = 64, kOwned = 128;
        static readonly int[] kBits = { kHhLot, kIndoor, kVerdict, kGroup, kFollowers, kChild, kUnknown, kOwned };
        static readonly int[] kBitSlots = { kSlotHhLot, kSlotIndoor, kSlotVerdict, kSlotGroup, kSlotFollowers, kSlotChild, kSlotUnknownCross, kSlotOwned };
        public static void CountRules(int[] c, int rules)
        {
            for (int i = 0; i < kBits.Length; i++) if ((rules & kBits[i]) != 0) c[kBitSlots[i]]++;
        }
        public static string RuleText(int rules)
        {
            string[] names = { "household lot", "indoors", "verdict", "group", "followers", "child", "unknown colours", "owned boat" };
            string s = null;
            for (int i = 0; i < kBits.Length; i++) if ((rules & kBits[i]) != 0) s = s == null ? names[i] : s + "+" + names[i];
            return s ?? "-";
        }

        // ---- party (group members or route followers) ----
        // one member as UbGate sees it; Child = child age (not teen or older), ParentInParty = a teen-or-older parent or step-parent
        // of it is the leader or another member
        public struct Member
        {
            public bool Pet, Human, ChildOrAbove, Child, Held, Exempt, NoHousehold, Seen, ParentInParty;
        }
        // null = every member may be moved with the leader; else the refusal. Pets and horses stay with EA and petlonghome (it places
        // their own trips home), so a party with an animal keeps the exact path
        public static string PartyWhy(IList<Member> members)
        {
            if (members.Count > kMaxParty) return "party size";
            foreach (Member m in members)
            {
                if (m.Pet || !m.Human) return "party pet";
                if (!m.ChildOrAbove) return "party toddler";
                if (m.Held) return "party held";
                if (m.Exempt || m.NoHousehold) return "party household";  // household, selected or unknown scope: never moved
                if (m.Seen) return "party seen";
                if (m.Child && !m.ParentInParty) return "party child alone";
            }
            return null;
        }
        // the trip's own Sim under teen: only with a parent in the party (a child alone keeps the exact path)
        public static bool ChildOk(bool childOrAbove, bool child, bool parentInParty) { return childOrAbove && (!child || parentInParty); }

        public static bool GroupOk(bool isLeader) { return isLeader; }  // a member's own plan never acts: the leader moves the group

        // ---- owned boat ----
        // the boat GetOwnedAndUsableBoat would hand GetInBoatRouteAction may stay where it is when it is only the Sim's way across:
        // EA fetches a boat from a mooring post or the inventory for every trip (GetInBoatRouteAction: RemoveByForce / UnReserveSpot,
        // then PlaceAt) [verified, decompiled], and Park puts it in the driver's inventory when it cannot moor. Refused: a boat the
        // current interaction chose (ISelectVehicleInteraction), the reserved boat, a temporary refusal, a boat with occupants
        public static bool OwnedMayStay(bool temporary, bool selectedByInteraction, bool reserved, bool occupied)
        {
            return !temporary && !selectedByInteraction && !reserved && !occupied;
        }

        // ---- unknown colours: lot entry fallback ----
        // answers of ts3native's colour query at the goal's probe points: 1 = start's colour, -1 = another, 0 = unknown. One
        // component only when they agree; a mix is a shore between components and stays unknown
        public static int Combine(IList<int> answers)
        {
            bool same = false, diff = false;
            foreach (int a in answers) { if (a > 0) same = true; else if (a < 0) diff = true; }
            if (same == diff) return 0;
            return same ? 1 : -1;
        }
        // probe offsets around a point (x, z pairs): the point itself, then a ring of 4 at radius r
        public static float[] Ring(float r) { return new float[] { 0f, 0f, r, 0f, -r, 0f, 0f, r, 0f, -r }; }
        public const float kProbeRingM = 8f;

        // ---- goal placement ----
        // goal kinds the street side (or mailbox) placement may take; water never
        public const int kGoalOutdoor = 0, kGoalIndoorOrRaised = 1, kGoalWater = 2;
        public static int GoalKind(bool outdoorGround, bool water) { return water ? kGoalWater : outdoorGround ? kGoalOutdoor : kGoalIndoorOrRaised; }
        // the active household's lot as a goal: only a visitor (not its own home), only to its street side, never a houseboat
        public static bool HhLotOk(bool goalIsActiveLot, bool goalIsSimHome, bool houseboat) { return goalIsActiveLot && !goalIsSimHome && !houseboat; }

        // placements around the leader (1.5 m ring, 72 degrees apart): x, z offsets of member i (1-based)
        public static void MemberOffset(int i, out float dx, out float dz)
        {
            double a = i * 1.2566;
            dx = 1.5f * (float)System.Math.Cos(a);
            dz = 1.5f * (float)System.Math.Sin(a);
        }
    }
}
