using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.Store.Objects;

namespace Ts3Fixes
{
    // buffstate (tuning kBuffState, off by default; docs/557). EA bug fixes, exact for a single Sim, every Sim.
    //
    // A Buff object is shared: BuffManager keeps one per buff guid (BuffDictionary) and every Sim's BuffInstance points
    // at it, so a field on a Buff subclass is one slot for every Sim that has the buff [verified, decompiled]. Six EA buffs
    // keep per-Sim state there; with two Sims holding the buff at once (or, for Downtrodden, one Sim across a save) the
    // second overwrites the first and OnRemoval works on the wrong Sim:
    //   - BuffDowntrodden (Late Night bars) mPushDowntroddenAlarm: a 5-minute repeating AlwaysPersisted alarm that pushes
    //     the hidden BeDowntrodden interaction whenever the Sim's queue is empty. OnRemoval removes the field's handle, so
    //     the first Sim's alarm stays for good (saved with the game) and the second Sim's goes early. After a load the field
    //     is empty (Buff fields are not saved; OnAddition does not rerun) while the persisted alarm is back, so any Sim saved
    //     with the buff keeps the alarm forever once the buff ends.
    //   - VoodooDoll+VoodooCurseBuffStore and BabyDragon+DragonCurseBuffStore mWokeUpListener (kSimWokeUp, Bad Night's
    //     Sleep on waking): the first Sim's listener is never removed (bad sleep every wake for the session), the second
    //     Sim's is removed with the first curse.
    //   - MassageTable+HighFertilityStore: mAlreadyHadTrait is set once and never cleared, mPregoSim keeps only the last
    //     Sim and the kNewBaby listener (any baby in town) is added only while the field is null (it stays set across a
    //     world reload in the same session). So an earlier Sim, any Sim after one who already had the trait, and every
    //     Sim in a later world of the session keep the added Fertility Treatment trait (saved) for good.
    //   - Hypnotizer+HypnotizedBuffStore: QueueChanged += OnAQChanged is never removed and OnAQChanged tests mOwner (the
    //     last hypnotized Sim): every earlier subject's queue change can end the current subject's trance; handlers pile up.
    //   - BabySwing+QueasyBuffStore: OnRemoval uses mOwner (last toddler), so the first toddler keeps crying and its
    //     BuffsChanged handler keeps flagging the last toddler hungry; a toddler that left the swing keeps the handler.
    // With the switch on each Sim gets its own state, the way the code reads: Downtrodden alarms are looked up per Sim in
    // AlarmManager (callback PushDowntrodden on that Sim; the hourly pass also removes the alarm from Sims without the
    // buff, which repairs saves), curse listeners are kept per Sim and handed to EA's OnRemoval for its own Sim, High
    // Fertility keeps a per-Sim had-trait flag and at the next baby (EA's trigger, unchanged) clears every Sim it added
    // the trait to, and the hypnosis / queasy handlers are per-Sim objects removed at OnRemoval. One Sim alone behaves as
    // vanilla in every case except the Downtrodden save case, where vanilla never removes the alarm.
    // Counters: [2428] Downtrodden removals where EA would leave the Sim's own alarm (also off), [2429] Downtrodden alarms
    // removed by the fix, [2430] curse removals where EA's field held another Sim's listener (also off), [2431] curse
    // listeners removed for their own Sim, [2432] Fertility Treatment traits removed per Sim, [2433] hypnosis / queasy
    // shared handlers replaced or removed, [2434] errors (registry fixes-2428). Harness check: BuffStateCheck, repro
    // buff-shared-state.
    public static partial class Fixes
    {
        const ulong kBsHypnotized = 11132721365296056002uL;
        static FieldInfo sBsDownAlarm, sBsVoodooL, sBsDragonL, sBsQueasyOwner, sBsAmIndex, sBsAmTimers, sBsTimerCb;
        static readonly Dictionary<Sim, EventListener> sBsVoodoo = new Dictionary<Sim, EventListener>(), sBsDragon = new Dictionary<Sim, EventListener>();
        static readonly Dictionary<ulong, bool> sBsFertHad = new Dictionary<ulong, bool>();  // SimDescriptionId -> had the trait before the buff
        static EventListener sBsFertListener;
        static readonly Dictionary<Sim, BsSimHandler> sBsHyp = new Dictionary<Sim, BsSimHandler>(), sBsQueasy = new Dictionary<Sim, BsSimHandler>();

