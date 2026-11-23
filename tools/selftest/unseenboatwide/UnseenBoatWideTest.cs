using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of UbWideCore (docs/248 §12, switch unseenboatwide).
static class UnseenBoatWideTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static UbWideCore.Member Adult()
    {
        UbWideCore.Member m = new UbWideCore.Member();
        m.Human = true; m.ChildOrAbove = true;
        return m;
    }

    static void Main()
    {
        // party rules
        List<UbWideCore.Member> p = new List<UbWideCore.Member>();
        Check(UbWideCore.PartyWhy(p) == null, "empty party passes");
        p.Add(Adult());
        Check(UbWideCore.PartyWhy(p) == null, "one unseen adult townie passes");
        UbWideCore.Member m = Adult(); m.Pet = true; m.Human = false; p.Add(m);
        Check(UbWideCore.PartyWhy(p) == "party pet", "a pet or horse keeps the exact path (petlonghome owns animals)");
        p.RemoveAt(1);
        m = Adult(); m.Exempt = true; p.Add(m);
        Check(UbWideCore.PartyWhy(p) == "party household", "a household or selected member keeps the exact path");
        p[1] = Adult(); m = p[1]; m.NoHousehold = true; p[1] = m;
        Check(UbWideCore.PartyWhy(p) == "party household", "a member of unknown scope counts as household");
        m = Adult(); m.Seen = true; p[1] = m;
        Check(UbWideCore.PartyWhy(p) == "party seen", "a seen member keeps the exact path");
        m = Adult(); m.Held = true; p[1] = m;
        Check(UbWideCore.PartyWhy(p) == "party held", "a riding or carried member keeps the exact path");
        m = Adult(); m.ChildOrAbove = false; p[1] = m;
        Check(UbWideCore.PartyWhy(p) == "party toddler", "a toddler or baby keeps the exact path");
        m = Adult(); m.Child = true; p[1] = m;
        Check(UbWideCore.PartyWhy(p) == "party child alone", "a child member with no parent in the party keeps the exact path");
        m.ParentInParty = true; p[1] = m;
        Check(UbWideCore.PartyWhy(p) == null, "a child member with a parent in the party passes");
        List<UbWideCore.Member> big = new List<UbWideCore.Member>();
        for (int i = 0; i <= UbWideCore.kMaxParty; i++) big.Add(Adult());
        Check(UbWideCore.PartyWhy(big) == "party size", "more than kMaxParty members keeps the exact path");
        big.RemoveAt(0);
        Check(UbWideCore.PartyWhy(big) == null, "kMaxParty members pass");

        // the trip's own Sim
        Check(UbWideCore.ChildOk(true, false, false), "teen or older passes without a parent");
        Check(!UbWideCore.ChildOk(true, true, false), "a child alone is refused");
        Check(UbWideCore.ChildOk(true, true, true), "a child with a parent passes");
        Check(!UbWideCore.ChildOk(false, false, true), "a toddler never passes");
        Check(UbWideCore.GroupOk(true) && !UbWideCore.GroupOk(false), "only the group's leader moves the group");

        // owned boat
        Check(UbWideCore.OwnedMayStay(false, false, false, false), "a parked or inventory boat that is only the way across may stay");
        Check(!UbWideCore.OwnedMayStay(true, false, false, false), "a temporary refusal keeps the exact path");
        Check(!UbWideCore.OwnedMayStay(false, true, false, false), "a boat the interaction chose keeps the exact path");
        Check(!UbWideCore.OwnedMayStay(false, false, true, false), "a reserved boat keeps the exact path");
        Check(!UbWideCore.OwnedMayStay(false, false, false, true), "an occupied boat keeps the exact path");

        // colour combine
        Check(UbWideCore.Combine(new int[] { 0, 0, 0 }) == 0, "all unknown: unknown");
        Check(UbWideCore.Combine(new int[] { 0, -1, 0, -1 }) == -1, "only other colours: a crossing");
        Check(UbWideCore.Combine(new int[] { 1, 0 }) == 1, "only the start colour: one land area");
        Check(UbWideCore.Combine(new int[] { 1, -1, 0 }) == 0, "a mix (a shore between components): unknown");
        Check(UbWideCore.Combine(new int[0]) == 0, "no probes: unknown");
        float[] r = UbWideCore.Ring(8f);
        Check(r.Length == 10 && r[0] == 0f && r[1] == 0f && r[2] == 8f && r[9] == -8f, "ring: centre then 4 points at 8 m");

        // goal kinds and the household lot
        Check(UbWideCore.GoalKind(true, false) == UbWideCore.kGoalOutdoor, "outdoor dry goal");
        Check(UbWideCore.GoalKind(false, false) == UbWideCore.kGoalIndoorOrRaised, "indoor or raised dry goal");
        Check(UbWideCore.GoalKind(false, true) == UbWideCore.kGoalWater && UbWideCore.GoalKind(true, true) == UbWideCore.kGoalWater, "water wins: never placed in water");
        Check(UbWideCore.HhLotOk(true, false, false), "a visitor to the active household's lot may go to its street side");
        Check(!UbWideCore.HhLotOk(true, true, false), "its own home: not this rule");
        Check(!UbWideCore.HhLotOk(true, false, true), "the active household's houseboat: exact");
        Check(!UbWideCore.HhLotOk(false, false, false), "not the active household's lot: not this rule");

        // counters
        int[] c = new int[4200];
        UbWideCore.CountRules(c, UbWideCore.kGroup | UbWideCore.kChild | UbWideCore.kOwned);
        Check(c[4184] == 1 && c[4186] == 1 && c[4189] == 1 && c[4181] == 0 && c[4187] == 0, "one count per rule bit, in its own slot");
        UbWideCore.CountRules(c, 0);
        int sum = 0;
        for (int i = 4181; i <= 4199; i++) sum += c[i];
        Check(sum == 3, "no rule: no count");
        Check(UbWideCore.kSlotMemberLeft <= 4199 && UbWideCore.kSlotHhLot >= 4181, "slots inside 4181-4199");
        Check(UbWideCore.RuleText(0) == "-" && UbWideCore.RuleText(UbWideCore.kHhLot | UbWideCore.kIndoor) == "household lot+indoors", "rule text");
        float dx, dz;
        UbWideCore.MemberOffset(1, out dx, out dz);
        Check(Math.Abs(dx * dx + dz * dz - 2.25f) < 0.01f, "members on a 1.5 m ring");

        Console.WriteLine(sFails == 0 ? "unseenboatwide selftest: ok" : "unseenboatwide selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
