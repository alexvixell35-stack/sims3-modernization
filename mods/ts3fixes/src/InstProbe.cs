using System;
using System.Collections;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // instprobe (docs/325, 2026-11-22, measurement only, default off): what one Sim instantiation and one Sim.Destroy cost on
    // the sim thread, split by part, for the far-Sim model's spread queue (docs/324). On when ts3native's [InstProbe] Enabled=1
    // (GetOption2 q 15, asked once: covers the world load's own instantiations) or the switch instprobe (patches) is on.
    // [verified, decompiled] SimDescription.Instantiate(Vector3, ResourceKey, bool, bool) is where every SimDescription
    // instantiate overload ends (Instantiate(Lot, ResourceKey) and InstantiateOffScreen pick a spot first): CreatedSim check,
    // AgingState.PreInstantiateSim (a deferred age-up's outfit build: sleeps until the SimBuilderTask is done),
    // GlobalFunctions.CreateObjectWithOverrides("GameSim", ...) (engine object + Sim.OnCreation: managers, autonomy, motives,
    // interaction queue, buffs; Sim.OnStartup: components, interactions, alarms, SimUpdate task [inferred: both run inside the
    // create call; the startuponly row counts any OnStartup outside an instantiation]), SimRoutingComponent
    // EnableDynamicFootprint + ForceUpdateDynamicFootprint, AddInitialObjects, then skills / occult / kSimInstantiated
    // listeners / health ("rest"). Every hook calls EA's method with the same arguments and returns its result: no Sim, lot or
    // save changes, so household Sims are untouched by construction (scope is only recorded: household, selected, npc,
    // unknown). Lines go out from the frame task, never inside a timed call. Per-task records: the age-up wait sleeps, so
    // another task's instantiation can run inside it; each task attributes parts to its own open record only.
    // Counters: [1530] instantiations timed, [1531] destroys timed, [1532] OnStartup outside an instantiation (load-from-save
    // Sims), [1533] per-event lines dropped over the cap, [1534] probe errors, [1535] 1 = ts3native [InstProbe] Enabled=1 seen.
    public static partial class Fixes
    {
        const int kIpPartNone = 0, kIpPartCreate = 1, kIpPartFoot = 2, kIpPartInit = 3, kIpPartAge = 4;
        const int kIpLineCap = 4000, kIpSummaryMs = 300000;

        sealed class IpEv
        {
            public SimDescription Desc;
            public string Path, Head, Phase;
            public uint TOuter, TInner, InnerUs, TotalUs, AgeupUs, CreateUs, OnCreUs, OnStartUs, FootUs, InitUs;
            public long Tick0, AgeTicks, Ticks;
            public int Part, Frames, F0, F1, Seq;
            public bool Made, Nested;
        }

        static readonly Dictionary<ulong, List<IpEv>> sIpOpen = new Dictionary<ulong, List<IpEv>>();
        static int sIpOpenN;  // open records over all tasks: 0 = every part hook passes straight through
        static readonly List<IpEv> sIpPending = new List<IpEv>();  // finished, waiting for their two frames
        static readonly List<string> sIpQueue = new List<string>();  // destroy / startuponly lines for the frame task
        static readonly InstProbeCore.Stats sIpStats = new InstProbeCore.Stats();
        static readonly int[] sIpRing = new int[256];
        static int sIpRingN, sIpRingAt, sIpNative = -1, sIpLoadEnd, sIpLines, sIpSeq, sIpBeat, sIpSumAt, sIpGen;
        static bool sIpLoaded, sIpTaskUp, sIpSaid;
        static string sIpMark;

        static void ApplyInstProbe()
        {
            const System.Reflection.BindingFlags all = kInstAll | System.Reflection.BindingFlags.DeclaredOnly;
            Patch(typeof(SimDescription), "Instantiate", new Type[] { typeof(Vector3), typeof(ResourceKey), typeof(bool), typeof(bool) }, typeof(IpInstHook), "InstHook", "InstOrig");
            Patch(typeof(SimDescription), "Instantiate", new Type[] { typeof(Lot), typeof(ResourceKey) }, typeof(IpInstHook), "LotHook", "LotOrig");
            Patch(typeof(SimDescription), "InstantiateOffScreen", new Type[] { typeof(Lot), typeof(ResourceKey) }, typeof(IpInstHook), "OffHook", "OffOrig");
            Patch(typeof(AgingState), "PreInstantiateSim", new Type[] { typeof(ResourceKey).MakeByRefType() }, typeof(IpAgeHook), "PreHook", "PreOrig");
            Patch(typeof(GlobalFunctions), "CreateObjectWithOverrides", new Type[] { typeof(string), typeof(ProductVersion), typeof(Vector3), typeof(int), typeof(Vector3), typeof(Hashtable), typeof(Simulator.ObjectInitParameters) }, typeof(IpCowHook), "CowHook", "CowOrig");
            if (typeof(Sim).GetMethod("OnCreation", all, null, Type.EmptyTypes, null) != null) Patch(typeof(Sim), "OnCreation", Type.EmptyTypes, typeof(IpSimHook), "CreHook", "CreOrig");
            if (typeof(Sim).GetMethod("OnStartup", all, null, Type.EmptyTypes, null) != null) Patch(typeof(Sim), "OnStartup", Type.EmptyTypes, typeof(IpSimHook), "StartHook", "StartOrig");
            Patch(typeof(Sim), "AddInitialObjects", new Type[] { typeof(bool) }, typeof(IpSimHook), "InitHook", "InitOrig");
            if (typeof(Sim).GetMethod("Destroy", all, null, Type.EmptyTypes, null) != null) Patch(typeof(Sim), "Destroy", Type.EmptyTypes, typeof(IpSimHook), "DestroyHook", "DestroyOrig");
            Patch(typeof(SimRoutingComponent), "EnableDynamicFootprint", Type.EmptyTypes, typeof(IpFootHook), "EnableHook", "EnableOrig");
            Patch(typeof(SimRoutingComponent), "ForceUpdateDynamicFootprint", Type.EmptyTypes, typeof(IpFootHook), "ForceHook", "ForceOrig");
            World.OnWorldLoadFinishedEventHandler += delegate { try { IpWorldLoaded(); } catch (Exception) { } };
        }

        static bool IpOn()
        {
            if (sIpNative < 0)
            {
                try { sIpNative = HcWould(sYieldProbe, 15) ? 1 : 0; } catch (Exception) { sIpNative = 0; }
                if (sIpNative == 1) PatchCounters()[1535] = 1;
            }
            return sIpNative == 1 || ShortcutOn("instprobe");
        }

        static uint IpNow() { return SrcNowUs(); }

        static ulong IpTaskKey() { return Simulator.CurrentTask.Value; }

        static IpEv IpTop()
        {
            List<IpEv> st;
            return sIpOpen.TryGetValue(IpTaskKey(), out st) && st.Count > 0 ? st[st.Count - 1] : null;
        }

        static IpEv IpPush(SimDescription d, string path)
        {
            ulong k = IpTaskKey();
            List<IpEv> st;
            if (!sIpOpen.TryGetValue(k, out st)) sIpOpen[k] = st = new List<IpEv>();
            IpEv e = new IpEv();
            e.Desc = d; e.Path = path; e.Nested = st.Count > 0;
            e.Tick0 = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            e.TOuter = IpNow();
            st.Add(e);
            sIpOpenN++;
            return e;
        }

        static void IpPop(IpEv e)
        {
            ulong k = IpTaskKey();
            List<IpEv> st;
            if (sIpOpen.TryGetValue(k, out st) && st.Remove(e))
            {
                sIpOpenN--;
                if (st.Count == 0) sIpOpen.Remove(k);
            }
        }

        // A Lot entry (Instantiate(Lot, key), InstantiateOffScreen(Lot, key)): opens the record unless this task's top record
        // is the same Sim's (Instantiate(Lot) -> InstantiateOffScreen nests) or the Sim exists (EA returns CreatedSim at once).
        static IpEv IpOuter(SimDescription d, string path)
        {
            try
            {
                if (d == null || d.CreatedSim != null || !IpOn()) return null;
                IpEv top = sIpOpenN > 0 ? IpTop() : null;
                if (top != null && top.Desc == d && top.TInner == 0) return null;
                return IpPush(d, path);
            }
            catch (Exception) { PatchCounters()[1534]++; return null; }
        }

        static void IpOuterEnd(IpEv e)
        {
            if (e == null) return;
            try
            {
                uint now = IpNow();
                IpPop(e);
                if (!e.Made) return;  // no Sim came out (EA threw or returned null): nothing to time
                e.TotalUs = unchecked(now - e.TOuter);
                e.Ticks = Sims3.Gameplay.Utilities.SimClock.CurrentTicks - e.Tick0;
                Sim s = e.Desc.CreatedSim;
                e.Head = IpHead(e.Desc, s);
                e.Phase = IpPhase();
                e.Seq = ++sIpSeq;
                PatchCounters()[1530]++;
                sIpPending.Add(e);
                IpEnsureTask();
            }
            catch (Exception) { PatchCounters()[1534]++; }
        }

        static string IpPhase() { return InstProbeCore.Phase(System.Environment.TickCount, sIpLoadEnd, sIpLoaded, sIpMark); }

        static string IpScope(SimDescription d, Sim s)
        {
            try
            {
                if (d == null) return "unknown";
                Household h = d.Household, act = Household.ActiveHousehold;
                if (h == null || act == null) return "unknown";
                if (h == act) return "household";
                if (s != null && (s == PlumbBob.SelectedActor || s.IsSelectable)) return "selected";
                return "npc";
            }
            catch (Exception) { return "unknown"; }
        }

        static string IpHead(SimDescription d, Sim s)
        {
            int outfits = 0;
            string age = "?", species = "?", lot = "-";
            try
            {
                if (d != null)
                {
                    age = d.Age.ToString(); species = d.Species.ToString();
                    Hashtable m = d.GetCurrentOutfits();
                    if (m != null) foreach (DictionaryEntry de in m) { ArrayList l = de.Value as ArrayList; if (l != null) outfits += l.Count; }
                }
                Lot lc = s != null ? s.LotCurrent : null;
                if (lc != null) lot = (lc.IsWorldLot ? "world" : lc.IsCommunityLot ? "community" : lc.IsResidentialLot ? "residential" : "lot") + ":" + lc.LotId;
            }
            catch (Exception) { }
            return "scope " + IpScope(d, s) + " age " + age + " species " + species + " outfits " + outfits + " lot " + lot;
        }

        static void IpWorldLoaded()
        {
            sIpLoaded = true;
            sIpLoadEnd = System.Environment.TickCount;
            sIpTaskUp = false;  // the old world's tasks are gone; the next event starts a new frame task
            sIpPending.Clear();
            sIpRingN = sIpRingAt = 0;
            if (IpOn()) IpEnsureTask();
        }

        static void IpEnsureTask()
        {
            if (sIpTaskUp && unchecked(System.Environment.TickCount - sIpBeat) < 10000) return;
            sIpTaskUp = true;
            sIpBeat = System.Environment.TickCount;
            Simulator.AddObject(new IpFrameTask(++sIpGen));  // an older frame task sees the new generation and ends
        }

        // Harness API (reflect_call): set the phase label of the events that follow ("travel", "visitor"; "" = automatic).
        public static string InstProbeMark(string label)
        {
            sIpMark = string.IsNullOrEmpty(label) ? null : label;
            DiagLine("instprobe: mark " + (sIpMark ?? "auto") + " at " + System.Environment.TickCount + " ms");
            return "instprobe mark " + (sIpMark ?? "auto") + ", on " + IpOn() + ", events " + PatchCounters()[1530] + ", destroys " + PatchCounters()[1531];
        }

        // Harness API (reflect_call): write the summary lines now; returns them joined, for the repro answer.
        public static string InstProbeSummary(string why)
        {
            List<string> ls = sIpStats.Lines(string.IsNullOrEmpty(why) ? "now" : why);
            foreach (string l in ls) DiagLine(l);
            string tail = "instprobe: summary " + (string.IsNullOrEmpty(why) ? "now" : why) + " totals: events " + PatchCounters()[1530] + ", destroys " + PatchCounters()[1531] + ", startuponly " + PatchCounters()[1532]
                + ", lines dropped " + PatchCounters()[1533] + ", errors " + PatchCounters()[1534] + ", samples dropped " + sIpStats.Dropped + ", native ini " + sIpNative + ", switch " + ShortcutOn("instprobe");
            DiagLine(tail);
            return string.Join(" | ", ls.ToArray()) + " | " + tail;
        }

        static void IpLine(string s)
        {
            if (sIpLines >= kIpLineCap) { PatchCounters()[1533]++; return; }
            sIpLines++;
            DiagLine(s);
        }

        static void IpQueue(string phase, string part, int us, string line)
        {
            sIpStats.Add(phase, part, us);
            if (sIpQueue.Count < 512) sIpQueue.Add(line); else PatchCounters()[1533]++;
            IpEnsureTask();
        }

        // Runs once per sim frame: frame times, each pending instantiation's two frames, its line and samples, queued lines,
        // the 5-minute summary. The only place instprobe writes to the log.
        sealed class IpFrameTask : Task
        {
            readonly int mGen;
            public IpFrameTask(int gen) { mGen = gen; }
            public override void Simulate()
            {
                try { Loop(); }
                finally { Simulator.DestroyObject(ObjectId); }
            }
            void Loop()
            {
                uint last = IpNow();
                sIpSumAt = System.Environment.TickCount;
                while (true)
                {
                    Simulator.Sleep(0);
                    if (mGen != sIpGen) return;
                    try
                    {
                        sIpBeat = System.Environment.TickCount;
                        uint now = IpNow();
                        int dt = (int)Math.Min(unchecked(now - last), (uint)int.MaxValue);
                        last = now;
                        if (!sIpSaid && IpOn())
                        {
                            sIpSaid = true;
                            DiagLine("instprobe: on (docs/325), native [InstProbe] Enabled " + sIpNative + ", switch instprobe " + ShortcutOn("instprobe") + "; measurement only, every hook calls EA unchanged");
                        }
                        for (int i = 0; i < sIpPending.Count; i++)
                        {
                            IpEv e = sIpPending[i];
                            if (e.Frames == 0) { e.F0 = dt; e.Frames = 1; continue; }
                            e.F1 = dt;
                            IpDone(e, InstProbeCore.Median(sIpRing, sIpRingN));
                            sIpPending.RemoveAt(i--);
                        }
                        sIpRing[sIpRingAt] = dt;
                        sIpRingAt = (sIpRingAt + 1) % sIpRing.Length;
                        if (sIpRingN < sIpRing.Length) sIpRingN++;
                        for (int i = 0; i < sIpQueue.Count; i++) IpLine(sIpQueue[i]);
                        sIpQueue.Clear();
                        if (unchecked(System.Environment.TickCount - sIpSumAt) >= kIpSummaryMs) { sIpSumAt = System.Environment.TickCount; if (PatchCounters()[1530] + PatchCounters()[1531] > 0) InstProbeSummary("5min"); }
                    }
                    catch (ResetException) { throw; }
                    catch (Exception) { PatchCounters()[1534]++; }
                }
            }
        }

        static void IpDone(IpEv e, int baseUs)
        {
            int[] p = InstProbeCore.Split(e.TotalUs, e.InnerUs, e.AgeupUs, e.CreateUs, e.OnCreUs, e.OnStartUs, e.FootUs, e.InitUs);
            for (int i = 0; i < p.Length; i++) if (i != 2 || e.TOuter != e.TInner) sIpStats.Add(e.Phase, InstProbeCore.kParts[i], p[i]);
            int f1x = e.F1 > baseUs ? e.F1 - baseUs : 0;
            sIpStats.Add(e.Phase, "f0", e.F0);
            sIpStats.Add(e.Phase, "f1", e.F1);
            sIpStats.Add(e.Phase, "f1x", f1x);
            bool yielded = e.Ticks != e.AgeTicks;  // sim time moved outside the age-up wait: another task ran inside this call
            IpLine("instprobe: inst #" + e.Seq + " " + e.Phase + " " + e.Head + " path " + e.Path + (e.Nested ? " nested" : "") + " t " + System.Environment.TickCount
                + " total " + InstProbeCore.Ms(p[0]) + " busy " + InstProbeCore.Ms(p[1]) + " spot " + InstProbeCore.Ms(p[2]) + " ageup " + InstProbeCore.Ms(p[3])
                + " create " + InstProbeCore.Ms(p[4]) + " (native " + InstProbeCore.Ms(p[5]) + " oncreation " + InstProbeCore.Ms(p[6]) + " onstartup " + InstProbeCore.Ms(p[7])
                + ") footprint " + InstProbeCore.Ms(p[8]) + " initobj " + InstProbeCore.Ms(p[9]) + " rest " + InstProbeCore.Ms(p[10])
                + " f0 " + InstProbeCore.Ms(e.F0) + " f1 " + InstProbeCore.Ms(e.F1) + " base " + InstProbeCore.Ms(baseUs) + " yielded " + (yielded ? 1 : 0) + " ms");
        }

        public class IpInstHook
        {
            public Sim InstHook(Vector3 position, ResourceKey outfitKey, bool addInitialObjects, bool forceAlwaysAnimate)
            {
                SimDescription d = (SimDescription)(object)this;
                IpEv e = null;
                bool own = false;
                try
                {
                    if (d.CreatedSim == null && IpOn())
                    {
                        IpEv top = sIpOpenN > 0 ? IpTop() : null;
                        if (top != null && top.Desc == d && top.TInner == 0) e = top;
                        else { e = IpPush(d, "pos"); own = true; }
                        e.TInner = IpNow();
                    }
                }
                catch (Exception) { PatchCounters()[1534]++; }
                Sim r = null;
                try { r = InstOrig(position, outfitKey, addInitialObjects, forceAlwaysAnimate); return r; }
                finally
                {
                    if (e != null)
                    {
                        e.InnerUs = unchecked(IpNow() - e.TInner);
                        e.Made = r != null;
                        if (own) IpOuterEnd(e);
                    }
                }
            }
            public Sim InstOrig(Vector3 position, ResourceKey outfitKey, bool addInitialObjects, bool forceAlwaysAnimate) { return null; } // receives SimDescription.Instantiate(Vector3, ResourceKey, bool, bool)

            public Sim LotHook(Lot lot, ResourceKey outfitKey)
            {
                IpEv e = IpOuter((SimDescription)(object)this, "lot");
                try { return LotOrig(lot, outfitKey); }
                finally { IpOuterEnd(e); }
            }
            public Sim LotOrig(Lot lot, ResourceKey outfitKey) { return null; } // receives SimDescription.Instantiate(Lot, ResourceKey)

            public Sim OffHook(Lot lot, ResourceKey outfitKey)
            {
                IpEv e = IpOuter((SimDescription)(object)this, "offscreen");
                try { return OffOrig(lot, outfitKey); }
                finally { IpOuterEnd(e); }
            }
            public Sim OffOrig(Lot lot, ResourceKey outfitKey) { return null; } // receives SimDescription.InstantiateOffScreen(Lot, ResourceKey)
        }

        public class IpAgeHook
        {
            public void PreHook(ref ResourceKey outfitKey)
            {
                IpEv e = sIpOpenN > 0 ? IpTop() : null;
                if (e == null || e.Part != kIpPartNone || e.TInner == 0) { PreOrig(ref outfitKey); return; }
                e.Part = kIpPartAge;
                uint t0 = IpNow();
                long k0 = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                try { PreOrig(ref outfitKey); }
                finally { e.AgeupUs += unchecked(IpNow() - t0); e.AgeTicks += Sims3.Gameplay.Utilities.SimClock.CurrentTicks - k0; e.Part = kIpPartNone; }
            }
            public void PreOrig(ref ResourceKey outfitKey) { } // receives AgingState.PreInstantiateSim(ref ResourceKey)
        }

        public class IpCowHook
        {
            // only the record's own create: the first one after Instantiate(Vector3) began, outside every other part
            public static IGameObject CowHook(string instanceName, ProductVersion version, Vector3 initPos, int level, Vector3 initFwd, Hashtable overrides, Simulator.ObjectInitParameters initData)
            {
                IpEv e = sIpOpenN > 0 ? IpTop() : null;
                if (e == null || e.Part != kIpPartNone || e.TInner == 0 || e.CreateUs != 0) return CowOrig(instanceName, version, initPos, level, initFwd, overrides, initData);
                e.Part = kIpPartCreate;
                uint t0 = IpNow();
                try { return CowOrig(instanceName, version, initPos, level, initFwd, overrides, initData); }
                finally { uint us = unchecked(IpNow() - t0); e.CreateUs = us == 0 ? 1u : us; e.Part = kIpPartNone; }
            }
            public static IGameObject CowOrig(string instanceName, ProductVersion version, Vector3 initPos, int level, Vector3 initFwd, Hashtable overrides, Simulator.ObjectInitParameters initData) { return null; } // receives GlobalFunctions.CreateObjectWithOverrides
        }

        public class IpSimHook
        {
            public void CreHook()
            {
                IpEv e = sIpOpenN > 0 ? IpTop() : null;
                if (e == null || e.Part != kIpPartCreate || e.OnCreUs != 0) { CreOrig(); return; }
                uint t0 = IpNow();
                try { CreOrig(); }
                finally { uint us = unchecked(IpNow() - t0); e.OnCreUs = us == 0 ? 1u : us; }
            }
            public void CreOrig() { } // receives Sim.OnCreation

            public void StartHook()
            {
                IpEv e = sIpOpenN > 0 ? IpTop() : null;
                if (e != null && e.Part == kIpPartCreate && e.OnStartUs == 0)
                {
                    uint t0 = IpNow();
                    try { StartOrig(); }
                    finally { uint us = unchecked(IpNow() - t0); e.OnStartUs = us == 0 ? 1u : us; }
                    return;
                }
                bool alone = false;
                try { alone = e == null && ((object)this).GetType() == typeof(Sim) && IpOn(); } catch (Exception) { }  // not PieMenuSimHead / CAS preview Sims
                if (!alone) { StartOrig(); return; }
                uint s0 = IpNow();
                try { StartOrig(); }
                finally
                {
                    try
                    {
                        int us = (int)Math.Min(unchecked(IpNow() - s0), (uint)int.MaxValue);
                        PatchCounters()[1532]++;
                        Sim s = (Sim)(object)this;
                        string ph = IpPhase();
                        IpQueue(ph, "startuponly", us, "instprobe: startuponly " + ph + " " + IpHead(s.SimDescription, s) + " t " + System.Environment.TickCount + " ms " + InstProbeCore.Ms(us));
                    }
                    catch (Exception) { PatchCounters()[1534]++; }
                }
            }
            public void StartOrig() { } // receives Sim.OnStartup

            public void InitHook(bool bSeedNPCInventory)
            {
                IpEv e = sIpOpenN > 0 ? IpTop() : null;
                if (e == null || e.Part != kIpPartNone || e.TInner == 0) { InitOrig(bSeedNPCInventory); return; }
                e.Part = kIpPartInit;
                uint t0 = IpNow();
                try { InitOrig(bSeedNPCInventory); }
                finally { e.InitUs += unchecked(IpNow() - t0); e.Part = kIpPartNone; }
            }
            public void InitOrig(bool bSeedNPCInventory) { } // receives Sim.AddInitialObjects(bool)

            public void DestroyHook()
            {
                Sim s = null;
                string head = null;
                try { s = (Sim)(object)this; if (s.GetType() == typeof(Sim) && IpOn()) head = IpHead(s.SimDescription, s); } catch (Exception) { PatchCounters()[1534]++; }
                if (head == null) { DestroyOrig(); return; }
                uint t0 = IpNow();
                try { DestroyOrig(); }
                finally
                {
                    try
                    {
                        int us = (int)Math.Min(unchecked(IpNow() - t0), (uint)int.MaxValue);
                        PatchCounters()[1531]++;
                        string ph = IpPhase();
                        IpQueue(ph, "destroy", us, "instprobe: destroy " + ph + " " + head + " t " + System.Environment.TickCount + " ms " + InstProbeCore.Ms(us));
                    }
                    catch (Exception) { PatchCounters()[1534]++; }
                }
            }
            public void DestroyOrig() { } // receives Sim.Destroy
        }

        public class IpFootHook
        {
            public void EnableHook()
            {
                IpEv e = sIpOpenN > 0 ? IpTop() : null;
                if (e == null || e.Part != kIpPartNone || e.TInner == 0) { EnableOrig(); return; }
                e.Part = kIpPartFoot;
                uint t0 = IpNow();
                try { EnableOrig(); }
                finally { e.FootUs += unchecked(IpNow() - t0); e.Part = kIpPartNone; }
            }
            public void EnableOrig() { } // receives SimRoutingComponent.EnableDynamicFootprint

            public void ForceHook()
            {
                IpEv e = sIpOpenN > 0 ? IpTop() : null;
                if (e == null || e.Part != kIpPartNone || e.TInner == 0) { ForceOrig(); return; }
                e.Part = kIpPartFoot;
                uint t0 = IpNow();
                try { ForceOrig(); }
                finally { e.FootUs += unchecked(IpNow() - t0); e.Part = kIpPartNone; }
            }
            public void ForceOrig() { } // receives SimRoutingComponent.ForceUpdateDynamicFootprint
        }
    }
}