        static void ApplyBuffState()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type timer = typeof(AlarmManager).GetNestedType("Timer", BindingFlags.NonPublic);
            sBsAmIndex = typeof(AlarmManager).GetField("mGameObjectIndex", inst);
            sBsAmTimers = typeof(AlarmManager).GetField("mTimers", inst);
            sBsTimerCb = timer == null ? null : timer.GetField("CallBack", inst);
            sBsDownAlarm = typeof(BuffDowntrodden).GetField("mPushDowntroddenAlarm", inst);
            if (sBsAmIndex != null && sBsAmTimers != null && sBsTimerCb != null && sBsDownAlarm != null)
            {
                Patch(typeof(BuffDowntrodden), "OnAddition", typeof(BsDownHook), "AddHook", "AddOrig");
                Patch(typeof(BuffDowntrodden), "OnRemoval", typeof(BsDownHook), "RemoveHook", "RemoveOrig");
            }
            else AppliedLog().Add("buffstate: AlarmManager or BuffDowntrodden members not found, Downtrodden not patched");
            Type voodoo = typeof(VoodooDoll).GetNestedType("VoodooCurseBuffStore", BindingFlags.Public | BindingFlags.NonPublic);
            Type dragon = typeof(BabyDragon).GetNestedType("DragonCurseBuffStore", BindingFlags.Public | BindingFlags.NonPublic);
            sBsVoodooL = voodoo == null ? null : voodoo.GetField("mWokeUpListener", inst);
            sBsDragonL = dragon == null ? null : dragon.GetField("mWokeUpListener", inst);
            if (sBsVoodooL != null)
            {
                Patch(typeof(VoodooDoll).GetNestedType("VoodooCurseBuffStore", BindingFlags.Public | BindingFlags.NonPublic), "OnAddition", typeof(BsVoodooHook), "AddHook", "AddOrig");
                Patch(typeof(VoodooDoll).GetNestedType("VoodooCurseBuffStore", BindingFlags.Public | BindingFlags.NonPublic), "OnRemoval", typeof(BsVoodooHook), "RemoveHook", "RemoveOrig");
            }
            else AppliedLog().Add("buffstate: VoodooCurseBuffStore.mWokeUpListener not found, not patched");
            if (sBsDragonL != null)
            {
                Patch(typeof(BabyDragon).GetNestedType("DragonCurseBuffStore", BindingFlags.Public | BindingFlags.NonPublic), "OnAddition", typeof(BsDragonHook), "AddHook", "AddOrig");
                Patch(typeof(BabyDragon).GetNestedType("DragonCurseBuffStore", BindingFlags.Public | BindingFlags.NonPublic), "OnRemoval", typeof(BsDragonHook), "RemoveHook", "RemoveOrig");
            }
            else AppliedLog().Add("buffstate: DragonCurseBuffStore.mWokeUpListener not found, not patched");
            Patch(typeof(MassageTable).GetNestedType("HighFertilityStore", BindingFlags.Public | BindingFlags.NonPublic), "OnAddition", typeof(BsFertHook), "AddHook", "AddOrig");
            Patch(typeof(MassageTable).GetNestedType("HighFertilityStore", BindingFlags.Public | BindingFlags.NonPublic), "OnNewBaby", typeof(BsFertHook), "BabyHook", "BabyOrig");
            Patch(typeof(Hypnotizer).GetNestedType("HypnotizedBuffStore", BindingFlags.Public | BindingFlags.NonPublic), "OnAddition", typeof(BsHypHook), "AddHook", "AddOrig");
            Patch(typeof(Hypnotizer).GetNestedType("HypnotizedBuffStore", BindingFlags.Public | BindingFlags.NonPublic), "OnRemoval", typeof(BsHypHook), "RemoveHook", "RemoveOrig");
            Type queasy = typeof(BabySwing).GetNestedType("QueasyBuffStore", BindingFlags.Public | BindingFlags.NonPublic);
            sBsQueasyOwner = queasy == null ? null : queasy.GetField("mOwner", inst);
            if (sBsQueasyOwner != null)
            {
                Patch(typeof(BabySwing).GetNestedType("QueasyBuffStore", BindingFlags.Public | BindingFlags.NonPublic), "OnAddition", typeof(BsQueasyHook), "AddHook", "AddOrig");
                Patch(typeof(BabySwing).GetNestedType("QueasyBuffStore", BindingFlags.Public | BindingFlags.NonPublic), "OnRemoval", typeof(BsQueasyHook), "RemoveHook", "RemoveOrig");
            }
            else AppliedLog().Add("buffstate: QueasyBuffStore.mOwner not found, not patched");
        }

