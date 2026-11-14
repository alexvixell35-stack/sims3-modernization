using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.DreamsAndPromises;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // minihomelot (docs/266, EA bug fix, tuning kMiniHomeLot, on: small fix + one repro): a Sim keeps the wish "Move In With
    // <Sim>" for a Sim it already lives with (player report, twopointjj, 2026-11-14).
    // EA's check DreamsAndPromisesDelegateFunctions.MoveInWithSimCheckFunction throws the wish away when
    // subject.HasSameHomeLot(owner), and the manager retests visible wishes every 2 sim-hours, so the check itself is right.
    // HasSameHomeLot compares SimDescription.LotHomeId, and that getter returns the Sim's MiniSimDescription.LotHomeId whenever
    // a mini exists (every Sim of the world gets one at the first trip, saved with the game). EA writes the mini's home lot only
    // in SimDescription.OnHouseholdChanged (joining a household); Household.SetLot (Lot.MoveIn: the household moves to a new
    // lot, by the player or NRaas StoryProgression) never does. After a household move the members keep the old lot in their
    // minis; a Sim that joins later gets the new lot, so the two compare unequal and the wish (and every other HasSameHomeLot
    // test: SimLivesInMyHouseholdRightNow, ...) treats them as living apart. [verified in the decompiled scripts, docs/266 §2]
    // Fix: after EA's Household.SetLot, each member's mini home lot is set to the household's lot (EA's own OnHouseholdChanged
    // value); at world load every household's members are repaired the same way (stale minis already in saves). Only at home:
    // not while changing worlds, only Sims whose home world is the current one, only real lots. No mod data, EA's field holds
    // the value EA itself writes on a join. Household Sims are affected (an EA bug fix): their HasSameHomeLot answers become
    // what they would be without the stale field. Same approach as NRaas Traveler (LiveModeStateEx, return home), not copied.
    // Counters [991] minis synced after SetLot, [992] minis repaired at world load, [993] same-household pairs the repro check
    // found living apart by HasSameHomeLot.
    public static partial class Fixes
    {
        public class MiniHomeLotHook
        {
            public void SetLotHook(Lot lot)
            {
                SetLotOrig(lot);
                if (!ShortcutOn("minihomelot")) return;
                try { PatchCounters()[991] += MiniHomeLotSync((Household)(object)this); }
                catch (Exception e) { ShortcutLog("minihomelot: SetLot hook: " + e.GetType().Name + ": " + e.Message); }
            }
            public void SetLotOrig(Lot lot) { } // receives Household.SetLot(Lot)'s original header
        }

        // Members of h whose mini home lot was set to h's lot.
        static int MiniHomeLotSync(Household h)
        {
            if (h == null) return 0;
            Lot lot = h.LotHome;
            if (lot == null || lot.IsWorldLot) return 0;
            List<SimDescription> members = h.AllSimDescriptions;
            if (members == null) return 0;
            WorldName here = GameUtils.GetCurrentWorld();
            int n = 0;
            foreach (SimDescription d in members)
            {
                if (d == null) continue;
                MiniSimDescription m = MiniSimDescription.Find(d.SimDescriptionId);
                if (m == null || !MiniHomeLotCore.ShouldSync(GameStates.IsTravelling, d.HomeWorld == here, lot.LotId, m.LotHomeId)) continue;
                m.LotHomeId = lot.LotId;
                n++;
            }
            return n;
        }

        // World load (Instantiator): repairs the stale minis a save already holds.
        public static void MiniHomeLotWorldLoad()
        {
            if (!ShortcutOn("minihomelot") || Household.sHouseholdList == null) return;
            int n = 0;
            foreach (Household h in new List<Household>(Household.sHouseholdList)) n += MiniHomeLotSync(h);
            PatchCounters()[992] += n;
            if (n > 0) ShortcutLog("minihomelot: world load: " + n + " stale mini home lot(s) repaired");
        }

        // Repro check (tools/harness/repros.json minihomelot-movein-check), on the active household's first two humans (a married
        // pair first). The harness does not save; "stop" puts the mini value back and removes a mini "pick" created.
        //   "pick":   picks A and B, gives B a mini if it has none, sets B's mini home lot to another lot (the state after a
        //             household move, docs/266 §2).
        //   "setlot": EA's Household.SetLot with the household's own lot (the hook's path; a same-lot SetLot only re-sets
        //             inventories and map tags).
        //   "load":   the world-load repair.
        //   "expect apart" / "expect same": throws unless HasSameHomeLot(B, A) is false / true (fails the repro step).
        //   "state":  lots, HasSameHomeLot both ways (EA's MoveInWithSim check), and the visible Move In With wishes of A and B
        //             about a member of their household with their check's result now.
        static SimDescription sMhA, sMhB;
        static ulong sMhSaved;
        static bool sMhCreated;
        public static string MiniHomeLotCheck(string what)
        {
            if (what == "pick")
            {
                MiniHomeLotRestore();
                Household h = Household.ActiveHousehold;
                if (h == null || h.LotHome == null) throw new InvalidOperationException("no active household with a lot");
                sMhA = sMhB = null;
                foreach (SimDescription d in h.AllSimDescriptions)
                    if (d != null && d.IsHuman && d.TeenOrAbove && d.Partner != null && d.Partner.Household == h) { sMhA = d; sMhB = d.Partner; break; }
                if (sMhA == null)
                    foreach (SimDescription d in h.AllSimDescriptions)
                        if (d != null && d.IsHuman && d.TeenOrAbove) { if (sMhA == null) sMhA = d; else if (sMhB == null) sMhB = d; }
                if (sMhB == null) throw new InvalidOperationException("the active household has fewer than two humans teen or older");
                ulong other = 0;
                foreach (Lot l in LotManager.AllLots) if (l != null && !l.IsWorldLot && l.LotId != h.LotId) { other = l.LotId; break; }
                if (other == 0) throw new InvalidOperationException("no other lot");
                MiniSimDescription m = MiniSimDescription.Find(sMhB.SimDescriptionId);
                sMhCreated = m == null;
                if (m == null) { MiniSimDescription.AddMiniSim(sMhB); m = MiniSimDescription.Find(sMhB.SimDescriptionId); }
                if (m == null) throw new InvalidOperationException("AddMiniSim made no mini");
                sMhSaved = m.LotHomeId;
                m.LotHomeId = other;
                return "A " + sMhA.FullName + ", B " + sMhB.FullName + (sMhA.Partner == sMhB ? " (partners)" : "") + "; B mini " + (sMhCreated ? "created" : "existing") + ", lot " + sMhSaved + " -> " + other + "; " + MiniHomeLotState();
            }
            if (what == "stop") return MiniHomeLotRestore();
            if (sMhA == null) throw new InvalidOperationException("pick first");
            if (what == "setlot") { Household h = sMhA.Household; h.SetLot(h.LotHome); return "SetLot done; " + MiniHomeLotState(); }
            if (what == "load") { MiniHomeLotWorldLoad(); return "world-load repair done; " + MiniHomeLotState(); }
            if (what == "state") return MiniHomeLotState();
            if (what == "expect apart" || what == "expect same")
            {
                string st = MiniHomeLotState();
                if (sMhB.HasSameHomeLot(sMhA) != (what == "expect same")) throw new InvalidOperationException(what + " failed: " + st);
                return what + " ok: " + st;
            }
            throw new ArgumentException("unknown: " + what);
        }

        static string MiniHomeLotState()
        {
            MiniSimDescription mb = MiniSimDescription.Find(sMhB.SimDescriptionId);
            bool ab = sMhB.HasSameHomeLot(sMhA), ba = sMhA.HasSameHomeLot(sMhB);
            if (!ab && sMhA.Household == sMhB.Household) PatchCounters()[993]++;
            return "household lot " + (sMhA.Household != null ? sMhA.Household.LotId : 0) + ", A LotHomeId " + sMhA.LotHomeId + ", B LotHomeId " + sMhB.LotHomeId
                + " (mini " + (mb != null ? mb.LotHomeId.ToString() : "none") + "), sameHomeLot B-A " + ab + " A-B " + ba
                + " (MoveInWithSim check: " + (ab ? "kThrowAway" : "kBecomeActive") + "), wishes A: " + MoveInWishes(sMhA) + ", B: " + MoveInWishes(sMhB)
                + "; switch " + ShortcutOn("minihomelot");
        }

        // The visible MoveInWithSim wishes of d about a member of d's household, with their check's result now.
        static string MoveInWishes(SimDescription d)
        {
            Sim s = d.CreatedSim;
            DreamsAndPromisesManager dm = s != null ? s.DreamsAndPromisesManager : null;
            if (dm == null) return "no wish manager";
            FieldInfo f = typeof(DreamsAndPromisesManager).GetField("mPromiseNodes", BindingFlags.Instance | BindingFlags.NonPublic);
            ActiveDreamNode[] nodes = f != null ? f.GetValue(dm) as ActiveDreamNode[] : null;
            if (nodes == null) return "none";
            string r = "";
            foreach (ActiveDreamNode n in nodes)
            {
                if (n == null || n.NodeInstance == null || n.NodeInstance.CheckFunction == null || n.NodeInstance.CheckFunction.Method.Name != "MoveInWithSimCheckFunction") continue;
                SimDescription t = n.Subject != null ? n.Subject.SimDescription as SimDescription : null;
                r += (r.Length > 0 ? "; " : "") + (t != null ? t.FullName : "?") + (t != null && t.Household == d.Household ? " (same household)" : "") + " -> " + n.Test();
            }
            return r.Length > 0 ? r : "none";
        }

        static string MiniHomeLotRestore()
        {
            if (sMhB == null) return "nothing to restore";
            MiniSimDescription m = MiniSimDescription.Find(sMhB.SimDescriptionId);
            string s;
            if (sMhCreated)
            {
                FieldInfo f = typeof(MiniSimDescription).GetField("sMiniSims", BindingFlags.Static | BindingFlags.NonPublic);
                Dictionary<ulong, MiniSimDescription> all = f != null ? f.GetValue(null) as Dictionary<ulong, MiniSimDescription> : null;
                s = all != null && all.Remove(sMhB.SimDescriptionId) ? "created mini removed" : "created mini NOT removed";
            }
            else if (m != null) { m.LotHomeId = sMhSaved; s = "mini lot put back to " + sMhSaved; }
            else s = "mini gone";
            sMhB = null; sMhA = null; sMhCreated = false;
            return s;
        }
    }
}
