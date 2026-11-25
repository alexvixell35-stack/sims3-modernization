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
    // petlonghome (tuning kPetLongHome, default off; docs/656 §8.2). NPC pets and horses walk home (GoHome) or to a lot
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
    // Rule: PetLongHomeCore. Counters [4622] placed, [4623] refused (seen, lot seen, no safe spot, lot kind), [4624] exempt
    // (pets block), [4625] errors (pets block). Log "petlonghome: placed|refused <pet> ...", capped at 10 a minute.
    // docs/656 §4.3 additions, both default off: kPetLongHomeNoSpotMinutes > 0 remembers a (lot, species class) whose safe spot
    // search failed and refuses it without a new search for that long ([4178] searches saved; the refusal still counts in
    // 4623 / 4180); switch petlongvisit (kPetLongVisit) gives the same placement to an owned NPC pet's own VisitCommunityLot
    // walk to a community lot (VisitCommunityLotHook; health.jsonl: Minnie Cat "Visit Eugi's" 2203 sim-min, Snowshoe Cat
    // "Visit Buena Vista Resort" 1793, Cookie Horse "Visit Bridgeport Sports Zone" 1106), counted in [4179] placed and
    // [4180] refused. Strays, wild horses, unicorns and pet-pool animals keep EA's visit walk.
    // docs/656 §4.5 herd spot share (also only with kPetLongHomeNoSpotMinutes > 0): a spot SafeSpot just verified for a
    // (lot, species class) is handed to the next pet of that class for the same life (cheap checks only: still on the lot,
    // unseen; [4196]); a pet whose search would start while another task's search of the same key runs waits for it in
    // its own task ([4197]); a search that ended with its unseen gate gone is not noted as "no spot" ([4198]).
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
            int placedSlot = visit ? 4179 : 4622, refusedSlot = visit ? 4180 : 4623;
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
                if (pre == PetLongHomeCore.kExempt) { c[4624]++; return; }
                if (pre != PetLongHomeCore.kOk) return;  // held, followers or a short trip: EA's walk, not counted
                string kind = null;
                if (lot == Household.ActiveHouseholdLot) kind = "active household's lot";
                else if (!HouseboatOk(lot) || lot.BuildableShells.Length > 0) kind = "houseboat or apartment";
                else if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving) kind = "resort or diving lot";
                if (kind != null) { c[refusedSlot]++; PlhLog("refused " + sim.FullName + " (" + d.Species + ") -> " + lot.Name + ": " + kind + ", " + (int)dist + " m, via " + via); return; }
                Vector3 entry = lot.EntryPoint();
                bool petUnseen = TownieUnseen(sim, null);
                bool goalUnseen = petUnseen && FarFromViewers(lot.Position) && FarFromViewers(entry) && UfGoalUnseen(entry);
                ulong nsKey = PetLongHomeCore.NoSpotKey(lot.LotId, PetLongHomeCore.SpeciesClass(d.IsHorse, d.IsLittleDog, d.IsADogSpecies));
                long nsLife = SimClock.ConvertToTicks(Instantiator.kPetLongHomeNoSpotMinutes, TimeUnit.Minutes);
                // herd spot share (docs/656 §4.5, nsLife > 0 only): wait for a search of the same (lot, class) running in another
                // task (this is the pet's own interaction task; MayYield), then take its answer instead of a search of our own
                if (petUnseen && goalUnseen && nsLife > 0 && sPlh.Busy(nsKey, now, SimClock.ConvertToTicks(30f, TimeUnit.Minutes))
                    && Simulator.CurrentTask == sim.ObjectId && MayYield())
                {
                    c[4197]++;
                    for (int w = 0; w < 40 && sPlh.Busy(nsKey, SimClock.CurrentTicks, SimClock.ConvertToTicks(30f, TimeUnit.Minutes)); w++) Simulator.Sleep(1);
                    if (sim.HasBeenDestroyed || !sim.InWorld) return;
                    now = SimClock.CurrentTicks;
                    petUnseen = TownieUnseen(sim, null);
                    goalUnseen = petUnseen && FarFromViewers(lot.Position) && FarFromViewers(entry) && UfGoalUnseen(entry);
                }
                bool remembered = petUnseen && goalUnseen && nsLife > 0 && sPlh.NoSpotLive(nsKey, now);
                if (remembered) c[4178]++;
                Vector3 pos = Vector3.Invalid, fwd = Vector3.Invalid;
                bool spot = false, shared = false;
                float[] gv = new float[6];
                if (petUnseen && goalUnseen && !remembered && nsLife > 0 && sPlh.GoodGet(nsKey, now, gv))
                {
                    // cheap checks only (no plan): still on this lot and unseen; the spot passed SafeSpot's routability check
                    pos = new Vector3(gv[0], gv[1], gv[2]); fwd = new Vector3(gv[3], gv[4], gv[5]);
                    if (LotManager.GetLotAtPoint(pos) == lot && UfGoalUnseen(pos)) { spot = shared = true; c[4196]++; }
                    else sPlh.GoodDrop(nsKey);
                }
                if (!spot && petUnseen && goalUnseen && !remembered)
                {
                    if (nsLife > 0) sPlh.Begin(nsKey, now);
                    try { spot = SafeSpot(sim, lot, entry, out pos, out fwd, "petlonghome", true) && UfGoalUnseen(pos); }
                    finally { if (nsLife > 0) sPlh.End(nsKey); }
                    now = SimClock.CurrentTicks;
                    if (spot && nsLife > 0) sPlh.GoodNote(nsKey, now, nsLife, pos.x, pos.y, pos.z, fwd.x, fwd.y, fwd.z);
                    // the search may have waited on its job: only a search that still had its unseen gate counts as "no spot"
                    if (!spot && (sim.HasBeenDestroyed || !TownieUnseen(sim, null) || !FarFromViewers(entry)))
                    {
                        if (nsLife > 0) c[4198]++;
                        petUnseen = !sim.HasBeenDestroyed && TownieUnseen(sim, null);
                        goalUnseen = petUnseen && FarFromViewers(entry);
                    }
                }
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
                    + (int)tripMin + " sim-min" + (shared ? ", herd spot shared" : ""));
            }
            catch (Exception ex) { c[4625]++; PlhLog("error " + ex.GetType().Name); }
        }
    }
}