        // World load: per-Sim tables of the last world are stale (listeners and handlers died with it).
        public static void BuffStateWorldLoad()
        {
            sBsVoodoo.Clear(); sBsDragon.Clear(); sBsHyp.Clear(); sBsQueasy.Clear(); sBsFertHad.Clear();
            sBsFertListener = null;
        }

        // ---- Downtrodden ----

        // The PushDowntrodden alarms of this Sim in the global alarm manager (owner index, callback on that Sim).
        static List<AlarmHandle> BsDownAlarms(Sim sim)
        {
            List<AlarmHandle> res = new List<AlarmHandle>();
            AlarmManager am = AlarmManager.Global;
            if (am == null || sim == null || sBsAmIndex == null) return res;
            IDictionary idx = sBsAmIndex.GetValue(am) as IDictionary, timers = sBsAmTimers.GetValue(am) as IDictionary;
            if (idx == null || timers == null || !idx.Contains(sim)) return res;
            IList hs = idx[sim] as IList;
            if (hs == null) return res;
            foreach (object h in new ArrayList(hs))
            {
                IList ts = timers.Contains(h) ? timers[h] as IList : null;
                if (ts == null) continue;
                foreach (object t in ts)
                {
                    Delegate cb = sBsTimerCb.GetValue(t) as Delegate;
                    if (cb != null && ReferenceEquals(cb.Target, sim) && cb.Method.Name == "PushDowntrodden") { res.Add((AlarmHandle)h); break; }
                }
            }
            return res;
        }

