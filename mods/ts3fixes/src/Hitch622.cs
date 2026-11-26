using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using AutonomyT = Sims3.Gameplay.Autonomy.Autonomy;

namespace Ts3Fixes
{
    // docs/622: script CPU hitch fixes from the docs/617 leads, the docs/620 moodlet review and the docs/618 GC leads. Every
    // switch is default off and keeps the answers of the path it replaces. Slots 3490-3513 (3514-3519 kept free in the block).
    // Skipped (docs/622 §0): Stereo RemoveInteractionByType `is` pre-test (FairyHouse+DanceTogetherA+Definition derives from
    // Stereo+DanceTogetherA+Definition), O1 UpdateBuffs rewrite, F3 OnReAddition sign rule, F2 stale paused sources.
    public static partial class Fixes
    {
        // ---- docs/622 §3 swslots: per-generation switch slots for per-tick hooks ----------------------------------------
        // ShortcutOn costs SwCurrent (two compares) plus SwitchCache.Get (a string hash and probe) on every call. A hot site
        // takes a slot once (static readonly int k = SwSlot("name")) and asks SwOn(k): when the SwitchCache built for the
        // current generation and count is the one the slots were filled for, the answer is an array load; else every slot is
        // refilled through ShortcutOn. Switch off: SwOn(k) is ShortcutOn(name). The switch itself is read at the refill.
        // Same answers as ShortcutOn under its own rule (every writer bumps "ts3fixes.switchgen"; docs/605 §2), except a
        // table entry overwritten with no bump by an agent build older than that rule: ShortcutOn reads such names live,
        // the slots see them at the next bump. Counters: [3496] refills, [3497] check mismatches, [3498] check runs.
        static string[] sSlotNames;     // no initializers: static readonly slot fields in other files run SwSlot first
        static bool[] sSlotVal;
        static int sSlotN;
        static SwitchCache sSlotFor;    // the table the slots were filled for; null = refill on the next ask
        static bool sSlotsOn;

        internal static int SwSlot(string name)
        {
            if (sSlotNames == null) { sSlotNames = new string[64]; sSlotVal = new bool[64]; }
            for (int i = 0; i < sSlotN; i++) if (sSlotNames[i] == name) return i;
            if (sSlotN == sSlotNames.Length)
            {
                string[] n = new string[sSlotN * 2]; bool[] v = new bool[sSlotN * 2];
                Array.Copy(sSlotNames, n, sSlotN); Array.Copy(sSlotVal, v, sSlotN);
                sSlotNames = n; sSlotVal = v;
            }
            sSlotNames[sSlotN] = name;
            sSlotFor = null;
            return sSlotN++;
        }

        static bool SwOn(int slot)
        {
            SwitchCache t = sSwCache;
            if (t == null) return ShortcutOn(sSlotNames[slot]);  // not cacheable yet: ShortcutOn renews it when it can
            if (!ReferenceEquals(t, sSlotFor) || t.Gen != sSwGen[0] || t.Count != sSwitches.Count)
                if (!SwSlotsFill()) return ShortcutOn(sSlotNames[slot]);
            return sSlotsOn ? sSlotVal[slot] : ShortcutOn(sSlotNames[slot]);
        }

        static bool SwSlotsFill()
        {
            SwitchCache t = SwCurrent();
            if (t == null) { sSlotFor = null; return false; }
            for (int i = 0; i < sSlotN; i++) sSlotVal[i] = ShortcutOn(sSlotNames[i]);
            sSlotsOn = ShortcutOn("swslots");
            sSlotFor = SwCurrent();  // the table every answer above came from (ShortcutOn renews nothing while it is current)
            PatchCounters()[3496]++;
            return sSlotFor != null;
        }

        public static object SwSlotsCheck(string what)
        {
            string counters = B582Counters("swslots", 3496, new[] { "slot refills", "check mismatches", "check runs" }, "");
            if (what == "result") return counters;
            if (what != "run") return "swslots <run|result>";
            int[] c = PatchCounters();
            c[3498]++;
            bool on = ShortcutOn("swslots");
            int bad = 0, n = sSlotN;
            string firstBad = null;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < n; i++)
                    if (SwOn(i) != ShortcutOnSlow(sSlotNames[i])) { bad++; if (firstBad == null) firstBad = sSlotNames[i]; }
            // flip one slot's name through the table with a bump, as the agent's patches command does
            int probe = SwSlot("swslotsprobe");
            SwTable();
            bool p0 = SwOn(probe);
            sSwitches["swslotsprobe"] = true; SwitchesChanged(); bool p1 = SwOn(probe);
            sSwitches.Remove("swslotsprobe"); SwitchesChanged(); bool p2 = SwOn(probe);
            bool flip = !p0 && p1 && !p2;
            c[3497] += bad;
            uint t0 = SrcNowUs();
            for (int r = 0; r < 50; r++) for (int i = 0; i < n; i++) ShortcutOn(sSlotNames[i]);
            uint t1 = SrcNowUs();
            for (int r = 0; r < 50; r++) for (int i = 0; i < n; i++) SwOn(i);
            uint t2 = SrcNowUs();
            uint calls = (uint)(50 * Math.Max(1, n));
            bool fixedNow = on && bad == 0 && flip && n > 1 && sSlotsOn && sSlotFor != null;
            string ctl = "slots " + n + ", mismatches vs uncached answer " + bad + (firstBad != null ? " (first " + firstBad + ")" : "") + ", bump flip ok=" + flip
                + "; cost per ask: ShortcutOn " + ((t1 - t0) * 1000u / calls) + " ns, SwOn " + ((t2 - t1) * 1000u / calls) + " ns (switch " + (on ? "on: array load" : "off: SwOn is ShortcutOn") + ")";
            return B582Line("swslots", on, "hot sites answered from the slot array, same answers", fixedNow, ctl, bad == 0 && flip, "", counters);
        }

