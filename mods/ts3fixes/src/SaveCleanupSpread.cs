using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI.Controller;

namespace Ts3Fixes
{
    // The daily save cleanup as one spread pass (docs/14 §8): the 2:00 alarm starts one script task that runs each part
    // in ~2 ms slices (EaSpreadCore.kSliceUs, one slice per tick, Simulator.Sleep(0) between), re-checks the world state
    // between slices and every candidate at its turn. Parts by switch:
    //   kSnapshotCap > 0:      snapshot holders (all Sims, sliced), then the active-household cap (docs/14 7b).
    //   savecleanup:           weak NPC pairs (a), other-world MiniSims (b), empty life-event lists (c), stale route
    //                          callbacks (PortalAdaptorLeak.cs).
    //   savecleanupaggressive  (kSaveCleanupAggressive, off; also runs the savecleanup parts):
    //     1 memory pictures of Sims outside the active household (former members, moved-out children; only selectable
    //       Sims ever get one) capped at kAggressiveSnapshotKeep each, the 7b way;
    //     2 history of dead Sims and Sims outside the active household: read-book list, completed and rejected
    //       opportunities, spoiled-gift dates (dead only: minor-pet object relationships);
    //     3 cleanse the dead (stands down when NRaas Overwatch is loaded): graves/urns in a mausoleum whose Sim has no
    //       Sim object, no household, no living or ghost parent/child/sibling and no family or relationship tie to the
    //       active household, destroyed the game's way (Urnstone.Destroy -> Dispose -> SimDescription.Dispose);
    //     4 friend-or-weaker NPC pairs (Stranger, Acquaintance, Disliked, Distant Friend, Friend; same ties guard as (a))
    //       not talked for kAggressiveQuietDays sim days;
    //     5 outfits of dead, non-instantiated humans beyond Naked/Everyday/Special/Supernatural and the default outfit.
    // Counters: [411] passes finished, [412] slices, [413] longest slice us, [414] total ms, [415] passes refused or
    // stopped, [416] pictures removed by 1, [417] pairs removed by 4 only, [418] history entries removed, [419] dead
    // outfits removed, [420] dead Sims disposed, [421] cleanse passes skipped for Overwatch, [422] dead Sims still holding
    // a passport inventory list at the last pass (counted, never touched). Parts (a)-(d) keep their counters [40]-[55].
    // Played households (PlayedHouseholds.cs, §8.6): Sims within kPlayedGenerations of a living member of a household that was
    // ever active get the lighter tier (pictures capped at kPlayedSnapshotKeep, no 2, 3 or 4; 5 still applies); every action
    // logs one ts3native.log line with household id, played flag and generation distance.
    public static partial class Fixes
    {
        static ObjectGuid sCleanupTask = ObjectGuid.InvalidObjectGuid;
        static StopWatch sCleanupWatch;
        static long sCleanupTotalUs;
        static readonly FieldInfo sOppCompleted = typeof(OpportunityHistory).GetField("mCompletedOpportunities", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo sOppRejected = typeof(OpportunityHistory).GetField("mUserRejectedOpportunities", BindingFlags.Instance | BindingFlags.NonPublic);

        static bool AggressiveCleanupOn() { return ShortcutOn("savecleanupaggressive"); }

        // Null when the pass may act; else why not.
        static string SpreadCleanupUnsafe()
        {
            string why = CleanupUnsafe();
            if (why != null) return why;
            if (!GameStates.IsLiveState) return "not live";
            foreach (SimDescription d in Household.ActiveHousehold.AllSimDescriptions)
                if (d == null || !d.IsValidDescription) return "active household member invalid (load not fixed up)";
            return null;
        }

        // Starts the spread pass (2:00 alarm, or the agent). False when nothing is on, one is running, or the state is unsafe.
        public static bool SaveCleanupSpreadStart()
        {
            if (!SaveCleanupOn() && !AggressiveCleanupOn() && Instantiator.kSnapshotCap <= 0) return false;
            if (TaskAlive(sCleanupTask)) return false;
            if (SpreadCleanupUnsafe() != null) { PatchCounters()[43]++; PatchCounters()[415]++; return false; }
            sCleanupTask = Simulator.AddObject(new LoopTask(SaveCleanupSpreadRun));
            return true;
        }

        static bool OverwatchLoaded()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "NRaasOverwatch") return true;
            return false;
        }

