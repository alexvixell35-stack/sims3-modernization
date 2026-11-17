using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.TuningValues;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/274 §5.1-§5.2: two dry probes on the sim thread. Both only count; the game's own call runs and its result is used.
    //   envlistmemo (R1): at each SimUpdate.CalculateSimEnvironmentScore (EnvHook), before the game's scan, the same list
    //     calls the scan makes (indoor: GetObjectsInRoom<GameObject>(room), GetObjects<IAromainator>(),
    //     GetObjectsInRoom<IAromainator>(room); outdoor on a residential lot: GetObjects<EnvironmentObject>() per lot in range),
    //     each checked against a memo keyed (lot, room, list) that a per-lot / per-room generation keeps valid (ListMemo).
    //     Invalidation (docs/274 §5.1 verdict): managed World.OnObjectLotUpdate (the only path that changes a managed RoomId or
    //     a lot's membership, LotManager.UpdateObjectLot [verified, decompiled]; our handler goes first so the object still has
    //     its old lot and room), OnObjectPendingDestruction, OnObjectPlacedInLot, LotManager.PlaceObjectOnLot (script object
    //     start-up, no event: hooked), every bump once more at the next tick, and a full clear on lot add / remove / bounds /
    //     type, world load / quit and build/buy exit (LotManager.ExitBuildBuyMode, hooked: walls move portals and rooms).
    //   bcposdry (R2): at each ReactionBroadcaster.GetInRadiusSims (RadiusHook) of an unseen broadcaster with no household Sim
    //     on its lot or in range (BcScopeOf, as the autoffunseen pick), the candidate set from one Sim position table per
    //     tick (LotManager.Actors, InWorld) with the game's own filters (BcKeep), line of sight and reacting rule (BcRebuild),
    //     compared with the game's set. Pre-empted by autoffbcast (both are test switches; not run together).
    // Counters 1091-1114 (docs/registry.md); test switches, default off, no tuning.
    public static partial class Fixes
    {
        const int cEl = 1091, cBp = 1103, kBpTable = 512, kElWrongLog = 20;
        static readonly ListMemo sEl = new ListMemo();
        static bool sElHooksOk, sElSubscribed;
        static EventHandler sElLotUpd, sElGone, sElPlaced, sElReset;
        static StopWatch sElWatch;
        static long sElUsAll, sElUsHit;
        static int sElWrongLogged;
        // docs/274 §8.5: objects past OnObjectPendingDestruction that still exist (native destroy is deferred [inferred]); their
        // lots are bumped at every probe call until Objects.IsValid turns false, then once more at the next tick (Bump)
        struct ElDying { public ObjectGuid Id; public Lot Lot; }
        const int kElDyingMax = 4096;
        static readonly List<ElDying> sElDying = new List<ElDying>();

        static void EnvListProbe(Sim s)
        {
            int[] c = PatchCounters();
            try
            {
                if (!sElSubscribed && !ElSubscribe()) return;
                c[cEl + 7]++;
                long tick = Simulator.TicksElapsed();
                for (int i = sElDying.Count - 1; i >= 0; i--)
                {
                    sEl.Bump(sElDying[i].Lot, -1, tick);
                    if (!Sims3.SimIFace.Objects.IsValid(sElDying[i].Id)) sElDying.RemoveAt(i);
                }
                if (s.RabbitHoleEnvironmentScore != float.MinValue) return;  // the game scans nothing
                Lot lot = s.LotCurrent;
                if (lot == null) return;
                if (sElWatch == null) sElWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
                if (s.IsOutside)
                {
                    if (!lot.IsResidentialLot) return;  // the game lists environment objects only from a residential lot
                    foreach (Lot l in LotManager.LotsWithinRadialRange(s.PositionXZ, EnvironmentScore.OutdoorEnvironmentRange))
                    {
                        sElWatch.Restart();
                        EnvironmentObject[] e = l.GetObjects<EnvironmentObject>();
                        ElCount(c, l, -1, 3, e, tick);
                    }
                    return;
                }
                int room = s.RoomId;
                if (Sims3.SimIFace.World.GetInformationAboutRoom(lot.LotId, room).roomData.sizeInTileQuadrants == 0) return;  // the game's early return
                sElWatch.Restart();
                List<GameObject> g = lot.GetObjectsInRoom<GameObject>(room);
                ElCount(c, lot, room, 0, g, tick);
                sElWatch.Restart();
                IAromainator[] a = lot.GetObjects<IAromainator>();
                ElCount(c, lot, -1, 1, a, tick);
                sElWatch.Restart();
                List<IAromainator> ar = lot.GetObjectsInRoom<IAromainator>(room);
                ElCount(c, lot, room, 2, ar, tick);
            }
            catch (Exception) { c[cEl + 6]++; }
        }

        static void ElCount(int[] c, Lot lot, int room, int kind, IList now, long tick)
        {
            long us = sElWatch.GetElapsedTime();
            int cls = sEl.Check(lot, room, kind, now, tick);
            c[cEl]++;
            c[cEl + 1 + cls]++;  // 1 first, 2 hit, 3 hit_wrong, 4 miss_same, 5 miss_diff (ListMemo.k*)
            sElUsAll += us;
            if (cls == ListMemo.kHit) sElUsHit += us;
            c[cEl + 10] = (int)(sElUsAll / 1000);
            c[cEl + 11] = (int)(sElUsHit / 1000);
            if (cls == ListMemo.kHitWrong && sElWrongLogged < kElWrongLog)
            {
                sElWrongLogged++;
                AppliedLog().Add("EnvListMemo: wrong hit lot " + lot.LotId + " room " + room + " list " + kind + " n " + now.Count + ElDiff(sEl.WrongOld, now));
            }
        }

        // what the missed change was: up to 3 objects gone from / new in the list, or a pure reorder
        static string ElDiff(object[] old, IList now)
        {
            if (old == null) return "";
            string r = " was " + old.Length;
            int k = 0;
            foreach (object o in old) if (k < 3 && !now.Contains(o)) { r += " gone " + ElDesc(o); k++; }
            int g = k; k = 0;
            foreach (object o in now) if (k < 3 && Array.IndexOf(old, o) < 0) { r += " new " + ElDesc(o); k++; }
            if (g + k != 0) return r;
            // docs/274 §8.6 diag: the first moved index, whether the Sims' relative order changed (the scan's reaction gates
            // are first-match over Sims, SimUpdate.DoSimEnvironmentUpdateInternal), and the lot's events since the list was last equal
            int i = 0;
            while (i < old.Length && i < now.Count && ReferenceEquals(old[i], now[i])) i++;
            r += " reorder at " + i + (i < old.Length ? " old " + ElDesc(old[i]) : "") + (i < now.Count ? " now " + ElDesc(now[i]) : "");
            int so = 0, sn = 0; bool simMoved = false;
            for (; ; so++, sn++)
            {
                while (so < old.Length && !(old[so] is Sim)) so++;
                while (sn < now.Count && !(now[sn] is Sim)) sn++;
                if (so >= old.Length || sn >= now.Count) break;
                if (!ReferenceEquals(old[so], now[sn])) { simMoved = true; break; }
            }
            return r + (simMoved ? " simorder moved" : " simorder same") + " ev " + sEl.WrongEv + " age " + sEl.WrongAge;
        }

        static string ElDesc(object o)
        {
            GameObject go = o as GameObject;
            if (go == null) return o == null ? "null" : o.GetType().Name;
            bool dying = false;
            foreach (ElDying d in sElDying) if (d.Id == go.ObjectId) dying = true;
            return go.GetType().Name + "(room " + go.RoomId + (go is Sim ? " sim" : "") + (go.HasBeenDestroyed ? " destroyed" : "") + (dying ? " dying" : "")
                + (Sims3.SimIFace.Objects.IsValid(go.ObjectId) ? "" : " invalid") + (go.Parent != null ? " child" : "")
                + (go.InWorld ? "" : " notinworld") + ")";
        }

        // once per world (again after a quit): our lot-update handler in front of LotManager's, the rest appended
        static bool ElSubscribe()
        {
            if (!sElHooksOk) return false;
            FieldInfo f = typeof(Sims3.SimIFace.World).GetField("sOnObjectLotUpdateEventHandler", BindingFlags.Static | BindingFlags.NonPublic);
            if (f == null) { sElHooksOk = false; AppliedLog().Add("EnvListMemo: off: no World.sOnObjectLotUpdateEventHandler"); return false; }
            if (sElLotUpd == null)
            {
                sElLotUpd = ElLotUpdate; sElGone = ElGone; sElPlaced = ElPlaced; sElReset = ElReset;
            }
            EventHandler cur = f.GetValue(null) as EventHandler;
            if (cur != null) cur = (EventHandler)Delegate.Remove(cur, sElLotUpd);
            f.SetValue(null, Delegate.Combine(sElLotUpd, cur));
            Sims3.SimIFace.World.OnObjectPendingDestructionEventHandler -= sElGone;
            Sims3.SimIFace.World.OnObjectPendingDestructionEventHandler += sElGone;
            Sims3.SimIFace.World.OnObjectPlacedInLotEventHandler -= sElPlaced;
            Sims3.SimIFace.World.OnObjectPlacedInLotEventHandler += sElPlaced;
            Sims3.SimIFace.World.OnLotAddedEventHandler -= sElReset;
            Sims3.SimIFace.World.OnLotAddedEventHandler += sElReset;
            Sims3.SimIFace.World.OnLotRemovedEventHandler -= sElReset;
            Sims3.SimIFace.World.OnLotRemovedEventHandler += sElReset;
            Sims3.SimIFace.World.OnLotBoundsChangedEventHandler -= sElReset;
            Sims3.SimIFace.World.OnLotBoundsChangedEventHandler += sElReset;
            Sims3.SimIFace.World.OnLotTypeChangedEventHandler -= sElReset;
            Sims3.SimIFace.World.OnLotTypeChangedEventHandler += sElReset;
            Sims3.SimIFace.World.OnWorldLoadFinishedEventHandler -= sElReset;
            Sims3.SimIFace.World.OnWorldLoadFinishedEventHandler += sElReset;
            Sims3.SimIFace.World.OnWorldQuitEventHandler -= sElReset;
            Sims3.SimIFace.World.OnWorldQuitEventHandler += sElReset;
            sEl.Clear();
            sElSubscribed = true;
            return true;
        }

        static Lot ElLotOf(ulong id)
        {
            Lot l = LotManager.GetLot(id == 0 ? ulong.MaxValue : id);  // as LotManager.UpdateObjectLot: no lot = the world lot
            return l != null ? l : LotManager.GetWorldLot();
        }

        static void ElLotUpdate(object sender, EventArgs e)
        {
            Sims3.SimIFace.World.OnObjectLotUpdateEventArgs a = e as Sims3.SimIFace.World.OnObjectLotUpdateEventArgs;
            if (a == null) return;
            try
            {
                long tick = Simulator.TicksElapsed();
                PatchCounters()[cEl + 8]++;
                IGameObject o = a.ObjectId.ObjectFromId<IGameObject>();
                Lot nl = ElLotOf(a.LotId), ol = o != null ? o.InternalLotCurrent : null;
                if (o == null || ol != nl) { sEl.Bump(nl, -1, tick); sEl.Bump(ol, -1, tick); return; }
                if (o is IPortalConnectionObject || o is RoomConnectionObject) { sEl.Bump(nl, -1, tick); return; }  // in every room it connects
                int room = a.LotId == 0 ? 0 : a.RoomId;
                if (o.StaleRoomId != room) { sEl.Bump(nl, o.StaleRoomId, tick); sEl.Bump(nl, room, tick); }
            }
            catch (Exception) { PatchCounters()[cEl + 6]++; sEl.Clear(); }
        }

        static void ElGone(object sender, EventArgs e)
        {
            Sims3.SimIFace.World.OnObjectPendingDestructionEventArgs a = e as Sims3.SimIFace.World.OnObjectPendingDestructionEventArgs;
            if (a == null) return;
            try
            {
                long tick = Simulator.TicksElapsed();
                PatchCounters()[cEl + 8]++;
                Lot el = ElLotOf(a.LotId);
                sEl.Bump(el, -1, tick);
                ElDie(a.ObjectId, el);
                IGameObject o = a.ObjectId.ObjectFromId<IGameObject>();
                if (o != null && o.InternalLotCurrent != el) { sEl.Bump(o.InternalLotCurrent, -1, tick); ElDie(a.ObjectId, o.InternalLotCurrent); }
            }
            catch (Exception) { PatchCounters()[cEl + 6]++; sEl.Clear(); }
        }

        static void ElDie(ObjectGuid id, Lot lot)
        {
            if (lot == null) return;
            if (sElDying.Count >= kElDyingMax) { PatchCounters()[cEl + 6]++; sEl.Clear(); return; }  // shortcut: overflow loses this object's later bumps; never seen (destroys finish within ticks)
            ElDying d; d.Id = id; d.Lot = lot;
            sElDying.Add(d);
        }

        static void ElPlaced(object sender, EventArgs e)
        {
            Sims3.SimIFace.World.OnObjectPlacedInLotEventArgs a = e as Sims3.SimIFace.World.OnObjectPlacedInLotEventArgs;
            if (a == null) return;
            try { PatchCounters()[cEl + 8]++; sEl.Bump(ElLotOf(a.LotId), -1, Simulator.TicksElapsed()); }
            catch (Exception) { PatchCounters()[cEl + 6]++; sEl.Clear(); }
        }

        static void ElReset(object sender, EventArgs e)
        {
            sEl.Clear();
            sElDying.Clear();
            PatchCounters()[cEl + 9]++;
            if (e is Sims3.SimIFace.World.OnWorldQuitEventArgs) sElSubscribed = false;  // next world: in front of LotManager again
        }

        public class ElHooks
        {
            public static void PlaceHook(IGameObject gameObject, ObjectGuid objectId)
            {
                PlaceOrig(gameObject, objectId);
                if (!sElSubscribed || gameObject == null) return;
                try { PatchCounters()[cEl + 8]++; sEl.Bump(gameObject.InternalLotCurrent, -1, Simulator.TicksElapsed()); }
                catch (Exception) { PatchCounters()[cEl + 6]++; sEl.Clear(); }
            }
            public static void PlaceOrig(IGameObject gameObject, ObjectGuid objectId) { } // receives LotManager.PlaceObjectOnLot's original header
            public static void ExitHook()
            {
                ExitOrig();
                if (!sElSubscribed) return;
                sEl.Clear();
                PatchCounters()[cEl + 9]++;
            }
            public static void ExitOrig() { } // receives LotManager.ExitBuildBuyMode's original header
        }

        // ---- bcposdry ----

        static readonly Sim[] sBpSims = new Sim[kBpTable];
        static readonly float[] sBpX = new float[kBpTable], sBpZ = new float[kBpTable];
        static readonly int[] sBpAt = new int[kBpTable];
        static readonly BcReq sBpReq = new BcReq();
        static readonly BcRes sBpRes = new BcRes();
        static readonly object[] sBpMine = new object[kBcSims], sBpGame = new object[kBpTable];
        static int sBpN;
        static long sBpTick = -1, sBpTableUs, sBpScanUs, sBpGameUs;
        static StopWatch sBpWatch;

        // the table answer, or null when the pulse is out of scope (household or seen) or the answer cannot be built
        static Dictionary<ObjectGuid, Sim> BcPosAnswer(ReactionBroadcaster b, float pulseRadius, int[] c)
        {
            c[cBp]++;
            if (sBpWatch == null) sBpWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            long tick = Simulator.TicksElapsed();
            if (tick != sBpTick)
            {
                sBpWatch.Restart();
                sBpTick = tick; sBpN = 0;
                foreach (Sim sim in LotManager.Actors)
                {
                    if (sim == null || !sim.InWorld) continue;  // the game's first filter
                    if (sBpN >= kBpTable) { sBpN = -1; break; }
                    Vector3 p = sim.Position;
                    sBpSims[sBpN] = sim; sBpX[sBpN] = p.x; sBpZ[sBpN++] = p.z;
                }
                c[cBp + 7]++;
                sBpTableUs += sBpWatch.GetElapsedTime();
                c[cBp + 8] = (int)(sBpTableUs / 1000);
            }
            sBpWatch.Restart();
            try
            {
                if (BcScopeOf(b, sNoBc, pulseRadius) != AutOffloadCore.kScopeApply) return null;  // household / seen: the game's set only
                IGameObject o = b.BroadcastingObject;
                ReactionBroadcasterParams p = b.BroadcasterParams;
                BcReq q = sBpReq;
                q.Skip = o == null || p == null || sBpN < 0 ? 2 : RoomCheckKind(b.GetType());
                if (q.Skip != 0) { c[cBp + 11]++; return null; }
                q.B = b; q.Obj = o; q.R = pulseRadius;
                q.Species = p.SpeciesToEffect; q.RoomOnly = p.AffectBroadcasterRoomOnly; q.Level = p.AutonomyLevelToReact;
                Vector3 bp = o.Position;
                BcRes r = sBpRes;
                r.N = 0;
                int n = AutOffloadCore.Scan(sBpX, sBpZ, sBpN, bp.x, bp.z, pulseRadius + sBcBuffer + kBcFoot, sBpAt);
                for (int i = 0; i < n; i++)
                {
                    Sim sim = sBpSims[sBpAt[i]];
                    int k = BcKeep(q, o, sim);
                    if (k == 0) continue;
                    if (k < 0 || r.N >= kBcSims) { c[cBp + 11]++; return null; }
                    r.Sims[r.N] = sim;
                    r.Near[r.N++] = o.GetDistanceToObject(sim) <= pulseRadius;
                }
                return BcRebuild(b, r, pulseRadius);
            }
            finally
            {
                sBpScanUs += sBpWatch.GetElapsedTime();
                c[cBp + 9] = (int)(sBpScanUs / 1000);
            }
        }

        static void BcPosCompare(Dictionary<ObjectGuid, Sim> mine, Dictionary<ObjectGuid, Sim> game, long gameUs, int[] c)
        {
            c[cBp + 1]++;
            sBpGameUs += gameUs;
            c[cBp + 10] = (int)(sBpGameUs / 1000);
            if (game == null || game.Count > sBpGame.Length) { c[cBp + 11]++; return; }
            int nm = 0, ng = 0, miss, extra;
            foreach (Sim s in mine.Values) sBpMine[nm++] = s;
            foreach (Sim s in game.Values) sBpGame[ng++] = s;
            int cls = AutOffloadCore.SetDiff(sBpMine, nm, sBpGame, ng, out miss, out extra);
            Array.Clear(sBpMine, 0, nm); Array.Clear(sBpGame, 0, ng);
            if (cls < 2) c[cBp + 2]++;
            if (cls == 0) c[cBp + 3]++;
            c[cBp + 4] += miss; c[cBp + 5] += extra;
        }
    }
}
