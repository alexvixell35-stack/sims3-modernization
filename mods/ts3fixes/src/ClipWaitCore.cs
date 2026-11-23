namespace Ts3Fixes
{
    // Game-independent rules of ClipWait.cs (clipwait / clipwaithh, docs/686 §9), checked offline by tools/selftest/clipwait.
    //
    // A Sim that has stood for a grace period because other Sims or pets are in its way walks on through them. What it
    // may never do: walk through an object, end inside a Sim that is using the object it walks to, change how two Sims
    // place themselves for a social, or replace the search of a player's own Go Here.
    public static class ClipWaitCore
    {
        public const int kNone = 0, kWould = 1, kExempt = 2, kGo = 3;

        // why a wait that reached the grace period was left to the game
        public const int kKeptNot = 0, kKeptObject = 1, kKeptInUse = 2, kKeptSocial = 3, kKeptGoHere = 4;
        public static readonly string[] kKeptNames = { "", "object in the way", "Sim at the object in use", "social", "player Go Here" };

        // where the route ends
        public const int kDestFloor = 0, kDestObject = 1, kDestSim = 2;

        public struct Case
        {
            public float ElapsedMin, GraceMin;
            public int Sims, Others;        // Sims and pets in the way (toddler or older) / anything else in the way
            public int SimsOnWay;           // of the Sims, those not at the route's end
            public bool SimAtDest;          // one of them stands at the route's end
            public int DestKind;
            public bool DestInUse;          // a Sim at the route's end uses the object the route goes to
            public bool ObjectDestAllowed;  // tuning: an object whose spot is taken by a Sim that does not use it
            public bool Social;             // the walker's interaction targets a Sim
            public bool PlayerGoHere;       // the walker runs a Go Here the player gave
            public bool SimInScope, AllInScope;
        }

        // true = the wait is old enough to be judged at all
        public static bool Due(float elapsedMin, float graceMin)
        {
            if (!(graceMin >= 0f)) graceMin = 0f;
            return elapsedMin >= graceMin;
        }

        // The rule. kept != 0 with kNone = left to the game for that reason. destAccepted = the walk may end in a Sim's
        // place. A Sim at the route's end that may not be walked into does not keep the walker from passing the Sims on
        // its way there: the game's own handling of the route's end then runs when the walker gets there.
        public static int Decide(bool on, Case k, out int kept, out bool destAccepted)
        {
            kept = kKeptNot; destAccepted = false;
            if (k.Sims <= 0 || !Due(k.ElapsedMin, k.GraceMin)) return kNone;
            if (k.Others > 0) { kept = kKeptObject; return kNone; }
            if (k.SimAtDest)
            {
                int why = kKeptNot;
                if (k.Social || k.DestKind == kDestSim) why = kKeptSocial;
                else if (k.PlayerGoHere) why = kKeptGoHere;
                else if (k.DestKind == kDestObject && (k.DestInUse || !k.ObjectDestAllowed)) why = kKeptInUse;
                if (why == kKeptNot) destAccepted = true;
                else if (k.SimsOnWay <= 0) { kept = why; return kNone; }
            }
            if (!on) return kWould;
            return k.SimInScope && k.AllInScope ? kGo : kExempt;
        }

        // sim-minutes to tenths for the wait counters (never negative, capped so a broken clock cannot flood a counter)
        public static int Tenths(float minutes)
        {
            if (!(minutes > 0f)) return 0;
            if (minutes > 600f) minutes = 600f;
            return (int)(minutes * 10f + 0.5f);
        }
    }
}
