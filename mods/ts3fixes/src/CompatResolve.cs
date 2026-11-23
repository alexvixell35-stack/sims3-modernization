using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.DreamsAndPromises;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Gardening;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI.Hud;
using Sims3.UI;
using Sims3.UI.CAS;
using System.Collections;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Core;

namespace Ts3Fixes
{
    // Runtime precedence for mod methods that skip one of our EA fixes (docs/328 §7, table in CompatResolveCore.cs). A mod
    // whose override does not call the game's method (or whose copy hides a non-virtual one) never reaches our hook on the
    // game's method, so the EA bug stays for its interactions. Here the same fix goes onto the mod's method at run time; the
    // mod's files stay as they are. No switch of its own: each hook is pass-through while its fix's switch is off, and the
    // fix's counters count these cases too. Household rule: as each fix (cleanupvfx and lifeguardquit are exact EA bug fixes
    // for every Sim; stairwaitend keeps household and unknown-scope Sims waiting, HouseholdExempt).
    // When: after the fixes at tuning time and again at every world load (script mods may load after us), each target once;
    // a mod method that is already compiled is refused by Patch and logged. One "compat precedence: ..." line per target
    // found goes to ts3native.log at the first world load (DiagLine) and to the applied log.
    public static partial class Fixes
    {
        static readonly List<string> sCrDone = new List<string>();   // "<asm>/<type>.<method>" handled (once per session)
        static readonly List<string> sCrLines = new List<string>();  // lines not yet sent to ts3native.log
        static readonly Type[][] kCrHooks = {
            new Type[] { typeof(CrCv0), typeof(CrCv1), typeof(CrCv2), typeof(CrCv3), typeof(CrCv4), typeof(CrCv5), typeof(CrCv6), typeof(CrCv7), typeof(CrCv8), typeof(CrCv9), typeof(CrCv10), typeof(CrCv11) },
            new Type[] { typeof(CrLq0) },
            new Type[] { typeof(CrSwe0) },
            new Type[] { typeof(CrHe0), typeof(CrHe1) },
            new Type[] { typeof(CrDu0) },
            new Type[] { typeof(CrOa0), typeof(CrOa1), typeof(CrOa2), typeof(CrOa3), typeof(CrOa4), typeof(CrOa5), typeof(CrOa6), typeof(CrOa7), typeof(CrOa8), typeof(CrOa9), typeof(CrOa10), typeof(CrOa11), typeof(CrOa12), typeof(CrOa13), typeof(CrOa14), typeof(CrOa15), typeof(CrOa16), typeof(CrOa17), typeof(CrOa18), typeof(CrOa19), typeof(CrOa20), typeof(CrOa21), typeof(CrOa22), typeof(CrOa23) },
            new Type[] { typeof(CrOr0), typeof(CrOr1), typeof(CrOr2), typeof(CrOr3), typeof(CrOr4), typeof(CrOr5), typeof(CrOr6), typeof(CrOr7), typeof(CrOr8), typeof(CrOr9), typeof(CrOr10), typeof(CrOr11), typeof(CrOr12), typeof(CrOr13), typeof(CrOr14), typeof(CrOr15), typeof(CrOr16), typeof(CrOr17), typeof(CrOr18), typeof(CrOr19), typeof(CrOr20), typeof(CrOr21), typeof(CrOr22), typeof(CrOr23) },
            new Type[] { typeof(CrBr0) },
            new Type[] { typeof(CrBh0), typeof(CrBh1) },
            new Type[] { typeof(CrBc0), typeof(CrBc1) },
            Type.EmptyTypes,  // VisitRun: VclSubHook* (ApplyTripSubclasses) is the hook
            new Type[] { typeof(CrEr0) },
            new Type[] { typeof(CrPbR0), typeof(CrPbR1), typeof(CrPbR2) },
            new Type[] { typeof(CrPbC0), typeof(CrPbC1), typeof(CrPbC2) },
            new Type[] { typeof(CrQg0) },
            new Type[] { typeof(CrRo0) },
            new Type[] { typeof(CrWw0) },
            new Type[] { typeof(CrCa0) },
            new Type[] { typeof(CrCr0) } };

        internal static void ApplyCompatPrecedence()
        { // reapply-safe: each target once (sCrDone)
            List<string> names = new List<string>();
            List<Assembly> asms = new List<Assembly>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { names.Add(a.GetName().Name); asms.Add(a); }
            CompatResolveCore.Target[] table = CompatResolveCore.Targets;
            foreach (int i in CompatResolveCore.Loaded(table, names.ToArray()))
            {
                CompatResolveCore.Target t = table[i];
                string key = t.Asm + "/" + t.Type + "." + t.Method;
                if (sCrDone.Contains(key)) continue;
                sCrDone.Add(key);
                Assembly a = asms[names.IndexOf(t.Asm)];
                string line = CompatResolveCore.Line(t, ShortMvid(a), CrApply(a, t, CompatResolveCore.Slot(table, i)));
                AppliedLog().Add(line);
                sCrLines.Add(line);
            }
        }

