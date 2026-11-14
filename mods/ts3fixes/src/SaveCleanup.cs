using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI.Controller;

namespace Ts3Fixes
{
    // Opt-in save cleanup (docs/14, ranks 1 and 2 of docs/09 4b), once per sim day at 2:00 (after the game's relationship
    // decay at 0:00 and its daily genealogy purge; before Overwatch's nightly run), never while saving, travelling or on
    // vacation. Off unless Instantiator.kSaveCleanup or the "savecleanup" test switch is on. The alarm starts the spread
    // pass in SaveCleanupSpread.cs (~2 ms slices); the Run*Once methods below are the one-tick versions for the test agent.
    //   (a) Weak NPC-NPC relationships: what the game's own decay (Relationship.DecayAll / CanRelationshipDecayAway) would
    //       drop if NPC decay were not 0.001x, removed with Relationship.RemoveRelationship (both directions).
    //   (b) MiniSimDescriptions of Sims from other worlds with no family links and no tie to the active household,
    //       removed with MiniSimDescription.RemoveMSD (unlinks the other sides' mini relationships).
    //   (c) Empty life-event lists (docs/14 7a): LifeEventManager keys whose List is empty; the game recreates one on demand.
    //   (d) Stale route callbacks (Sim.OnExitReasonsAdded -> ScriptAdaptor of a finished route action) left by the portal
    //       adaptor leak in existing saves; in PortalAdaptorLeak.cs, counter [55].
    // Counters: [40] relationship pairs removed, [41] MiniSims removed, [42] cleanup runs, [43] runs skipped or failed,
    // [52] empty life-event lists removed, [53] memory pictures removed by (d).
    //   (d) Memory snapshot cap (docs/14 7b): its own tunable Instantiator.kSnapshotCap (0 = off), same 2:00 alarm and skips.
    public static partial class Fixes
    {
        const float kCleanupHour = 2f;
        const float kWeakLikingBand = 10f;    // |liking| at most this; Stranger/Acquaintance already sit below Friend and above Disliked
        const float kQuietDays = 2f;          // not talked for this many sim days
        static AlarmHandle sCleanupAlarm = AlarmHandle.kInvalidHandle;

        static bool SaveCleanupOn()
        {
            ShortcutOn("savecleanup"); // makes sure the switch dictionary exists
            bool on;
            if (sSwitches.TryGetValue("savecleanup", out on)) return on;
            return Instantiator.kSaveCleanup; // not in ShortcutOn's list on purpose: its default there is "on"
        }

        // Called on every world load (Instantiator). The alarm is NeverPersisted, so it is not written into the save.
        public static void StartSaveCleanup()
        {
            try { PlayedWorldLoad(); } catch (Exception) { } // played households are recorded even while every cleanup is off
            if (AlarmManager.Global == null) return;
            if (sCleanupAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sCleanupAlarm);
            sCleanupTask = ObjectGuid.InvalidObjectGuid;
            sCleanupAlarm = AlarmManager.Global.AddAlarmDay(kCleanupHour, DaysOfTheWeek.All, DailySaveCleanup, "Ts3Fixes.SaveCleanup", AlarmType.NeverPersisted, null);
        }

        // The 2:00 alarm starts the spread pass (SaveCleanupSpread.cs): every part runs in ~2 ms slices, one per tick.
        static void DailySaveCleanup()
        {
            SaveCleanupSpreadStart();
        }

        // (c) On demand for the test agent too. Only empty lists go: every reader uses TryGetValue and creates a missing
        // list again (LifeEventManager.ProcessLifeEvent, GetActiveNode), and export/import just walk the dictionaries.
        public static string RunLifeEventTrimOnce()
        {
            int[] c = PatchCounters();
            string unsafeWhy = CleanupUnsafe();
            if (unsafeWhy != null) { c[43]++; return "skipped: " + unsafeWhy; }
            string before = LifeEventCounts();
            int lists = 0;
            foreach (SimDescription d in Household.EverySimDescription()) lists += TrimLifeEventLists(d);
            c[52] += lists;
            string line = "life-event trim: -" + lists + " lists | before " + before + " | after " + LifeEventCounts();
            ShortcutLog(line);
            AppDomain.CurrentDomain.SetData("ts3fixes.lifeeventtrim", line);
            return line;
        }

        static int TrimLifeEventLists(SimDescription d)
        {
            if (d == null || d.LifeEventManager == null) return 0;
            return RemoveEmptyLists(LifeEventDict(d.LifeEventManager, "mLifeEvents")) + RemoveEmptyLists(LifeEventDict(d.LifeEventManager, "mActiveNodes"));
        }

