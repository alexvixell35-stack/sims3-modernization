using System;
using System.Collections.Generic;

namespace Ts3Agent
{
    // What a player must not see when the camera lands on a Sim (docs/222 §11.2). Game-independent, so tools/selftest/focusrules
    // checks it; Handlers/FocusHandlers.cs fills the samples and throws on the reason returned here.
    public static class FocusRules
    {
        public sealed class Limits
        {
            public double SnapS = 3;          // real seconds from first on screen to exact state: hold over, queued picks pass Test
            public double MaxStepM = 12;      // one sample step; walking + a 1 s hitch stays under it, a placement of a Sim usually does not
            public double IdleSimMin = 240;   // sim minutes on screen with nothing to do (fixed, Rodney 2026-11-06, docs/222 §11)
            public double StuckS = 60;        // real seconds routing or held without moving
            public double StarveMotive = -95; // hunger or energy at the end of the watch
        }

        public sealed class Sample
        {
            public double Sec, SimMin, X, Z, MinMotive;
            public bool OnScreen, Hidden, HasAction, Routing, Held;
            public bool InRabbitHole;   // SimInRabbitHolePosture (docs/222 §11.10: did EA's rabbit-hole entry place him)
            public string Interaction;  // current interaction name, null = none
            public int BadQueue;  // queued autonomous picks that fail a live Test; -1 = not tested at this sample
            public string BadNames;  // their interaction names, null = none
        }

        public sealed class Stats { public double MaxStepM, MaxIdleSimMin, FirstSeenS = -1, SnapDoneS = -1; public int RhSnaps; public string RhNote; }

        // focus_watch distinct (docs/222 §11): the ring of recent direct-pick tags, newest last, no repeats, at most cap
        public static void RingAdd(List<ulong> ring, ulong id, int cap)
        {
            if (id == 0) return;
            ring.Remove(id);
            ring.Add(id);
            while (ring.Count > cap) ring.RemoveAt(0);
        }
        // newest id not judged yet that the caller can watch (alive, outside the household), 0 = none
        public static ulong PickNewest(List<ulong> ring, Dictionary<ulong, bool> judged, Predicate<ulong> watchable)
        {
            for (int i = ring.Count - 1; i >= 0; i--)
                if (!judged.ContainsKey(ring[i]) && watchable(ring[i])) return ring[i];
            return 0;
        }

        // docs/222 §11.2: the 3 s rule judges our snap-back (FlipCheck), so it counts only once our worker tagged a direct pick
        // (ts3fixes counter 624 autunseen_tagged). With none, every queued pick is the game's own, and a vanilla queue may hold
        // picks that fail Test until they reach the head (bp-gu2-arrive-off 2026-11-08, autunseen off: Deidre Littler).
        public static int SnapQueue(int failing, int workerTagged) { return workerTagged > 0 ? failing : 0; }

        // docs/222 §11.11: an arrival counts toward arrivalsMin only for a spawn_sims crowd Sim (SimDescriptionId); passers-by and
        // rabbit-hole workers on the camera lot made bp-gu2-arrive 3 (off) vs 2 (on) by chance while no crowd Sim arrived in either arm.
        public static bool CrowdArrival(ulong descId, Dictionary<ulong, bool> crowd) { return descId != 0 && crowd.ContainsKey(descId); }

        // docs/222 §11.12: focus_watch lot. A houseboat lot's centre is off-world and the ground under the docked boat is its port
        // lot (IP Atlantis in Picturesque Port), so a houseboat counts when the camera target is within 30 m of the boat.
        public static bool CameraOnLot(bool lotUnderTarget, bool houseboat, double toBoatM) { return lotUnderTarget || (houseboat && toBoatM <= 30); }

        static string Pt(Sample x) { return x.X.ToString("F1") + "," + x.Z.ToString("F1") + " @" + x.Sec.ToString("F2") + " s"; }

        // null = nothing a player would notice; else the reason (the caller throws it). Rules run only from the first on-screen sample.
        public static string Judge(List<Sample> s, Limits l, Stats st)
        {
            int first = s.FindIndex(x => x.OnScreen);
            if (first < 0) return "never on screen: the camera did not reach it, nothing was checked";
            double t0 = s[first].Sec, idleFrom = -1, stillFrom = -1;
            st.FirstSeenS = t0;
            string snap = "no sample " + l.SnapS + " s after first on screen";
            for (int i = first; i < s.Count; i++)
            {
                Sample c = s[i], p = i > first ? s[i - 1] : null;
                bool pending = c.Held || c.BadQueue > 0;
                if (c.Sec - t0 >= l.SnapS && pending)
                    return "not exact " + (c.Sec - t0).ToString("F1") + " s after focus: " + (c.Held ? "still in a relaxed hold" : c.BadQueue + " queued pick(s) fail a live Test" + (c.BadNames != null ? " (" + c.BadNames + ")" : ""));
                if (!pending && st.SnapDoneS < 0 && c.BadQueue >= 0) st.SnapDoneS = c.Sec - t0;
                if (c.Sec - t0 >= l.SnapS) snap = null;
                double step = p == null ? 0 : Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Z - p.Z) * (c.Z - p.Z));
                if (p != null && c.OnScreen && p.OnScreen && !c.Hidden && !p.Hidden)
                {
                    // docs/222 §11.10: EA's rabbit-hole entry/exit places the Sim at the slot (game teleport, in_rh flips in the sample), not a pop
                    bool rhSnap = (c.InRabbitHole || p.InRabbitHole) && step > l.MaxStepM;
                    if (rhSnap)
                    {
                        st.RhSnaps++;
                        if (st.RhNote == null) st.RhNote = "rh_snap: " + step.ToString("F1") + " m at " + (c.Sec - t0).ToString("F1") + " s (" + Pt(p) + " -> " + Pt(c) + "; in_rh " + p.InRabbitHole + " -> " + c.InRabbitHole + ")";
                    }
                    else st.MaxStepM = Math.Max(st.MaxStepM, step);
                    if (!rhSnap && step > l.MaxStepM) return "pop: moved " + step.ToString("F1") + " m in one step on screen at " + (c.Sec - t0).ToString("F1") + " s"
                        + " (" + Pt(p) + " -> " + Pt(c) + "; interaction " + (p.Interaction ?? "none") + " -> " + (c.Interaction ?? "none")
                        + "; in_rh " + p.InRabbitHole + " -> " + c.InRabbitHole + ")";
                }
                if (!(c.Routing || c.Held) || step > 0.05) stillFrom = -1;
                else if (stillFrom < 0) stillFrom = c.Sec;
                else if (c.Sec - stillFrom >= l.StuckS) return "stuck: routing without moving for " + (c.Sec - stillFrom).ToString("F0") + " s";
                if (c.HasAction || c.Routing || c.Held) idleFrom = -1;
                else if (idleFrom < 0) idleFrom = c.SimMin;
                else
                {
                    st.MaxIdleSimMin = Math.Max(st.MaxIdleSimMin, c.SimMin - idleFrom);
                    if (c.SimMin - idleFrom > l.IdleSimMin) return "idle: nothing to do for " + (c.SimMin - idleFrom).ToString("F0") + " sim minutes on screen";
                }
            }
            if (snap != null) return snap;
            if (s[s.Count - 1].MinMotive <= l.StarveMotive) return "needs starving: lowest of hunger, energy " + s[s.Count - 1].MinMotive.ToString("F0");
            return null;
        }
    }
}
