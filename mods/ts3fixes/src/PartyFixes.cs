using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Situations;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Party system EA bugs in the core house-party flow (docs/554, static audit). Two switches, all off by default; each is an EA bug fix, so it applies
    // to household hosts and guests too (results equal EA's intent, see docs/554 for the evidence per bug).
    //
    // partyoutcome: two outcome errors in HouseParty [verified].
    //   lame: with University Life installed, GetPartyOutcome's "below the EP9 lame threshold" branch only gives a lame
    //     party inside a University world; elsewhere it leaves Neutral and the else-if chain skips the base lame test, so
    //     outside University worlds no party is ever lame. Fix: a Neutral result whose score (mTotalPartyScore, written by
    //     the same call) is below kThrewLamePartyBuffMoodRequirement becomes Bad, which is what the base test gives (any
    //     other path to Neutral already passed that test).
    //   per guest: GetPartyOutcomeForSim tests Good before Amazing / Killer / Raging, the reverse of GetPartyOutcome's
    //     order, so with ascending thresholds every happy guest gets the Good leaving message. Fix: the same tiers in
    //     GetPartyOutcome's order (Raging, Killer, Amazing with EP9; Good or Legendary host; Lame; else Neutral). Only the
    //     guest's leaving message uses it [verified: one caller, LeavingMessage].
    // partymotive: HouseParty.Happening gives each guest an "enjoy party" motive and removes it at CleanUp only for Sims
    //   still in Guests; RemoveGuest moves a leaving guest to SimsWhoLeftParty without removing it, so every guest that
    //   left before the end keeps the party motive (Motives keep a created motive until RemoveMotive) [verified]. Fix: after
    //   HouseParty.RemoveGuest, while the party is in Happening, the guest's party motive is removed the way Happening
    //   does it (its own partyMotives entry).
    // Counters ([2398-2399] unused: partylisteners was dropped to a candidate, docs/554): [2400] lame outcomes restored,
    // [2401] guest outcomes changed by the tier order, [2402] partyoutcome errors, [2403] party motives removed from
    // leaving guests, [2404] partymotive errors. Harness check: ApartmentLeaveCheck "throw" / "partyreport" / "partyend"
    // (AptLeavePartyCheck.cs) and PartyFixCheck below.
    public static partial class Fixes
    {
        static void ApplyPartyFixes()
        {
            Patch(typeof(HouseParty), "GetPartyOutcome", typeof(PfOutcomeHook), "OutcomeHook", "OutcomeOrig");
            Patch(typeof(HouseParty), "GetPartyOutcomeForSim", typeof(PfOutcomeHook), "GuestHook", "GuestOrig");
            Patch(typeof(HouseParty), "RemoveGuest", typeof(PfRemoveHook), "RemoveHook", "RemoveOrig");
        }

        const BindingFlags kPfAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static FieldInfo PfField(Type t, string name) { return t.GetField(name, kPfAll); }
        static readonly FieldInfo sPfScore = PfField(typeof(HouseParty), "mTotalPartyScore");
        static readonly FieldInfo sPfGuests = PfField(typeof(HouseParty), "mNumberOfGuests");
        static readonly FieldInfo sPfMotives = PfField(typeof(HouseParty.Happening), "partyMotives");

        internal static float PfTuning(string name)
        {
            FieldInfo f = PfField(typeof(HouseParty), name);
            return f != null ? (float)f.GetValue(null) : float.NaN;
        }

        // GetPartyOutcome's tier order for one guest's hosted mood (partyoutcome).
        internal static Party.PartyOutcomes PfGuestOutcome(HouseParty p, Sim sim)
        {
            float mood = sim.MoodManager.MoodValue + (p.SimsWhoLeftEarly.Contains(sim) ? HostedSituation.kPenaltyForEarlyDeparture : 0);
            bool ep9 = GameUtils.IsInstalled(ProductVersion.EP9), legendary = p.Host != null && p.Host.HasTrait(Sims3.Gameplay.ActorSystems.TraitNames.LegendaryHost);
            if (ep9 && mood > PfTuning("kThrewRagingPartyBuffMoodRequirement")) return Party.PartyOutcomes.kRagingParty;
            if (ep9 && mood > PfTuning("kThrewKillerPartyBuffMoodRequirement")) return Party.PartyOutcomes.kKillerParty;
            if (ep9 && mood > PfTuning("kThrewAmazingPartyBuffMoodRequirement")) return Party.PartyOutcomes.kAmazingParty;
            if (mood > PfTuning("kThrewGreatPartyBuffMoodRequirement") || legendary) return Party.PartyOutcomes.kGoodParty;
            if (mood < PfTuning("kThrewLamePartyBuffMoodRequirement")) return Party.PartyOutcomes.kBadParty;
            return Party.PartyOutcomes.kNeutralParty;
        }

        // partyoutcome lame part: would the fix turn this result into Bad? (shared with the check)
        internal static bool PfLameMissed(HouseParty p, Party.PartyOutcomes r)
        {
            if (r != Party.PartyOutcomes.kNeutralParty || sPfScore == null || sPfGuests == null) return false;
            if ((int)sPfGuests.GetValue(p) <= 0) return false;
            return (float)sPfScore.GetValue(p) < PfTuning("kThrewLamePartyBuffMoodRequirement");
        }

        public class PfOutcomeHook
        {
            public Party.PartyOutcomes OutcomeHook()
            {
                Party.PartyOutcomes r = OutcomeOrig();
                if (!ShortcutOn("partyoutcome")) return r;
                try
                {
                    if (PfLameMissed((HouseParty)(object)this, r)) { PatchCounters()[2400]++; return Party.PartyOutcomes.kBadParty; }
                }
                catch (Exception) { PatchCounters()[2402]++; }
                return r;
            }
            public Party.PartyOutcomes OutcomeOrig() { return Party.PartyOutcomes.kNeutralParty; } // receives HouseParty.GetPartyOutcome

            public Party.PartyOutcomes GuestHook(Sim sim)
            {
                if (!ShortcutOn("partyoutcome") || sim == null) return GuestOrig(sim);
                Party.PartyOutcomes ea = GuestOrig(sim);
                try
                {
                    Party.PartyOutcomes r = PfGuestOutcome((HouseParty)(object)this, sim);
                    if (r != ea) PatchCounters()[2401]++;
                    return r;
                }
                catch (Exception) { PatchCounters()[2402]++; return ea; }
            }
            public Party.PartyOutcomes GuestOrig(Sim sim) { return Party.PartyOutcomes.kNeutralParty; } // receives HouseParty.GetPartyOutcomeForSim
        }

        public class PfRemoveHook
        {
            public void RemoveHook(Sim sim, bool bLeftEarly)
            {
                RemoveOrig(sim, bLeftEarly);
                if (sim == null || !ShortcutOn("partymotive")) return;
                try
                {
                    HouseParty p = (HouseParty)(object)this;
                    HouseParty.Happening h = p.Child as HouseParty.Happening;
                    Dictionary<Sim, CommodityKind> motives = h != null && sPfMotives != null ? sPfMotives.GetValue(h) as Dictionary<Sim, CommodityKind> : null;
                    CommodityKind k;
                    if (motives == null || !motives.TryGetValue(sim, out k)) return;
                    motives.Remove(sim);
                    if (k == CommodityKind.None || sim.HasBeenDestroyed || sim.Motives == null) return;
                    sim.Motives.RemoveMotive(k);
                    PatchCounters()[2403]++;
                }
                catch (Exception) { PatchCounters()[2404]++; }
            }
            public void RemoveOrig(Sim sim, bool bLeftEarly) { } // receives HouseParty.RemoveGuest's original header
        }
    }
}