        // Called at world load (Instantiator), after ApplyCompatPrecedence: the lines go to ts3native.log once.
        internal static void CompatPrecedenceLog()
        {
            // store targets not loaded at world load: one "not loaded" line each per session, [1589] counts them (docs/328 §7.5)
            List<string> names = new List<string>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) names.Add(a.GetName().Name);
            CompatResolveCore.Target[] table = CompatResolveCore.Targets;
            foreach (int i in CompatResolveCore.NotLoadedOnDemand(table, names.ToArray()))
            {
                CompatResolveCore.Target t = table[i];
                string key = "absent/" + t.Asm + "/" + t.Type + "." + t.Method;
                if (sCrDone.Contains(key)) continue;
                sCrDone.Add(key);
                PatchCounters()[1589]++;
                sCrLines.Add(CompatResolveCore.Line(t, "-", "not loaded"));
            }
            while (sCrLines.Count > 0 && DiagLine(sCrLines[0])) sCrLines.RemoveAt(0);
        }

        static string CrApply(Assembly a, CompatResolveCore.Target t, int slot)
        {
            Type type;
            try { type = a.GetType(t.Type, false); } catch (Exception e) { return "type lookup failed: " + e.GetType().Name; }
            if (type == null) return "type not in this build, left alone";
            if (slot < 0) return "no hook slot, left alone";
            Type[] args;
            string hook, stub;
            switch (t.Kind)
            {
                case CompatResolveCore.Cleanup:
                    if (sCvList == null) return "cleanupvfx not available (InteractionInstance.mVfxs not found)";
                    args = Type.EmptyTypes; hook = "CleanupHook"; stub = "CleanupOrig"; break;
                case CompatResolveCore.SurveyLot:
                    args = new Type[] { typeof(StateMachineClient), typeof(InteractionInstance.LoopData) }; hook = "LoopHook"; stub = "LoopOrig"; break;
                case CompatResolveCore.PortalStart:
                    if (sSweSleeping == null || sSweResult == null) return "stairwaitend not available (ActiveUserEntry fields not found)";
                    if (SweTasksOf(type) == null) return "no mSleepingTasks on the type, left alone";
                    args = new Type[] { typeof(Sim) }; hook = "StartHook"; stub = "StartOrig"; break;
                case CompatResolveCore.HarvestCopy:
                    if (sHeM13.Length > 0) return "harvestextras not available (not found: " + sHeM13 + ")";
                    args = new Type[] { typeof(HarvestPlant), typeof(Sim), typeof(bool), typeof(BurglarSituation) }; hook = "HarvestHook"; stub = "HarvestOrig"; break;
                case CompatResolveCore.WishCopy:
                    args = new Type[] { typeof(DreamsAndPromisesManager) }; hook = "UpdateHook"; stub = "UpdateOrig"; break;
                case CompatResolveCore.BoatRun:
                    if (!typeof(Sims3.Gameplay.Objects.Vehicles.Boat.BoatAround).IsAssignableFrom(type)) return "not a BoatAround subclass, left alone";
                    args = Type.EmptyTypes; hook = "RunHook"; stub = "RunOrig"; break;
                case CompatResolveCore.OccultAdd:
                    args = new Type[] { typeof(OccultManager), typeof(OccultTypes), typeof(bool), typeof(bool), typeof(bool), typeof(OccultBaseClass) }; hook = "AddHook"; stub = "AddOrig"; break;
                case CompatResolveCore.OccultRemove:
                    args = new Type[] { typeof(SimDescription), typeof(OccultTypes), typeof(bool) }; hook = "RemoveHook"; stub = "RemoveOrig";
                    if (type.GetMethod(t.Method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, args, null) == null)
                    { args = new Type[] { typeof(SimDescription), typeof(OccultTypes) }; hook = "Remove2Hook"; stub = "Remove2Orig"; }  // older NRaas Common builds
                    break;
                case CompatResolveCore.BirthHave:
                    if (!typeof(Pregnancy).IsAssignableFrom(type)) return "not a Pregnancy subclass, left alone";
                    args = Type.EmptyTypes; hook = "HaveHook"; stub = "HaveOrig"; break;
                case CompatResolveCore.BirthCleanup:
                    if (CrBirthFields(type) == null) return "no BabyShouldBeBorn / mNewborns on the type, left alone";
                    args = Type.EmptyTypes; hook = "CleanupHook"; stub = "CleanupOrig"; break;
                case CompatResolveCore.VisitRun:
                    if (!type.IsSubclassOf(typeof(VisitCommunityLot))) return "not a VisitCommunityLot subclass, left alone";
                    return sVclSubOk.Contains(type) ? "ok" : "Run not patched by the trip-subclass pass (" + (sVclSubPatched.Contains(type) ? "refused or no slot" : "not seen") + ")";
                case CompatResolveCore.ElevReset:
                    if (CrErFields(type) == null) return "no mAssignedSims / ActiveUserEntry.mbSleeping / mTask on the type, left alone";
                    args = Type.EmptyTypes; hook = "ResetHook"; stub = "ResetOrig"; break;
                case CompatResolveCore.BreakRun:
                case CompatResolveCore.BreakCleanup:
                    if (!typeof(InteractionInstance).IsAssignableFrom(type) || CrPbDoorField(type) == null) return "no Door mDoor on an interaction type, left alone";
                    args = Type.EmptyTypes;
                    if (t.Kind == CompatResolveCore.BreakRun) { hook = "RunHook"; stub = "RunOrig"; } else { hook = "CleanupHook"; stub = "CleanupOrig"; }
                    break;
                case CompatResolveCore.QuitGuard:
                    args = new Type[] { typeof(object), typeof(EventArgs) }; hook = "QuitHook"; stub = "QuitOrig"; break;
                case CompatResolveCore.RouteOpts:
                    args = new Type[] { typeof(SimRoutingComponent), typeof(Route), typeof(uint) }; hook = "OptsHook"; stub = "OptsOrig"; break;
                case CompatResolveCore.WooHootyWait:
                    if (!typeof(Phone.SendWooHootyText).IsAssignableFrom(type)) return "not a SendWooHootyText subclass, left alone";
                    args = new Type[] { typeof(Sim), typeof(float) }; hook = "WaitHook"; stub = "WaitOrig"; break;
                case CompatResolveCore.CasAdd:
                    if (sCrlDict == null || sCrlDeregister == null || sCrlHandle == null) return "casrowleak not available (UIManager members not found)";
                    args = new Type[] { typeof(ItemGrid), typeof(object), typeof(ResourceKey), typeof(object) }; hook = "AddHook"; stub = "AddOrig"; break;
                case CompatResolveCore.CasRow:
                    if (sCrlDict == null || sCrlDeregister == null || sCrlHandle == null) return "casrowleak not available (UIManager members not found)";
                    args = CrCasRowArgs(type);  // (CASClothingRow, List<the mod's preset type>, bool): the mod's own type, read here
                    if (args == null) return "no static CreateGridItems(CASClothingRow, list, bool), left alone";
                    hook = "CreateHook"; stub = "CreateOrig"; break;
                default: return "unknown kind";
            }
            const BindingFlags decl = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            if (type.GetMethod(t.Method, decl, null, args, null) == null) return "method not declared with the expected parameters, left alone";
            if (Patch(type, t.Method, args, kCrHooks[t.Kind][slot], hook, stub)) return "ok";
            List<object> log = AppliedLog();
            return "refused: " + (log.Count > 0 ? log[log.Count - 1] : "?");
        }

