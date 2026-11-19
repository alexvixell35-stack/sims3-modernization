using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;
using Sims3.Store.Objects;

namespace Ts3Fixes
{
    // docs/500 static bug hunt (2026-11-19, Store objects, CAS/CAST, plus one routing lead): four EA bug fixes, each off by
    // default. Pure decisions in EaBugs500Core.cs (self-test tools/selftest/eabugs500). Exact EA bug fixes, so household
    // Sims are included. Counters: fixed / seen with the switch off / errors (lotentrypet also: lots repaired at load).
    //
    // playpenflags [4017-4019] [verified, decompiled] PlaypenClassic keeps four "busy" marks on the pen: mToddlerBeingPickedUp1/2
    //   (TakeOut), mPlayingWithOutsideSim + mOutsideSim (PlayWithOutsideSim) and mSyncing (PlaySocial). Each Run sets its mark
    //   and clears it only at the end of its normal path; no class has a Cleanup and no Run has a finally, so only the pen's
    //   own DoReset clears them. A reset or exception of the adult or the leading toddler mid-run leaves the mark set, and the
    //   loops that wait on it never end: BeingPickedUp (the toddler cancels everything queued, for good), PlayingWithOutsideSim
    //   and the PlaySocial follower (Sleep loops with no exit test), and every later TakeOut / PlayWithOutsideSim, whose own
    //   waits for the toddler to go idle have no cancel test either (the adult stands there for good). Fix: when one of the
    //   three Runs ends by an exception (reset included), the mark it set is cleared. A normal end runs EA's code unchanged.
    //
    // voodoobindwait [4020-4022] [verified, decompiled] VoodooDoll+Bind.RunFromInventory waits "while (sim.IsSleeping)
    //   { Sleep(0); LoopIdle(); }" for the picked Sim (the picker offers sleeping Sims on the lot) with no cancel or destroyed
    //   test: the binder cannot be cancelled until the sleeper wakes. Fix: the same wait runs first in our hook and ends (no
    //   bind, as a refused bind) when the binder gets a Canceled exit reason or the sleeper is destroyed; then EA's body runs.
    //
    // saunawoohoopreg [4023-4025] [verified, decompiled + IL] SaunaClassic+CuddleSeatedWooHooSauna+Definition.Test opens with
    //   "a.SimDescription.IsVisuallyPregnant || a.SimDescription.IsVisuallyPregnant" (IL: the closure's `a` loaded twice, the
    //   target argument never), so a visibly pregnant target is offered sauna WooHoo; CanGetRomantic has no pregnancy test.
    //   The sibling StartSaunaSeatedCuddleA tests both. Fix: a visibly pregnant target fails the test first, as EA meant.
    //
    // lotentrypet [4026-4029] [verified, decompiled] Lot.PlanRouteIntoLot validates its entry cache before it plans: it sets
    //   BestRouteIntoLotLocationValidated, finds the inside door spot and plans to it; a failed plan resets the spot to
    //   Invalid and leaves the cache validated. So the first Sim to plan decides for everyone until build/buy resets the flag
    //   (saved). A horse (no stairs, no water planning) or a pet the door refuses fails, and every later Sim, household humans
    //   included, skips the door and plans to the mailbox. Fix: when a non-human's call validated the cache and left no spot,
    //   the cache is left unvalidated, so the next Sim decides; a human's result is vanilla. At world load a validated cache
    //   with no spot (possibly poisoned in an older save) is unvalidated once, as EA's own build/buy reset does.
    //   The same hook carries horselotentry and its counters (EaBugs500b.cs): a non-player horse's doomed door plan is skipped.
    public static partial class Fixes
    {
        static FieldInfo sE5Pick1, sE5Pick2, sE5Playing, sE5Outside, sE5Sync, sE5SocialPP, sE5OutsideDefPen, sE5LotSpot;