        // (d) On demand for the test agent too (reflect_call with args [cap]). Per active-household Sim, keeps the newest
        // `cap` memory pictures and clears older ones the way LifeEvent.Forget does (DeleteTravelDBResource), but keeps the
        // memory itself: with no snapshot LifeEvent.Snapshot falls back to the event type's stock image. Custom memories
        // (id 0, taken by the player) are never counted or touched; a picture any other LifeEvent still holds is skipped.
        public static string RunSnapshotCapOnce(int cap)
        {
            int[] c = PatchCounters();
            if (cap <= 0) return "off: cap " + cap;
            string unsafeWhy = CleanupUnsafe();
            if (unsafeWhy != null) { c[43]++; return "skipped: " + unsafeWhy; }
            string before = LifeEventCounts();
            Dictionary<ResourceKey, bool> shared = SnapshotHolders();
            int removed = 0, kept = 0;
            foreach (SimDescription d in Household.ActiveHousehold.AllSimDescriptions)
            {
                int r = CapSnapshotsFor(d, cap, shared, ref kept);
                if (r < 0) return "failed: no LifeEvent.UpdateSnapshot";
                removed += r;
            }
            c[53] += removed;
            string line = "snapshot cap " + cap + ": -" + removed + " pictures, " + kept + " shared kept | before " + before + " | after " + LifeEventCounts();
            ShortcutLog(line);
            AppDomain.CurrentDomain.SetData("ts3fixes.snapshotcap", line);
            return line;
        }

        // Keeps the newest `cap` own pictures of one Sim and clears older ones; returns how many, -1 when LifeEvent changed.
        static int CapSnapshotsFor(SimDescription d, int cap, Dictionary<ResourceKey, bool> shared, ref int kept)
        {
            if (d == null || d.LifeEventManager == null) return 0;
            List<Sims3.UI.Hud.ILifeEvent> snaps = new List<Sims3.UI.Hud.ILifeEvent>();
            foreach (Sims3.UI.Hud.ILifeEvent e in d.LifeEventManager.GetVisibleLifeEvents())
                if (e.HasSnapshot && e.Id != 0) snaps.Add(e);
            snaps.Sort(); // LifeEvent.CompareTo orders by mTimestamp, oldest first
            int removed = 0;
            for (int i = 0; i < snaps.Count - cap; i++)
            {
                Sims3.UI.Hud.ILifeEvent e = snaps[i];
                ResourceKey key = e.Snapshot;
                bool isShared;
                if (shared.TryGetValue(key, out isShared) && isShared) { kept++; continue; }
                MethodInfo update = e.GetType().GetMethod("UpdateSnapshot"); // LifeEvent is a private nested class
                if (update == null) return -1;
                update.Invoke(e, new object[] { ResourceKey.kInvalidResourceKey });
                World.DeleteTravelDBResource(key);
                removed++;
            }
            return removed;
        }

        // Every snapshot key held by a LifeEvent (visible or not) of any Sim in any household or urnstone: true when more
        // than one LifeEvent holds it (EventWithScreenshot can hand one picture to several Sims; no script code builds one).
        static Dictionary<ResourceKey, bool> SnapshotHolders()
        {
            Dictionary<ResourceKey, object> owner = new Dictionary<ResourceKey, object>();
            Dictionary<ResourceKey, bool> shared = new Dictionary<ResourceKey, bool>();
            foreach (SimDescription d in SnapshotHolderSims()) AddSnapshotHolders(d, owner, shared);
            return shared;
        }

        static List<SimDescription> SnapshotHolderSims()
        {
            List<SimDescription> all = new List<SimDescription>();
            foreach (Household h in Household.sHouseholdList) all.AddRange(h.AllSimDescriptions); // incl. previous travellers
            all.AddRange(SimDescription.GetHomelessSimDescriptionsFromUrnstones());
            return all;
        }

        static void AddSnapshotHolders(SimDescription d, Dictionary<ResourceKey, object> owner, Dictionary<ResourceKey, bool> shared)
        {
            if (d == null || d.LifeEventManager == null) return;
            System.Collections.IDictionary dict = LifeEventDict(d.LifeEventManager, "mLifeEvents");
            if (dict == null) return;
            foreach (System.Collections.IEnumerable list in dict.Values)
            {
                if (list == null) continue;
                foreach (Sims3.UI.Hud.ILifeEvent e in list)
                {
                    if (e == null || !e.HasSnapshot) continue;
                    object first;
                    if (!owner.TryGetValue(e.Snapshot, out first)) { owner[e.Snapshot] = e; shared[e.Snapshot] = false; }
                    else if (!ReferenceEquals(first, e)) shared[e.Snapshot] = true; // the same Sim listed twice is not sharing
                }
            }
        }

