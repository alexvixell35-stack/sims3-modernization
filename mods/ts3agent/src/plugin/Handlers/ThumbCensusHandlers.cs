using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.Gameplay.CAS;

namespace Ts3Agent
{
    // Thumbnail backlog census (leak hunt 11-11, notes: pc-logs/2026-11-06-test-runs/re-thumb-1111). READ ONLY: it changes
    // nothing in the game. EA packs a Sim away in a script task (SimDescription.PackUpToMiniSimDescription ->
    // MiniSimDescription.PackSim -> GenerateCrossWorldThumbnail -> ThumbnailManager.GenerateHouseholdSimThumbnail, wait: true);
    // the task sleeps inside that call until the native UI/ThumbnailManager has rendered the Sim's thumbnail, and only then
    // disposes the SimDescription and clears its outfits. The native manager works ONE request at a time (the front of its
    // InProgressRequests deque, a state machine stepped once per frame on the main thread). This command counts both ends.
    public static partial class Handlers
    {
        static void RegisterThumbCensus()
        {
            Dispatcher.Register("thumb_census", ThumbCensus);
        }

        // Steam 1.67 [verified: disassembly + Ghidra, 11-11]: UI/ThumbnailManager singleton pointer, its service vtable, the
        // vtable's main-thread slot (+0x1C = ThumbnailManager_Update 0xD61670, the exe check)
        const int kTmPtr = 0x011EDC30, kTmVtable = 0x01088F2C, kTmUpdate = 0x00D61670;
        const int kTmPendBegin = 0xC8, kTmPendEnd = 0xCC, kTmPendSize = 0x1A8;  // PendingRequests vector
        const int kTmDqBeginCur = 0x100, kTmDqBeginFirst = 0x104, kTmDqBeginNode = 0x10C;  // InProgressRequests deque
        const int kTmDqEndCur = 0x110, kTmDqEndFirst = 0x114, kTmDqEndNode = 0x11C, kTmReqSize = 0x248;
        const int kReqState = 0x108, kReqPrepared = 0x238, kReqCreated = 0x23C;  // one in-progress request

        static FieldInfo sPackTaskField, sTmInstanceField, sTmResultsField;
        static bool sThumbFieldsTried;
        static string sThumbMissing = "";
        static readonly Dictionary<ulong, long> sPackSeen = new Dictionary<ulong, long>();  // description id -> first seen (ticks)
        static int sPacksStarted, sPacksFinished, sFrontChanges, sCensusCalls;
        static long sFrontKey, sFrontSince, sMaxPackWaitMs, sMaxFrontMs;
        static int sMaxInProgress, sMaxPackTasks;
        static bool sDqSeen;
        static int sDqMgr, sDqBegin, sDqEnd, sDqBreaks;
        static long sTmDone, sTmQueued;

        static int Rd(int addr) { return Marshal.ReadInt32(new IntPtr(addr)); }

        static void ThumbFields()
        {
            if (sThumbFieldsTried) return;
            sThumbFieldsTried = true;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            sPackTaskField = typeof(SimDescription).GetField("mPackingDescriptionTask", f);
            if (sPackTaskField == null) sThumbMissing += "SimDescription.mPackingDescriptionTask ";
            sTmInstanceField = typeof(Sims3.SimIFace.ThumbnailManager).GetField("sInstance", f);
            if (sTmInstanceField == null) sThumbMissing += "ThumbnailManager.sInstance ";
        }

        // every household the game knows: the list plus each static Household field (NPC, pet, tourist, ...), once each
        static List<Household> AllHouseholdsForCensus()
        {
            List<Household> hs = new List<Household>();
            if (Household.sHouseholdList != null)
                foreach (Household h in Household.sHouseholdList) if (h != null && !hs.Contains(h)) hs.Add(h);
            foreach (FieldInfo fi in typeof(Household).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (fi.FieldType != typeof(Household)) continue;
                Household h = null;
                try { h = fi.GetValue(null) as Household; } catch (Exception) { }
                if (h != null && !hs.Contains(h)) hs.Add(h);
            }
            return hs;
        }

