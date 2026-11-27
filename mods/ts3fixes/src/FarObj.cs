using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.RealEstate;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // FarLight (docs/350 §4-§5, §10 EP1 / EP2 / EP4; lane FO; build notes docs/358). A lot past the far edge for the dwell
    // time, with no Sim on or near it and on no never-far row, stops its three per-lot pulses: Lot.UpdateReactions,
    // Lot.UpdateNumZombiesOnLot, Lot.UpdateDoorPrivacy. Its alarm manager, detail priority and display level run as EA
    // wrote them. No model. Leaving FarLight is docs/350 L6: the lot's pulse timers are reset and the zombie body gets the
    // skipped time once, on its next run in the lot's own task.
    //
    // Switches, all default off:
    //   farobj            master: off = no pass, no record, hooks pass through. On alone: the pass keeps records, nothing is skipped
    //   farobjlight       the skip itself
    //   farobjshadow      diagnostic: pass and records run, pulses are only counted as "would skip" (wins over farobjlight)
    //   farobjpulseprobe  diagnostic: every 16th pulse body is timed into docs/322 distance bands (the timing queue's ms/s by band)
    //
    // Household rule. Boundary pass (EP1 + EP2, one call): rows X1-X4, X6-X10 of docs/350 §4.3 plus houseboat lots; a
    // household, selected or unknown-scope Sim (IsPlayerSim, [Experimental] HouseholdToo not read: a skip) excludes its
    // lot, the lots of its queued interaction targets, its work and school lots. No active household or no selected Sim
    // = every lot excluded. Any Sim at all on the lot or inside its pulse radius keeps it live.
    // Every pulse (EP4): viewer distance (camera, household Sims, the selected Sim, player Sims outside the household),
    // home / active / selected-Sim lot, household and player Sims' lots and the lot's actor count
    // are read again; any hit runs the body and drops the record. Every 16th skip is cross-checked with FarLotExempt
    // (counter 1874, must stay 0).
    // Not checked (docs/358 §5): X5 beyond interaction targets, X11 map-view / lot-info observation, X12 mod rows.
    //
    // Counters [1856-1895]: [1856] records, [1857] high-water, [1858-1863] lots per state as of the last pass (Live,
    // GoFarPending, FarLight, FarParked, PendingLive, Excluded), [1864] passes, [1865] lots that took FarLight, [1866]
    // go-nears from FarLight, [1867] records of gone lots, [1868-1870] pulses skipped (reactions, zombies, door privacy),
    // [1871-1873] the same, would skip (shadow), [1874] skip cross-check found an exempt lot (must be 0), [1875] FarLight
    // lot caught on a household row at a pulse, [1876] left because a Sim arrived, [1877] left because a viewer came near,
    // [1878] kept live by a Sim near, [1879] lots with zombie time still owed, [1880] lots excluded by a household row, [1881] by another row
    // (summed over passes), [1882] errors, [1883] longest pass slice us, [1884] records dropped by flush / switch off,
    // [1885-1889] probe: timed pulse us by band (x16 = total), [1890-1894] timed pulses by band, [1895] timed pulses that yielded,
    // [1896] lots whose owed zombie time was paid (L6), [1897] broadcaster pulse timers reset (L6).
    public static partial class Fixes
    {
        const int kFo = 1856;
        static bool sFoIn, sFoOn, sFoLight, sFoShadow, sFoProbe, sFoHook, sFoRun;
        static int sFoGen = -2, sFoErrors, sFoLines, sFoVerify, sFoSample, sFoPassUsMax, sFoPasses, sFoHhSkip;
        static FieldInfo sFoSaved, sFoReactions, sFoBrTime;

        static void ApplyFarObj()
        {
            sFoSaved = typeof(Lot).GetField("mSavedData", kInstAll);
            sFoReactions = sFoSaved == null ? null : sFoSaved.FieldType.GetField("mReactions", kInstAll);
            sFoBrTime = typeof(ReactionBroadcaster).GetField("mTimeSinceLastSimulate", kInstAll);
            // L6 needs both: without them a lot could not be given back as the spec says, so nothing is patched
            if (sFoReactions == null || sFoBrTime == null || sFoBrTime.FieldType != typeof(float)) { AppliedLog().Add("farobj: Lot.mSavedData.mReactions or ReactionBroadcaster.mTimeSinceLastSimulate not found, FarLight hooks not patched"); FarHubRegister(FarHoldCore.OwnerFO, null, sFoFlush, sFoReset, sFoStep, sFoLotGone); return; }
            // all three or none: a FarLight lot must not skip one pulse and run another
            Type[] rf = { typeof(float), typeof(bool) }, f = { typeof(float) };
            string late = NotPatchable(typeof(Lot), "UpdateReactions", rf) ?? NotPatchable(typeof(Lot), "UpdateNumZombiesOnLot", f) ?? NotPatchable(typeof(Lot), "UpdateDoorPrivacy", Type.EmptyTypes);
            if (late != null) { AppliedLog().Add("farobj: " + late + ", FarLight hooks not patched"); return; }
            sFoIn = Patch(typeof(Lot), "UpdateReactions", rf, typeof(FoLotHook), "ReactHook", "ReactOrig")
                & Patch(typeof(Lot), "UpdateNumZombiesOnLot", f, typeof(FoLotHook), "ZombieHook", "ZombieOrig")
                & Patch(typeof(Lot), "UpdateDoorPrivacy", Type.EmptyTypes, typeof(FoLotHook), "DoorHook", "DoorOrig");
            FarHubRegister(FarHoldCore.OwnerFO, null, sFoFlush, sFoReset, sFoStep, sFoLotGone);
        }

        static void FoRefresh()
        {
            sFoOn = ShortcutOn("farobj");
            sFoLight = ShortcutOn("farobjlight");
            sFoShadow = ShortcutOn("farobjshadow");
            sFoProbe = ShortcutOn("farobjpulseprobe");
            sFoRun = sFoIn && sFoOn;  // pass and records: with the master switch, so an A/B on farobjlight keeps its records
            sFoHook = sFoRun && (sFoLight || sFoShadow || sFoProbe);
            sFoGen = FarSwGen();
            FarHubWant(FarHoldCore.OwnerFO, sFoRun);
        }

        public class FoLotHook
        {
            public void ReactHook(float timePassed, bool simulateFullLot)
            {
                if (sFoGen != FarSwGen()) FoRefresh();
                if (!sFoHook) { ReactOrig(timePassed, simulateFullLot); return; }
                Lot lot = (Lot)(object)this;
                if (FoPulse(lot, 0, 0f)) return;
                if (!sFoProbe || (++sFoSample & 15) != 0) { ReactOrig(timePassed, simulateFullLot); return; }
                long tick = SimClock.CurrentTicks;
                uint t0 = SrcNowUs();
                ReactOrig(timePassed, simulateFullLot);
                FoTimed(lot, tick, t0);
            }
            public void ReactOrig(float timePassed, bool simulateFullLot) { }  // receives Lot.UpdateReactions

            public void ZombieHook(float timePassed)
            {
                if (sFoGen != FarSwGen()) FoRefresh();
                if (!sFoHook && sFoOwed.Count == 0) { ZombieOrig(timePassed); return; }
                Lot lot = (Lot)(object)this;
                if (sFoHook && FoPulse(lot, 1, timePassed)) return;
                ZombieOrig(timePassed + FoPayZombie(lot));  // L6: the time skipped while FarLight, once
            }
            public void ZombieOrig(float timePassed) { }  // receives Lot.UpdateNumZombiesOnLot

            public void DoorHook()
            {
                if (sFoGen != FarSwGen()) FoRefresh();
                if (!sFoHook || !FoPulse((Lot)(object)this, 2, 0f)) DoorOrig();
            }
            public void DoorOrig() { }  // receives Lot.UpdateDoorPrivacy
        }

        // Probe: UpdateReactions can yield; a body that crossed a sim tick is not a clean time (as WakePark.cs).
        static void FoTimed(Lot lot, long tick, uint t0)
        {
            try
            {
                int[] c = PatchCounters();
                if (SimClock.CurrentTicks != tick) { c[kFo + 39]++; return; }
                uint us = unchecked(SrcNowUs() - t0);
                Vector3 p; float r;
                UfLotShape(lot, tick, out p, out r);
                int b = FarObjModel.Band(FoViewerDist(p.x, p.y, p.z) - r);
                c[kFo + 29 + b] += (int)us;
                c[kFo + 34 + b]++;
            }
            catch (Exception) { sFoErrors++; }
        }

        // ---- EP4: one pulse of one lot ---------------------------------------------------------------------------------
        static long sFoTick = -1;
        static bool sFoScopeOk;
        static ulong[] sFoHhLots = new ulong[16];
        static int sFoHhLotCount, sFoXvCount;
        static float[] sFoXv = new float[24];  // x, y, z of the extra viewers below
        // Sims outside the active household that count as the player's (IsPlayerSim: selectable, or scope unknown), as of
        // the last pass slice. They are viewers for the 150 m rule like household Sims. Sim references: dropped at every
        // slice, on reset and on world quit.
        static readonly List<Sim> sFoPlayers = new List<Sim>();

        // Once per sim tick: the household's lots (X1-X3) and the extra viewers. The scope counts as known only when
        // every read below worked: a partial list must never pass as the whole household.
        static void FoTick(long now)
        {
            sFoTick = now;
            sFoScopeOk = false;
            sFoHhLotCount = 0; sFoXvCount = 0;
            try
            {
                Household hh = Household.ActiveHousehold;
                Sim sel = PlumbBob.SelectedActor;
                if (hh == null || sel == null) return;
                FoHhLot(hh.LotHome); FoHhLot(LotManager.ActiveLot);
                if (!FoViewer(sel)) return;
                foreach (Sim s in hh.AllActors) { if (s == null) return; FoHhLot(s.LotCurrent); }  // their positions: FarViewerDist
                for (int i = 0; i < sFoPlayers.Count; i++) if (!FoViewer(sFoPlayers[i])) return;
                sFoScopeOk = true;
            }
            catch (Exception) { sFoErrors++; }
        }
        static void FoHhLot(Lot lot)
        {
            if (lot == null) return;
            if (sFoHhLotCount == sFoHhLots.Length) Array.Resize(ref sFoHhLots, sFoHhLots.Length * 2);
            sFoHhLots[sFoHhLotCount++] = lot.LotId;
        }
        // false = the Sim cannot be read (gone): scope unknown until the next pass slice lists the Sims again
        static bool FoViewer(Sim s)
        {
            if (s == null || s.HasBeenDestroyed) return false;
            FoHhLot(s.LotCurrent);
            if (sFoXvCount * 3 == sFoXv.Length) Array.Resize(ref sFoXv, sFoXv.Length * 2);
            Vector3 p = s.Position;
            sFoXv[sFoXvCount * 3] = p.x; sFoXv[sFoXvCount * 3 + 1] = p.y; sFoXv[sFoXvCount * 3 + 2] = p.z;
            sFoXvCount++;
            return true;
        }
        // Nearest viewer: camera and household Sims (FarViewerDist) plus the selected Sim and the other player Sims.
        static float FoViewerDist(float x, float y, float z)
        {
            float best = FarViewerDist(x, y, z);
            if (sFoXvCount == 0) return best;
            best *= best;
            for (int i = 0; i < sFoXvCount; i++)
            {
                float dx = x - sFoXv[i * 3], dy = y - sFoXv[i * 3 + 1], dz = z - sFoXv[i * 3 + 2], d = dx * dx + dy * dy + dz * dz;
                if (d < best) best = d;
            }
            return (float)Math.Sqrt(best);
        }

        // true = skip this body. minutes: the zombie body's timePassed (kind 1), owed to the lot when skipped.
        static bool FoPulse(Lot lot, int kind, float minutes)
        {
            if (FarObjModel.LightLots == 0) return false;
            try
            {
                ulong id = lot.LotId;
                FarObjModel.Rec rec = FarObjModel.Get(id);
                if (rec == null || rec.State != FarObjModel.FarLight) return false;
                if (!sFoRun || (!sFoLight && !sFoShadow)) return false;  // switch went off: the pass / the hub clears the state
                if (FarHubPaused()) return false;  // read live, before any per-tick answer: the sim tick stands still while frozen
                long now = SimClock.CurrentTicks;
                int d;
                if (rec.At == now) d = rec.Dec;
                else
                {
                    if (now != sFoTick) FoTick(now);
                    bool hh = false;
                    for (int i = 0; i < sFoHhLotCount; i++) if (sFoHhLots[i] == id) { hh = true; break; }
                    d = FarObjModel.Pulse(id, rec, now, false, sFoScopeOk, hh, lot.GetAllActorsCount(), FoViewerDist(rec.X, rec.Y, rec.Z) - rec.R, sFoLight && !sFoShadow);
                    if (d != FarObjModel.Run && (++sFoVerify & 15) == 0)
                    {
                        if (lot.HasBeenDestroyed) { FarObjModel.LotGone(id); FoDrainLeft(); return false; }  // a dying lot is not a household miss
                        if (FarLotExempt(lot))
                        {
                            // must not happen: the cheap checks above cover every row FarLotExempt reads
                            sFoHhSkip++;
                            if (sFoLines++ < 16) DiagLine("farobj: HOUSEHOLD CHECK MISSED on lot 0x" + id.ToString("X") + ", record dropped");
                            FarObjModel.Drop(id);
                            d = FarObjModel.Run;
                        }
                    }
                }
                if (FarObjModel.LeftCount != 0) FoDrainLeft();
                if (d == FarObjModel.Run) return false;
                PatchCounters()[kFo + (d == FarObjModel.Skip ? 12 : 15) + kind]++;
                if (d == FarObjModel.Skip && kind == 1) FarObjModel.OweZombie(rec, minutes);
                return d == FarObjModel.Skip;
            }
            catch (Exception) { sFoErrors++; return false; }
        }

        // ---- L6: a lot that left FarLight after a real skip ----------------------------------------------------------
        // Pulse timers: every broadcaster's mTimeSinceLastSimulate back to 0 (docs/350 L6). Zombie time: kept per lot id and
        // added to the zombie body's timePassed on its next run in the lot's own task (it can yield, as EA wrote it), once.
        static readonly Dictionary<ulong, float> sFoOwed = new Dictionary<ulong, float>();
        static readonly List<ulong> sFoLeftIds = new List<ulong>();
        static readonly List<float> sFoLeftOwed = new List<float>();
        static int sFoCatchUp, sFoTimerResets;

        static void FoDrainLeft()
        {
            if (FarObjModel.LeftCount == 0) return;
            sFoLeftIds.Clear(); sFoLeftOwed.Clear();
            FarObjModel.TakeLeft(sFoLeftIds, sFoLeftOwed);
            for (int i = 0; i < sFoLeftIds.Count; i++)
            {
                try
                {
                    Lot lot = LotManager.GetLot(sFoLeftIds[i]);
                    if (lot == null || lot.HasBeenDestroyed) continue;  // L13: no catch-up for a lot that is going away
                    List<ReactionBroadcaster> l = sFoReactions.GetValue(sFoSaved.GetValue(lot)) as List<ReactionBroadcaster>;
                    if (l != null) foreach (ReactionBroadcaster b in l) if (b != null) { sFoBrTime.SetValue(b, 0f); sFoTimerResets++; }
                    if (sFoLeftOwed[i] > 0f)
                    {
                        float had;
                        sFoOwed.TryGetValue(sFoLeftIds[i], out had);
                        sFoOwed[sFoLeftIds[i]] = had + sFoLeftOwed[i];
                    }
                }
                catch (Exception) { sFoErrors++; }
            }
            sFoLeftIds.Clear(); sFoLeftOwed.Clear();
        }

        static float FoPayZombie(Lot lot)
        {
            if (sFoOwed.Count == 0) return 0f;
            float owed;
            ulong id = lot.LotId;
            if (!sFoOwed.TryGetValue(id, out owed)) return 0f;
            sFoOwed.Remove(id);
            sFoCatchUp++;
            return owed;
        }

        // ---- EP1 / EP2: the boundary pass, a slice per frame inside the hub's budget ------------------------------------
        static readonly List<ulong> sFoIds = new List<ulong>();
        static readonly Dictionary<ulong, int> sFoExcl = new Dictionary<ulong, int>();
        static float[] sFoSimX = new float[256], sFoSimZ = new float[256];
        static int sFoSimCount, sFoIdx = -1, sFoPass;
        static long sFoNextPass;
        static bool sFoX8;
        static readonly int[] sFoCounts = new int[6], sFoByState = new int[6];

        static readonly FarHubCore.StepFn sFoStep = delegate(FarHubCore.More more)
        {
            if (sFoGen != FarSwGen()) FoRefresh();
            if (!sFoRun)
            {
                if (FarObjModel.Records != 0) { int n = FarObjModel.DropAll(); FoDrainLeft(); DiagLine("farobj: switch off, " + n + " records dropped"); }
                sFoIdx = -1;
                FoCounters();
                return;
            }
            long now = SimClock.CurrentTicks;
            if (sFoIdx < 0)
            {
                if (now < sFoNextPass) return;
                sFoNextPass = now + SimClock.ConvertToTicks(FarObjModel.BoundaryPassMin, TimeUnit.Minutes);
                sFoIds.Clear();
                foreach (Lot l in LotManager.AllLots) if (l != null) sFoIds.Add(l.LotId);
                sFoIds.Sort();  // lot id order (docs/350 §4.2)
                sFoIdx = 0; sFoPass++;
                Array.Clear(sFoCounts, 0, sFoCounts.Length);
            }
            uint t0 = SrcNowUs();
            try
            {
                FoSliceSetup();
                FoTick(now);  // the extra viewers of this slice's player list
                long dwell = SimClock.ConvertToTicks(FarObjModel.ParkDwellMin, TimeUnit.Minutes);
                while (sFoIdx < sFoIds.Count)
                {
                    if ((sFoIdx & 7) == 7 && !more()) break;
                    FoJudge(sFoIds[sFoIdx++], now, dwell);
                }
            }
            catch (Exception) { sFoErrors++; sFoIdx = sFoIds.Count; }  // a pass that threw ends; the next one starts clean
            int us = (int)unchecked(SrcNowUs() - t0);
            if (us > sFoPassUsMax) sFoPassUsMax = us;
            FoDrainLeft();
            if (sFoIdx >= sFoIds.Count)
            {
                FarObjModel.Sweep(sFoPass);
                FoDrainLeft();
                if (sFoOwed.Count != 0)
                {
                    sFoLeftIds.Clear();
                    foreach (ulong id in sFoOwed.Keys) { Lot l = LotManager.GetLot(id); if (l == null || l.HasBeenDestroyed) sFoLeftIds.Add(id); }
                    foreach (ulong id in sFoLeftIds) sFoOwed.Remove(id);
                    sFoLeftIds.Clear();
                }
                Array.Copy(sFoCounts, sFoByState, 6);
                sFoIdx = -1; sFoPasses++;
                sFoIds.Clear(); sFoExcl.Clear(); sFoSimCount = 0;
            }
            FoCounters();
        };

        static void FoMark(Lot lot, int row)
        {
            if (lot == null) return;
            int v;
            sFoExcl.TryGetValue(lot.LotId, out v);
            sFoExcl[lot.LotId] = v | row;
        }

        // Read fresh at the start of every slice (nothing yields inside a slice): the never-far rows by lot and where every Sim stands.
        // shortcut: every Sim is read per slice and tested per candidate lot (Sims x far candidate lots per pass); a grid keyed
        // by world cell when a world has thousands of lots.
        static void FoSliceSetup()
        {
            sFoExcl.Clear();
            sFoSimCount = 0;
            sFoPlayers.Clear();
            Household hh = Household.ActiveHousehold;
            Sim sel = PlumbBob.SelectedActor;
            sFoX8 = hh == null || sel == null;   // X8: scope not known = every lot excluded
            if (sFoX8) return;
            try
            {
                FoMark(hh.LotHome, FarObjModel.X1);
                FoMark(LotManager.ActiveLot, FarObjModel.X2);
                FoMark(sel.LotCurrent, FarObjModel.X2);
                foreach (Sim s in LotManager.Actors)
                {
                    if (s == null || s.HasBeenDestroyed) continue;
                    if (sFoSimCount == sFoSimX.Length) { Array.Resize(ref sFoSimX, sFoSimCount * 2); Array.Resize(ref sFoSimZ, sFoSimCount * 2); }
                    Vector3 p = s.Position;
                    sFoSimX[sFoSimCount] = p.x; sFoSimZ[sFoSimCount++] = p.z;
                    if (!IsPlayerSim(s)) continue;
                    if (s.Household != hh) sFoPlayers.Add(s);  // a player Sim outside the household: a viewer too
                    FoMark(s.LotCurrent, FarObjModel.X3);
                    if (s.InteractionQueue != null)
                        foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                        {
                            IGameObject t = i == null ? null : i.Target;
                            if (t != null) FoMark(t.LotCurrent, FarObjModel.X4);
                        }
                    GameObject work = s.CareerLocation as GameObject;
                    if (work != null) FoMark(work.LotCurrent, FarObjModel.X4);
                    if (s.School != null && s.School.CareerLoc != null && s.School.CareerLoc.Owner != null) FoMark(s.School.CareerLoc.Owner.LotCurrent, FarObjModel.X4);
                }
                if (hh.RealEstateManager != null)
                    foreach (object o in hh.RealEstateManager.AllProperties)
                    {
                        PropertyData pd = o as PropertyData;
                        if (pd != null) FoMark(LotManager.GetLot(pd.LotId), FarObjModel.X6);
                    }
                foreach (Situation sit in Situation.sAllSituations) if (sit != null) FoMark(sit.Lot, FarObjModel.X7);
            }
            catch (Exception) { sFoErrors++; sFoX8 = true; sFoPlayers.Clear(); }  // a row that cannot be read = scope unknown
        }

        // The never-far rows for one lot, from the slice's read. Also its circle.
        static int FoExclBits(Lot lot, ulong id, long now, out Vector3 c, out float r)
        {
            int excl;
            sFoExcl.TryGetValue(id, out excl);
            if (sFoX8) excl |= FarObjModel.X8;
            if (lot.IsWorldLot) excl |= FarObjModel.X9;
            else
            {
                if (lot.IsHouseboatLot()) excl |= FarObjModel.XBoat;
                if (lot.FireManager != null && lot.FireManager.FireCount > 0) excl |= FarObjModel.X10;
            }
            UfLotShape(lot, now, out c, out r);
            if (!(r > 0f)) excl |= FarObjModel.XBoat;  // no corners: the lot's extent is unknown, never far
            return excl;
        }
        // For FarHoldDiag: the same rows outside a pass. A running pass reads them again at its next slice.
        static void FoNeverFarRead() { FoSliceSetup(); FoTick(SimClock.CurrentTicks); }
        static bool FoNeverFar(Lot lot)
        {
            try { Vector3 c; float r; return lot == null || FoExclBits(lot, lot.LotId, SimClock.CurrentTicks, out c, out r) != 0; }
            catch (Exception) { return true; }
        }

        static void FoJudge(ulong id, long now, long dwell)
        {
            Lot lot = LotManager.GetLot(id);
            if (lot == null || lot.HasBeenDestroyed) return;  // its record, if any, goes in the sweep
            Vector3 c; float r;
            int excl = FoExclBits(lot, id, now, out c, out r);
            bool simNear = false;
            if (excl == 0)
            {
                simNear = lot.GetAllActorsCount() != 0;
                if (!simNear && FarObjModel.NeedsNear(id, now, dwell))
                {
                    float reach = r + Math.Max(FarObjModel.MinPulseM, FoPulseRadius(lot));
                    reach *= reach;
                    for (int i = 0; i < sFoSimCount && !simNear; i++)
                    {
                        float dx = sFoSimX[i] - c.x, dz = sFoSimZ[i] - c.z;
                        simNear = dx * dx + dz * dz < reach;
                    }
                }
            }
            int st = FarObjModel.Judge(id, sFoPass, now, dwell, FoViewerDist(c.x, c.y, c.z) - r, excl, simNear, sFoLight || sFoShadow, c.x, c.y, c.z, r);
            sFoCounts[st]++;
        }

        // The largest pulse radius of the lot's broadcasters (docs/350 §4.2 R_pulse). Not readable = a wide guess.
        static float FoPulseRadius(Lot lot)
        {
            try
            {
                List<ReactionBroadcaster> l = sFoReactions == null ? null : sFoReactions.GetValue(sFoSaved.GetValue(lot)) as List<ReactionBroadcaster>;
                if (l == null) return 50f;
                float max = 0f;
                foreach (ReactionBroadcaster b in l) if (b != null && b.BroadcasterParams != null && b.BroadcasterParams.PulseRadius > max) max = b.BroadcasterParams.PulseRadius;
                return max;
            }
            catch (Exception) { return 50f; }
        }

        // ---- hub entries -----------------------------------------------------------------------------------------------
        static readonly FarHubCore.Fn sFoFlush = delegate
        {
            FarObjModel.DropAll();
            if (sFarWorld) FoDrainLeft(); else FarObjModel.ClearLeft();
        };
        static readonly FarHubCore.Fn sFoReset = delegate
        {
            sFoIdx = -1; sFoNextPass = 0; sFoTick = -1; sFoHhLotCount = 0; sFoSimCount = 0; sFoXvCount = 0; sFoScopeOk = false;
            sFoIds.Clear(); sFoExcl.Clear(); sFoPlayers.Clear();
            if (!sFarWorld) sFoOwed.Clear();  // world quit: the lots are gone. Switch off / Edit Town: the owed zombie time stays
            Array.Clear(sFoByState, 0, 6);
            FoCounters();
        };
        static readonly FarHubCore.LotFn sFoLotGone = delegate(ulong id) { FarObjModel.LotGone(id); FarObjModel.ClearLeft(); sFoOwed.Remove(id); };

        static void FoCounters()
        {
            int[] c = PatchCounters();
            c[kFo] = FarObjModel.Records; c[kFo + 1] = FarObjModel.RecordsHigh;
            for (int i = 0; i < 6; i++) c[kFo + 2 + i] = sFoByState[i];
            c[kFo + 8] = sFoPasses; c[kFo + 9] = FarObjModel.LightIn; c[kFo + 10] = FarObjModel.GoNear; c[kFo + 11] = FarObjModel.Gone;
            c[kFo + 18] = sFoHhSkip; c[kFo + 19] = FarObjModel.HhCaught; c[kFo + 20] = FarObjModel.SimArrive; c[kFo + 21] = FarObjModel.NearCall;
            c[kFo + 22] = FarObjModel.BlockSim; c[kFo + 24] = FarObjModel.ExclHousehold; c[kFo + 25] = FarObjModel.ExclOther; c[kFo + 26] = sFoErrors;
            c[kFo + 23] = sFoOwed.Count; c[kFo + 27] = sFoPassUsMax; c[kFo + 28] = FarObjModel.Drops; c[kFo + 40] = sFoCatchUp; c[kFo + 41] = sFoTimerResets;
        }

        // Harness entry (reflect_call), diagnostics only. "" / "counts": the read-back line; "pass": the next pass starts
        // on the next running frame; "dwell:<sim min>", "passmin:<sim min>", "edges:<park m>,<near m>": shorter test settings.
        public static string FarObjDiag(string what)
        {
            string r = "";
            try
            {
                if (sFoGen != FarSwGen()) FoRefresh();
                if (what == "pass") sFoNextPass = 0;
                else if (what != null && what.StartsWith("dwell:")) FarObjModel.ParkDwellMin = Math.Max(0, int.Parse(what.Substring(6)));
                else if (what != null && what.StartsWith("passmin:")) { FarObjModel.BoundaryPassMin = Math.Max(1, int.Parse(what.Substring(8))); sFoNextPass = 0; }
                else if (what != null && what.StartsWith("edges:"))
                {
                    string[] p = what.Substring(6).Split(',');
                    float park = float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), near = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (near > 0f && park > near) { FarObjModel.ParkEdgeM = park; FarObjModel.NearEdgeM = near; } else r = "refused (park edge must be above near edge)";
                }
            }
            catch (Exception e) { sFoErrors++; r = "error " + e.GetType().Name; }
            FoCounters();
            int[] c = PatchCounters();
            r = "farobj: " + what + (r.Length > 0 ? ": " + r : "") + "; on " + sFoOn + ", light " + sFoLight + ", shadow " + sFoShadow + ", probe " + sFoProbe + ", hooks " + sFoIn
                + ", records " + FarObjModel.Records + " (high " + FarObjModel.RecordsHigh + "), states " + sFoByState[0] + "/" + sFoByState[1] + "/" + sFoByState[2] + "/" + sFoByState[3] + "/" + sFoByState[4] + "/" + sFoByState[5]
                + ", passes " + sFoPasses + ", light " + FarObjModel.LightIn + ", go-near " + FarObjModel.GoNear + ", gone " + FarObjModel.Gone
                + ", skipped " + c[kFo + 12] + "/" + c[kFo + 13] + "/" + c[kFo + 14] + ", would " + c[kFo + 15] + "/" + c[kFo + 16] + "/" + c[kFo + 17]
                + ", hh skip " + sFoHhSkip + ", hh caught " + FarObjModel.HhCaught + ", sim arrive " + FarObjModel.SimArrive + ", near " + FarObjModel.NearCall
                + ", catch-up " + sFoCatchUp + ", owed lots " + sFoOwed.Count + ", excl hh " + FarObjModel.ExclHousehold + ", excl other " + FarObjModel.ExclOther + ", dwell " + FarObjModel.ParkDwellMin + " min, edges " + (int)FarObjModel.ParkEdgeM + "/" + (int)FarObjModel.NearEdgeM
                + " m, errors " + sFoErrors;
            DiagLine(r);
            return r;
        }
    }
}