        // lists/empty over both dictionaries of every Sim; events = LifeEvent objects; visible/snaps for the active household.
        public static string LifeEventCounts()
        {
            int lists = 0, empty = 0, events = 0, visible = 0, snaps = 0, snapmax = 0;
            foreach (SimDescription d in Household.EverySimDescription())
            {
                if (d == null || d.LifeEventManager == null) continue;
                foreach (string field in new string[] { "mLifeEvents", "mActiveNodes" })
                {
                    System.Collections.IDictionary dict = LifeEventDict(d.LifeEventManager, field);
                    if (dict == null) continue;
                    foreach (System.Collections.ICollection list in dict.Values)
                    {
                        lists++;
                        if (list == null) continue;
                        if (list.Count == 0) empty++;
                        else if (field == "mLifeEvents") events += list.Count;
                    }
                }
            }
            if (Household.ActiveHousehold != null)
                foreach (SimDescription d in Household.ActiveHousehold.AllSimDescriptions)
                {
                    if (d.LifeEventManager == null) continue;
                    int own = 0;
                    foreach (Sims3.UI.Hud.ILifeEvent e in d.LifeEventManager.GetVisibleLifeEvents())
                    {
                        visible++;
                        if (e.HasSnapshot) snaps++;
                        if (e.HasSnapshot && e.Id != 0) own++; // what the snapshot cap counts
                    }
                    snapmax = Math.Max(snapmax, own);
                }
            return "lists=" + lists + " empty=" + empty + " events=" + events + " visible=" + visible + " snaps=" + snaps + " snapmax=" + snapmax
                + " hh=" + Household.sHouseholdList.Count + " sims=" + Household.EverySimDescription().Count;
        }

        // Private Dictionary<uint, List<T>>; T (LifeEventActiveNode) is a private type, so use the non-generic view.
        static System.Collections.IDictionary LifeEventDict(LifeEventManager m, string field)
        {
            FieldInfo f = typeof(LifeEventManager).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null ? f.GetValue(m) as System.Collections.IDictionary : null;
        }

        static int RemoveEmptyLists(System.Collections.IDictionary dict)
        {
            if (dict == null) return 0;
            List<object> drop = new List<object>();
            foreach (System.Collections.DictionaryEntry kv in dict)
            {
                System.Collections.ICollection list = kv.Value as System.Collections.ICollection;
                if (list != null && list.Count == 0) drop.Add(kv.Key); // null values are left alone: not ours to judge
            }
            foreach (object key in drop) dict.Remove(key);
            return drop.Count;
        }

        // On demand for the test agent (reflect_call); ignores the tunable but keeps every safety check.
        public static string RunSaveCleanupOnce()
        {
            int[] c = PatchCounters();
            string unsafeWhy = CleanupUnsafe();
            if (unsafeWhy != null) { c[43]++; return "skipped: " + unsafeWhy; }
            string before = SaveCleanupCounts();
            int pairs = PruneWeakRelationships(), minis = PruneMiniSims();
            c[40] += pairs; c[41] += minis; c[42]++;
            string line = "save cleanup: -" + pairs + " pairs, -" + minis + " minis | before " + before + " | after " + SaveCleanupCounts();
            ShortcutLog(line);
            AppDomain.CurrentDomain.SetData("ts3fixes.savecleanup", line);
            return line;
        }

        static string CleanupUnsafe()
        {
            if (!GameStates.IsInWorld() || GameStates.IsGameShuttingDown) return "no world";
            if (GameStates.IsTravelling || GameStates.IsOnVacation) return "travelling or on vacation";
            Sims3.Gameplay.UI.OptionsModel options = Sims3.Gameplay.UI.Responder.Instance != null ? Sims3.Gameplay.UI.Responder.Instance.OptionsModel as Sims3.Gameplay.UI.OptionsModel : null;
            if (options != null && options.SaveGameInProgress) return "saving";
            if (Household.ActiveHousehold == null) return "no active household";
            return null;
        }

