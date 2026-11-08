using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.Objects.Island;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.TuningValues;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // docs/208: two more docs/129 candidates on the [AutShadow] worker.
    //   env   = SimUpdate.CalculateSimEnvironmentScore's read part: the Sim's positive / negative environment score, the
    //           outside / window / light flags, the new-object count, the room info, and the scan's sequence of Sims
    //           (reactions) and objects (events) in the game's order, before any buff, event or reaction.
    //   bcast = ReactionBroadcaster.GetInRadiusSims's candidate set: the Sims within radius + buffer + kBcMargin that pass
    //           the room, species, tomb, CanReact and vehicle filters, with their distance verdict. Since docs/208 §8 the
    //           range comes from LotManager.Actors and each Sim's position (the worker's object rows), not the native
    //           range query (a cut row the sim thread had to fill per broadcaster). Line of sight, could-see and the
    //           reacting set stay on the sim thread (docs/129 §2.2).
    // Both ride the autonomy batch's shared cut (docs/122 §14.18): the B13 request that starts a batch adds up to kEnvMax
    // Sims (those whose environment update is due next, docs/208 §8.1) and kBcMax broadcasters (the latest that pulsed),
    // AutReference scores them on the sim thread at the cut (Mode R; autoffnoref skips it), AutWork on the worker (with
    // [AutShadow] Satb=2 during the next tick), AutFinish compares bit for bit and keeps the accepted answers in a sim-only
    // ready store. Switches (off; they need autshadow on: its probe, warm-up and mod guard): autoffenv / autoffbcast (Mode R),
    // autoffenvapply / autoffbcastapply (the sim uses the answer at the game's own call: docs/208 §8.2, §8.3), autoffnoref
    // (no reference at the cut: answers kept unchecked; timing runs), autoffdry (the live driver at the game's own call
    // against the answer: staleness, and for env what the game or the apply wrote).
    // docs/208 §9 (audit O7, O13), Mode R only, test switches, off: autofft2 reads the four docs/219 shared tier-2 rows for the
    // actor and up to kT2Max objects of its lot ([AutShadow] Tier2Rows=1); autofflot picks Camera.GetClosestVisibleLot's lot
    // on the worker from the request's inputs (autoffdry: against the game's own pick). No apply.
    // Counters 448-465, 481-490, 763-776 (docs/registry.md); abort reasons by native reason index in AppDomain
    // ts3fixes.autoffload.reasons (int[96]: env 0-23, bcast 24-47, t2 48-71, lot 72-95; 23 = a mod type on the worker's path).
    public static partial class Fixes
    {
        const int kEnvMax = 8, kBcMax = 16, kBcSims = 64, kOffRing = 32, kReasonModType = 23, kEnvSeq = 256, kEnvReady = 16;
        const float kBcMargin = 2f;      // metres a Sim may move between the cut and the pulse (docs/208 §3.2); Mode R uses it on both sides
        const float kBcFoot = 1f;        // the native range query tests footprint bounds, the position answer centres: this covers a Sim's bounds [inferred, I-404-3]
        const float kEnvDueMin = 0.5f;   // a Sim is requested once half its own update period has passed (docs/208 §8.1)
        const float kEnvMoveMax = 1f;    // apply: an outside Sim that moved farther since the cut uses the game's scan
        const float kBcObjMoveMax = 0.5f;
        const long kDryTicks = 240;      // a worker answer older than this (Simulator ticks since the request) is not dry-checked or applied
        const long kBcApplyTicks = 15;   // bcast apply: kBcMargin covers about this many ticks of walking [inferred, I-404-4]
        const int cEnv = 448, cBc = 457, cEnvAp = 481, cBcAp = 486, cTick = 534, cT2 = 763, cLot = 769, cIc = 878;
        const int kT2Max = 16, kLotSims = 32;
        const long kLotDryTicks = 15;    // autoffdry: a pick older than this is not compared with the game's call

        // docs/206 §15 (autofftick): one Mode R item that reads the sim clock (SimClock.CurrentTicks = Simulator / SimClock
        // sSimTickCnt + start + advanced, Hours24, the day) at the cut. With [AutShadow] Satb=2 the worker runs while the sim
        // stores tick t+1 natively (0xD81D39 / 0xD81D49); A0 needs 0.36.143's cut log of those statics. Counters 534-537.
        sealed class TickRes { public bool Done; public long Ticks; public float Hours; public int Day; }
        static readonly TickRes sTickRef = new TickRes(), sTickWork = new TickRes();
        static bool sTickPosted;
        static void TickScore(TickRes r)
        {
            r.Done = false;
            r.Ticks = SimClock.CurrentTicks; r.Hours = SimClock.Hours24; r.Day = (int)SimClock.CurrentDayOfWeek;
            r.Done = true;
        }

        // docs/219 §2: one object's answers of the four shared rows
        sealed class T2Res { public bool Done, Exc, Roof; public int Abort = -1, Height; public Vector3 Fwd; public ulong Pool; }
        static readonly T2Res[] sT2Ref = NewT2(), sT2Work = NewT2();
        static readonly GameObject[] sT2Objs = new GameObject[kT2Max];
        static int sT2N, sT2At;
        // docs/126 §8 (autofft2ic, with autofft2 and [AutShadow] Tier2IcRows=1): the class-a IC rows for the same objects. Each
        // group writes its own slots of V (G1 0-5, G2 6-8, G3 9-10, G4 11-14, docs/data/tier2-rows-handoff.md); equal = all slots
        const int kIcV = 23;  // slots 0-14: IcG1-IcG4; 15-19: IcG5 (E rows); 20-21: IcG6 (E3 rows); 22: IcG7 (P6)
        sealed class IcRes { public bool Done, Exc; public int Abort = -1; public readonly long[] V = new long[kIcV]; }
        static readonly IcRes[] sIcRef = NewIc(), sIcWork = NewIc();
        static int sIcN;
        static Household sIcHh;  // the active household at the post (sim thread), for IcG5's household rule
        // World.gWorld is not public; read once on the sim thread (RankRequest), never filled on the worker
        static IWorld sIcWorld;
        static IcRes[] NewIc() { IcRes[] a = new IcRes[kT2Max]; for (int i = 0; i < a.Length; i++) a[i] = new IcRes(); return a; }
        static T2Res[] NewT2() { T2Res[] a = new T2Res[kT2Max]; for (int i = 0; i < a.Length; i++) a[i] = new T2Res(); return a; }

        // docs/208 §9.2: what Camera.GetClosestVisibleLot reads that is not a plain lot field, taken on the sim thread at the
        // request (a Sim's IsOutside is a position-keyed row that misses for a walking Sim), so both sides score the same input
        sealed class LotReq
        {
            public Vector3 Target; public float Hours; public Lot Active, World; public int N; public Household H;
            public readonly List<Lot> Base = new List<Lot>();
            public readonly Lot[] SimLot = new Lot[kLotSims]; public readonly bool[] Inside = new bool[kLotSims], Upper = new bool[kLotSims];
        }
        sealed class LotRes { public bool Done, Exc, Used; public int Abort = -1, Best; public Lot Pick, Focus; public long Tick; }
        static readonly LotReq sLotReq = new LotReq();
        static readonly LotRes sLotRef = new LotRes(), sLotWork = new LotRes(), sLotReady = new LotRes();
        static bool sLotPosted, sLotHookOk;

        sealed class EnvRes
        {
            public Sim S; public bool Done, Exc, SeqOver; public int Abort = -1;
            public float Pos, Neg; public int Flags, Num;   // flags: 1 outside, 2 window, 4 light, 8 empty room (dirtiness 0, early return), 16 residential, 32 scanned (not in a rabbit hole)
            public uint Puddle, Burnt, Size; public float Covered;  // room info (inside)
            public Lot Lot; public int Room; public Vector3 At;    // context at the cut
            public readonly IGameObject[] Seq = new IGameObject[kEnvSeq]; public int SeqN;  // Sims (reactions) and objects (events), game order
            public long Tick; public bool Used;             // ready store only
        }
        sealed class BcReq
        {
            public ReactionBroadcaster B; public IGameObject Obj; public float R; public CASAgeGenderFlags[] Species;
            public bool RoomOnly; public AutonomyLevel Level; public int Skip;   // Skip: 1 RoomCheck override (pool), 2 other override
        }
        sealed class BcRes
        {
            public bool Done, Exc; public int Abort = -1, N;
            public readonly Sim[] Sims = new Sim[kBcSims]; public readonly bool[] Near = new bool[kBcSims];
            public Vector3 At;                              // the broadcasting object's position at the cut
            public ReactionBroadcaster B; public long Tick; public bool Used;  // ready store only
        }

        static readonly EnvRes[] sEnvRef = NewEnv(kEnvMax), sEnvWork = NewEnv(kEnvMax), sEnvReady = NewEnv(kEnvReady);
        static readonly BcReq[] sBcReq = NewBcReq();
        static readonly BcRes[] sBcRef = NewBc(kBcMax), sBcWork = NewBc(kBcMax), sBcReady = NewBc(kBcMax);
        static readonly Sim[] sEnvSims = new Sim[kEnvMax];
        static int sEnvN, sBcN, sOffBcRing, sEnvReadyAt;
        static bool sOffPosted, sOffRefRan;
        static long sOffTick;
        static readonly ReactionBroadcaster[] sBcRing = new ReactionBroadcaster[kOffRing];
        static readonly int[] sOffReasons = new int[96];
        static readonly Dictionary<Type, bool> sOffTypes = new Dictionary<Type, bool>();  // filled on the sim thread only
        static FieldInfo fUpdSim, fBcReacting, fEnvTime;
        static MethodInfo mDoSimEnv, mSendObjEnv;
        static float sBcBuffer = 0.01f;
        static bool sOffHooksOk;

        // docs/208 §8.1: when each Sim's environment update last ran and how long its period was (Simulator ticks); the
        // request takes the Sims whose next update is nearest (largest share of the period passed), not the ones that just ran
        sealed class EnvDue { public long Last, Period; }
        static readonly Dictionary<Sim, EnvDue> sEnvDue = new Dictionary<Sim, EnvDue>();
        static readonly List<Sim> sEnvGone = new List<Sim>();
        static readonly float[] sEnvPick = new float[kEnvMax];

        delegate void DoSimEnvFn(Sim s, bool isPreggo, bool isWildHorse, bool isDeer, ref bool foundEnemy, ref bool foundLover, ref bool reactionPlayed, ref bool areAttracted);
        delegate void SendObjEnvFn(GameObject obj);

        static EnvRes[] NewEnv(int n) { EnvRes[] a = new EnvRes[n]; for (int i = 0; i < a.Length; i++) a[i] = new EnvRes(); return a; }
        static BcRes[] NewBc(int n) { BcRes[] a = new BcRes[n]; for (int i = 0; i < a.Length; i++) a[i] = new BcRes(); return a; }
        static BcReq[] NewBcReq() { BcReq[] a = new BcReq[kBcMax]; for (int i = 0; i < a.Length; i++) a[i] = new BcReq(); return a; }

        static void ApplyAutOffload()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                fUpdSim = typeof(SimUpdate).GetField("mSim", all);
                fEnvTime = typeof(SimUpdate).GetField("mTimePassedEnvironment", all);
                fBcReacting = typeof(ReactionBroadcaster).GetField("mSimsReacting", all);
                FieldInfo buf = typeof(ReactionBroadcaster).GetField("kBroadcasterRadiusBuffer", all);
                mDoSimEnv = typeof(SimUpdate).GetMethod("DoSimEnvironmentUpdateInternal", all);
                mSendObjEnv = typeof(SimUpdate).GetMethod("SendObjectEnvironmentUpdateEvents", all);
                if (fUpdSim == null || fEnvTime == null || fBcReacting == null || buf == null || mDoSimEnv == null || mSendObjEnv == null)
                    throw new MissingMemberException("private member not found");
                sBcBuffer = (float)buf.GetValue(null);
            }
            catch (Exception e) { AppliedLog().Add("AutOffload: off: " + e.Message); return; }
            List<string> failed = new List<string>();
            sOffHooksOk = Hook(failed, typeof(SimUpdate), "CalculateSimEnvironmentScore", null, typeof(AutEnvHook), "EnvHook", "EnvOrig")
                & Hook(failed, typeof(ReactionBroadcaster), "GetInRadiusSims", new Type[] { typeof(float) }, typeof(AutBcastHook), "RadiusHook", "RadiusOrig");
            sLotHookOk = Hook(failed, typeof(Camera), "GetClosestVisibleLot", new Type[] { typeof(Lot), typeof(Vector3).MakeByRefType() }, typeof(AutLotHook), "LotHook", "LotOrig");
            if (failed.Count > 0) AppliedLog().Add("AutOffload: not patched: " + string.Join(", ", failed.ToArray()));
            AppDomain.CurrentDomain.SetData("ts3fixes.autoffload.reasons", sOffReasons);
        }

        // ---- request (sim thread, TryRequest before Post, first request of a batch) ----

        static void OffRequest()
        {
            sOffPosted = false; sOffRefRan = false; sEnvN = sBcN = 0;
            sTickPosted = ShortcutOn("autofftick");
            if (sTickPosted) { PatchCounters()[cTick]++; sOffPosted = true; sOffTick = Simulator.TicksElapsed(); }
            RankRequest();
            if (!sOffHooksOk) return;
            bool env = ShortcutOn("autoffenv"), bc = ShortcutOn("autoffbcast");
            if (!env && !bc) return;
            int[] c = PatchCounters();
            try
            {
                if (env) PickDueSims();
                for (int k = 0; bc && k < kOffRing && sBcN < kBcMax; k++)
                {
                    int at = (sOffBcRing - 1 - k + 2 * kOffRing) % kOffRing;
                    ReactionBroadcaster b = sBcRing[at];
                    if (b == null || b.BroadcastingObject == null || b.BroadcasterParams == null) continue;
                    bool dup = false;
                    for (int j = 0; j < sBcN; j++) dup |= sBcReq[j].B == b;
                    if (dup) continue;
                    BcReq q = sBcReq[sBcN++];
                    q.B = b; q.Obj = b.BroadcastingObject; q.R = b.GetPulseRadius();
                    ReactionBroadcasterParams p = b.BroadcasterParams;
                    q.Species = p.SpeciesToEffect; q.RoomOnly = p.AffectBroadcasterRoomOnly; q.Level = p.AutonomyLevelToReact;
                    q.Skip = RoomCheckKind(b.GetType());
                    AutShadowNative.AddRow(q.Obj.ObjectId.Value, 0);
                }
            }
            catch (Exception e) { c[cEnv + 5]++; AutNote("offload request: " + e.Message); }
            c[cEnv] += sEnvN; c[cBc] += sBcN;
            sOffPosted = sEnvN + sBcN + sT2N > 0 || sTickPosted || sLotPosted;
            sOffTick = Simulator.TicksElapsed();
        }

        // docs/208 §9: the autofft2 / autofflot items of this batch. Done is cleared here: OffScore can stop before their loops.
        static void RankRequest()
        {
            sT2N = 0; sIcN = 0; sLotPosted = false;
            sLotRef.Done = sLotWork.Done = false;
            for (int i = 0; i < kT2Max; i++) sT2Ref[i].Done = sT2Work[i].Done = sIcRef[i].Done = sIcWork[i].Done = false;
            int[] c = PatchCounters();
            try
            {
                if (ShortcutOn("autofft2")) { T2Pick(sReqs[0].Actor); if (ShortcutOn("autofft2ic") && IcWorld()) { sIcN = sT2N; sIcHh = Household.ActiveHousehold; } }
                if (ShortcutOn("autofflot")) sLotPosted = LotRequest(sLotReq);
            }
            catch (Exception e) { c[cT2 + 5]++; AutNote("offload rank request: " + e.Message); }
            c[cT2] += sT2N; c[cIc] += sIcN;
            if (sLotPosted) c[cLot]++;
            if (sT2N > 0 || sLotPosted) { sOffPosted = true; sOffTick = Simulator.TicksElapsed(); }
        }

        // the actor (three rows; its pool row is keyed by a moving position, so it is not asked) and kT2Max - 1 objects of its
        // lot, a different run of them each batch, so the rows cover the lot over a load
        static void T2Pick(Sim actor)
        {
            Lot lot = actor != null ? actor.LotCurrent : null;
            if (lot == null) return;
            sT2Objs[sT2N++] = actor;
            GameObject[] all = lot.GetObjects<GameObject>();
            for (int i = 0; i < all.Length && sT2N < kT2Max; i++)
            {
                GameObject o = all[(sT2At + i) % all.Length];
                if (o == null || o is Sim || o.HasBeenDestroyed || !o.InWorld || !OffTypeOk(o.GetType(), false)) continue;
                sT2Objs[sT2N++] = o;
                AutShadowNative.AddRow(o.ObjectId.Value, 0);
            }
            sT2At += kT2Max;
        }

        // Filled in the order Camera.GetClosestVisibleLot reads them. False: no household selected (the game's method then
        // only handles the focus change) or more Sims than the arrays hold.
        static bool LotRequest(LotReq q)
        {
            if (!sLotHookOk) return false;
            IActor sel = PlumbBob.SelectedActor;
            Household h = sel != null ? sel.Household : null;
            if (h == null) return false;
            q.H = h;  // houseboat row: the household's own lots stay refused (docs/208 §9.2)
            Sim active = Sim.ActiveActor;
            Lot home = active != null ? active.LotHome : null;
            q.Base.Clear();
            q.Base.AddRange(h.GreetedLots);
            if (h.RealEstateManager != null) q.Base.AddRange(h.RealEstateManager.GetPrivateLotMoveToLots());
            Windows.LookInWindow peep = sel.CurrentInteraction as Windows.LookInWindow;
            if (peep != null && peep.IsPeeping) q.Base.Add(sel.LotCurrent);
            if (home != null && !q.Base.Contains(home)) q.Base.Add(home);
            q.N = 0;
            foreach (Sim s in h.AllActors) if (!LotAddSim(q, s)) return false;
            Sims3.Gameplay.Passport.Passport pp = Sims3.Gameplay.Passport.Passport.Instance;
            Sim hosted = pp != null ? pp.GetHostedSim() : null;
            if (hosted != null && !LotAddSim(q, hosted)) return false;
            q.Target = CameraController.GetLODInterestPosition();
            q.Hours = SimClock.HoursPassedOfDay;
            q.Active = LotManager.ActiveLot;
            q.World = LotManager.GetWorldLot();
            GameUtils.GetCurrentWorldType();  // pre-warm: the lookup's first UserCreated answer adds to a static dictionary (Lot.IsOwned path)
            return true;
        }

        static bool LotAddSim(LotReq q, Sim s)
        {
            if (q.N >= kLotSims) return false;
            q.SimLot[q.N] = s.LotCurrent; q.Inside[q.N] = !s.IsOutside; q.Upper[q.N] = s.Level != 0;
            q.N++;
            return true;
        }

        // docs/208 §8.1: the kEnvMax Sims with the largest share of their own update period passed (>= kEnvDueMin), so
        // their next update comes soon after this cut; a Sim seen once (no period yet) waits for its second update
        static void PickDueSims()
        {
            long now = Simulator.TicksElapsed();
            sEnvGone.Clear();
            foreach (KeyValuePair<Sim, EnvDue> kv in sEnvDue)
            {
                Sim s = kv.Key;
                if (s.HasBeenDestroyed) { sEnvGone.Add(s); continue; }
                EnvDue d = kv.Value;
                if (d.Period <= 0 || s.LotCurrent == null) continue;
                float f = (float)(now - d.Last) / d.Period;
                if (f < kEnvDueMin) continue;
                sEnvN = AutOffloadCore.InsertTop(sEnvPick, sEnvSims, sEnvN, kEnvMax, f, s);
            }
            for (int i = 0; i < sEnvGone.Count; i++) sEnvDue.Remove(sEnvGone[i]);
            for (int i = 0; i < sEnvN; i++)
            {
                Sim s = sEnvSims[i];
                AutShadowNative.AddRow(s.ObjectId.Value, 1);
                AutShadowNative.AddRow(s.LotCurrent.ObjectId.Value, 0);
            }
        }

        // 0 = the base RoomCheck (Lot.AreInSameRoom), 1 = PoolRoomCheckBroadcaster, 2 = any other override (mods)
        static readonly Dictionary<Type, int> sRoomKinds = new Dictionary<Type, int>();
        static int RoomCheckKind(Type t)
        {
            int k;
            if (sRoomKinds.TryGetValue(t, out k)) return k;
            MethodInfo m = t.GetMethod("RoomCheck", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            k = m == null || (m.DeclaringType == typeof(ReactionBroadcaster) && L3Ok(m)) ? 0 : m.DeclaringType.Name == "PoolRoomCheckBroadcaster" ? 1 : 2;
            sRoomKinds[t] = k;
            return k;
        }

        // A type whose environment virtuals are the game's own, unswapped (docs/128 G-M5 for this closure). Sim thread fills,
        // the worker only reads; a type the worker meets first aborts the Sim with kReasonModType (the reference ran first
        // at the same cut and saw the same objects, so this is rare).
        static bool OffTypeOk(Type t, bool onWorker)
        {
            bool ok;
            if (sOffTypes.TryGetValue(t, out ok)) return ok;
            if (onWorker) return false;
            ok = Array.IndexOf(kGameModules, t.Assembly.GetName().Name) >= 0;
            MethodInfo m = ok ? t.GetMethod("GetEnvironmentScore", new Type[] { typeof(Sim) }) : null;
            if (m != null) ok = Array.IndexOf(kGameModules, m.DeclaringType.Assembly.GetName().Name) >= 0 && L3Ok(m);
            sOffTypes[t] = ok;
            return ok;
        }

        // ---- the cut: reference (sim) and worker; then the finish ----

        static void OffScore(bool worker)
        {
            if (!sOffPosted) return;
            if (!worker)
            {
                if (ShortcutOn("autoffnoref")) return;  // timing runs: no sim-side copy of the work at the cut
                sOffRefRan = true;
            }
            if (sTickPosted)
            {
                TickRes t = worker ? sTickWork : sTickRef;
                try { TickScore(t); } catch (Exception) { t.Done = false; }
                if (worker && AutShadowNative.Stopped()) t.Done = false;
            }
            EnvRes[] er = worker ? sEnvWork : sEnvRef;
            BcRes[] br = worker ? sBcWork : sBcRef;
            int i = 0;
            for (; i < sEnvN; i++)
            {
                EnvRes r = er[i];
                r.Done = false; r.Exc = false; r.Abort = -1; r.S = sEnvSims[i];
                if (worker && AutShadowNative.Stopped()) break;
                try { EnvScore(sEnvSims[i], r, worker); }
                catch (Exception) { r.Exc = true; }
                Settle(ref r.Abort);
                r.Done = !(worker && AutShadowNative.Stopped());  // KeepPartial rule (docs/122 §14.21): valid if no stop after its reads
                if (!r.Done) return;
            }
            for (int j = 0; j < sBcN; j++)
            {
                BcRes r = br[j];
                r.Done = false; r.Exc = false; r.Abort = -1; r.N = 0;
                if (worker && AutShadowNative.Stopped()) return;
                try { BcCandidates(sBcReq[j], r); }
                catch (Exception) { r.Exc = true; }
                Settle(ref r.Abort);
                r.Done = !(worker && AutShadowNative.Stopped());
                if (!r.Done) return;
            }
            T2Res[] tr = worker ? sT2Work : sT2Ref;
            for (int k = 0; k < sT2N; k++)
            {
                T2Res r = tr[k];
                r.Exc = false; r.Abort = -1;
                if (worker && AutShadowNative.Stopped()) return;
                try { T2Read(sT2Objs[k], r); }
                catch (Exception) { r.Exc = true; }
                Settle(ref r.Abort);
                r.Done = !(worker && AutShadowNative.Stopped());
                if (!r.Done) return;
            }
            IcRes[] ir = worker ? sIcWork : sIcRef;
            for (int k = 0; k < sIcN; k++)
            {
                IcRes r = ir[k];
                r.Exc = false; r.Abort = -1;
                if (worker && AutShadowNative.Stopped()) return;
                try { IcRead(sT2Objs[k], r.V); }
                catch (Exception) { r.Exc = true; }
                Settle(ref r.Abort);
                r.Done = !(worker && AutShadowNative.Stopped());
                if (!r.Done) return;
            }
            if (sLotPosted)
            {
                LotRes r = worker ? sLotWork : sLotRef;
                r.Exc = false; r.Abort = -1;
                if (worker && AutShadowNative.Stopped()) return;
                try { LotPick(sLotReq, r); }
                catch (Exception) { r.Exc = true; }
                Settle(ref r.Abort);
                r.Done = !(worker && AutShadowNative.Stopped());
            }
        }

        static void Settle(ref int abort)
        {
            int ab = AutShadowNative.Aborted();  // always clear off the worker
            if (ab >= 0) { if (abort < 0) abort = ab; AutShadowNative.ResetAbort(); }
        }

        static void OffFinish(int lr)
        {
            if (!sOffPosted) return;
            sOffPosted = false;
            int[] c = PatchCounters();
            bool ok = lr == 0 || (lr == 1 && sKeepPartial);
            bool cmp = sOffRefRan && ok, noref = !sOffRefRan && ok;  // noref: autoffnoref, the worker's answers kept unchecked
            if (sTickPosted)
            {
                if (!cmp || !sTickWork.Done || !sTickRef.Done) c[cTick + 3]++;
                else if (sTickWork.Ticks == sTickRef.Ticks && AutShadowCore.Bits(sTickWork.Hours) == AutShadowCore.Bits(sTickRef.Hours) && sTickWork.Day == sTickRef.Day) c[cTick + 1]++;
                else { c[cTick + 2]++; OffLog("tick", null, (float)(sTickWork.Ticks - sTickRef.Ticks), 0f, sTickWork.Hours, sTickRef.Hours, sTickWork.Day, sTickRef.Day); }
                sTickPosted = false;
            }
            for (int i = 0; i < sEnvN; i++)
            {
                EnvRes w = sEnvWork[i], r = sEnvRef[i];
                if (!(cmp || noref) || !w.Done || (cmp && !r.Done)) { c[cEnv + 4]++; continue; }
                if (w.Exc || (cmp && r.Exc)) { c[cEnv + 5]++; continue; }
                if (w.Abort >= 0 || (cmp && r.Abort >= 0)) { c[cEnv + 3]++; Reason(0, w.Abort >= 0 ? w.Abort : r.Abort); continue; }
                if (cmp)
                {
                    bool eq = EnvEqual(w, r);
                    c[eq ? cEnv + 1 : cEnv + 2]++;
                    if (!eq) { OffLog("env", r.S, w.Pos, r.Pos, w.Neg, r.Neg, w.Flags, r.Flags); continue; }
                }
                KeepEnv(w);
                c[cEnvAp]++;
            }
            for (int j = 0; j < sBcN; j++)
            {
                BcRes w = sBcWork[j], r = sBcRef[j];
                if (!(cmp || noref) || !w.Done || (cmp && !r.Done)) { c[cBc + 5]++; continue; }
                if (w.Exc || (cmp && r.Exc)) { c[cBc + 6]++; continue; }
                if (w.Abort >= 0 || (cmp && r.Abort >= 0)) { c[cBc + 4]++; Reason(24, w.Abort >= 0 ? w.Abort : r.Abort); continue; }
                if (cmp)
                {
                    int k = BcClass(w, r);
                    c[cBc + 1 + k]++;  // 0 A0, 1 A1 (same set, other order), 2 A2
                    if (k != 0) { OffLog("bcast", sBcReq[j].Obj, w.N, r.N, 0, 0, k, 0); continue; }
                }
                KeepBc(sBcReq[j].B, w);
                c[cBcAp]++;
            }
            for (int k = 0; k < sT2N; k++)
            {
                T2Res w = sT2Work[k], r = sT2Ref[k];
                if (!cmp || !w.Done || !r.Done) { c[cT2 + 4]++; continue; }
                if (w.Exc || r.Exc) { c[cT2 + 5]++; continue; }
                if (w.Abort >= 0 || r.Abort >= 0) { c[cT2 + 3]++; Reason(48, w.Abort >= 0 ? w.Abort : r.Abort); continue; }
                bool eq = w.Roof == r.Roof && w.Height == r.Height && w.Pool == r.Pool && AutShadowCore.Bits(w.Fwd.x) == AutShadowCore.Bits(r.Fwd.x)
                    && AutShadowCore.Bits(w.Fwd.y) == AutShadowCore.Bits(r.Fwd.y) && AutShadowCore.Bits(w.Fwd.z) == AutShadowCore.Bits(r.Fwd.z);
                c[eq ? cT2 + 1 : cT2 + 2]++;
                if (!eq) OffLog("t2", sT2Objs[k], w.Fwd.x, r.Fwd.x, w.Fwd.z, r.Fwd.z, w.Roof ? 1 : 0, r.Roof ? 1 : 0);
            }
            for (int k = 0; k < sIcN; k++)
            {
                IcRes w = sIcWork[k], r = sIcRef[k];
                if (!cmp || !w.Done || !r.Done) { c[cIc + 4]++; continue; }
                if (w.Exc || r.Exc) { c[cIc + 5]++; continue; }
                if (w.Abort >= 0 || r.Abort >= 0) { c[cIc + 3]++; Reason(48, w.Abort >= 0 ? w.Abort : r.Abort); continue; }
                int d = 0;
                while (d < kIcV && w.V[d] == r.V[d]) d++;
                c[d == kIcV ? cIc + 1 : cIc + 2]++;
                if (d < kIcV) OffLog("t2ic", sT2Objs[k], d, 0, 0, 0, (int)w.V[d], (int)r.V[d]);
            }
            if (sLotPosted)
            {
                LotRes w = sLotWork, r = sLotRef;
                if (!(cmp || noref) || !w.Done || (cmp && !r.Done)) c[cLot + 4]++;
                else if (w.Exc || (cmp && r.Exc)) c[cLot + 5]++;
                else if (w.Abort >= 0 || (cmp && r.Abort >= 0)) { c[cLot + 3]++; Reason(72, w.Abort >= 0 ? w.Abort : r.Abort); }
                else
                {
                    bool eq = !cmp || (w.Pick == r.Pick && w.Focus == r.Focus && w.Best == r.Best);
                    if (cmp) c[eq ? cLot + 1 : cLot + 2]++;
                    if (eq) { sLotReady.Pick = w.Pick; sLotReady.Tick = sOffTick; sLotReady.Used = false; sLotReady.Done = true; }
                    else OffLog("lot", w.Pick, 0f, 0f, 0f, 0f, r.Pick != null ? (int)r.Pick.LotId : 0, 0);
                }
            }
        }

        static bool EnvEqual(EnvRes w, EnvRes r)
        {
            if (AutShadowCore.Bits(w.Pos) != AutShadowCore.Bits(r.Pos) || AutShadowCore.Bits(w.Neg) != AutShadowCore.Bits(r.Neg)
                || w.Flags != r.Flags || w.Num != r.Num || w.SeqN != r.SeqN || w.SeqOver != r.SeqOver || w.Puddle != r.Puddle
                || w.Burnt != r.Burnt || w.Size != r.Size || AutShadowCore.Bits(w.Covered) != AutShadowCore.Bits(r.Covered)) return false;
            for (int i = 0; i < w.SeqN; i++) if (!ReferenceEquals(w.Seq[i], r.Seq[i])) return false;
            return true;
        }

        // The ready store (sim thread only): accepted answers wait here for the Sim's own update or the broadcaster's next
        // pulse. The worker's arrays are rewritten by the next job (with Satb act during the next tick), so nothing at the
        // game's call reads them.
        static void KeepEnv(EnvRes w)
        {
            EnvRes d = null;
            for (int i = 0; i < kEnvReady && d == null; i++) if (sEnvReady[i].S == w.S) d = sEnvReady[i];  // a newer cut replaces it
            if (d == null) { d = sEnvReady[sEnvReadyAt]; sEnvReadyAt = (sEnvReadyAt + 1) % kEnvReady; }
            d.S = w.S; d.Done = true; d.Used = false; d.Tick = sOffTick;
            d.Pos = w.Pos; d.Neg = w.Neg; d.Flags = w.Flags; d.Num = w.Num; d.Puddle = w.Puddle; d.Burnt = w.Burnt; d.Size = w.Size;
            d.Covered = w.Covered; d.Lot = w.Lot; d.Room = w.Room; d.At = w.At; d.SeqOver = w.SeqOver; d.SeqN = w.SeqN;
            Array.Copy(w.Seq, d.Seq, w.SeqN);
            for (int i = w.SeqN; i < kEnvSeq && d.Seq[i] != null; i++) d.Seq[i] = null;
        }

        static void KeepBc(ReactionBroadcaster b, BcRes w)
        {
            BcRes d = null;
            int free = -1;
            for (int i = 0; i < kBcMax && d == null; i++)
            {
                if (sBcReady[i].B == b) d = sBcReady[i];
                else if (free < 0 && (sBcReady[i].B == null || sBcReady[i].Used || sOffTick - sBcReady[i].Tick > kDryTicks)) free = i;
            }
            if (d == null) d = sBcReady[free >= 0 ? free : (int)(sOffTick % kBcMax)];
            d.B = b; d.Done = true; d.Used = false; d.Tick = sOffTick; d.At = w.At; d.N = w.N;
            Array.Copy(w.Sims, d.Sims, w.N); Array.Copy(w.Near, d.Near, w.N);
        }

        static void Reason(int at, int r) { if (r >= 0 && r < 24) sOffReasons[at + r]++; }

        static int BcClass(BcRes w, BcRes r) { return AutOffloadCore.SeqClass(w.Sims, w.Near, w.N, r.Sims, r.Near, r.N); }

        static int sOffLogged;
        static void OffLog(string what, IGameObject o, float wa, float ra, float wb, float rb, int wf, int rf)
        {
            if (sOffLogged >= 100) return;
            sOffLogged++;
            try
            {
                AutShadowNative.Log("{\"t\":\"off-" + what + "\",\"obj\":" + (o != null ? o.ObjectId.Value : 0UL) + ",\"wa\":\"" + AutShadowCore.Bits(wa).ToString("X8")
                    + "\",\"ra\":\"" + AutShadowCore.Bits(ra).ToString("X8") + "\",\"wb\":\"" + AutShadowCore.Bits(wb).ToString("X8") + "\",\"rb\":\""
                    + AutShadowCore.Bits(rb).ToString("X8") + "\",\"wf\":" + wf + ",\"rf\":" + rf + "}");
            }
            catch (Exception) { }
        }

        // ---- drivers (sim thread for the reference and the dry check, worker for the run) ----

        static void EnvPush(EnvRes r, IGameObject o)
        {
            if (r.SeqN < kEnvSeq) r.Seq[r.SeqN++] = o;
            else r.SeqOver = true;
        }

        // The read part of SimUpdate.CalculateSimEnvironmentScore, in the game's order, through the game's own static
        // helpers (CalculateObjectEnvironmentScore, CalculateContributingObjectScore). Skipped, as they write: Sim
        // reactions (DoSimEnvironmentUpdateInternal), environment events, every buff and motive write after the scan; the
        // Sims and objects they apply to are recorded in r.Seq for the apply (docs/208 §8.2).
        static void EnvScore(Sim s, EnvRes r, bool worker)
        {
            r.Pos = r.Neg = 0f; r.Flags = 0; r.Num = 0; r.SeqN = 0; r.SeqOver = false; r.Puddle = r.Burnt = r.Size = 0; r.Covered = 0f;
            Lot lot = s.LotCurrent, home = s.LotHome;
            r.Lot = lot; r.Room = s.RoomId; r.At = s.Position;
            bool res = lot.IsResidentialLot, light = false, window = false;
            float pos = 0f, neg = 0f;
            int num = 0, flags = res ? 16 : 0;
            float ls = lot.GetEnvironmentScore(s);
            if (ls > 0f) pos += ls;
            if (ls < 0f) neg += ls;
            if (s.RabbitHoleEnvironmentScore == float.MinValue)
            {
                flags |= 32;
                List<float> list = new List<float>();
                if (s.IsOutside)
                {
                    flags |= 1;
                    float range = EnvironmentScore.OutdoorEnvironmentRange;
                    GameObject[] objs = Sims3.Gameplay.Queries.GetObjects<GameObject>(s.Position, range);
                    List<EnvironmentObject> envs = new List<EnvironmentObject>();
                    foreach (Lot l in LotManager.LotsWithinRadialRange(s.PositionXZ, range)) if (res) envs.AddRange(l.GetObjects<EnvironmentObject>());
                    foreach (GameObject o in objs)
                    {
                        Sim so = o as Sim;
                        if (so != null) { if (so != s) EnvPush(r, so); continue; }  // the apply checks rabbit hole / outside live
                        Lot ol = o.LotCurrent;
                        if (ol == null || o is EnvironmentObject || !o.IsOutside) continue;
                        if (ol.IsResidentialLot)
                        {
                            if (!EnvObjOk(o, worker)) return;
                            SimUpdate.CalculateObjectEnvironmentScore(s, o, list, ref pos, ref neg, ref light);
                            if (ol == home && FromCatalog(o)) num++;
                        }
                        EnvPush(r, o);
                    }
                    foreach (EnvironmentObject e in envs)
                    {
                        if (!EnvObjOk(e, worker)) return;
                        float v = e.GetEnvironmentScore(s);
                        if (v == 0f) continue;
                        uint trees = Sims3.SimIFace.World.TreesWithinRadialRange(e.ObjectId, s.Position, range);
                        if (v < 0f) { neg += (float)trees * v; continue; }
                        for (int j = 0; j < trees; j++) list.Add(v);
                    }
                    SimUpdate.CalculateContributingObjectScore(list, ref pos, ref neg, (float)Math.PI * EnvironmentScore.OutdoorEnvironmentRange * EnvironmentScore.OutdoorEnvironmentRange, true);
                }
                else
                {
                    EnvironmentRoomInfo info = Sims3.SimIFace.World.GetInformationAboutRoom(lot.LotId, s.RoomId);
                    r.Puddle = info.roomData.puddleCount; r.Burnt = info.roomData.burntTileCount; r.Size = info.roomData.sizeInTileQuadrants;
                    r.Covered = info.roomData.percentageOfCoveredFloorTiles;
                    neg += (float)info.roomData.puddleCount * EnvironmentScore.PenaltyPerPuddleInRoom;
                    neg += (float)info.roomData.burntTileCount * EnvironmentScore.PenaltyPerBurntTileInRoom;
                    if (info.roomData.sizeInTileQuadrants == 0) { r.Pos = pos; r.Neg = neg; r.Flags = flags | 8; return; }
                    List<GameObject> inRoom = lot.GetObjectsInRoom<GameObject>(s.RoomId);
                    IAromainator[] aro = lot.GetObjects<IAromainator>();
                    int extra = aro.Length - lot.GetObjectsInRoom<IAromainator>(s.RoomId).Count;
                    for (int k = 0; k < extra; k++) inRoom.Add((GameObject)aro[k]);
                    foreach (GameObject o in inRoom)
                    {
                        Sim so = o as Sim;
                        if (so != null) { if (so != s) EnvPush(r, so); continue; }
                        if (!EnvObjOk(o, worker)) return;
                        EnvPush(r, o);
                        if (!window) { Windows w = o as Windows; if (w != null && w.ConnectsToRoom(0)) window = true; }
                        SimUpdate.CalculateObjectEnvironmentScore(s, o, list, ref pos, ref neg, ref light);
                        if (lot == home && FromCatalog(o)) num++;
                    }
                    SimUpdate.CalculateContributingObjectScore(list, ref pos, ref neg, info.roomData.sizeInTileQuadrants / 4, false);
                }
            }
            r.Pos = pos; r.Neg = neg; r.Num = num;
            r.Flags = flags | (window ? 2 : 0) | (light ? 4 : 0);
        }

        static bool EnvObjOk(GameObject o, bool worker)
        {
            bool ok = OffTypeOk(o.GetType(), worker);
            IGameObject p = ok ? o.Parent : null;
            if (p != null) ok = OffTypeOk(p.GetType(), worker);
            if (!ok && worker) AutShadowNative.Abort(kReasonModType);
            return ok || !worker;  // the sim thread scores mod types as the game does (reference = game code)
        }

        static bool FromCatalog(GameObject o)
        {
            ComeAndSeeComponent cs = o.ComeAndSee;
            return cs != null && cs.ComeAndSeeType == ComeAndSeeType.AddedFromCatalog;
        }

        // The filter part of ReactionBroadcaster.GetInRadiusSims on a radius kBcMargin wider (the candidate superset). The
        // range part is answered from the Sim positions (docs/208 §8.3, the docs/122 §2.2 design for Sim range queries):
        // every actor in the world within radius + buffer + kBcMargin + kBcFoot of the broadcaster on the ground plane, in
        // LotManager.Actors order. The native query's own order and bounds test are not reproduced: the candidate set is a
        // superset, the order differs (equal in distribution, I-402-5); the sim thread re-checks distance at the apply.
        static void BcCandidates(BcReq q, BcRes r)
        {
            r.N = 0;
            if (q.Skip != 0) { OffAbort(r); return; }
            IGameObject b = q.Obj;
            Vector3 bp = b.Position;
            r.At = bp;
            float rq = q.R + sBcBuffer + kBcMargin + kBcFoot, rq2 = rq * rq;
            foreach (Sim sim in LotManager.Actors)
            {
                if (sim == null || !sim.InWorld) continue;  // InWorld is the game's first filter; the position answer needs a world object
                Vector3 sp = sim.Position;
                float dx = sp.x - bp.x, dz = sp.z - bp.z;
                if (dx * dx + dz * dz > rq2) continue;
                if (sim.GetType() != typeof(Sim)) { OffAbort(r); return; }  // mod Sim subclasses: their overrides are not checked
                if (sim.RabbitHoleCurrent != null || sim.SimDescription.IsBonehilda || !HasSpecies(q.Species, sim.SimDescription.Species)) continue;
                if (TombRoomManager.IsObjectInATombRoom(sim) && sim.LotCurrent == b.LotCurrent && b.RoomId != sim.RoomId) continue;
                if (q.RoomOnly && !Lot.AreInSameRoom(sim, b)) continue;
                if (sim == b || !sim.CanReact(q.Level) || sim.Posture is SittingInVehicle) continue;
                if (r.N >= kBcSims) { OffAbort(r); return; }
                r.Sims[r.N] = sim;
                r.Near[r.N++] = b.GetDistanceToObject(sim) <= q.R;
            }
        }

        // docs/219 §2: the four shared rows, through the game's own getters (the paths there end in these calls)
        static void T2Read(GameObject o, T2Res r)
        {
            r.Roof = o.IsOnRoof;
            r.Height = AutShadowCore.Bits(o.HeightAboveFloor);
            r.Fwd = o.ForwardVector;
            r.Pool = o is Sim ? 0UL : Sims3.SimIFace.World.GetPoolObjectIdForPoolBelowPosition(o.Position).Value;
        }

        // docs/126 §8: the IC rows through the game's own wrappers, one function per group (each lane edits only its own)
        static bool IcWorld()
        {
            if (sIcWorld == null)
            {
                FieldInfo f = typeof(Sims3.SimIFace.World).GetField("gWorld", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                sIcWorld = f != null ? f.GetValue(null) as IWorld : null;
            }
            return sIcWorld != null;
        }

        static void IcRead(GameObject o, long[] v)
        {
            Array.Clear(v, 0, v.Length);
            IcG1(o, v); IcG2(o, v); IcG3(o, v); IcG4(o, v); IcG5(o, v); IcG6(o, v); IcG7(o, v);
        }

        // G1 terrain and floor, slots 0-5 (rows: TerrainHeight, TerrainType, HasFloorFeatures, HasFloorFeaturesWorldPos,
        // IsCameraRoutable, IsOnPlatform). Flags 4 = RainPuddle (docs/126 §4.1 #6: the only flag WorldPos answers), mask all.
        static void IcG1(GameObject o, long[] v)
        {
            Vector3 p = o.Position;
            v[0] = AutShadowCore.Bits(Sims3.SimIFace.World.GetTerrainHeight(p.x, p.z, true));
            v[1] = (long)Sims3.SimIFace.World.GetTerrainType(p.x, p.z, 0);
            Lot lot = o.LotCurrent;
            if (lot != null)
            {
                LotLocation loc = new LotLocation();
                Sims3.SimIFace.World.GetLotLocation(p, ref loc);
                v[2] = sIcWorld.HasFloorFeatures(lot.LotId, loc, (FloorFeatureFlags)4, 0xFFFF) ? 1 : 2;
            }
            v[3] = sIcWorld.HasFloorFeaturesWorldPos(p, (FloorFeatureFlags)4, 0xFFFF) ? 1 : 2;
            v[4] = Sims3.SimIFace.World.IsCameraRoutable(p.x, p.z) ? 1 : 2;
            v[5] = Sims3.SimIFace.World.IsOnPlatform(p) ? 1 : 2;
        }

        // G2 pools, slots 6-8 (IsInPool, HasPool, GetPondAtPoint): the lot rows need a lot, the pond row asks the world (lot 0) too.
        static void IcG2(GameObject o, long[] v)
        {
            Vector3 p = o.Position;
            Lot lot = o.LotCurrent;
            if (lot != null)
            {
                LotLocation loc = new LotLocation();
                Sims3.SimIFace.World.GetLotLocation(p, ref loc);
                v[7] = Sims3.SimIFace.World.HasPool(lot.LotId, loc) ? 1 : 2;
                v[8] = (long)Sims3.SimIFace.World.GetPondAtPoint(lot.LotId, p) + 1;
            }
            v[6] = Sims3.SimIFace.World.IsInPool(p) ? 1 : 2;
        }

        // G3 lot and room, slots 9-10 (GetRoomId, LotIsEntranceFromTargetPoint); the room id is +1 so 0 stays "not asked"
        static void IcG3(GameObject o, long[] v)
        {
            Lot lot = o.LotCurrent;
            if (lot == null) return;
            LotLocation loc = new LotLocation();
            Sims3.SimIFace.World.GetLotLocation(o.Position, ref loc);
            v[9] = (long)Sims3.SimIFace.World.GetRoomId(lot.LotId, loc, eRoomDefinition.Default) + 0x10001;
            v[10] = Sims3.SimIFace.World.LotIsEntranceFromTargetPoint(lot.LotId) ? 1 : 2;
        }

        // G4 object fields and session, slots 11-14 (GetBoundingBox as a hash of the 6 float bits, IsValid, LightGetDimmer bits,
        // GetWorldFileName length + hash)
        static void IcG4(GameObject o, long[] v)
        {
            BoundingBox b = Sims3.SimIFace.Objects.GetBoundingBox(o.ObjectId);
            long h = 17;
            h = h * 31 + AutShadowCore.Bits(b.min.x); h = h * 31 + AutShadowCore.Bits(b.min.y); h = h * 31 + AutShadowCore.Bits(b.min.z);
            h = h * 31 + AutShadowCore.Bits(b.max.x); h = h * 31 + AutShadowCore.Bits(b.max.y); h = h * 31 + AutShadowCore.Bits(b.max.z);
            v[11] = h;
            v[12] = Sims3.SimIFace.Objects.IsValid(o.ObjectId) ? 1 : 2;
            v[13] = AutShadowCore.Bits(Sims3.SimIFace.World.LightGetDimmer(o.ObjectId));
            string n = Sims3.SimIFace.World.GetWorldFileName();
            long w = 7;
            if (n != null) for (int i = 0; i < n.Length; i++) w = w * 31 + n[i];
            v[14] = w + (n != null ? n.Length : 0) + 1;
        }

        // G5 E rows (docs/data/tier2-enablers-handoff.md), slots 15-19: GetTilesWithFloorFeatures (puddles, hash of the tiles),
        // FindPondRepresentativePositions (hash of the floats, 2 = false), GetLotLocationAtFloor (lot id and the out over a
        // fixed in-value, so a byte the game leaves must come back as that in-value), WorldToHouseboat and HouseboatToWorld
        // (out over a fixed in-value; 2 = false). A household-owned lot is not asked (household rule, docs/208 §9.2).
        static void IcG5(GameObject o, long[] v)
        {
            Lot lot = o.LotCurrent;
            if (lot == null) return;
            if (lot.Household != null && lot.Household == sIcHh) return;
            Vector3 p = o.Position;
            LotLocation[] tiles = sIcWorld.GetTilesWithFloorFeatures(lot.LotId, eRoomDefinition.PlacementBlocking, FloorFeatureFlags.Puddle, 0xFFFF);
            long h = 19 + (tiles != null ? tiles.Length : -1);
            if (tiles != null) foreach (LotLocation t in tiles) h = ((h * 31 + t.mX) * 31 + t.mZ) * 31 + t.mLevel * 65536 + t.mRoom;
            v[15] = h;
            float[] pond = null;
            if (sIcWorld.FindPondRepresentativePositions(lot.LotId, ref pond))
            {
                h = 23 + (pond != null ? pond.Length : -1);
                if (pond != null) for (int i = 0; i < pond.Length; i++) h = h * 31 + AutShadowCore.Bits(pond[i]);
                v[16] = h;
            }
            else v[16] = pond == null ? 2 : 3;
            LotLocation loc = new LotLocation(0x5A5A, 0x6B6B, 0x7C, 0x1D1D);
            ulong id = sIcWorld.GetLotLocationAtFloor(p, ref loc, eRoomDefinition.Default);
            v[17] = (long)id * 31 + (((long)loc.mX * 31 + loc.mZ) * 31 + loc.mLevel) * 65536 + loc.mRoom;
            Vector3 q = new Vector3(-7f, -8f, -9f);
            v[18] = Sims3.SimIFace.World.GetWorldToHouseboatDisplayPosition(lot.LotId, p, out q) ? AutShadowCore.Bits(q.x) * 31 + AutShadowCore.Bits(q.z) : 2;
            q = new Vector3(-7f, -8f, -9f);
            v[19] = Sims3.SimIFace.World.GetHouseboatDisplayToWorldPosition(lot.LotId, p, out q) ? AutShadowCore.Bits(q.x) * 31 + AutShadowCore.Bits(q.z) : 2;
        }

        // G6 E3 rows (docs/data/tier2-enablers-handoff.md), slots 20-21: GetTerrainSurfaceType (+1) and GetFloorSurfaceType
        // (null = 2, else a hash of the string). Same household-lot skip as IcG5.
        static void IcG6(GameObject o, long[] v)
        {
            Lot lot = o.LotCurrent;
            if (lot == null) return;
            if (lot.Household != null && lot.Household == sIcHh) return;
            Vector3 p = o.Position;
            v[20] = (long)Sims3.SimIFace.World.GetTerrainSurfaceType(p) + 1;
            string s = Sims3.SimIFace.World.GetFloorSurfaceType(lot.LotId, p);
            long h = 29;
            if (s != null) for (int i = 0; i < s.Length; i++) h = h * 31 + s[i];
            v[21] = s == null ? 2 : h + s.Length + 3;
        }

        // G6 P6 (docs/data/tier2-rows35-handoff.md), slot 22: Query_CountObjectsInRange as Sofa.GetCouchPotatoMultiplier asks it
        // (TVs within the couch-potato radius of the object's position), + 1 so 0 stays "not asked". Not asked for a household,
        // selected or unknown request-0 actor: AutWork shuts the native row for it (household rule), both sides leave slot 0.
        static void IcG7(GameObject o, long[] v)
        {
            if (sReqN < 1 || sReqs[0].Hh) return;
            v[22] = (long)Sims3.Gameplay.Queries.CountObjects<Sims3.Gameplay.Objects.Electronics.TV>(o.Position,
                Sims3.Gameplay.TuningValues.TraitTuning.CouchPotatoTraitSofaTVDistanceForBonus) + 1;
        }

        // The read part of Camera.GetClosestVisibleLot from the request's inputs, in the game's order: the candidate lots, then
        // the closest one. Not done: the sCurrentFocusedGreetedLot write and LotFocusChanged, which stay on the sim thread.
        // No List.Contains here: its EqualityComparer<T>.Default is a lazy static fill.
        static void LotPick(LotReq q, LotRes r)
        {
            List<Lot> list = new List<Lot>(q.Base);
            foreach (Lot a in LotManager.AllLots)
            {
                if (a.IsCommunityLot)
                {
                    if (!a.IsOpenVenue(q.Hours) && a.CommercialLotSubType != CommercialLotSubType.kEP6_BigShow) { if (LotHasSim(q, a, false)) list.Add(a); }
                    else if (a.BuildableShells.Length == 0 || LotHasSim(q, a, true)) list.Add(a);
                }
                else if (a.HasVirtualResidentialSlots) { if (LotHasSim(q, a, true)) list.Add(a); }
                else if (a.IsOwned) list.Add(a);
            }
            float best = float.MaxValue;
            Lot pick = null, focus = q.World;
            for (int i = 0; i < list.Count; i++)
            {
                Lot l = list[i];
                if (l == null) continue;
                // household-owned houseboat: the worker never asks the houseboat row for it (household rule, docs/208 §9.2)
                if (l.Household != null && l.Household == q.H && l.IsHouseboatLot() && OnWorker()) { AutShadowNative.Abort(kReasonModType); return; }
                float d;
                int cell = LotCellOf(l, q.Target, out d);
                int step = AutOffloadCore.LotPickStep(cell, d, l.IsHouseboatLot(), ref best);
                if (step != 0) pick = l;
                if (step == 2) focus = l;
            }
            r.Pick = pick; r.Focus = focus; r.Best = AutShadowCore.Bits(best);
        }

        static bool LotHasSim(LotReq q, Lot a, bool loose)
        {
            for (int i = 0; i < q.N; i++)
                if (q.SimLot[i] == a && (q.Inside[i] || (loose && (q.Upper[i] || q.Active == a)))) return true;
            return false;
        }

        // the game's quadrant and distance of a camera target to a lot (AutOffloadCore has the game-free part). -1: no corners.
        static int LotCellOf(Lot l, Vector3 t, out float dist)
        {
            dist = 0f;
            Corners c = l.Corners;
            if (c == null) return -1;
            float pad = 0f;
            Vector3 boat;
            if (l.IsHouseboatLot() && Sims3.SimIFace.World.GetHouseboatDisplayToWorldPosition(l.LotId, t, out boat))
            {
                t = boat;
                pad = 10f;
                Houseboat h = l.GetHouseboat();
                if (h != null && h.LotCurrent.IsDivingLot) pad = 0f;
            }
            Matrix44 m = l.WorldToLotTransform;
            Vector3 o = c[Corner.Origin], e = c[Corner.Extent];
            Vector3 lo = m * new Vector3(o.x, 0f, o.z), hi = m * new Vector3(e.x, 0f, e.z), lt = m * new Vector3(t.x, 0f, t.z);
            int cell = AutOffloadCore.LotCell(lt.x, lt.z, lo.x, lo.z, hi.x, hi.z, pad);
            dist = AutOffloadCore.LotCellDist(cell, Math.Abs(lt.x - lo.x), Math.Abs(lt.z - lo.z), Math.Abs(lt.x - hi.x), Math.Abs(lt.z - hi.z),
                LotCornerSq(t, c[Corner.YAxis]), LotCornerSq(t, c[Corner.Extent]), LotCornerSq(t, c[Corner.Origin]), LotCornerSq(t, c[Corner.XAxis]));
            return cell;
        }

        static float LotCornerSq(Vector3 t, Vector3 corner) { return (new Vector2(corner.x, corner.z) - new Vector2(t.x, t.z)).LengthSqr(); }

        // no generic Array.IndexOf on the worker: its EqualityComparer<T>.Default is a lazy static fill
        static bool HasSpecies(CASAgeGenderFlags[] a, CASAgeGenderFlags x)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] == x) return true;
            return false;
        }

        // the worker aborts through ts3native (first reason wins, read by Settle); the sim thread must never call Abort
        static void OffAbort(BcRes r)
        {
            if (OnWorker()) AutShadowNative.Abort(kReasonModType);
            else if (r.Abort < 0) r.Abort = kReasonModType;
        }

        // ---- the game's own call: ready answers (sim thread) ----

        static readonly EnvRes sDryEnv = new EnvRes();

        static EnvRes ReadyEnvFor(Sim s)
        {
            long now = Simulator.TicksElapsed();
            for (int i = 0; i < kEnvReady; i++)
            {
                EnvRes a = sEnvReady[i];
                if (a.S == s && a.Done && !a.Used && now - a.Tick <= kDryTicks) return a;
            }
            return null;
        }

        static BcRes ReadyBcFor(ReactionBroadcaster b, long maxAge)
        {
            long now = Simulator.TicksElapsed();
            for (int j = 0; j < kBcMax; j++)
            {
                BcRes a = sBcReady[j];
                if (a.B == b && a.Done && !a.Used && now - a.Tick <= maxAge) return a;
            }
            return null;
        }

        // docs/208 §8.2: the answer stands for this update only when the Sim is where the cut saw it: same lot and room,
        // same inside/outside and rabbit-hole state, the scan's sequence complete, and outside within kEnvMoveMax (the
        // range query's centre). Object changes on the lot since the cut are not checked (no generation here): autoffdry
        // measures that share (I-404-1).
        static bool EnvFresh(Sim s, EnvRes a)
        {
            if (a.SeqOver || s.LotCurrent != a.Lot || s.LotCurrent == null) return false;
            bool scanned = s.RabbitHoleEnvironmentScore == float.MinValue;
            if (scanned != ((a.Flags & 32) != 0) || s.LotCurrent.IsResidentialLot != ((a.Flags & 16) != 0)) return false;
            if (!scanned) return true;
            bool outside = s.IsOutside;
            if (outside != ((a.Flags & 1) != 0) || s.RoomId != a.Room) return false;
            if (!outside) return true;
            Vector3 p = s.Position;
            float dx = p.x - a.At.x, dy = p.y - a.At.y, dz = p.z - a.At.z;
            return dx * dx + dy * dy + dz * dz <= kEnvMoveMax * kEnvMoveMax;
        }

        static bool EnvSame(EnvRes w, EnvRes d)
        {
            return AutShadowCore.Bits(w.Pos) == AutShadowCore.Bits(d.Pos) && AutShadowCore.Bits(w.Neg) == AutShadowCore.Bits(d.Neg)
                && w.Flags == d.Flags && w.Num == d.Num;
        }

        // docs/208 §8.2: SimUpdate.CalculateSimEnvironmentScore with the scan replaced by the worker's answer `a`. The scan's
        // side effects run here in the scan's order on the sim thread, with the live state: the environment events of each
        // object (SendObjectEnvironmentUpdateEvents) and the reactions to each other Sim (DoSimEnvironmentUpdateInternal,
        // with the game's own rabbit-hole / outside / servo checks); then everything after the scan as the game does it, in
        // its order, from the answer's flags, room info and scores. Destroyed objects and Sims are skipped (the game's live
        // list would not have them).
        static void EnvApply(SimUpdate u, Sim s, EnvRes a)
        {
            DoSimEnvFn doSim = (DoSimEnvFn)Delegate.CreateDelegate(typeof(DoSimEnvFn), u, mDoSimEnv);
            SendObjEnvFn sendObj = (SendObjEnvFn)Delegate.CreateDelegate(typeof(SendObjEnvFn), u, mSendObjEnv);
            float pos = a.Pos, neg = a.Neg;
            bool outside = false, window = false, foundEnemy = false, foundLover = false, reactionPlayed = false, attracted = false, servo = false;
            Lot lot = s.LotCurrent, home = s.LotHome;
            bool res = lot.IsResidentialLot;
            bool darkHours = SimClock.IsTimeBetweenTimes(SimClock.Hours24, TraitTuning.CowardTraitTimeToGetAfraidOfDarkStart, TraitTuning.CowardTraitTimeToGetAfraidOfDarkEnd);
            int num = a.Num;
            bool preggo = s.BuffManager.HasElement(BuffNames.Pregnant), wildHorse = WildHorses.IsWildHorse(s), deer = s.IsDeer;
            BuffManager bm = s.BuffManager;
            if ((a.Flags & 32) != 0)
            {
                bool light = (a.Flags & 4) != 0;
                if ((a.Flags & 1) != 0)
                {
                    outside = true;
                    for (int i = 0; i < a.SeqN; i++)
                    {
                        Sim o = a.Seq[i] as Sim;
                        if (o != null)
                        {
                            if (o.HasBeenDestroyed || o.RabbitHoleEnvironmentScore != float.MinValue) continue;
                            if (o.IsEP11Bot) servo = true;
                            if (o.IsOutside) doSim(o, preggo, wildHorse, deer, ref foundEnemy, ref foundLover, ref reactionPlayed, ref attracted);
                            continue;
                        }
                        GameObject g = a.Seq[i] as GameObject;
                        if (g != null && !g.HasBeenDestroyed) sendObj(g);
                    }
                    if (res && darkHours && s.HasTrait(TraitNames.Coward) && !(s.CurrentInteraction is ICountAsProvideLightInteraction) && !FairyInFairyHouse(s))
                        bm.AddElement(BuffNames.AfraidOfTheDark, Origin.FromDarkness);
                    else bm.RemoveElement(BuffNames.AfraidOfTheDark);
                    bm.RemoveElement(BuffNames.UnfinishedRoom);
                    bm.RemoveElement(BuffNames.ItsDark);
                }
                else
                {
                    if (a.Puddle != 0 || a.Burnt != 0) EventTracker.SendEvent(EventTypeId.kSawPuddleOrBurntSpot, s);
                    if ((a.Flags & 8) != 0) { s.Motives.SetValue(CommodityKind.Dirtiness, 0f); return; }
                    window = (a.Flags & 2) != 0;
                    for (int i = 0; i < a.SeqN; i++)
                    {
                        Sim o = a.Seq[i] as Sim;
                        if (o != null)
                        {
                            if (o.HasBeenDestroyed || o.RabbitHoleEnvironmentScore != float.MinValue) continue;
                            doSim(o, preggo, wildHorse, deer, ref foundEnemy, ref foundLover, ref reactionPlayed, ref attracted);
                            if (o.IsEP11Bot) servo = true;
                            continue;
                        }
                        GameObject g = a.Seq[i] as GameObject;
                        if (g != null && !g.HasBeenDestroyed) sendObj(g);
                    }
                    if (res && !ElevatorInterior.IsSimInsideElevator(s)) EnvInsideBuffs(s, lot, a, light, window, darkHours);
                }
            }
            EnvAfterScan(u, s, lot, home, res, outside, window, darkHours, foundEnemy, foundLover, attracted, servo, num, pos, neg);
        }

        // the inside branch's unfinished-room and darkness buffs (residential lot, not in an elevator), game order
        static void EnvInsideBuffs(Sim s, Lot lot, EnvRes a, bool light, bool window, bool darkHours)
        {
            BuffManager bm = s.BuffManager;
            if (!TombRoomManager.IsObjectInATombRoom(s))
            {
                bool check = true;
                int room = s.RoomId;
                Pool pool = s.Posture is SwimmingInPool ? s.Posture.Container as Pool : null;
                if (pool != null)
                {
                    room = pool.GetAdjoiningRoom(room);
                    if (LotManager.RoomIdIsOutside(room)) check = false;
                }
                if (check)
                {
                    float walls = 1f - Sims3.SimIFace.World.GetPercentageOfWallsCovered(lot.LotId, room);
                    float floor = 1f - a.Covered;
                    if (s.Level >= 1) floor = 0f;
                    if (floor + walls > BuffUnfinishedRoom.PercentMissingForBuff) bm.AddElement(BuffNames.UnfinishedRoom, Origin.FromUncoveredWallsOrFloors);
                    else bm.RemoveElement(BuffNames.UnfinishedRoom);
                }
                else bm.RemoveElement(BuffNames.UnfinishedRoom);
            }
            if (!s.IsSleeping && !FairyInFairyHouse(s) && ((darkHours && !light) || (!darkHours && !light && !window && !LotManager.LightingRoomIdIsOutside(s.Position))))
            {
                if (s.TraitManager.HasElement(TraitNames.Coward)) bm.AddElement(BuffNames.AfraidOfTheDark, Origin.FromDarkness);
                else if (!s.TraitManager.HasElement(TraitNames.Evil))
                {
                    InteractionInstance cur = s.CurrentInteraction;
                    if (cur is Sim.TellGhostStory || cur is Sim.ListenToGhostStory)
                    {
                        bm.RemoveElement(BuffNames.ItsDark);
                        bm.AddElement(BuffNames.ItsDarkNeutral, Origin.FromDarkness);
                    }
                    else
                    {
                        bm.AddElement(BuffNames.ItsDark, Origin.FromDarkness);
                        bm.RemoveElement(BuffNames.ItsDarkNeutral);
                    }
                }
            }
            else
            {
                bm.RemoveElement(BuffNames.AfraidOfTheDark);
                bm.RemoveElement(BuffNames.ItsDark);
                bm.RemoveElement(BuffNames.ItsDarkNeutral);
            }
        }

        static bool FairyInFairyHouse(Sim s)
        {
            return GameUtils.IsInstalled(ProductVersion.EP7) && s.SimDescription.IsFairy && s.Posture != null && s.Posture is IFairyHousePosture;
        }

        static void AddOrRemove(BuffManager bm, bool add, BuffNames buff, Origin origin)
        {
            if (add) bm.AddElement(buff, origin);
            else bm.RemoveElement(buff);
        }

        // everything after the scan, in the game's order (venue, trait, occult, season and workaholic buffs, the new-object
        // count, then the Decoration / Dirtiness writes)
        static void EnvAfterScan(SimUpdate u, Sim s, Lot lot, Lot home, bool res, bool outside, bool window, bool darkHours, bool foundEnemy,
            bool foundLover, bool attracted, bool servo, int num, float pos, float neg)
        {
            BuffManager bm = s.BuffManager;
            HorseManager hm = s.IsHorse ? s.SimDescription.PetManager as HorseManager : null;
            if (hm != null) hm.DoFoalMotherRelationshipEnvironmentEffects(s);
            if (!foundEnemy) bm.RemoveElement(BuffNames.Enemy);
            if (!foundLover) bm.RemoveElement(BuffNames.MyLove);
            if (!attracted) bm.RemoveElement(BuffNames.AttractiveCompany);
            if (s.TraitManager.HasElement(TraitNames.AntiTV) && !bm.HasElement(BuffNames.AntiTV) && servo) bm.AddElement(BuffNames.AntiTV, Origin.FromTechnophobeNearServobot);
            MetaAutonomyVenueType venue = lot.GetMetaAutonomyVenueType();
            switch (venue)
            {
                case MetaAutonomyVenueType.Restaurant: case MetaAutonomyVenueType.SmallPark: case MetaAutonomyVenueType.BigPark:
                case MetaAutonomyVenueType.SmallShop: case MetaAutonomyVenueType.BigShop: case MetaAutonomyVenueType.Pool:
                case MetaAutonomyVenueType.Garden: case MetaAutonomyVenueType.DogPark: case MetaAutonomyVenueType.CatJungle:
                case MetaAutonomyVenueType.SpringFestival: case MetaAutonomyVenueType.SummerFestival: case MetaAutonomyVenueType.FallFestival:
                case MetaAutonomyVenueType.WinterFestival: case MetaAutonomyVenueType.ConstructionFestival: case MetaAutonomyVenueType.RecreationPark:
                    AddOrRemove(bm, !s.HasTrait(TraitNames.HatesOutdoors), BuffNames.BeautifulView, Origin.FromBeautifulLot);
                    break;
                default:
                    bm.RemoveElement(BuffNames.BeautifulView);
                    break;
            }
            if (outside || window) { if (!bm.HasElement(BuffNames.BeautifulVista)) s.CheckForBeautifulVista(window, outside); }
            else bm.RemoveElement(BuffNames.BeautifulVista);
            AddOrRemove(bm, darkHours && venue == MetaAutonomyVenueType.Graveyard && !s.HasTrait(TraitNames.Brave) && !s.HasTrait(TraitNames.Evil),
                BuffNames.CreepyGraveyard, Origin.FromGraveyard);
            if (venue != MetaAutonomyVenueType.ArtGallery) s.ArtViewed = 0;
            AddOrRemove(bm, venue == MetaAutonomyVenueType.Gym, BuffNames.FitAtmosphere, Origin.FromGym);
            AddOrRemove(bm, venue == MetaAutonomyVenueType.ChineseGarden, BuffNames.Zen, Origin.FromChineseGarden);
            AddOrRemove(bm, venue == MetaAutonomyVenueType.Library, BuffNames.PowerStudy, Origin.FromLibrary);
            AddOrRemove(bm, outside && s.TraitManager.HasElement(TraitNames.HatesOutdoors) && !(s.Posture is SittingInVehicle), BuffNames.PlaguedByNature, Origin.FromBeingOutside);
            AddOrRemove(bm, outside && s.TraitManager.HasElement(TraitNames.LovesTheOutdoors) && !(s.Posture is SittingInVehicle), BuffNames.OneWithNature, Origin.FromBeingOutside);
            bool sunSafe = OccultVampire.IsProtectedFromTheSun(s), night = SimClock.IsNightTime();
            if (outside && s.SimDescription.IsVampire && !night && !sunSafe)
            {
                if (s.HasTrait(TraitNames.SuperVampire)) bm.AddElement(BuffNames.PermaSparkly, Origin.FromTheSun);
                else bm.AddElement(BuffNames.HeatingUp, Origin.FromTheSun);
                EventTracker.SendEvent(new TimePassedEvent(EventTypeId.kVampireInSun, s, null, (float)fEnvTime.GetValue(u)));
            }
            else
            {
                if (s.HasTrait(TraitNames.SuperVampire)) bm.RemoveElement(BuffNames.PermaSparkly);
                else bm.RemoveElement(BuffNames.HeatingUp);
                if (sunSafe) EventTracker.SendEvent(EventTypeId.kChangedInsideOutsideStatus, s);
            }
            if (s.SimDescription.IsEP11Bot)
                AddOrRemove(bm, outside && SimClock.IsTimeBetweenTimes(SimClock.Hours24, 6.5f, 18.5f) && s.SimDescription.HasTrait(TraitNames.SolarPoweredChip),
                    BuffNames.AbsorbingSunlight, Origin.FromTheSun);
            if (GameUtils.IsInstalled(ProductVersion.EP9) && GameUtils.IsInstalled(ProductVersion.EP8))
            {
                Sims3.SimIFace.Enums.PrecipitationIntensity pi;
                bool raining = SeasonsManager.Enabled && SeasonsManager.IsRaining(out pi);
                if (outside && s.SimDescription.IsPlantSim && raining) s.Motives.ChangeValue(CommodityKind.Hygiene, OccultPlantSim.kWaterMotiveIncrement);
            }
            AddOrRemove(bm, outside && OccultUnicorn.IsNPCPoolUnicorn(s), BuffNames.MoonlitUnicorn, Origin.None);
            if (home == lot)
            {
                if (!s.TraitManager.HasElement(TraitNames.HatesOutdoors)) bm.AddElementPaused(BuffNames.StirCrazy, Origin.FromBeingInside, BuffStirCrazy.HoursToRemoveBuff * 60f);
            }
            else if (bm.HasElement(BuffNames.StirCrazy)) bm.UnpauseBuff(BuffNames.StirCrazy);
            else bm.RemoveElement(BuffNames.StirCrazy);
            if (bm.HasElement(BuffNames.NewStuff))
            {
                BuffNewStuff.BuffInstanceNewStuff ns = bm.GetElement(BuffNames.NewStuff) as BuffNewStuff.BuffInstanceNewStuff;
                if (ns != null)
                {
                    if (num > ns.NewObjectCount)
                    {
                        ns.NewObjectCount = num;
                        if (ns.NewObjectCount >= BuffNewStuff.NewObjectsForBuff) bm.AddElement(BuffNames.NewStuff, Origin.FromNewObjects);
                    }
                    else ns.NewObjectCount = num;
                }
            }
            else if (num >= BuffNewStuff.NewObjectsForBuff)
            {
                bm.AddElement(BuffNames.NewStuff, Origin.FromNewObjects);
                BuffNewStuff.BuffInstanceNewStuff ns = bm.GetElement(BuffNames.NewStuff) as BuffNewStuff.BuffInstanceNewStuff;
                if (ns != null) ns.NewObjectCount = num;
            }
            BuffCryingBaby.BuffInstanceCryingBaby cb = bm.GetElement(BuffNames.CryingBaby) as BuffCryingBaby.BuffInstanceCryingBaby;
            if (cb != null)
            {
                Sim baby = cb.Baby;
                if (baby == null || !baby.InWorld || baby.HasBeenDestroyed || baby.SimDescription.ChildOrAbove || baby.IsSleeping || s.RabbitHoleCurrent != null
                    || ((!baby.IdleManager.IsCryingOnLot || baby.LotCurrent != lot) && (!baby.IdleManager.IsCryingOnRoom || baby.LotCurrent != lot || baby.RoomId != s.RoomId)))
                    bm.RemoveElement(BuffNames.CryingBaby);
            }
            if (s.HasTrait(TraitNames.Workaholic))
            {
                Occupation job = s.Occupation;
                if (!(job is AcademicCareer))
                {
                    if (job != null && job.IsAtWork) { bm.AddElement(BuffNames.LikesWork, Origin.FromWork); bm.RemoveElement(BuffNames.MissingWork); }
                    else if (job != null && job.ShouldBeAtWork() && job.MissingWork) { bm.AddElement(BuffNames.MissingWork, Origin.FromMissingWork); bm.RemoveElement(BuffNames.LikesWork); }
                    else { bm.RemoveElement(BuffNames.MissingWork); bm.RemoveElement(BuffNames.LikesWork); }
                }
            }
            if (s.SimDescription.IsWitch)
            {
                bool cat = false;
                if (s.Household != null) foreach (Sim h in s.Household.AllActors) if (h.IsCat && h.LotCurrent == s.LotCurrent) cat = true;
                List<MinorPet> pets = s.Inventory.FindAll<MinorPet>(false);
                AddOrRemove(bm, cat || (pets != null && pets.Count > 0), BuffNames.AnimalFamiliar, Origin.FromFamiliar);
            }
            if (s.Occupation != null && s.Occupation.IsAtWork && bm.HasElement(BuffNames.EnjoyingMusic)) s.Inventory.TurnOfMP3();
            if (SeasonsManager.Enabled)
            {
                bool sheltered = sunSafe || SeasonsManager.IsShelteredFromPrecipitation(s);
                SimTemperature.AddSimTemperatureBuffsIfNecessary(s, outside, sheltered, night);
                OccultMermaid.HandleDryingUpBuffIfNecessary(s);
            }
            if (res)
            {
                s.Motives.SetValue(CommodityKind.Decoration, pos);
                s.Motives.SetValue(CommodityKind.Dirtiness, neg);
                if (neg < 0f) EventTracker.SendEvent(EventTypeId.kHasNegativeEnvironmentScore, s);
            }
            else
            {
                s.Motives.SetValue(CommodityKind.Decoration, 0f);
                s.Motives.SetValue(CommodityKind.Dirtiness, 0f);
            }
        }

        // ---- hooks ----

        public class AutEnvHook
        {
            public void EnvHook()
            {
                if (!sOffHooksOk || !ShortcutOn("autoffenv")) { EnvOrig(); return; }
                SimUpdate u = (SimUpdate)(object)this;
                Sim s = null;
                EnvRes a = null;
                bool apply = false, dry = false;
                int[] c = PatchCounters();
                try
                {
                    s = fUpdSim.GetValue(u) as Sim;
                    if (s != null)
                    {
                        long now = Simulator.TicksElapsed();
                        EnvDue d;
                        if (!sEnvDue.TryGetValue(s, out d)) sEnvDue[s] = d = new EnvDue();
                        else d.Period = now - d.Last;
                        d.Last = now;
                        a = ReadyEnvFor(s);
                    }
                    if (a != null)
                    {
                        a.Used = true;  // one use per answer
                        dry = ShortcutOn("autoffdry");
                        if (dry)
                        {
                            EnvScore(s, sDryEnv, false);  // staleness: the cut answer against the live driver now
                            c[cEnv + 6]++;
                            if (EnvSame(a, sDryEnv)) c[cEnv + 7]++;
                        }
                        if (ShortcutOn("autoffenvapply"))
                        {
                            apply = EnvFresh(s, a);
                            c[apply ? cEnvAp + 1 : cEnvAp + 3]++;
                        }
                    }
                    else if (s != null && ShortcutOn("autoffenvapply")) c[cEnvAp + 2]++;
                }
                catch (Exception) { c[cEnv + 5]++; a = null; apply = false; dry = false; }
                if (apply)
                {
                    EnvApply(u, s, a);  // exceptions propagate as the game's own would
                    if (dry && EnvSame(a, sDryEnv)) c[cEnvAp + 4]++;
                }
                else EnvOrig();
                if (!dry || apply) return;
                try
                {
                    // apply equivalence: the game's own Decoration / Dirtiness writes against the live driver (residential lots;
                    // the empty-room early return writes Dirtiness 0 only)
                    Motives m = s.Motives;
                    if ((sDryEnv.Flags & 16) == 0 || !m.HasMotive(CommodityKind.Decoration) || !m.HasMotive(CommodityKind.Dirtiness)) return;
                    bool eq = (sDryEnv.Flags & 8) != 0 ? m.GetValue(CommodityKind.Dirtiness) == 0f
                        : AutShadowCore.Bits(m.GetValue(CommodityKind.Decoration)) == AutShadowCore.Bits(sDryEnv.Pos)
                          && AutShadowCore.Bits(m.GetValue(CommodityKind.Dirtiness)) == AutShadowCore.Bits(sDryEnv.Neg);
                    if (eq) c[cEnv + 8]++;
                }
                catch (Exception) { c[cEnv + 5]++; }
            }
            public void EnvOrig() { } // receives SimUpdate.CalculateSimEnvironmentScore's original header
        }

        public class AutBcastHook
        {
            public Dictionary<ObjectGuid, Sim> RadiusHook(float pulseRadius)
            {
                if (!sOffHooksOk || !ShortcutOn("autoffbcast")) return RadiusOrig(pulseRadius);
                ReactionBroadcaster b = (ReactionBroadcaster)(object)this;
                sBcRing[sOffBcRing] = b; sOffBcRing = (sOffBcRing + 1) % kOffRing;
                bool apply = ShortcutOn("autoffbcastapply"), dry = ShortcutOn("autoffdry");
                if (!apply && !dry) return RadiusOrig(pulseRadius);
                int[] c = PatchCounters();
                Dictionary<ObjectGuid, Sim> mine = null;
                BcRes w = null;
                try
                {
                    w = ReadyBcFor(b, apply ? kBcApplyTicks : kDryTicks);
                    if (w != null)
                    {
                        w.Used = true;
                        if (apply && !BcFresh(b, w)) { c[cBcAp + 3]++; apply = false; }
                        if (apply || dry) mine = BcRebuild(b, w, pulseRadius);  // a stale answer is rebuilt only to be measured
                    }
                    else if (apply) { c[cBcAp + 2]++; apply = false; }
                }
                catch (Exception) { c[cBc + 6]++; mine = null; apply = false; }
                if (apply && mine != null)
                {
                    c[cBcAp + 1]++;
                    if (!dry) return mine;
                }
                Dictionary<ObjectGuid, Sim> d = RadiusOrig(pulseRadius);  // apply + dry: the game's set as the measure, the apply's returned
                if (dry && mine != null && d != null)
                {
                    c[cBc + 7]++;
                    bool set = mine.Count == d.Count, order = set;
                    if (set) foreach (ObjectGuid g in d.Keys) set &= mine.ContainsKey(g);
                    if (set)
                    {
                        Dictionary<ObjectGuid, Sim>.KeyCollection.Enumerator e = mine.Keys.GetEnumerator();
                        foreach (ObjectGuid g in d.Keys) { e.MoveNext(); order &= e.Current == g; }
                    }
                    if (set) c[cBc + 8]++;          // same Sims (docs/208 §8.3: the order is the actor list's)
                    if (set && order) c[cBcAp + 4]++;
                }
                return apply && mine != null ? mine : d;
            }
            public Dictionary<ObjectGuid, Sim> RadiusOrig(float pulseRadius) { return null; } // receives ReactionBroadcaster.GetInRadiusSims's original header
        }

        // docs/208 §9.2 dry check: the worker's pick against the game's next own pick. No apply: the game's result is returned.
        public class AutLotHook
        {
            public Lot LotHook(Lot homeLot, ref Vector3 cameraTargetAsVec3)
            {
                Lot g = LotOrig(homeLot, ref cameraTargetAsVec3);
                if (!sLotReady.Done || sLotReady.Used || !ShortcutOn("autofflot") || !ShortcutOn("autoffdry")) return g;
                int[] c = PatchCounters();
                try
                {
                    sLotReady.Used = true;
                    if (Simulator.TicksElapsed() - sLotReady.Tick > kLotDryTicks) return g;
                    c[cLot + 6]++;
                    if (sLotReady.Pick == g) c[cLot + 7]++;
                }
                catch (Exception) { c[cLot + 5]++; }
                return g;
            }
            public Lot LotOrig(Lot homeLot, ref Vector3 cameraTargetAsVec3) { return null; } // receives Camera.GetClosestVisibleLot's original header
        }

        // the broadcaster has not moved off its cut position (a broadcaster on a walking Sim moves with it)
        static bool BcFresh(ReactionBroadcaster b, BcRes w)
        {
            IGameObject o = b.BroadcastingObject;
            if (o == null) return false;
            Vector3 p = o.Position;
            float dx = p.x - w.At.x, dy = p.y - w.At.y, dz = p.z - w.At.z;
            return dx * dx + dy * dy + dz * dz <= kBcObjMoveMax * kBcObjMoveMax;
        }

        // The apply step (docs/208 §3.3, §8.3): the worker's candidates in its order; per candidate, live on the sim thread:
        // line of sight and could-see when the params ask, then in the reacting set or within the pulse radius. A reacting
        // Sim counts while within the game's query radius (radius + buffer) plus kBcFoot (the query tests its bounds).
        static Dictionary<ObjectGuid, Sim> BcRebuild(ReactionBroadcaster b, BcRes w, float pulseRadius)
        {
            ReactionBroadcasterParams p = b.BroadcasterParams;
            IGameObject o = b.BroadcastingObject;
            Dictionary<Sim, Sim> reacting = fBcReacting.GetValue(b) as Dictionary<Sim, Sim>;
            Dictionary<ObjectGuid, Sim> d = new Dictionary<ObjectGuid, Sim>();
            if (p == null || o == null) return d;
            float rq = pulseRadius + sBcBuffer + kBcFoot;
            for (int i = 0; i < w.N; i++)
            {
                Sim sim = w.Sims[i];
                if (sim == null || sim.HasBeenDestroyed || !sim.InWorld || d.ContainsKey(sim.ObjectId)) continue;
                if (p.ReactorLineOfSight && !sim.CanSeeObject(o)) continue;
                if (p.ReactorCouldSeeCheck && !sim.CouldSeeObject(o, sim.Position)) continue;
                float dist = o.GetDistanceToObject(sim);
                if ((reacting != null && reacting.ContainsKey(sim) && dist <= rq) || dist <= pulseRadius) d.Add(sim.ObjectId, sim);
            }
            return d;
        }
    }
}
