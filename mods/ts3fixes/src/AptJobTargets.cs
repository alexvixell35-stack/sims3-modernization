using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // aptgasleak, aptpossess (tunings kAptGasLeak, kAptPossess, off by default; docs/863). EA bug fixes in two active-career
    // jobs on apartment lots: the job's target can land inside a neighbour's hidden unit, which no Sim can enter (every
    // door of a hidden room is a locked portal for everyone, docs/552 §1), so the task can never be done and the job
    // waits out its timeout (Job.OnJobTimeout re-arms while the job's Sim stays on the lot).
    //
    // [verified, decompiled] EA keeps hidden rooms out of its own placements on lots with virtual residential slots
    // (FindIsolatedFirePosition for fires, SpiritMaker.MoveToNewLocation for spirits, GhostHunterJob's allowed-room pick for
    // hidden ghosts), but not here:
    //   (a) aptgasleak: FireFightingJob.SetupTask(GasLeak) (earthquake jobs) starts the leak on the first stove of the lot
    //       without a leak, wherever it stands. Only FixGasLeak on that stove (or the stove being deleted) ends the task.
    //   (b) aptpossess: when an Exorcise fails, GhostHunterJob.GetPossessableObjectInRange sends the spirit to any
    //       possessable object of the lot on the same level within kPossessedObjectRange (8 m), with no route or room test
    //       (the first possession is route-checked; the hop is not). Through a wall that can be furniture in a hidden unit.
    // Fix (a): once a sim-minute (FireGreet's alarm) while an emergency is set up on such a lot: a leaking stove in a hidden
    // (or fogged) room has its leak stopped, the job's own disposal handler taken off it, and one GasLeak task dropped the way
    // SimDied drops a task (no task pay); the job then ends through EA's OnTaskComplete -> OnCompletion if nothing is left.
    // (b): a hop that lands in a hidden room is re-picked with EA's own rule over the objects outside hidden rooms; with none
    // left it returns null, EA's own "no object in range" outcome (the exorcise succeeds).
    // The hidden test is EA's (FindIsolatedFirePosition: LightBlocking room of the point is hidden, or the point is in a
    // fogged tomb room). The household firefighter / ghost hunter is affected only through the job's outcome (EA bug fix).
    // Counters: [2181] hidden leaks dropped, [2182] hidden leaks seen with aptgasleak off, [2183] errors; [2184] hops moved
    // out of a hidden room, [2185] hidden hops seen with aptpossess off, [2186] errors. Check: AptJobTargetsCheck.
    public static partial class Fixes
    {
        static readonly MethodInfo sAglDisposed = typeof(FireFightingJob).GetMethod("OnStoveDisposed", kInstAll);
        static readonly FieldInfo sAppList = typeof(GhostHunter.GhostHunterJob).GetField("mPossessableObjects", kInstAll);
        static readonly FieldInfo sAppRange = typeof(GhostHunter).GetField("kPossessedObjectRange", kStaticAll);
        static readonly List<ObjectGuid> sAglSeen = new List<ObjectGuid>();

        internal static string AptMissing()
        {
            string m = FloMissing();
            if (sAglDisposed == null) m += (m.Length > 0 ? ", " : "") + "FireFightingJob.OnStoveDisposed";
            if (sAppList == null) m += (m.Length > 0 ? ", " : "") + "GhostHunterJob.mPossessableObjects";
            if (sAppRange == null) m += (m.Length > 0 ? ", " : "") + "GhostHunter.kPossessedObjectRange";
            return m;
        }

        static void ApplyAptPossess()
        {
            if (sAppList == null || sAppRange == null) { AppliedLog().Add("aptpossess: members not found (" + AptMissing() + "), not patched"); return; }
            Patch(typeof(GhostHunter.GhostHunterJob), "GetPossessableObjectInRange", typeof(AppHook), "RangeHook", "RangeOrig");
        }

        // EA's FindIsolatedFirePosition test: a point in a hidden or fogged room of this lot. Decisions: AptCore (AptJobTargetsCore.cs).
        static bool AptHidden(Lot lot, Vector3 p)
        {
            LotLocation loc = LotLocation.Invalid;
            World.GetLotLocation(p, ref loc);
            return lot.IsRoomHidden(World.GetRoomId(lot.LotId, loc, eRoomDefinition.LightBlocking)) || TombRoomManager.IsPointInAFoggedRoom(p);
        }

        // Called from FireGreetTick, once a sim-minute. Runs with the switch off too, to count the off-arm cases once per stove.
        static void AptGasLeakTick()
        {
            try
            {
                FireFightingJob job; Lot lot; Sim owner;
                if (AptMissing().Length > 0 || !FgJob(out job, out lot, out owner) || !lot.HasVirtualResidentialSlots) return;
                if (sFloSituation.GetValue(job) == null) return;  // not set up yet (EA builds the situation last)
                if (job.PendingTasks == null || !job.PendingTasks.Contains(TaskId.GasLeak)) return;  // earthquakes only
                bool on = ShortcutOn("aptgasleak");
                foreach (IStove stove in Sims3.Gameplay.Queries.GetObjects<IStove>(lot))
                {
                    GameObject go = stove as GameObject;
                    List<TaskId> pending = job.PendingTasks;
                    if (ActiveFireFighter.sActiveJob != job || pending == null) return;  // the job ended
                    if (go == null || !stove.HasGasLeak || !AptCore.GasDrop(true, true, true, AptHidden(lot, go.Position), pending.Contains(TaskId.GasLeak))) continue;
                    if (!on)
                    {
                        if (sAglSeen.Contains(go.ObjectId)) continue;
                        if (sAglSeen.Count > 64) sAglSeen.Clear();
                        sAglSeen.Add(go.ObjectId);
                        PatchCounters()[2182]++;
                        continue;
                    }
                    stove.StopGasLeak();
                    // EA's own subscription for this leak (SetupTask), removed as EA's failed-job cleanup does; a later deletion of
                    // the stove must not complete another leak's task
                    go.ObjectDisposed -= (GameObject.ObjectDisposedEvent)Bind(typeof(GameObject.ObjectDisposedEvent), job, sAglDisposed);
                    if (job.Tasks != null) job.Tasks.Remove(TaskId.GasLeak);  // as SimDied: no task pay
                    job.OnTaskComplete(TaskId.GasLeak, null);                 // EA: PendingTasks.Remove, then OnCompletion if nothing is left
                    PatchCounters()[2181]++;
                }
            }
            catch (Exception) { PatchCounters()[2183]++; }
        }

        public class AppHook
        {
            public IGameObject RangeHook(IGameObject possessedObject)
            {
                IGameObject r = RangeOrig(possessedObject);
                if (r == null) return r;
                try
                {
                    Lot lot = r.LotCurrent;
                    bool on = ShortcutOn("aptpossess");
                    if (lot == null || !lot.HasVirtualResidentialSlots || !AptHidden(lot, r.Position)) return r;
                    if (AptCore.KeepHop(on, true, true)) { PatchCounters()[2185]++; return r; }  // switch off
                    // EA's own pick (same level, within kPossessedObjectRange, at random) over the objects outside hidden rooms
                    List<IGameObject> all = sAppList.GetValue((object)this) as List<IGameObject>, ok = new List<IGameObject>();
                    float range = (float)sAppRange.GetValue(null);
                    if (all != null)
                        foreach (IGameObject o in all)
                            if (o != null && AptCore.HopCandidate(o.Level == possessedObject.Level, o.GetDistanceToObject(possessedObject), range, AptHidden(lot, o.Position))) ok.Add(o);
                    PatchCounters()[2184]++;
                    return ok.Count > 0 ? RandomUtil.GetRandomObjectFromList(ok) : null;  // none: EA's "no object in range", the exorcise succeeds
                }
                catch (Exception) { PatchCounters()[2186]++; return r; }
            }
            public IGameObject RangeOrig(IGameObject possessedObject) { return null; }
        }

        // Per apartment lot: stoves and other unparented objects inside hidden rooms (can this world trigger either bug?).
        // Objects are approximate for (b): EA's possess filter (not wall / ceiling / hidden-model / INotHauntable) is not applied.
        static string AptScan()
        {
            int lots = 0, stoves = 0, objects = 0;
            string rows = "";
            foreach (object x in LotManager.AllLots)
            {
                Lot lot = x as Lot;
                if (lot == null || lot.IsWorldLot || !lot.HasVirtualResidentialSlots) continue;
                lots++;
                int s = 0, o = 0;
                foreach (GameObject g in lot.GetObjects<GameObject>())
                {
                    if (g == null || g.Parent != null || g is Sim || g is IPortalConnectionObject || g is Windows || g.GetType().Name.EndsWith("Marker") || !AptHidden(lot, g.Position)) continue;
                    if (g is IStove) s++; else o++;
                }
                stoves += s; objects += o;
                if (s + o > 0 && rows.Length < 1500) rows += " | " + lot.Name + " (" + lot.LotId.ToString("X") + "): stoves " + s + ", objects " + o;
            }
            return "apartment lots " + lots + ", in hidden rooms: stoves " + stoves + ", other objects " + objects + rows;
        }

        // AptJobTargetsCheck run: both decisions for the reported layouts (a leaking stove in a hidden unit with a GasLeak task
        // pending; a spirit hop into a hidden unit) with controls; read-only.
        //   off BROKEN = leak kept, hop kept; on FIXED = leak dropped, hop re-picked. Controls (EA's outcome in both): an
        //   ordinary lot, a stove outside hidden rooms, no leak, no GasLeak task pending, a hop outside hidden rooms.
        //   scan    per apartment lot: stoves / objects in hidden rooms; result  counters 2181-2186
        public static object AptJobTargetsCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = AptMissing();
            string counters = "aptgasleak: hidden leaks dropped 2181=" + c[2181] + ", seen off 2182=" + c[2182] + ", errors 2183=" + c[2183]
                + "; aptpossess: hops moved 2184=" + c[2184] + ", hidden hops seen off 2185=" + c[2185] + ", errors 2186=" + c[2186]
                + "; members missing: " + (missing.Length == 0 ? "none" : missing);
            if (what == "result") return counters;
            if (what == "scan") return "aptjobtargets scan: " + AptScan() + "; " + counters;
            if (what != "run") return "aptjobtargets <run|scan|result>";
            bool gOn = ShortcutOn("aptgasleak"), pOn = ShortcutOn("aptpossess");
            bool gas = AptCore.GasDrop(gOn, true, true, true, true), hop = !AptCore.KeepHop(pOn, true, true);
            bool controlsOk = !AptCore.GasDrop(gOn, false, true, true, true) && !AptCore.GasDrop(gOn, true, true, false, true)
                && !AptCore.GasDrop(gOn, true, false, true, true) && !AptCore.GasDrop(gOn, true, true, true, false)
                && AptCore.KeepHop(pOn, false, true) && AptCore.KeepHop(pOn, true, false)
                && AptCore.HopCandidate(true, 8f, 8f, false) && !AptCore.HopCandidate(true, 8.5f, 8f, false) && !AptCore.HopCandidate(false, 1f, 8f, false);
            string g = !gOn ? (gas ? "UNEXPECTED" : "BROKEN") : (gas ? "FIXED" : "UNEXPECTED");
            string p = !pOn ? (hop ? "UNEXPECTED" : "BROKEN") : (hop ? "FIXED" : "UNEXPECTED");
            string verdict = !controlsOk || missing.Length > 0 || g != p ? "UNEXPECTED" : g;
            return "aptjobtargets check (aptgasleak " + (gOn ? "on" : "off") + ", aptpossess " + (pOn ? "on" : "off") + "): hidden leaking stove -> dropped=" + gas
                + " (" + g + "), spirit hop into a hidden unit -> re-picked=" + hop + " (" + p + "); controls ok=" + controlsOk + "; verdict: " + verdict + "; " + counters;
        }
    }
}