        // Slots of the hot sites (docs/617 §7): per lot per tick, per Sim update, per event, per meta search.
        static readonly int kSwAlarmTime = SwSlot("alarmtime"), kSwDetailPrio = SwSlot("detailprio"), kSwDetailPrioExact = SwSlot("detailprioexact"),
            kSwBuffRemoveNoop = SwSlot("buffremovenoop"), kSwEventCost = SwSlot("eventcost"), kSwBuffBufCheck = SwSlot("buffbufcheck"),
            kSwMotiveBuf = SwSlot("motivebuf"), kSwMotiveBufCheck = SwSlot("motivebufcheck"), kSwAutUnseen = SwSlot("autunseen"),
            kSwAutSeenGame = SwSlot("autseengame"), kSwAutLiveSeen = SwSlot("autliveseen"), kSwAutLiveSeenDry = SwSlot("autliveseendry"),
            kSwAutPaceCredit = SwSlot("autunseenpacecredit"), kSwAutLiveLate = SwSlot("autlivelate"), kSwAutUnseenCmp = SwSlot("autunseencmp"),
            kSwMetaRows = SwSlot("autmetarows"), kSwBrMerge = SwSlot("buffremovemerge"), kSwLifeIdFirst = SwSlot("lifeeventidfirst"),
            kSwBuffSrcEmpty = SwSlot("buffsrcempty"), kSwBuffPausedTimeout = SwSlot("buffpausedtimeout"), kSwMdNoAlloc = SwSlot("mooddriftnoalloc");

        // ---- docs/622 §1 autmetarows: AddObj rows kept per meta object ------------------------------------------------
        // TryRequest (AutShadow.cs) rebuilds every row of every meta object for each sampled search: per ad an
        // InteractionDefinition.GetType() (Mono icall, the profile's top self frame), a sDefIndex probe and, for allow-listed
        // pairs with a scoring function, SfAllowed (reflection, GetInvocationList, Assembly.GetName). Those answers depend only
        // on (a) the object's runtime type (ImplVerdict), (b) the definition object (its type never changes) and (c) the
        // scoring delegate (immutable), plus the guard state that GuardSweep alone rewrites (sDefIndex, sDefGuardHit,
        // sImplGuardHit, sImplVerdict; MarkOwner and Census run inside it). So per object we keep, per ad index, the
        // definition seen there with its verdict and the delegate seen with its SfAllowed answer, all dropped when GuardSweep
        // runs (generation). A row whose ad at that index holds another definition or delegate is recomputed, so list edits
        // (build/buy, load) are seen. The rows posted to the worker are the same; [122] still counts each -3 pair.
        // L3Ok inside SfAllowed is cached until the next GuardSweep, the contract sImplVerdict already has.
        // Counters: [3490] pair rows answered from the cache, [3491] pair rows computed (GetType + probe), [3492] check mismatches.
        sealed class MetaRow
        {
            public int Impl;
            public object[] Def = new object[16];
            public int[] DefV = new int[16];
            public Delegate[] Sf = new Delegate[16];
            public bool[] SfOk = new bool[16];
        }

        sealed class RefEq622 : IEqualityComparer<object>
        {
            public static readonly RefEq622 Instance = new RefEq622();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        static readonly Dictionary<object, MetaRow> sMetaRows = new Dictionary<object, MetaRow>(RefEq622.Instance);  // sim thread only
        static int sMetaRowGen = 1, sMetaRowsAt;
        static bool sMetaRowsOn;  // read once per TryRequest
        const int kMetaRowsMax = 4096;

        static void MetaRowsNewGen() { unchecked { sMetaRowGen++; } }

        static MetaRow MetaRowGet(IMetaObject m)
        {
            if (sMetaRowsAt != sMetaRowGen || sMetaRows.Count >= kMetaRowsMax) { sMetaRows.Clear(); sMetaRowsAt = sMetaRowGen; }
            MetaRow r;
            if (!sMetaRows.TryGetValue(m, out r))
            {
                r = new MetaRow();
                r.Impl = ImplVerdict(m.GetType());
                sMetaRows[m] = r;
            }
            return r;
        }

        // The definition verdict AddObj stores before the scoring-function test: k (allow-listed), -2 (mod guard), -1.
        static int MetaDefVerdict(InteractionDefinition def)
        {
            int k;
            return def != null && sDefIndex.TryGetValue(def.GetType(), out k) ? (sDefGuardHit[k] ? -2 : k) : -1;
        }

