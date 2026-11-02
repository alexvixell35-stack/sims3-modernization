using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // rewardgiver (docs/525): GameObject.SetRewardGiver(sim) adds the owner component with
    // AddComponent<ObjectOwnerComponent>(sim, null); AddComponentInternal types the arguments with o.GetType(), so the
    // null thief throws NullReferenceException before any constructor is looked up [verified in the decompiled game
    // code]. The only caller is RewardsManager.GiveRewards' ObjectInMail case (an opportunity reward object sent by a
    // named Sim): the exception leaves GiveRewards, so the object is never mailed and the rest of the reward list, the
    // completion text and the follow-up opportunity are skipped. Fix: the hook builds the component itself, owner = sim,
    // no thief, as AddComponentInternal would have (version 0: the component has no [Version] attribute; OnCreation is
    // the base no-op). An object that already has an owner component is left alone, as EA's path would. Counter [2050]
    // components added (registry fixes-2050). Default off.
    public static partial class Fixes
    {
        static readonly FieldInfo sObjComponents = typeof(GameObject).GetField("mObjComponents", kInstAll);
        static readonly FieldInfo sOwnerField = typeof(ObjectOwnerComponent).GetField("mOwner", kInstAll);

        public class RewardGiverHook : GameObject
        {
            public void GiverHook(Sim sim)
            {
                if (!ShortcutOn("rewardgiver") || sim == null || sObjComponents == null) { GiverOrig(sim); return; }
                List<ObjectComponent> comps = sObjComponents.GetValue(this) as List<ObjectComponent>;
                if (comps == null) { comps = new List<ObjectComponent>(); sObjComponents.SetValue(this, comps); }
                foreach (ObjectComponent c in comps) if (c != null && c.BaseType == typeof(ObjectOwnerComponent)) return;
                ObjectOwnerComponent owner = new ObjectOwnerComponent(this, sim, null);
                owner.OnCreation();
                comps.Add(owner);
                PatchCounters()[2050]++;
            }
            public void GiverOrig(Sim sim) { } // receives GameObject.SetRewardGiver's original body
        }

        // Harness check (repro reward-giver-check): an object made out of world gets SetRewardGiver(active Sim) once.
        // "expect broken" (switch off) wants EA's NullReferenceException and no owner component; "expect fixed" (switch
        // on) wants no exception and an owner component whose owner is that Sim. The object is destroyed either way.
        public static object RewardGiverCheck(string what)
        {
            if (what != "expect broken" && what != "expect fixed") return "rewardgiver <expect broken|expect fixed>";
            Sim sim = Sim.ActiveActor;
            if (sim == null) throw new InvalidOperationException("rewardgiver check: no active Sim");
            GameObject o = GlobalFunctions.CreateObjectOutOfWorld("SocialJigTwoPerson") as GameObject;
            if (o == null) throw new InvalidOperationException("rewardgiver check: CreateObjectOutOfWorld(\"SocialJigTwoPerson\") made nothing");
            string threw = "nothing";
            try { o.SetRewardGiver(sim); } catch (Exception e) { threw = e.GetType().Name; }
            ObjectOwnerComponent oc = o.ObjectOwnerComponent;
            object owner = oc != null && sOwnerField != null ? sOwnerField.GetValue(oc) : null;
            o.Destroy();
            bool on = ShortcutOn("rewardgiver");
            string line = "threw " + threw + ", owner component " + (oc != null ? (owner == sim ? "owned by the Sim" : "owner is not the Sim") : "none") + " (switch " + on + ")";
            bool ok = what == "expect broken" ? !on && threw == "NullReferenceException" && oc == null : on && threw == "nothing" && oc != null && owner == sim;
            if (!ok) throw new InvalidOperationException("rewardgiver " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