        // thumb_census {deep?: true, reset?: false}. Fields:
        //  native: "ok", or why the native part was skipped (other exe, manager not created)
        //  tm_pending: requests queued this frame, not yet taken by the main thread (emptied every frame: stays near 0)
        //  tm_in_progress: requests in the serial render queue, the front one being worked on. THE BACKLOG.
        //  tm_done / tm_queued: requests finished / entered since the first call or reset, EXACT between calls (from how far
        //    the deque's ends moved); tm_count_breaks: steps dropped because the deque's node map moved
        //  tm_front_state: the front request's state (0-0x1E; 3 = waits for the script to prepare the scene; 0x11 = render;
        //    0x14/0x15 = waits for CAS parts and skin; 0x1E = done); tm_front_prepared: script preparation reported done
        //  tm_front_age_ms: how long the same request has been at the front, by this command's own clock (first seen)
        //  tm_front_changes: times the front request changed between two calls (a lower bound on requests finished)
        //  tm_preparing / tm_prep_count: the manager's "script preparation running" flag and counter (+0x239, +0x23C)
        //  thumb_waits_done: entries in ScriptCore.ThumbnailManager.mCallbackResults = script tasks whose waited request came
        //    back, since the game started (EA never removes an entry; one per task)
        //  pack_tasks: SimDescriptions whose mPackingDescriptionTask is set now (a pack-away task that has not finished)
        //  pack_oldest_ms: the longest any of them has been seen waiting (first seen by this command, so it needs sampling)
        //  packs_started / packs_finished: pack tasks first seen / seen gone, since the first call or reset (sampled)
        //  minisims / minisims_instantiated: MiniSimDescription.sMiniSims entries, and those flagged Instantiated
        //  minisim_updates_pending: MiniSimDescription.sPendingSimUpdates (thumbnail tasks AddMiniSims waits for at departure)
        //  max_*: the largest values any call has seen; calls: calls since reset; missing: fields this build does not have
        static object ThumbCensus(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            long now = DateTime.UtcNow.Ticks;
            if (a.Bool("reset", false))
            {
                sPackSeen.Clear();
                sPacksStarted = sPacksFinished = sFrontChanges = sCensusCalls = sMaxInProgress = sMaxPackTasks = 0;
                sFrontKey = sFrontSince = sMaxPackWaitMs = sMaxFrontMs = sTmDone = sTmQueued = 0;
                sDqSeen = false; sDqBreaks = 0;
            }
            sCensusCalls++;
            ThumbFields();

            // native side: plain reads of the manager's own words (and, deep, three words of the front request)
            try
            {
                int mgr = 0;
                if (Rd(kTmVtable + 0x1C) != kTmUpdate) r["native"] = "not the Steam 1.67 exe";
                else if ((mgr = Rd(kTmPtr)) == 0 || Rd(mgr) != kTmVtable) r["native"] = "no thumbnail manager";
                else
                {
                    r["native"] = "ok";
                    r["tm_pending"] = (Rd(mgr + kTmPendEnd) - Rd(mgr + kTmPendBegin)) / kTmPendSize;
                    int bc = Rd(mgr + kTmDqBeginCur), bf = Rd(mgr + kTmDqBeginFirst), bn = Rd(mgr + kTmDqBeginNode);
                    int ec = Rd(mgr + kTmDqEndCur), ef = Rd(mgr + kTmDqEndFirst), en = Rd(mgr + kTmDqEndNode);
                    int n = bc == ec ? 0 : (en - bn) + (ec - ef) / kTmReqSize - (bc - bf) / kTmReqSize;  // 4 requests a block, 4 bytes a node
                    if (n < 0 || n > 100000) n = -1;  // a torn read (the main thread moved the deque meanwhile)
                    r["tm_in_progress"] = n;
                    // exact counts between two calls from how far the deque's two ends moved (a node is 4 bytes and holds
                    // 4 requests, so node bytes = requests); a step that makes no sense (the node map was reallocated, or a
                    // torn read) is dropped
                    int bpos = bn + (bc - bf) / kTmReqSize, epos = en + (ec - ef) / kTmReqSize;
                    if (sDqSeen && mgr == sDqMgr)
                    {
                        int done = bpos - sDqBegin, queued = epos - sDqEnd;
                        if (done >= 0 && done < 50000 && queued >= 0 && queued < 50000) { sTmDone += done; sTmQueued += queued; }
                        else sDqBreaks++;
                    }
                    sDqSeen = true; sDqMgr = mgr; sDqBegin = bpos; sDqEnd = epos;
                    r["tm_done"] = sTmDone;
                    r["tm_queued"] = sTmQueued;
                    if (sDqBreaks > 0) r["tm_count_breaks"] = sDqBreaks;
                    if (n > sMaxInProgress) sMaxInProgress = n;
                    r["tm_preparing"] = Marshal.ReadByte(new IntPtr(mgr + 0x239)) != 0;
                    r["tm_prep_count"] = Rd(mgr + 0x23C);
                    long key = 0;
                    if (n > 0 && bc != 0 && a.Bool("deep", true))
                    {
                        int state = Rd(bc + kReqState);
                        r["tm_front_state"] = state;
                        r["tm_front_prepared"] = Marshal.ReadByte(new IntPtr(bc + kReqPrepared)) != 0;
                        key = ((long)bc << 32) | (uint)Rd(bc + kReqCreated);  // slot address + creation stamp names the request
                    }
                    else if (n > 0) key = bc;
                    if (key != sFrontKey)
                    {
                        if (sFrontKey != 0) sFrontChanges++;
                        sFrontKey = key;
                        sFrontSince = now;
                    }
                    long frontMs = key == 0 ? 0 : (now - sFrontSince) / 10000;
                    if (frontMs > sMaxFrontMs) sMaxFrontMs = frontMs;
                    r["tm_front_age_ms"] = frontMs;
                    r["tm_front_changes"] = sFrontChanges;
                }
            }
            catch (Exception e) { r["native"] = "error: " + e.GetType().Name; }

            // script side
            try
            {
                object tm = sTmInstanceField == null ? null : sTmInstanceField.GetValue(null);
                if (tm != null)
                {
                    if (sTmResultsField == null)
                        for (Type t = tm.GetType(); t != null && sTmResultsField == null; t = t.BaseType)
                            sTmResultsField = t.GetField("mCallbackResults", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                    // class check first: this Mono reads past the object when the field belongs to another class
                    if (sTmResultsField != null && sTmResultsField.DeclaringType.IsInstanceOfType(tm))
                    {
                        Hashtable done = sTmResultsField.GetValue(tm) as Hashtable;
                        if (done != null) r["thumb_waits_done"] = done.Count;
                    }
                }
            }
            catch (Exception) { }

            try
            {
                int packing = 0;
                long oldest = 0;
                Dictionary<ulong, bool> live = new Dictionary<ulong, bool>();
                if (sPackTaskField != null)
                    foreach (Household h in AllHouseholdsForCensus())
                    {
                        List<SimDescription> sds = null;
                        try { sds = h.AllSimDescriptions; } catch (Exception) { }
                        if (sds == null) continue;
                        foreach (SimDescription sd in new List<SimDescription>(sds))
                        {
                            if (sd == null || sPackTaskField.GetValue(sd) == null) continue;
                            ulong id = sd.SimDescriptionId;
                            if (live.ContainsKey(id)) continue;
                            live[id] = true;
                            packing++;
                            long first;
                            if (!sPackSeen.TryGetValue(id, out first)) { sPackSeen[id] = first = now; sPacksStarted++; }
                            if (now - first > oldest) oldest = now - first;
                        }
                    }
                foreach (ulong id in new List<ulong>(sPackSeen.Keys))
                    if (!live.ContainsKey(id)) { sPackSeen.Remove(id); sPacksFinished++; }
                if (packing > sMaxPackTasks) sMaxPackTasks = packing;
                if (oldest / 10000 > sMaxPackWaitMs) sMaxPackWaitMs = oldest / 10000;
                r["pack_tasks"] = packing;
                r["pack_oldest_ms"] = oldest / 10000;
                r["packs_started"] = sPacksStarted;
                r["packs_finished"] = sPacksFinished;
            }
            catch (Exception e) { r["pack_error"] = e.GetType().Name; }

            try
            {
                FieldInfo mf = typeof(MiniSimDescription).GetField("sMiniSims", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                IDictionary minis = mf == null ? null : mf.GetValue(null) as IDictionary;  // a static field: no object to class-check
                if (minis != null)
                {
                    int inst = 0;
                    foreach (object o in new ArrayList(minis.Values))
                    {
                        MiniSimDescription m = o as MiniSimDescription;
                        if (m != null && m.Instantiated) inst++;
                    }
                    // EA's own counter of MiniSimUpdater tasks still waiting on their thumbnail (AddMiniSims, before a world switch)
                    FieldInfo pf = typeof(MiniSimDescription).GetField("sPendingSimUpdates", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pf != null) r["minisim_updates_pending"] = Convert.ToInt64(pf.GetValue(null));
                    r["minisims"] = minis.Count;
                    r["minisims_instantiated"] = inst;
                }
            }
            catch (Exception) { }

            r["max_tm_in_progress"] = sMaxInProgress;
            r["max_tm_front_age_ms"] = sMaxFrontMs;
            r["max_pack_tasks"] = sMaxPackTasks;
            r["max_pack_wait_ms"] = sMaxPackWaitMs;
            r["calls"] = sCensusCalls;
            if (sThumbMissing.Length > 0) r["missing"] = sThumbMissing.Trim();
            return r;
        }
    }
}