        static int MetaRowDef(MetaRow r, int i, InteractionDefinition def, out bool hit)
        {
            hit = false;
            if (def == null) return -1;
            if (i >= r.Def.Length)
            {
                int n = r.Def.Length;
                while (n <= i) n *= 2;
                object[] d = new object[n]; int[] v = new int[n]; Delegate[] s = new Delegate[n]; bool[] ok = new bool[n];
                Array.Copy(r.Def, d, r.Def.Length); Array.Copy(r.DefV, v, r.DefV.Length); Array.Copy(r.Sf, s, r.Sf.Length); Array.Copy(r.SfOk, ok, r.SfOk.Length);
                r.Def = d; r.DefV = v; r.Sf = s; r.SfOk = ok;
            }
            if (ReferenceEquals(r.Def[i], def)) { hit = true; return r.DefV[i]; }
            int verdict = MetaDefVerdict(def);
            r.Def[i] = def; r.DefV[i] = verdict; r.Sf[i] = null;
            return verdict;
        }

        // Called only after MetaRowDef for the same index (arrays sized).
        static bool MetaRowSf(MetaRow r, int i, Delegate f)
        {
            if (ReferenceEquals(r.Sf[i], f)) return r.SfOk[i];
            bool ok = SfAllowed(f);
            r.Sf[i] = f; r.SfOk[i] = ok;
            return ok;
        }

        // MetaRowsCheck run: every public meta object and lot, each ad's verdicts by the uncached code (EA-side path of AddObj)
        // and by the rows, twice (fill, then hits). Off: the cost the request pays per search (GetType calls); on: rows answered.
        public static object MetaRowsCheck(string what)
        {
            string counters = B582Counters("autmetarows", 3490, new[] { "rows from cache", "rows computed", "check mismatches" }, "");
            if (what == "result") return counters;
            if (what != "run") return "autmetarows <run|result>";
            bool on = ShortcutOn("autmetarows");
            string missing = sDefIndex.Count == 0 ? "AutShadow guard sweep not run (no allow-list)" : "";
            int objs = 0, pairs = 0, bad = 0, hits2 = 0, miss2 = 0, sfCalls = 0;
            try
            {
                List<IMetaObject> list = new List<IMetaObject>();
                foreach (IMetaObject m in AutonomyT.PublicMetaObjects) if (m != null && !m.HasBeenDestroyed) list.Add(m);
                foreach (Lot lot in LotManager.AllLots) if (lot != null && !lot.HasBeenDestroyed) list.Add(lot);
                for (int pass = 0; pass < 2; pass++)
                    foreach (IMetaObject m in list)
                    {
                        if (pass == 0) objs++;
                        MetaRow row = MetaRowGet(m);
                        if (row.Impl != ImplVerdict(m.GetType())) bad++;
                        ArrayList ads = m.MetaAds;
                        if (ads == null) continue;
                        for (int i = 0; i < ads.Count; i++)
                        {
                            InteractionObjectPair iop = ads[i] as InteractionObjectPair;
                            if (iop == null) continue;
                            if (pass == 0) pairs++;
                            InteractionDefinition def = iop.InteractionDefinition;
                            bool hit;
                            int ea = MetaDefVerdict(def), ours = MetaRowDef(row, i, def, out hit);
                            if (pass == 1) { if (hit) hits2++; else if (def != null) miss2++; }
                            InteractionTuning tun = iop.Tuning;
                            if (ea >= 0 && tun != null && tun.ScoringFunction != null)
                            {
                                if (pass == 0) sfCalls++;
                                if (SfAllowed(tun.ScoringFunction) != MetaRowSf(row, i, tun.ScoringFunction)) bad++;
                            }
                            if (ea != ours) bad++;
                        }
                    }
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            PatchCounters()[3492] += bad;
            bool fixedNow = on && missing.Length == 0 && bad == 0 && pairs > 0 && miss2 == 0;
            string ctl = "objects " + objs + ", pairs " + pairs + ", mismatches " + bad + "; per request off: " + (pairs + objs) + " GetType + " + pairs
                + " probes + " + sfCalls + " SfAllowed; rows second pass: " + hits2 + " hits, " + miss2 + " recomputed";
            return B582Line("autmetarows", on, "every meta row answered from the cache, same verdicts", fixedNow, ctl, bad == 0 && pairs > 0, missing, counters);
        }

