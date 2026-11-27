using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI.Controller;
using FM = Ts3Fixes.FarSimModel;

namespace Ts3Fixes
{
    // Far-Sim shadow (docs/345 §14 "[Diag] FarSimShadow", packages FS-2 / FS-3 of docs/340; build notes docs/354). Runs the
    // far-Sim state machine (FarSimModel.cs) beside EA's live simulation and compares: which state the machine would have a
    // far Sim in against what EA's Sim is doing, the machine's needs read against EA's motive values, the modelled place
    // against the Sim's position, modelled relationship drift against EA's, visit proposals against EA's visits.
    // It changes nothing: no Sim, lot, motive, alarm or save is written, nothing is parked, hidden, moved or held, and no EA
    // method is hooked (polling only: one frame task, a boundary pass every BoundaryPassMin sim minutes over LotManager.Actors
    // in SimDescriptionId order with a count budget). EA keeps every Sim; "go-far" and "go-live" here are trace entries.
    // On: switches farsim (master) and farsimshadow, both default off, then FarSimShadow("start") (or a world load).
    // farsimwork / farsimvenue / farsimvisit / farsimdrift (default off) add those parts of the model to the comparison.
    // Household, selected and unknown-scope Sims: excluded at every entry (the pass EP1 / EP2, the drain EP3 through
    // FsInputs.Excl, the per-frame scope poll EP6, both ids of a drift pair EP7); the test is IsPlayerSim, so
    // [Experimental] HouseholdToo is not read (docs/345 §10). Counter householdInFar must stay 0.
    // Pause, CAS, Build / Buy, Edit Town, menus: SimFrozenOrNotLive stops the task's work (docs/345 §11).
    // Leaks: snapshots drop through the machine's one drop path; every table is reset on world quit and on switch off.
    // Counters [1780-1827]: [1780 + n] = FarSimModel counter n (0-25), [1806-1816] Sims per state, [1817] frames skipped
    // while frozen, [1818] would-be go-lives with a need below -40, [1819] lowest need seen at one (x 1), [1820] motives whose
    // curve minimum is below -40, [1821] passes, [1822] shadow errors, [1823] comparisons, [1824] EA visits seen,
    // [1825] drift pair-days, [1826] snapshots held, [1827] model version while the shadow runs (0 = off),
    // [1898] NPC ids held as "on the household's home lot" (size of the arrival list).
    public static partial class Fixes
    {
        const int kFsC = 1780, kFsByState = kFsC + 26, kFsSkippedPaused = kFsC + 37, kFsBelow40 = kFsC + 38, kFsNeedMin = kFsC + 39,
            kFsCurveBelowClamp = kFsC + 40, kFsPasses = kFsC + 41, kFsErrors = kFsC + 42, kFsSamples = kFsC + 43, kFsEaVisits = kFsC + 44,
            kFsPairDays = kFsC + 45, kFsSnaps = kFsC + 46, kFsVersion = kFsC + 47, kFsOnHhLot = 1898;
        const int kFsSlice = 24, kFsPendingPerFrame = 8, kFsLtrCap = 32, kFsDriftDaysMax = 14, kFsStretchCap = 15;

        sealed class FsSnap
        {
            public long Tick;
            public ulong Home;
            public int WakeMin, VisitPm;
            public ulong Occ;                 // occupation guid and level, compared each pass to raise I9
            public int ShiftDay = -1, ShiftS, ShiftE; public bool ShiftHas, ShiftSchool; public ulong ShiftPlace;
            public readonly List<KeyValuePair<ulong, int>> Phase = new List<KeyValuePair<ulong, int>>();  // commodity, thousandths of an hour
            public readonly List<KeyValuePair<ulong, int>> Ltr = new List<KeyValuePair<ulong, int>>();    // other id, liking at go-far
        }

        sealed class FsTabs
        {
            public readonly FM.Confusion State = new FM.Confusion();
            public readonly FM.ErrTable Place = new FM.ErrTable("placeM", 25, 50, 100, 200, 400);
            public readonly Dictionary<string, FM.ErrTable> Need = new Dictionary<string, FM.ErrTable>();
            public long PlaceLotSame, PlaceLotN;
        }

        static FM.Machine sFs;
        static readonly Dictionary<ulong, FsSnap> sFsSnap = new Dictionary<ulong, FsSnap>();
        static readonly Dictionary<ulong, Sim> sFsSims = new Dictionary<ulong, Sim>();   // this pass's Sims by id; cleared with every table
        static readonly List<Sim> sFsPass = new List<Sim>();
        static readonly List<ulong> sFsSeen = new List<ulong>(), sFsVenues = new List<ulong>(), sFsOnHhLot = new List<ulong>();
        static readonly List<string> sFsBelowSeen = new List<string>();
        static readonly List<Vector3> sFsViewers = new List<Vector3>();
        static readonly List<Lot> sFsHhLots = new List<Lot>();
        static readonly FsTabs sFsNear = new FsTabs(), sFsHour = new FsTabs();
        static readonly FM.ErrTable sFsRelDrift = new FM.ErrTable("relDrift", 0, 1, 3, 6, 15);
        static int sFsGen, sFsPassAt = -1, sFsPendAt, sFsHhCount, sFsSwitches = -1;
        static bool sFsTaskUp, sFsSaid, sFsDrift;
        static long sFsNextPass, sFsNextSample, sFsNextVenues, sFsEaBelow40, sFsFriendHours, sFsDriftSkipped, sFsModelDrift, sFsEaDrift;
        static Household sFsHh;
        static Sim sFsSel;
        static Lot sFsCamLot;  // the camera target's lot when no household Sim is on it: a viewer lot, not a near trigger
        static EventHandler sFsQuit;

        static void ApplyFarSim()
        {
            World.OnWorldLoadFinishedEventHandler += delegate { try { if (FsOn()) FsStart(); } catch (Exception) { PatchCounters()[kFsErrors]++; } };
        }

