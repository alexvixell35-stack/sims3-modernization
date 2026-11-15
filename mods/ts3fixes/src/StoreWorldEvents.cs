using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.SimIFace;
using Sims3.Store.Objects;

namespace Ts3Fixes
{
    // storeworldevents (tuning kStoreWorldEvents, off by default; docs/557 §4.2, docs/192 #2). EA leak fix, no gameplay change.
    //
    // Store objects subscribe instance handlers to static events in OnStartup and never unsubscribe [verified, decompiled]:
    // DeepFryerVegas and IceCreamMaker add OnPlacedInWorldHandler to World.OnObjectPlacedInLotEventHandler and
    // OnWorldQuitEventHandler to World.OnWorldQuitEventHandler, VoodooDoll and ChocolateFountain the placed-in-lot one,
    // WalkerClassic LoadSaveManager.ObjectGroupSaving; their Dispose / Destroy remove none. These are static fields of
    // SimIFace (World.sOnWorldQuitEventHandler, World.sOnObjectPlacedInLotEventHandler, the LoadSaveManager singleton's
    // ObjectGroupSaving) that no world load resets, and OnStartup runs again for every such object on every load. So each
    // deleted object and every object of an earlier world in the same session stays reachable from the static list (with
    // its lot and what it holds), and every placement or save runs one more dead handler per object per load.
    // The pass (world load finished, then hourly) removes from those three lists every handler whose target is a game
    // object that has been destroyed (no simulator proxy). Live objects keep their handlers, so nothing changes in play.
    // Counters: [2435] dead handlers seen (also off), [2436] removed, [2437] errors (registry fixes-2435).
    // Harness check: StoreWorldEventsCheck, repro store-world-events.
    public static partial class Fixes
    {
        const BindingFlags kSweStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static FieldInfo sSweQuit, sSwePlaced, sSweLsm, sSweSaving;

        static void ApplyStoreWorldEvents()
        {
            sSweQuit = typeof(World).GetField("sOnWorldQuitEventHandler", kSweStatic);
            sSwePlaced = typeof(World).GetField("sOnObjectPlacedInLotEventHandler", kSweStatic);
            sSweLsm = typeof(LoadSaveManager).GetField("gLoadSaveManager", kSweStatic);
            if (sSweQuit == null || sSwePlaced == null) AppliedLog().Add("storeworldevents: World event fields not found, World lists not swept");
        }

        // World load and hourly (ErrorTrapSims' alarm).
        public static void StoreWorldEventsPass()
        {
            bool on = ShortcutOn("storeworldevents");
            try
            {
                SweSweep(sSweQuit, null, on);
                SweSweep(sSwePlaced, null, on);
                object lsm = sSweLsm == null ? null : sSweLsm.GetValue(null);
                if (lsm != null)
                {
                    if (sSweSaving == null) sSweSaving = FieldUp(lsm.GetType(), "ObjectGroupSaving", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    SweSweep(sSweSaving, lsm, on);
                }
            }
            catch (Exception) { PatchCounters()[2437]++; }
        }

        static bool SweDead(Delegate d)
        {
            Sims3.Gameplay.Abstracts.ScriptObject so = d.Target as Sims3.Gameplay.Abstracts.ScriptObject;
            return so != null && so.HasBeenDestroyed;
        }

        // Removes dead handlers from the delegate in field f (owner null for a static field); returns the dead count.
        static int SweSweep(FieldInfo f, object owner, bool fix)
        {
            Delegate ev = f == null ? null : f.GetValue(owner) as Delegate;
            if (ev == null) return 0;
            List<Delegate> keep = new List<Delegate>();
            int dead = 0;
            foreach (Delegate d in ev.GetInvocationList()) { if (SweDead(d)) dead++; else keep.Add(d); }
            if (dead == 0) return 0;
            PatchCounters()[2435] += dead;
            if (!fix) return dead;
            f.SetValue(owner, keep.Count == 0 ? null : Delegate.Combine(keep.ToArray()));
            PatchCounters()[2436] += dead;
            return dead;
        }
    }
}