        // ---- docs/622 §2 buffremovemerge: one lookup on the no-op remove path ------------------------------------------
        // [verified, ilspycmd BuffManager] RemoveElement(BuffNames) = RemoveElement((ulong)g, true); RemoveElement(ulong, bool):
        // (1) clear && mBuffCountList.ContainsKey(g) -> remove that entry; (2) a pending mDelayedBuffs timer for g -> remove
        // it, return; (3) base GenericManager.RemoveElement = mValues.TryGetValue + Remove + OnRemoved. So for an absent buff
        // with no delayed timer and (no clear, or no ref-count entry at all) nothing changes. Today a no-op remove through our
        // two hooks is: BuffRemoveHook (ShortcutOn, HasElement, two FieldInfo reads), BBrdHook (HasElement), EA's ContainsKey,
        // EA's enumerator over mDelayedBuffs and base TryGetValue. Merged (switch on, sim thread): BuffRemoveHook passes
        // straight through; BBrdHook does one HasElement and two integer reads (the two private field references are memoized
        // per BuffManager: only the constructors store them, IL stfld in .ctor only) and returns on the no-op; an absent buff
        // that is not a no-op runs EA's body once with no BrPending (buffremovedelayed acts only on live buffs); a live buff
        // takes the buffremovedelayed path with the pending test read from the memo. Counters: [3493] no-op removes returned
        // after one lookup, [3494] removes passed to EA's body, [3495] errors (old path taken).
        static readonly FieldInfo sBrmCountsF = typeof(BuffManager).GetField("mBuffCountList", kInstAll);
        static bool sBrdReady;  // BBrdHook installed (Batch589cLogic.cs ApplyBuffRemoveDelayed)
        static BuffManager sBrmLast;
        static ICollection sBrmCounts;
        static ListSorted<BuffTimer> sBrmDelayed;

        static bool BrmReady() { return sBrdReady && sBrmCountsF != null && sBrdDelayed != null; }

        // 0 = no-op (EA changes nothing), 1 = absent but EA's body must run, 2 = live.
        internal static int BrmRule(bool live, int delayedCount, bool clear, int countEntries)
        {
            if (live) return 2;
            if (delayedCount > 0 || (clear && countEntries > 0)) return 1;
            return 0;
        }

        static int BrmPath(BuffManager bm, ulong guid, bool clear, out ListSorted<BuffTimer> delayed)
        {
            if (!ReferenceEquals(bm, sBrmLast))
            {
                sBrmCounts = sBrmCountsF.GetValue(bm) as ICollection;
                sBrmDelayed = sBrdDelayed.GetValue(bm) as ListSorted<BuffTimer>;
                sBrmLast = bm;
            }
            delayed = sBrmDelayed;
            bool live = ((GenericManager<BuffNames, BuffInstance, BuffInstance>)bm).HasElement(guid);
            return BrmRule(live, delayed != null ? delayed.Count : 0, clear, sBrmCounts != null ? sBrmCounts.Count : 0);
        }

        // EA's pending-timer loop over a list already read.
        static bool BrmPending(ListSorted<BuffTimer> delayed, ulong guid)
        {
            if (delayed == null || delayed.Count == 0) return false;
            foreach (BuffTimer t in delayed) if (t != null && t.Buff == (BuffNames)guid) return true;
            return false;
        }

        // BuffRemoveMergeCheck run: every Sim's BuffManager against every buff in the static table, remove with clear: the
        // merged rule's no-op answer vs EA's own state test (absent, no entry, no pending timer). A no-op claimed where EA
        // would change something is a mismatch. Off: the old touches per no-op call; on: one lookup.
        public static object BuffRemoveMergeCheck(string what)
        {
            string counters = B582Counters("buffremovemerge", 3493, new[] { "no-op removes after one lookup", "removes to EA's body", "errors" },
                BrmReady() ? "" : "BBrdHook / BuffManager fields");
            if (what == "result") return counters;
            if (what != "run") return "buffremovemerge <run|result>";
            bool on = ShortcutOn("buffremovemerge");
            string missing = BrmReady() ? "" : "BBrdHook / BuffManager fields";
            int sims = 0, asks = 0, noops = 0, eaNoops = 0, bad = 0;
            try
            {
                if (missing.Length == 0 && sBuffTable != null)
                    foreach (Sim s in LotManager.Actors)
                    {
                        if (s == null || s.BuffManager == null) continue;
                        BuffManager bm = s.BuffManager;
                        sims++;
                        IDictionary counts = sBrmCountsF.GetValue(bm) as IDictionary;
                        foreach (ulong g in sBuffTable.Keys)
                        {
                            asks++;
                            ListSorted<BuffTimer> delayed;
                            int r = BrmPath(bm, g, true, out delayed);
                            bool ea = !bm.HasElement((BuffNames)g) && !(counts != null && counts.Contains((BuffNames)g)) && !BrmPending(delayed, g);
                            if (r == 0) noops++;
                            if (ea) eaNoops++;
                            if (r == 0 && !ea) bad++;
                        }
                    }
                if (sBuffTable == null) missing = EpJoin(missing, "buff table (permamemo apply)");
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            sBrmLast = null;
            bool pure = BrmRule(false, 0, true, 0) == 0 && BrmRule(false, 0, false, 3) == 0 && BrmRule(false, 1, false, 0) == 1 && BrmRule(false, 0, true, 2) == 1 && BrmRule(true, 0, false, 0) == 2;
            bool fixedNow = on && missing.Length == 0 && bad == 0 && noops > 0 && pure;
            string ctl = "Sims " + sims + ", asks " + asks + ", merged no-ops " + noops + " of EA's " + eaNoops + ", wrong no-ops " + bad + ", rule table ok=" + pure
                + "; per no-op call off: 2 HasElement + 2 FieldInfo reads + ContainsKey + delayed enumerator + TryGetValue, on: 1 HasElement";
            return B582Line("buffremovemerge", on, "no-op removes return after one lookup, never where EA acts", fixedNow, ctl, bad == 0 && pure, missing, counters);
        }