        sealed class CleanupPhase
        {
            public Gather Items;
            public Action<object> Step;
            public CleanupPhase(Gather items, Action<object> step) { Items = items; Step = step; }
        }

        static void SaveCleanupSpreadRun()
        {
            int[] c = PatchCounters();
            if (sCleanupWatch == null) sCleanupWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            bool aggr = AggressiveCleanupOn(), basic = aggr || SaveCleanupOn();
            try { PlayedSyncKey(); } catch (Exception) { }
            if (aggr && !sPlayedStoreReady)
            {
                // without the played-household store we cannot tell rotation households apart: aggressive parts stand down
                aggr = false;
                c[415]++;
                DiagLine("savecleanup: aggressive tier off this pass: played-household store unavailable (ts3native / [UserDiag])");
            }
            int cap = Instantiator.kSnapshotCap, keepNpc = Math.Max(0, Instantiator.kAggressiveSnapshotKeep);
            Dictionary<ResourceKey, object> owner = new Dictionary<ResourceKey, object>();
            Dictionary<ResourceKey, bool> shared = new Dictionary<ResourceKey, bool>();
            Dictionary<ulong, bool> keep = null;
            int kept = 0, passport = 0;
            bool cleanse = aggr && !OverwatchLoaded();
            if (aggr && !cleanse) c[421]++;

            List<CleanupPhase> phases = new List<CleanupPhase>();
            phases.Add(new CleanupPhase(delegate { sPlayedNear = BuildPlayedNear(); return new List<object>(); }, delegate(object o) { })); // §8.6, timed as a slice
            if (cap > 0 || aggr)
                phases.Add(new CleanupPhase(delegate { return ToObjects(SnapshotHolderSims()); },
                    delegate(object o) { AddSnapshotHolders(o as SimDescription, owner, shared); }));
            if (cap > 0)
                phases.Add(new CleanupPhase(delegate { return ToObjects(Household.ActiveHousehold.AllSimDescriptions); },
                    delegate(object o)
                    {
                        SimDescription d = o as SimDescription;
                        if (d == null || d.Household != Household.ActiveHousehold) return; // the player switched households mid-pass
                        int r = CapSnapshotsFor(d, cap, shared, ref kept);
                        if (r > 0) { c[53] += r; CleanupAct("pictures-" + r, "cap", d); }
                    }));
            if (aggr)
                phases.Add(new CleanupPhase(delegate { return ToObjects(SnapshotHolderSims()); },
                    delegate(object o)
                    {
                        SimDescription d = o as SimDescription;
                        if (!OutsideActive(d)) return;
                        bool light = PlayedGen(d) >= 0; // played household within kPlayedGenerations: the larger cap
                        int r = CapSnapshotsFor(d, light ? Math.Max(0, Instantiator.kPlayedSnapshotKeep) : keepNpc, shared, ref kept);
                        if (r <= 0) return;
                        c[light ? 425 : 416] += r;
                        CleanupAct("pictures-" + r, light ? "light" : "aggr", d);
                    }));
            if (basic)
            {
                phases.Add(new CleanupPhase(delegate { return ToObjects(Relationship.sAllRelationships.Keys); },
                    delegate(object o) { c[40] += PruneWeakFor(o as SimDescription, aggr); }));
                phases.Add(new CleanupPhase(delegate
                    {
                        keep = MiniSimKeep();
                        Dictionary<ulong, MiniSimDescription> minis = MiniSims();
                        return keep == null || minis == null ? new List<object>() : ToObjects(minis.Keys);
                    },
                    delegate(object o) { if (PruneMiniSim((ulong)o, keep)) c[41]++; }));
                phases.Add(new CleanupPhase(delegate { return ToObjects(Household.EverySimDescription()); },
                    delegate(object o)
                    {
                        int r = TrimLifeEventLists(o as SimDescription);
                        if (r > 0) { c[52] += r; CleanupAct("emptylists-" + r, "basic", o as SimDescription); }
                    }));
                phases.Add(new CleanupPhase(delegate { return Objects<Sim>(); },
                    delegate(object o) { c[55] += RouteAdaptorCleanSim(o as Sim); }));
            }
            if (aggr)
            {
                phases.Add(new CleanupPhase(delegate { return ToObjects(SnapshotHolderSims()); },
                    delegate(object o) { c[418] += TrimHistory(o as SimDescription); }));
                phases.Add(new CleanupPhase(delegate { return Objects<Urnstone>(); },
                    delegate(object o) { c[419] += TrimDeadOutfits(o as Urnstone, ref passport); }));
                if (cleanse)
                    phases.Add(new CleanupPhase(delegate { return Objects<Urnstone>(); },
                        delegate(object o) { if (CleanseDead(o as Urnstone)) c[420]++; }));
            }

            try
            {
                foreach (CleanupPhase phase in phases)
                {
                    sCleanupWatch.Restart();
                    SlicedPass<object> pass = new SlicedPass<object>(phase.Items()); // the gather is part of the first slice
                    CleanupPhase p = phase;
                    while (true)
                    {
                        pass.RunSlice(delegate(object o) { try { p.Step(o); } catch (Exception) { c[415]++; } }, int.MaxValue, EaSpreadCore.kSliceUs, delegate { return sCleanupWatch.GetElapsedTime(); });
                        CleanupSliceDone(c);
                        Simulator.Sleep(0);
                        if (SpreadCleanupUnsafe() != null) { c[43]++; c[415]++; return; } // re-check before acting again
                        if (pass.Done) break;
                        sCleanupWatch.Restart();
                    }
                }
                if (aggr) c[422] = passport;
                if (basic) c[42]++;
                c[411]++;
                ShortcutLog("save cleanup pass: " + phases.Count + " parts, " + (aggr ? "aggressive" : basic ? "basic" : "snapshot cap only") + (aggr && !cleanse ? ", cleanse off (Overwatch)" : ""));
            }
            finally { sCleanupTask = ObjectGuid.InvalidObjectGuid; }
        }

