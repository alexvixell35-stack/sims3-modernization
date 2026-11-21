using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Fireplaces;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // LotManager world callbacks, fireplace herb and trash rummage broadcaster fixes (docs/611, s80). All default off; EA bug
    // fixes as EA wrote the code next to each bug. Household rule: each fix only drops what EA itself stopped using (a
    // duplicate copy of a callback EA meant to hold once, a broadcaster or effect nothing references any more), so the
    // result for household Sims is EA's intended one and there is no exemption (as heartbrokenbcast, docs/609 §7).
    // fireplaceherb gives the second herb EA's full burn time (EA's RemoveHerb then AddHerb, as bonfirespice does).
    public static partial class Fixes
    {
        // ---- docs/611 §2 lotmgrcallbacks ----------------------------------------------------------------------------------
        // [verified, ilspycmd LotManager + World + LoadSaveManager] LotManager.PreWorldLoad adds 55 handlers to World and
        // LoadSaveManager statics; LotManager.Shutdown removes 49 of them. Six are added on every world load and never removed:
        // World.OnPortalLockedCallback (event over the static mOnPortalLocked_Delegate, re-registered with the engine on each
        // change), World.ShouldDoorBeVisibleInLiveModeCallback, World.OnWallsChangedCallback,
        // World.HandToolGetObjectIdForCloneCallback, World.HandToolCloneObjectCallback (public static delegate fields) and
        // LoadSaveManager.ObjectGroupSaving (event on the ScriptCore LoadSaveManager instance). Statics live for the session,
        // so after k world loads (travel, return, load another save) each list holds k copies: every portal-lock query runs
        // Lot.OnPortalPathPlan k times, every glass wall gets k OnWallsChanged, every saved object group is attached k times,
        // and [inferred] a hand-tool clone of an IOverrideHandToolCloneObject runs its clone k times (k - 1 orphans).
        // Fix as EA intended: Shutdown also removes these six (after EA's body), and PreWorldLoad first removes every copy
        // already there (before EA's body adds one), so the list holds exactly one copy and a stacked list is cleaned.
        // Only entries whose method is LotManager's own handler are touched; other mods' handlers stay.
        // Counters: [3435] stacked copies removed at PreWorldLoad, [3436] copies found stacked at PreWorldLoad with the
        // switch off, [3437] copies removed at Shutdown, [3438] errors.
        sealed class LmcSpec
        {
            internal string Name;
            internal FieldInfo Field;        // public static delegate field on World (null for an event)
            internal EventInfo Event;        // static event (null for a field)
            internal MethodInfo Handler;     // LotManager's handler method
            internal Delegate Copy;          // one copy of the handler, of the list's delegate type
        }

        static string sLmcMissing = "";
        static readonly List<LmcSpec> sLmcSpecs = new List<LmcSpec>();
        static FieldInfo sLmcPortalList = typeof(World).GetField("mOnPortalLocked_Delegate", kStaticAll);
        static FieldInfo sLmcLsm = typeof(LoadSaveManager).GetField("gLoadSaveManager", kStaticAll);

        static void ApplyLotMgrCallbacks()
        {
            sLmcSpecs.Clear();
            List<string> miss = new List<string>();
            LmcAdd(miss, "OnWallsChangedCallback", "OnWallsChanged", true);
            LmcAdd(miss, "ShouldDoorBeVisibleInLiveModeCallback", "ShouldDoorBeVisibleInLiveMode", true);
            LmcAdd(miss, "HandToolGetObjectIdForCloneCallback", "HandToolGetObjectIdForClone", true);
            LmcAdd(miss, "HandToolCloneObjectCallback", "HandToolCloneObject", true);
            LmcAdd(miss, "OnPortalLockedCallback", "PortalLockedCallback", false);
            LmcAdd(miss, "ObjectGroupSaving", "LoadSaveManager_ObjectGroupSaving", false);
            if (sLmcPortalList == null) miss.Add("World.mOnPortalLocked_Delegate");
            if (sLmcLsm == null) miss.Add("LoadSaveManager.gLoadSaveManager");
            sLmcMissing = string.Join(", ", miss.ToArray());
            if (sLmcSpecs.Count == 0) { AppliedLog().Add("lotmgrcallbacks: no callback found, not patched"); return; }
            Patch(typeof(LotManager), "PreWorldLoad", typeof(LmcLoadHook), "PreHook", "PreOrig");
            Patch(typeof(LotManager), "Shutdown", typeof(LmcLoadHook), "ShutHook", "ShutOrig");
        }

        static void LmcAdd(List<string> miss, string list, string handler, bool isField)
        {
            LmcSpec s = new LmcSpec();
            s.Name = list;
            s.Handler = typeof(LotManager).GetMethod(handler, kStaticAll);
            Type dt = null;
            if (isField)
            {
                s.Field = typeof(World).GetField(list, kStaticAll);
                if (s.Field != null) dt = s.Field.FieldType;
            }
            else
            {
                s.Event = (list == "ObjectGroupSaving" ? typeof(LoadSaveManager) : typeof(World)).GetEvent(list, kStaticAll);
                if (s.Event != null) dt = s.Event.EventHandlerType;
            }
            if (s.Handler == null || dt == null) { miss.Add(list); return; }
            try { s.Copy = Delegate.CreateDelegate(dt, s.Handler); }
            catch (Exception) { miss.Add(list + " handler"); return; }
            sLmcSpecs.Add(s);
        }

        // Pure: does a list entry belong to the handler? (same method, static: no target)
        static bool LmcIs(Delegate entry, MethodInfo m)
        {
            return entry != null && entry.Target == null && entry.Method.MethodHandle.Value == m.MethodHandle.Value;
        }

        // Pure: copies of m in the list.
        internal static int LmcCount(Delegate list, MethodInfo m)
        {
            if (list == null) return 0;
            int n = 0;
            foreach (Delegate d in list.GetInvocationList()) if (LmcIs(d, m)) n++;
            return n;
        }

        // Pure: the list without any copy of m (same object when there is none; null when nothing is left).
        internal static Delegate LmcStrip(Delegate list, MethodInfo m)
        {
            if (list == null) return null;
            Delegate[] all = list.GetInvocationList();
            List<Delegate> keep = new List<Delegate>(all.Length);
            foreach (Delegate d in all) if (!LmcIs(d, m)) keep.Add(d);
            if (keep.Count == all.Length) return list;
            return keep.Count == 0 ? null : Delegate.Combine(keep.ToArray());
        }

        // The live list of a spec (null when empty or not readable: readable = false).
        static Delegate LmcRead(LmcSpec s, out bool readable)
        {
            readable = true;
            if (s.Field != null) return s.Field.GetValue(null) as Delegate;
            if (s.Event.DeclaringType == typeof(World))
            {
                if (sLmcPortalList == null) { readable = false; return null; }
                return sLmcPortalList.GetValue(null) as Delegate;
            }
            object lsm = sLmcLsm != null ? sLmcLsm.GetValue(null) : null;
            FieldInfo f = lsm != null ? lsm.GetType().GetField(s.Event.Name, kInstAll) : null;
            if (f == null) { readable = false; return null; }
            return f.GetValue(lsm) as Delegate;
        }

        // Removes every copy of the spec's handler from its live list. Returns the copies removed (-1: count unreadable,
        // one remove sent anyway, harmless when the handler is absent).
        static int LmcRemoveAll(LmcSpec s)
        {
            bool readable;
            Delegate list = LmcRead(s, out readable);
            int k = readable ? LmcCount(list, s.Handler) : -1;
            if (k == 0) return 0;
            if (s.Field != null) { s.Field.SetValue(null, LmcStrip(list, s.Handler)); return k; }
            for (int i = 0; i < (k < 0 ? 1 : k); i++) s.Event.RemoveEventHandler(null, s.Copy);   // the event's remove re-registers the engine callback
            return k;
        }

        static void LmcBeforeLoad(bool on)
        {
            int[] c = PatchCounters();
            foreach (LmcSpec s in sLmcSpecs)
            {
                try
                {
                    if (on) { int k = LmcRemoveAll(s); if (k > 0) c[3435] += k; continue; }
                    bool readable;
                    int n = LmcCount(LmcRead(s, out readable), s.Handler);
                    if (n > 0) c[3436] += n;
                }
                catch (ResetException) { throw; }
                catch (Exception) { c[3438]++; }
            }
        }

        static void LmcAfterShutdown()
        {
            int[] c = PatchCounters();
            foreach (LmcSpec s in sLmcSpecs)
            {
                try { int k = LmcRemoveAll(s); if (k > 0) c[3437] += k; }
                catch (ResetException) { throw; }
                catch (Exception) { c[3438]++; }
            }
        }

        public static class LmcLoadHook
        {
            public static void PreHook()
            {
                LmcBeforeLoad(ShortcutOn("lotmgrcallbacks"));
                PreOrig();
            }
            public static void PreOrig() { } // receives LotManager.PreWorldLoad
            public static void ShutHook()
            {
                ShutOrig();
                if (ShortcutOn("lotmgrcallbacks")) LmcAfterShutdown();
            }
            public static void ShutOrig() { } // receives LotManager.Shutdown
        }

        static void LmcCtlA(object sender, EventArgs e) { }
        static void LmcCtlB(object sender, EventArgs e) { }

        // LotMgrCallbacksCheck run: pure model on a private list (another handler A, LotManager-like B), then a replay on
        // copies of the six live lists (delegates are immutable: nothing in the world changes). Replay = a second
        // PreWorldLoad: EA adds one more copy to the list as it is now (off: 2+), ours removes every copy then adds one
        // (on: 1). The live counts are shown (after a travel with the switch off: 2+).
        public static object LotMgrCallbacksCheck(string what)
        {
            string counters = B582Counters("lotmgrcallbacks", 3435, new[] { "stacked copies removed at load", "stacked copies with switch off", "copies removed at shutdown", "errors" }, sLmcMissing);
            if (what == "result") return counters;
            if (what != "run") return "lotmgrcallbacks <run|result>";
            bool on = ShortcutOn("lotmgrcallbacks");
            MethodInfo mb = typeof(Fixes).GetMethod("LmcCtlB", kStaticAll);
            EventHandler a = LmcCtlA, b = LmcCtlB;
            Delegate first = Delegate.Combine(a, b);
            bool eaBug = LmcCount(Delegate.Combine(first, b), mb) == 2;
            Delegate ours = Delegate.Combine(LmcStrip(Delegate.Combine(first, b), mb), b);
            bool pureFixed = LmcCount(ours, mb) == 1 && ours.GetInvocationList().Length == 2;
            // controls: A survives the strip; a list without B comes back as the same object; an empty list stays empty
            bool controls = eaBug && LmcCount(LmcStrip(first, mb), typeof(Fixes).GetMethod("LmcCtlA", kStaticAll)) == 1
                && ReferenceEquals(LmcStrip(a, mb), a) && LmcStrip(null, mb) == null && LmcStrip(b, mb) == null;
            System.Text.StringBuilder live = new System.Text.StringBuilder();
            bool replayOk = sLmcSpecs.Count == 6;
            foreach (LmcSpec s in sLmcSpecs)
            {
                try
                {
                    bool readable;
                    Delegate list = LmcRead(s, out readable);
                    int now = LmcCount(list, s.Handler);
                    int offN = LmcCount(Delegate.Combine(list, s.Copy), s.Handler);
                    int onN = LmcCount(Delegate.Combine(LmcStrip(list, s.Handler), s.Copy), s.Handler);
                    if (readable) replayOk &= on ? onN == 1 : offN >= 2;
                    live.Append(live.Length == 0 ? "" : ", ").Append(s.Name).Append(readable ? " live " + now + " next load " + (on ? onN : offN) : " unreadable");
                }
                catch (Exception e) { replayOk = false; live.Append(", ").Append(s.Name).Append(" threw ").Append(e.GetType().Name); }
            }
            return B582Line("lotmgrcallbacks", on, "a second world load leaves one copy of each LotManager callback", (on ? pureFixed : !eaBug) && replayOk,
                "EA stacks (" + eaBug + "), other handlers kept; replay " + live, controls, sLmcMissing, counters);
        }

        // ---- docs/611 §3 fireplaceherb ------------------------------------------------------------------------------------
        // [verified, ilspycmd Fireplace] BurnHerbInteraction.Run checks Target.HasHerb before routing and the animation, and
        // the herb lands in the event-101 callback (AddHerbCallback -> Fireplace.AddHerb). Two Sims burning a herb at once
        // both pass; the second AddHerb overwrites mAddedHerb, mHerbFX and mHerbEffectBroadcast without stopping or
        // disposing the first ones, and keeps the first burnout alarm (it is only added when none is set). RemoveHerb then
        // frees only the second set: the first herb's fire VFX keeps playing and its haze ReactionBroadcaster stays
        // registered on the lot until the object is reloaded. BonFire's twin (SpiceHitFire) is bonfirespice (docs/561 §6).
        // Fix as EA intended (and as bonfirespice): AddHerb with a herb already burning (HasHerb or a live broadcaster) runs
        // EA's RemoveHerb first, so the new herb replaces it cleanly with its own burnout alarm.
        // Counters: [3439] burning herbs removed before a new one, [3440] left with the switch off, [3441] errors.
        static FieldInfo sFphBcast = typeof(Fireplace).GetField("mHerbEffectBroadcast", kInstAll);
        static FieldInfo sFphParams = typeof(Fireplace).GetField("kAddedHerbBroadcastParams", kStaticAll);
        static string sFphMissing = "";

        static void ApplyFireplaceHerb()
        {
            sFphMissing = sFphBcast == null ? "Fireplace.mHerbEffectBroadcast" : "";
            if (sFphBcast == null) { AppliedLog().Add("fireplaceherb: Fireplace.mHerbEffectBroadcast not found, not patched"); return; }
            Patch(typeof(Fireplace), "AddHerb", new Type[] { typeof(Herb) }, typeof(FphAddHook), "AddHook", "AddOrig");
        }

        // Pure: run EA's RemoveHerb before AddHerb?
        internal static bool FphRemoveFirst(bool on, bool hasHerb, bool hasBcast)
        {
            return on && (hasHerb || hasBcast);
        }

        // The step before EA's AddHerb (hook and check): true when the burning herb was removed.
        static bool FphBefore(Fireplace fp, bool on)
        {
            if (fp == null) return false;
            bool hasHerb = fp.HasHerb, hasBcast = sFphBcast.GetValue(fp) != null;
            if (!FphRemoveFirst(on, hasHerb, hasBcast))
            {
                if (hasHerb || hasBcast) PatchCounters()[3440]++;
                return false;
            }
            fp.RemoveHerb();
            PatchCounters()[3439]++;
            return true;
        }

        public class FphAddHook
        {
            public void AddHook(Herb herb)
            {
                try { FphBefore((object)this as Fireplace, ShortcutOn("fireplaceherb")); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3441]++; }
                AddOrig(herb);
            }
            public void AddOrig(Herb herb) { } // receives Fireplace.AddHerb
        }

        static void FphNoop(Sim s, ReactionBroadcaster rb) { }

        // FireplaceHerbCheck run: pure decision with controls, then a live replay on a world fireplace with no herb burning:
        // broadcaster A set as its herb broadcaster (a first herb), our step, then EA's overwrite with B (the second herb).
        // Off: A still registered. On: A disposed, B kept. The check disposes both and clears the field after.
        public static object FireplaceHerbCheck(string what)
        {
            string counters = B582Counters("fireplaceherb", 3439, new[] { "burning herbs removed first", "left with switch off", "errors" }, sFphMissing);
            if (what == "result") return counters;
            if (what != "run") return "fireplaceherb <run|result>";
            bool on = ShortcutOn("fireplaceherb");
            bool eaBug = !FphRemoveFirst(false, true, true);
            bool fixedPure = on && FphRemoveFirst(on, true, true) && FphRemoveFirst(on, false, true);
            bool controls = eaBug && !FphRemoveFirst(true, false, false);
            string live = "no fireplace without a herb in the world (pure only)";
            bool liveOk = true;
            Fireplace fp = null;
            try
            {
                foreach (Fireplace f in Sims3.Gameplay.Queries.GetObjects<Fireplace>())
                    if (f != null && f.InWorld && !f.HasHerb && sFphBcast != null && sFphBcast.GetValue(f) == null) { fp = f; break; }
            }
            catch (Exception e) { liveOk = false; live = "fireplace scan threw " + e.GetType().Name; }
            if (fp != null)
            {
                ReactionBroadcaster a = null, b = null;
                try
                {
                    int[] c = PatchCounters();
                    int c1 = c[3439], c2 = c[3440];
                    ReactionBroadcasterParams p = (sFphParams != null ? sFphParams.GetValue(null) as ReactionBroadcasterParams : null) ?? new ReactionBroadcasterParams();
                    a = new ReactionBroadcaster(fp, p, FphNoop);
                    sFphBcast.SetValue(fp, a);
                    FphBefore(fp, on);
                    b = new ReactionBroadcaster(fp, p, FphNoop);
                    sFphBcast.SetValue(fp, b);   // EA's overwrite
                    c[3439] = c1; c[3440] = c2;   // the check's own broadcasters are not counted
                    bool aGone = a.BroadcastingObject == null, bKept = b.BroadcastingObject != null;
                    liveOk = bKept && (on ? aGone : !aGone);
                    live = "live replay on fireplace " + fp.ObjectId.Value + ": first " + (aGone ? "disposed" : "still registered") + ", second " + (bKept ? "kept" : "lost");
                }
                catch (Exception e) { liveOk = false; live = "live replay threw " + e.GetType().Name; }
                finally
                {
                    try { if (a != null) a.Dispose(); } catch (Exception) { }
                    try { if (b != null) b.Dispose(); } catch (Exception) { }
                    try { sFphBcast.SetValue(fp, null); } catch (Exception) { }
                }
            }
            return B582Line("fireplaceherb", on, "a second herb removes the burning one first", fixedPure && liveOk && sFphMissing.Length == 0,
                "EA leaves it (" + eaBug + "), no herb untouched, " + live, controls && liveOk, sFphMissing, counters);
        }

        // ---- docs/611 §4 rummagebcast -------------------------------------------------------------------------------------
        // [verified, ilspycmd TrashcanOutside] Rummage.Run creates the can's rummage ReactionBroadcaster
        // (RummageBroadcasterCreate) before DoLoop and destroys it only on the normal path after the loop. Rummage.Cleanup
        // destroys the lid prop only. A reset during the loop (ResetException) therefore leaves the broadcaster registered,
        // and RummagingSim is never cleared, so RummageReactionCallback keeps queueing RummageReaction (insult, STC change)
        // on every Sim passing the can; the next rummage overwrites the field without disposing it. RummageBroadcasterDestroy
        // dereferences the field without a null check. RecycleBinOutdoor's twins are empty.
        // Fix as EA intended: Rummage.Cleanup disposes the broadcaster when this interaction's Sim is the can's rummager
        // (null-safe, the field cleared as RummageBroadcasterDestroy does), and RummageBroadcasterCreate disposes a stale one
        // before EA replaces it (cleans one leaked before the switch was on).
        // Counters: [3442] broadcasters disposed at Cleanup, [3443] stale ones disposed at create, [3444] left with the
        // switch off, [3445] errors.
        static FieldInfo sRmbBcast = typeof(TrashcanOutside).GetField("mRummageBroadcast", kInstAll);
        static FieldInfo sRmbParams = typeof(TrashcanOutside).GetField("kRummageBroadcastParams", kStaticAll);
        static string sRmbMissing = "";

        static void ApplyRummageBcast()
        {
            sRmbMissing = sRmbBcast == null ? "TrashcanOutside.mRummageBroadcast" : "";
            if (sRmbBcast == null) { AppliedLog().Add("rummagebcast: TrashcanOutside.mRummageBroadcast not found, not patched"); return; }
            Patch(typeof(TrashcanOutside.Rummage), "Cleanup", typeof(RmbCleanupHook), "CleanupHook", "CleanupOrig");
            Patch(typeof(TrashcanOutside), "RummageBroadcasterCreate", typeof(RmbCreateHook), "CreateHook", "CreateOrig");
        }

        // Pure: dispose the can's broadcaster? At Cleanup only when this Sim is the can's rummager; at create always.
        internal static bool RmbDispose(bool on, bool hasBcast, bool ownRummager)
        {
            return on && hasBcast && ownRummager;
        }

        // The step (hook and check): disposes and clears the field when RmbDispose says so. True when disposed.
        static bool RmbStep(TrashcanOutside can, bool ownRummager, bool on, int doneSlot)
        {
            if (can == null) return false;
            ReactionBroadcaster rb = sRmbBcast.GetValue(can) as ReactionBroadcaster;
            if (!RmbDispose(on, rb != null, ownRummager))
            {
                if (rb != null && ownRummager) PatchCounters()[3444]++;
                return false;
            }
            sRmbBcast.SetValue(can, null);
            rb.Dispose();
            PatchCounters()[doneSlot]++;
            return true;
        }

        public class RmbCleanupHook
        {
            public void CleanupHook()
            {
                try
                {
                    TrashcanOutside.Rummage r = (object)this as TrashcanOutside.Rummage;
                    TrashcanOutside can = r != null ? r.Target as TrashcanOutside : null;
                    if (can != null) RmbStep(can, r.Actor != null && ReferenceEquals(can.GetRummagingSim(), r.Actor), ShortcutOn("rummagebcast"), 3442);
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3445]++; }
                CleanupOrig();
            }
            public void CleanupOrig() { } // receives TrashcanOutside+Rummage.Cleanup
        }

        public class RmbCreateHook
        {
            public void CreateHook()
            {
                try { RmbStep((object)this as TrashcanOutside, true, ShortcutOn("rummagebcast"), 3443); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3445]++; }
                CreateOrig();
            }
            public void CreateOrig() { } // receives TrashcanOutside.RummageBroadcasterCreate
        }

        // RummageBcastCheck run: pure decision with controls, then a live replay on a world TrashcanOutside with no
        // broadcaster: EA's RummageBroadcasterCreate, then the Cleanup step of a reset rummage by the can's rummager. Off: the
        // broadcaster stays registered; on: disposed and the field cleared. Also counts cans holding a broadcaster whose
        // rummager is not rummaging them now (leaked now) and shows the tuning's TimeDuration (> 0 = expires by itself).
        public static object RummageBcastCheck(string what)
        {
            string counters = B582Counters("rummagebcast", 3442, new[] { "disposed at cleanup", "stale disposed at create", "left with switch off", "errors" }, sRmbMissing);
            if (what == "result") return counters;
            if (what != "run") return "rummagebcast <run|result>";
            bool on = ShortcutOn("rummagebcast");
            bool eaBug = !RmbDispose(false, true, true);
            bool fixedPure = on && RmbDispose(on, true, true);
            bool controls = eaBug && !RmbDispose(true, true, false) && !RmbDispose(true, false, true);
            string live = "no idle TrashcanOutside in the world (pure only)";
            bool liveOk = true;
            int leaked = 0;
            TrashcanOutside can = null;
            try
            {
                foreach (TrashcanOutside t in Sims3.Gameplay.Queries.GetObjects<TrashcanOutside>())
                {
                    if (t == null || sRmbBcast == null) continue;
                    if (sRmbBcast.GetValue(t) == null) { if (can == null && t.InWorld) can = t; continue; }
                    Sim rs = t.GetRummagingSim();
                    Sims3.Gameplay.Interactions.InteractionInstance cur = rs != null ? rs.CurrentInteraction : null;
                    if (!(cur is TrashcanOutside.Rummage) || !ReferenceEquals(cur.Target, t)) leaked++;
                }
            }
            catch (Exception e) { liveOk = false; live = "can scan threw " + e.GetType().Name; }
            if (can != null)
            {
                ReactionBroadcaster a = null;
                try
                {
                    int[] c = PatchCounters();
                    int c1 = c[3442], c2 = c[3443], c3 = c[3444];
                    can.RummageBroadcasterCreate();   // EA's create (our create hook: nothing stale here)
                    a = sRmbBcast.GetValue(can) as ReactionBroadcaster;
                    RmbStep(can, true, on, 3442);      // the reset rummage's Cleanup
                    c[3442] = c1; c[3443] = c2; c[3444] = c3;   // the check's own broadcaster is not counted
                    bool aGone = a != null && a.BroadcastingObject == null, cleared = sRmbBcast.GetValue(can) == null;
                    liveOk = a != null && (on ? aGone && cleared : !aGone && !cleared);
                    live = "live replay on can " + can.ObjectId.Value + ": broadcaster " + (a == null ? "not created" : aGone ? "disposed" : "still registered");
                }
                catch (Exception e) { liveOk = false; live = "live replay threw " + e.GetType().Name; }
                finally
                {
                    try { if (a != null) a.Dispose(); } catch (Exception) { }
                    try { sRmbBcast.SetValue(can, null); } catch (Exception) { }
                }
            }
            string tuning = "";
            try
            {
                ReactionBroadcasterParams p = sRmbParams != null ? sRmbParams.GetValue(null) as ReactionBroadcasterParams : null;
                tuning = p != null ? ", tuning TimeDuration " + p.TimeDuration : ", tuning unreadable";
            }
            catch (Exception) { tuning = ", tuning unreadable"; }
            return B582Line("rummagebcast", on, "a reset rummage disposes the can's broadcaster", fixedPure && liveOk && sRmbMissing.Length == 0,
                "EA leaves it (" + eaBug + "), another Sim's rummage and no broadcaster untouched, " + live + ", leaked now " + leaked + tuning, controls && liveOk, sRmbMissing, counters);
        }
    }
}
