using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.MinorPets;
using Sims3.Gameplay.Objects.Misc;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // birdtvnull: docs/559 second-pass gameplay scan. Off by default. Harness check: BirdTvNullChecks.cs.
    // birdtvnull (tuning kBirdTvNull; docs/559 §9). MinorPetBirdLarge.ShouldReactToTv dereferences TV.CurrentChannel for
    // every TurnedOn TV in range; CurrentChannel is null for a TV switched on while broken (TurnOnOff returns before a
    // channel is set) or with a channel from a removed pack [verified]. The bird's idle behaviour throws every time and
    // the talking bird stops reacting. Fix: the same test with TVs that have no channel skipped. Counters [2465] TVs
    // skipped, [2466] errors.
    public static partial class Fixes
    {
        static FieldInfo sBirdRadius;

        static void ApplyBirdTvNull()
        {
            sBirdRadius = typeof(MinorPetBirdLarge).GetField("kRadiusToCheckForTvReactions", kStaticAll);
            if (sBirdRadius == null) AppliedLog().Add("birdtvnull: kRadiusToCheckForTvReactions not found, not patched");
            else Patch(typeof(MinorPetBirdLarge), "ShouldReactToTv", typeof(BirdHook), "ReactHook", "ReactOrig");
        }

        public class BirdHook
        {
            public bool ReactHook(out MinorPetBirdLarge.SpeechType speechTypeToUse)
            {
                if (!ShortcutOn("birdtvnull")) return ReactOrig(out speechTypeToUse);
                try { return BirdReact((object)this as MinorPetBirdLarge, out speechTypeToUse); }
                catch (Exception) { PatchCounters()[2466]++; speechTypeToUse = MinorPetBirdLarge.SpeechType.Squawk; return false; }
            }
            public bool ReactOrig(out MinorPetBirdLarge.SpeechType speechTypeToUse) { speechTypeToUse = MinorPetBirdLarge.SpeechType.Squawk; return false; } // receives MinorPetBirdLarge.ShouldReactToTv
        }

        // A TV the bird can react to: on and with a channel (EA dereferences the channel unconditionally).
        static bool BirdTvChannel(TV tv, out TVChannelData channel)
        {
            channel = null;
            if (tv == null || !tv.TurnedOn) return false;
            channel = tv.CurrentChannel;
            if (channel != null) return true;
            PatchCounters()[2465]++;
            return false;
        }

        // EA's ShouldReactToTv with TVs that have no channel skipped.
        static bool BirdReact(MinorPetBirdLarge bird, out MinorPetBirdLarge.SpeechType speechTypeToUse)
        {
            speechTypeToUse = MinorPetBirdLarge.SpeechType.Squawk;
            if (bird == null) return false;
            if (bird.CurrentBirdState == Bird.BirdState.Suspending || bird.CurrentBirdState == Bird.BirdState.Suspended) return false;
            bool love = bird.BirdKnowsSpeechType(MinorPetBirdLarge.SpeechType.Love);
            bool scared = bird.BirdKnowsSpeechType(MinorPetBirdLarge.SpeechType.Scared);
            if (!love && !scared) return false;
            bool romantic = false, horror = false;
            TV[] tvs = Sims3.Gameplay.Queries.GetObjects<TV>(bird.Position, (float)sBirdRadius.GetValue(null));
            TVChannelData ch;
            if (love) foreach (TV tv in tvs) if (BirdTvChannel(tv, out ch) && ch.IsRomanticChannel) { romantic = true; break; }
            if (scared) foreach (TV tv in tvs) if (BirdTvChannel(tv, out ch) && ch.IsHorrorChannel) { horror = true; break; }
            if (romantic && horror)
            {
                speechTypeToUse = RandomUtil.CoinFlip() ? MinorPetBirdLarge.SpeechType.Love : MinorPetBirdLarge.SpeechType.Scared;
                return true;
            }
            if (romantic) { speechTypeToUse = MinorPetBirdLarge.SpeechType.Love; return true; }
            if (horror) { speechTypeToUse = MinorPetBirdLarge.SpeechType.Scared; return true; }
            return false;
        }
    }
}
