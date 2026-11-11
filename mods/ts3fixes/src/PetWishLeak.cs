using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.DreamsAndPromises;

namespace Ts3Fixes
{
    // petwishleak (tuning kPetWishLeak, default off until measured; docs/191 "pet wish nodes").
    //
    // EA bug: DreamsAndPromisesManager.AddActiveNode files a wish in mPotentialRemovalNodes (the table ObjectDestroyedDelegate
    // reads when a wish's subject dies or is destroyed) by mSimDescriptionId when the subject type is 9, 11, 12 or 24, and
    // by the object for type 5. RemoveActiveNode(node, true) takes it out again only for types 9, 24 and 5 [verified IL:
    // AddActiveNode instructions 14-74, RemoveActiveNode 29-55]. So every fulfilled, cancelled or expired wish whose subject
    // is of type 11 or 12 (the pet subject types) stays in the table, and keeps the dead node alive, until that pet is
    // destroyed or the world is reloaded (PersistPostLoad rebuilds the table).
    //
    // The pass: after RemoveActiveNode(node, true) returns, a wish with subject type 11 or 12 is removed from the list under
    // its mSimDescriptionId, and the list is dropped when empty: the same steps EA's own code does for types 9 and 24.
    // This is household-visible code (only selectable Sims have a wish manager), so exactness matters: in vanilla the stale
    // entry is only ever read by ObjectDestroyedDelegate, which hands it to RemoveActiveNode again. For a node that is
    // already gone that is a no-op the player cannot see: FireRemoveDream and the "DreamFailureTNS" notice fire only for a
    // displayed wish, ActiveDreamNode.BreakPromise acts only while mPromised [verified IL: CancelDream 0-13,
    // ActiveDreamNode.BreakPromise 0-9, ObjectDestroyedDelegate 98-138]. RemoveActiveNode(node, false) (the call from
    // ObjectDestroyedDelegate itself, which is walking the list) is not touched.
    // Counters: [2120] removed pet-subject wishes still left in the table (also with the switch off), [2121] table entries removed, [2122] errors.
    public static partial class Fixes
    {
        static FieldInfo sPwlTable, sPwlType, sPwlId;
        static PropertyInfo sPwlSubject;

        static void ApplyPetWishLeak()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sPwlTable = typeof(DreamsAndPromisesManager).GetField("mPotentialRemovalNodes", inst);
            sPwlSubject = typeof(ActiveNodeBase).GetProperty("Subject", inst);
            Type subj = sPwlSubject != null ? sPwlSubject.PropertyType : null;
            sPwlType = subj != null ? subj.GetField("mType", inst) : null;
            sPwlId = subj != null ? subj.GetField("mSimDescriptionId", inst) : null;
            if (sPwlTable == null || sPwlSubject == null || sPwlType == null || sPwlId == null)
            {
                AppliedLog().Add("petwishleak: members not found (table " + (sPwlTable != null) + ", subject " + (sPwlSubject != null)
                    + ", type " + (sPwlType != null) + ", id " + (sPwlId != null) + "), not patched");
                return;
            }
            Patch(typeof(DreamsAndPromisesManager), "RemoveActiveNode", new Type[] { typeof(ActiveNodeBase), typeof(bool) },
                typeof(PwlHook), "RemoveHook", "RemoveOrig");
        }

        public class PwlHook
        {
            public void RemoveHook(ActiveNodeBase node, bool fromTable)
            {
                RemoveOrig(node, fromTable);
                ActiveDreamNode dn = node as ActiveDreamNode;
                if (!fromTable || dn == null) return;
                int[] c = PatchCounters();
                try
                {
                    object s = sPwlSubject.GetValue(dn, null);
                    if (s == null) return;
                    int t = Convert.ToInt32(sPwlType.GetValue(s));
                    if (t != 11 && t != 12) return;
                    Dictionary<object, List<ActiveDreamNode>> table = sPwlTable.GetValue(this) as Dictionary<object, List<ActiveDreamNode>>;
                    object key = (ulong)sPwlId.GetValue(s);
                    List<ActiveDreamNode> list;
                    // EA files a node only when its NodeInstance.SubjectType equals Subject.mType, so count only nodes
                    // really left in the table (also with the switch off)
                    if (table == null || !table.TryGetValue(key, out list) || !list.Contains(dn)) return;
                    c[2120]++;
                    if (!ShortcutOn("petwishleak")) return;
                    if (list.Remove(dn)) c[2121]++;
                    if (list.Count == 0) table.Remove(key);
                }
                catch (Exception) { c[2122]++; }
            }
            public void RemoveOrig(ActiveNodeBase node, bool fromTable) { } // receives RemoveActiveNode(ActiveNodeBase, bool)'s original header
        }
    }
}
