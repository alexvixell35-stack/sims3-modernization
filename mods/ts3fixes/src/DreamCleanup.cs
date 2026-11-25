using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.DreamsAndPromises;

namespace Ts3Fixes
{
    // dreamcleanup (tuning kDreamCleanup, off by default; docs/580 §17-§18). EA bug fix, memory only: the wish manager's
    // clean-up index (DreamsAndPromisesManager.mPotentialRemovalNodes: subject -> nodes to drop when that Sim or object
    // leaves) is kept wrong in two places [verified, decompiled].
    //   1. AddActiveNode files a node under its subject for Sim, Pet, SimOrPet and OutOfWorldSim subjects (by
    //      SimDescriptionId) and Object subjects. RemoveActiveNode unfiles only Sim, OutOfWorldSim and Object subjects, so a
    //      removed Pet / SimOrPet node stays referenced from the index (no listener: RemoveListenerFromTracker already ran).
    //   1b. For an Object subject RemoveActiveNode derives the key from Subject.mObject, which is null once the object is
    //      destroyed, so a node whose object went first stays filed, holding the destroyed object as the key.
    //   2. Update wakes a repeatable node from mSleepingNodes with mActiveNodes.Add + AddToReferenceList, not AddActiveNode,
    //      so the woken node is not filed again (its earlier RemoveActiveNode unfiled it). If its subject later leaves, the
    //      subject clean-up misses it.
    // Fix at the point where the stale entry is made: (1) after EA's RemoveActiveNode(node, true), a Pet / SimOrPet subject
    // node is taken out of the index under the same key AddActiveNode used, and (1b) an Object-subject node whose object is
    // gone is found in the index (rare path: one scan of the manager's keys) and taken out; this replaces the dnppetkeys /
    // dnpobjkeys hourly sweeps (DnpRemovalKeys.cs) for new state, which then only matter for entries already in a save; (2) after EA's full Update, a node that was sleeping before and is active now is filed
    // as AddActiveNode files it, unless it is already there. Nothing else is touched: no listener, timer, panel list,
    // promise or score; EA decides which wishes exist and when. Household managers included (memory only, approved).
    // Counters: [2804] nodes unfiled (on), [2805] woken nodes filed (on), [2806] either case seen with the switch off,
    // [2807] errors. The Update part shares WishListenerFixes' WlMgr hook (one patch per method). Check: DreamCleanupChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sDcRemoval = typeof(DreamsAndPromisesManager).GetField("mPotentialRemovalNodes", kInstAll);
        static readonly FieldInfo sDcSleeping = typeof(DreamsAndPromisesManager).GetField("mSleepingNodes", kInstAll);
        static readonly FieldInfo sDcActive = typeof(DreamsAndPromisesManager).GetField("mActiveNodes", kInstAll);
        static string sDcMissing = "";

        static void ApplyDreamCleanup()
        {
            sDcMissing = ((sDcRemoval == null ? "mPotentialRemovalNodes " : "") + (sDcSleeping == null ? "mSleepingNodes " : "")
                + (sDcActive == null ? "mActiveNodes " : "")
                + (typeof(DreamsAndPromisesManager).GetMethod("RemoveActiveNode", kInstAll, null, new Type[] { typeof(ActiveNodeBase), typeof(bool) }, null) == null ? "RemoveActiveNode(node, bool)" : "")).Trim();
            if (sDcMissing.Length > 0) { AppliedLog().Add("dreamcleanup: not found: " + sDcMissing); return; }
            Patch(typeof(DreamsAndPromisesManager), "RemoveActiveNode", new Type[] { typeof(ActiveNodeBase), typeof(bool) }, typeof(DcHook), "RemoveHook", "RemoveOrig");
            // Update: WlMgr.UpdateHook (WishListenerFixes.cs) calls DcBeforeUpdate / DcAfterUpdate.
        }

        // ---- pure decisions (also used by the check) ----
        const int kDcNone = 0, kDcById = 1, kDcByObject = 2;

        // AddActiveNode's filing key kind: by SimDescriptionId when the instance's and the subject's types are both Sim, Pet,
        // SimOrPet or OutOfWorldSim (the same one); by object for Object; else not filed.
        internal static int DcFileKind(DreamNodePrimitive.InputSubjectType instanceType, DreamNodePrimitive.InputSubjectType subjectType)
        {
            if (instanceType != subjectType) return kDcNone;
            switch (subjectType)
            {
                case DreamNodePrimitive.InputSubjectType.Sim:
                case DreamNodePrimitive.InputSubjectType.Pet:
                case DreamNodePrimitive.InputSubjectType.SimOrPet:
                case DreamNodePrimitive.InputSubjectType.OutOfWorldSim: return kDcById;
                case DreamNodePrimitive.InputSubjectType.Object: return kDcByObject;
                default: return kDcNone;
            }
        }

        // (1) unfile after EA's remove? Only the cases EA's RemoveActiveNode skips: Pet / SimOrPet subjects (no key in EA's
        // code), and an Object subject whose object is already destroyed (Subject.mObject is then null, so EA has no key).
        internal static bool DcUnfile(bool removeFlag, DreamNodePrimitive.InputSubjectType subjectType, bool objectKeyGone)
        {
            if (!removeFlag) return false;
            if (subjectType == DreamNodePrimitive.InputSubjectType.Pet || subjectType == DreamNodePrimitive.InputSubjectType.SimOrPet) return true;
            return subjectType == DreamNodePrimitive.InputSubjectType.Object && objectKeyGone;
        }

