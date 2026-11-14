using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness check for syncpartnergone (docs/546 §14; test only, called through the agent's reflect_call, never by the
    // game). SyncPartnerGoneCheck:
    //   "start reset" / "start cancel": the two NPC teens-or-older nearest the active Sim (IsNpcSim: never the household)
    //       get their queues cancelled and are placed 3 m and 10 m from the active Sim; the first (the waiter) is queued
    //       EA's Chat on the second (the partner), as the pie menu does. A watcher task waits until the two are in sync on
    //       that social (waiter in sync with the partner, the partner running the linked SocialInteractionB), then resets
    //       the partner (SetObjectToReset: the bug) or cancels its SocialInteractionB (control: EA passes the cancel on),
    //       and times in sim minutes how long the waiter's social stays running after that.
    //   "result expect stuck" (off arm): PASS when the waiter stood >= 20 sim min (EA's 30 min giveup) or is still standing.
    //   "result expect early" (on arm): PASS when it ended within 5 sim min and counter 2370 or 2371 rose.
    //   "result expect any": the times only (control arm).
    // Every line gives: mode, waiter, partner, minutes from the reset/cancel to the end of the waiter's social, whether the
    // waiter got SynchronizationFailed, and counters 2370-2373 since start.
    public static partial class Fixes
    {
        static Sim sScWaiter, sScPartner;
        static InteractionInstance sScSocial;
        static string sScMode, sScState = "not started";
        static long sScHitTicks;
        static float sScMinutes = -1f;
        static bool sScSyncFailed;
        static int sScGen;
        static int[] sScC0 = new int[4];
        const float kScSyncLimitMin = 30f, kScStandLimitMin = 60f;

        public static object SyncPartnerGoneCheck(string what)
        {
            if (what.StartsWith("result")) return ScResult(what.Length > 7 ? what.Substring(7) : "expect any");
            if (what != "start reset" && what != "start cancel") return "syncpartnergone check: <start reset|start cancel|result expect stuck|result expect early|result expect any>";
            Sim active = Sim.ActiveActor;
            if (active == null || active.LotCurrent == null) return "FAIL: no active Sim on a lot";
            List<Sim> npcs = new List<Sim>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                if (IsNpcSim(s) && s.InWorld && s.LotCurrent != null && s.SimDescription.IsHuman && s.SimDescription.TeenOrAbove
                    && s.Service == null && !s.IsSleeping && !(s.Posture is Sims3.Gameplay.ActorSystems.InRabbitHolePosture))
                    npcs.Add(s);
            if (npcs.Count < 2) return "FAIL: needs two NPC teens or older in the world, found " + npcs.Count;
            Vector3 at = active.Position;
            npcs.Sort((a, b) => (a.Position - at).LengthSqr().CompareTo((b.Position - at).LengthSqr()));
            Sim waiter = npcs[0], partner = npcs[1];
            string why = ScPlace(waiter, at + new Vector3(3f, 0f, 0f)) ?? ScPlace(partner, at + new Vector3(10f, 0f, 0f));
            if (why != null) return "FAIL: " + why;
            InteractionInstance chat = new SocialInteractionA.Definition("Chat", new string[0], null, false)
                .CreateInstance(partner, waiter, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            if (!waiter.InteractionQueue.Add(chat)) return "FAIL: Chat not queued on " + waiter.FullName;
            sScWaiter = waiter; sScPartner = partner; sScSocial = chat; sScMode = what.Substring(6);
            sScState = "waiting for sync"; sScMinutes = -1f; sScSyncFailed = false;
            int[] c = PatchCounters();
            for (int i = 0; i < 4; i++) sScC0[i] = c[2370 + i];
            int gen = ++sScGen;
            Simulator.AddObject(new LoopTask(delegate { ScWatch(gen); }));
            return "started " + sScMode + ": " + waiter.FullName + " Chat on " + partner.FullName + ", switch " + (ShortcutOn("syncpartnergone") ? "on" : "off");
        }

        static string ScPlace(Sim s, Vector3 near)
        {
            Vector3 pos, fwd;
            if (!GlobalFunctions.FindGoodLocation(s, new World.FindGoodLocationParams(near), out pos, out fwd)) return "no good location for " + s.FullName;
            s.InteractionQueue.CancelAllInteractions();
            PlaceSim(s, pos, fwd, "syncpartnergone check");
            return null;
        }

        // Watcher task: until the two are in sync, then the reset or cancel, then until the waiter's social ends.
        static void ScWatch(int gen)
        {
            Sim w = sScWaiter, p = sScPartner;
            InteractionInstance a = sScSocial;
            long t0 = SimClock.CurrentTicks;
            SocialInteractionB b = null;
            while (gen == sScGen)
            {
                if (!w.InteractionQueue.IsRunning(a, true) && !w.InteractionQueue.HasInteraction(a)) { sScState = "FAIL: the social ended before the two were in sync"; return; }
                b = a.LinkedInteractionInstance as SocialInteractionB;
                if (b != null && p.CurrentInteraction == b && w.SynchronizationTarget == p && w.SynchronizationLevel >= Sim.SyncLevel.Started) break;
                if (SimClock.ConvertFromTicks(SimClock.CurrentTicks - t0, TimeUnit.Minutes) > kScSyncLimitMin) { sScState = "FAIL: not in sync after " + kScSyncLimitMin + " sim min"; return; }
                Simulator.Sleep(0u);
            }
            if (gen != sScGen) return;
            string partnerLevel = p.SynchronizationLevel.ToString(), routing = p.SimRoutingComponent != null && p.SimRoutingComponent.IsRouting ? ", routing" : "";
            if (sScMode == "reset") p.SetObjectToReset();
            else p.InteractionQueue.CancelInteraction(b, false);
            sScHitTicks = SimClock.CurrentTicks;
            sScState = "standing (partner " + sScMode + " at " + partnerLevel + routing + ", waiter at " + w.SynchronizationLevel + ")";
            while (gen == sScGen)
            {
                if (w.HasExitReason(ExitReason.SynchronizationFailed)) sScSyncFailed = true;
                float min = SimClock.ConvertFromTicks(SimClock.CurrentTicks - sScHitTicks, TimeUnit.Minutes);
                if (!w.InteractionQueue.IsRunning(a, true)) { sScMinutes = min; sScState = "ended" + sScState.Substring(8); return; }
                if (min > kScStandLimitMin) { sScState = "still " + sScState; return; }
                Simulator.Sleep(0u);
            }
        }

        static string ScResult(string expect)
        {
            int[] c = PatchCounters();
            string counters = "counters 2370 +" + (c[2370] - sScC0[0]) + " 2371 +" + (c[2371] - sScC0[1]) + " 2372 +" + (c[2372] - sScC0[2]) + " 2373 +" + (c[2373] - sScC0[3]);
            if (sScWaiter == null) return "FAIL: not started";
            bool ended = sScMinutes >= 0f;
            float stood = ended ? sScMinutes : SimClock.ConvertFromTicks(SimClock.CurrentTicks - sScHitTicks, TimeUnit.Minutes);
            string line = expect + ": " + sScMode + ", " + sScWaiter.FullName + " -> " + sScPartner.FullName + ", " + sScState + ", stood "
                + stood.ToString("0.0") + " sim min" + (ended ? "" : " so far") + ", sync failed " + sScSyncFailed + ", " + counters;
            if (sScState.StartsWith("FAIL") || sScState == "waiting for sync") return "FAIL " + line;
            bool ok;
            if (expect == "expect stuck") ok = stood >= 20f;
            else if (expect == "expect early") ok = ended && stood <= 5f && (c[2370] > sScC0[0] || c[2371] > sScC0[1]) && c[2373] == sScC0[3];
            else ok = ended;
            return (ok ? "PASS " : "FAIL ") + line;
        }
    }
}