        static bool FsOn() { return ShortcutOn("farsim") && ShortcutOn("farsimshadow"); }

        static int FsSwitchBits() { return (ShortcutOn("farsimwork") ? 1 : 0) | (ShortcutOn("farsimvenue") ? 2 : 0) | (ShortcutOn("farsimvisit") ? 4 : 0) | (ShortcutOn("farsimdrift") ? 8 : 0); }

        // Per-world far radius, working values of docs/345 §4.2 (sidecar K line t3EdgeM once ME-4 has measured ones).
        static int FsFarRadiusCm()
        {
            switch (GameUtils.GetCurrentWorld())
            {
                case WorldName.SunsetValley: return 30000;
                default: return 45000;  // Bridgeport, Isla Paradiso and every other world: FarRadiusM
            }
        }

        static void FsStart()
        {
            if (sFsQuit == null) sFsQuit = delegate { FsReset(); };
            World.OnWorldQuitEventHandler -= sFsQuit;
            World.OnWorldQuitEventHandler += sFsQuit;
            if (sFs == null)
            {
                FM.Cfg c = new FM.Cfg();
                // no sidecar in the shadow build: a per-session seed (docs/345 §14), from the clock and the world
                c.WorldSeed = FM.M(FM.M((ulong)(uint)System.Environment.TickCount ^ ((ulong)SimClock.CurrentTicks << 20)) ^ (ulong)(uint)(int)GameUtils.GetCurrentWorld());
                c.FarRadiusCm = FsFarRadiusCm();
                sFs = new FM.Machine(c, new FsInputs());
                sFs.OnDrop = delegate(ulong id) { sFsSnap.Remove(id); };
                sFsSaid = false;
                sFsNextPass = sFsNextSample = sFsNextVenues = 0;
                sFsSwitches = -1;
            }
            if (sFsTaskUp) return;
            sFsTaskUp = true;
            Simulator.AddObject(new FsTask(++sFsGen));
        }

        // World quit, switch off: every table empty (docs/345 §13.3). Event counters stay as session totals.
        static void FsReset()
        {
            sFsGen++;
            sFsTaskUp = false;
            if (sFs != null) { sFs.Reset(); FsCounters(); }
            sFs = null;
            sFsSnap.Clear(); sFsSims.Clear(); sFsPass.Clear(); sFsSeen.Clear(); sFsVenues.Clear(); sFsOnHhLot.Clear(); sFsViewers.Clear(); sFsHhLots.Clear();
            sFsPassAt = -1;
            sFsHh = null; sFsSel = null; sFsCamLot = null;
            FsClearTabs();
            int[] k = PatchCounters();
            k[kFsSnaps] = 0; k[kFsVersion] = 0; k[kFsOnHhLot] = 0;
        }

        sealed class FsTask : Task
        {
            readonly int mGen;
            public FsTask(int gen) { mGen = gen; }
            public override void Simulate()
            {
                try
                {
                    while (true)
                    {
                        Simulator.Sleep(0);
                        if (mGen != sFsGen) return;
                        try { if (!FsFrame()) return; }
                        catch (ResetException) { throw; }
                        catch (Exception) { PatchCounters()[kFsErrors]++; }
                    }
                }
                finally { if (mGen == sFsGen) sFsTaskUp = false; Simulator.DestroyObject(ObjectId); }
            }
        }

        // One sim frame. False = the shadow went off and the task ends.
        static bool FsFrame()
        {
            int[] k = PatchCounters();
            if (sFs == null) return false;
            long now = SimClock.CurrentTicks;
            if (!FsOn())
            {
                // switch off at runtime: T15 switchOff for every record at once, then reset (docs/345 §14)
                sFs.PromoteAll(now, FM.WSwitchOff);
                for (int i = sFs.Count - 1; i >= 0; i--) if (sFs.At(i).State == FM.PendingLive) sFs.GoLiveDone(sFs.At(i).Id, now, true);
                DiagLine("farsim: shadow off, " + sFs.Count + " records released");
                FsReset();
                return false;
            }
            if (SimFrozenOrNotLive()) { k[kFsSkippedPaused]++; return true; }
            FM.Cfg c = sFs.C;
            int sw = FsSwitchBits();
            if (sw != sFsSwitches)
            {
                sFsSwitches = sw;
                c.Work = (sw & 1) != 0; c.Venue = (sw & 2) != 0; c.Visit = (sw & 4) != 0; sFsDrift = (sw & 8) != 0;
                sFs.Reconfigure(now);
                FsFinishPending(now);
            }
            if (!sFsSaid)
            {
                sFsSaid = true;
                k[kFsVersion] = FM.kModelVersion;
                DiagLine("farsim: shadow on (docs/345), model v" + FM.kModelVersion + ", seed " + c.WorldSeed.ToString("x16") + ", work " + (c.Work ? 1 : 0) + " venue " + (c.Venue ? 1 : 0) + " visit " + (c.Visit ? 1 : 0)
                    + " drift " + (sFsDrift ? 1 : 0) + ", R_live " + c.LiveRadiusCm / 100 + " R_far " + c.FarRadiusCm / 100 + " m, pass " + c.BoundaryPassMin + " min; compare only, nothing is parked, moved, held or written");
            }
            FsGonePoll();                     // T24 by polling
            FsScopePoll(now);                 // EP6 by polling, before anything is drained
            sFs.Drain(now, c.DueBudget);      // EP3 inside
            FsFinishPending(now);
            if (sFsPassAt < 0 && now >= sFsNextPass) FsPassBegin(now);
            if (sFsPassAt >= 0)
            {
                int end = Math.Min(sFsPass.Count, sFsPassAt + kFsSlice);
                while (sFsPassAt < end) FsOne(sFsPass[sFsPassAt++], now, true);  // advance first: a Sim that throws is not retried
                if (sFsPassAt >= sFsPass.Count)
                {
                    // whatever the pass end does, the pass is over: the next one starts on time
                    try { FsPassEnd(now); }
                    finally { sFsPassAt = -1; sFsNextPass = now + sFs.C.Ticks(sFs.C.BoundaryPassMin); }
                }
            }
            else
            {
                // a pending Sim's safe point is short: poll a few of them every frame between passes
                int n = sFs.Count;
                for (int i = 0, done = 0; i < n && done < kFsPendingPerFrame; i++)
                {
                    if (sFsPendAt >= sFs.Count) sFsPendAt = 0;
                    FM.Rec r = sFs.At(sFsPendAt++);
                    Sim s;
                    if (r.State != FM.GoFarPending || !sFsSims.TryGetValue(r.Id, out s)) continue;
                    done++;
                    FsOne(s, now, false);
                }
            }
            return true;
        }

