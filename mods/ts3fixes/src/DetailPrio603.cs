using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // detailprioexact (kDetailPriorityExact, default off; docs/605 §3). [verified, decompiled] Lot.Simulate calls
    // UpdateDetailPriority -> CalculateDetailPriority on every pass of every lot (each tick on simulating lots, every 30
    // ticks on the others). EA's body: no active household -> None; its home lot -> Home; else
    // `foreach (Sim s in Household.ActiveHousehold.AllActors) if (s.LotCurrent == this && s.RabbitHoleCurrent == null)
    // return PlayerSim`, else None. AllActors is Members.GetActorsOfType(all ages and species): a new list of the human
    // then pet descriptions (AddRange of mSimDescriptions, mPetSimDescriptions), then a second new list of each one's
    // CreatedSim where HasSameCASAGSFlags(mask) holds and CreatedSim is set. Two lists and two copies per lot pass.
    // Same answer without the lists: walk SimDescriptions then PetSimDescriptions (the very lists EA copies) in EA's
    // order with EA's tests, first match wins. Every getter read (CreatedSim: a field; HasSameCASAGSFlags: an age/species
    // mapping; LotCurrent: the parent walk; RabbitHoleCurrent: Posture.Container) has no side effect and nothing yields,
    // so calling them in another interleaving cannot change anything. Throws: EA's list build throws on a null member list
    // (AddRange) or a null description (HasSameCASAGSFlags) before any test, so the whole scan runs first and either case
    // returns -1 = "EA's own body" (which throws as before). Household Sims: the answer is EA's for everyone.
    // Counters: [2793] exact answers, [2794] EA's body (a null list or description, or an error), [2795] check mismatches,
    // [2796] errors.
    public static partial class Fixes
    {
        const CASAGSAvailabilityFlags kDpMask = CASAGSAvailabilityFlags.AllAnimalsMask | CASAGSAvailabilityFlags.HumanAgeMask;

        // (int)LotDetailPriority, or -1 for "run EA's body".
        static int DpExact(Lot lot)
        {
            Household hh = Household.ActiveHousehold;
            if (hh == null) return (int)LotDetailPriority.None;
            if (hh.LotHome == lot) return (int)LotDetailPriority.Home;
            List<SimDescription> humans = hh.SimDescriptions, pets = hh.PetSimDescriptions;
            if (humans == null || pets == null) return -1;
            bool hit = false;
            if (!DpScan(humans, lot, ref hit) || !DpScan(pets, lot, ref hit)) return -1;
            return (int)(hit ? LotDetailPriority.PlayerSim : LotDetailPriority.None);
        }

        // One member list: false on a null description (EA throws there). The flag test runs for every description, as
        // EA's list build does; the lot tests run until the first hit, as EA's loop does.
        static bool DpScan(List<SimDescription> list, Lot lot, ref bool hit)
        {
            for (int i = 0; i < list.Count; i++)
            {
                SimDescription d = list[i];
                if (d == null) return false;
                if (!d.HasSameCASAGSFlags(kDpMask) || hit) continue;
                Sim s = d.CreatedSim;
                if (s != null && s.LotCurrent == lot && s.RabbitHoleCurrent == null) hit = true;
            }
            return true;
        }

        // EA's body, word for word, over the public members it uses (the reference for the check and the timing).
        static LotDetailPriority DpEaReference(Lot lot)
        {
            Household activeHousehold = Household.ActiveHousehold;
            if (activeHousehold != null)
            {
                if (activeHousehold.LotHome == lot) return LotDetailPriority.Home;
                foreach (Sim allActor in Household.ActiveHousehold.AllActors)
                    if (allActor.LotCurrent == lot && allActor.RabbitHoleCurrent == null) return LotDetailPriority.PlayerSim;
            }
            return LotDetailPriority.None;
        }

        static System.Reflection.MethodInfo sDpCalc;
        delegate LotDetailPriority DpCalcFn();

        // The hooked Lot.CalculateDetailPriority (private), as Lot.Simulate calls it: with the switch on, our exact path.
        // Through a bound delegate: MethodInfo.Invoke with a target throws on a swapped method (tools/selftest/swapinvoke).
        static LotDetailPriority DpHooked(Lot lot)
        {
            if (sDpCalc == null) sDpCalc = typeof(Lot).GetMethod("CalculateDetailPriority", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return ((DpCalcFn)Fixes.Bind(typeof(DpCalcFn), lot, sDpCalc))();
        }

        // Controls on the household itself: its home lot is Home; the lot of each member outside a rabbit hole (not the
        // home lot) is PlayerSim; a lot with no member on it is None. Both paths must give each expected answer.
        static bool DpControls(out string text)
        {
            Household hh = Household.ActiveHousehold;
            if (hh == null) { text = "no active household"; return false; }
            int n = 0;
            bool ok = true;
            if (hh.LotHome != null) { n++; ok &= DpExact(hh.LotHome) == (int)LotDetailPriority.Home && DpEaReference(hh.LotHome) == LotDetailPriority.Home; }
            Dictionary<Lot, bool> used = new Dictionary<Lot, bool>();
            foreach (Sim s in hh.AllActors)
            {
                if (s.LotCurrent == null) continue;
                used[s.LotCurrent] = true;
                if (s.LotCurrent == hh.LotHome || s.RabbitHoleCurrent != null) continue;
                n++;
                ok &= DpExact(s.LotCurrent) == (int)LotDetailPriority.PlayerSim && DpEaReference(s.LotCurrent) == LotDetailPriority.PlayerSim;
            }
            foreach (Lot lot in LotManager.AllLots)
            {
                if (lot == null || used.ContainsKey(lot) || lot == hh.LotHome) continue;
                n++;
                ok &= DpExact(lot) == (int)LotDetailPriority.None && DpEaReference(lot) == LotDetailPriority.None;
                break;
            }
            text = n + " expected answers (home, member lots, an empty lot)";
            return ok && n >= 2;
        }

        // DetailPrioExactCheck run: every lot, our scan vs EA's body (reference) vs the hooked method as the game calls it;
        // a difference counts [2795]. fixed = switch on, every lot the same and the hook answered from the exact path.
        public static object DetailPrioExactCheck(string what)
        {
            string counters = B582Counters("detailprioexact", 2793, new[] { "exact answers", "EA's body", "check mismatches", "errors" }, EpJoin(sDetailPrioReady ? "" : "Lot.CalculateDetailPriority hook", EpLive(typeof(DetailPriorityHook), 1)));
            if (what == "result") return counters + "; " + DpTiming();
            if (what != "run") return "detailprioexact <run|result>";
            bool on = ShortcutOn("detailprioexact");
            string ctlText, live, missing = "";
            bool controls = false, fixedNow = false;
            try
            {
                controls = DpControls(out ctlText);
                int[] c = PatchCounters();
                int lots = 0, diff = 0, home = 0, player = 0, exact0 = c[2793];
                foreach (Lot lot in LotManager.AllLots)
                {
                    if (lot == null) continue;
                    lots++;
                    LotDetailPriority ea = DpEaReference(lot), hooked = DpHooked(lot);
                    int ours = DpExact(lot);
                    if (ours != (int)ea || hooked != ea) { diff++; c[2795]++; }
                    if (ea == LotDetailPriority.Home) home++;
                    if (ea == LotDetailPriority.PlayerSim) player++;
                }
                bool viaExact = c[2793] - exact0 >= lots;
                controls &= diff == 0;
                fixedNow = on && diff == 0 && viaExact && lots > 0 && !ShortcutOn("detailprio");
                live = "live: lots " + lots + " (home " + home + ", player Sim " + player + "), differ " + diff + ", hook answered exact " + (c[2793] - exact0)
                    + ", detailprio (tick set) " + (ShortcutOn("detailprio") ? "on: the exact path is not used" : "off");
            }
            catch (Exception e) { missing = "threw " + e.GetType().Name; live = "live: threw"; ctlText = "threw"; controls = false; }
            return B582Line("detailprioexact", on, "every lot answered from the exact scan, same as EA", fixedNow, ctlText + ", " + live, controls, missing, counters);
        }

        static string DpTiming()
        {
            try
            {
                List<Lot> lots = new List<Lot>();
                foreach (Lot l in LotManager.AllLots) if (l != null) lots.Add(l);
                if (lots.Count == 0) return "timing: no lots";
                uint t0 = SrcNowUs();
                for (int r = 0; r < 10; r++) foreach (Lot l in lots) DpEaReference(l);
                uint t1 = SrcNowUs();
                for (int r = 0; r < 10; r++) foreach (Lot l in lots) DpExact(l);
                uint t2 = SrcNowUs();
                Household hh = Household.ActiveHousehold;
                uint n = (uint)(10 * lots.Count);
                return "timing: EA body " + ((t1 - t0) * 1000u / n) + " ns, exact scan " + ((t2 - t1) * 1000u / n) + " ns per lot pass (lots " + lots.Count
                    + ", household members " + (hh == null ? 0 : hh.AllSimDescriptions.Count) + ")";
            }
            catch (Exception e) { return "timing: threw " + e.GetType().Name; }
        }
    }
}
