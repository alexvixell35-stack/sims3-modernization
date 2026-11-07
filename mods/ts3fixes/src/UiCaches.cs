using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.SimIFace;
using Sims3.UI;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // HUD fixes from docs/49 (PLAN fix round items 25 and 26). Both are UI script work on the sim thread.
    //
    // (25) MotiveBar image cache, switch "motivebar" (tuning kMotiveBarImageCache, default off). The motive panel's private
    // MotiveBar.Update loads the moodlet icon and three background images with UIManager.LoadUIImage on every update, even
    // when the motive's moodlet is the same one it showed last time (58 of 59 LoadUIImage samples, 0.48% managed, docs/49).
    // Each load builds a new UI texture (docs/43). The hook remembers, per bar and per side (positive / negative-or-neutral),
    // the thumbnail key and background variant the game's own Update last put on that side's drawables. When the next
    // update would put the same images there, it only does the non-image part (bar size, colours, which side is visible,
    // tooltip tag, arrows); the drawables keep the images they already hold. Any other case (new moodlet icon, polarity
    // change to a side that showed something else, no moodlet, first update of a bar) runs the game's Update unchanged and
    // records what it set. State is dropped with SetMinMaxValues (bar range re-read) and with MotivesPanel.Dispose (UI
    // teardown; releases our references to the bars and their windows). Counters: [74] updates without image loads,
    // [75] game updates that load images.
    //
    // (26) Moodlet grid in place, switch "buffgrid" (tuning kBuffGridInPlace, default off). SimDisplay.PopulateBuffGrid clears
    // the moodlet grid and loads one HUDMoodletGridCell layout per moodlet each time the buffs change (30 of 36 LoadLayout
    // samples, docs/49), also when only timeouts changed. The hook keeps a snapshot of what the game's rebuild produced
    // (each cell's moodlet, its polarity and thumbnail key, in order). When the grid still holds exactly those moodlets in
    // the same order as the new buff list and none changed polarity (which picks the cell layout) or icon, it updates the
    // cells in place with the game's own UpdateMoodletTimeouts (the only per-cell part that depends on values); cells,
    // tags, tooltip callbacks and order stay those the game built. Any add, remove, reorder or icon/polarity change runs
    // the game's rebuild. Counters: [76] in-place updates, [77] game rebuilds (while the switch is on).
    public static partial class Fixes
    {
        static bool sMotiveBarReady, sBuffGridReady;

        static void ApplyUiCaches()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type panel = typeof(SimDisplay).Assembly.GetType("Sims3.UI.Hud.MotivesPanel");
            Type bar = panel != null ? panel.GetNestedType("MotiveBar", BindingFlags.NonPublic) : null;
            if (bar == null) AppliedLog().Add("MotiveBar: type not found, skipped");
            else
            {
                MotiveBarHook.sFields = new FieldInfo[kBarFields.Length];
                bool fields = true;
                for (int i = 0; i < kBarFields.Length; i++)
                    if ((MotiveBarHook.sFields[i] = bar.GetField(kBarFields[i], inst)) == null) fields = false;
                MotiveBarHook.sBgKeys = panel.GetField("sMotiveMoodletBGImageKeys", BindingFlags.Static | BindingFlags.NonPublic);
                bool arrow = fields && MotiveBarHook.sBgKeys != null && CopyStub(bar, "SetArrowState", typeof(MotiveBarHook), "ArrowOrig");
                // all or nothing: without the range or teardown hook the cached state could go stale
                bool range = arrow && Patch(bar, "SetMinMaxValues", null, typeof(MotiveBarHook), "RangeHook", "RangeOrig");
                bool dispose = range && Patch(panel, "Dispose", Type.EmptyTypes, typeof(MotiveBarHook), "DisposeHook", "DisposeOrig");
                sMotiveBarReady = dispose && Patch(bar, "Update", null, typeof(MotiveBarHook), "UpdateHook", "UpdateOrig");
                if (!sMotiveBarReady) AppliedLog().Add("MotiveBar cache: off (fields " + fields + ", arrow stub " + arrow + ", range " + range + ", dispose " + dispose + ")");
            }
            BuffGridHook.sGrid = typeof(SimDisplay).GetField("mMoodletItemGrid", BindingFlags.Instance | BindingFlags.NonPublic);
            sBuffGridReady = BuffGridHook.sGrid != null && CopyStub(typeof(SimDisplay), "UpdateMoodletTimeouts", typeof(BuffGridHook), "TimeoutsOrig")
                && Patch(typeof(SimDisplay), "PopulateBuffGrid", null, typeof(BuffGridHook), "PopulateHook", "PopulateOrig");
            if (!sBuffGridReady) AppliedLog().Add("BuffGrid in place: off");
        }

        // Gives `stub` (an empty instance method on a hook class) the header of target.method without replacing the target,
        // so a hook can call a private game method the same way it calls an original.
        static bool CopyStub(Type target, string method, Type hook, string stub)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            try
            {
                MethodInfo t = target.GetMethod(method, all);
                if (Marshal.ReadInt32(new IntPtr(t.MethodHandle.Value.ToInt32() + 0x14)) != 0) throw new InvalidOperationException("already compiled");
                MethodInfo s = hook.GetMethod(stub, all);
                SwapMethod(s, t);
                StubDirect(s);
                return true;
            }
            catch (Exception e) { AppliedLog().Add(target.Name + "." + method + " stub: " + e.Message); return false; }
        }

        static readonly string[] kBarFields = { "mIndex", "mValueMin", "mValueInvRange", "mSizeReferenceWin", "mSizeControlWin",
            "mBackgroundFillWin", "mForegroundFillWin", "mMoodletNegativeWin", "mMoodletPositiveWin", "mArrowTopLevelWin" };

        class MotiveBarState
        {
            public int Index;
            public float Min, InvRange;
            public WindowBase SizeRef, SizeCtl, BackFill, ForeFill, NegWin, PosWin, ArrowTop;
            public bool PosSet;           // positive side holds the icon PosThumb and the positive background
            public ResourceKey PosThumb;
            public int NegBg = -1;        // negative side background row: 0 negative, 2 neutral, -1 not set by the game yet
            public ResourceKey NegThumb;
        }

        // Plain class: MotiveBar is a private nested type; `this` is the MotiveBar at run time.
        public class MotiveBarHook
        {
            internal static FieldInfo[] sFields;
            internal static FieldInfo sBgKeys;
            static readonly Dictionary<object, MotiveBarState> sBars = new Dictionary<object, MotiveBarState>();
            const int kMaxBars = 64; // 6 bars per panel; more means panels were rebuilt without Dispose

            public void UpdateHook(SimInfo info)
            {
                MotiveBarState s = sMotiveBarReady && info != null && info.mMotives != null && ShortcutOn("motivebar") ? State(this) : null;
                MotiveInfo m = s != null && s.Index >= 0 && s.Index < info.mMotives.Length ? info.mMotives[s.Index] : null;
                IBuff b = m != null ? m.mMoodlet : null;
                if (b == null) // off, or no moodlet: the game's Update loads no image then and leaves the drawables alone
                {
                    UpdateOrig(info);
                    return;
                }
                int[] c = PatchCounters();
                BuffPolarity pol = b.UIBuffPolarity;
                ResourceKey thumb = b.ThumbKey;
                int negBg = pol == BuffPolarity.Neutral ? 2 : 0;
                bool same = pol == BuffPolarity.Positive ? s.PosSet && s.PosThumb == thumb : s.NegBg == negBg && s.NegThumb == thumb;
                if (!same)
                {
                    c[75]++;
                    UpdateOrig(info);
                    if (pol == BuffPolarity.Positive) { s.PosSet = true; s.PosThumb = thumb; }
                    else { s.NegBg = negBg; s.NegThumb = thumb; }
                    return;
                }
                c[74]++;
                Rect area = s.SizeCtl.Area;
                float f = (m.mValue - s.Min) * s.InvRange;
                area.Set(area.TopLeft.x, area.TopLeft.y, area.TopLeft.x + f * s.SizeRef.Area.Width, area.BottomRight.y);
                s.SizeCtl.Area = area;
                s.BackFill.ShadeColor = m.mBackColor;
                s.ForeFill.ShadeColor = m.mFillColor;
                WindowBase show = pol == BuffPolarity.Positive ? s.PosWin : s.NegWin, hide = pol == BuffPolarity.Positive ? s.NegWin : s.PosWin;
                hide.Tag = null;
                hide.Visible = false;
                show.Tag = b; // the moodlet tooltip reads it
                show.Visible = true;
                if (!(s.ArrowTop.Tag is int)) s.ArrowTop.Tag = 0;
                if ((int)s.ArrowTop.Tag != m.mArrowValue)
                {
                    ArrowOrig(m.mArrowValue);
                    s.ArrowTop.Tag = m.mArrowValue;
                }
            }
            public void UpdateOrig(SimInfo info) { } // receives MotiveBar.Update's original header
            public void ArrowOrig(int arrowIndex) { } // receives MotiveBar.SetArrowState's header (not replaced)

            public void RangeHook(float min, float max)
            {
                RangeOrig(min, max);
                sBars.Remove(this); // re-read with the new range at the next update (the game's Update reloads images then)
            }
            public void RangeOrig(float min, float max) { } // receives MotiveBar.SetMinMaxValues' original header

            public void DisposeHook()
            {
                sBars.Clear();
                DisposeOrig();
            }
            public void DisposeOrig() { } // receives MotivesPanel.Dispose's original header

            static MotiveBarState State(object bar)
            {
                MotiveBarState s;
                if (sBars.TryGetValue(bar, out s)) return s;
                if (sBars.Count >= kMaxBars) sBars.Clear();
                s = new MotiveBarState();
                s.Index = (int)sFields[0].GetValue(bar);
                s.Min = (float)sFields[1].GetValue(bar);
                s.InvRange = (float)sFields[2].GetValue(bar);
                s.SizeRef = (WindowBase)sFields[3].GetValue(bar);
                s.SizeCtl = (WindowBase)sFields[4].GetValue(bar);
                s.BackFill = (WindowBase)sFields[5].GetValue(bar);
                s.ForeFill = (WindowBase)sFields[6].GetValue(bar);
                s.NegWin = (WindowBase)sFields[7].GetValue(bar);
                s.PosWin = (WindowBase)sFields[8].GetValue(bar);
                s.ArrowTop = (WindowBase)sFields[9].GetValue(bar);
                sBars[bar] = s;
                return s;
            }
        }

        struct BuffCell { public IBuff Buff; public BuffPolarity Polarity; public ResourceKey Thumb; }

        public class BuffGridHook
        {
            internal static FieldInfo sGrid;
            static ItemGrid sSnapGrid;               // grid the snapshot belongs to (null = none)
            static readonly List<BuffCell> sSnap = new List<BuffCell>();

            public void PopulateHook(SimInfo info)
            {
                if (!sBuffGridReady || !ShortcutOn("buffgrid"))
                {
                    sSnapGrid = null;
                    sSnap.Clear();
                    PopulateOrig(info);
                    return;
                }
                int[] c = PatchCounters();
                ItemGrid grid = sGrid.GetValue(this) as ItemGrid;
                List<ItemGridCellItem> items = grid != null && grid == sSnapGrid && info != null && info.mBuffs.Count > 0 ? grid.Items : null;
                if (items != null && Same(items, info.mBuffs))
                {
                    c[76]++;
                    TimeoutsOrig();
                    return;
                }
                c[77]++;
                sSnapGrid = null;
                sSnap.Clear();
                PopulateOrig(info);
                if (grid == null || info == null || info.mBuffs.Count == 0) return;
                foreach (ItemGridCellItem it in grid.Items)
                {
                    IBuff b = it.mTag as IBuff;
                    if (b == null) { sSnap.Clear(); return; } // not a grid the game's rebuild made: no snapshot
                    BuffCell cell;
                    cell.Buff = b; cell.Polarity = b.UIBuffPolarity; cell.Thumb = b.ThumbKey;
                    sSnap.Add(cell);
                }
                sSnapGrid = grid;
            }
            public void PopulateOrig(SimInfo info) { } // receives SimDisplay.PopulateBuffGrid's original header
            public void TimeoutsOrig() { } // receives SimDisplay.UpdateMoodletTimeouts' header (not replaced)

            // The grid still shows the snapshot, the snapshot is the new buff list in the same order, and no moodlet changed
            // the polarity (cell layout) or icon it was built with.
            static bool Same(List<ItemGridCellItem> items, List<IBuff> buffs)
            {
                if (items.Count != sSnap.Count || buffs.Count != sSnap.Count) return false;
                for (int i = 0; i < sSnap.Count; i++)
                {
                    BuffCell s = sSnap[i];
                    if (!ReferenceEquals(items[i].mTag, s.Buff) || !ReferenceEquals(buffs[i], s.Buff)) return false;
                    if (s.Buff.UIBuffPolarity != s.Polarity || s.Buff.ThumbKey != s.Thumb) return false;
                }
                return true;
            }
        }
    }
}