        // Shadow: a would-be go-live is finished at once (EA never lost the Sim).
        static void FsFinishPending(long now)
        {
            for (int i = sFs.Count - 1; i >= 0; i--)
            {
                if (i >= sFs.Count) continue;
                FM.Rec r = sFs.At(i);
                if (r.State == FM.PendingLive) sFs.GoLiveDone(r.Id, now, true);
            }
        }

        // T24 by polling: a record whose Sim was destroyed, or is no longer among the pass's Sims, is dropped before the drain
        // can ask about it (records hold ids only; the id map is this pass's view of LotManager.Actors).
        static void FsGonePoll()
        {
            for (int i = sFs.Count - 1; i >= 0; i--)
            {
                if (i >= sFs.Count) continue;
                ulong id = sFs.At(i).Id;
                Sim s;
                if (!sFsSims.TryGetValue(id, out s) || s == null || s.HasBeenDestroyed || s.SimDescription == null) { sFsSims.Remove(id); FsOffHhLot(id); sFs.Gone(id); }
            }
        }

        static void FsOffHhLot(ulong id) { int at = sFsOnHhLot.BinarySearch(id); if (at >= 0) sFsOnHhLot.RemoveAt(at); }

        // EP6 by polling: the active household, its size or the selected Sim changed. Every record of a Sim now in scope is
        // promoted before the drain can touch it; the next pass starts at once.
        static void FsScopePoll(long now)
        {
            Household hh = Household.ActiveHousehold;
            Sim sel = PlumbBob.SelectedActor;
            int n = hh != null && hh.SimDescriptions != null ? hh.SimDescriptions.Count : -1;
            if (hh == sFsHh && sel == sFsSel && n == sFsHhCount) return;
            sFsHh = hh; sFsSel = sel; sFsHhCount = n;
            for (int i = sFs.Count - 1; i >= 0; i--)
            {
                if (i >= sFs.Count) continue;
                FM.Rec r = sFs.At(i);
                if (r.State == FM.Live || r.State == FM.Excluded) continue;
                int x = sFs.In.Excl(r.Id);
                if (!FM.ScopeExcl(x)) continue;
                sFs.Boundary(r.Id, now, x, 0, false, false, false, 0);
                if (r.State == FM.PendingLive) sFs.GoLiveDone(r.Id, now, true);
            }
            if (sFsPassAt < 0) sFsNextPass = now;
        }

        static int FsCmpSim(Sim a, Sim b) { return a.SimDescription.SimDescriptionId.CompareTo(b.SimDescription.SimDescriptionId); }