        public class BsDownHook
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travelReaddition)
            {
                try
                {
                    // a Sim that still has an alarm (saved with the game, or EA's leak) would get a second one
                    if (ShortcutOn("buffstate") && bm != null && bm.Actor != null)
                        foreach (AlarmHandle h in BsDownAlarms(bm.Actor)) { bm.Actor.RemoveAlarm(h); PatchCounters()[2429]++; }
                }
                catch (Exception) { PatchCounters()[2434]++; }
                AddOrig(bm, bi, travelReaddition);
            }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travelReaddition) { } // receives BuffDowntrodden.OnAddition's original body

            public void RemoveHook(BuffManager bm, BuffInstance bi)
            {
                try
                {
                    Sim a = bm == null ? null : bm.Actor;
                    List<AlarmHandle> own = BsDownAlarms(a);
                    AlarmHandle field = (AlarmHandle)sBsDownAlarm.GetValue(this);
                    if (own.Count > 0 && !(own.Count == 1 && own[0] == field)) PatchCounters()[2428]++;
                    if (ShortcutOn("buffstate") && a != null)
                    {
                        // EA's body removes the field's handle: give it this Sim's own alarm, remove any extra one here
                        sBsDownAlarm.SetValue(this, own.Count > 0 ? own[0] : AlarmHandle.kInvalidHandle);
                        for (int i = 1; i < own.Count; i++) { a.RemoveAlarm(own[i]); PatchCounters()[2429]++; }
                        if (own.Count > 0 && own[0] != field) PatchCounters()[2429]++;
                    }
                }
                catch (Exception) { PatchCounters()[2434]++; }
                RemoveOrig(bm, bi);
            }
            public void RemoveOrig(BuffManager bm, BuffInstance bi) { } // receives BuffDowntrodden.OnRemoval's original body
        }

        // Hourly (ErrorTrapSims' alarm): Sims with a PushDowntrodden alarm and no Downtrodden buff (EA's leak, or a save
        // made while the buff was up) lose the alarm. Vanilla never removes it; it would push BeDowntrodden for good.
        static void BuffStatePass()
        {
            if (!ShortcutOn("buffstate") || sBsAmIndex == null) return;
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                try
                {
                    if (s == null || s.HasBeenDestroyed || s.BuffManager == null || s.BuffManager.HasElement(BuffNames.Downtrodden)) continue;
                    foreach (AlarmHandle h in BsDownAlarms(s)) { s.RemoveAlarm(h); PatchCounters()[2429]++; }
                }
                catch (Exception) { PatchCounters()[2434]++; }
            }
        }

        // ---- Voodoo / dragon curse (same body, own field) ----

        static void BsCurseAdd(object buff, FieldInfo f, Dictionary<Sim, EventListener> own, BuffManager bm)
        {
            Sim a = bm == null ? null : bm.Actor;
            EventListener old;
            if (a != null && own.TryGetValue(a, out old)) { EventTracker.RemoveListener(old); own.Remove(a); PatchCounters()[2431]++; }  // a re-add (travel) would leave the first
            if (a != null) own[a] = f.GetValue(buff) as EventListener;
        }

        static void BsCurseRemove(object buff, FieldInfo f, Dictionary<Sim, EventListener> own, BuffManager bm)
        {
            Sim a = bm == null ? null : bm.Actor;
            EventListener l = f.GetValue(buff) as EventListener, mine;
            if (l != null && a != null && !ReferenceEquals(l.ScriptObject, a)) PatchCounters()[2430]++;
            if (!ShortcutOn("buffstate") || a == null) return;
            if (own.TryGetValue(a, out mine)) { own.Remove(a); if (!ReferenceEquals(mine, l)) PatchCounters()[2431]++; }
            f.SetValue(buff, mine);  // EA's body removes this Sim's own listener (none after a load: nothing to remove)
        }

        public class BsVoodooHook
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travelReaddition)
            {
                AddOrig(bm, bi, travelReaddition);
                try { if (ShortcutOn("buffstate")) BsCurseAdd(this, sBsVoodooL, sBsVoodoo, bm); } catch (Exception) { PatchCounters()[2434]++; }
            }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travelReaddition) { } // receives VoodooCurseBuffStore.OnAddition's original body
            public void RemoveHook(BuffManager bm, BuffInstance bi)
            {
                try { BsCurseRemove(this, sBsVoodooL, sBsVoodoo, bm); } catch (Exception) { PatchCounters()[2434]++; }
                RemoveOrig(bm, bi);
            }
            public void RemoveOrig(BuffManager bm, BuffInstance bi) { } // receives VoodooCurseBuffStore.OnRemoval's original body
        }

        public class BsDragonHook
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travelReaddition)
            {
                AddOrig(bm, bi, travelReaddition);
                try { if (ShortcutOn("buffstate")) BsCurseAdd(this, sBsDragonL, sBsDragon, bm); } catch (Exception) { PatchCounters()[2434]++; }
            }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travelReaddition) { } // receives DragonCurseBuffStore.OnAddition's original body
            public void RemoveHook(BuffManager bm, BuffInstance bi)
            {
                try { BsCurseRemove(this, sBsDragonL, sBsDragon, bm); } catch (Exception) { PatchCounters()[2434]++; }
                RemoveOrig(bm, bi);
            }
            public void RemoveOrig(BuffManager bm, BuffInstance bi) { } // receives DragonCurseBuffStore.OnRemoval's original body
        }

        // ---- High Fertility ----

        public class BsFertHook
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travelReaddition)
            {
                bool on = false, had = false;
                SimDescription d = null;
                try
                {
                    on = ShortcutOn("buffstate");
                    d = bm == null || bm.Actor == null ? null : bm.Actor.SimDescription;
                    if (on && d != null) had = d.TraitManager != null && d.TraitManager.HasElement(TraitNames.FertilityTreatment);
                }
                catch (Exception) { PatchCounters()[2434]++; on = false; }
                AddOrig(bm, bi, travelReaddition);  // EA adds the trait when missing (and its own listener when its field is null)
                if (!on || d == null) return;
                try
                {
                    // a Sim already tracked keeps its first answer: the trait it has now is the one the buff added
                    if (!sBsFertHad.ContainsKey(d.SimDescriptionId)) sBsFertHad[d.SimDescriptionId] = had;
                    if (sBsFertListener == null) sBsFertListener = EventTracker.AddListener(EventTypeId.kNewBaby, BsFertNewBaby);
                }
                catch (Exception) { PatchCounters()[2434]++; }
            }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travelReaddition) { } // receives HighFertilityStore.OnAddition's original body

            // EA's own listener: with the switch on our listener does the work (all tracked Sims), so EA's only stops.
            public ListenerAction BabyHook(Event e)
            {
                bool on = false;
                try { on = ShortcutOn("buffstate") && sBsFertListener != null; } catch (Exception) { PatchCounters()[2434]++; }
                if (!on) return BabyOrig(e);
                try { FieldInfo f = GetType().GetField("mNewBabyEventListener"); if (f != null) f.SetValue(this, null); } catch (Exception) { PatchCounters()[2434]++; }
                return ListenerAction.Remove;
            }
            public ListenerAction BabyOrig(Event e) { return ListenerAction.Keep; } // receives HighFertilityStore.OnNewBaby's original body
        }

        // The next new baby (EA's trigger: any baby, unchanged) ends the treatment of every Sim the buff gave the trait to,
        // not only of the last one.
        static ListenerAction BsFertNewBaby(Event e)
        {
            try
            {
                foreach (KeyValuePair<ulong, bool> kv in new List<KeyValuePair<ulong, bool>>(sBsFertHad))
                {
                    if (kv.Value) continue;
                    SimDescription d = SimDescription.Find(kv.Key);
                    if (d != null && d.TraitManager != null && d.TraitManager.HasElement(TraitNames.FertilityTreatment))
                    {
                        d.TraitManager.RemoveElement(TraitNames.FertilityTreatment);
                        PatchCounters()[2432]++;
                    }
                }
            }
            catch (Exception) { PatchCounters()[2434]++; }
            sBsFertHad.Clear();
            sBsFertListener = null;
            return ListenerAction.Remove;
        }

        // ---- Hypnotized / Queasy: one handler object per Sim ----

        public class BsSimHandler
        {
            readonly Sim mSim;
            public BsSimHandler(Sim sim) { mSim = sim; }

            // HypnotizedBuffStore.OnAQChanged for this Sim (EA tests the last hypnotized Sim)
            public void Hypnotized()
            {
                Sim s = mSim;
                if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null) return;
                InteractionInstance head = s.InteractionQueue.GetHeadInteraction();
                string n = head == null ? null : head.GetType().Name;
                if (n != "BeHypnotizedMainLoop" && n != "HypnotizeSelf") s.BuffManager.RemoveElement(kBsHypnotized);
            }

            // QueasyBuffStore.OnBuffsChanged for this Sim (EA flags the last queasy toddler)
            public void Queasy()
            {
                Sim s = mSim;
                if (s != null && !s.HasBeenDestroyed && s.IdleManager != null && (s.IdleManager.cryingCauses & IdleManager.CryingReasons.Hungry) == 0)
                    s.IdleManager.cryingCauses |= IdleManager.CryingReasons.Hungry;
            }
        }

        public class BsHypHook
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travelReaddition)
            {
                AddOrig(bm, bi, travelReaddition);
                try
                {
                    Sim a = bm == null ? null : bm.Actor;
                    if (!ShortcutOn("buffstate") || a == null || a.InteractionQueue == null) return;
                    Hypnotizer.HypnotizedBuffStore buff = (object)this as Hypnotizer.HypnotizedBuffStore;
                    a.InteractionQueue.QueueChanged -= buff.OnAQChanged;  // the copy EA just added
                    BsSimHandler h;
                    if (sBsHyp.TryGetValue(a, out h)) a.InteractionQueue.QueueChanged -= h.Hypnotized;
                    h = new BsSimHandler(a);
                    sBsHyp[a] = h;
                    a.InteractionQueue.QueueChanged += h.Hypnotized;
                    PatchCounters()[2433]++;
                }
                catch (Exception) { PatchCounters()[2434]++; }
            }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travelReaddition) { } // receives HypnotizedBuffStore.OnAddition's original body

            public void RemoveHook(BuffManager bm, BuffInstance bi)
            {
                try
                {
                    Sim a = bm == null ? null : bm.Actor;
                    if (ShortcutOn("buffstate") && a != null && a.InteractionQueue != null)
                    {
                        BsSimHandler h;
                        if (sBsHyp.TryGetValue(a, out h)) { a.InteractionQueue.QueueChanged -= h.Hypnotized; sBsHyp.Remove(a); }
                        // copies EA left on this Sim's queue while the switch was off (one per earlier hypnosis)
                        Hypnotizer.HypnotizedBuffStore buff = (object)this as Hypnotizer.HypnotizedBuffStore;
                        for (int i = 0; i < 64 && BsHasHandler(a.InteractionQueue, "QueueChanged", buff); i++) { a.InteractionQueue.QueueChanged -= buff.OnAQChanged; PatchCounters()[2433]++; }
                    }
                }
                catch (Exception) { PatchCounters()[2434]++; }
                RemoveOrig(bm, bi);
            }
            public void RemoveOrig(BuffManager bm, BuffInstance bi) { } // receives HypnotizedBuffStore.OnRemoval's original body
        }

        // True when the event field `name` of owner has a handler whose target is `target`.
        static bool BsHasHandler(object owner, string name, object target)
        {
            FieldInfo f = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Delegate d = f == null ? null : f.GetValue(owner) as Delegate;
            if (d == null) return false;
            foreach (Delegate x in d.GetInvocationList()) if (ReferenceEquals(x.Target, target)) return true;
            return false;
        }

        public class BsQueasyHook
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travelReaddition)
            {
                AddOrig(bm, bi, travelReaddition);
                try
                {
                    Sim a = bm == null ? null : bm.Actor;
                    if (!ShortcutOn("buffstate") || a == null || a.BuffManager == null || !BsHasHandler(a.BuffManager, "BuffsChanged", this)) return;
                    BabySwing.QueasyBuffStore buff = (object)this as BabySwing.QueasyBuffStore;
                    a.BuffManager.BuffsChanged -= buff.OnBuffsChanged;  // the copy EA just added
                    BsSimHandler h;
                    if (sBsQueasy.TryGetValue(a, out h)) a.BuffManager.BuffsChanged -= h.Queasy;
                    h = new BsSimHandler(a);
                    sBsQueasy[a] = h;
                    a.BuffManager.BuffsChanged += h.Queasy;
                    PatchCounters()[2433]++;
                }
                catch (Exception) { PatchCounters()[2434]++; }
            }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travelReaddition) { } // receives QueasyBuffStore.OnAddition's original body

            public void RemoveHook(BuffManager bm, BuffInstance bi)
            {
                try
                {
                    Sim a = bm == null ? null : bm.Actor;
                    if (ShortcutOn("buffstate") && a != null)
                    {
                        BsSimHandler h;
                        if (a.BuffManager != null && sBsQueasy.TryGetValue(a, out h)) { a.BuffManager.BuffsChanged -= h.Queasy; sBsQueasy.Remove(a); }
                        sBsQueasyOwner.SetValue(this, a);  // EA's body stops the crying of mOwner: make it this toddler
                    }
                }
                catch (Exception) { PatchCounters()[2434]++; }
                RemoveOrig(bm, bi);
            }
            public void RemoveOrig(BuffManager bm, BuffInstance bi) { } // receives QueasyBuffStore.OnRemoval's original body
        }
    }
}
