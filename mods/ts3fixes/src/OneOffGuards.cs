using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
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
    // left to reset. Counters [3986] skipped, [3987] seen with the switch off (EA throws).
    //
    // motivesnohousehold [verified, decompiled]: Motives.CreateMotives reads mSim.Household three times
    // (UpdateMotivesToLookAfterPets for pets, UpdateMotivesToLookAfterBabiesAndToddlers and HasPetsOfType for humans) with
    // no null test. A human Sim object with no household (a dead SimDescription restored at load or re-instantiated by
    // Overwatch's FixInvisible, 2026-11-17 19:17) throws in Sim.OnCreation and gets no motives. Fix: for that call only,
    // SimDescription.mHousehold is Household.NpcHousehold (a special household: both methods return at once, so no baby
    // or pet motives, what a Sim with no household should get), and null again after. The pet case stays eanullguards'
    // (EaFixes.cs MotivesHook). Counters [3988] created with the stand-in, [3989] seen with the switch off (EA throws).
    //
    // fetchabort [verified path, inferred cause]: PlayFetchHumanInteraction.ThrowLoop hands the stick to the dog with
    // PetCarrySystem.EnterWhileHolding, whose RequestState("x", "Carry") on the dog's carry machine throws
    // SacsAbortException when the dog was interrupted mid-throw (2026-11-18 02:57); nothing catches it and the thrower is
    // reset. Fix: that exception ends the game the way ThrowLoop's own no-dog branch does (ExitReason.Finished): Run's loop
    // ends and its tail plays Exit, destroys the stick and finishes the dog's side. Counters [3990] fetch games ended,
    // [3991] seen with the switch off (EA's exception goes on).
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
                    if (!ShortcutOn("overwatchresetgone")) { PatchCounters()[3987]++; ResetOrig(info); return; }
                    PatchCounters()[3986]++;
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
            return sim != null && sim.Household == null && sim.SimDescription != null && !sim.SimDescription.IsPet && sMnhHousehold != null && Household.NpcHousehold != null;
        }

        public class FetchAbortHook
        {
            public void ThrowHook()
            {
                try { ThrowOrig(); }
                catch (SacsAbortException)
                {
                    if (!ShortcutOn("fetchabort")) { PatchCounters()[3991]++; throw; }
                    ((PlayFetchJig.PlayFetchHumanInteraction)(object)this).Actor.AddExitReason(ExitReason.Finished);
                    PatchCounters()[3990]++;
                }
            }
            public void ThrowOrig() { } // receives PlayFetchJig.PlayFetchHumanInteraction.ThrowLoop's original body
        }
    }
}
