using System;
using Sims3.Gameplay.PetSystems;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // petlonghome (tuning kPetLongHome, default off; docs/645 §4). NPC pets and horses walk home (GoHome) or to a lot
    // (GoToLot) on foot at pet speed for 10-35 sim-hours (OFF runs, health.jsonl "Go Home" interaction_min: Tigger Cat 2110
    // min in Bridgeport, Majora Cat 1777 in Sunset Valley, Lucky Coyle 816 in Isla Paradiso; 113 cat, 52 dog, 13 horse samples
    // over 300 min, plus 33 wild horses on "Go to Lot"). Our trip shortcuts never take pets: GoHomeHook.Unseen and
    // ShortcutToLot refuse any Sim whose household has pets, so only the 5:30 Overwatch reset ends those walks.
    // Fix: at the start of GoHome.Run / GoToLot.Run (GoHomeHook, GoToLotHook), an NPC pet or horse that is not exempt, not
    // held (UfHeld: carried, ridden, riding, carrying; or parented, e.g. a leash jig), has no followers and is not on the lot
    // yet, whose trip is long (straight distance > kPetLongHomeMeters, or this trip to this lot began more than
    // kPetLongHomeHours sim-hours ago) is placed like the unseen trip shortcuts place a human townie: TownieUnseen for the pet,
    // FarFromViewers + UfGoalUnseen for the lot's entry, SafeSpot on the lot near Lot.EntryPoint (its home lot; for an owned
    // pet that is its owner household's lot, never the active household's), UfGoalUnseen for the spot, then PlaceSim. EA's
    // Run then runs as usual and walks the last metres. Houseboats, apartment shells, resorts and the active household's lot
    // keep the game's walk. Household, selected and unknown-scope pets (HouseholdExempt or IsPlayerSim) are never touched.
    // Rule: PetLongHomeCore. Counters [4076] placed, [4077] refused (seen, lot seen, no safe spot, lot kind), [4078] exempt
    // (shared), [4079] errors (shared). Log "petlonghome: placed|refused <pet> ...", capped at 10 a minute.
    // docs/656 §4.3 additions, both default off: kPetLongHomeNoSpotMinutes > 0 remembers a (lot, species class) whose safe spot
    // search failed and refuses it without a new search for that long ([4178] searches saved; the refusal still counts in
    // 4077 / 4180); switch petlongvisit (kPetLongVisit) gives the same placement to an owned NPC pet's own VisitCommunityLot
    // walk to a community lot (VisitCommunityLotHook; health.jsonl: Minnie Cat "Visit Eugi's" 2203 sim-min, Snowshoe Cat
    // "Visit Buena Vista Resort" 1793, Cookie Horse "Visit Bridgeport Sports Zone" 1106), counted in [4179] placed and
    // [4180] refused. Strays, wild horses, unicorns and pet-pool animals keep EA's visit walk.
    public static partial class Fixes
    {
        static readonly PetLongHomeCore sPlh = new PetLongHomeCore();
        static int sPlhMinute = -1, sPlhLogged;

        static void PlhLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sPlhMinute) { sPlhMinute = minute; sPlhLogged = 0; }
            if (sPlhLogged++ < 10) DiagLine("petlonghome: " + s);
        }

        // GoHomeHook.RunHook / GoToLotHook.RunHook / VisitCommunityLotHook.RunHook ("visit", petlongvisit), before EA's Run
        static void PlhBefore(Sim sim, Lot lot, List<Sim> followers, string via)
        {
            if (sim == null || lot == null) return;
            bool visit = via == "visit";
            if (!ShortcutOn(visit ? "petlongvisit" : "petlonghome")) return;
            SimDescription d = sim.SimDescription;
            if (d == null || !d.IsPet) return;
            // unicorns, wild horses and other pet-pool animals (strays) keep EA's walk: their trips are EA's spawn/summon
            // behaviour, not a walk home; only a pet heading to its own home lot is placed
            // wild horses (not unicorns) may also be placed at the lot their herd walks to: the herd's long walks and failed
            // routes cost route time, and an unseen herd arriving early changes nothing the player sees
            bool wildHorse = !d.IsUnicorn && WildHorses.IsWildHorse(d);
            if (visit)
            {
                // petlongvisit: an owned pet's own visit to a community lot only
                if (wildHorse || d.IsUnicorn || d.IsStray || d.Household == null || d.Household.IsPetHousehold || !lot.IsCommunityLot) return;
            }
            else
            {
                if (!wildHorse && (d.IsUnicorn || d.Household == null || d.Household.IsPetHousehold || lot != d.LotHome)) return;
                if (wildHorse && via != "gotolot") return;
            }
            int[] c = PatchCounters();
            int placedSlot = visit ? 4179 : 4076, refusedSlot = visit ? 4180 : 4077;
            try
            {
                if (lot.IsWorldLot) return;
                ulong id = sim.ObjectId.Value;
                long now = SimClock.CurrentTicks;
                double tripMin = sPlh.TripMinutes(id, lot.LotId, now, SimClock.ConvertToTicks(1f, TimeUnit.Minutes));
                float dist = (sim.Position - lot.Position).Length();
                bool exempt = HouseholdExempt(sim) || IsPlayerSim(sim);
                int pre = PetLongHomeCore.Pre(true, exempt, UfHeld(sim) || sim.Parent != null, followers != null && followers.Count > 0,
                    sim.LotCurrent == lot, dist, tripMin, Instantiator.kPetLongHomeMeters, Instantiator.kPetLongHomeHours * 60f);
                if (pre == PetLongHomeCore.kOnLot) { sPlh.Done(id); return; }
                if (pre == PetLongHomeCore.kExempt) { c[4078]++; return; }
                if (pre != PetLongHomeCore.kOk) return;  // held, followers or a short trip: EA's walk, not counted
                string kind = null;
                if (lot == Household.ActiveHouseholdLot) kind = "active household's lot";
                else if (!HouseboatOk(lot) || lot.BuildableShells.Length > 0) kind = "houseboat or apartment";
                else if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving) kind = "resort or diving lot";
                if (kind != null) { c[refusedSlot]++; PlhLog("refused " + sim.FullName + " -> " + lot.Name + ": " + kind); return; }
                Vector3 entry = lot.EntryPoint();
                bool petUnseen = TownieUnseen(sim, null);
                bool goalUnseen = petUnseen && FarFromViewers(lot.Position) && FarFromViewers(entry) && UfGoalUnseen(entry);
                ulong nsKey = PetLongHomeCore.NoSpotKey(lot.LotId, d.IsHorse ? 1 : 2);
                long nsLife = SimClock.ConvertToTicks(Instantiator.kPetLongHomeNoSpotMinutes, TimeUnit.Minutes);
                bool remembered = petUnseen && goalUnseen && nsLife > 0 && sPlh.NoSpotLive(nsKey, now);
                if (remembered) c[4178]++;
                Vector3 pos = Vector3.Invalid, fwd = Vector3.Invalid;
                bool spot = petUnseen && goalUnseen && !remembered && SafeSpot(sim, lot, entry, out pos, out fwd, "petlonghome", true) && UfGoalUnseen(pos);
                int post = PetLongHomeCore.Post(petUnseen, goalUnseen, spot);
                if (post != PetLongHomeCore.kOk)
                {
                    c[refusedSlot]++;
                    if (post == PetLongHomeCore.kNoSpot && !remembered) sPlh.NoSpotNote(nsKey, now, nsLife);
                    PlhLog("refused " + sim.FullName + " (" + d.Species + ") -> " + lot.Name + ": " + PetLongHomeCore.Name(post)
                        + (remembered ? " (remembered)" : "") + ", " + (int)dist + " m, trip " + (int)tripMin + " sim-min, via " + via);
                    return;
                }
                string from = sim.LotCurrent != null ? sim.LotCurrent.Name : "street";
                PlaceSim(sim, pos, fwd, "petlonghome " + lot.Name);
                sPlh.Done(id);
                c[placedSlot]++;
                ShortcutLog(sim.FullName + " => " + lot.Name + " (petlonghome " + via + " from " + from + ")");
                PlhLog("placed " + sim.FullName + " (" + d.Species + ") at " + lot.Name + " via " + via + ", " + (int)dist + " m away, trip "
                    + (int)tripMin + " sim-min");
            }
            catch (Exception ex) { c[4079]++; PlhLog("error " + ex.GetType().Name); }
        }
    }
}
