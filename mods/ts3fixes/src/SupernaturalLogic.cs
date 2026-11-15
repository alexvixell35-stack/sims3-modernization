using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // permazombiemoon (kPermaZombieMoon, off by default; docs/571 §8). EA bug fix in Supernatural.
    //
    // [verified, BuffPermaZombie.cs / BuffTransformation.cs / EventTracker.cs] A PermaZombie is meant to be cured at the
    // next new-moon moonrise: BuffInstancePermaZombie.ApplyEffects -> AddMoonListener registers a kMoonRise listener, but
    // only "if (mListener == null)". The buff instance is saved with its mListener field (EventListener is Persistable),
    // while EventTracker is created empty at every world start (PreWorldStartup) and nothing registers the saved listener
    // again; BuffTransformationInstance.OnLoadFixup calls ApplyEffects only when the effects were not applied, and even
    // then AddMoonListener sees the saved, non-null field and does nothing. So after any save/load the Sim stays a zombie
    // past every new moon until cured with an item.
    // Fix: when a PermaZombie buff instance is loaded and kept, the saved (unregistered) listener is dropped and a new one
    // registered through EA's AddMoonListener, before EA's own fixup runs. Other transformation buffs are not touched.
    // Counters [2595] listeners re-registered at load, [2596] errors. Check: SupernaturalLogicChecks.cs.
    //
    // lunacysocials (kLunacySocials, off by default; docs/572 §1) [verified, BuffLunacy.cs / Buff.cs]: Buff objects are one
    // per buff type, shared by every Sim, yet BuffLunacy keeps the random-social alarm handle and the Sim (mActorSim) in
    // its own fields. The full moon gives Lunacy to every Insane / Supernatural Fan / unhappy Sim at once: each addition
    // cancels the previous Sim's alarm (ResetRandomSocialAlarm removes the stored handle) and points mActorSim at the
    // newest Sim, so only the last one does lunatic embraces / slaps / freak-outs; the first Lunacy that ends cancels the
    // remaining alarm for everyone; after a load (the alarm is never persisted) nobody does them. Fix: every Sim with
    // Lunacy gets its own never-persisted alarm (EA's tunables kMinRange..kMaxRange); when it fires, EA's AddRandomSocial
    // runs for that Sim (mActorSim set to it) and EA's own re-arm alarm is replaced by the Sim's. Loaded Sims with Lunacy
    // are re-armed at world load. Counters [2604] per-Sim alarms scheduled, [2605] re-armed at load, [2606] errors.
    // fairyauratimer (kFairyAuraTimer, off by default; docs/572 §2) [verified, OccultFairy.cs FixUpOccult]: on load the fairy's
    // projecting aura (Soothe, Creative or Mind) is removed and added again, and the old timeout / pause / start time are
    // copied back - but only into GetElement(FairyAuraProjectingSoothe) (copy-paste: it should be the re-added guid). A
    // Creative or Mind aura gets its full duration back on every load. Fix: after EA's FixUpOccult, a re-added Creative or
    // Mind aura gets the saved timer values, as EA does for Soothe. Counters [2607] timers restored, [2608] errors.
    public static partial class Fixes
    {
        static readonly FieldInfo sLnActor = typeof(BuffLunacy).GetField("mActorSim", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly Dictionary<ulong, AlarmHandle> sLnAlarms = new Dictionary<ulong, AlarmHandle>();
        static bool sLnHooked;

        static void ApplySupernaturalLogic()
        {
            Patch(typeof(BuffTransformation.BuffTransformationInstance), "OnLoadFixup", typeof(PermaZombieHook), "LoadHook", "LoadOrig");
            if (sLnActor == null) AppliedLog().Add("lunacysocials: BuffLunacy.mActorSim not found, not patched");
            else
            {
                Patch(typeof(BuffLunacy), "OnAddition", typeof(LunacyHook), "AddHook", "AddOrig");
                Patch(typeof(BuffLunacy), "OnRemoval", typeof(LunacyHook), "RemoveHook", "RemoveOrig");
            }
            Patch(typeof(OccultFairy), "FixUpOccult", typeof(FairyAuraHook), "FixHook", "FixOrig");
            if (!sLnHooked)
            {
                sLnHooked = true;
                World.OnWorldQuitEventHandler += delegate { sLnAlarms.Clear(); };
            }
        }

        // ---- lunacysocials ----
        // How many Sims keep a live random-social alarm after n Sims got Lunacy (EA: the shared field holds one).
        internal static int LnAliveAfter(bool on, int n)
        {
            return on ? n : Math.Min(n, 1);
        }

        static void LnCancel(Sim sim)
        {
            AlarmHandle h;
            if (sim == null || !sLnAlarms.TryGetValue(sim.ObjectId.Value, out h)) return;
            sLnAlarms.Remove(sim.ObjectId.Value);
            sim.RemoveAlarm(h);
        }

        static void LnSchedule(BuffLunacy buff, Sim sim)
        {
            LnCancel(sim);
            float t = Sims3.Gameplay.Core.RandomUtil.GetFloat(BuffLunacy.kMinRange, BuffLunacy.kMaxRange);
            sLnAlarms[sim.ObjectId.Value] = sim.AddAlarm(t, TimeUnit.Minutes, delegate { LnFire(buff, sim); }, "Buff Lunacy Social", AlarmType.NeverPersisted);
            PatchCounters()[2604]++;
        }

        // EA's alarm handle field is kept invalid while the fix is on: drop EA's own alarm (its callback acts on the shared
        // mActorSim, which is whoever got Lunacy last).
        static void LnDropEaAlarm(BuffLunacy buff, Sim sim)
        {
            if (buff.mRandomSocialAlarm != AlarmHandle.kInvalidHandle && sim != null) sim.RemoveAlarm(buff.mRandomSocialAlarm);
            buff.mRandomSocialAlarm = AlarmHandle.kInvalidHandle;
        }

        static void LnFire(BuffLunacy buff, Sim sim)
        {
            try
            {
                sLnAlarms.Remove(sim.ObjectId.Value);
                if (!ShortcutOn("lunacysocials") || sim.HasBeenDestroyed || sim.BuffManager == null || !sim.BuffManager.HasElement(BuffNames.Lunacy) || sim.LotCurrent == null) return;
                buff.mRandomSocialAlarm = AlarmHandle.kInvalidHandle;
                sLnActor.SetValue(buff, sim);
                buff.AddRandomSocial();       // EA's social for this Sim; it ends by arming EA's alarm on mActorSim
                LnDropEaAlarm(buff, sim);
                LnSchedule(buff, sim);
            }
            catch (Exception) { PatchCounters()[2606]++; }
        }

        // World load finished (Instantiator): Sims loaded with Lunacy get their alarm (EA's is never persisted).
        public static void LunacySocialsWorldLoad()
        {
            try
            {
                if (!ShortcutOn("lunacysocials")) return;
                foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    BuffInstance bi = sim == null || sim.BuffManager == null ? null : sim.BuffManager.GetElement(BuffNames.Lunacy);
                    BuffLunacy buff = bi == null ? null : bi.mBuff as BuffLunacy;
                    if (buff == null) continue;
                    LnSchedule(buff, sim);
                    PatchCounters()[2605]++;
                }
            }
            catch (Exception) { PatchCounters()[2606]++; }
        }

        public class LunacyHook
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travelReaddition)
            {
                BuffLunacy me = (BuffLunacy)(object)this;
                bool on = false;
                try { on = ShortcutOn("lunacysocials") && bm != null && bm.Actor != null; if (on) me.mRandomSocialAlarm = AlarmHandle.kInvalidHandle; }
                catch (Exception) { PatchCounters()[2606]++; on = false; }
                AddOrig(bm, bi, travelReaddition);   // EA: freak-out, then its (shared) alarm for this Sim
                if (!on) return;
                try { LnDropEaAlarm(me, bm.Actor); LnSchedule(me, bm.Actor); }
                catch (Exception) { PatchCounters()[2606]++; }
            }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travelReaddition) { }

            public void RemoveHook(BuffManager bm, BuffInstance bi)
            {
                BuffLunacy me = (BuffLunacy)(object)this;
                try
                {
                    if (ShortcutOn("lunacysocials") && bm != null && bm.Actor != null)
                    {
                        LnCancel(bm.Actor);
                        me.mRandomSocialAlarm = AlarmHandle.kInvalidHandle;  // EA's removal would otherwise cancel another Sim's alarm
                    }
                }
                catch (Exception) { PatchCounters()[2606]++; }
                RemoveOrig(bm, bi);
            }
            public void RemoveOrig(BuffManager bm, BuffInstance bi) { }
        }

        // ---- fairyauratimer ----
        // Restore the timer into the re-added aura? EA does it for Soothe only.
        internal static bool FaRestore(bool on, BuffNames guid)
        {
            if (guid == BuffNames.FairyAuraProjectingSoothe) return false;  // EA's own copy
            return on && (guid == BuffNames.FairyAuraProjectingCreative || guid == BuffNames.FairyAuraProjectingMind);
        }

        public class FairyAuraHook
        {
            public void FixHook(SimDescription simDescription)
            {
                Sim sim = null;
                BuffInstance old = null;
                try
                {
                    sim = simDescription == null ? null : simDescription.CreatedSim;
                    if (sim != null && sim.BuffManager != null && ShortcutOn("fairyauratimer") && sim.BuffManager.GetElement(BuffNames.FairyAuraProjectingSoothe) == null)
                    {
                        old = sim.BuffManager.GetElement(BuffNames.FairyAuraProjectingCreative);
                        if (old == null) old = sim.BuffManager.GetElement(BuffNames.FairyAuraProjectingMind);
                    }
                }
                catch (Exception) { PatchCounters()[2608]++; old = null; }
                FixOrig(simDescription);
                if (old == null) return;
                try
                {
                    BuffNames guid = (BuffNames)old.BuffGuid;
                    if (!FaRestore(true, guid)) return;
                    BuffInstance now = sim.BuffManager.GetElement(guid);
                    if (now == null || now == old) return;
                    now.mTimeoutCount = old.mTimeoutCount;
                    now.mTimeoutPaused = old.mTimeoutPaused;
                    now.mStartTime = old.mStartTime;
                    PatchCounters()[2607]++;
                }
                catch (Exception) { PatchCounters()[2608]++; }
            }
            public void FixOrig(SimDescription simDescription) { }
        }

        // Decision: re-register the moon listener for a loaded instance? Only a PermaZombie instance the load kept.
        internal static bool PzRearm(bool on, bool isPermaZombie, bool keptByLoad, bool listenerNowRegistered)
        {
            return on && isPermaZombie && keptByLoad && !listenerNowRegistered;
        }

        public class PermaZombieHook
        {
            public bool LoadHook(Sim actor)
            {
                BuffPermaZombie.BuffInstancePermaZombie pz = (object)this as BuffPermaZombie.BuffInstancePermaZombie;
                bool on = false;
                try
                {
                    on = pz != null && ShortcutOn("permazombiemoon");
                    if (on) pz.mListener = null;  // the saved listener object is not registered in this world's EventTracker
                }
                catch (Exception) { PatchCounters()[2596]++; on = false; }
                bool kept = LoadOrig(actor);  // may run ApplyEffects -> AddMoonListener (now registers, the field being null)
                if (!on) return kept;
                try
                {
                    if (PzRearm(true, true, kept, pz.mListener != null))
                    {
                        pz.AddMoonListener();
                        PatchCounters()[2595]++;
                    }
                }
                catch (Exception) { PatchCounters()[2596]++; }
                return kept;
            }
            public bool LoadOrig(Sim actor) { return false; }
        }
    }
}
