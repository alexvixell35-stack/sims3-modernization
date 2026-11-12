using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.Gameplay.CAS;

namespace Ts3Agent
{
    // Thumbnail backlog census (issue #40). READ ONLY: it changes
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
            Dispatcher.Register("thumb_outfits", ThumbOutfits);  // repro trigger, ThumbOutfitsHandlers.cs (not read only)
        }

        // Steam 1.67 [verified: disassembly + Ghidra, 11-11]: UI/ThumbnailManager singleton pointer, its service vtable, the
        // vtable's main-thread slot (+0x1C = ThumbnailManager_Update 0xD61670, the exe check)
        const int kTmPtr = 0x011EDC30, kTmVtable = 0x01088F2C, kTmUpdate = 0x00D61670;
        const int kTmPendBegin = 0xC8, kTmPendEnd = 0xCC, kTmPendSize = 0x1A8;  // PendingRequests vector
        const int kTmDqBeginCur = 0x100, kTmDqBeginFirst = 0x104, kTmDqBeginNode = 0x10C;  // InProgressRequests deque
        const int kTmDqEndCur = 0x110, kTmDqEndFirst = 0x114, kTmDqEndNode = 0x11C, kTmReqSize = 0x248;
        const int kReqState = 0x108, kReqType = 0x118, kReqPrepared = 0x238, kReqCreated = 0x23C;  // one in-progress request

        static FieldInfo sPackTaskField, sTmInstanceField, sTmResultsField;
        static bool sThumbFieldsTried;
        static string sThumbMissing = "";
        static readonly Dictionary<ulong, long> sPackSeen = new Dictionary<ulong, long>();  // description id -> first seen (ticks)
        static int sPacksStarted, sPacksFinished, sFrontChanges, sCensusCalls;
        static long sFrontKey, sFrontSince, sMaxPackWaitMs, sMaxFrontMs;
        static int sMaxInProgress, sMaxPackTasks;
        static bool sDqSeen;
        static int sDqMgr, sDqBegin, sDqEnd, sDqBreaks, sDqLen;
        static int sBlk, sBlkChanges, sMaxLodInuse;

        // the Steam 1.67 exe: the manager's Update slot, or ([ThumbStall] Action 3 hooks that slot) the manager's own vtable
        static bool Steam167()
        {
            if (Rd(kTmVtable + 0x1C) == kTmUpdate) return true;
            int mgr = Rd(kTmPtr);
            return mgr != 0 && (mgr & 3) == 0 && Rd(mgr) == kTmVtable;
        }
        static long sBlkSince;
        static long sTmDone, sTmQueued;

        static int sFrontState, sKicks;
        static bool sKickRunning;
        static string sKickError;

        static ulong sWhoInst;
        static string sWho;
        // the SimDescription (any household, the NPC and tourist ones included) with an outfit whose key instance is inst
        static string FindOutfitOwner(ulong inst)
        {
            try
            {
                foreach (Sims3.Gameplay.CAS.Household h in Sims3.Gameplay.CAS.Household.sHouseholdList)
                {
                    if (h == null || h.AllSimDescriptions == null) continue;
                    foreach (Sims3.Gameplay.CAS.SimDescription sd in h.AllSimDescriptions)
                    {
                        if (sd == null) continue;
                        foreach (Sims3.SimIFace.CAS.OutfitCategories cat in Enum.GetValues(typeof(Sims3.SimIFace.CAS.OutfitCategories)))
                        {
                            int count;
                            try { count = sd.GetOutfitCount(cat); } catch (Exception) { continue; }
                            for (int i = 0; i < count; i++)
                            {
                                Sims3.SimIFace.CAS.SimOutfit o = sd.GetOutfit(cat, i);
                                if (o == null || o.Key.InstanceId != inst) continue;
                                System.Text.StringBuilder s = new System.Text.StringBuilder();
                                s.Append(sd.FullName).Append(" | ").Append(h.Name).Append(" | ").Append(sd.Age).Append(" ").Append(cat).Append(i)
                                    .Append(sd.CreatedSim != null ? " | live" : " | not live").Append(" | parts");
                                foreach (Sims3.SimIFace.CAS.CASPart p in o.Parts)
                                    s.Append(" ").Append(p.Key.InstanceId.ToString("x16")).Append(":").Append(p.BodyType);
                                return s.ToString();
                            }
                        }
                    }
                }
                return "no owner found";
            }
            catch (Exception e) { return "error: " + e.GetType().Name; }
        }
        static FieldInfo sBuildersField;
        static ArrayList ThumbBuilders()  // a static field: no object to class-check; the value's type is checked by the cast
        {
            if (sBuildersField == null)
                sBuildersField = typeof(Sims3.Gameplay.Core.ThumbnailHelper).GetField("sBuilders", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return sBuildersField == null ? null : sBuildersField.GetValue(null) as ArrayList;
        }

        static void ThumbKick()
        {
            try
            {
                ArrayList tb = ThumbBuilders();
                Sims3.SimIFace.CAS.SimBuilder sb = tb != null && tb.Count > 0 ? tb[0] as Sims3.SimIFace.CAS.SimBuilder : null;
                if (sb != null && sb.IsDirty()) sb.BuildModel(0, 200u);
            }
            catch (Exception e) { sKickError = e.GetType().Name; }
            finally { sKickRunning = false; }
        }

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
        //  tm_front_type: the front request's key type word (+0x118); tm_kinds: that word over the whole queue (first 256)
        //  blk*: only while the front is in state 0x11 (the "whole scene has no pending composite" wait): the scene object
        //    that blocks it (request +0x244): pointer, vtable, flags (+0x40: 2 = composite not finished, 0x40 = pending
        //    list not empty), layers (+0x44), blk_composite_state (3 = finished), blk_own (it is one of the request's own
        //    models, +0xA8..+0xAC), blk_age_ms (same blocker since), blk_changes (times the blocker changed)
        //    blk_handle: the 29 words of the blocker's model handle (+0x210); blk_slots_loading: which slots are not settled
        //    (model = +0 still a key, pending = +4 set, nolod = +8 empty or a key, lodload = +0xC set, override = +0x10 set);
        //    blk_res0..blk_res10: vtable, state (+0x30, 3 = loaded) and reference count of each slot that holds a resource;
        //    blk_composite_state: the current LOD's state word, when +8 holds a resource
        //  tm_front_key: the front request's four key words (+0x110..+0x11C)
        //  tb_builders / tb_dirty: ThumbnailHelper.sBuilders count, and whether builder 0 still has an unfinished build
        //  tb_kicks / tb_kick_running / tb_kick_error: the optional re-kick (args {"kick":true}), see below
        //  cas_registry_n: entries in the CAS part-compositor registry ([0x11CE6A0] +0x110), every sample
        //  rc_n / rc_mb / rc_cold_mb: the resource cache's entry count, held MB and idle MB (every sample)
        //  game_speed: Gameflow.CurrentGameSpeed
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
                sDqSeen = false; sDqBreaks = 0; sBlk = 0; sBlkChanges = 0; sBlkSince = 0; sMaxLodInuse = 0;
            }
            sCensusCalls++;
            ThumbFields();
            int blkProv = 0;  // the blocker's LOD provider (model handle +0x3C), for the SimService part below

            // native side: plain reads of the manager's own words (and, deep, three words of the front request)
            try
            {
                int mgr = 0, upd = Rd(kTmVtable + 0x1C);
                // [ThumbStall] Action=3 hooks this Update slot (0x1088F48); the manager's own vtable check below still proves the exe
                if (upd != kTmUpdate) r["tm_update_slot"] = "0x" + upd.ToString("x8");
                if ((mgr = Rd(kTmPtr)) == 0 || Rd(mgr) != kTmVtable) r["native"] = upd != kTmUpdate ? "not the Steam 1.67 exe" : "no thumbnail manager";
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
                        else
                        {
                            // lower bounds from the two lengths, so a break loses little
                            sDqBreaks++;
                            if (n >= 0 && sDqLen >= 0) { if (n < sDqLen) sTmDone += sDqLen - n; else sTmQueued += n - sDqLen; }
                        }
                    }
                    sDqSeen = true; sDqMgr = mgr; sDqBegin = bpos; sDqEnd = epos; sDqLen = n;
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
                        sFrontState = state;
                        r["tm_front_prepared"] = Marshal.ReadByte(new IntPtr(bc + kReqPrepared)) != 0;
                        key = ((long)bc << 32) | (uint)Rd(bc + kReqCreated);  // slot address + creation stamp names the request
                        r["tm_front_type"] = "0x" + Rd(bc + kReqType).ToString("x8");
                        // the request's key words (+0x110..+0x11C; +0x118 is the type): names the outfit, so the Sim
                        r["tm_front_key"] = Rd(bc + 0x110).ToString("x8") + " " + Rd(bc + 0x114).ToString("x8") + " " + Rd(bc + 0x118).ToString("x8") + " " + Rd(bc + 0x11C).ToString("x8");
                        // tm_front_who: while the front waits in 0x11 for 3 s, the Sim whose outfit has this key and the
                        // outfit's CAS parts (instance:bodytype). Looked up once per outfit key; read only.
                        if (state == 0x11 && sFrontSince != 0 && (now - sFrontSince) / 10000 >= 3000 && a.Bool("who", true))
                        {
                            ulong inst = ((ulong)(uint)Rd(bc + 0x114) << 32) | (uint)Rd(bc + 0x110);
                            if (inst != sWhoInst) { sWhoInst = inst; sWho = FindOutfitOwner(inst); }
                            r["tm_front_who"] = sWho;
                        }
                        // state 0x11 keeps the scene object that blocks it in request +0x244 (0xD5A9CC)
                        int blk = state == 0x11 ? Rd(bc + 0x244) : 0;
                        if (blk != sBlk) { if (blk != 0 && sBlk != 0) sBlkChanges++; sBlk = blk; sBlkSince = now; }
                        if (blk != 0 && (blk & 3) == 0)
                        {
                            r["blk"] = "0x" + blk.ToString("x8");
                            r["blk_age_ms"] = (now - sBlkSince) / 10000;
                            r["blk_vt"] = "0x" + Rd(blk).ToString("x8");
                            r["blk_flags"] = "0x" + Rd(blk + 0x40).ToString("x");
                            r["blk_layers"] = "0x" + Rd(blk + 0x44).ToString("x8");
                            r["blk_composite_on"] = Marshal.ReadByte(new IntPtr(blk + 0x210 + 0x5E)) != 0;
                            r["blk_pending_list"] = Rd(blk + 0x210 + 0x18) != Rd(blk + 0x210 + 0x14);
                            // +0x210 is the SceneModel's model handle (docs/186 10.3): tagged resource slots +0 model, +4 pending
                            // model, +8 current LOD, +0xC loading LOD, +0x10 override (low two bits set = still a key being
                            // loaded), +0x14 / +0x18 the two words 0x6DB760 compares, +0x1C version, +0x28..+0x34 model key,
                            // +0x44 LOD stream record, +0x5E ready byte. All 29 words, raw, for offline reading.
                            System.Text.StringBuilder hw = new System.Text.StringBuilder();
                            for (int q = 0; q < 0x74; q += 4) hw.Append(q == 0 ? "" : " ").Append(Rd(blk + 0x210 + q).ToString("x8"));
                            r["blk_handle"] = hw.ToString();
                            r["blk_slots_loading"] = ((Rd(blk + 0x210) & 3) != 0 ? "model " : "") + (Rd(blk + 0x214) != 0 ? "pending " : "")
                                + ((Rd(blk + 0x218) & 3) != 0 || Rd(blk + 0x218) == 0 ? "nolod " : "") + (Rd(blk + 0x21C) != 0 ? "lodload " : "")
                                + (Rd(blk + 0x220) != 0 ? "override" : "");
                            // an untagged slot holds a counted reference to a live resource: its vtable and state word (+0x30, 3 = loaded)
                            for (int slot = 0; slot <= 0x10; slot += 4)
                            {
                                int res = Rd(blk + 0x210 + slot);
                                if (res != 0 && (res & 3) == 0)
                                    r["blk_res" + slot.ToString("x")] = Rd(res).ToString("x8") + " state " + Rd(res + 0x30) + " refs " + (Rd(res + 4) >> 8);
                            }
                            int lod = Rd(blk + 0x218);
                            if (lod != 0 && (lod & 3) == 0) r["blk_composite_state"] = Rd(lod + 0x30);
                            bool own = false;
                            int mb = Rd(bc + 0xA8), me = Rd(bc + 0xAC);
                            for (int q = mb; mb != 0 && q < me && q < mb + 256; q += 4) if (Rd(q) == blk) own = true;
                            r["blk_own"] = own;
                            r["tm_front_models"] = mb == 0 ? 0 : (me - mb) / 4;
                            blkProv = Rd(blk + 0x210 + 0x3C);
                        }
                        r["blk_changes"] = sBlkChanges;
                        // request types through the queue (the key's type word, request +0x118), at most 256 requests
                        Dictionary<string, object> kinds = Util.Obj();
                        int cur = bc, first = bf, node = bn, walked = 0;
                        while (walked < n && walked < 256 && cur != 0)
                        {
                            string k = "0x" + Rd(cur + kReqType).ToString("x8");
                            kinds[k] = kinds.ContainsKey(k) ? (int)kinds[k] + 1 : 1;
                            walked++;
                            cur += kTmReqSize;
                            if (cur >= first + 4 * kTmReqSize) { node += 4; first = Rd(node); cur = first; }
                        }
                        r["tm_kinds"] = kinds;
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

            // resource cache (as ts3native memdiag's "rc" / "rcN"): manager [0x11C78D8], vtable 0xFFE250 or 0xFB2DA0; cache array
            // +0xA0/+0xA4 of {cache, priority}; the priority-0 cache with vtable 0xFFDD94: +0x94 held bytes, +0x98 idle bytes,
            // +0x28 entries. Plain reads of the manager's own words; not part of "deep".
            try
            {
                int rm = Steam167() ? Rd(0x011C78D8) : 0;
                if (rm != 0 && (Rd(rm) == 0x00FFE250 || Rd(rm) == 0x00FB2DA0))
                {
                    int cb = Rd(rm + 0xA0), ce = Rd(rm + 0xA4);
                    for (int q = cb; cb != 0 && q + 8 <= ce && q < cb + 128; q += 8)
                    {
                        int c = Rd(q);
                        if (c == 0 || Rd(q + 4) != 0 || Rd(c) != 0x00FFDD94) continue;
                        r["rc_n"] = Rd(c + 0x28);
                        r["rc_mb"] = Math.Round((uint)Rd(c + 0x94) / 1048576.0, 1);
                        r["rc_cold_mb"] = Math.Round((uint)Rd(c + 0x98) / 1048576.0, 1);
                        break;
                    }
                }
            }
            catch (Exception) { }
            // CAS part-compositor registry (castrim.cpp, issue #284): service [0x11CE6A0], entry count at +0x110. Every
            // SimBuilder outfit build inserts new part compositors (0x5EECD0) and nothing erases them in play.
            try
            {
                int svc = Steam167() ? Rd(0x011CE6A0) : 0;
                if (svc != 0 && (svc & 3) == 0) r["cas_registry_n"] = Rd(svc + 0x110);
                // The same object is the CAS SimService (vtable 0xFE514C), which builds Sim LODs (thumbstall.cpp "Why the
                // provider refuses"): lod_inuse = cached LODs held outside the cache (+0x4C, recounted every update),
                // lod_limit = ActiveLODLimit (+0x40, GraphicsRules.sgr, 200), lod_cached (+0x50), lod_min = MinSimLOD (+0x30),
                // lod_over_limit: no LOD is built for a model that has none in use; lod_jobs_loading / lod_jobs_ready: the
                // +0x58 / +0x60 job lists are not empty; lod_job_current: +0x68 set. Scalar reads only (the lists move on
                // the main thread); the stall log line walks them. blk_provider / _vt: the blocker's provider (state 0x11).
                if (svc != 0 && (svc & 3) == 0 && Rd(svc) == 0x00FE514C)
                {
                    int inuse = Rd(svc + 0x4C), limit = Rd(svc + 0x40);
                    r["lod_inuse"] = inuse;
                    r["lod_limit"] = limit;
                    r["lod_over_limit"] = inuse > limit;
                    r["lod_cached"] = Rd(svc + 0x50);
                    r["lod_min"] = Rd(svc + 0x30);
                    r["lod_jobs_loading"] = Rd(svc + 0x58) != svc + 0x58;
                    r["lod_jobs_ready"] = Rd(svc + 0x60) != svc + 0x60;
                    r["lod_job_current"] = Rd(svc + 0x68) != 0;
                    if (inuse > sMaxLodInuse) sMaxLodInuse = inuse;
                    r["max_lod_inuse"] = sMaxLodInuse;
                    if (blkProv != 0 && (blkProv & 3) == 0)
                    {
                        r["blk_provider"] = "0x" + blkProv.ToString("x8");
                        r["blk_provider_vt"] = "0x" + Rd(blkProv).ToString("x8");
                    }
                }
            }
            catch (Exception) { }

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

            try { r["game_speed"] = Sims3.Gameplay.Gameflow.CurrentGameSpeed.ToString(); } catch (Exception) { }
            // the shared thumbnail SimBuilder (ThumbnailHelper.sBuilders[0]). EA builds each portrait's Sim with
            // BuildModel(0, 200): ScriptCore's loop gives up after 200 ProcessModel rounds and leaves the builder dirty.
            try
            {
                ArrayList tb = ThumbBuilders();
                if (tb != null)
                {
                    r["tb_builders"] = tb.Count;
                    Sims3.SimIFace.CAS.SimBuilder sb = tb.Count > 0 ? tb[0] as Sims3.SimIFace.CAS.SimBuilder : null;
                    if (sb != null)
                    {
                        bool dirty = sb.IsDirty();
                        r["tb_dirty"] = dirty;
                        // kick: true (NOT read only, off by default): while the front request has waited in state 0x11 for
                        // 2 s and the builder is still dirty, run BuildModel(0, 200) again in a task of its own
                        if (a.Bool("kick", false) && dirty && sFrontState == 0x11 && sFrontKey != 0 && (now - sFrontSince) / 10000 >= 2000 && !sKickRunning)
                        {
                            sKickRunning = true;
                            sKicks++;
                            Sims3.SimIFace.Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(new Sims3.Gameplay.Function(ThumbKick)));
                        }
                    }
                }
                r["tb_kicks"] = sKicks;
                r["tb_kick_running"] = sKickRunning;
                if (sKickError != null) r["tb_kick_error"] = sKickError;
            }
            catch (Exception e) { r["tb_error"] = e.GetType().Name; }

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
