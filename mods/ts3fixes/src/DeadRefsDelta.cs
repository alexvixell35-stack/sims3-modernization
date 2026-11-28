using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.MinorPets;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;

namespace Ts3Fixes
{
    // deadrefdelta (docs/336, LC-5 of docs/333; default OFF, needs deadrefsweep + deadrefcadence): a hybrid holder index
    // (DeadRefsCore.DeltaIndex, ids only) so an event-driven object pass runs the owner, relationship-handler and cooking
    // bodies (phases 11, 12, 14) only on the holders of what died:
    //   - owner components keyed by their owner / thief Sim, stepped when kSimDestroyed names that Sim;
    //   - relationship handlers keyed by their SimDescriptionId, stepped when kSimDescriptionDisposed names it or one of its
    //     relationships decays (GameObjectRelationship.RemoveRelationship, hooked);
    //   - cooking handlers (a process lets go of the object without a hook) and holders already gone when noted: unkeyed,
    //     stepped on every delta pass.
    // deadrefslimuse (default OFF, needs deadrefdelta): phase 10 (use, reference, routing-reference lists) walks only objects
    // with a non-empty list, kept by hooks on AddToUseList (+ ServingContainer's override), AddToReferenceList and
    // AddToRoutingReferenceList, plus the 17 direct ActorsUsingMe.Add writers through pins (DeadRefsPin.cs, docs/336 §9):
    // exact by construction; verify still counts use misses (must stay 0).
    // The first pass after a load, the 24 h floor (kDeadRefObjHours) and any pass with an unseeded index walk every object
    // and re-seed. Writers indexed [verified decompiled, docs/336 §2]. Same removals as the full walk for the holders,
    // household Sims included (docs/336 §3). deadrefdeltaverify (test only) walks every object, compares, counts misses.
    // No leak (docs/336 §4): ids only, keys taken when stepped, dead objects pruned every delta pass, reset on every load,
    // capped (pending marks, entries over 2 x live objects + 1024).
    // [1671] delta passes, [1672] holder objects stepped, [1673] index entries, [1674] hook notes, [1675] -, [1676] seed walks,
    // [1677] verify objects checked, [1678] verify holder misses (must stay 0), [1679] unseeds by a hook call with a switch
    // off, [1680] by-Sim entries, [1681] by-desc entries, [1682] unkeyed, [1683] use set, [1684] high-water entries,
    // [1685] high-water use set, [1686] Sims marked, [1687] descriptions marked, [1688] holders stepped from keys,
    // [1689] holders stepped from the unkeyed set, [1690] objects walked by slim use passes, [1691] dead ids pruned,
    // [1692] verify use misses (bypass writers), [1693] verify: keyed entries naming a dead object after the prune (must stay
    // 0), [1694] verify: indexed objects the snapshot does not hold, [1695] resets at load, [1696] overflow resets,
    // [1697] verify: passes with more entries than live objects (must stay 0), [1698] slim use passes.
    public static partial class Fixes
    {
        static readonly DeltaIndex sDrIdx = new DeltaIndex();
        static bool sDrIdxVerify, sDrIdxCook, sDrSlim;
        static Dictionary<ulong, bool> sDrHolders, sDrUseTaken, sDrVerifyLeft;

