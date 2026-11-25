using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Stair wait deadline and Sim.Dispose description link (docs/612, s82). Both default off; EA bug fixes as EA wrote the
    // code next to each bug.
    public static partial class Fixes
    {
        // ---- docs/612 §2 stairwaitend -------------------------------------------------------------------------------------
        // [verified, ilspycmd Stairs+StairsPortalComponent::OnPortalStart] After Route.StartStairTraversal succeeds, the Sim's
        // task registers an ActiveUserEntry in mSleepingTasks and loops Sleep(10) until mbSleeping is false. Only
        // HandleStairsCompleted (the native stair callback) and WakeAll (OnLoad) clear it: the loop has no deadline. The method
        // stores SimClock.ElapsedTime(Minutes) in a local (V_6) and never reads it again: the start of a deadline EA did not
        // finish. Its sibling, ElevatorPortalComponent.OnPortalStart (same ActiveUserEntry/mbSleeping design), ends its wait
        // after 30 sim-minutes with SetObjectToReset and returns false (IL: ElapsedTime(Minutes, mFailsafeTime), ldc.r4 30).
        // Fix as EA intended: with the switch on, each stair wait arms one 30 sim-minute alarm; if the Sim is still in that
        // wait when it fires, the entry ends the way HandleStairsCompleted(false) ends it (mbResult false, mbSleeping false)
        // and the Sim gets EA's elevator failsafe (SetObjectToReset). The alarm goes away when OnPortalStart returns.
        // Household rule: household, selected, selectable and unknown-scope Sims keep EA's wait (HouseholdExempt).
        // Counters: [3446] waits ended at the deadline, [3447] waits past the deadline with the switch turned off since
        // arming, [3448] waits past the deadline kept by the household rule, [3449] errors.
        const float kStairWaitEndMinutes = 30f;   // EA's elevator portal failsafe (ElevatorPortalComponent.OnPortalStart)
        static readonly FieldInfo sSweTasks = typeof(Stairs.StairsPortalComponent).GetField("mSleepingTasks", kInstAll);
        static readonly Type sSweEntryType = typeof(Stairs.StairsPortalComponent).GetNestedType("ActiveUserEntry", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sSweSleeping = sSweEntryType == null ? null : sSweEntryType.GetField("mbSleeping", kInstAll);
        static readonly FieldInfo sSweResult = sSweEntryType == null ? null : sSweEntryType.GetField("mbResult", kInstAll);
        static string sSweMissing = "";

        static void ApplyStairWaitEnd()
        {
            sSweMissing = ((sSweTasks == null ? "StairsPortalComponent.mSleepingTasks " : "")
                + (sSweSleeping == null || sSweResult == null ? "ActiveUserEntry.mbSleeping/mbResult" : "")).Trim();
            if (sSweMissing.Length > 0) { AppliedLog().Add("stairwaitend: not found: " + sSweMissing); return; }
            Patch(typeof(Stairs.StairsPortalComponent), "OnPortalStart", new Type[] { typeof(Sim) }, typeof(SwePortalHook), "StartHook", "StartOrig");
        }

        // Pure: end this stair wait now? (EA: never.)
        internal static bool SweEnd(bool on, float waitedMinutes, bool sleeping, bool householdExempt)
        {
            return on && sleeping && !householdExempt && waitedMinutes >= kStairWaitEndMinutes;
        }

        // The Sim's live entry in this stair's wait list (null when it is not waiting there).
        static object SweEntry(Stairs.StairsPortalComponent pc, ObjectGuid sim)
        {
            FieldInfo f = pc == null ? null : SweTasksOf(pc.GetType());
            IDictionary d = f == null ? null : f.GetValue(pc) as IDictionary;
            return d != null && d.Contains(sim) ? d[sim] : null;
        }

        // The wait list a stair component's OnPortalStart fills: EA's field, or a mod subclass's own mSleepingTasks when its
        // OnPortalStart copy declares one (docs/328 §7: Toddler Independence's lizcandorStairs keeps its own list of EA's entries).
        static readonly Dictionary<Type, FieldInfo> sSweTasksBy = new Dictionary<Type, FieldInfo>();
        static FieldInfo SweTasksOf(Type t)
        {
            FieldInfo f;
            if (sSweTasksBy.TryGetValue(t, out f)) return f;
            f = sSweTasks;
            for (Type c = t; c != null && c != typeof(Stairs.StairsPortalComponent); c = c.BaseType)
            {
                FieldInfo own = c.GetField("mSleepingTasks", kInstAll | BindingFlags.DeclaredOnly);
                if (own != null && typeof(IDictionary).IsAssignableFrom(own.FieldType)) { f = own; break; }
            }
            sSweTasksBy[t] = f;
            return f;
        }

        // One stair wait's deadline alarm (InvalidHandle when the switch is off); shared by EA's OnPortalStart hook and the
        // mod-override hooks of CompatResolve.cs.
        static AlarmHandle SweArm(object component, Sim s)
        {
            AlarmHandle h = AlarmHandle.kInvalidHandle;
            try
            {
                if (s != null && sSweMissing.Length == 0 && ShortcutOn("stairwaitend"))
                {
                    Stairs.StairsPortalComponent pc = component as Stairs.StairsPortalComponent;
                    long t0 = SimClock.CurrentTicks;
                    h = AlarmManager.Global.AddAlarm(kStairWaitEndMinutes, TimeUnit.Minutes, delegate { SweFire(pc, s, t0); },
                        "Ts3Fixes.StairWaitEnd", AlarmType.NeverPersisted, null);
                }
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3449]++; }
            return h;
        }

        static void SweDisarm(AlarmHandle h)
        {
            if (h != AlarmHandle.kInvalidHandle) { try { AlarmManager.Global.RemoveAlarm(h); } catch (Exception) { } }
        }

        // HandleStairsCompleted(false)'s end of the wait: the loop exits at its next check and OnPortalStart returns false.
        static void SweFinish(object entry)
        {
            sSweResult.SetValue(entry, false);
            sSweSleeping.SetValue(entry, false);
        }

        // The deadline alarm (alarm task). Reads everything again: the wait may have ended or the switch changed.
        static void SweFire(Stairs.StairsPortalComponent pc, Sim s, long startTicks)
        {
            try
            {
                if (s == null || pc == null) return;
                object entry = SweEntry(pc, s.ObjectId);
                if (entry == null || !(bool)sSweSleeping.GetValue(entry)) return;
                float waited = SimClock.ConvertFromTicks(SimClock.CurrentTicks - startTicks, TimeUnit.Minutes);
                bool on = ShortcutOn("stairwaitend"), hh = HouseholdExempt(s);
                if (!SweEnd(on, waited, true, hh))
                {
                    if (waited >= kStairWaitEndMinutes) PatchCounters()[on ? 3448 : 3447]++;
                    return;
                }
                SweFinish(entry);
                PatchCounters()[3446]++;
                if (!s.HasBeenDestroyed) s.SetObjectToReset();  // EA's elevator failsafe
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3449]++; }
        }

        public class SwePortalHook
        {
            public bool StartHook(Sim s)
            {
                AlarmHandle h = AlarmHandle.kInvalidHandle;
                try
                {
                    if (s != null && sSweMissing.Length == 0 && ShortcutOn("stairwaitend"))
                    {
                        Stairs.StairsPortalComponent pc = (object)this as Stairs.StairsPortalComponent;
                        long t0 = SimClock.CurrentTicks;
                        h = AlarmManager.Global.AddAlarm(kStairWaitEndMinutes, TimeUnit.Minutes, delegate { SweFire(pc, s, t0); },
                            "Ts3Fixes.StairWaitEnd", AlarmType.NeverPersisted, null);
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3449]++; }
                try { return StartOrig(s); }
                finally
                {
                    if (h != AlarmHandle.kInvalidHandle) { try { AlarmManager.Global.RemoveAlarm(h); } catch (Exception) { } }
                }
            }
            public bool StartOrig(Sim s) { return false; } // receives Stairs+StairsPortalComponent.OnPortalStart
        }

        // StairWaitEndCheck run: pure decision with controls, then a live replay on a world staircase: a dummy entry (key no
        // object uses) put in its wait list asleep, our end step when the decision says so, then removed. Off: still asleep.
        // On: ended with result false. Prints stair waits asleep now.
        public static object StairWaitEndCheck(string what)
        {
            string counters = B582Counters("stairwaitend", 3446, new[] { "waits ended at 30 sim-min", "past deadline, switch off", "past deadline, household kept", "errors" }, sSweMissing);
            if (what == "result") return counters;
            if (what != "run") return "stairwaitend <run|result>";
            bool on = ShortcutOn("stairwaitend");
            bool eaBug = !SweEnd(false, 600f, true, false);
            bool fixedPure = on && SweEnd(on, 30f, true, false);
            bool controls = eaBug && !SweEnd(true, 29.9f, true, false) && !SweEnd(true, 600f, false, false) && !SweEnd(true, 600f, true, true);
            string live = "no staircase in the world (pure only)";
            bool liveOk = true;
            int asleep = 0;
            Stairs.StairsPortalComponent pc = null;
            try
            {
                foreach (Stairs st in Sims3.Gameplay.Queries.GetObjects<Stairs>())
                {
                    Stairs.StairsPortalComponent c = st == null ? null : st.PortalComponent as Stairs.StairsPortalComponent;
                    IDictionary d = c == null || sSweTasks == null ? null : sSweTasks.GetValue(c) as IDictionary;
                    if (d == null) continue;
                    if (pc == null) pc = c;
                    foreach (object e in d.Values) if (e != null && (bool)sSweSleeping.GetValue(e)) asleep++;
                }
            }
            catch (Exception e) { liveOk = false; live = "stairs scan threw " + e.GetType().Name; }
            if (pc != null && sSweEntryType != null)
            {
                IDictionary d = sSweTasks.GetValue(pc) as IDictionary;
                ObjectGuid key = new ObjectGuid(0xFFFFFFFF00000ABCUL);
                try
                {
                    if (d.Contains(key)) d.Remove(key);
                    object entry = Activator.CreateInstance(sSweEntryType, true);
                    sSweSleeping.SetValue(entry, true);
                    d[key] = entry;
                    object found = SweEntry(pc, key);
                    if (found != null && SweEnd(on, 31f, (bool)sSweSleeping.GetValue(found), false)) SweFinish(found);
                    bool ended = !(bool)sSweSleeping.GetValue(entry), result = (bool)sSweResult.GetValue(entry);
                    liveOk = found == entry && (on ? ended && !result : !ended);
                    live = "live replay on stairs " + pc.Owner.ObjectId.Value + ": wait " + (ended ? "ended (result " + result + ")" : "still asleep");
                }
                catch (Exception e) { liveOk = false; live = "live replay threw " + e.GetType().Name; }
                finally { try { d.Remove(key); } catch (Exception) { } }
            }
            return B582Line("stairwaitend", on, "a stair wait ends at EA's 30 sim-min failsafe", fixedPure && liveOk && sSweMissing.Length == 0,
                "EA never ends it (" + eaBug + "), 29.9 min / awake / household untouched, " + live + ", stair waits asleep now " + asleep, controls && liveOk, sSweMissing, counters);
        }

        // ---- docs/612 §3 simdisposelink -----------------------------------------------------------------------------------
        // [verified, ilspycmd Sim::Dispose IL_0013..IL_001a] Sim.Dispose sets mSimDescription.CreatedSim = null with no check
        // that the description is bound to this Sim. [verified, Sim::get_HasBeenDestroyed] EA counts a Sim whose description
        // is bound to another Sim as destroyed (CreatedSim != this). [verified, Sim::OnLoad and OnCreation] every Sim object
        // rebinds its description (SetSimDescription: CreatedSim = this), last one wins. [verified,
        // SimDescription::Instantiate(Vector3, ResourceKey, bool, bool)] the only guard against a second Sim object is
        // CreatedSim != null. So disposing a Sim EA already treats as destroyed (an unbound duplicate: a second saved Sim
        // object of the description, or one a cleanup destroys) unbinds the live Sim: the household gets kSimRemoved for
        // the live Sim (Household.OnMemberChanged), the live Sim reads HasBeenDestroyed == true while in the world, and the
        // next Instantiate of the description (service, visit, travel return, GoHome) creates a second live Sim object.
        // Fix as EA intended (HasBeenDestroyed's own rule: the bound Sim is the Sim): Dispose of a Sim whose description is
        // bound to another live Sim leaves the link. The raw field is cleared before EA's body (so EA's set is a no-op and
        // fires no household event for the live Sim) and set back to the bound Sim after. A bound Sim, or a description bound
        // to nobody or to a destroyed Sim, runs EA's Dispose unchanged. Household Sims: the result is EA's intended one (the
        // live Sim stays the household's member), so there is no exemption.
        // Counters: [3450] links kept, [3451] unbound disposes with the switch off (EA unbinds the live Sim), [3452] errors.
        static readonly FieldInfo sSdlSim = typeof(SimDescription).GetField("mSim", kInstAll);
        static string sSdlMissing = "";

        static void ApplySimDisposeLink()
        {
            sSdlMissing = sSdlSim == null || sSdlSim.FieldType != typeof(Sim) ? "SimDescription.mSim" : "";
            if (sSdlMissing.Length > 0) { AppliedLog().Add("simdisposelink: not found: " + sSdlMissing); return; }
            Patch(typeof(Sim), "Dispose", Type.EmptyTypes, typeof(SdlDisposeHook), "DisposeHook", "DisposeOrig");
        }

        // Pure: keep the description's link to the bound Sim over this Sim's Dispose? (EA: never.)
        internal static bool SdlKeep(bool on, bool hasDesc, bool boundIsOther, bool boundAlive)
        {
            return on && hasDesc && boundIsOther && boundAlive;
        }

        // Before EA's Dispose: the bound Sim to restore after it, or null (EA's path).
        static Sim SdlBefore(Sim self, SimDescription d, bool on)
        {
            if (d == null) return null;
            Sim bound = sSdlSim.GetValue(d) as Sim;
            bool other = bound != null && !ReferenceEquals(bound, self);
            bool alive = other && bound.SimDescription == d && bound.ObjectId.IsValid && Simulator.GetProxy(bound.ObjectId) != null;
            if (!SdlKeep(on, true, other, alive))
            {
                if (other) PatchCounters()[3451]++;
                return null;
            }
            sSdlSim.SetValue(d, null);  // raw: no household event; EA's CreatedSim = null is then a no-op
            return bound;
        }

        static void SdlAfter(SimDescription d, Sim bound)
        {
            if (d == null || bound == null) return;
            if (sSdlSim.GetValue(d) == null) { sSdlSim.SetValue(d, bound); PatchCounters()[3450]++; }
        }

        public class SdlDisposeHook
        {
            public void DisposeHook()
            {
                Sim self = (object)this as Sim;
                SimDescription d = null;
                Sim bound = null;
                try
                {
                    if (sSdlMissing.Length == 0 && self != null && ShortcutOn("simdisposelink")) { d = self.SimDescription; bound = SdlBefore(self, d, true); }
                    else if (sSdlMissing.Length == 0 && self != null && self.SimDescription != null) SdlBefore(self, self.SimDescription, false);
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3452]++; bound = null; }
                try { DisposeOrig(); }
                finally
                {
                    try { SdlAfter(d, bound); } catch (Exception) { PatchCounters()[3452]++; }
                }
            }
            public void DisposeOrig() { } // receives Sim.Dispose
        }

        // SimDisposeLinkCheck run: pure decision with controls, a live replay of the link steps on a world NPC's description
        // (raw field only, no Dispose and no household event: our step with another Sim as the disposed one, then EA's
        // no-op set, then our restore), and a duplicate census: live Sims whose description is bound to another Sim,
        // descriptions with two live Sim objects, and SimDescriptionIds held by two descriptions in the world.
        public static object SimDisposeLinkCheck(string what)
        {
            string counters = B582Counters("simdisposelink", 3450, new[] { "links kept", "unbound disposes, switch off", "errors" }, sSdlMissing);
            if (what == "result") return counters;
            if (what != "run") return "simdisposelink <run|result>";
            bool on = ShortcutOn("simdisposelink");
            bool eaBug = !SdlKeep(false, true, true, true);
            bool fixedPure = on && SdlKeep(on, true, true, true);
            bool controls = eaBug && !SdlKeep(true, true, false, true) && !SdlKeep(true, true, true, false) && !SdlKeep(true, false, true, true);
            string live = "fewer than two NPC Sims in the world (pure only)", census = "census n/a";
            bool liveOk = true;
            Sim npc = null, other = null;
            try
            {
                int unbound = 0, twoObjects = 0, twoDescs = 0;
                Dictionary<SimDescription, int> perDesc = new Dictionary<SimDescription, int>();
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s == null || s.SimDescription == null) continue;
                    SimDescription d = s.SimDescription;
                    if (!ReferenceEquals(sSdlSim == null ? d.CreatedSim : sSdlSim.GetValue(d), s)) unbound++;
                    int n; perDesc.TryGetValue(d, out n); perDesc[d] = n + 1;
                    if (n == 1) twoObjects++;
                    if (npc == null && !IsPlayerSim(s) && ReferenceEquals(d.CreatedSim, s)) npc = s;
                    else if (other == null && npc != null && !ReferenceEquals(s, npc)) other = s;
                }
                Dictionary<ulong, SimDescription> perId = new Dictionary<ulong, SimDescription>();
                foreach (SimDescription d in Household.EverySimDescription())
                {
                    if (d == null) continue;
                    SimDescription prev;
                    if (perId.TryGetValue(d.SimDescriptionId, out prev)) { if (!ReferenceEquals(prev, d)) twoDescs++; }
                    else perId[d.SimDescriptionId] = d;
                }
                census = "census: unbound live Sims " + unbound + ", descriptions with two Sim objects " + twoObjects + ", ids held by two descriptions " + twoDescs;
            }
            catch (Exception e) { census = "census threw " + e.GetType().Name; }
            if (npc != null && other != null && sSdlMissing.Length == 0)
            {
                SimDescription d = npc.SimDescription;
                int[] c = PatchCounters();
                int c0 = c[3450], c1 = c[3451];
                try
                {
                    Sim bound = SdlBefore(other, d, on);   // `other` stands in for an unbound duplicate of d
                    bool clearedForEa = sSdlSim.GetValue(d) == null;
                    SdlAfter(d, bound);
                    bool kept = ReferenceEquals(sSdlSim.GetValue(d), npc);
                    liveOk = kept && (on ? bound == npc && clearedForEa : bound == null && !clearedForEa);
                    live = "live replay on " + npc.ObjectId.Value + ": " + (on ? "EA's set made a no-op, link restored=" + kept : "EA's path (would unbind), link untouched=" + kept);
                }
                catch (Exception e) { liveOk = false; live = "live replay threw " + e.GetType().Name; }
                finally
                {
                    try { if (!ReferenceEquals(sSdlSim.GetValue(d), npc)) sSdlSim.SetValue(d, npc); } catch (Exception) { }
                    c[3450] = c0; c[3451] = c1;   // the check's own steps are not counted
                }
            }
            return B582Line("simdisposelink", on, "an unbound Sim's Dispose keeps the live Sim's link", fixedPure && liveOk && sSdlMissing.Length == 0,
                "EA unbinds (" + eaBug + "), bound/dead/no-description untouched, " + live + ", " + census, controls && liveOk, sSdlMissing, counters);
        }
    }
}