        static void ApplyEaBugs500()
        {
            Type pen = typeof(PlaypenClassic);
            sE5Pick1 = pen.GetField("mToddlerBeingPickedUp1", kInstAll);
            sE5Pick2 = pen.GetField("mToddlerBeingPickedUp2", kInstAll);
            sE5Playing = pen.GetField("mPlayingWithOutsideSim", kInstAll);
            sE5Outside = pen.GetField("mOutsideSim", kInstAll);
            sE5Sync = pen.GetField("mSyncing", kInstAll);
            Type social = pen.GetNestedType("PlaySocial", kSbNested);
            sE5SocialPP = social == null ? null : social.GetField("mPP", kInstAll);
            Type outside = pen.GetNestedType("PlayWithOutsideSim", kSbNested);
            Type outsideDef = outside == null ? null : outside.GetNestedType("Definition", kSbNested);
            sE5OutsideDefPen = outsideDef == null ? null : outsideDef.GetField("mPlaypen", kInstAll);
            if (sE5Pick1 == null || sE5Pick2 == null || sE5Playing == null || sE5Outside == null || sE5Sync == null || sE5SocialPP == null || sE5OutsideDefPen == null)
                AppliedLog().Add("playpenflags: not found: PlaypenClassic marks");
            else
            {
                Patch(typeof(PlaypenClassic).GetNestedType("TakeOut", kSbNested), "Run", Type.EmptyTypes, typeof(E500TakeOutHook), "RunHook", "RunOrig");
                Patch(typeof(PlaypenClassic).GetNestedType("PlayWithOutsideSim", kSbNested), "Run", Type.EmptyTypes, typeof(E500OutsideHook), "RunHook", "RunOrig");
                Patch(typeof(PlaypenClassic).GetNestedType("PlaySocial", kSbNested), "Run", Type.EmptyTypes, typeof(E500SocialHook), "RunHook", "RunOrig");
            }
            Patch(typeof(VoodooDoll).GetNestedType("Bind", kSbNested), "RunFromInventory", Type.EmptyTypes, typeof(E500BindHook), "RunHook", "RunOrig");
            // one line: check-live-map.ts reads Patch calls line by line
            Patch(typeof(SaunaClassic.CuddleSeatedWooHooSauna).GetNestedType("Definition", kSbNested), "Test", new Type[] { typeof(Sim), typeof(Sim), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() }, typeof(E500SaunaHook), "TestHook", "TestOrig");
            sE5LotSpot = typeof(Lot).GetField("mBestRouteIntoLotLocation", kInstAll);
            if (sE5LotSpot == null) AppliedLog().Add("lotentrypet: not found: Lot.mBestRouteIntoLotLocation");
            else Patch(typeof(Lot), "PlanRouteIntoLot", new Type[] { typeof(Route), typeof(bool), typeof(Sim) }, typeof(E500LotEntryHook), "PlanHook", "PlanOrig");   // horselotentry (EaBugs500b.cs) shares this hook
        }

        static ulong E5Id(Sim s) { return s != null && s.SimDescription != null ? s.SimDescription.SimDescriptionId : 0UL; }

        static PlaypenClassic E5PenOf(Sim toddler)
        {
            PlaypenClassic.InPlaypenPosture p = toddler == null ? null : toddler.Posture as PlaypenClassic.InPlaypenPosture;
            return p == null ? null : p.mPlaypen;
        }

        // one mark after an aborted run: cleared with the switch on, counted either way. Callers pass ShortcutOn("playpenflags")
        // so tools/static/check-live-map.ts ties each playpen hook class to playpenflags alone.
        static void E5Clear(bool on, PlaypenClassic pen, FieldInfo f, object clearValue, FieldInfo f2, object clear2)
        {
            if (on)
            {
                f.SetValue(pen, clearValue);
                if (f2 != null) f2.SetValue(pen, clear2);
                PatchCounters()[4017]++;
            }
            else PatchCounters()[4018]++;
        }

        public class E500TakeOutHook
        {
            public bool RunHook()
            {
                Sim toddler = ((InteractionInstance)(object)this).Target as Sim;
                PlaypenClassic pen = E5PenOf(toddler);   // EA's Run reads the pen from the toddler's posture at entry too
                ulong id = E5Id(toddler);
                bool done = false;
                try { bool r = RunOrig(); done = true; return r; }
                finally
                {
                    if (!done && pen != null)
                    {
                        try
                        {
                            if (Eb500Core.PpClearId((ulong)sE5Pick1.GetValue(pen), id)) E5Clear(ShortcutOn("playpenflags"), pen, sE5Pick1, 0UL, null, null);
                            if (Eb500Core.PpClearId((ulong)sE5Pick2.GetValue(pen), id)) E5Clear(ShortcutOn("playpenflags"), pen, sE5Pick2, 0UL, null, null);
                        }
                        catch (Exception) { PatchCounters()[4019]++; }
                    }
                }
            }
            public bool RunOrig() { return false; } // PlaypenClassic+TakeOut.Run
        }

        public class E500OutsideHook
        {
            public bool RunHook()
            {
                InteractionInstance ii = (InteractionInstance)(object)this;
                // EA's Run takes the pen from its definition (PlaypenClassic.cs PlayWithOutsideSim.Run)
                PlaypenClassic pen = ii.InteractionDefinition == null ? null : sE5OutsideDefPen.GetValue(ii.InteractionDefinition) as PlaypenClassic;
                ulong adult = E5Id(ii.InstanceActor);
                bool done = false;
                try { bool r = RunOrig(); done = true; return r; }
                finally
                {
                    if (!done && pen != null)
                    {
                        try
                        {
                            if (Eb500Core.PpClearOutside((bool)sE5Playing.GetValue(pen), (ulong)sE5Outside.GetValue(pen), adult))
                                E5Clear(ShortcutOn("playpenflags"), pen, sE5Playing, false, sE5Outside, 0UL);
                        }
                        catch (Exception) { PatchCounters()[4019]++; }
                    }
                }
            }
            public bool RunOrig() { return false; } // PlaypenClassic+PlayWithOutsideSim.Run
        }

