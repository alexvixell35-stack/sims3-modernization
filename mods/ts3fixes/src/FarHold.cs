using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // FarHold (docs/346 §3, docs/351 §2; lane MS): the shared owner table and Hook A on AlarmManager.Simulate.
    // Switches, both default off: farhold (a parked lot's alarm manager is not simulated) and farholdshadow (diagnostic:
    // the same decision is only counted, the manager is simulated). Off and no shadow: Hook A is two static reads and
    // the original.
    //
    // Built (FO-3 part 1): parked-lot case of Hook A (docs/346 §3.2 steps 1-2) with the §3.6 exemption re-read on every
    // call, the table API, release on world quit / switch off / Edit Town / lot gone.
    // NOT built: take mode for held Sims and pass-through timers (§3.2 steps 3-4), Hook B on AlarmManager.Write. So
    // FarHoldSim refuses every Sim, and nothing of ours is in a save: a parked lot's queue is never changed.
    // Nothing in this lane parks a lot yet (FarParked needs the FO-4 model); FarHoldDiag parks one test lot for the queue.
    //
    // Household rule: FarLotExempt (world lot, active lot, home lot, a household / selected / unknown-scope Sim on the lot,
    // scope not readable) is asked on every Hook A call for a parked lot and by FarParkLot. [Experimental] HouseholdToo
    // is not read: a hold is a skip (docs/346 §3.6), so the Sim test is IsPlayerSim, not HouseholdExempt.
    //
    // Counters [1834-1847]: parked lots, their high-water, held Sims (0 in this build), ids added, ids released,
    // ReleaseAll calls, ownerConflict, managers not simulated, managers that would not be (shadow), exempt lots evicted
    // (EP5), parks refused (switch off, exempt lot, Sim hold), parked lots simulated because the pass-through set has
    // rows, errors, parked ids whose lot was gone.
    public static partial class Fixes
    {
        const int kFh = 1834;
        static FieldInfo sFhLotId;
        static bool sFhIn, sFhOn, sFhShadow;
        static int sFhGen = -2, sFhGone, sFhLines;

        static void ApplyFarHold()
        {
            ApplyFarHub();  // first: the hub serves FarLight too, with or without Hook A
            sFhLotId = typeof(AlarmManager).GetField("mLotId", kInstAll);
            if (sFhLotId == null) { AppliedLog().Add("farhold: AlarmManager.mLotId not found, Hook A not patched: no lot can park (FarParkLot answers false); FarHub and FarLight are not affected"); return; }
            sFhIn = Patch(typeof(AlarmManager), "Simulate", Type.EmptyTypes, typeof(FhAlarmHook), "SimHook", "SimOrig");
        }

        // The switch generation (every switch write and every world load bumps it): the far hooks re-read their switches on a change.
        static int FarSwGen() { int[] g = sSwGen; return g == null ? -1 : g[0]; }

        static void FhRefresh()
        {
            sFhOn = ShortcutOn("farhold");
            sFhShadow = ShortcutOn("farholdshadow");
            sFhGen = FarSwGen();
            FarHubWant(0, sFhOn || sFhShadow);
        }

        public class FhAlarmHook
        {
            public void SimHook()
            {
                if (sFhGen != FarSwGen()) FhRefresh();
                if (FarHoldCore.Lots != 0 && (sFhOn || sFhShadow) && FhSkip(this)) return;
                SimOrig();
            }
            public void SimOrig() { }  // receives AlarmManager.Simulate
        }

        // docs/346 §3.2 step 2 for one manager; true = do not simulate it this call.
        static bool FhSkip(object am)
        {
            try
            {
                ulong id = (ulong)sFhLotId.GetValue(am);
                if (!FarHoldCore.IsLotParked(id)) return false;
                int d = FarHoldCore.Decide(sFhOn, sFhShadow, true, FarLotExempt(LotManager.GetLot(id)), FarHubPaused());
                if (d == FarHoldCore.Evict)
                {
                    FarHoldCore.ReleaseLot(id);
                    if (sFhLines++ < 32) DiagLine("farhold: lot 0x" + id.ToString("X") + " is exempt (world / active / home lot or a household Sim on it), removed from the parked set");
                }
                return d == FarHoldCore.Skip;
            }
            catch (Exception) { FarHoldCore.Errors++; return false; }
        }

        // docs/351 §5: never treated as parked. Unknown scope (no lot, no active household, any exception) = exempt.
        public static bool FarLotExempt(Lot lot)
        {
            try
            {
                if (lot == null || lot.HasBeenDestroyed || lot.IsWorldLot) return true;
                Household hh = Household.ActiveHousehold;
                if (hh == null || lot == LotManager.ActiveLot || lot == hh.LotHome) return true;
                Sim sel = PlumbBob.SelectedActor;
                if (sel == null || sel.LotCurrent == lot) return true;
                foreach (Sim s in hh.AllActors) if (s == null || s.LotCurrent == lot) return true;
                foreach (Sim s in lot.GetAllActors()) if (IsPlayerSim(s)) return true;  // selectable or unknown-scope Sim on the lot
                return false;
            }
            catch (Exception) { return true; }
        }

        // ---- docs/351 §2 API (FS / FO / LZ call these; the tables are FarHoldCore's) --------------------------------------

        // Take mode is not built: no Sim is held, whoever asks.
        public static bool FarHoldSim(ulong simId, int owner) { FarHoldCore.Refused++; return false; }
        public static void FarReleaseSim(ulong simId) { FarHoldCore.ReleaseSim(simId); }
        public static bool FarIsSimHeld(ulong simId) { return FarHoldCore.IsSimHeld(simId); }

        // True only when the lot is really parked. Shadow: the id goes into the table so Hook A can count, the answer stays
        // false (the caller keeps the lot live) and the caller's drop path releases it as always.
        public static bool FarParkLot(ulong lotId, int owner)
        {
            if (sFhGen != FarSwGen()) FhRefresh();
            if (!sFhIn || (!sFhOn && !sFhShadow) || FarLotExempt(LotManager.GetLot(lotId))) { FarHoldCore.Refused++; return false; }
            return FarHoldCore.ParkLot(lotId, owner) && sFhOn && !sFhShadow;
        }
        public static void FarReleaseLot(ulong lotId) { FarHoldCore.ReleaseLot(lotId); }

        // Read only; already false for an exempt lot, in shadow and with the switch off.
        public static bool FarIsLotParked(ulong lotId)
        {
            if (FarHoldCore.Lots == 0 || !sFhOn || sFhShadow || !FarHoldCore.IsLotParked(lotId)) return false;
            return !FarLotExempt(LotManager.GetLot(lotId));
        }

        // FarHub only.
        static void FarReleaseAll(string reason)
        {
            int n = FarHoldCore.ReleaseAll(reason);
            if (n != 0) DiagLine("farhold: release all (" + reason + "), " + n + " ids");
        }

        public static void FarPassThrough(string declaringType, string method) { FarHoldCore.PassThrough(declaringType, method); }

        // A parked id whose lot is gone leaves the set (the hub's lot events are not used: docs/348 §5 gaps). Runs frozen too.
        static readonly System.Collections.Generic.List<ulong> sFhTmp = new System.Collections.Generic.List<ulong>();
        static void FarHoldSweep()
        {
            if (FarHoldCore.Lots == 0) return;
            sFhTmp.Clear();
            FarHoldCore.CopyLots(sFhTmp);
            foreach (ulong id in sFhTmp)
            {
                Lot lot = LotManager.GetLot(id);
                if (lot != null && !lot.HasBeenDestroyed) continue;
                sFhGone++;
                FarHubCore.LotGone(id);
            }
            sFhTmp.Clear();
        }

        static void FarHoldCounters()
        {
            int[] c = PatchCounters();
            c[kFh] = FarHoldCore.Lots; c[kFh + 1] = FarHoldCore.LotsHigh; c[kFh + 2] = FarHoldCore.Sims; c[kFh + 3] = FarHoldCore.Parks;
            c[kFh + 4] = FarHoldCore.Releases; c[kFh + 5] = FarHoldCore.ReleaseAlls; c[kFh + 6] = FarHoldCore.OwnerConflict; c[kFh + 7] = FarHoldCore.Skipped;
            c[kFh + 8] = FarHoldCore.WouldSkip; c[kFh + 9] = FarHoldCore.Evicted; c[kFh + 10] = FarHoldCore.Refused; c[kFh + 11] = FarHoldCore.PassBlocked;
            c[kFh + 12] = FarHoldCore.Errors; c[kFh + 13] = sFhGone;
        }

        // Harness entry (reflect_call), diagnostics only, never called by the game. "" / "counts": the read-back line.
        // "parkfar": parks the lot farthest from the viewers that is not exempt, on no never-far row of the FarLight pass and empty (owner FO) and names it; "parkhome": asks for the
        // active household's home lot (must be refused); "release:<hex id>"; "releaseall".
        public static string FarHoldDiag(string what)
        {
            string r = "";
            try
            {
                if (sFhGen != FarSwGen()) FhRefresh();
                if (what == "parkfar")
                {
                    Lot best = null; float bestD = -1f;
                    FoNeverFarRead();  // the FarLight pass's never-far rows (docs/350 §4.3), read now
                    foreach (Lot lot in LotManager.AllLots)
                    {
                        if (FarLotExempt(lot) || FoNeverFar(lot) || lot.GetAllActorsCount() != 0) continue;
                        float d = FoViewerDist(lot.Position.x, lot.Position.y, lot.Position.z);
                        if (d > bestD) { bestD = d; best = lot; }
                    }
                    r = best == null ? "no lot" : "lot 0x" + best.LotId.ToString("X") + " at " + (int)bestD + " m, parked " + FarParkLot(best.LotId, FarHoldCore.OwnerFO);
                }
                else if (what == "parkhome")
                {
                    Lot home = Household.ActiveHousehold == null ? null : Household.ActiveHousehold.LotHome;
                    r = home == null ? "no home lot" : "home lot 0x" + home.LotId.ToString("X") + ", parked " + FarParkLot(home.LotId, FarHoldCore.OwnerFO);
                }
                else if (what != null && what.StartsWith("release:")) { ulong id = Convert.ToUInt64(what.Substring(8), 16); FarReleaseLot(id); r = "released 0x" + id.ToString("X"); }
                else if (what == "releaseall") { FarReleaseAll("selftest"); r = "released all"; }
            }
            catch (Exception e) { FarHoldCore.Errors++; r = "error " + e.GetType().Name; }
            FarHoldCounters();
            r = "farhold: " + what + (r.Length > 0 ? ": " + r : "") + "; on " + sFhOn + ", shadow " + sFhShadow + ", hook " + sFhIn + ", lots " + FarHoldCore.Lots + " (high " + FarHoldCore.LotsHigh
                + "), sims " + FarHoldCore.Sims + ", parks " + FarHoldCore.Parks + ", releases " + FarHoldCore.Releases + ", skipped " + FarHoldCore.Skipped + ", would " + FarHoldCore.WouldSkip
                + ", evicted " + FarHoldCore.Evicted + ", refused " + FarHoldCore.Refused + ", ownerConflict " + FarHoldCore.OwnerConflict + ", errors " + FarHoldCore.Errors;
            DiagLine(r);
            return r;
        }
    }
}
