using System;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // casdefaultskin (tuning kCasDefaultSkin, default off; docs/658 §6.3). [verified, decompiled CASLogic.LoadDB] LoadDB
    // sets mSkinToneKey = each skin-tone ramp it enumerates (KeySearch 0x0354796A), so the default skin, which
    // SetDefaultBuilderValues hands to every new builder, is whichever ramp the package order lists last; LoadDB also
    // runs mid-session on new content and then leaves mSkinToneKey different from the builder's skin.
    // Fix: after EA's LoadDB, mSkinToneKey = CasDefaultSkinCore.Pick (the builder's skin mid-session, else the human
    // base ramp CASSkinTones.HumanSkinTone when installed, else EA's pick). The hook also logs, switch on or off, which
    // ramp EA picked and which one is used, at most 10 lines a minute and 100 per game ("casdefaultskin: ...").
    // Counters [4207] LoadDB runs seen, [4208] default changed by the fix, [4209] errors.
    public static partial class Fixes
    {
        static readonly LineCapCore sCdsCap = new LineCapCore(10, 60000, 100);

        static void ApplyCasDefaultSkin()
        {
            Patch(typeof(CASLogic), "LoadDB", Type.EmptyTypes, typeof(CasSkinHook), "LoadDbHook", "LoadDbOrig");
        }

        public abstract class CasSkinHook : CASLogic
        {
            public void LoadDbHook()
            {
                LoadDbOrig();
                int[] c = PatchCounters();
                try
                {
                    c[4207]++;
                    bool on = ShortcutOn("casdefaultskin");
                    ResourceKey ea = mSkinToneKey;
                    ulong[] ramps = new ulong[mSkinToneRamps.Count];
                    for (int i = 0; i < ramps.Length; i++) ramps[i] = mSkinToneRamps[i].InstanceId;
                    bool hasBuilder = mBuilder != null;
                    ulong pick;
                    int why = CasDefaultSkinCore.Pick(ramps, ea.InstanceId, hasBuilder, hasBuilder ? mBuilder.SkinTone.InstanceId : 0, out pick);
                    ResourceKey key = why == CasDefaultSkinCore.kBuilder ? mBuilder.SkinTone
                        : why == CasDefaultSkinCore.kHumanBase ? new ResourceKey(pick, ea.TypeId != 0 ? ea.TypeId : 55867754u, 0u) : ea;
                    bool change = on && key != ea;
                    if (change) { mSkinToneKey = key; c[4208]++; }
                    if (sCdsCap.Take(System.Environment.TickCount))
                        DiagLine("casdefaultskin: " + ramps.Length + " ramps, EA's last-enumerated 0x" + ea.InstanceId.ToString("X16")
                            + (on ? ", used 0x" + mSkinToneKey.InstanceId.ToString("X16") + " (" + CasDefaultSkinCore.Name(why) + ")"
                                  : ", switch off: EA's kept (fix would use 0x" + key.InstanceId.ToString("X16") + ", " + CasDefaultSkinCore.Name(why) + ")"));
                }
                catch (Exception ex) { c[4209]++; if (sCdsCap.Take(System.Environment.TickCount)) DiagLine("casdefaultskin: error " + ex.GetType().Name); }
            }
            public void LoadDbOrig() { } // receives CASLogic.LoadDB's original body
        }
    }
}
