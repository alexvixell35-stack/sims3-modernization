using System;
using System.Reflection;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // ghostspawnargs (tuning kGhostSpawnArgs, off by default; docs/563 §2). EA bug fix, exact for every Sim.
    //
    // Urnstone [verified, decompiled]: GhostSpawn(creatingForBurialGround, position, lot, isAngry) (the IUrnstone overload)
    // forwards to the 5-argument GhostSpawn with LotCurrent and isAngry: false, dropping both of its own arguments.
    // Callers that pass them: GhostHunter.GhostHunterJob.CreateAngryGhost (isAngry: true, the job lot; its urn is created
    // out of the world), SpellBook and OccultGenie (the caster's lot). For the Ghost Hunter's angry-ghost jobs this means:
    // the spawn is refused once kGhostMaxGhostsInWorld non-playable ghosts are out (the job's ghost is not made), the
    // ghost gets the normal "react to ghost" broadcaster instead of the job's panic one (OnPanicStart), it greets the urn's
    // lot instead of the job lot, and it gets a return-to-grave alarm (which then skips it, since it has BeAngryGhost).
    // Fix: the overload passes its own lot (the urn's LotCurrent when null) and isAngry to the 5-argument GhostSpawn, with
    // the same duration EA picks (kGhostBurialGroundDurationHours / kGhostSpawnDuration).
    // Counters: [2508] angry spawns forwarded, [2509] spawns forwarded with another lot only. Check: GhostSpawnArgsChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sGsSpawnHours = typeof(Urnstone).GetField("kGhostSpawnDuration", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sGsBurialHours = typeof(Urnstone).GetField("kGhostBurialGroundDurationHours", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        static void ApplyGhostSpawnArgs()
        {
            if (sGsSpawnHours == null || sGsBurialHours == null) { AppliedLog().Add("ghostspawnargs: Urnstone spawn durations not found, not patched"); return; }
            Patch(typeof(Urnstone), "GhostSpawn", new Type[] { typeof(bool), typeof(Vector3), typeof(Lot), typeof(bool) }, typeof(GsHook), "SpawnHook", "SpawnOrig");
        }

        // The decision (also used by the check): 0 = EA's body, 1 = forward an angry spawn, 2 = forward a spawn whose lot
        // differs from the urn's (not angry).
        internal static int GsForward(bool on, bool angry, bool lotGiven, bool lotDiffers)
        {
            if (!on) return 0;
            if (angry) return 1;
            return lotGiven && lotDiffers ? 2 : 0;
        }

        public class GsHook
        {
            public bool SpawnHook(bool creatingForBurialGround, Vector3 position, Lot lot, bool isAngry)
            {
                if (!ShortcutOn("ghostspawnargs")) return SpawnOrig(creatingForBurialGround, position, lot, isAngry);  // fast path
                Urnstone urn = (object)this as Urnstone;
                if (urn == null) return SpawnOrig(creatingForBurialGround, position, lot, isAngry);
                Lot own = urn.LotCurrent;
                int act = GsForward(true, isAngry, lot != null, lot != own);
                if (act == 0) return SpawnOrig(creatingForBurialGround, position, lot, isAngry);
                PatchCounters()[act == 1 ? 2508 : 2509]++;
                float hours = (float)(creatingForBurialGround ? sGsBurialHours : sGsSpawnHours).GetValue(null);
                return urn.GhostSpawn(creatingForBurialGround, position, lot ?? own, isAngry, hours);
            }
            public bool SpawnOrig(bool creatingForBurialGround, Vector3 position, Lot lot, bool isAngry) { return false; } // receives Urnstone.GhostSpawn(bool, Vector3, Lot, bool)'s original body
        }
    }
}
