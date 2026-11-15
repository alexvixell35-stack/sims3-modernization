using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Object spawner cap checks, docs/147 §5 D1 (count first, exact). Insect, rock/gem/metal and seed spawners call
    // MinorPet.CanSpawnMoreMinorPetsInWorld and Wildflower.CanSpawnWildflowers on every spawn try; each builds an array of
    // every object of that type in the world (Queries.GetObjects<T>) to count the uncaptured / uncollected ones up to a cap
    // (33 of 43 samples of the spawner cost, docs/147 §5).
    //   spawncapcount (kSpawnCapCount, off): ask the native count (Queries.CountObjects<T>, same type query as GetObjects)
    //     first; below the cap the walk cannot return false (SpawnCapCore.UnderCap), so the answer is EA's guards alone, no
    //     array. At or over the cap EA's method runs. Spawners are objects, not Sims: no household scope involved.
    //   spawncapprobe (diagnostic, off): EA's method always runs; counts the checks and the under-cap share (the capture
    //     docs/147 §5 asks for before D1/D2), and when under the cap compares EA's answer with D1's: [1003] must stay 0.
    // D2 (tick memo, near-exact) is not built: the design builds it only if the probe shows few under-cap answers.
    // Counters: [999] minor-pet checks, [1000] of them under the cap, [1001] wildflower checks, [1002] of them under the
    // cap, [1003] probe mismatches (D1 answer != EA's).
    public static partial class Fixes
    {
        static void ApplySpawnCap()
        {
            Patch(typeof(MinorPet), "CanSpawnMoreMinorPetsInWorld", typeof(SpawnCapHook), "PetsHook", "PetsOrig");
            Patch(typeof(Wildflower), "CanSpawnWildflowers", typeof(SpawnCapHook), "FlowersHook", "FlowersOrig");
        }

        // EA's checks before the walk, as written in CanSpawnMoreMinorPetsInWorld
        static bool PetGuards() { return GameUtils.IsInstalled(ProductVersion.EP5) && GameUtils.GetCurrentWorld() != WorldName.AppaloosaPlains; }

        // EA's checks before the walk, as written in CanSpawnWildflowers
        static bool FlowerGuards()
        {
            bool any = false;
            if (Wildflower.sWildflowerData != null)
                foreach (var d in Wildflower.sWildflowerData)
                    if (GameUtils.IsInstalled(d.Value.ProductVersion)) { any = true; break; }
            if (!any) return false;
            return !(GameUtils.IsInstalled(ProductVersion.EP8) && SeasonsManager.Enabled && SeasonsManager.Temperature < Wildflower.kMinimumTemperatureForWildflowerSpawn);
        }

        // kind 0 = minor pets, 1 = wildflowers. Returns 0/1 = D1's answer, -1 = run EA's method.
        static int SpawnCapAnswer(int kind, bool probe)
        {
            bool count = ShortcutOn("spawncapcount");
            if (!count && !probe) return -1;
            int[] c = PatchCounters();
            c[kind == 0 ? 999 : 1001]++;
            uint total = kind == 0 ? Sims3.Gameplay.Queries.CountObjects<MinorPet>() : Sims3.Gameplay.Queries.CountObjects<Wildflower>();
            int cap = kind == 0 ? MinorPet.GetCapBasedOnDeviceConfiguration(MinorPet.kMinorPetsAllowedInWildNonEP5Worlds) : Wildflower.kMaxWildflowersInWorld;
            if (!SpawnCapCore.UnderCap(total, cap)) return -1;
            c[kind == 0 ? 1000 : 1002]++;
            return (kind == 0 ? PetGuards() : FlowerGuards()) ? 1 : 0;
        }

        // 0/1 = return D1's answer; -1 = EA's method only; 2/3 = probe: run EA's method, compare with D1's answer (minus 2)
        static int SpawnCapPre(int kind)
        {
            bool probe = ShortcutOn("spawncapprobe");
            int a;
            try { a = SpawnCapAnswer(kind, probe); }
            catch (Exception) { a = -1; }  // a failed count leaves EA's answer
            return a >= 0 && probe ? a + 2 : a;
        }

        static void SpawnCapPost(int pre, bool ea) { if (pre >= 2 && ea != (pre == 3)) PatchCounters()[1003]++; }

        public class SpawnCapHook
        {
            public static bool PetsHook() { int p = SpawnCapPre(0); if (p == 0 || p == 1) return p == 1; bool ea = PetsOrig(); SpawnCapPost(p, ea); return ea; }
            public static bool PetsOrig() { return false; } // receives MinorPet.CanSpawnMoreMinorPetsInWorld's original header
            public static bool FlowersHook() { int p = SpawnCapPre(1); if (p == 0 || p == 1) return p == 1; bool ea = FlowersOrig(); SpawnCapPost(p, ea); return ea; }
            public static bool FlowersOrig() { return false; } // receives Wildflower.CanSpawnWildflowers's original header
        }

        // Agent check (repro spawncap-check): calls both checks n times with the switches the repro set and returns the
        // counters, so the repro reads checks, under-cap answers and mismatches without waiting for a spawner.
        public static string SpawnCapCheck(string count)
        {
            int n;
            if (!int.TryParse(count, out n) || n < 1) n = 50;
            int pets = 0, flowers = 0;
            for (int i = 0; i < n; i++) { if (MinorPet.CanSpawnMoreMinorPetsInWorld()) pets++; if (Wildflower.CanSpawnWildflowers()) flowers++; }
            int[] c = PatchCounters();
            if (c[1003] > 0) throw new InvalidOperationException("spawncap: " + c[1003] + " D1 answers differ from EA's");  // fails the repro step
            return "pets yes " + pets + "/" + n + " (total " + Sims3.Gameplay.Queries.CountObjects<MinorPet>() + "), flowers yes " + flowers + "/" + n + " (total " + Sims3.Gameplay.Queries.CountObjects<Wildflower>()
                + "); checks " + c[999] + "/" + c[1001] + ", under cap " + c[1000] + "/" + c[1002] + ", mismatches " + c[1003];
        }
    }
}