        static void FsPassBegin(long now)
        {
            sFsPass.Clear(); sFsSims.Clear(); sFsSeen.Clear(); sFsViewers.Clear(); sFsHhLots.Clear();
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null) continue;
                ulong id = s.SimDescription.SimDescriptionId;
                if (id == 0 || sFsSims.ContainsKey(id)) continue;
                sFsSims[id] = s;
                sFsPass.Add(s);
            }
            sFsPass.Sort(FsCmpSim);
            // viewer points, sampled once per pass: camera target, camera position, every household Sim
            sFsViewers.Add(CameraController.GetTarget());
            sFsViewers.Add(CameraController.GetPosition());
            Household hh = Household.ActiveHousehold;
            if (hh != null)
            {
                if (hh.LotHome != null) sFsHhLots.Add(hh.LotHome);
                foreach (Sim s in hh.AllActors)
                {
                    if (s == null || s.HasBeenDestroyed) continue;
                    sFsViewers.Add(s.Position);
                    Lot l = s.LotCurrent;
                    if (l != null && !l.IsWorldLot && !sFsHhLots.Contains(l)) sFsHhLots.Add(l);
                }
            }
            sFsCamLot = null;
            Lot cam = LotManager.GetLotAtPoint(sFsViewers[0]);
            if (cam != null && !cam.IsWorldLot && !sFsHhLots.Contains(cam)) { sFsHhLots.Add(cam); sFsCamLot = cam; }  // "a lot a viewer point is on" for the parked rule
            if (now >= sFsNextVenues)
            {
                sFsNextVenues = now + sFs.C.Ticks(60);
                sFsVenues.Clear();
                foreach (object o in LotManager.AllLots) { Lot l = o as Lot; if (l != null && l.IsCommunityLot && !l.IsWorldLot) sFsVenues.Add(l.LotId); }
                sFsVenues.Sort();
            }
            sFsPassAt = 0;
        }

        static void FsPassEnd(long now)
        {
            int[] k = PatchCounters();
            sFsSeen.Clear();
            sFsSeen.AddRange(sFsSims.Keys);  // ids only: a Sim destroyed during the pass is not read here (FsGonePoll took its id out)
            sFsSeen.Sort();
            for (int i = sFsOnHhLot.Count - 1; i >= 0; i--) if (sFsSeen.BinarySearch(sFsOnHhLot[i]) < 0) sFsOnHhLot.RemoveAt(i);  // arrivals of Sims that are gone
            k[kFsOnHhLot] = sFsOnHhLot.Count;
            sFs.EndPass(sFsSeen);   // T24: records of Sims that are gone
            sFs.Audit(now);         // V2, V4, stand-down
            FsFinishPending(now);
            if (now >= sFsNextSample)
            {
                sFsNextSample = now + sFs.C.Ticks(60);
                for (int i = 0; i < sFsPass.Count; i++)
                {
                    Sim s = sFsPass[i];
                    if (s == null || s.HasBeenDestroyed || s.SimDescription == null) continue;
                    FM.Rec r = sFs.Find(s.SimDescription.SimDescriptionId);
                    if (r == null || r.State < FM.FarHome || r.State > FM.VisitProposed) continue;
                    FsCompare(s, r, r.State, now, sFsHour);
                    FsSnap sn;
                    if (sFsSnap.TryGetValue(r.Id, out sn) && sn.VisitPm > 0) sFsFriendHours++;
                }
            }
            FsCounters();
            k[kFsPasses]++;
            k[kFsSnaps] = sFsSnap.Count;
            sFsPassAt = -1;
            sFsNextPass = now + sFs.C.Ticks(sFs.C.BoundaryPassMin);
        }

        static void FsCounters()
        {
            int[] k = PatchCounters();
            sFs.Sizes();
            for (int i = 0; i < FM.kCounters; i++) k[kFsC + i] = (int)Math.Min(sFs.N[i], int.MaxValue);
            for (int i = 0; i < FM.kStates; i++) k[kFsByState + i] = (int)sFs.ByState[i];
        }

        // Exclusion reason of one Sim now (docs/345 §5.3), scope first. Read-only getters.
        // Not evaluated in this build: ageUp within PrebuildHours (only "in progress"), pendingEvent, heldClass (needs the
        // docs/346 hold census), modOwned (FS-14), blood relatives in the tie rule; placeholder Sims have no Sim object and are
        // never in the pass.
        static int FsExcl(Sim s)
        {
            try
            {
                if (s == null || s.HasBeenDestroyed) return FM.XUnknownScope;
                SimDescription d = s.SimDescription;
                Household act = Household.ActiveHousehold;
                if (d == null || act == null || d.Household == null) return FM.XUnknownScope;
                if (d.Household == act) return FM.XHousehold;
                if (s == PlumbBob.SelectedActor || s.IsSelectable) return FM.XSelected;
                if (IsPlayerSim(s)) return FM.XUnknownScope;  // the shared predicate exempts it for a reason not named above
                if (!d.IsHuman) return FM.XPet;
                if (d.LotHome == null) return FM.XNoHome;
                if (d.CreatedByService != null || d.HasActiveRole || d.AssignedRole != null || s.Service != null) return FM.XService;
                if (s.Autonomy == null || s.Autonomy.SituationComponent == null) return FM.XUnknownScope;
                if (s.Autonomy.SituationComponent.Situations.Count > 0) return FM.XSituation;
                if (d.IsPregnant) return FM.XPregnant;
                if (d.AgingState != null && d.AgingState.IsAgingInProgress()) return FM.XAgeUp;
                if (d.IsGhost || d.IsDead) return FM.XDying;
                if (s.BuffManager != null) foreach (BuffInstance b in s.BuffManager.Buffs) if (b != null && b.TimeoutCount > 0f) return FM.XTimedBuff;  // strict first rule
                Occupation occ = d.Occupation;
                foreach (SimDescription h in act.SimDescriptions)
                {
                    if (h == null) continue;
                    if (occ != null && (occ.Boss == h || (occ.Coworkers != null && occ.Coworkers.Contains(h)))) return FM.XTie;
                    Relationship r = Relationship.Get(d, h, false);
                    if (r == null) continue;
                    LongTermRelationshipTypes t = r.LTR != null ? r.LTR.CurrentLTR : LongTermRelationshipTypes.Undefined;
                    if (r.AreRomantic() || t == LongTermRelationshipTypes.BestFriend || t == LongTermRelationshipTypes.BestFriendsForever) return FM.XTie;
                }
                return FM.XNone;
            }
            catch (Exception) { return FM.XUnknownScope; }
        }

        // §5.4 safe point, the parts a getter can answer: empty queue, no running interaction, standing, not carried or
        // contained, not routing, not in a rabbit hole. Reservations and household-facing queues are not read (FS-7).
        static bool FsSafe(Sim s)
        {
            return s.InteractionQueue != null && s.InteractionQueue.Count == 0 && s.CurrentInteraction == null && s.Posture is Sim.StandingPosture
                && s.Parent == null && !s.IsRouting && !s.SimInRabbitHolePosture;
        }

        static int FsDistCm(Vector3 p)
        {
            double best = double.MaxValue;
            for (int i = 0; i < sFsViewers.Count; i++)
            {
                double dx = p.x - sFsViewers[i].x, dz = p.z - sFsViewers[i].z, d = dx * dx + dz * dz;
                if (d < best) best = d;
            }
            double cm = Math.Sqrt(best) * 100.0;
            return cm > 2000000000.0 ? 2000000000 : (int)cm;
        }

        // Near triggers a poll can see (§4.2 rule 3): the Sim is on a household lot or a lot a household Sim is on, or a
        // household Sim's running interaction targets it. Camera jumps, map tags and pickers need hooks (FS-8 / FS-9).
        static bool FsTrigger(Sim s, Lot lc)
        {
            if (lc != null && !lc.IsWorldLot && lc != sFsCamLot && sFsHhLots.Contains(lc)) return true;
            Household hh = Household.ActiveHousehold;
            if (hh == null) return false;
            if (lc != null && lc == hh.LotHome) return true;
            foreach (Sim h in hh.AllActors)
            {
                if (h == null || h.HasBeenDestroyed) continue;
                InteractionInstance ii = h.CurrentInteraction;
                if (ii != null && ii.Target == s) return true;
            }
            return false;
        }

        // One Sim of the pass (EP1, EP2). pass = false: the per-frame poll of a pending Sim.
        static void FsOne(Sim s, long now, bool pass)
        {
            int[] k = PatchCounters();
            if (s == null || s.HasBeenDestroyed || s.SimDescription == null) return;
            SimDescription d = s.SimDescription;
            ulong id = d.SimDescriptionId;
            int excl = FsExcl(s);
            FM.Rec r = sFs.Find(id);
            int before = r != null ? r.State : FM.Live;
            Lot lc = s.LotCurrent, home = d.LotHome;
            if (FM.ScopeExcl(excl)) FsOffHhLot(id);
            else if (pass)
            {
                // an NPC arriving on the household's home lot: EA's own visits, for the visit calibration (FS-3). Counted for every
                // NPC, excluded or not (service, role and tie Sims arrive too); only household, selected and unknown scope skip.
                Household act = Household.ActiveHousehold;
                int at = sFsOnHhLot.BinarySearch(id);
                if (lc != null && act != null && lc == act.LotHome) { if (at < 0) { sFsOnHhLot.Insert(~at, id); k[kFsEaVisits]++; } }
                else if (at >= 0) sFsOnHhLot.RemoveAt(at);
            }
            if (excl != FM.XNone) { sFs.Boundary(id, now, excl, 0, false, false, false, 0); FsAfter(s, id, before, now); return; }
            int dCm = FsDistCm(s.Position);
            bool trigger = FsTrigger(s, lc);
            bool parked = s.SimInRabbitHolePosture || (lc != null && !lc.IsWorldLot && !sFsHhLots.Contains(lc));
            bool safe = before == FM.GoFarPending && !trigger && lc != null && FsSafe(s) && !World.IsObjectOnScreen(s.ObjectId);
            if (before >= FM.FarHome && before <= FM.VisitProposed)
            {
                // I9: job, level or home changed since the snapshot
                FsSnap sn;
                if (sFsSnap.TryGetValue(id, out sn) && (sn.Occ != FsOccKey(d) || sn.Home != (home != null ? home.LotId : 0)))
                {
                    sn.Occ = FsOccKey(d); sn.Home = home != null ? home.LotId : 0; sn.WakeMin = FsWakeMin(d); sn.ShiftDay = -1;
                    r.Home = sn.Home;
                    sFs.PlanChanged(id, now);
                }
            }
            if (safe && !sFsSnap.ContainsKey(id)) FsSnapTake(s, d, id, now);  // the plan reads it at T4
            sFs.Boundary(id, now, FM.XNone, dCm, parked, trigger, safe, home != null ? home.LotId : 0);
            FsAfter(s, id, before, now);
        }

        static void FsAfter(Sim s, ulong id, int before, long now)
        {
            FM.Rec r = sFs.Find(id);
            if (r == null) return;
            if (r.State == FM.PendingLive)
            {
                // the would-be go-live: compare what the machine had (state before T15) with EA's Sim, then finish it
                if (s != null && before >= FM.FarHome && before <= FM.VisitProposed) { FsCompare(s, r, before, now, sFsNear); FsDriftCompare(s, r, now); }
                sFs.GoLiveDone(id, now, true);
            }
        }

        static ulong FsOccKey(SimDescription d)
        {
            Occupation o = d.Occupation;
            School sc = d.CareerManager != null ? d.CareerManager.School : null;
            return (o != null ? (ulong)o.Guid * 131 + (ulong)(uint)o.CareerLevel : 0) ^ (sc != null ? (ulong)sc.Guid << 20 : 0);
        }

        static int FsWakeMin(SimDescription d)
        {
            Occupation o = d.Occupation;
            float h = o != null ? o.WakeupTime : 6f;   // docs/345 §6.2: Occupation.WakeupTime, else 6:00
            int m = (int)(h * 60f);
            return m < 0 || m > 1439 ? 360 : m;
        }

        // Go-far snapshot (§9.1), read-only: phases, plan inputs, drift baselines. Nothing is written to the Sim; where EA's
        // motive has no phase yet the machine draws one and keeps it here (the real build writes it, §6.1).
        static void FsSnapTake(Sim s, SimDescription d, ulong id, long now)
        {
            FsSnap sn = new FsSnap();
            sn.Tick = now;
            sn.Home = d.LotHome != null ? d.LotHome.LotId : 0;
            sn.WakeMin = FsWakeMin(d);
            sn.Occ = FsOccKey(d);
            FM.Rec r = sFs.Find(id);
            uint ep = (r != null ? r.Ep : 0) + 1;
            ulong seed = FM.Seed(sFs.C.WorldSeed, id, FM.kModelVersion);
            if (s.Autonomy != null && s.Autonomy.Motives != null) foreach (Motive m in s.Autonomy.Motives.AllMotives)
            {
                if (m == null || m.Tuning == null || m.Tuning.AutoSatisfyCurve == null) continue;
                ulong c = unchecked((ulong)m.Commodity);
                sn.Phase.Add(new KeyValuePair<ulong, int>(c, m.AutoUpdated ? (int)Math.Round(m.MotiveTimeDelta * 1000f) : FM.PhaseMilliHours(seed, ep, c)));
            }
            Household act = Household.ActiveHousehold;
            float best = 0f;
            if (act != null) foreach (SimDescription h in act.SimDescriptions)
            {
                Relationship rel = h != null ? Relationship.Get(d, h, false) : null;
                if (rel != null && rel.LTR != null && rel.LTR.Liking > best) best = rel.LTR.Liking;
            }
            sn.VisitPm = (int)(best * 2f);  // [inferred first value: liking 50 = one visit in ten days], calibrated by FS-4
            if (sFsDrift) foreach (Relationship rel in Relationship.Get(d))
            {
                if (sn.Ltr.Count >= kFsLtrCap) break;
                SimDescription o = rel != null ? rel.GetOtherSimDescription(d) : null;
                if (o == null || rel.LTR == null || IsPlayerDesc(o) || IsPlayerDesc(d)) continue;  // EP7: never a pair with a household, selected or unknown-scope side
                sn.Ltr.Add(new KeyValuePair<ulong, int>(o.SimDescriptionId, (int)Math.Round(rel.LTR.Liking)));
            }
            sFsSnap[id] = sn;
        }

        static int FsEaClass(Sim s, SimDescription d, Lot lc)
        {
            if (s.IsSleeping) return lc != null && lc == d.LotHome ? FM.EaSleep : FM.EaOther;
            if (s.SimInRabbitHolePosture)
            {
                if (s.IsAtWork) return FM.EaWork;
                School sc = d.CareerManager != null ? d.CareerManager.School : null;
                return sc != null && sc.IsAtWork ? FM.EaSchool : FM.EaVenue;
            }
            if (lc == null || lc.IsWorldLot || s.IsRouting) return FM.EaTransit;
            if (lc == d.LotHome) return FM.EaHome;
            return lc.IsCommunityLot ? FM.EaVenue : lc.IsResidentialLot ? FM.EaVisit : FM.EaOther;
        }

        // Model against EA for one far Sim: state, place, needs. state = the far state the machine has (or had) it in.
        static void FsCompare(Sim s, FM.Rec r, int state, long now, FsTabs t)
        {
            int[] k = PatchCounters();
            SimDescription d = s.SimDescription;
            Lot lc = s.LotCurrent;
            k[kFsSamples]++;
            t.State.Add(state, FsEaClass(s, d, lc));
            Lot ml = r.Place != 0 ? LotManager.GetLot(r.Place) : null;
            if (ml != null)
            {
                Vector3 c = ml.GetCenterPosition(), p = s.Position;
                double dx = c.x - p.x, dz = c.z - p.z;
                t.Place.Add((long)Math.Sqrt(dx * dx + dz * dz), 0);
                t.PlaceLotN++;
                if (lc == ml) t.PlaceLotSame++;
            }
            FsSnap sn;
            if (!sFsSnap.TryGetValue(r.Id, out sn) || s.Autonomy == null || s.Autonomy.Motives == null) return;
            float hour = SimClock.HoursPassedOfDay;
            float low = 0f;
            bool eaLow = false;
            foreach (Motive m in s.Autonomy.Motives.AllMotives)
            {
                if (m == null || m.Tuning == null || m.Tuning.AutoSatisfyCurve == null) continue;
                ulong c = unchecked((ulong)m.Commodity);
                int phase = int.MinValue;
                for (int i = 0; i < sn.Phase.Count; i++) if (sn.Phase[i].Key == c) { phase = sn.Phase[i].Value; break; }
                if (phase == int.MinValue) continue;
                float model = m.Tuning.AutoSatisfyCurve.Fx((phase / 1000f + hour + 24f) % 24f);  // EA's own curve object (docs/341 §9.4)
                string name = m.Commodity.ToString();
                FM.ErrTable e;
                if (!t.Need.TryGetValue(name, out e)) t.Need[name] = e = new FM.ErrTable("need:" + name, 2, 5, 10, 20, 40);
                e.Add((long)Math.Round(model), (long)Math.Round(m.Value));
                if (model < low) low = model;
                if (m.Value < -40f) eaLow = true;
                if (!sFsBelowSeen.Contains(name))
                {
                    // once per motive kind: is the curve minimum below the dropped -40 clamp (§6.1)
                    sFsBelowSeen.Add(name);
                    for (int q = 0; q < 96; q++) if (m.Tuning.AutoSatisfyCurve.Fx(q * 0.25f) < -40f) { k[kFsCurveBelowClamp]++; break; }
                }
            }
            if (t != sFsNear) return;
            if (low < -40f) { k[kFsBelow40]++; if ((int)low < k[kFsNeedMin]) k[kFsNeedMin] = (int)low; }
            if (eaLow) sFsEaBelow40++;
        }

        // FS-3: relationship drift of the far stretch, model against what EA's simulation did to the same pairs.
        static void FsDriftCompare(Sim s, FM.Rec r, long now)
        {
            FsSnap sn;
            if (!sFsDrift || !sFsSnap.TryGetValue(r.Id, out sn) || sn.Ltr.Count == 0) return;
            int[] k = PatchCounters();
            SimDescription d = s.SimDescription;
            FM.Cfg c = sFs.C;
            int day1 = (int)(now / c.TicksPerDay), day0 = (int)(sn.Tick / c.TicksPerDay);
            if (day1 - day0 >= kFsDriftDaysMax) day0 = day1 - kFsDriftDaysMax + 1;
            for (int i = 0; i < sn.Ltr.Count; i++)
            {
                ulong other = sn.Ltr[i].Key;
                Sim os;
                FM.Rec or = sFs.Find(other);
                // EP7 on both ids, again at the apply point: a pair with a side now in scope is skipped, never computed
                if (IsPlayerSim(s) || !sFsSims.TryGetValue(other, out os) || IsPlayerSim(os)) { sFsDriftSkipped++; continue; }
                if (or == null || or.State < FM.FarHome || or.State > FM.VisitProposed) continue;   // drift needs both sides far
                Relationship rel = Relationship.Get(d, os.SimDescription, false);
                if (rel == null || rel.LTR == null) continue;
                int model = 0;
                for (int day = day0; day <= day1; day++)
                {
                    int shared = FM.SharedBlocks(c, sFs.PlanOf(r.Id, day), sFs.PlanOf(other, day), day);
                    model += FM.CapStretch(model, FM.RelDriftDay(c.WorldSeed, r.Id, other, r.Ep, (uint)day, shared, sn.Ltr[i].Value), kFsStretchCap);
                    k[kFsPairDays]++;
                }
                int ea = (int)Math.Round(rel.LTR.Liking) - sn.Ltr[i].Value;
                sFsRelDrift.Add(model, ea);
                sFsModelDrift += model; sFsEaDrift += ea;
            }
        }

        // I5 / I10 answers for the machine. Each answer is taken once and kept in the snapshot (recorded input).
        sealed class FsInputs : FM.IInputs
        {
            public int Excl(ulong id)
            {
                Sim s;
                return sFsSims.TryGetValue(id, out s) ? FsExcl(s) : FM.XUnknownScope;  // a Sim the last pass did not see: unknown scope
            }
            public int WakeMin(ulong id) { FsSnap sn; return sFsSnap.TryGetValue(id, out sn) ? sn.WakeMin : 360; }
            // shortcut: start / finish hour and work days of the current level, not Occupation.IsWorkHour with its overrides
            // (days off, holidays); upgrade before farsimwork is timed (docs/345 §6.2).
            public bool Shift(ulong id, int day, out int s, out int e, out ulong place, out bool school)
            {
                s = e = 0; place = 0; school = false;
                FsSnap sn; Sim sim;
                if (!sFsSnap.TryGetValue(id, out sn)) return false;
                if (sn.ShiftDay != day)
                {
                    sn.ShiftDay = day; sn.ShiftHas = false;
                    try
                    {
                        if (sFsSims.TryGetValue(id, out sim) && sim != null && !sim.HasBeenDestroyed && sim.SimDescription != null)
                        {
                            SimDescription d = sim.SimDescription;
                            Occupation o = d.Occupation;
                            School sc = d.CareerManager != null ? d.CareerManager.School : null;
                            Career c = o as Career ?? sc;
                            if (c != null && c.StartTime >= 0f && c.FinishTime >= 0f && (c.DaysOfWeekToWork & SimClock.IntToDay(day)) != DaysOfTheWeek.None && c.CareerLoc != null && c.CareerLoc.Owner != null && c.CareerLoc.Owner.LotCurrent != null)
                            {
                                sn.ShiftS = (int)(c.StartTime * 60f); sn.ShiftE = (int)(c.FinishTime * 60f);
                                if (sn.ShiftE <= sn.ShiftS) sn.ShiftE = 1440;
                                sn.ShiftPlace = c.CareerLoc.Owner.LotCurrent.LotId; sn.ShiftSchool = c == sc && o == null;
                                sn.ShiftHas = true;
                            }
                        }
                    }
                    catch (Exception) { PatchCounters()[kFsErrors]++; }
                }
                s = sn.ShiftS; e = sn.ShiftE; place = sn.ShiftPlace; school = sn.ShiftSchool;
                return sn.ShiftHas;
            }
            // [inferred] every community lot counts as open; EA's opening hours are not asked (I10, FS-4 reads the error)
            public int VenueCount(int day) { return sFsVenues.Count; }
            public ulong Venue(int day, int index) { return sFsVenues[index]; }
            // shortcut: car speed for every Sim; the description's vehicle (car, bike, walk) is not read yet
            public int TravelMin(ulong from, ulong to)
            {
                Lot a = LotManager.GetLot(from), b = LotManager.GetLot(to);
                if (a == null || b == null) return 20;
                Vector3 p = a.GetCenterPosition(), q = b.GetCenterPosition();
                double dx = p.x - q.x, dz = p.z - q.z;
                return FM.TravelMin((long)(Math.Sqrt(dx * dx + dz * dz) * 100.0), FM.kSpeedCmPerMin[0]);
            }
            public int VisitPermille(ulong id, int day) { FsSnap sn; return sFsSnap.TryGetValue(id, out sn) ? sn.VisitPm : 0; }
            // [inferred] EA takes no visits between 24:00 and 05:00 (docs/347 §2); its cooldowns are not asked here
            public bool VisitOk(ulong id, int day, int minute) { return minute >= 300; }
        }

        // Harness API (reflect_call). "start": start the shadow task (switches farsim + farsimshadow must be on);
        // "state": one totals line; "summary <why>": every table to the log and the totals back; "clear": empty the
        // comparison tables; "set <key> <n>": one key of docs/345 §4.2 (FarRadiusM, LiveRadiusM, HandoffRadiusM, FarDwellMin,
        // ParkedDwellMin, BoundaryPassMin, GoFarTimeoutMin, GoFarRetryMin, FlapWindowMin, DueBudget, VenueGoPct, StatesOff).
        public static string FarSimShadow(string cmd)
        {
            try
            {
                string[] a = (cmd ?? "").Split(' ');
                switch (a[0])
                {
                    case "start":
                        if (!FsOn()) return "farsim: shadow not started: switches farsim " + ShortcutOn("farsim") + " farsimshadow " + ShortcutOn("farsimshadow");
                        if (!GameStates.IsInWorld()) return "farsim: shadow not started: not in a world";
                        FsStart();
                        return "farsim: shadow started, model v" + FM.kModelVersion;
                    case "state": return FsTotals("state")[0];
                    case "clear": FsClearTabs(); return "farsim: shadow tables cleared";
                    case "summary":
                    {
                        string why = a.Length > 1 ? a[1] : "now";
                        if (sFs == null) return "farsim: shadow totals " + why + ": off";
                        foreach (FsTabs t in new FsTabs[] { sFsNear, sFsHour })
                        {
                            string w = why + (t == sFsNear ? " near" : " hour");
                            foreach (string l in t.State.Lines(w)) DiagLine(l);
                            DiagLine(t.Place.Line(w) + " samelot " + t.PlaceLotSame + " of " + t.PlaceLotN);
                            List<string> names = new List<string>(t.Need.Keys);
                            names.Sort(string.CompareOrdinal);
                            foreach (string n in names) DiagLine(t.Need[n].Line(w));
                        }
                        DiagLine(sFsRelDrift.Line(why + " near") + " modelsum " + sFsModelDrift + " easum " + sFsEaDrift + " scopeskipped " + sFsDriftSkipped);
                        List<string> ls = FsTotals(why);
                        foreach (string l in ls) DiagLine(l);
                        return string.Join(" | ", ls.ToArray());
                    }
                    case "set":
                    {
                        int v;
                        if (sFs == null || a.Length < 3 || !int.TryParse(a[2], out v) || v < 0) return "farsim: set <key> <n> needs a running shadow and a number";
                        FM.Cfg c = sFs.C;
                        switch (a[1])
                        {
                            case "FarRadiusM": c.FarRadiusCm = v * 100; break;
                            case "LiveRadiusM": c.LiveRadiusCm = v * 100; break;
                            case "HandoffRadiusM": c.HandoffRadiusCm = v * 100; break;
                            case "FarDwellMin": c.FarDwellMin = v; break;
                            case "ParkedDwellMin": c.ParkedDwellMin = v; break;
                            case "BoundaryPassMin": c.BoundaryPassMin = Math.Max(1, v); break;
                            case "GoFarTimeoutMin": c.GoFarTimeoutMin = v; break;
                            case "GoFarRetryMin": c.GoFarRetryMin = v; break;
                            case "FlapWindowMin": c.FlapWindowMin = v; break;
                            case "DueBudget": c.DueBudget = v; break;
                            case "VenueGoPct": c.VenueGoPct = Math.Min(100, v); break;
                            case "StatesOff": c.StatesOff = v & 0x1FC; break;
                            default: return "farsim: unknown key " + a[1];
                        }
                        if (c.LiveRadiusCm + 5000 > c.FarRadiusCm) c.LiveRadiusCm = Math.Max(0, c.FarRadiusCm - 5000);  // the radii never touch (§4.2 rule 2)
                        sFs.Reconfigure(SimClock.CurrentTicks);
                        FsFinishPending(SimClock.CurrentTicks);
                        string line = "farsim: shadow set " + a[1] + " " + v + ", R_live " + c.LiveRadiusCm / 100 + " R_far " + c.FarRadiusCm / 100 + " m";
                        DiagLine(line);
                        return line;
                    }
                    default: return "farsim: unknown command " + a[0] + " (start, state, summary <why>, clear, set <key> <n>)";
                }
            }
            catch (Exception e) { PatchCounters()[kFsErrors]++; return "farsim: error " + e.GetType().Name; }
        }

        static void FsClearTabs()
        {
            foreach (FsTabs t in new FsTabs[] { sFsNear, sFsHour }) { t.State.Clear(); t.Place.Clear(); t.Need.Clear(); t.PlaceLotN = t.PlaceLotSame = 0; }
            sFsRelDrift.Clear();
            sFsModelDrift = sFsEaDrift = sFsDriftSkipped = sFsFriendHours = sFsEaBelow40 = 0;
        }

        // The totals, as lines of at most 470 characters (DiagLine's limit). The queue regexes read these exact texts.
        static List<string> FsTotals(string why)
        {
            int[] k = PatchCounters();
            List<string> l = new List<string>();
            if (sFs == null) { l.Add("farsim: shadow totals " + why + ": off"); return l; }
            FsCounters();
            long[] n = sFs.N;
            l.Add("farsim: shadow totals " + why + ": model v" + FM.kModelVersion + " passes " + k[kFsPasses] + " sims " + sFsPass.Count + " records " + n[FM.CRecords] + " high " + n[FM.CRecordsHigh]
                + " dwell " + n[FM.CDwell] + " due " + n[FM.CDueSize] + " plans " + n[FM.CPlanCache] + " snaps " + sFsSnap.Count + " transitions " + n[FM.CTransitions] + " goFar " + n[FM.CGoFar] + " goLive " + n[FM.CGoLive]
                + " goLiveFallback " + n[FM.CGoLiveFallback] + " noSafePoint " + n[FM.CNoSafePoint] + " flap " + n[FM.CFlap] + " catchUp " + n[FM.CCatchUp] + " gone " + n[FM.CGone] + " stateOff " + n[FM.CStateOff]
                + " edgeOther " + n[FM.CEdgeOther] + " comparisons " + k[kFsSamples] + " skippedPaused " + k[kFsSkippedPaused] + " errors " + k[kFsErrors]);
            l.Add("farsim: shadow household " + why + ": householdInFar " + n[FM.CHouseholdInFar] + " hhCaughtInFar " + n[FM.CHhCaughtInFar] + " driftHousehold " + n[FM.CDriftHousehold] + " ownerConflict " + n[FM.COwnerConflict]
                + " fViolations " + n[FM.CFViolations] + " illegal " + n[FM.CIllegal] + " stoodDown " + n[FM.CStoodDown] + " hhBlockedAtGoFar " + n[FM.CHhBlockedAtGoFar] + " scopePromoted " + n[FM.CScopePromoted]);
            StringBuilder b = new StringBuilder("farsim: shadow bystate ").Append(why).Append(':');
            for (int i = 0; i < FM.kStates; i++) b.Append(' ').Append(FM.kStateName[i]).Append(' ').Append(sFs.ByState[i]);
            l.Add(b.ToString());
            b = new StringBuilder("farsim: shadow excl ").Append(why).Append(':');
            for (int i = 1; i < FM.kExcl; i++) b.Append(' ').Append(FM.kExclName[i]).Append(' ').Append(sFs.ExclN[i]);
            l.Add(b.ToString());
            l.Add("farsim: shadow needs " + why + ": goLiveBelow40 " + k[kFsBelow40] + " goLiveNeedMin " + k[kFsNeedMin] + " eaBelow40 " + sFsEaBelow40 + " curveBelowClamp " + k[kFsCurveBelowClamp]
                + " goLiveDistress not-measured");
            l.Add("farsim: shadow visits " + why + ": proposed " + n[FM.CVisitsProposed] + " handover " + n[FM.CVisitHandover] + " friendHours " + sFsFriendHours + " eaArrivals " + k[kFsEaVisits]
                + " driftPairDays " + k[kFsPairDays] + " driftModel " + sFsModelDrift + " driftEa " + sFsEaDrift);
            return l;
        }
    }
}
