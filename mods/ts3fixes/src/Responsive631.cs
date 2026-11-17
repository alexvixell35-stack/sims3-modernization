using System;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Responsiveness switches (docs/631). Both default off.
    public static partial class Fixes
    {
        // ---- docs/631 §2 playerfirst --------------------------------------------------------------------------------------
        // What delays a click (docs/631 §1, phase-2026111* ts3native.log, 219 runs): the async route worker runs one job at a
        // time, oldest START first (ts3native ServeAsync). 52102 own-START jobs ran >= 200 ms (37323 unseen, 6321 seen NPC,
        // 8380 household); 171 household long jobs started within 3 ms of the end of a townie long job they had queued
        // behind (mean 353 ms, max 1965 ms of townie work first). The Sim's own InteractionQueue already puts UserDirected
        // ahead of autonomous work (EA); the route worker and our replanspread replay (FIFO, kSpreadPerTick a tick) did not.
        // Fix: a route the player ordered (household or selected Sim, or the Sim's current interaction is UserDirected) is
        //  - marked for ts3native right before its START (UserDiag op 37); with [RouteAsync] PlayerFirst=1 the worker runs a
        //    marked queued job before older unmarked ones (oldest first within each group);
        //  - put at the front of the replanspread replay (after the player entries already there, in their order).
        // Each job / replay is the same call on the same snapshot as before: only the order changes, never an answer.
        // Counters: [3660] marks honoured by ts3native, [3661] marks not honoured (PlayerFirst=0 or an older ts3native),
        // [3662] replanspread entries put first, [3663] player routes seen with the switch off, [3664] errors, [3665-3669] spare.
        internal static bool PfPlayerOrdered(bool on, bool ownCheck, bool household, bool selected, bool userDirected)
        {
            return on && !ownCheck && (household || selected || userDirected);
        }

        static bool PfIsPlayer(Sim s)
        {
            if (s == null || s.HasBeenDestroyed) return false;  // no Sim (traffic, a driverless route): never first
            if (IsPlayerSim(s)) return true;  // household, selectable or selected
            InteractionInstance ia = s.CurrentInteraction;
            return ia != null && !ia.Autonomous && ia.GetPriority().Level == InteractionPriorityLevel.UserDirected;
        }

        // UnseenFallback's plan hook, right after the scope op: mark this route for the worker
        static void PfRouteMark(Sim who, uint handle, bool ownCheck)
        {
            try
            {
                if (who == null || ownCheck) return;
                bool on = ShortcutOn("playerfirst");
                bool player = PfIsPlayer(who);
                if (!PfPlayerOrdered(on, ownCheck, player, false, false)) { if (!on && player) PatchCounters()[3663]++; return; }
                PatchCounters()[DiagSend(37, handle) ? 3660 : 3661]++;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3664]++; }
        }

        // ReplanDefer.HoldBack: a player Sim's held replan goes first
        static bool PfSpreadFirst(RoutingComponent rc)
        {
            try
            {
                if (!ShortcutOn("playerfirst") || rc == null) return false;
                return PfIsPlayer(rc.Owner as Sim);
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3664]++; return false; }
        }

        // PlayerFirstCheck run: the mark decision and the replay order with controls. Live: SpreadQueue order on a sample.
        public static object PlayerFirstCheck(string what)
        {
            string counters = B582Counters("playerfirst", 3660, new[] { "marks honoured", "marks not honoured", "replays first", "player routes, switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "playerfirst <run|result>";
            bool on = ShortcutOn("playerfirst");
            bool eaBug = !PfPlayerOrdered(false, false, true, false, false);
            // the replay order: townies a, b queued first, then player p; EA / off: a first; on: p first, then a, b in order
            string orderOn = PfOrder(true), orderOff = PfOrder(false);
            bool fixedPure = on && PfPlayerOrdered(on, false, true, false, false) && PfPlayerOrdered(on, false, false, true, false)
                && PfPlayerOrdered(on, false, false, false, true) && orderOn == "pqab";
            bool controls = eaBug && orderOff == "abpq"
                && !PfPlayerOrdered(true, false, false, false, false)   // a townie with an autonomous interaction
                && !PfPlayerOrdered(true, true, true, false, false);    // our own route checks (stuck check, routable probes)
            return B582Line("playerfirst", on, "a player-ordered route is marked and its replay runs first (order " + orderOn + ")", fixedPure,
                "EA order " + orderOff + ", townies and our own checks unmarked", controls, "", counters);
        }

        static string PfOrder(bool first)
        {
            SpreadQueue<string> q = new SpreadQueue<string>();
            q.Add("a", false); q.Add("b", false); q.Add("p", first); q.Add("q", first); q.Add("p", first);
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            q.DrainTick(10, delegate (string x) { return true; }, delegate (string x) { return true; }, delegate (string x) { b.Append(x); });
            return b.ToString();
        }

        // ---- docs/631 §3 zoomtier -----------------------------------------------------------------------------------------
        // [verified, ilspycmd Sims3.SimIFace.CameraController] GetPosition() (camera eye, world metres), IsMapViewModeEnabled();
        // no script API exposes the field of view or the back-buffer size. A Sim is ~1.8 m tall; at camera distance d its
        // height on screen is H * 1.8 / (2 d tan(fov/2)) pixels. With the TS3 world camera's ~45 degree vertical view (the
        // value is not readable from script: an estimate) that is 1739 / d px on an 800-line screen (Steam Deck) and 2609 / d
        // px on 1200 lines (Legion Go S): at kZoomTierMeters = 250 m a Sim is ~7 px (800) / ~10 px (1200) tall, a blob with no
        // readable pose or face. Map view draws the town as the map (Sims not shown as Sims): always "too small".
        // ZoomHidden is the one shared helper for the off-screen tier-2 decisions (rabbit hole rhentryrelaxnear here,
        // unseenservice_offscreen on feat/unseen-service-1117 calls it in place of its !UcOnScreen): true = not on screen, or
        // (switch on) on screen but too small. Never for a household or selected Sim (IsPlayerSim), which stays EA's.
        // Counters: [3670] tier-2 decisions lifted by the zoom rule (on screen but too small), [3671] the same with the switch
        // off (would have been lifted), [3672] errors, [3673-3689] spare.
        internal const float kZoomTierMeters = 250f;

        // Pure: does a tier-2 decision treat this object as unseen?
        internal static bool ZtHidden(bool on, bool onScreen, bool player, bool mapView, float camDist)
        {
            if (!onScreen) return true;  // EA's own rule for the tier (World.IsObjectOnScreen)
            return on && !player && (mapView || camDist >= kZoomTierMeters);
        }

        static bool ZtTooSmall(Vector3 p, out bool mapView)
        {
            mapView = CameraController.IsMapViewModeEnabled();
            if (mapView) return true;
            Vector3 c = CameraController.GetPosition();
            float dx = c.x - p.x, dy = c.y - p.y, dz = c.z - p.z;
            return dx * dx + dy * dy + dz * dz >= kZoomTierMeters * kZoomTierMeters;
        }

        // The shared helper. sim = the Sim this decision is about (null for a door, a bar slot: then only the object's own
        // on-screen / zoom test). A player or selected Sim is never hidden by the zoom rule.
        internal static bool ZoomHidden(GameObject o, Sim sim)
        {
            if (o == null) return true;
            if (!UcOnScreen(o)) return true;
            try
            {
                if (sim != null && IsPlayerSim(sim)) return false;
                bool map;
                if (!ZtTooSmall(o.Position, out map)) return false;
                bool on = ShortcutOn("zoomtier");
                PatchCounters()[on ? 3670 : 3671]++;
                return on;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3672]++; return false; }
        }

        internal static bool ZoomHidden(Sim s) { return ZoomHidden(s, s); }

        internal static bool ZoomHidden(ObjectGuid id, Sim sim)
        {
            GameObject o = GameObject.GetObject<GameObject>(id);
            return o != null ? ZoomHidden(o, sim) : !UcOnScreen(id);
        }

        // ZoomTierCheck run: the tier-2 unseen decision with controls. Live: camera distance / map view now.
        public static object ZoomTierCheck(string what)
        {
            string counters = B582Counters("zoomtier", 3670, new[] { "tier-2 lifted by zoom", "would lift, switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "zoomtier <run|result>";
            bool on = ShortcutOn("zoomtier");
            bool eaBug = !ZtHidden(false, true, false, false, 400f);  // on screen, 400 m away: EA counts it as seen
            bool fixedPure = on && ZtHidden(on, true, false, false, 400f) && ZtHidden(on, true, false, true, 10f);
            bool controls = eaBug
                && ZtHidden(true, false, true, false, 5f)      // off screen stays the tier's own rule (EA)
                && !ZtHidden(true, true, false, false, 100f)   // on screen and close: seen
                && !ZtHidden(true, true, true, false, 400f)    // household / selected: never
                && !ZtHidden(true, true, true, true, 400f);    // ... not even in map view
            string live = "";
            try
            {
                Vector3 c = CameraController.GetPosition();
                live = "; camera map view=" + CameraController.IsMapViewModeEnabled() + " zoom=" + CameraController.GetZoom()
                    + " eye=(" + c.x.ToString("F0") + "," + c.y.ToString("F0") + "," + c.z.ToString("F0") + ") limit=" + kZoomTierMeters + " m";
            }
            catch (Exception) { live = "; camera not readable"; }
            return B582Line("zoomtier", on, "an on-screen NPC farther than " + kZoomTierMeters + " m or in map view counts as unseen for tier 2", fixedPure,
                "off-screen kept, close / household / selected seen", controls, "", counters + live);
        }
    }
}
