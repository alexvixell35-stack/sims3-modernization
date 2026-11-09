using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Agent
{
    // Pet churn for the leak stress (issue #40; user 11-08: "make a lot of sims adopt pets, give away pets"). TOWNIES ONLY:
    // households that are not active, not special (pet pool, services) and have a home lot, as spawn_sims. The steps are
    // EA's own [verified: decompiled 1.67 Sims3GameplaySystems]: SocialWorkerPetAdoptionSituation.AdoptPet and
    // SocialWorkerPetPutUpSituation.GrabPet.CleanUpPet, without the social worker (no car, route, dialog or notification).
    public static partial class Handlers
    {
        static void RegisterPets()
        {
            Dispatcher.Register("pet_adopt", PetAdopt);
            Dispatcher.Register("pet_give_away", PetGiveAway);
        }

        static readonly PetPoolType[] kAdoptPools = { PetPoolType.AdoptDog, PetPoolType.AdoptCat };

        static List<Household> TownieHomes()
        {
            List<Household> homes = new List<Household>();
            foreach (Household h in Household.sHouseholdList)
                if (h != null && !h.IsActive && !h.IsSpecialHousehold && h.LotHome != null) homes.Add(h);
            return homes;
        }

        static Dictionary<string, object> PetPoolSizes()
        {
            Dictionary<string, object> d = Util.Obj();
            foreach (PetPoolType t in kAdoptPools) d[t.ToString()] = PetPoolManager.GetPoolSize(t);
            return d;
        }

        static Dictionary<string, object> PetRow(SimDescription pet, Household h)
        {
            Dictionary<string, object> d = Util.Obj();
            d["pet"] = pet.FullName;
            d["id"] = Util.Id(pet.SimDescriptionId);
            d["species"] = pet.Species.ToString();
            d["age"] = pet.Age.ToString();
            d["household"] = h != null ? h.Name : null;
            return d;
        }

        // pet_adopt {count?: 3, species?: "dog" | "cat" | "any"}: `count` townie households each adopt one pet out of EA's
        // adoption pool (AdoptDog / AdoptCat; no horses). Per pet, as AdoptPet.AddPetToHousehold + BringPetToSim (on-lot
        // branch) + PostPetTeleportToLot + OnAdoptionEnd: out of the pool, Household.Add, IsNeverSelectable false,
        // WasAdopted, Instantiate at a random spot of the home lot, the adoption parent flag and PetAdoption.PetAdoptionEnd
        // (kPetAdoption / kArkBuilder / kNewPet / kPetBeAdopted events, liking) with a YA+ household Sim in the world,
        // MakeSimGoHome for grown pets. The pool removal starts EA's refill alarm (RemovePet startAutoFilling: a new pool pet
        // from EA's creators every 30 sim-min), where AdoptPet's GetPetOutOfPool does not: the pools would run dry. A
        // household needs room (CanAddSpeciesToHousehold: 6 pets, 10 members). Returns adopted, skipped and the pool sizes.
        static object PetAdopt(Args a)
        {
            RequireLive();
            int n = (int)a.Long("count", 3);
            if (n < 1 || n > 10) throw new McpException("count must be 1..10");
            string sp = a.Str("species", "any").ToLowerInvariant();
            if (sp != "any" && sp != "dog" && sp != "cat") throw new McpException("species: dog, cat or any");
            List<Household> homes = TownieHomes();
            List<object> adopted = new List<object>(), skipped = new List<object>();
            for (int i = 0; i < n; i++)
            {
                PetPoolType t = sp == "dog" ? PetPoolType.AdoptDog : sp == "cat" ? PetPoolType.AdoptCat : kAdoptPools[RandomUtil.GetInt(kAdoptPools.Length - 1)];
                List<SimDescription> pool = PetPoolManager.GetPetsByType(t);
                if (pool == null || pool.Count == 0) { skipped.Add(t + " pool empty"); continue; }
                SimDescription pet = RandomUtil.GetRandomObjectFromList(pool);
                List<Household> room = homes.FindAll(delegate(Household h) { return h.CanAddSpeciesToHousehold(pet.Species); });
                if (room.Count == 0) { skipped.Add("no townie household with room for a " + pet.Species); continue; }
                Household home = RandomUtil.GetRandomObjectFromList(room);
                if (!PetPoolManager.RemovePet(t, pet, true)) { skipped.Add(pet.FullName + ": not removed from " + t); continue; }
                home.Add(pet);
                pet.IsNeverSelectable = false;
                pet.WasAdopted = true;
                Sim ps = pet.Instantiate(Service.GetPositionInRandomLot(home.LotHome));
                List<Sim> adults = home.GetSimsOfType(CASAGSAvailabilityFlags.HumanYoungAdult | CASAGSAvailabilityFlags.HumanAdult | CASAGSAvailabilityFlags.HumanElder);
                Sim adult = adults != null && adults.Count > 0 ? RandomUtil.GetRandomObjectFromList(adults) : null;
                if (ps != null && adult != null)
                {
                    Relationship.CheckAddHumanParentFlagOnAdoption(adult, ps);
                    PetAdoption.PetAdoptionEnd(ps, adult, false);
                }
                if (ps != null && !ps.IsKitten && !ps.IsPuppy) Sim.MakeSimGoHome(ps, false);
                Dictionary<string, object> d = PetRow(pet, home);
                d["instantiated"] = ps != null;
                d["adult"] = adult != null ? adult.FullName : null;
                adopted.Add(d);
            }
            Dictionary<string, object> r = Util.Obj();
            r["adopted"] = adopted;
            r["skipped"] = skipped;
            r["pools"] = PetPoolSizes();
            return r;
        }

        // pet_give_away {count?: 1}: `count` pets of townie households go back to EA's adoption pool, as CleanUpPet:
        // Household.Remove, PetAdoption.PutPetBackToPool (a full pool first drops a random pet with RandomRemovePet, the
        // petpoolorphan path, docs/534; a pet the pool refuses is disposed), then the pet's Sim is destroyed. Dogs and cats
        // only, not ghosts, unicorns or pregnant pets (PetAdoption.CanPutPetUp), only from a household that keeps a human,
        // never a pet Sim on the active household's home lot. A pet Sim on a parent (carried, slotted) or routing is reset
        // first and given away by a task 30 ticks later, as remove_sims (disposing a rider mid-ride crashed, docs/143 §8.11).
        // Returns given_away, deferred and the pool sizes.
        static object PetGiveAway(Args a)
        {
            RequireLive();
            int n = (int)a.Long("count", 1);
            if (n < 1 || n > 10) throw new McpException("count must be 1..10");
            Household active = Household.ActiveHousehold;
            Lot activeLot = active != null ? active.LotHome : null;
            List<SimDescription> pets = new List<SimDescription>();
            foreach (Household h in TownieHomes())
            {
                bool human = false;
                foreach (SimDescription d in h.AllSimDescriptions) if (d.IsHuman) { human = true; break; }
                if (!human) continue;
                foreach (SimDescription d in h.PetSimDescriptions)
                {
                    PetPoolType t = PetAdoption.GetTypeBySpecies(d.Species);
                    if (d.IsGhost || d.IsUnicorn || d.IsPregnant || (t != PetPoolType.AdoptDog && t != PetPoolType.AdoptCat)) continue;
                    Sim s = d.CreatedSim;
                    if (s != null && !s.HasBeenDestroyed && activeLot != null && s.LotCurrent == activeLot) continue;
                    pets.Add(d);
                }
            }
            List<object> given = new List<object>(), deferred = new List<object>();
            List<SimDescription> later = new List<SimDescription>();
            for (int i = 0; i < n && pets.Count > 0; i++)
            {
                SimDescription pet = RandomUtil.GetRandomObjectFromList(pets);
                pets.Remove(pet);
                Dictionary<string, object> d = PetRow(pet, pet.Household);
                Sim s = pet.CreatedSim;
                if (s != null && !s.HasBeenDestroyed && (s.Parent != null || s.IsRouting))
                {
                    s.SetObjectToReset();
                    later.Add(pet);
                    deferred.Add(d);
                    continue;
                }
                GiveAwayPet(pet);
                given.Add(d);
            }
            if (later.Count > 0)
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    Simulator.Sleep(30);
                    foreach (SimDescription pet in later)
                    {
                        try { GiveAwayPet(pet); }
                        catch (Exception) { }  // gone meanwhile
                    }
                }));
            Dictionary<string, object> r = Util.Obj();
            r["given_away"] = given;
            r["deferred"] = deferred;
            r["pools"] = PetPoolSizes();
            return r;
        }

        // CleanUpPet's order: out of the household, back to the pool, then the Sim (held from before: a refused pet is disposed)
        static void GiveAwayPet(SimDescription pet)
        {
            Household h = pet.Household;
            if (h == null || h.IsActive || h.IsSpecialHousehold) return;  // moved meanwhile (deferred): never the active household
            Sim s = pet.CreatedSim;
            h.Remove(pet);
            PetAdoption.PutPetBackToPool(pet);
            if (s != null && !s.HasBeenDestroyed) s.Destroy();
        }
    }
}
