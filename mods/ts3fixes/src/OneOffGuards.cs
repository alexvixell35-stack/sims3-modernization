using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.PetObjects;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Guards for one-off EA errors seen in natural play 2026-11-17/18 (local, each default off). Each replaces one sure
    // exception; everything else runs EA's code unchanged.
    //
    // overwatchresetgone [verified, decompiled]: Sim.SetObjectToReset(ResetInformation) calls Proxy.SetObjectToReset()
    // with no check; for a destroyed Sim Proxy is null (HasReset / SetObjectAsReset use ObjectId, which is null-safe). Its
    // callers (GameObject.DoReset over mActorsUsingMe / mReferenceList) test for null but not HasBeenDestroyed, so an
    // object that still lists a destroyed Sim throws on reset, and the rest of its reset never runs (NRaas Overwatch's
    // elevator cleanup, 3 times in one session 2026-11-17 19:50). Fix: a destroyed Sim (no proxy) is skipped: nothing is
    // left to reset. Counters [3994] skipped, [3995] seen with the switch off (EA throws).
    //
    // motivesnohousehold [verified, decompiled]: Motives.CreateMotives reads mSim.Household three times
    // (UpdateMotivesToLookAfterPets for pets, UpdateMotivesToLookAfterBabiesAndToddlers and HasPetsOfType for humans) with
    // no null test. A human Sim object with no household (a dead SimDescription restored at load or re-instantiated by
    // Overwatch's FixInvisible, 2026-11-17 19:17) throws in Sim.OnCreation and gets no motives. Fix: for that call only,
    // SimDescription.mHousehold is Household.NpcHousehold (a special household: both methods return at once, so no baby
    // or pet motives, what a Sim with no household should get), and null again after. The pet case stays eanullguards'
    // (EaFixes.cs MotivesHook). Household rule: only a Sim of known scope (active household exists, not selected, not selectable) gets the stand-in, else vanilla (MnhApplies). Counters [3996] created with the stand-in, [3997] seen with the switch off (EA throws).
    public static partial class Fixes
    {
        public class OverwatchResetGoneHook
        {
            // GameObject.ResetInformation is protected: the hook takes it as object (Patch checks the argument count)
            public void ResetHook(object info)
            {
                Sim sim = (Sim)(object)this;
                if (sim.Proxy == null)
                {
                    if (!ShortcutOn("overwatchresetgone")) { PatchCounters()[3995]++; ResetOrig(info); return; }
                    PatchCounters()[3994]++;
                    return;
                }
                ResetOrig(info);
            }
            public void ResetOrig(object info) { } // receives Sim.SetObjectToReset(ResetInformation)'s original body
        }

        static readonly FieldInfo sMnhHousehold = typeof(SimDescription).GetField("mHousehold", BindingFlags.Instance | BindingFlags.NonPublic);

        // called by EaFixes.cs MotivesHook for a human with no household; true when it ran EA's CreateMotives itself
        static bool MnhApplies(Sim sim)
        {
            if (!(sim != null && sim.Household == null && sim.SimDescription != null && !sim.SimDescription.IsPet && sMnhHousehold != null && Household.NpcHousehold != null)) return false;
            // household rule (CLAUDE.md): a household-less Sim is IsPlayerSim's unknown scope, so it needs a known scope here: an active
            // household must exist (no selected Sim = every scope unknown) and the Sim is neither the selected nor a selectable one
            // (selectable needs a household, so only a stale answer could say so). Anything else gets EA's vanilla throw.
            try { return Household.ActiveHousehold != null && !sim.HasBeenDestroyed && sim != PlumbBob.SelectedActor && !sim.IsSelectable; }
            catch (Exception) { return false; }
        }
    }
}
