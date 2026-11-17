using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Entertainment;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.Gardening;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Insect;
using Sims3.Gameplay.Objects.Misc;
using Sims3.Gameplay.Objects.Pets;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EP batch 1, part b (docs/587 leads 12-20 plus extras found while re-reading the patched functions; batch EP1).
    // Every switch off by default; none of these methods is changed by simler90 UPDATE242. Checks: EpBatch1Checks.cs.
    public static partial class Fixes
    {
        // ---- EP1 §17 cattoypickerrel (tuning kCatToyPickerRel) ---------------------------------------------------------
        // [verified, decompiled + shipped tuning] CatToyWallDancer.GetValidSims admits a Sim with no relationship only when
        // `requiredLtr > 0`, the reverse of the threshold (no relationship counts as liking 0): with the shipped -10, Sims
        // and pets the actor has never met are left out of Play With, and the interaction is hidden when nobody else on the
        // lot is known. Fix: no relationship passes when 0 >= requiredLtr; everything else as EA (order kept).
        // Household-visible. Counters: [3244] strangers added, [3245] left out with the switch off, [3246] errors.
        static readonly MethodInfo sCtValidSim = typeof(CatToyWallDancer).GetMethod("IsValidSim", kEpStatic, null, new Type[] { typeof(Sim) }, null);
        static readonly MethodInfo sCtValidPet = typeof(CatToyWallDancer).GetMethod("IsValidPet", kEpStatic, null, new Type[] { typeof(Sim) }, null);
        static Predicate<Sim> sCtSimOk, sCtPetOk;

        // Pure: is a candidate admitted?
        internal static bool CtAdmit(bool on, bool hasRel, float liking, float requiredLtr)
        {
            if (hasRel) return liking >= requiredLtr;
            return on ? requiredLtr <= 0f : requiredLtr > 0f;
        }

        static void ApplyCatToyPickerRel()
        {
            if (sCtValidSim == null || sCtValidPet == null) { AppliedLog().Add("cattoypickerrel: not found: CatToyWallDancer.IsValidSim/IsValidPet"); return; }
            sCtSimOk = (Predicate<Sim>)Delegate.CreateDelegate(typeof(Predicate<Sim>), sCtValidSim);
            sCtPetOk = (Predicate<Sim>)Delegate.CreateDelegate(typeof(Predicate<Sim>), sCtValidPet);
            Patch(typeof(CatToyWallDancer), "GetValidSims", new Type[] { typeof(Sim), typeof(Lot), typeof(bool), typeof(float) }, typeof(BCtHook), "ValidHook", "ValidOrig");
        }

        public class BCtHook
        {
            public static List<Sim> ValidHook(Sim actor, Lot lot, bool petsOnly, float requiredLtr)
            {
                // Our copy is EA's loop with CtAdmit as the test (same order, null when nobody), so the switch-off result
                // is EA's; with the switch on a stranger passes when 0 >= requiredLtr (also for a positive threshold,
                // where EA admitted strangers: the same reversed test).
                bool on = ShortcutOn("cattoypickerrel");
                List<Sim> r = null;
                try
                {
                    Predicate<Sim> ok = petsOnly ? sCtPetOk : sCtSimOk;
                    foreach (Sim s in petsOnly ? lot.GetAnimals() : lot.GetSims())
                    {
                        if (s == actor || !ok(s)) continue;
                        Relationship rel = actor.GetRelationship(s, false);
                        bool admit = CtAdmit(on, rel != null, rel == null ? 0f : rel.CurrentLTRLiking, requiredLtr);
                        if (rel == null && admit != CtAdmit(!on, false, 0f, requiredLtr) && CtAdmit(true, false, 0f, requiredLtr))
                            PatchCounters()[on ? 3244 : 3245]++;   // a stranger the fix lists: added (on) / left out (off)
                        if (!admit) continue;
                        if (r == null) r = new List<Sim>();
                        r.Add(s);
                    }
                }
                catch (Exception) { PatchCounters()[3246]++; return ValidOrig(actor, lot, petsOnly, requiredLtr); }
                return r;
            }
            public static List<Sim> ValidOrig(Sim actor, Lot lot, bool petsOnly, float requiredLtr) { return null; } // CatToyWallDancer.GetValidSims
        }

    }
}
