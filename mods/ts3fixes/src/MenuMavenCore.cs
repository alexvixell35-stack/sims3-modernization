namespace Ts3Fixes
{
    // Game-independent rules of menumaven (MenuMaven.cs, docs/501). Self-check: tools/selftest/menumaven.
    public static class MenuMavenCore
    {
        // A recipe counts when EA's Recipe.Learn can teach it: Learnable, not a snack, at or below the top cooking level.
        public static bool Counts(bool learnable, bool snack, int levelRequired, int maxLevel)
        {
            return learnable && !snack && levelRequired <= maxLevel;
        }

        // No recipe counts (no recipe list yet, or a mod that marks none learnable): EA's tuned count decides.
        public static bool UseEa(int target)
        {
            return target <= 0;
        }

        // knownThatCount = distinct known recipes that count; known recipes no longer installed are not in it.
        public static bool Done(int knownThatCount, int target)
        {
            return target > 0 && knownThatCount >= target;
        }
    }
}