        // the key under which an index list holds this node (the destroyed-object case: EA cannot derive it any more)
        internal static object DcFindKey(IDictionary index, object node)
        {
            // IDictionaryEnumerator's Key / Value, not `foreach (DictionaryEntry ...)`: the game's Mono may hand out boxed
            // KeyValuePairs as Current for a generic Dictionary (the live check returned nothing, 2026-11-15)
            IDictionaryEnumerator en = index.GetEnumerator();
            while (en.MoveNext())
            {
                IList l = en.Value as IList;
                if (l != null && l.Contains(node)) return en.Key;
            }
            return null;
        }

        // (2) file a node after Update? It was sleeping before, is active and not sleeping now, has a filing key, is not filed.
        internal static bool DcRefile(bool wasSleeping, bool nowActive, bool nowSleeping, int fileKind, bool alreadyFiled)
        {
            return wasSleeping && nowActive && !nowSleeping && fileKind != kDcNone && !alreadyFiled;
        }

        // index edits on the manager's dictionary (object -> List<ActiveDreamNode>), as EA's own code does them
        // The game's Mono throws KeyNotFoundException from a generic Dictionary's IDictionary indexer on a missing key
        // (.NET returns null; live 2026-11-15, phase-20261115-2218): every lookup goes through DcIndexList.
        static IList DcIndexList(IDictionary index, object key)
        {
            return key != null && index.Contains(key) ? index[key] as IList : null;
        }

        internal static bool DcIndexRemove(IDictionary index, object key, object node)
        {
            IList l = DcIndexList(index, key);
            if (l == null || !l.Contains(node)) return false;
            l.Remove(node);
            if (l.Count == 0) index.Remove(key);
            return true;
        }

        internal static bool DcIndexHas(IDictionary index, object key, object node)
        {
            IList l = DcIndexList(index, key);
            return l != null && l.Contains(node);
        }

        internal static void DcIndexAdd(IDictionary index, object key, object node, Type listType)
        {
            IList l = DcIndexList(index, key);
            if (l == null) { l = (IList)Activator.CreateInstance(listType); index[key] = l; }
            l.Add(node);
        }

        static object DcKey(ActiveDreamNode n, int kind)
        {
            if (kind == kDcById) return n.Subject.mSimDescriptionId;  // boxed ulong, as AddActiveNode files it
            if (kind == kDcByObject) return n.Subject.mObject;
            return null;
        }

        // ---- hooks ----
        public class DcHook
        {
            public void RemoveHook(ActiveNodeBase node, bool removeFromPotentialRemovalList)
            {
                RemoveOrig(node, removeFromPotentialRemovalList);
                try
                {
                    ActiveDreamNode n = node as ActiveDreamNode;
                    if (n == null || n.Subject == null) return;
                    bool isObj = n.Subject.mType == DreamNodePrimitive.InputSubjectType.Object;
                    if (!DcUnfile(removeFromPotentialRemovalList, n.Subject.mType, isObj && n.Subject.mObject == null)) return;
                    IDictionary index = sDcRemoval.GetValue(this) as IDictionary;
                    if (index == null) return;
                    object key = isObj ? DcFindKey(index, n) : n.Subject.mSimDescriptionId;
                    if (!DcIndexHas(index, key, n)) return;
                    if (!ShortcutOn("dreamcleanup")) { PatchCounters()[2806]++; return; }
                    if (DcIndexRemove(index, key, n)) PatchCounters()[2804]++;
                }
                catch (Exception) { PatchCounters()[2807]++; }
            }
            public void RemoveOrig(ActiveNodeBase node, bool removeFromPotentialRemovalList) { } // receives DreamsAndPromisesManager.RemoveActiveNode(node, bool)'s original body
        }

        // From WlMgr.UpdateHook before EA's Update: the sleeping nodes (null when nothing to do; a copy of a short list).
        internal static List<object> DcBeforeUpdate(object mgr)
        {
            try
            {
                if (sDcSleeping == null) return null;
                IList s = sDcSleeping.GetValue(mgr) as IList;
                if (s == null || s.Count == 0) return null;
                List<object> r = new List<object>(s.Count);
                foreach (object o in s) r.Add(o);
                return r;
            }
            catch (Exception) { PatchCounters()[2807]++; return null; }
        }

        // From WlMgr.UpdateHook after EA's Update: file the nodes that woke.
        internal static void DcAfterUpdate(object mgr, List<object> wasSleeping)
        {
            if (wasSleeping == null) return;
            try
            {
                IList sleeping = sDcSleeping.GetValue(mgr) as IList, active = sDcActive.GetValue(mgr) as IList;
                IDictionary index = sDcRemoval.GetValue(mgr) as IDictionary;
                if (sleeping == null || active == null || index == null) return;
                bool on = ShortcutOn("dreamcleanup");
                Type listType = sDcRemoval.FieldType.GetGenericArguments()[1];
                foreach (object o in wasSleeping)
                {
                    ActiveDreamNode n = o as ActiveDreamNode;
                    if (n == null || n.Subject == null || n.NodeInstance == null) continue;
                    int kind = DcFileKind(n.NodeInstance.SubjectType, n.Subject.mType);
                    object key = DcKey(n, kind);
                    if (key == null || !DcRefile(true, active.Contains(n), sleeping.Contains(n), kind, DcIndexHas(index, key, n))) continue;
                    if (!on) { PatchCounters()[2806]++; continue; }
                    DcIndexAdd(index, key, n, listType);
                    PatchCounters()[2805]++;
                }
            }
            catch (Exception) { PatchCounters()[2807]++; }
        }
    }
}
