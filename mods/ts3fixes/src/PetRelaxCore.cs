using System;

namespace Ts3Fixes
{
    // Game-independent rules of PetRelax.cs (petrelax / petrelaxhh, docs/686 §11), checked offline by tools/selftest/petrelax.
    //
    // Small pets (cats, dogs, puppies, kittens, raccoons) and the Sims around them walk through each other: at once near a
    // portal, after the shared grace anywhere else (ShareLanesCore.Timed, docs/686 §8.8). Horses, foals and deer are left
    // to clipwait (their bodies are large). A pet that is about to lie down on the end of a stair, door or ladder lane
    // first walks to a free spot next to it.
    public static class PetRelaxCore
    {
        public const int kNone = 0, kWould = 1, kExempt = 2, kGo = 3;

        // why a case with a small pet in it was left to the game (and to simpass / clipwait)
        public const int kKeptNot = 0, kKeptHorse = 1, kKeptObject = 2;

        public struct Case
        {
            public bool WalkerSmallPet, WalkerLarge;  // large = horse, foal, deer (or a Sim riding or leading a horse)
            public int Humans, SmallPets, Large;      // in the way, toddler or older; a lying pet's floor jig counts as that pet
            public int Others;                        // anything else in the way
            public bool WalkerInScope, AllInScope;
        }

        // The walk-through rule. A small pet walks through Sims and small pets; a Sim walks through small pets when
        // nothing else is in its way (the game's ignore-obstacles timer ignores everything, so a Sim that also has
        // another Sim in its way is left to simpass / clipwait). kNone with kept = 0: no small pet in the case.
        public static int Pass(bool on, Case k, out int kept)
        {
            kept = kKeptNot;
            if (k.Humans + k.SmallPets + k.Large <= 0) return kNone;
            if (!k.WalkerSmallPet && k.SmallPets <= 0) return kNone;
            if (k.WalkerLarge || k.Large > 0) { kept = kKeptHorse; return kNone; }
            if (!k.WalkerSmallPet && k.Humans > 0) return kNone;
            if (k.Others > 0) { kept = kKeptObject; return kNone; }
            if (!on) return kWould;
            return k.WalkerInScope && k.AllInScope ? kGo : kExempt;
        }

        // A route's plan failed and names its blocker. A lying pet's floor jig as the blocker is passed (when: Timed, so
        // away from a portal the walker first stands for the grace); any other blocker is not ours.
        public static int PlanJig(bool on, bool blockerIsPetJig, bool walkerLarge, bool walkerInScope, bool petInScope, out int kept)
        {
            kept = kKeptNot;
            if (!blockerIsPetJig) return kNone;
            if (walkerLarge) { kept = kKeptHorse; return kNone; }
            if (!on) return kWould;
            return walkerInScope && petInScope ? kGo : kExempt;
        }

        // A pet about to lie down where it stands, with a portal lane end within the clearance.
        public static int LieDown(bool on, bool petInScope, bool atLaneEnd)
        {
            if (!atLaneEnd) return kNone;
            if (!on) return kWould;
            return petInScope ? kGo : kExempt;
        }

        // Horizontal distance test against one lane end; ends on another floor (dy) do not count.
        public static bool Near(float x, float y, float z, float ex, float ey, float ez, float clear)
        {
            if (!(clear > 0f) || Math.Abs(y - ey) > kLevelDy) return false;
            float dx = x - ex, dz = z - ez;
            return dx * dx + dz * dz < clear * clear;
        }
        public const float kLevelDy = 1.5f;

        // Candidate spots for the pet: two rings around where it stands, the direction away from the lane end first, then
        // alternating to both sides (0, +45, -45, +90, -90, +135, -135, 180 degrees). i = 0 .. kCandidates - 1.
        public const int kCandidates = 16;
        static readonly float[] kRings = { 2f, 3f };
        static readonly int[] kTurns = { 0, 1, -1, 2, -2, 3, -3, 4 };

        public static bool Candidate(int i, float x, float z, float ex, float ez, out float cx, out float cz)
        {
            cx = x; cz = z;
            if (i < 0 || i >= kCandidates) return false;
            float dx = x - ex, dz = z - ez;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < 0.01f) { dx = 1f; dz = 0f; } else { dx /= len; dz /= len; }  // standing on the end itself: any direction
            double a = kTurns[i % kTurns.Length] * (Math.PI / 4.0);
            float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
            float r = kRings[i / kTurns.Length];
            cx = x + (dx * cos - dz * sin) * r;
            cz = z + (dx * sin + dz * cos) * r;
            return true;
        }
    }
}
