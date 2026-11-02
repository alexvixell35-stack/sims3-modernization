using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Skills;

namespace Ts3Fixes
{
    // menumaven (docs/501): Cooking.IsMenuMaven decides both the Menu Maven skill challenge and the Culinary Librarian
    // lifetime wish ("know every recipe"). EA compares the Sim's known recipe count with the tuned
    // Cooking.kRecipesLearnedForMenuMaven, 28 in EA's tuning: never raised for the expansions, store recipes or custom
    // recipe mods, so the wish can complete with most recipes unknown.
    // Switch on: the Sim must know every recipe of this install that EA's Recipe.Learn can teach (Learnable, not a snack,
    // at or below the top cooking level), counted live from Recipe.Recipes, so recipes a mod or the store adds count with
    // no tuning edit. Known recipes that are no longer installed do not count. EA's rule decides when no recipe counts.
    // [2000] calls answered by the live rule, [2001] of them true.
    public static partial class Fixes
    {
        public class MenuMavenHook
        {
            public bool IsHook()
            {
                if (!ShortcutOn("menumaven")) return IsOrig();
                int known, target;
                if (!MenuMavenCount((Cooking)(object)this, out known, out target)) return IsOrig();
                bool done = MenuMavenCore.Done(known, target);
                int[] pc = PatchCounters();
                pc[2000]++;
                if (done) pc[2001]++;
                return done;
            }
            public bool IsOrig() { return false; } // receives Cooking.IsMenuMaven's original header
        }

        // Keys of the recipes that count, value = the stamp of the last call that saw the key in a Sim's known list.
        static Dictionary<string, int> sMmKeys;
        static List<Recipe> sMmList;
        static int sMmListCount = -1, sMmMax = -1, sMmStamp;

        // known = distinct known recipes that count; target = recipes that count. False: EA's rule decides.
        static bool MenuMavenCount(Cooking c, out int known, out int target)
        {
            known = 0;
            target = 0;
            List<Recipe> all = Recipe.Recipes;
            if (c == null || c.KnownRecipes == null || all == null) return false;
            int max = c.MaxSkillLevel;
            // rebuilt when the list changes (a recipe added after load) or the top level does
            if (sMmKeys == null || !object.ReferenceEquals(all, sMmList) || all.Count != sMmListCount || max != sMmMax)
            {
                Dictionary<string, int> keys = new Dictionary<string, int>();
                foreach (Recipe r in all)
                    if (r != null && r.Key != null && MenuMavenCore.Counts(r.Learnable, r.IsSnack, r.CookingSkillLevelRequired, max)) keys[r.Key] = 0;
                sMmKeys = keys;
                sMmList = all;
                sMmListCount = all.Count;
                sMmMax = max;
                sMmStamp = 0;
            }
            target = sMmKeys.Count;
            if (MenuMavenCore.UseEa(target)) return false;
            int stamp = ++sMmStamp;
            foreach (string k in c.KnownRecipes)
            {
                int seen;
                if (k != null && sMmKeys.TryGetValue(k, out seen) && seen != stamp)
                {
                    sMmKeys[k] = stamp;
                    known++;
                }
            }
            return true;
        }