        public class E500SocialHook
        {
            public bool RunHook()
            {
                PlaypenClassic pen = sE5SocialPP.GetValue(this) as PlaypenClassic;
                bool atEntry = pen != null && (bool)sE5Sync.GetValue(pen);
                bool done = false;
                try { bool r = RunOrig(); done = true; return r; }
                finally
                {
                    // shortcut: a follower that aborts after the leader set the mark also clears it; the leader's play goes on
                    // unaffected (the mark only holds followers) and clears it again at its end
                    if (!done && pen != null)
                    {
                        try { if (Eb500Core.PpClearSync(atEntry, (bool)sE5Sync.GetValue(pen))) E5Clear(ShortcutOn("playpenflags"), pen, sE5Sync, false, null, null); }
                        catch (Exception) { PatchCounters()[4019]++; }
                    }
                }
            }
            public bool RunOrig() { return false; } // PlaypenClassic+PlaySocial.Run
        }

        public class E500BindHook
        {
            public bool RunHook()
            {
                InteractionInstance ii = (InteractionInstance)(object)this;
                Sim sleeper = ii.GetSelectedObject() as Sim;
                Sim actor = ii.InstanceActor;
                if (sleeper != null && actor != null && sleeper.IsSleeping)
                {
                    if (!ShortcutOn("voodoobindwait")) PatchCounters()[4021]++;   // EA's body waits, uncancellable
                    else
                    {
                        while (sleeper.IsSleeping)   // EA's own wait, plus the exit tests it lacks
                        {
                            if (Eb500Core.VbStop(actor.HasExitReason(ExitReason.Canceled), sleeper.HasBeenDestroyed)) { PatchCounters()[4020]++; return false; }
                            Simulator.Sleep(0u);
                            actor.LoopIdle();
                        }
                    }
                }
                return RunOrig();
            }
            public bool RunOrig() { return false; } // VoodooDoll+Bind.RunFromInventory
        }

        public class E500SaunaHook
        {
            public bool TestHook(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                if (target != null && target.SimDescription != null && target.SimDescription.IsVisuallyPregnant)
                {
                    if (ShortcutOn("saunawoohoopreg")) { PatchCounters()[4023]++; return false; }   // EA's first test, on the target
                    PatchCounters()[4024]++;
                }
                return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
            }
            public bool TestOrig(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // SaunaClassic+CuddleSeatedWooHooSauna+Definition.Test
        }

        public class E500LotEntryHook
        {
            public void PlanHook(Route r, bool planToLotAsFallback, Sim actor)
            {
                Lot lot = (Lot)(object)this;
                bool atEntry = lot.BestRouteIntoLotLocationValidated;
                uint t0 = SrcNowUs();
                GameObject mailbox = null;
                int site = HlSite(lot, r, actor, atEntry, out mailbox);   // horselotentry (EaBugs500b.cs): 0 unless a horse's door plan is doomed
                if (site == 0) PlanOrig(r, planToLotAsFallback, actor);
                else if (ShortcutOn("horselotentry"))
                {
                    PatchCounters()[4030]++;
                    try { HlFallback(lot, r, planToLotAsFallback, actor, site, mailbox); }
                    finally { HlTime(4035, ref sHlOnUs, t0); }
                }
                else
                {
                    PatchCounters()[4031]++;
                    try { PlanOrig(r, planToLotAsFallback, actor); }
                    finally { HlTime(4034, ref sHlOffUs, t0); }
                    HlCheckOff(lot, r, site);
                }
                if (atEntry || actor == null) return;
                try
                {
                    if (Eb500Core.LeUnvalidate(atEntry, lot.BestRouteIntoLotLocationValidated, actor.IsHuman, (Vector3)sE5LotSpot.GetValue(lot) == Vector3.Invalid))
                    {
                        if (ShortcutOn("lotentrypet")) { lot.BestRouteIntoLotLocationValidated = false; PatchCounters()[4026]++; }
                        else PatchCounters()[4027]++;
                    }
                }
                catch (Exception) { PatchCounters()[4028]++; }
            }
            public void PlanOrig(Route r, bool planToLotAsFallback, Sim actor) { } // Lot.PlanRouteIntoLot
        }

        // World load: a cache validated with no spot is decided again by the next plan (EA's build/buy reset, LotManager).
        public static void LotEntryPetWorldLoad()
        {
            if (!ShortcutOn("lotentrypet") || sE5LotSpot == null) return;
            try
            {
                foreach (Lot lot in LotManager.Lots)
                {
                    if (lot == null || !Eb500Core.LeRepair(lot.BestRouteIntoLotLocationValidated, (Vector3)sE5LotSpot.GetValue(lot) == Vector3.Invalid)) continue;
                    lot.BestRouteIntoLotLocationValidated = false;
                    PatchCounters()[4029]++;
                }
            }
            catch (Exception) { PatchCounters()[4028]++; }
        }
    }
}
