using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // unseenoutfitdefer (issue #451, docs/586, default off): an unseen townie's instant outfit change waits for its native
    // model build until the Sim can be seen. Local outfit logging (12-minute games, 4 worlds): live instant outfit sets were
    // 1148-1361 per game for unseen townies against 33-198 for household Sims, and the per-minute count follows the main
    // thread's CAS/SimService frame time (r 0.78-0.89), 1.3-7.6 s of it per minute.
    //
    // What is skipped: only the native ScriptCore.CASUtils.CASUtilsSetOutfitInGameObject call (the non-delayed set) for a
    // live, unseen townie (not household, not selected, not unknown scope: IsPlayerSim; UfWhy 0: the unseen camera rule).
    // Everything EA does around it still runs at EA's time: Sim.SwitchToOutfitWithoutSpin sets mVisibleOutfitInfo /
    // mPendingOutfitInfo, the censor, the body shape, the revealing score and PostOutfitSwap; RefreshCurrentOutfit sets
    // mPreviousOutfitKey. We keep (object id -> the key asked for), and the native call runs later with that key, so the Sim
    // ends in the state EA's call made.
    // Why script code cannot tell [read in the game's script assemblies]:
    //  - Script reads of the model's outfit all go through CASUtilsGetOutfitInGameObject (Sim.CurrentOutfit, PreviousKey
    //    snapshots, SwitchOutfitHelper's no-change test, laundry, age-up outfit compares, career uniform cleanup, mannequins,
    //    the outfit picker). It is hooked: for a waiting Sim it answers the waiting key, what EA's native would answer. The
    //    table is copied on write, so a read on the autonomy worker thread (that method is in AutShadow's closure list) sees a
    //    whole table.
    //  - No script code waits on a non-delayed set: it has no ready callback (only the delayed forms have one; the
    //    outfit-ready waits wait on delayed swaps).
    //  - Portraits do not read the world model: the thumbnail helper puts the description's Everyday or Career outfit on its
    //    own copy.
    //  - The rig, its slots and whatever hangs in them do not change with an outfit of the same age and species.
    //  - The save: the save hook (DisposeSaveHook) applies every waiting outfit before OptionsModel.SaveGame writes, so the
    //    world file holds EA's model state; travel and every non-live mode (build/buy, CAS, edit town) flush through the
    //    sweep too, and no set is held back in them.
    // Not deferred (EA's call runs at once): household / selected / unknown-scope Sims (IsPlayerSim), seen townies, not live
    // mode, saving or travelling; a Sim's first set after SimDescription.Instantiate (the instantiate's own model may still be
    // loading); while a delayed swap of that Sim is open (CASUtilsSetOutfitInGameObjectDelayed / SetModelInGameObjectDelayed
    // until CompleteDelayedModelSwap or AbortDelayedModelSwap); a call not from a Sim's outfit path (mPendingOutfitInfo empty
    // and not RefreshCurrentOutfit: OccultRobot setup, thumbnails, pie-menu heads); a Sim within kOdMarginM of being seen; and
    // unless both the new key's and the shown key's categories are in kOdSafe (Everyday, Formalwear, Sleepwear, Swimwear,
    // Athletic, Career, Outerwear). Naked is out both ways: AutoEnableCensor follows the managed category, so a late model
    // could show a body with no censor. Singed, MartialArts, AuxCareer, Makeover, SkinnyDippingTowel, Racing, Jumping, Bridle,
    // Massage, ChildImagination, Special, Supernatural and None are out too.
    // Delayed sets (the spin path: SwitchToOutfitWithSpin, SwitchToOutfitWithHiding, SwitchToOutfitDelayed, CAS previews) are
    // never deferred; they wait for their own ready callback.
    // When the waiting outfit goes on: the sweep task (every kOdTickMs real time, kOdCheck Sims checked, kOdApply applied a
    // tick; Simulator.Sleep(0) runs while paused too) when the Sim is seen, selected or no longer a townie, or when the switch
    // is off / not live / travelling / saving; within kOdMarginM of being seen (a camera pan or a mass change, such as a
    // weather turn putting many unseen Sims in Outerwear, must not show old outfits); at once before a delayed set or delayed
    // model of that Sim (EA would have built it before that call, so the delayed swap and an abort of it fall back to EA's
    // state); and before a save. A newer set replaces the entry (deferred again) or ends it (EA's own call ran); a
    // non-delayed model set ends it (a whole new model). A Sim gone is dropped. A reset keeps the entry: EA's model would
    // still show that outfit. A Sim the camera jumps to (portrait click) can show the old outfit for one sweep tick plus the
    // build, then changes as EA's WithoutSpin changes do (an instant swap).
    // Cleanup: the sweep loop ends when nothing waits, sleeps every pass, and applies at most kOdApply a tick; the open and
    // fresh tables are pruned of gone Sims at 2048; no retry of a failed apply (counted, entry gone); the round robin moves
    // on by the Sims checked, not by kOdCheck (a tick that stops early at its apply cap must not skip Sims).
    // Counters kOd+0 .. kOd+18 (slots 2570-2588): see the block in Fixes.cs.
    public static partial class Fixes
    {
        const int kOd = 1981;
        const int kOdDeferred = kOd, kOdReplaced = kOd + 1, kOdAppSeen = kOd + 2, kOdAppSel = kOd + 3, kOdAppDelayed = kOd + 4,
            kOdAppFlush = kOd + 5, kOdGone = kOd + 6, kOdEaSet = kOd + 7, kOdModel = kOd + 8, kOdWaiting = kOd + 9, kOdMost = kOd + 10,
            kOdNotFresh = kOd + 11, kOdNotOpen = kOd + 12, kOdNotCat = kOd + 13, kOdThrew = kOd + 14, kOdNotNear = kOd + 15,
            kOdAppNear = kOd + 16, kOdWorst = kOd + 17, kOdLate = kOd + 18;

        // a tick every 125 ms checks up to 128 waiting Sims and applies up to 12 (seen ones first), so a seen one goes on
        // within 250 ms
        const int kOdTickMs = 125, kOdCheck = 128, kOdApply = 12, kOdApplyFlush = 12, kOdPrune = 2048, kOdLateMs = 250;
        const OutfitCategories kOdSafe = OutfitCategories.Everyday | OutfitCategories.Formalwear | OutfitCategories.Sleepwear
            | OutfitCategories.Swimwear | OutfitCategories.Athletic | OutfitCategories.Career | OutfitCategories.Outerwear;

        sealed class OdEntry
        {
            public readonly ResourceKey Key, Shown;  // asked for / what the native model still shows
            public readonly bool Body;               // EA called RefreshBodyShape right after its set (WithoutSpin path)
            public OdEntry(ResourceKey key, ResourceKey shown, bool body) { Key = key; Shown = shown; Body = body; }
        }

        static bool sOdHooksOk, sOdOurs, sOdRefresh;
        static System.Reflection.FieldInfo sOdPendingField;
        static Dictionary<ulong, OdEntry> sOdPending = new Dictionary<ulong, OdEntry>();  // copy on write: read on the worker too
        static readonly Dictionary<ulong, bool> sOdOpen = new Dictionary<ulong, bool>(), sOdFresh = new Dictionary<ulong, bool>();
        static readonly List<ulong> sOdKeys = new List<ulong>(), sOdNearKeys = new List<ulong>();
        static readonly Dictionary<ulong, int> sOdChecked = new Dictionary<ulong, int>();  // last TickCount a waiting Sim was unseen and outside the margin
        static ObjectGuid sOdTask = ObjectGuid.InvalidObjectGuid;
        static int sOdLastTick, sOdRr;

        static void ApplyUnseenOutfitDefer()
        {
            Patch(typeof(ScriptCore.CASUtils), "CASUtilsSetOutfitInGameObject", typeof(OdIcHook), "SetHook", "SetOrig");
            Patch(typeof(ScriptCore.CASUtils), "CASUtilsSetOutfitInGameObjectDelayed", typeof(OdIcHook), "SetDHook", "SetDOrig");
            Patch(typeof(ScriptCore.CASUtils), "CASUtilsSetModelInGameObject", typeof(OdIcHook), "ModelHook", "ModelOrig");
            Patch(typeof(ScriptCore.CASUtils), "CASUtilsSetModelInGameObjectDelayed", typeof(OdIcHook), "ModelDHook", "ModelDOrig");
            Patch(typeof(Sim), "RefreshCurrentOutfit", new Type[] { typeof(bool) }, typeof(OdSimHook), "RefreshHook", "RefreshOrig");
            Patch(typeof(SimDescription), "Instantiate", new Type[] { typeof(Vector3), typeof(ResourceKey), typeof(bool), typeof(bool) }, typeof(OdDescHook), "InstHook", "InstOrig");
            // the read-back and the delayed-swap end both ways: all three or no deferring
            bool g = Patch(typeof(ScriptCore.CASUtils), "CASUtilsGetOutfitInGameObject", null, typeof(OdGetHook), "GetHook", "GetOrig");
            bool d = Patch(typeof(ScriptCore.CASUtils), "CASUtilsCompleteDelayedModelSwap", null, typeof(OdGetHook), "DoneHook", "DoneOrig");
            bool a = Patch(typeof(ScriptCore.CASUtils), "CASUtilsAbortDelayedModelSwap", null, typeof(OdGetHook), "AbortHook", "AbortOrig");
            sOdHooksOk = g && d && a;
        }

        public class OdIcHook
        {
            public void SetHook(ResourceKey resKey, ObjectGuid scriptHandle)
            {
                if (!sOdOurs && OdSet(resKey, scriptHandle)) return;  // the native call waits until the Sim is seen
                SetOrig(resKey, scriptHandle);
            }
            public void SetOrig(ResourceKey resKey, ObjectGuid scriptHandle) { } // receives ScriptCore.CASUtils.CASUtilsSetOutfitInGameObject's original header
            public void SetDHook(ResourceKey resKey, ObjectGuid scriptHandle, Delegate finishedCallback)
            {
                OdBeforeDelayed(scriptHandle);
                SetDOrig(resKey, scriptHandle, finishedCallback);
            }
            public void SetDOrig(ResourceKey resKey, ObjectGuid scriptHandle, Delegate finishedCallback) { } // receives ...SetOutfitInGameObjectDelayed's original header
            public void ModelHook(uint modelHandle, ObjectGuid scriptHandle)
            {
                OdDrop(scriptHandle.Value, kOdModel);  // a whole new model replaces what was waiting
                ModelOrig(modelHandle, scriptHandle);
            }
            public void ModelOrig(uint modelHandle, ObjectGuid scriptHandle) { } // receives ...SetModelInGameObject's original header
            public void ModelDHook(uint modelHandle, ObjectGuid scriptHandle, Delegate finishedCallback)
            {
                OdBeforeDelayed(scriptHandle);
                ModelDOrig(modelHandle, scriptHandle, finishedCallback);
            }
            public void ModelDOrig(uint modelHandle, ObjectGuid scriptHandle, Delegate finishedCallback) { } // receives ...SetModelInGameObjectDelayed's original header
        }

        public class OdSimHook
        {
            public bool RefreshHook(bool resetIndex)
            {
                if (!ShortcutOn("unseenoutfitdefer")) return RefreshOrig(resetIndex);
                bool was = sOdRefresh;  // RefreshCurrentOutfit never yields, so one static flag is enough
                sOdRefresh = true;
                try { return RefreshOrig(resetIndex); }
                finally { sOdRefresh = was; }
            }
            public bool RefreshOrig(bool resetIndex) { return false; } // receives Sim.RefreshCurrentOutfit's original header
        }

        public class OdDescHook
        {
            public Sim InstHook(Vector3 position, ResourceKey outfitKey, bool addInitialObjects, bool forceAlwaysAnimate)
            {
                Sim r = InstOrig(position, outfitKey, addInitialObjects, forceAlwaysAnimate);
                if (r != null && ShortcutOn("unseenoutfitdefer")) OdFresh(r.ObjectId.Value);  // its first set after this always runs
                return r;
            }
            public Sim InstOrig(Vector3 position, ResourceKey outfitKey, bool addInitialObjects, bool forceAlwaysAnimate) { return null; } // receives SimDescription.Instantiate(Vector3, ResourceKey, bool, bool)'s original header
        }

        public class OdGetHook
        {
            public ResourceKey GetHook(ObjectGuid scriptHandle)
            {
                Dictionary<ulong, OdEntry> d = sOdPending;
                OdEntry e;
                if (d.Count != 0 && d.TryGetValue(scriptHandle.Value, out e)) return e.Key;
                return GetOrig(scriptHandle);
            }
            public ResourceKey GetOrig(ObjectGuid scriptHandle) { return default(ResourceKey); } // receives ScriptCore.CASUtils.CASUtilsGetOutfitInGameObject's original header
            public void DoneHook(ObjectGuid scriptHandle) { sOdOpen.Remove(scriptHandle.Value); DoneOrig(scriptHandle); }
            public void DoneOrig(ObjectGuid scriptHandle) { } // receives ...CASUtilsCompleteDelayedModelSwap's original header
            public void AbortHook(ObjectGuid scriptHandle) { sOdOpen.Remove(scriptHandle.Value); AbortOrig(scriptHandle); }
            public void AbortOrig(ObjectGuid scriptHandle) { } // receives ...CASUtilsAbortDelayedModelSwap's original header
        }

        static void OdPut(ulong id, OdEntry e)
        {
            Dictionary<ulong, OdEntry> d = new Dictionary<ulong, OdEntry>(sOdPending);
            if (e == null) { d.Remove(id); sOdChecked.Remove(id); }
            else { d[id] = e; sOdChecked[id] = System.Environment.TickCount; }  // known unseen and outside the margin now
            sOdPending = d;
            int[] c = PatchCounters();
            c[kOdWaiting] = d.Count;
            if (d.Count > c[kOdMost]) c[kOdMost] = d.Count;
        }

        static void OdDrop(ulong id, int counter)
        {
            if (sOdPending.Count == 0 || !sOdPending.ContainsKey(id)) return;
            OdPut(id, null);
            PatchCounters()[counter]++;
        }

        static void OdFresh(ulong id)
        {
            if (sOdFresh.Count >= kOdPrune) OdPrune(sOdFresh);
            sOdFresh[id] = true;
        }

        static void OdPrune(Dictionary<ulong, bool> t)
        {
            sOdKeys.Clear();
            foreach (ulong k in t.Keys) if (GameObject.GetObject<Sim>(new ObjectGuid(k)) == null) sOdKeys.Add(k);
            foreach (ulong k in sOdKeys) t.Remove(k);
            if (t.Count >= kOdPrune) t.Clear();  // all alive: forget the oldest marks rather than grow (open: a Sim may defer early; fresh: one first set may defer)
        }

        // the set hook: true = skip the native call (deferred)
        static bool OdSet(ResourceKey key, ObjectGuid id)
        {
            bool on = sOdHooksOk && ShortcutOn("unseenoutfitdefer");
            if (!on && sOdPending.Count == 0) return false;
            try
            {
                int[] c = PatchCounters();
                OdEntry old;
                bool had = sOdPending.TryGetValue(id.Value, out old);
                int why = on ? OdRefuse(key, id) : 1;
                if (why != 0)
                {
                    if (why > 1) c[why]++;
                    if (had) OdDrop(id.Value, kOdEaSet);  // EA's own call runs now and replaces the waiting outfit
                    return false;
                }
                ResourceKey shown = had ? old.Shown : CASUtils.GetOutfitInGameObject(id);  // no entry: the hook answers the native key
                if (!had && !OdSafeKey(id, shown)) { c[kOdNotCat]++; return false; }
                OdPut(id.Value, new OdEntry(key, shown, !sOdRefresh));
                c[kOdDeferred]++;
                if (had) c[kOdReplaced]++;
                if (!TaskAlive(sOdTask)) sOdTask = Simulator.AddObject(new LoopTask(OdSweep));
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception) { return false; }
        }

        // 0 = may defer; 1 = no (a seen or household Sim, not counted); else the counter slot of the reason
        static int OdRefuse(ResourceKey key, ObjectGuid id)
        {
            Sim s = GameObject.GetObject<Sim>(id);
            if (s == null) return 1;
            if (!GameStates.IsLiveState || GameStates.IsTravelling || SaveInProgress() || WorldGone()) return 1;
            if (s.HasBeenDestroyed || s == PlumbBob.SelectedActor || IsPlayerSim(s)) return 1;  // household rule: unknown scope counts as household
            if (!sOdRefresh && OdPendingCat(s) == OutfitCategories.None) return kOdNotCat;  // not from a Sim outfit path
            ulong sid = s.ObjectId.Value;
            if (sOdFresh.Remove(sid)) return kOdNotFresh;  // this first set runs; later ones may wait
            if (sOdOpen.ContainsKey(sid)) return kOdNotOpen;
            if (!OdSafeKey(s.ObjectId, key)) return kOdNotCat;
            if (SafeUfWhy(s) != 0) return 1;
            if (OdNear(s)) return kOdNotNear;  // would come into view soon, so build it now while it is still out of view
            return 0;
        }

        static OutfitCategories OdPendingCat(Sim s)
        {
            if (sOdPendingField == null) sOdPendingField = typeof(Sim).GetField("mPendingOutfitInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return sOdPendingField == null ? OutfitCategories.None : ((Sim.OutfitInfo)sOdPendingField.GetValue(s)).Category;
        }

        // margin: ts3native's seen query for a kOdMarginM ball at the Sim instead of the Sim's 3 m one. That query (unseentable.cpp
        // / unseenfb.cpp) adds the radius to the camera reach (max(80 m, 2 x camera height) + r: 80 -> 120 m, 1.5x at the usual
        // zoom) and to the frustum sphere, so a Sim walking in or a camera pan meets the new model built.
        const float kOdMarginM = 40f;
        static bool OdNear(Sim s) { return !UfPosUnseen(s.Position, kOdMarginM); }

        static bool OdSafeKey(ObjectGuid id, ResourceKey key)
        {
            Sim s = GameObject.GetObject<Sim>(id);
            SimDescription sd = s != null ? s.SimDescription : null;
            if (sd == null || key == ResourceKey.kInvalidResourceKey) return false;
            OutfitCategories cat = sd.GetOutfitCategoryFromResKey(key);
            return cat != OutfitCategories.None && (cat & ~kOdSafe) == 0;
        }

        // before a delayed set or delayed model of the Sim: EA built the waiting outfit before this call, so do it now
        static void OdBeforeDelayed(ObjectGuid id)
        {
            if (sOdOurs) return;
            if (sOdHooksOk && ShortcutOn("unseenoutfitdefer"))
            {
                if (sOdOpen.Count >= kOdPrune) OdPrune(sOdOpen);
                sOdOpen[id.Value] = true;
            }
            OdEntry e;
            if (sOdPending.Count == 0 || !sOdPending.TryGetValue(id.Value, out e)) return;
            try { OdApply(id.Value, e, kOdAppDelayed); }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[kOdThrew]++; }
        }

        static void OdApply(ulong id, OdEntry e, int counter)
        {
            int[] c = PatchCounters();
            OdPut(id, null);
            Sim s = GameObject.GetObject<Sim>(new ObjectGuid(id));
            if (s == null || s.HasBeenDestroyed) { c[kOdGone]++; return; }
            sOdOurs = true;
            try { CASUtils.SetOutfitInGameObject(e.Key, s.ObjectId); }
            finally { sOdOurs = false; }
            if (e.Body && s.SimDescription != null) s.SimDescription.RefreshBodyShape();  // EA's order after its own set
            c[counter]++;
        }

        // the save hook (DisposeSpread.cs): every waiting outfit on its Sim before the file is written
        static void OdFlushAll()
        {
            Dictionary<ulong, OdEntry> d = sOdPending;
            if (d.Count == 0) return;
            foreach (KeyValuePair<ulong, OdEntry> kv in d)
            {
                try { OdApply(kv.Key, kv.Value, kOdAppFlush); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[kOdThrew]++; }
            }
        }

        // world quit (OutfitWait.HeldRefsWorldQuit): object ids of the old world
        static void OdWorldQuit()
        {
            if (sOdPending.Count > 0) { PatchCounters()[kOdGone] += sOdPending.Count; sOdPending = new Dictionary<ulong, OdEntry>(); PatchCounters()[kOdWaiting] = 0; }
            sOdChecked.Clear();
            sOdOpen.Clear();
            sOdFresh.Clear();
        }

        static void OdSweep()
        {
            try
            {
                while (sOdPending.Count > 0)
                {
                    if (WorldGone()) { OdWorldQuit(); return; }
                    int now = System.Environment.TickCount;
                    if (unchecked(now - sOdLastTick) >= kOdTickMs)
                    {
                        sOdLastTick = now;
                        OdTick(!ShortcutOn("unseenoutfitdefer") || !GameStates.IsLiveState || GameStates.IsTravelling || SaveInProgress());
                    }
                    Simulator.Sleep(0);
                }
            }
            finally { sOdTask = ObjectGuid.InvalidObjectGuid; }
        }

        // one tick: up to kOdCheck waiting Sims, round robin; apply the ones seen now (and not townies / selected now) first,
        // then the ones within the margin, kOdApply in all.
        static void OdTick(bool flush)
        {
            Dictionary<ulong, OdEntry> d = sOdPending;
            sOdKeys.Clear();
            sOdKeys.AddRange(d.Keys);
            sOdNearKeys.Clear();
            int n = sOdKeys.Count, applied = 0, limit = flush ? kOdApplyFlush : kOdApply, i = 0;
            if (n == 0) return;
            int now = System.Environment.TickCount;
            int[] c = PatchCounters();
            for (; i < n && i < kOdCheck && applied < limit; i++)
            {
                ulong id = sOdKeys[(sOdRr + i) % n];
                OdEntry e;
                if (!d.TryGetValue(id, out e)) continue;
                try
                {
                    Sim s = GameObject.GetObject<Sim>(new ObjectGuid(id));
                    if (s == null || s.HasBeenDestroyed) { OdDrop(id, kOdGone); continue; }
                    int why = flush ? kOdAppFlush : (s == PlumbBob.SelectedActor || IsPlayerSim(s)) ? kOdAppSel : SafeUfWhy(s) != 0 ? kOdAppSeen : 0;
                    if (why == 0)
                    {
                        if (OdNear(s)) sOdNearKeys.Add(id); else sOdChecked[id] = now;
                        continue;
                    }
                    if (why == kOdAppSeen)
                    {
                        int t0, lat = sOdChecked.TryGetValue(id, out t0) ? unchecked(now - t0) : 0;  // upper bound: it was unseen at t0
                        if (lat > c[kOdWorst]) c[kOdWorst] = lat;
                        if (lat > kOdLateMs) c[kOdLate]++;
                    }
                    OdApply(id, e, why);
                    applied++;
                }
                catch (ResetException) { throw; }
                catch (Exception)
                {
                    c[kOdThrew]++;
                    
                    if (sOdPending.ContainsKey(id)) OdPut(id, null);  // no retry: a Sim that throws here would throw every tick
                }
            }
            sOdRr = (sOdRr + i) % n;
            for (int j = 0; j < sOdNearKeys.Count && applied < limit; j++)
            {
                ulong id = sOdNearKeys[j];
                OdEntry e;
                if (!sOdPending.TryGetValue(id, out e)) continue;
                try { OdApply(id, e, kOdAppNear); applied++; }
                catch (ResetException) { throw; }
                catch (Exception)
                {
                    c[kOdThrew]++;
                    
                    if (sOdPending.ContainsKey(id)) OdPut(id, null);
                }
            }
        }
    }
}
