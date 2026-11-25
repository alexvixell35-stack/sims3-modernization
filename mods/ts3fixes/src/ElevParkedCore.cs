namespace Ts3Fixes
{
    // Game-independent rules of elevparked / elevparkedhh (ElevParked.cs, docs/686 §34). Offline selftest: tools/selftest/elevparked.
    public static class ElevParkedCore
    {
        public const int kNone = 0, kGo = 1, kWould = 2, kExempt = 3;

        // A Sim about to board at closed doors (EA: 7 sim-minutes of "the car arrives" effects first). carHere: the column's
        // car last stopped at these doors (a ride ended here, or a boarding here was the car's last call) and nobody has
        // called it since; carEmpty: no rider registered in it. Only then are the doors opened at once.
        public static int Decide(bool on, bool inScope, bool doorsOpen, bool carHere, bool carEmpty)
        {
            if (doorsOpen || !carHere || !carEmpty) return kNone;
            if (!on) return kWould;
            if (!inScope) return kExempt;
            return kGo;
        }
    }
}
