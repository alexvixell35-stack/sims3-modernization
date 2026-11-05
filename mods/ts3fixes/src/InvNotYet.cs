using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // invnotyet (docs/531): EA's Sim.TryAddObjectToInventory(IGameObject) calls base.Inventory.TryToAdd(obj) with no null
    // check; Inventory is InventoryComp?.Inventory, which is still null while a Sim is being created (Sim.OnCreation)
    // [verified in the decompiled game code; simler90's UPDATE242 core keeps EA's body]. The player's 2026-10-19 Isla
    // Paradiso session (6 ErrorTrap reports in one minute): Sim.OnCreation -> SimDescription.AssignSchool ->
    // Career.GiveCareerLevelSkills -> SkillManager.AddElement -> BandSkill.OnSkillAddition hands a non-selectable pupil
    // a free instrument through this method, the NullReferenceException stops the creation half way, and the Sim is left
    // at 0,0,0 with no lot. EA's own school levels give no skill; career-metric mods do. Fix: with no inventory the hook
    // does what the method already does when the inventory refuses the object: destroys it (null-safe) and returns false.
    // Every caller handles false. With an inventory nothing changes. Counter [2058] refused (registry fixes-2058).
    // Default off.
    public static partial class Fixes
    {
        public class InvNotYetHook : Sim
        {
            public bool AddHook(IGameObject obj)
            {
                if (base.Inventory != null || !ShortcutOn("invnotyet")) return AddOrig(obj);
                if (obj != null) obj.Destroy();
                PatchCounters()[2058]++;
                return false;
            }
            public bool AddOrig(IGameObject obj) { return false; } // receives Sim.TryAddObjectToInventory(IGameObject)'s original body
        }

        // Harness check (repro inv-not-yet-check): for one synchronous call the active Sim's InventoryComponent.Inventory
        // is null (the state OnCreation is in), restored in a finally before anything else runs. A SocialJigTwoPerson made
        // out of world goes through TryAddObjectToInventory. "expect broken" (switch off) wants EA's
        // NullReferenceException; "expect fixed" (switch on) wants false and the hook's Destroy (counter [2058] +1). Destroy
        // only drops the object's proxy later (HasBeenDestroyed is Simulator.GetProxy(ObjectId) == null), so "expect gone",
        // a step later, wants that object gone. The check destroys the object itself only when the hook did not, and checks
        // that the Sim's inventory is back.
        static ulong sInvNotYetLast;
        public static object InvNotYetCheck(string what)
        {
            if (what == "expect gone")
            {
                GameObject g = sInvNotYetLast == 0 ? null : GameObject.GetObject(new ObjectGuid(sInvNotYetLast));
                if (sInvNotYetLast == 0) throw new InvalidOperationException("invnotyet expect gone: no earlier expect fixed");
                if (g != null && !g.HasBeenDestroyed) throw new InvalidOperationException("invnotyet expect gone: the object the hook destroyed is still there");
                return "as expected; the object the hook destroyed is gone";
            }
            if (what != "expect broken" && what != "expect fixed") return "invnotyet <expect broken|expect fixed|expect gone>";
            Sim sim = Sim.ActiveActor;
            if (sim == null) throw new InvalidOperationException("invnotyet check: no active Sim");
            InventoryComponent ic = sim.InventoryComp;
            if (ic == null || ic.Inventory == null) throw new InvalidOperationException("invnotyet check: the active Sim has no inventory to hide");
            GameObject o = GlobalFunctions.CreateObjectOutOfWorld("SocialJigTwoPerson") as GameObject;
            if (o == null) throw new InvalidOperationException("invnotyet check: CreateObjectOutOfWorld(\"SocialJigTwoPerson\") made nothing");
            Inventory inv = ic.Inventory;
            string threw = "nothing";
            bool added = false;
            int before = PatchCounters()[2058];
            ic.Inventory = null;
            try { added = sim.TryAddObjectToInventory(o); }
            catch (Exception e) { threw = e.GetType().Name; }
            finally { ic.Inventory = inv; }
            bool destroyed = PatchCounters()[2058] == before + 1;
            sInvNotYetLast = o.ObjectId.Value;
            if (!destroyed) o.Destroy();
            if (sim.Inventory != inv) throw new InvalidOperationException("invnotyet check: the Sim's inventory did not come back");
            bool on = ShortcutOn("invnotyet");
            string line = "threw " + threw + ", returned " + added + ", object " + (destroyed ? "destroyed by the hook" : "left") + " (switch " + on + ")";
            bool ok = what == "expect broken" ? !on && threw == "NullReferenceException" : on && threw == "nothing" && !added && destroyed;
            if (!ok) throw new InvalidOperationException("invnotyet " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