        // ---- the fix steps the hooks share ----

        // cleanupvfx (Batch592Logic.cs) after the mod's Cleanup.
        static void CrCleanupAfter(object self)
        {
            try
            {
                InteractionInstance ii = self as InteractionInstance;
                if (ii == null || sCvList.GetValue(ii) == null || GameStates.IsGameShuttingDown) return;
                if (!ShortcutOn("cleanupvfx")) { PatchCounters()[3179]++; return; }
                if (CvDisposeList(ii, true)) PatchCounters()[3178]++;
            }
            catch (Exception) { PatchCounters()[3180]++; }
        }

        // lifeguardquit (Batch590Logic.cs) before the mod's survey loop body; true = the loop ends here.
        static bool CrSurveyStop(object self)
        {
            InteractionInstance ii = self as InteractionInstance;
            Sim a = ii == null ? null : ii.InstanceActor as Sim;
            bool quit = false;
            try { quit = a != null && CompatResolveCore.SurveyStop(a.Occupation != null, a.Occupation != null && a.Occupation.CurrentJob != null); }
            catch (Exception) { PatchCounters()[3072]++; }
            if (!quit) return false;
            if (ShortcutOn("lifeguardquit")) { a.AddExitReason(ExitReason.Finished); PatchCounters()[3070]++; return true; }
            PatchCounters()[3071]++;
            return false;
        }