        static void CleanupSliceDone(int[] c)
        {
            long us = sCleanupWatch.GetElapsedTime();
            c[412]++;
            if (us > c[413]) c[413] = (int)Math.Min(us, int.MaxValue);
            sCleanupTotalUs += us;
            c[414] = (int)(sCleanupTotalUs / 1000);
        }

        // A Sim the aggressive parts may trim: dead and not a playable ghost, or alive outside the active household, never
        // selectable, not travelling or at boarding school.
        static bool OutsideActive(SimDescription d)
        {
            if (d == null || !d.IsValidDescription) return false;
            if (Household.ActiveHousehold == null || d.IsPlayableGhost) return false;
            // household rule (PlayerSims.cs), with one deliberate exception: a dead description without a household (a grave)
            // is trimmed although its scope is "unknown"; an alive household-less one stays (IsPlayerDesc)
            if (!(d.IsDead && d.Household == null) && IsPlayerDesc(d)) return false;
            if (d.CreatedSim != null && (d.CreatedSim.IsSelectable || d.CreatedSim.HasBeenDestroyed)) return false;
            return !GameStates.IsIdTravelling(d.SimDescriptionId) && !d.IsEnrolledInBoardingSchool();
        }

        // 2: per-Sim history; returns how many entries went.
        static int TrimHistory(SimDescription d)
        {
            if (!OutsideActive(d)) return 0;
            if (PlayedGen(d) >= 0) { PatchCounters()[426]++; return 0; } // lighter tier: played Sims keep their history
            int n = 0;
            if (d.ReadBookDataList != null) { n += d.ReadBookDataList.Count; d.ReadBookDataList.Clear(); } // ReadBookData re-adds on the next read
            OpportunityHistory h = d.OpportunityHistory;
            if (h != null)
                foreach (FieldInfo f in new FieldInfo[] { sOppCompleted, sOppRejected })
                {
                    IDictionary dict = f != null ? f.GetValue(h) as IDictionary : null;
                    if (dict != null) { n += dict.Count; dict.Clear(); } // current and last opportunities stay
                }
            if (d.SpoiledGiftHistory != null && (d.IsDead || d.TeenOrAbove))
            {
                n += d.SpoiledGiftHistory.Count;
                d.SpoiledGiftHistory = null; // what AgingState does at teen age-up; GiveGift recreates it on demand
            }
            if (d.IsDead && d.GameObjectRelationships != null) { n += d.GameObjectRelationships.Count; d.GameObjectRelationships.Clear(); }
            if (n > 0) CleanupAct("history-" + n, "aggr", d);
            return n;
        }

        // A dead human with no Sim object and no household (a grave or urn), never a playable ghost.
        static SimDescription BuriedSim(Urnstone stone)
        {
            SimDescription d = stone != null && !stone.HasBeenDestroyed ? stone.DeadSimsDescription : null;
            if (d == null || !d.IsValidDescription || !d.IsDead || d.IsPlayableGhost || d.CreatedSim != null || d.Household != null) return null;
            return d;
        }

