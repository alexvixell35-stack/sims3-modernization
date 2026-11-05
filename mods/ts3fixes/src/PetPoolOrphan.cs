using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.PetSystems;

namespace Ts3Fixes
{
    // petpoolorphan (docs/534): PetPool.RandomRemovePet and ClearAllPets (the adoption and horse pools' refresh,
    // PetPoolManager.RefreshPool) take a pet out of the pool and out of Household.PetHousehold (RemovePet ->
    // RemoveTemporary). EA 1.67 disposes none of them; simler90's UPDATE242 core disposes the ones whose Genealogy has
    // relations [verified: the game's PetPool and the core patch's base IL]. A dropped pet without family links stays a
    // valid description with no household: the relationship table, AgingManager and its aging alarms keep it, so it goes
    // into the save, and at the next load no household fixes it up, so EA drops it there and ErrorTrap's load scan logs
    // "SimDescription Removed" (the player's run 44: 37 of 42 removals were pool pets dropped in the session before;
    // issue #166 build 52). Fix: after the original, each pet that left the pool, is still valid, has no household, no
    // family links and no Sim in the world is disposed (Dispose(clearInstantiatedBit: true, keepPartnerGenealogy: false,
    // addMiniSim: false): with no family links a MiniSim keeps nothing). Counters [2062] disposed, [2063] left because a
    // Sim of it is in the world, [2064] Dispose threw (registry fixes-2062). Default off.
    // shortcut: a pet whose Sim is in the world is only counted (the core build disposes it later, once the Sim is
    // gone); upgrade if [2063] is not 0 in play.
    public static partial class Fixes
    {
        static List<SimDescription> PoolPets(PetPool p)
        {
            List<SimDescription> l = new List<SimDescription>();
            Household ph = Household.PetHousehold;
            if (p.SimDescriptionIds == null || ph == null) return l;
            foreach (ulong id in p.SimDescriptionIds)
            {
                SimDescription d = ph.CurrentMembers.GetSimDescriptionFromId(id);
                if (d != null) l.Add(d);
            }
            return l;
        }

        static void DisposeDroppedPets(PetPool p, List<SimDescription> before)
        {
            int[] c = PatchCounters();
            foreach (SimDescription d in before)
            {
                if (p.SimDescriptionIds != null && p.SimDescriptionIds.Contains(d.SimDescriptionId)) continue;
                if (!d.IsValidDescription || d.Household != null) continue;               // disposed (simler90) or rehomed
                if (d.Genealogy != null && d.Genealogy.HasAnyGenealogyRelations()) continue; // family links: not this fix
                if (d.CreatedSim != null) { c[2063]++; continue; }
                try { d.Dispose(true, false, false); c[2062]++; }
                catch (Exception e) { c[2064]++; ShortcutLog("petpoolorphan: Dispose threw " + e.GetType().Name); }
            }
        }

        public class PetPoolOrphanHook : PetPool
        {
            public bool RandomHook()
            {
                if (!ShortcutOn("petpoolorphan")) return RandomOrig();
                List<SimDescription> before = PoolPets(this);
                bool r = RandomOrig();
                DisposeDroppedPets(this, before);
                return r;
            }
            public bool RandomOrig() { return false; } // receives PetPool.RandomRemovePet's original body

            public bool ClearHook()
            {
                if (!ShortcutOn("petpoolorphan")) return ClearOrig();
                List<SimDescription> before = PoolPets(this);
                bool r = ClearOrig();
                DisposeDroppedPets(this, before);
                return r;
            }
            public bool ClearOrig() { return false; } // receives PetPool.ClearAllPets's original body
        }

        // Harness check (repro pet-pool-orphan-check): a new adoption cat from EA's own creator (PetPoolManager's private
        // CreatePetByType), which has no family links, goes into a new AdoptCat pool of its own, then RandomRemovePet
        // drops it (the only pet). "expect broken" (switch off) wants EA's state: removed, still valid, no household; the
        // check disposes it itself. "expect fixed" (switch on) wants it removed and disposed; petpoolorphan_disposed +1.
        public static object PetPoolOrphanCheck(string what)
        {
            if (what != "expect broken" && what != "expect fixed") return "petpoolorphan <expect broken|expect fixed>";
            MethodInfo create = typeof(PetPoolManager).GetMethod("CreatePetByType", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (create == null) throw new InvalidOperationException("petpoolorphan check: PetPoolManager.CreatePetByType not found");
            SimDescription d = create.Invoke(null, new object[] { PetPoolType.AdoptCat }) as SimDescription;
            if (d == null) throw new InvalidOperationException("petpoolorphan check: CreatePetByType(AdoptCat) made nothing");
            if (d.Genealogy != null && d.Genealogy.HasAnyGenealogyRelations()) { d.Dispose(true, false, false); throw new InvalidOperationException("petpoolorphan check: the new cat has family links"); }
            PetPool p = new PetPool(PetPoolType.AdoptCat);
            if (!p.AddPet(d)) { d.Dispose(true, false, false); throw new InvalidOperationException("petpoolorphan check: AddPet refused the new cat (no pet household, or it is in it already)"); }
            bool removed = p.RandomRemovePet();
            bool valid = d.IsValidDescription, hh = d.Household != null;
            if (valid && hh && Household.PetHousehold != null) Household.PetHousehold.Remove(d);
            if (d.IsValidDescription) d.Dispose(true, false, false);
            bool on = ShortcutOn("petpoolorphan");
            string line = "RandomRemovePet " + removed + ", then valid " + valid + ", household " + hh + " (switch " + on + ")";
            bool ok = what == "expect broken" ? !on && removed && valid && !hh : on && removed && !valid;
            if (!ok) throw new InvalidOperationException("petpoolorphan " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