        static void ApplyDeadRefDelta()
        {
            Patch(typeof(GameObject), "SetStealActors", typeof(DrStealHook), "StealHook", "StealOrig");
            Patch(typeof(ObjectOwnerComponent), "ImportContent", new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamReader) }, typeof(DrOwnerImportHook), "ImportHook", "ImportOrig");
            Patch(typeof(Vehicle), "GetNewRelationship", typeof(DrCarRelHook), "NewHook", "NewOrig");
            Patch(typeof(MinorPetBirdLarge), "GetNewRelationship", typeof(DrBirdRelHook), "NewHook", "NewOrig");
            Patch(typeof(CookingProcess), "AddObjectListeners", typeof(DrCookAddHook), "AddHook", "AddOrig");
            Patch(typeof(GameObjectRelationship), "RemoveRelationship", typeof(DrRelDecayHook), "DecayHook", "DecayOrig");
            Patch(typeof(GameObject), "AddToReferenceList", new Type[] { typeof(Sim) }, typeof(DrRefAddHook), "AddHook", "AddOrig");
            Patch(typeof(GameObject), "AddToRoutingReferenceList", new Type[] { typeof(Sim) }, typeof(DrRouteRefAddHook), "AddHook", "AddOrig");
            Patch(typeof(GameObject), "AddToUseList", new Type[] { typeof(Sim) }, typeof(DrUseAddHook), "AddHook", "AddOrig");
            Patch(typeof(ServingContainer), "AddToUseList", new Type[] { typeof(Sim) }, typeof(DrServeUseAddHook), "AddHook", "AddOrig");
            ApplyDeadRefPins(); // DeadRefsPin.cs, docs/336 §9: the 17 direct ActorsUsingMe.Add writers
        }

        static void DrIdxGauges(int[] c)
        {
            c[1673] = sDrIdx.Entries; c[1680] = sDrIdx.SimEntries; c[1681] = sDrIdx.DescEntries; c[1682] = sDrIdx.UnkeyedCount;
            c[1683] = sDrIdx.UseCount; c[1684] = sDrIdx.HighEntries; c[1685] = sDrIdx.HighUse; c[1696] = sDrIdx.Overflows;
        }

        // world load (StartDeadRefSweep): a new world, nothing of the old one stays indexed
        static void DrIdxLoadReset()
        {
            int[] c = PatchCounters();
            sDrIdx.Reset(); sDrHolders = sDrUseTaken = sDrVerifyLeft = null;
            DrPinLoadReset(); DrPlLoadReset(); // docs/336 §9
            c[1695]++; DrIdxGauges(c);
        }

        static bool DrIdxLive() { return sDrIdx.Seeded || sDrIdx.Seeding; }

        // A hook call with a switch off: the index misses this write, so it is unseeded until the next full walk.
        static void DrIdxUnseed(int[] c) { if (DrIdxLive()) { sDrIdx.Reset(); c[1679]++; } }

        // A holder writer touched o (hooks; our own DrGiveReward).
        static void DrIdxNote(GameObject o)
        {
            if (o == null || !DrIdxLive()) return;
            int[] c = PatchCounters();
            if (!ShortcutOn("deadrefdelta")) { DrIdxUnseed(c); return; }
            try { DrIdxNoteHolders(o); c[1674]++; } catch (Exception) { c[410]++; }
            DrIdxGauges(c);
        }

        // A use-list writer touched o.
        static void DrIdxNoteUseW(GameObject o)
        {
            if (o == null || !DrIdxLive() || !sDrIdx.SeedSlim) return;
            int[] c = PatchCounters();
            if (!ShortcutOn("deadrefdelta") || !ShortcutOn("deadrefslimuse")) { DrIdxUnseed(c); return; }
            sDrIdx.NoteUse(o.ObjectId.Value);
        }

        static void DrIdxOwnerSim(Sim s, ulong id)
        {
            if (s == null) return;
            if (s.HasBeenDestroyed) sDrIdx.NoteUnkeyed(id); // no event will name it again: step it next pass
            else sDrIdx.NoteSim(s.ObjectId.Value, id);
        }

        // Keys of everything o holds now: owner / thief Sim, the description of each live relationship handler, unkeyed
        // for cooking handlers and for relationship handlers already gone.
        static void DrIdxNoteHolders(GameObject o)
        {
            ulong id = o.ObjectId.Value;
            ObjectOwnerComponent oc = o.ObjectOwnerComponent;
            if (oc != null && sOwnerOwner != null && sOwnerThief != null)
            {
                DrIdxOwnerSim(sOwnerOwner.GetValue(oc) as Sim, id);
                DrIdxOwnerSim(sOwnerThief.GetValue(oc) as Sim, id);
            }
            Delegate ev = sObjDisposed != null ? sObjDisposed.GetValue(o) as Delegate : null;
            if (ev == null) return;
            foreach (Delegate d in ev.GetInvocationList())
            {
                if (d.Target is CookingProcess) { sDrIdx.NoteUnkeyed(id); continue; }
                GameObjectRelationship r = d.Target as GameObjectRelationship;
                if (r == null) continue;
                SimDescription desc = sRelDesc != null ? sRelDesc.GetValue(r) as SimDescription : null;
                if (desc == null || RelGone(r)) sDrIdx.NoteUnkeyed(id);
                else sDrIdx.NoteDesc(desc.SimDescriptionId, id);
            }
        }

        static bool DrUseHeld(GameObject o)
        {
            List<Sim> routing = o.RoutingReferenceList;
            return o.ActorsUsingMe.Count > 0 || o.ReferenceList.Count > 0 || (routing != null && routing.Count > 0);
        }

        // seed walk (DrObjectStep with sDrIdxSeeding): every holder and every non-empty use list
        static void DrIdxSeedNote(GameObject o)
        {
            DrIdxNoteHolders(o);
            if (sDrIdx.SeedSlim && DrUseHeld(o)) sDrIdx.NoteUse(o.ObjectId.Value);
        }

        static bool DrDeadId(ulong id)
        {
            GameObject g = GameObject.GetObject(new ObjectGuid(id));
            return g == null || g.HasBeenDestroyed;
        }

        // A delta pass starts (phase 10, DeadRefSweepRun): take the holders of what died plus the unkeyed set, prune dead ids,
        // take the use set (slim), apply the caps.
        static void DrDeltaBegin(bool slim, int liveObjects)
        {
            int[] c = PatchCounters();
            c[1671]++;
            sDrSlim = slim;
            sDrIdxVerify = ShortcutOn("deadrefdeltaverify");
            sDrIdxCook = ShortcutOn("deadrefcooking");
            c[1691] += sDrIdx.Prune(DrDeadId, false);
            if (sDrIdxVerify)
            {
                c[1693] += sDrIdx.Prune(DrDeadId, true);
                if (liveObjects > 0 && sDrIdx.Entries > liveObjects) c[1697]++;
            }
            int fromKeys, fromUnkeyed;
            sDrHolders = sDrIdx.TakeHolders(out fromKeys, out fromUnkeyed);
            c[1688] += fromKeys; c[1689] += fromUnkeyed;
            sDrUseTaken = slim ? sDrIdx.TakeUse() : null;
            sDrUseAll = false;
            if (slim) DrPinsIntoPass(c); // docs/336 §9: objects a direct writer may be writing right now
            sDrVerifyLeft = null;
            if (sDrIdxVerify)
            {
                sDrVerifyLeft = new Dictionary<ulong, bool>(sDrHolders);
                if (sDrUseTaken != null) foreach (ulong id in sDrUseTaken.Keys) sDrVerifyLeft[id] = true;
            }
            if (liveObjects > 0) sDrIdx.OverCap(liveObjects); // too big for the world: reset, the next pass walks again
            DrIdxGauges(c);
        }

        // The objects phase 10 steps on a delta pass: every object (verify, or deadrefslimuse off) or only the taken use set and
        // holders (slim).
        static List<object> DrDeltaItems(Gather walk)
        {
            if (sDrIdxVerify || !sDrSlim || sDrUseAll) return walk(); // sDrUseAll: a pin over its cap (docs/336 §9)
            int[] c = PatchCounters();
            Dictionary<ulong, bool> ids = new Dictionary<ulong, bool>(sDrUseTaken);
            foreach (ulong id in sDrHolders.Keys) ids[id] = true;
            List<object> l = new List<object>(ids.Count);
            foreach (ulong id in ids.Keys) { GameObject g = GameObject.GetObject(new ObjectGuid(id)); if (g != null) l.Add(g); }
            c[1698]++; c[1690] += l.Count;
            return l;
        }

        // phase 10 of a delta pass finished
        static void DrDeltaEnd()
        {
            int[] c = PatchCounters();
            if (sDrVerifyLeft != null) foreach (ulong id in sDrVerifyLeft.Keys) if (!DrDeadId(id)) c[1694]++;
            sDrHolders = sDrUseTaken = sDrVerifyLeft = null;
            DrIdxGauges(c);
        }

        // The object step of a delta pass: DrObjectStep's bodies and tries, phase 10 on use objects (all objects when slim is
        // off), phases 11, 12, 14 on taken holders; what the object still holds is noted again. Verify (every object walked):
        // an object the delta pass would skip is tested with the read-only Gone counts first; a hit is a miss, logged, cleaned.
        static void DrDeltaStep(object x)
        {
            GameObject o = x as GameObject;
            int[] c = PatchCounters();
            try { if (o == null || o.HasBeenDestroyed) return; } catch (Exception) { c[410]++; return; }
            ulong id = o.ObjectId.Value;
            bool hold = sDrHolders != null && sDrHolders.ContainsKey(id);
            bool use = !sDrSlim || sDrUseAll || (sDrUseTaken != null && sDrUseTaken.ContainsKey(id));
            if (sDrIdxVerify)
            {
                c[1677]++;
                if (sDrVerifyLeft != null) sDrVerifyLeft.Remove(id);
                try
                {
                    if (!use && UseListGone(o) > 0)
                    {
                        use = true;
                        if (c[1692]++ < 5) ShortcutLog("deadrefdelta verify: " + o.GetType().Name + " " + id.ToString("X") + " has a gone Sim in a use list outside the use set (unhooked writer)");
                    }
                    int gone = hold ? 0 : OwnerGone(o) + HandlerGone(o) + (sDrIdxCook ? CookingGone(o) : 0);
                    if (gone > 0)
                    {
                        hold = true;
                        if (c[1678]++ < 5) ShortcutLog("deadrefdelta verify: " + o.GetType().Name + " " + id.ToString("X") + " holds " + gone + " gone entries outside the taken holders (owner " + OwnerGone(o) + ", handlers " + HandlerGone(o) + ")");
                    }
                }
                catch (Exception) { c[410]++; }
            }
            if (use) try { if (UseListGone(o) > 0) UseListBody(o); } catch (Exception) { c[410]++; }
            if (hold)
            {
                c[1672]++;
                try { OwnerBody(o); } catch (Exception) { c[410]++; }
                try { HandlerBody(o); } catch (Exception) { c[410]++; }
                try { if (sDrIdxCook) CookingBody(o); } catch (Exception) { c[410]++; }
                try { DrIdxNoteHolders(o); } catch (Exception) { c[410]++; }
            }
            if (sDrSlim && use) try { if (DrUseHeld(o)) sDrIdx.NoteUse(id); } catch (Exception) { c[410]++; }
        }

        // kSimDestroyed (target = the Sim) / kSimDescriptionDisposed: the holders of that Sim / description are due
        static void DrIdxMarkEvent(Sims3.Gameplay.EventSystem.Event e)
        {
            int[] c = PatchCounters();
            Sim s = e != null ? e.TargetObject as Sim : null;
            if (s != null) { sDrIdx.MarkSim(s.ObjectId.Value); c[1686]++; }
            SimDescriptionEvent de = e as SimDescriptionEvent;
            if (de != null && de.SimDescription != null) { sDrIdx.MarkDesc(de.SimDescription.SimDescriptionId); c[1687]++; }
            DrIdxGauges(c);
        }

        public class DrStealHook : GameObject
        {
            public void StealHook(Sim thief, GameObject victim) { StealOrig(thief, victim); DrIdxNote(this); }
            public void StealOrig(Sim thief, GameObject victim) { } // receives GameObject.SetStealActors' original body
        }

        public class DrOwnerImportHook : ObjectOwnerComponent
        {
            public bool ImportHook(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader)
            {
                DrTravelStep("ObjectOwnerComponent.ImportContent", "enter");
                bool ok = ImportOrig(resKeyTable, objIdTable, reader);
                try { DrIdxNote(Owner); } catch (Exception e) { DrTravelFail("ObjectOwnerComponent.ImportContent", e); }
                DrTravelStep("ObjectOwnerComponent.ImportContent", "done");
                return ok;
            }
            public bool ImportOrig(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader) { return false; } // ObjectOwnerComponent.ImportContent
        }

        public class DrCarRelHook
        {
            public GameObjectRelationship NewHook(SimDescription simDesc)
            {
                GameObjectRelationship r = NewOrig(simDesc);
                if (r != null) DrIdxNote((object)this as GameObject);
                return r;
            }
            public GameObjectRelationship NewOrig(SimDescription simDesc) { return null; } // Vehicle.GetNewRelationship
        }

        public class DrBirdRelHook
        {
            public GameObjectRelationship NewHook(SimDescription simDesc)
            {
                GameObjectRelationship r = NewOrig(simDesc);
                if (r != null) DrIdxNote((object)this as GameObject);
                return r;
            }
            public GameObjectRelationship NewOrig(SimDescription simDesc) { return null; } // MinorPetBirdLarge.GetNewRelationship
        }

        public class DrCookAddHook
        {
            public void AddHook()
            {
                AddOrig();
                CookingProcess cp = (object)this as CookingProcess;
                if (cp != null) DrIdxNote(cp.ObjectClickedOn);
            }
            public void AddOrig() { } // CookingProcess.AddObjectListeners
        }

        // A decayed relationship leaves its description's list (the CarRelationship override calls this base): its handler
        // is now gone, so the description's holders are due. No event exists for it (docs/205 §2.3a).
        public class DrRelDecayHook
        {
            public void DecayHook()
            {
                SimDescription d = sRelDesc != null ? sRelDesc.GetValue(this) as SimDescription : null;
                DecayOrig();
                if (d != null && DrIdxLive()) { sDrIdx.MarkDesc(d.SimDescriptionId); PatchCounters()[1687]++; }
            }
            public void DecayOrig() { } // GameObjectRelationship.RemoveRelationship
        }

        public class DrRefAddHook : GameObject
        {
            public void AddHook(Sim actor) { AddOrig(actor); DrIdxNoteUseW(this); }
            public void AddOrig(Sim actor) { } // GameObject.AddToReferenceList
        }

        public class DrRouteRefAddHook : GameObject
        {
            public void AddHook(Sim actor) { AddOrig(actor); DrIdxNoteUseW(this); }
            public void AddOrig(Sim actor) { } // GameObject.AddToRoutingReferenceList
        }

        public class DrUseAddHook : GameObject
        {
            public void AddHook(Sim a) { AddOrig(a); DrIdxNoteUseW(this); }
            public void AddOrig(Sim a) { } // GameObject.AddToUseList (overrides that call base reach it)
        }

        // ServingContainer's override adds to its own list and to every serving container stacked in its stacking slot,
        // never calling base: note the same chain
        public class DrServeUseAddHook : GameObject
        {
            public void AddHook(Sim actor)
            {
                AddOrig(actor);
                GameObject g = this;
                for (int i = 0; g != null && i < 64; i++)
                {
                    DrIdxNoteUseW(g);
                    g = g.GetContainedObject(ServingContainer.kStackingContainmentSlot) as ServingContainer;
                }
            }
            public void AddOrig(Sim actor) { } // ServingContainer.AddToUseList
        }
    }
}
