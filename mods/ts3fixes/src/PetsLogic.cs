using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Skills;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // Pets logic fixes, no routing (docs/572 §13-15), each off by default.
    //
    // horsetraitscore (kHorseTraitScore, docs/572 §13) [verified, EquestrianCenter.cs EnterEquestrianCompetition]:
    //   AddScoreForTraits loops over Fast / Agile / Lazy / Hates Jumping and tests Actor.HasTrait - the RIDER, who can never
    //   have pet traits (the line before reads Horse.TraitManager and discards it). The trait bonus / penalty is always 0.
    //   Fix: the same loop over the horse's traits, scored with EA's GetScoreForTrait. Counters [2630] scores computed on
    //   the horse, [2631] errors.
    // wildhorsegender (kWildHorseGender, docs/572 §14) [verified, WildHorseCreator.DecideGender]: kFemalePercent (80, "share
    //   of wild horses that are female") is used as "if (!RandomChance(kFemalePercent)) return Female", giving 20% mares;
    //   the age roll next to it uses the same shape for the share of ADULTS, which is right there. Fix: when the lead-horse
    //   rule did not decide (a female lead exists), the rolled gender is swapped. NPC herds only.
    //   Counters [2632] genders swapped, [2633] errors.
    // littercap (kLitterCap, docs/572 §15) [verified, PetAdoption.CanAddPetInHouse / CreatePetByMom]: the neighbour litter
    //   size is capped at 6 - pets, ignoring the 10-member household cap (CanAddSpeciesToHousehold checks one more member
    //   only), so 6 Sims + 2 pets can get 3 kittens = 11 members. Fix: the cap also respects 10 - (Sims + pets), counted as
    //   EA's own CanAddSpeciesToHousehold counts. Counters [2634] caps lowered, [2635] errors.
    // Checks: PetsLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kPlAll = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sHtHorse = typeof(EquestrianCenter.EnterEquestrianCompetition).GetField("Horse", kPlAll);
        static readonly FieldInfo sHtType = typeof(EquestrianCenter.EnterEquestrianCompetition).GetField("mCompetitionType", kPlAll);
        static readonly MethodInfo sHtScore = typeof(EquestrianCenter.EnterEquestrianCompetition).GetMethod("GetScoreForTrait", kPlAll);
        static readonly TraitNames[] sHtTraits = new TraitNames[] { TraitNames.FastPet, TraitNames.AgilePet, TraitNames.LazyHorse, TraitNames.HatesJumpingPet };

        internal static string HtMissing()
        {
            string m = "";
            if (sHtHorse == null) m += "EnterEquestrianCompetition.Horse ";
            if (sHtType == null) m += "EnterEquestrianCompetition.mCompetitionType ";
            if (sHtScore == null) m += "EnterEquestrianCompetition.GetScoreForTrait ";
            return m.Trim();
        }

        static void ApplyPetsLogic()
        {
            if (HtMissing().Length > 0) AppliedLog().Add("horsetraitscore: members not found (" + HtMissing() + "), not patched");
            else Patch(typeof(EquestrianCenter.EnterEquestrianCompetition), "AddScoreForTraits", typeof(HorseTraitHook), "TraitsHook", "TraitsOrig");
            Patch(typeof(WildHorseCreator), "DecideGender", typeof(WildHorseGenderHook), "GenderHook", "GenderOrig");
            Patch(typeof(PetAdoption), "CanAddPetInHouse", new Type[] { typeof(Household), typeof(int).MakeByRefType() }, typeof(LitterCapHook), "CapHook", "CapOrig");
        }

        // ---- horsetraitscore ----
        // Whose traits are scored: EA the rider's, fixed the horse's.
        internal static bool HtUseHorse(bool on)
        {
            return on;
        }

        public class HorseTraitHook
        {
            public int TraitsHook()
            {
                try
                {
                    if (HtUseHorse(ShortcutOn("horsetraitscore")))
                    {
                        Sim horse = sHtHorse.GetValue((object)this) as Sim;
                        if (horse != null)
                        {
                            object type = sHtType.GetValue((object)this);
                            int n = 0;
                            foreach (TraitNames t in sHtTraits)
                                if (horse.HasTrait(t)) n += (int)sHtScore.Invoke((object)this, new object[] { t, type });
                            PatchCounters()[2630]++;
                            return n;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2631]++; }
                return TraitsOrig();
            }
            public int TraitsOrig() { return 0; }
        }

        // ---- wildhorsegender ----
        internal static CASAgeGenderFlags WgGender(bool on, bool leadRuleDecided, CASAgeGenderFlags ea)
        {
            if (!on || leadRuleDecided) return ea;
            return ea == CASAgeGenderFlags.Female ? CASAgeGenderFlags.Male : ea == CASAgeGenderFlags.Male ? CASAgeGenderFlags.Female : ea;
        }

        public class WildHorseGenderHook
        {
            public CASAgeGenderFlags GenderHook()
            {
                CASAgeGenderFlags ea = GenderOrig();
                try
                {
                    if (!ShortcutOn("wildhorsegender")) return ea;
                    PetPool pool;
                    WildHorsePool wp = PetPoolManager.TryGetPetPool(PetPoolType.WildHorse, out pool) ? pool as WildHorsePool : null;
                    SimDescription lead = wp == null ? null : wp.GetLeadHorse();
                    bool leadRule = wp != null && (lead == null || lead.Gender != CASAgeGenderFlags.Female);  // EA's first branch
                    CASAgeGenderFlags r = WgGender(true, leadRule, ea);
                    if (r != ea) PatchCounters()[2632]++;
                    return r;
                }
                catch (Exception) { PatchCounters()[2633]++; return ea; }
            }
            public CASAgeGenderFlags GenderOrig() { return CASAgeGenderFlags.None; }
        }

        // ---- littercap ----
        internal static int LcCap(bool on, int eaMax, int sims, int pets)
        {
            return on ? Math.Min(eaMax, 10 - (sims + pets)) : eaMax;
        }

        public class LitterCapHook
        {
            public static bool CapHook(Household house, out int maxnumber)
            {
                bool ok = CapOrig(house, out maxnumber);
                try
                {
                    if (ok && house != null && ShortcutOn("littercap"))
                    {
                        int sims, pets;
                        house.GetNumberOfSimsAndPets(true, out sims, out pets);
                        int cap = LcCap(true, maxnumber, sims, pets);
                        if (cap < maxnumber) { maxnumber = Math.Max(cap, 0); PatchCounters()[2634]++; }
                    }
                }
                catch (Exception) { PatchCounters()[2635]++; }
                return ok;
            }
            public static bool CapOrig(Household house, out int maxnumber) { maxnumber = 0; return false; }
        }
    }
}
