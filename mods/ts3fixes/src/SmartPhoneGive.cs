using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.SimIFace;
using Sims3.SimIFace.Enums;

namespace Ts3Fixes
{
    // The University Life smartphone: how a Sim gets, keeps or loses it (docs/573 §10-11), each off by default. EA bug
    // fixes, every Sim; no Sim is moved.
    //
    // Paths read [verified in the decompiled game code]: Sim.GrantCellPhone (age-up via AgingState
    // .AgeTransitionCompleted -> GrantSpecialObjects, instantiation via Sim.OnStartup, AddInitialObjects, Bin import),
    // Sim.GivePhoneOfType (the only place a phone is created and replaced: Phone Settings smart/future switch, FutureSim
    // trait, plumbot creation, Social Networking / Rebel unlocks), Sim.FixUpCellPhone (the type check: toddler/pet = no
    // phone, EP9 = smartphone, EP11 future Sim = future phone; run only from Sim.PostWorldLoadInit for Sims in the world
    // at load), Inventory.ImportContent (travel / bin: one phone kept, duplicates destroyed; skin, broken state and
    // ringtone come back through GameStates.StashHousehold/UnstashHousehold).
    //
    // phonefixup (kPhoneFixUp, docs/573 §10) [mechanism verified, trigger inferred]: the phone-type check runs only at
    //   world load. A Sim that gets its objects later (a household from the bin or library saved before University Life,
    //   a Sim instantiated or aged up after the load) runs GrantCellPhone, which only adds a phone when there is none: an
    //   old basic cell phone stays, so the Sim has no smartphone apps, Social Networking phone interactions or smartphone
    //   photo until the next load. Fix: after GrantSpecialObjects, EA's own FixUpCellPhone runs for that Sim (it changes
    //   nothing when the phone is already the right type). Counters [2668] phones checked after a grant, [2669] errors.
    // phonegive (kPhoneGive, docs/573 §11) [verified]: GivePhoneOfType removes only the first phone, then gives up when
    //   any phone remains, so a Sim holding two phones keeps the wrong one (the Phone Settings switch, the FutureSim trait
    //   and plumbot creation silently do nothing); when the inventory refuses the new phone it is left out of the world
    //   and the Sim has none; and the replacement starts with the default ringtone and only the default skin unlocked
    //   (skins come back only at the next GrantSpecialObjects). Fix: the same steps, with every old phone removed, the
    //   ringtone and unlocked skins carried to the new phone, and a refused phone destroyed. Counters [2670] phones given
    //   with a correction, [2671] errors.
    // Checks: SmartPhoneGiveChecks.cs.
    public static partial class Fixes
    {
        static void ApplySmartPhoneGive()
        {
            Patch(typeof(Sim), "GrantSpecialObjects", typeof(PhoneFixUpHook), "GrantHook", "GrantOrig");
            Patch(typeof(Sim), "GivePhoneOfType", typeof(PhoneGiveHook), "GiveHook", "GiveOrig");
        }

        // ---- phonefixup ----
        // Whether the load-time type check should run after a grant: a human child or older (EA's FixUpCellPhone also
        // handles toddlers and pets, by removing the phone, which GrantCellPhone already did).
        internal static bool PfRunFixUp(bool on, bool humanChildOrAbove)
        {
            return on && humanChildOrAbove;
        }

        public class PhoneFixUpHook
        {
            public void GrantHook(bool bForce, bool simIsAgingUp)
            {
                GrantOrig(bForce, simIsAgingUp);
                try
                {
                    Sim sim = (Sim)(object)this;
                    if (ShortcutOn("phonefixup") && sim.SimDescription != null && sim.Inventory != null
                        && PfRunFixUp(true, sim.SimDescription.ChildOrAbove && sim.IsHuman))
                    {
                        sim.FixUpCellPhone();
                        PatchCounters()[2668]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2669]++; }
            }
            public void GrantOrig(bool bForce, bool simIsAgingUp) { }
        }

        // ---- phonegive ----
        // What GivePhoneOfType does with the phones a Sim holds: removed = phones taken out (EA: the first only);
        // proceed = a new phone is created (EA: only when none is left).
        internal static void PgPlan(bool on, int phonesHeld, out int removed, out bool proceed)
        {
            removed = on ? phonesHeld : Math.Min(phonesHeld, 1);
            proceed = phonesHeld - removed == 0;
        }

        public class PhoneGiveHook
        {
            public void GiveHook(PhoneType type)
            {
                if (!ShortcutOn("phonegive")) { GiveOrig(type); return; }
                Sim sim = (Sim)(object)this;
                bool corrected = false;
                try
                {
                    // the old phones' settings, then every old phone out (EA: the first only)
                    uint ringtone = 0; bool haveRingtone = false;
                    List<PhoneSkins> skins = null;
                    List<IPhoneCell> old = new List<IPhoneCell>();
                    foreach (IPhoneCell p in sim.Inventory.FindAll<IPhoneCell>(false)) old.Add(p);
                    if (old.Count > 1) corrected = true;
                    foreach (IPhoneCell p in old)
                    {
                        if (!haveRingtone) { ringtone = p.GetRingtone(); haveRingtone = true; }
                        PhoneSmart ps = p as PhoneSmart;
                        if (skins == null && ps != null && ps.mUnlockedSkins != null) skins = new List<PhoneSkins>(ps.mUnlockedSkins);
                        sim.Inventory.RemoveByForce(p);
                        p.Destroy();
                    }
                    if (sim.Inventory.AmountIn<IPhone>() != 0) return;  // EA: a non-cell phone left (none in base data)
                    IPhoneCell phone = null;
                    switch (type)
                    {
                        case PhoneType.FuturePhone:
                            phone = Sims3.Gameplay.GlobalFunctions.CreateObjectOutOfWorld("phoneSmartFuture", ProductVersion.EP11, "Sims3.Gameplay.Objects.Electronics.PhoneFuture", null) as IPhoneCell;
                            break;
                        case PhoneType.SmartPhone:
                            phone = Sims3.Gameplay.GlobalFunctions.CreateObjectOutOfWorld("PhoneSmartPhone", ProductVersion.EP9, "Sims3.Gameplay.Objects.Electronics.PhoneSmart", null) as IPhoneCell;
                            if (GameUtils.IsInstalled(ProductVersion.EP9)) Sims3.Gameplay.Socializing.ActiveTopic.AddToSim(sim, "Smart Phone Topic");
                            break;
                        case PhoneType.CellPhone:
                            phone = Sims3.Gameplay.GlobalFunctions.CreateObjectOutOfWorld("PhoneCell") as IPhoneCell;
                            break;
                    }
                    if (phone == null) return;
                    if (haveRingtone) { phone.SetRingtoneWithoutAudio(ringtone); corrected = true; }
                    PhoneSmart ns = phone as PhoneSmart;
                    if (ns != null && skins != null)
                        foreach (PhoneSkins s in skins) if (!ns.mUnlockedSkins.Contains(s)) { ns.UnlockPhoneSkin(s); corrected = true; }
                    if (sim.Inventory.TryToAdd(phone))
                    {
                        PhoneService.RegisterPhone(phone);
                        if (sim.IsActiveSim)
                        {
                            Sims3.Gameplay.UI.HudModel hud = Sims3.UI.Responder.Instance.HudModel as Sims3.Gameplay.UI.HudModel;
                            if (hud != null) hud.UpdateCellPhoneIcon(type, 0, false);
                        }
                    }
                    else { phone.Destroy(); corrected = true; }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2671]++; }
                if (corrected) PatchCounters()[2670]++;
            }
            public void GiveOrig(PhoneType type) { }
        }
    }
}
