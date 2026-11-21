namespace Ts3Fixes
{
    // Game-independent rule of casdefaultskin (CasDefaultSkin.cs, docs/658 §6.3). Self-check: tools/selftest/casdefaultskin.
    // CASLogic.LoadDB assigns mSkinToneKey on every skin-tone ramp its KeySearch enumerates, so the default skin is the
    // last ramp in package order. EA's intended human default is CASSkinTones.HumanSkinTone (the ramp EA's own code sets
    // for humans: ThumbnailHelper, Genetics). Pick: during a CAS session (a builder exists, LoadDB re-run for new content)
    // keep the builder's skin, as EA's SkinToneKey getter would on its next read; at session start the human base ramp
    // when it is installed; else EA's pick (nothing better known).
    public static class CasDefaultSkinCore
    {
        public const ulong kHumanSkinTone = 16764603368953075979uL;  // CASSkinTones.HumanSkinTone
        public const int kKeptEa = 0, kHumanBase = 1, kBuilder = 2;

        public static int Pick(ulong[] ramps, ulong eaPick, bool hasBuilder, ulong builderSkin, out ulong pick)
        {
            if (hasBuilder && builderSkin != 0) { pick = builderSkin; return kBuilder; }
            if (ramps != null)
                for (int i = 0; i < ramps.Length; i++)
                    if (ramps[i] == kHumanSkinTone) { pick = kHumanSkinTone; return kHumanBase; }
            pick = eaPick;
            return kKeptEa;
        }

        public static string Name(int why)
        {
            switch (why)
            {
                case kKeptEa: return "EA's last-enumerated ramp kept (no human base ramp)";
                case kHumanBase: return "human base ramp";
                case kBuilder: return "the builder's skin (CAS session open)";
                default: return "?";
            }
        }
    }
}
