using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.DreamsAndPromises;

namespace Ts3Fixes
{
    // dnppetkeys and dnpobjkeys (default off; townie Sims only, household Sims' managers are skipped; errortrapsims' hourly alarm, one switch each; not save data): the wish manager's
    // mPotentialRemovalNodes (subject key -> nodes) is how ObjectDestroyedDelegate finds the nodes of a destroyed subject.
    // AddActiveNode files a new ActiveDreamNode under its subject key for subject types Sim (9), Pet (11), SimOrPet (12),
    // OutOfWorldSim (24) (key: the SimDescription id) and Object (5) (key: the GameObject). RemoveActiveNode(node, true)
    // takes it out again only for Sim, OutOfWorldSim and Object, deriving the Object key from Subject.mObject, which is
    // GameObject.GetObject(id) and null once the object is destroyed. So:
    //  - dnppetkeys: nodes of Pet and SimOrPet subjects are never taken out (RemoveActiveNode has no key for them), so every
    //    pet-subject wish node ever activated stays reachable with its owner Sim and promise chain, per wish event.
    //  - dnpobjkeys: an Object-subject entry whose object was destroyed before its node ended stays, holding the destroyed
    //    GameObject and the node list, because the key cannot be derived any more.
    // Each pass drops, from every manager of a Sim in the world, the listed nodes that are no longer in mActiveNodes (the game
    // already ended them; RemoveActiveNode would have dropped them for the other subject types), for Pet/SimOrPet nodes
    // (dnppetkeys) or for entries whose key object is destroyed (dnpobjkeys), and a key whose list became empty. A node still
    // in mActiveNodes is left alone, so no wish is ended or changed. Counters: [2122] pet/simorpet nodes dropped,
    // [2123] object-key nodes dropped.
    public static partial class Fixes
    {
        static FieldInfo sDrkRemoval, sDrkActive, sDrkSimMgr;
        static bool sDrkTried, sDrkThrew;

        static void DnpRemovalKeysPass()
        {
            try
            {
                bool pets = ShortcutOn("dnppetkeys"), objs = ShortcutOn("dnpobjkeys");
                if (!pets && !objs) return;
                if (!sDrkTried)
                {
                    sDrkTried = true;
                    const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    FieldInfo r = typeof(DreamsAndPromisesManager).GetField("mPotentialRemovalNodes", inst);
                    FieldInfo a = typeof(DreamsAndPromisesManager).GetField("mActiveNodes", inst);
                    FieldInfo m = typeof(Sim).GetField("mDreamsAndPromisesManager", inst);
                    bool ok = r != null && a != null && m != null && typeof(IDictionary).IsAssignableFrom(r.FieldType) && typeof(IList).IsAssignableFrom(a.FieldType);
                    sDrkRemoval = ok ? r : null;
                    sDrkActive = ok ? a : null;
                    sDrkSimMgr = ok ? m : null;
                    AppliedLog().Add("dnppetkeys/dnpobjkeys: " + (ok ? "ok" : "DreamsAndPromisesManager differs from the known build, off"));
                }
                if (sDrkRemoval == null) return;
                int petDrop = 0, objDrop = 0;
                List<object> deadKeys = new List<object>();
                foreach (Sim s in new List<Sim>(Sims3.Gameplay.Queries.GetObjects<Sim>()))
                {
                    if (s == null || s.IsSelectable || s.Household == null || s.Household == Household.ActiveHousehold) continue;  // townie Sims only; unknown = household
                    object mgr = s == null ? null : sDrkSimMgr.GetValue(s);
                    IDictionary table = mgr == null ? null : sDrkRemoval.GetValue(mgr) as IDictionary;
                    IList active = mgr == null ? null : sDrkActive.GetValue(mgr) as IList;
                    if (table == null || active == null || table.Count == 0) continue;
                    deadKeys.Clear();
                    foreach (DictionaryEntry kv in table)
                    {
                        List<ActiveDreamNode> nodes = kv.Value as List<ActiveDreamNode>;
                        if (nodes == null) continue;
                        GameObject go = kv.Key as GameObject;
                        bool objectGone = objs && go != null && go.HasBeenDestroyed;
                        for (int i = nodes.Count - 1; i >= 0; i--)
                        {
                            ActiveDreamNode n = nodes[i];
                            if (n == null || active.Contains(n)) continue;  // a live node stays
                            bool petType = false;
                            if (pets && n.Subject != null)
                                petType = n.Subject.mType == DreamNodePrimitive.InputSubjectType.Pet || n.Subject.mType == DreamNodePrimitive.InputSubjectType.SimOrPet;
                            if (petType) petDrop++;
                            else if (objectGone) objDrop++;
                            else continue;
                            nodes.RemoveAt(i);
                        }
                        if (nodes.Count == 0) deadKeys.Add(kv.Key);
                    }
                    for (int k = 0; k < deadKeys.Count; k++) table.Remove(deadKeys[k]);  // after the walk
                }
                int[] c = PatchCounters();
                c[2122] += petDrop;
                c[2123] += objDrop;
            }
            catch (Exception e)
            {
                if (!sDrkThrew) { sDrkThrew = true; AppliedLog().Add("dnppetkeys/dnpobjkeys: pass threw " + e.Message); }
            }
        }
    }
}