        // 5: outfits a grave, urn, wandering ghost (default outfit) or resurrection does not use; released the game's way.
        static int TrimDeadOutfits(Urnstone stone, ref int passport)
        {
            SimDescription d = BuriedSim(stone);
            if (d == null) return 0;
            if (d.mInventoryItemsWhileInPassport != null && d.mInventoryItemsWhileInPassport.Count > 0) passport++;
            if (!d.IsHuman) return 0;
            int n = 0;
            ResourceKey def = d.DefaultOutfitKey;
            foreach (uint cat in SaveCleanupCore.kDeadRemovableOutfits)
                foreach (bool maternity in new bool[] { false, true })
                {
                    ArrayList list = d.GetOutfits((Sims3.SimIFace.CAS.OutfitCategories)cat, maternity);
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        Sims3.SimIFace.CAS.SimOutfit o = list[i] as Sims3.SimIFace.CAS.SimOutfit;
                        if (o != null && o.Key == def) continue;
                        d.RemoveOutfit((Sims3.SimIFace.CAS.OutfitCategories)cat, i, true, maternity); // release: only when no other category holds it
                        n++;
                    }
                }
            if (n > 0) CleanupAct("outfits-" + n, "dead", d); // every buried Sim's, played or not (like the active household's dead)
            return n;
        }

        // 3: true when the grave or urn was destroyed.
        static bool CleanseDead(Urnstone stone)
        {
            SimDescription d = BuriedSim(stone);
            if (d == null || GameStates.IsEditTownState || !AggressiveCleanupOn()) return false;
            Inventory inv = Inventory.ParentInventory(stone);
            if (inv == null || !(inv.Owner is IMausoleum)) return false; // only stored graves; placed ones are the player's
            if (HasLiveCloseFamily(d.Genealogy) || TiedToActive(d)) return false;
            if (PlayedGen(d) >= 0) { PatchCounters()[426]++; return false; } // within kPlayedGenerations of a played household
            CleanupAct("cleanse", "aggr", d); // logged first: the description is disposed with the grave
            stone.Destroy(); // GameObject.Destroy leaves the inventory, Urnstone.Dispose disposes the description
            return true;
        }

        // Parents, children, siblings alive or playable ghosts; unknown genealogy counts as family.
        static bool HasLiveCloseFamily(Genealogy g)
        {
            if (g == null) return true;
            List<Genealogy> close = new List<Genealogy>(g.Parents);
            close.AddRange(g.Children);
            close.AddRange(g.GetSiblings());
            foreach (Genealogy r in close)
            {
                if (r == null) continue;
                if (r.IsAlive()) return true;
                if (r.SimDescription != null ? r.SimDescription.IsPlayableGhost : r.IMiniSimDescription != null && r.IMiniSimDescription.IsPlayableGhost) return true;
            }
            return false;
        }

        // Family (spouse, parent, child, sibling) or any relationship entry with an active-household member.
        static bool TiedToActive(SimDescription d)
        {
            Household active = Household.ActiveHousehold;
            if (active == null) return true;
            Genealogy g = d.Genealogy;
            Dictionary<SimDescription, Relationship> rels;
            Relationship.sAllRelationships.TryGetValue(d, out rels);
            List<Genealogy> siblings = g.GetSiblings();
            foreach (SimDescription m in active.AllSimDescriptions)
            {
                if (m == null) continue;
                if (d.Partner == m || m.Partner == d || (rels != null && rels.ContainsKey(m))) return true;
                Genealogy mg = m.Genealogy;
                if (mg != null && (g.Spouse == mg || g.Parents.Contains(mg) || g.Children.Contains(mg) || siblings.Contains(mg))) return true;
            }
            return false;
        }

        // Agent checks (repros bp-multigen-*). "count": candidates per part now (no change). "run": starts the pass.
        // "cleanse": part 3 alone in one tick, also with Overwatch loaded (test saves only; the pass itself stands down).
        public static string SaveCleanupAggressiveCheck(string what)
        {
            switch (what)
            {
                case "run": return "run: started " + SaveCleanupSpreadStart() + " (" + (SpreadCleanupUnsafe() ?? "safe") + ")";
                case "count": return AggressiveCounts();
                case "cleanse":
                {
                    string why = SpreadCleanupUnsafe();
                    if (why == null && !sPlayedStoreReady) why = "played-household store unavailable";
                    if (why != null || !AggressiveCleanupOn()) return "cleanse: refused (" + (why ?? "switch off") + ")";
                    int n = 0;
                    sPlayedNear = BuildPlayedNear();
                    foreach (Urnstone s in Sims3.Gameplay.Queries.GetObjects<Urnstone>()) if (CleanseDead(s)) n++;
                    PatchCounters()[420] += n;
                    return "cleanse: -" + n + " dead Sims" + (OverwatchLoaded() ? " (test check; Overwatch loaded, the pass skips this part)" : "");
                }
                default: return "unknown check " + what + " (count, run, cleanse)";
            }
        }

        static string AggressiveCounts()
        {
            int npcSnaps = 0, history = 0, buried = 0, cleanable = 0, outfits = 0, quietPairs = 0;
            sPlayedNear = BuildPlayedNear();
            string played = "played households " + sPlayed.Count + (sPlayedStoreReady ? " (store " + sPlayedKey + ")" : " (store unavailable: aggressive tier off)")
                + ", Sims within " + Instantiator.kPlayedGenerations + " generations " + sPlayedNear.Count;
            foreach (SimDescription d in SnapshotHolderSims())
            {
                if (!OutsideActive(d)) continue;
                if (d.LifeEventManager != null)
                    foreach (Sims3.UI.Hud.ILifeEvent e in d.LifeEventManager.GetVisibleLifeEvents()) if (e.HasSnapshot && e.Id != 0) npcSnaps++;
                if (d.ReadBookDataList != null) history += d.ReadBookDataList.Count;
            }
            foreach (Urnstone s in Sims3.Gameplay.Queries.GetObjects<Urnstone>())
            {
                SimDescription d = BuriedSim(s);
                if (d == null) continue;
                buried++;
                foreach (uint cat in SaveCleanupCore.kDeadRemovableOutfits) outfits += d.GetOutfitCount((Sims3.SimIFace.CAS.OutfitCategories)cat);
                Inventory inv = Inventory.ParentInventory(s);
                if (inv != null && inv.Owner is IMausoleum && !HasLiveCloseFamily(d.Genealogy) && !TiedToActive(d) && PlayedGen(d) < 0) cleanable++;
            }
            foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> kv in Relationship.sAllRelationships)
            {
                if (!NpcForCleanup(kv.Key) || kv.Value == null) continue;
                foreach (KeyValuePair<SimDescription, Relationship> inner in kv.Value)
                    if (inner.Key != null && kv.Key.SimDescriptionId < inner.Key.SimDescriptionId && NpcForCleanup(inner.Key) && PlayedGen(kv.Key) < 0 && PlayedGen(inner.Key) < 0 && QuietFriendPair(inner.Value, kv.Key, inner.Key)) quietPairs++;
            }
            return "count: npc pictures " + npcSnaps + " (keep " + Instantiator.kAggressiveSnapshotKeep + " each), read-book entries " + history
                + ", buried " + buried + " (removable outfits " + outfits + ", cleanable " + cleanable + (OverwatchLoaded() ? ", Overwatch loaded: cleanse off" : "") + ")"
                + ", quiet friend-or-weaker pairs " + quietPairs + " | " + played + " | " + (SpreadCleanupUnsafe() ?? "safe") + " | " + LifeEventCounts() + " | " + SaveCleanupCounts();
        }

        // 4: friend or weaker, no ties, not talked for kAggressiveQuietDays sim days (or never, with the state that old).
        static bool QuietFriendPair(Relationship r, SimDescription a, SimDescription b)
        {
            if (r == null || r.LTR == null || r.SimDescriptionA == null || r.SimDescriptionB == null) return false;
            switch (r.LTR.CurrentLTR)
            {
                case LongTermRelationshipTypes.Stranger: case LongTermRelationshipTypes.Acquaintance: case LongTermRelationshipTypes.Disliked:
                case LongTermRelationshipTypes.DistantFriend: case LongTermRelationshipTypes.Friend: break;
                default: return false;
            }
            if (!NoTiesPair(r, a, b)) return false;
            bool never = r.WhenLastTalked.Ticks == 0;
            return SaveCleanupCore.QuietFor(never, never ? 0 : SimClock.ElapsedTime(TimeUnit.Days, r.WhenLastTalked),
                SimClock.ElapsedTime(TimeUnit.Days, r.LTR.WhenStateStarted), Instantiator.kAggressiveQuietDays);
        }
    }
}
