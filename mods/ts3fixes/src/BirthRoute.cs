using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // birthroute (tuning kBirthRoute, default off; docs/558 §1): EA's human birth flow (Pregnancy.HaveTheBaby ->
    // HaveBabyHospital / HaveBabyHome -> Cleanup re-have alarm) fixed at its causes. EA flaws [verified, decompiled
    // Pregnancy.cs / RabbitHole.cs / InteractionQueue.cs unless marked]:
    //  F1 Unbounded 1-minute re-have: HaveBabyHospital.Cleanup (and HaveBabyHome.Cleanup) re-call HaveTheBaby 1 sim-minute
    //     after every failed trip, for ever; nothing counts, nothing falls back. RouteNearEntranceAndEnterRabbitHole does not
    //     retry a failed plan at all (only failed outside-routes), so one bad plan = one more lap.
    //  F2 Lost birth: HaveTheBaby removes the pregnancy alarms first, then ignores InteractionQueue.Add's result. The queue
    //     refuses an interaction whose Test fails (InsertInteraction: no Cleanup, no callback for these), e.g. HaveBabyHospital
    //     while a HaveBabyHome is queued, or either one without the BabyIsComing buff: then nothing ever calls it again and the
    //     mom stays at hour 72 in labour. A birth interaction dropped from the queue without running (save/load or a reset
    //     clearing the queue [inferred]) is lost the same way.
    //  F3 Duplicate / stale calls: the re-have alarms are AlwaysPersisted and Continue() (Sim instantiation at hour 72) adds
    //     another, so HaveTheBaby can run twice; EA dedupes only a queued HaveBabyHospital, so a queued HaveBabyHome gets a
    //     second birth interaction (whose Cleanup later reads the nulled Pregnancy). A re-have alarm firing after the birth
    //     (Pregnancy completed) runs HaveTheBaby on a finished pregnancy.
    //  F4 Homeless mom (townie, no home lot): HaveTheBaby throws on mMom.LotHome.GetDistanceToObject after removing the alarms
    //     (no birth ever); after a hospital birth AfterExitingRabbitHole pushes GoHome / GoInsideLot on the null lot (throws
    //     before the baby basket is put down [inferred]).
    //  F5 Cleanup NRE: HaveBabyHospital.Cleanup calls Actor.SimDescription.Pregnancy.HaveTheBaby when the pregnancy was
    //     cleared meanwhile (ClearPregnancyData: Dispose, travel merge) and the birth never ran.
    //  F6 Our part: unseenfbplan / KindDeadlines cut unseen NPC birth routes (2026-11-14 logs: every failed plan of Patricia
    //     Bedlington / Stella Striker was our cut), which turned F1 into a visible loop.
    // The fix (every Sim = pure bug fixes that only change a case where EA loses, duplicates or throws; NPC moms = policy):
    //  all   F2: a missing BabyIsComing buff is restored before queuing (EA's own Continue() does the same at hour >= 70); when
    //        no birth interaction is in the queue after EA's code, the re-have alarm is re-armed in 10 min [2470]; a watchdog
    //        re-have (EA's own delegate, AlwaysPersisted) 3 h later re-queues a birth that vanished [2474]. F3: a call for a
    //        finished pregnancy or while a birth interaction is already queued does nothing [2471]. F5: the flag is cleared
    //        so EA's Cleanup adds no alarm on a missing pregnancy [2442]. F4 after the birth: no GoHome on a null lot [2475].
    //  NPC   F6: birth plans get scope kBirth (never cut, native's seen-NPC ceiling) [2438]. F1: re-haves wait 1, 15, then
    //        60 sim-min after the last attempt instead of 1 [2469]; an unseen NPC mom whose hospital trip failed is placed
    //        next to the hospital entrance (EA's HaveBabyHome does the same for its own target) when that spot is unseen
    //        too, so EA's rabbit-hole route only walks the last metres [2473]; from the 3rd failed attempt the other kind
    //        is queued: home birth (EA's own no-hospital branch, the safety net) after hospital failures, the hospital after
    //        home failures [2440]. F4: a homeless NPC mom gets the closest hospital [2441].
    // Counters: [2438] birth plans kept uncut, [2439] failed hospital births (also off), [2440] safety-net switches of kind,
    // [2441] homeless moms to a hospital, [2442] errors / F5 guards, [2469] re-haves deferred, [2470] lost births re-armed,
    // [2471] stale or duplicate calls dropped, [2472] BabyIsComing restored, [2473] unseen moms placed at the hospital,
    // [2474] watchdogs armed, [2475] homeless after-birth GoHome skipped, [2476] births completed after a failed attempt.
    public static partial class Fixes
    {
        const int kBirthOtherKindAfter = 3;
        const float kBirthLostRetryMin = 10f, kBirthWatchdogMin = 180f;
        sealed class BrState { public int Attempts, LastKind; public long Last; public AlarmHandle Watch = AlarmHandle.kInvalidHandle; }
        static readonly Dictionary<ulong, BrState> sBr = new Dictionary<ulong, BrState>();  // SimDescriptionId -> birth attempts
        static readonly Dictionary<ulong, int> sBirthFails = new Dictionary<ulong, int>();  // SimDescriptionId -> failed hospital births (counter only)
        static FieldInfo sBrNewborns, sBrBroadcast, sBrContractions;

        static void ApplyBirthRoute()
        {
            sBrNewborns = typeof(Pregnancy.HaveBabyHospital).GetField("mNewborns", kInstAll);
            sBrBroadcast = typeof(Pregnancy).GetField("mContractionBroadcast", kInstAll);
            sBrContractions = typeof(Pregnancy).GetField("mContractionsAlarm", kInstAll);
            if (sBrNewborns == null) AppliedLog().Add("birthroute: HaveBabyHospital.mNewborns not found, Cleanup not patched");
            else
            {
                Patch(typeof(Pregnancy.HaveBabyHospital), "Cleanup", typeof(BrHospitalHook), "CleanupHook", "CleanupOrig");
                Patch(typeof(Pregnancy.HaveBabyHospital), "AfterExitingRabbitHole", typeof(BrHospitalHook), "AfterHook", "AfterOrig");
            }
            if (sBrBroadcast == null || sBrContractions == null) AppliedLog().Add("birthroute: Pregnancy alarm fields not found, HaveTheBaby not patched");
            else Patch(typeof(Pregnancy), "HaveTheBaby", typeof(BrPregnancyHook), "HaveHook", "HaveOrig");
        }

        // ---------- F6: birth routes are never cut ----------
        // UfPlanBody asks for every unseen NPC plan: one dictionary lookup when off, three priority reads when on.
        static bool BirthRouteKept(Sim s)
        {
            if (!ShortcutOn("birthroute")) return false;
            try
            {
                InteractionQueue q = s.InteractionQueue;
                if (!BirthIa(s.CurrentInteraction) && (q == null || (!BirthIa(q.TransitionInteraction) && !BirthIa(q.GetHeadInteraction())))) return false;
                PatchCounters()[2438]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2442]++; return false; }
        }

        static bool BirthIa(InteractionInstance ia)
        {
            return ia != null && ia.GetPriority().Level == InteractionPriorityLevel.Pregnancy;
        }

        // ---------- the pure decisions (also used by the check) ----------
        // F1: minutes a re-have must wait after the last queued attempt. EA: 1 for everyone. On: NPC 1, 15, then 60.
        internal static float BirthRetryWaitMin(bool on, bool npc, int attempts)
        {
            if (!on || !npc || attempts <= 1) return 1f;
            return attempts == 2 ? 15f : 60f;
        }

        // F1 safety net: 0 = EA's choice (hospital if one is closer than home, else home), 1 = hospital, 2 = home.
        internal static int BirthKind(bool on, bool npc, int attempts, int lastKind, bool homeOk, bool hospital)
        {
            if (!on || !npc || attempts < kBirthOtherKindAfter) return 0;
            if (lastKind != 2 && homeOk) return 2;
            if (lastKind == 2 && hospital) return 1;
            return 0;
        }

        // F2 / F3: what a HaveTheBaby call does. 0 = EA's code, 1 = nothing (stale or duplicate), 2 = EA's code, then the
        // re-have alarm re-armed when it queued nothing.
        internal static int BirthCallAction(bool on, bool pregnancyCurrent, bool birthQueued)
        {
            if (!on) return 0;
            if (!pregnancyCurrent || birthQueued) return 1;
            return 2;
        }

        // ---------- hooks ----------
        public class BrHospitalHook
        {
            public void CleanupHook()
            {
                try { BrBeforeCleanup((object)this as Pregnancy.HaveBabyHospital); } catch (Exception) { PatchCounters()[2442]++; }
                CleanupOrig();
            }
            public void CleanupOrig() { } // receives Pregnancy+HaveBabyHospital.Cleanup's original body

            public bool AfterHook()
            {
                Pregnancy.HaveBabyHospital h = (object)this as Pregnancy.HaveBabyHospital;
                bool skip = false;
                try { skip = ShortcutOn("birthroute") && h != null && h.Actor != null && h.Actor.LotHome == null; } catch (Exception) { PatchCounters()[2442]++; }
                if (!skip) return AfterOrig();
                try { BrAfterHomeless(h); } catch (Exception) { PatchCounters()[2442]++; }
                return true;  // RabbitHoleInteraction.AfterExitingRabbitHole returns true
            }
            public bool AfterOrig() { return true; } // receives Pregnancy+HaveBabyHospital.AfterExitingRabbitHole's original body
        }

        static void BrBeforeCleanup(Pregnancy.HaveBabyHospital h)
        {
            Sim mom = h != null ? h.Actor : null;
            if (mom == null || mom.SimDescription == null) return;
            ulong id = mom.SimDescription.SimDescriptionId;
            if (!h.BabyShouldBeBorn) return;
            if (sBrNewborns.GetValue(h) != null)
            {
                int f;
                if (sBirthFails.TryGetValue(id, out f) && f > 0) PatchCounters()[2476]++;
                sBirthFails.Remove(id);
                BrState st;
                if (sBr.TryGetValue(id, out st)) { if (st.Watch != AlarmHandle.kInvalidHandle) mom.RemoveAlarm(st.Watch); sBr.Remove(id); }
                return;
            }
            int n;
            sBirthFails.TryGetValue(id, out n);
            if (sBirthFails.Count > 256) sBirthFails.Clear();  // shortcut: a storm of distinct moms starts over
            sBirthFails[id] = n + 1;
            PatchCounters()[2439]++;
            // F5: EA's Cleanup reads Actor.SimDescription.Pregnancy.HaveTheBaby; with no pregnancy left there is nothing to birth
            if (ShortcutOn("birthroute") && mom.SimDescription.Pregnancy == null) { h.BabyShouldBeBorn = false; PatchCounters()[2442]++; }
        }

        // F4 after a hospital birth: EA's tail without the GoHome / GoInsideLot on the null home lot.
        static void BrAfterHomeless(Pregnancy.HaveBabyHospital h)
        {
            Sim mom = h.Actor;
            BabyBasket basket = mom.GetObjectInRightHand() as BabyBasket;
            if (basket != null)
            {
                InteractionInstance put = Pregnancy.PutDownBabyBasket.Singleton.CreateInstance(basket, mom, h.GetPriority(), false, false);
                mom.InteractionQueue.PushAsContinuation(put, true);
                Pregnancy.SetupPickUpBabiesInBasket(mom, basket, h.GetPriority(), false, false);
            }
            PatchCounters()[2475]++;
        }

        public class BrPregnancyHook : Pregnancy
        {
            public void HaveHook()
            {
                bool done = false;
                try { done = BrHave((object)this as Pregnancy, mMom, HaveOrig, HaveTheBaby); }
                catch (Exception) { PatchCounters()[2442]++; done = sBrOrigRan; }
                if (!done) HaveOrig();
            }
            public void HaveOrig() { } // receives Pregnancy.HaveTheBaby's original body
        }

        static bool sBrOrigRan;  // sim thread only

        static bool BrHave(Pregnancy p, Sim mom, AlarmTimerCallback orig, AlarmTimerCallback reHave)  // orig: any void() delegate (no Action in the game's mscorlib)
        {
            sBrOrigRan = false;
            if (!ShortcutOn("birthroute") || p == null || p is PetPregnancy || mom == null || mom.HasBeenDestroyed || mom.SimDescription == null) return false;
            int[] c = PatchCounters();
            ulong id = mom.SimDescription.SimDescriptionId;
            bool current = mom.SimDescription.Pregnancy == p;
            int act = BirthCallAction(true, current, current && BrBirthQueued(mom));
            if (act == 1)
            {
                if (!current) sBr.Remove(id);
                c[2471]++;
                return true;
            }
            bool npc = IsNpcSim(mom);
            BrState st;
            if (!sBr.TryGetValue(id, out st))
            {
                if (sBr.Count > 256) sBr.Clear();  // shortcut: a storm of distinct moms starts over
                sBr[id] = st = new BrState();
            }
            long now = SimClock.CurrentTicks;
            if (npc && st.Attempts > 0)
            {
                long wait = SimClock.ConvertToTicks(BirthRetryWaitMin(true, true, st.Attempts), TimeUnit.Minutes);
                long left = st.Last + wait - now;
                if (left > 0 && now >= st.Last)
                {
                    mom.AddAlarm(Math.Max(1f, SimClock.ConvertFromTicks(left, TimeUnit.Minutes)), TimeUnit.Minutes, reHave, "Re-have the baby.", AlarmType.AlwaysPersisted);
                    c[2469]++;
                    return true;
                }
            }
            if (!mom.BuffManager.HasElement(BuffNames.BabyIsComing))
            {
                mom.BuffManager.AddElement(BuffNames.BabyIsComing, Origin.FromPregnancy);  // as EA's Continue() at hour >= 70
                c[2472]++;
            }
            List<RabbitHole> hospitals = RabbitHole.GetRabbitHolesOfType(RabbitHoleType.Hospital);
            int kind = BirthKind(true, npc, st.Attempts, st.LastKind, BrHomeOk(mom), hospitals.Count > 0);
            if (npc && mom.LotHome == null && kind != 2)
            {
                if (!BrQueueHospital(p, mom, hospitals)) { orig(); sBrOrigRan = true; }  // no hospital: EA's code
                else c[2441]++;
            }
            else if (kind == 2)
            {
                BrPreamble(p, mom);
                if (mom.InteractionQueue.Add(Pregnancy.HaveBabyHome.Singleton.CreateInstance(mom.LotHome, mom, new InteractionPriority(InteractionPriorityLevel.Pregnancy), false, false)))
                {
                    ActiveTopic.AddToSim(mom, "Recently Had Baby");
                    c[2440]++;
                    DiagLine("birthroute: " + mom.FullName + " home birth after " + st.Attempts + " failed attempts");
                }
            }
            else if (kind == 1)
            {
                if (BrQueueHospital(p, mom, hospitals))
                {
                    c[2440]++;
                    DiagLine("birthroute: " + mom.FullName + " hospital birth after " + st.Attempts + " failed attempts");
                }
            }
            else { orig(); sBrOrigRan = true; }
            InteractionInstance queued = BrBirthInteraction(mom);
            if (queued == null)
            {
                // F2: the queue refused it (or EA threw): re-arm instead of losing the birth
                mom.AddAlarm(kBirthLostRetryMin, TimeUnit.Minutes, reHave, "Re-have the baby.", AlarmType.AlwaysPersisted);
                c[2470]++;
                return true;
            }
            Pregnancy.HaveBabyHospital hh = queued as Pregnancy.HaveBabyHospital;
            st.Attempts++;
            st.Last = now;
            st.LastKind = hh != null ? 1 : 2;
            if (npc && hh != null && st.Attempts >= 2) BrPlaceNearHospital(mom, hh.Target);
            // F2 watchdog: EA's own re-have delegate, AlwaysPersisted like EA's; a no-op (F3) while the birth is queued or done
            if (st.Watch != AlarmHandle.kInvalidHandle) mom.RemoveAlarm(st.Watch);
            st.Watch = mom.AddAlarm(kBirthWatchdogMin, TimeUnit.Minutes, reHave, "Re-have the baby.", AlarmType.AlwaysPersisted);
            c[2474]++;
            return true;
        }

        // EA's HaveTheBaby preamble: the contraction broadcaster and the hourly / contraction alarms end.
        static void BrPreamble(Pregnancy p, Sim mom)
        {
            ReactionBroadcaster b = sBrBroadcast.GetValue(p) as ReactionBroadcaster;
            if (b != null) b.Dispose();
            mom.RemoveAlarm(p.PreggersAlarm);
            mom.RemoveAlarm((AlarmHandle)sBrContractions.GetValue(p));
        }

        // EA's hospital branch of HaveTheBaby without its home-distance comparison: the closest hospital, BabyShouldBeBorn.
        static bool BrQueueHospital(Pregnancy p, Sim mom, List<RabbitHole> hospitals)
        {
            RabbitHole best = null;
            float bestD = float.MaxValue;
            foreach (RabbitHole r in hospitals)
            {
                if (r == null || r.RabbitHoleProxy == null) continue;
                float d = r.RabbitHoleProxy.GetDistanceToObject(mom);
                if (d < bestD) { bestD = d; best = r; }
            }
            if (best == null) return false;
            BrPreamble(p, mom);
            Pregnancy.HaveBabyHospital h = Pregnancy.HaveBabyHospital.Singleton.CreateInstance(best, mom, new InteractionPriority(InteractionPriorityLevel.Pregnancy), false, false) as Pregnancy.HaveBabyHospital;
            h.BabyShouldBeBorn = true;
            if (!mom.InteractionQueue.Add(h)) return true;  // refused: the lost-birth re-arm follows
            ActiveTopic.AddToSim(mom, "Recently Had Baby");
            if (mom.LotHome == null) DiagLine("birthroute: " + mom.FullName + " (no home lot) sent to the closest hospital");
            return true;
        }

        // a birth interaction already in the queue (running or waiting): HaveBabyHospital with the birth due, or HaveBabyHome
        static bool BrBirthQueued(Sim mom) { return BrBirthInteraction(mom) != null; }
        static InteractionInstance BrBirthInteraction(Sim mom)
        {
            InteractionQueue q = mom.InteractionQueue;
            if (q == null) return null;
            foreach (InteractionInstance ia in q.InteractionList)
            {
                Pregnancy.HaveBabyHospital h = ia as Pregnancy.HaveBabyHospital;
                if ((h != null && h.BabyShouldBeBorn) || ia is Pregnancy.HaveBabyHome) return ia;
            }
            return null;
        }

        // HaveBabyHome.Definition.Test: the target lot's household is the Sim's. Women only (male alien pregnancies use
        // MaleHaveBabyHome), a live pregnancy.
        static bool BrHomeOk(Sim mom)
        {
            Lot home = mom.LotHome;
            return home != null && !home.IsWorldLot && home.Household != null && home.Household == mom.Household
                && !mom.IsMale && !mom.IsPet && mom.SimDescription.Pregnancy != null;
        }

        // An unseen NPC mom after a failed hospital trip: placed at a good spot by the hospital (EA's HaveBabyHome places its
        // mom at her lot the same way when its route fails). Only standing (no carried child, horse or vehicle), and only when
        // the spot is unseen too.
        static void BrPlaceNearHospital(Sim mom, RabbitHole hospital)
        {
            if (hospital == null || hospital.RabbitHoleProxy == null || mom.Posture != mom.Standing || !SimUnseen(mom)) return;
            Vector3 at = hospital.RabbitHoleProxy.Position;
            if (!UfPosUnseen(at, 10f)) return;
            if (GlobalFunctions.PlaceAtGoodLocation(mom, new World.FindGoodLocationParams(at), false))
            {
                PatchCounters()[2473]++;
                DiagLine("birthroute: " + mom.FullName + " (unseen) placed at the hospital after a failed trip");
            }
        }
    }
}