        // Counts for before/after checks; also callable by the test agent.
        public static string SaveCleanupCounts()
        {
            int keys = 0, pairs = 0;
            foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> kv in Relationship.sAllRelationships)
            {
                keys++;
                pairs += kv.Value.Count;
            }
            Dictionary<ulong, MiniSimDescription> minis = MiniSims();
            return "rel=" + keys + " pairs=" + (pairs / 2) + " mini=" + (minis != null ? minis.Count : 0)
                + " hh=" + Household.sHouseholdList.Count + " sims=" + Household.EverySimDescription().Count;
        }

        static Dictionary<ulong, MiniSimDescription> MiniSims()
        {
            FieldInfo f = typeof(MiniSimDescription).GetField("sMiniSims", BindingFlags.Static | BindingFlags.NonPublic);
            return f != null ? f.GetValue(null) as Dictionary<ulong, MiniSimDescription> : null;
        }

        // (a) Pairs the game's own decay would drop for players (Stranger/Acquaintance, other household, not related, no
        // working relation), plus: both NPCs outside the active household, alive, near-zero liking, quiet for 2 days, never
        // romantic, not partners, not step/in-law/future family, same exemptions as DecayAll (long-distance friend trait,
        // boarding school).
        static int PruneWeakRelationships()
        {
            int n = 0;
            foreach (SimDescription a in new List<SimDescription>(Relationship.sAllRelationships.Keys)) n += PruneWeakFor(a, false);
            return n;
        }

        // The pairs of one key Sim (each pair once, from its lower id) that rule (a) drops; with `aggressive` also the
        // quiet friend-or-weaker rule (SaveCleanupSpread.cs). Collected first, then removed with the game's RemoveRelationship.
        static int PruneWeakFor(SimDescription a, bool aggressive)
        {
            Dictionary<SimDescription, Relationship> mine;
            if (!NpcForCleanup(a) || !Relationship.sAllRelationships.TryGetValue(a, out mine) || mine == null) return 0;
            List<Relationship> drop = new List<Relationship>();
            foreach (KeyValuePair<SimDescription, Relationship> inner in mine)
            {
                SimDescription b = inner.Key;
                if (b == null || a.SimDescriptionId >= b.SimDescriptionId || !NpcForCleanup(b)) continue; // each pair once
                if (WeakPair(inner.Value, a, b)) drop.Add(inner.Value);
                else if (aggressive && QuietFriendPair(inner.Value, a, b))
                {
                    if (PlayedGen(a) >= 0 || PlayedGen(b) >= 0) PatchCounters()[426]++; // played households get the lighter tier
                    else drop.Add(inner.Value);
                }
            }
            int n = 0;
            foreach (Relationship r in drop)
            {
                Dictionary<SimDescription, Relationship> da, db;
                if (!Relationship.sAllRelationships.TryGetValue(r.SimDescriptionA, out da) || !da.ContainsKey(r.SimDescriptionB)) continue;
                if (!Relationship.sAllRelationships.TryGetValue(r.SimDescriptionB, out db) || !db.ContainsKey(r.SimDescriptionA)) continue;
                bool weak = WeakPair(r, r.SimDescriptionA, r.SimDescriptionB);
                SimDescription sa = r.SimDescriptionA, sb = r.SimDescriptionB;
                Relationship.RemoveRelationship(r);
                n++;
                if (!weak) PatchCounters()[417]++; // removed by the aggressive rule only
                CleanupAct("pair", weak ? "basic" : "aggr", sa, sb);
            }
            return n;
        }

        static bool NpcForCleanup(SimDescription d)
        {
            // household rule (PlayerSims.cs): household, selected, household-less and no-active-household descriptions stay
            if (IsPlayerDesc(d) || !d.IsValidDescription || d.IsDead) return false;
            if (d.Genealogy == null || d.TraitManager == null || d.TraitManager.HasElement(TraitNames.LongDistanceFriend)) return false;
            if (d.IsEnrolledInBoardingSchool() || GameStates.IsIdTravelling(d.SimDescriptionId)) return false;
            return true;
        }

        static bool WeakPair(Relationship r, SimDescription a, SimDescription b)
        {
            if (r == null || r.LTR == null || r.SimDescriptionA == null || r.SimDescriptionB == null) return false;
            LongTermRelationshipTypes ltr = r.LTR.CurrentLTR;
            if (ltr != LongTermRelationshipTypes.Stranger && ltr != LongTermRelationshipTypes.Acquaintance) return false;
            if (Math.Abs(r.LTR.Liking) > kWeakLikingBand) return false;
            if (!NoTiesPair(r, a, b)) return false;
            if (SimClock.ElapsedTime(TimeUnit.Hours, r.LTR.WhenStateStarted) < SocialComponent.MinHoursToHoldRelationshipState) return false;
            if (r.WhenLastTalked.Ticks != 0 && SimClock.ElapsedTime(TimeUnit.Days, r.WhenLastTalked) < kQuietDays) return false;
            return true;
        }