        // ---- docs/622 §5 lifeeventidfirst: SendLifeEvent's id check before the household actor list ---------------------
        // [verified, ilspycmd LifeEventManager::ProcessLifeEvent(Event)] after the enabled and ActiveHousehold tests EA builds
        // Household.ActiveHousehold.AllActors (Members.GetActorsOfType: two new lists, reads of SimDescription.mSim and age /
        // species flags, no side effect [verified]) and only then sData.TryGetValue(e.Id) returns for ids with no life event:
        // most events. Fix: an id absent from sData returns first; any other event (and a null event or table) runs EA's body
        // unchanged. sData is stored only by Startup and Shutdown [verified, the only stsfld sites]; our hooks on both mark the
        // cached key set stale (and the switch stays inert unless all three hooks are in). The second docs/617 §3 item (read
        // AllActors only when an entry is third-party) needs EA's private per-actor overload called by reflection: not done.
        // Counters: [3502] events returned before the list (no life event for the id), [3503] events to EA's body, [3504] errors.
        static FieldInfo sLeDataF;
        static object sLeData;                                            // the sData the id set was built from
        static readonly Dictionary<int, bool> sLeIds = new Dictionary<int, bool>();  // its keys (EventTypeId is an int enum; no boxing per event)
        static bool sLeKnown, sLeInStartup, sLeReady;

