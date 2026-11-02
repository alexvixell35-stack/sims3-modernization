using System.Collections.Generic;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // unseencost probe (docs/227, counters 690-743, test runs only): what the script unseen checks cost, so docs/227's rule can
    // decide whether one central per-tick unseen table pays. Changes no behaviour. Per site (kUc*): calls, top-level calls (not
    // inside another timed site; sum mean x top-level calls for a total), and 1 call in 16 timed (StopWatch us). Also the
    // interop calls they make (World.IsObjectOnScreen, UfCamAsk, RefreshViewers reads) and Sims asked per sim tick vs distinct
    // Sims (repeat ratio = what a per-tick table would save). The probe's own cost is measured: every 1024th call runs 16 no-op
    // Begin/End pairs (+ Sim notes) on scratch counters and one bare clock pair (the floor every sample carries).
    // Counters: [690 + 4 i] calls, [+1] top-level, [+2] timed, [+3] timed us for site i 0..10; [734] IsObjectOnScreen, [735]
    // UfCamAsk, [736] RefreshViewers reads; [737] Sim asks, [738] distinct Sims summed over ticks, [739] ticks with the probe on;
    // [740] calibration pairs, [741] their us, [742] floor pairs, [743] their us; [750-757] sites 11-12 (utfetch, utlookup).
    public static partial class Fixes
    {
        const int kUcBase = 690, kUcSites = 11;
        const int kUcFar = 0, kUcRefresh = 1, kUcSimUnseen = 2, kUcTownie = 3, kUcGoHome = 4, kUcPos = 5, kUcLot = 6, kUcGoal = 7,
            kUcDispose = 8, kUcGate = 9, kUcUfWhy = 10;
        // docs/227 §9: the unseen table's script reads, counters [750 + 4 (i - 11)]: 11 utfetch (the copy path's per-tick Fetch),
        // 12 utlookup (one row per ask: a Bits IC, or the copy path's binary search)
        const int kUcBase2 = 750, kUcUtFetch = 11, kUcUtLookup = 12;
        static int UcAt(int site) { return site < kUcSites ? kUcBase + 4 * site : kUcBase2 + 4 * (site - kUcSites); }
        const int kUcOnScreen = 734, kUcCamAsk = 735, kUcViewRead = 736, kUcSimAsks = 737, kUcSimDistinct = 738, kUcTicks = 739,
            kUcSelfN = 740, kUcSelfUs = 741, kUcFloorN = 742, kUcFloorUs = 743;
        // the switch is read once per sim tick (ShortcutOn is a dictionary lookup; some sites run ~1000 times/s)
        static long sUcTick = long.MinValue;
        static bool sUcOn, sUcCal;
        static int sUcDepth, sUcAll, sUcNextCal = 1024;
        static StopWatch sUcSw;
        static readonly Dictionary<ulong, bool> sUcSims = new Dictionary<ulong, bool>(), sUcCalSims = new Dictionary<ulong, bool>();
        static readonly int[] sUcScratch = new int[kUcFloorUs + 1];  // the calibration's counters (same indices, thrown away)

        static bool UcOn()
        {
            long now = SimClock.CurrentTicks;
            if (now == sUcTick) return sUcOn;
            sUcTick = now;
            sUcOn = ShortcutOn("unseencost");
            sUcDepth = 0;  // shortcut: a site body that threw left it raised; reset per tick
            sUcSims.Clear();
            if (sUcOn)
            {
                if (sUcSw == null) { sUcSw = StopWatch.Create(StopWatch.TickStyles.Microseconds); sUcSw.Restart(); }
                PatchCounters()[kUcTicks]++;
            }
            return sUcOn;
        }

        // -2 = probe off (UcEnd does nothing), -1 = counted, not timed, else the clock at the start (us, wraps: uint math)
        static long UcBegin(int site, ulong simId)
        {
            if (!UcOn()) return -2;
            return UcBeginIn(PatchCounters(), sUcSims, UcAt(site), simId);
        }

        static long UcBeginIn(int[] c, Dictionary<ulong, bool> sims, int at, ulong simId)
        {
            bool top = sUcDepth == 0;
            if (top) c[at + 1]++;
            sUcDepth++;
            if (simId != 0)
            {
                c[kUcSimAsks]++;
                if (!sims.ContainsKey(simId)) { sims[simId] = true; c[kUcSimDistinct]++; }
            }
            // top level only, so no timed outer call ever contains a calibration
            if (++sUcAll >= sUcNextCal && top && !sUcCal) { sUcNextCal = sUcAll + 1024; UcCalibrate(); }
            return (++c[at] & 15) == 0 ? (long)sUcSw.GetElapsedTime() : -1;
        }

        static void UcEnd(int site, long t0) { if (t0 != -2) UcEndIn(PatchCounters(), UcAt(site), t0); }

        static void UcEndIn(int[] c, int at, long t0)
        {
            sUcDepth--;
            if (t0 < 0) return;
            c[at + 2]++;
            c[at + 3] += (int)unchecked(sUcSw.GetElapsedTime() - (uint)t0);
        }

        static void UcCalibrate()
        {
            int[] c = PatchCounters();
            sUcCal = true;
            sUcCalSims.Clear();
            uint a = sUcSw.GetElapsedTime();
            for (int i = 0; i < 16; i++) UcEndIn(sUcScratch, kUcBase, UcBeginIn(sUcScratch, sUcCalSims, kUcBase, (ulong)(i + 1)));
            uint b = sUcSw.GetElapsedTime(), d = sUcSw.GetElapsedTime();
            sUcCal = false;
            c[kUcSelfN] += 16;
            c[kUcSelfUs] += (int)unchecked(b - a);
            c[kUcFloorN]++;
            c[kUcFloorUs] += (int)unchecked(d - b);
        }

        // interop calls of the unseen checks (counted while the probe is on)
        static void UcCount(int slot) { if (sUcOn) PatchCounters()[slot]++; }
        static bool UcOnScreen(IHasScriptProxy o) { UcCount(kUcOnScreen); return World.IsObjectOnScreen(o); }
        static bool UcOnScreen(ObjectGuid id) { UcCount(kUcOnScreen); return World.IsObjectOnScreen(id); }
    }
}
