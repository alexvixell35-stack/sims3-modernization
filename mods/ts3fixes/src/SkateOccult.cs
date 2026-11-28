using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // skateoccult (2026-11-27, switch off by default): an autonomous skate (ice rink, roller rink, frozen pond) ends after a
    // random 60-120 sim minutes: SkatingRink.Skate.SkatingRouteCallback (a travel callback while the Sim skates) adds
    // ExitReason.CanceledByScript once SimClock.ElapsedTime(mStartedSkatingTime) >= mTimeToSkateAutonomously. EA put that test
    // inside "if (!mIsOccultSkater && Autonomous && !IsSpinning && !IsExiting)", the block that also picks autonomous spins and
    // backward skating, which occult skaters cannot do. So an occult skater never times out and skates until a motive fails.
    // Occult skaters: EA's CalculateIfActorIsOccultSkater = SimBot, genie, mummy, fairy, zombie, vampire not tired, werewolf
    // form, imaginary friend in costume, ghost; NRaas Hybrid's SkateEx stores its own = SimBot, mummy, zombie, imaginary friend
    // in costume, any transform buff (werewolf form), and genie, fairy, vampire, ghost only with its occult skating setting off.
    // Seen 2026-11-27 in a natural Sunset Valley winter run: Holly Alto, a werewolf, skated the Central Park pond for 2543+ sim
    // minutes against a limit of 104 (state Skating, routing, moving); in a rerun she skated in human form (not an occult
    // skater) and left at her time.
    // Fix: after EA's callback, an autonomous occult skater still in state Skating whose time is up gets the same exit reason
    // EA gives every other skater; EA's own exit then runs (RouteExitReasonsAddedCallback > SetTryingToExit > exit queue).
    // Counters: [4012] occult skates ended, [4013] occult skates past their time seen with the switch off (once per skate),
    // [4014] errors.
    public static partial class Fixes
    {
        const BindingFlags kSoFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sSoStarted = typeof(SkatingRink.Skate).GetField("mStartedSkatingTime", kSoFlags);
        static readonly FieldInfo sSoLimit = typeof(SkatingRink.Skate).GetField("mTimeToSkateAutonomously", kSoFlags);
        static readonly Dictionary<ulong, int> sSoSeenOff = new Dictionary<ulong, int>();  // Sim id -> the skate counted with the switch off

        static void ApplySkateOccult()
        {
            if (sSoStarted == null || sSoLimit == null) { PatchCounters()[4014]++; return; }  // another game version: leave EA's code alone
            Patch(typeof(SkatingRink.Skate), "SkatingRouteCallback", new Type[] { typeof(RouteEvent), typeof(Sim.WalkStyle) },
                typeof(SkOccHook), "CbHook", "CbOrig");
        }

        public class SkOccHook
        {
            public void CbHook(RouteEvent re, Sim.WalkStyle walkStyle)
            {
                CbOrig(re, walkStyle);
                try
                {
                    SkatingRink.Skate k = (SkatingRink.Skate)(object)this;
                    if (!k.IsOccultSkater || !k.Autonomous || !k.IsSkating) return;  // EA's test without the occult part (IsSkating: not spinning, not exiting)
                    Sim a = k.Actor;
                    if (a == null || a.HasExitReason(ExitReason.CanceledByScript)) return;
                    if (SimClock.ElapsedTime(TimeUnit.Minutes, (DateAndTime)sSoStarted.GetValue(k)) < (float)sSoLimit.GetValue(k)) return;
                    if (!ShortcutOn("skateoccult"))
                    {
                        ulong id = a.SimDescription != null ? a.SimDescription.SimDescriptionId : 0UL;
                        int h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k), seen;
                        if (sSoSeenOff.TryGetValue(id, out seen) && seen == h) return;
                        if (sSoSeenOff.Count > 256) sSoSeenOff.Clear();
                        sSoSeenOff[id] = h;
                        PatchCounters()[4013]++;
                        return;
                    }
                    a.AddExitReason(ExitReason.CanceledByScript);  // what EA adds for every other autonomous skater
                    PatchCounters()[4012]++;
                }
                catch (Exception) { PatchCounters()[4014]++; }
            }
            public void CbOrig(RouteEvent re, Sim.WalkStyle walkStyle) { } // receives SkatingRink.Skate.SkatingRouteCallback's original header
        }
    }
}