        // Guards every relationship rule shares: never romantic, no household, partner, family, in-law or working tie.
        static bool NoTiesPair(Relationship r, SimDescription a, SimDescription b)
        {
            if (r.AreRomantic() || r.WasRomantic()) return false; // any romance visibility state, now or before
            if (a.Household == b.Household || a.Partner == b || b.Partner == a) return false;
            Genealogy ga = a.Genealogy, gb = b.Genealogy;
            if (ga.Spouse == gb || ga.IsSufficientlyRelatedToRuleOutRomance(gb) || gb.IsSufficientlyRelatedToRuleOutRomance(ga)) return false;
            if (Genealogy.IsParentInLaw(ga, gb) || Genealogy.IsParentInLaw(gb, ga) || Genealogy.IsSiblingInLaw(ga, gb)) return false;
            if (Occupation.HaveWorkingRelation(a, b)) return false;
            return true;
        }

        // (b) MiniSims from other worlds: not this world's Sims, no genealogy links, no protection, no partner, not a
        // playable ghost, not travelling, no active-household member in its household list, no mini relationship to or
        // from the active household, and every mini relationship it has is a plain Stranger/Acquaintance one.
        static int PruneMiniSims()
        {
            Dictionary<ulong, bool> keep = MiniSimKeep();
            if (keep == null) return 0;
            int n = 0;
            foreach (ulong id in new List<ulong>(MiniSims().Keys)) if (PruneMiniSim(id, keep)) n++;
            return n;
        }

        // Ids no MiniSim cleanup may remove or leave a tie to; null when sMiniSims is not readable.
        static Dictionary<ulong, bool> MiniSimKeep()
        {
            Dictionary<ulong, MiniSimDescription> minis = MiniSims();
            if (minis == null) return null;
            Dictionary<ulong, bool> keep = new Dictionary<ulong, bool>();
            foreach (SimDescription d in SimDescription.GetSimDescriptionsInWorld()) keep[d.SimDescriptionId] = true;
            if (GameStates.TravelerIds != null) foreach (ulong id in GameStates.TravelerIds) keep[id] = true;
            foreach (SimDescription d in Household.ActiveHousehold.AllSimDescriptions)
            {
                keep[d.SimDescriptionId] = true;
                MiniSimDescription own;
                if (minis.TryGetValue(d.SimDescriptionId, out own) && own != null)
                    foreach (MiniRelationship mr in own.MiniRelationships) keep[mr.SimDescriptionId] = true;
            }
            return keep;
        }

        // One MiniSim, tested at its turn; true when removed.
        static bool PruneMiniSim(ulong id, Dictionary<ulong, bool> keep)
        {
            Dictionary<ulong, MiniSimDescription> minis = MiniSims();
            MiniSimDescription m;
            if (minis == null || !minis.TryGetValue(id, out m) || m == null) return false;
            if (keep.ContainsKey(id) || m.HomeWorld == GameUtils.GetCurrentWorld() || m.Instantiated) return false;
            if (m.ProtectionFlags != MiniSimDescription.ProtectionFlag.None || m.PartnerId != 0 || m.IsPlayableGhost) return false;
            if (HasFamilyLinks(m.Genealogy) || TiedToKept(m, keep)) return false;
            MiniSimDescription.RemoveMSD(id);
            return true;
        }

        static bool HasFamilyLinks(Genealogy g)
        {
            return g != null && (g.Spouse != null || g.Parents.Count > 0 || g.Children.Count > 0 || g.ISiblings.Count > 0);
        }

        static bool TiedToKept(MiniSimDescription m, Dictionary<ulong, bool> keep)
        {
            if (m.HouseholdMembers != null)
                foreach (ulong id in m.HouseholdMembers) if (keep.ContainsKey(id) && id != m.SimDescriptionId) return true;
            foreach (MiniRelationship mr in m.MiniRelationships)
            {
                if (keep.ContainsKey(mr.SimDescriptionId)) return true;
                if (mr.CurrentLtr != LongTermRelationshipTypes.Stranger && mr.CurrentLtr != LongTermRelationshipTypes.Acquaintance
                    && mr.CurrentLtr != LongTermRelationshipTypes.Undefined) return true;
                if (mr.RomanceState != RomanceVisibilityStateType.None) return true;
            }
            return false;
        }
    }
}
