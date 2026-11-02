using System;
using Ts3Fixes;

// Offline self-check of MenuMavenCore (docs/501): every input of the recipe rule against the rule written out a second
// time, the done rule over a range, and the named cases from the doc.
static class MenuMavenTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        for (int m = 0; m < 4; m++)
            for (int lvl = 0; lvl <= 12; lvl++)
            {
                bool learnable = (m & 1) != 0, snack = (m & 2) != 0;
                Check(MenuMavenCore.Counts(learnable, snack, lvl, 10) == (learnable && !snack && lvl <= 10), "counts " + m + " " + lvl);
            }
        for (int target = -1; target <= 70; target++)
        {
            Check(MenuMavenCore.UseEa(target) == (target <= 0), "useEa " + target);
            for (int known = 0; known <= 72; known++)
                Check(MenuMavenCore.Done(known, target) == (target > 0 && known >= target), "done " + known + "/" + target);
        }
        Check(!MenuMavenCore.Done(28, 64), "EA's 28 of a 64-recipe install: not done");
        Check(MenuMavenCore.Done(64, 64), "every recipe that counts: done");
        Check(!MenuMavenCore.Done(63, 64), "one missing: not done");
        Check(!MenuMavenCore.Done(64, 65), "a mod adds a recipe: not done until it is learned");
        Check(!MenuMavenCore.Counts(true, true, 0, 10), "a snack never counts (Recipe.Learn refuses snacks)");
        Check(!MenuMavenCore.Counts(false, false, 0, 10), "a recipe that is not Learnable never counts");
        Check(!MenuMavenCore.Counts(true, false, 11, 10), "a recipe above the top cooking level never counts");
        Check(MenuMavenCore.UseEa(0), "no recipe counts: EA's rule");
        Console.WriteLine(sFails == 0 ? "menumaven selftest: ok" : "menumaven selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