        static Cooking sMmCooking;
        static List<string> sMmSaved;
        // Agent checks (repro menumaven-all-recipes-check). The harness does not save; "stop" puts the known list back anyway.
        //   "pick":       the first active-household Sim, teen or older, in the world; gives it the Cooking skill if it has
        //                 none, saves its known recipes and empties the list.
        //   "learn <n>":  EA's Cooking.AddRecipe for the next n recipes that count (n = "tuned": EA's tuned number).
        //   "learnall":   AddRecipe for every recipe that counts.
        //   "unlearn":    removes the last known recipe that counts.
        //   "state":      known, counting known / target, tuned, NumLearnableRecipes, the recipe census, IsMenuMaven, [2000] [2001].
        //   "stop":       puts the saved known list back.
        public static string MenuMavenCheck(string what)
        {
            if (what == "pick")
            {
                sMmCooking = null;
                if (Household.ActiveHousehold != null)
                    foreach (SimDescription d in Household.ActiveHousehold.SimDescriptions)
                        if (d.TeenOrAbove && d.CreatedSim != null && d.CreatedSim.InWorld && d.SkillManager != null)
                        {
                            Cooking c = d.SkillManager.GetSkill<Cooking>(SkillNames.Cooking);
                            if (c == null) c = d.SkillManager.AddElement(SkillNames.Cooking) as Cooking;
                            if (c == null || c.KnownRecipes == null) continue;
                            sMmCooking = c;
                            sMmSaved = new List<string>(c.KnownRecipes);
                            c.KnownRecipes.Clear();
                            return d.FullName + " (saved " + sMmSaved.Count + " known recipes, list emptied); switch " + ShortcutOn("menumaven") + "; " + MenuMavenCheck("state");
                        }
                return "no active-household Sim, teen or older, in the world";
            }
            if (sMmCooking == null) return "pick first";
            Cooking ck = sMmCooking;
            if (what == "stop")
            {
                ck.KnownRecipes.Clear();
                ck.KnownRecipes.AddRange(sMmSaved);
                sMmCooking = null;
                return "stopped, " + sMmSaved.Count + " known recipes put back";
            }
            if (what.StartsWith("learn"))
            {
                int n = what == "learnall" ? int.MaxValue : what == "learn tuned" ? MenuMavenTuned() : int.Parse(what.Substring(6));
                int added = 0;
                foreach (Recipe r in Recipe.Recipes)
                {
                    if (added >= n) break;
                    if (r != null && r.Key != null && MenuMavenCore.Counts(r.Learnable, r.IsSnack, r.CookingSkillLevelRequired, ck.MaxSkillLevel) && !ck.KnownRecipes.Contains(r.Key) && ck.AddRecipe(r)) added++;
                }
                return "added " + added + "; " + MenuMavenCheck("state");
            }
            if (what == "unlearn")
            {
                for (int i = ck.KnownRecipes.Count - 1; i >= 0; i--)
                {
                    Recipe r;
                    if (Recipe.NameToRecipeHash.TryGetValue(ck.KnownRecipes[i], out r) && MenuMavenCore.Counts(r.Learnable, r.IsSnack, r.CookingSkillLevelRequired, ck.MaxSkillLevel))
                    {
                        string k = ck.KnownRecipes[i];
                        ck.KnownRecipes.RemoveAt(i);
                        return "removed " + k + "; " + MenuMavenCheck("state");
                    }
                }
                return "nothing to remove; " + MenuMavenCheck("state");
            }
            if (what == "state")
            {
                int total = 0, snack = 0, notLearnable = 0, above = 0;
                foreach (Recipe r in Recipe.Recipes)
                {
                    if (r == null) continue;
                    total++;
                    if (r.IsSnack) snack++;
                    else if (!r.Learnable) notLearnable++;
                    else if (r.CookingSkillLevelRequired > ck.MaxSkillLevel) above++;
                }
                int known, target;
                bool live = MenuMavenCount(ck, out known, out target);
                int[] pc = PatchCounters();
                return "known " + ck.KnownRecipes.Count + ", counting known " + known + " of target " + target + (live ? "" : " (EA rule)")
                    + ", tuned " + MenuMavenTuned() + ", NumLearnableRecipes " + Recipe.NumLearnableRecipes
                    + ", recipes " + total + " (snack " + snack + ", not learnable " + notLearnable + ", above level " + above + ")"
                    + ", IsMenuMaven " + ck.IsMenuMaven() + ", reads " + pc[2000] + ", done " + pc[2001];
            }
            return "unknown check " + what + " (pick, learn <n>|tuned, learnall, unlearn, state, stop)";
        }

        static int MenuMavenTuned()
        {
            FieldInfo f = typeof(Cooking).GetField("kRecipesLearnedForMenuMaven", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return f != null ? (int)f.GetValue(null) : -1;
        }
    }
}