        static void ApplyLifeEventIdFirst()
        {
            sLeDataF = typeof(LifeEventManager).GetField("sData", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (sLeDataF == null) { AppliedLog().Add("lifeeventidfirst: not found: LifeEventManager.sData"); return; }
            // all three or the switch stays inert: without the Startup/Shutdown hooks the cached key set could go stale
            Patch(typeof(LifeEventManager), "Startup", Type.EmptyTypes, typeof(LeIdHook), "StartHook", "StartOrig");
            Patch(typeof(LifeEventManager), "Shutdown", Type.EmptyTypes, typeof(LeIdHook), "StopHook", "StopOrig");
            Patch(typeof(LifeEventManager), "ProcessLifeEvent", new Type[] { typeof(Event) }, typeof(LeIdHook), "ProcHook", "ProcOrig");
            int n = EpSwapCount(typeof(LeIdHook));
            sLeReady = n == 3;
            if (!sLeReady) AppliedLog().Add("lifeeventidfirst: " + n + " of 3 hooks in (Startup, Shutdown, ProcessLifeEvent), switch inert");
        }

        // The id set for the live sData; false = no table (EA's body then runs and throws or returns as it does).
        static bool LeIdsNow()
        {
            if (sLeKnown) return sLeData != null;
            object d = sLeDataF.GetValue(null);
            sLeIds.Clear();
            IDictionary dict = d as IDictionary;
            if (dict != null) foreach (object k in dict.Keys) sLeIds[(int)(EventTypeId)k] = true;
            sLeData = dict;
            sLeKnown = true;
            return dict != null;
        }

        // Pure: return before EA's list? (only an id the table does not hold)
        internal static bool LeSkip(bool on, bool eventKnown, bool tableKnown, bool idInTable)
        {
            return on && eventKnown && tableKnown && !idInTable;
        }

        public static class LeIdHook
        {
            public static void ProcHook(Event e)
            {
                if (e != null && sLeReady && !sLeInStartup && SwOn(kSwLifeIdFirst))
                {
                    try
                    {
                        bool table = LeIdsNow();
                        if (LeSkip(true, true, table, table && sLeIds.ContainsKey((int)e.Id))) { PatchCounters()[3502]++; return; }
                        PatchCounters()[3503]++;
                    }
                    catch (Exception) { PatchCounters()[3504]++; }
                }
                ProcOrig(e);
            }
            public static void ProcOrig(Event e) { } // receives LifeEventManager.ProcessLifeEvent(Event)
            public static void StartHook() { sLeKnown = false; sLeInStartup = true; try { StartOrig(); } finally { sLeInStartup = false; sLeKnown = false; } }
            public static void StartOrig() { } // receives LifeEventManager.Startup
            public static void StopHook() { try { StopOrig(); } finally { sLeKnown = false; } }
            public static void StopOrig() { } // receives LifeEventManager.Shutdown
        }

        // LifeEventIdFirstCheck run: every EventTypeId value against the live table (skip answer = EA's early return), plus the
        // AllActors purity control (two reads give the same Sims, the household's member count unchanged).
        public static object LifeEventIdFirstCheck(string what)
        {
            string counters = B582Counters("lifeeventidfirst", 3502, new[] { "events returned before the list", "events to EA's body", "errors" },
                sLeReady ? "" : "LifeEventManager hooks");
            if (what == "result") return counters;
            if (what != "run") return "lifeeventidfirst <run|result>";
            bool on = ShortcutOn("lifeeventidfirst");
            string missing = sLeReady ? "" : "LifeEventManager hooks";
            int ids = 0, skip = 0, bad = 0;
            bool pureOk = false;
            try
            {
                IDictionary d = sLeDataF != null ? sLeDataF.GetValue(null) as IDictionary : null;
                if (d == null) missing = EpJoin(missing, "LifeEventManager.sData (no world)");
                else
                {
                    bool table = LeIdsNow();
                    if (!table || !ReferenceEquals(sLeData, d) || sLeIds.Count != d.Count) bad++;  // cached set stale
                    foreach (EventTypeId id in Enum.GetValues(typeof(EventTypeId)))
                    {
                        ids++;
                        bool ea = d.Contains(id), s = LeSkip(true, true, table, table && sLeIds.ContainsKey((int)id));
                        if (s) skip++;
                        if (s == ea) bad++;  // skip exactly when EA's TryGetValue fails
                    }
                }
                Household h = Household.ActiveHousehold;
                if (h != null)
                {
                    int members = h.AllSimDescriptions.Count;
                    List<Sim> a = h.AllActors, b = h.AllActors;
                    pureOk = a.Count == b.Count && members == h.AllSimDescriptions.Count && !ReferenceEquals(a, b);
                    for (int i = 0; pureOk && i < a.Count; i++) pureOk = ReferenceEquals(a[i], b[i]);
                }
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            bool pure = !LeSkip(false, true, true, false) && !LeSkip(true, false, true, false) && !LeSkip(true, true, false, false) && !LeSkip(true, true, true, true);
            bool fixedNow = on && missing.Length == 0 && bad == 0 && pureOk && pure && skip > 0;
            string ctl = "event ids " + ids + ", without a life event " + skip + " (off: AllActors built for each of those events, on: returned first), wrong answers " + bad
                + ", AllActors pure=" + pureOk + ", rule ok=" + pure + ", hooks ready=" + sLeReady;
            return B582Line("lifeeventidfirst", on, "events with no life event return before AllActors", fixedNow, ctl, bad == 0 && pureOk && pure, missing, counters);
        }

        // ---- docs/622 §6 buffsrcempty (docs/620 F1b): emptied ref-count entries dropped after UpdateBuffs ------------------
        // [verified, ilspycmd BuffManager] BuffSourcesList.TimeoutSources drops timed-out sources but leaves the
        // mBuffCountList entry; UpdateBuffs then times the buff out by guid with clearRefCounts false, so an empty entry stays.
        // Every later UpdateBuffs sets any live copy of that buff to the empty list's MaxTimeout (float.MinValue) and removes
        // it: a later add without an id (Jukebox EnjoyingMusic after a Stereo one, StinkJuice Disgusted) lasts one update.
        // EA deletes the entry itself whenever its last source goes by id (RemoveElement(guid,id,inv), UnpauseBuff: Count == 0
        // -> Remove). Every path that empties an entry [verified, ilspycmd: the only callers of TimeoutSources and
        // BuffSourcesList.Remove]: (1) UpdateBuffs' TimeoutSources(minutes); (2) RemoveElement(guid,id,inv)'s TimeoutSources(0f)
        // after a source is removed (it then re-adds over the empty entry); (3) load: mBuffCountList and BuffSourcesList are
        // persisted, so saves carry entries emptied in earlier sessions. Only UpdateBuffs acts on an empty entry (the MaxTimeout
        // kill). Fix: entries with BuffActiveCount 0 are removed, as EA's id paths do, before EA's UpdateBuffs (2, 3; the
        // first time the switch is seen on in a world, every Sim's) and after it (1). Readers of
        // an empty vs a missing entry answer the same (BuffActiveCount 0, GetBuffSourceList empty, AddElementFromList only
        // after BuffActiveCount > 0 in BuffWarmed). All households (EA intent). Counters: [3505] entries dropped, [3506] empty
        // entries seen by the Check, [3507] errors.
        static readonly List<object> sBseDrop = new List<object>();  // sim thread only
        static object sBseWorld;  // Household.ActiveHousehold of the last world-wide pass (new world or household: pass again)

        // before: the Sim's own entries plus, once per world with the switch on, every Sim's; after: this walk's timeouts.
        static void BseRun(BuffManager bm, bool before)
        {
            try
            {
                int[] c = PatchCounters();
                if (before)
                {
                    object world = Household.ActiveHousehold;
                    if (world != null && !ReferenceEquals(world, sBseWorld))
                    {
                        sBseWorld = world;
                        foreach (Sim s in LotManager.Actors)
                            if (s != null && s.BuffManager != null && !ReferenceEquals(s.BuffManager, bm)) c[3505] += BseSweep(s.BuffManager, true);
                    }
                }
                c[3505] += BseSweep(bm, true);
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3507]++; }
        }

        static int BseSweep(BuffManager bm, bool drop)
        {
            IDictionary counts = sBrmCountsF.GetValue(bm) as IDictionary;
            if (counts == null || counts.Count == 0) return 0;
            sBseDrop.Clear();
            foreach (object k in counts.Keys) if (bm.BuffActiveCount((BuffNames)k) == 0) sBseDrop.Add(k);
            int n = sBseDrop.Count;
            if (drop) for (int i = 0; i < n; i++) counts.Remove(sBseDrop[i]);
            sBseDrop.Clear();
            return n;
        }

        public static object BuffSrcEmptyCheck(string what)
        {
            string counters = B582Counters("buffsrcempty", 3505, new[] { "emptied entries dropped", "empty entries seen by the check", "errors" }, sBrmCountsF == null ? "BuffManager.mBuffCountList" : "");
            if (what == "result") return counters;
            if (what != "run") return "buffsrcempty <run|result>";
            bool on = ShortcutOn("buffsrcempty");
            string missing = sBrmCountsF == null ? "BuffManager.mBuffCountList" : "";
            int sims = 0, empty = 0, entries = 0;
            try
            {
                if (missing.Length == 0)
                    foreach (Sim s in LotManager.Actors)
                    {
                        if (s == null || s.BuffManager == null) continue;
                        sims++;
                        IDictionary counts = sBrmCountsF.GetValue(s.BuffManager) as IDictionary;
                        if (counts != null) entries += counts.Count;
                        empty += BseSweep(s.BuffManager, false);
                    }
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            PatchCounters()[3506] += empty;
            // with the switch on every UpdateBuffs drops them, so a Sim census shows none (a Sim not updated since: at most one)
            bool fixedNow = on && missing.Length == 0 && empty == 0 && sims > 0;
            string ctl = "Sims " + sims + ", ref-count entries " + entries + ", empty entries now " + empty + " (each kills the next id-less add of its buff)";
            return B582Line("buffsrcempty", on, "no empty ref-count entry left after UpdateBuffs", fixedNow, ctl, sims > 0, missing, counters);
        }

        // ---- docs/622 §7 buffpausedtimeout (docs/620 F1a): paused-only re-add with float.MinValue ---------------------------
        // [verified, ilspycmd BuffManager::RemoveElement(BuffNames,ulong,bool)] after one of 2+ sources goes, EA calls
        // TimeoutSources(0f); with only paused sources left (CountOther == 0) MaxTimeout is float.MinValue (TimeoutSources
        // starts at MinValue and looks at mSourcesListOther only), and EA re-adds with AddElementPaused(g, origin, MaxTimeout):
        // the paused instance gets mTimeoutCount = -3.4e38, the tooltip shows it, and when the last source unpauses the buff is
        // removed on the next update instead of running its tuned time. EA's twin path UnpauseBuff re-adds paused-only with
        // AddElementPaused(g, origin) (timeout float.MaxValue = the buff's tuned default). Fix: AddElementPaused(BuffNames,
        // Origin, float) called with exactly float.MinValue gets float.MaxValue, so the call equals UnpauseBuff's. The only other
        // caller of that overload passes StirCrazy's hours * 60 [verified, grep of the decompile]. All households.
        // Counters: [3508] re-adds given the tuned timeout, [3509] MinValue re-adds with the switch off, [3510] errors.
        static bool sBptReady;

        static void ApplyBuffPausedTimeout()
        {
            Patch(typeof(BuffManager), "AddElementPaused", new Type[] { typeof(BuffNames), typeof(Origin), typeof(float) }, typeof(BptHook), "PausedHook", "PausedOrig");
            sBptReady = EpSwapCount(typeof(BptHook)) > 0;
        }

        internal static float BptTimeout(bool on, float timeout)
        {
            return on && timeout == float.MinValue ? float.MaxValue : timeout;
        }

        public class BptHook
        {
            public bool PausedHook(BuffNames buffName, Origin origin, float timeout)
            {
                if (timeout == float.MinValue)
                {
                    try
                    {
                        bool on = SwOn(kSwBuffPausedTimeout);
                        timeout = BptTimeout(on, timeout);
                        PatchCounters()[on ? 3508 : 3509]++;
                    }
                    catch (Exception) { PatchCounters()[3510]++; }
                }
                return PausedOrig(buffName, origin, timeout);
            }
            public bool PausedOrig(BuffNames buffName, Origin origin, float timeout) { return false; } // receives BuffManager.AddElementPaused(BuffNames,Origin,float)
        }

        public static object BuffPausedTimeoutCheck(string what)
        {
            string counters = B582Counters("buffpausedtimeout", 3508, new[] { "re-adds given the tuned timeout", "MinValue re-adds, switch off", "errors" }, sBptReady ? "" : "BuffManager.AddElementPaused hook");
            if (what == "result") return counters;
            if (what != "run") return "buffpausedtimeout <run|result>";
            bool on = ShortcutOn("buffpausedtimeout");
            bool fixedPure = BptTimeout(on, float.MinValue) == float.MaxValue;
            bool controls = BptTimeout(false, float.MinValue) == float.MinValue && BptTimeout(true, 120f) == 120f && BptTimeout(true, -1f) == -1f && BptTimeout(true, float.MaxValue) == float.MaxValue;
            return B582Line("buffpausedtimeout", on, "paused-only re-add gets the tuned timeout as UnpauseBuff's", fixedPure && sBptReady,
                "off keeps MinValue, StirCrazy minutes / -1 / MaxValue kept", controls, sBptReady ? "" : "BuffManager.AddElementPaused hook", counters);
        }

        // ---- docs/622 §8 mooddriftnoalloc: MdHook's per-update sum without allocation -----------------------------------
        // MdHook (MoodDrift.cs) runs after every UpdateBuffs (each Sim, at most once per sim-minute) and allocated three
        // objects each time: new int[n], bm.List (GenericManager.List = mValues.Values: a ValueCollection) and its enumerator
        // boxed through ICollection<T> (docs/618 §2.1). Switch on (sim thread): a static scratch int[] of the same length,
        // cleared, and a foreach over mValues read as Dictionary<ulong, BuffInstance> (struct enumerator, same entries, same
        // version check). Integer sums do not depend on order: same diff, same correction. Counters: [3511] updates summed
        // without allocation, [3512] check mismatches, [3513] reserved (errors stay in mooddrift's [2253]).
        static readonly FieldInfo sMdValues = typeof(GenericManager<BuffNames, BuffInstance, BuffInstance>).GetField("mValues", kInstAll);
        static int[] sMdScratch = new int[0];

        // The live buffs' mEffectValue per axis into sum (length = the target's). EA's walk when vals is null.
        static void MdSum(BuffManager bm, Dictionary<ulong, BuffInstance> vals, int[] sum)
        {
            if (vals != null)
            {
                foreach (KeyValuePair<ulong, BuffInstance> kv in vals)
                {
                    BuffInstance bi = kv.Value;
                    if (bi == null) continue;
                    int ax = (int)bi.mAxisEffected;
                    if (ax >= 0 && ax < sum.Length) sum[ax] += bi.mEffectValue;
                }
                return;
            }
            foreach (BuffInstance bi in bm.List)
            {
                if (bi == null) continue;
                int ax = (int)bi.mAxisEffected;
                if (ax >= 0 && ax < sum.Length) sum[ax] += bi.mEffectValue;
            }
        }

        public static object MoodDriftNoAllocCheck(string what)
        {
            string counters = B582Counters("mooddriftnoalloc", 3511, new[] { "updates summed without allocation", "check mismatches", "reserved" }, sMdValues == null ? "GenericManager.mValues" : "");
            if (what == "result") return counters;
            if (what != "run") return "mooddriftnoalloc <run|result>";
            bool on = ShortcutOn("mooddriftnoalloc");
            string missing = sMdValues == null ? "GenericManager.mValues" : "";
            int sims = 0, bad = 0;
            try
            {
                if (missing.Length == 0)
                    foreach (Sim s in LotManager.Actors)
                    {
                        if (s == null || s.BuffManager == null) continue;
                        sims++;
                        int[] a = new int[8], b = new int[8];
                        MdSum(s.BuffManager, null, a);
                        MdSum(s.BuffManager, sMdValues.GetValue(s.BuffManager) as Dictionary<ulong, BuffInstance>, b);
                        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { bad++; break; }
                    }
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            PatchCounters()[3512] += bad;
            bool fixedNow = on && missing.Length == 0 && bad == 0 && sims > 0;
            string ctl = "Sims " + sims + ", sums differing " + bad + "; per update off: 3 objects (int[], ValueCollection, boxed enumerator), on: 0";
            return B582Line("mooddriftnoalloc", on, "mood sums from the scratch array and struct walk, same totals", fixedNow, ctl, bad == 0 && sims > 0, missing, counters);
        }

        // ---- docs/622 §4 stuckwatchscan: StuckWatchdog's two passes share one Sim scan ----------------------------------
        // Counters: [3499] idle passes on the shared scan, [3500] idle passes on their own scan (switch off), [3501] check mismatches.
        // Logic in RouteFixes.cs (StuckWatchdog.Simulate / Check / CheckIdle).
        public static object StuckWatchScanCheck(string what)
        {
            string counters = B582Counters("stuckwatchscan", 3499, new[] { "idle passes on the shared scan", "idle passes on their own scan (switch off)", "check mismatches" }, "");
            if (what == "result") return counters;
            if (what != "run") return "stuckwatchscan <run|result>";
            bool on = ShortcutOn("stuckwatchscan");
            int n1 = 0, n2 = 0, bad = 0;
            string missing = "";
            try
            {
                // the snapshot the idle pass gets with the switch on (destroyed Sims skipped) vs a fresh query, as with it off
                Sim[] a = Sims3.Gameplay.Queries.GetObjects<Sim>(), b = Sims3.Gameplay.Queries.GetObjects<Sim>();
                Dictionary<object, bool> fresh = new Dictionary<object, bool>(RefEq622.Instance);
                foreach (Sim s in b) if (s != null) { fresh[s] = true; n2++; }
                foreach (Sim s in a) { if (s == null || s.HasBeenDestroyed) continue; n1++; if (!fresh.ContainsKey(s)) bad++; }
                if (n1 != n2) bad++;
            }
            catch (Exception e) { missing = "threw " + e.GetType().Name; }
            PatchCounters()[3501] += bad;
            int[] c = PatchCounters();
            bool fixedNow = on && missing.Length == 0 && bad == 0 && n1 > 0;
            string ctl = "Sims in the shared scan " + n1 + ", in a fresh one " + n2 + ", differing " + bad + "; per round off: 2 world queries + 2 new dictionaries + copies, on: 1 query, dictionaries swapped"
                + "; idle passes so far on the shared scan " + c[3499] + ", on their own " + c[3500];
            return B582Line("stuckwatchscan", on, "both watchdog passes walk one Sim scan", fixedNow, ctl, bad == 0 && n1 > 0, missing, counters);
        }
    }
}
