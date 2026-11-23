namespace Ts3Fixes
{
    // Game-independent rules of caspropagateoutfits and caspropagateextra (CasPropagate.cs, docs/658 §6.1-6.2).
    // Self-checks: tools/selftest/caspropagateoutfits, tools/selftest/caspropagateextra.
    //
    // EA's CASLogic.PropagatePhysicalProperties rebuilds every stored outfit of the edited Sim with the builder's skin,
    // morphs and blends (clothing parts kept). In its loop each outfit is taken off the front of its category list
    // (RemoveOutfit(cat, 0)) and either re-added unchanged (the current outfit, the performance-career Career[0]) or
    // rebuilt and appended. An outfit whose age or gender differs from the builder is uncached and NOT appended: EA drops
    // it on purpose (§6.1). The drop shifts every later index of that category, but EA's own mCurrentOutfitIndex and the
    // Sim's CareerOutfitIndex are not moved, so EA's tail (reload the current outfit into the builder by index) reads the
    // wrong outfit, or none, which leaves the builder with no parts.
    public static class CasPropagateCore
    {
        public const int kHuman = 0, kHorse = 1, kOtherPet = 2;
        public const uint kNaked = 1u, kEveryday = 2u, kCareer = 256u;

        // EA's early return at the top of PropagatePhysicalProperties: true when the method goes on to propagate.
        public static bool Runs(int species, uint current, uint humanPrimary, uint horsePrimary)
        {
            if (species == kHuman) return (current & humanPrimary) != 0;
            if (current == kNaked) return true;
            if (species == kHorse) return (current & horsePrimary) != 0;
            return current == kEveryday;
        }

        // Which outfit of a category EA drops: mismatch[i] (age or gender differs from the builder), except the protected
        // ones EA re-adds before its age / gender test (protA, protB: -1 = none).
        public static bool[] Drops(bool[] mismatch, int protA, int protB)
        {
            bool[] d = new bool[mismatch == null ? 0 : mismatch.Length];
            for (int i = 0; i < d.Length; i++) d[i] = mismatch[i] && i != protA && i != protB;
            return d;
        }

        public static int DropsBelow(bool[] drops, int index)
        {
            int n = 0;
            if (drops == null) return 0;
            for (int i = 0; i < drops.Length && i < index; i++) if (drops[i]) n++;
            return n;
        }

        // The index after EA's pass. found: where EA's re-added current outfit is now (-1 = not found, then the old index
        // less the drops below it); always inside 0..count-1 (0 for an empty list).
        public static int Realign(int found, int old, int dropsBelow, int count)
        {
            if (count <= 0) return 0;
            int i = found >= 0 ? found : old - dropsBelow;
            if (i < 0) i = 0;
            if (i >= count) i = count - 1;
            return i;
        }

        // caspropagateextra: the human categories EA's loop never visits, whose stored outfits keep the old skin, morphs
        // and blends after a CAS edit. EA's own in-world outfit sweeps (OccultRobot.FixupOutfits_OnRemoval,
        // CASLogic.CompleteSimDescription) skip Special and Supernatural (occult forms and special outfits carry their own
        // skin), and CompleteSimDescription drops Singed; those three stay out. MartialArts 128, AuxCareer 512,
        // Makeover 1024, SkinnyDippingTowel 2048, Massage 32768, ChildImagination 65536.
        public static readonly uint[] kExtraHuman = { 128u, 512u, 1024u, 2048u, 32768u, 65536u };

        public static bool IsExtraHuman(uint cat)
        {
            for (int i = 0; i < kExtraHuman.Length; i++) if (kExtraHuman[i] == cat) return true;
            return false;
        }

        // caspropagateextra: the performance-career Career[0] EA re-adds unchanged; rebuilt unless it is the current outfit
        // (the builder already holds it) or its age / gender differs (EA would drop it; we leave it as it is).
        public static bool PerfCareerZero(bool editExisting, bool perfCareer, uint cat, int index, uint current, int currentIndex, bool mismatch)
        {
            if (!editExisting || !perfCareer || cat != kCareer || index != 0) return false;
            if (current == kCareer && currentIndex == 0) return false;
            return !mismatch;
        }
    }
}