        // One hook class per patched method (each stub receives that method's original body).
        public class CrCv0 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv1 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv2 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv3 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv4 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv5 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv6 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv7 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv8 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv9 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv10 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }
        public class CrCv11 { public void CleanupHook() { CleanupOrig(); CrCleanupAfter(this); } public void CleanupOrig() { } }

        public class CrLq0
        {
            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData ld) { if (CrSurveyStop(this)) return; LoopOrig(smc, ld); }
            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData ld) { }
        }

        public class CrSwe0
        {
            public bool StartHook(Sim s)
            {
                AlarmHandle h = SweArm(this, s);
                try { return StartOrig(s); }
                finally { SweDisarm(h); }
            }
            public bool StartOrig(Sim s) { return false; }
        }

        // harvestextras for a mod's static DoHarvest copy (NRaas Overwatch: its Harvest replacement calls it, so B13HeHook never
        // runs for menu harvests). Switch off, or a MoneyTree (the mod's copy has its own money-tree journal): the mod's body.
        public class CrHe0
        {
            public static bool HarvestHook(HarvestPlant hp, Sim actor, bool hasHarvested, BurglarSituation b)
            {
                if (hp is MoneyTree) return HarvestOrig(hp, actor, hasHarvested, b);
                if (!ShortcutOn("harvestextras")) { PatchCounters()[3209]++; return HarvestOrig(hp, actor, hasHarvested, b); }
                return B13HeHook.HeDoHarvest(hp, actor, hasHarvested, b);
            }
            public static bool HarvestOrig(HarvestPlant hp, Sim actor, bool hasHarvested, BurglarSituation b) { return false; }
        }
        public class CrHe1
        {
            public static bool HarvestHook(HarvestPlant hp, Sim actor, bool hasHarvested, BurglarSituation b)
            {
                if (hp is MoneyTree) return HarvestOrig(hp, actor, hasHarvested, b);
                if (!ShortcutOn("harvestextras")) { PatchCounters()[3209]++; return HarvestOrig(hp, actor, hasHarvested, b); }
                return B13HeHook.HeDoHarvest(hp, actor, hasHarvested, b);
            }
            public static bool HarvestOrig(HarvestPlant hp, Sim actor, bool hasHarvested, BurglarSituation b) { return false; }
        }

        // wishupdatelost + dreamcleanup part 2 around a mod's static Update copy (NRaas Dreamer drains the update list with it
        // before EA's drain runs). Each step checks its own switch (pass-through while off); the mod's own exception handling stays.
        public class CrDu0
        {
            public static void UpdateHook(DreamsAndPromisesManager mgr)
            {
                object prev; bool prevLost;
                W86UpdateBegin(mgr, out prev, out prevLost);
                bool ok = false;
                try { List<object> woke = DcBeforeUpdate(mgr); UpdateOrig(mgr); DcAfterUpdate(mgr, woke); ok = true; }
                finally { W86UpdateEnd(mgr, prev, prevLost, ok); }
            }
            public static void UpdateOrig(DreamsAndPromisesManager mgr) { }
        }

        // boatroamcap before a mod's BoatAround.Run (Autonomous Fun in the Sun roams rowboats / paddleboats itself, motorboats via
        // base): the same BrcBefore check as BrcRunHook; while the mod's body runs, BrcBefore skips its base call (BrcModRuns).
        public class CrBr0
        {
            public bool RunHook()
            {
                if (ShortcutOn("boatroamcap"))
                {
                    bool capped = false;
                    try { capped = BrcBefore((object)this as Sims3.Gameplay.Objects.Vehicles.Boat.BoatAround); }
                    catch (Exception ex) { PatchCounters()[1396]++; BrcLog("error " + ex.GetType().Name); }
                    if (capped) return true;  // no roam; the pushed ReturnToShore runs next
                }
                sBrcModRuns.Add(this);
                try { return RunOrig(); }
                finally { sBrcModRuns.Remove(this); }
            }
            public bool RunOrig() { return false; }
        }

        // birthroute around a mod's own birth step (Enhanced Hospitals' donor / surrogate pregnancies: HaveTheBaby without base,
        // their own HaveBabyHospital* interactions). BrHave's F3 / F1 / F2 steps and BrCleanupStep's F5 on the mod's types; the
        // home-birth switch, the homeless-mom hospital and the BabyIsComing restore are EA-flow steps and stay EA-only (docs/328
        // §7). Household, selected and unknown-scope moms (IsNpcSim false) keep the 1-minute re-have, as in BrHave.
        public class CrBh0 : Pregnancy
        {
            public void HaveHook()
            {
                Sim mom = null;
                int act = CompatResolveCore.BmOff;
                try { act = CrBirthBefore((object)this as Pregnancy, mMom, HaveTheBaby, out mom); } catch (Exception) { PatchCounters()[2442]++; act = CompatResolveCore.BmOff; }
                if (act == CompatResolveCore.BmNothing || act == CompatResolveCore.BmDefer) return;
                HaveOrig();
                if (act == CompatResolveCore.BmRun) try { CrBirthAfter((object)this as Pregnancy, mom, HaveTheBaby); } catch (Exception) { PatchCounters()[2442]++; }
                if (sBmrMom != null) BmrFailQueued((object)this as Pregnancy, mMom);  // harness repro only (CompatResolveChecks.cs)
            }
            public void HaveOrig() { }
        }
        public class CrBh1 : Pregnancy
        {
            public void HaveHook()
            {
                Sim mom = null;
                int act = CompatResolveCore.BmOff;
                try { act = CrBirthBefore((object)this as Pregnancy, mMom, HaveTheBaby, out mom); } catch (Exception) { PatchCounters()[2442]++; act = CompatResolveCore.BmOff; }
                if (act == CompatResolveCore.BmNothing || act == CompatResolveCore.BmDefer) return;
                HaveOrig();
                if (act == CompatResolveCore.BmRun) try { CrBirthAfter((object)this as Pregnancy, mom, HaveTheBaby); } catch (Exception) { PatchCounters()[2442]++; }
                if (sBmrMom != null) BmrFailQueued((object)this as Pregnancy, mMom);  // harness repro only (CompatResolveChecks.cs)
            }
            public void HaveOrig() { }
        }
        public class CrBc0 { public void CleanupHook() { CrBirthCleanup(this); CleanupOrig(); } public void CleanupOrig() { } }
        public class CrBc1 { public void CleanupHook() { CrBirthCleanup(this); CleanupOrig(); } public void CleanupOrig() { } }

        static readonly Dictionary<Type, FieldInfo[]> sCrBirthF = new Dictionary<Type, FieldInfo[]>();

        // { BabyShouldBeBorn, mNewborns } of a mod's birth interaction (public fields, as on EA's HaveBabyHospital), else null
        static FieldInfo[] CrBirthFields(Type t)
        {
            FieldInfo[] f;
            if (sCrBirthF.TryGetValue(t, out f)) return f;
            FieldInfo b = t.GetField("BabyShouldBeBorn", BindingFlags.Instance | BindingFlags.Public), n = t.GetField("mNewborns", BindingFlags.Instance | BindingFlags.Public);
            f = typeof(InteractionInstance).IsAssignableFrom(t) && b != null && b.FieldType == typeof(bool) && n != null && n.FieldType == typeof(List<Sim>) ? new FieldInfo[] { b, n } : null;
            sCrBirthF[t] = f;
            return f;
        }

        // The Sim who gives birth: a surrogate pregnancy's mSurrogateMom (null until the mod sets it: then no step of ours), else mMom.
        static Sim CrBirthMom(Pregnancy p, Sim mMom)
        {
            for (Type t = p.GetType(); t != null && t != typeof(Pregnancy); t = t.BaseType)
            {
                FieldInfo f = t.GetField("mSurrogateMom", kInstAll | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(p) as Sim;
            }
            return mMom;
        }

        // The mod's birth interaction in the mom's queue with the birth due (BrBirthInteraction for the mod's types).
        static InteractionInstance CrBirthQueued(Sim mom, Assembly modAsm)
        {
            InteractionQueue q = mom.InteractionQueue;
            if (q == null) return null;
            foreach (InteractionInstance ia in q.InteractionList)
            {
                if (ia == null || ia.GetType().Assembly != modAsm) continue;
                FieldInfo[] f = CrBirthFields(ia.GetType());
                if (f != null && (bool)f[0].GetValue(ia)) return ia;
            }
            return null;
        }

        static int CrBirthBefore(Pregnancy p, Sim mMom, AlarmTimerCallback reHave, out Sim mom)  // reHave: the mod's HaveTheBaby (protected: bound in the hook)
        {
            mom = null;
            if (!ShortcutOn("birthroute") || p == null) return CompatResolveCore.BmOff;
            mom = CrBirthMom(p, mMom);
            if (mom == null || mom.HasBeenDestroyed || mom.SimDescription == null) return CompatResolveCore.BmOff;
            ulong id = mom.SimDescription.SimDescriptionId;
            bool current = mom.SimDescription.Pregnancy == p, npc = IsNpcSim(mom);
            BrState st;
            sBr.TryGetValue(id, out st);
            long now = SimClock.CurrentTicks;
            float waitLeft = 0f;
            if (npc && st != null && st.Attempts > 0)
            {
                long left = st.Last + SimClock.ConvertToTicks(BirthRetryWaitMin(true, true, st.Attempts), TimeUnit.Minutes) - now;
                if (left > 0 && now >= st.Last) waitLeft = SimClock.ConvertFromTicks(left, TimeUnit.Minutes);
            }
            int act = CompatResolveCore.BirthModBefore(true, current, current && CrBirthQueued(mom, p.GetType().Assembly) != null, npc, waitLeft > 0f);
            if (act == CompatResolveCore.BmNothing) { if (!current) sBr.Remove(id); PatchCounters()[2471]++; }
            else if (act == CompatResolveCore.BmDefer)
            {
                mom.AddAlarm(Math.Max(1f, waitLeft), TimeUnit.Minutes, reHave, "Re-have the baby.", AlarmType.AlwaysPersisted);
                PatchCounters()[2469]++;
            }
            return act;
        }

        static void CrBirthAfter(Pregnancy p, Sim mom, AlarmTimerCallback reHave)
        {
            if (mom == null || mom.SimDescription == null) return;
            int[] c = PatchCounters();
            ulong id = mom.SimDescription.SimDescriptionId;
            InteractionInstance queued = CrBirthQueued(mom, p.GetType().Assembly);
            if (CompatResolveCore.BirthModRearm(queued != null, mom.SimDescription.Pregnancy == p))
            {
                mom.AddAlarm(kBirthLostRetryMin, TimeUnit.Minutes, reHave, "Re-have the baby.", AlarmType.AlwaysPersisted);  // F2
                c[2470]++;
                return;
            }
            if (queued == null) { sBr.Remove(id); return; }  // the mod ended the pregnancy itself (no hospital)
            BrState st;
            if (!sBr.TryGetValue(id, out st))
            {
                if (sBr.Count > 256) sBr.Clear();  // shortcut: a storm of distinct moms starts over
                sBr[id] = st = new BrState();
            }
            st.Attempts++;
            st.Last = SimClock.CurrentTicks;
            st.LastKind = 1;
            if (IsNpcSim(mom) && st.Attempts >= 2) BrPlaceNearHospital(mom, queued.Target as RabbitHole);
            if (st.Watch != AlarmHandle.kInvalidHandle) mom.RemoveAlarm(st.Watch);
            st.Watch = mom.AddAlarm(kBirthWatchdogMin, TimeUnit.Minutes, reHave, "Re-have the baby.", AlarmType.AlwaysPersisted);  // F2 watchdog
            c[2474]++;
        }

        // before the mod's birth Cleanup: BrCleanupStep (failed / completed counts, F5) on the mod's fields
        static void CrBirthCleanup(object self)
        {
            try
            {
                InteractionInstance ii = self as InteractionInstance;
                FieldInfo[] f = ii == null ? null : CrBirthFields(ii.GetType());
                if (f == null) return;
                if (BrCleanupStep(ii.InstanceActor as Sim, (bool)f[0].GetValue(ii), f[1].GetValue(ii) != null)) f[0].SetValue(ii, false);
            }
            catch (Exception) { PatchCounters()[2442]++; }
        }

        // doorgen after NRaas Common's occult add / remove (each NRaas assembly has its own copy): the same occult-source refresh
        // DoorGenOccultHook sends after EA's Add/RemoveOccultType; DoorGenBump is a no-op while doorgen is off.
        public class CrOa0 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr0 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa1 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr1 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa2 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr2 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa3 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr3 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa4 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr4 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa5 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr5 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa6 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr6 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa7 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr7 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa8 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr8 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa9 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr9 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa10 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr10 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa11 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr11 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa12 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr12 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa13 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr13 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa14 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr14 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa15 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr15 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa16 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr16 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa17 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr17 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa18 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr18 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa19 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr19 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa20 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr20 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa21 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr21 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa22 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr22 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }
        public class CrOa23 { public static bool AddHook(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(m, t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool AddOrig(OccultManager m, OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; } }
        public class CrOr23 { public static bool RemoveHook(SimDescription s, OccultTypes t, bool outfit) { bool r = RemoveOrig(s, t, outfit); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool RemoveOrig(SimDescription s, OccultTypes t, bool outfit) { return false; } public static bool Remove2Hook(SimDescription s, OccultTypes t) { bool r = Remove2Orig(s, t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; } public static bool Remove2Orig(SimDescription s, OccultTypes t) { return false; } }

        // ---- docs/328 §7.5 (2026-11-23): the six top500 / NRaas mods with no hook before ----

        // elevatorresetriders on a mod's copy of the elevator portal (Modern Elevators: Sims3.Diriel.Elevator, its own
        // ElevatorPortalComponent deriving PortalComponent; OnReset clears mAssignedSims while riders sleep in OnPortalStart's
        // wait, as EA's) [verified, decompiled 1.48 build]. Same release as BErHook; household riders too (EA's failsafe action).
        static FieldInfo[] sCrEr;  // { mAssignedSims, ActiveUserEntry.mbSleeping, ActiveUserEntry.mTask } (one slot)
        static FieldInfo[] CrErFields(Type t)
        {
            FieldInfo a = t.GetField("mAssignedSims", kInstAll);
            Type e = t.GetNestedType("ActiveUserEntry", BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo s = e == null ? null : e.GetField("mbSleeping", kInstAll), k = e == null ? null : e.GetField("mTask", kInstAll);
            if (a == null || s == null || k == null || s.FieldType != typeof(bool) || k.FieldType != typeof(ObjectGuid) || !typeof(IDictionary).IsAssignableFrom(a.FieldType)) return null;
            return sCrEr = new FieldInfo[] { a, s, k };
        }
        static List<Sim> CrErWaiting(object pc)
        {
            List<Sim> w = new List<Sim>();
            try
            {
                IDictionary d = sCrEr == null ? null : sCrEr[0].GetValue(pc) as IDictionary;
                if (d == null) return w;
                foreach (DictionaryEntry e in d)
                {
                    SimDescription sd = e.Key as SimDescription;
                    Sim s = sd == null ? null : sd.CreatedSim;
                    ObjectGuid task = (ObjectGuid)sCrEr[2].GetValue(e.Value);
                    bool live = task != ObjectGuid.InvalidObjectGuid && Simulator.GetTask(task) != null;
                    if (ErRelease(true, (bool)sCrEr[1].GetValue(e.Value), live, s != null && !s.HasBeenDestroyed)) w.Add(s);
                }
            }
            catch (Exception) { PatchCounters()[2844]++; }
            return w;
        }
        static void CrErRelease(List<Sim> w)
        {
            if (w.Count == 0) return;
            try
            {
                if (!ShortcutOn("elevatorresetriders")) { PatchCounters()[2843] += w.Count; return; }
                foreach (Sim s in w) { s.SetObjectToReset(); PatchCounters()[2842]++; }  // EA's failsafe action, now instead of in 30 minutes
            }
            catch (Exception) { PatchCounters()[2844]++; }
        }
        public class CrEr0 { public void ResetHook() { List<Sim> w = CrErWaiting(this); ResetOrig(); CrErRelease(w); } public void ResetOrig() { } }

        // privateeyecase part d on a mod's copy of the break-in interaction (Break Into House: Jadaytime.BreakIntoHouse, its own
        // Interaction<Sim, Lot>: Run sets Target.FindFrontDoor() to Nobody, Cleanup sets mDoor to Anybody and nulls it)
        // [verified, decompiled, all 6 builds]. The lock (type, owner, one-way room) is read before Run and put back after
        // Cleanup when Cleanup reset that door (PeRestoreLock), as PeBreakHook does on EA's; an EA bug fix for every Sim.
        static readonly Dictionary<Type, FieldInfo> sCrPbDoor = new Dictionary<Type, FieldInfo>();
        static FieldInfo CrPbDoorField(Type t)
        {
            FieldInfo f = null;
            if (sCrPbDoor.TryGetValue(t, out f)) return f;
            for (Type x = t; x != null && f == null; x = x.BaseType)
            {
                f = x.GetField("mDoor", kInstAll | BindingFlags.DeclaredOnly);
                if (f != null && !typeof(Door).IsAssignableFrom(f.FieldType)) f = null;
            }
            sCrPbDoor[t] = f;
            return f;
        }
        static void CrPbBeforeRun(object self)
        {
            if (!ShortcutOn("privateeyecase")) return;
            try
            {
                InteractionInstance ia = self as InteractionInstance;
                Lot lot = ia == null ? null : ia.Target as Lot;
                Door d = lot == null ? null : lot.FindFrontDoor();  // the mod's own pick (FindFrontDoor only)
                if (d != null)
                {
                    PeLock l = new PeLock { Door = d, Type = d.LockType, Owner = d.LockOwner, OneWay = d.OneWayRoomDestinationId };
                    lock (sPeLocks) sPeLocks[self] = l;
                }
            }
            catch (Exception) { PatchCounters()[2503]++; }
        }
        static object[] CrPbBeforeCleanup(object self)
        {
            PeLock l = null;
            lock (sPeLocks) { if (sPeLocks.TryGetValue(self, out l)) sPeLocks.Remove(self); }
            if (l == null) return null;
            Door used = null;
            try { FieldInfo f = CrPbDoorField(self.GetType()); used = f == null ? null : f.GetValue(self) as Door; } catch (Exception) { PatchCounters()[2503]++; }
            return new object[] { l, used };
        }
        static void CrPbAfterCleanup(object[] st)
        {
            if (st == null) return;
            PeLock l = (PeLock)st[0];
            Door used = st[1] as Door;
            if (!PeRestoreLock(ShortcutOn("privateeyecase"), used != null && ReferenceEquals(used, l.Door), l.Type == CommonDoor.tLock.Anybody)) return;
            try
            {
                if (l.Door.HasBeenDestroyed) return;
                l.Door.SetLockTypeAndOwner(l.Type, l.Owner, l.OneWay);
                PatchCounters()[2502]++;
            }
            catch (Exception) { PatchCounters()[2503]++; }
        }
        public class CrPbR0 { public bool RunHook() { CrPbBeforeRun(this); return RunOrig(); } public bool RunOrig() { return false; } }
        public class CrPbR1 { public bool RunHook() { CrPbBeforeRun(this); return RunOrig(); } public bool RunOrig() { return false; } }
        public class CrPbR2 { public bool RunHook() { CrPbBeforeRun(this); return RunOrig(); } public bool RunOrig() { return false; } }
        public class CrPbC0 { public void CleanupHook() { object[] st = CrPbBeforeCleanup(this); CleanupOrig(); CrPbAfterCleanup(st); } public void CleanupOrig() { } }
        public class CrPbC1 { public void CleanupHook() { object[] st = CrPbBeforeCleanup(this); CleanupOrig(); CrPbAfterCleanup(st); } public void CleanupOrig() { } }
        public class CrPbC2 { public void CleanupHook() { object[] st = CrPbBeforeCleanup(this); CleanupOrig(); CrPbAfterCleanup(st); } public void CleanupOrig() { } }

        // modquitguard (kModQuitGuard, default ON since 2026-11-23): Functional Sewing Table's OnWorldQuit throws TypeInitializationException on
        // every quit (Ferry<PersistedData> has no [PersistableStatic] field) [verified, docs/318 643837], and World.OnWorldQuit
        // runs the multicast with no catch, so every handler after it is skipped (EA's AutoSave flag, DynamicChallengeManager,
        // other mods', maybe ours). On: the exception ends at the mod's handler (it had done nothing anyway). Off: rethrown, as
        // now. No Sim is touched. Counters [1585] caught, [1586] seen with the switch off.
        public class CrQg0
        {
            public static void QuitHook(object sender, EventArgs e)
            {
                try { QuitOrig(sender, e); }
                catch (Exception ex)
                {
                    if (!ShortcutOn("modquitguard"))
                    {
                        if (PatchCounters()[1586]++ == 0) DiagLine("modquitguard off: mod quit handler threw " + ex.GetType().Name + ", rethrown");
                        throw;
                    }
                    if (PatchCounters()[1585]++ == 0) DiagLine("modquitguard: caught " + ex.GetType().Name + " in a mod quit handler, " + QuitAfter() + " quit handler(s) after it run");
                }
            }
            public static void QuitOrig(object sender, EventArgs e) { }
            // handlers registered after the Sewing Table one in World's quit event ("?" when the backing field is not found)
            static string QuitAfter()
            {
                try
                {
                    System.Reflection.FieldInfo f = typeof(Sims3.SimIFace.World).GetField("sOnWorldQuitEventHandler", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    Delegate d = f == null ? null : f.GetValue(null) as Delegate;
                    if (d == null) return "?";
                    Delegate[] l = d.GetInvocationList();
                    for (int i = 0; i < l.Length; i++)
                        if (l[i].Method.DeclaringType != null && l[i].Method.DeclaringType.FullName == "Lyralei.GlobalOptionsSewingTable") return (l.Length - 1 - i).ToString();
                    return "?";
                }
                catch (Exception) { return "?"; }
            }
        }

        // failreplan after NRaas GoHere's static Common SimRoutingComponentEx.SetRouteOptions (its routing component's
        // CreateRoute() calls it for every Sim route, never EA's SimRoutingComponent.SetRouteOptions) [verified, V46 binary +
        // source]: the same step as RouteOptionsHook; TownieUnseen leaves household, selected and seen Sims alone.
        public class CrRo0
        {
            public static void OptsHook(SimRoutingComponent ths, Route r, uint ageGenderFlags)
            {
                OptsOrig(ths, r, ageGenderFlags);
                try
                {
                    if (r != null && ths != null && ShortcutOn("failreplan") && TownieUnseen(ths.OwnerSim, null))
                    {
                        r.SetOption(Route.RouteOption.ReplanToFindObstacleWhenPathPlanFails, false);
                        PatchCounters()[29]++;
                    }
                }
                catch (Exception) { }
            }
            public static void OptsOrig(SimRoutingComponent ths, Route r, uint ageGenderFlags) { }
        }

        // woohootywait before NRaas Woohooer's GoToLotSuccessEx (SendWooHootyTextEx, a SendWooHootyText subclass: the same
        // unbounded while (Actor.CurrentInteraction != null) Sleep(5) after the texter's queue is cancelled) [verified, V130].
        // Our steps run first: relationship test, STC, cancel, then the bounded wait (WwPoll, kWwCap, every Sim, docs/289). Given
        // up: the mod's body is skipped (no WooHoo; EA's never got there either). Free: the mod's body runs; its own wait ends at
        // once. Not mapped: KamaSimtraSettings+ServiceData.GoToLotSuccessEx (its guard chain differs; docs/328 §7.5).
        static bool CrWwBefore(object self, Sim sim)
        {
            if (!ShortcutOn("woohootywait")) return true;
            try
            {
                Phone.SendWooHootyText me = self as Phone.SendWooHootyText;
                Sim actor = me == null ? null : me.Actor;
                if (actor == null) return true;  // the mod's body returns at once
                Sims3.Gameplay.Socializing.Relationship rel = Sims3.Gameplay.Socializing.Relationship.Get(actor.SimDescription, me.mOtherSimDesc as SimDescription, false);
                if (rel == null) return true;
                rel.STC.Set(actor, sim, Sims3.Gameplay.Socializing.CommodityTypes.Amorous, 500f);
                actor.InteractionQueue.CancelAllInteractions();
                for (int polls = 0; ; polls++)
                {
                    int r = WwPoll(true, polls, actor.HasBeenDestroyed, sim == null || sim.HasBeenDestroyed, actor.CurrentInteraction != null);
                    if (r == 2) { PatchCounters()[2642]++; return false; }
                    if (r == 0) return true;
                    Simulator.Sleep(5u);
                }
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[2643]++; return true; }
        }
        public class CrWw0 { public void WaitHook(Sim sim, float f) { if (CrWwBefore(this, sim)) WaitOrig(sim, f); } public void WaitOrig(Sim sim, float f) { } }

        // casrowleak on NRaas MasterController's static AddGridItem copy (CASClothingCategoryEx) and its preset row filler
        // (CASClothingRowEx.CreateGridItems): an empty row is neither added to the grid nor disposed, as in EA's [verified, V135].
        // The same depth / recorder / dispose steps as CrlCategoryHook / CrlRowHook (CasRowLeak.cs); its featured branch calls
        // EA's row.CreateGridItems, which CrlRowHook records once the depth is up. CAS only; no Sim state.
        static Type[] CrCasRowArgs(Type t)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != "CreateGridItems" || m.ReturnType != typeof(ArrayList)) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 3 && p[0].ParameterType == typeof(CASClothingRow) && p[2].ParameterType == typeof(bool) && !p[1].ParameterType.IsValueType)
                    return new Type[] { p[0].ParameterType, p[1].ParameterType, p[2].ParameterType };
            }
            return null;
        }
        public class CrCa0
        {
            public static bool AddHook(ItemGrid grid, object current, ResourceKey key, object context)
            {
                sCrlEmpty = null;
                sCrlDepth++;
                bool r;
                try { r = AddOrig(grid, current, key, context); }
                finally { sCrlDepth--; }
                CrlAfterAdd();
                return r;
            }
            public static bool AddOrig(ItemGrid grid, object current, ResourceKey key, object context) { return false; }
        }
        public class CrCr0
        {
            public static ArrayList CreateHook(CASClothingRow row, object parts, bool allowTemp)
            {
                ArrayList r = CreateOrig(row, parts, allowTemp);
                WindowBase w = (object)row as WindowBase;
                if (sCrlDepth > 0 && (r == null || r.Count == 0) && w != null && w.Parent == null) sCrlEmpty = w;
                return r;
            }
            public static ArrayList CreateOrig(CASClothingRow row, object parts, bool allowTemp) { return null; }
        }
    }
}
